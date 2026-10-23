using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using Rule = Insurance.Domain.Finance.RiCommission;

namespace Insurance.Application.Services;

/// <summary>
/// The reinsurer's commission receivable at a day, read from the ledgers (docs/05 3ე; Insurance.Domain.Finance.RiCommission holds the
/// rule), and its settlements written by the accountant (database/migrations/0011, legacy 0039). Every amount is read from its stored
/// decimal in whole minor units. Ported from Insurance-Software PR #362 (head 540e81a): src/lib/ri-commission.ts (the reader) and
/// src/app/(app)/accounting/ri-commission/actions.ts (recording and reversing a settlement).
/// </summary>
public static partial class RiCommissionLedger
{
    public const string Permission = "reinsurance.commission.settle";

    public sealed record TreatyCommission(Guid TreatyId, string Currency, BigInteger Accrued, BigInteger Settled, BigInteger Balance, string Number, string Name, string Reinsurer);

    public sealed record SettlementRow(Guid Id, Guid TreatyId, DateOnly SettledOn, string Kind, string Currency, BigInteger Amount, Guid? ReversesId, bool Reversed,
        string? Reference, string? Note);

    public sealed record RateUsed(string Currency, decimal Rate, DateOnly Date);

    /// <param name="ReceivableGel">N1-4 00140 in tetri.</param>
    public sealed record Report(DateOnly AsOf, IReadOnlyList<TreatyCommission> Positions, BigInteger ReceivableGel, IReadOnlyList<TreatyCommission> OverSettled,
        IReadOnlyList<RateUsed> Rates, IReadOnlyList<SettlementRow> Settlements);

    private sealed record CessionRow(Guid PremiumTransactionId, decimal SharePercent, Guid TreatyId, decimal Commission, DateOnly BookedOn, string Currency);

    private sealed record DatedAccrual(Guid TreatyId, string Currency, DateOnly On, BigInteger Commission);

    private sealed record StoredSettlement(Guid Id, Guid TreatyId, DateOnly SettledOn, string Kind, string Currency, decimal Amount, Guid? ReversesId, string? Reference, string? Note);

    /// <summary>
    /// The commission of every cession of a premium row ceded 100% in total (its cessions' shares add up to exactly 100%), with the day it
    /// is booked, per treaty, in the policy's currency (cancellations negative, as stored). Every cession is read; the totals are added up
    /// here, exactly, not in SQL.
    /// </summary>
    private static async Task<List<DatedAccrual>> FullyCededAccrualsAsync(Tx db)
    {
        var rows = (await db.QueryAsync<CessionRow>("""
            select c.premium_transaction_id as PremiumTransactionId, c.share_percent as SharePercent, c.treaty_id as TreatyId, c.commission as Commission,
                   c.booked_on as BookedOn, po.premium_currency as Currency
            from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id
            join core.policy po on po.id = l.policy_id
            order by c.booked_on, c.created_at, c.id
            """)).ToList();
        var full = Rule.FullyCededRows(rows.Select(r => new Rule.Share(Uuid.Text(r.PremiumTransactionId), r.SharePercent.ToString(CultureInfo.InvariantCulture))));
        return rows.Where(r => full.Contains(Uuid.Text(r.PremiumTransactionId)))
            .Select(r => new DatedAccrual(r.TreatyId, r.Currency.Trim(), r.BookedOn, ReportMath.Tetri(r.Commission))).ToList();
    }

    private static async Task<List<SettlementRow>> SettlementsByAsync(Tx db, DateOnly asOf)
    {
        var rows = (await db.QueryAsync<StoredSettlement>("""
            select s.id as Id, s.treaty_id as TreatyId, s.settled_on as SettledOn, s.kind as Kind, s.currency as Currency, s.amount as Amount,
                   s.reverses_id as ReversesId, s.reference as Reference, s.note as Note
            from core.ri_commission_settlement s
            where s.settled_on <= @asOf
            order by s.settled_on, s.created_at, s.id
            """, new { asOf })).ToList();
        var reversed = rows.Where(r => r.ReversesId is not null).Select(r => r.ReversesId!.Value).ToHashSet();
        return rows.Select(r => new SettlementRow(r.Id, r.TreatyId, r.SettledOn, r.Kind, r.Currency.Trim(), ReportMath.Tetri(r.Amount), r.ReversesId, reversed.Contains(r.Id),
            r.Reference, r.Note)).ToList();
    }

