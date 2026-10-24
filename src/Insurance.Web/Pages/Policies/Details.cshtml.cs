using System.Numerics;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>
/// The policy card (legacy policies/[id]/page.tsx with policy-actions.tsx): the policy, its changes, coverages, cessions,
/// financial position, schedule, refunds and reversals waiting for the financial director, and its payments. Its forms
/// post here: approve (approvePolicy), reverse a receipt (reversePayment), refund an overpayment (refundOverpayment),
/// the financial director's approvals (approveReceiptReversalRequest, approvePremiumRefundRequest), and a facultative
/// placement (placeFacultativeOnPolicy).
/// </summary>
public sealed class DetailsModel(Rates rates) : AppPageModel
{
    public sealed record PolicyRow(Guid Id, string Number, string ContractNumber, Guid PolicyholderId, string Policyholder, string Segment, string? Product,
        DateOnly IssuedOn, DateOnly EffectiveFrom, DateOnly EffectiveTo, string Status, string PremiumCurrency, decimal PremiumRate, int? CancelGraceDays,
        DateOnly? CancelledOn, string? CancelReason, Guid ContractId, Guid? ReplacesPolicyId, string? ReplacesNumber, string? ChangeNote)
    {
        public string Currency => PremiumCurrency.Trim();
    }

    public sealed record Related(Guid Id, string Number, DateOnly EffectiveFrom, string Status, string? ChangeNote, decimal Premium);

    public sealed record Line(Guid Id, string? Coverage, string ClassCode, decimal? SumInsured, decimal? Deductible, decimal Premium, decimal PremiumGel);

    public sealed record Payment(Guid Id, DateOnly ReceivedOn, decimal AmountGel, string Method, string Kind, string? Reference, bool Reversed, string? AllocatedOn, decimal? HereGel, bool Shared);

    public sealed record IntermediaryRow(string Name, decimal CommissionPercent);

    public sealed record RefundRequestRow(Guid Id, DateOnly RefundedOn, decimal AmountGel, string? Requester, string? Approver);

    public sealed record ReversalRequestRow(Guid Id, DateOnly ReversedOn, decimal AmountGel, string Reason, string? Requester, string? Approver);

    public sealed record Cession(Guid Id, Guid TreatyId, string TreatyNumber, string TreatyName, string Kind, string ClassCode, decimal SharePercent, decimal Amount,
        decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, bool IsCancellation);

    public sealed record Facultative(Guid Id, string Number, string Name, decimal? CededPercent);

    public PolicyRow Policy { get; private set; } = null!;
    public IReadOnlyList<Related> Changes { get; private set; } = [];
    public string Objects { get; private set; } = "";
    public IReadOnlyList<Line> Lines { get; private set; } = [];
    /// <summary>Each line's current premium in tetri, as of the card's day (PolicyCard.CurrentPremiumByLine).</summary>
    public IReadOnlyDictionary<Guid, BigInteger> CurrentByLine { get; private set; } = new Dictionary<Guid, BigInteger>();
    public IReadOnlyList<Payment> Payments { get; private set; } = [];
    public IReadOnlyList<IntermediaryRow> Intermediaries { get; private set; } = [];
    public IReadOnlyList<RefundRequestRow> RefundRequests { get; private set; } = [];
    public IReadOnlyList<ReversalRequestRow> ReversalRequests { get; private set; } = [];
    public IReadOnlyList<Cession> Cessions { get; private set; } = [];
    public IReadOnlyList<Facultative> FacultativeTreaties { get; private set; } = [];
    public bool Receives { get; private set; }
    public bool Approves { get; private set; }
    public bool AuthorisesLarge { get; private set; }
    public BigInteger AdvancesGel { get; private set; }
    public BigInteger ReleasableGel { get; private set; }
    public DateOnly Today { get; private set; }
    public (decimal Rate, DateOnly Date) Valuation { get; private set; }
    public PolicyCard.Position Position { get; private set; } = null!;
    public PolicyCard.Position PositionGel { get; private set; } = null!;
    public BigInteger CommissionBasisPoints { get; private set; }
    public PolicyCard.Position CommissionGel { get; private set; } = null!;
    public PolicyCard.Receivable Receivable { get; private set; } = null!;
    public DateOnly? CancelDate { get; private set; }
    public PolicyActions.ApproveAnswer? Answer { get; private set; }

    /// <summary>The address bar's message codes, single strings only (a repeated key is dropped).</summary>
    public string? Q(string name) => Query(name);

    /// <summary>A receipt's, an allocation's or a transfer's answer in whole tetri; a malformed one shows no notice.</summary>
    public BigInteger? Tetri(string name) => PolicyCard.TetriParam(Query(name));

