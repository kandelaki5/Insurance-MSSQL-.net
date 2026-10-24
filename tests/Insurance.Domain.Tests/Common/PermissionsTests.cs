// Ported from src/lib/permissions.test.ts. The permissions evaluator — owner 15.10.2026: each role's starting set, a user's
// own grant or removal on top, and where each permission comes from. The starting set of premium.receipt.create: the
// accountant (docs/05 ვ3) and the admin (docs/05 ვ7). Pure; the module is not called by any action or page yet.
using Insurance.Application.Texts;
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class PermissionsTests
{
    private const string Key = "premium.receipt.create";
    private const string Anna = "user-anna";
    private const string Beka = "user-beka";
    private const string SalesDesk = "grp-sales-desk";
    private const string Cashiers = "grp-cashiers";

    private static PermissionOverride Allow(string userId, string permission = Key) => new(userId, permission, "allow");
    private static PermissionOverride Deny(string userId, string permission = Key) => new(userId, permission, "deny");
    private static PermissionResult Result(bool allowed, PermissionSource source, InvalidReason? reason = null) => new(Key, allowed, source, reason);
    private static GroupMembership Member(string userId, string groupId) => new(userId, groupId);
    private static GroupGrant Grant(string groupId, string permission = Key) => new(groupId, permission);
    private static PermissionResult FromGroups(bool allowed, PermissionSource source, params string[] groups) => new(Key, allowed, source, null, groups);
    private static PermissionResult NotInRole(string permission) => new(permission, false, PermissionSource.NotInRole);
    private static PermissionGroups Groups(GroupMembership[] memberships, GroupGrant[] grants) => new(memberships, grants);

    private static PermissionResult Eval(string userId, string roleCode, string permission, PermissionOverride[] overrides, PermissionGroups? groups = null) =>
        Permissions.EvaluatePermission(new PermissionSubject(userId, roleCode), permission, overrides, groups);

    private static string Show(PermissionResult r) =>
        $"{r.Permission}|{r.Allowed}|{r.Source.ToKebabCode()}|{(r.Reason is { } reason ? reason.ToKebabCode() : "-")}|{(r.Groups is null ? "-" : string.Join(",", r.Groups))}";

    private static void Same(IEnumerable<PermissionResult> expected, IEnumerable<PermissionResult> actual) =>
        Assert.Equal(expected.Select(Show).ToList(), actual.Select(Show).ToList());

    private static void Same(PermissionResult expected, PermissionResult actual) => Assert.Equal(Show(expected), Show(actual));

    private static readonly string[] Rest = ["claim.recovery.receive", "claim.payment.record", "policy.approve", "accounting.period.close", "claim.payment.reverse", "claim.reserve.approve", "audit.log.view", "payment.large.authorize", "finance.panel.view",
        // Insurance-Software PR #362 (src/lib/permissions.test.ts, head 540e81a): the reinsurer's commission settlement, last.
        "reinsurance.commission.settle"];

    // ── the starting set by role (no overrides) ──

    [Fact(DisplayName = "premium.receipt.create — the accountant and the admin have it; every other role does not")]
    public void StartingSetByRole()
    {
        // By hand, every role there is (db/migrations 0001 and 0008): accountant and admin from the role, the five others not.
        Assert.Equal(["admin", "underwriter", "sales", "claims", "accountant", "viewer", "operations_head"], Permissions.RoleCodes);
        Same(
            [
                Result(true, PermissionSource.Role), Result(false, PermissionSource.NotInRole), Result(false, PermissionSource.NotInRole),
                Result(false, PermissionSource.NotInRole), Result(true, PermissionSource.Role), Result(false, PermissionSource.NotInRole),
                Result(false, PermissionSource.NotInRole),
            ],
            Permissions.RoleCodes.Select(roleCode => Eval(Anna, roleCode, Key, [])));
    }

    // ── a user's own override on top of the role ──

    [Fact(DisplayName = "the accountant, removed: refused, \"revoked\"; the override gone (inherit): the role again")]
    public void AccountantRemoved() =>
        Same([Result(false, PermissionSource.Revoked), Result(true, PermissionSource.Role)], [Eval(Anna, "accountant", Key, [Deny(Anna)]), Eval(Anna, "accountant", Key, [])]);

    [Fact(DisplayName = "the underwriter, granted: allowed, \"granted\"; the grant removed (inherit): refused again, \"not-in-role\"")]
    public void UnderwriterGranted() =>
        Same([Result(true, PermissionSource.Granted), Result(false, PermissionSource.NotInRole)], [Eval(Anna, "underwriter", Key, [Allow(Anna)]), Eval(Anna, "underwriter", Key, [])]);

    [Fact(DisplayName = "a grant to one role that already has it, or a removal from one that does not, still says where it comes from")]
    public void OverrideSaysWhere() =>
        Same([Result(true, PermissionSource.Granted), Result(false, PermissionSource.Revoked)], [Eval(Anna, "accountant", Key, [Allow(Anna)]), Eval(Anna, "sales", Key, [Deny(Anna)])]);

    [Fact(DisplayName = "one user's override is that user's only: Anna's grant does not reach Beka, Beka's removal does not reach Anna")]
    public void OverrideIsOwn()
    {
        PermissionOverride[] overrides = [Allow(Anna), Deny(Beka)];
        Same(
            [Result(true, PermissionSource.Granted), Result(false, PermissionSource.NotInRole), Result(false, PermissionSource.Revoked), Result(true, PermissionSource.Role)],
            [Eval(Anna, "claims", Key, overrides), Eval(Beka, "claims", Key, [Allow(Anna)]), Eval(Beka, "accountant", Key, overrides), Eval(Anna, "accountant", Key, [Deny(Beka)])]);
    }

    [Fact(DisplayName = "an override of another permission does not touch this one")]
    public void OtherPermissionOverride() => Same(Result(true, PermissionSource.Role), Eval(Anna, "accountant", Key, [Deny(Anna, "claim.register")]));

    // ── anything unknown or invalid is refused, never allowed ──

    [Fact(DisplayName = "an unknown permission — even for the admin, even with a grant — is refused as unknown")]
    public void UnknownPermission()
    {
        foreach (var key in new[] { "premium.receipt.delete", "", "__proto__", "constructor", "toString" })
        {
            var expected = new PermissionResult(key, false, PermissionSource.Invalid, InvalidReason.UnknownPermission);
            Same([expected, expected], [Eval(Anna, "admin", key, []), Eval(Anna, "admin", key, [Allow(Anna, key)])]);
        }
    }

    [Fact(DisplayName = "an unknown role — even with a grant — and a subject without a user are refused")]
    public void UnknownRoleAndNoUser() =>
        Same(
            [Result(false, PermissionSource.Invalid, InvalidReason.UnknownRole), Result(false, PermissionSource.Invalid, InvalidReason.UnknownRole), Result(false, PermissionSource.Invalid, InvalidReason.UnknownRole), Result(false, PermissionSource.Invalid, InvalidReason.NoUser)],
            [Eval(Anna, "chief_accountant", Key, []), Eval(Anna, "chief_accountant", Key, [Allow(Anna)]), Eval(Anna, "", Key, []), Eval("", "accountant", Key, [Allow("")])]);

    [Fact(DisplayName = "a decision other than allow or deny is refused, whatever the role says")]
    public void InvalidDecision()
    {
        foreach (var decision in new[] { "ALLOW", "yes", "inherit", "", "true" })
        {
            var expected = Result(false, PermissionSource.Invalid, InvalidReason.InvalidDecision);
            Same([expected, expected], [Eval(Anna, "accountant", Key, [new(Anna, Key, decision)]), Eval(Anna, "underwriter", Key, [new(Anna, Key, decision)])]);
        }
    }

    [Fact(DisplayName = "two overrides of the same user and permission — conflicting or the same — are refused, not resolved")]
    public void DuplicateOverrides()
    {
        var expected = Result(false, PermissionSource.Invalid, InvalidReason.DuplicateOverride);
        Same(
            [expected, expected, expected, expected],
            [Eval(Anna, "underwriter", Key, [Allow(Anna), Deny(Anna)]), Eval(Anna, "underwriter", Key, [Deny(Anna), Allow(Anna)]), Eval(Anna, "underwriter", Key, [Allow(Anna), Allow(Anna)]), Eval(Anna, "accountant", Key, [Deny(Anna), Deny(Anna)])]);
    }

    // ── the whole catalogue and the inputs ──

    [Fact(DisplayName = "evaluateAll lists every permission of the catalogue with its source; catalogue() names it in Georgian")]
    public void EvaluateAllAndCatalogue()
    {
        // The grant is of the premium receipt only: the recovery, the claim payment, the approval and the month close stay as the role has them.
        Same([Result(true, PermissionSource.Granted), .. Rest.Select(NotInRole)], Permissions.EvaluateAll(new PermissionSubject(Anna, "underwriter"), [Allow(Anna)]));
        Same([Result(false, PermissionSource.NotInRole), .. Rest.Select(NotInRole)], Permissions.EvaluateAll(new PermissionSubject(Anna, "viewer"), []));
        CatalogueItem[] expected =
        [
            new(Key, "პრემიის მიღება", "პრემიები", ["accountant", "admin"]),
            new("claim.recovery.receive", "რეგრესის / გადარჩენილი ქონების თანხის მიღება", "ზარალები", ["accountant", "admin"]),
            new("claim.payment.record", "ზარალის გადახდის შეყვანა", "ზარალები", ["accountant", "admin"]),
            new("policy.approve", "პოლისის დამტკიცება", "პოლისები", ["operations_head", "admin"]),
            new("accounting.period.close", "თვის დახურვა", "საანგარიშო პერიოდები", ["accountant", "admin"]),
            new("claim.payment.reverse", "ზარალის გადახდის შებრუნება", "ზარალები", ["accountant", "admin"]),
            new("claim.reserve.approve", "რეზერვის ცვლილების დამტკიცება", "ზარალები", ["admin"]),
            new("audit.log.view", "ცვლილებების ჟურნალის ნახვა", "აუდიტი", ["admin"]),
            new("payment.large.authorize", "ზღვარზე მეტი გადახდის ავტორიზაცია (ფინანსური დირექტორი)", "ფინანსები", ["admin"]),
            new("finance.panel.view", "ფინანსური დირექტორის პანელის ნახვა", "ფინანსები", ["admin"]),
            // Insurance-Software PR #362 (src/lib/permissions.test.ts L131, head 540e81a).
            new("reinsurance.commission.settle", "გადამზღვევლის საკომისიოს ანგარიშსწორება", "გადაზღვევა", ["accountant", "admin"]),
        ];
        static string ShowItem(CatalogueItem i) => $"{i.Key}|{i.Name}|{i.Section}|{string.Join(",", i.Roles)}";
        Assert.Equal(expected.Select(ShowItem), Permissions.Catalogue(PermissionTexts.Permissions, PermissionTexts.Sections).Select(ShowItem));
    }

    [Fact(DisplayName = "the same input gives the same answer, and the inputs are not changed")]
    public void SameInputSameAnswer()
    {
        var subject = new PermissionSubject(Anna, "underwriter");
        PermissionOverride[] overrides = [Allow(Anna), Deny(Beka)];
        var copy = overrides.ToArray();
        var first = Permissions.EvaluatePermission(subject, Key, overrides);
        Same(first, Permissions.EvaluatePermission(subject, Key, overrides));
        Assert.Equal(copy, overrides);
        Assert.Equal(new PermissionSubject(Anna, "underwriter"), subject);
    }

    // ── groups of users — a default the group gives its members, a user's own override on top ──
    // Owner 15.10.2026: permissions can be given to groups of users as their default settings, and a particular user's can
    // still be extended. The technical rule: role and groups add up (a group only adds); the user's own override decides.

    [Fact(DisplayName = "an underwriter in a group that has it: allowed, \"group\", with the group; out of the group: refused again")]
    public void UnderwriterInGroup()
    {
        var groups = Groups([Member(Anna, Cashiers)], [Grant(Cashiers)]);
        Same(
            [FromGroups(true, PermissionSource.Group, Cashiers), Result(false, PermissionSource.NotInRole)],
            [Eval(Anna, "underwriter", Key, [], groups), Eval(Anna, "underwriter", Key, [], Groups([], [.. groups.Grants]))]);
    }

    [Fact(DisplayName = "several groups add up: each giving group listed once, sorted — repeated memberships and grants are not a conflict")]
    public void SeveralGroupsAddUp()
    {
        var groups = Groups([Member(Anna, SalesDesk), Member(Anna, Cashiers), Member(Anna, SalesDesk)], [Grant(SalesDesk), Grant(Cashiers), Grant(SalesDesk)]);
        Same(FromGroups(true, PermissionSource.Group, Cashiers, SalesDesk), Eval(Anna, "sales", Key, [], groups));
    }

    [Fact(DisplayName = "a role that has it stays \"role\", the giving groups shown beside it")]
    public void RoleStaysRole() =>
        Same(FromGroups(true, PermissionSource.Role, Cashiers), Eval(Anna, "accountant", Key, [], Groups([Member(Anna, Cashiers)], [Grant(Cashiers)])));

    [Fact(DisplayName = "the user's own removal beats every group and the role; the own grant gives it without one; inherit leaves the groups")]
    public void OwnOverrideBeatsGroups()
    {
        var groups = Groups([Member(Anna, Cashiers), Member(Anna, SalesDesk)], [Grant(Cashiers), Grant(SalesDesk)]);
        Same(
            [Result(false, PermissionSource.Revoked), Result(false, PermissionSource.Revoked), Result(true, PermissionSource.Granted), FromGroups(true, PermissionSource.Group, Cashiers, SalesDesk)],
            [Eval(Anna, "underwriter", Key, [Deny(Anna)], groups), Eval(Anna, "accountant", Key, [Deny(Anna)], groups), Eval(Anna, "claims", Key, [Allow(Anna)], Groups([], [])), Eval(Anna, "underwriter", Key, [], groups)]);
    }

    [Fact(DisplayName = "only a real member, only that group's grant, only this permission: another user's membership, another group's grant, another permission give nothing")]
    public void OnlyRealMembership()
    {
        var expected = Result(false, PermissionSource.NotInRole);
        Same(
            [expected, expected, expected, expected],
            [
                Eval(Anna, "underwriter", Key, [], Groups([Member(Beka, Cashiers)], [Grant(Cashiers)])),
                Eval(Anna, "underwriter", Key, [], Groups([Member(Anna, SalesDesk)], [Grant(Cashiers)])),
                Eval(Anna, "underwriter", Key, [], Groups([Member(Anna, Cashiers)], [Grant(Cashiers, "claim.register")])),
                Eval(Beka, "underwriter", Key, [], Groups([Member(Anna, Cashiers)], [Grant(Cashiers)])),
            ]);
    }

    [Fact(DisplayName = "a broken link gives nothing: a membership of a group with no grants, a grant of a group with no members, an empty group id")]
    public void BrokenLinks()
    {
        var expected = Result(false, PermissionSource.NotInRole);
        Same(
            [expected, expected, expected, expected],
            [
                Eval(Anna, "viewer", Key, [], Groups([Member(Anna, Cashiers)], [])),
                Eval(Anna, "viewer", Key, [], Groups([], [Grant(Cashiers)])),
                Eval(Anna, "viewer", Key, [], Groups([Member(Anna, "")], [Grant(Cashiers)])),
                Eval(Anna, "viewer", Key, [], Groups([Member(Anna, Cashiers)], [Grant("")])),
            ]);
    }

    // The test above has the empty id on one side only; here it is on both sides, so the two would meet (Codex
    // PR231-EMPTY-GROUP-ID): an empty group id is no group — it gives nothing and is never listed as a source.
    [Fact(DisplayName = "an empty group id on both sides gives nothing; beside a real group it is not listed; a role keeps its own source")]
    public void EmptyGroupIdBothSides()
    {
        var empty = Groups([Member(Anna, "")], [Grant("")]);
        var mixed = Groups([Member(Anna, ""), Member(Anna, "g-valid")], [Grant(""), Grant("g-valid")]);
        Same(
            [Result(false, PermissionSource.NotInRole), FromGroups(true, PermissionSource.Group, "g-valid"), Result(true, PermissionSource.Role), Result(true, PermissionSource.Role), FromGroups(true, PermissionSource.Role, "g-valid")],
            [Eval(Anna, "underwriter", Key, [], empty), Eval(Anna, "underwriter", Key, [], mixed), Eval(Anna, "accountant", Key, [], empty), Eval(Anna, "admin", Key, [], empty), Eval(Anna, "accountant", Key, [], mixed)]);
    }

    [Fact(DisplayName = "with empty ids about: the own removal still beats a real group, and nothing passes to another user")]
    public void EmptyIdsAbout()
    {
        var groups = Groups([Member(Anna, ""), Member(Anna, Cashiers), Member(Beka, "")], [Grant(""), Grant(Cashiers)]);
        Same(
            [Result(false, PermissionSource.Revoked), Result(false, PermissionSource.NotInRole), FromGroups(true, PermissionSource.Group, Cashiers)],
            [Eval(Anna, "underwriter", Key, [Deny(Anna)], groups), Eval(Beka, "underwriter", Key, [], groups), Eval(Anna, "underwriter", Key, [], groups)]);
    }

    [Fact(DisplayName = "a group grant does not rescue an unknown permission, an unknown role, a missing user or an invalid override")]
    public void GroupDoesNotRescue()
    {
        var groups = Groups([Member(Anna, Cashiers), Member("", Cashiers)], [Grant(Cashiers), Grant(Cashiers, "premium.receipt.delete")]);
        Same(
            [
                new PermissionResult("premium.receipt.delete", false, PermissionSource.Invalid, InvalidReason.UnknownPermission),
                Result(false, PermissionSource.Invalid, InvalidReason.UnknownRole), Result(false, PermissionSource.Invalid, InvalidReason.NoUser),
                Result(false, PermissionSource.Invalid, InvalidReason.InvalidDecision), Result(false, PermissionSource.Invalid, InvalidReason.DuplicateOverride),
            ],
            [
                Eval(Anna, "underwriter", "premium.receipt.delete", [], groups),
                Eval(Anna, "chief_accountant", Key, [], groups),
                Eval("", "underwriter", Key, [], groups),
                Eval(Anna, "underwriter", Key, [new(Anna, Key, "inherit")], groups),
                Eval(Anna, "underwriter", Key, [Allow(Anna), Deny(Anna)], groups),
            ]);
    }

    [Fact(DisplayName = "evaluateAll passes the groups on; the groups given are not changed")]
    public void EvaluateAllPassesGroups()
    {
        var groups = Groups([Member(Anna, Cashiers)], [Grant(Cashiers)]);
        Same([FromGroups(true, PermissionSource.Group, Cashiers), .. Rest.Select(NotInRole)], Permissions.EvaluateAll(new PermissionSubject(Anna, "viewer"), [], groups));
        Assert.Equal([Member(Anna, Cashiers)], groups.Memberships);
        Assert.Equal([Grant(Cashiers)], groups.Grants);
    }

    // ── the admin (owner 15.10.2026 — every permission, its own included) ──

    [Fact(DisplayName = "every permission of the catalogue lists the admin, so the admin has each from the role")]
    public void AdminHasEverything()
    {
        Assert.DoesNotContain(Permissions.All, p => !p.Value.Roles.Contains("admin"));
        Assert.DoesNotContain(Permissions.EvaluateAll(new PermissionSubject(Anna, "admin"), []), r => !r.Allowed);
    }

    [Fact(DisplayName = "a group that does not tick it takes nothing from the admin; the admin's own override still decides")]
    public void GroupTakesNothingFromAdmin()
    {
        var groups = Groups([Member(Anna, "grp-viewers")], [Grant("grp-cashiers")]);
        Same(
            [Result(true, PermissionSource.Role), Result(false, PermissionSource.Revoked), Result(true, PermissionSource.Granted)],
            [Eval(Anna, "admin", Key, [], groups), Eval(Anna, "admin", Key, [Deny(Anna)], groups), Eval(Anna, "admin", Key, [Allow(Anna)], groups)]);
    }
}
