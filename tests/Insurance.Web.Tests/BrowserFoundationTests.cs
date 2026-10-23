using Microsoft.Playwright;
using Insurance.Application.Texts;

namespace Insurance.Web.Tests;

/// <summary>Browser checks of the foundation: sign-in, the menu by role, and the dd/mm/yyyy date field.</summary>
public sealed class BrowserFoundationTests(BrowserApp app) : IClassFixture<BrowserApp>
{
    [Fact]
    public async Task Sign_in_shows_the_menu_of_the_role_and_sign_out_ends_it()
    {
        var page = await app.SignedInPageAsync("accountant");
        await page.GotoAsync(app.BaseUrl + "/reference/classes");
        Assert.Contains("დაზღვევის სახეობები", await page.TitleAsync());
        var menu = await page.InnerTextAsync("aside.sidebar nav");
        Assert.Contains("საანგარიშო პერიოდები", menu);
        Assert.DoesNotContain("მომხმარებლები", menu);
        // The workspace's sidebar (Codex's Angular shell) signs out by its icon button, named by the legacy text; it posts the
        // layout's logout form with its anti-forgery token.
        await page.ClickAsync($"aside.sidebar button[aria-label=\"{Ka.Nav.Logout}\"]");
        await page.WaitForURLAsync(app.BaseUrl + "/login");
        await page.GotoAsync(app.BaseUrl + "/reference/classes");
        Assert.EndsWith("/login", page.Url);
    }

    /// <summary>
    /// The workspace's light/dark switch (Codex, codex/angular-workspace, docs/angular/THEMES.md): light by default; the choice is kept
    /// in this browser, comes back on reload and on a Razor page reached from the menu, and switching keeps what was typed in a form.
    /// </summary>
    [Fact]
    public async Task The_theme_switch_is_kept_across_reload_and_pages_and_keeps_the_form()
    {
        var page = await app.SignedInPageAsync("accountant");
        await page.GotoAsync(app.BaseUrl + "/reference/rates");
        Assert.Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
        var typed = page.Locator(".date-input input[type=text]");
        await typed.FillAsync("");
        await typed.PressSequentiallyAsync("14102026");
        await page.ClickAsync("button.theme-toggle");
        // Angular redraws the button after the click: wait for its state rather than read it at once (CI run 40030076010).
        await Assertions.Expect(page.Locator("button.theme-toggle")).ToHaveAttributeAsync("aria-pressed", "true");
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
        Assert.Equal("14/10/2026", await typed.InputValueAsync());
        Assert.EndsWith("/reference/rates", page.Url);
        await page.ReloadAsync();
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
        await page.ClickAsync($"aside.sidebar a[href=\"/reference/classes\"]");
        await page.WaitForURLAsync(app.BaseUrl + "/reference/classes");
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
        await page.ClickAsync("button.theme-toggle");
        await Assertions.Expect(page.Locator("button.theme-toggle")).ToHaveAttributeAsync("aria-pressed", "false");
        Assert.Equal("light", await page.EvaluateAsync<string>("() => localStorage.getItem('aguli-color-theme')"));
    }

    [Fact]
    public async Task The_date_field_is_typed_as_day_month_year_and_posts_the_iso_date()
    {
        var page = await app.SignedInPageAsync("accountant");
        await page.GotoAsync(app.BaseUrl + "/reference/rates");
        var visible = page.Locator(".date-input input[type=text]");
        await visible.FillAsync("");
        await visible.PressSequentiallyAsync("14102026");
        Assert.Equal("14/10/2026", await visible.InputValueAsync());
        Assert.Equal("2026-10-14", await page.Locator(".date-input input[type=hidden]").InputValueAsync());
        // A day that does not exist leaves the form invalid in the browser and posts nothing.
        await visible.FillAsync("");
        await visible.PressSequentiallyAsync("31022026");
        Assert.Equal("", await page.Locator(".date-input input[type=hidden]").InputValueAsync());
        Assert.False(await visible.EvaluateAsync<bool>("e => e.checkValidity()"));
        Assert.Equal("თარიღი შეიყვანეთ ფორმატით დდ/თთ/წწწწ (მაგ. 14/10/2026)", await visible.EvaluateAsync<string>("e => e.validationMessage"));
    }

    [Fact]
    public async Task A_viewer_sees_no_rate_loading_form()
    {
        var page = await app.SignedInPageAsync("viewer");
        await page.GotoAsync(app.BaseUrl + "/reference/rates");
        Assert.Equal(0, await page.Locator("form.toolbar").CountAsync());
    }
}
