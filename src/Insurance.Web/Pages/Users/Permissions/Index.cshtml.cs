using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Users.Permissions;

/// <summary>
/// The admin's permissions panel (owner, 15.10.2026; legacy users/permissions/page.tsx, permission-forms.tsx, actions.ts):
/// find a user, see each permission — the role's starting set, the groups that give it, the user's own choice, the
/// result and where it comes from — and change the own choice; the groups of users: create, rename, tick their
/// permissions, their members. Only the admin, checked before anything is read or changed. What is shown comes from one
/// snapshot (PermissionPanel.ReadAsync) and the real evaluator.
/// </summary>
public sealed class IndexModel : AppPageModel
{
    public PermissionPanel.Panel Panel { get; private set; } = null!;

    /// <summary>The address parameters kept by the panel's links and searches.</summary>
    public (string? Q, string? Gq, string? User, string? Group) Keep { get; private set; }

    /// <summary>Which form answered (its hidden answer_for field) and its answer, shown next to that form only.</summary>
    public string? AnswerFor { get; private set; }
    public PermissionPanel.Answer? Answer { get; private set; }

    public string Href(string? q, string? gq, string? user, string? group)
    {
        var kept = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value) { if (!string.IsNullOrEmpty(value)) kept.Add(new(key, value)); }
        Add("q", q);
        Add("gq", gq);
        Add("user", user);
        Add("group", group);
        return kept.Count == 0 ? "/users/permissions" : "/users/permissions?" + string.Join("&", kept.Select(p => $"{FormEncode(p.Key)}={FormEncode(p.Value)}"));
    }

    /// <summary>A value as URLSearchParams writes it (application/x-www-form-urlencoded): space as +, the rest of UTF-8 as %XX.</summary>
    public static string FormEncode(string value)
    {
        var b = new System.Text.StringBuilder();
        foreach (var octet in System.Text.Encoding.UTF8.GetBytes(value))
        {
            var c = (char)octet;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '*' or '-' or '.' or '_') b.Append(c);
            else if (c == ' ') b.Append('+');
            else b.Append('%').Append(octet.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }

    /// <summary>
    /// Where a change form posts: this page's own address with its parameters (a server action posted to the page as it
    /// was open, so the panel answered with the same user and group chosen).
    /// </summary>
    public string PostUrl(string handler)
    {
        var rest = string.Join("&", Request.Query.Where(p => p.Key != "handler").SelectMany(p => p.Value.Select(v => $"{FormEncode(p.Key)}={FormEncode(v ?? "")}")));
        return $"/users/permissions?handler={handler}" + (rest == "" ? "" : "&" + rest);
    }

    private async Task LoadAsync()
    {
        Panel = await PermissionPanel.ReadAsync(Db, Query("q"), Query("gq"), Query("user"), Query("group"));
        Keep = (Panel.Query == "" ? null : Panel.Query, Panel.GroupQuery == "" ? null : Panel.GroupQuery, Query("user"), Query("group"));
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ChangeAsync(Func<Tx, Task<PermissionStore.ChangeResult>> run)
    {
        if (!Me.IsAdmin) return Redirect("/");
        Answer = await PermissionPanel.ChangeAsync(Db, Me.Id, run);
        AnswerFor = Form("answer_for");
        // The panel shows the change at once: it is read again.
        await LoadAsync();
        return Page();
    }

    public Task<IActionResult> OnPostCreateGroupAsync() => ChangeAsync(tx => PermissionPanel.CreateGroupAsync(tx, Form("name")));

    public Task<IActionResult> OnPostRenameGroupAsync() => ChangeAsync(tx => PermissionPanel.RenameGroupAsync(tx, Form("group_id"), Form("name")));

    public Task<IActionResult> OnPostSetGroupMemberAsync() => ChangeAsync(tx => PermissionPanel.SetGroupMemberAsync(tx, Form("group_id"), Form("user_id"), Form("member")));

    public Task<IActionResult> OnPostSetGroupGrantAsync() => ChangeAsync(tx => PermissionPanel.SetGroupGrantAsync(tx, Form("group_id"), Form("permission"), Form("granted")));

    public Task<IActionResult> OnPostSetUserDecisionAsync() => ChangeAsync(tx => PermissionPanel.SetUserDecisionAsync(tx, Form("user_id"), Form("permission"), Form("decision")));
}
