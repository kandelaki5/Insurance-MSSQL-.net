using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.ReportMath;

namespace Insurance.Application.Services;

/// <summary>
/// Reinsurance bordereau (legacy src/lib/bordereau.ts): the statement of one treaty for a period — premium ceded, commission, the
/// reinsurer's share of claims paid and of the reserve at period end, and the balance. Fields follow the internal records order
/// №11/2016, annex 4. Amounts in whole minor units; GEL values rounded as Math.round did (a half towards +∞), exactly.
/// </summary>
public static class Bordereau
{
    public sealed record PremiumRow(string PolicyNumber, string Policyholder, string? Object, string ClassCode, DateOnly CoverFrom, DateOnly CoverTo,
        decimal? SumInsured, string Currency, decimal SharePercent, long CededPremium, long CededPremiumGel, long CommissionGel, DateOnly BookedOn, bool IsCancellation);

    public sealed record ClaimRow(string ClaimNumber, string PolicyNumber, DateOnly OccurredOn, DateOnly ReportedOn, string Status, string Currency,
        /* Reinsurer's share of payments made in the period, claim currency and GEL. */ long PaidShare, long PaidShareGel,
        /* Reinsurer's share of the reserve at period end. */ long ReserveShare, long ReserveShareGel);

    public sealed record TreatyInfo(Guid Id, string Number, string Name, string Reinsurer, string? Broker, string Kind, string Currency, decimal BrokerPercent);

    public sealed record Totals(long CededGel, long CommissionGel, long BrokerageGel, long ClaimsPaidGel, long ReserveGel, long BalanceGel);

    public sealed record Statement(TreatyInfo Treaty, DateOnly From, DateOnly To, IReadOnlyList<PremiumRow> Premiums, IReadOnlyList<ClaimRow> Claims, Totals Totals);

    private sealed record PremiumDb(string PolicyNumber, string Policyholder, string? Object, string ClassCode, DateOnly CoverFrom, DateOnly CoverTo, decimal? SumInsured,
        string Currency, decimal SharePercent, decimal Amount, decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, bool IsCancellation);

    private sealed record ClaimDb(Guid Id, string Number, string PolicyNumber, DateOnly OccurredOn, DateOnly ReportedOn, string Status, string Currency, decimal PaidMonth, decimal PaidMonthGel);

    public static Task<Statement?> BuildAsync(Tx db, Rates rates, Guid treatyId, DateOnly from, DateOnly to) => BuildAsync(db, rates.LatestRateOnOrBeforeAsync, treatyId, from, to);

