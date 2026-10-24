using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using SubrogationRules = Insurance.Domain.Finance.Subrogation;
using SubrogationChecks = Insurance.Domain.Common.Subrogation;

namespace Insurance.Application.Services;

/// <summary>What a regress operation ended in: the id it wrote, or the legacy error code ('missing', 'cap', 'balance'…).</summary>
public sealed record SubrogationResult(bool Ok, Guid? Id, string? Error)
{
    public static SubrogationResult Done(Guid id) => new(true, id, null);
    public static SubrogationResult Fail(string error) => new(false, null, error);
}

/// <summary>Runs one operation in one transaction: everything it wrote is kept, or — if it throws — nothing.</summary>
public delegate Task<SubrogationResult> SubrogationTransact(Func<ISubrogationStore, Task<SubrogationResult>> operation);

/// <summary>
/// Regress operations (docs/08 version 8; legacy src/lib/subrogation.ts), each in one transaction. The rules are in
/// Insurance.Domain.Finance.Subrogation; this class orders the reads and writes. Storage is reached through an
/// <see cref="ISubrogationStore"/>; the database's triggers (migration 0003, legacy 0030) check the same rules again.
/// There is no page for them in the old program: they are services, used by tests and by the screens to come.
/// </summary>
public static class SubrogationCases
{
    /// <summary>The usual transaction: Db.AsUserAsync with the SQL Server store, attributed to the user.</summary>
    public static SubrogationTransact InDatabase(Db db, Guid userId) => operation => db.AsUserAsync(userId, tx => operation(new SqlSubrogationStore(tx)));

    private static SubrogationResult Fail(string error) => SubrogationResult.Fail(error);

    private static string Code<T>(T value) where T : struct, Enum => Codes.Of(value);

    private static readonly DocumentKind[] Claimed = [DocumentKind.DemandLetter];
    private static readonly DocumentKind[] Confirmed = [DocumentKind.DebtorAcceptance, DocumentKind.CourtDecision];
    private static readonly DocumentKind[] Cancelled = [DocumentKind.CourtDecision];

    public static Task<SubrogationResult> OpenCaseAsync(SubrogationTransact transact, Guid claimId, DateOnly openedOn, string? note, Guid requestKey) =>
        transact(async s =>
        {
            if (await s.CaseByKeyAsync(requestKey) is not null) return Fail(SubrogationErrors.Duplicate);
            var facts = await s.ClaimFactsAsync(claimId);
            if (facts is null) return Fail(SubrogationErrors.Missing);
            if (SubrogationRules.Ineligibility(facts) is { } why) return Fail(Code(why));
            await s.LockClaimAsync(claimId);
            if (await s.HasCaseAsync(claimId)) return Fail(SubrogationErrors.Open);
            var number = await s.NextNumberAsync();
            if (number is null) return Fail(SubrogationErrors.Series);
            return SubrogationResult.Done(await s.InsertCaseAsync(new NewCase(number, claimId, openedOn, note, requestKey)));
        });

    public static Task<SubrogationResult> AddPartyAsync(SubrogationTransact transact, Guid caseId, Guid partyId, Domain.Common.PartyRole role, string? policyNumber, string? coverage, long? limit) =>
        transact(async s =>
        {
            if (await s.GetCaseAsync(caseId) is null) return Fail(SubrogationErrors.Missing);
            if (SubrogationChecks.PartyProblem(new NewParty(caseId.ToString(), partyId.ToString(), role, policyNumber, coverage, limit)) is { } problem) return Fail(problem);
            return SubrogationResult.Done(await s.InsertPartyAsync(caseId, partyId, role, policyNumber, coverage, limit));
        });

    /// <summary>The document that shows who was at fault (step 2).</summary>
    public static Task<SubrogationResult> RecordEvidenceAsync(SubrogationTransact transact, Guid caseId, BasisKind kind, string number, DateOnly date) =>
        transact(async s =>
        {
            var c = await LockedCaseAsync(s, caseId);
            if (c is null) return Fail(SubrogationErrors.Missing);
            if (SubrogationChecks.IsBlank(number)) return Fail(SubrogationErrors.Document);
            await s.SetEvidenceAsync(c.Id, kind, JsText.Trim(number), date);
            if (c.Status == CaseStatus.Opened) await s.SetStatusAsync(c.Id, CaseStatus.EvidenceConfirmed);
            return SubrogationResult.Done(c.Id);
        });

