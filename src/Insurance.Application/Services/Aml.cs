using System.Globalization;
using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Domain.Compliance;

namespace Insurance.Application.Services;

/// <summary>
/// Database side of AML cases and FMS suspensions (legacy src/lib/aml.ts).
///
/// Locks (docs/04 წესი 19, review H13): an operation that a suspension can block holds its client's lock shared and
/// checks for a suspension inside its transaction; registering, confirming or attaching a suspension holds the client's
/// lock exclusively. Legacy used the party row (for share / for update); here a transaction-owned application lock on
/// the client, 'core.party:&lt;id&gt;', with the same modes — so a suspension waits for operations already in flight, an
/// operation that starts after it sees it, and operations do not block each other.
/// </summary>
public static partial class Aml
{
    public sealed record SuspensionRow(
        Guid Id, Guid PartyId, string PartyName, string Kind, string Scope, string? TransactionRef, Guid? TargetPolicyId, Guid? TargetClaimId,
        bool LegacyUnmapped, string InstructedAt, string? WrittenConfirmedAt, string ConfirmBy, string EndsAt, string? LiftedAt, string? LiftReason,
        string? CaseNumber, Guid? CaseId, string? Note, string State)
    {
        public SuspensionTargeting Targeting => new(Codes.Parse<SuspensionState>(State), Codes.Parse<SuspensionScope>(Scope),
            TargetPolicyId?.ToString(), TargetClaimId?.ToString(), LegacyUnmapped, TransactionRef);
    }

    /// <summary>AML cases and suspensions are confidential: only the compliance roles see them (AML law art. 28).</summary>
    public static bool CanSeeAml(CurrentUser user) => user.RoleCode is "admin" or "operations_head" or "accountant";

    /// <summary>Compliance work is for the administrator, the head of operations and the accountant.</summary>
    public static bool IsComplianceRole(CurrentUser user) => CanSeeAml(user) && user.CanWrite;

    private static string Ts(string column) => $"format(switchoffset({column}, '+04:00'), 'dd.MM.yyyy HH:mm')";

    private static readonly string Select = $"""
        select s.id as Id, s.party_id as PartyId, pa.display_name as PartyName, s.kind as Kind, s.scope as Scope, s.transaction_ref as TransactionRef,
               s.target_policy_id as TargetPolicyId, s.target_claim_id as TargetClaimId, s.legacy_unmapped as LegacyUnmapped,
               {Ts("s.instructed_at")} as InstructedAt, {Ts("s.written_confirmed_at")} as WrittenConfirmedAt,
               {Ts("dateadd(hour, 24, s.instructed_at)")} as ConfirmBy,
               {Ts("s.ends_at")} as EndsAt, {Ts("s.lifted_at")} as LiftedAt, s.lift_reason as LiftReason,
               c.number as CaseNumber, s.case_id as CaseId, s.note as Note,
               case when s.lifted_at is not null or s.ends_at <= sysdatetimeoffset() then N'lapsed'
                    when s.kind = N'written' or s.written_confirmed_at is not null then N'active'
                    when sysdatetimeoffset() < dateadd(hour, 24, s.instructed_at) then N'awaiting'
                    else N'unconfirmed' end as State
        from core.fms_suspension s
        join core.party pa on pa.id = s.party_id
        left join core.aml_case c on c.id = s.case_id
        """;

    private static async Task<List<SuspensionRow>> OfPartyAsync(Tx db, Guid partyId) =>
        (await db.QueryAsync<SuspensionRow>($"{Select} where s.party_id = @partyId order by s.instructed_at desc", new { partyId })).ToList();

    /// <summary>
    /// The suspension in force for a client now, if any: not lifted, not expired, and — for an oral instruction —
    /// confirmed in writing or still inside the 24-hour confirmation window.
    /// </summary>
    public static async Task<SuspensionRow?> ActiveSuspensionAsync(Tx db, Guid partyId) =>
        (await OfPartyAsync(db, partyId)).FirstOrDefault(r => r.State != "lapsed");

