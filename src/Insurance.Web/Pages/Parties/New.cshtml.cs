using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Parties;

/// <summary>A new person or organization (legacy parties/new/page.tsx; the form's action saveParty).</summary>
public sealed class NewModel : AppPageModel
{
    public PartyFormView PartyFormModel { get; private set; } = null!;

    private static string KindOf(string? kind) => kind == "organization" ? "organization" : "person";

    public void OnGet() =>
        PartyFormModel = new PartyFormView(KindOf(Query("kind")), new Dictionary<string, string> { ["is_resident"] = "true", ["country_code"] = "GE" }, new Dictionary<string, string>(), null, !Me.CanWrite);

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var values = TrimmedForm();
        var outcome = await PartyForm.SaveAsync(Db, Me, values, Posted);
        if (outcome.SavedId is { } id) return Redirect($"/parties/{id}?saved=1");
        PartyFormModel = new PartyFormView(KindOf(values.GetValueOrDefault("kind")), values, outcome.Errors, outcome.Message, !Me.CanWrite);
        return Page();
    }
}
