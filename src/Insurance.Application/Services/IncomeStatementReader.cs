using System.Globalization;
using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using Insurance.Domain.Regulatory;
using static Insurance.Application.Services.ReportMath;

namespace Insurance.Application.Services;

/// <summary>
/// „ფორმა 1"'s profit and loss from the program's own data (owner, chat 20.10.2026; docs/05 ბ6; old program PR #366, head d251848,
/// src/lib/income-statement-report.ts buildIncomeStatementSource — ported statement by statement): every figure from the start of the
/// reporting date's year to that date, by the rules the program's reports already have:
///   - written premium: every premium ledger row booked in the period, cancellations included — the corrected premium, by class and the
///     policy's reporting segment (frozen at issue; owner 21.10.2026: „ეს მონაცემი არ იცვლება. არის ის, როგორც გაიცა"); a policy is counted
///     when an issue row of it is booked in the period — a policy change is a new policy, issued with its own issue rows whatever their
///     sign (owner, 20.10.2026); a cancellation row counts no policy;
///   - reinsurance premium and commission: the cessions booked in the period (as the monthly report); a policy is counted when the
///     cession of its issue row is; the deferred commission at a day — the cessions' commission unearned by days, like the premium, a
///     correction linked to the cession it cancels;
///   - claims paid: gross, each payment at its own rate, by class and segment; a case is its loss on its class (owner, 20.10.2026:
///     „2 სახეობაში 1 ზარალი, 2 შემთხვევაა"), once however many payments and claims it had there;
///   - the reinsurer's share of the claims paid: month by month exactly as the monthly report books it (proportional at the payments'
///     rates, excess of loss at the month-end rate);
///   - subrogation and salvage: received in the period; treaty premium: each month's share at its month-end rate, per treaty with its
///     kind and listed classes; a quota share's key — the premium ceded under it earned in the period, by class (owner, 21.10.2026);
///   - the intermediaries' commission: the policy card's commission (PolicyCard.ExactPremiumPosition), booked in the period and deferred
///     at the start and the end — agents' with employees', brokers'; banks' booked only, never deferred (owner, 21.10.2026);
///   - the opening balances: the monthly report and the impairment report of the previous year's last day.
/// Read only. The rates are decimals to the millionth (the old Number(rate) is not reintroduced).
/// </summary>
public static class IncomeStatementReader
{
    private static readonly HashSet<string> SegmentNames = new(IncomeStatement.Segments, StringComparer.Ordinal);

    private static string Segment(string s) => SegmentNames.Contains(s) ? s : throw new InvalidOperationException($"Unknown reporting segment {s}");
    private static string Id(Guid id) => id.ToString("D");
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly MonthEnd(DateOnly anyDay) => new DateOnly(anyDay.Year, anyDay.Month, 1).AddMonths(1).AddDays(-1);

    private sealed record WrittenDb(string ClassCode, string Segment, Guid PolicyId, decimal AmountGel, int Issued);
    private sealed record CededDb(string ClassCode, string Segment, Guid PolicyId, decimal AmountGel, decimal CommissionGel, int Issued);
    private sealed record CessionDb(Guid Id, Guid? CancelsId, Guid TreatyId, string ClassCode, decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);
    private sealed record PaidDb(string ClassCode, string Segment, Guid ClaimId, Guid EventId, decimal PaidGel);
    private sealed record ReinsuredDb(Guid Id, string ClassCode, string Segment, string Currency);
    private sealed record MonthlyPaidDb(Guid ClaimId, int Year, int Month, decimal PaidGel, decimal Paid);
    private sealed record TreatyDb(Guid Id, string Number, string Kind, decimal Premium, string Currency, DateOnly PeriodFrom, DateOnly PeriodTo);
    private sealed record IntermediaryDb(Guid PolicyId, string Kind, decimal Percent, string Currency, decimal Rate);
    private sealed record LedgerDb(Guid PolicyId, Guid Id, Guid? CancelsId, decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);

