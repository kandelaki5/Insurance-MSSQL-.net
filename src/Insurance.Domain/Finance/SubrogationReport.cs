using System.Globalization;
using System.Numerics;

namespace Insurance.Domain.Finance;

/// <summary>A value on a date, or 'unknown' when the audit cannot tell it for certain (OrUnknown&lt;T&gt; of the old code).</summary>
public readonly record struct OrUnknown<T>(bool IsUnknown, T Value)
{
    public static OrUnknown<T> Unknown => new(true, default!);

    public static OrUnknown<T> Known(T value) => new(false, value);

    public static implicit operator OrUnknown<T>(T value) => Known(value);

    public override string ToString() => IsUnknown ? SubrogationReport.UnknownText : $"{Value}";
}

/// <summary>A value as the audit recorded it: `At` is the writing transaction's time (audit_log.at), oldest first by audit id.</summary>
public sealed record Recorded<T>(string At, T Value);

public enum PartyRole
{
    LiablePerson,
    LiabilityInsurer,
}

/// <summary>What of a party can change and is shown on a date: its answer, the day of it, and the insurer's limit.</summary>
public sealed record PartyTerms(PartyResponse? Response, DateOnly? ResponseOn, BigInteger? Limit);

/// <summary>A party's terms on a date: the limit may be unknown on its own, while the answer is known.</summary>
public sealed record PartyTermsOnDate(PartyResponse? Response, DateOnly? ResponseOn, OrUnknown<BigInteger?> Limit);

public sealed record CaseBasis(string Kind, string Number, DateOnly Date);

/// <param name="StatusNow">The status stored on the case: today's. The date's comes from `StatusHistory`.</param>
/// <param name="StatusHistory">The case's status as the audit recorded it, oldest first (Q-R2); null or empty = no history.</param>
public sealed record CaseRecord(
    string CaseId,
    string CaseNumber,
    string ClaimId,
    string ClaimNumber,
    string PolicyNumber,
    string ClassCode,
    DateOnly OpenedOn,
    CaseStatus StatusNow,
    CaseBasis? Basis,
    IReadOnlyList<Recorded<CaseStatus>>? StatusHistory = null);

/// <param name="Response">Today's answer; its day is kept, its earlier values are only in the audit.</param>
/// <param name="History">The party as the audit recorded it over time, oldest first (Q-R3); null or empty = no history.</param>
public sealed record PartyRecord(string Id, string Name, PartyRole Role, BigInteger? Limit, PartyResponse? Response, DateOnly? ResponseOn, IReadOnlyList<Recorded<PartyTerms>>? History = null);

/// <summary>A movement with what the card shows of it.</summary>
/// <param name="PayerId">The party row (subrogation_party.id) that paid a receipt.</param>
public sealed record MovementRecord(
    string Id,
    MovementKind Kind,
    BigInteger Amount,
    DateOnly BookedOn,
    string? ReversesId,
    string DocumentKind,
    string? DocumentNumber,
    DateOnly? DocumentDate,
    string? PayerId,
    string? Note) : Movement(Id, Kind, Amount, BookedOn, ReversesId);

/// <summary>For a reversal: the kind it undoes, and the original's id and day.</summary>
public sealed record Undoes(MovementKind Kind, string Id, DateOnly BookedOn);

public sealed record HistoryDocument(string Kind, string? Number, DateOnly? Date);

/// <param name="Amount">Signed, as stored: negative only on a reversal.</param>
/// <param name="BalanceEffect">What the line does to the balance: + for a confirmation, − for a receipt or a cancellation, 0 for a claim.</param>
/// <param name="BalanceAfter">The balance after this line, on the line's day.</param>
public sealed record HistoryLine(string Id, DateOnly BookedOn, MovementKind Kind, Undoes? Undoes, BigInteger Amount, BigInteger BalanceEffect, HistoryDocument Document, string? Payer, string? Note, BigInteger BalanceAfter);

public sealed record CaseCardHeader(string CaseNumber, string ClaimNumber, string PolicyNumber, string ClassCode, DateOnly OpenedOn, CaseStatus StatusNow, CaseBasis? Basis, OrUnknown<CaseStatus> StatusOnDate);

