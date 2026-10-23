using Insurance.Application.Texts;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.PolicyPremiumWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The premium report by policy and insurance class in the owner's NBG list layout (legacy policy-premium-report.test.ts), over the same
/// fictional ledger in SQL Server. Expected figures by hand from docs/04 r1–r9 and the owner's answers of 14.10.2026; the monthly report
/// over the same rows gives the same written, earned and UPR totals.
/// </summary>
public sealed class PolicyPremiumReportTests
{
    private static PolicyPremiumReport.Totals Totals(long stat, long correction, long corrected, long earned, long unearned, long riPremium = 0, long riEarned = 0, long riUnearned = 0, long commissionEarned = 0, long commissionUnearned = 0) =>
        new(stat, correction, corrected, earned, unearned, riPremium, riEarned, riUnearned, commissionEarned, commissionUnearned);

    private static readonly string[] October =
    [
        "T-0001|5.1|GEL|36500|36500|0|36500|2026-10-01|2027-10-01|null|3100|33400",
        "T-0002|5.1|GEL|73000|73000|64000|-64000|2026-09-01|2027-09-01|2026-10-16|3000|0",
        "T-0003|5.1|USD|29200|73000|0|73000|2026-10-15|2027-10-15|null|3400|69600",
        $"T-0003|{Liability}|USD|14600|36500|0|36500|2026-10-15|2027-10-15|null|1700|34800",
        "T-0006|5.1|GEL|36500|0|34500|-34500|2026-09-01|2027-09-01|2026-09-21|-1000|0",
        "T-0007|5.1|GEL|36500|36500|0|2000|2026-10-05|2027-10-05|2026-10-25|2000|0",
    ];

