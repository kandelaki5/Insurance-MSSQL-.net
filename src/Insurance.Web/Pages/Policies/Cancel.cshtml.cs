using System.Text.Json;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>
/// Cancelling an active policy (legacy policies/[id]/cancel/page.tsx, cancel-form.tsx, action cancelPolicy). The preview
/// follows the date being entered (UAT H22): it is worked out here by the finance core (CancelPreview) for every day the
/// form allows, and the page only shows the one for the date typed.
/// </summary>
public sealed class CancelModel : AppPageModel
{
    public string Number { get; private set; } = "";
    public string Currency { get; private set; } = "";
    public DateOnly MinDate { get; private set; }
    public DateOnly MaxDate { get; private set; }
    public CancelPreviewResult Preview { get; private set; } = null!;
    public string PreviewsJson { get; private set; } = "{}";
    public string StartOn { get; private set; } = "";

    private async Task<IActionResult?> LoadAsync(Guid id, string? shownDate)
    {
        if (!Me.CanWrite) return Redirect($"/policies/{id}");
        var policy = await Db.ReadAsync(c => c.QuerySingleOrDefaultAsync<(string Number, string Status, DateOnly EffectiveFrom, DateOnly EffectiveTo, string PremiumCurrency)?>(
            "select number, status, effective_from, effective_to, premium_currency from core.policy where id = @id", new { id }));
        if (policy is not { } p) return NotFound();
        if (p.Status != "active") return Redirect($"/policies/{id}");
        var currency = Billing.ParseCurrency(p.PremiumCurrency);
        var rows = await Db.ReadAsync(c => c.QueryAsync<(decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)>("""
            select t.amount, t.booked_on, t.cover_from, t.cover_to
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id = @id and t.cancels_transaction_id is null
              and not exists (select 1 from core.premium_transaction r where r.cancels_transaction_id = t.id)
            """, new { id }));
        var paid = await Db.ReadAsync(c => c.ExecuteScalarAsync<decimal>("""
            select coalesce(sum(a.amount), 0) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id
            """, new { id }));
        var ledger = rows.Select(r => new PremiumTransaction(new Money(Billing.Tetri(r.Amount), currency), r.BookedOn, r.CoverFrom, r.CoverTo)).ToList();
        var cover = new DateRange(p.EffectiveFrom, p.EffectiveTo);
        var paidMinor = Billing.Tetri(paid);
        Number = p.Number;
        Currency = p.PremiumCurrency.Trim();
        MinDate = p.EffectiveFrom;
        MaxDate = Dates.AddDays(p.EffectiveTo, -1);
        // The form starts on today (moved into the cover).
        var startOn = CancelPreview.CancellationPreview(ledger, Rates.TodayInTbilisi(), 0, cover).On;
        StartOn = Dates.ToIso(startOn);
        Values.TryAdd("cancel_on", StartOn);
        var on = shownDate is not null && Dates.IsIsoDate(shownDate) ? Dates.ParseIsoDate(shownDate) : startOn;
        Preview = CancelPreview.CancellationPreview(ledger, on, paidMinor, cover);
        var previews = new Dictionary<string, object>();
        for (var day = MinDate; day <= MaxDate; day = day.AddDays(1))
        {
            var x = CancelPreview.CancellationPreview(ledger, day, paidMinor, cover);
            previews[Dates.ToIso(day)] = new[] { Fmt.Date(x.On), Billing.GroupedTetri(x.Written), Billing.GroupedTetri(x.Refund), Billing.GroupedTetri(x.Retained),
                Billing.GroupedTetri(x.Paid), Billing.GroupedTetri(Math.Abs(x.Balance)), x.Balance < 0 ? "1" : "0" };
        }
        PreviewsJson = JsonSerializer.Serialize(previews);
        return null;
    }

    private bool ValidRoute(out Guid id) => Uuid.TryParse(RouteData.Values["id"]?.ToString(), out id);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!ValidRoute(out var id)) return NotFound();
        return await LoadAsync(id, null) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ValidRoute(out var id)) return NotFound();
        var values = TrimmedForm();
        var outcome = await PolicyActions.CancelPolicyAsync(Db, Me, values);
        if (outcome.Redirect is { } to) return Redirect(to);
        foreach (var (k, v) in outcome.Errors) Errors[k] = v;
        FormError = outcome.Message;
        ShowValues(values);
        return await LoadAsync(id, values.GetValueOrDefault("cancel_on")) ?? Page();
    }
}
