using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests;

/// <summary>The shared change log (legacy audit/page.tsx, scripts/integration/audit-log-viewer.test.ts) and the home page's reminders.</summary>
public sealed class AuditHomeTests(WebApp app) : IClassFixture<WebApp>
{
    [Fact]
    public async Task The_log_is_for_audit_log_view_only_the_admin_by_role_another_user_by_a_group_and_a_personal_deny_takes_it_away()
    {
        var (accountant, accountantId) = await app.SignedInAsync("accountant");
        var refused = await Html.GetAsync(accountant, "/audit");
        Assert.Contains(AuditTexts.NotAllowed, refused);
        Assert.DoesNotContain(AuditTexts.Columns.Changes, refused);
        Assert.DoesNotContain("href=\"/audit\"", refused);

        await using var c = await app.Db.OpenAdminAsync();
        var group = Guid.NewGuid();
        await c.ExecuteAsync("""
            insert core.user_group (id, name) values (@group, @name);
            insert core.user_group_permission (group_id, permission) values (@group, N'audit.log.view');
            insert core.user_group_member (group_id, user_id) values (@group, @accountantId);
            """, new { group, name = "აუდიტი " + group.ToString("N")[..6], accountantId });
        var allowed = await Html.GetAsync(accountant, "/audit");
        Assert.DoesNotContain(AuditTexts.NotAllowed, allowed);
        Assert.Contains("href=\"/audit\"", allowed);
        await c.ExecuteAsync("insert core.user_permission (user_id, permission, decision) values (@accountantId, N'audit.log.view', N'deny')", new { accountantId });
        Assert.Contains(AuditTexts.NotAllowed, await Html.GetAsync(accountant, "/audit"));
    }

    [Fact]
    public async Task The_log_is_filtered_by_table_user_and_day_and_read_a_page_at_a_time()
    {
        var (admin, adminId) = await app.SignedInAsync("admin");
        var (sales, salesId) = await app.SignedInAsync("sales");
        var tag = Guid.NewGuid().ToString("N")[..6];
        // 55 changes of a party by the sales user: more than one page of 50.
        var party = await app.Db.App.AsUserAsync(salesId, tx => Fixtures.PersonAsync(tx));
        for (var i = 0; i < 55; i++)
            await app.Db.App.AsUserAsync(salesId, tx => tx.ExecuteAsync("update core.party set email = @email where id = @party", new { email = $"{tag}-{i}@example.ge", party }));
        var url = $"/audit?table=party&user={salesId}";
        var first = await Html.GetAsync(admin, url);
        Assert.Equal(50, System.Text.RegularExpressions.Regex.Matches(first, "<code>").Count);
        Assert.Contains($"{tag}-53@example.ge → {tag}-54@example.ge", Html.Text(first));
        var next = System.Text.RegularExpressions.Regex.Match(first, "href=\"(/audit\\?table=party&amp;user=[^\"]+&amp;before=\\d+)\">").Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal);
        Assert.NotEqual("", next);
        var second = await Html.GetAsync(admin, next);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(second, "<code>").Count); // 5 updates and the insert
        Assert.Contains(AuditTexts.First, second);
        Assert.DoesNotContain(AuditTexts.Next, second);
        Assert.Contains(Ka.Audit.Actions["insert"], Html.Text(second));

        // Another user's changes are not in this user's filter; a day with nothing says so; a malformed filter is dropped.
        Assert.Contains(AuditTexts.Empty, await Html.GetAsync(admin, $"/audit?table=party&user={adminId}&from=2026-01-01&to=2026-01-02"));
        Assert.Contains(AuditTexts.Empty, await Html.GetAsync(admin, "/audit?table=party&from=2000-01-01&to=2000-01-01"));
        var today = Insurance.Web.Infrastructure.Fmt.Iso(Rates.TodayInTbilisi());
        Assert.Contains($"{tag}-54@example.ge", await Html.GetAsync(admin, $"/audit?table=party&from={today}&to={today}&user={salesId.ToString().ToUpperInvariant()}"));
        var unfiltered = await Html.GetAsync(admin, "/audit?table=DROP TABLE&user=x&before=abc&from=2026-13-01");
        Assert.DoesNotContain(AuditTexts.Empty, unfiltered);
        Assert.Contains("selected=\"selected\">" + AuditTexts.Filters.Any, unfiltered);
    }

    [Fact]
    public async Task The_home_page_counts_the_reminders_and_shows_the_unconfirmed_oral_instruction_only_to_compliance_roles()
    {
        var (viewer, _) = await app.SignedInAsync("viewer");
        var (accountant, _) = await app.SignedInAsync("accountant");
        await using var c = await app.Db.OpenAdminAsync();
        var party = Guid.NewGuid();
        await c.ExecuteAsync("""
            insert core.party (id, kind, first_name, last_name, tax_id, risk_level, risk_review_on) values (@party, N'person', N'ა', N'შეხსენება', @taxId, N'low', '2020-01-01');
            insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at)
            values (newid(), @party, N'oral', N'all', dateadd(hour, -25, sysdatetimeoffset()), dateadd(day, 2, sysdatetimeoffset()));
            insert core.complaint (id, number, received_on, channel, customer_name, category, description) values (newid(), @number, '2026-01-05', N'written', N'x', 1, N'y');
            insert core.exchange_rate (currency, rate_date, rate) values (N'USD', '2026-01-02', 2.7), (N'USD', '2026-01-05', 2.71234), (N'EUR', '2026-01-05', 3.1);
            """, new { party, taxId = Fixtures.RandomTaxId(), number = "PR-H" + party.ToString("N")[..6] });
        var counts = await c.QuerySingleAsync<(int Unrated, int Reviews, int Complaints, int Parties)>("""
            select (select count(*) from core.party where risk_level is null), (select count(*) from core.party where risk_review_on <= @soon),
                   (select count(*) from core.complaint where status = N'open'), (select count(*) from core.party)
            """, new { soon = Rates.TodayInTbilisi().AddDays(30) });
        var home = Html.Text(await Html.GetAsync(viewer, "/"));
        Assert.Contains($"{counts.Parties} {Ka.Home.Parties}", home);
        Assert.Contains($"{counts.Reviews} {Ka.Home.RiskReviewsDue}", home);
        Assert.Contains($"{counts.Complaints} {Ka.Home.OpenComplaints}", home);
        Assert.Contains("USD: 2.7123 (2026-01-05)", home);
        Assert.Contains("EUR: 3.1000 (2026-01-05)", home);
        Assert.DoesNotContain(AmlTexts.Suspension.UnconfirmedReminder, home);
        var compliance = await Html.GetAsync(accountant, "/");
        Assert.Contains($"<strong>1</strong> {AmlTexts.Suspension.UnconfirmedReminder}", compliance);
        Assert.Contains("href=\"/compliance/aml-cases\"", compliance);
        if (counts.Unrated > 0) Assert.Contains($"{counts.Unrated} {Ka.Home.UnratedParties}", home);
        else Assert.DoesNotContain(Ka.Home.UnratedParties, home);
    }
}
