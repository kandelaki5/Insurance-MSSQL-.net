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
        signedIn.MapGet("/policies", async (IPolicyListQuery policies, string? search, int? offset, int? limit, CancellationToken ct) =>
            {
                var o = offset ?? 0;
                var l = limit ?? 50;
                if (o < 0 || l < 1 || l > PolicyListRequest.MaxLimit) return Problems.InvalidPaging(PolicyListRequest.MaxLimit);
                var list = await policies.ListAsync(new PolicyListRequest(search ?? "", o, l), ct);
                return Results.Ok(new PolicyListResponse(list.Items.Select(Item).ToList(), list.Total, o, l, list.AsOf));
            })
            .WithSummary("The policies, newest first, searched by policy number, contract number, client or id number; paged by offset and limit (1–200, default 50).")
            .Produces<PolicyListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static PolicyListItemResponse Item(PolicyListItem p) => new(p.Id, p.Number, p.Policyholder, p.Product, p.Objects, p.EffectiveFrom,
        p.EffectiveTo, p.Status, Money.Of(p.Premium, p.Currency), Money.Of(p.Paid, p.Currency),
        p.Outstanding is { } o ? Money.Of(o, p.Currency) : null, p.Reinsured);
}

/// <param name="AsOf">The day the amounts are as of (today in Tbilisi).</param>
public sealed record PolicyListResponse(IReadOnlyList<PolicyListItemResponse> Items, int Total, int Offset, int Limit, DateOnly AsOf);

/// <param name="Outstanding">Null for a draft or a policy waiting for approval.</param>
public sealed record PolicyListItemResponse(Guid Id, string Number, string Policyholder, string? Product, string? Objects, DateOnly EffectiveFrom,
    DateOnly EffectiveTo, string Status, Money Premium, Money Paid, Money? Outstanding, bool Reinsured);