    public static async Task<Statement?> BuildAsync(Tx db, Valuation valuation, Guid treatyId, DateOnly from, DateOnly to)
    {
        var treaty = await db.QuerySingleOrDefaultAsync<TreatyInfo>("""
            select t.id as Id, t.number as Number, t.name as Name, r.display_name as Reinsurer, b.display_name as Broker, t.kind as Kind, t.currency as Currency,
                   t.broker_percent as BrokerPercent
            from core.ri_treaty t join core.party r on r.id = t.reinsurer_party_id left join core.party b on b.id = t.broker_party_id
            where t.id = @treatyId
            """, new { treatyId });
        if (treaty is null) return null;
        treaty = treaty with { Currency = treaty.Currency.Trim() };

        var premiums = (await db.QueryAsync<PremiumDb>("""
            select p.number as PolicyNumber, h.display_name as Policyholder,
                   (select string_agg(o.description, N'; ') from core.insured_object o where o.policy_id = p.id) as Object,
                   l.class_code as ClassCode, c.cover_from as CoverFrom, c.cover_to as CoverTo, l.sum_insured as SumInsured, p.premium_currency as Currency,
                   c.share_percent as SharePercent, c.amount as Amount, c.amount_gel as AmountGel, c.commission_gel as CommissionGel, c.booked_on as BookedOn,
                   convert(bit, case when c.cancels_cession_id is not null then 1 else 0 end) as IsCancellation
            from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id
            join core.policy p on p.id = l.policy_id
            join core.contract co on co.id = p.contract_id
            join core.party h on h.id = co.policyholder_id
            where c.treaty_id = @treatyId and c.booked_on >= @from and c.booked_on <= @to
            order by c.booked_on, p.number, c.created_at, c.id
            """, new { treatyId, from, to }))
            .Select(r => new PremiumRow(r.PolicyNumber, r.Policyholder, r.Object, r.ClassCode, r.CoverFrom, r.CoverTo, r.SumInsured, r.Currency.Trim(), r.SharePercent,
                Billing.Tetri(r.Amount), Billing.Tetri(r.AmountGel), Billing.Tetri(r.CommissionGel), r.BookedOn, r.IsCancellation))
            .ToList();

        var claimRows = await db.QueryAsync<ClaimDb>("""
            select c.id as Id, c.number as Number, p.number as PolicyNumber, c.occurred_on as OccurredOn, c.reported_on as ReportedOn, c.status as Status, c.currency as Currency,
                   (select coalesce(sum(cp.amount), 0) from core.claim_payment cp where cp.claim_id = c.id and cp.paid_on >= @from and cp.paid_on <= @to) as PaidMonth,
                   (select coalesce(sum(round(cp.amount * cp.rate, 2)), 0) from core.claim_payment cp where cp.claim_id = c.id and cp.paid_on >= @from and cp.paid_on <= @to) as PaidMonthGel
            from core.claim_cession cc join core.claim c on c.id = cc.claim_id join core.policy p on p.id = c.policy_id
            where cc.treaty_id = @treatyId and c.reported_on <= @to
            order by c.number
            """, new { treatyId, from, to });
        var claims = new List<ClaimRow>();
        foreach (var r in claimRows)
        {
            var atEnd = (await ReinsuranceStore.ClaimSharesAsync(db, r.Id, to)).FirstOrDefault(s => s.TreatyId == treatyId);
            var atStart = (await ReinsuranceStore.ClaimSharesAsync(db, r.Id, from.AddDays(-1))).FirstOrDefault(s => s.TreatyId == treatyId);
            if (atEnd is null) continue;
            var paidShare = atEnd.InPaid - (atStart?.InPaid ?? 0);
            if (paidShare == 0 && atEnd.InReserve == 0) continue;
            var currency = r.Currency.Trim();
            var v = await valuation(currency, to);
            var paidMonth = Billing.Tetri(r.PaidMonth);
            var paidShareGel = paidMonth == 0 || atEnd.Kind == TreatyKind.ExcessOfLoss
                ? AtRateHalfUp(paidShare, v.Rate)
                : DivHalfUp(Billing.Tetri(r.PaidMonthGel) * (BigInteger)paidShare, paidMonth);
            claims.Add(new ClaimRow(r.Number, r.PolicyNumber, r.OccurredOn, r.ReportedOn, r.Status, currency,
                paidShare, (long)paidShareGel, atEnd.InReserve, (long)AtRateHalfUp(atEnd.InReserve, v.Rate)));
        }

        var cededGel = premiums.Sum(p => p.CededPremiumGel);
        var commissionGel = premiums.Sum(p => p.CommissionGel);
        // Math.round(ceded × percent / 100): the brokerage to the nearest tetri, a half towards +∞, exactly.
        var brokerageGel = (long)DivHalfUp(cededGel * Micro(treaty.BrokerPercent), 100 * RateScale);
        var claimsPaidGel = claims.Sum(c => c.PaidShareGel);
        var reserveGel = claims.Sum(c => c.ReserveShareGel);
        // Payable to the reinsurer: ceded premium less commission and brokerage, less its share of claims paid.
        return new Statement(treaty, from, to, premiums, claims,
            new Totals(cededGel, commissionGel, brokerageGel, claimsPaidGel, reserveGel, cededGel - commissionGel - brokerageGel - claimsPaidGel));
    }
}
