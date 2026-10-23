using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.ReportMath;

namespace Insurance.Application.Services;

/// <summary>
/// Impairment reserve on insurance receivables at a reporting date, per policy, in the columns of the owner's calculation template,
/// with totals per receivable type of financial reporting form N1-4 (direct / from brokers / from agents) — legacy
/// src/lib/impairment-report.ts. Amounts in whole minor units, exactly from the stored decimals.
/// </summary>
public static class ImpairmentReport
{
    public static readonly IReadOnlyList<string> DebtorKinds = ["client", "broker", "agent"];
    public static readonly IReadOnlyList<string> TotalKinds = ["client", "broker", "agent", "all"];

    public sealed record Row(
        Guid PolicyId, string PolicyNumber, DateOnly CoverFrom, DateOnly CoverTo, DateOnly? CancelledOn,
        /* Receivable type of form N1-4, from the instalments (the debtor of each instalment). */ IReadOnlyList<string> DebtorKinds,
        string? Intermediaries, string Policyholder, string Segment, string Classes, string? Insured, string? Beneficiary, string Currency,
        /* Premium written by the reporting date (approved reductions included), minor units. */ long Premium,
        /* Premium not yet paid, earned or not (form N1-4 gross receivable), minor units. */ long UnpaidPremium,
        decimal Rate, long UnpaidPremiumGel, ImpairmentResult Impairment, long ReserveGel)
    {
        /// <summary>The policy's receivable type: its first debtor in the order client, broker, agent.</summary>
        public string PrimaryKind => KindOf(DebtorKinds);
    }

    public sealed record Totals(string Kind, int Policies, long UnpaidPremiumGel, long UnpaidEarnedGel, long ReserveGel);

    public sealed record RateUsed(string Currency, decimal Rate, DateOnly Date);

    public sealed record Report(DateOnly AsOf, IReadOnlyList<Row> Rows, IReadOnlyList<Totals> Totals, IReadOnlyList<RateUsed> Rates);

    /// <summary>N1-4's totals per receivable type, in whole tetri, exactly.</summary>
    public sealed record TotalsExact(string Kind, int Policies, BigInteger UnpaidPremiumGel, BigInteger ReserveGel);

    public sealed record ReportExact(DateOnly AsOf, IReadOnlyList<TotalsExact> Totals);

    private sealed record PolicyDb(Guid Id, string Number, string Currency, DateOnly EffectiveFrom, DateOnly EffectiveTo, DateOnly? CancelledOn,
        string Policyholder, string Segment, string Classes, string? DebtorKinds, string? Intermediaries, string? Insured, string? Beneficiary);

    private sealed record LedgerDb(Guid Id, Guid? CancelsId, decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, Guid? ReceivableId);

    private sealed record Inputs(List<PolicyDb> Policies, List<LedgerDb> Ledger, Dictionary<Guid, decimal> Paid, List<(string Currency, decimal Rate, DateOnly Date)> RateOf);

    public static string KindOf(IReadOnlyList<string> debtorKinds) => DebtorKinds.FirstOrDefault(debtorKinds.Contains) ?? "client";

    private static IReadOnlyList<string> SplitKinds(string? kinds) => (kinds ?? "client").Split(',');

