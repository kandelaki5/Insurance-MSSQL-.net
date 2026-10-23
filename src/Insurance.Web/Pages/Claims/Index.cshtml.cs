using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Claims;

/// <summary>The claims list (legacy src/app/(app)/claims/page.tsx).</summary>
public sealed class IndexModel : AppPageModel
{
    public string Q { get; private set; } = "";
    public string Status { get; private set; } = "";
    public IReadOnlyList<ClaimReads.ListRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Q = JsText.Trim(Query("q") ?? "");
        var status = Query("status");
        Status = status is not null && ClaimTexts.Status.ContainsKey(status) ? status : "";
        Rows = await Db.ReadTxAsync(tx => ClaimReads.ListAsync(tx, Q, Status));
    }

    public static string Badge(string status) => status is "paid" or "closed" ? "low" : status == "rejected" ? "high" : "warn";
}
