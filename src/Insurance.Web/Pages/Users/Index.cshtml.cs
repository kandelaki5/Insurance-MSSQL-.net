using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Users;

/// <summary>Users of the system, admin only (legacy users/page.tsx, user-forms.tsx; actions createUser, setUserActive, resetPassword).</summary>
public sealed partial class IndexModel(Auth auth) : AppPageModel
{
    public sealed record UserRow(Guid Id, string Username, string FullName, string RoleName, string? BranchName, bool IsActive);

    public sealed record Option(string Value, string Label);

    public const int MinPasswordLength = 10;

    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public IReadOnlyList<Option> Roles { get; private set; } = [];
    public IReadOnlyList<Option> Branches { get; private set; } = [];

    /// <summary>The answer of the password form of one user (legacy: each row's form has its own).</summary>
    public Guid? ResetFor { get; private set; }
    public string? ResetError { get; private set; }
    public bool ResetOk { get; private set; }

    private async Task LoadAsync() =>
        (Users, Roles, Branches) = await Db.ReadTxAsync(async tx => (
            (IReadOnlyList<UserRow>)(await tx.QueryAsync<UserRow>("""
                select u.id as Id, u.username as Username, u.full_name as FullName, r.name_ka as RoleName, b.name_ka as BranchName, u.is_active as IsActive
                from core.app_user u
                join core.role r on r.code = u.role_code
                left join core.branch b on b.id = u.branch_id
                order by u.username
                """)).ToList(),
            (IReadOnlyList<Option>)(await tx.QueryAsync<Option>("select code as Value, name_ka as Label from core.role order by name_ka, code")).ToList(),
            (IReadOnlyList<Option>)(await tx.QueryAsync<Option>("select lower(convert(nchar(36), id)) as Value, name_ka as Label from core.branch order by name_ka, id")).ToList()));

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        await LoadAsync();
        return Page();
    }

    [GeneratedRegex(@"^[a-z0-9.-]{3,}\z")]
    private static partial Regex Username();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        var password = Form("password") ?? "";
        var values = PostedValues();
        values.Remove("password");
        var username = JsText.Trim(values.GetValueOrDefault("username") ?? "").ToLowerInvariant();
        var e = Ka.User.Errors.ByKey;
        if (!Username().IsMatch(username)) Errors["username"] = e["username"];
        if (string.IsNullOrEmpty(values.GetValueOrDefault("full_name"))) Errors["full_name"] = e["fullName"];
        if (password.Length < MinPasswordLength) Errors["password"] = e["password"];
        if (Errors.Count == 0)
        {
            try
            {
                var branch = values.GetValueOrDefault("branch_id") is { Length: > 0 } b ? Guid.Parse(b) : (Guid?)null;
                // As before, written without an actor: the audit shows the system as the author of a new user.
                await Db.InTransactionAsync(tx => tx.ExecuteAsync("""
                    insert into core.app_user (id, username, full_name, password_hash, role_code, branch_id)
                    values (@id, @username, @fullName, @hash, @role, @branch)
                    """, new { id = Guid.NewGuid(), username, fullName = values["full_name"], hash = Password.HashPassword(password), role = values.GetValueOrDefault("role_code"), branch }));
                Notice = Ka.User.Created;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex) when (DbError.From(ex)?.Code == DbError.UniqueViolation)
            {
                Errors["username"] = e["duplicate"];
            }
        }
        foreach (var (k, v) in values) Values[k] = v;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetActiveAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        var id = Form("id");
        var active = Form("active") == "true";
        if (PermissionPanel.Uuid(id) is { } userId && !(userId == Me.Id && !active))
        {
            await Db.InTransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("update core.app_user set is_active = @active where id = @userId", new { active, userId });
                if (!active) await tx.ExecuteAsync("delete from core.session where user_id = @userId", new { userId });
                return 0;
            });
        }
        else if (PermissionPanel.Uuid(id) is null)
        {
            // A malformed id was refused by PostgreSQL's uuid type (an error page).
            throw new ArgumentException("not a user id");
        }
        return Redirect("/users");
    }

    public async Task<IActionResult> OnPostResetPasswordAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        var id = PermissionPanel.Uuid(Form("id")) ?? throw new ArgumentException("not a user id");
        var password = Form("password") ?? "";
        ResetFor = id;
        if (password.Length < MinPasswordLength) ResetError = Ka.User.Errors.Password;
        else
        {
            var hash = Password.HashPassword(password);
            await Db.InTransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("update core.app_user set password_hash = @hash where id = @id", new { hash, id });
                // A new password invalidates every existing session of that user, including old ones of the admin.
                await tx.ExecuteAsync("delete from core.session where user_id = @id", new { id });
                return 0;
            });
            if (id == Me.Id) RequestUser.SetCookie(HttpContext, await auth.CreateSessionAsync(Me.Id));
            ResetOk = true;
        }
        await LoadAsync();
        return Page();
    }
}
