using Insurance.Application.Data;

namespace Insurance.Application.Services;

/// <summary>What the claim screens read (legacy src/app/(app)/claims/page.tsx, new/page.tsx and [id]/page.tsx).</summary>
public static class ClaimReads
{
    public sealed record ListRow(Guid Id, string Number, string PolicyNumber, string Policyholder, string? Object, DateOnly OccurredOn, DateOnly ReportedOn,
        string Status, string Currency, decimal Reserve, decimal Paid);

    /// <summary>
    /// The claims list, newest first, at most 200: shown as the loss event number and the class (owner, 06.10.2026: "მხოლოდ
    /// შემთხვევის ნომერი და სახეობა"); searched by event, claim or policy number or the policyholder's name.
    /// </summary>
    public static async Task<IReadOnlyList<ListRow>> ListAsync(Tx db, string q, string status)
    {
        var pattern = $"%{q.ToLowerInvariant()}%";
        return (await db.QueryAsync<ListRow>("""
            select top (200) c.id as Id, v.number + N' · ' + l.class_code as Number, p.number as PolicyNumber, h.display_name as Policyholder,
                   (select string_agg(o.description, N'; ') from core.insured_object o where o.policy_id = p.id) as Object,
                   c.occurred_on as OccurredOn, c.reported_on as ReportedOn, c.status as Status, c.currency as Currency,
                   (select coalesce(sum(r.change), 0) from core.claim_reserve r where r.claim_id = c.id) as Reserve,
                   (select coalesce(sum(cp.amount), 0) from core.claim_payment cp where cp.claim_id = c.id) as Paid
            from core.claim c
            join core.loss_event v on v.id = c.loss_event_id
            join core.policy_line l on l.id = c.policy_line_id
            join core.policy p on p.id = c.policy_id
            join core.contract co on co.id = p.contract_id
            join core.party h on h.id = co.policyholder_id
            where (@q = N'' or lower(v.number) like @pattern or lower(c.number) like @pattern or lower(p.number) like @pattern or lower(h.display_name) like @pattern)
              and (@status = N'' or c.status = @status)
            order by c.created_at desc
            """, new { q, pattern, status })).ToList();
    }

    public sealed record PolicyForNew(Guid Id, string Number, string Status, DateOnly EffectiveFrom, DateOnly EffectiveTo, string PremiumCurrency, string Policyholder, string? Object);

    public sealed record CoverageLine(Guid Id, string? Name, string ClassCode, decimal? SumInsured, decimal? Deductible);

    public static Task<PolicyForNew?> PolicyByNumberAsync(Tx db, string number) =>
        db.QueryFirstOrDefaultAsync<PolicyForNew>("""
            select p.id as Id, p.number as Number, p.status as Status, p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   p.premium_currency as PremiumCurrency, h.display_name as Policyholder,
                   (select string_agg(o.description, N'; ') from core.insured_object o where o.policy_id = p.id) as Object
            from core.policy p join core.contract c on c.id = p.contract_id join core.party h on h.id = c.policyholder_id
            where upper(p.number) = @number
            """, new { number });

    /// <summary>The policy's coverages in the product's order (PostgreSQL puts a line without a product coverage last).</summary>
    public static async Task<IReadOnlyList<CoverageLine>> LinesAsync(Tx db, Guid policyId) =>
        (await db.QueryAsync<CoverageLine>("""
            select l.id as Id, pc.name_ka as Name, l.class_code as ClassCode, l.sum_insured as SumInsured, l.deductible as Deductible
            from core.policy_line l left join core.product_coverage pc on pc.id = l.product_coverage_id
            where l.policy_id = @policyId order by case when pc.sort_order is null then 1 else 0 end, pc.sort_order
            """, new { policyId })).ToList();

    public sealed record ClaimCard(Guid Id, string Number, Guid PolicyId, string PolicyNumber, string PolicyStatus, string Policyholder, string? Object, string? Coverage,
        string ClassCode, decimal? SumInsured, decimal? Deductible, string Currency, DateOnly OccurredOn, string? OccurredPlace, DateOnly ReportedOn, string Description,
        string Status, decimal? ClaimedAmount, decimal? ApprovedAmount, decimal DeductibleAmount, bool IsTotalLoss, string? RejectionReason, DateOnly? ClosedOn,
        string PolicyCurrency, decimal? LimitGel, decimal? DeductibleGel, decimal? LimitRate, DateOnly? LimitRateOn)
    {
        /// <summary>A GEL claim on a foreign-currency policy (H46): its limit and deductible were fixed in GEL on the occurrence day.</summary>
        public bool CrossCurrency => LimitRate is not null;
    }

    public sealed record ReserveRow(Guid Id, DateOnly BookedOn, decimal Change, string Reason, string? UserName);