    /// <summary>The profit and loss's source for the period from the year's start to <paramref name="to"/> (a month's last day).</summary>
    public static async Task<IncomeStatementSource> ReadAsync(Tx db, Valuation valuation, DateOnly to)
    {
        var from = new DateOnly(to.Year, 1, 1);
        if (MonthEnd(to) != to) throw new InvalidOperationException($"Not a month's end: {Iso(to)}");
        var openingDay = from.AddDays(-1);
        var openingReport = await PremiumReport.BuildAsync(db, valuation, openingDay.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        var openingImpairment = await ImpairmentReport.BuildTotalsExactAsync(db, valuation, openingDay);
        var opening = IncomeStatement.BalancesOf(RegulatoryForms.ToForms(openingReport), RegulatoryForms.ToForms(openingImpairment));
        var months = new List<DateOnly>();
        for (var m = from; m <= to; m = m.AddMonths(1)) months.Add(m);

        // Each (currency, day) valued once for this call.
        var valuations = new Dictionary<string, Task<(decimal Rate, DateOnly Date)>>(StringComparer.Ordinal);
        async Task<decimal> RateAt(string currency, DateOnly date)
        {
            var key = $"{currency} {Iso(date)}";
            if (!valuations.TryGetValue(key, out var v)) valuations[key] = v = valuation(currency, date);
            return (await v).Rate;
        }

        var writtenRows = await db.QueryAsync<WrittenDb>("""
            select l.class_code as ClassCode, p.segment as Segment, l.policy_id as PolicyId, sum(t.amount_gel) as AmountGel,
                   max(case when t.kind = N'issue' then 1 else 0 end) as Issued
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id
            where t.booked_on >= @from and t.booked_on <= @to
            group by l.class_code, p.segment, l.policy_id
            order by l.class_code, p.segment, l.policy_id
            """, new { from, to });
        var written = writtenRows.Select(r => new FlowItem(r.ClassCode, Segment(r.Segment), Id(r.PolicyId), Tetri(r.AmountGel), r.Issued == 1)).ToList();

        var cededRows = (await db.QueryAsync<CededDb>("""
            select l.class_code as ClassCode, p.segment as Segment, l.policy_id as PolicyId, sum(c.amount_gel) as AmountGel, sum(c.commission_gel) as CommissionGel,
                   max(case when t.kind = N'issue' then 1 else 0 end) as Issued
            from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id
            where c.booked_on >= @from and c.booked_on <= @to
            group by l.class_code, p.segment, l.policy_id
            order by l.class_code, p.segment, l.policy_id
            """, new { from, to })).ToList();
        var ceded = cededRows.Select(r => new FlowItem(r.ClassCode, Segment(r.Segment), Id(r.PolicyId), Tetri(r.AmountGel), r.Issued == 1)).ToList();

        // The reinsurance commission deferred at a day: every cession's commission by the day, earning like the premium it follows, a
        // correction linked to the cession it cancels (as the monthly report links the cessions).
        var cessions = (await db.QueryAsync<CessionDb>("""
            select c.id as Id, c.cancels_cession_id as CancelsId, c.treaty_id as TreatyId, l.class_code as ClassCode, c.amount_gel as AmountGel,
                   c.commission_gel as CommissionGel, c.booked_on as BookedOn, c.cover_from as CoverFrom, c.cover_to as CoverTo
            from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id join core.policy_line l on l.id = t.policy_line_id
            where c.booked_on <= @to
            order by c.created_at, c.id
            """, new { to })).ToList();
        var commissionTx = PremiumReport.LinkExact(cessions.Select(c => (c.Id, c.CancelsId, new ExactPremiumTx(Tetri(c.CommissionGel), c.BookedOn, c.CoverFrom, c.CoverTo))).ToList(), "Cession");
        var commissionTxs = cessions.Select(c => commissionTx[c.Id]).ToList();
        var riCommission = new CommissionFlow(
            cededRows.Aggregate(BigInteger.Zero, (s, r) => s + Tetri(r.CommissionGel)),
            PremiumReport.Unearned(commissionTxs, openingDay),
            PremiumReport.Unearned(commissionTxs, to));

        // A case is a loss (its event) on a class: each claim keeps its amount on its class, its count is the event's.
        var paidRows = await db.QueryAsync<PaidDb>("""
            select l.class_code as ClassCode, p.segment as Segment, c.id as ClaimId, c.loss_event_id as EventId, sum(round(cp.amount * cp.rate, 2)) as PaidGel
            from core.claim_payment cp join core.claim c on c.id = cp.claim_id
            join core.policy_line l on l.id = c.policy_line_id join core.policy p on p.id = l.policy_id
            where cp.paid_on >= @from and cp.paid_on <= @to
            group by l.class_code, p.segment, c.id, c.loss_event_id
            order by l.class_code, p.segment, c.id
            """, new { from, to });
        var paid = paidRows.Select(r => new FlowItem(r.ClassCode, Segment(r.Segment), Id(r.EventId), Tetri(r.PaidGel), !Tetri(r.PaidGel).IsZero)).ToList();

        // The reinsurer's share of the claims paid, each month as the monthly report books it (PremiumReport, its reinsured claims).
        var reinsured = (await db.QueryAsync<ReinsuredDb>("""
            select c.id as Id, l.class_code as ClassCode, p.segment as Segment, c.currency as Currency
            from core.claim c join core.policy_line l on l.id = c.policy_line_id join core.policy p on p.id = l.policy_id
            where exists (select 1 from core.claim_cession cc where cc.claim_id = c.id)
            order by c.created_at, c.id
            """)).ToList();
        var monthlyPaid = reinsured.Count == 0 ? [] : await Billing.InChunksAsync(reinsured.Select(c => c.Id).ToList(), ids => db.QueryAsync<MonthlyPaidDb>("""
            select p.claim_id as ClaimId, year(p.paid_on) as Year, month(p.paid_on) as Month, sum(round(p.amount * p.rate, 2)) as PaidGel, sum(p.amount) as Paid
            from core.claim_payment p
            where p.claim_id in @ids and p.paid_on >= @from and p.paid_on <= @to
            group by p.claim_id, year(p.paid_on), month(p.paid_on)
            """, new { ids, from, to }));
        var paidIn = monthlyPaid.ToDictionary(r => (r.ClaimId, r.Year, r.Month));
        var reinsurerPaid = new List<FlowItem>();
        foreach (var claim in reinsured)
        {
            var currency = claim.Currency.Trim();
            var atStart = await ReinsuranceStore.ClaimSharesExactAsync(db, claim.Id, openingDay);
            BigInteger share = 0;
            foreach (var month in months)
            {
                var end = MonthEnd(month);
                var atEnd = await ReinsuranceStore.ClaimSharesExactAsync(db, claim.Id, end);
                var rate = await RateAt(currency, end);
                var p = paidIn.TryGetValue((claim.Id, month.Year, month.Month), out var row) ? row : null;
                BigInteger paidMonth = p is null ? 0 : Tetri(p.Paid), paidMonthGel = p is null ? 0 : Tetri(p.PaidGel);
                foreach (var s in atEnd)
                {
                    var paidShare = s.InPaid - (atStart.FirstOrDefault(x => x.TreatyId == s.TreatyId)?.InPaid ?? BigInteger.Zero);
                    if (paidShare.IsZero) continue;
                    share += s.Kind == TreatyKind.ExcessOfLoss || paidMonth.IsZero ? AtRateHalfUp(paidShare, rate) : DivHalfUp(paidMonthGel * paidShare, paidMonth);
                }
                atStart = atEnd;
            }
            if (!share.IsZero) reinsurerPaid.Add(new FlowItem(claim.ClassCode, Segment(claim.Segment), Id(claim.Id), share, false));
        }

        var recovered = await db.ExecuteScalarAsync<decimal>(
            "select coalesce(sum(amount_gel), 0) from core.claim_recovery where received_on >= @from and received_on <= @to", new { from, to });

        // Treaty premium not tied to policies: each month's share at its month-end rate, as the monthly report has it — per treaty, with
        // its kind and the classes it lists, which place it (IncomeStatement.TreatyAllocation).
        var treatyRows = (await db.QueryAsync<TreatyDb>("""
            select t.id as Id, t.number as Number, t.kind as Kind, t.treaty_premium as Premium, t.currency as Currency, t.period_from as PeriodFrom, t.period_to as PeriodTo
            from core.ri_treaty t where t.treaty_premium is not null and t.period_from <= @to and t.period_to >= @from
            order by t.number, t.id
            """, new { from, to })).ToList();
        var classesOf = treatyRows.Count == 0 ? [] : (await Billing.InChunksAsync(treatyRows.Select(t => t.Id).ToList(), ids => db.QueryAsync<(Guid TreatyId, string ClassCode)>(
                "select treaty_id, class_code from core.ri_treaty_class where treaty_id in @ids", new { ids })))
            .GroupBy(r => r.TreatyId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.ClassCode).Order(StringComparer.Ordinal).ToList());
        // A quota share's key: the premium ceded under it earned in the period, by class (each cession earning like the premium it
        // follows, a correction linked to the cession it cancels).
        var cededTx = PremiumReport.LinkExact(cessions.Select(c => (c.Id, c.CancelsId, new ExactPremiumTx(Tetri(c.AmountGel), c.BookedOn, c.CoverFrom, c.CoverTo))).ToList(), "Cession");
        IReadOnlyDictionary<string, BigInteger> EarnedCededOf(Guid treatyId) =>
            cessions.Where(c => c.TreatyId == treatyId).GroupBy(c => c.ClassCode, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => PremiumReport.EarnedIn(g.Select(c => cededTx[c.Id]), from, to), StringComparer.Ordinal);
        var treaties = new List<TreatyPremium>();
        foreach (var tr in treatyRows)
        {
            BigInteger amountGel = 0;
            foreach (var month in months)
            {
                var mTo = MonthEnd(month);
                if (tr.PeriodFrom > mTo || tr.PeriodTo < month) continue;
                amountGel += AtRateHalfUp(PremiumReport.TreatyPremiumInPeriodExact(Tetri(tr.Premium), tr.PeriodFrom, tr.PeriodTo, month, mTo), await RateAt(tr.Currency.Trim(), mTo));
            }
            treaties.Add(new TreatyPremium(Id(tr.Id), tr.Number, tr.Kind, classesOf.TryGetValue(tr.Id, out var cl) ? cl : [], amountGel,
                tr.Kind == "quota_share" ? EarnedCededOf(tr.Id) : null));
        }

        // The intermediaries' commission: the policy card's — on each policy's ledger at its fixed rate, by kind. An employee's goes with
        // the agents' (N2-1 00130–00150), a bank's to N2-6 00160 (owner, 20.10.2026 and 21.10.2026).
        var intermediaries = (await db.QueryAsync<IntermediaryDb>("""
            select pi.policy_id as PolicyId, i.kind as Kind, pi.commission_percent as [Percent], p.premium_currency as Currency, p.premium_rate as Rate
            from core.policy_intermediary pi join core.intermediary i on i.id = pi.intermediary_id join core.policy p on p.id = pi.policy_id
            order by p.created_at, p.id, i.kind, i.id
            """)).ToList();
        static string KindOf(string kind) => kind switch
        {
            "agent" or "employee" => "agent",
            "broker" or "bank" => kind,
            _ => throw new InvalidOperationException($"Unknown intermediary kind {kind}"),
        };
        var flows = new Dictionary<string, (BigInteger Written, BigInteger Opening, BigInteger Closing)>(StringComparer.Ordinal)
        {
            ["agent"] = (0, 0, 0), ["broker"] = (0, 0, 0), ["bank"] = (0, 0, 0),
        };
        var policyIds = intermediaries.Select(i => i.PolicyId).Distinct().ToList();
        var ledger = policyIds.Count == 0 ? [] : await Billing.InChunksAsync(policyIds, ids => db.QueryAsync<LedgerDb>("""
            select l.policy_id as PolicyId, t.id as Id, t.cancels_transaction_id as CancelsId, t.amount as Amount, t.booked_on as BookedOn,
                   t.cover_from as CoverFrom, t.cover_to as CoverTo
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id in @ids and t.booked_on <= @to
            order by t.created_at, t.id
            """, new { ids, to }));
        foreach (var policyId in policyIds)
        {
            var rows = ledger.Where(r => r.PolicyId == policyId).Select(r => new PolicyCard.LedgerRow(r.Id, r.CancelsId, r.Amount, r.BookedOn, r.CoverFrom, r.CoverTo)).ToList();
            var of = intermediaries.Where(i => i.PolicyId == policyId).ToList();
            foreach (var kind in new[] { "agent", "broker", "bank" })
            {
                var basisPoints = of.Where(i => KindOf(i.Kind) == kind).Aggregate(BigInteger.Zero, (s, i) => s + Tetri(i.Percent));
                if (basisPoints.IsZero) continue;
                var currency = of[0].Currency.Trim();
                var atOpening = PolicyCard.ExactPremiumPosition(rows, openingDay, currency, of[0].Rate, (long)basisPoints).PositionGel;
                var atEnd = PolicyCard.ExactPremiumPosition(rows, to, currency, of[0].Rate, (long)basisPoints).PositionGel;
                var f = flows[kind];
                // A bank's commission is not deferred (owner, 21.10.2026).
                flows[kind] = kind == "bank"
                    ? (f.Written + atEnd.Written - atOpening.Written, f.Opening, f.Closing)
                    : (f.Written + atEnd.Written - atOpening.Written, f.Opening + atOpening.Unearned, f.Closing + atEnd.Unearned);
            }
        }
        CommissionFlow Flow(string kind) => new(flows[kind].Written, flows[kind].Opening, flows[kind].Closing);

        return new IncomeStatementSource(Iso(from), Iso(to), opening, written, ceded, paid, reinsurerPaid, Tetri(recovered), riCommission,
            new IntermediaryCommission(Flow("agent"), Flow("broker"), Flow("bank")), treaties);
    }
}
