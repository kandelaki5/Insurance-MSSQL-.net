using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The printed policy shows stored amounts exactly (legacy print-policy-money.test.ts, the cases PrintPolicyTests leaves
/// out): the largest amounts in USD with the currency, ordinary GEL and USD amounts, an exact total over many lines,
/// only instalments above zero, the vehicle's value as stored text, a JSON number or a dash, the rate and the period,
/// the page only reading; and the page's formatter (Billing.GroupedTetri) against the old number formatter.
/// </summary>
public sealed class GapsPrintPolicyTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;

    private static string Text(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]*>", "")).Trim();

    private static List<List<string[]>> Tables(string html) => Regex.Matches(html, @"<tbody>([\s\S]*?)</tbody>").Select(b =>
        Regex.Matches(b.Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr>").Select(r => Regex.Matches(r.Groups[1].Value, @"<td\b[^>]*>([\s\S]*?)</td>").Select(c => Text(c.Groups[1].Value)).ToArray()).ToList()).ToList();

    private static Dictionary<string, string> Facts(string html) => Regex.Matches(html, @"<dt>([\s\S]*?)</dt>\s*<dd>([\s\S]*?)</dd>")
        .GroupBy(m => Text(m.Groups[1].Value)).ToDictionary(g => g.Key, g => Text(g.First().Groups[2].Value));

    private sealed record LineSpec(string Premium, string? SumInsured = null, string? Deductible = null);

    /// <summary>A policy from 01.10.2026 with the lines (class 5.1, no coverage), the schedule and the vehicle attributes given; the viewer's printed page.</summary>
    private async Task<string> PageAsync(string currency, decimal rate, LineSpec[] lines, string[]? schedule = null, string? attributes = "{}")
    {
        var p = await W.PendingPolicyAsync(await W.ClientAsync(), new DateOnly(2026, 10, 1), currency, rate, premium: 1m, schedule: []);
        await using (var c = await app.Db.OpenAppAsync())
        {
            await c.ExecuteAsync("delete from core.policy_line where policy_id = @id", new { id = p.Id });
            foreach (var l in lines)
                await c.ExecuteAsync("insert core.policy_line (policy_id, class_code, sum_insured, deductible, premium, premium_gel) values (@id, N'5.1', @sum, @deductible, @premium, @premium)",
                    new { id = p.Id, sum = l.SumInsured is null ? (decimal?)null : decimal.Parse(l.SumInsured, CultureInfo.InvariantCulture), deductible = l.Deductible is null ? (decimal?)null : decimal.Parse(l.Deductible, CultureInfo.InvariantCulture), premium = decimal.Parse(l.Premium, CultureInfo.InvariantCulture) });
            var k = 0;
            foreach (var s in schedule ?? [])
                await c.ExecuteAsync("insert core.installment (policy_id, due_on, amount) values (@id, @due, @amount)", new { id = p.Id, due = new DateOnly(2027, ++k, 1), amount = decimal.Parse(s, CultureInfo.InvariantCulture) });
            if (attributes is not null)
                await c.ExecuteAsync("insert core.insured_object (policy_id, object_type, description, attributes) values (@id, N'vehicle', N'ტესტი ავტომობილი', @attributes)", new { id = p.Id, attributes });
        }
        var (viewer, _) = await app.SignedInAsync("viewer");
        return await HtmlAsync(viewer, $"/print/policies/{p.Id}");
    }

    private static (List<string[]> Lines, string Total) Coverage(string html)
    {
        var rows = Tables(html)[0];
        return (rows[..^1].Select(r => r[2..]).ToList(), rows[^1][^1]);
    }

    [Fact]
    public async Task Numeric_18_2s_largest_amounts_keep_their_last_tetri_in_every_money_cell_in_USD()
    {
        var html = await PageAsync("USD", 2.7123m, [new("90071992547408.01", "9999999999999999.99", "90071992547408.01"), new("0.01")], ["90071992547408.01", "9999999999999999.99"]);
        var (lines, total) = Coverage(html);
        Assert.Equal([["9,999,999,999,999,999.99", "90,071,992,547,408.01", "90,071,992,547,408.01"], ["—", "—", "0.01"]], lines.OrderByDescending(l => l[2].Length).ToList());
        // The premium total is added in whole tetri: 90071992547408.01 + 0.01.
        Assert.Equal("90,071,992,547,408.02 USD", total);
        Assert.Equal(["90,071,992,547,408.01", "9,999,999,999,999,999.99"], Tables(html)[1].Select(r => r[1]));
    }

    [Theory]
    [InlineData("GEL")]
    [InlineData("USD")]
    public async Task Ordinary_GEL_and_USD_amounts_look_as_before_grouping_two_decimals_the_currency(string currency)
    {
        var html = await PageAsync(currency, currency == "GEL" ? 1m : 2.7m, [new("1234.50", "25000.00", "500.00"), new("0.00"), new("100.10"), new("0.70")], ["617.25", "617.25"]);
        var (lines, total) = Coverage(html);
        Assert.Equal(new[] { "25,000.00|500.00|1,234.50", "—|—|0.00", "—|—|100.10", "—|—|0.70" }.Order(StringComparer.Ordinal), lines.Select(l => string.Join("|", l)).Order(StringComparer.Ordinal));
        Assert.Equal($"1,335.30 {currency}", total);
        Assert.Equal(["617.25", "617.25"], Tables(html)[1].Select(r => r[1]));
    }

    [Fact]
    public async Task A_total_over_many_lines_is_their_exact_sum()
    {
        string[] premiums = ["0.10", "0.20", "0.10", "0.20", "0.10", "0.20", "0.10", "0.20", "0.10", "0.20", "45035996273704.00", "45035996273704.00"];
        Assert.Equal("90,071,992,547,409.50 GEL", Coverage(await PageAsync("GEL", 1m, [.. premiums.Select(p => new LineSpec(p))])).Total);
    }

    [Fact]
    public async Task Only_instalments_above_zero_are_printed_decided_in_tetri() =>
        Assert.Equal(["0.01", "99.99"], Tables(await PageAsync("GEL", 1m, [new("100.00")], ["0.00", "0.01", "0.00", "99.99"]))[1].Select(r => r[1]));

    [Fact]
    public async Task The_vehicles_value_a_stored_text_exactly_a_JSON_number_as_before_none_as_a_dash()
    {
        async Task<string> Shown(string attributes) => Facts(await PageAsync("GEL", 1m, [new("1.00")], attributes: attributes))[PolicyTexts.Fields.Value];
        Assert.Equal("90,071,992,547,408.01 GEL", await Shown("""{"value":"90071992547408.01"}"""));
        Assert.Equal("25,000.00 GEL", await Shown("""{"value":"25000"}"""));
        Assert.Equal("25,000.50 GEL", await Shown("""{"value":"25000.5"}"""));
        Assert.Equal("25,000.00 GEL", await Shown("""{"value":25000}"""));
        // Already a JSON number: the digits a double keeps, shown as the old formatter did (Node: toLocaleString en-US, two
        // decimals, gives …408.02). Before the fix the page showed …408.00 (.NET's custom format kept 15 digits).
        Assert.Equal("90,071,992,547,408.02 GEL", await Shown("""{"value":90071992547408.01}"""));
        Assert.Equal("—", await Shown("{}"));
    }

    [Fact]
    public async Task The_rate_and_the_period_are_as_before_the_page_only_reads()
    {
        var html = await PageAsync("USD", 2.712345m, [new("10.00")], ["10.00"]);
        Assert.Equal($"USD ({PolicyTexts.Fields.Rate}: 2.7123)", Facts(html)[PolicyTexts.Fields.Currency]);
        Assert.Contains("01.10.2026 00:00 – 30.09.2027 24:00", WebUtility.HtmlDecode(html));
        // Opened again: the page itself writes nothing.
        var after = await Gaps.LedgerAsync(W);
        var (viewer, _) = await app.SignedInAsync("viewer");
        var id = await W.ScalarAsync<Guid>("select top (1) p.id from core.policy p order by p.created_at desc");
        await HtmlAsync(viewer, $"/print/policies/{id}");
        Assert.Equal(after, await Gaps.LedgerAsync(W));
    }

    [Fact]
    public void The_pages_formatter_agrees_with_the_old_number_formatter_wherever_a_number_is_exact_and_the_counterexamples()
    {
        // The old formatter: Number(s).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).
        static string Old(string s) => double.Parse(s, CultureInfo.InvariantCulture).ToString("N2", CultureInfo.InvariantCulture);
        foreach (var s in new[] { "0.00", "0", "0.5", "1", "12", "123", "1234", "12345.67", "123456", "1234567.89", "1000000", "9007199254740.99", "-1234.5", "-1234.50", "-999999.99" })
            Assert.Equal(Old(s), Billing.GroupedTetri(Billing.Tetri(decimal.Parse(s, CultureInfo.InvariantCulture))));
        Assert.Equal("-0.05", Billing.GroupedTetri(Billing.Tetri(-0.05m)));
        Assert.Equal(Billing.GroupedTetri(Billing.Tetri(1234.5m)), Billing.GroupedTetri(Billing.Tetri(1234.50m)));
        Assert.Equal("0.00", Billing.GroupedTetri(Billing.Tetri(-0.00m)));
        Assert.Equal("90,071,992,547,408.01", Billing.GroupedTetri(Billing.Tetri(90071992547408.01m)));
        Assert.Equal("9,999,999,999,999,999.99", Billing.GroupedTetri(Billing.Tetri(9999999999999999.99m)));
        Assert.Equal("90,071,992,547,408.02", Billing.GroupedTetri(Billing.Tetri(90071992547408.01m) + Billing.Tetri(0.01m)));
        // The old formatter loses the last tetri there.
        Assert.NotEqual("90,071,992,547,408.01", Old("90071992547408.01"));
    }
    [Theory]
    [InlineData(90071992547408.01, "90,071,992,547,408.02")]
    [InlineData(25000.5, "25,000.50")]
    [InlineData(0.125, "0.13")]
    [InlineData(0.135, "0.14")]
    [InlineData(1.005, "1.01")]
    [InlineData(2.675, "2.68")]
    [InlineData(0.005, "0.01")]
    [InlineData(1e21, "1,000,000,000,000,000,000,000.00")]
    [InlineData(123456789012345680000d, "123,456,789,012,345,680,000.00")]
    public void A_JSON_number_is_shown_as_the_old_toLocaleString_did(double value, string shown) =>
        // Expected values from Node 24: Number(x).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).
        Assert.Equal(shown, Insurance.Web.Pages.Print.PolicyModel.LocaleFixed2(value));
}
