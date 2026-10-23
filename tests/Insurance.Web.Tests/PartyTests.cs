using System.Net;
using Dapper;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests;

/// <summary>The client register and the party form over HTTP (legacy parties/page.tsx, new/page.tsx, saveParty).</summary>
public sealed class PartyTests(WebApp app) : IClassFixture<WebApp>
{
    private static Dictionary<string, string> Person(string taxId, Dictionary<string, string>? extra = null)
    {
        var form = new Dictionary<string, string>
        {
            ["id"] = "", ["kind"] = "person", ["last_name"] = " ბერიძე ", ["first_name"] = "ნინო", ["tax_id"] = taxId, ["birth_date"] = "1990-05-04",
            ["gender"] = "female", ["is_resident"] = "on", ["foreign_id"] = "", ["country_code"] = "ge", ["registration_place"] = "",
            ["legal_address"] = "თბილისი", ["actual_address"] = "", ["phone_code"] = "", ["phone"] = "599 12 34 56", ["email"] = "",
            ["identified_at"] = "", ["verified_at"] = "", ["sanctions_checked_at"] = "",
        };
        foreach (var (k, v) in extra ?? []) form[k] = v;
        return form;
    }

    [Fact]
    public async Task A_person_is_created_with_the_tax_id_checked_the_phone_normalised_and_the_audit_naming_the_user()
    {
        var (client, userId) = await app.SignedInAsync("sales");
        var taxId = Fixtures.RandomTaxId();

        var refused = await WebApp.PostFormAsync(client, "/parties/new?handler=Save", Person(taxId[..10]), "/parties/new?kind=person");
        var html = await Html.BodyAsync(refused);
        Assert.Contains(Ka.Party.Errors.TaxIdPerson, html);
        Assert.Contains("value=\"ბერიძე\"", html); // the values come back as typed, trimmed

        var badPhone = await WebApp.PostFormAsync(client, "/parties/new?handler=Save", Person(taxId, new() { ["phone"] = "12" }), "/parties/new");
        Assert.Contains(Ka.Party.Errors.Phone, await Html.BodyAsync(badPhone));
        var badDate = await WebApp.PostFormAsync(client, "/parties/new?handler=Save", Person(taxId, new() { ["birth_date"] = "19900-05-04" }), "/parties/new");
        Assert.Contains(Ka.Party.Errors.Date, await Html.BodyAsync(badDate));

        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party where tax_id = @taxId", new { taxId }));

        var saved = await WebApp.PostFormAsync(client, "/parties/new?handler=Save", Person(taxId), "/parties/new");
        var location = Html.Location(saved);
        Assert.Matches("^/parties/[0-9a-f-]{36}\\?saved=1$", location);
        var row = await c.QuerySingleAsync<(Guid Id, string LastName, string Phone, string Country, string Segment, string Gender)>(
            "select id, last_name, phone, country_code, segment, gender from core.party where tax_id = @taxId", new { taxId });
        Assert.Equal(("ბერიძე", "+995 599123456", "GE", "retail", "female"), (row.LastName, row.Phone, row.Country, row.Segment, row.Gender));
        Assert.Equal($"/parties/{row.Id}?saved=1", location);
        Assert.Equal(userId, await c.ExecuteScalarAsync<Guid>("select user_id from core.audit_log where table_name = N'party' and row_id = @id and action = N'insert'", new { id = row.Id.ToString() }));

        var card = await Html.GetAsync(client, location);
        Assert.Contains(Ka.Party.Saved, card);
        Assert.Contains("ბერიძე ნინო", card);
        Assert.Contains(Ka.Party.Segment["retail"], card);

        // The same personal number again is refused as a duplicate.
        var duplicate = await WebApp.PostFormAsync(client, "/parties/new?handler=Save", Person(taxId), "/parties/new");
        Assert.Contains(Ka.Party.Errors.Duplicate, await Html.BodyAsync(duplicate));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.party where tax_id = @taxId", new { taxId }));
    }

    [Fact]
    public async Task An_organization_needs_nine_digits_and_a_share_up_to_100_and_a_state_share_makes_it_a_state_segment()
    {
        var (client, _) = await app.SignedInAsync("underwriter");
        var code = Fixtures.RandomTaxId()[..9];
        var form = new Dictionary<string, string>
        {
            ["id"] = "", ["kind"] = "organization", ["name"] = "შპს სატესტო", ["tax_id"] = code + "1", ["legal_form"] = "შპს",
            ["state_share_percent"] = "150", ["is_resident"] = "on", ["country_code"] = "", ["self_declaration_on"] = "2026-01-10",
        };
        var refused = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/parties/new?handler=Save", form, "/parties/new?kind=organization"));
        Assert.Contains(Ka.Party.Errors.TaxIdOrganization, refused);
        Assert.Contains(Ka.Party.Errors.StateShare, refused);

        form["tax_id"] = code;
        form["state_share_percent"] = "60";
        var location = Html.Location(await WebApp.PostFormAsync(client, "/parties/new?handler=Save", form, "/parties/new?kind=organization"));
        await using var c = await app.Db.OpenAdminAsync();
        var row = await c.QuerySingleAsync<(string Segment, decimal Share, DateTime SelfDeclaration, string Country)>(
            "select segment, state_share_percent, self_declaration_on, country_code from core.party where tax_id = @code", new { code });
        Assert.Equal(("state", 60.00m, new DateTime(2026, 1, 10), "GE"), row);
        var card = await Html.GetAsync(client, location);
        Assert.Contains(Ka.Party.Segment["state"], card);
        Assert.Contains(Ka.Party.Owners.Title, card); // organizations have the beneficial owners' section
    }