    /// <summary>
    /// The suspension that blocks an operation of this client, if any: one in force over all of the client's operations,
    /// or one over the specific policy or claim the operation touches (H11). The operation's numbers are passed too, for
    /// suspensions saved before targets existed that could not be attached (legacy migration 0022).
    /// </summary>
    public static async Task<SuspensionRow?> BlockingSuspensionAsync(Tx db, Guid partyId, OperationTargets? targets = null) =>
        (await OfPartyAsync(db, partyId)).FirstOrDefault(r => Suspension.SuspensionBlocks(r.Targeting, targets ?? new OperationTargets()));

    private static string PartyLock(Guid partyId) => partyId.ToString();

    /// <summary>
    /// The authoritative suspension check of an operation, inside its transaction: the client's lock shared first, then
    /// the check. A receipt can no longer commit after a suspension that it did not see.
    /// </summary>
    public static async Task<SuspensionRow?> SuspensionInTransactionAsync(Tx tx, Guid partyId, OperationTargets? targets = null)
    {
        await tx.LockAsync("core.party", PartyLock(partyId), shared: true);
        return await BlockingSuspensionAsync(tx, partyId, targets);
    }

    /// <summary>
    /// Locks for any change that can make a suspension take effect: registering, confirming in writing, attaching to a
    /// target (review H13). The targeted policy or claim row is read under a shared lock first (legacy "for key share",
    /// the lock its foreign key takes), then the client's lock is taken exclusively. Operations lock their policy or claim
    /// first and the client after, so both sides take the locks in the same order.
    /// </summary>
    public static async Task LockForSuspensionChangeAsync(Tx tx, Guid partyId, Guid? policyId = null, Guid? claimId = null)
    {
        if (policyId is { } p) await tx.ExecuteAsync("select id from core.policy with (holdlock, rowlock) where id = @p", new { p });
        if (claimId is { } c) await tx.ExecuteAsync("select id from core.claim with (holdlock, rowlock) where id = @c", new { c });
        await tx.LockAsync("core.party", PartyLock(partyId));
    }

    /// <summary>
    /// Resolves the number typed for a transaction suspension to the client's own policy or claim, with the database's
    /// own rule (core.resolve_suspension_target): case and spaces are ignored, and a number that is empty, unknown,
    /// another client's, or carried by both a policy and a claim of the client resolves to nothing (review H11-U2).
    /// </summary>
    public static async Task<(Guid? PolicyId, Guid? ClaimId, string Number)?> SuspensionTargetAsync(Tx db, Guid partyId, string number)
    {
        var r = await db.QuerySingleOrDefaultAsync<(Guid? PolicyId, Guid? ClaimId, string? Number)?>(
            "select policy_id, claim_id, number from core.resolve_suspension_target(@partyId, @number)", new { partyId, number });
        if (r is not { } t || (t.PolicyId is null && t.ClaimId is null) || t.Number is null) return null;
        return (t.PolicyId, t.ClaimId, t.Number);
    }

    /// <summary>All suspensions of a client or a case (the latest 200), or only those still in force.</summary>
    public static async Task<IReadOnlyList<SuspensionRow>> SuspensionsOfAsync(Tx db, Guid? partyId = null, Guid? caseId = null, bool inForce = false)
    {
        var rows = (await db.QueryAsync<SuspensionRow>(
            $"{Select} where (@partyId is null or s.party_id = @partyId) and (@caseId is null or s.case_id = @caseId) order by s.instructed_at desc offset 0 rows fetch next 200 rows only",
            new { partyId, caseId })).ToList();
        return inForce ? rows.Where(r => r.State != "lapsed").ToList() : rows;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}")]
    private static partial Regex LocalDateTime();

    /// <summary>A datetime-local value entered in Tbilisi time (Georgia keeps UTC+4 all year).</summary>
    public static DateTimeOffset? TbilisiInstant(string? value)
    {
        if (value is null || !LocalDateTime().IsMatch(value)) return null;
        return DateTimeOffset.TryParseExact(value[..16] + ":00+04:00", "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    /// <summary>The current Tbilisi time as a datetime-local value.</summary>
    public static string TbilisiNowLocal(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(4)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}
