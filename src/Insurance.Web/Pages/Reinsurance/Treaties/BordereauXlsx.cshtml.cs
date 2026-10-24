using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Reinsurance.Treaties;

/// <summary>The bordereau of a treaty for a period as an Excel file (legacy bordereau/xlsx/route.ts).</summary>
public sealed class BordereauXlsxModel(Rates rates) : AppPageModel
{
    public async Task<IActionResult> OnGetAsync(string id)
    {
        var from = Query("from") ?? "";
        var to = Query("to") ?? "";
        if (!Guid.TryParseExact(id, "D", out var treatyId) || !Dates.IsIsoDate(from) || !Dates.IsIsoDate(to) || string.CompareOrdinal(to, from) < 0
            || from.StartsWith("0000", StringComparison.Ordinal))
            return Downloads.Text("Bad request", 400);
        var b = await Db.ReadTxAsync(tx => Insurance.Application.Services.Bordereau.BuildAsync(tx, rates, treatyId, Dates.ParseIsoDate(from), Dates.ParseIsoDate(to)));
        if (b is null) return Downloads.Text("Not found", 404);
        return Downloads.Xlsx(Response, ReportWorkbooks.Bordereau(b), $"bordereau-{b.Treaty.Number}-{from}-{to}.xlsx");
    }
}
