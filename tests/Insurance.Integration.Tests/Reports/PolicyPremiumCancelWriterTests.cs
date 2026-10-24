using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.PolicyPremiumWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The combined USD policy C cancelled by the real writer (PolicyCancel.CancelPolicyInTxAsync with its cancellation rows, cession mirrors and
/// schedule reductions), first from 25.10.2026 in the open October, then — October closed — brought forward to 20.10 (H24); the rows it
/// books feed the premium report by policy and its Excel file (legacy policy-premium-cancel-writer.test.ts and policy-premium-combined.test.ts).
/// </summary>
public sealed class PolicyPremiumCancelWriterTests
{
    private static async Task<Guid> InstallmentAsync(ReportWorld w, Guid policy)
    {
        var id = Guid.NewGuid();
        await w.ExecAsync("insert core.installment (id, policy_id, due_on, amount) values (@id, @policy, '2026-10-15', 438.00)", new { id, policy });
        return id;
    }

    private static Task<(PolicyCancel.CancelOutcome Outcome, long Retained)> CancelAsync(ReportWorld w, Guid policy, string on, bool bringForward = false) =>
        w.WriteAsync(tx => PolicyCancel.CancelPolicyInTxAsync(tx, policy, D(on), "სრული განადგურება (ტესტი)", w.Admin, bringForward));

    private static List<string> Ledger(IEnumerable<(Guid Line, string Kind, decimal Amount, decimal AmountGel, DateOnly Booked, DateOnly From, DateOnly To)> rows, Ids ids) =>
        rows.Select(r => $"{ids.Lines.Single(l => l.Value == r.Line).Key}|{r.Kind}|{r.Amount:0.00}|{r.AmountGel:0.00}|{r.Booked:yyyy-MM-dd}|{r.From:yyyy-MM-dd}|{r.To:yyyy-MM-dd}").ToList();

    private static async Task<List<(Guid Line, string Kind, decimal Amount, decimal AmountGel, DateOnly Booked, DateOnly From, DateOnly To)>> WrittenByAsync(ReportWorld w, Guid policy)
    {
        await using var c = await w.Database.OpenAdminAsync();
        return (await c.QueryAsync<(Guid, string, decimal, decimal, DateOnly, DateOnly, DateOnly)>("""
            select t.policy_line_id, t.kind, t.amount, t.amount_gel, t.booked_on, t.cover_from, t.cover_to
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id = @policy and t.created_by = @user and t.kind <> N'issue' and t.created_at > '2026-06-01'
            order by t.created_at, t.booked_on, t.kind desc, l.class_code
            """, new { policy, user = w.Admin })).ToList();
    }

    private static string[] TheC(string[][] sheet) => sheet.Where(r => r[1] == "T-0003").Select(r => string.Join("|", r)).ToArray();

    [Fact]
    public async Task Cancelled_25_10_in_the_open_October_then_brought_forward_to_20_10_after_it_closed()
    {
        await using var w = await CreateAsync();
        var ids = await WriteAsync(w, Variant.History);
        var inst = await InstallmentAsync(w, ids.Policies["pC"]);
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 1200L), await CancelAsync(w, ids.Policies["pC"], "2026-10-25"));
        var octoberAtEnd = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        var octoberFileAtEnd = XlsxSheet.Rows(ReportWorkbooks.PolicyPremium(octoberAtEnd), "NBG list");
        await w.ClosePeriodAsync("2026-10");
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 600L), await CancelAsync(w, ids.Policies["pC"], "2026-10-20", bringForward: true));

        var october = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        Assert.Equal([
            "T-0003|5.1|USD|29200|73000|0|2000|2026-10-15|2027-10-15|2026-10-25|2000|0",
            $"T-0003|{Liability}|USD|14600|36500|0|1000|2026-10-15|2027-10-15|2026-10-25|1000|0",
        ], october.Rows.Where(r => r.PolicyNumber == "T-0003").Select(Flat));
        Assert.Equal(new PolicyPremiumReport.Totals(255500, 98500, -57000, 10100, 33400, 0, 0, 0, 0, 0), october.Totals);
        Assert.Equal(octoberAtEnd.Rows, october.Rows);
        var octoberFile = XlsxSheet.Rows(ReportWorkbooks.PolicyPremium(october), "NBG list");
        Assert.Equal(octoberFileAtEnd.Select(r => string.Join("|", r)), octoberFile.Select(r => string.Join("|", r)));
        var n51 = "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)";
        var n61 = "ავტომოტოტრანსპორტის მფლობელთა სამოქალაქო პასუხისმგებლობის სავალდებულო დაზღვევა";
        Assert.Equal([
            $"C-T-002|T-0003|ტესტი შპს|არა|5.1|{n51}|USD|292|730|0|20|15.10.2026|15.10.2026|15.10.2027|25.10.2026|20|0|0|0|0|0|0",
            $"C-T-002|T-0003|ტესტი შპს|არა|{Liability}|{n61}|USD|146|365|0|10|15.10.2026|15.10.2026|15.10.2027|25.10.2026|10|0|0|0|0|0|0",
        ], TheC([.. octoberFile]));
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "2555", "985", "-570", "", "", "", "", "101", "334", "0", "0", "0", "0", "0"], octoberFile[^1]);

        var november = await w.PolicyPremiumAsync("2026-11-01", "2026-11-30");
        Assert.Equal([
            "T-0003|5.1|USD|29200|0|1000|-1000|2026-10-15|2027-10-15|2026-10-20|-1000|0",
            $"T-0003|{Liability}|USD|14600|0|500|-500|2026-10-15|2027-10-15|2026-10-20|-500|0",
        ], november.Rows.Where(r => r.PolicyNumber == "T-0003").Select(Flat));
        Assert.Equal(new PolicyPremiumReport.Totals(73000, 6500, 30000, 700, 62700, 0, 0, 0, 0, 0), november.Totals);
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "730", "65", "300", "", "", "", "", "7", "627", "0", "0", "0", "0", "0"], XlsxSheet.Rows(ReportWorkbooks.PolicyPremium(november), "NBG list")[^1]);

        // What the writer booked: the 25.10 cancellations, then on 01.11 a correction of each and the 20.10 cancellations.
        Assert.Equal(new[]
        {
            "lC1|cancellation|-284.00|-710.00|2026-10-25|2026-10-25|2027-10-15", "lC1|cancellation|-288.00|-720.00|2026-11-01|2026-10-20|2027-10-15",
            "lC1|correction|284.00|710.00|2026-11-01|2026-10-25|2027-10-15", "lC2|cancellation|-142.00|-355.00|2026-10-25|2026-10-25|2027-10-15",
            "lC2|cancellation|-144.00|-360.00|2026-11-01|2026-10-20|2027-10-15", "lC2|correction|142.00|355.00|2026-11-01|2026-10-25|2027-10-15",
        }, Ledger(await WrittenByAsync(w, ids.Policies["pC"]), ids).Order(StringComparer.Ordinal));
        var schedules = await w.App.ReadTxAsync(async tx => (await Billing.LoadScheduleAsync(tx, ids.Policies["pC"], D("2026-10-31")), await Billing.LoadScheduleAsync(tx, ids.Policies["pC"], D("2026-11-30"))));
        Assert.Equal((inst, 12.00m), (schedules.Item1.Single().Id, schedules.Item1.Single().Amount));
        Assert.Equal(6.00m, schedules.Item2.Single().Amount);
        await using var c = await w.Database.OpenAdminAsync();
        Assert.Equal(("cancelled", D("2026-10-20")), await c.QuerySingleAsync<(string, DateOnly)>("select status, cancelled_on from core.policy where id = @id", new { id = ids.Policies["pC"] }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.premium_cession"));
    }

    [Fact]
    public async Task The_reinsurer_share_follows_each_cancellation_on_the_closed_treaty()
    {
        await using var w = await CreateAsync();
        var ids = await WriteAsync(w, Variant.History);
        await InstallmentAsync(w, ids.Policies["pC"]);
        var q30 = await w.TreatyAsync("RI-QS30", "quota_share", "2026-01-01", "2027-12-31", ["5", "6"], "USD", cededPercent: 30m);
        var cC1 = await w.CessionAsync(ids.Rows["tC1"], q30, 30m, 87.60m, 219.00m, "2026-10-15", "2026-10-15", "2027-10-15");
        var cC2 = await w.CessionAsync(ids.Rows["tC2"], q30, 30m, 43.80m, 109.50m, "2026-10-15", "2026-10-15", "2027-10-15");
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @q30", new { q30 });
        var q50 = await w.TreatyAsync("RI-QS50", "quota_share", "2026-01-01", "2027-12-31", ["5", "6"], "USD", cededPercent: 50m);
        await CancelAsync(w, ids.Policies["pC"], "2026-10-25");
        var octoberAtEnd = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        await w.ClosePeriodAsync("2026-10");
        await CancelAsync(w, ids.Policies["pC"], "2026-10-20", bringForward: true);

        await using var c = await w.Database.OpenAdminAsync();
        var ceded = (await c.QueryAsync<(Guid Line, decimal Amount, decimal AmountGel, DateOnly Booked, Guid Treaty, decimal Share, decimal CommissionGel, Guid? Cancels)>("""
            select t.policy_line_id, c.amount, c.amount_gel, c.booked_on, c.treaty_id, c.share_percent, c.commission_gel, c.cancels_cession_id
            from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            """)).ToList();
        long Sum(Guid line, string from, string to, bool gel) =>
            ceded.Where(x => x.Line == line && x.Booked >= D(from) && x.Booked <= D(to)).Sum(x => Billing.Tetri(gel ? x.AmountGel : x.Amount));
        Assert.Equal((600L, 300L), (Sum(ids.Lines["lC1"], "2026-10-01", "2026-10-31", true), Sum(ids.Lines["lC2"], "2026-10-01", "2026-10-31", true)));
        Assert.Equal((-300L, -150L), (Sum(ids.Lines["lC1"], "2026-11-01", "2026-11-30", true), Sum(ids.Lines["lC2"], "2026-11-01", "2026-11-30", true)));
        Assert.Equal((240L, 120L, -120L, -60L), (Sum(ids.Lines["lC1"], "2026-10-01", "2026-10-31", false), Sum(ids.Lines["lC2"], "2026-10-01", "2026-10-31", false),
            Sum(ids.Lines["lC1"], "2026-11-01", "2026-11-30", false), Sum(ids.Lines["lC2"], "2026-11-01", "2026-11-30", false)));
        // Every mirror on the closed 30 % treaty, no commission; nothing on the active 50 % one.
        Assert.Equal(8, ceded.Count);
        Assert.All(ceded, x => Assert.Equal((q30, 30m, 0m), (x.Treaty, x.Share, x.CommissionGel)));
        Assert.DoesNotContain(ceded, x => x.Treaty == q50);
        Assert.Equal(new[] { -216.00m, -213.00m }, ceded.Where(x => x.Cancels == cC1).Select(x => x.AmountGel).Order());
        Assert.Equal(new[] { -108.00m, -106.50m }, ceded.Where(x => x.Cancels == cC2).Select(x => x.AmountGel).Order());
        // The gross side is as without reinsurance; October as at its end.
        var october = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        Assert.Equal(octoberAtEnd.Rows.Select(Flat), october.Rows.Select(Flat));
        Assert.Equal([
            "T-0003|5.1|USD|29200|0|1000|-1000|2026-10-15|2027-10-15|2026-10-20|-1000|0",
            $"T-0003|{Liability}|USD|14600|0|500|-500|2026-10-15|2027-10-15|2026-10-20|-500|0",
        ], (await w.PolicyPremiumAsync("2026-11-01", "2026-11-30")).Rows.Where(r => r.PolicyNumber == "T-0003").Select(Flat));
        Assert.Equal(19, await c.ExecuteScalarAsync<int>("select count(*) from core.premium_transaction"));
    }

    [Fact]
    public async Task The_combined_ledger_written_as_rows_gives_the_same_October_and_November()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w, Variant.Combined);
        Assert.Equal([
            "T-0003|5.1|USD|29200|73000|0|2000|2026-10-15|2027-10-15|2026-10-25|2000|0",
            $"T-0003|{Liability}|USD|14600|36500|0|1000|2026-10-15|2027-10-15|2026-10-25|1000|0",
        ], (await w.PolicyPremiumAsync("2026-10-01", "2026-10-31")).Rows.Where(r => r.PolicyNumber == "T-0003").Select(Flat));
        Assert.Equal([
            "T-0003|5.1|USD|29200|0|1000|-1000|2026-10-15|2027-10-15|2026-10-20|-1000|0",
            $"T-0003|{Liability}|USD|14600|0|500|-500|2026-10-15|2027-10-15|2026-10-20|-500|0",
        ], (await w.PolicyPremiumAsync("2026-11-01", "2026-11-30")).Rows.Where(r => r.PolicyNumber == "T-0003").Select(Flat));
        await using var o = await CreateAsync();
        await WriteAsync(o, Variant.CombinedOctoberOnly);
        Assert.Equal((await w.PolicyPremiumAsync("2026-10-01", "2026-10-31")).Rows.Select(Flat), (await o.PolicyPremiumAsync("2026-10-01", "2026-10-31")).Rows.Select(Flat));
    }
}
