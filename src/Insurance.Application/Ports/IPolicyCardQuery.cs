using System.Numerics;
using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Application.Ports;

/// <summary>
/// The policy card's figures that are the same for every reader (legacy policies/[id]/page.tsx): the policy, its changes, coverages,
/// current premium, financial position, schedule, payments, intermediaries and cessions, all as of one day. The Razor card and
/// /api/v1/policies/{id} read them here (ADR 0002); what a card offers its reader (rights, advances, the director's requests) the
/// card reads beside them, in the same transaction.
/// </summary>
public interface IPolicyCardQuery
{
    /// <summary>The card as of today in Tbilisi, in its own snapshot transaction (one committed state); null when there is no such policy.</summary>
    Task<PolicyCardView?> ReadAsync(Guid id, CancellationToken ct = default);

    /// <summary>The card as of <paramref name="today"/> inside the caller's transaction, which should be a snapshot (Db.SnapshotAsync).</summary>
    Task<PolicyCardView?> ReadAsync(Tx tx, Guid id, DateOnly today);
}

public sealed record PolicyCardView(
    PolicyCardView.Header Policy,
    IReadOnlyList<PolicyCardView.Related> Changes,
    string Objects,
    IReadOnlyList<PolicyCardView.Line> Lines,
    IReadOnlyDictionary<Guid, BigInteger> CurrentByLine,
    IReadOnlyList<PolicyCardView.Payment> Payments,
    IReadOnlyList<PolicyCardView.IntermediaryRow> Intermediaries,
    IReadOnlyList<PolicyCardView.Cession> Cessions,
    DateOnly Today,
    (decimal Rate, DateOnly Date) Valuation,
    PolicyCard.Position Position,
    PolicyCard.Position PositionGel,
    BigInteger CommissionBasisPoints,
    PolicyCard.Position CommissionGel,
    PolicyCard.Receivable Receivable,
    DateOnly? CancelDate)
{
    public sealed record Header(Guid Id, string Number, string ContractNumber, Guid PolicyholderId, string Policyholder, string Segment, string? Product,
        DateOnly IssuedOn, DateOnly EffectiveFrom, DateOnly EffectiveTo, string Status, string PremiumCurrency, decimal PremiumRate, int? CancelGraceDays,
        DateOnly? CancelledOn, string? CancelReason, Guid ContractId, Guid? ReplacesPolicyId, string? ReplacesNumber, string? ChangeNote)
    {
        public string Currency => PremiumCurrency.Trim();
    }

    public sealed record Related(Guid Id, string Number, DateOnly EffectiveFrom, string Status, string? ChangeNote, decimal Premium);

    public sealed record Line(Guid Id, string? Coverage, string ClassCode, decimal? SumInsured, decimal? Deductible, decimal Premium, decimal PremiumGel);

    public sealed record Payment(Guid Id, DateOnly ReceivedOn, decimal AmountGel, string Method, string Kind, string? Reference, bool Reversed, string? AllocatedOn, decimal? HereGel, bool Shared);

    public sealed record IntermediaryRow(string Name, decimal CommissionPercent);

    public sealed record Cession(Guid Id, Guid TreatyId, string TreatyNumber, string TreatyName, string Kind, string ClassCode, decimal SharePercent, decimal Amount,
        decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, bool IsCancellation);

    /// <summary>The premium as it stands on the card's day, in minor units (a correction in the open month included, PR #211).</summary>
    public BigInteger CurrentPremium => CurrentByLine.Values.Aggregate(BigInteger.Zero, (a, b) => a + b);

    /// <summary>The premium as issued, the lines' premium, in minor units.</summary>
    public BigInteger IssuedPremium => Lines.Aggregate(BigInteger.Zero, (s, l) => s + PaymentActions.Exact(l.Premium));
}
