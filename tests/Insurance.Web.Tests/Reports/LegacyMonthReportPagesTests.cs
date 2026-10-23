using Insurance.Integration.Tests.Accounting;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// Legacy scripts/month-report-precision-db-check.ts over HTTP ("PREPARED, NOT RUN" in the old program): every step of the cases R1–R10
/// (tests/Insurance.Integration.Tests/Accounting/MonthReportCases*.cs) reads the month three ways, as the legacy child did — the report,
/// the monthly report page as an accountant and the financial director's panel as an admin — and every figure the page and the panel
/// show is parsed back from its text and compared exactly: the class row and the totals, the ceded premium, the claims row and total,
/// the treaty premium line, each currency's receivable with its rate and date. The National Bank is asked nothing (all rates stored).
/// </summary>
public sealed class LegacyMonthReportPagesTests
{
    public static TheoryData<string> Names => [.. MonthReportCase.Cases.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Each_case_on_the_report_page_and_the_panel_exactly(string name)
    {
        await using var site = await ReportSite.CreateAsync();
        var (accountant, _) = await site.SignedInAsync("accountant");
        var (admin, _) = await site.SignedInAsync("admin");
        var c = new MonthReportCase
        {
            W = site.World,
            M0 = MonthReportCase.MonthOf(name),
            Read = async month => new MonthView(await site.World.MonthAsync(month),
                await ReportSite.PageAsync(accountant, $"/accounting/premium-report?month={month}"),
                await ReportSite.PageAsync(admin, $"/finance/panel?month={month}")),
        };
        await MonthReportCase.Cases[name].Run(c);
        Assert.Contains(c.Checked, l => l.Contains("the report page", StringComparison.Ordinal));
        Assert.Contains(c.Checked, l => l.Contains("the panel", StringComparison.Ordinal));
        Assert.Empty(site.Nbg.Requests);
    }
}
