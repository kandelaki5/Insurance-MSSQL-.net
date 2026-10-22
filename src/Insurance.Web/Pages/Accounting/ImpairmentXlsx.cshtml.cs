using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting;

/// <summary>The impairment calculation as an Excel file in the column order of the owner's template (legacy impairment/xlsx/route.ts).</summary>
public sealed class ImpairmentXlsxModel(Rates rates) : AppPageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Downloads.Text("Forbidden", 403);
        var date = Query("date") ?? "";
        if (!Dates.IsIsoDate(date) || date.StartsWith("0000", StringComparison.Ordinal) || Dates.ParseIsoDate(date) > Rates.TodayInTbilisi())
            return Downloads.Text("Bad date", 400);
        var report = await Db.ReadTxAsync(tx => ImpairmentReport.BuildAsync(tx, rates, Dates.ParseIsoDate(date)));
        return Downloads.Xlsx(Response, ReportWorkbooks.Impairment(report), $"impairment-{date}.xlsx");
    }
}
