using System.Globalization;
using Insurance.Application.Ports;
using Insurance.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Insurance.Api;

/// <summary>
/// /api/v1/policies/{id}: the policy card's common figures, read-only (B2/B5), as IPolicyCardQuery reads them for the Razor card.
/// Amounts are in the policy's currency unless their name ends in Gel.
/// </summary>
public static class PolicyCardEndpoints
{
    public static void Map(RouteGroupBuilder signedIn)
    {
        signedIn.MapGet("/policies/{id:guid}", async (Guid id, IPolicyCardQuery cards, CancellationToken ct) =>
                await cards.ReadAsync(id, ct) is { } card ? Results.Ok(Response(card)) : Problems.NoSuchPolicy())
            .WithSummary("One policy: its coverages, current and issued premium, receivable, schedule, payments, cessions and changes, as of today.")
            .Produces<PolicyCardResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static Money? Maybe(decimal? amount, string currency) => amount is { } a ? Money.Of(PaymentActions.Exact(a), currency) : null;

    private static string Plain(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static PolicyCardResponse Response(PolicyCardView card)
    {
        var p = card.Policy;
        var cur = p.Currency;
        var r = card.Receivable;
        return new PolicyCardResponse(p.Id, p.Number, p.ContractNumber, p.Policyholder, p.Product, card.Objects, p.IssuedOn, p.EffectiveFrom, p.EffectiveTo,
            p.Status, cur, Plain(p.PremiumRate), p.ReplacesPolicyId, p.ReplacesNumber, p.ChangeNote, p.CancelledOn,
            Money.Of(card.CurrentPremium, cur), Money.Of(card.IssuedPremium, cur),
            new ReceivableResponse(Money.Of(r.Outstanding, cur), Money.Of(r.OutstandingGel, "GEL"), Money.Of(r.Overdue, cur), Money.Of(r.Overpaid, cur),
                Plain(card.Valuation.Rate), card.Valuation.Date),
            r.Balances.Select(b => new InstalmentResponse(b.DueOn, Money.Of(b.Amount, cur), Money.Of(b.Outstanding, cur))).ToList(),
            card.Lines.Select(l => new LineResponse(l.Id, l.Coverage, l.ClassCode, Maybe(l.SumInsured, cur), Maybe(l.Deductible, cur),
                Money.Of(PaymentActions.Exact(l.Premium), cur), Money.Of(card.CurrentByLine.GetValueOrDefault(l.Id), cur))).ToList(),
            card.Payments.Select(m => new PaymentResponse(m.Id, m.ReceivedOn, Money.Of(PaymentActions.Exact(m.AmountGel), "GEL"), m.Method, m.Kind, m.Reversed,
                Maybe(m.HereGel, "GEL"), m.Shared)).ToList(),
            card.Cessions.Select(c => new CessionResponse(c.TreatyId, c.TreatyNumber, c.TreatyName, c.Kind, c.ClassCode, Plain(c.SharePercent),
                Money.Of(PaymentActions.Exact(c.Amount), cur), Money.Of(PaymentActions.Exact(c.AmountGel), "GEL"), c.BookedOn, c.IsCancellation)).ToList(),
            card.Changes.Select(c => new ChangeResponse(c.Id, c.Number, c.EffectiveFrom, c.Status, c.ChangeNote, Money.Of(PaymentActions.Exact(c.Premium), cur))).ToList(),
            card.Today);
    }
}

/// <param name="PremiumRate">The rate the premium was fixed in GEL at (effective day), as a decimal string.</param>
/// <param name="Premium">The premium as it stands on <paramref name="AsOf"/> (an open-month correction included).</param>
/// <param name="IssuedPremium">The premium as issued (the lines).</param>
public sealed record PolicyCardResponse(Guid Id, string Number, string ContractNumber, string Policyholder, string? Product, string Objects,
    DateOnly IssuedOn, DateOnly EffectiveFrom, DateOnly EffectiveTo, string Status, string Currency, string PremiumRate, Guid? ReplacesPolicyId,
    string? ReplacesNumber, string? ChangeNote, DateOnly? CancelledOn, Money Premium, Money IssuedPremium, ReceivableResponse Receivable,
    IReadOnlyList<InstalmentResponse> Schedule, IReadOnlyList<LineResponse> Lines, IReadOnlyList<PaymentResponse> Payments,
    IReadOnlyList<CessionResponse> Cessions, IReadOnlyList<ChangeResponse> Changes, DateOnly AsOf);

/// <param name="OutstandingGel">The outstanding revalued at <paramref name="ValuationRate"/> of <paramref name="ValuationDate"/>.</param>
public sealed record ReceivableResponse(Money Outstanding, Money OutstandingGel, Money Overdue, Money Overpaid, string ValuationRate, DateOnly ValuationDate);

public sealed record InstalmentResponse(DateOnly DueOn, Money Amount, Money Outstanding);

public sealed record LineResponse(Guid Id, string? Coverage, string ClassCode, Money? SumInsured, Money? Deductible, Money Premium, Money CurrentPremium);

/// <param name="AmountGel">The whole receipt.</param>
/// <param name="HereGel">The part of it allocated to this policy.</param>
/// <param name="Shared">The receipt is allocated to other policies too.</param>
public sealed record PaymentResponse(Guid Id, DateOnly ReceivedOn, Money AmountGel, string Method, string Kind, bool Reversed, Money? HereGel, bool Shared);

public sealed record CessionResponse(Guid TreatyId, string TreatyNumber, string TreatyName, string Kind, string ClassCode, string SharePercent, Money Amount,
    Money AmountGel, DateOnly BookedOn, bool IsCancellation);

public sealed record ChangeResponse(Guid Id, string Number, DateOnly EffectiveFrom, string Status, string? ChangeNote, Money Premium);
