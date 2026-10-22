using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Reinsurance.Treaties;

/// <summary>A new reinsurance treaty (legacy reinsurance/treaties/new/page.tsx, treaty-form.tsx, actions.ts createTreaty).</summary>
public sealed class NewModel : AppPageModel
{
    public sealed record ClassOption(string Code, string Name);

    public IReadOnlyList<ClassOption> Classes { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.CanWrite) return Redirect("/reinsurance/treaties");
        var year = DateTime.Now.Year;
        Values["kind"] = "quota_share";
        Values["currency"] = "GEL";
        Values["period_from"] = $"{year}-01-01";
        Values["period_to"] = $"{year}-12-31";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // The ticked classes go back with the other values, so a refused form keeps them (Codex H68).
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var classes = new List<string>();
        if (Request.HasFormContentType)
            foreach (var (key, value) in Request.Form)
            {
                if (key == "__RequestVerificationToken") continue;
                values[key] = (value.Count > 0 ? value[^1] ?? "" : "").Trim();
                if (key.StartsWith("class_", StringComparison.Ordinal)) classes.Add(key[6..]);
            }
        var result = await Insurance.Application.Services.Treaties.CreateAsync(Db, Me, values, classes);
        if (result.Id is { } id) return Redirect($"/reinsurance/treaties/{id}?saved=1");
        foreach (var (k, v) in values) Values[k] = v;
        foreach (var (k, v) in result.Errors) Errors[k] = v;
        FormError = result.Message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync() =>
        Classes = (await Db.ReadTxAsync(tx => tx.QueryAsync<ClassOption>(
            "select code as Code, name_ka as Name from core.insurance_class where parent_code is null order by sort_order"))).ToList();
}
