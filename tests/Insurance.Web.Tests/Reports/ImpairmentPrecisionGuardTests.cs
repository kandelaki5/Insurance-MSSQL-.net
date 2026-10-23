using System.Net;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// Legacy PR #361 (scripts/integration/impairment-precision-guard.test.ts, 30.06.2026): the impairment page and its Excel file
/// never give a figure that cannot be held. The old code computed in JavaScript numbers, so some figures came out a tetri off
/// ("inexact"), and it also refused figures of more than 15 digits, which Excel cannot keep to the tetri ("notForExcel"). Here
/// every figure is exact (long tetri from decimal, checked arithmetic): the cases the old code called inexact are shown exactly,
/// and refused as a file only when Excel could not keep them — a deliberate difference, recorded in MIGRATION-COVERAGE. A sum
/// beyond a long is the one inexact case left: reported as such, never an error page.
/// </summary>
public sealed class ImpairmentPrecisionGuardTests
{
    private const string Date = "2026-06-30";

    private static async Task<(ReportSite Site, HttpClient Client)> SiteAsync(decimal premium, decimal paid, int lines = 1)
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        var p = await w.PolicyAsync("P-GUARD", "2026-01-01", "2027-01-01", Enumerable.Range(0, lines).Select(_ => new LineSpec("5.1", premium, premium)).ToList(),
            [("2026-01-01", premium, "client")]);
        if (paid != 0) await w.PayAsync(p.Installments[0], "2026-01-15", paid);
        var (client, _) = await site.SignedInAsync("accountant");
        return (site, client);
    }

    private static async Task<(string Page, HttpResponseMessage File)> ReadAsync(HttpClient client) =>
        (ReportSite.Decode(await ReportSite.PageAsync(client, $"/accounting/impairment?date={Date}")), await client.GetAsync($"/accounting/impairment/xlsx?date={Date}"));

    private static async Task Refused(HttpResponseMessage file, string text)
    {
        Assert.Equal((HttpStatusCode)422, file.StatusCode);
        Assert.Equal("text/plain", file.Content.Headers.ContentType!.MediaType);
        Assert.Null(file.Content.Headers.ContentDisposition);
        Assert.Equal(text, await file.Content.ReadAsStringAsync());
    }

    private const string Link = "href=\"/accounting/impairment/xlsx?date=2026-06-30\"";

    [Fact]
    public async Task An_ordinary_date_shows_its_figures_and_gives_the_file()
    {
        var (site, client) = await SiteAsync(365m, 100m);
        await using var _ = site;
        var (page, file) = await ReadAsync(client);
        Assert.Contains(Link, page);
        Assert.DoesNotContain(ImpairmentTexts.Inexact, page);
        Assert.DoesNotContain(ImpairmentTexts.NotForExcel, page);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(ReportWorkbooks.XlsxType, file.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    // Legacy: the premium read as a number lost a tetri (9007199254740802), so "inexact"; here exact, beyond 15 digits.
    [InlineData("90071992547408.01", "0", "90,071,992,547,408.01")]
    // Legacy: 0.01 paid on 90 071 992 547 408.00 gave 9007199254740799 tetri unpaid; here 90 071 992 547 407.99 exactly.
    [InlineData("90071992547408.00", "0.01", "90,071,992,547,407.99")]
    // Legacy too: exact, but 16 digits — the page shows it, Excel cannot keep it.
    [InlineData("70000000000000.00", "0.01", "69,999,999,999,999.99")]
    // One tetri past the 15 digits.
    [InlineData("10000000000000.00", "0", "10,000,000,000,000.00")]
    public async Task A_figure_beyond_15_digits_is_shown_exactly_but_not_given_as_a_file(string premium, string paid, string shown)
    {
        var (site, client) = await SiteAsync(decimal.Parse(premium, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(paid, System.Globalization.CultureInfo.InvariantCulture));
        await using var _ = site;
        var (page, file) = await ReadAsync(client);
        Assert.Contains(shown, page);
        Assert.Contains(ImpairmentTexts.NotForExcel, page);
        Assert.DoesNotContain(ImpairmentTexts.Inexact, page);
        Assert.DoesNotContain(Link, page);
        await Refused(file, ImpairmentTexts.NotForExcel);
    }

    [Fact]
    public async Task Fifteen_digits_exactly_are_given_as_a_file()
    {
        var (site, client) = await SiteAsync(9999999999999.99m, 0m);
        await using var _ = site;
        var (page, file) = await ReadAsync(client);
        Assert.Contains("9,999,999,999,999.99", page);
        Assert.Contains(Link, page);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    }

    [Fact]
    public async Task A_sum_beyond_a_long_is_reported_inexact_never_an_error_page()
    {
        // Ten lines of the largest stored amount: their written premium overflows a long of tetri.
        var (site, client) = await SiteAsync(9999999999999999.99m, 0m, lines: 10);
        await using var _ = site;
        var (page, file) = await ReadAsync(client);
        Assert.Contains(ImpairmentTexts.Inexact, page);
        Assert.DoesNotContain(Link, page);
        Assert.DoesNotContain(ImpairmentTexts.TotalsTitle, page);
        await Refused(file, ImpairmentTexts.Inexact);
    }
}
