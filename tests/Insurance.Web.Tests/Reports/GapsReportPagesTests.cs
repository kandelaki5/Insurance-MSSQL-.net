using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.PolicyPremiumWorld;
using Import = Insurance.Application.Services.PolicyImport;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The monthly premium report page over HTTP on rows the real actions wrote (legacy policy-cancel-asuser.test.ts "the
/// writer's reinsurance rows on the real monthly report page" and its non-zero commission case; premium-report-page-joint
/// .test.ts with the rows of the real import and approval). Each test on a site and database of its own.
/// </summary>
public sealed class GapsReportPagesTests
{
    private static string Text(string cell) => WebUtility.HtmlDecode(Regex.Replace(cell, "<[^>]*>", "")).Trim();

    /// <summary>The page's first table (the premium table): the last three headings and, per body row, its first cell and its last three.</summary>
    private static (string[] Headings, List<string[]> Rows) Ri(string html)
    {
        var table = Regex.Match(html, @"<table\b[^>]*>([\s\S]*?)</table>").Groups[1].Value;
        var header = Regex.Matches(Regex.Match(table, @"<thead>([\s\S]*?)</thead>").Groups[1].Value, @"<th\b[^>]*>([\s\S]*?)</th>").Select(c => Text(c.Groups[1].Value)).ToArray();
        var rows = Regex.Matches(Regex.Match(table, @"<tbody>([\s\S]*?)</tbody>").Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr>")
            .Select(r => Regex.Matches(r.Groups[1].Value, @"<td\b[^>]*>([\s\S]*?)</td>").Select(c => Text(c.Groups[1].Value)).ToArray()).ToList();
        return (header[^3..], rows.Select(r => (string[])[r[0], .. r[^3..]]).ToList());
    }

    private static List<string[]> Tables(string html, int index) =>
        Regex.Matches(Regex.Match(Regex.Matches(html, @"<table\b[^>]*>([\s\S]*?)</table>")[index].Groups[1].Value, @"<tbody>([\s\S]*?)</tbody>").Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr>")
            .Select(r => Regex.Matches(r.Groups[1].Value, @"<td\b[^>]*>([\s\S]*?)</td>").Select(c => Text(c.Groups[1].Value)).ToArray()).ToList();

    private static readonly string[] RiHeadings = ["გადაზღვევის პრემია", "გადამზღვევლის წილი UPR-ში", "გადაზღვევის საკომისიო"];

    /// <summary>The reinsured combined USD policy C (the legacy reinsuredWorld; with a 20 % commission when asked), cancelled 25.10, then October closed.</summary>
    private static async Task<(ReportSite Site, Func<string, bool, Task> Cancel)> ReinsuredAsync(decimal commission)
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        var ids = await WriteAsync(w, Variant.History);
        var c = ids.Policies["pC"];
        await w.ExecAsync("insert core.installment (policy_id, due_on, amount) values (@c, '2026-10-15', 438.00)", new { c });
        var q30 = await w.TreatyAsync("RI-T-30", "quota_share", "2026-01-01", "2026-12-31", ["5.1", Liability], "USD", cededPercent: 30m, commissionPercent: commission);
        await w.CessionAsync(ids.Rows["tC1"], q30, 30m, 87.60m, 219.00m, "2026-10-15", "2026-10-15", "2027-10-15", commission > 0 ? 17.52m : 0m, commission > 0 ? 43.80m : 0m);
        await w.CessionAsync(ids.Rows["tC2"], q30, 30m, 43.80m, 109.50m, "2026-10-15", "2026-10-15", "2027-10-15", commission > 0 ? 8.76m : 0m, commission > 0 ? 21.90m : 0m);
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @q30", new { q30 });
        await w.TreatyAsync("RI-T-50", "quota_share", "2026-01-01", "2026-12-31", ["5.1", Liability], "USD", cededPercent: 50m);
        await w.RateAsync("USD", "2026-10-31", 2.5m);
        await w.RateAsync("USD", "2026-11-30", 2.5m);
        async Task Cancel(string on, bool bringForward) =>
            Assert.Equal(PolicyCancel.CancelOutcome.Done, (await w.WriteAsync(tx => PolicyCancel.CancelPolicyInTxAsync(tx, c, D(on), "სრული განადგურება (ტესტი)", w.Admin, bringForward))).Outcome);
        return (site, Cancel);
    }

    [Theory]
    [InlineData(0, "0.00", "0.00", "0.00", "0.00", "0.00", "0.00")]
    [InlineData(20, "1.20", "0.60", "1.80", "-0.60", "-0.30", "-0.90")]
    public async Task The_writers_reinsurance_rows_on_the_monthly_page_October_6_00_3_00_9_00_the_same_after_the_bring_forward_November_minus_3_00_minus_1_50_minus_4_50(
        int commission, string octCasco, string octLiability, string octTotal, string novCasco, string novLiability, string novTotal)
    {
        var (site, cancel) = await ReinsuredAsync(commission);
        await using var _ = site;
        var (client, _) = await site.SignedInAsync("accountant");
        await cancel("2026-10-25", false);
        var octoberAt25 = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10");
        await site.World.ClosePeriodAsync("2026-10");
        await cancel("2026-10-20", true);
        var october = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10");
        var november = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11");
        var o = Ri(october);
        Assert.Equal(RiHeadings, o.Headings);
        Assert.Equal([["5.1", "6.00", "0.00", octCasco], [Liability, "3.00", "0.00", octLiability], ["სულ", "9.00", "0.00", octTotal]], o.Rows);
        var n = Ri(november);
        Assert.Equal([["5.1", "-3.00", "0.00", novCasco], [Liability, "-1.50", "0.00", novLiability], ["სულ", "-4.50", "0.00", novTotal]], n.Rows);
        // October's whole premium table the same before and after November's bring-forward.
        Assert.Equal(Tables(octoberAt25, 0).Select(r => string.Join("|", r)), Tables(october, 0).Select(r => string.Join("|", r)));
        Assert.Contains("01.10.2026 – 31.10.2026", october);
        Assert.Contains("01.11.2026 – 30.11.2026", november);
        Assert.Empty(site.Nbg.Requests);
    }

    private static DateOnly? DayOf(object? value) => value switch
    {
        DateOnly d => d,
        DateTime t => DateOnly.FromDateTime(t),
        _ => null,
    };

    /// <summary>
    /// Legacy policy-cancel-asuser.test.ts "the real monthly page when the reinsurance ledger cannot be read, then again": the
    /// writer's rows as above (cancelled 25.10, October closed, brought forward to 20.10); the cession ledger read for 31.10 fails
    /// once — the page comes back as that error (500, its message, none of the page: no reinsurance column of zeros), after the
    /// gross ledger was read, nothing written, no network; the next request on the same rows shows October's reinsurance
    /// 6.00 / 3.00 / 9.00 and the whole premium table as on the healthy page. The site over the same database carries the
    /// command interceptor (FaultSite.Over).
    /// </summary>
    [Fact]
    public async Task The_monthly_page_when_the_cession_read_fails_once_is_that_error_with_nothing_written_the_next_request_shows_October_6_00_3_00_9_00()
    {
        var (site, cancel) = await ReinsuredAsync(0);
        await using var _ = site;
        await cancel("2026-10-25", false);
        await site.World.ClosePeriodAsync("2026-10");
        await cancel("2026-10-20", true);
        await using var faulty = Insurance.Web.Tests.FailureInjection.FaultSite.Over(site.World.Database);
        var (client, _) = await faulty.SignedInAsync("accountant");
        const string url = "/accounting/premium-report?month=2026-10";
        var healthy = await ReportSite.PageAsync(client, url);
        var before = await site.World.FingerprintAsync();

        static bool CessionLedger(Insurance.Application.Data.CommandEvent c) =>
            Insurance.Integration.Tests.FailureInjection.Faults.Has(c, "c.cancels_cession_id as CancelsId") && Insurance.Integration.Tests.FailureInjection.Faults.Has(c, "from core.premium_cession c");
        faulty.Faults.Reset();
        var sentinel = new Insurance.Integration.Tests.FailureInjection.InjectedFault("MONTHLY_RI_LEDGER_UNAVAILABLE");
        faulty.Faults.FailOnce(CessionLedger, sentinel);
        var failed = await Insurance.Web.Tests.FailureInjection.FaultSite.FailedAsync(await client.GetAsync(url), sentinel);
        Assert.DoesNotContain(RiHeadings[0], failed, StringComparison.Ordinal);
        Assert.True(faulty.Faults.AllFired);
        var log = faulty.Faults.Log.ToList();
        var hit = log.FindIndex(CessionLedger);
        Assert.Equal(new DateOnly(2026, 10, 31), DayOf(log[hit].Parameter("to")));
        Assert.Contains(log.Take(hit), c => c.Sql.Contains("from core.premium_transaction t", StringComparison.Ordinal));
        Assert.Equal(before, await site.World.FingerprintAsync());

        var retry = await ReportSite.PageAsync(client, url);
        var o = Ri(retry);
        Assert.Equal(RiHeadings, o.Headings);
        Assert.Equal([["5.1", "6.00", "0.00", "0.00"], [Liability, "3.00", "0.00", "0.00"], ["სულ", "9.00", "0.00", "0.00"]], o.Rows);
        Assert.Equal(Tables(healthy, 0).Select(r => string.Join("|", r)), Tables(retry, 0).Select(r => string.Join("|", r)));
        Assert.Matches(@"<input\b[^>]*\bid=""month""[^>]*\bvalue=""2026-10""|<input\b[^>]*\bvalue=""2026-10""[^>]*\bid=""month""", retry);
        Assert.Equal(before, await site.World.FingerprintAsync());
        Assert.Empty(faulty.Nbg.Requests);
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task The_premium_report_page_over_rows_written_by_the_real_import_and_approval_October_4360_written_1600_USD_owed_300_overdue_4800_November_earned_358_36_UPR_3674_31_IBNR_218()
    {
        await using var site = await ReportSite.CreateAsync();
        var w = new World(site.World.Database);
        foreach (var (day, rate) in new[] { ("2026-10-01", 2.7m), ("2026-10-15", 2.8m), ("2026-10-20", 2.9m), ("2026-10-31", 3m), ("2026-11-30", 3.5m) })
            await site.World.RateAsync("USD", day, rate);
        var underwriter = await w.UserAsync("underwriter");
        var head = await w.UserAsync("operations_head");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        const string csv = "make,model,plate,value,currency,effective_from,effective_to,installments,premium_damage,limit_damage,deductible_damage,premium_theft,limit_theft,deductible_theft\n"
            + "ტესტი,დოლარი,US-011-PT,30000,USD,2026-10-01,2027-10-01,11,900,30000,0,300,30000,0\n"
            + "ტესტი,მეორე,US-004-PT,20000,USD,2026-10-15,2027-10-15,4,300,20000,0,100,20000,0\n";
        var form = World.Form(("policyholder_id_number", taxId), ("effective_from", "2026-10-01"), ("effective_to", "2027-10-01"), ("currency", "GEL"), ("installments", "1"));
        var preview = await Import.PreviewAsync(w.App, underwriter, form, Encoding.UTF8.GetBytes(csv));
        Assert.NotNull((await Import.ConfirmAsync(w.App, w.Rates, underwriter, preview.Payload, preview)).Redirect);
        foreach (var id in await w.QueryAsync<Guid>("select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder", new { holder }))
            Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, head, id.ToString())).Outcome);
        var (client, _) = await site.SignedInAsync("accountant");
        var october = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10");
        // By hand: written 2 430 + 810 + 840 + 280 = 4 360.00; earned 206.38 + 68.79 (31/365) + 39.12 + 13.04 (17/365) = 327.33.
        Assert.Equal(["5.1", "2", "4,360.00", "4,360.00", "327.33", "4,032.67"], Tables(october, 0)[0] is var row ? (string[])[row[0], .. row[2..7]] : []);
        Assert.Equal([["USD", "1,600.00", "300.00", "3.0000 (31.10.2026)", "4,800.00"]], Tables(october, 2));
        var november = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11");
        // November (30 days; 61 and 47 days of the year by 30.11): earned 685.69 − 327.33 = 358.36, UPR 4 360.00 − 685.69 = 3 674.31; IBNR 5 % of 4 360.00.
        Assert.Equal(["5.1", "0", "0.00", "0.00", "358.36", "3,674.31"], Tables(november, 0)[0] is var nrow ? (string[])[nrow[0], .. nrow[2..7]] : []);
        Assert.Equal("218.00", Tables(november, 1)[0][4]);
        Assert.Empty(site.Nbg.Requests);
    }

    /// <summary>
    /// The legacy scenario on its own days, the database's clock standing on each action's day: September closed by the real close and the
    /// two USD policies imported and approved on 20.10.2026 (15.10's 2.80 stored here; its fetch from the bank: GapsImportPlanTests), then
    /// 600.00 GEL received 31.10 registered on the first policy on 07.11.2026 (31.10's 3.00: 200.00 USD, booked 07.11). The page read on
    /// 01.12.2026, as the legacy page was.
    /// </summary>
    [Fact]
    public async Task The_premium_report_page_over_the_real_import_approval_and_late_receipt_on_the_legacy_days_November_1400_USD_200_overdue_3_5_4900_October_1600_300_3_0_4800()
    {
        await using var site = await ReportSite.CreateAsync();
        var w = new World(site.World.Database);
        foreach (var (day, rate) in new[] { ("2026-10-01", 2.7m), ("2026-10-15", 2.8m), ("2026-10-20", 2.9m), ("2026-10-31", 3m), ("2026-11-07", 3.2m), ("2026-11-30", 3.5m) })
            await site.World.RateAsync("USD", day, rate);
        var underwriter = await w.UserAsync("underwriter");
        var head = await w.UserAsync("operations_head");
        var accountant = await w.UserAsync("accountant");
        await Gaps.DecideAsync(w, underwriter.Id, "premium.receipt.create", "allow");
        Gaps.On(w, new DateOnly(2026, 10, 20));
        Assert.Equal(new PeriodClose.Answer("done", "2026-09-01"), await PeriodClose.CloseAsync(w.App, accountant, "2026-09-01"));
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        const string csv = "make,model,plate,value,currency,effective_from,effective_to,installments,premium_damage,limit_damage,deductible_damage,premium_theft,limit_theft,deductible_theft\n"
            + "ტესტი,დოლარი,US-011-PT,30000,USD,2026-10-01,2027-10-01,11,900,30000,0,300,30000,0\n"
            + "ტესტი,მეორე,US-004-PT,20000,USD,2026-10-15,2027-10-15,4,300,20000,0,100,20000,0\n";
        var form = World.Form(("policyholder_id_number", taxId), ("effective_from", "2026-10-01"), ("effective_to", "2027-10-01"), ("currency", "GEL"), ("installments", "1"));
        var preview = await Import.PreviewAsync(w.App, underwriter, form, Encoding.UTF8.GetBytes(csv));
        Assert.NotNull((await Import.ConfirmAsync(w.App, w.Rates, underwriter, preview.Payload, preview)).Redirect);
        var ids = await w.QueryAsync<Guid>("select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder order by p.effective_from", new { holder });
        foreach (var id in ids) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, head, id.ToString())).Outcome);
        Gaps.On(w, new DateOnly(2026, 11, 7));
        Assert.Equal($"/policies/{ids[0]}?paid=0", (await w.PayAsync(underwriter, ids[0], "600.00", new DateOnly(2026, 10, 31), reference: "IMPORT-USD-600")).Redirect);
        Assert.Equal([(200m, 600m, 3m, new DateOnly(2026, 11, 7))], await w.QueryAsync<(decimal, decimal, decimal, DateOnly)>("select amount, amount_gel, rate, booked_on from core.payment_allocation"));
        Gaps.On(w, new DateOnly(2026, 12, 1));
        var (client, _) = await site.SignedInAsync("accountant");
        var november = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11");
        // By hand (tetri / cents), November at 30.11: owed 160000 − 20000 = 140000 USD cents, overdue 20000 (the first's 01.11, the second's
        // 15.10), at 3.50 490000; nothing written; earned 68569 − 32733 = 35836; UPR 436000 − 68569 = 367431; IBNR 5 % of 436000 = 21800.
        Assert.Equal([["USD", "1,400.00", "200.00", "3.5000 (30.11.2026)", "4,900.00"]], Tables(november, 2));
        Assert.Equal(["5.1", "0", "0.00", "0.00", "358.36", "3,674.31", "0.00", "0.00", "0.00"], Tables(november, 0)[0] is var nrow ? (string[])[nrow[0], .. nrow[2..]] : []);
        Assert.Equal(["0.00", "0.00", "218.00", "0.00", "0.00", "0.00"], Tables(november, 1)[0][2..]);
        Assert.Contains("01.11.2026 – 30.11.2026", november);
        // October at 31.10 from the same rows: the receipt's allocation is booked 07.11 — owed 1 600.00 USD, 300.00 overdue, at 3.00 4 800.00.
        var october = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10");
        Assert.Equal([["USD", "1,600.00", "300.00", "3.0000 (31.10.2026)", "4,800.00"]], Tables(october, 2));
        Assert.Equal(["5.1", "2", "4,360.00", "4,360.00", "327.33", "4,032.67", "0.00", "0.00", "0.00"], Tables(october, 0)[0] is var orow ? (string[])[orow[0], .. orow[2..]] : []);
        Assert.Equal("218.00", Tables(october, 1)[0][4]);
        Assert.Empty(site.Nbg.Requests);
        // The page without a month opens on today's month by the clock: December (its 31.12 rate is not stored, so the bank is asked for it).
        Assert.Contains("01.12.2026 – 31.12.2026", await ReportSite.PageAsync(client, "/accounting/premium-report"));
    }
}
