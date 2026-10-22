using Insurance.Application.Data;
using Insurance.Domain.Common;

namespace Insurance.Application.Services;

/// <summary>
/// Where permissions are kept (database/migrations/0001_foundation.sql, legacy 0032) — reading them for the evaluator
/// and the admin's changes to them (legacy src/lib/permission-store.ts).
///
/// - Reading: the user's own decisions, memberships, and the grants of exactly those groups, in one snapshot. A failed
///   read is thrown as it is: never "no decisions", "no groups" or the role alone.
/// - Changing: one permission or one membership at a time, by its key, in the admin's transaction (Db.AsUserAsync), so
///   the audit records who changed what; a change to what is already there writes nothing.
/// - Refused, with nothing written: a permission the catalogue does not have, a decision other than allow / deny /
///   inherit, a user or group that does not exist, an empty or taken group name.
/// Who may change is not decided here: the pages check the admin role first.
/// </summary>
public static class PermissionStore
{
    public sealed record Inputs(IReadOnlyList<PermissionOverride> Overrides, PermissionGroups Groups);

    // uuids are compared and ordered as PostgreSQL text (lower case), so lists come out in the legacy order.
    private const string Overrides = "select lower(convert(nchar(36), user_id)) as UserId, permission as Permission, decision as Decision from core.user_permission where user_id = @userId order by permission";
    private const string Memberships = "select lower(convert(nchar(36), user_id)) as UserId, lower(convert(nchar(36), group_id)) as GroupId from core.user_group_member where user_id = @userId order by lower(convert(nchar(36), group_id)) collate Latin1_General_BIN2";
    private const string Grants = "select lower(convert(nchar(36), group_id)) as GroupId, permission as Permission from core.user_group_permission where group_id in @ids order by lower(convert(nchar(36), group_id)) collate Latin1_General_BIN2, permission";

    /// <summary>The user's inputs from one snapshot: a snapshot-isolation transaction, so the three reads see the same committed state (legacy repeatable read, read only).</summary>
    public static Task<Inputs> ReadAsync(Db db, Guid userId) =>
        db.SnapshotAsync(tx => LoadAsync(tx, userId));

    public static async Task<Inputs> LoadAsync(Tx tx, Guid userId)
    {
        var overrides = (await tx.QueryAsync<PermissionOverride>(Overrides, new { userId })).ToList();
        var memberships = (await tx.QueryAsync<GroupMembership>(Memberships, new { userId })).ToList();
        var ids = memberships.Select(m => Guid.Parse(m.GroupId)).Distinct().ToList();
        var grants = ids.Count == 0 ? [] : (await tx.QueryAsync<GroupGrant>(Grants, new { ids })).ToList();
        foreach (var o in overrides)
            if (string.IsNullOrEmpty(o.Permission) || string.IsNullOrEmpty(o.Decision)) throw new InvalidOperationException("permission read: a decision is not a value the schema allows");
        return new Inputs(overrides, new PermissionGroups(memberships, grants));
    }

    /// <summary>Whether the user may use one permission, read now through <paramref name="tx"/> (the writing transaction, before anything is written).</summary>
    public static async Task<bool> HasPermissionAsync(Tx tx, CurrentUser user, string permission)
    {
        var inputs = await LoadAsync(tx, user.Id);
        return Permissions.EvaluatePermission(user.Subject, permission, inputs.Overrides, inputs.Groups).Allowed;
    }

    public static async Task<bool> HasPermissionAsync(Db db, CurrentUser user, string permission)
    {
        var inputs = await ReadAsync(db, user.Id);
        return Permissions.EvaluatePermission(user.Subject, permission, inputs.Overrides, inputs.Groups).Allowed;
    }

    /// <summary>Every permission of the catalogue for the user, from what is stored.</summary>
    public static async Task<IReadOnlyList<PermissionResult>> LoadUserPermissionsAsync(Db db, CurrentUser user)
    {
        var inputs = await ReadAsync(db, user.Id);
        return Permissions.EvaluateAll(user.Subject, inputs.Overrides, inputs.Groups);
    }

    public enum Refusal { InvalidId, UnknownPermission, InvalidDecision, MissingUser, MissingGroup, InvalidName, DuplicateName }

    /// <summary>What a change did: refused (nothing written), or done — <see cref="Changed"/> false when it was already so.</summary>
    public sealed record ChangeResult(bool Ok, Refusal? Reason = null, bool Changed = false, Guid? Id = null)
    {
        public static ChangeResult Refused(Refusal r) => new(false, r);
        public static ChangeResult Done(bool changed, Guid? id = null) => new(true, null, changed, id);
    }

    public const int MaxName = 100;

    /// <summary>A group's name as stored: trimmed, not empty, at most 100 characters; anything else is null.</summary>
    public static string? GroupName(string? raw)
    {
        var name = (raw ?? "").Trim();
        return name != "" && name.Length <= MaxName ? name : null;
    }

    private static bool KnownPermission(string? key) => key is not null && Permissions.All.ContainsKey(key);

    private static async Task<bool> NameTakenAsync(Tx tx, string name, Guid? self = null) =>
        (await tx.QueryAsync<Guid>("select id from core.user_group where lower(name) = lower(@name)", new { name })).Any(id => id != self);

