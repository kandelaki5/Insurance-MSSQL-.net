using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests;

/// <summary>The client card's own forms over HTTP: beneficial owners, the risk assessment and KYC (legacy parties/[id]).</summary>
public sealed class PartyCardTests(WebApp app) : IClassFixture<WebApp>
{
    private async Task<Guid> OrganizationAsync()
    {
        var id = Guid.NewGuid();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("insert core.party (id, kind, name, tax_id) values (@id, N'organization', N'შპს მფლობელები', @taxId)", new { id, taxId = Fixtures.RandomTaxId()[..9] });
        return id;
    }

    private async Task<Guid> PersonAsync()
    {
        var id = Guid.NewGuid();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("insert core.party (id, kind, first_name, last_name, tax_id) values (@id, N'person', N'ლევან', N'რისკიანი', @taxId)", new { id, taxId = Fixtures.RandomTaxId() });
        return id;
    }

    private static Dictionary<string, string> Owner(Guid partyId, string share, string criterion = "share") => new()
    {
        ["party_id"] = partyId.ToString(), ["last_name"] = "მესაკუთრე", ["first_name"] = "თამარ", ["birth_date"] = "1975-03-02",
        ["personal_number"] = "01001010101", ["citizenship"] = "geo", ["document_number"] = "13AB45678", ["document_country"] = "ge",
        ["criterion"] = criterion, ["share_percent"] = share, ["identified_at"] = "2026-01-05", ["is_nominee"] = "on",
    };

