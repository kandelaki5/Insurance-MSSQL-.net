using Insurance.Application.Data;
using Insurance.Domain.Compliance;

namespace Insurance.Application.Services;

/// <summary>Database side of customer due diligence: gathers what the KYC rules need per client (legacy src/lib/kyc.ts).</summary>
public static class PartyKyc
{
    public sealed record OwnerRow(
        Guid Id, string FirstName, string LastName, DateOnly? BirthDate, string? PersonalNumber, string? DocumentNumber, string? DocumentCountry,
        DateOnly? DocumentValidTo, string? Citizenship, string Criterion, decimal? SharePercent, bool IsDirect, bool IsNominee, bool IsPep,
        DateOnly? IdentifiedAt, DateOnly? VerifiedAt, string? VerificationSource, DateOnly? SanctionsCheckedAt);

    private sealed record PartyKycRow(
        Guid Id, string Kind, DateOnly? IdentifiedAt, DateOnly? VerifiedAt, DateOnly? SelfDeclarationOn, DateOnly? RelationshipFrom,
        decimal AnnualPremiumGel, bool HasLife);

    private sealed record OwnerDates(Guid PartyId, DateOnly? IdentifiedAt, DateOnly? VerifiedAt);

    private static async Task<List<PartyKycRow>> PartyRowsAsync(Tx db, DateOnly asOf, Guid? partyId = null)
    {
        var yearStart = new DateOnly(asOf.Year, 1, 1);
        return (await db.QueryAsync<PartyKycRow>("""
            select p.id as Id, p.kind as Kind, p.identified_at as IdentifiedAt, p.verified_at as VerifiedAt, p.self_declaration_on as SelfDeclarationOn,
                   (select min(po.effective_from) from core.contract c join core.policy po on po.contract_id = c.id
                    where c.policyholder_id = p.id and po.status in (N'active', N'cancelled')) as RelationshipFrom,
                   (select coalesce(sum(t.amount_gel), 0)
                    from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                    join core.insurance_class ic on ic.code = l.class_code
                    join core.policy po on po.id = l.policy_id join core.contract c on c.id = po.contract_id
                    where c.policyholder_id = p.id and ic.category = N'non_life' and t.booked_on >= @yearStart and t.booked_on <= @asOf) as AnnualPremiumGel,
                   convert(bit, case when exists (
                       select 1 from core.policy_line l join core.insurance_class ic on ic.code = l.class_code
                       join core.policy po on po.id = l.policy_id join core.contract c on c.id = po.contract_id
                       where c.policyholder_id = p.id and ic.category = N'life' and po.status = N'active') then 1 else 0 end) as HasLife
            from core.party p
            where @partyId is null or p.id = @partyId
            """, new { yearStart, asOf, partyId })).ToList();
    }

    /// <summary>The client's beneficial owners on record now (not ended), in the order they were recorded.</summary>
    public static async Task<IReadOnlyList<OwnerRow>> CurrentOwnersAsync(Tx db, Guid partyId) =>
        (await db.QueryAsync<OwnerRow>("""
            select id as Id, first_name as FirstName, last_name as LastName, birth_date as BirthDate, personal_number as PersonalNumber,
                   document_number as DocumentNumber, document_country as DocumentCountry, document_valid_to as DocumentValidTo,
                   citizenship as Citizenship, criterion as Criterion, share_percent as SharePercent, is_direct as IsDirect, is_nominee as IsNominee,
                   is_pep as IsPep, identified_at as IdentifiedAt, verified_at as VerifiedAt, verification_source as VerificationSource,
                   sanctions_checked_at as SanctionsCheckedAt
            from core.party_beneficial_owner where party_id = @partyId and valid_to is null order by created_at, id
            """, new { partyId })).ToList();

    private static KycInput ToInput(PartyKycRow row, IEnumerable<KycOwner> owners) => new(
        row.Kind == "organization" ? ClientKind.Organization : ClientKind.Person,
        row.AnnualPremiumGel,
        row.HasLife,
        row.IdentifiedAt,
        row.VerifiedAt,
        row.RelationshipFrom,
        owners.ToList(),
        row.SelfDeclarationOn);

    public sealed record Result(KycStatus Status, KycInput Input, IReadOnlyList<OwnerRow> Owners);

    /// <summary>The client's due-diligence status as of a date, or null when there is no such client.</summary>
    public static async Task<Result?> LoadKycStatusAsync(Tx db, Guid partyId, DateOnly asOf)
    {
        var row = (await PartyRowsAsync(db, asOf, partyId)).FirstOrDefault();
        if (row is null) return null;
        var owners = await CurrentOwnersAsync(db, partyId);
        var input = ToInput(row, owners.Select(o => new KycOwner(o.VerifiedAt, o.IdentifiedAt)));
        return new Result(Kyc.KycStatus(input, asOf), input, owners);
    }

    /// <summary>Clients whose due diligence is incomplete as of a date, for the reminders.</summary>
    public static async Task<int> PartiesWithKycIssuesAsync(Tx db, DateOnly asOf)
    {
        var rows = await PartyRowsAsync(db, asOf);
        var owners = (await db.QueryAsync<OwnerDates>(
            "select party_id as PartyId, identified_at as IdentifiedAt, verified_at as VerifiedAt from core.party_beneficial_owner where valid_to is null"))
            .GroupBy(o => o.PartyId).ToDictionary(g => g.Key, g => g.Select(o => new KycOwner(o.VerifiedAt, o.IdentifiedAt)).ToList());
        return rows.Count(row => Kyc.KycStatus(ToInput(row, owners.GetValueOrDefault(row.Id) ?? []), asOf).Issues.Count > 0);
    }
}