/// <summary>Each party with its terms on the date (or unknown) and today's, apart (Q-R3).</summary>
public sealed record CaseCardParty(string Name, PartyRole Role, OrUnknown<PartyTermsOnDate> OnDate, PartyTerms Now);

/// <summary>The split on today's business day (H89): Day is today.</summary>
public sealed record ExpectedNow(DateOnly Day, BigInteger? Insurer, BigInteger? Person);

/// <param name="Expected">
/// Who is expected to pay how much of what was confirmed on the date (O3), with the insurer's terms of
/// that date; unknown terms give no split (null). Information only, never a second debt.
/// </param>
/// <param name="ExpectedNow">
/// The split on today's business day, apart from the date asked for (Codex H89): what was confirmed by
/// today, with today's limit and with the answer whose own day is not after today. A movement or an
/// answer dated after today does not count. Information only, never a second debt.
/// </param>
public sealed record CaseCard(DateOnly AsOf, CaseCardHeader Header, CaseTotals Totals, IReadOnlyList<CaseCardParty> Parties, ExpectedSplit Expected, ExpectedNow ExpectedNow, IReadOnlyList<HistoryLine> History);

/// <param name="Debtors">The parties' names, each once, the liable person first.</param>
/// <param name="Accrued">Confirmed on the date = the accrued regress claim (O1).</param>
public sealed record ReportRow(string CaseNumber, string ClaimNumber, string PolicyNumber, IReadOnlyList<string> Debtors, BigInteger Accrued, BigInteger Claimed, BigInteger Received, BigInteger Cancelled, BigInteger Balance, OrUnknown<CaseStatus> StatusOnDate, CaseStatus StatusNow);

public enum OutsideKind
{
    Subrogation,
    Salvage,
}

/// <summary>Money of a claim received outside any regress case: old subrogation rows and salvage (Q-R4).</summary>
/// <param name="Amount">Signed: a reversal of an old row is negative.</param>
public sealed record OutsideRecovery(string ClaimNumber, OutsideKind Kind, DateOnly ReceivedOn, BigInteger Amount);

public sealed record ReportTotal(BigInteger Accrued, BigInteger Claimed, BigInteger Received, BigInteger Cancelled, BigInteger Balance);

public sealed record OutsideLine(string ClaimNumber, OutsideKind Kind, BigInteger Received);

public sealed record OutsideTotal(BigInteger Subrogation, BigInteger Salvage);

/// <param name="Total">The totals of the rows shown: regress cases only.</param>
/// <param name="Outside">Shown apart, never added to `Total` (Q-R4): per claim and kind, received by the date.</param>
public sealed record Report(DateOnly AsOf, IReadOnlyList<ReportRow> Rows, ReportTotal Total, IReadOnlyList<OutsideLine> Outside, OutsideTotal OutsideTotal);

/// <param name="OnlyWithBalance">Q-R1: show only the cases with something still owed on the date (balance ≠ 0). Off by default: every case opened by then.</param>
public sealed record ReportOptions(bool OnlyWithBalance = false, IReadOnlyList<OutsideRecovery>? Outside = null);

public sealed record ReportCase(CaseRecord Record, IReadOnlyList<PartyRecord> Parties, IReadOnlyList<Movement> Movements);

public enum PeriodKind
{
    Month,
    Quarter,
    Year,
}

public enum PeriodProblem
{
    /// <summary>Not a real date.</summary>
    Date,

    /// <summary>The first day is after the second.</summary>
    Order,
}

/// <summary>What moved in a period: the totals' changes and the balance's.</summary>
public sealed record PeriodMovement(BigInteger Claimed, BigInteger Confirmed, BigInteger Received, BigInteger Cancelled, BigInteger BalanceChange);