    /// <summary>A new group of users. A name another group has (in any case) is refused.</summary>
    public static async Task<ChangeResult> CreateGroupAsync(Tx tx, string? rawName)
    {
        var name = GroupName(rawName);
        if (name is null) return ChangeResult.Refused(Refusal.InvalidName);
        if (await NameTakenAsync(tx, name)) return ChangeResult.Refused(Refusal.DuplicateName);
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("insert into core.user_group (id, name) values (@id, @name)", new { id, name });
        return ChangeResult.Done(true, id);
    }

    /// <summary>A group's new name; the same name writes nothing.</summary>
    public static async Task<ChangeResult> RenameGroupAsync(Tx tx, Guid groupId, string? rawName)
    {
        var name = GroupName(rawName);
        if (name is null) return ChangeResult.Refused(Refusal.InvalidName);
        var current = await tx.QuerySingleOrDefaultAsync<string>("select name from core.user_group with (updlock, rowlock) where id = @groupId", new { groupId });
        if (current is null) return ChangeResult.Refused(Refusal.MissingGroup);
        if (string.Equals(current, name, StringComparison.Ordinal)) return ChangeResult.Done(false);
        if (await NameTakenAsync(tx, name, groupId)) return ChangeResult.Refused(Refusal.DuplicateName);
        await tx.ExecuteAsync("update core.user_group set name = @name where id = @groupId", new { name, groupId });
        return ChangeResult.Done(true);
    }

    private static async Task<bool> GroupExistsAsync(Tx tx, Guid groupId) =>
        await tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.user_group with (updlock, rowlock) where id = @groupId", new { groupId }) is not null;

    private static async Task<bool> UserExistsAsync(Tx tx, Guid userId) =>
        await tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.app_user where id = @userId", new { userId }) is not null;

    /// <summary>A user put in a group (<paramref name="member"/> true) or taken out of it; already so writes nothing.</summary>
    public static async Task<ChangeResult> SetGroupMemberAsync(Tx tx, Guid groupId, Guid userId, bool member)
    {
        if (!await GroupExistsAsync(tx, groupId)) return ChangeResult.Refused(Refusal.MissingGroup);
        if (!await UserExistsAsync(tx, userId)) return ChangeResult.Refused(Refusal.MissingUser);
        var row = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.user_group_member with (updlock, rowlock, holdlock) where group_id = @groupId and user_id = @userId", new { groupId, userId });
        if (member == row.HasValue) return ChangeResult.Done(false);
        if (member) await tx.ExecuteAsync("insert into core.user_group_member (group_id, user_id) values (@groupId, @userId)", new { groupId, userId });
        else await tx.ExecuteAsync("delete from core.user_group_member where id = @id", new { id = row });
        return ChangeResult.Done(true);
    }

    /// <summary>One permission ticked for a group (<paramref name="granted"/> true) or unticked; already so writes nothing.</summary>
    public static async Task<ChangeResult> SetGroupGrantAsync(Tx tx, Guid groupId, string? permission, bool granted)
    {
        if (!KnownPermission(permission)) return ChangeResult.Refused(Refusal.UnknownPermission);
        if (!await GroupExistsAsync(tx, groupId)) return ChangeResult.Refused(Refusal.MissingGroup);
        var row = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.user_group_permission with (updlock, rowlock, holdlock) where group_id = @groupId and permission = @permission", new { groupId, permission });
        if (granted == row.HasValue) return ChangeResult.Done(false);
        if (granted) await tx.ExecuteAsync("insert into core.user_group_permission (group_id, permission) values (@groupId, @permission)", new { groupId, permission });
        else await tx.ExecuteAsync("delete from core.user_group_permission where id = @id", new { id = row });
        return ChangeResult.Done(true);
    }

    public static readonly IReadOnlyList<string> Choices = ["allow", "deny", "inherit"];

    /// <summary>A user's own decision on one permission: allow, deny, or inherit (no decision — the role and the groups decide).</summary>
    public static async Task<ChangeResult> SetUserDecisionAsync(Tx tx, Guid userId, string? permission, string? choice)
    {
        if (!KnownPermission(permission)) return ChangeResult.Refused(Refusal.UnknownPermission);
        if (choice is null || !Choices.Contains(choice)) return ChangeResult.Refused(Refusal.InvalidDecision);
        if (!await UserExistsAsync(tx, userId)) return ChangeResult.Refused(Refusal.MissingUser);
        var row = await tx.QuerySingleOrDefaultAsync<(Guid Id, string Decision)?>("select id, decision from core.user_permission with (updlock, rowlock, holdlock) where user_id = @userId and permission = @permission", new { userId, permission });
        if (choice == "inherit")
        {
            if (row is null) return ChangeResult.Done(false);
            await tx.ExecuteAsync("delete from core.user_permission where id = @id", new { id = row.Value.Id });
            return ChangeResult.Done(true);
        }
        if (row is null)
        {
            await tx.ExecuteAsync("insert into core.user_permission (user_id, permission, decision) values (@userId, @permission, @choice)", new { userId, permission, choice });
            return ChangeResult.Done(true);
        }
        if (row.Value.Decision == choice) return ChangeResult.Done(false);
        await tx.ExecuteAsync("update core.user_permission set decision = @choice where id = @id", new { choice, id = row.Value.Id });
        return ChangeResult.Done(true);
    }
}
