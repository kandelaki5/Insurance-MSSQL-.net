using System.Text.Json;
using System.Text.Json.Nodes;
using Insurance.Application.Data;
using Insurance.Domain.Common;
using Insurance.Domain.Compliance;

namespace Insurance.Application.Services;

/// <summary>
/// Database side of the client risk assessment: gathers the facts the pure rules need and records the result in the
/// assessment history and on the party (legacy src/lib/client-risk.ts).
/// </summary>
public static class PartyRisk
{
    private sealed record PartyRow(
        string Kind, bool IsResident, string CountryCode, bool IsPep, bool IsPublicEntity, decimal? StateSharePercent,
        DateOnly? VerifiedAt, DateOnly? SanctionsCheckedAt);

    /// <summary>Facts of one party as of a date, including its written premium this year and last.</summary>
    public static async Task<ClientFacts?> LoadClientFactsAsync(Tx db, Guid partyId, DateOnly asOf)
    {
        var p = await db.QuerySingleOrDefaultAsync<PartyRow>("""
            select kind as Kind, is_resident as IsResident, country_code as CountryCode, is_pep as IsPep, is_public_entity as IsPublicEntity,
                   state_share_percent as StateSharePercent, verified_at as VerifiedAt, sanctions_checked_at as SanctionsCheckedAt
            from core.party where id = @partyId
            """, new { partyId });
        if (p is null) return null;
        var yearStart = new DateOnly(asOf.Year, 1, 1);
        var previousStart = new DateOnly(asOf.Year - 1, 1, 1);
        var premium = await db.QuerySingleOrDefaultAsync<(decimal Current, decimal Previous)>("""
            select coalesce(sum(case when t.booked_on >= @yearStart and t.booked_on <= @asOf then t.amount_gel end), 0),
                   coalesce(sum(case when t.booked_on >= @previousStart and t.booked_on < @yearStart then t.amount_gel end), 0)
            from core.premium_transaction t
            join core.policy_line l on l.id = t.policy_line_id
            join core.policy po on po.id = l.policy_id
            join core.contract c on c.id = po.contract_id
            where c.policyholder_id = @partyId
            """, new { yearStart, previousStart, asOf, partyId });
        return new ClientFacts(
            p.Kind == "organization" ? ClientKind.Organization : ClientKind.Person,
            p.IsResident,
            p.CountryCode,
            p.IsPep,
            p.IsPublicEntity,
            p.StateSharePercent,
            p.VerifiedAt,
            p.SanctionsCheckedAt,
            premium.Current,
            premium.Previous);
    }

    public sealed record Computed(ClientFacts Facts, RiskAssessment Assessment);

    public static async Task<Computed?> ComputePartyRiskAsync(Tx db, Guid partyId, DateOnly asOf)
    {
        var facts = await LoadClientFactsAsync(db, partyId, asOf);
        if (facts is null) return null;
        return new Computed(facts, ClientRisk.AssessClientRisk(facts, asOf, ClientRisk.RiskPolicyFromSettings(await Settings.LoadSettingsAsync(db))));
    }

    /// <summary>The factors as the history keeps them: [{"code":"pep","points":3,"decisive":true}, …].</summary>
    public static string FactorsJson(IEnumerable<RiskFactor> factors) =>
        new JsonArray(factors.Select(f => (JsonNode)new JsonObject
        {
            ["code"] = f.Code.ToSnakeCode(),
            ["points"] = f.Points,
            ["decisive"] = f.Decisive,
        }).ToArray()).ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>The codes of the factors kept with an assessment, in their order.</summary>
    public static IReadOnlyList<string> FactorCodes(string factorsJson) =>
        JsonNode.Parse(factorsJson) is JsonArray a ? a.Select(n => n?["code"]?.GetValue<string>() ?? "").ToList() : [];

    public sealed record Override(RiskLevel Level, string Reason);

    /// <summary>
    /// Records an assessment: the computed result, or a manual level with its reason. The party's current level and
    /// review date follow the latest record.
    /// </summary>
    public static async Task RecordPartyRiskAsync(Tx tx, Guid partyId, Guid userId, DateOnly asOf, RiskAssessment computed, Override? manual)
    {
        var level = manual?.Level ?? computed.Level;
        var reviewDueOn = manual is not null
            ? ClientRisk.ReviewDueFor(manual.Level, asOf, ClientRisk.RiskPolicyFromSettings(await Settings.LoadSettingsAsync(tx)))
            : computed.ReviewDueOn;
        await tx.ExecuteAsync("""
            insert into core.party_risk_assessment (id, party_id, assessed_on, computed_level, level, score, factors, is_override, reason, review_due_on, assessed_by)
            values (@id, @partyId, @asOf, @computedLevel, @level, @score, @factors, @isOverride, @reason, @reviewDueOn, @userId)
            """, new
        {
            id = Guid.NewGuid(), partyId, asOf, computedLevel = computed.Level.ToSnakeCode(), level = level.ToSnakeCode(), score = computed.Score,
            factors = FactorsJson(computed.Factors), isOverride = manual is not null, reason = manual?.Reason, reviewDueOn, userId,
        });
        await tx.ExecuteAsync("""
            update core.party set risk_level = @level, risk_assessed_on = @asOf, risk_review_on = @reviewDueOn, updated_at = sysdatetimeoffset()
            where id = @partyId
            """, new { level = level.ToSnakeCode(), asOf, reviewDueOn, partyId });
    }
}
