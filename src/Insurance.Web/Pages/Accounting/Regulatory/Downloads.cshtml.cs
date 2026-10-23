using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting.Regulatory;

/// <summary>
/// The official financial reporting annex N1 ("Form 1") filled with the month's insurance figures, as the page shows them (legacy
/// regulatory/form1/xlsx/route.ts). Only a file whose every figure Excel keeps as it is; else a plain refusal naming the cells (422).
/// </summary>
public sealed class Form1XlsxModel(Rates rates) : AppPageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Downloads.Text("Forbidden", 403);
        var requested = Query("month");
        var month = Insurance.Application.Services.PremiumReport.ReportMonth(requested, Db.Today());
        if (requested != month) return Downloads.Text("Bad month", 400);
        var (report, impairment, settings) = await Db.ReadTxAsync(tx => RegulatoryForms.MonthAsync(tx, rates.LatestRateOnOrBeforeAsync, month));
        return Downloads.Form(Response, RegulatoryForms.Form1(report, impairment, settings), $"financial-annex-n1-{month}.xlsx");
    }
}

/// <summary>The quarterly statistical forms workbook with form 6 filled for the quarter's last day (legacy regulatory/form6/xlsx/route.ts).</summary>
public sealed class Form6XlsxModel : AppPageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Downloads.Text("Forbidden", 403);
        var quarter = Query("quarter") ?? "";
        var asOf = Insurance.Domain.Regulatory.Form6.QuarterEnd(quarter);
        if (asOf is null) return Downloads.Text("Bad quarter", 400);
        var workbook = await Db.ReadTxAsync(tx => RegulatoryForms.Form6WorkbookAsync(tx, asOf));
        // What the template's grid cannot hold goes into rows added below it (owner, 20.10.2026); nothing is left out.
        return Downloads.Form(Response, workbook, $"form6-{quarter}.xlsx");
    }
}

/// <summary>The official reserves placement workbook filled with the month's figures (legacy regulatory/reserves/xlsx/route.ts).</summary>
public sealed class ReservesXlsxModel(Rates rates) : AppPageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Downloads.Text("Forbidden", 403);
        var requested = Query("month");
        var month = Insurance.Application.Services.PremiumReport.ReportMonth(requested, Db.Today());
        if (requested != month) return Downloads.Text("Bad month", 400);
        var (report, settings) = await Db.ReadTxAsync(async tx => (await Insurance.Application.Services.PremiumReport.BuildAsync(tx, rates, month), await Insurance.Application.Services.Settings.LoadSettingsAsync(tx)));
        return Downloads.Form(Response, RegulatoryForms.Reserves(report, settings).Workbook, $"reserves-placement-{month}.xlsx");
    }
}
