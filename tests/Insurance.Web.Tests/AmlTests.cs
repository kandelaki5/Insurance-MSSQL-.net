using System.Net;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Compliance;
using Insurance.Integration.Tests;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Tests;

/// <summary>AML cases and FMS suspensions over HTTP (legacy registerCase, decideCase, createSuspension, updateSuspension).</summary>
public sealed class AmlTests(WebApp app) : IClassFixture<WebApp>
{
    private async Task<(Guid Id, string TaxId)> ClientAsync()
    {
        var id = Guid.NewGuid();
        var taxId = Fixtures.RandomTaxId();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("insert core.party (id, kind, first_name, last_name, tax_id) values (@id, N'person', N'ეკა', N'შემოწმებული', @taxId)", new { id, taxId });
        return (id, taxId);
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("underwriter")]
    [InlineData("claims")]
    [InlineData("viewer")]
    public async Task Roles_outside_compliance_are_turned_away_from_the_AML_pages_and_their_posts_write_nothing(string role)
    {
        var (client, _) = await app.SignedInAsync(role);
        Assert.Equal("/", Html.Location(await client.GetAsync("/compliance/aml-cases")));
        Assert.Equal("/", Html.Location(await client.GetAsync("/compliance/aml-report")));
        Assert.Equal("/", Html.Location(await client.GetAsync($"/compliance/aml-cases/{Guid.NewGuid()}")));
        Assert.Equal("/compliance/aml-cases", Html.Location(await client.GetAsync("/compliance/aml-cases/new")));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/compliance/aml-report/xlsx?year=2026&half=1")).StatusCode);
        var menu = await Html.GetAsync(client, "/");
        Assert.DoesNotContain("href=\"/compliance/aml-cases\"", menu);

        // A suspension posted from the client's card by this role is not registered.
        var (party, _) = await ClientAsync();
        var card = await Html.GetAsync(client, $"/parties/{party}");
        Assert.DoesNotContain(AmlTexts.Suspension.Create, card);
        await WebApp.PostFormAsync(client, $"/parties/{party}?handler=CreateSuspension",
            new() { ["party_id"] = party.ToString(), ["kind"] = "written", ["scope"] = "all", ["instructed_at"] = "", ["back"] = $"/parties/{party}" }, $"/parties/{party}");
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.fms_suspension where party_id = @party", new { party }));
    }

