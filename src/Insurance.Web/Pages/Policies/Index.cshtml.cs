using Insurance.Application.Ports;
using Insurance.Application.Services;
using Insurance.Infrastructure.Policies;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>
/// The policy list (legacy src/app/(app)/policies/page.tsx): the latest 200, searched by number, contract, client or id number. The
/// rows and their amounts are IPolicyListQuery's, the same as /api/v1/policies (docs/architecture, B2).
/// </summary>
public sealed class IndexModel(IPolicyListQuery policies) : AppPageModel
{
    // The Angular view uses the same authenticated page and the existing exact billing rules.
    // No financial write operation is exposed by this read-only handler.
    public async Task<IActionResult> OnGetDataAsync()
    {
        var list = await ListAsync();
        var data = list.Items.Select(p => new { p.Id, p.Number, Client=p.Policyholder, p.Product, Vehicle=p.Objects ?? "—", Plate="",
            Premium=PaymentActions.Decimal(p.Premium), p.Currency, Paid=PaymentActions.Decimal(p.Paid),
            Outstanding=p.Outstanding is { } o ? PaymentActions.Decimal(o) : (decimal?)null,
            p.Status, From=p.EffectiveFrom, To=p.EffectiveTo, p.Reinsured }).ToArray();
        return new JsonResult(new { Rows=data, Limit=PolicyListRequest.MaxLimit, list.AsOf, Me.CanWrite });
    }

    public IReadOnlyList<PolicyListItem> Rows { get; private set; } = [];
    public string Q { get; private set; } = "";

    /// <summary>A LIKE pattern of the text as PostgreSQL read it: % and _ stay wildcards, [ is literal.</summary>
    public static string Pattern(string q) => PolicyListQuery.Pattern(q);

    private async Task<PolicyList> ListAsync()
    {
        Q = Insurance.Domain.Common.JsText.Trim(Query("q") ?? "");
        var list = await policies.ListAsync(new PolicyListRequest(Q), HttpContext.RequestAborted);
        Rows = list.Items;
        return list;
    }

    public Task OnGetAsync() => ListAsync();
}
