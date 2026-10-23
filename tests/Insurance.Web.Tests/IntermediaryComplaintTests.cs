using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Tests;

/// <summary>Intermediaries and complaints over HTTP (legacy saveIntermediary; registerComplaint, acknowledgeComplaint, respondComplaint).</summary>
public sealed class IntermediaryComplaintTests(WebApp app) : IClassFixture<WebApp>
{
    private async Task<(Guid Id, string TaxId)> PersonAsync(string? phone = null)
    {
        var id = Guid.NewGuid();
        var taxId = Fixtures.RandomTaxId();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("insert core.party (id, kind, first_name, last_name, tax_id, phone) values (@id, N'person', N'ანა', N'შუამავალი', @taxId, @phone)", new { id, taxId, phone });
        return (id, taxId);
    }

    [Fact]
    public async Task An_intermediary_must_be_a_registered_party_with_a_type_and_a_commission_up_to_100()
    {
        var (client, _) = await app.SignedInAsync("accountant");
        var (partyId, taxId) = await PersonAsync();
        var refused = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/intermediaries/new?handler=Save",
            new() { ["id"] = "", ["party_id_number"] = "00000000000", ["kind"] = "", ["commission_percent"] = "101", ["status"] = "active" }, "/intermediaries/new"));
        Assert.Contains(Ka.Intermediary.Errors.PartyNotFound, refused);
        Assert.Contains(Ka.Intermediary.Errors.Kind, refused);
        Assert.Contains(Ka.Intermediary.Errors.Commission, refused);

        var location = Html.Location(await WebApp.PostFormAsync(client, "/intermediaries/new?handler=Save",
            new() { ["id"] = "", ["party_id_number"] = taxId, ["kind"] = "broker", ["contract_no"] = "B-1", ["contract_date"] = "2026-02-01", ["valid_to"] = "", ["commission_percent"] = "12.5", ["status"] = "active" },
            "/intermediaries/new"));
        Assert.Matches("^/intermediaries/[0-9a-f-]{36}\\?saved=1$", location);
        var id = location.Split('/', '?')[2];
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal((partyId, "broker", "B-1", 12.50m, "active"), await c.QuerySingleAsync<(Guid, string, string, decimal, string)>(
            "select party_id, kind, contract_no, commission_percent, status from core.intermediary where id = @id", new { id }));
        var card = await Html.GetAsync(client, location);
        Assert.Contains(Ka.Party.Saved, card);
        Assert.Contains("value=\"12.50\"", card);

        // On the card the party is fixed; the rest changes.
        Assert.Equal($"/intermediaries/{id}?saved=1", Html.Location(await WebApp.PostFormAsync(client, $"/intermediaries/{id}?handler=Save",
            new() { ["id"] = id, ["kind"] = "agent", ["contract_no"] = "", ["contract_date"] = "2026-02-01", ["valid_to"] = "2027-01-31", ["commission_percent"] = "", ["status"] = "terminated" }, $"/intermediaries/{id}")));
        Assert.Equal(("agent", (string?)null, (decimal?)null, "terminated", new DateTime(2027, 1, 31)), await c.QuerySingleAsync<(string, string?, decimal?, string, DateTime)>(
            "select kind, contract_no, commission_percent, status, valid_to from core.intermediary where id = @id", new { id }));
        var list = Html.Text(await Html.GetAsync(client, "/intermediaries"));
        Assert.Contains($"შუამავალი ანა {taxId} {Ka.Intermediary.Kind["agent"]}", list);
        Assert.Contains("31.01.2027", list);

        var (viewer, _) = await app.SignedInAsync("viewer");
        var readOnly = await Html.BodyAsync(await WebApp.PostFormAsync(viewer, $"/intermediaries/{id}?handler=Save",
            new() { ["id"] = id, ["kind"] = "bank", ["status"] = "active" }, $"/intermediaries/{id}"));
        Assert.Contains(Ka.Common.ReadOnly, readOnly);
        Assert.Equal("agent", await c.ExecuteScalarAsync<string>("select kind from core.intermediary where id = @id", new { id }));
    }

    [Fact]
    public async Task A_complaint_is_registered_with_its_checks_acknowledged_and_answered()
    {
        var (client, userId) = await app.SignedInAsync("sales");
        var (partyId, taxId) = await PersonAsync("+995 599112233");
        var tomorrow = Fmt.Iso(Rates.TodayInTbilisi().AddDays(1));
        var refused = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/complaints/new",
            new() { ["received_on"] = tomorrow, ["channel"] = "oral", ["category"] = "", ["description"] = "", ["policy_number"] = "NO-SUCH", ["claim_number"] = "NO-CLAIM" }, "/complaints/new"));
        foreach (var message in new[] { ComplaintTexts.Errors.ReceivedOn, ComplaintTexts.Errors.Category, ComplaintTexts.Errors.Description, ComplaintTexts.Errors.Customer, ComplaintTexts.Errors.Policy, ComplaintTexts.Errors.Claim })
            Assert.Contains(message, refused);

        var today = Fmt.Iso(Rates.TodayInTbilisi());
        var location = Html.Location(await WebApp.PostFormAsync(client, "/complaints/new",
            new() { ["received_on"] = today, ["channel"] = "electronic", ["category"] = "2", ["party_id_number"] = taxId, ["description"] = "დაგვიანებული პასუხი", ["acknowledged"] = "on" }, "/complaints/new"));
        Assert.Matches("^/complaints/[0-9a-f-]{36}\\?registered=1$", location);
        var id = location.Split('/', '?')[2];
        await using var c = await app.Db.OpenAdminAsync();
        var row = await c.QuerySingleAsync<(string Number, Guid PartyId, string Name, string Contact, short Category, string Channel, bool Acknowledged, Guid By)>(
            "select number, party_id, customer_name, customer_contact, category, channel, case when acknowledged_at is null then 0 else 1 end, created_by from core.complaint where id = @id", new { id });
        Assert.Matches("^PR[0-9]{6}$", row.Number);
        Assert.Equal((partyId, "შუამავალი ანა", "+995 599 11 22 33", (short)2, "electronic", true, userId), (row.PartyId, row.Name, row.Contact, row.Category, row.Channel, row.Acknowledged, row.By));
        Assert.Contains(ComplaintTexts.Registered, await Html.GetAsync(client, location));

        // A second one without the acknowledgement, then acknowledged from its card.
        var second = Html.Location(await WebApp.PostFormAsync(client, "/complaints/new",
            new() { ["received_on"] = today, ["channel"] = "written", ["category"] = "4", ["customer_name"] = "უცნობი მომხმარებელი", ["description"] = "სხვა" }, "/complaints/new")).Split('/', '?')[2];
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.complaint where id = @second and acknowledged_at is not null", new { second }));
        Assert.Equal($"/complaints/{second}", Html.Location(await WebApp.PostFormAsync(client, $"/complaints/{second}?handler=Acknowledge", new() { ["id"] = second }, $"/complaints/{second}")));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.complaint where id = @second and acknowledged_at is not null", new { second }));

        var noText = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/complaints/{id}?handler=Respond",
            new() { ["id"] = id, ["status"] = "upheld", ["responded_on"] = today, ["response_text"] = "" }, $"/complaints/{id}"));
        Assert.Contains(ComplaintTexts.Errors.Response, noText);
        var answered = await Html.BodyAsync(await WebApp.PostFormAsync(client, $"/complaints/{id}?handler=Respond",
            new() { ["id"] = id, ["status"] = "partly_upheld", ["responded_on"] = today, ["response_text"] = "ნაწილობრივ დაკმაყოფილდა", ["measures"] = "ტრენინგი", ["outcome"] = "", ["note"] = "" }, $"/complaints/{id}"));
        Assert.Contains($"<div class=\"alert ok\">{ComplaintTexts.Saved}</div>", answered);
        Assert.Equal(("partly_upheld", "ნაწილობრივ დაკმაყოფილდა", "ტრენინგი", (string?)null), await c.QuerySingleAsync<(string, string, string, string?)>(
            "select status, response_text, measures, outcome from core.complaint where id = @id", new { id }));
        var list = await Html.GetAsync(client, "/complaints?status=open");
        Assert.DoesNotContain(row.Number, list);
        Assert.Contains(row.Number, await Html.GetAsync(client, "/complaints?status=partly_upheld"));

        // The viewer cannot open the form and its posts write nothing.
        var (viewer, _) = await app.SignedInAsync("viewer");
        Assert.Equal("/complaints", Html.Location(await viewer.GetAsync("/complaints/new")));
        await WebApp.PostFormAsync(viewer, $"/complaints/{id}?handler=Respond", new() { ["id"] = id, ["status"] = "open" }, $"/complaints/{id}");
        Assert.Equal("partly_upheld", await c.ExecuteScalarAsync<string>("select status from core.complaint where id = @id", new { id }));
        Assert.Contains("ნაწილობრივ დაკმაყოფილდა", await Html.GetAsync(viewer, $"/complaints/{id}"));
    }

    [Fact]
    public async Task A_run_out_complaint_series_says_so_and_writes_nothing()
    {
        var (client, _) = await app.SignedInAsync("sales");
        await using var c = await app.Db.OpenAdminAsync();
        var (next, padding) = await c.QuerySingleAsync<(long, int)>("select next_value, padding from core.number_sequence where code = N'complaint'");
        var before = await c.ExecuteScalarAsync<int>("select count(*) from core.complaint");
        await c.ExecuteAsync("update core.number_sequence set next_value = @v where code = N'complaint'", new { v = (long)Math.Pow(10, padding) });
        try
        {
            var html = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/complaints/new",
                new() { ["received_on"] = Fmt.Iso(Rates.TodayInTbilisi()), ["channel"] = "written", ["category"] = "1", ["customer_name"] = "x", ["description"] = "y" }, "/complaints/new"));
            Assert.Contains(Ka.Common.SeriesExhausted, html);
            Assert.Equal(before, await c.ExecuteScalarAsync<int>("select count(*) from core.complaint"));
        }
        finally
        {
            await c.ExecuteAsync("update core.number_sequence set next_value = @next where code = N'complaint'", new { next });
        }
    }
}
