using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests;

/// <summary>
/// H46 over HTTP: the card of a GEL claim on a USD policy (old program docs/07 §4.5, owner 06.10.2026) shows the coverage's limit and
/// deductible in USD with the occurrence day's rate and their GEL, the GEL payment note, and a set-off as its USD × rate = GEL.
/// "Today" is the real day in Tbilisi, as on the other claim pages.
/// </summary>
public sealed class ClaimGelCardTests(WebApp app) : IClassFixture<WebApp>
{
    private readonly ClaimWorld w = new(app.Db);
    private static DateOnly Today => Rates.TodayInTbilisi();

    [Fact]
    public async Task The_card_shows_the_USD_limit_at_the_occurrence_days_rate_and_the_set_off_as_100_USD_x_2_7_270_GEL()
    {
        var admin = await w.UserAsync("admin");
        var occurred = Today.AddDays(-10);
        await w.RateAsync("USD", occurred, 2.6m);
        await w.RateAsync("USD", Today, 2.7m);
        var p = await w.PolicyAsync(admin.Id, Today.AddDays(-40), [new ClaimWorld.Line("damage", 1000m, 100m, 100m)], [(Today.AddDays(-40), 100m)], "USD", 2.5m);
        var claims = await w.UserAsync("claims");
        var claim = (await w.RegisterAsync(claims, p, occurred, occurred.AddDays(1), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1260", Today);
        var (viewer, _) = await app.SignedInAsync("viewer");
        var (accountant, _) = await app.SignedInAsync("accountant");
        // Before the payment the payment form explains the GEL claim and the set-off in the policy's currency.
        Assert.Contains(ClaimTexts.Notes.PaymentGel, Html.Text(await (await accountant.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync()));
        Assert.NotNull((await w.PayAsync(await w.UserAsync("accountant"), claim, "1260", Today, Today, ("offset_premium", "on"))).RedirectTo);

        var card = Html.Text(await (await viewer.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync());
        var day = occurred.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"1,000.00 USD × 2.6 ({day}) = 2,600.00 ₾", card);
        Assert.Contains($"100.00 USD × 2.6 ({day}) = 260.00 ₾", card);
        Assert.Contains("100.00 USD × 2.7 = 270.00 ₾", card);
    }
}
