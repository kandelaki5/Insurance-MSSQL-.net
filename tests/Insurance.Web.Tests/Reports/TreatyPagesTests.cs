using System.Net;
using Dapper;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The treaty list, the new-treaty form with its refusals, the treaty card and its close, over HTTP (legacy reinsurance/treaties/page.tsx,
/// new/page.tsx + treaty-form.tsx, [id]/page.tsx, actions.ts createTreaty / closeTreaty; treaty-form-date.test.ts).
/// </summary>
public sealed class TreatyPagesTests
{
    private static Dictionary<string, string> Quota(string reinsurer, string percent = "40", params string[] classes)
    {
        var form = new Dictionary<string, string>
        {
            ["name"] = "  კვოტა 40% (სატესტო)  ", ["kind"] = "quota_share", ["currency"] = "GEL", ["reinsurer_id_number"] = reinsurer, ["broker_id_number"] = "",
            ["period_from"] = "2026-01-01", ["period_to"] = "2026-12-31", ["ceded_percent"] = percent, ["commission_percent"] = "12,5", ["broker_percent"] = "",
            ["reinsurer_rating"] = "A+", ["rating_agency"] = "S&P", ["note"] = "",
        };
        foreach (var c in classes.Length == 0 ? ["5"] : classes) form["class_" + c] = "on";
        return form;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, Dictionary<string, string> form) =>
        ReportSite.PostFormAsync(client, "/reinsurance/treaties/new", form);

    [Fact]
    public async Task A_treaty_is_created_with_its_classes_and_shown_on_its_card_and_in_the_list()
    {
        await using var site = await ReportSite.CreateAsync();
        await site.World.PartyAsync("გადამზღვეველი (გამოგონილი)", taxId: "204000111");
        var (client, userId) = await site.SignedInAsync("accountant");
        var form = await ReportSite.PageAsync(client, "/reinsurance/treaties/new");
        Assert.Contains("value=\"01/01/" + DateTime.Now.Year, form);
        Assert.Contains("name=\"period_from\" value=\"" + DateTime.Now.Year + "-01-01\"", form);
        var response = await CreateAsync(client, Quota("204000111", "40", "5", "13"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Matches("^/reinsurance/treaties/[0-9a-f-]{36}\\?saved=1$", location);
        await using var c = await site.World.Database.OpenAdminAsync();
        var t = await c.QuerySingleAsync<(string Number, string Name, string Kind, decimal? Ceded, decimal Commission, decimal Broker, string Rating, Guid By)>(
            "select number, name, kind, ceded_percent, commission_percent, broker_percent, reinsurer_rating, created_by from core.ri_treaty");
        Assert.Equal(("RI000001", "კვოტა 40% (სატესტო)", "quota_share", (decimal?)40m, 12.5m, 0m, "A+", userId), t);
        Assert.Equal(["13", "5"], await c.QueryAsync<string>("select class_code from core.ri_treaty_class order by class_code"));
        var card = ReportSite.Decode(await ReportSite.PageAsync(client, location));
        Assert.Contains(ReinsuranceTexts.Saved, card);
        Assert.Contains("RI000001 · კვოტა 40% (სატესტო)", card);
        Assert.Contains($"{ReinsuranceTexts.Fields.CededPercent.Replace(", %", "")}: 40 %", card);
        Assert.Contains("A+ (S&P)", card);
        Assert.Contains("5. სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა (გარდა სარკინიგზო ტრანსპორტისა); 13. ", card);
        Assert.Contains(ReinsuranceTexts.Cessions.None, card);
        var list = ReportSite.Decode(await ReportSite.PageAsync(client, "/reinsurance/treaties"));
        Assert.Equal(["RI000001", "კვოტა 40% (სატესტო)", "გადამზღვეველი (გამოგონილი)", "კვოტური", "01.01.2026 – 31.12.2026", "40.0000 %", "13, 5", "0.00", "მოქმედი"],
            ReportSite.Cells(list, "RI000001"));
    }

    [Fact]
    public async Task A_refused_form_says_why_and_keeps_what_was_typed_and_ticked()
    {
        await using var site = await ReportSite.CreateAsync();
        await site.World.PartyAsync("გადამზღვეველი (გამოგონილი)", taxId: "204000111");
        var (client, _) = await site.SignedInAsync("accountant");
        var bad = Quota("999999999", "140", "13");
        bad["name"] = "";
        bad["period_to"] = "2025-12-31";
        bad["commission_percent"] = "-1";
        var html = ReportSite.Decode(await (await CreateAsync(client, bad)).Content.ReadAsStringAsync());
        foreach (var e in new[] { ReinsuranceTexts.Errors.Name, ReinsuranceTexts.Errors.Reinsurer, ReinsuranceTexts.Errors.Period, ReinsuranceTexts.Errors.CededPercent, ReinsuranceTexts.Errors.Percent })
            Assert.Contains($"<div class=\"field-error\">{e}</div>", html);
        Assert.Contains("id=\"class_13\" name=\"class_13\" type=\"checkbox\" checked=\"checked\"", html);
        Assert.Contains("value=\"999999999\"", html);
        // No class ticked for an obligatory treaty.
        var noClass = Quota("204000111");
        noClass.Remove("class_5");
        Assert.Contains(ReinsuranceTexts.Errors.Classes, ReportSite.Decode(await (await CreateAsync(client, noClass)).Content.ReadAsStringAsync()));
        // Surplus and excess of loss terms.
        var surplus = Quota("204000111");
        surplus["kind"] = "surplus";
        Assert.Contains(ReinsuranceTexts.Errors.Surplus, ReportSite.Decode(await (await CreateAsync(client, surplus)).Content.ReadAsStringAsync()));
        var xl = Quota("204000111");
        xl["kind"] = "excess_of_loss";
        Assert.Contains(ReinsuranceTexts.Errors.Priority, ReportSite.Decode(await (await CreateAsync(client, xl)).Content.ReadAsStringAsync()));
        await using var c = await site.World.Database.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.ri_treaty"));
    }

    [Fact]
    public async Task Overlapping_obligatory_treaties_are_refused_when_entered()
    {
        await using var site = await ReportSite.CreateAsync();
        await site.World.PartyAsync("გადამზღვეველი (გამოგონილი)", taxId: "204000111");
        var (client, _) = await site.SignedInAsync("accountant");
        Assert.Equal(HttpStatusCode.Redirect, (await CreateAsync(client, Quota("204000111", "60", "5"))).StatusCode);
        // 60 % + 50 % on the parent class 5 and its 5.1 — over 100 %.
        Assert.Contains(ReinsuranceTexts.Errors.OverCeded, ReportSite.Decode(await (await CreateAsync(client, Quota("204000111", "50", "5"))).Content.ReadAsStringAsync()));
        var xl = Quota("204000111", "", "5");
        xl["kind"] = "excess_of_loss";
        xl["priority"] = "1000";
        Assert.Contains(ReinsuranceTexts.Errors.XolWithProportional, ReportSite.Decode(await (await CreateAsync(client, xl)).Content.ReadAsStringAsync()));
        var xl13 = Quota("204000111", "", "13");
        xl13["kind"] = "excess_of_loss";
        xl13["priority"] = "1000";
        xl13["treaty_limit"] = "5000";
        xl13["treaty_premium"] = "1200";
        Assert.Equal(HttpStatusCode.Redirect, (await CreateAsync(client, xl13)).StatusCode);
        Assert.Contains(ReinsuranceTexts.Errors.XolLayers, ReportSite.Decode(await (await CreateAsync(client, xl13)).Content.ReadAsStringAsync()));
        // A facultative treaty needs no class and is not checked against the others.
        var fac = Quota("204000111", "70");
        fac["kind"] = "facultative";
        fac.Remove("class_5");
        Assert.Equal(HttpStatusCode.Redirect, (await CreateAsync(client, fac)).StatusCode);
        await using var c = await site.World.Database.OpenAdminAsync();
        Assert.Equal(["excess_of_loss|1000.00|5000.00|1200.00", "facultative|||", "quota_share|||"],
            (await c.QueryAsync<string>("select concat(kind, N'|', priority, N'|', treaty_limit, N'|', treaty_premium) from core.ri_treaty order by kind")));
    }

    [Fact]
    public async Task A_viewer_cannot_create_or_close_and_the_writer_closes_a_treaty_once()
    {
        await using var site = await ReportSite.CreateAsync();
        var treaty = await site.World.TreatyAsync("RI-CLOSE", "quota_share", "2026-01-01", "2026-12-31", ["5"], cededPercent: 30m);
        var (viewer, _) = await site.SignedInAsync("viewer");
        var newPage = await viewer.GetAsync("/reinsurance/treaties/new");
        Assert.Equal((HttpStatusCode.Redirect, "/reinsurance/treaties"), (newPage.StatusCode, newPage.Headers.Location?.ToString()));
        Assert.DoesNotContain("href=\"/reinsurance/treaties/new\"", await ReportSite.PageAsync(viewer, "/reinsurance/treaties"));
        var card = await ReportSite.PageAsync(viewer, $"/reinsurance/treaties/{treaty}");
        Assert.DoesNotContain(ReinsuranceTexts.Actions.Close, ReportSite.Decode(card));
        // The viewer's forged close changes nothing.
        await ReportSite.PostFormAsync(viewer, $"/reinsurance/treaties/{treaty}?handler=Close", new() { ["id"] = treaty.ToString() }, "/reinsurance/treaties");
        await using var c = await site.World.Database.OpenAdminAsync();
        Assert.Equal("active", await c.ExecuteScalarAsync<string>("select status from core.ri_treaty where id = @treaty", new { treaty }));
        var (writer, _) = await site.SignedInAsync("sales");
        var closed = await ReportSite.PostFormAsync(writer, $"/reinsurance/treaties/{treaty}?handler=Close", new() { ["id"] = treaty.ToString() }, $"/reinsurance/treaties/{treaty}");
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.Equal("closed", await c.ExecuteScalarAsync<string>("select status from core.ri_treaty where id = @treaty", new { treaty }));
        Assert.Contains(ReinsuranceTexts.Statuses["closed"], ReportSite.Decode(await ReportSite.PageAsync(writer, $"/reinsurance/treaties/{treaty}")));
        Assert.DoesNotContain(ReinsuranceTexts.Actions.Close, ReportSite.Decode(await ReportSite.PageAsync(writer, $"/reinsurance/treaties/{treaty}")));
        // A viewer posting the form: the read-only message, nothing written.
        var refused = ReportSite.Decode(await (await ReportSite.PostFormAsync(viewer, "/reinsurance/treaties/new", Quota("204000111"), "/reinsurance/treaties")).Content.ReadAsStringAsync());
        Assert.Contains(Ka.Common.ReadOnly, refused);
    }

    [Fact]
    public async Task The_card_lists_the_cessions_with_their_totals_and_an_unknown_id_is_not_found()
    {
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        var p = await w.PolicyAsync("P-CARD", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 365m, 365m)]);
        var treaty = await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5"], cededPercent: 30m, commissionPercent: 20m);
        await w.CedeAsync(p.IssueRows[0]);
        var cancel = await w.TxAsync(p.Lines[0], "cancellation", -273m, -273m, "2026-10-01", "2026-10-01", "2027-07-01", p.IssueRows[0]);
        await w.WriteAsync(async tx => { await Insurance.Application.Services.ReinsuranceStore.CedeCancellationAsync(tx, cancel, w.Admin); return 0; });
        var (client, _) = await site.SignedInAsync("viewer");
        var card = ReportSite.Decode(await ReportSite.PageAsync(client, $"/reinsurance/treaties/{treaty}"));
        Assert.Equal(["P-CARD", "5.1", "01.07.2026", "01.07.2026 – 01.07.2027", "30", "109.50 GEL", "109.50", "21.90"], ReportSite.Cells(card, "P-CARD"));
        Assert.Equal("-81.90 GEL", ReportSite.Cells(card, "P-CARD (" + PolicyTexts.Status["cancelled"] + ")")[5]);
        Assert.Equal(["სულ", "27.60", "5.52"], ReportSite.TotalRows(card).Single());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reinsurance/treaties/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/reinsurance/treaties/not-a-uuid")).StatusCode);
        var list = ReportSite.Decode(await ReportSite.PageAsync(client, "/reinsurance/treaties"));
        Assert.Equal("27.60", ReportSite.Cells(list, "RI030")[7]);
    }
}
