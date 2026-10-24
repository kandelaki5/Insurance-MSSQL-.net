using System.Net;
using Dapper;
using Insurance.Integration.Tests;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// Form 6's grouping fixes through the real page and the real form 6 download over fictional rows in SQL Server (legacy form6-fixes.test.ts,
/// Codex PR358_FORM6_EQUAL_SORT_ORDER_GROUPING_20261020; the owner's example of 20.10.2026). Classes sharing a place in the reference
/// (insurance_class.sort_order is not unique — here 11…17 all set to 100 in the test database) stay one group each, in the order of their
/// code: 2 × 2 → 2 groups, 5 × 2 → 5 groups, 6 × 5 fills the template exactly, 7 → the seventh below the grid. Nine facultative treaties
/// placed on 5.1 with nine periods: nine rows, five in the group and four below the grid, on the page and in the file. The quarter's year
/// cases of the old test are in RegulatoryPagesTests and Form6Tests.
/// </summary>
public sealed class Form6FixesTests
{
    private const string Sheet6 = "გადამზღვევლები და ბროკერები";

    /// <summary><paramref name="classes"/> classes from 11 on, all at sort_order 100, each with <paramref name="reinsurers"/> quota shares
    /// (reinsurers Re A, Re B … — named so that an order by reinsurer would interleave the classes).</summary>
    private static async Task<(ReportSite Site, List<string> Codes, Dictionary<string, string> Names)> TiedAsync(int classes, int reinsurers)
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        var codes = Enumerable.Range(11, classes).Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await using var c = await w.Database.OpenAdminAsync();
        await c.ExecuteAsync("update core.insurance_class set sort_order = 100 where code in @codes", new { codes });
        var names = (await c.QueryAsync<(string Code, string Name)>("select code, name_ka from core.insurance_class where code in @codes", new { codes })).ToDictionary(x => x.Code, x => x.Name);
        foreach (var code in codes)
            for (var r = 0; r < reinsurers; r++)
            {
                var party = await w.PartyAsync($"Re {(char)('A' + r)} (გამოგონილი)", foreignId: $"RE-{code}-{r}", country: "DE");
                await w.TreatyAsync($"RI-{code}-{r}", "quota_share", "2026-01-01", "2026-12-31", [code], cededPercent: 10m, reinsurer: party);
            }
        return (site, codes, names);
    }

    private static async Task<HttpResponseMessage> FileAsync(HttpClient client)
    {
        var r = await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=2026-Q4");
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return r;
    }

    /// <summary>The file's groups: each group's class label (A) and the reinsurers under it (B).</summary>
    private static async Task<List<(string Label, List<string> Reinsurers)>> GroupsAsync(HttpResponseMessage file)
    {
        var sheet = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
        var groups = new List<(string, List<string>)>();
        for (var k = 0; k < 6; k++)
        {
            var top = 9 + k * 5;
            var label = XlsxSheet.Text(sheet[$"A{top}"]);
            if (label == "") continue;
            groups.Add((label, Enumerable.Range(top, 5).Select(r => XlsxSheet.Text(sheet[$"B{r}"])).Where(t => t != "").ToList()));
        }
        return groups;
    }

    private static string Label(Dictionary<string, string> names, string code) => $"{code} {names[code]}";

    private static async Task<bool> PageOffersAsync(HttpClient client) =>
        (await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12")).Contains("href=\"/accounting/regulatory/form6/xlsx?quarter=2026-Q4\"", StringComparison.Ordinal);

    [Fact]
    public async Task Two_classes_by_two_reinsurers_at_one_sort_order_two_groups_in_code_order_each_with_its_own_two()
    {
        var (site, _, names) = await TiedAsync(2, 2);
        await using var _s = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var groups = await GroupsAsync(await FileAsync(client));
        Assert.Equal([(Label(names, "11"), "Re A (გამოგონილი)|Re B (გამოგონილი)"), (Label(names, "12"), "Re A (გამოგონილი)|Re B (გამოგონილი)")],
            groups.Select(g => (g.Label, string.Join("|", g.Reinsurers))));
        // The page lists them the same way: each class's rows together, in code order, its reinsurers in order.
        var page = await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12");
        var rows = System.Text.RegularExpressions.Regex.Matches(page, "<tr[^>]*>([\\s\\S]*?)</tr>")
            .Select(m => System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, "<td[^>]*>([\\s\\S]*?)</td>").Select(td => WebUtility.HtmlDecode(td.Groups[1].Value).Trim()).ToArray())
            .Where(cells => cells.Length > 1 && (cells[0] == Label(names, "11") || cells[0] == Label(names, "12")))
            .Select(cells => $"{cells[0].Split(' ')[0]} {cells[1]}");
        Assert.Equal(["11 Re A (გამოგონილი)", "11 Re B (გამოგონილი)", "12 Re A (გამოგონილი)", "12 Re B (გამოგონილი)"], rows);
    }

    [Fact]
    public async Task Five_by_two_tied_five_groups_the_file_given_no_missing_cell()
    {
        var (site, codes, _) = await TiedAsync(5, 2);
        await using var _s = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var groups = await GroupsAsync(await FileAsync(client));
        Assert.Equal(codes.Select(c => $"{c} 2"), groups.Select(g => $"{g.Label.Split(' ')[0]} {g.Reinsurers.Count}"));
    }

    [Fact]
    public async Task Six_by_five_tied_fills_the_template_exactly_and_the_page_offers_it()
    {
        var (site, codes, _) = await TiedAsync(6, 5);
        await using var _s = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var file = await FileAsync(client);
        var groups = await GroupsAsync(file);
        Assert.Equal(codes.Select(c => $"{c} 5"), groups.Select(g => $"{g.Label.Split(' ')[0]} {g.Reinsurers.Count}"));
        // Nothing below the grid: the row after it is the template's own.
        var sheet = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
        Assert.Equal("", XlsxSheet.Text(sheet["B39"]));
        Assert.True(await PageOffersAsync(client));
        Assert.DoesNotContain("სტრიქონი ბადის ქვემოთ", ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12")));
    }

    [Fact]
    public async Task Seven_classes_tied_the_seventh_in_the_row_added_below_the_grid_the_file_given_and_offered()
    {
        var (site, _, names) = await TiedAsync(7, 1);
        await using var _s = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var sheet = XlsxSheet.Read(await (await FileAsync(client)).Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
        Assert.Equal([Label(names, "16"), Label(names, "17"), "Re A (გამოგონილი)"], new[] { "A34", "A39", "B39" }.Select(r => XlsxSheet.Text(sheet[r])));
        Assert.True(await PageOffersAsync(client));
        Assert.Contains("1 სტრიქონი ბადის ქვემოთ", ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12")));
    }

    [Fact]
    public async Task Nine_facultative_treaties_on_5_1_with_nine_periods_nine_rows_five_in_its_group_four_below_none_merged_or_left_out()
    {
        // The owner, 20.10.2026: „9 განსხვავებული ვადა თუ არის ამ 9 ხელშეკრულებაზე, დაგვჭირდება 9 raw".
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        var policy = await w.PolicyAsync("P-FAC9", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)]);
        var reinsurer = await w.PartyAsync("Fictional Re (გამოგონილი)", foreignId: "RE-FAC9", country: "DE");
        var froms = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            var from = $"2026-0{i}-01";
            froms.Add(from);
            var treaty = await w.TreatyAsync($"FAC-{i}", "facultative", from, $"2027-0{i}-01", [], cededPercent: 10m, reinsurer: reinsurer);
            // Placed by hand (a facultative treaty lists no class of its own: the placed line's class counts).
            await w.CessionAsync(policy.IssueRows[0], treaty, 10m, 36.50m, 36.50m, "2026-01-01", "2026-01-01", "2027-01-01");
        }
        var (client, _) = await site.SignedInAsync("accountant");
        await using var c = await w.Database.OpenAdminAsync();
        var label = "05.1 " + await c.ExecuteScalarAsync<string>("select name_ka from core.insurance_class where code = N'5.1'");
        var sheet = XlsxSheet.Read(await (await FileAsync(client)).Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
        var periods = new[] { 9, 10, 11, 12, 13, 39, 40, 41, 42 }.Select(r => XlsxSheet.Text(sheet[$"D{r}"])).ToList();
        Assert.Equal(froms.Select(f => $"{f[8..10]}.{f[5..7]}.{f[..4]} – {f[8..10]}.{f[5..7]}.2027"), periods.Order(StringComparer.Ordinal));
        Assert.Equal(9, periods.Distinct().Count());
        Assert.Equal([label, label, label, ""], new[] { "A9", "A39", "A42", "A14" }.Select(r => XlsxSheet.Text(sheet[r])));
        Assert.All(new[] { 9, 10, 11, 12, 13, 39, 40, 41, 42 }, r => Assert.Equal("Fictional Re (გამოგონილი)", XlsxSheet.Text(sheet[$"B{r}"])));
        // The footer moved below the added rows (42 → 46); nothing written between.
        Assert.StartsWith("გენერალური დირექტორი", XlsxSheet.Text(sheet["A46"]));
        Assert.All(new[] { "A43", "B43", "D43", "A44", "B44", "D44", "A45", "B45", "D45" }, r => Assert.Equal("", XlsxSheet.Text(sheet[r])));
        var page = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12"));
        foreach (var f in froms) Assert.Contains($"{f[8..10]}.{f[5..7]}.{f[..4]}", page);
        Assert.Contains("4 სტრიქონი ბადის ქვემოთ", page);
        Assert.Equal(9, ReportSite.Rows(page, label).Count);
    }

    [Fact]
    public async Task The_page_given_quarter_0099_Q4_falls_back_to_the_months_own_and_never_shows_1999()
    {
        // legacy: „the page: a refused quarter falls back to the month's own; it never reads 1999" (the GET's 400 is in RegulatoryPagesTests).
        var (site, _, _) = await TiedAsync(1, 1);
        await using var _s = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var page = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10&quarter=0099-Q4"));
        Assert.Contains("href=\"/accounting/regulatory/form6/xlsx?quarter=2026-Q4\"", page);
        Assert.Contains("საანგარიშო თარიღი: 31.12.2026", page);
        Assert.DoesNotContain("31.12.1999", page);
        Assert.DoesNotContain("1999-Q4", page);
        Assert.DoesNotContain("0099-Q4", page);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=0099-Q4")).StatusCode);
    }
}
