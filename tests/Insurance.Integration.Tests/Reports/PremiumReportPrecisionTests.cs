using System.Numerics;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The monthly report's written premium, receivable, earned and UPR, exactly, on a real SQL Server database (legacy
/// precision-month-report.test.ts, MONTH_REPORT_WRITTEN_RECEIVABLE_DECIMAL_PRECISION_20261019 and
/// MONTH_REPORT_MIXED_PRECISION_EARNED_UPR_20261019). Expected values by an independent integer reckoning.
/// </summary>
public sealed class PremiumReportPrecisionTests
{
    internal static BigInteger T(string text) => ReportMath.Tetri(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A row's earned to the end of its days-th day of cover days, half away from zero.</summary>
    internal static BigInteger EarnedTo(BigInteger minor, int days, int cover) => ReportMath.DivRound(minor * days, cover);

    internal static string Shown(BigInteger v) => ReportMath.Grouped(v);

    private static async Task<PolicyRef> IssuedAsync(ReportWorld w, string number, string premium, string gel, (string, string)[] insts, string currency = "GEL", decimal rate = 1m) =>
        await w.PolicyAsync(number, "2026-10-01", "2027-10-01", [new LineSpec("5.1", decimal.Parse(premium), decimal.Parse(gel))],
            insts.Select(i => (i.Item1, decimal.Parse(i.Item2), "client")).ToList(), currency, rate);

    [Fact]
    public async Task An_issue_of_90071992547408_01_unpaid_is_written_and_owed_to_the_tetri()
    {
        await using var w = await CreateAsync();
        await w.ClosePeriodAsync("2026-09");
        await IssuedAsync(w, "P-S", "90071992547408.01", "90071992547408.01", [("2026-10-01", "90071992547408.01")]);
        var before = await w.FingerprintAsync();
        var r = await w.MonthAsync("2026-10");
        Assert.Equal(before, await w.FingerprintAsync());
        var gel = Assert.Single(r.Receivables);
        Assert.Equal([9007199254740801, 9007199254740801, 9007199254740801], new[] { r.Totals.FinancialWrittenGel, gel.Outstanding, gel.Overdue });
        Assert.Equal("90,071,992,547,408.01", PremiumReport.ReportMoney(r.Totals.FinancialWrittenGel));
    }

    [Fact]
    public async Task The_largest_issues_in_one_class_add_up_without_being_cut_and_are_owed_and_overdue_exactly()
    {
        await using var w = await CreateAsync();
        await IssuedAsync(w, "P-L", "90071992547408.01", "90071992547408.01", [("2026-10-01", "90071992547408.00"), ("2026-11-01", "0.01")]);
        await IssuedAsync(w, "P-M", "9999999999999999.99", "9999999999999999.99", [("2026-10-15", "9999999999999999.99")]);
        var written = T("90071992547408.01") + T("9999999999999999.99");
        var overdue = T("90071992547408.00") + T("9999999999999999.99");
        var r = await w.MonthAsync("2026-10");
        var c = r.Classes.Single(x => x.ClassCode == "5.1");
        Assert.Equal((2, written, written), (c.PoliciesIssued, c.StatisticalWrittenGel, c.FinancialWrittenGel));
        Assert.Equal((written, written), (r.Totals.StatisticalWrittenGel, r.Totals.FinancialWrittenGel));
        Assert.True(written > T("9999999999999999.99"));
        Assert.Equal(new PremiumReport.ReceivableRow("GEL", written, overdue, written, 1m, D("2026-10-31")), r.Receivables.Single());
    }

    [Fact]
    public async Task Earned_and_UPR_exactly_written_is_earned_plus_UPR_to_the_tetri()
    {
        await using var w = await CreateAsync();
        await IssuedAsync(w, "P-E", "90071992547408.01", "90071992547408.01", [("2026-10-01", "90071992547408.01")]);
        var minor = T("90071992547408.01");
        var earned = EarnedTo(minor, 31, 365);
        Assert.Equal(new BigInteger[] { 764995005197164, 8242204249543637 }, new[] { earned, minor - earned });
        var r = await w.MonthAsync("2026-10");
        var c = r.Classes.Single(x => x.ClassCode == "5.1");
        Assert.Equal((minor, earned, minor - earned), (c.FinancialWrittenGel, c.EarnedGel, c.UnearnedGel));
        Assert.Equal(("7,649,950,051,971.64", "82,422,042,495,436.37"), (Shown(c.EarnedGel), Shown(c.UnearnedGel)));
        var nov = (await w.MonthAsync("2026-11")).Classes.Single(x => x.ClassCode == "5.1");
        Assert.Equal((BigInteger.Zero, EarnedTo(minor, 61, 365) - earned, minor - EarnedTo(minor, 61, 365)), (nov.FinancialWrittenGel, nov.EarnedGel, nov.UnearnedGel));
    }

    [Fact]
    public async Task Two_of_the_largest_earn_each_by_its_own_rounding()
    {
        await using var w = await CreateAsync();
        await IssuedAsync(w, "P-L", "90071992547408.01", "90071992547408.01", [("2026-10-01", "90071992547408.01")]);
        await IssuedAsync(w, "P-M", "9999999999999999.99", "9999999999999999.99", [("2026-10-15", "9999999999999999.99")]);
        BigInteger l = T("90071992547408.01"), m = T("9999999999999999.99");
        var c = (await w.MonthAsync("2026-10")).Classes.Single(x => x.ClassCode == "5.1");
        var earned = EarnedTo(l, 31, 365) + EarnedTo(m, 31, 365);
        Assert.Equal((earned, l + m - earned, l + m), (c.EarnedGel, c.UnearnedGel, c.EarnedGel + c.UnearnedGel));
    }

    [Fact]
    public async Task A_linked_cancellation_written_before_its_original_and_its_reversal_earn_as_the_rule_says()
    {
        await using var w = await CreateAsync();
        var g = await IssuedAsync(w, "P-G", "1.00", "1.00", [("2026-10-01", "1.00")]);
        // The cancellation's row written with an earlier created_at than its original (H14): linked once every row is read.
        var cancel = await w.TxAsync(g.Lines[0], "cancellation", -0.88m, -0.88m, "2026-11-15", "2026-11-15", "2027-10-01", g.IssueRows[0], new DateTimeOffset(2025, 9, 30, 10, 0, 0, TimeSpan.FromHours(4)));
        var nov = (await w.MonthAsync("2026-11")).Classes.Single(x => x.ClassCode == "5.1");
        // November: the original earns 17 − 8 = 9; the cancellation mirrors its earning from 15.11: −(17 − 12) = −5; UPR nothing left.
        Assert.Equal((new BigInteger(-88), new BigInteger(4), BigInteger.Zero), (nov.FinancialWrittenGel, nov.EarnedGel, nov.UnearnedGel));
        await w.TxAsync(g.Lines[0], "correction", 0.88m, 0.88m, "2026-11-20", "2026-11-15", "2027-10-01", cancel);
        nov = (await w.MonthAsync("2026-11")).Classes.Single(x => x.ClassCode == "5.1");
        Assert.Equal((BigInteger.Zero, new BigInteger(9), new BigInteger(83)), (nov.FinancialWrittenGel, nov.EarnedGel, nov.UnearnedGel));
        var oct = (await w.MonthAsync("2026-10")).Classes.Single(x => x.ClassCode == "5.1");
        Assert.Equal((new BigInteger(100), new BigInteger(8), new BigInteger(92)), (oct.FinancialWrittenGel, oct.EarnedGel, oct.UnearnedGel));
    }

    [Fact]
    public async Task One_tetri_owed_is_not_overdue_before_its_day_and_overdue_after()
    {
        await using var w = await CreateAsync();
        var t = await IssuedAsync(w, "P-T", "100.01", "100.01", [("2026-10-01", "100.00"), ("2026-11-01", "0.01")]);
        await IssuedAsync(w, "P-V", "5.00", "5.00", [("2026-10-31", "5.00")]);
        await w.PayAsync(t.Installments[0], "2026-10-20", 100.00m);
        var oct = await w.MonthAsync("2026-10");
        Assert.Equal((new BigInteger(10501), new BigInteger(501), BigInteger.Zero), (oct.Totals.FinancialWrittenGel, oct.Receivables.Single().Outstanding, oct.Receivables.Single().Overdue));
        var nov = await w.MonthAsync("2026-11");
        Assert.Equal((BigInteger.Zero, new BigInteger(501), new BigInteger(501)), (nov.Totals.FinancialWrittenGel, nov.Receivables.Single().Outstanding, nov.Receivables.Single().Overdue));
        Assert.Equal("5.01", Shown(nov.Receivables.Single().OutstandingGel));
    }

    [Fact]
    public async Task USD_receivable_is_revalued_at_the_month_end_rate()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-20", 2.7m);
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        var u = await IssuedAsync(w, "P-U", "100.00", "270.00", [("2026-10-01", "100.00")], "USD", 2.7m);
        var receipt = await w.ReceiptAsync("2026-10-20", 27.00m);
        await w.AllocateAsync(receipt, u.Installments[0], 10.00m, 27.00m, 2.7m, "2026-10-20");
        var r = await w.MonthAsync("2026-10");
        Assert.Equal(T("270.00"), r.Totals.FinancialWrittenGel);
        var owed = T("90.00");
        Assert.Equal(new PremiumReport.ReceivableRow("USD", owed, owed, T("252.00"), 2.8m, D("2026-10-31")), r.Receivables.Single());
    }

