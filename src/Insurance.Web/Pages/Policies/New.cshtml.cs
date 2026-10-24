using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>A new motor policy (legacy policies/new/page.tsx, policy-form.tsx, action issuePolicy).</summary>
public sealed class NewModel(Rates rates) : AppPageModel
{
    public sealed record Coverage(string Code, string Name, string ClassCode, bool IsMandatory);

    public sealed record Intermediary(Guid Id, string Label);

    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = "";
    public IReadOnlyList<Coverage> Coverages { get; private set; } = [];
    public IReadOnlyList<Intermediary> Intermediaries { get; private set; } = [];

    /// <summary>Same calendar day one year later (the end date itself is not covered); 365 days on for 29 February.</summary>
    public static DateOnly OneYearAfter(DateOnly date) =>
        date.Month == 2 && date.Day == 29 && !DateTime.IsLeapYear(date.Year + 1) ? date.AddDays(365) : new DateOnly(date.Year + 1, date.Month, date.Day);

    private async Task<bool> LoadAsync()
    {
        var product = await Db.ReadAsync(c => c.QuerySingleOrDefaultAsync<(Guid Id, string NameKa)?>(
            "select id, name_ka from core.product where code = N'CASCO' and is_active = 1"));
        if (product is not { } p) return false;
        ProductId = p.Id;
        ProductName = p.NameKa;
        Coverages = (await Db.ReadAsync(c => c.QueryAsync<Coverage>("""
            select code as Code, name_ka as Name, class_code as ClassCode, is_mandatory as IsMandatory from core.product_coverage
            where product_id = @id order by sort_order
            """, new { id = p.Id }))).ToList();
        var intermediaries = await Db.ReadAsync(c => c.QueryAsync<(Guid Id, string Label, decimal? Commission)>("""
            select i.id, p.display_name, i.commission_percent
            from core.intermediary i join core.party p on p.id = i.party_id
            where i.status = N'active' order by lower(p.display_name)
            """));
        Intermediaries = intermediaries.Select(i => new Intermediary(i.Id,
            $"{i.Label} ({Ka.Intermediary.Fields.CommissionPercent} {(i.Commission is { } c ? Fmt.Plain(c) : "—")})")).ToList();
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.CanWrite) return Redirect("/policies");
        if (!await LoadAsync()) return Redirect("/policies");
        var today = Db.Today();
        Values["effective_from"] = Dates.ToIso(today);
        Values["effective_to"] = Dates.ToIso(OneYearAfter(today));
        Values["currency"] = "GEL";
        Values["installments"] = "1";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var values = TrimmedForm();
        var outcome = await PolicyActions.IssuePolicyAsync(Db, rates, Me, values, code => Posted($"cov_{code}_on"));
        if (outcome.Redirect is { } to) return Redirect(to);
        foreach (var (k, v) in outcome.Errors) Errors[k] = v;
        FormError = outcome.Message;
        ShowValues(values);
        if (!await LoadAsync()) return Redirect("/policies");
        return Page();
    }
}
