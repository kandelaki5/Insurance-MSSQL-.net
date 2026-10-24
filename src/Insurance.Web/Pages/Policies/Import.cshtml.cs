using System.Text.Json;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>
/// Policies uploaded as a list (legacy policies/import/page.tsx, import-form.tsx; actions previewImport, confirmImport).
/// The preview's result is kept in the confirmation form, as the legacy page kept it in the browser, so a refused
/// confirmation shows the preview again with its message.
/// </summary>
public sealed class ImportModel(Rates rates) : AppPageModel
{
    public PolicyImport.ImportState? Preview { get; private set; }

    /// <summary>The preview as kept in the confirmation form (display only: the confirmation checks the payload again).</summary>
    public string PreviewState => Preview is null ? "" : JsonSerializer.Serialize(Preview);

    public int Valid => Preview?.Rows?.Count(r => r.Errors.Count == 0) ?? 0;

    public IActionResult OnGet()
    {
        if (!Me.CanWrite) return Redirect("/policies");
        var today = Dates.ToIso(Db.Today());
        Values["effective_from"] = today;
        // The same day next year as text, as the legacy page built it (a 29 February gives a date the form refuses).
        Values["effective_to"] = $"{int.Parse(today[..4], System.Globalization.CultureInfo.InvariantCulture) + 1}{today[4..]}";
        Values["currency"] = "GEL";
        Values["installments"] = "1";
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        var values = TrimmedForm();
        var file = Request.Form.Files.GetFile("file");
        byte[]? bytes = null;
        if (file is not null)
        {
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory);
            bytes = memory.ToArray();
        }
        var state = await PolicyImport.PreviewAsync(Db, Me, values, bytes);
        ShowValues(state.Values);
        FormError = state.Message;
        Preview = state.Rows is null ? null : state;
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        PolicyImport.ImportState? previous = null;
        try
        {
            if (Form("preview_state") is { Length: > 0 } kept) previous = JsonSerializer.Deserialize<PolicyImport.ImportState>(kept);
        }
        catch (JsonException)
        {
            previous = null;
        }
        previous ??= new PolicyImport.ImportState(new Dictionary<string, string>());
        var state = await PolicyImport.ConfirmAsync(Db, rates, Me, Form("payload"), previous);
        if (state.Redirect is { } to) return Redirect(to);
        ShowValues(state.Values);
        FormError = state.Message;
        Preview = state.Rows is null ? null : state with { Message = null };
        return Page();
    }
}