    [Fact]
    public async Task A_reversed_receipt_brings_the_debt_back_only_from_the_reversal_month()
    {
        await using var w = await CreateAsync();
        var a = await IssuedAsync(w, "P-XA", "10000.00", "10000.00", [("2026-10-15", "10000.00")]);
        var b = await IssuedAsync(w, "P-XB", "0.01", "0.01", [("2026-11-01", "0.01")]);
        var receipt = await w.ReceiptAsync("2026-10-20", 10000.01m);
        await w.AllocateAsync(receipt, a.Installments[0], 10000.00m, 10000.00m, 1m, "2026-10-20");
        var october = await w.MonthAsync("2026-10");
        Assert.Equal((new BigInteger(1000001), BigInteger.One, BigInteger.Zero), (october.Totals.FinancialWrittenGel, october.Receivables.Single().Outstanding, october.Receivables.Single().Overdue));
        // 03.11: the advance taken by b; the receipt reversed with its mirror lines on 03.11.
        await w.AllocateAsync(receipt, b.Installments[0], 0.01m, 0.01m, 1m, "2026-11-03");
        var nov = await w.MonthAsync("2026-11");
        Assert.Equal((BigInteger.Zero, BigInteger.Zero), (nov.Receivables.Single().Outstanding, nov.Receivables.Single().Overdue));
        var mirror = await w.ReversalAsync(receipt, "2026-11-03", -10000.01m);
        await w.AllocateAsync(mirror, a.Installments[0], -10000.00m, -10000.00m, 1m, "2026-11-03");
        await w.AllocateAsync(mirror, b.Installments[0], -0.01m, -0.01m, 1m, "2026-11-03");
        nov = await w.MonthAsync("2026-11");
        Assert.Equal((BigInteger.Zero, T("10000.01"), T("10000.01")), (nov.Totals.FinancialWrittenGel, nov.Receivables.Single().Outstanding, nov.Receivables.Single().Overdue));
        // October read again: exactly as before (everything booked in November).
        var again = await w.MonthAsync("2026-10");
        Assert.Equal(october.Receivables.Single(), again.Receivables.Single());
        Assert.Equal(october.Totals, again.Totals);
    }
}
