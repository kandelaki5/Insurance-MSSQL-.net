using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.Billing;

namespace Insurance.Application.Services;

/// <summary>
/// Premium of every policy line for a period from..to (both days included), in the owner's NBG list layout (legacy
/// src/lib/policy-premium-report.ts): one row per insurance class of a policy (docs/04 r9), from the premium ledger as booked by the
/// period end, each row at its own fixed GEL rate. Earned premium and UPR follow the same day count as the monthly report (r1–r3,
/// r6). A change issued as its own policy is its own row, marked as a change. The reinsurance columns follow the line's cessions the
/// same way (owner 14.10.2026). A line is listed when it has money in the period or its policy was cancelled in the period.
/// Amounts in whole tetri (long), exactly from the stored decimals.
/// </summary>
public static class PolicyPremiumReport
{
    public sealed record Row(
        string ContractNumber, string PolicyNumber, string Policyholder,
        /* A change issued as its own policy (docs/04 r10), as the NBG list's "Is Modification". */ bool IsModification,
        string ClassCode, string ClassName, string Currency,
        /* The line's premium in the policy's currency, as issued. */ long Premium,
        /* The line's premium in GEL as issued, when the policy was in force in the period (before cancellation); else 0. */ long StatisticalGel,
        /* Refund of a policy started before the period and cancelled in it, as a positive amount (docs/04 r8). */ long CorrectionGel,
        /* Premium booked in the period, cancellations included (docs/04 r7). */ long CorrectedGel,
        DateOnly StartDate, DateOnly ChangeDate, DateOnly EndDate,
        /* The day of the cancellation in force at the period end, from the ledger booked by then. */ DateOnly? CancelDate,
        long EarnedGel, long UnearnedGel, long RiPremiumGel, long RiEarnedGel, long RiUnearnedGel, long CommissionEarnedGel, long CommissionUnearnedGel);

    public sealed record Totals(long StatisticalGel, long CorrectionGel, long CorrectedGel, long EarnedGel, long UnearnedGel,
        long RiPremiumGel, long RiEarnedGel, long RiUnearnedGel, long CommissionEarnedGel, long CommissionUnearnedGel);

    public sealed record Report(DateOnly From, DateOnly To, IReadOnlyList<Row> Rows, Totals Totals);