    [Fact]
    public async Task A_non_resident_needs_an_identifier_and_an_edit_on_the_card_is_kept_in_its_history()
    {
        var (client, _) = await app.SignedInAsync("sales");
        var form = Person("", new() { ["is_resident"] = "", ["tax_id"] = "" });
        form.Remove("is_resident");
        var refused = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/parties/new?handler=Save", form, "/parties/new"));
        Assert.Contains(Ka.Party.Errors.ForeignId, refused);

        var passport = "P" + Guid.NewGuid().ToString("N")[..8];
        form["foreign_id"] = passport;
        form["country_code"] = "deu";
        var location = Html.Location(await WebApp.PostFormAsync(client, "/parties/new?handler=Save", form, "/parties/new"));
        var id = location.Split('/', '?')[2];

        var edit = new Dictionary<string, string>(form) { ["id"] = id, ["phone"] = "555 00 11 22", ["is_pep"] = "on" };
        var again = Html.Location(await WebApp.PostFormAsync(client, $"/parties/{id}?handler=Save", edit, $"/parties/{id}"));
        Assert.Equal($"/parties/{id}?saved=1", again);
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(("DE", false, true, "+995 555001122"), await c.QuerySingleAsync<(string, bool, bool, string)>(
            "select country_code, is_resident, is_pep, phone from core.party where id = @id", new { id }));
        var card = Html.Text(await Html.GetAsync(client, again));
        Assert.Contains($"{Ka.Party.Fields["phone"]}: +995 599123456 → +995 555001122", card);
        Assert.Contains($"{Ka.Party.Fields["isPep"]}: {Ka.Common.No} → {Ka.Common.Yes}", card);
    }

    [Fact]
    public async Task The_viewer_sees_the_form_read_only_and_a_post_writes_nothing()
    {
        var (client, _) = await app.SignedInAsync("viewer");
        var list = await Html.GetAsync(client, "/parties");
        Assert.DoesNotContain("/parties/new?kind=person", list);
        var taxId = Fixtures.RandomTaxId();
        var response = await WebApp.PostFormAsync(client, "/parties/new?handler=Save", Person(taxId), "/parties/new");
        Assert.Contains(Ka.Common.ReadOnly, await Html.BodyAsync(response));
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party where tax_id = @taxId", new { taxId }));
    }

    [Fact]
    public async Task The_list_searches_by_name_and_number_and_filters_unrated_and_reviews_due()
    {
        var (client, _) = await app.SignedInAsync("viewer");
        var tag = "ძიება" + Guid.NewGuid().ToString("N")[..6];
        var unrated = Fixtures.RandomTaxId();
        var due = Fixtures.RandomTaxId();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("""
            insert core.party (kind, tax_id, first_name, last_name) values (N'person', @unrated, N'ა', @tag);
            insert core.party (kind, tax_id, first_name, last_name, risk_level, risk_review_on) values (N'person', @due, N'ბ', @tag, N'low', '2020-01-01');
            """, new { unrated, due, tag });
        var all = await Html.GetAsync(client, $"/parties?q={Uri.EscapeDataString(tag.ToUpperInvariant())}");
        Assert.Contains(unrated, all);
        Assert.Contains(due, all);
        Assert.Contains(Ka.Party.RiskCard.Overdue, all);
        var byNumber = await Html.GetAsync(client, $"/parties?q={due}");
        Assert.Contains(due, byNumber);
        Assert.DoesNotContain(unrated, byNumber);
        var onlyUnrated = await Html.GetAsync(client, $"/parties?q={Uri.EscapeDataString(tag)}&filter=unrated");
        Assert.Contains(unrated, onlyUnrated);
        Assert.DoesNotContain(due, onlyUnrated);
        var reviewDue = await Html.GetAsync(client, $"/parties?q={Uri.EscapeDataString(tag)}&filter=reviewDue");
        Assert.Contains(due, reviewDue);
        Assert.DoesNotContain(unrated, reviewDue);
        // An unknown filter is dropped (all).
        var unknown = await Html.GetAsync(client, $"/parties?q={Uri.EscapeDataString(tag)}&filter=nonsense");
        Assert.Contains(unrated, unknown);
        Assert.Contains(due, unknown);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/parties/not-a-uuid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/parties/{Guid.NewGuid()}")).StatusCode);
    }
}