/// <summary>
/// What a regress case shows, and the report "regress claims on a date" (owner, O1: the accrued claim is
/// needed for audit; income stays on receipt) — a read model over the append-only ledger of
/// <see cref="Subrogation"/>, free of the database (Codex INS-MOTOR-REGRESS-READONLY-REPORT-PREP-20261009).
/// Nothing here writes, and nothing is wired to a screen yet: docs/tests/2026-10-09-regress-report-design.md. (subrogation-report.ts)
///
/// - Every amount on a date comes from the movements booked up to and including that day; a later
///   movement does not count yet (G12), a reversal counts in its own day against the kind it undoes (G10ა).
/// - Claimed, confirmed (= accrued, O1), received and cancelled are kept apart; the balance is
///   confirmed − received − cancelled, once per case, whoever paid (3.3): the parties never add a second debt.
/// - A case is in the report on a date by its opening day, never by its status today: a case closed
///   now still shows what it owed then.
/// - Only a case's own movements go into its row and the totals. Regress money of a claim outside any
///   case (old rows) and salvage are shown apart and never added to them, nor made into a case (Q-R4).
/// - On a date, the case's status and each party's answer and limit are those the audit recorded by that
///   day; today's are shown apart; with no record by then they are "unknown" (Q-R2, Q-R3).
/// - Owner's answers to Q-R1–Q-R5 and R3: docs/tests/2026-10-09-regress-report-design.md §6, 09.10.2026.
/// - Amounts are tetri (big integers): sums are exact.
/// </summary>
public static class SubrogationReport
{
    /// <summary>The text the old code used for an unknown value.</summary>
    public const string UnknownText = "unknown";

    private static readonly CaseStatus[] Money = [CaseStatus.Accepted, CaseStatus.PartiallyReceived, CaseStatus.Closed];

    /// <summary>
    /// What the audit can tell for certain about a value on a day (Codex H88). `audit_log.at` is when the
    /// writing transaction began (`now()`), not when it committed: a change begun on day D may have taken
    /// effect only on D + 1 or later. So a change can be placed for certain only on the days before the one
    /// it began: the first recorded value (the row as created) holds on every day before the first change
    /// began, and from the day a change began the value on that day is not certain — unknown. Changes that
    /// leave the value as it was do not count.
    /// </summary>
    private static OrUnknown<T> CertainOn<T>(IReadOnlyList<Recorded<T>>? history, DateOnly asOf, Func<T, T, bool> same)
    {
        if (history is null || history.Count == 0) return OrUnknown<T>.Unknown;
        var first = history[0].Value;
        var previous = first;
        foreach (var r in history.Skip(1))
        {
            if (!same(r.Value, previous) && TbilisiDay(r.At) <= asOf) return OrUnknown<T>.Unknown;
            previous = r.Value;
        }
        return first;
    }

    /// <summary>
    /// The case's status on a date (Q-R2), never a later one and never a guess (Codex H88):
    /// - once something was confirmed by that day, the money's status on that day, from the ledger by
    ///   business day (accepted, partially received, closed — G26): certain;
    /// - before that, the status the case was opened with, as long as no change of status had begun by that
    ///   day; after a change began it is unknown (`CertainOn`), and a money status there is unknown too.
    /// Today's status never stands in for it.
    /// </summary>
    public static OrUnknown<CaseStatus> StatusOn(IReadOnlyList<Recorded<CaseStatus>>? history, IReadOnlyList<Movement> movements, DateOnly asOf, DateOnly openedOn)
    {
        // 'opened' holds from the case's opening business day (opened_on), not from its audit time; before it, no status.
        if (asOf < openedOn) return OrUnknown<CaseStatus>.Unknown;
        var t = Subrogation.Totals(movements, asOf);
        if (t.Confirmed > 0) return Subrogation.StatusAfterMoney(CaseStatus.Accepted, t);
        var certain = CertainOn(history, asOf, (x, y) => x == y);
        return certain.IsUnknown || Money.Contains(certain.Value) ? OrUnknown<CaseStatus>.Unknown : certain;
    }

