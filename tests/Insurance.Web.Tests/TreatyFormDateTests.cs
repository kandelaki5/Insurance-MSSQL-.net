using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests;

/// <summary>
/// The treaty form's period dates in the shared dd/mm/yyyy field, and every date field of every form (legacy treaty-form-date.test.ts,
/// Codex PR224-TREATY-DATE-FORMAT): the real pages rendered over HTTP on a test database with fictional rows, then the page sources. A
/// date-only field is the shared field (a text field without a name showing dd/mm/yyyy, a hidden field with the form's name carrying the
/// ISO date, the browser's own date field nameless as its picker); the three date-and-time fields stay the browser's datetime-local.
/// </summary>
public sealed partial class TreatyFormDateTests(WebApp app) : IClassFixture<WebApp>
{
    [GeneratedRegex("<input [^>]*>")]
    private static partial Regex InputTag();

    [GeneratedRegex("<span class=\"date-input\"[^>]*>(.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex DateBox();

    private static List<string> Inputs(string html) => InputTag().Matches(html).Select(m => m.Value).ToList();

    private static string? Attr(string tag, string name) =>
        Regex.Match(tag, $"\\s{name}=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static bool Has(string tag, string name) => Regex.IsMatch(tag, $"\\s{name}(\\s|/|>|=)");

    [Fact]
    public async Task Period_from_and_period_to_show_dd_mm_yyyy_and_send_the_ISO_date_under_their_own_names()
    {
        var (client, _) = await app.SignedInAsync("admin");
        // A refused treaty (no name, no reinsurer) comes back with the periods as posted.
        var html = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/reinsurance/treaties/new",
            new() { ["kind"] = "quota_share", ["currency"] = "GEL", ["period_from"] = "2026-10-14", ["period_to"] = "2027-10-14", ["name"] = "", ["ceded_percent"] = "30", ["commission_percent"] = "0" }));
        Assert.Contains(ReinsuranceTexts.Errors.Name, html);
        var tags = Inputs(html);
        string?[] Field(string name)
        {
            var visible = tags.Single(t => Attr(t, "id") == name);
            var hidden = tags.Single(t => Attr(t, "type") == "hidden" && Attr(t, "name") == name);
            return [Attr(visible, "type"), Attr(visible, "value"), Attr(visible, "placeholder"), Attr(visible, "name"), Attr(hidden, "value")];
        }
        Assert.Equal([["text", "14/10/2026", "დდ/თთ/წწწწ", null, "2026-10-14"], ["text", "14/10/2027", "დდ/თთ/წწწწ", null, "2027-10-14"]], [Field("period_from"), Field("period_to")]);
        Assert.DoesNotContain(tags, t => Attr(t, "type") == "date" && Attr(t, "name") is not null);
        // The other fields as before: the number fields of a quota share and the plain text field keep their names.
        Assert.Equal([["name", "text"], ["ceded_percent", "number"], ["commission_percent", "number"]],
            new[] { "name", "ceded_percent", "commission_percent" }.Select(n => new[] { n, Attr(tags.First(t => Attr(t, "name") == n), "type") }));
        // The page opened afresh: the current year's first and last day, the same way.
        var fresh = Inputs(await Html.GetAsync(client, "/reinsurance/treaties/new"));
        var year = DateTime.Now.Year;
        Assert.Equal([$"{year}-01-01", $"{year}-12-31"],
            new[] { "period_from", "period_to" }.Select(n => Attr(fresh.Single(t => Attr(t, "type") == "hidden" && Attr(t, "name") == n), "value")));
    }

    /// <summary>The pages with forms, rendered as the administrator over fictional rows: every page's date-only fields (their names).</summary>
    private async Task<List<(string Url, string Html)>> PagesAsync()
    {
        var (client, admin) = await app.SignedInAsync("admin");
        await using var c = await app.Db.OpenAdminAsync();
        // A person and an organization (the owners' form is on an organization's card), a policy, a claim on it, a treaty.
        var person = Guid.NewGuid();
        var personTax = Fixtures.RandomTaxId();
        await c.ExecuteAsync("insert core.party (id, kind, first_name, last_name, tax_id, birth_date) values (@person, N'person', N'ეკა', N'თარიღაძე', @personTax, '1985-04-12')", new { person, personTax });
        var organization = Guid.NewGuid();
        await c.ExecuteAsync("insert core.party (id, kind, name, tax_id) values (@organization, N'organization', N'შპს თარიღი (გამოგონილი)', @tax)", new { organization, tax = Fixtures.RandomTaxId()[..9] });
        var (policy, claim) = await app.Db.App.AsUserAsync(admin, async tx =>
        {
            var p = await Fixtures.ActivePolicyAsync(tx, admin, Rates.TodayInTbilisi().AddDays(-30));
            var (claimId, _) = await ComplianceFixtures.ClaimAsync(tx, p);
            return (p, claimId);
        });
        var policyNumber = await c.ExecuteScalarAsync<string>("select number from core.policy where id = @id", new { id = policy.PolicyId });
        var reinsurer = Guid.NewGuid();
        await c.ExecuteAsync("insert core.party (id, kind, name, foreign_id, country_code) values (@reinsurer, N'organization', N'Re Date (გამოგონილი)', N'DE-DATE', N'DE')", new { reinsurer });
        var treaty = Guid.NewGuid();
        await app.Db.App.AsUserAsync(admin, tx => tx.ExecuteAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, commission_percent, status, created_by)
            values (@treaty, N'RI-DATE', N'კვოტა (გამოგონილი)', @reinsurer, N'quota_share', '2026-01-01', '2026-12-31', N'GEL', 30, 0, N'active', @admin);
            insert core.ri_treaty_class (treaty_id, class_code) values (@treaty, N'5.1');
            """, new { treaty, reinsurer, admin }));
        // A complaint, an intermediary and an AML case, registered through their forms.
        var today = Rates.TodayInTbilisi().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var complaint = Html.Location(await WebApp.PostFormAsync(client, "/complaints/new",
            new() { ["received_on"] = today, ["channel"] = "written", ["category"] = "4", ["customer_name"] = "უცნობი მომხმარებელი", ["description"] = "სხვა" })).Split('?')[0];
        var intermediary = Html.Location(await WebApp.PostFormAsync(client, "/intermediaries/new?handler=Save",
            new() { ["id"] = "", ["party_id_number"] = personTax, ["kind"] = "broker", ["contract_no"] = "B-DATE", ["contract_date"] = "2026-02-01", ["valid_to"] = "", ["commission_percent"] = "10", ["status"] = "active" },
            "/intermediaries/new")).Split('?')[0];
        var amlCase = Html.Location(await WebApp.PostFormAsync(client, "/compliance/aml-cases/new",
            new() { ["party_id_number"] = personTax, ["opened_on"] = "2026-03-10", ["source"] = "employee", ["class_code"] = "5", ["transaction_ref"] = "CAS-DATE", ["description"] = "ნაღდი 50 000" })).Split('?')[0];

        var urls = new[]
        {
            "/parties/new", "/parties/new?kind=organization", $"/parties/{person}", $"/parties/{organization}",
            "/policies/new", "/policies/import", $"/policies/{policy.PolicyId}/payment", $"/policies/{policy.PolicyId}/endorse", $"/policies/{policy.PolicyId}/cancel",
            $"/claims/new?policy={policyNumber}", $"/claims/{claim}",
            "/complaints/new", complaint, "/intermediaries/new", intermediary,
            "/compliance/aml-cases", "/compliance/aml-cases/new", amlCase,
            "/reinsurance/treaties/new", $"/reinsurance/treaties/{treaty}/bordereau",
            "/reference/rates", "/accounting/impairment", "/accounting/policy-premium", "/accounting/ri-commission", "/audit",
        };
        var pages = new List<(string, string)>();
        foreach (var url in urls) pages.Add((url, await Html.GetAsync(client, url)));
        return pages;
    }

    [Fact]
    public async Task Every_rendered_form_has_its_date_only_fields_in_the_shared_field_and_only_the_three_date_and_time_fields_apart()
    {
        var pages = await PagesAsync();
        var dateTime = new List<string>();
        var dateOnly = new Dictionary<string, List<string>>();
        foreach (var (url, html) in pages)
        {
            var tags = Inputs(html);
            // The browser's own date field: only as the shared field's nameless, hidden picker.
            foreach (var t in tags.Where(t => Attr(t, "type") == "date"))
            {
                Assert.True(Attr(t, "name") is null && Attr(t, "class") == "date-input-picker" && Attr(t, "tabindex") == "-1", $"{url}: {t}");
            }
            var boxes = DateBox().Matches(html).Select(m => Inputs(m.Groups[1].Value)).ToList();
            Assert.Equal(tags.Count(t => Attr(t, "type") == "date"), boxes.Count);
            foreach (var box in boxes)
            {
                // Each shared field: a nameless text field typed as dd/mm/yyyy, the name on the hidden ISO field, the picker.
                Assert.Equal(["text", "hidden", "date"], box.Select(t => Attr(t, "type")));
                Assert.True(Attr(box[0], "name") is null && Attr(box[0], "placeholder") == Ka.Common.DateFormat && Attr(box[0], "maxlength") == "10", $"{url}: {box[0]}");
                var iso = Attr(box[1], "value") ?? "";
                Assert.True(iso == "" || Regex.IsMatch(iso, @"^\d{4}-\d{2}-\d{2}$"), $"{url}: {box[1]}");
                Assert.Equal(iso == "" ? "" : $"{iso[8..10]}/{iso[5..7]}/{iso[..4]}", Attr(box[0], "value"));
                Assert.Equal(iso, Attr(box[2], "value"));
            }
            dateOnly[url] = boxes.Select(b => Attr(b[1], "name")!).ToList();
            dateTime.AddRange(tags.Where(t => Attr(t, "type") == "datetime-local").Select(t => $"{(url.StartsWith("/parties/", StringComparison.Ordinal) ? "/parties/{id}" : url.StartsWith("/compliance/aml-cases/", StringComparison.Ordinal) && url != "/compliance/aml-cases/new" ? "/compliance/aml-cases/{id}" : url)}:{Attr(t, "name")}"));
            // No other input carries a date-looking type.
            Assert.DoesNotContain(tags, t => Attr(t, "type") is "month" or "week" or "time");
        }
        // The three date-and-time fields: the AML decision's suspicion and report times, the FMS instruction's time (on the case list, the
        // client's card and the case's card, each section once).
        Assert.Equal(
            ["/compliance/aml-cases/{id}:instructed_at", "/compliance/aml-cases/{id}:str_sent_at", "/compliance/aml-cases/{id}:suspicion_at", 
             "/parties/{id}:instructed_at", "/parties/{id}:instructed_at"],
            dateTime.Order(StringComparer.Ordinal));
        Assert.Equal(["instructed_at", "str_sent_at", "suspicion_at"], dateTime.Select(d => d.Split(':')[1]).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(Expected, dateOnly.Select(p => $"{Shape(p.Key)}: {string.Join(",", p.Value)}"));
    }

    private static string Shape(string url) => Regex.Replace(url, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "{id}").Split("?policy=")[0];

    /// <summary>Each page's shared date fields by name, in the page's order (a field shown only in another state of the record — a claim's
    /// reversal or a recovery, say — is not on these pages; the source scan counts every one).</summary>
    private static readonly string[] Expected = [
        "/parties/new: birth_date,identified_at,verified_at,sanctions_checked_at",
        "/parties/new?kind=organization: identified_at,verified_at,sanctions_checked_at,self_declaration_on",
        "/parties/{id}: birth_date,identified_at,verified_at,sanctions_checked_at",
        "/parties/{id}: identified_at,verified_at,sanctions_checked_at,self_declaration_on,birth_date,document_issued_on,document_valid_to,identified_at,verified_at,sanctions_checked_at",
        "/policies/new: effective_from,effective_to",
        "/policies/import: effective_from,effective_to",
        "/policies/{id}/payment: received_on",
        "/policies/{id}/endorse: change_on",
        "/policies/{id}/cancel: cancel_on",
        "/claims/new: occurred_on,reported_on",
        "/claims/{id}: paid_on,booked_on,received_on",
        "/complaints/new: received_on",
        "/complaints/{id}: responded_on",
        "/intermediaries/new: contract_date,valid_to",
        "/intermediaries/{id}: contract_date,valid_to",
        "/compliance/aml-cases: ",
        "/compliance/aml-cases/new: opened_on",
        "/compliance/aml-cases/{id}: examined_on",
        "/reinsurance/treaties/new: period_from,period_to",
        "/reinsurance/treaties/{id}/bordereau: from,to",
        "/reference/rates: date",
        "/accounting/impairment: date",
        "/accounting/policy-premium: from,to",
        "/accounting/ri-commission: date",
        "/audit: from,to",
    ];

    [Fact]
    public void Every_date_field_of_the_page_sources_date_only_ones_in_the_shared_field_the_three_date_and_time_ones_apart()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Insurance.Web"));
        var pages = Path.Combine(root, "Pages");
        string Rel(string f) => Path.GetRelativePath(pages, f).Replace(Path.DirectorySeparatorChar, '/');
        var sources = Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories).Select(f => (File: Rel(f), Text: File.ReadAllText(f))).ToList();
        Assert.True(sources.Count > 40, $"{sources.Count} pages under {pages}");
        // Literal native date fields: none in the pages; only the shared field's own picker (the tag helper) writes one.
        Assert.Empty(sources.Where(s => Regex.IsMatch(s.Text, "type=\\\\?\"date\\\\?\"")).Select(s => s.File));
        var code = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/"))
            .Where(f => File.ReadAllText(f).Contains("type=\\\"date\\\"", StringComparison.Ordinal)).Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'));
        Assert.Equal(["Infrastructure/DateInputTagHelper.cs"], code);
        // Forms that ask their helper for a "date" field: each helper turns "date" into the shared field.
        var generic = sources.Where(s => Regex.IsMatch(s.Text, ",\\s*\"date\"\\)")).Select(s => s.File).Order(StringComparer.Ordinal).ToList();
        var routed = sources.Where(s => Regex.IsMatch(s.Text, "type == \"date\"\\)\\s*\\{\\s*<date-input")).Select(s => s.File).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Complaints/New.cshtml", "Intermediaries/_IntermediaryForm.cshtml", "Parties/Card.cshtml", "Parties/_PartyForm.cshtml", "Policies/New.cshtml"], generic);
        Assert.Equal(generic, routed);
        // The treaty form writes its periods with the shared field itself; its own field helper is never asked for a date.
        var treaty = sources.Single(s => s.File == "Reinsurance/Treaties/New.cshtml").Text;
        Assert.Equal(["period_from", "period_to"], Regex.Matches(treaty, "<date-input id=\"([a-z_]+)\" name=\"\\1\"").Select(m => m.Groups[1].Value));
        Assert.DoesNotMatch("Field\\([^)]*\"date", treaty);
        // The date-and-time fields: the three, apart.
        var dateTime = sources.SelectMany(s => Regex.Matches(s.Text, "name=\"([a-z_]+)\" type=\"datetime-local\"").Select(m => $"{s.File}:{m.Groups[1].Value}")).Order(StringComparer.Ordinal);
        Assert.Equal(["Compliance/AmlCases/Card.cshtml:str_sent_at", "Compliance/AmlCases/Card.cshtml:suspicion_at", "Compliance/_SuspensionSection.cshtml:instructed_at"], dateTime);
        Assert.Equal(3, sources.Sum(s => Regex.Matches(s.Text, "datetime-local").Count));
        // Every shared field in the pages, counted per page (the rendered test sees them filled).
        var shared = sources.Select(s => (s.File, Count: Regex.Matches(s.Text, "<date-input ").Count)).Where(s => s.Count > 0).Select(s => $"{s.File}:{s.Count}").Order(StringComparer.Ordinal);
        Assert.Equal(SharedFields, shared);
    }

    private static readonly string[] SharedFields = [
        "Accounting/Impairment.cshtml:1",
        "Accounting/PolicyPremium.cshtml:2",
        "Accounting/RiCommission.cshtml:3",
        "Audit/Index.cshtml:2",
        "Claims/Claim.cshtml:6",
        "Claims/New.cshtml:2",
        "Complaints/Card.cshtml:1",
        "Complaints/New.cshtml:1",
        "Compliance/AmlCases/Card.cshtml:1",
        "Compliance/AmlCases/New.cshtml:1",
        "Intermediaries/_IntermediaryForm.cshtml:1",
        "Parties/Card.cshtml:1",
        "Parties/_PartyForm.cshtml:1",
        "Policies/Cancel.cshtml:1",
        "Policies/Endorse.cshtml:1",
        "Policies/Import.cshtml:2",
        "Policies/New.cshtml:1",
        "Policies/Payment.cshtml:1",
        "Reference/Rates.cshtml:1",
        "Reinsurance/Treaties/Bordereau.cshtml:2",
        "Reinsurance/Treaties/New.cshtml:2",
    ];
}