    /// <summary>
    /// The report's reads, in their order: the receivable policies, the premium ledger, the premium paid, the rates (shared by the
    /// report and its exact totals). Null when no policy is a receivable.
    /// </summary>
    private static async Task<Inputs?> ReadInputsAsync(Tx db, Valuation valuation, DateOnly asOf)
    {
        // string_agg(distinct …) of the old code: each list of distinct values, in their order.
        var policies = (await db.QueryAsync<PolicyDb>("""
            select p.id as Id, p.number as Number, p.premium_currency as Currency, p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   p.cancelled_on as CancelledOn, h.display_name as Policyholder, h.segment as Segment,
                   (select string_agg(x.v, N', ') within group (order by x.v)
                    from (select distinct l.class_code as v from core.policy_line l where l.policy_id = p.id) x) as Classes,
                   (select string_agg(x.v, N',') within group (order by x.v)
                    from (select distinct i.debtor_kind as v from core.installment i where i.policy_id = p.id) x) as DebtorKinds,
                   (select string_agg(x.v, N', ') within group (order by x.v)
                    from (select distinct ip.display_name as v from core.policy_intermediary pi
                          join core.intermediary im on im.id = pi.intermediary_id join core.party ip on ip.id = im.party_id
                          where pi.policy_id = p.id) x) as Intermediaries,
                   (select string_agg(x.v, N', ') within group (order by x.v)
                    from (select distinct pp2.display_name as v from core.policy_party r join core.party pp2 on pp2.id = r.party_id
                          where r.policy_id = p.id and r.role = N'insured') x) as Insured,
                   (select string_agg(x.v, N', ') within group (order by x.v)
                    from (select distinct pp3.display_name as v from core.policy_party r join core.party pp3 on pp3.id = r.party_id
                          where r.policy_id = p.id and r.role = N'beneficiary') x) as Beneficiary
            from core.policy p
            join core.contract c on c.id = p.contract_id
            join core.party h on h.id = c.policyholder_id
            where exists (select 1 from core.installment i where i.policy_id = p.id)
              and exists (select 1 from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                          where l.policy_id = p.id and t.kind = N'issue' and t.booked_on <= @asOf)
            order by p.number
            """, new { asOf })).Select(p => p with { Currency = p.Currency.Trim() }).ToList();
        if (policies.Count == 0) return null;

        // Premium ledger in policy currency, attributed to the policy whose schedule it moves.
        var ledger = (await db.QueryAsync<LedgerDb>("""
            select t.id as Id, t.cancels_transaction_id as CancelsId, t.amount as Amount, t.booked_on as BookedOn, t.cover_from as CoverFrom, t.cover_to as CoverTo,
                   case when exists (select 1 from core.installment i where i.policy_id = po.id) then po.id else po.replaces_policy_id end as ReceivableId
            from core.premium_transaction t
            join core.policy_line l on l.id = t.policy_line_id
            join core.policy po on po.id = l.policy_id
            where t.booked_on <= @asOf
            order by t.created_at, t.id
            """, new { asOf })).ToList();

        // Premium paid by the reporting date: every allocation booked by then (receipts, claim set-offs, reversals and refunds), in policy currency.
        var paid = (await db.QueryAsync<(Guid PolicyId, decimal Paid)>("""
            select i.policy_id, coalesce(sum(a.amount), 0)
            from core.payment_allocation a join core.installment i on i.id = a.installment_id
            where a.booked_on <= @asOf group by i.policy_id
            """, new { asOf })).ToDictionary(r => r.PolicyId, r => r.Paid);

        var rateOf = new List<(string Currency, decimal Rate, DateOnly Date)>();
        foreach (var currency in policies.Select(p => p.Currency).Distinct())
        {
            if (currency == "GEL") rateOf.Add((currency, 1m, asOf));
            else
            {
                var v = await valuation(currency, asOf);
                rateOf.Add((currency, v.Rate, v.Date));
            }
        }
        return new Inputs(policies, ledger, paid, rateOf);
    }

    public static Task<Report> BuildAsync(Tx db, Rates rates, DateOnly asOf) => BuildAsync(db, rates.LatestRateOnOrBeforeAsync, asOf);

    /// <summary>
    /// Every policy that is a receivable on the date: it has a payment schedule and its premium was booked by then (the same recognition
    /// rule as the monthly receivable, review H03). Approved reductions issued as separate change policies without a schedule of their
    /// own lower the policy they change, as they lower its schedule.
    /// </summary>
    public static async Task<Report> BuildAsync(Tx db, Valuation valuation, DateOnly asOf)
    {
        var inputs = await ReadInputsAsync(db, valuation, asOf);
        if (inputs is null) return new Report(asOf, [], TotalsOf([]), []);
        var currencyOf = inputs.Policies.ToDictionary(p => p.Id, p => p.Currency);

        // Every row is built first and cancellations are linked afterwards (review H14).
        var linked = EarnedPremium.LinkLedger(inputs.Ledger.Select(r =>
        {
            var currency = r.ReceivableId is { } rid && currencyOf.TryGetValue(rid, out var c) ? c : "GEL";
            return new LedgerRow(r.Id.ToString(), r.CancelsId?.ToString(),
                new PremiumTransaction(new Money(Billing.Tetri(r.Amount), Billing.ParseCurrency(currency)), r.BookedOn, r.CoverFrom, r.CoverTo));
        }).ToList());
        var txsOf = new Dictionary<Guid, List<PremiumTransaction>>();
        for (var i = 0; i < inputs.Ledger.Count; i++)
        {
            var r = inputs.Ledger[i];
            if (r.ReceivableId is not { } rid || !currencyOf.ContainsKey(rid)) continue;
            if (!txsOf.TryGetValue(rid, out var list)) txsOf[rid] = list = [];
            list.Add(linked[i]);
        }

        var rows = inputs.Policies.Select(p =>
        {
            var txs = txsOf.TryGetValue(p.Id, out var t) ? t : [];
            var paid = inputs.Paid.TryGetValue(p.Id, out var pd) ? Billing.Tetri(pd) : 0;
            var premium = txs.Sum(tx => tx.Amount.Minor);
            var debtorKinds = SplitKinds(p.DebtorKinds);
            // A broker's receivable is aged but not impaired (owner, 19.10.2026).
            var aged = Impairment.ImpairmentOf(txs, paid, asOf);
            var impairment = KindOf(debtorKinds) == "broker" ? Impairment.WithoutReserve(aged) : aged;
            var rate = inputs.RateOf.First(r => r.Currency == p.Currency).Rate;
            return new Row(p.Id, p.Number, p.EffectiveFrom, p.EffectiveTo, p.CancelledOn, debtorKinds, p.Intermediaries, p.Policyholder, p.Segment,
                p.Classes, p.Insured, p.Beneficiary, p.Currency, premium, premium - paid, rate, InGel(premium - paid, rate), impairment, InGel(impairment.TotalReserve, rate));
        }).ToList();

        return new Report(asOf, rows, TotalsOf(rows),
            inputs.RateOf.Where(r => r.Currency != "GEL").Select(r => new RateUsed(r.Currency, r.Rate, r.Date)).ToList());
    }