    /// <summary>
    /// A party's terms on a date (Q-R3), never later ones (Codex H88):
    /// - the answer has its own business day, `response_on`: the answer in force is the recorded one with
    ///   the latest such day on or before the date, whenever it was recorded; with none there was no answer;
    /// - the limit has no business day at all, not even for its first value: the audit's time of the row's
    ///   creation is when that transaction began, which says neither when the limit took effect nor that it
    ///   was in force on an earlier day. So on a date it is unknown (Codex H88), until the limit is recorded
    ///   with a business day of its own (owner question Q-R6). Today's limit is in `Now`.
    /// No history at all: unknown.
    /// </summary>
    public static OrUnknown<PartyTermsOnDate> TermsOn(IReadOnlyList<Recorded<PartyTerms>>? history, DateOnly asOf)
    {
        if (history is null || history.Count == 0) return OrUnknown<PartyTermsOnDate>.Unknown;
        PartyResponse? response = null;
        DateOnly? responseOn = null;
        foreach (var r in history)
        {
            var (rResponse, rResponseOn, _) = r.Value;
            if (rResponse is not null && rResponseOn is { } day && day <= asOf && (responseOn is null || day >= responseOn.Value))
            {
                response = rResponse;
                responseOn = day;
            }
        }
        return new PartyTermsOnDate(response, responseOn, OrUnknown<BigInteger?>.Unknown);
    }

    /// <summary>
    /// The O3 split on a date: no insurer — the person owes all; an insurer that had rejected by then — the
    /// same; otherwise the insurer's limit of that day is needed, and an unknown one gives no split (null),
    /// never a 0 presented as known.
    /// </summary>
    private static ExpectedSplit ExpectedOn(BigInteger confirmed, PartyRecord? insurer, OrUnknown<PartyTermsOnDate>? terms)
    {
        if (insurer is null) return Subrogation.ExpectedShares(confirmed, null);
        if (terms is not { IsUnknown: false } known) return new ExpectedSplit(null, null);
        if (known.Value.Response == PartyResponse.Rejected) return Subrogation.ExpectedShares(confirmed, new InsurerShare(null, PartyResponse.Rejected));
        if (known.Value.Limit.IsUnknown) return new ExpectedSplit(null, null);
        return Subrogation.ExpectedShares(confirmed, new InsurerShare(known.Value.Limit.Value, known.Value.Response));
    }

    /// <summary>
    /// Today's split (H89): what was confirmed by today's business day only, today's limit, and the answer in
    /// force today — chosen from the answer history by its own business day, as `TermsOn` chooses it for any
    /// date: an earlier answer still in force is kept, a later-dated one counts only from its day. Without a
    /// history the current row tells only its own answer: if that answer is dated after today, what was in
    /// force before it is not known, and so the split is not given (null). No answer recorded at all is no answer.
    /// </summary>
    private static ExpectedSplit SplitToday(IReadOnlyList<Movement> movements, PartyRecord? insurer, DateOnly today)
    {
        var confirmed = Subrogation.Totals(movements, today).Confirmed;
        if (insurer is null) return Subrogation.ExpectedShares(confirmed, null);
        OrUnknown<PartyResponse?> response;
        if (insurer.History is { Count: > 0 })
        {
            var terms = TermsOn(insurer.History, today);
            response = terms.IsUnknown ? OrUnknown<PartyResponse?>.Unknown : terms.Value.Response;
        }
        else if (insurer.Response is null)
        {
            response = (PartyResponse?)null;
        }
        else
        {
            response = insurer.ResponseOn is { } on && on <= today ? insurer.Response : OrUnknown<PartyResponse?>.Unknown;
        }
        if (response.IsUnknown) return new ExpectedSplit(null, null);
        return Subrogation.ExpectedShares(confirmed, new InsurerShare(insurer.Limit, response.Value));
    }