    [Fact]
    public async Task A_beneficial_owner_is_added_with_the_share_rule_then_verified_and_ended()
    {
        var (client, userId) = await app.SignedInAsync("underwriter");
        var party = await OrganizationAsync();
        var card = await Html.GetAsync(client, $"/parties/{party}");
        Assert.Contains(Ka.Party.Kyc.Issues["no_beneficial_owner"], card);
        Assert.Contains(Ka.Party.Owners.None, card);

        var below = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Owner", Owner(party, "20"), $"/parties/{party}"));
        Assert.Contains(Ka.Party.Owners.ShareBelowThreshold, below);
        Assert.Contains("value=\"მესაკუთრე\"", below);
        var over = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Owner", Owner(party, "120", "control"), $"/parties/{party}"));
        Assert.Contains(Ka.Party.Owners.ShareError, over);
        var badDate = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Owner",
            new Dictionary<string, string>(Owner(party, "30")) { ["document_valid_to"] = "20300-01-01" }, $"/parties/{party}"));
        Assert.Contains(Ka.Party.Errors.Date, badDate);
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party_beneficial_owner where party_id = @party", new { party }));

        Assert.Equal($"/parties/{party}", Html.Location(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Owner", Owner(party, "30.555"), $"/parties/{party}")));
        var owner = await c.QuerySingleAsync<(Guid Id, decimal Share, string Citizenship, string Country, bool IsDirect, bool IsNominee, bool IsPep, Guid CreatedBy)>(
            "select id, share_percent, citizenship, document_country, is_direct, is_nominee, is_pep, created_by from core.party_beneficial_owner where party_id = @party", new { party });
        // JavaScript's (30.555).toFixed(2) is "30.55": the double just below 30.555.
        Assert.Equal((30.55m, "GE", "GE", true, true, false, userId), (owner.Share, owner.Citizenship, owner.Country, owner.IsDirect, owner.IsNominee, owner.IsPep, owner.CreatedBy));

        card = await Html.GetAsync(client, $"/parties/{party}");
        Assert.Contains("მესაკუთრე თამარ", card);
        Assert.DoesNotContain(Ka.Party.Kyc.Issues["no_beneficial_owner"], card);

        var verified = await WebApp.PostFormAsync(client, $"/parties/{party}?handler=OwnerUpdate",
            new() { ["id"] = owner.Id.ToString(), ["party_id"] = party.ToString(), ["action"] = "verify", ["verification_source"] = " რეესტრი " }, $"/parties/{party}");
        Assert.Equal($"/parties/{party}", Html.Location(verified));
        var today = Rates.TodayInTbilisi();
        Assert.Equal((today.ToDateTime(TimeOnly.MinValue), "რეესტრი"), await c.QuerySingleAsync<(DateTime, string)>(
            "select verified_at, verification_source from core.party_beneficial_owner where id = @id", new { id = owner.Id }));
        Assert.Contains($"{Insurance.Web.Infrastructure.Fmt.Date(today)} · რეესტრი", await Html.GetAsync(client, $"/parties/{party}"));

        await WebApp.PostFormAsync(client, $"/parties/{party}?handler=OwnerUpdate",
            new() { ["id"] = owner.Id.ToString(), ["party_id"] = party.ToString(), ["action"] = "remove" }, $"/parties/{party}");
        Assert.Equal(today.ToDateTime(TimeOnly.MinValue), await c.ExecuteScalarAsync<DateTime>("select valid_to from core.party_beneficial_owner where id = @id", new { id = owner.Id }));
        card = await Html.GetAsync(client, $"/parties/{party}");
        Assert.DoesNotContain("მესაკუთრე თამარ", card);
        Assert.Contains(Ka.Party.Kyc.Issues["no_beneficial_owner"], card);
    }

    [Fact]
    public async Task The_risk_is_confirmed_as_computed_or_overridden_only_with_a_reason_and_the_history_keeps_both()
    {
        var (client, userId) = await app.SignedInAsync("sales");
        var party = await PersonAsync();
        var card = Html.Text(await Html.GetAsync(client, $"/parties/{party}"));
        // Not verified (+1) and never screened (+2): score 3, medium.
        Assert.Contains($"{Ka.Party.RiskCard.Computed} {Ka.Party.Risk["medium"]} ({Ka.Party.RiskCard.Score}: 3)", card);
        Assert.Contains(Ka.Party.RiskCard.FactorNames["sanctions_not_checked"] + " (+2)", card);

        var same = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Risk",
            new() { ["party_id"] = party.ToString(), ["mode"] = "override", ["level"] = "medium", ["reason"] = "x" }, $"/parties/{party}"));
        Assert.Contains($"<div class=\"alert error\">{Ka.Party.RiskCard.SameAsComputed}</div>", same);
        var noReason = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Risk",
            new() { ["party_id"] = party.ToString(), ["mode"] = "override", ["level"] = "high", ["reason"] = "  " }, $"/parties/{party}"));
        // The legacy form showed a missing reason in the "ok" colour (its class follows the level field only).
        Assert.Contains($"<div class=\"alert ok\">{Ka.Party.RiskCard.ReasonRequired}</div>", noReason);
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party_risk_assessment where party_id = @party", new { party }));

        var today = Rates.TodayInTbilisi();
        Assert.Equal($"/parties/{party}", Html.Location(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Risk",
            new() { ["party_id"] = party.ToString(), ["mode"] = "accept" }, $"/parties/{party}")));
        Assert.Equal(("medium", today.AddMonths(24).ToDateTime(TimeOnly.MinValue)), await c.QuerySingleAsync<(string, DateTime)>(
            "select risk_level, risk_review_on from core.party where id = @party", new { party }));

        Assert.Equal($"/parties/{party}", Html.Location(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Risk",
            new() { ["party_id"] = party.ToString(), ["mode"] = "override", ["level"] = "high", ["reason"] = " ცნობილი ინციდენტი " }, $"/parties/{party}")));
        var rows = (await c.QueryAsync<(string Computed, string Level, int Score, bool IsOverride, string? Reason, DateTime Review, Guid By, string Factors)>("""
            select computed_level, level, score, is_override, reason, review_due_on, assessed_by, factors from core.party_risk_assessment
            where party_id = @party order by created_at
            """, new { party })).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("medium", "medium", 3, false, (string?)null), (rows[0].Computed, rows[0].Level, rows[0].Score, rows[0].IsOverride, rows[0].Reason));
        Assert.Equal(("medium", "high", true, "ცნობილი ინციდენტი", today.AddMonths(12).ToDateTime(TimeOnly.MinValue), userId),
            (rows[1].Computed, rows[1].Level, rows[1].IsOverride, rows[1].Reason, rows[1].Review, rows[1].By));
        Assert.Equal("""[{"code":"not_verified","points":1,"decisive":false},{"code":"sanctions_not_checked","points":2,"decisive":false}]""", rows[1].Factors);
        Assert.Equal("high", await c.ExecuteScalarAsync<string>("select risk_level from core.party where id = @party", new { party }));
        card = Html.Text(await Html.GetAsync(client, $"/parties/{party}"));
        Assert.Contains($"{Ka.Party.Risk["high"]} {Ka.Party.RiskCard.Manual}", card);
        Assert.Contains($"{Ka.Party.Risk["medium"]} {Ka.Party.RiskCard.ByRules}", card);
        Assert.Contains("ცნობილი ინციდენტი", card);
    }

    [Fact]
    public async Task The_viewer_writes_no_owner_and_no_assessment()
    {
        var (client, _) = await app.SignedInAsync("viewer");
        var party = await OrganizationAsync();
        var card = await Html.GetAsync(client, $"/parties/{party}");
        Assert.DoesNotContain("handler=Owner", card);
        Assert.DoesNotContain("handler=Risk", card);
        await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Owner", Owner(party, "50"), $"/parties/{party}");
        var risk = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/parties/{party}?handler=Risk", new() { ["party_id"] = party.ToString(), ["mode"] = "accept" }, $"/parties/{party}"));
        Assert.Contains(Ka.Party.RiskCard.Title, risk);
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party_beneficial_owner where party_id = @party", new { party }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party_risk_assessment where party_id = @party", new { party }));
        // Not a compliance role: no screening or suspension section.
        Assert.DoesNotContain(SanctionsTexts.Screen.One, card);
        Assert.DoesNotContain(AmlTexts.Suspension.Title, card);
    }
}
