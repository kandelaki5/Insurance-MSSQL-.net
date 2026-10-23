using Insurance.Application.Services;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Accounting;

/// <summary>The monthly premium report (legacy accounting/premium-report/page.tsx): every signed-in user may read it.</summary>
public sealed class PremiumReportModel(Rates rates) : AppPageModel
{
    public string Month { get; private set; } = "";
    public PremiumReport.Report Report { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Month = PremiumReport.ReportMonth(Query("month") ?? "", Db.Today());
        Report = await Db.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, Month));
    }
}