    /// <remarks>OffsetRate, OffsetCurrency: the set-off's conversion (H46); OffsetInPolicy: the set-off in the policy's currency, from its receipt's allocations.</remarks>
    public sealed record PaymentRow(Guid Id, DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel,
        string PayeeKind, string? PayeeName, string? Note, string Kind, Guid? ReversesClaimPaymentId, string? ReversalReason, DateOnly? ReturnedOn, string? BankReference,
        decimal? OffsetRate, string? OffsetCurrency, decimal? OffsetInPolicy)
    {
        /// <summary>For a reversal: the day of the payment it reverses.</summary>
        public DateOnly? ReversesOn { get; init; }

        /// <summary>For a payment: the day it was reversed.</summary>
        public DateOnly? ReversedOn { get; init; }
    }

    public sealed record ReserveRequestRow(Guid Id, DateOnly BookedOn, decimal BaseLevel, decimal NewLevel, decimal Change, string Reason, Guid CreatedBy, string? Requester, string? Approver);

    public sealed record PaymentRequestRow(Guid Id, DateOnly PaidOn, decimal Amount, decimal NetPaid, decimal NetPaidGel, string? PayeeName, string? Requester, string? Approver);

    public sealed record RecoveryRow(Guid Id, string Kind, DateOnly ReceivedOn, decimal AmountGel, string? Note);

    public sealed record EventPart(Guid Id, string ClassCode, string? Coverage, string Status, decimal Reserve, decimal Paid, Guid LineId);

    public sealed record FreeLine(Guid Id, string Name, string ClassCode);

    public sealed record ClaimPage(ClaimCard Claim, IReadOnlyList<ReserveRow> Reserves, IReadOnlyList<PaymentRow> Payments, IReadOnlyList<ReserveRequestRow> ReserveRequests,
        IReadOnlyList<PaymentRequestRow> PaymentRequests, IReadOnlyList<RecoveryRow> Recoveries, (Guid Id, string Number)? Event, IReadOnlyList<EventPart> Parts,
        IReadOnlyList<FreeLine> FreeLines, IReadOnlyList<ReinsuranceStore.ClaimShare> Shares);

