using System.Globalization;
using System.Numerics;

namespace Insurance.Domain.Finance;

public enum MovementKind
{
    Claimed,
    Confirmed,
    Received,
    Cancelled,
    Reversal,
}

/// <summary>A movement of a regress case.</summary>
/// <param name="Amount">Tetri: positive on every kind but a reversal, which is minus its original.</param>
public record Movement(string Id, MovementKind Kind, BigInteger Amount, DateOnly BookedOn, string? ReversesId);

/// <param name="Balance">What is still owed on the case: confirmed − received − cancelled.</param>
public sealed record CaseTotals(BigInteger Claimed, BigInteger Confirmed, BigInteger Received, BigInteger Cancelled, BigInteger Balance);

/// <summary>One payment of the claim.</summary>
/// <param name="Amount">Gross indemnity in the claim's currency (tetri, cents); with DeductibleWithheld and PremiumOffset not used for the cap.</param>
/// <param name="NetPaidGel">What left the company in GEL (`claim_payment.net_paid_gel`), tetri.</param>
/// <param name="OffsetGel">The premium set off, in GEL (`amount_gel` of the set-off receipt), tetri; 0 without a set-off.</param>
public sealed record ClaimPayment(BigInteger Amount, BigInteger DeductibleWithheld, BigInteger PremiumOffset, BigInteger NetPaidGel, BigInteger OffsetGel, DateOnly PaidOn);

/// <param name="CoverageCode">The coverage code of the claim's policy line; null when the line has no product coverage.</param>
public sealed record ClaimFacts(string ClassCode, string? CoverageCode);

/// <summary>Why a claim cannot carry a regress.</summary>
public enum Ineligible
{
    Class,
    Theft,
    Coverage,
}

public sealed record CourtDocument(string Number, DateOnly Date);

/// <summary>
/// A movement to add: an original kind with its amount (and, for a court cancellation, the court's decision), or a
/// reversal of a movement already on the case (its amount is taken from the original).
/// </summary>
public sealed record NewMovement
{
    private NewMovement(MovementKind kind, BigInteger amount, DateOnly bookedOn, CourtDocument? court, string? reversesId)
    {
        Kind = kind;
        Amount = amount;
        BookedOn = bookedOn;
        Court = court;
        ReversesId = reversesId;
    }

    public MovementKind Kind { get; }

    /// <summary>The amount of an original kind; 0 on a reversal.</summary>
    public BigInteger Amount { get; }

    public DateOnly BookedOn { get; }

    public CourtDocument? Court { get; }

    /// <summary>A reversal's original.</summary>
    public string? ReversesId { get; }

    public static NewMovement Original(MovementKind kind, BigInteger amount, DateOnly bookedOn, CourtDocument? court = null)
    {
        if (kind == MovementKind.Reversal) throw new ArgumentException("A reversal is made with NewMovement.Reversal", nameof(kind));
        return new NewMovement(kind, amount, bookedOn, court, null);
    }

    public static NewMovement Reversal(string reversesId, DateOnly bookedOn) => new(MovementKind.Reversal, BigInteger.Zero, bookedOn, null, reversesId);
}

public enum MovementError
{
    /// <summary>Not a positive amount, or not a real date.</summary>
    Amount,

    /// <summary>Claimed or confirmed beyond what was reimbursed on the claim, less its other regress money, on some day.</summary>
    Cap,

    /// <summary>Received or cancelled beyond what was confirmed on some day, or a reversal that would leave that.</summary>
    Balance,

    /// <summary>A court cancellation without the court's decision number and date.</summary>
    Document,

    /// <summary>A reversal of a movement that is not on the case, or of a reversal.</summary>
    Target,

    /// <summary>The movement was reversed already.</summary>
    Reversed,

    /// <summary>A reversal dated before its original.</summary>
    Date,
}

/// <summary>Regress money already on the claim outside this case: old rows from before cases (signed).</summary>
public sealed record OtherRecovery(BigInteger Amount, DateOnly ReceivedOn);

/// <summary>What the claim brings to the check: its payments and its regress money outside the case.</summary>
public sealed record ClaimMoney(IReadOnlyList<ClaimPayment> Payments, IReadOnlyList<OtherRecovery> Others);

public enum CaseStatus
{
    Opened,
    EvidenceConfirmed,
    DemandSent,
    Accepted,
    Disputed,
    Rejected,
    PartiallyReceived,
    Closed,
}

/// <summary>A party's answer to the claim.</summary>
public enum PartyResponse
{
    Accepted,
    Disputed,
    Rejected,
}

/// <param name="Limit">The liability insurer's limit in GEL tetri; null when it is not known yet.</param>
/// <param name="Response">The insurer's answer; a rejection leaves the whole claim on the person (G6).</param>
public sealed record InsurerShare(BigInteger? Limit, PartyResponse? Response);

/// <summary>Who is expected to pay how much (the result of <see cref="Subrogation.ExpectedShares"/>); null where it cannot be told.</summary>
public sealed record ExpectedSplit(BigInteger? Insurer, BigInteger? Person);

