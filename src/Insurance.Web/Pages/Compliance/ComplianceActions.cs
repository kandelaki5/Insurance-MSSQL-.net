using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Compliance;

namespace Insurance.Web.Pages.Compliance;

/// <summary>The suspensions section on a client's card, an AML case and the register of suspensions in force.</summary>
public sealed record SuspensionSectionView(
    string Title, Guid? PartyId, Guid? CaseId, IReadOnlyList<Aml.SuspensionRow> Rows, bool Writable, string Back, bool ShowParty, bool TargetError,
    DateTimeOffset Now);

/// <summary>Screening rows with their matches and, for open ones, the decision form (on the sanctions page and a client's card).</summary>
public sealed record ScreeningTableView(IReadOnlyList<SanctionsScreening.ScreeningRow> Rows, bool Writable, string Back, bool ShowParty);

/// <summary>
/// The actions shared by several pages (legacy compliance/aml-cases/actions.ts createSuspension, updateSuspension and
/// compliance/sanctions/actions.ts screenOneParty, decideScreening). Each returns the address to go to, or null when the
/// legacy action returned without doing anything (the page is shown again as it was).
/// </summary>
public static partial class ComplianceActions
{
    [GeneratedRegex(@"^/(parties|compliance)/[A-Za-z0-9_/-]*\z")]
    private static partial Regex SafeBackPath();

    /// <summary>Only paths inside this system are valid return addresses.</summary>
    public static string SafeBack(string? value, string fallback) => value is not null && SafeBackPath().IsMatch(value) ? value : fallback;

    /// <summary>Registers an FMS instruction to suspend a client's operations; the client is blocked at once.</summary>
    public static async Task<string?> CreateSuspensionAsync(Db db, CurrentUser user, Func<string, string?> form)
    {
        if (!Aml.IsComplianceRole(user)) return null;
        var partyText = form("party_id") ?? "";
        var caseId = PermissionPanel.Uuid(form("case_id"));
        var kind = form("kind") == "written" ? "written" : "oral";
        var scope = form("scope") == "transaction" ? "transaction" : "all";
        var instructedAt = Aml.TbilisiInstant(form("instructed_at") ?? "") ?? db.Now();
        var back = SafeBack(form("back"), $"/parties/{partyText}");
        if (PermissionPanel.Uuid(partyText) is not { } partyId) return null;
        var endsAt = Suspension.SuspensionEnd(instructedAt);
        var note = JsText.Trim(form("note") ?? "");
        var saved = await db.AsUserAsync(user.Id, async tx =>
        {
            // A suspension of one transaction must name this client's own policy or claim (H11); otherwise nothing is
            // saved — it would look active and block nothing.
            var target = scope == "transaction" ? await Aml.SuspensionTargetAsync(tx, partyId, form("transaction_ref") ?? "") : null;
            if (scope == "transaction" && target is null) return false;
            // Target first, then the client exclusively: waits for the client's operations in flight, so none of them can
            // commit after this suspension (Aml.LockForSuspensionChangeAsync).
            await Aml.LockForSuspensionChangeAsync(tx, partyId, target?.PolicyId, target?.ClaimId);
            await tx.ExecuteAsync("""
                insert into core.fms_suspension (id, party_id, case_id, kind, scope, transaction_ref, target_policy_id, target_claim_id,
                                                 instructed_at, written_confirmed_at, ends_at, note, created_by)
                values (@id, @partyId, @caseId, @kind, @scope, @ref, @policyId, @claimId, @instructedAt, @confirmedAt, @endsAt, @note, @userId)
                """, new
            {
                id = Guid.NewGuid(), partyId, caseId, kind, scope, @ref = target?.Number, policyId = target?.PolicyId, claimId = target?.ClaimId,
                instructedAt, confirmedAt = kind == "written" ? instructedAt : (DateTimeOffset?)null, endsAt, note = note == "" ? null : note, userId = user.Id,
            });
            return true;
        });
        return saved ? back : $"{back}?suspension=target";
    }

