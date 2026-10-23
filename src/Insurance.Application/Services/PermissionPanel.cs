using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Common;

namespace Insurance.Application.Services;

/// <summary>
/// What the admin's permissions panel shows (/users/permissions; legacy src/lib/permission-panel.ts): users found by name,
/// the chosen user's permissions with where each comes from, the groups of users, and the chosen group's permissions and
/// members. All read in one snapshot (<see cref="ReadAsync"/>), by parameterised SELECTs with a bounded size — no query per
/// group. The chosen user's permissions come from the same reader as every decision (PermissionStore.LoadAsync) and the
/// real evaluator: the panel does not decide anything itself. A failed read is thrown: never an empty panel, no groups, or
/// the role alone. No password hash, session or token is read.
///
/// The admin's changes as the panel's forms send them (legacy users/permissions/actions.ts and the id checks of
/// permission-store.ts) are <see cref="ChangeAsync"/> and the five operations after it.
/// </summary>
public static class PermissionPanel
{
    public const int UserLimit = 20;
    public const int GroupLimit = 200;
    private const int QueryMax = 100;

    public sealed record PanelUser(Guid Id, string Username, string FullName, string RoleCode, string RoleName, bool Active);

    public sealed record PanelGroup(Guid Id, string Name);

    /// <param name="RoleDefault">The role's starting set has it.</param>
    /// <param name="GroupNames">The names of the user's groups that give it, by the role-and-groups rule (before the user's own choice).</param>
    public sealed record PanelPermission(string Key, string Name, bool RoleDefault, IReadOnlyList<string> GroupNames, string Choice, bool Allowed, PermissionSource Source, string SourceText);

    /// <param name="MembershipGroups">
    /// The groups the user can be put in or taken out of here, each once, by name: the user's own groups, the chosen group,
    /// the groups found by name and the first 200 (a group past the first 200 is reached by searching or choosing it).
    /// </param>
    public sealed record ChosenUser(PanelUser User, IReadOnlyList<PanelPermission> Permissions, IReadOnlyList<Guid> MemberOf, IReadOnlyList<PanelGroup> MembershipGroups);

    public sealed record ChosenGroup(PanelGroup Group, IReadOnlyList<string> Granted, IReadOnlyList<PanelUser> Members);

    /// <param name="Groups">Every group (up to 200): the chosen user's memberships are shown against them.</param>
    /// <param name="FoundGroups">The groups found by name (all of Groups when nothing is searched).</param>
    /// <param name="UserMissing">A user was asked for and is not one (or the id is not an id).</param>
    public sealed record Panel(
        string Query, IReadOnlyList<PanelUser> Users, IReadOnlyList<PanelGroup> Groups, string GroupQuery, IReadOnlyList<PanelGroup> FoundGroups,
        ChosenUser? User, bool UserMissing, ChosenGroup? Group, bool GroupMissing);

    /// <summary>The words searched for: trimmed, at most 100 characters.</summary>
    public static string SearchText(string? raw)
    {
        var text = raw is null ? "" : JsText.Trim(raw);
        return text.Length > QueryMax ? text[..QueryMax] : text;
    }

    /// <summary>A UUID as the legacy code accepted it (8-4-4-4-12 hexadecimal digits, any case); anything else is null.</summary>
    public static Guid? Uuid(string? value) => value is not null && Guid.TryParseExact(value, "D", out var id) ? id : null;

    private sealed record UserRow(Guid Id, string Username, string FullName, string RoleCode, bool IsActive);

    private const string UserColumns = "id as Id, username as Username, full_name as FullName, role_code as RoleCode, is_active as IsActive";

    /// <summary>Everything the panel shows, from one snapshot (legacy: read-only, repeatable read).</summary>
    public static Task<Panel> ReadAsync(Db db, string? q, string? gq, string? user, string? group) =>
        db.SnapshotAsync(tx => LoadAsync(tx, q, gq, user, group));

