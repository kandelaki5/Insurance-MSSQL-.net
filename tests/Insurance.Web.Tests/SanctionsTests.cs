using System.Net;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insurance.Web.Tests;

/// <summary>
/// Sanctions screening over HTTP (legacy refreshUnList, screenEveryone, screenOneParty, decideScreening). The UN list is a
/// local file in the list's XML format, given to the application as its list address: the network is never used.
/// </summary>
public sealed class SanctionsTests(WebApp app) : IClassFixture<WebApp>
{
    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync(WebApplicationFactory<Program> factory, string role)
    {
        var (id, username, password) = await app.UserAsync(role);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password })).StatusCode);
        return (client, id);
    }

    private WebApplicationFactory<Program> WithList(string url) =>
        app.Factory.WithWebHostBuilder(b => b.UseSetting("Insurance:Sanctions:UnListUrl", url));

    [Fact]
    public async Task The_list_is_loaded_from_its_address_everyone_is_screened_and_a_hit_is_dismissed_confirmed_and_lifted()
    {
        var file = ComplianceFixtures.UnListFile();
        await using var factory = WithList(new Uri(file).AbsoluteUri);
        var (client, userId) = await SignedInAsync(factory, "accountant");
        var page = await Html.GetAsync(client, "/compliance/sanctions");
        Assert.Contains(SanctionsTexts.List.NotLoaded, page);
        Assert.DoesNotContain("handler=ScreenAll", page);

        var hit = Guid.NewGuid();
        var clean = Guid.NewGuid();
        var org = Guid.NewGuid();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("""
            insert core.party (id, kind, first_name, last_name, tax_id, birth_date) values (@hit, N'person', N'ზურაბ', N'სანქციონელი', @t1, '1980-05-01');
            insert core.party (id, kind, first_name, last_name, tax_id) values (@clean, N'person', N'მარიამ', N'სუფთაშვილი', @t2);
            insert core.party (id, kind, name, tax_id) values (@org, N'organization', N'შპს მფლობელი', @t3);
            insert core.party_beneficial_owner (party_id, first_name, last_name, criterion, share_percent, document_number) values (@org, N'Eric', N'Badege', N'share', 60, N'AB123456');
            """, new { hit, clean, org, t1 = Fixtures.RandomTaxId(), t2 = Fixtures.RandomTaxId(), t3 = Fixtures.RandomTaxId()[..9] });

        Assert.Equal("/compliance/sanctions?loaded=3", Html.Location(await WebApp.PostFormAsync(client, "/compliance/sanctions?handler=Refresh", [], "/compliance/sanctions")));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("select count(*) from core.sanction_list_entry where source = N'un'"));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 23, 0, 5, 487, TimeSpan.Zero), await c.ExecuteScalarAsync<DateTimeOffset>("select generated_at from core.sanction_list_version where source = N'un'"));
        page = Html.Text(await Html.GetAsync(client, "/compliance/sanctions?loaded=3"));
        Assert.Contains($"{SanctionsTexts.List.Loaded}: 3 {SanctionsTexts.List.Entries}", page);
        Assert.Contains("03.10.2026 03:00", page); // generated 23:00 UTC is 03:00 next day in Tbilisi

        var subjects = await c.ExecuteScalarAsync<int>("select (select count(*) from core.party) + (select count(*) from core.party_beneficial_owner where valid_to is null)");
        Assert.Equal($"/compliance/sanctions?screened={subjects}&hits=2", Html.Location(await WebApp.PostFormAsync(client, "/compliance/sanctions?handler=ScreenAll", [], "/compliance/sanctions")));
        var today = Rates.TodayInTbilisi().ToDateTime(TimeOnly.MinValue);
        Assert.Equal(today, await c.ExecuteScalarAsync<DateTime?>("select sanctions_checked_at from core.party where id = @clean", new { clean }));
        Assert.Null(await c.ExecuteScalarAsync<DateTime?>("select sanctions_checked_at from core.party where id = @hit", new { hit }));
        var screening = await c.QuerySingleAsync<(Guid Id, string Matches, Guid By)>("select id, matches, created_by from core.sanctions_screening where subject_id = @hit", new { hit });
        Assert.Equal(userId, screening.By);
        Assert.Equal("""[{"dataId":"1","name":"Zurab Sankcioneli","reference":"T.001","listType":"Test","score":1,"matchedName":"Zurabi Sanktsioneli","strong":true,"reasons":["name","birth_year"]}]""", screening.Matches);
        Assert.Equal("possible", await c.ExecuteScalarAsync<string>("select outcome from core.sanctions_screening s join core.party_beneficial_owner o on o.id = s.subject_id where o.party_id = @org", new { org }));
        page = Html.Text(await Html.GetAsync(client, "/compliance/sanctions"));
        Assert.Contains($"Zurab Sankcioneli (T.001, Test) · {SanctionsTexts.Fields.ListedAs}: Zurabi Sanktsioneli · {SanctionsTexts.Fields.Score}: 100% · {SanctionsTexts.Fields.Reasons}: {SanctionsTexts.Reasons["name"]}, {SanctionsTexts.Reasons["birth_year"]} {SanctionsTexts.Strong}", page);
        Assert.Contains($"{SanctionsTexts.SubjectKinds["owner"]}: Badege Eric", page);

        // No reason: nothing is decided.
        await WebApp.PostFormAsync(client, "/compliance/sanctions?handler=DecideScreening",
            new() { ["id"] = screening.Id.ToString(), ["back"] = "/compliance/sanctions", ["decision"] = "dismiss", ["note"] = " " }, "/compliance/sanctions");
        Assert.Equal("possible", await c.ExecuteScalarAsync<string>("select outcome from core.sanctions_screening where id = @id", new { id = screening.Id }));
        Assert.Equal("/compliance/sanctions", Html.Location(await WebApp.PostFormAsync(client, "/compliance/sanctions?handler=DecideScreening",
            new() { ["id"] = screening.Id.ToString(), ["back"] = "/compliance/sanctions", ["decision"] = "dismiss", ["note"] = "სხვა პირი, სხვა დაბადების თარიღი" }, "/compliance/sanctions")));
        Assert.Equal(("false_positive", userId, "სხვა პირი, სხვა დაბადების თარიღი"), await c.QuerySingleAsync<(string, Guid, string)>(
            "select outcome, decided_by, note from core.sanctions_screening where id = @id", new { id = screening.Id }));
        Assert.Equal(today, await c.ExecuteScalarAsync<DateTime?>("select sanctions_checked_at from core.party where id = @hit", new { hit }));

        // Screening the client again from its card: as in the legacy program, the dismissed hit is flagged again.
        var card = $"/parties/{hit}";
        Assert.Equal($"{card}?screened=1&hits=1", Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=Screen", new() { ["party_id"] = hit.ToString() }, card)));
        var again = await c.ExecuteScalarAsync<Guid>("select top (1) id from core.sanctions_screening where subject_id = @hit and outcome = N'possible'", new { hit });
        Assert.Contains("alert warn", await Html.GetAsync(client, $"{card}?screened=1&hits=1"));

        // Confirmed: the client is blocked; then the block is lifted. A return address to another site is not followed.
        Assert.Equal(card, Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=DecideScreening",
            new() { ["id"] = again.ToString(), ["back"] = card, ["decision"] = "confirm", ["note"] = "დადასტურდა" }, card)));
        Assert.True(await c.ExecuteScalarAsync<bool>("select sanctions_blocked from core.party where id = @hit", new { hit }));
        Assert.Contains(SanctionsTexts.Blocked, await Html.GetAsync(client, card));
        Assert.Equal("/compliance/sanctions", Html.Location(await WebApp.PostFormAsync(client, $"{card}?handler=DecideScreening",
            new() { ["id"] = again.ToString(), ["back"] = "//elsewhere.example", ["decision"] = "lift", ["note"] = "ფმს-მა მოხსნა" }, card)));
        Assert.Equal((false, "false_positive"), await c.QuerySingleAsync<(bool, string)>(
            "select p.sanctions_blocked, s.outcome from core.party p join core.sanctions_screening s on s.party_id = p.id where s.id = @again", new { again }));

        // A list that cannot be read keeps the stored one.
        await using var broken = WithList(new Uri(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.xml")).AbsoluteUri);
        var (other, _) = await SignedInAsync(broken, "admin");
        Assert.Equal("/compliance/sanctions?loaded=failed", Html.Location(await WebApp.PostFormAsync(other, "/compliance/sanctions?handler=Refresh", [], "/compliance/sanctions")));
        Assert.Contains(SanctionsTexts.List.LoadFailed, await Html.GetAsync(other, "/compliance/sanctions?loaded=failed"));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("select count(*) from core.sanction_list_entry where source = N'un'"));
        File.Delete(file);
    }

    [Fact]
    public async Task The_viewer_may_open_the_page_as_before_but_loads_screens_and_decides_nothing()
    {
        var file = ComplianceFixtures.UnListFile("2026-11-01T00:00:00.000Z");
        await using var factory = WithList(new Uri(file).AbsoluteUri);
        var (client, _) = await SignedInAsync(factory, "viewer");
        var page = await Html.GetAsync(client, "/compliance/sanctions");
        Assert.DoesNotContain("handler=Refresh", page);
        await using var c = await app.Db.OpenAdminAsync();
        var before = await c.ExecuteScalarAsync<int>("select count(*) from core.sanctions_screening");
        var versions = await c.ExecuteScalarAsync<int>("select count(*) from core.sanction_list_version where generated_at = '2026-11-01T00:00:00Z'");
        Assert.Equal(HttpStatusCode.OK, (await WebApp.PostFormAsync(client, "/compliance/sanctions?handler=Refresh", [], "/compliance/sanctions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WebApp.PostFormAsync(client, "/compliance/sanctions?handler=ScreenAll", [], "/compliance/sanctions")).StatusCode);
        Assert.Equal(before, await c.ExecuteScalarAsync<int>("select count(*) from core.sanctions_screening"));
        Assert.Equal(versions, await c.ExecuteScalarAsync<int>("select count(*) from core.sanction_list_version where generated_at = '2026-11-01T00:00:00Z'"));
        File.Delete(file);
    }
}
