using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting;

/// <summary>The premium report by policy and class as an Excel file, for the period asked (legacy policy-premium/xlsx/route.ts).</summary>
public sealed class PolicyPremiumXlsxModel : AppPageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Downloads.Text("Forbidden", 403);
        var from = Query("from") ?? "";
        var to = Query("to") ?? "";
        if (!Dates.IsIsoDate(from) || !Dates.IsIsoDate(to) || string.CompareOrdinal(from, to) > 0 || from.StartsWith("0000", StringComparison.Ordinal))
            return Downloads.Text("Bad period", 400);
        var report = await Db.ReadTxAsync(tx => PolicyPremiumReport.BuildAsync(tx, Dates.ParseIsoDate(from), Dates.ParseIsoDate(to)));
        return Downloads.Xlsx(Response, ReportWorkbooks.PolicyPremium(report), $"premium-by-policy-{from}-{to}.xlsx");
    }
}
