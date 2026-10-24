using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>
/// Receiving premium on a policy (legacy policies/[id]/payment/page.tsx and payment-form.tsx; actions recordPayment,
/// allocateReceipt, transferOverpayment). Two rights meet here: a new receipt is premium.receipt.create; using an
/// earlier receipt's advance or another policy's overpayment is canWrite. Each part is shown by its own right.
/// </summary>
public sealed class PaymentModel(Rates rates) : AppPageModel
{
    public sealed record Overpaid(Guid PaymentId, DateOnly ReceivedOn, string Method, decimal PaymentGel, BigInteger Gel, string Policies);

    public string Number { get; private set; } = "";
    public string Currency { get; private set; } = "";
    public BigInteger Outstanding { get; private set; }
    public IReadOnlyList<Billing.AdvanceRow> Advances { get; private set; } = [];
    public IReadOnlyList<Overpaid> OverpaidRows { get; private set; } = [];
    public bool Receives { get; private set; }
    public bool Writes => Me.CanWrite;

    private bool ValidRoute(out Guid id) => Uuid.TryParse(RouteData.Values["id"]?.ToString(), out id);

    private async Task<IActionResult?> LoadAsync(Guid id)
    {
        var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
        Receives = Permissions.EvaluatePermission(Me.Subject, "premium.receipt.create", inputs.Overrides, inputs.Groups).Allowed;
        if (!Writes && !Receives) return Redirect($"/policies/{id}");
        var policy = await Db.ReadAsync(c => c.QuerySingleOrDefaultAsync<(string Number, string PremiumCurrency, string Status, Guid PolicyholderId)?>("""
            select p.number, p.premium_currency, p.status, c.policyholder_id
            from core.policy p join core.contract c on c.id = p.contract_id where p.id = @id
            """, new { id }));
        if (policy is not { } p) return NotFound();
        Number = p.Number;
        Currency = p.PremiumCurrency.Trim();
        var today = Db.Today();
        await Db.ReadTxAsync(async tx =>
        {
            // As of today, like the card and the payment itself (H23).
            var installments = await Billing.LoadScheduleAsync(tx, id, today);
            var allocations = (await tx.QueryAsync<Billing.AllocationRow>("""
                select a.amount as Amount, p.received_on as ReceivedOn, p.method as Method
                from core.payment_allocation a join core.payment p on p.id = a.payment_id join core.installment i on i.id = a.installment_id
                where i.policy_id = @id
                """, new { id })).ToList();
            Advances = Writes ? await Billing.LoadAdvancesAsync(tx, p.PolicyholderId) : [];
            // Overpayments on the client's other policies, grouped by the receipt that holds them (owner, 07.10.2026).
            var releasable = Writes ? await Billing.LoadReleasableAsync(tx, p.PolicyholderId, today, id) : [];
            OverpaidRows = releasable.GroupBy(r => r.PaymentId).Select(g => new Overpaid(g.Key, g.Last().ReceivedOn, g.Last().Method, g.Last().PaymentGel,
                g.Aggregate(BigInteger.Zero, (s, x) => s + PaymentActions.Exact(x.ExcessGel)), string.Join(", ", g.Select(x => x.PolicyNumber).Distinct()))).ToList();
            // Today's rate is needed, as receivableAsOf's GEL valuation demanded it, only when there is something to value.
            var rate = await rates.RateOnAsync(Currency, today) ?? 0;
            if (Currency != "GEL" && (installments.Count > 0 || allocations.Count > 0) && !(rate > 0))
                throw new InvalidOperationException($"No {Currency} rate for {Dates.ToIso(today)}");
            Outstanding = PolicyCard.ExactOutstanding(installments, allocations, today);
            return 0;
        });
        Values.TryAdd("received_on", Dates.ToIso(today));
        Values.TryAdd("method", "bank");
        return null;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!ValidRoute(out var id)) return NotFound();
        return await LoadAsync(id) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ValidRoute(out var id)) return NotFound();
        var values = TrimmedForm();
        var outcome = await PaymentActions.RecordPaymentAsync(Db, rates, Me, values);
        if (outcome.Redirect is { } to) return Redirect(to);
        foreach (var (k, v) in outcome.Errors) Errors[k] = v;
        FormError = outcome.Message;
        ShowValues(values);
        return await LoadAsync(id) ?? Page();
    }

    public async Task<IActionResult> OnPostAllocateAsync() =>
        Redirect(await PaymentActions.AllocateReceiptAsync(Db, rates, Me, Form("policy_id"), Form("payment_id")));

    public async Task<IActionResult> OnPostTransferAsync() =>
        Redirect(await PaymentActions.TransferOverpaymentAsync(Db, rates, Me, Form("policy_id"), Form("payment_id")));
}
