using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The HTTP checks of the month close against the real application on a test database of its own:
/// legacy scripts/address-guard-periods-check.ts (Codex H26, H28, H29, H30: an answer to "close month" is never taken from the address
/// bar; the current, an impossible and a closed month cannot be closed by the admin and an operator is refused, each press answered in its
/// own response without a cookie; no month closed), scripts/answer-parallel-check.ts (Codex H30, PR #23: presses at once each show their
/// own answer whatever answer cookies the browser holds; periods unchanged) and the settings-audit part of scripts/p6-close-test.ts (C11b).
/// The answer texts are those of the legacy i18n at 17c1f1f (the scripts' own snippets were older wordings of the same answers).
/// </summary>
public sealed partial class LegacyPeriodsHttpTests
{
    private static readonly DateOnly Current = new(Rates.TodayInTbilisi().Year, Rates.TodayInTbilisi().Month, 1);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<string> Shown(string html) =>
        [.. Ka.Periods.CloseOutcomes.Where(t => WebUtility.HtmlDecode(html).Contains(t.Value, StringComparison.Ordinal)).Select(t => t.Key)];

    private static async Task<string> PeriodsAsync(ReportSite site)
    {
        await using var c = await site.World.Database.OpenAdminAsync();
        return string.Join("|", await c.QueryAsync<string>(
            "select concat(convert(char(10), period_start, 23), ':', status, ':', convert(nvarchar(40), closed_at, 127)) from core.accounting_period order by period_start"));
    }

    /// <summary>A site whose months run from M−3 (a first policy from its 1st), M−3 closed by the real close.</summary>
    private static async Task<ReportSite> SiteAsync()
    {
        var site = await ReportSite.CreateAsync();
        var first = Current.AddMonths(-3);
        await site.World.PolicyAsync("P-ADDR", Iso(first), Iso(first.AddYears(1)), [new LineSpec("5.1", 365m, 365m)]);
        var accountant = new CurrentUser(site.World.Accountant, "test.accountant", "სატესტო", "accountant", "accountant");
        Assert.Equal("done", (await PeriodClose.CloseAsync(site.World.App, accountant, Iso(first))).Outcome);
        return site;
    }

    // ================================================================ scripts/address-guard-periods-check.ts

    [Fact]
    public async Task A_close_month_answer_is_never_taken_from_the_address_and_months_that_cannot_close_are_answered_and_closed_by_nobody()
    {
        await using var site = await SiteAsync();
        var (admin, _) = await site.SignedInAsync("admin");
        var (operatorClient, _) = await site.SignedInAsync("sales");
        var open = Iso(Current);
        var closed = Iso(Current.AddMonths(-3));
        var before = await PeriodsAsync(site);

        string[] odd = ["__proto__", "constructor", "toString", "hasOwnProperty", "nonsense"];
        var typed = odd.Select(v => $"close={v}")
            .Concat(["close=done&close=x", "close=forbidden", "close=invalid", $"close=earlier-open&month={open}", $"close=done&month={open}",
                $"close=already&month={open}", $"close=not-ended&month={open}", $"close=not-ended&month={closed}", $"close=already&month={closed}", $"close=done&month={closed}"])
            .Concat(new[] { "0000-01-01", "0000-12-01", "1899-12-01", "2101-01-01" }.Select(m => $"close=already&month={m}"))
            .Append("close=forbidden&ack=AbCdEfGh12345_-x");
        foreach (var q in typed)
        {
            var response = await admin.GetAsync($"/accounting/periods?{q}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(Shown(html).Count == 0 && !html.Contains("class=\"alert", StringComparison.Ordinal), $"typed /accounting/periods?{q} shows {string.Join(", ", Shown(html))}");
        }

