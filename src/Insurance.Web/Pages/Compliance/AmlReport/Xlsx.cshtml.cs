using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Compliance.AmlReport;

/// <summary>The AML report aggregates as an Excel file, one sheet per form (legacy compliance/aml-report/xlsx/route.ts).</summary>
public sealed class XlsxModel : AppPageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (!Aml.CanSeeAml(Me)) return new ContentResult { StatusCode = StatusCodes.Status403Forbidden, Content = "Forbidden", ContentType = "text/plain; charset=utf-8" };
        var year = Query("year") ?? "";
        var (period, _) = Application.Services.AmlReport.ParsePeriod(year, Query("half") ?? "1", Rates.TodayInTbilisi());
        var report = await Db.ReadTxAsync(tx => Application.Services.AmlReport.BuildAsync(tx, period));
        return File(Application.Services.AmlReport.Workbook(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"aml-report-{Fmt.Iso(period.From)}-{Fmt.Iso(period.To)}.xlsx");
    }
}
