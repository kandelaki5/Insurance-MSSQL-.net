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

    /// <summary>
    /// The list pages, searches and filters on the server (Codex 006, item 2 of the first slice): a client's nine policies, seven a page;
    /// the second page has the other two; the tabs count the search; "unpaid" leaves the active ones that still owe. The design's
    /// markup is the same (table, tabs, pagination).
    /// </summary>
    [Fact]
    public async Task The_list_pages_and_filters_on_the_server_seven_a_page_counts_by_status_and_unpaid_only()
    {
        var head = await W.UserAsync("operations_head");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await W.ClientAsync(taxId);
        var from = W.App.Today().AddDays(-10);
        var numbers = new List<string>();
        for (var i = 0; i < 8; i++) numbers.Add((await W.ActivePolicyAsync(head, holder, from)).Number);
        numbers.Add((await W.PendingPolicyAsync(holder, from)).Number);
        numbers.Reverse();

        var viewer = await app.SignedInPageAsync("viewer");
        await viewer.GotoAsync(app.BaseUrl + "/policies?q=" + taxId);
        var rows = viewer.Locator("table.policy-table tbody tr");
        await Assertions.Expect(rows.First).ToContainTextAsync(numbers[0]);
        await Assertions.Expect(rows).ToHaveCountAsync(7);
        Assert.Equal(numbers.Take(7), (await rows.Locator("button.policy-link").AllInnerTextsAsync()).Select(t => t.Trim()));
        await Assertions.Expect(viewer.Locator(".pagination")).ToContainTextAsync("1–7 /");
        await viewer.Locator("button.page-button[aria-label='გვერდი 2']").ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        Assert.Equal(numbers.Skip(7), (await rows.Locator("button.policy-link").AllInnerTextsAsync()).Select(t => t.Trim()));

        var tabs = viewer.Locator(".policy-tabs button");
        await Assertions.Expect(tabs.Nth(0)).ToContainTextAsync("9");
        await Assertions.Expect(tabs.Nth(1)).ToContainTextAsync("8");
        await Assertions.Expect(tabs.Nth(2)).ToContainTextAsync("1");
        await viewer.Locator("select[aria-label='გადახდის ფილტრი']").SelectOptionAsync("unpaid");
        await Assertions.Expect(viewer.Locator(".filter-result")).ToContainTextAsync("მოიძებნა 8 პოლისი");
        await Assertions.Expect(rows).ToHaveCountAsync(7);
        Assert.DoesNotContain(numbers[0], await rows.AllInnerTextsAsync() is var texts ? string.Join("\n", texts) : "");
    }
}
