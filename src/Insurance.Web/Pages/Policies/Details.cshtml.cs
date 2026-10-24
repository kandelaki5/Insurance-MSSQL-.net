using System.Numerics;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Ports;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using static Insurance.Application.Ports.PolicyCardView;

namespace Insurance.Web.Pages.Policies;

/// <summary>
/// The policy card (legacy policies/[id]/page.tsx with policy-actions.tsx): the policy, its changes, coverages, cessions,
/// financial position, schedule, refunds and reversals waiting for the financial director, and its payments. Its forms
/// post here: approve (approvePolicy), reverse a receipt (reversePayment), refund an overpayment (refundOverpayment),
/// the financial director's approvals (approveReceiptReversalRequest, approvePremiumRefundRequest), and a facultative
/// placement (placeFacultativeOnPolicy).
/// </summary>
public sealed class DetailsModel(Rates rates, IPolicyCardQuery cards) : AppPageModel
{
    public sealed record RefundRequestRow(Guid Id, DateOnly RefundedOn, decimal AmountGel, string? Requester, string? Approver);

    public sealed record ReversalRequestRow(Guid Id, DateOnly ReversedOn, decimal AmountGel, string Reason, string? Requester, string? Approver);

    public sealed record Facultative(Guid Id, string Number, string Name, decimal? CededPercent);

    public Header Policy { get; private set; } = null!;
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
    /// <summary>The figures every reader sees, as IPolicyCardQuery read them.</summary>
    public PolicyCardView Card { get; private set; } = null!;
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
        // One snapshot for the whole card (Codex R006-03): what every reader sees and what it offers this reader, one committed state.
        return await Db.SnapshotAsync(async tx =>
        {
            // The figures every reader sees (IPolicyCardQuery, the same as /api/v1/policies/{id}).
            if (await cards.ReadAsync(tx, id, Today) is not { } card) return false;
            (Policy, Changes, Objects, Lines, CurrentByLine, Payments, Intermediaries, Cessions) =
                (card.Policy, card.Changes, card.Objects, card.Lines, card.CurrentByLine, card.Payments, card.Intermediaries, card.Cessions);
            (Valuation, Position, PositionGel, CommissionBasisPoints, CommissionGel, Receivable, CancelDate) =
                (card.Valuation, card.Position, card.PositionGel, card.CommissionBasisPoints, card.CommissionGel, card.Receivable, card.CancelDate);
            Card = card;
            var policy = card.Policy;

            // What this card offers its reader, in the same transaction.
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
            FacultativeTreaties = (await tx.QueryAsync<Facultative>("""
                select id as Id, number as Number, name as Name, ceded_percent as CededPercent from core.ri_treaty
                where kind = N'facultative' and status = N'active' and period_from <= @from and period_to >= @from
                  and id not in (select treaty_id from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
                                 join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id)
                order by number
                """, new { from = policy.EffectiveFrom, id })).ToList();
            return true;
        });
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
            Premium=(decimal)Card.CurrentPremium/100m, IssuedPremium=Lines.Sum(l=>l.Premium),
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
