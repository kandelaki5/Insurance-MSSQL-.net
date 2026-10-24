using Microsoft.Playwright;

namespace Insurance.Web.Tests;

/// <summary>
/// Swagger UI's Authorize in a real browser (ADR 0003, INS-DOTNET-MSSQL-004 B3): the signed-in user authorizes Swagger through the
/// embedded provider (Authorization Code with PKCE, in Swagger's own pop-up window), and "Try it out" then calls /api/v1/me with the
/// bearer token it got — the answer is that user.
/// </summary>
public sealed class BrowserSwaggerTests(BrowserApp app) : IClassFixture<BrowserApp>
{
    [Fact]
    public async Task Swagger_authorizes_with_PKCE_and_calls_the_api_with_its_bearer_token()
    {
        var page = await app.SignedInPageAsync("accountant");
        await page.GotoAsync(app.BaseUrl + "/api/swagger/index.html");
        await page.Locator("button.authorize").First.ClickAsync();
        var modal = page.Locator(".modal-ux");
        await modal.Locator("input[type=checkbox]").First.CheckAsync();
        var popup = await page.RunAndWaitForPopupAsync(() => modal.Locator("button.authorize").ClickAsync());
        await Assertions.Expect(modal).ToContainTextAsync("Authorized", new() { Timeout = 15000 });
        Assert.True(popup.IsClosed || popup.Url.Contains("oauth2-redirect", StringComparison.Ordinal), popup.Url);
        await modal.Locator("button.close-modal").ClickAsync();

        string? authorization = null;
        page.Request += (_, r) => { if (r.Url.EndsWith("/api/v1/me", StringComparison.Ordinal)) authorization = r.Headers.GetValueOrDefault("authorization"); };
        var operation = page.Locator(".opblock", new() { Has = page.Locator(".opblock-summary-path[data-path='/api/v1/me']") });
        await operation.Locator(".opblock-summary").ClickAsync();
        await operation.Locator("button.try-out__btn").ClickAsync();
        await operation.Locator("button.execute").ClickAsync();
        await Assertions.Expect(operation.Locator(".live-responses-table tr.response .response-col_status").First).ToHaveTextAsync("200");
        await Assertions.Expect(operation.Locator(".responses-table.live-responses-table")).ToContainTextAsync("\"role\": \"accountant\"");
        Assert.NotNull(authorization);
        Assert.StartsWith("Bearer ", authorization);
    }
}
