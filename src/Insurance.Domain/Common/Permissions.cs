// Permissions: what a user may do, worked out from the user's role, the user's groups and the user's own overrides
// (owner, 15.10.2026: an admin panel where a user is found and permissions are ticked — each role's starting set, a
// per-user grant or removal on top, and where each permission comes from shown; then: permissions given to groups of
// users as their default settings, with a particular user's permissions still extendable; the admin has every permission,
// its own included). Pure: no database, no session, no network. Nothing calls it yet; the premium receipt and its form
// still use canWrite until the panel, its storage and its audit are in place.
//
// How they combine (the technical rule of this module): the role's starting set and every group the user is a member of
// add to each other — a group can only add, never take away; then the user's own override, if any, decides — allow adds a
// permission neither gives, deny removes one they give; no override (inherit) leaves the role and the groups.
//
// The caller vouches for its input: the subject comes from the server's own session (currentUser), the overrides, the
// memberships and the groups' grants from the server's own store, read in full — a failed read must stop the caller,
// never come here as "none". Nothing sent by the browser (a form field, a hidden role or group, a ticked box) is evidence
// of any of them.
// Ported from src/lib/permissions.ts. The Georgian names of the catalogue are interface texts
// (Insurance.Application.Texts.PermissionTexts); Catalogue() takes them as arguments, since the domain does not see them.
using System.Collections.ObjectModel;

namespace Insurance.Domain.Common;

/// <param name="Section">The section the panel lists it under (not a group of users).</param>
/// <param name="Roles">The roles that have the permission unless a user's own override says otherwise.</param>
public sealed record CatalogueEntry(string Section, IReadOnlyList<string> Roles);

/// <summary>A user's own decision on one permission; no override means the role decides (inherit).</summary>
public sealed record PermissionOverride(string UserId, string Permission, string Decision);

public sealed record PermissionSubject(string UserId, string RoleCode);

/// <summary>A user's membership of a group of users.</summary>
public sealed record GroupMembership(string UserId, string GroupId);

/// <summary>A permission ticked for a group of users: its members have it by default.</summary>
public sealed record GroupGrant(string GroupId, string Permission);

/// <summary>The groups of users, as read from the server's store: who is in which group and what each group is given.</summary>
public sealed record PermissionGroups(IReadOnlyList<GroupMembership> Memberships, IReadOnlyList<GroupGrant> Grants)
{
    public static readonly PermissionGroups None = new([], []);
}

/// <summary>Codes 'role', 'group', 'not-in-role', 'granted', 'revoked', 'invalid' (EnumCode.ToKebabCode).</summary>
public enum PermissionSource { Role, Group, NotInRole, Granted, Revoked, Invalid }

/// <summary>Codes 'unknown-permission', 'unknown-role', 'no-user', 'invalid-decision', 'duplicate-override' (EnumCode.ToKebabCode).</summary>
public enum InvalidReason { UnknownPermission, UnknownRole, NoUser, InvalidDecision, DuplicateOverride }

/// <param name="Reason">Only for source Invalid: what was wrong.</param>
/// <param name="Groups">Only when no override decides: the ids of the user's groups that give the permission, sorted, each once.</param>
public sealed record PermissionResult(string Permission, bool Allowed, PermissionSource Source, InvalidReason? Reason = null, IReadOnlyList<string>? Groups = null);

/// <summary>A permission as the panel shows it: key, Georgian name and section, the roles that have it by default.</summary>
public sealed record CatalogueItem(string Key, string Name, string Section, IReadOnlyList<string> Roles);

public static class Permissions
{
    /// <summary>The roles that exist (db/migrations/0001_foundation.sql 92–98, 0008_approval_and_periods.sql 6).</summary>
    public static readonly IReadOnlyList<string> RoleCodes = ["admin", "underwriter", "sales", "claims", "accountant", "viewer", "operations_head"];