    /// <summary>
    /// The debtor's answer (step 4). A dispute or a rejection is not a confirmed debt (G5): it moves no money. An acceptance
    /// is recorded here too, but the debt is confirmed only by <see cref="AddMovementAsync"/> ('confirmed', with its amount).
    /// </summary>
    public static Task<SubrogationResult> RecordResponseAsync(SubrogationTransact transact, Guid caseId, Guid casePartyId, SubrogationResponse response, DateOnly on) =>
        transact(async s =>
        {
            var c = await LockedCaseAsync(s, caseId);
            if (c is null) return Fail(SubrogationErrors.Missing);
            if (!(await s.PartiesAsync(c.Id)).Any(p => p.Id == casePartyId)) return Fail(SubrogationErrors.Payer);
            await s.SetResponseAsync(casePartyId, response, on);
            var t = SubrogationRules.Totals(Plain(await s.MovementsAsync(c.Id)));
            if (response != SubrogationResponse.Accepted && t.Confirmed == 0 && c.Status != CaseStatus.Closed)
                await s.SetStatusAsync(c.Id, response == SubrogationResponse.Disputed ? CaseStatus.Disputed : CaseStatus.Rejected);
            return SubrogationResult.Done(casePartyId);
        });

    private static MovementKind KindOf(DocumentedMovement kind) => kind switch
    {
        DocumentedMovement.Claimed => MovementKind.Claimed,
        DocumentedMovement.Confirmed => MovementKind.Confirmed,
        _ => MovementKind.Cancelled,
    };

    private static IReadOnlyList<DocumentKind> DocumentsOf(DocumentedMovement kind) => kind switch
    {
        DocumentedMovement.Claimed => Claimed,
        DocumentedMovement.Confirmed => Confirmed,
        _ => Cancelled,
    };

    /// <summary>The claim, its confirmation, or its cancellation by a court (steps 3, 4, 6).</summary>
    public static Task<SubrogationResult> AddMovementAsync(SubrogationTransact transact, Guid caseId, DocumentedMovement kind, BigInteger amount, DateOnly bookedOn,
        DocumentKind documentKind, string? documentNumber, DateOnly? documentDate, string? note, Guid requestKey) =>
        transact(async s =>
        {
            var c = await LockedCaseAsync(s, caseId);
            if (c is null) return Fail(SubrogationErrors.Missing);
            if (await s.MovementByKeyAsync(requestKey) is not null) return Fail(SubrogationErrors.Duplicate);
            if (!DocumentsOf(kind).Contains(documentKind)) return Fail(SubrogationErrors.Document);
            var court = documentKind == DocumentKind.CourtDecision && !string.IsNullOrEmpty(documentNumber) && documentDate is { } dd ? new CourtDocument(documentNumber, dd) : null;
            var movements = Plain(await s.MovementsAsync(c.Id));
            var next = NewMovement.Original(KindOf(kind), amount, bookedOn, court);
            if (SubrogationRules.CheckMovement(movements, next, await ClaimMoneyAsync(s, c)) is { } error) return Fail(Code(error));
            var id = await s.InsertMovementAsync(new MovementRow(c.Id, KindOf(kind), amount, bookedOn, null, null, null, documentKind, documentNumber, documentDate, note, requestKey));
            await SettleStatusAsync(s, c, [.. movements, SubrogationRules.AsMovement(id.ToString(), next, movements)]);
            return SubrogationResult.Done(id);
        });

    /// <summary>
    /// Money received from the liable person or their insurer (step 5): one claim_recovery row on the case's own claim and
    /// its 'received' movement, in the same transaction — either both are kept or neither (3.4, 3.4ბ). The report counts the
    /// claim_recovery row only, so the money counts once.
    /// </summary>
    public static Task<SubrogationResult> ReceiveAsync(SubrogationTransact transact, Guid caseId, Guid payerId, BigInteger amount, DateOnly receivedOn,
        string? bankReference, string? note, Guid requestKey) =>
        transact(async s =>
        {
            var c = await LockedCaseAsync(s, caseId);
            if (c is null) return Fail(SubrogationErrors.Missing);
            if (await s.MovementByKeyAsync(requestKey) is not null) return Fail(SubrogationErrors.Duplicate);
            var reference = SubrogationChecks.BankReference(bankReference);
            if (reference is not null && await s.RecoveryByReferenceAsync(c.Id, reference) is not null) return Fail(SubrogationErrors.Duplicate);
            if (!(await s.PartiesAsync(c.Id)).Any(p => p.Id == payerId)) return Fail(SubrogationErrors.Payer);
            var movements = Plain(await s.MovementsAsync(c.Id));
            var next = NewMovement.Original(MovementKind.Received, amount, receivedOn);
            if (SubrogationRules.CheckMovement(movements, next, await ClaimMoneyAsync(s, c)) is { } error) return Fail(Code(error));
            var id = await s.RecordCaseMoneyAsync(
                new NewRecovery(c.ClaimId, c.Id, "receipt", null, receivedOn, amount, reference, note),
                new MovementRow(c.Id, MovementKind.Received, amount, receivedOn, null, null, payerId, DocumentKind.BankStatement, reference, receivedOn, note, requestKey));
            await SettleStatusAsync(s, c, [.. movements, SubrogationRules.AsMovement(id.ToString(), next, movements)]);
            return SubrogationResult.Done(id);
        });