    /// <summary>Today's business day in Tbilisi, for the reader to pass as `today`.</summary>
    public static DateOnly TodayInTbilisi(DateTimeOffset? now = null) =>
        TbilisiDay((now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    /// <summary>
    /// The calendar day in Tbilisi of a timestamp: the day a change counts from. The timestamp is ISO 8601 text; one
    /// without an offset is taken as UTC (the old code's Date.parse took it in the server's own time zone).
    /// </summary>
    public static DateOnly TbilisiDay(string at)
    {
        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            throw new ArgumentException($"not a timestamp: {at}");
        }
        return Insurance.Domain.Common.TbilisiTime.Date(instant);
    }

    /// <summary>
    /// The value in force on `asOf`: the last one recorded on or before that day (Tbilisi time), in the
    /// order recorded. Nothing recorded by then — no history, or a history that starts later — is unknown:
    /// today's value is never put in its place (Q-R2, Q-R3).
    /// </summary>
    public static OrUnknown<T> OnDate<T>(IReadOnlyList<Recorded<T>>? history, DateOnly asOf)
    {
        // Only for what has no business day of its own (an insurer's limit): see TermsOn and StatusOn (H88).
        var found = OrUnknown<T>.Unknown;
        foreach (var r in history ?? []) if (TbilisiDay(r.At) <= asOf) found = r.Value;
        return found;
    }

    private static BigInteger SignOnBalance(MovementKind kind, string id) => kind switch
    {
        MovementKind.Claimed => 0,
        MovementKind.Confirmed => 1,
        MovementKind.Received => -1,
        MovementKind.Cancelled => -1,
        _ => throw new InvalidOperationException($"movement {id} reverses nothing it can"),
    };

    /// <summary>
    /// The movements up to and including `asOf` in the order the card shows them: by day, then as read,
    /// except that a reversal always comes after the movement it undoes (Codex H87). The read order is not the
    /// write order: `created_at` is the start of the writing transaction, and a reversal whose transaction
    /// began before its original's (it waited on the claim's lock) carries the earlier time; ids are random.
    /// A reversal whose original is not in `movements` at all is refused: the card would be wrong.
    /// </summary>
    public static List<MovementRecord> InShowOrder(IReadOnlyList<MovementRecord> movements, DateOnly asOf)
    {
        var ids = movements.Select(m => m.Id).ToHashSet();
        foreach (var m in movements)
        {
            if (m.Kind == MovementKind.Reversal && !ids.Contains(m.ReversesId ?? "")) throw new InvalidOperationException($"movement {m.Id} reverses {m.ReversesId ?? "null"}, which is not on the case");
        }
        // A stable sort by day keeps the read order within a day.
        var byDay = movements.Where(m => m.BookedOn <= asOf).OrderBy(m => m.BookedOn).ToList();
        var output = new List<MovementRecord>();
        var placed = new HashSet<string>();
        var waiting = new Dictionary<string, List<MovementRecord>>();
        void Place(MovementRecord m)
        {
            output.Add(m);
            placed.Add(m.Id);
            if (waiting.TryGetValue(m.Id, out var rs))
            {
                foreach (var r in rs.ToList()) Place(r);
            }
            waiting.Remove(m.Id);
        }
        foreach (var m in byDay)
        {
            var target = m.Kind == MovementKind.Reversal ? m.ReversesId : null;
            if (!string.IsNullOrEmpty(target) && !placed.Contains(target))
            {
                if (!waiting.TryGetValue(target, out var list)) waiting[target] = list = [];
                list.Add(m);
            }
            else
            {
                Place(m);
            }
        }
        // A reversal is never dated before its original (the ledger refuses it), so nothing can still wait here.
        if (waiting.Count > 0) throw new InvalidOperationException($"reversals dated before their originals: {string.Join(", ", waiting.Keys)}");
        return output;
    }

    /// <summary>The case on a date: header, totals, parties with the expected split, and its history up to that day.</summary>
    public static CaseCard CaseCardOf(CaseRecord c, IReadOnlyList<PartyRecord> parties, IReadOnlyList<MovementRecord> movements, DateOnly asOf, DateOnly today)
    {
        var byId = new Dictionary<string, MovementRecord>();
        foreach (var m in movements) byId[m.Id] = m;
        var names = new Dictionary<string, string>();
        foreach (var p in parties) names[p.Id] = p.Name;
        BigInteger claimed = 0, confirmed = 0, received = 0, cancelled = 0;
        var history = new List<HistoryLine>();
        foreach (var m in InShowOrder(movements, asOf))
        {
            var original = m.Kind == MovementKind.Reversal ? (byId.TryGetValue(m.ReversesId ?? "", out var o) ? o : null) : null;
            var kind = original?.Kind ?? m.Kind;
            var effect = SignOnBalance(kind, m.Id) * m.Amount;
            switch (kind)
            {
                case MovementKind.Claimed: claimed += m.Amount; break;
                case MovementKind.Confirmed: confirmed += m.Amount; break;
                case MovementKind.Received: received += m.Amount; break;
                case MovementKind.Cancelled: cancelled += m.Amount; break;
            }
            history.Add(new HistoryLine(
                m.Id, m.BookedOn, m.Kind,
                original is null ? null : new Undoes(kind, original.Id, original.BookedOn),
                m.Amount, effect,
                new HistoryDocument(m.DocumentKind, m.DocumentNumber, m.DocumentDate),
                string.IsNullOrEmpty(m.PayerId) ? null : names.TryGetValue(m.PayerId, out var name) ? name : null,
                m.Note,
                confirmed - received - cancelled));
        }
        var t = Subrogation.Totals(movements, asOf);
        var insurer = parties.FirstOrDefault(p => p.Role == PartyRole.LiabilityInsurer);
        OrUnknown<PartyTermsOnDate>? insurerOnDate = insurer is null ? (OrUnknown<PartyTermsOnDate>?)null : TermsOn(insurer.History, asOf);
        var split = SplitToday(movements, insurer, today);
        return new CaseCard(
            asOf,
            new CaseCardHeader(c.CaseNumber, c.ClaimNumber, c.PolicyNumber, c.ClassCode, c.OpenedOn, c.StatusNow, c.Basis, StatusOn(c.StatusHistory, movements, asOf, c.OpenedOn)),
            t,
            parties.Select(p => new CaseCardParty(p.Name, p.Role, TermsOn(p.History, asOf), new PartyTerms(p.Response, p.ResponseOn, p.Limit))).ToList(),
            ExpectedOn(t.Confirmed, insurer, insurerOnDate),
            new ExpectedNow(today, split.Insurer, split.Person),
            history);
    }

    /// <summary>CaseCardOf with today given as text: a text that is not a real 'YYYY-MM-DD' date is refused.</summary>
    public static CaseCard CaseCardOf(CaseRecord c, IReadOnlyList<PartyRecord> parties, IReadOnlyList<MovementRecord> movements, DateOnly asOf, string today)
    {
        if (!Dates.IsIsoDate(today) || today.StartsWith("0000", StringComparison.Ordinal)) throw new ArgumentException($"today must be a business day: {today}");
        return CaseCardOf(c, parties, movements, asOf, Dates.ParseIsoDate(today));
    }

    /// <summary>
    /// "Regress claims on a date": every case opened by that day — whatever its status today — with what was
    /// claimed, accrued (confirmed), received and cancelled by then, and the balance; and the column totals.
    /// </summary>
    public static Report ReportOnDate(IReadOnlyList<ReportCase> cases, DateOnly asOf, ReportOptions? options = null)
    {
        options ??= new ReportOptions();
        var rows = new List<ReportRow>();
        foreach (var (record, parties, movements) in cases)
        {
            if (record.OpenedOn > asOf) continue;
            var t = Subrogation.Totals(movements, asOf);
            var debtors = parties.OrderBy(p => p.Role == PartyRole.LiablePerson ? 0 : 1).Select(p => p.Name).Distinct().ToList();
            rows.Add(new ReportRow(
                record.CaseNumber, record.ClaimNumber, record.PolicyNumber, debtors,
                t.Confirmed, t.Claimed, t.Received, t.Cancelled, t.Balance,
                StatusOn(record.StatusHistory, movements, asOf, record.OpenedOn), record.StatusNow));
        }
        var shown = (options.OnlyWithBalance ? rows.Where(r => r.Balance != 0) : rows).OrderBy(r => r.CaseNumber, StringComparer.Ordinal).ToList();
        var total = new ReportTotal(
            shown.Aggregate(BigInteger.Zero, (s, r) => s + r.Accrued),
            shown.Aggregate(BigInteger.Zero, (s, r) => s + r.Claimed),
            shown.Aggregate(BigInteger.Zero, (s, r) => s + r.Received),
            shown.Aggregate(BigInteger.Zero, (s, r) => s + r.Cancelled),
            shown.Aggregate(BigInteger.Zero, (s, r) => s + r.Balance));
        var outside = new Dictionary<(string, OutsideKind), BigInteger>();
        BigInteger subrogation = 0, salvage = 0;
        foreach (var o in options.Outside ?? [])
        {
            if (o.ReceivedOn > asOf) continue;
            var key = (o.ClaimNumber, o.Kind);
            outside[key] = (outside.TryGetValue(key, out var sum) ? sum : 0) + o.Amount;
            if (o.Kind == OutsideKind.Subrogation) subrogation += o.Amount; else salvage += o.Amount;
        }
        // By claim number, then kind, as text: 'salvage' before 'subrogation'.
        var outsideLines = outside.Select(e => new OutsideLine(e.Key.Item1, e.Key.Item2, e.Value))
            .OrderBy(l => l.ClaimNumber, StringComparer.Ordinal).ThenBy(l => KindText(l.Kind), StringComparer.Ordinal).ToList();
        return new Report(asOf, shown, total, outsideLines, new OutsideTotal(subrogation, salvage));
    }

    private static string KindText(OutsideKind kind) => kind == OutsideKind.Subrogation ? "subrogation" : "salvage";

    /// <summary>Q-R5: the month, quarter or year that contains `day`, both ends included.</summary>
    public static DateRange PeriodOf(PeriodKind kind, DateOnly day)
    {
        var y = day.Year;
        var m = day.Month;
        var first = kind == PeriodKind.Year ? 1 : kind == PeriodKind.Quarter ? (m - 1) / 3 * 3 + 1 : m;
        var months = kind == PeriodKind.Year ? 12 : kind == PeriodKind.Quarter ? 3 : 1;
        var (ey, em, ed) = Dates.JsDateUtc(y, first - 1 + months, 0);
        return new DateRange(new DateOnly(y, first, 1), new DateOnly((int)ey, (int)em, (int)ed));
    }

    /// <summary>PeriodOf for a day given as text: a text that is not a real 'YYYY-MM-DD' date is refused.</summary>
    public static DateRange PeriodOf(PeriodKind kind, string day)
    {
        if (!Dates.IsIsoDate(day) || day.StartsWith("0000", StringComparison.Ordinal)) throw new ArgumentException($"not a date: {day}");
        return PeriodOf(kind, Dates.ParseIsoDate(day));
    }

    /// <summary>Q-R5: a free period is two real days, the first not after the second.</summary>
    public static PeriodProblem? CheckPeriod(string from, string to)
    {
        if (!Dates.IsIsoDate(from) || !Dates.IsIsoDate(to)) return PeriodProblem.Date;
        return string.CompareOrdinal(from, to) <= 0 ? null : PeriodProblem.Order;
    }

    /// <summary>
    /// What moved in a period (`from`..`to`, both included): a reversal counts in its own period against the
    /// kind it undoes, so an October receipt reversed in November leaves October as it was and shows −600 in
    /// November (G10ა). The received column is the regress income of the period (O1: income on receipt).
    /// </summary>
    public static PeriodMovement MovedIn(IReadOnlyList<Movement> movements, DateOnly from, DateOnly to)
    {
        var before = Subrogation.Totals(movements, from.AddDays(-1));
        var after = Subrogation.Totals(movements, to);
        return new PeriodMovement(
            after.Claimed - before.Claimed, after.Confirmed - before.Confirmed,
            after.Received - before.Received, after.Cancelled - before.Cancelled,
            after.Balance - before.Balance);
    }

    /// <summary>An amount for a reader: '1 000.00'-style is the screen's business; here the exact decimal text.</summary>
    public static string Lari(BigInteger tetri) => Subrogation.FromTetri(tetri);
}
