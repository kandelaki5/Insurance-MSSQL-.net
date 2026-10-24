using System.Net;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using Insurance.Integration.Tests;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The bordereau of a treaty over HTTP: the page and its Excel file (legacy reinsurance/treaties/[id]/bordereau/page.tsx and
/// bordereau/xlsx/route.ts over lib/bordereau.ts). The world is written by the real writers: a USD and a GEL policy ceded 40 % by approval's
/// writer, the GEL one cancelled by the cancellation writer (its cession mirror), a USD claim with its treaty share, a reserve and a payment.
/// The legacy has no test of the bordereau; the expected figures are worked by hand from buildBordereau's rules.
/// </summary>
public sealed class BordereauPagesTests
{
    private sealed record World(ReportSite Site, Guid Treaty);

    private static async Task<World> WorldAsync()
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.RateAsync("USD", "2026-03-01", 2.7m);
        await w.RateAsync("USD", "2026-04-30", 2.8m);
        var broker = await w.PartyAsync("ბროკერი (გამოგონილი)", foreignId: "BR-1");
        var treaty = await w.TreatyAsync("RI-B1", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 40m, commissionPercent: 10m, brokerPercent: 2.5m, broker: broker);
        var usd = await w.PolicyAsync("P-B1", "2026-03-01", "2027-03-01", [new LineSpec("5.1", 365m, 985.50m, 20000m, "USD")], currency: "USD", rate: 2.7m);
        await w.CedeAsync(usd.IssueRows[0]);
        var gel = await w.PolicyAsync("P-B2", "2026-03-10", "2027-03-10", [new LineSpec("5.1", 365m, 365m)]);
        await w.CedeAsync(gel.IssueRows[0]);
        Assert.Equal(PolicyCancel.CancelOutcome.Done,
            (await w.WriteAsync(tx => PolicyCancel.CancelPolicyInTxAsync(tx, gel.Id, D("2026-04-10"), "სატესტო გაუქმება", w.Admin))).Outcome);
        var claim = await w.ClaimAsync(usd.Id, usd.Lines[0], "USD", "2026-03-15", "2026-03-16", number: "CL-B1");
        await w.AttachClaimCessionsAsync(claim.Id, usd.Lines[0], "5.1", "2026-03-15");
        await w.ReserveAsync(claim.Id, "2026-03-16", 1000m);
        await w.ClaimPaymentAsync(claim.Id, "2026-04-05", 300m, 2.75m);
        await w.ReserveAsync(claim.Id, "2026-04-05", -300m);
        return new World(site, treaty);
    }

    [Fact]
    public async Task The_page_and_its_file_show_the_ceded_premium_the_claims_share_and_the_balance()
    {
        var (site, treaty) = await WorldAsync();
        await using var owner = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var url = $"/reinsurance/treaties/{treaty}/bordereau?from=2026-03-01&to=2026-04-30";
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, url));
        var bal = ReinsuranceTexts.Bordereau.Balance;
        Assert.Contains("<h1>ბორდერო: RI-B1 · ხელშეკრულება RI-B1</h1>", html);
        Assert.Contains($"<dd>გადამზღვეველი RI-B1 (გამოგონილი) · {ReinsuranceTexts.Fields.Broker}: ბროკერი (გამოგონილი)</dd>", html);
        Assert.Contains("<dd>01.03.2026 – 30.04.2026</dd>", html);
        Assert.Contains($"<dt>{bal["cededGel"]}</dt><dd>406.60</dd>", html);
        Assert.Contains($"<dt>{bal["commissionGel"]}</dt><dd>40.66</dd>", html);
        // 2.5 % of 406.60 is 10.165: the half rounds up, as Math.round did.
        Assert.Contains($"<dt>{bal["brokerageGel"]}</dt><dd>10.17 (2.5 %)</dd>", html);
        Assert.Contains($"<dt>{bal["claimsPaidGel"]}</dt><dd>330.00</dd>", html);
        Assert.Contains($"<dt>{bal["reserveGel"]}</dt><dd>784.00</dd>", html);
        Assert.Contains($"<dt>{bal["balanceGel"]}</dt><dd><strong>25.77</strong></dd>", html);
        var p = ReinsuranceTexts.Bordereau.Premium;
        Assert.Equal(["P-B1", "დამზღვევი P-B1 (გამოგონილი)", "", "5.1", "01.03.2026 – 01.03.2027", "20,000.00 USD", "40", "146.00 USD", "394.20", "39.42", "01.03.2026", p["issue"]],
            ReportSite.Cells(html, "P-B1"));
        Assert.Equal([
            $"P-B2|დამზღვევი P-B2 (გამოგონილი)||5.1|10.03.2026 – 10.03.2027||40|146.00 GEL|146.00|14.60|10.03.2026|{p["issue"]}",
            $"P-B2|დამზღვევი P-B2 (გამოგონილი)||5.1|10.04.2026 – 10.03.2027||40|-133.60 GEL|-133.60|-13.36|10.04.2026|{p["cancellation"]}",
        ], ReportSite.Rows(html, "P-B2").Select(r => string.Join("|", r)));
        Assert.Equal(["CL-B1", "P-B1", "15.03.2026", "16.03.2026", ClaimTexts.Status["in_review"], "120.00 USD", "330.00", "280.00 USD", "784.00"], ReportSite.Cells(html, "CL-B1"));
        Assert.Contains($"href=\"/reinsurance/treaties/{treaty}/bordereau/xlsx?from=2026-03-01&to=2026-04-30\"", html);

        var file = await client.GetAsync($"/reinsurance/treaties/{treaty}/bordereau/xlsx?from=2026-03-01&to=2026-04-30");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(ReportWorkbooks.XlsxType, file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment; filename=\"bordereau-RI-B1-2026-03-01-2026-04-30.xlsx\"", file.Content.Headers.GetValues("Content-Disposition").Single());
        var book = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync());
        Assert.Equal(["Premium", "Claims", "Balance"], book.SheetNames);
        var premium = XlsxSheet.Rows(book, "Premium");
        Assert.Equal(new[] { "policyNumber", "policyholder", "object", "classCode", "coverFrom", "coverTo", "sumInsured", "currency", "sharePercent", "cededPremium", "cededPremiumGel", "commissionGel", "bookedOn", "kind" }
            .Select(k => p[k]), premium[0]);
        Assert.Equal([
            $"P-B1|დამზღვევი P-B1 (გამოგონილი)||5.1|2026-03-01|2027-03-01|20000|USD|40|146|394.2|39.42|2026-03-01|{p["issue"]}",
            $"P-B2|დამზღვევი P-B2 (გამოგონილი)||5.1|2026-03-10|2027-03-10||GEL|40|146|146|14.6|2026-03-10|{p["issue"]}",
            $"P-B2|დამზღვევი P-B2 (გამოგონილი)||5.1|2026-04-10|2027-03-10||GEL|40|-133.6|-133.6|-13.36|2026-04-10|{p["cancellation"]}",
        ], premium.Skip(1).Select(r => string.Join("|", r)));
        var c = ReinsuranceTexts.Bordereau.Claims;
        var claims = XlsxSheet.Rows(book, "Claims");
        Assert.Equal(new[] { "claimNumber", "policyNumber", "occurredOn", "reportedOn", "status", "currency", "paidShare", "paidShareGel", "reserveShare", "reserveShareGel" }.Select(k => c[k]), claims[0]);
        Assert.Equal($"CL-B1|P-B1|2026-03-15|2026-03-16|{ClaimTexts.Status["in_review"]}|USD|120|330|280|784", string.Join("|", claims[1]));
        Assert.Equal(2, claims.Count);
        Assert.Equal([
            $"{bal["treaty"]}|RI-B1 · ხელშეკრულება RI-B1", $"{bal["reinsurer"]}|გადამზღვეველი RI-B1 (გამოგონილი)", $"{bal["period"]}|2026-03-01 – 2026-04-30",
            $"{bal["cededGel"]}|406.6", $"{bal["commissionGel"]}|40.66", $"{bal["brokerageGel"]}|10.17", $"{bal["claimsPaidGel"]}|330", $"{bal["reserveGel"]}|784", $"{bal["balanceGel"]}|25.77",
        ], XlsxSheet.Rows(book, "Balance").Select(r => string.Join("|", r)));

        // April alone: only the cancellation, and the share of the payment made in April.
        var april = ReportSite.Decode(await ReportSite.PageAsync(client, $"/reinsurance/treaties/{treaty}/bordereau?from=2026-04-01&to=2026-04-30"));
        Assert.Contains($"<dt>{bal["cededGel"]}</dt><dd>-133.60</dd>", april);
        Assert.Equal("120.00 USD", ReportSite.Cells(april, "CL-B1")[5]);
        Assert.Empty(ReportSite.Cells(april, "P-B1"));
    }

    [Fact]
    public async Task Bad_requests_are_refused_unknown_treaties_are_not_found_and_the_period_defaults_to_this_month()
    {
        var (site, treaty) = await WorldAsync();
        await using var owner = site;
        var anonymous = site.Client();
        var signedOut = await anonymous.GetAsync($"/reinsurance/treaties/{treaty}/bordereau/xlsx?from=2026-03-01&to=2026-04-30");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.StartsWith("/login", signedOut.Headers.Location!.ToString());

        // Like the legacy route, any signed-in user may read it (requireUser only).
        var (viewer, _) = await site.SignedInAsync("viewer");
        foreach (var query in new[] { "from=2026-03-01", "to=2026-04-30", "from=2026-04-31&to=2026-05-01", "from=2026-05-01&to=2026-04-30", "from=20260301&to=2026-04-30" })
        {
            var r = await viewer.GetAsync($"/reinsurance/treaties/{treaty}/bordereau/xlsx?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("Bad request", await r.Content.ReadAsStringAsync());
        }
        var badId = await viewer.GetAsync("/reinsurance/treaties/not-a-uuid/bordereau/xlsx?from=2026-03-01&to=2026-04-30");
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        var unknown = await viewer.GetAsync($"/reinsurance/treaties/{Guid.NewGuid()}/bordereau/xlsx?from=2026-03-01&to=2026-04-30");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Not found", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/reinsurance/treaties/{Guid.NewGuid()}/bordereau")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/reinsurance/treaties/not-a-uuid/bordereau")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/reinsurance/treaties/{treaty}/bordereau/xlsx?from=2026-03-01&to=2026-04-30")).StatusCode);

        var today = Rates.TodayInTbilisi();
        var first = new DateOnly(today.Year, today.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        string Link(DateOnly f, DateOnly t) => $"/bordereau/xlsx?from={f:yyyy-MM-dd}&amp;to={t:yyyy-MM-dd}\"";
        var plain = await ReportSite.PageAsync(viewer, $"/reinsurance/treaties/{treaty}/bordereau");
        Assert.Contains(Link(first, last), plain);
        Assert.Contains(ReinsuranceTexts.Bordereau.NoRows, ReportSite.Decode(plain));
        // A to before the from (or not a date) falls back to this month's end, a from not a date to this month's start.
        Assert.Contains(Link(new DateOnly(2026, 4, 1), last), await ReportSite.PageAsync(viewer, $"/reinsurance/treaties/{treaty}/bordereau?from=2026-04-01&to=2026-03-31"));
        Assert.Contains(Link(first, new DateOnly(today.Year + 1, 1, 1)), await ReportSite.PageAsync(viewer, $"/reinsurance/treaties/{treaty}/bordereau?from=x&to={today.Year + 1}-01-01"));
    }
}
