using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using SubrogationRules = Insurance.Domain.Finance.Subrogation;

namespace Insurance.Application.Services;

/// <summary>A case as the operations read it.</summary>
public sealed record CaseRow(Guid Id, string Number, Guid ClaimId, CaseStatus Status);

/// <param name="Limit">The liability insurer's limit in tetri; null when not known.</param>
public sealed record CaseParty(Guid Id, Guid CaseId, Guid PartyId, Domain.Common.PartyRole Role, BigInteger? Limit, SubrogationResponse? Response);

/// <summary>A movement of the case with the money row of a receipt.</summary>
public sealed record StoredMovement(string Id, MovementKind Kind, BigInteger Amount, DateOnly BookedOn, string? ReversesId, Guid? ClaimRecoveryId)
{
    public Movement AsMovement => new(Id, Kind, Amount, BookedOn, ReversesId);
}

public sealed record NewCase(string Number, Guid ClaimId, DateOnly OpenedOn, string? Note, Guid RequestKey);

/// <summary>A money row of a case: a receipt, or the reversal of one (amount negative).</summary>
public sealed record NewRecovery(Guid ClaimId, Guid CaseId, string RowKind, Guid? ReversesId, DateOnly ReceivedOn, BigInteger Amount, string? BankReference, string? Note);

public sealed record MovementRow(Guid CaseId, MovementKind Kind, BigInteger Amount, DateOnly BookedOn, Guid? ReversesId, Guid? ClaimRecoveryId, Guid? PayerId,
    DocumentKind DocumentKind, string? DocumentNumber, DateOnly? DocumentDate, string? Note, Guid RequestKey);

/// <summary>
/// Reads and writes of one transaction for the regress operations (legacy SubrogationStore of src/lib/subrogation.ts).
/// Every method runs inside the transaction it was given in. The legacy store wrote a receipt's money row and its
/// movement with two inserts, checked together at commit (the deferred SG034); here they are one call,
/// <see cref="RecordCaseMoneyAsync"/>, through core.record_case_money (database/README.md, difference 1).
/// </summary>
public interface ISubrogationStore
{
    /// <summary>Serialises all regress of one claim: the same lock the regress triggers take (H81).</summary>
    Task LockClaimAsync(Guid claimId);
    Task<ClaimFacts?> ClaimFactsAsync(Guid claimId);
    Task<bool> HasCaseAsync(Guid claimId);
    Task<Guid?> CaseByKeyAsync(Guid requestKey);
    Task<string?> NextNumberAsync();
    Task<Guid> InsertCaseAsync(NewCase row);
    Task<CaseRow?> GetCaseAsync(Guid caseId);
    Task SetStatusAsync(Guid caseId, CaseStatus status);
    Task SetEvidenceAsync(Guid caseId, BasisKind kind, string number, DateOnly date);
    Task<IReadOnlyList<CaseParty>> PartiesAsync(Guid caseId);
    Task<Guid> InsertPartyAsync(Guid caseId, Guid partyId, Domain.Common.PartyRole role, string? policyNumber, string? coverage, long? limit);
    Task SetResponseAsync(Guid casePartyId, SubrogationResponse response, DateOnly on);
    Task<IReadOnlyList<StoredMovement>> MovementsAsync(Guid caseId);
    Task<IReadOnlyList<ClaimPayment>> PaymentsAsync(Guid claimId);
    /// <summary>The claim's subrogation money outside this case: old rows from before cases (H81). Never salvage.</summary>
    Task<IReadOnlyList<OtherRecovery>> OtherRecoveriesAsync(Guid claimId, Guid caseId);
    Task<Guid?> MovementByKeyAsync(Guid requestKey);
    Task<Guid?> RecoveryByReferenceAsync(Guid caseId, string bankReference);
    /// <summary>A movement without money (claimed, confirmed, cancelled, or the reversal of one of them).</summary>
    Task<Guid> InsertMovementAsync(MovementRow row);
    /// <summary>A money row and its movement together (a receipt, or the reversal of one): both are kept or neither. Returns the movement's id.</summary>
    Task<Guid> RecordCaseMoneyAsync(NewRecovery recovery, MovementRow movement);
}

/// <summary>The SQL Server side of the regress operations (legacy src/lib/subrogation-store.ts): each method is one statement in the given transaction.</summary>
public sealed class SqlSubrogationStore(Tx tx) : ISubrogationStore
{
    private static BigInteger T(decimal amount) => Billing.Tetri(amount);

    private static decimal D(BigInteger tetri) => decimal.Parse(SubrogationRules.FromTetri(tetri), System.Globalization.CultureInfo.InvariantCulture);

