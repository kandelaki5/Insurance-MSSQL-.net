using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Insurance.Web.Infrastructure;

/// <summary>
/// Base of every signed-in page. A form posts back to its page; a refusal shows the page again with the field errors
/// and the values as typed (legacy server actions returning { errors, values }).
/// </summary>
public abstract class AppPageModel : PageModel
{
    protected CurrentUser CurrentUser => HttpContext.RequestServices.GetRequiredService<RequestUser>().Current;
    protected Db Db => HttpContext.RequestServices.GetRequiredService<Db>();

    public CurrentUser Me => CurrentUser;

    /// <summary>Field → message for the form being shown again.</summary>
    public Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);

    /// <summary>Field → value as typed, for the form being shown again.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>A message for the whole form (above the fields).</summary>
    public string? FormError { get; set; }

    /// <summary>A message after a successful action.</summary>
    public string? Notice { get; set; }

    /// <summary>A posted field as one string: missing or repeated is null (legacy singleParams / String(form.get())).</summary>
    protected string? Form(string name)
    {
        if (!Request.HasFormContentType || !Request.Form.TryGetValue(name, out var v) || v.Count != 1) return null;
        return v[0];
    }

    /// <summary>A query parameter as one string: missing or repeated is null (legacy singleParams).</summary>
    protected string? Query(string name) => Request.Query.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;

    /// <summary>Keeps every posted field so the form shows what was typed.</summary>
    protected void KeepTrimmedForm()
    {
        if (!Request.HasFormContentType) return;
        foreach (var (key, value) in Request.Form)
            if (value.Count == 1 && key != "__RequestVerificationToken") Values[key] = value[0] ?? "";
    }

    /// <summary>
    /// Every posted text field, trimmed as JavaScript trims; a repeated field keeps its last value (legacy
    /// Object.fromEntries([...form.entries()].map(([k, v]) => [k, String(v).trim()])), the values a form action received
    /// and returned to show the form again).
    /// </summary>
    protected Dictionary<string, string> TrimmedForm()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Request.HasFormContentType) return values;
        foreach (var (key, value) in Request.Form)
            if (key != "__RequestVerificationToken" && value.Count > 0) values[key] = Insurance.Domain.Common.JsText.Trim(value[^1] ?? "");
        return values;
    }

    /// <summary>Whether the form posted a field at all (legacy form.has: a ticked checkbox).</summary>
    protected bool Posted(string name) => Request.HasFormContentType && Request.Form.ContainsKey(name);

    /// <summary>Shows the form again with these values (what the legacy action returned as `values`).</summary>
    protected void ShowValues(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values) Values[key] = value;
    }

    public string Value(string name, string? fallback = null) => Values.TryGetValue(name, out var v) ? v : fallback ?? "";
    public string? Error(string name) => Errors.GetValueOrDefault(name);
}