    [Fact]
    public async Task October_started_cancelled_and_combined_USD_lines_an_ended_and_a_not_yet_booked_policy_are_not_listed()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w);
        var before = await w.FingerprintAsync();
        var report = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Equal(October, report.Rows.Select(Flat));
        Assert.Equal(Totals(255500, 98500, 49500, 12200, 137800), report.Totals);
        Assert.Equal(["C-T-001", "C-T-002", "C-T-002", "C-T-002", "C-T-003", "C-T-001"], report.Rows.Select(r => r.ContractNumber));
        Assert.Equal(["ტესტი პირველი", "ტესტი შპს", "ტესტი შპს", "ტესტი შპს", "ტესტი პირველი", "ტესტი პირველი"], report.Rows.Select(r => r.Policyholder));
    }

    [Fact]
    public async Task September_shows_F_in_force_without_a_cancel_date()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w);
        Assert.Equal([
            "T-0002|5.1|GEL|73000|73000|0|73000|2026-09-01|2027-09-01|null|6000|67000",
            "T-0006|5.1|GEL|36500|36500|0|36500|2026-09-01|2027-09-01|null|3000|33500",
        ], (await w.PolicyPremiumAsync("2026-09-01", "2026-09-30")).Rows.Select(Flat));
    }

    [Fact]
    public async Task The_monthly_report_over_the_same_rows_gives_the_same_written_earned_and_UPR()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w);
        foreach (var (month, from, to, want) in new[] { ("2026-09", "2026-09-01", "2026-09-30", (109500L, 9000L, 100500L)), ("2026-10", "2026-10-01", "2026-10-31", (49500L, 12200L, 137800L)) })
        {
            var byPolicy = (await w.PolicyPremiumAsync(from, to)).Totals;
            var monthly = (await w.MonthAsync(month)).Totals;
            Assert.Equal(want, (byPolicy.CorrectedGel, byPolicy.EarnedGel, byPolicy.UnearnedGel));
            Assert.Equal(want, ((long)monthly.FinancialWrittenGel, (long)monthly.EarnedGel, (long)monthly.UnearnedGel));
        }
    }

    [Fact]
    public async Task A_period_across_two_months_counts_the_cancellations_booked_inside_it()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w);
        var report = await w.PolicyPremiumAsync("2026-09-15", "2026-10-15");
        Assert.Equal([
            "T-0001|5.1|GEL|36500|36500|0|36500|2026-10-01|2027-10-01|null|1500|35000",
            "T-0002|5.1|GEL|73000|73000|0|0|2026-09-01|2027-09-01|null|6200|64000",
            "T-0003|5.1|USD|29200|73000|0|73000|2026-10-15|2027-10-15|null|200|72800",
            $"T-0003|{Liability}|USD|14600|36500|0|36500|2026-10-15|2027-10-15|null|100|36400",
            "T-0006|5.1|GEL|36500|36500|34500|-34500|2026-09-01|2027-09-01|2026-09-21|600|0",
            "T-0007|5.1|GEL|36500|36500|0|36500|2026-10-05|2027-10-05|null|1100|35400",
        ], report.Rows.Select(Flat));
        Assert.Equal(Totals(292000, 34500, 148000, 9700, 243600), report.Totals);
    }

    [Fact]
    public async Task The_Excel_file_has_the_period_the_columns_one_row_per_line_and_the_totals()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w);
        var sheet = XlsxSheet.Rows(ReportWorkbooks.PolicyPremium(await w.PolicyPremiumAsync("2026-10-01", "2026-10-31")), "NBG list");
        var c = PolicyPremiumTexts.Columns.ByKey;
        var className51 = "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)";
        var className61 = "ავტომოტოტრანსპორტის მფლობელთა სამოქალაქო პასუხისმგებლობის სავალდებულო დაზღვევა";
        string[] Blank(string first = "") => [first, .. Enumerable.Repeat("", 21)];
        Assert.Equal(
        [
            Blank("01.10.2026 – 31.10.2026"),
            Blank(),
            [c["contractNumber"], c["policyNumber"], c["policyholder"], c["isModification"], c["classCode"], c["className"], c["currency"], c["premium"], c["statistical"], c["correction"], c["corrected"], c["startDate"], c["changeDate"], c["endDate"], c["cancelDate"], c["earned"], c["unearned"], c["riPremium"], c["riEarned"], c["riUnearned"], c["commissionEarned"], c["commissionUnearned"]],
            ["C-T-001", "T-0001", "ტესტი პირველი", "არა", "5.1", className51, "GEL", "365", "365", "0", "365", "01.10.2026", "01.10.2026", "01.10.2027", "", "31", "334", "0", "0", "0", "0", "0"],
            ["C-T-002", "T-0002", "ტესტი შპს", "არა", "5.1", className51, "GEL", "730", "730", "640", "-640", "01.09.2026", "01.09.2026", "01.09.2027", "16.10.2026", "30", "0", "0", "0", "0", "0", "0"],
            ["C-T-002", "T-0003", "ტესტი შპს", "არა", "5.1", className51, "USD", "292", "730", "0", "730", "15.10.2026", "15.10.2026", "15.10.2027", "", "34", "696", "0", "0", "0", "0", "0"],
            ["C-T-002", "T-0003", "ტესტი შპს", "არა", Liability, className61, "USD", "146", "365", "0", "365", "15.10.2026", "15.10.2026", "15.10.2027", "", "17", "348", "0", "0", "0", "0", "0"],
            ["C-T-003", "T-0006", "ტესტი პირველი", "არა", "5.1", className51, "GEL", "365", "0", "345", "-345", "01.09.2026", "01.09.2026", "01.09.2027", "21.09.2026", "-10", "0", "0", "0", "0", "0", "0"],
            ["C-T-001", "T-0007", "ტესტი პირველი", "არა", "5.1", className51, "GEL", "365", "365", "0", "20", "05.10.2026", "05.10.2026", "05.10.2027", "25.10.2026", "20", "0", "0", "0", "0", "0", "0"],
            [PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "2555", "985", "495", "", "", "", "", "122", "1378", "0", "0", "0", "0", "0"],
        ], sheet);
    }

    [Fact]
    public void The_period_is_the_one_asked_when_valid_else_the_previous_calendar_month()
    {
        Assert.Equal((D("2026-09-15"), D("2026-10-15")), PolicyPremiumReport.ReportPeriod("2026-09-15", "2026-10-15", D("2026-10-14")));
        Assert.Equal((D("2026-09-01"), D("2026-09-30")), PolicyPremiumReport.ReportPeriod(null, null, D("2026-10-14")));
        Assert.Equal((D("2026-09-01"), D("2026-09-30")), PolicyPremiumReport.ReportPeriod("2026-10-31", "2026-10-01", D("2026-10-14")));
        Assert.Equal((D("2025-12-01"), D("2025-12-31")), PolicyPremiumReport.ReportPeriod("2026-13-01", "2026-10-01", D("2026-01-05")));
    }

    // ---- A cancellation brought forward after its month closed (review PR223-HISTORY-CANCEL).

    [Fact]
    public async Task October_taken_again_after_a_bring_forward_is_unchanged()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w, Variant.History);
        await w.ClosePeriodAsync("2026-09");
        await w.ClosePeriodAsync("2026-10");
        var report = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        Assert.Equal(October, report.Rows.Select(Flat));
        Assert.Equal(Totals(255500, 98500, 49500, 12200, 137800), report.Totals);
        var sheet = XlsxSheet.Rows(ReportWorkbooks.PolicyPremium(report), "NBG list");
        Assert.Equal(["C-T-002", "T-0002", "ტესტი შპს", "არა", "5.1", "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", "GEL", "730", "730", "640", "-640", "01.09.2026", "01.09.2026", "01.09.2027", "16.10.2026", "30", "0", "0", "0", "0", "0", "0"], sheet[4]);
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "2555", "985", "495", "", "", "", "", "122", "1378", "0", "0", "0", "0", "0"], sheet[9]);
    }

    [Fact]
    public async Task November_shows_the_new_cancel_date()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w, Variant.History);
        var report = await w.PolicyPremiumAsync("2026-11-01", "2026-11-30");
        Assert.Equal([
            "T-0001|5.1|GEL|36500|36500|0|0|2026-10-01|2027-10-01|null|3000|30400",
            "T-0002|5.1|GEL|73000|0|5000|-5000|2026-09-01|2027-09-01|2026-09-21|-5000|0",
            "T-0003|5.1|USD|29200|73000|0|0|2026-10-15|2027-10-15|null|6000|63600",
            $"T-0003|{Liability}|USD|14600|36500|0|0|2026-10-15|2027-10-15|null|3000|31800",
            "T-0005|5.1|GEL|36500|36500|0|36500|2026-10-20|2027-10-20|null|4200|32300",
        ], report.Rows.Select(Flat));
        Assert.Equal(Totals(182500, 5000, 31500, 11200, 158100), report.Totals);
    }

    [Fact]
    public async Task September_unchanged_and_the_monthly_report_agrees_for_October_and_November()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w, Variant.History);
        Assert.Equal([
            "T-0002|5.1|GEL|73000|73000|0|73000|2026-09-01|2027-09-01|null|6000|67000",
            "T-0006|5.1|GEL|36500|36500|0|36500|2026-09-01|2027-09-01|null|3000|33500",
        ], (await w.PolicyPremiumAsync("2026-09-01", "2026-09-30")).Rows.Select(Flat));
        foreach (var (month, from, to, want) in new[] { ("2026-10", "2026-10-01", "2026-10-31", (49500L, 12200L, 137800L)), ("2026-11", "2026-11-01", "2026-11-30", (31500L, 11200L, 158100L)) })
        {
            var byPolicy = (await w.PolicyPremiumAsync(from, to)).Totals;
            var monthly = (await w.MonthAsync(month)).Totals;
            Assert.Equal(want, (byPolicy.CorrectedGel, byPolicy.EarnedGel, byPolicy.UnearnedGel));
            Assert.Equal(want, ((long)monthly.FinancialWrittenGel, (long)monthly.EarnedGel, (long)monthly.UnearnedGel));
        }
    }

    // ---- The change and reinsurance columns (owner, 14.10.2026).

    private static string Wide(PolicyPremiumReport.Row r) =>
        string.Join("|", r.PolicyNumber, r.IsModification, r.StartDate.ToString("yyyy-MM-dd"), r.ChangeDate.ToString("yyyy-MM-dd"), r.EndDate.ToString("yyyy-MM-dd"), r.StatisticalGel, r.CorrectedGel,
            r.EarnedGel, r.UnearnedGel, r.RiPremiumGel, r.RiEarnedGel, r.RiUnearnedGel, r.CommissionEarnedGel, r.CommissionUnearnedGel);

    [Fact]
    public async Task October_change_H_and_As_reinsurance()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w, Variant.ChangeAndReinsurance);
        var report = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        Assert.Equal([
            "T-0001|False|2026-10-01|2026-10-01|2027-10-01|36500|36500|3100|33400|10950|930|10020|186|2004",
            "T-0008|True|2026-10-01|2026-10-16|2027-10-01|-3500|-3500|-160|-3340|0|0|0|0|0",
        ], report.Rows.Where(r => r.PolicyNumber is "T-0001" or "T-0008").Select(Wide));
        Assert.Equal(Totals(252000, 98500, 46000, 12040, 134460, 10950, 930, 10020, 186, 2004), report.Totals);
        Assert.All(report.Rows.Where(r => r.PolicyNumber is not ("T-0001" or "T-0008")), r =>
            Assert.Equal((false, true, 0L, 0L, 0L, 0L, 0L), (r.IsModification, r.ChangeDate == r.StartDate, r.RiPremiumGel, r.RiEarnedGel, r.RiUnearnedGel, r.CommissionEarnedGel, r.CommissionUnearnedGel)));
        var monthly = (await w.MonthAsync("2026-10")).Totals;
        Assert.Equal((46000L, 12040L, 134460L, 10950L, 10020L), ((long)monthly.FinancialWrittenGel, (long)monthly.EarnedGel, (long)monthly.UnearnedGel, (long)monthly.CededWrittenGel, (long)monthly.CededUnearnedGel));
        var sheet = XlsxSheet.Rows(ReportWorkbooks.PolicyPremium(report), "NBG list");
        var className = "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)";
        Assert.Equal(["C-T-001", "T-0001", "ტესტი პირველი", "არა", "5.1", className, "GEL", "365", "365", "0", "365", "01.10.2026", "01.10.2026", "01.10.2027", "", "31", "334", "109.5", "9.3", "100.2", "1.86", "20.04"], sheet.Single(r => r[1] == "T-0001"));
        Assert.Equal(["C-T-001", "T-0008", "ტესტი პირველი", "კი", "5.1", className, "GEL", "-35", "-35", "0", "-35", "01.10.2026", "16.10.2026", "01.10.2027", "", "-1.6", "-33.4", "0", "0", "0", "0", "0"], sheet.Single(r => r[1] == "T-0008"));
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "2520", "985", "460", "", "", "", "", "120.4", "1344.6", "109.5", "9.3", "100.2", "1.86", "20.04"], sheet[^1]);
    }

    [Fact]
    public async Task November_A_and_H_earn_and_nothing_is_ceded()
    {
        await using var w = await CreateAsync();
        await WriteAsync(w, Variant.ChangeAndReinsurance);
        var report = await w.PolicyPremiumAsync("2026-11-01", "2026-11-30");
        Assert.Equal([
            "T-0001|False|2026-10-01|2026-10-01|2027-10-01|36500|0|3000|30400|0|900|9120|180|1824",
            "T-0008|True|2026-10-01|2026-10-16|2027-10-01|-3500|0|-300|-3040|0|0|0|0|0",
        ], report.Rows.Where(r => r.PolicyNumber is "T-0001" or "T-0008").Select(Wide));
        var t = report.Totals;
        Assert.Equal((0L, 900L, 9120L, 180L, 1824L), (t.RiPremiumGel, t.RiEarnedGel, t.RiUnearnedGel, t.CommissionEarnedGel, t.CommissionUnearnedGel));
        var monthly = (await w.MonthAsync("2026-11")).Totals;
        Assert.Equal((36500L, 15900L, 155060L, 0L, 9120L), (t.CorrectedGel, t.EarnedGel, t.UnearnedGel, t.RiPremiumGel, t.RiUnearnedGel));
        Assert.Equal((36500L, 15900L, 155060L, 0L, 9120L), ((long)monthly.FinancialWrittenGel, (long)monthly.EarnedGel, (long)monthly.UnearnedGel, (long)monthly.CededWrittenGel, (long)monthly.CededUnearnedGel));
    }
}
