using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Intermediaries;

/// <summary>A new intermediary (legacy intermediaries/new/page.tsx; the form's action saveIntermediary).</summary>
public sealed class NewModel : AppPageModel
{
    public IntermediaryFormView FormModel { get; private set; } = null!;

    public void OnGet() => FormModel = new IntermediaryFormView(new Dictionary<string, string> { ["status"] = "active" }, new Dictionary<string, string>(), null, !Me.CanWrite);

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var values = TrimmedForm();
        var outcome = await IntermediaryForm.SaveAsync(Db, Me, values);
        if (outcome.SavedId is { } id) return Redirect($"/intermediaries/{id}?saved=1");
        FormModel = new IntermediaryFormView(values, outcome.Errors, outcome.Message, !Me.CanWrite);
        return Page();
    }
}
