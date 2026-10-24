using Insurance.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Insurance.Api;

/// <summary>/api/v1/policies: the policy list, read-only (B2/B5), the same rows and amounts as the Razor list (IPolicyListQuery).</summary>
public static class PolicyEndpoints
{
    public static void Map(RouteGroupBuilder signedIn)
    {
        signedIn.MapGet("/policies", async (IPolicyListQuery policies, string? search, int? offset, int? limit, string? status, string? payment, string? sort,
                CancellationToken ct) =>
            {
                var o = offset ?? 0;
                var l = limit ?? 50;
                if (o < 0 || l < 1 || l > PolicyListRequest.MaxLimit) return Problems.InvalidPaging(PolicyListRequest.MaxLimit);
                if (status is not null && !PolicyListRequest.Statuses.Contains(status)) return Problems.InvalidFilter("status", PolicyListRequest.Statuses);
                PolicyPayment? pay = payment switch { null or "all" => PolicyPayment.All, "paid" => PolicyPayment.Paid, "unpaid" => PolicyPayment.Unpaid, _ => null };
                if (pay is null) return Problems.InvalidFilter("payment", ["all", "paid", "unpaid"]);
                if (sort is not (null or "newest" or "oldest")) return Problems.InvalidFilter("sort", ["newest", "oldest"]);
                var list = await policies.ListAsync(new PolicyListRequest(search ?? "", o, l, status, pay.Value, sort == "oldest", SearchObjects: true), ct);
                return Results.Ok(new PolicyListResponse(list.Items.Select(Item).ToList(), list.Total, o, l, list.Counts, list.AsOf));
            })
            .WithSummary("The policies, searched by policy number, contract number, client, id number or insured object; filtered by status and by payment " +
                "(paid / unpaid: active policies by what their schedule still asks); newest or oldest first; paged by offset and limit " +
                "(1–200, default 50). Counts are by status for the search alone.")
            .Produces<PolicyListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static PolicyListItemResponse Item(PolicyListItem p) => new(p.Id, p.Number, p.Policyholder, p.Product, p.Objects, p.EffectiveFrom,
        p.EffectiveTo, p.Status, Money.Of(p.Premium, p.Currency), Money.Of(p.Paid, p.Currency),
        p.Outstanding is { } o ? Money.Of(o, p.Currency) : null, p.Reinsured);
}

/// <param name="Total">The policies the search and the filters find, all pages together.</param>
/// <param name="Counts">The policies the search finds by status (draft, pending_approval, active, cancelled), before the filters.</param>
/// <param name="AsOf">The day the amounts are as of (today in Tbilisi).</param>
public sealed record PolicyListResponse(IReadOnlyList<PolicyListItemResponse> Items, int Total, int Offset, int Limit, IReadOnlyDictionary<string, int> Counts,
    DateOnly AsOf);

/// <param name="Outstanding">Null for a draft or a policy waiting for approval.</param>
public sealed record PolicyListItemResponse(Guid Id, string Number, string Policyholder, string? Product, string? Objects, DateOnly EffectiveFrom,
    DateOnly EffectiveTo, string Status, Money Premium, Money Paid, Money? Outstanding, bool Reinsured);