    public static async Task<Panel> LoadAsync(Tx tx, string? q, string? gq, string? user, string? group)
    {
        var query = SearchText(q);
        var groupQuery = SearchText(gq);
        var roleNames = (await tx.QueryAsync<(string Code, string NameKa)>("select code, name_ka from core.role order by code"))
            .ToDictionary(r => r.Code, r => r.NameKa, StringComparer.Ordinal);
        PanelUser ToUser(UserRow r) => new(r.Id, r.Username, r.FullName, r.RoleCode, roleNames.GetValueOrDefault(r.RoleCode) ?? r.RoleCode, r.IsActive);
        var users = (query == ""
            ? await tx.QueryAsync<UserRow>($"select top (@limit) {UserColumns} from core.app_user order by username", new { limit = UserLimit })
            : await tx.QueryAsync<UserRow>($"""
                select top (@limit) {UserColumns} from core.app_user
                where charindex(lower(@query), lower(username)) > 0 or charindex(lower(@query), lower(full_name)) > 0
                order by username
                """, new { query, limit = UserLimit })).Select(ToUser).ToList();
        var groups = (await tx.QueryAsync<PanelGroup>("select top (@limit) id as Id, name as Name from core.user_group order by name", new { limit = GroupLimit })).ToList();
        var foundGroups = groupQuery == ""
            ? groups
            : (await tx.QueryAsync<PanelGroup>("select top (@limit) id as Id, name as Name from core.user_group where charindex(lower(@groupQuery), lower(name)) > 0 order by name",
                new { groupQuery, limit = GroupLimit })).ToList();

        ChosenUser? chosenUser = null;
        var userMissing = false;
        if (!string.IsNullOrEmpty(user))
        {
            var row = Uuid(user) is { } userId
                ? await tx.QuerySingleOrDefaultAsync<UserRow>($"select {UserColumns} from core.app_user where id = @userId", new { userId })
                : null;
            if (row is null) userMissing = true;
            else
            {
                var u = ToUser(row);
                var inputs = await PermissionStore.LoadAsync(tx, u.Id);
                var memberOf = inputs.Groups.Memberships.Select(m => Guid.Parse(m.GroupId)).Distinct().ToList();
                var ownGroups = memberOf.Count == 0 ? [] : (await tx.QueryAsync<PanelGroup>("select id as Id, name as Name from core.user_group where id in @memberOf order by name", new { memberOf })).ToList();
                var names = ownGroups.ToDictionary(g => g.Id.ToString(), g => g.Name, StringComparer.Ordinal);
                var subject = new PermissionSubject(u.Id.ToString(), u.RoleCode);
                var permissions = Permissions.Catalogue(PermissionTexts.Permissions, PermissionTexts.Sections).Select(entry =>
                {
                    var result = Permissions.EvaluatePermission(subject, entry.Key, inputs.Overrides, inputs.Groups);
                    var inherited = Permissions.EvaluatePermission(subject, entry.Key, [], inputs.Groups);
                    var own = inputs.Overrides.Where(o => o.Permission == entry.Key).ToList();
                    var choice = own.Count == 1 && (own[0].Decision == "allow" || own[0].Decision == "deny") ? own[0].Decision : "inherit";
                    var source = result.Source.ToKebabCode();
                    return new PanelPermission(entry.Key, entry.Name, entry.Roles.Contains(u.RoleCode, StringComparer.Ordinal),
                        (inherited.Groups ?? []).Select(id => names.GetValueOrDefault(id) ?? id).ToList(), choice, result.Allowed, result.Source,
                        PermissionTexts.Sources.GetValueOrDefault(source) ?? source);
                }).ToList();
                chosenUser = new ChosenUser(u, permissions, memberOf, ownGroups);
            }
        }

        ChosenGroup? chosenGroup = null;
        var groupMissing = false;
        if (!string.IsNullOrEmpty(group))
        {
            var row = Uuid(group) is { } groupId
                ? await tx.QuerySingleOrDefaultAsync<PanelGroup>("select id as Id, name as Name from core.user_group where id = @groupId", new { groupId })
                : null;
            if (row is null) groupMissing = true;
            else
            {
                var granted = (await tx.QueryAsync<string>("select permission from core.user_group_permission where group_id = @id order by permission", new { id = row.Id })).ToList();
                var ids = (await tx.QueryAsync<Guid>("select user_id from core.user_group_member where group_id = @id", new { id = row.Id })).ToList();
                var members = ids.Count == 0 ? [] : (await tx.QueryAsync<UserRow>($"select {UserColumns} from core.app_user where id in @ids order by username", new { ids })).Select(ToUser).ToList();
                chosenGroup = new ChosenGroup(row, granted, members);
            }
        }

        if (chosenUser is not null)
        {
            var byId = new Dictionary<Guid, PanelGroup>();
            foreach (var g in chosenUser.MembershipGroups.Concat(chosenGroup is null ? [] : [chosenGroup.Group]).Concat(foundGroups).Concat(groups))
                byId.TryAdd(g.Id, g);
            // JavaScript's < on the names, then on the ids as text.
            chosenUser = chosenUser with
            {
                MembershipGroups = byId.Values.Order(Comparer<PanelGroup>.Create((a, b) =>
                    string.CompareOrdinal(a.Name, b.Name) is var c && c != 0 ? c : string.CompareOrdinal(a.Id.ToString(), b.Id.ToString()))).ToList(),
            };
        }
        return new Panel(query, users, groups, groupQuery, foundGroups, chosenUser, userMissing, chosenGroup, groupMissing);
    }

