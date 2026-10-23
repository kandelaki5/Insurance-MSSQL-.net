using System.Net;
using System.Text.RegularExpressions;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The payment plans in the two real forms an operator chooses one in — a new policy's and an import's — over HTTP on
/// the real pages (legacy scripts/integration/payment-plan-forms-render.test.ts). The owner's standard plans are 1, 4, 11
/// and 12 (13.10.2026; docs/05 a16); two payments is an individual schedule, not offered. The options and labels below
/// are written out by hand, not read from the texts, so a change of either is caught. The legacy test rendered the
/// React components with an empty "initial"; the pages here are rendered as the legacy pages called them (initial
/// installments '1'), and an explicit plan is checked on the form shown again after a refusal.
/// </summary>
public sealed partial class GapsPaymentPlanFormsTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private static readonly (string Value, string Label)[] Plans =
    [
        ("1", "1 — 12 თვე ერთად"),
        ("4", "4 — კვარტალურად"),
        ("11", "11 — პირველი და ბოლო თვე წინასწარ, დანარჩენი თვეობით"),
        ("12", "12 — ყოველთვიურად"),
    ];

    [GeneratedRegex(@"<select\b([^>]*)>([\s\S]*?)</select>")]
    private static partial Regex Selects();

    [GeneratedRegex(@"<option\b([^>]*)>([\s\S]*?)</option>")]
    private static partial Regex Options();

    [GeneratedRegex(@"<label for=""installments"">([\s\S]*?)</label>")]
    private static partial Regex Labels();

    [GeneratedRegex(@"\bid=""installments""")]
    private static partial Regex Ids();

    private static string? Attr(string attrs, string name) => Regex.Match(attrs, $@"\b{name}=""([^""]*)""") is { Success: true } m ? m.Groups[1].Value : null;

    private sealed record Control(int Selects, int Ids, IReadOnlyList<string> Labels, IReadOnlyList<(string? Value, string Label)> Options, IReadOnlyList<(string? Value, string Label)> Selected);

    /// <summary>The installments control as rendered: how many selects and ids it has, its label(s), its options in order, the selected ones.</summary>
    private static Control Installments(string html)
    {
        var selects = Selects().Matches(html).Where(m => Attr(m.Groups[1].Value, "name") == "installments").ToList();
        var options = selects.Count == 0 ? [] : Options().Matches(selects[0].Groups[2].Value)
            .Select(o => (Value: Attr(o.Groups[1].Value, "value"), Label: WebUtility.HtmlDecode(o.Groups[2].Value), Selected: Regex.IsMatch(o.Groups[1].Value, @"\bselected\b"))).ToList();
        Assert.All(selects, s => Assert.Equal("installments", Attr(s.Groups[1].Value, "id")));
        return new Control(selects.Count, Ids().Matches(html).Count, Labels().Matches(html).Select(l => WebUtility.HtmlDecode(l.Groups[1].Value)).ToList(),
            options.Select(o => (o.Value, o.Label)).ToList(), options.Where(o => o.Selected).Select(o => (o.Value, o.Label)).ToList());
    }

    private static List<(string?, string)> PlanList => Plans.Select(p => ((string?)p.Value, p.Label)).ToList();

    [Fact]
    public async Task The_new_policys_form_offers_1_4_11_and_12_with_their_Georgian_names_no_blank_and_no_other_an_explicit_plan_stays_chosen()
    {
        var (sales, _) = await app.SignedInAsync("sales");
        var page = Installments(await HtmlAsync(sales, "/policies/new"));
        // First check, by hand: one control, its label, the four standard plans in order — nothing else.
        Assert.Equal((1, 1), (page.Selects, page.Ids));
        Assert.Equal(["შენატანების რაოდენობა"], page.Labels);
        Assert.Equal(PlanList, page.Options);
        // The page opens on 1 (the legacy page's initial installments '1').
        Assert.Equal([PlanList[0]], page.Selected);
        // Each explicit plan, 11 included, is the one selected option on the form shown again (a refusal: no make).
        var (product, _) = await app.W.CascoAsync();
        foreach (var (plan, label) in Plans)
        {
            var refused = await WebApp.PostFormAsync(sales, "/policies/new", new()
            {
                ["product_id"] = product.ToString(), ["policyholder_id_number"] = "000000000", ["make"] = "", ["installments"] = plan,
                ["effective_from"] = "2026-10-01", ["effective_to"] = "2027-10-01", ["currency"] = "GEL",
            });
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var shown = Installments(await refused.Content.ReadAsStringAsync());
            Assert.Equal(PlanList, shown.Options);
            Assert.Equal([(plan, label)], shown.Selected);
        }
        Assert.Equal(0, await app.W.ScalarAsync<int>("select count(*) from core.policy"));
    }

    [Fact]
    public async Task The_imports_form_offers_1_4_11_and_12_with_their_Georgian_names_1_when_nothing_is_given_an_explicit_plan_stays_chosen()
    {
        var (sales, _) = await app.SignedInAsync("sales");
        var page = Installments(await HtmlAsync(sales, "/policies/import"));
        Assert.Equal((1, 1), (page.Selects, page.Ids));
        Assert.Equal(["შენატანების რაოდენობა (თუ ფაილში ცარიელია)"], page.Labels);
        Assert.Equal(PlanList, page.Options);
        Assert.Equal([PlanList[0]], page.Selected);
        foreach (var (plan, label) in Plans)
        {
            // The preview refused (no client found): the form shown again keeps the plan chosen.
            var refused = await PostMultipartAsync(sales, "/policies/import?handler=Preview",
                new() { ["policyholder_id_number"] = "000000000", ["installments"] = plan, ["currency"] = "GEL" }, null, "/policies/import");
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var shown = Installments(await refused.Content.ReadAsStringAsync());
            Assert.Equal(PlanList, shown.Options);
            Assert.Equal([(plan, label)], shown.Selected);
        }
        // Nothing given: 1.
        var none = await PostMultipartAsync(sales, "/policies/import?handler=Preview", new() { ["policyholder_id_number"] = "000000000" }, null, "/policies/import");
        Assert.Equal([PlanList[0]], Installments(await none.Content.ReadAsStringAsync()).Selected);
        Assert.Equal(0, await app.W.ScalarAsync<int>("select count(*) from core.policy"));
    }
}
