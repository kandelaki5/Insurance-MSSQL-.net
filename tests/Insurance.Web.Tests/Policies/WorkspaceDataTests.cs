using System.Net;
using System.Text.Json;
using Insurance.Application.Services;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The read-only JSON handlers the Angular workspace reads (Codex, codex/angular-workspace: the home page's, the policies list's and
/// the card's ?handler=Data) agree with the screens after an open-month correction (legacy PRs #207/#225, owner 13.10.2026): the
/// correction's record is no policy of its own — not counted, not listed — and the original stands once at its corrected premium.
/// They answer only a signed-in user.
/// </summary>
public sealed class WorkspaceDataTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private static async Task<JsonElement> JsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Theory]
    [InlineData("/?handler=Data")]
    [InlineData("/policies?handler=Data")]
    [InlineData("/policies/00000000-0000-0000-0000-000000000001?handler=Data")]
    public async Task Without_a_session_the_data_is_not_given_the_user_is_sent_to_sign_in(string url)
    {
        var response = await app.Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task After_a_correction_the_home_list_and_card_data_count_the_original_once_at_320()
    {
        var head = await app.W.UserAsync("operations_head");
        var october = new DateOnly(2026, 10, 1);
        var o = await app.W.PendingPolicyAsync(await app.W.ClientAsync(), october, premium: 400m, schedule: [(0, 100m), (31, 100m), (61, 100m), (92, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(app.W.App, head, o.Id.ToString())).Outcome);
        Assert.NotNull((await app.W.EndorseAsync(head, o.Id, october.AddDays(6), "-80")).Redirect);
        var change = (await app.W.ChangeOfAsync(o.Id))!.Value;
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(app.W.App, await app.W.UserAsync("operations_head"), change.Id.ToString())).Outcome);
        var (viewer, _) = await app.SignedInAsync("viewer");

        var portfolio = (await JsonAsync(viewer, "/?handler=Data")).GetProperty("portfolio");
        Assert.Equal((1, 1, 320m), (portfolio.GetProperty("active").GetInt32(), portfolio.GetProperty("allPolicies").GetInt32(), portfolio.GetProperty("activePremiumGel").GetDecimal()));

        var row = Assert.Single((await JsonAsync(viewer, "/policies?handler=Data")).GetProperty("rows").EnumerateArray());
        Assert.Equal((o.Id, 320m), (row.GetProperty("id").GetGuid(), row.GetProperty("premium").GetDecimal()));

        var card = await JsonAsync(viewer, $"/policies/{o.Id}?handler=Data");
        Assert.Equal((320m, 400m), (card.GetProperty("premium").GetDecimal(), card.GetProperty("issuedPremium").GetDecimal()));
        // The correction's own card keeps its −80.00 as issued and carries nothing now (its money is on the original).
        var record = await JsonAsync(viewer, $"/policies/{change.Id}?handler=Data");
        Assert.Equal((0m, -80m), (record.GetProperty("premium").GetDecimal(), record.GetProperty("issuedPremium").GetDecimal()));
    }
}