    /// <summary>
    /// An amount in the policy's currency valued in GEL as finance/money.ts toGel values it: the rate to the millionth, rounded half
    /// away from zero.
    /// </summary>
    public static long InGel(long minor, decimal rate) => (long)AtRateAway(minor, rate);

    /// <summary>
    /// Totals per receivable type of form N1-4. A policy whose instalments are owed by different debtors is counted under the first in
    /// the order client, broker, agent (owner, 04.10.2026), whatever the order of its debtor kinds on the row.
    /// </summary>
    public static List<Totals> TotalsOf(IReadOnlyList<Row> rows) =>
        TotalKinds.Select(kind =>
        {
            var of = rows.Where(r => kind == "all" || r.PrimaryKind == kind).ToList();
            return new Totals(kind, of.Count, of.Sum(r => Math.Max(0, r.UnpaidPremiumGel)), of.Sum(r => InGel(r.Impairment.UnpaidEarned, r.Rate)), of.Sum(r => r.ReserveGel));
        }).ToList();

    public static Task<ReportExact> BuildTotalsExactAsync(Tx db, Rates rates, DateOnly asOf) => BuildTotalsExactAsync(db, rates.LatestRateOnOrBeforeAsync, asOf);

    /// <summary>
    /// The report's N1-4 totals exactly (INS-FORM1-DATA-FIDELITY-FIX-OFFLINE-20261019; legacy buildImpairmentTotalsExact): the same reads,
    /// rows and rules — each policy's unpaid premium and impairment reserve in GEL at its currency's rate, half up, a policy once under the
    /// first of client → broker → agent, an overpaid policy's unpaid as 0 — every amount on big integers.
    /// </summary>
    public static async Task<ReportExact> BuildTotalsExactAsync(Tx db, Valuation valuation, DateOnly asOf)
    {
        var inputs = await ReadInputsAsync(db, valuation, asOf);
        if (inputs is null) return new ReportExact(asOf, TotalKinds.Select(k => new TotalsExact(k, 0, 0, 0)).ToList());
        var currencyOf = inputs.Policies.ToDictionary(p => p.Id, p => p.Currency);
        var linked = PremiumReport.LinkExact(inputs.Ledger.Select(r => (r.Id, r.CancelsId, new ExactPremiumTx(Tetri(r.Amount), r.BookedOn, r.CoverFrom, r.CoverTo))).ToList(), "Ledger row");
        var txsOf = new Dictionary<Guid, List<ExactPremiumTx>>();
        foreach (var r in inputs.Ledger)
        {
            if (r.ReceivableId is not { } rid || !currencyOf.ContainsKey(rid)) continue;
            if (!txsOf.TryGetValue(rid, out var list)) txsOf[rid] = list = [];
            list.Add(linked[r.Id]);
        }
        var each = inputs.Policies.Select(p =>
        {
            var txs = txsOf.TryGetValue(p.Id, out var t) ? t : [];
            var paid = inputs.Paid.TryGetValue(p.Id, out var pd) ? Tetri(pd) : BigInteger.Zero;
            var premium = txs.Aggregate(BigInteger.Zero, (s, tx) => s + tx.Minor);
            var rate = inputs.RateOf.First(r => r.Currency == p.Currency).Rate;
            var kind = KindOf(SplitKinds(p.DebtorKinds));
            // A broker's receivable is not impaired (owner, 19.10.2026), as on the report's own rows.
            var reserve = kind == "broker" ? BigInteger.Zero : Impairment.ImpairmentOfExact(txs, paid, asOf).TotalReserve;
            return (Kind: kind, UnpaidGel: AtRateHalfUp(premium - paid, rate), ReserveGel: AtRateHalfUp(reserve, rate));
        }).ToList();
        return new ReportExact(asOf, TotalKinds.Select(kind =>
        {
            var of = each.Where(e => kind == "all" || e.Kind == kind).ToList();
            return new TotalsExact(kind, of.Count,
                of.Aggregate(BigInteger.Zero, (s, e) => s + (e.UnpaidGel > 0 ? e.UnpaidGel : 0)),
                of.Aggregate(BigInteger.Zero, (s, e) => s + e.ReserveGel));
        }).ToList());
    }
}
