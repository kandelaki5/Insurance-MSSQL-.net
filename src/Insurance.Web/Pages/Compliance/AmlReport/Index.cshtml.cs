using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Compliance.AmlReport;

/// <summary>The AML supervisory report, order №47 (legacy compliance/aml-report/page.tsx).</summary>
public sealed class IndexModel : AppPageModel
{
    public Application.Services.AmlReport.Report Report { get; private set; } = null!;
    public string HalfValue { get; private set; } = "1";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Aml.CanSeeAml(Me)) return Redirect("/");
        var (period, half) = Application.Services.AmlReport.ParsePeriod(Query("year"), Query("half"), Rates.TodayInTbilisi());
        HalfValue = half;
        Report = await Db.ReadTxAsync(tx => Application.Services.AmlReport.BuildAsync(tx, period));
        return Page();
    }
}