/// <summary>
/// Regress (subrogation) on one paid CASCO claim — the rules of docs/08 version 8, free of the
/// database so they can be tested on their own. Migration 0030 checks the same rules again in its
/// triggers; the application checks them first to give the user a clear message. (subrogation.ts)
///
/// - One case belongs to one claim of class 5.1 that is not theft, and a claim has one case: its
///   whole regress history stays on it (owner, 08.10.2026: one concrete regress per concrete claim).
/// - What may be claimed is what was reimbursed on that claim, in the GEL actually paid: the net
///   paid in GEL plus the premium set off in GEL (O2, G7ა). A claim in another currency is not
///   converted again: its payments were already paid, and stored, in GEL (owner, 09.10.2026, R1).
/// - The cap is the claim's: regress money of the claim outside the case (old receipts from before
///   cases) counts against it too (Codex H81).
/// - The liable person and their insurer share one balance: confirmed − received − cancelled (3.3).
/// - Every rule holds on every day, not only today: a later confirmation or reversal cannot cover an
///   earlier over-receipt (Codex H82).
/// - Every movement is a new row; a mistake is undone by a reversal of exactly the original amount,
///   once, on a later or the same day (3.4ა). There is no write-off; only a court cancels (O4).
///
/// Amounts are whole tetri as big integers, so sums are exact whatever their size.
/// </summary>
public static class Subrogation
{
    /// <summary>'1234.5' or '1234.50' → 123450 tetri; anything else (sign, three decimals, spaces inside) → null.</summary>
    public static BigInteger? ToTetri(string text)
    {
        // /^(\d{1,16})(?:\.(\d{1,2}))?$/ on text.trim(), ASCII digits.
        var t = JsMath.Trim(text);
        var dot = t.IndexOf('.');
        var whole = dot < 0 ? t : t[..dot];
        var fraction = dot < 0 ? "" : t[(dot + 1)..];
        if (whole.Length is < 1 or > 16 || !whole.All(JsMath.IsDigit)) return null;
        if (dot >= 0 && (fraction.Length is < 1 or > 2 || !fraction.All(JsMath.IsDigit))) return null;
        return BigInteger.Parse(whole, CultureInfo.InvariantCulture) * 100 + BigInteger.Parse(fraction.PadRight(2, '0'), CultureInfo.InvariantCulture);
    }

    /// <summary>123450 → '1234.50', -60000 → '-600.00'.</summary>
    public static string FromTetri(BigInteger tetri)
    {
        var sign = tetri < 0 ? "-" : "";
        var abs = BigInteger.Abs(tetri);
        return $"{sign}{(abs / 100).ToString(CultureInfo.InvariantCulture)}.{(abs % 100).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}";
    }

    /// <summary>
    /// The case's totals from its movements, on a date when `asOf` is given (G12): a movement counts
    /// from its own day, so an October receipt reversed in November still counts in October (G10ა).
    /// A reversal counts against the kind of the movement it reverses.
    /// </summary>
    public static CaseTotals Totals(IReadOnlyList<Movement> movements, DateOnly? asOf = null)
    {
        var byId = new Dictionary<string, Movement>();
        foreach (var m in movements) byId[m.Id] = m;
        BigInteger claimed = 0, confirmed = 0, received = 0, cancelled = 0;
        foreach (var m in movements)
        {
            if (asOf is { } day && m.BookedOn > day) continue;
            MovementKind? kind = m.Kind == MovementKind.Reversal ? (byId.TryGetValue(m.ReversesId ?? "", out var target) ? target.Kind : null) : m.Kind;
            switch (kind)
            {
                case MovementKind.Claimed: claimed += m.Amount; break;
                case MovementKind.Confirmed: confirmed += m.Amount; break;
                case MovementKind.Received: received += m.Amount; break;
                case MovementKind.Cancelled: cancelled += m.Amount; break;
                default: throw new InvalidOperationException($"movement {m.Id} reverses nothing it can");
            }
        }
        return new CaseTotals(claimed, confirmed, received, cancelled, confirmed - received - cancelled);
    }

    /// <summary>
    /// What was reimbursed on the claim up to and including `asOf`, in the GEL actually paid:
    /// Σ(net paid in GEL + premium set off in GEL). The deductible was never paid, so it is not in it;
    /// the set-off was part of what the client got, so it is (G7ა). No rate is applied (R1).
    /// </summary>
    public static BigInteger Reimbursed(IReadOnlyList<ClaimPayment> payments, DateOnly asOf)
    {
        var total = BigInteger.Zero;
        foreach (var p in payments) if (p.PaidOn <= asOf) total += p.NetPaidGel + p.OffsetGel;
        return total;
    }

