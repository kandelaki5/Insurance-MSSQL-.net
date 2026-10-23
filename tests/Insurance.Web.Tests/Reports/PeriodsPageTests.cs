using System.Globalization;
using System.Net;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The accounting periods page and its close over HTTP (legacy accounting/periods, close-answer.tsx, actions.ts closePeriod;
/// period-close-permission.test.ts): who may close (accounting.period.close: the accountant and the admin by role, anyone else by a
/// group or a personal grant, a personal removal takes it away), which month (the oldest open month that has ended), once, every press
/// answered on the page that was pressed. Months are relative to today in Tbilisi.
/// </summary>
public sealed class PeriodsPageTests
{
    private static readonly DateOnly Current = new(Rates.TodayInTbilisi().Year, Rates.TodayInTbilisi().Month, 1);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string M(int back) => Iso(Current.AddMonths(-back));

    /// <summary>A first policy from the middle of the month three months back: months M-3 … M are listed.</summary>
    private static async Task<ReportSite> SiteAsync()
    {
        var site = await ReportSite.CreateAsync();
        var first = Current.AddMonths(-3).AddDays(14);
        await site.World.PolicyAsync("P-PERIODS", Iso(first), Iso(first.AddYears(1)), [new LineSpec("5.1", 365m, 365m)]);
        return site;
    }

    private static Task<HttpResponseMessage> CloseAsync(HttpClient client, string month) =>
        ReportSite.PostFormAsync(client, "/accounting/periods?handler=Close", new() { ["period_start"] = month }, "/accounting/periods");

    private static async Task<List<(DateOnly Month, string Status, DateTimeOffset? At, Guid? By)>> PeriodsAsync(ReportSite site)
    {
        await using var c = await site.World.Database.OpenAdminAsync();
        return (await c.QueryAsync<(DateOnly, string, DateTimeOffset?, Guid?)>("select period_start, status, closed_at, closed_by from core.accounting_period order by period_start")).ToList();
    }

    private static string Answer(string month, string outcome) => $"{Month.MonthNameKa(month)}: {Ka.Periods.CloseOutcomes[outcome]}";

    [Theory]
    [InlineData("accountant", true)]
    [InlineData("admin", true)]
    [InlineData("operations_head", false)]
    [InlineData("sales", false)]
    [InlineData("viewer", false)]
    public async Task The_roles_starting_set_closes_and_the_others_are_refused_before_anything_is_written(string role, bool may)
    {
        await using var site = await SiteAsync();
        var (client, userId) = await site.SignedInAsync(role);
        var html = await ReportSite.PageAsync(client, "/accounting/periods");
        Assert.Contains(Ka.Periods.Deadline(5), ReportSite.Decode(html));
        Assert.Equal(may, html.Contains($"value=\"{M(3)}\"", StringComparison.Ordinal));
        var response = await CloseAsync(client, M(3));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = ReportSite.Decode(await response.Content.ReadAsStringAsync());
        if (may)
        {
            Assert.Contains($"<div class=\"alert ok\">{Answer(M(3), "done")}</div>", answer);
            var row = Assert.Single(await PeriodsAsync(site));
            Assert.Equal((D(M(3)), "closed", userId), (row.Month, row.Status, row.By!.Value));
            // The page now offers the next month and shows the closer.
            Assert.Contains($"value=\"{M(2)}\"", answer);
            Assert.Contains($"სატესტო {role}", answer);
        }
        else
        {
            Assert.Contains($"<div class=\"alert error\">{Answer(M(3), "forbidden")}</div>", answer);
            Assert.Empty(await PeriodsAsync(site));
        }
    }

    [Fact]
    public async Task A_viewer_given_it_personally_or_by_a_group_closes_and_a_personal_removal_beats_the_group_and_the_role()
    {
        await using var site = await SiteAsync();
        var (viewer, viewerId) = await site.SignedInAsync("viewer");
        await using (var c = await site.World.Database.OpenAdminAsync())
            await c.ExecuteAsync("insert core.user_permission (user_id, permission, decision) values (@viewerId, N'accounting.period.close', N'allow')", new { viewerId });
        Assert.Contains($"value=\"{M(3)}\"", await ReportSite.PageAsync(viewer, "/accounting/periods"));
        Assert.Contains(Answer(M(3), "done"), ReportSite.Decode(await (await CloseAsync(viewer, M(3))).Content.ReadAsStringAsync()));

        var (grouped, groupedId) = await site.SignedInAsync("sales");
        var (accountant, accountantId) = await site.SignedInAsync("accountant");
        await using (var c = await site.World.Database.OpenAdminAsync())
        {
            var group = Guid.NewGuid();
            await c.ExecuteAsync("insert core.user_group (id, name) values (@group, N'დახურვა')", new { group });
            await c.ExecuteAsync("insert core.user_group_permission (group_id, permission) values (@group, N'accounting.period.close')", new { group });
            await c.ExecuteAsync("insert core.user_group_member (group_id, user_id) values (@group, @groupedId)", new { group, groupedId });
            await c.ExecuteAsync("insert core.user_group_member (group_id, user_id) values (@group, @accountantId)", new { group, accountantId });
            await c.ExecuteAsync("insert core.user_permission (user_id, permission, decision) values (@accountantId, N'accounting.period.close', N'deny')", new { accountantId });
        }
        Assert.Contains(Answer(M(2), "done"), ReportSite.Decode(await (await CloseAsync(grouped, M(2))).Content.ReadAsStringAsync()));
        // The accountant's personal removal beats both its role and the group.
        Assert.DoesNotContain($"value=\"{M(1)}\"", await ReportSite.PageAsync(accountant, "/accounting/periods"));
        Assert.Contains(Answer(M(1), "forbidden"), ReportSite.Decode(await (await CloseAsync(accountant, M(1))).Content.ReadAsStringAsync()));
        Assert.Equal([(D(M(3)), viewerId), (D(M(2)), groupedId)], (await PeriodsAsync(site)).Select(p => (p.Month, p.By!.Value)));
    }

    [Fact]
    public async Task Which_month_and_once_already_not_ended_invalid_and_earlier_open()
    {
        await using var site = await SiteAsync();
        var (client, _) = await site.SignedInAsync("accountant");
        // An earlier month still open: refused, nothing closed.
        Assert.Contains(Answer(M(2), "earlier-open"), ReportSite.Decode(await (await CloseAsync(client, M(2))).Content.ReadAsStringAsync()));
        Assert.Empty(await PeriodsAsync(site));
        Assert.Contains(Answer(M(3), "done"), ReportSite.Decode(await (await CloseAsync(client, M(3))).Content.ReadAsStringAsync()));
        var closed = await PeriodsAsync(site);
        // Again: "already", the row exactly as it was.
        Assert.Contains(Answer(M(3), "already"), ReportSite.Decode(await (await CloseAsync(client, M(3))).Content.ReadAsStringAsync()));
        Assert.Equal(closed, await PeriodsAsync(site));
        // The current month and the next: not ended; a month that is not one: invalid, without a month name.
        Assert.Contains(Answer(M(0), "not-ended"), ReportSite.Decode(await (await CloseAsync(client, M(0))).Content.ReadAsStringAsync()));
        Assert.Contains(Answer(M(-1), "not-ended"), ReportSite.Decode(await (await CloseAsync(client, M(-1))).Content.ReadAsStringAsync()));
        foreach (var bad in new[] { "2026-13-01", "2026-00-01", "0000-01-01", "2026-10-02", "x" })
        {
            var html = ReportSite.Decode(await (await CloseAsync(client, bad)).Content.ReadAsStringAsync());
            Assert.Contains($"<div class=\"alert error\">{Ka.Periods.CloseOutcomes["invalid"]}</div>", html);
        }
        Assert.Equal(closed, await PeriodsAsync(site));
        // A typed or shared address shows no answer.
        Assert.DoesNotContain("class=\"alert", await ReportSite.PageAsync(client, "/accounting/periods?close=done"));
    }

    [Fact]
    public async Task The_page_lists_every_month_from_the_first_policys_with_its_state()
    {
        await using var site = await SiteAsync();
        await site.World.ClosePeriodAsync(M(3)[..7]);
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/periods"));
        Assert.Equal(Ka.Periods.Closed, ReportSite.Cells(html, Month.MonthNameKa(M(3)))[1]);
        Assert.Contains(ReportSite.Cells(html, Month.MonthNameKa(M(2)))[1], new[] { Ka.Periods.OverdueClose, Ka.Periods.DueSoon });
        Assert.Equal(Ka.Periods.PreviousOpen, ReportSite.Cells(html, Month.MonthNameKa(M(1)))[4]);
        Assert.Equal(Ka.Periods.Open, ReportSite.Cells(html, Month.MonthNameKa(M(0)))[1]);
        Assert.Equal(4, html.Split("<tr>").Length - 2);
        // The closing deadline is the company's setting.
        await using (var c = await site.World.Database.OpenAdminAsync())
            await c.ExecuteAsync("merge core.setting as t using (select N'accounting.close_within_days' as k) s on t.[key] = s.k when matched then update set value = N'12' when not matched then insert ([key], value) values (s.k, N'12');");
        Assert.Contains(Ka.Periods.Deadline(12), ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/periods")));
    }

    [Fact]
    public async Task No_policy_at_all_the_asked_month_is_its_own_start_and_closes()
    {
        await using var site = await ReportSite.CreateAsync();
        var (client, _) = await site.SignedInAsync("accountant");
        Assert.Contains(Answer(M(2), "done"), ReportSite.Decode(await (await CloseAsync(client, M(2))).Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Nobody_signed_in_is_sent_to_the_login()
    {
        await using var site = await ReportSite.CreateAsync();
        var response = await site.Client().GetAsync("/accounting/periods");
        Assert.Equal((HttpStatusCode.Redirect, "/login"), (response.StatusCode, response.Headers.Location?.ToString()));
    }
}