    public Task LockClaimAsync(Guid claimId) => tx.ExecuteAsync("exec core.lock_subrogation_claim @claimId", new { claimId });

    public async Task<ClaimFacts?> ClaimFactsAsync(Guid claimId)
    {
        var row = await tx.QuerySingleOrDefaultAsync<(string ClassCode, string? CoverageCode)?>("""
            select l.class_code, pc.code
            from core.claim c join core.policy_line l on l.id = c.policy_line_id
            left join core.product_coverage pc on pc.id = l.product_coverage_id
            where c.id = @claimId
            """, new { claimId });
        return row is { } r ? new ClaimFacts(r.ClassCode, r.CoverageCode) : null;
    }

    public async Task<bool> HasCaseAsync(Guid claimId) =>
        await tx.ExecuteScalarAsync<int?>("select 1 from core.subrogation_case where claim_id = @claimId", new { claimId }) is not null;

    public Task<Guid?> CaseByKeyAsync(Guid requestKey) =>
        tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.subrogation_case where request_key = @requestKey", new { requestKey });

    public Task<string?> NextNumberAsync() => tx.NextNumberAsync("subrogation");

    public async Task<Guid> InsertCaseAsync(NewCase c)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.subrogation_case (id, number, claim_id, opened_on, note, request_key) values (@id, @number, @claimId, @openedOn, @note, @requestKey)
            """, new { id, number = c.Number, claimId = c.ClaimId, openedOn = c.OpenedOn, note = c.Note, requestKey = c.RequestKey });
        return id;
    }

    public async Task<CaseRow?> GetCaseAsync(Guid caseId)
    {
        var row = await tx.QuerySingleOrDefaultAsync<(Guid Id, string Number, Guid ClaimId, string Status)?>(
            "select id, number, claim_id, status from core.subrogation_case where id = @caseId", new { caseId });
        return row is { } r ? new CaseRow(r.Id, r.Number, r.ClaimId, Codes.Parse<CaseStatus>(r.Status)) : null;
    }

    public Task SetStatusAsync(Guid caseId, CaseStatus status) =>
        tx.ExecuteAsync("update core.subrogation_case set status = @status where id = @caseId", new { status = Codes.Of(status), caseId });

    public Task SetEvidenceAsync(Guid caseId, BasisKind kind, string number, DateOnly date) =>
        tx.ExecuteAsync("update core.subrogation_case set basis_kind = @kind, basis_number = @number, basis_date = @date where id = @caseId",
            new { kind = Codes.Of(kind), number, date, caseId });

    public async Task<IReadOnlyList<CaseParty>> PartiesAsync(Guid caseId) =>
        (await tx.QueryAsync<(Guid Id, Guid CaseId, Guid PartyId, string Role, decimal? LimitGel, string? Response)>(
            "select id, case_id, party_id, role, limit_gel, response from core.subrogation_party where case_id = @caseId order by created_at", new { caseId }))
        .Select(r => new CaseParty(r.Id, r.CaseId, r.PartyId, Codes.Parse<Domain.Common.PartyRole>(r.Role), r.LimitGel is { } l ? T(l) : null,
            r.Response is { } s ? Codes.Parse<SubrogationResponse>(s) : null))
        .ToList();

    public async Task<Guid> InsertPartyAsync(Guid caseId, Guid partyId, Domain.Common.PartyRole role, string? policyNumber, string? coverage, long? limit)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.subrogation_party (id, case_id, party_id, role, policy_number, coverage, limit_gel)
            values (@id, @caseId, @partyId, @role, @policyNumber, @coverage, @limit)
            """, new { id, caseId, partyId, role = Codes.Of(role), policyNumber, coverage, limit = limit is { } l ? D(l) : (decimal?)null });
        return id;
    }

    public Task SetResponseAsync(Guid casePartyId, SubrogationResponse response, DateOnly on) =>
        tx.ExecuteAsync("update core.subrogation_party set response = @response, response_on = @on where id = @casePartyId",
            new { response = Codes.Of(response), on, casePartyId });

    public async Task<IReadOnlyList<StoredMovement>> MovementsAsync(Guid caseId) =>
        (await tx.QueryAsync<(Guid Id, string Kind, decimal AmountGel, DateOnly BookedOn, Guid? ReversesMovementId, Guid? ClaimRecoveryId)>("""
            select id, kind, amount_gel, booked_on, reverses_movement_id, claim_recovery_id
            from core.subrogation_movement where case_id = @caseId order by created_at, lower(convert(nchar(36), id)) collate Latin1_General_BIN2
            """, new { caseId }))
        .Select(r => new StoredMovement(r.Id.ToString(), Codes.Parse<MovementKind>(r.Kind), T(r.AmountGel), r.BookedOn, r.ReversesMovementId?.ToString(), r.ClaimRecoveryId))
        .ToList();

    public async Task<IReadOnlyList<ClaimPayment>> PaymentsAsync(Guid claimId) =>
        (await tx.QueryAsync<(decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaidGel, decimal OffsetGel, DateOnly PaidOn)>("""
            select cp.amount, cp.deductible_withheld, cp.premium_offset, cp.net_paid_gel, coalesce(p.amount_gel, 0), cp.paid_on
            from core.claim_payment cp left join core.payment p on p.id = cp.offset_payment_id
            where cp.claim_id = @claimId
            """, new { claimId }))
        .Select(r => new ClaimPayment(T(r.Amount), T(r.DeductibleWithheld), T(r.PremiumOffset), T(r.NetPaidGel), T(r.OffsetGel), r.PaidOn))
        .ToList();

    public async Task<IReadOnlyList<OtherRecovery>> OtherRecoveriesAsync(Guid claimId, Guid caseId) =>
        (await tx.QueryAsync<(decimal AmountGel, DateOnly ReceivedOn)>("""
            select amount_gel, received_on from core.claim_recovery
            where claim_id = @claimId and kind = N'subrogation' and (subrogation_case_id is null or subrogation_case_id <> @caseId)
            """, new { claimId, caseId }))
        .Select(r => new OtherRecovery(T(r.AmountGel), r.ReceivedOn))
        .ToList();

    public Task<Guid?> MovementByKeyAsync(Guid requestKey) =>
        tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.subrogation_movement where request_key = @requestKey", new { requestKey });

    public Task<Guid?> RecoveryByReferenceAsync(Guid caseId, string bankReference) =>
        tx.QueryFirstOrDefaultAsync<Guid?>("select id from core.claim_recovery where subrogation_case_id = @caseId and bank_reference = @bankReference",
            new { caseId, bankReference });

    private static object MovementParameters(Guid id, MovementRow m) => new
    {
        id, caseId = m.CaseId, kind = Codes.Of(m.Kind), bookedOn = m.BookedOn, amount = D(m.Amount), reverses = m.ReversesId, recovery = m.ClaimRecoveryId,
        payer = m.PayerId, documentKind = Codes.Of(m.DocumentKind), documentNumber = m.DocumentNumber, documentDate = m.DocumentDate, note = m.Note, requestKey = m.RequestKey,
    };

    public async Task<Guid> InsertMovementAsync(MovementRow m)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.subrogation_movement (id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id,
                                                   document_kind, document_number, document_date, note, request_key)
            values (@id, @caseId, @kind, @bookedOn, @amount, @reverses, @recovery, @payer, @documentKind, @documentNumber, @documentDate, @note, @requestKey)
            """, MovementParameters(id, m));
        return id;
    }

    public async Task<Guid> RecordCaseMoneyAsync(NewRecovery r, MovementRow m)
    {
        var recoveryId = Guid.NewGuid();
        var movementId = Guid.NewGuid();
        await tx.ExecuteAsync("""
            exec core.record_case_money @recovery_id = @recoveryId, @claim_id = @claimId, @case_id = @caseId, @row_kind = @rowKind,
                 @reverses_recovery_id = @reversesRecovery, @received_on = @receivedOn, @amount_gel = @amount, @bank_reference = @bankReference,
                 @recovery_note = @recoveryNote, @created_by = @createdBy,
                 @movement_id = @movementId, @movement_kind = @movementKind, @reverses_movement_id = @reversesMovement, @payer_id = @payer,
                 @document_kind = @documentKind, @document_number = @documentNumber, @document_date = @documentDate, @movement_note = @movementNote,
                 @request_key = @requestKey
            """, new
        {
            recoveryId, claimId = r.ClaimId, caseId = r.CaseId, rowKind = r.RowKind, reversesRecovery = r.ReversesId, receivedOn = r.ReceivedOn, amount = D(r.Amount),
            bankReference = r.BankReference, recoveryNote = r.Note,
            // Who wrote the money row: the actor of the transaction (legacy current_setting('core.user_id')).
            createdBy = await tx.ExecuteScalarAsync<Guid?>("select core.current_actor()"),
            movementId, movementKind = Codes.Of(m.Kind), reversesMovement = m.ReversesId, payer = m.PayerId, documentKind = Codes.Of(m.DocumentKind),
            documentNumber = m.DocumentNumber, documentDate = m.DocumentDate, movementNote = m.Note, requestKey = m.RequestKey,
        });
        return movementId;
    }
}