    /// <summary>
    /// Why a claim cannot carry a regress, or null when it can. Only class 5.1 (owner, O7); not theft,
    /// which is no accident (v7); a line without a known coverage cannot be told apart from theft, so it
    /// is refused too. The claim's currency does not matter: the regress follows the GEL paid (R1).
    /// </summary>
    public static Ineligible? Ineligibility(ClaimFacts claim)
    {
        if (claim.ClassCode != "5.1") return Ineligible.Class;
        if (claim.CoverageCode is null) return Ineligible.Coverage;
        if (claim.CoverageCode == "theft") return Ineligible.Theft;
        return null;
    }

    /// <summary>The movement as it will be stored, with a reversal's amount taken from its original.</summary>
    public static Movement AsMovement(string id, NewMovement next, IReadOnlyList<Movement> movements)
    {
        if (next.Kind != MovementKind.Reversal) return new Movement(id, next.Kind, next.Amount, next.BookedOn, null);
        var target = movements.FirstOrDefault(m => m.Id == next.ReversesId);
        return new Movement(id, MovementKind.Reversal, -(target?.Amount ?? BigInteger.Zero), next.BookedOn, next.ReversesId);
    }

    /// <summary>What may still be claimed or confirmed on the claim by `asOf`: reimbursed less its other regress money.</summary>
    public static BigInteger Room(ClaimMoney claim, DateOnly asOf)
    {
        var others = BigInteger.Zero;
        foreach (var o in claim.Others) if (o.ReceivedOn <= asOf) others += o.Amount;
        return Reimbursed(claim.Payments, asOf) - others;
    }

    /// <summary>
    /// Whether `next` may be added to the case's `movements`. The case is checked as it would stand on
    /// every day from the new movement's day on — each day a movement, a payment or other regress money
    /// of the claim changes something — so that no day ever shows more received or cancelled than
    /// confirmed, or more claimed or confirmed than the claim's room (H82). Days before the movement
    /// are not touched by it. (A date here is always a real date, so the old check of the day's text is not needed.)
    /// </summary>
    public static MovementError? CheckMovement(IReadOnlyList<Movement> movements, NewMovement next, ClaimMoney claim)
    {
        if (next.Kind == MovementKind.Reversal)
        {
            var target = movements.FirstOrDefault(m => m.Id == next.ReversesId);
            if (target is null || target.Kind == MovementKind.Reversal) return MovementError.Target;
            if (movements.Any(m => m.ReversesId == target.Id)) return MovementError.Reversed;
            if (next.BookedOn < target.BookedOn) return MovementError.Date;
        }
        else
        {
            if (!(next.Amount > 0)) return MovementError.Amount;
            if (next.Kind == MovementKind.Cancelled && (next.Court is null || JsMath.Trim(next.Court.Number).Length == 0)) return MovementError.Document;
        }
        var after = movements.Append(AsMovement("(new)", next, movements)).ToList();
        var days = new HashSet<DateOnly> { next.BookedOn };
        foreach (var m in after) days.Add(m.BookedOn);
        foreach (var p in claim.Payments) days.Add(p.PaidOn);
        foreach (var o in claim.Others) days.Add(o.ReceivedOn);
        foreach (var day in days.Where(d => d >= next.BookedOn).Order())
        {
            var t = Totals(after, day);
            if (t.Balance < 0) return MovementError.Balance;
            if (next.Kind == MovementKind.Claimed && t.Claimed > Room(claim, day)) return MovementError.Cap;
            if (next.Kind == MovementKind.Confirmed && t.Confirmed > Room(claim, day)) return MovementError.Cap;
        }
        return null;
    }

    private static readonly CaseStatus[] MoneyStatuses = [CaseStatus.Accepted, CaseStatus.PartiallyReceived, CaseStatus.Closed];

    /// <summary>
    /// The status the money leaves the case in (docs/08 §4). A case closes only when something was
    /// confirmed and nothing is owed any more (G26): an empty new case is not "settled". Statuses set by
    /// people (evidence, demand, the debtor's answer) stay until money moves.
    /// </summary>
    public static CaseStatus StatusAfterMoney(CaseStatus current, CaseTotals t)
    {
        if (t.Confirmed > 0 && t.Balance == 0) return CaseStatus.Closed;
        if (t.Confirmed > 0 && t.Received > 0) return CaseStatus.PartiallyReceived;
        if (t.Confirmed > 0) return CaseStatus.Accepted;
        if (!MoneyStatuses.Contains(current)) return current;
        return t.Claimed > 0 ? CaseStatus.DemandSent : CaseStatus.Opened;
    }

    /// <summary>
    /// Who is expected to pay how much of the confirmed claim (owner, O3): the insurer up to its limit,
    /// the liable person the rest. Information only — both reduce the same one balance. With an insurer
    /// whose limit is not known, neither share can be told yet (null).
    /// </summary>
    public static ExpectedSplit ExpectedShares(BigInteger confirmed, InsurerShare? insurer)
    {
        if (insurer is null || insurer.Response == PartyResponse.Rejected) return new ExpectedSplit(BigInteger.Zero, confirmed);
        if (insurer.Limit is not { } limit) return new ExpectedSplit(null, null);
        var share = limit < confirmed ? limit : confirmed;
        return new ExpectedSplit(share, confirmed - share);
    }
}