    /// <summary>The period asked for, or the previous calendar month (the usual reporting period) — legacy reportPeriod.</summary>
    public static (DateOnly From, DateOnly To) ReportPeriod(string? from, string? to, DateOnly today)
    {
        if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to) && Dates.IsIsoDate(from) && Dates.IsIsoDate(to)
            && string.CompareOrdinal(from, to) <= 0 && !from.StartsWith("0000", StringComparison.Ordinal))
            return (Dates.ParseIsoDate(from), Dates.ParseIsoDate(to));
        var previousEnd = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        return (new DateOnly(previousEnd.Year, previousEnd.Month, 1), previousEnd);
    }

    private sealed record LedgerDb(Guid Id, Guid? CancelsId, string Kind, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo,
        Guid LineId, string ClassCode, string ClassName, int SortOrder, string PolicyNumber, DateOnly EffectiveFrom, DateOnly EffectiveTo, string Currency,
        Guid? ReplacesPolicyId, string ContractNumber, string Policyholder);

    private sealed record CessionDb(Guid Id, Guid? CancelsId, Guid LineId, decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);

    private static long Gel(IReadOnlyDictionary<Currency, long> byCurrency) => byCurrency.TryGetValue(Currency.GEL, out var v) ? v : 0;
    private static long Upr(IReadOnlyDictionary<Currency, PremiumPosition> byCurrency) => byCurrency.TryGetValue(Currency.GEL, out var v) ? v.Unearned : 0;

    public static async Task<Report> BuildAsync(Tx db, DateOnly from, DateOnly to)
    {
        var ledger = (await db.QueryAsync<LedgerDb>("""
            select t.id as Id, t.cancels_transaction_id as CancelsId, t.kind as Kind, t.amount as Amount, t.amount_gel as AmountGel, t.booked_on as BookedOn,
                   t.cover_from as CoverFrom, t.cover_to as CoverTo,
                   l.id as LineId, l.class_code as ClassCode, c.name_ka as ClassName, c.sort_order as SortOrder,
                   p.number as PolicyNumber, p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo, p.premium_currency as Currency,
                   p.replaces_policy_id as ReplacesPolicyId, k.number as ContractNumber, h.display_name as Policyholder
            from core.premium_transaction t
            join core.policy_line l on l.id = t.policy_line_id
            join core.insurance_class c on c.code = l.class_code
            join core.policy p on p.id = l.policy_id
            join core.contract k on k.id = p.contract_id
            join core.party h on h.id = k.policyholder_id
            where t.booked_on <= @to
            order by p.number, c.sort_order, t.created_at, t.id
            """, new { to })).ToList();

        var linked = EarnedPremium.LinkLedger(ledger.Select(r => new LedgerRow(r.Id.ToString(), r.CancelsId?.ToString(),
            new PremiumTransaction(new Money(Tetri(r.AmountGel), Currency.GEL), r.BookedOn, r.CoverFrom, r.CoverTo))).ToList());
        var lines = new List<(LedgerDb First, List<(LedgerDb Row, PremiumTransaction Tx)> Rows)>();
        var lineIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < ledger.Count; i++)
        {
            var row = ledger[i];
            if (!lineIndex.TryGetValue(row.LineId, out var at)) { lineIndex[row.LineId] = at = lines.Count; lines.Add((row, [])); }
            lines[at].Rows.Add((row, linked[i]));
        }

        // The start of each policy a change replaces.
        var replaced = ledger.Where(r => r.ReplacesPolicyId is not null).Select(r => r.ReplacesPolicyId!.Value).Distinct().ToList();
        var startOf = new Dictionary<Guid, DateOnly>();
        if (replaced.Count > 0)
            foreach (var s in await InChunksAsync(replaced, ids => db.QueryAsync<(Guid Id, DateOnly EffectiveFrom)>(
                "select p.id, p.effective_from from core.policy p where p.id in @ids", new { ids })))
                startOf[s.Id] = s.EffectiveFrom;

        // The reinsurer's share: every cession mirrors a ledger row; a cession's cancellation is tied to the cession it reverses.
        var cessions = (await db.QueryAsync<CessionDb>("""
            select c.id as Id, c.cancels_cession_id as CancelsId, t.policy_line_id as LineId, c.amount_gel as AmountGel, c.commission_gel as CommissionGel,
                   c.booked_on as BookedOn, c.cover_from as CoverFrom, c.cover_to as CoverTo
            from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id
            where c.booked_on <= @to
            order by c.created_at, c.id
            """, new { to })).ToList();
        PremiumTransaction Of(CessionDb c, decimal amount) => new(new Money(Tetri(amount), Currency.GEL), c.BookedOn, c.CoverFrom, c.CoverTo);
        var cededPremium = EarnedPremium.LinkLedger(cessions.Select(c => new LedgerRow(c.Id.ToString(), c.CancelsId?.ToString(), Of(c, c.AmountGel))).ToList());
        var cededCommission = EarnedPremium.LinkLedger(cessions.Select(c => new LedgerRow(c.Id.ToString(), c.CancelsId?.ToString(), Of(c, c.CommissionGel))).ToList());
        var ceded = new Dictionary<Guid, (List<PremiumTransaction> Premium, List<PremiumTransaction> Commission, List<(DateOnly On, long Premium)> Booked)>();
        for (var i = 0; i < cessions.Count; i++)
        {
            var c = cessions[i];
            if (!ceded.TryGetValue(c.LineId, out var line)) ceded[c.LineId] = line = ([], [], []);
            line.Premium.Add(cededPremium[i]);
            line.Commission.Add(cededCommission[i]);
            line.Booked.Add((c.BookedOn, cededPremium[i].Amount.Minor));
        }

        var rows = new List<Row>();
        foreach (var (first, entries) in lines)
        {
            var txs = entries.Select(e => e.Tx).ToList();
            var issued = entries.Where(e => e.Row.Kind == "issue").ToList();
            // The cancellation in force at the period end, from the ledger as booked by then — never the policy's cancel date today
            // (H24; docs/04 r14). A cancellation's cover starts on its cancel day.
            var undone = entries.Where(e => e.Row.Kind == "correction" && e.Row.CancelsId is not null).Select(e => e.Row.CancelsId!.Value).ToHashSet();
            var live = entries.Where(e => e.Row.Kind == "cancellation" && !undone.Contains(e.Row.Id)).Select(e => e.Row.CoverFrom).Order().ToList();
            DateOnly? cancelDate = live.Count > 0 && live[0] <= to ? live[0] : null;
            var coverEnd = cancelDate ?? first.EffectiveTo;
            var inForce = first.EffectiveFrom <= to && coverEnd > from;
            var inPeriod = entries.Where(e => e.Row.BookedOn >= from && e.Row.BookedOn <= to).ToList();
            var correctedGel = inPeriod.Sum(e => e.Tx.Amount.Minor);
            // A refund is shown as a positive amount.
            var correctionGel = first.EffectiveFrom < from ? inPeriod.Where(e => e.Row.Kind != "issue").Sum(e => -e.Tx.Amount.Minor) : 0;
            var earnedGel = Gel(EarnedPremium.EarnedInPeriod(txs, from, to));
            var unearnedGel = Upr(EarnedPremium.PremiumPositionOf(txs, to));
            var cancelledInPeriod = cancelDate is { } cd && cd >= from && cd <= to;
            var hasRi = ceded.TryGetValue(first.LineId, out var ri);
            var riPremiumGel = hasRi ? ri.Booked.Where(b => b.On >= from && b.On <= to).Sum(b => b.Premium) : 0;
            var riEarnedGel = hasRi ? Gel(EarnedPremium.EarnedInPeriod(ri.Premium, from, to)) : 0;
            var riUnearnedGel = hasRi ? Upr(EarnedPremium.PremiumPositionOf(ri.Premium, to)) : 0;
            var commissionEarnedGel = hasRi ? Gel(EarnedPremium.EarnedInPeriod(ri.Commission, from, to)) : 0;
            var commissionUnearnedGel = hasRi ? Upr(EarnedPremium.PremiumPositionOf(ri.Commission, to)) : 0;
            long[] amounts = [correctedGel, earnedGel, unearnedGel, riPremiumGel, riEarnedGel, riUnearnedGel, commissionEarnedGel, commissionUnearnedGel];
            if (amounts.All(m => m == 0) && !cancelledInPeriod) continue;
            rows.Add(new Row(
                first.ContractNumber, first.PolicyNumber, first.Policyholder, first.ReplacesPolicyId is not null,
                first.ClassCode, first.ClassName, first.Currency.Trim(),
                issued.Sum(e => Tetri(e.Row.Amount)),
                inForce ? issued.Sum(e => e.Tx.Amount.Minor) : 0,
                correctionGel, correctedGel,
                first.ReplacesPolicyId is { } replaces && startOf.TryGetValue(replaces, out var start) ? start : first.EffectiveFrom,
                first.EffectiveFrom, first.EffectiveTo, cancelDate,
                earnedGel, unearnedGel, riPremiumGel, riEarnedGel, riUnearnedGel, commissionEarnedGel, commissionUnearnedGel));
        }

        var totals = new Totals(rows.Sum(r => r.StatisticalGel), rows.Sum(r => r.CorrectionGel), rows.Sum(r => r.CorrectedGel), rows.Sum(r => r.EarnedGel),
            rows.Sum(r => r.UnearnedGel), rows.Sum(r => r.RiPremiumGel), rows.Sum(r => r.RiEarnedGel), rows.Sum(r => r.RiUnearnedGel),
            rows.Sum(r => r.CommissionEarnedGel), rows.Sum(r => r.CommissionUnearnedGel));
        return new Report(from, to, rows, totals);
    }
}
