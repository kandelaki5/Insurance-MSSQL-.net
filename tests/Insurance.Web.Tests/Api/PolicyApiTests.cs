using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insurance.Api;
using Insurance.Application.Services;
using Insurance.Integration.Tests.Policies;
using Insurance.Web.Tests.Policies;

namespace Insurance.Web.Tests.Api;

/// <summary>
/// /api/v1/policies (INS-DOTNET-MSSQL-004 B2/B5) reads the policy list through IPolicyListQuery, as the Razor list and the Angular
/// view's ?handler=Data do: the three show the same policies in the same order with the same amounts; the API gives them exactly, as
/// minor units in a string, pages them without repeating or skipping one, refuses a bad page as problem details, and answers only a
/// signed-in user.
/// </summary>
public sealed partial class PolicyApiTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private static async Task<JsonElement> JsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [GeneratedRegex("<a href=\"/policies/[0-9a-f-]{36}\">([^<]+)</a>")]
    private static partial Regex PageNumber();

    /// <summary>One client's three policies: waiting for approval; active 365.00 GEL with 100.00 allocated; active 1 000.01 USD.</summary>
    private async Task<(string TaxId, World.Policy Pending, World.Policy Gel, World.Policy Usd)> ClientWithThreeAsync()
    {
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(CultureInfo.InvariantCulture);
        var holder = await app.W.ClientAsync(taxId);
        var head = await app.W.UserAsync("operations_head");
        var from = app.W.App.Today().AddDays(-10);
        var gel = await app.W.ActivePolicyAsync(head, holder, from);
        var receipt = await app.W.ReceiptAsync(holder, from.AddDays(1), 100m);
        Assert.NotNull(await PaymentActions.AllocateReceiptAsync(app.W.App, app.W.Rates, await app.W.UserAsync("admin"), gel.Id.ToString(), receipt.ToString()));
        var usd = await app.W.ActivePolicyAsync(head, holder, from, "USD", 2.7m, 1000.01m);
        var pending = await app.W.PendingPolicyAsync(holder, from);
        return (taxId, pending, gel, usd);
    }

    [Fact]
    public async Task Without_a_session_the_list_is_401_problem_details()
    {
        var r = await app.Client().GetAsync("/api/v1/policies");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_api_the_angular_data_and_the_razor_list_show_the_same_policies_in_the_same_order_with_the_same_amounts()
    {
        var (taxId, pending, gel, usd) = await ClientWithThreeAsync();
        var (viewer, _) = await app.SignedInAsync("viewer");
        var api = await JsonAsync(viewer, $"/api/v1/policies?search={taxId}");
        var data = await JsonAsync(viewer, $"/policies?handler=Data&q={taxId}");
        var html = await PolicyWebApp.HtmlAsync(viewer, $"/policies?q={taxId}");

        Assert.Equal(3, api.GetProperty("total").GetInt32());
        var items = api.GetProperty("items").EnumerateArray().ToArray();
        var rows = data.GetProperty("rows").EnumerateArray().ToArray();
        // Newest first: the pending policy was written last.
        Assert.Equal([pending.Number, usd.Number, gel.Number], items.Select(i => i.GetProperty("number").GetString()!).ToArray());
        Assert.Equal(items.Select(i => i.GetProperty("id").GetGuid()), rows.Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal([pending.Number, usd.Number, gel.Number], PageNumber().Matches(html).Select(m => m.Groups[1].Value).ToArray());

        static string Minor(JsonElement money)
        {
            Assert.Equal(JsonValueKind.String, money.GetProperty("minor").ValueKind);
            Assert.Equal(Money.ProgramScale, money.GetProperty("scale").GetInt32());
            return money.GetProperty("minor").GetString()!;
        }
        var byNumber = items.ToDictionary(i => i.GetProperty("number").GetString()!);
        Assert.Equal(("100001", "USD"), (Minor(byNumber[usd.Number].GetProperty("premium")), byNumber[usd.Number].GetProperty("premium").GetProperty("currency").GetString()));
        Assert.Equal(("36500", "10000", "26500"), (Minor(byNumber[gel.Number].GetProperty("premium")), Minor(byNumber[gel.Number].GetProperty("paid")),
            Minor(byNumber[gel.Number].GetProperty("outstanding"))));
        Assert.Equal(JsonValueKind.Null, byNumber[pending.Number].GetProperty("outstanding").ValueKind);
        // The Angular handler's decimals are the same amounts.
        foreach (var (item, row) in items.Zip(rows))
        {
            Assert.Equal(decimal.Parse(Minor(item.GetProperty("premium")), CultureInfo.InvariantCulture) / 100m, row.GetProperty("premium").GetDecimal());
            Assert.Equal(decimal.Parse(Minor(item.GetProperty("paid")), CultureInfo.InvariantCulture) / 100m, row.GetProperty("paid").GetDecimal());
            decimal? apiOutstanding = item.GetProperty("outstanding").ValueKind == JsonValueKind.Null ? null : decimal.Parse(Minor(item.GetProperty("outstanding")), CultureInfo.InvariantCulture) / 100m;
            decimal? dataOutstanding = row.GetProperty("outstanding").ValueKind == JsonValueKind.Null ? null : row.GetProperty("outstanding").GetDecimal();
            Assert.Equal(apiOutstanding, dataOutstanding);
        }
        Assert.Contains("1000.01 USD", html);
    }

    [Fact]
    public async Task Pages_cover_the_search_once_with_no_policy_repeated_or_skipped_and_a_bad_page_is_refused()
    {
        var (taxId, pending, gel, usd) = await ClientWithThreeAsync();
        var (viewer, _) = await app.SignedInAsync("viewer");
        var first = await JsonAsync(viewer, $"/api/v1/policies?search={taxId}&limit=2");
        var second = await JsonAsync(viewer, $"/api/v1/policies?search={taxId}&offset=2&limit=2");
        Assert.Equal((3, 3), (first.GetProperty("total").GetInt32(), second.GetProperty("total").GetInt32()));
        var numbers = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("number").GetString()!).ToArray();
        Assert.Equal([pending.Number, usd.Number, gel.Number], numbers);
        foreach (var bad in new[] { "limit=0", "limit=201", "offset=-1" })
        {
            var r = await viewer.GetAsync($"/api/v1/policies?search={taxId}&{bad}");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(Problems.InvalidPagingCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        // A number that is no number is the framework's own 400, not a page.
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/policies?limit=x")).StatusCode);
    }
}
