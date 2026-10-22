using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>A change to an active policy (legacy policies/[id]/endorse/page.tsx, endorse-form.tsx, action endorsePolicy).</summary>
public sealed class EndorseModel(Rates rates) : AppPageModel
{
    public sealed record Coverage(string Code, string Name, string ClassCode, string CurrentLimit, string CurrentDeductible, string CurrentPremium, bool Included);

    public string Number { get; private set; } = "";
    public string Currency { get; private set; } = "";
    public DateOnly MinDate { get; private set; }
    public DateOnly MaxDate { get; private set; }
    public IReadOnlyList<Coverage> Coverages { get; private set; } = [];

    private static string Show(decimal? value) => value is { } v ? Fmt.Fixed(v, 2) : "—";

    private bool ValidRoute(out Guid id) => Uuid.TryParse(RouteData.Values["id"]?.ToString(), out id);

    private async Task<IActionResult?> LoadAsync(Guid id)
    {
        if (!Me.CanWrite) return Redirect($"/policies/{id}");
        var policy = await Db.ReadAsync(c => c.QuerySingleOrDefaultAsync<(string Number, string Status, DateOnly EffectiveFrom, DateOnly EffectiveTo, string PremiumCurrency, Guid? ProductId)?>(
            "select number, status, effective_from, effective_to, premium_currency, product_id from core.policy where id = @id", new { id }));
        if (policy is not { } p) return NotFound();
        if (p.Status != "active" || p.ProductId is not { } productId) return Redirect($"/policies/{id}");
        // Current terms per coverage: this policy's line for each coverage.
        var rows = await Db.ReadAsync(c => c.QueryAsync<(string Code, string NameKa, string ClassCode, decimal? SumInsured, decimal? Deductible, decimal? Premium)>("""
            select pc.code, pc.name_ka, pc.class_code, l.sum_insured, l.deductible, l.premium
            from core.product_coverage pc
            left join core.policy_line l on l.product_coverage_id = pc.id and l.policy_id = @id
            where pc.product_id = @productId
            order by pc.sort_order
            """, new { id, productId }));
        Coverages = rows.Select(c => new Coverage(c.Code, c.NameKa, c.ClassCode, Show(c.SumInsured), Show(c.Deductible), Show(c.Premium), c.Premium is not null)).ToList();
        var today = Rates.TodayInTbilisi();
        Values.TryAdd("change_on", Dates.ToIso(today < p.EffectiveFrom ? p.EffectiveFrom : today >= p.EffectiveTo ? Dates.AddDays(p.EffectiveTo, -1) : today));
        Number = p.Number;
        Currency = p.PremiumCurrency.Trim();
        MinDate = p.EffectiveFrom;
        MaxDate = Dates.AddDays(p.EffectiveTo, -1);
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
        var outcome = await PolicyActions.EndorsePolicyAsync(Db, rates, Me, values, code => Posted($"cov_{code}_on"));
        if (outcome.Redirect is { } to) return Redirect(to);
        foreach (var (k, v) in outcome.Errors) Errors[k] = v;
        FormError = outcome.Message;
        ShowValues(values);
        return await LoadAsync(id) ?? Page();
    }
}
