using System.Net;
using Dapper;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests;

/// <summary>
/// The rates page's form over HTTP (legacy src/app/(app)/reference/rates/actions.ts loadRates → Pages/Reference/Rates OnPostAsync): a
/// writer's date loaded from the National Bank and stored, answered „კურსები ჩაიტვირთა" and shown in the table; a day without rates, a
/// failing bank and a date that is not yyyy-mm-dd answered „ეროვნული ბანკიდან კურსის მიღება ვერ მოხერხდა"; a viewer neither sees the form
/// nor loads anything. The bank is <see cref="AnsweringNbg"/>: offline, every request recorded.
/// </summary>
public sealed class LoadRatesActionTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly AnsweringNbg _nbg = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Insurance", _db.AppConnection);
            b.ConfigureTestServices(s => s.AddSingleton<IHttpClientFactory>(_nbg));
        });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<HttpClient> SignedInAsync(string role)
    {
        var id = Guid.NewGuid();
        var username = $"test.{role}.{id.ToString("N")[..8]}";
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await using (var c = await _db.OpenAdminAsync())
            await c.ExecuteAsync("""
                insert core.app_user (id, username, full_name, password_hash, role_code, branch_id)
                values (@id, @username, @fullName, @hash, @role, (select id from core.branch where code = N'HQ'))
                """, new { id, username, fullName = $"სატესტო {role}", hash = Password.HashPassword(password), role });
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password })).StatusCode);
        return client;
    }

    private async Task<string> CacheAsync()
    {
        await using var c = await _db.OpenAdminAsync();
        return string.Join("; ", await c.QueryAsync<string>(
            "select concat(rtrim(currency), ' ', convert(nchar(10), rate_date, 23), ' ', rate) from core.exchange_rate order by rate_date, currency"));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string date) =>
        WebApp.PostFormAsync(client, "/reference/rates", new() { ["date"] = date });

    private static string Alert(string html, string kind)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, $"<div class=\"alert {kind}\">([^<]*)</div>");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("accountant")]
    public async Task A_writers_date_is_loaded_stored_answered_loaded_and_shown_in_the_table(string role)
    {
        var client = await SignedInAsync(role);
        var html = await Html.BodyAsync(await PostAsync(client, "2026-07-31"));
        Assert.Equal((Ka.Rates.Loaded, ""), (Alert(html, "ok"), Alert(html, "error")));
        Assert.Equal(["GET https://nbg.gov.ge/gw/api/ct/monetarypolicy/currencies/ka/json/?currencies=USD&currencies=EUR&date=2026-07-31"],
            _nbg.Requests.Select(r => $"{r.Method} {r.Url}"));
        Assert.Equal("EUR 2026-07-31 3.500000; USD 2026-07-31 3.141500", await CacheAsync());
        // The table: the date as dd.mm.yyyy, USD and EUR to four places (legacy Number(rate).toFixed(4)).
        Assert.Equal(["31.07.2026", "3.1415", "3.5000"], Reports.ReportSite.Cells(html, "31.07.2026"));
        // The form shows the date as posted.
        Assert.Contains("value=\"31/07/2026\"", html);
        Assert.Contains("type=\"hidden\" name=\"date\" value=\"2026-07-31\"", html);
        // The same date again: the bank asked again, the stored rates replaced (an upsert), still one row per currency.
        _nbg.Answer = _ => AnsweringNbg.Json("""[{"currencies":[{"code":"USD","quantity":1,"rate":2.7015}]}]""");
        Assert.Equal(Ka.Rates.Loaded, Alert(await Html.BodyAsync(await PostAsync(client, "2026-07-31")), "ok"));
        Assert.Equal("EUR 2026-07-31 3.500000; USD 2026-07-31 2.701500", await CacheAsync());
        Assert.Equal(2, _nbg.Requests.Count);
        // The page opened again shows the stored rates (legacy revalidatePath).
        Assert.Equal(["31.07.2026", "2.7015", "3.5000"], Reports.ReportSite.Cells(await Html.GetAsync(client, "/reference/rates"), "31.07.2026"));
    }

    [Fact]
    public async Task A_day_without_rates_is_answered_failed_and_stores_nothing()
    {
        var client = await SignedInAsync("accountant");
        foreach (var body in new[] { AnsweringNbg.NoRates, "[]", """[{"currencies":[{"code":"GBP","quantity":1,"rate":7},{"code":"USD","quantity":1,"rate":0}]}]""" })
        {
            _nbg.Answer = _ => AnsweringNbg.Json(body);
            var html = await Html.BodyAsync(await PostAsync(client, "2026-08-15"));
            Assert.Equal((Ka.Rates.Failed, ""), (Alert(html, "error"), Alert(html, "ok")));
            Assert.Contains($"<div class=\"card empty\">{Ka.Common.NoData}</div>", html);
            // The date stays as posted.
            Assert.Contains("type=\"hidden\" name=\"date\" value=\"2026-08-15\"", html);
        }
        Assert.Equal(3, _nbg.Requests.Count);
        Assert.Equal("", await CacheAsync());
    }

    [Fact]
    public async Task A_failing_bank_is_answered_failed_and_stores_nothing()
    {
        var client = await SignedInAsync("admin");
        var answers = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => AnsweringNbg.Json(AnsweringNbg.Payload, HttpStatusCode.ServiceUnavailable),
            _ => AnsweringNbg.Json("not json"),
            _ => throw new HttpRequestException("no connection"),
            _ => throw new TaskCanceledException("timed out"),
        };
        foreach (var answer in answers)
        {
            _nbg.Answer = answer;
            Assert.Equal(Ka.Rates.Failed, Alert(await Html.BodyAsync(await PostAsync(client, "2026-07-31")), "error"));
        }
        Assert.Equal(answers.Length, _nbg.Requests.Count);
        Assert.Equal("", await CacheAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("31/07/2026")]
    [InlineData("2026-7-31")]
    [InlineData("2026-07-31T00:00")]
    [InlineData("20260731")]
    public async Task A_date_that_is_not_yyyy_mm_dd_is_answered_failed_without_asking_the_bank(string date)
    {
        var client = await SignedInAsync("accountant");
        Assert.Equal(Ka.Rates.Failed, Alert(await Html.BodyAsync(await PostAsync(client, date)), "error"));
        Assert.Empty(_nbg.Requests);
        Assert.Equal("", await CacheAsync());
    }

    [Fact]
    public async Task A_viewer_sees_no_form_and_a_post_of_it_loads_nothing()
    {
        var client = await SignedInAsync("viewer");
        var page = await Html.GetAsync(client, "/reference/rates");
        Assert.DoesNotContain("<form method=\"post\" class=\"toolbar\"", page);
        Assert.DoesNotContain(Ka.Rates.Load, page);
        // The post made by hand (the page's sign-out form gives the anti-forgery token): legacy answered common.readOnly before
        // reading the date; nothing is asked of the bank, nothing stored, no form or message shown.
        var response = await WebApp.PostFormAsync(client, "/reference/rates", new() { ["date"] = "2026-07-31" });
        var html = await Html.BodyAsync(response);
        Assert.Empty(_nbg.Requests);
        Assert.Equal("", await CacheAsync());
        Assert.DoesNotContain(Ka.Rates.Loaded, html);
        Assert.DoesNotContain(Ka.Rates.Failed, html);
        Assert.DoesNotContain("<form method=\"post\" class=\"toolbar\"", html);
    }
}