    /// <summary>
    /// Undoes one movement by its mirror (3.4ა), dated in an open month on or after the original. A receipt is undone in the
    /// money too: a claim_recovery reversal of exactly its amount, on the same claim and case, written with the movement in
    /// one call. The original rows stay as they are.
    /// </summary>
    public static Task<SubrogationResult> ReverseAsync(SubrogationTransact transact, Guid caseId, Guid movementId, DateOnly bookedOn, string reason, Guid requestKey) =>
        transact(async s =>
        {
            var c = await LockedCaseAsync(s, caseId);
            if (c is null) return Fail(SubrogationErrors.Missing);
            if (await s.MovementByKeyAsync(requestKey) is not null) return Fail(SubrogationErrors.Duplicate);
            if (SubrogationChecks.IsBlank(reason)) return Fail(SubrogationErrors.Document);
            var stored = await s.MovementsAsync(c.Id);
            var movements = Plain(stored);
            var next = NewMovement.Reversal(movementId.ToString(), bookedOn);
            if (SubrogationRules.CheckMovement(movements, next, await ClaimMoneyAsync(s, c)) is { } error) return Fail(Code(error));
            var target = stored.First(m => m.Id == movementId.ToString());
            var why = JsText.Trim(reason);
            Guid id;
            if (target.Kind == MovementKind.Received)
            {
                if (target.ClaimRecoveryId is not { } recovery) return Fail(Code(MovementError.Target));
                id = await s.RecordCaseMoneyAsync(
                    new NewRecovery(c.ClaimId, c.Id, "reversal", recovery, bookedOn, -target.Amount, null, why),
                    new MovementRow(c.Id, MovementKind.Reversal, -target.Amount, bookedOn, movementId, null, null, DocumentKind.Correction, null, bookedOn, why, requestKey));
            }
            else
            {
                id = await s.InsertMovementAsync(new MovementRow(c.Id, MovementKind.Reversal, -target.Amount, bookedOn, movementId, null, null, DocumentKind.Correction, null, bookedOn, why, requestKey));
            }
            await SettleStatusAsync(s, c, [.. movements, SubrogationRules.AsMovement(id.ToString(), next, movements)]);
            return SubrogationResult.Done(id);
        });

    private static List<Movement> Plain(IReadOnlyList<StoredMovement> stored) => stored.Select(m => m.AsMovement).ToList();

    /// <summary>
    /// The case as it stands once its claim is locked (H83). The claim of a case never changes, so the first read only finds
    /// which claim to lock; everything decided afterwards — the status above all — comes from the second read, made under
    /// the lock, so an operation that committed in between is seen. Every writer of a case's money or status takes this lock
    /// first, in this order.
    /// </summary>
    private static async Task<CaseRow?> LockedCaseAsync(ISubrogationStore s, Guid caseId)
    {
        var first = await s.GetCaseAsync(caseId);
        if (first is null) return null;
        await s.LockClaimAsync(first.ClaimId);
        return await s.GetCaseAsync(caseId);
    }

    private static async Task<ClaimMoney> ClaimMoneyAsync(ISubrogationStore s, CaseRow c) =>
        new(await s.PaymentsAsync(c.ClaimId), await s.OtherRecoveriesAsync(c.ClaimId, c.Id));

    private static async Task SettleStatusAsync(ISubrogationStore s, CaseRow c, IReadOnlyList<Movement> movements)
    {
        var status = SubrogationRules.StatusAfterMoney(c.Status, SubrogationRules.Totals(movements));
        if (status != c.Status) await s.SetStatusAsync(c.Id, status);
    }
}