    /// <summary>
    /// Confirms an oral instruction in writing, lifts a suspension (FMS decision or expiry), or attaches an old unmapped
    /// suspension to the client's policy or claim.
    /// </summary>
    public static async Task<string?> UpdateSuspensionAsync(Db db, CurrentUser user, Func<string, string?> form)
    {
        if (!Aml.IsComplianceRole(user)) return null;
        var action = form("action") ?? "";
        var back = SafeBack(form("back"), "/compliance/aml-cases");
        if (PermissionPanel.Uuid(form("id")) is not { } id) return null;
        // Whether a client is blocked is always computed from the suspensions themselves (Aml), so expiry and the lapse
        // of an unconfirmed oral instruction need no stored flag.
        var attached = await db.AsUserAsync(user.Id, async tx =>
        {
            var partyId = await tx.QuerySingleOrDefaultAsync<Guid?>("select party_id from core.fms_suspension where id = @id", new { id });
            if (partyId is not { } party) return true;
            if (action == "attach")
            {
                // Repair of a suspension saved before targets existed (legacy migration 0022): attach it to the client's own
                // policy or claim; the database then clears its "unmapped" mark.
                var target = await Aml.SuspensionTargetAsync(tx, party, form("attach_ref") ?? "");
                if (target is not { } t) return false;
                await Aml.LockForSuspensionChangeAsync(tx, party, t.PolicyId, t.ClaimId);
                await tx.ExecuteAsync("""
                    update core.fms_suspension set target_policy_id = @policyId, target_claim_id = @claimId, transaction_ref = @number
                    where id = @id and legacy_unmapped = 1
                    """, new { policyId = t.PolicyId, claimId = t.ClaimId, number = t.Number, id });
            }
            else if (action == "confirm")
            {
                // An oral instruction stays in force after its 24 hours until the officer resumes the operations (art. 36
                // p. 3; the owner's decision, 04.10.2026), so the written confirmation only records the letter. It still
                // takes the same locks as registering (review H13).
                await Aml.LockForSuspensionChangeAsync(tx, party);
                await tx.ExecuteAsync("update core.fms_suspension set written_confirmed_at = core.tx_now() where id = @id and written_confirmed_at is null and lifted_at is null", new { id });
            }
            else if (action == "lift")
            {
                var reason = JsText.Trim(form("lift_reason") ?? "");
                if (reason == "") return true;
                await tx.ExecuteAsync("update core.fms_suspension set lifted_at = core.tx_now(), lift_reason = @reason where id = @id and lifted_at is null", new { reason, id });
            }
            return true;
        });
        return attached ? back : $"{back}?suspension=target";
    }

    /// <summary>Screens one client and its owners now; the viewer, or a malformed id, does nothing.</summary>
    public static async Task<string?> ScreenOnePartyAsync(Db db, CurrentUser user, Func<string, string?> form)
    {
        var partyText = form("party_id") ?? "";
        if (!user.CanWrite || PermissionPanel.Uuid(partyText) is not { } partyId) return null;
        var result = await db.AsUserAsync(user.Id, tx => SanctionsScreening.ScreenPartiesAsync(tx, user.Id, partyId));
        return $"/parties/{partyText}?screened={result.Screened}&hits={result.Hits}";
    }

    /// <summary>
    /// The return address of a screening decision: a path of this site. The legacy code took anything starting with '/',
    /// which also lets '//host' through to another site; those are sent to the sanctions page instead.
    /// </summary>
    public static string DecisionBack(string? back)
    {
        var path = back ?? "/compliance/sanctions";
        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal) ? path : "/compliance/sanctions";
    }

    /// <summary>Decides a screening with hits: dismissed as a false positive, confirmed (client blocked), or the block lifted.</summary>
    public static async Task<string?> DecideScreeningAsync(Db db, CurrentUser user, Func<string, string?> form)
    {
        var decision = form("decision") ?? "";
        var note = JsText.Trim(form("note") ?? "");
        if (!user.CanWrite || PermissionPanel.Uuid(form("id")) is not { } id || note == "") return null;
        await db.AsUserAsync(user.Id, async tx =>
        {
            await SanctionsScreening.DecideAsync(tx, user.Id, id, decision, note);
            return 0;
        });
        return DecisionBack(form("back"));
    }
}
