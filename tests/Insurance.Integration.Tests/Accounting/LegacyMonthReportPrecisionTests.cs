namespace Insurance.Integration.Tests.Accounting;

/// <summary>
/// Legacy scripts/month-report-precision-db-check.ts (cases R1–R9 of scripts/integration/month-report-cases.ts; "PREPARED, NOT RUN" in the
/// old program — real evidence here for the first time): the monthly report's money exactly to the tetri on a real SQL Server, each
/// case in a database of its own, through the report the page and the panel are built from (PremiumReport.BuildAsync, the stored
/// rates only — the National Bank is not asked). The page and the panel over the same cases: Insurance.Web.Tests LegacyMonthReportPagesTests.
/// </summary>
public sealed class LegacyMonthReportPrecisionTests
{
    public static TheoryData<string> Names => [.. MonthReportCase.Cases.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Each_case_adds_exactly_its_own_figures_to_both_months(string name)
    {
        await using var w = await ReportWorld.CreateAsync();
        var c = new MonthReportCase { W = w, M0 = MonthReportCase.MonthOf(name), Read = async month => new MonthView(await w.MonthAsync(month), null, null) };
        await MonthReportCase.Cases[name].Run(c);
        Assert.NotEmpty(c.Checked);
    }
}