    /// <summary>What a change answers in the panel: the Georgian text and whether it is a refusal.</summary>
    public sealed record Answer(bool Ok, string Text, bool Changed);

    /// <summary>
    /// One change in the admin's transaction (Db.AsUserAsync), so the audit records who changed what. Who may change is
    /// checked before (the page: admin only).
    /// </summary>
    public static async Task<Answer> ChangeAsync(Db db, Guid adminId, Func<Tx, Task<PermissionStore.ChangeResult>> run)
    {
        var result = await db.AsUserAsync(adminId, run);
        if (!result.Ok) return new Answer(false, PermissionTexts.Changes.Refusals[result.Reason!.Value.ToKebabCode()], false);
        return new Answer(true, result.Changed ? PermissionTexts.Changes.Saved : PermissionTexts.Changes.Unchanged, result.Changed);
    }

    private static readonly PermissionStore.ChangeResult InvalidId = PermissionStore.ChangeResult.Refused(PermissionStore.Refusal.InvalidId);

    /// <summary>'true' or 'false' from a form; anything else is null.</summary>
    public static bool? Flag(string? value) => value == "true" ? true : value == "false" ? false : null;

    public static Task<PermissionStore.ChangeResult> CreateGroupAsync(Tx tx, string? name) => PermissionStore.CreateGroupAsync(tx, name);

    public static Task<PermissionStore.ChangeResult> RenameGroupAsync(Tx tx, string? groupId, string? name) =>
        Uuid(groupId) is { } id ? PermissionStore.RenameGroupAsync(tx, id, name) : Task.FromResult(InvalidId);

    public static Task<PermissionStore.ChangeResult> SetGroupMemberAsync(Tx tx, string? groupId, string? userId, string? member)
    {
        if (Flag(member) is not { } m) return Task.FromResult(PermissionStore.ChangeResult.Refused(PermissionStore.Refusal.InvalidDecision));
        if (Uuid(groupId) is not { } g || Uuid(userId) is not { } u) return Task.FromResult(InvalidId);
        return PermissionStore.SetGroupMemberAsync(tx, g, u, m);
    }

    public static Task<PermissionStore.ChangeResult> SetGroupGrantAsync(Tx tx, string? groupId, string? permission, string? granted)
    {
        if (Flag(granted) is not { } on) return Task.FromResult(PermissionStore.ChangeResult.Refused(PermissionStore.Refusal.InvalidDecision));
        if (Uuid(groupId) is not { } g) return Task.FromResult(InvalidId);
        return PermissionStore.SetGroupGrantAsync(tx, g, permission, on);
    }

    public static Task<PermissionStore.ChangeResult> SetUserDecisionAsync(Tx tx, string? userId, string? permission, string? decision) =>
        Uuid(userId) is { } id ? PermissionStore.SetUserDecisionAsync(tx, id, permission, decision) : Task.FromResult(InvalidId);
}