    [Fact]
    public async Task A_case_is_registered_with_its_checks_and_decided_with_the_report_fields()
    {
        var (client, userId) = await app.SignedInAsync("operations_head");
        var (party, taxId) = await ClientAsync();
        var tomorrow = Fmt.Iso(Rates.TodayInTbilisi().AddDays(1));
        var refused = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/compliance/aml-cases/new",
            new() { ["party_id_number"] = "99999999999", ["opened_on"] = tomorrow, ["source"] = "x", ["description"] = " " }, "/compliance/aml-cases/new"));
        Assert.Contains(AmlTexts.Cases.Errors.Party, refused);
        Assert.Contains(AmlTexts.Cases.Errors.OpenedOn, refused);
        Assert.Contains(AmlTexts.Cases.Errors.Description, refused);

        var prefilled = await Html.GetAsync(client, $"/compliance/aml-cases/new?party={taxId}");
        Assert.Contains($"value=\"{taxId}\"", prefilled);
        var location = Html.Location(await WebApp.PostFormAsync(client, "/compliance/aml-cases/new",
            new() { ["party_id_number"] = taxId, ["opened_on"] = "2026-03-10", ["source"] = "employee", ["class_code"] = "5", ["transaction_ref"] = "CAS-1", ["description"] = "ნაღდი 50 000" },
            "/compliance/aml-cases/new"));
        Assert.Matches("^/compliance/aml-cases/[0-9a-f-]{36}\\?registered=1$", location);
        var id = location.Split('/', '?')[3];
        await using var c = await app.Db.OpenAdminAsync();
        var row = await c.QuerySingleAsync<(string Number, Guid Party, string Source, string Status, string ClassCode, Guid By)>(
            "select number, party_id, source, status, class_code, created_by from core.aml_case where id = @id", new { id });
        Assert.Matches("^AML[0-9]{6}$", row.Number);
        Assert.Equal((party, "employee", "open", "5", userId), (row.Party, row.Source, row.Status, row.ClassCode, row.By));
        Assert.Contains(AmlTexts.Cases.Registered, await Html.GetAsync(client, location));

        var url = $"/compliance/aml-cases/{id}?handler=Decide";
        var page = $"/compliance/aml-cases/{id}";
        var noExamination = await Html.BodyAsync(await WebApp.PostFormAsync(client, url, new() { ["id"] = id, ["decision"] = "examine", ["examination"] = "" }, page));
        Assert.Contains(AmlTexts.Cases.Errors.Examination, noExamination);
        var noStr = await Html.BodyAsync(await WebApp.PostFormAsync(client, url,
            new() { ["id"] = id, ["decision"] = "report", ["examination"] = "საეჭვოა", ["suspicion_at"] = "2026-03-10T10:00", ["str_sent_at"] = "", ["str_type"] = "other" }, page));
        Assert.Contains(AmlTexts.Cases.Errors.Str, noStr);
        Assert.Contains(AmlTexts.Cases.Errors.OtherBasis, noStr);
        Assert.Equal("open", await c.ExecuteScalarAsync<string>("select status from core.aml_case where id = @id", new { id }));

        var reported = await Html.BodyAsync(await WebApp.PostFormAsync(client, url, new()
        {
            ["id"] = id, ["decision"] = "report", ["examined_on"] = "2026-03-11", ["examination"] = "საეჭვოა", ["suspicion_at"] = "2026-03-10T10:00",
            ["str_sent_at"] = "2026-03-12T09:30", ["str_type"] = "ml", ["str_other_basis"] = "", ["fms_reference"] = "FMS-7", ["note"] = "",
        }, page));
        Assert.Contains($"<div class=\"alert ok\">{AmlTexts.Cases.Saved}</div>", reported);
        Assert.Contains(AmlTexts.Cases.LateStr, reported);
        var stored = await c.QuerySingleAsync<(string Status, DateTime Examined, DateTimeOffset Suspicion, DateTimeOffset Sent, string Type, string Reference)>(
            "select status, examined_on, suspicion_at, str_sent_at, str_type, fms_reference from core.aml_case where id = @id", new { id });
        Assert.Equal(("reported", new DateTime(2026, 3, 11), "ml", "FMS-7"), (stored.Status, stored.Examined, stored.Type, stored.Reference));
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 6, 0, 0, TimeSpan.Zero), stored.Suspicion.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 3, 12, 5, 30, 0, TimeSpan.Zero), stored.Sent.ToUniversalTime());
        var list = Html.Text(await Html.GetAsync(client, "/compliance/aml-cases?status=reported"));
        Assert.Contains($"{row.Number} 10.03.2026", list);
        Assert.Contains("12.03.2026 09:30", list);

        var closed = await Html.BodyAsync(await WebApp.PostFormAsync(client, url, new() { ["id"] = id, ["decision"] = "close", ["note"] = "დასრულდა" }, page));
        Assert.DoesNotContain("handler=Decide", closed);
        Assert.Equal(("closed", Rates.TodayInTbilisi().ToDateTime(TimeOnly.MinValue), "დასრულდა"), await c.QuerySingleAsync<(string, DateTime, string)>(
            "select status, closed_on, note from core.aml_case where id = @id", new { id }));
    }

    [Fact]
    public async Task Suspensions_are_registered_for_all_or_one_own_transaction_and_confirmed_lifted_and_attached()
    {
        var (client, userId) = await app.SignedInAsync("accountant");
        var policy = await app.Db.App.AsUserAsync(userId, tx => Fixtures.ActivePolicyAsync(tx, userId, new DateOnly(2026, 1, 15)));
        var claim = await app.Db.App.AsUserAsync(userId, tx => ComplianceFixtures.ClaimAsync(tx, policy));
        var other = await app.Db.App.AsUserAsync(userId, tx => Fixtures.ActivePolicyAsync(tx, userId, new DateOnly(2026, 1, 15)));
        await using var c = await app.Db.OpenAdminAsync();
        var policyNumber = await c.ExecuteScalarAsync<string>("select number from core.policy where id = @id", new { id = policy.PolicyId });
        var otherNumber = await c.ExecuteScalarAsync<string>("select number from core.policy where id = @id", new { id = other.PolicyId });
        var party = policy.PartyId;
        var card = $"/parties/{party}";
        Dictionary<string, string> Form(string kind, string scope, string reference = "", string instructed = "2026-10-20T10:00") => new()
        {
            ["party_id"] = party.ToString(), ["back"] = card, ["instructed_at"] = instructed, ["kind"] = kind, ["scope"] = scope, ["transaction_ref"] = reference, ["note"] = " ფმს-ის წერილი ",
        };

        // Another client's policy number: nothing is saved and the card says why.
        Assert.Equal($"{card}?suspension=target", Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=CreateSuspension", Form("written", "transaction", otherNumber!), card)));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.fms_suspension where party_id = @party", new { party }));
        Assert.Contains(AmlTexts.Suspension.TargetError, await Html.GetAsync(client, $"{card}?suspension=target"));
        // An unknown number too.
        Assert.Equal($"{card}?suspension=target", Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=CreateSuspension", Form("written", "transaction", "NO-SUCH-1"), card)));

        // The client's own policy, typed in small letters with spaces: resolved to the policy, stored with its number.
        Assert.Equal(card, Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=CreateSuspension", Form("written", "transaction", $"  {policyNumber!.ToLowerInvariant()} "), card)));
        var target = await c.QuerySingleAsync<(Guid? Policy, Guid? Claim, string Ref, string Note, DateTimeOffset Instructed, DateTimeOffset Confirmed, DateTimeOffset Ends, Guid By)>(
            "select target_policy_id, target_claim_id, transaction_ref, note, instructed_at, written_confirmed_at, ends_at, created_by from core.fms_suspension where party_id = @party and scope = N'transaction'", new { party });
        Assert.Equal((policy.PolicyId, (Guid?)null, policyNumber, "ფმს-ის წერილი", userId), (target.Policy, target.Claim, target.Ref, target.Note, target.By));
        var instructed = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(4));
        Assert.Equal(instructed, target.Instructed);
        Assert.Equal(instructed, target.Confirmed);
        Assert.Equal(Suspension.SuspensionEnd(instructed), target.Ends);

        // The claim's number targets the claim.
        Assert.Equal(card, Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=CreateSuspension", Form("oral", "transaction", claim.Number), card)));
        Assert.Equal(claim.ClaimId, await c.ExecuteScalarAsync<Guid?>("select target_claim_id from core.fms_suspension where party_id = @party and kind = N'oral'", new { party }));

        // All of the client's operations, an oral instruction given now: in force, awaiting the letter.
        Assert.Equal(card, Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=CreateSuspension", Form("oral", "all", instructed: Aml.TbilisiNowLocal()), card)));
        var all = await c.ExecuteScalarAsync<Guid>("select id from core.fms_suspension where party_id = @party and scope = N'all'", new { party });
        var html = await Html.GetAsync(client, card);
        Assert.Contains(AmlTexts.Suspension.Active, html);
        Assert.Contains(AmlTexts.Suspension.States["awaiting"], html);
        var register = await Html.GetAsync(client, "/compliance/aml-cases");
        Assert.Contains(AmlTexts.Suspension.InForceTitle, register);
        Assert.Contains($"href=\"/parties/{party}\"", register);

        // Confirmed in writing, then lifted with a reason (an empty reason lifts nothing).
        await WebApp.PostFormAsync(client, $"{card}?handler=UpdateSuspension", new() { ["id"] = all.ToString(), ["back"] = card, ["action"] = "confirm" }, card);
        Assert.NotNull(await c.ExecuteScalarAsync<DateTimeOffset?>("select written_confirmed_at from core.fms_suspension where id = @all", new { all }));
        await WebApp.PostFormAsync(client, $"{card}?handler=UpdateSuspension", new() { ["id"] = all.ToString(), ["back"] = card, ["action"] = "lift", ["lift_reason"] = " " }, card);
        Assert.Null(await c.ExecuteScalarAsync<DateTimeOffset?>("select lifted_at from core.fms_suspension where id = @all", new { all }));
        // A return address outside this system is not followed.
        Assert.Equal("/compliance/aml-cases", Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=UpdateSuspension",
            new() { ["id"] = all.ToString(), ["back"] = "https://elsewhere.example/x", ["action"] = "lift", ["lift_reason"] = "ფმს-ის გადაწყვეტილება" }, card)));
        Assert.Equal("ფმს-ის გადაწყვეტილება", await c.ExecuteScalarAsync<string>("select lift_reason from core.fms_suspension where id = @all and lifted_at is not null", new { all }));

        // An old unmapped suspension is attached to the client's claim from the register; the mark is cleared.
        var legacy = await ComplianceFixtures.LegacyUnmappedSuspensionAsync(app.Db, party, "PAY-LEGACY-1");
        Assert.Contains(AmlTexts.Suspension.LegacyUnmapped, await Html.GetAsync(client, "/compliance/aml-cases"));
        Assert.Equal("/compliance/aml-cases?suspension=target", Html.Location(await WebApp.PostFormAsync(client, "/compliance/aml-cases?handler=UpdateSuspension",
            new() { ["id"] = legacy.ToString(), ["back"] = "/compliance/aml-cases", ["action"] = "attach", ["attach_ref"] = otherNumber! }, "/compliance/aml-cases")));
        Assert.Equal("/compliance/aml-cases", Html.Location(await WebApp.PostFormAsync(client, "/compliance/aml-cases?handler=UpdateSuspension",
            new() { ["id"] = legacy.ToString(), ["back"] = "/compliance/aml-cases", ["action"] = "attach", ["attach_ref"] = claim.Number.ToLowerInvariant() }, "/compliance/aml-cases")));
        Assert.Equal((claim.ClaimId, claim.Number, false), await c.QuerySingleAsync<(Guid?, string, bool)>(
            "select target_claim_id, transaction_ref, legacy_unmapped from core.fms_suspension where id = @legacy", new { legacy }));
    }

    [Fact]
    public async Task A_suspension_registered_on_a_case_is_linked_to_it()
    {
        var (client, _) = await app.SignedInAsync("admin");
        var (party, taxId) = await ClientAsync();
        var caseId = Html.Location(await WebApp.PostFormAsync(client, "/compliance/aml-cases/new",
            new() { ["party_id_number"] = taxId, ["opened_on"] = Fmt.Iso(Rates.TodayInTbilisi()), ["source"] = "fms_request", ["description"] = "ფმს-ის მოთხოვნა" }, "/compliance/aml-cases/new")).Split('/', '?')[3];
        var page = $"/compliance/aml-cases/{caseId}";
        Assert.Equal(page, Html.Location(await WebApp.PostFormAsync(client, $"{page}?handler=CreateSuspension",
            new() { ["party_id"] = party.ToString(), ["case_id"] = caseId, ["back"] = page, ["instructed_at"] = "", ["kind"] = "written", ["scope"] = "all" }, page)));
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(Guid.Parse(caseId), await c.ExecuteScalarAsync<Guid>("select case_id from core.fms_suspension where party_id = @party", new { party }));
        var html = await Html.GetAsync(client, page);
        Assert.Contains(AmlTexts.Suspension.States["active"], html);
    }
}
