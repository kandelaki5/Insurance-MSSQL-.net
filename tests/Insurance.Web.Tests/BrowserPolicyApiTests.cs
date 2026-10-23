using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Integration.Tests.Policies;
using Microsoft.Playwright;

namespace Insurance.Web.Tests;

/// <summary>
/// The workspace's policy list and card on /api/v1 (INS-DOTNET-MSSQL-004 B5), in a real browser: the rows and the card come from
/// /api/v1/policies, never from the pages' old JSON handlers, and the amounts are written from their minor units — 1 234 567.88 USD
/// with its grouping and both decimals, the 265.00 GEL still owed after a 100.00 receipt — in the same design as before.
/// </summary>
public sealed class BrowserPolicyApiTests(BrowserApp app) : IClassFixture<BrowserApp>
{
    private World W => new(app.Web.Db);

    [Fact]
    public async Task The_list_and_the_card_read_the_api_and_show_the_exact_amounts()
    {
        var head = await W.UserAsync("operations_head");
        var holder = await W.ClientAsync();
        var from = W.App.Today().AddDays(-10);
        var gel = await W.ActivePolicyAsync(head, holder, from);
        var receipt = await W.ReceiptAsync(holder, from.AddDays(1), 100m);
        Assert.NotNull(await PaymentActions.AllocateReceiptAsync(W.App, W.Rates, await W.UserAsync("admin"), gel.Id.ToString(), receipt.ToString()));
        var usd = await W.ActivePolicyAsync(head, holder, from, "USD", 2.7m, 1234567.88m);
        await W.RateAsync("USD", from, 2.7m);

        var viewer = await app.SignedInPageAsync("viewer");
        var requests = new List<string>();
        viewer.Request += (_, r) => requests.Add(r.Url);
        await viewer.GotoAsync(app.BaseUrl + "/policies");
        await viewer.Locator("main tr", new() { HasText = usd.Number }).First.WaitForAsync();
        await Assertions.Expect(viewer.Locator("main tr", new() { HasText = usd.Number })).ToContainTextAsync("1,234,567.88 USD");
        await Assertions.Expect(viewer.Locator("main tr", new() { HasText = gel.Number })).ToContainTextAsync("365.00 GEL");
        await Assertions.Expect(viewer.Locator("main tr", new() { HasText = gel.Number })).ToContainTextAsync("265.00 GEL");

        await viewer.Locator("main tr", new() { HasText = usd.Number }).Locator("button.policy-link").ClickAsync();
        var card = viewer.Locator("section.detail-panel");
        await Assertions.Expect(card.Locator("h2")).ToHaveTextAsync(usd.Number);
        await Assertions.Expect(card).ToContainTextAsync("1,234,567.88 USD");
        // The four instalments of the schedule, 308 641.97 each, as the card's own figures give them.
        await Assertions.Expect(card.Locator(".finance-summary").Last).ToContainTextAsync("/ 308,641.97");

        Assert.Contains(requests, u => u.Contains("/api/v1/policies?", StringComparison.Ordinal));
        Assert.Contains(requests, u => u.EndsWith($"/api/v1/policies/{usd.Id}", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, u => Regex.IsMatch(u, "/policies(/[0-9a-f-]{36})?\\?handler=Data"));
    }
}
