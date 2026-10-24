using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Claims;

/// <summary>Registering a loss event (legacy src/app/(app)/claims/new/page.tsx with claim-form.tsx and registerClaim).</summary>
public sealed class NewModel(Rates rates) : AppPageModel
{
    public string Number { get; private set; } = "";
    public ClaimReads.PolicyForNew? Policy { get; private set; }
    public IReadOnlyList<ClaimReads.CoverageLine> Lines { get; private set; } = [];
    public bool Usable => Policy is { Status: "active" or "cancelled" };

    /// <summary>The currency the claim will be recorded in (H46: GEL on a foreign-currency policy, by the company setting).</summary>
    public string ClaimCurrency { get; private set; } = "";

    /// <summary>A GEL claim on a foreign-currency policy: the limit and deductible stay in the policy's currency.</summary>
    public bool CrossCurrency => Policy is not null && ClaimCurrency != Policy.PremiumCurrency.Trim();
    private DateOnly? today;
    public DateOnly Today => today ??= Db.Today();

    /// <summary>The form's one-off key: a second submit opens the event the first one registered (docs/07 §10).</summary>
    public string RequestKey { get; private set; } = Guid.NewGuid().ToString();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.CanWrite) return Redirect("/claims");
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await ClaimActions.RegisterClaimAsync(Db, rates, Me, Request.PostedForm());
        if (outcome.RedirectTo is { } to) return Redirect(to);
        if (!Me.CanWrite) return Redirect("/claims");
        KeepTrimmedForm();
        foreach (var (field, message) in outcome.Errors) Errors[field] = message;
        FormError = outcome.Message;
        RequestKey = Value("request_key", RequestKey);
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Number = JsText.ToUpperCase(JsText.Trim(Query("policy") ?? ""));
        if (Number == "") return;
        Policy = await Db.ReadTxAsync(tx => ClaimReads.PolicyByNumberAsync(tx, Number));
        if (Policy is not null) Lines = await Db.ReadTxAsync(tx => ClaimReads.LinesAsync(tx, Policy.Id));
        if (Policy is not null) ClaimCurrency = await Db.ReadTxAsync(tx => ClaimActions.NewClaimCurrencyAsync(tx, Policy.PremiumCurrency));
        if (Policy is not null && !Values.ContainsKey("occurred_on") && !Values.ContainsKey("reported_on"))
        {
            Values["occurred_on"] = Fmt.Iso(Today < Policy.EffectiveFrom ? Policy.EffectiveFrom : Today);
            Values["reported_on"] = Fmt.Iso(Today);
        }
    }

    public static string Show(decimal? value) => value is { } v ? Fmt.Fixed(v, 2) : "—";
}