    /// <summary>The receivable from each treaty's reinsurer at the day, and N1-4 00140 at the day's rates.</summary>
    public static async Task<Report> BuildReportAsync(Tx db, Valuation valuation, DateOnly asOf)
    {
        var accruals = (await FullyCededAccrualsAsync(db)).Where(a => a.On <= asOf).ToList();
        var settlements = await SettlementsByAsync(db, asOf);
        var positions = Rule.CommissionPositions(
            accruals.Select(a => new Rule.Accrual(Uuid.Text(a.TreatyId), a.Currency, a.Commission)),
            settlements.Select(s => new Rule.Settlement(Uuid.Text(s.TreatyId), s.Currency, s.Amount)));
        var treaties = positions.Count == 0 ? new Dictionary<string, (Guid Id, string Number, string Name, string Reinsurer)>() :
            (await db.QueryAsync<(Guid Id, string Number, string Name, string Reinsurer)>("""
                select t.id, t.number, t.name, p.display_name
                from core.ri_treaty t join core.party p on p.id = t.reinsurer_party_id
                """)).ToDictionary(t => Uuid.Text(t.Id), StringComparer.Ordinal);
        TreatyCommission Named(Rule.Position p)
        {
            var t = treaties.GetValueOrDefault(p.TreatyId);
            return new TreatyCommission(Guid.Parse(p.TreatyId), p.Currency, p.Accrued, p.Settled, p.Balance, t.Number ?? "", t.Name ?? "", t.Reinsurer ?? "");
        }
        // By treaty number (localeCompare), then currency, as the page lists them; the rates and the over-settled ones in that order too
        // (the old reader took them in the order its unsorted query gave).
        var ordered = positions.Select(p => (Position: p, Named: Named(p)))
            .OrderBy(x => x.Named.Number, Comparer<string>.Create(Insurance.Domain.Regulatory.JsSemantics.LocaleCompare))
            .ThenBy(x => x.Named.Currency, Comparer<string>.Create(Insurance.Domain.Regulatory.JsSemantics.LocaleCompare))
            .ToList();
        // The rate is asked only for a foreign currency with something still owed.
        var rates = new List<RateUsed>();
        foreach (var currency in ordered.Where(x => x.Position.Balance.Sign > 0 && x.Position.Currency != "GEL").Select(x => x.Position.Currency).Distinct(StringComparer.Ordinal))
        {
            var (rate, date) = await valuation(currency, asOf);
            rates.Add(new RateUsed(currency, rate, date));
        }
        var (receivableGel, overSettled) = Rule.CommissionReceivableGel(ordered.Select(x => x.Position),
            c => c == "GEL" ? 1m : rates.First(r => r.Currency == c).Rate);
        var named = ordered.ToDictionary(x => x.Position, x => x.Named);
        return new Report(asOf, ordered.Select(x => x.Named).ToList(), receivableGel, overSettled.Select(p => named[p]).ToList(), rates, settlements);
    }

    /// <summary>N1-4 00140 of a day in tetri, for form 1 (its page and its download).</summary>
    public static async Task<BigInteger> ReceivableGelAsync(Tx db, Valuation valuation, DateOnly asOf) =>
        (await BuildReportAsync(db, valuation, asOf)).ReceivableGel;