    private bool ValidRoute(out Guid id)
    {
        id = Guid.Empty;
        return Uuid.TryParse(RouteData.Values["id"]?.ToString(), out id);
    }

    private async Task<bool> LoadAsync(Guid id)
    {
        // One reporting day for the whole card, taken once (Codex PR #18 review, item 3).
        Today = Db.Today();
        var policy = await Db.ReadAsync(c => c.QuerySingleOrDefaultAsync<PolicyRow>("""
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
            """, new { id }));
        if (policy is null) return false;
        Policy = policy;
        var currency = policy.Currency;

        await Db.ReadTxAsync(async tx =>
        {
            Changes = (await tx.QueryAsync<Related>("""
                select p.id as Id, p.number as Number, p.effective_from as EffectiveFrom, p.status as Status, p.change_note as ChangeNote,
                       (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id) as Premium
                from core.policy p where p.contract_id = @contractId and p.id <> @id order by p.effective_from, p.created_at
                """, new { contractId = policy.ContractId, id })).ToList();
            Objects = string.Join("; ", await tx.QueryAsync<string>("select description from core.insured_object where policy_id = @id", new { id }));
            Lines = (await tx.QueryAsync<Line>("""
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
            CurrentByLine = PolicyCard.CurrentPremiumByLine(Lines.Select(l => l.Id), await tx.QueryAsync<PolicyCard.LineLedgerRow>("""
                select t.policy_line_id as LineId, t.amount as Amount, t.booked_on as BookedOn
                from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id
                """, new { id }), Today);
            // The card is as of today (Tbilisi): a schedule reduction booked for a later day counts only from that day (H23).
            var schedule = await Billing.LoadScheduleAsync(tx, id, Today);
            // One receipt may be split over several policies (Codex PR #17, item 5): the whole receipt, the part allocated
            // here, and whether a reversal reaches other policies.
            Payments = (await tx.QueryAsync<Payment>("""
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
            Intermediaries = (await tx.QueryAsync<IntermediaryRow>("""
                select p.display_name as Name, pi.commission_percent as CommissionPercent
                from core.policy_intermediary pi
                join core.intermediary i on i.id = pi.intermediary_id
                join core.party p on p.id = i.party_id
                where pi.policy_id = @id
                """, new { id })).ToList();
            var advances = Me.CanWrite && policy.Status != "draft" ? await Billing.LoadAdvancesAsync(tx, policy.PolicyholderId) : [];
            var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
            bool Has(string key) => Permissions.EvaluatePermission(Me.Subject, key, inputs.Overrides, inputs.Groups).Allowed;
            // "Record a payment" is the premium receipt's own permission; print, change and cancel stay with canWrite.
            Receives = Has("premium.receipt.create");
            Approves = Has("policy.approve");
            AuthorisesLarge = Has("payment.large.authorize");
            RefundRequests = (await tx.QueryAsync<RefundRequestRow>("""
                select q.id as Id, q.refunded_on as RefundedOn, q.amount_gel as AmountGel, u.full_name as Requester, a.full_name as Approver
                from core.premium_refund_request q left join core.app_user u on u.id = q.created_by
                left join core.payment m on m.refund_request_id = q.id left join core.app_user a on a.id = m.created_by
                where q.policy_id = @id order by q.created_at
                """, new { id })).ToList();
            ReversalRequests = (await tx.QueryAsync<ReversalRequestRow>("""
                select q.id as Id, q.reversed_on as ReversedOn, q.amount_gel as AmountGel, q.reason as Reason, u.full_name as Requester, a.full_name as Approver
                from core.receipt_reversal_request q left join core.app_user u on u.id = q.created_by
                left join core.payment m on m.reversal_request_id = q.id left join core.app_user a on a.id = m.created_by
                where q.policy_id = @id order by q.created_at
                """, new { id })).ToList();
            AdvancesGel = advances.Aggregate(BigInteger.Zero, (s, r) => s + PaymentActions.Exact(r.AvailableGel));
            // Overpayments on the client's other policies that could pay this one (owner, 07.10.2026): shown from issue on.
            ReleasableGel = Me.CanWrite && policy.Status != "draft"
                ? (await Billing.LoadReleasableAsync(tx, policy.PolicyholderId, Today, id)).Aggregate(BigInteger.Zero, (s, r) => s + PaymentActions.Exact(r.ExcessGel))
                : 0;
            Cessions = (await tx.QueryAsync<Cession>("""
                select c.id as Id, tr.id as TreatyId, tr.number as TreatyNumber, tr.name as TreatyName, tr.kind as Kind, l.class_code as ClassCode,
                       c.share_percent as SharePercent, c.amount as Amount, c.amount_gel as AmountGel, c.commission_gel as CommissionGel, c.booked_on as BookedOn,
                       c.cover_from as CoverFrom, c.cover_to as CoverTo, convert(bit, case when c.cancels_cession_id is not null then 1 else 0 end) as IsCancellation
                from core.premium_cession c
                join core.ri_treaty tr on tr.id = c.treaty_id
                join core.premium_transaction t on t.id = c.premium_transaction_id
                join core.policy_line l on l.id = t.policy_line_id
                where l.policy_id = @id order by c.created_at
                """, new { id })).ToList();
            FacultativeTreaties = (await tx.QueryAsync<Facultative>("""
                select id as Id, number as Number, name as Name, ceded_percent as CededPercent from core.ri_treaty
                where kind = N'facultative' and status = N'active' and period_from <= @from and period_to >= @from
                  and id not in (select treaty_id from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
                                 join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id)
                order by number
                """, new { from = policy.EffectiveFrom, id })).ToList();

            // Receivables are revalued at the official rate of the reporting date; when today's rate is not published yet,
            // the latest earlier rate is used and the card says which date.
            Valuation = await rates.LatestRateOnOrBeforeAsync(currency, Today);
            (Position, PositionGel) = PolicyCard.ExactPremiumPosition(ledger, Today, currency, policy.PremiumRate);
            // The intermediaries' percents summed exactly as basis points (12.50 % → 1 250), then the same ledger as commission.
            CommissionBasisPoints = Intermediaries.Aggregate(BigInteger.Zero, (s, i) => s + PaymentActions.Exact(i.CommissionPercent));
            CommissionGel = PolicyCard.ExactPremiumPosition(ledger, Today, currency, policy.PremiumRate, (long)CommissionBasisPoints).PositionGel;
            Receivable = PolicyCard.ExactReceivable(schedule, allocations, Today, currency, Valuation.Rate);
            CancelDate = PolicyCard.ExactCancellableFrom(Receivable.Balances, policy.CancelGraceDays);
            return 0;
        });
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!ValidRoute(out var id) || !await LoadAsync(id)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync()
    {
        if (!ValidRoute(out var id) || !await LoadAsync(id)) return NotFound();
        return new JsonResult(new {
            Policy.Id, Policy.Number, Client=Policy.Policyholder, Policy.Product, Vehicle=Objects,
            From=Policy.EffectiveFrom, To=Policy.EffectiveTo, Policy.Status, Policy.Currency,
            // The premium as it stands on the card's day (a correction in the open month included, PR #211), and as issued.
            Premium=(decimal)CurrentByLine.Values.Aggregate(BigInteger.Zero, (a, b) => a + b)/100m, IssuedPremium=Lines.Sum(l=>l.Premium),
            Outstanding=(decimal)Receivable.Outstanding/100m,
            OutstandingGel=(decimal)Receivable.OutstandingGel/100m,
            Overdue=(decimal)Receivable.Overdue/100m,
            Overpaid=(decimal)Receivable.Overpaid/100m,
            Cessions=Cessions.Select(c=>new { c.TreatyId,c.TreatyNumber,c.TreatyName,c.SharePercent,c.Amount,c.AmountGel,c.BookedOn,c.IsCancellation }),
            Schedule=Receivable.Balances.Select(b=>new {b.DueOn,Amount=(decimal)b.Amount/100m,Outstanding=(decimal)b.Outstanding/100m}),
            AsOf=Today, Receives, Approves, Me.CanWrite
        });
    }

    private IActionResult Back(string? to) => Redirect(to ?? $"/policies/{RouteData.Values["id"]}");

    public async Task<IActionResult> OnPostApproveAsync()
    {
        if (!ValidRoute(out var id)) return NotFound();
        Answer = await PolicyActions.ApprovePolicyAsync(Db, Me, Form("id") ?? "");
        if (!await LoadAsync(id)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostReversePaymentAsync() =>
        Back(await PaymentActions.ReversePaymentAsync(Db, Me, Form("payment_id"), Form("policy_id"), Form("reason")));

    public async Task<IActionResult> OnPostRefundOverpaymentAsync() =>
        Back(await PaymentActions.RefundOverpaymentAsync(Db, rates, Me, Form("policy_id"), Form("method"), Form("reference")));

    public async Task<IActionResult> OnPostApproveReversalRequestAsync() =>
        Back(await PaymentActions.ApproveReceiptReversalRequestAsync(Db, Me, Form("policy_id"), Form("request_id")));

    public async Task<IActionResult> OnPostApproveRefundRequestAsync() =>
        Back(await PaymentActions.ApprovePremiumRefundRequestAsync(Db, rates, Me, Form("policy_id"), Form("request_id")));

    public async Task<IActionResult> OnPostPlaceFacultativeAsync() =>
        Back(await PolicyActions.PlaceFacultativeAsync(Db, Me, Form("policy_id"), Form("treaty_id")));
}