    /// <summary>Everything the claim page shows, or null when there is no such claim.</summary>
    public static async Task<ClaimPage?> PageAsync(Tx db, Guid id)
    {
        var claim = await db.QuerySingleOrDefaultAsync<ClaimCard>("""
            select c.id as Id, c.number as Number, p.id as PolicyId, p.number as PolicyNumber, p.status as PolicyStatus, h.display_name as Policyholder,
                   (select string_agg(o.description, N'; ') from core.insured_object o where o.policy_id = p.id) as Object,
                   pc.name_ka as Coverage, l.class_code as ClassCode, l.sum_insured as SumInsured, l.deductible as Deductible, c.currency as Currency,
                   c.occurred_on as OccurredOn, c.occurred_place as OccurredPlace, c.reported_on as ReportedOn, c.description as Description, c.status as Status,
                   c.claimed_amount as ClaimedAmount, c.approved_amount as ApprovedAmount, c.deductible_amount as DeductibleAmount, c.is_total_loss as IsTotalLoss,
                   c.rejection_reason as RejectionReason, c.closed_on as ClosedOn, p.premium_currency as PolicyCurrency, c.limit_gel as LimitGel,
                   c.deductible_gel as DeductibleGel, c.limit_rate as LimitRate, c.limit_rate_on as LimitRateOn
            from core.claim c
            join core.policy p on p.id = c.policy_id
            join core.contract co on co.id = p.contract_id
            join core.party h on h.id = co.policyholder_id
            join core.policy_line l on l.id = c.policy_line_id
            left join core.product_coverage pc on pc.id = l.product_coverage_id
            where c.id = @id
            """, new { id });
        if (claim is null) return null;
        var reserves = (await db.QueryAsync<ReserveRow>("""
            select r.id as Id, r.booked_on as BookedOn, r.change as Change, r.reason as Reason, u.full_name as UserName
            from core.claim_reserve r left join core.app_user u on u.id = r.created_by
            where r.claim_id = @id order by r.created_at
            """, new { id })).ToList();
        var paymentRows = (await db.QueryAsync<PaymentRow>("""
            select cp.id as Id, cp.paid_on as PaidOn, cp.amount as Amount, cp.deductible_withheld as DeductibleWithheld, cp.premium_offset as PremiumOffset,
                   cp.net_paid as NetPaid, cp.net_paid_gel as NetPaidGel, cp.payee_kind as PayeeKind, pa.display_name as PayeeName, cp.note as Note, cp.kind as Kind,
                   cp.reverses_claim_payment_id as ReversesClaimPaymentId, cp.reversal_reason as ReversalReason, cp.returned_on as ReturnedOn, cp.bank_reference as BankReference,
                   cp.offset_rate as OffsetRate, cp.offset_currency as OffsetCurrency,
                   (select sum(a.amount) from core.payment_allocation a where a.payment_id = cp.offset_payment_id) as OffsetInPolicy
            from core.claim_payment cp left join core.party pa on pa.id = cp.payee_party_id
            where cp.claim_id = @id order by cp.created_at
            """, new { id })).ToList();
        // A reversal and the payment it reverses are on the same claim: each row is told the other's day.
        var paidOnOf = paymentRows.ToDictionary(p => p.Id, p => p.PaidOn);
        var reversedOnOf = new Dictionary<Guid, DateOnly>();
        foreach (var p in paymentRows) if (p.ReversesClaimPaymentId is { } r) reversedOnOf[r] = p.PaidOn;
        var payments = paymentRows.Select(p => p with
        {
            ReversesOn = p.ReversesClaimPaymentId is { } r && paidOnOf.TryGetValue(r, out var d) ? d : null,
            ReversedOn = reversedOnOf.TryGetValue(p.Id, out var on) ? on : null,
        }).ToList();
        // The reserve's requests (docs/05 ვ8): each with who asked and, once approved, its movement and who approved it.
        var reserveRequests = (await db.QueryAsync<ReserveRequestRow>("""
            select q.id as Id, q.booked_on as BookedOn, q.base_level as BaseLevel, q.new_level as NewLevel, q.change as Change, q.reason as Reason, q.created_by as CreatedBy,
                   u.full_name as Requester, a.full_name as Approver
            from core.claim_reserve_request q left join core.app_user u on u.id = q.created_by
            left join core.claim_reserve m on m.request_id = q.id left join core.app_user a on a.id = m.created_by
            where q.claim_id = @id order by q.created_at
            """, new { id })).ToList();
        // Payments above the company's threshold, waiting for the financial director (docs/05 ვ8): each with who asked and,
        // once authorised, who authorised it (the payment's created_by).
        var paymentRequests = (await db.QueryAsync<PaymentRequestRow>("""
            select q.id as Id, q.paid_on as PaidOn, q.amount as Amount, q.net_paid as NetPaid, q.net_paid_gel as NetPaidGel, pa.display_name as PayeeName,
                   u.full_name as Requester, a.full_name as Approver
            from core.claim_payment_request q left join core.party pa on pa.id = q.payee_party_id left join core.app_user u on u.id = q.created_by
            left join core.claim_payment m on m.request_id = q.id left join core.app_user a on a.id = m.created_by
            where q.claim_id = @id order by q.created_at
            """, new { id })).ToList();
        var recoveries = (await db.QueryAsync<RecoveryRow>(
            "select id as Id, kind as Kind, received_on as ReceivedOn, amount_gel as AmountGel, note as Note from core.claim_recovery where claim_id = @id order by created_at",
            new { id })).ToList();
        // The loss event this claim belongs to, and all its claims (one per coverage) by class (docs/07 §10).
        var ev = await db.QuerySingleOrDefaultAsync<(Guid Id, string Number)?>(
            "select v.id, v.number from core.loss_event v join core.claim c on c.loss_event_id = v.id where c.id = @id", new { id });
        var parts = ev is { } e
            ? (await db.QueryAsync<EventPart>("""
                select c.id as Id, l.class_code as ClassCode, pc.name_ka as Coverage, c.status as Status,
                       (select coalesce(sum(r.change), 0) from core.claim_reserve r where r.claim_id = c.id) as Reserve,
                       (select coalesce(sum(p.amount), 0) from core.claim_payment p where p.claim_id = c.id) as Paid,
                       c.policy_line_id as LineId
                from core.claim c join core.policy_line l on l.id = c.policy_line_id left join core.product_coverage pc on pc.id = l.product_coverage_id
                where c.loss_event_id = @eventId order by l.class_code, c.created_at
                """, new { eventId = e.Id })).ToList()
            : [];
        var freeLines = (await db.QueryAsync<FreeLine>("""
            select l.id as Id, coalesce(pc.name_ka, l.class_code) as Name, l.class_code as ClassCode
            from core.policy_line l left join core.product_coverage pc on pc.id = l.product_coverage_id
            where l.policy_id = @policyId and l.id not in @taken order by case when pc.sort_order is null then 1 else 0 end, pc.sort_order
            """, new { policyId = claim.PolicyId, taken = parts.Select(p => p.LineId).ToList() })).ToList();
        var shares = await ReinsuranceStore.ClaimSharesAsync(db, id);
        return new ClaimPage(claim, reserves, payments, reserveRequests, paymentRequests, recoveries, ev, parts, freeLines, shares);
    }
}
