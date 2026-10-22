using System.Globalization;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Claims;

/// <summary>
/// The claim's card and its actions (legacy src/app/(app)/claims/[id]/page.tsx with pay-form.tsx, add-coverage-form.tsx and
/// the actions of claims/actions.ts). Every form posts to a handler of this page; the action itself is ClaimActions.
/// The financial director's panel links to the payment requests shown here; their approval is
/// <see cref="OnPostApproveClaimPaymentRequestAsync"/>.
/// </summary>
public sealed class ClaimModel(Rates rates) : AppPageModel
{
    public Guid Id { get; private set; }
    public ClaimReads.ClaimPage Data { get; private set; } = null!;
    public DateOnly Today { get; } = Rates.TodayInTbilisi();

    /// <summary>The form a refused post came from ("pay" or "coverage"): only it shows the values as typed and its errors.</summary>
    public string? PostedFormName { get; private set; }

    public bool Writable => Me.CanWrite;
    public bool Open => Data.Claim.Status is not ("rejected" or "closed");
    public bool ReceivesRecovery { get; private set; }
    public bool RecordsPayment { get; private set; }
    public bool ReversesPayment { get; private set; }
    public bool ApprovesReserve { get; private set; }
    public bool AuthorisesPayment { get; private set; }

    public long ReserveTetri => Data.Reserves.Sum(r => Billing.Tetri(r.Change));
    public long PaidTetri => Data.Payments.Sum(p => Billing.Tetri(p.Amount));
    public long RemainingTetri => Data.Claim.ApprovedAmount is { } a ? Billing.Tetri(a) - PaidTetri : 0;
    public string Currency => Data.Claim.Currency.Trim();

    public string? Flag(string name) => Query(name);

    public async Task<IActionResult> OnGetAsync(Guid id) => await ShowAsync(id);

    private async Task<IActionResult> ShowAsync(Guid id)
    {
        Id = id;
        var data = await Db.ReadTxAsync(tx => ClaimReads.PageAsync(tx, id));
        if (data is null) return NotFound();
        Data = data;
        // Recording a recovery, recording a payment, reversing one, approving a reserve request and authorising a large
        // payment are their own permissions, from one read; the claim's other actions keep canWrite.
        var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
        bool May(string key) => Permissions.EvaluatePermission(Me.Subject, key, inputs.Overrides, inputs.Groups).Allowed;
        ReceivesRecovery = May("claim.recovery.receive");
        RecordsPayment = May("claim.payment.record");
        ReversesPayment = May("claim.payment.reverse") && data.Claim.Status is "approved" or "paid";
        ApprovesReserve = May("claim.reserve.approve");
        AuthorisesPayment = May("payment.large.authorize");
        return Page();
    }

    /// <summary>The query of this page without the handler: what the legacy page kept after an action that stayed on it.</summary>
    private string PageQuery()
    {
        var kept = Request.Query.Where(q => q.Key != "handler").SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? "")}")).ToList();
        return kept.Count == 0 ? "" : "?" + string.Join("&", kept);
    }

    /// <summary>The url a form of this page posts to: the page as it is shown, with the action's handler.</summary>
    public string Action(string handler)
    {
        var query = PageQuery();
        return $"{Request.Path}{(query == "" ? "?" : query + "&")}handler={handler}";
    }

    /// <summary>An action that answered nothing stays on the page: the page is shown again as it was (legacy revalidatePath).</summary>
    private IActionResult Stay(string? to) => Redirect(to ?? $"{Request.Path}{PageQuery()}");

    private async Task<IActionResult> RefusedAsync(Guid id, FormOutcome outcome, string form)
    {
        KeepPostedValues();
        foreach (var (field, message) in outcome.Errors) Errors[field] = message;
        FormError = outcome.Message;
        PostedFormName = form;
        return await ShowAsync(id);
    }

    public async Task<IActionResult> OnPostAddCoverageAsync(Guid id)
    {
        var outcome = await ClaimActions.AddEventCoverageAsync(Db, Me, Request.PostedForm());
        return outcome.RedirectTo is { } to ? Redirect(to) : await RefusedAsync(id, outcome, "coverage");
    }

    public async Task<IActionResult> OnPostPayClaimAsync(Guid id)
    {
        var outcome = await ClaimActions.PayClaimAsync(Db, rates, Me, Request.PostedForm(), Rates.TodayInTbilisi());
        return outcome.RedirectTo is { } to ? Redirect(to) : await RefusedAsync(id, outcome, "pay");
    }

    public async Task<IActionResult> OnPostChangeReserveAsync() =>
        Stay(await ClaimActions.ChangeReserveAsync(Db, Me, Request.PostedForm(), Rates.TodayInTbilisi()));

    public async Task<IActionResult> OnPostApproveReserveRequestAsync() =>
        Stay(await ClaimActions.ApproveReserveRequestAsync(Db, Me, Request.PostedForm()));

    public async Task<IActionResult> OnPostDecideAsync()
    {
        await ClaimActions.DecideClaimAsync(Db, Me, Request.PostedForm(), Rates.TodayInTbilisi());
        return Stay(null);
    }

    /// <summary>The financial director's authorisation of a claim payment request (claim_id, request_id): approveClaimPaymentRequest.</summary>
    public async Task<IActionResult> OnPostApproveClaimPaymentRequestAsync() =>
        Stay(await ClaimActions.ApproveClaimPaymentRequestAsync(Db, rates, Me, Request.PostedForm()));

    public async Task<IActionResult> OnPostAddRecoveryAsync() =>
        Stay(await ClaimActions.AddRecoveryAsync(Db, Me, Request.PostedForm()));

    public async Task<IActionResult> OnPostReverseClaimPaymentAsync() =>
        Stay(await ClaimActions.ReverseClaimPaymentAsync(Db, Me, Request.PostedForm(), Rates.TodayInTbilisi()));

    /// <summary>A value of the form a refused post came from, else the form's own starting value.</summary>
    public string FormValue(string form, string name, string fallback) => PostedFormName == form ? Value(name) : fallback;

    public string? FormFieldError(string form, string name) => PostedFormName == form ? Error(name) : null;

    public string? FormMessage(string form) => PostedFormName == form ? FormError : null;

    public static string Money(decimal value) => Billing.GroupedTetri(Billing.Tetri(value));

    public static string MoneyTetri(long tetri) => Billing.GroupedTetri(tetri);

    public static string Badge(string status) => status is "paid" or "closed" ? "low" : status == "rejected" ? "high" : "warn";

    public static string Text(decimal? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "";

    public static string Kind(Domain.Finance.TreatyKind kind) => Codes.Of(kind);
}
