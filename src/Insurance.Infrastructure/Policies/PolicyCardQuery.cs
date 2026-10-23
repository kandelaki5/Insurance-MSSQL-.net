using Insurance.Application.Data;
using Insurance.Application.Ports;
using Insurance.Application.Services;
using static Insurance.Application.Ports.PolicyCardView;

namespace Insurance.Infrastructure.Policies;

/// <summary>The policy card's common figures on SQL Server (the card's reads before B2, moved unchanged).</summary>
public sealed class PolicyCardQuery(Db db, Rates rates) : IPolicyCardQuery
{
    public Task<PolicyCardView?> ReadAsync(Guid id, CancellationToken ct = default)
    {
        // One reporting day for the whole card, taken once (Codex PR #18 review, item 3).
        var today = db.Today();
        return db.ReadTxAsync(tx => ReadAsync(tx, id, today), ct);
    }

    public async Task<PolicyCardView?> ReadAsync(Tx tx, Guid id, DateOnly today)
    {
        var policy = await tx.QuerySingleOrDefaultAsync<Header>("""
            select p.id as Id, p.number as Number, c.number as ContractNumber, h.id as PolicyholderId, h.display_name as Policyholder, h.segment as Segment,
                   pr.name_ka as Product, p.issued_on as IssuedOn, p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo, p.status as Status,
                   p.premium_currency as PremiumCurrency, p.premium_rate as PremiumRate, p.cancel_grace_days as CancelGraceDays, p.cancelled_on as CancelledOn,
                   p.cancel_reason as CancelReason, p.contract_id as ContractId, p.replaces_policy_id as ReplacesPolicyId, rp.number as ReplacesNumber, p.change_note as ChangeNote
            from core.policy p
            join core.contract c on c.id = p.contract_id
            join core.party h on h.id = c.policyholder_id
            left join core.product pr on pr.id = p.product_id
            left join core.policy rp on rp.id = p.replaces_policy_id
            where p.id = @id
            """, new { id });
        if (policy is null) return null;
        var currency = policy.Currency;
        var changes = (await tx.QueryAsync<Related>("""
            select p.id as Id, p.number as Number, p.effective_from as EffectiveFrom, p.status as Status, p.change_note as ChangeNote,
                   (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id) as Premium
            from core.policy p where p.contract_id = @contractId and p.id <> @id order by p.effective_from, p.created_at
            """, new { contractId = policy.ContractId, id })).ToList();
        var objects = string.Join("; ", await tx.QueryAsync<string>("select description from core.insured_object where policy_id = @id", new { id }));
        var lines = (await tx.QueryAsync<Line>("""
            select l.id as Id, pc.name_ka as Coverage, l.class_code as ClassCode, l.sum_insured as SumInsured, l.deductible as Deductible,
                   l.premium as Premium, l.premium_gel as PremiumGel
            from core.policy_line l left join core.product_coverage pc on pc.id = l.product_coverage_id
            where l.policy_id = @id order by pc.sort_order
            """, new { id })).ToList();
        var ledger = (await tx.QueryAsync<PolicyCard.LedgerRow>("""
            select t.id as Id, t.cancels_transaction_id as CancelsId, t.amount as Amount, t.booked_on as BookedOn, t.cover_from as CoverFrom, t.cover_to as CoverTo
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id = @id order by t.created_at
            """, new { id })).ToList();
        var currentByLine = PolicyCard.CurrentPremiumByLine(lines.Select(l => l.Id), await tx.QueryAsync<PolicyCard.LineLedgerRow>("""
            select t.policy_line_id as LineId, t.amount as Amount, t.booked_on as BookedOn
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id
            """, new { id }), today);
        // The card is as of today (Tbilisi): a schedule reduction booked for a later day counts only from that day (H23).
        var schedule = await Billing.LoadScheduleAsync(tx, id, today);
        // One receipt may be split over several policies (Codex PR #17, item 5): the whole receipt, the part allocated
        // here, and whether a reversal reaches other policies.
        var payments = (await tx.QueryAsync<Payment>("""
            select p.id as Id, p.received_on as ReceivedOn, p.amount_gel as AmountGel, p.method as Method, p.kind as Kind, p.reference as Reference,
                   convert(bit, case when exists (select 1 from core.payment r where r.reverses_payment_id = p.id) then 1 else 0 end) as Reversed,
                   (select string_agg(d.day, ', ') within group (order by d.day) from (
                        select distinct format(a.booked_on, 'dd.MM.yyyy') as day from core.payment_allocation a
                        join core.installment i on i.id = a.installment_id where a.payment_id = p.id and i.policy_id = @id) d) as AllocatedOn,
                   (select sum(a.amount_gel) from core.payment_allocation a
                    join core.installment i on i.id = a.installment_id where a.payment_id = p.id and i.policy_id = @id) as HereGel,
                   convert(bit, case when exists (select 1 from core.payment_allocation a join core.installment i on i.id = a.installment_id
                                                  where a.payment_id = p.id and i.policy_id <> @id) then 1 else 0 end) as Shared
            from core.payment p
            where exists (select 1 from core.payment_allocation a join core.installment i on i.id = a.installment_id
                          where a.payment_id = p.id and i.policy_id = @id)
            order by p.received_on, p.created_at
            """, new { id })).ToList();
        var allocations = (await tx.QueryAsync<Billing.AllocationRow>("""
            select a.amount as Amount, p.received_on as ReceivedOn, p.method as Method
            from core.payment_allocation a
            join core.payment p on p.id = a.payment_id
            join core.installment i on i.id = a.installment_id
            where i.policy_id = @id
            """, new { id })).ToList();
        var intermediaries = (await tx.QueryAsync<IntermediaryRow>("""
            select p.display_name as Name, pi.commission_percent as CommissionPercent
            from core.policy_intermediary pi
            join core.intermediary i on i.id = pi.intermediary_id
            join core.party p on p.id = i.party_id
            where pi.policy_id = @id
            """, new { id })).ToList();
        var cessions = (await tx.QueryAsync<Cession>("""
            select c.id as Id, tr.id as TreatyId, tr.number as TreatyNumber, tr.name as TreatyName, tr.kind as Kind, l.class_code as ClassCode,
                   c.share_percent as SharePercent, c.amount as Amount, c.amount_gel as AmountGel, c.commission_gel as CommissionGel, c.booked_on as BookedOn,
                   c.cover_from as CoverFrom, c.cover_to as CoverTo, convert(bit, case when c.cancels_cession_id is not null then 1 else 0 end) as IsCancellation
            from core.premium_cession c
            join core.ri_treaty tr on tr.id = c.treaty_id
            join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id = @id order by c.created_at
            """, new { id })).ToList();

        // Receivables are revalued at the official rate of the reporting date; when today's rate is not published yet,
        // the latest earlier rate is used and the card says which date.
        var valuation = await rates.LatestRateOnOrBeforeAsync(currency, today);
        var (position, positionGel) = PolicyCard.ExactPremiumPosition(ledger, today, currency, policy.PremiumRate);
        // The intermediaries' percents summed exactly as basis points (12.50 % → 1 250), then the same ledger as commission.
        var commissionBasisPoints = intermediaries.Aggregate(System.Numerics.BigInteger.Zero, (s, i) => s + PaymentActions.Exact(i.CommissionPercent));
        var commissionGel = PolicyCard.ExactPremiumPosition(ledger, today, currency, policy.PremiumRate, (long)commissionBasisPoints).PositionGel;
        var receivable = PolicyCard.ExactReceivable(schedule, allocations, today, currency, valuation.Rate);
        var cancelDate = PolicyCard.ExactCancellableFrom(receivable.Balances, policy.CancelGraceDays);
        return new PolicyCardView(policy, changes, objects, lines, currentByLine, payments, intermediaries, cessions, today, valuation, position, positionGel,
            commissionBasisPoints, commissionGel, receivable, cancelDate);
    }
}