    /// <summary>
    /// What a new settlement on a day may take at most, on a treaty in a currency (read in the transaction that writes it, under the
    /// treaty's lock): the least balance from that day on over every accrual and settlement already recorded, whatever their date.
    /// </summary>
    public static async Task<BigInteger> SettleableFromAsync(Tx db, Guid treatyId, string currency, DateOnly on)
    {
        var accruals = (await FullyCededAccrualsAsync(db)).Where(a => a.TreatyId == treatyId && a.Currency == currency).ToList();
        var settlements = await db.QueryAsync<(DateOnly SettledOn, decimal Amount)>(
            "select s.settled_on, s.amount from core.ri_commission_settlement s where s.treaty_id = @treatyId and s.currency = @currency", new { treatyId, currency });
        return Rule.LeastBalanceFrom(
            accruals.Select(a => new Rule.DatedAmount(a.On, a.Commission)).ToList(),
            settlements.Select(s => new Rule.DatedAmount(s.SettledOn, ReportMath.Tetri(s.Amount))).ToList(),
            on);
    }

    // ---------------------------------------------------------------- the accountant's actions

    /// <summary>What a press got: the outcome and the day to show the page for (null: the page's own default).</summary>
    public sealed record Answer(string Outcome, string? Date);

    /// <summary>Where the press is answered (legacy redirect): /accounting/ri-commission?date=…&amp;result=…, the date only when it is one.</summary>
    public static string BackTo(Answer answer) =>
        $"/accounting/ri-commission?{(answer.Date is { } d && Dates.IsIsoDate(d) ? $"date={d}&" : "")}result={answer.Outcome}";

    [GeneratedRegex(@"^[0-9]{1,16}(\.[0-9]{1,2})?\z")]
    private static partial Regex AmountText();

    [GeneratedRegex(@"^[A-Z]{3}\z")]
    private static partial Regex CurrencyCode();

    private static readonly string[] SettlementKinds = ["offset", "receipt"];

    /// <summary>A day typed in the form: an ISO date not after today (legacy isIsoDate(x) &amp;&amp; x &lt;= todayInTbilisi()).</summary>
    private static DateOnly? DayNotAfter(string text, DateOnly today) =>
        Dates.IsIsoDate(text) && !text.StartsWith("0000", StringComparison.Ordinal) && Dates.ParseIsoDate(text) is var day && day <= today ? day : null;

    /// <summary>A closed month: core.assert_periods_open's check violation, which names no constraint.</summary>
    private static bool PeriodClosed(Exception e) => DbError.From(e) is { Code: DbError.CheckViolation, Constraint: null };

    /// <summary>The treaty row locked for the rest of the transaction, so two settlements cannot both take the same balance; false when there is none.</summary>
    private static async Task<bool> LockTreatyAsync(Tx tx, Guid treatyId) =>
        await tx.ExecuteScalarAsync<int?>("select 1 from core.ri_treaty with (updlock, holdlock, rowlock) where id = @treatyId", new { treatyId }) is not null;