    /// <summary>
    /// Every permission there is, in the catalogue's order; every entry lists the admin, who has every operation for the pilot (docs/05 ვ7).
    /// premium.receipt.create — recording a new premium receipt: the accountant (docs/05 ვ3).
    /// claim.recovery.receive — recording money recovered after a claim (subrogation, salvage): the accountant (docs/05 ვ2).
    /// claim.payment.record — recording a claim's payment (payClaim): the accountant (docs/05 ვ2). Who authorises large
    ///   payments (docs/05 ვ8, the finance director's 10 000) is still open and is not this permission.
    /// policy.approve — putting a pending policy in force (approvePolicy): the head of operations (confirmed 03.10.2026, the
    ///   role rule approval had until now). No one approves a policy they created themselves (docs/05 ვ4) — a separate
    ///   rule that no permission lifts; the admin may approve its own (owner, 16.10.2026; docs/05 ვ8).
    /// accounting.period.close — closing an accounting month (closePeriod): the accountant (the role rule closing had until
    ///   now). Which month may be closed is not this permission: only the oldest open month that has ended, never reopened.
    /// claim.payment.reverse — reversing a recorded claim payment (reverseClaimPayment): the accountant (owner, 17.10.2026;
    ///   docs/05 ვ8), with no approval after it. The reversal is a new row; the payment itself is never changed.
    /// claim.reserve.approve — approving a requested change of a claim's reserve (approveReserveRequest): the claims head
    ///   (owner, 17.10.2026; docs/05 ვ8), given as a permission to a group or a person — there is no claims-head role (owner:
    ///   "უფლება + ჯგუფი"). By role only the admin has it (ვ7). No one approves a request they made, the admin included.
    /// audit.log.view — seeing the shared change log (audit/page.tsx): the admin and the audit (owner, 17.10.2026; docs/05 ვ8:
    ///   "ადმინი და აუდიტი"); the audit department is given it as a group — there is no audit role. The party card's own
    ///   history is not this permission (not changed here).
    /// payment.large.authorize — the financial director's authorisation of a payment above the company's threshold
    ///   (payment.fd_threshold_gel, 10 000 GEL; owner, 17.10.2026, docs/05 ვ8), first for claim payments
    ///   (approveClaimPaymentRequest): given to a group or a person — there is no financial-director role ("უფლება + ჯგუფი");
    ///   by role only the admin has it. Whoever has it may approve a payment they asked for themselves (owner, 17.10.2026).
    ///   It is not a right to record payments: asking still needs claim.payment.record.
    /// finance.panel.view — seeing the financial director's panel (finance/panel/page.tsx): the requests above the threshold not
    ///   yet carried out (owner, 17.10.2026, docs/05 ვ8). For the test configuration the admin by role; the financial director
    ///   is given it by a group or personally — the company's own mapping comes after its meeting. Seeing is not authorising:
    ///   it does not give payment.large.authorize, nor does that give it.
    /// reinsurance.commission.settle — recording the settlement of a reinsurer's commission (offset or receipt, per treaty) and
    ///   its reversal (accounting/ri-commission): the accountant (owner, 20.10.2026, docs/05 3ე; Insurance-Software PR #362).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CatalogueEntry> All = Ordered(
        ("premium.receipt.create", new CatalogueEntry("premiums", ["accountant", "admin"])),
        ("claim.recovery.receive", new CatalogueEntry("claims", ["accountant", "admin"])),
        ("claim.payment.record", new CatalogueEntry("claims", ["accountant", "admin"])),
        ("policy.approve", new CatalogueEntry("policies", ["operations_head", "admin"])),
        ("accounting.period.close", new CatalogueEntry("periods", ["accountant", "admin"])),
        ("claim.payment.reverse", new CatalogueEntry("claims", ["accountant", "admin"])),
        ("claim.reserve.approve", new CatalogueEntry("claims", ["admin"])),
        ("audit.log.view", new CatalogueEntry("audit", ["admin"])),
        ("payment.large.authorize", new CatalogueEntry("finance", ["admin"])),
        ("finance.panel.view", new CatalogueEntry("finance", ["admin"])),
        ("reinsurance.commission.settle", new CatalogueEntry("reinsurance", ["accountant", "admin"])));

    private static IReadOnlyDictionary<string, CatalogueEntry> Ordered(params (string Key, CatalogueEntry Entry)[] entries)
    {
        var map = new OrderedDictionary<string, CatalogueEntry>(StringComparer.Ordinal);
        foreach (var (key, entry) in entries) map.Add(key, entry);
        return new ReadOnlyDictionary<string, CatalogueEntry>(map);
    }

    private static PermissionResult Invalid(string permission, InvalidReason reason) => new(permission, false, PermissionSource.Invalid, reason);

    /// <summary>
    /// Whether the subject may use the permission, and why. Refused, with the reason, when the permission or the role is
    /// unknown, there is no user, or the user's own overrides for this permission are not exactly one valid decision or
    /// none. Overrides, memberships and grants of other users, groups and permissions are ignored; a membership or a grant
    /// repeated counts once.
    /// </summary>
    public static PermissionResult EvaluatePermission(PermissionSubject subject, string permission, IReadOnlyList<PermissionOverride> overrides, PermissionGroups? groups = null)
    {
        groups ??= PermissionGroups.None;
        if (!All.TryGetValue(permission, out var entry)) return Invalid(permission, InvalidReason.UnknownPermission);
        if (string.IsNullOrEmpty(subject.UserId)) return Invalid(permission, InvalidReason.NoUser);
        if (!RoleCodes.Contains(subject.RoleCode, StringComparer.Ordinal)) return Invalid(permission, InvalidReason.UnknownRole);
        var mine = overrides.Where(o => o.UserId == subject.UserId && o.Permission == permission).ToList();
        if (mine.Count > 1) return Invalid(permission, InvalidReason.DuplicateOverride);
        if (mine.Count == 1)
        {
            var decision = mine[0].Decision;
            if (decision == "allow") return new PermissionResult(permission, true, PermissionSource.Granted);
            if (decision == "deny") return new PermissionResult(permission, false, PermissionSource.Revoked);
            return Invalid(permission, InvalidReason.InvalidDecision);
        }
        // An empty group id is no group: a membership of it is ignored, so an equally empty grant can never meet it.
        var member = groups.Memberships.Where(m => m.UserId == subject.UserId && m.GroupId != "").Select(m => m.GroupId).ToHashSet(StringComparer.Ordinal);
        var giving = groups.Grants.Where(g => g.Permission == permission && member.Contains(g.GroupId)).Select(g => g.GroupId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var from = giving.Count > 0 ? giving : null;
        if (entry.Roles.Contains(subject.RoleCode, StringComparer.Ordinal)) return new PermissionResult(permission, true, PermissionSource.Role, Groups: from);
        if (giving.Count > 0) return new PermissionResult(permission, true, PermissionSource.Group, Groups: from);
        return new PermissionResult(permission, false, PermissionSource.NotInRole);
    }

    /// <summary>Every permission of the catalogue for the subject, in the catalogue's order — what the panel lists.</summary>
    public static IReadOnlyList<PermissionResult> EvaluateAll(PermissionSubject subject, IReadOnlyList<PermissionOverride> overrides, PermissionGroups? groups = null) =>
        All.Keys.Select(key => EvaluatePermission(subject, key, overrides, groups)).ToList();

    /// <summary>
    /// The catalogue as the panel shows it: key, Georgian name and section, the roles that have it by default. The names are
    /// PermissionTexts.Permissions and PermissionTexts.Sections; a key or section without a name shows as itself.
    /// </summary>
    public static IReadOnlyList<CatalogueItem> Catalogue(IReadOnlyDictionary<string, string> permissionNames, IReadOnlyDictionary<string, string> sectionNames) =>
        All.Select(p => new CatalogueItem(
            p.Key,
            permissionNames.TryGetValue(p.Key, out var name) ? name : p.Key,
            sectionNames.TryGetValue(p.Value.Section, out var section) ? section : p.Value.Section,
            p.Value.Roles.ToList())).ToList();
}
