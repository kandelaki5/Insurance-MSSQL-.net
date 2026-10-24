using System.Numerics;

namespace Insurance.Application.Ports;

/// <summary>
/// The policy list (legacy policies/page.tsx), read once for the Razor page, the Angular view and /api/v1/policies (ADR 0002): newest
/// first, searched by policy number, contract number, client or id number.
/// </summary>
public interface IPolicyListQuery
{
    Task<PolicyList> ListAsync(PolicyListRequest request, CancellationToken ct = default);
}

/// <param name="Search">The search text as typed; trimmed as the old program trimmed it. Empty lists every policy.</param>
/// <param name="Offset">Rows skipped, from 0.</param>
/// <param name="Limit">Rows returned, 1 to <see cref="MaxLimit"/> (the old list's 200).</param>
/// <param name="Status">Only policies of this status (<see cref="Statuses"/>); null for every status.</param>
/// <param name="Payment">Paid or unpaid: only active policies, by what their schedule still asks as of the day.</param>
/// <param name="Oldest">Oldest first instead of newest first.</param>
/// <param name="SearchObjects">The search looks in the insured objects' descriptions too (the workspace's "vehicle"); the Razor list,
/// as the old one, does not.</param>
public sealed record PolicyListRequest(string Search, int Offset = 0, int Limit = PolicyListRequest.MaxLimit, string? Status = null,
    PolicyPayment Payment = PolicyPayment.All, bool Oldest = false, bool SearchObjects = false)
{
    public const int MaxLimit = 200;

    /// <summary>The policy statuses (core.policy's policy_status_check).</summary>
    public static readonly IReadOnlyList<string> Statuses = ["draft", "pending_approval", "active", "cancelled"];
}

public enum PolicyPayment { All, Paid, Unpaid }

/// <param name="Total">How many policies the search and the filters find, all pages together.</param>
/// <param name="Counts">How many policies the search finds by status, before the status and payment filters (the tabs).</param>
/// <param name="AsOf">The day the amounts are as of (today in Tbilisi), taken once.</param>
public sealed record PolicyList(IReadOnlyList<PolicyListItem> Items, int Total, IReadOnlyDictionary<string, int> Counts, DateOnly AsOf);

/// <summary>
/// One policy of the list. Amounts are exact, in minor units (tetri, cents) of <see cref="Currency"/>. A change approved in its
/// original's open month is no row of its own: the original is listed at its corrected premium (owner, 13.10.2026; PR #225).
/// </summary>
/// <param name="Premium">The policy's premium as written, with such a correction.</param>
/// <param name="Paid">Allocated to its schedule from receipts received by <see cref="PolicyList.AsOf"/>.</param>
/// <param name="Outstanding">What the schedule still asks; null for a draft or a policy waiting for approval (no schedule yet).</param>
/// <param name="Reinsured">A cession of its premium is booked by <see cref="PolicyList.AsOf"/>.</param>
public sealed record PolicyListItem(Guid Id, string Number, string Policyholder, string? Product, string? Objects, DateOnly EffectiveFrom,
    DateOnly EffectiveTo, string Status, string Currency, BigInteger Premium, BigInteger Paid, BigInteger? Outstanding, bool Reinsured);