        // The real form, as the admin sees it, submitted without JavaScript: each response shows its own answer.
        var page = await ReportSite.PageAsync(admin, "/accounting/periods");
        var oldestOpen = FormMonth().Match(page);
        Assert.True(oldestOpen.Success, "no close form on the periods page");
        Assert.Equal(Iso(Current.AddMonths(-2)), oldestOpen.Groups[1].Value);
        var presses = new (string Who, HttpClient Client, string Month, string Expect)[]
        {
            ("operator", operatorClient, oldestOpen.Groups[1].Value, "forbidden"),
            ("admin", admin, open, "not-ended"),
            ("admin", admin, "2026-13-01", "invalid"),
            ("admin", admin, "0000-01-01", "invalid"),
            ("admin", admin, closed, "already"),
        };
        foreach (var p in presses)
        {
            var response = await ReportSite.PostFormAsync(p.Client, "/accounting/periods?handler=Close", new() { ["period_start"] = p.Month }, "/accounting/periods");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([p.Expect], Shown(await response.Content.ReadAsStringAsync()));
            Assert.False(response.Headers.Contains("Set-Cookie"), $"{p.Who} presses close for {p.Month}: a cookie was set");
        }
        Assert.Equal(before, await PeriodsAsync(site));
    }

    [GeneratedRegex("name=\"period_start\" value=\"([^\"]+)\"")]
    private static partial Regex FormMonth();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    // ================================================================ scripts/answer-parallel-check.ts

    [Fact]
    public async Task Presses_of_close_month_at_once_each_show_their_own_answer_whatever_answer_cookies_the_browser_holds()
    {
        await using var site = await SiteAsync();
        var (accountantId, _, _) = await site.UserAsync("accountant");
        var token = await new Auth(site.World.App).CreateSessionAsync(accountantId);
        var client = site.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var thisMonth = Iso(Current);
        var before = await PeriodsAsync(site);

        // The page as the accountant's browser has it: its anti-forgery cookie and the form's token.
        var get = new HttpRequestMessage(HttpMethod.Get, "/accounting/periods");
        get.Headers.Add("Cookie", $"{Auth.Cookie}={token}");
        var pageResponse = await client.SendAsync(get);
        var html = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("name=\"period_start\"", html);
        var csrf = pageResponse.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith("csrf=", StringComparison.Ordinal));
        var formToken = WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value);

        foreach (var (held, count) in new[] { (0, 2), (7, 2), (8, 2), (0, 20) })
        {
            var jar = new Dictionary<string, string>(StringComparer.Ordinal) { [Auth.Cookie] = token, ["csrf"] = csrf["csrf=".Length..] };
            for (var i = 0; i < held; i++) jar[$"answer-{i}"] = $"old{i}.seal.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000 + i}";
            var cookie = string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
            var expect = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? "not-ended" : "invalid").ToArray();
            var responses = await Task.WhenAll(expect.Select(async e =>
            {
                var post = new HttpRequestMessage(HttpMethod.Post, "/accounting/periods?handler=Close")
                {
                    Content = new FormUrlEncodedContent([new("period_start", e == "not-ended" ? thisMonth : "2026-13-01"), new("__RequestVerificationToken", formToken)]),
                };
                post.Headers.Add("Cookie", cookie);
                var r = await client.SendAsync(post);
                return (r.StatusCode, Location: r.Headers.Location?.ToString(), Html: await r.Content.ReadAsStringAsync(),
                    SetCookie: r.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : []);
            }));
            // The browser applies every answer's Set-Cookie as it arrives; a redirect (as the earlier versions answered) is followed with them.
            foreach (var r in responses)
                foreach (var c in r.SetCookie)
                {
                    var pair = c.Split(';')[0];
                    var (name, value) = (pair[..pair.IndexOf('=', StringComparison.Ordinal)], pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..]);
                    if (value == "" || c.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)) jar.Remove(name); else jar[name] = value;
                }
            var misses = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var r = responses[i];
                var shown = r.StatusCode == HttpStatusCode.OK ? r.Html : "";
                if (r.Location is { } to)
                {
                    var follow = new HttpRequestMessage(HttpMethod.Get, to);
                    follow.Headers.Add("Cookie", string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}")));
                    shown = await (await client.SendAsync(follow)).Content.ReadAsStringAsync();
                }
                if (!WebUtility.HtmlDecode(shown).Contains(Ka.Periods.CloseOutcomes[expect[i]], StringComparison.Ordinal) || Shown(shown).Count != 1)
                    misses.Add($"{i}:{expect[i]}({(int)r.StatusCode}{(r.Location is null ? "" : " redirect")})");
            }
            Assert.True(misses.Count == 0, $"{count} presses at once, the browser holding {held} answer cookies: not shown {string.Join(", ", misses)}");
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.All(responses, r => Assert.Empty(r.SetCookie));
        }
        Assert.Equal(before, await PeriodsAsync(site));
    }

    // ================================================================ scripts/p6-close-test.ts C11b

    [Fact]
    public async Task P6_C11b_a_setting_saved_5_3_3_8_is_audited_insert_then_two_updates_each_by_the_admin()
    {
        await using var site = await ReportSite.CreateAsync();
        var (admin, adminId) = await site.SignedInAsync("admin");
        foreach (var value in new[] { "5", "3", "3", "8" })
        {
            var r = await ReportSite.PostFormAsync(admin, "/settings?handler=SaveSettings",
                new() { ["accounting.close_within_days"] = value, ["aml.high_risk_countries"] = "" }, "/settings");
            Assert.Equal("/settings?saved=1", r.Headers.Location?.ToString());
        }
        await using var c = await site.World.Database.OpenAdminAsync();
        var trail = await c.QueryAsync<(string Action, Guid? User, string? Before, string? After)>("""
            select action, user_id, json_value(old_row, '$.value'), json_value(new_row, '$.value') from core.audit_log
            where table_name = N'setting' and row_id = N'accounting.close_within_days' order by id
            """);
        Assert.Equal([("insert", (Guid?)adminId, (string?)null, (string?)"5"), ("update", adminId, "5", "3"), ("update", adminId, "3", "8")], trail);
    }
}