    /// <summary>
    /// Records a settlement of a reinsurer's commission (docs/05 3ე: offset or receipt, per treaty as a total; the accountant).
    /// Who may is the permission reinsurance.commission.settle, for the user of the session — never from the form: a quick check
    /// first, the binding one first in the transaction. The treaty is locked so two settlements cannot both take the same balance;
    /// a settlement above what is left to settle in that currency — on its day and on every later day of an accrual or settlement
    /// already recorded — is refused. The database keeps the day in an open month.
    /// </summary>
    public static async Task<Answer> RecordAsync(Db db, CurrentUser user, string? treatyIdText, string? settledOnText, string? kindText, string? currencyText,
        string? amountRaw, string? referenceRaw, string? noteRaw, DateOnly? today = null)
    {
        if (!await PermissionStore.HasPermissionAsync(db, user, Permission)) return new("not-allowed", null);
        var treatyText = treatyIdText ?? "";
        var settledOn = settledOnText ?? "";
        var kind = kindText ?? "";
        var currency = currencyText ?? "";
        var amountText = Insurance.Domain.Common.JsText.Trim(amountRaw ?? "");
        var reference = Insurance.Domain.Common.JsText.Trim(referenceRaw ?? "") is { Length: > 0 } r ? r : null;
        var note = Insurance.Domain.Common.JsText.Trim(noteRaw ?? "") is { Length: > 0 } n ? n : null;
        var day = DayNotAfter(settledOn, today ?? db.Today());
        if (!Uuid.TryParse(treatyText, out var treatyId) || day is null || !SettlementKinds.Contains(kind, StringComparer.Ordinal)
            || !CurrencyCode().IsMatch(currency) || !AmountText().IsMatch(amountText))
            return new("invalid", settledOn);
        var amount = decimal.Parse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var tetri = ReportMath.Tetri(amount);
        if (tetri.Sign <= 0) return new("invalid", settledOn);
        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                if (!await PermissionStore.HasPermissionAsync(tx, user, Permission)) return "not-allowed";
                if (!await LockTreatyAsync(tx, treatyId)) return "invalid";
                // Not above what is left on its day nor on any later day already recorded (a backdated one must not uncover a later one).
                if (tetri > await SettleableFromAsync(tx, treatyId, currency, day.Value)) return "exceeds";
                await tx.ExecuteAsync("""
                    insert into core.ri_commission_settlement (id, treaty_id, settled_on, kind, currency, amount, reference, note, created_by)
                    values (@id, @treatyId, @day, @kind, @currency, @amount, @reference, @note, @userId)
                    """, new { id = Guid.NewGuid(), treatyId, day = day.Value, kind, currency, amount, reference, note, userId = user.Id });
                return "ok";
            });
        }
        catch (Exception e) when (PeriodClosed(e))
        {
            outcome = "period-closed";
        }
        return new(outcome, settledOn);
    }

    /// <summary>
    /// Reverses a recorded settlement: a new row that names it, the amount negated, on a day not before it (the database checks the
    /// mirror and that it is reversed once, 0011); the settlement itself is never changed. The same permission as recording one.
    /// </summary>
    public static async Task<Answer> ReverseAsync(Db db, CurrentUser user, string? settlementIdText, string? reversedOnText, DateOnly? today = null)
    {
        if (!await PermissionStore.HasPermissionAsync(db, user, Permission)) return new("not-allowed", null);
        var reversedOn = reversedOnText ?? "";
        var day = DayNotAfter(reversedOn, today ?? db.Today());
        if (!Uuid.TryParse(settlementIdText, out var settlementId) || day is null) return new("invalid", reversedOn);
        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                if (!await PermissionStore.HasPermissionAsync(tx, user, Permission)) return "not-allowed";
                var original = await tx.QuerySingleOrDefaultAsync<StoredSettlement>("""
                    select id as Id, treaty_id as TreatyId, settled_on as SettledOn, kind as Kind, currency as Currency, amount as Amount,
                           reverses_id as ReversesId, reference as Reference, note as Note
                    from core.ri_commission_settlement where id = @settlementId
                    """, new { settlementId });
                if (original is null || original.ReversesId is not null) return "state";
                await LockTreatyAsync(tx, original.TreatyId);
                if (await tx.ExecuteScalarAsync<int?>("select 1 from core.ri_commission_settlement where reverses_id = @settlementId", new { settlementId }) is not null)
                    return "already";
                if (day.Value < original.SettledOn) return "invalid";
                await tx.ExecuteAsync("""
                    insert into core.ri_commission_settlement (id, treaty_id, settled_on, kind, currency, amount, reverses_id, created_by)
                    values (@id, @treatyId, @day, @kind, @currency, @amount, @settlementId, @userId)
                    """, new { id = Guid.NewGuid(), treatyId = original.TreatyId, day = day.Value, kind = original.Kind, currency = original.Currency.Trim(),
                    amount = -original.Amount, settlementId, userId = user.Id });
                return "reversed";
            });
        }
        catch (Exception e) when (PeriodClosed(e))
        {
            outcome = "period-closed";
        }
        catch (Exception e) when (DbError.From(e)?.Code == DbError.UniqueViolation)
        {
            outcome = "already";
        }
        return new(outcome, reversedOn);
    }
}
