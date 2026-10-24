using Insurance.Application.Services;
using Insurance.Application.Texts;
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
        if (!Dates.IsIsoDate(date) || date.StartsWith("0000", StringComparison.Ordinal) || Dates.ParseIsoDate(date) > Db.Today())
            return Downloads.Text("Bad date", 400);
        var c = await Db.ReadTxAsync(tx => ImpairmentReport.BuildCheckedAsync(tx, rates, Dates.ParseIsoDate(date)));
        // A figure Excel cannot keep to the tetri (or none exact to give) is not handed out as a file (legacy PR #361).
        if (!c.Exportable) return Downloads.Text(c.Exact ? ImpairmentTexts.NotForExcel : ImpairmentTexts.Inexact, 422);
        return Downloads.Xlsx(Response, ReportWorkbooks.Impairment(c.Report!), $"impairment-{date}.xlsx");
    }
}
