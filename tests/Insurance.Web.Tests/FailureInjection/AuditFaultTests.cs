using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.FailureInjection;

namespace Insurance.Web.Tests.FailureInjection;

/// <summary>
/// The change log's uncovered cases over HTTP on a real database (legacy scripts/integration/audit-log-viewer.test.ts): the admin's
/// own removal, a personal grant and a group's grant taken away, the permission read failing (the same error, no row of the log
/// read), the cursor kept exactly within 1..the bigint maximum, a password hash never shown, and reading leaving the log as it was.
/// </summary>
public sealed partial class AuditFaultTests(FaultSite site) : IClassFixture<FaultSite>
{
    private const string Key = "audit.log.view";
    private const string LogRead = "from core.audit_log a";

    [GeneratedRegex("<td><code>")]
    private static partial Regex LogRow();

    private static int Rows(string html) => LogRow().Matches(html).Count;

    private async Task<string> PageAsync(HttpClient client, string url = "/audit")
    {
        site.Faults.ClearLog();
        return await Html.GetAsync(client, url);
    }

    private bool LogWasRead() => site.Faults.Log.Any(c => Faults.Has(c, LogRead));

    private Task DecideAsync(Guid user, string decision) =>
        site.ExecAsync("delete from core.user_permission where user_id = @user and permission = @key; insert core.user_permission (user_id, permission, decision) values (@user, @key, @decision)",
            new { user, key = Key, decision });

    private async Task<Guid> GroupAsync(Guid member)
    {
        var group = Guid.NewGuid();
        await site.ExecAsync("""
            insert core.user_group (id, name) values (@group, @name);
            insert core.user_group_permission (group_id, permission) values (@group, @key);
            insert core.user_group_member (group_id, user_id) values (@group, @member);
            """, new { group, name = "აუდიტი " + group.ToString("N")[..6], key = Key, member });
        return group;
    }

    [Fact]
    public async Task The_audit_given_it_by_a_group_sees_the_log_its_own_removal_takes_it_away_an_admins_own_removal_too()
    {
        var (auditor, auditorId) = await site.SignedInAsync("viewer");
        await GroupAsync(auditorId);
        Assert.DoesNotContain(AuditTexts.NotAllowed, await PageAsync(auditor));
        Assert.True(LogWasRead());
        await DecideAsync(auditorId, "deny");
        Assert.Contains(AuditTexts.NotAllowed, await PageAsync(auditor));
        Assert.False(LogWasRead());
        var (admin, adminId) = await site.SignedInAsync("admin");
        Assert.DoesNotContain(AuditTexts.NotAllowed, await PageAsync(admin));
        await DecideAsync(adminId, "deny");
        var refused = await PageAsync(admin);
        Assert.Contains(AuditTexts.NotAllowed, refused);
        Assert.Equal(0, Rows(refused));
        Assert.False(LogWasRead());
    }

    [Fact]
    public async Task A_personal_grant_gives_it_the_groups_grant_taken_away_takes_it_back()
    {
        var (viewer, viewerId) = await site.SignedInAsync("viewer");
        Assert.Contains(AuditTexts.NotAllowed, await PageAsync(viewer));
        await DecideAsync(viewerId, "allow");
        Assert.DoesNotContain(AuditTexts.NotAllowed, await PageAsync(viewer));
        var (other, otherId) = await site.SignedInAsync("viewer");
        var group = await GroupAsync(otherId);
        Assert.DoesNotContain(AuditTexts.NotAllowed, await PageAsync(other));
        await site.ExecAsync("delete from core.user_group_permission where group_id = @group", new { group });
        Assert.Contains(AuditTexts.NotAllowed, await PageAsync(other));
        Assert.False(LogWasRead());
    }

    [Theory]
    [InlineData("own decisions")]
    [InlineData("memberships")]
    [InlineData("group grants")]
    public async Task The_read_of_the_permission_fails_the_same_error_no_row_of_the_log_read(string name)
    {
        var (auditor, auditorId) = await site.SignedInAsync("viewer");
        await GroupAsync(auditorId);
        site.Faults.ClearLog();
        var sentinel = new InjectedFault($"AUDIT_{name.Replace(' ', '_')}");
        site.Faults.FailOnce(Faults.Any(Faults.PermissionReads.Single(r => r.Name == name).Fragment), sentinel);
        await FaultSite.FailedAsync(await auditor.GetAsync("/audit"), sentinel);
        Assert.True(site.Faults.AllFired);
        Assert.False(LogWasRead());
        Assert.DoesNotContain(site.Faults.Log, c => Faults.Has(c, "from core.app_user order by full_name"));
        Assert.DoesNotContain(AuditTexts.NotAllowed, await PageAsync(auditor));
        Assert.True(LogWasRead());
    }

    private const string Max = "9223372036854775807";

    [Fact]
    public void The_cursor_is_kept_exactly_within_1_and_the_bigint_maximum_above_it_zero_signed_or_zero_led_it_is_dropped()
    {
        string[] kept = ["1", "11", "9007199254740993", Max];
        string[] dropped = ["9223372036854775808", "9999999999999999999", "10000000000000000000", "0", "-1", "011", "1e3", "1.5"];
        Assert.Equal(kept, kept.Select(v => AuditLog.Parse(k => k == "before" ? v : null).Before?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(dropped, v => Assert.Null(AuditLog.Parse(k => k == "before" ? v : null).Before));
    }

    [Fact]
    public async Task The_cursor_at_the_bigint_maximum_every_row_below_it_the_first_page_link_keeps_the_table_filter_and_one_above_it_is_dropped_before_the_query()
    {
        var (admin, adminId) = await site.SignedInAsync("admin");
        var db = new Db(new DbOptions { ConnectionString = site.Db.AppConnection });
        var party = await db.AsUserAsync(adminId, tx => Integration.Tests.Fixtures.PersonAsync(tx));
        for (var i = 0; i < 3; i++)
            await db.AsUserAsync(adminId, tx => tx.ExecuteAsync("update core.party set email = @email where id = @party", new { email = $"a{i}@example.ge", party }));
        var plain = await PageAsync(admin, "/audit?table=party");
        var atMax = await PageAsync(admin, $"/audit?table=party&before={Max}");
        Assert.True(Rows(atMax) >= 4);
        Assert.Equal(Rows(plain), Rows(atMax));
        Assert.Equal(Html.Text(Html.Between(plain, "<tbody>", "</tbody>")), Html.Text(Html.Between(atMax, "<tbody>", "</tbody>")));
        Assert.Contains("href=\"/audit?table=party\"", atMax);
        foreach (var over in new[] { "9223372036854775808", "9999999999999999999", "10000000000000000000" })
        {
            var h = await PageAsync(admin, $"/audit?table=party&before={over}");
            Assert.Equal(Html.Text(Html.Between(plain, "<tbody>", "</tbody>")), Html.Text(Html.Between(h, "<tbody>", "</tbody>")));
            Assert.DoesNotContain(AuditTexts.First, h);
            Assert.Null(site.Faults.Log.Single(c => Faults.Has(c, LogRead)).Parameter("before"));
        }
    }

    [Fact]
    public async Task A_change_is_shown_field_by_field_a_password_hash_never_shows_the_log_rows_are_unchanged_by_reading()
    {
        var (admin, adminId) = await site.SignedInAsync("admin");
        var db = new Db(new DbOptions { ConnectionString = site.Db.AppConnection });
        var user = Guid.NewGuid();
        await db.AsUserAsync(adminId, tx => tx.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code) values (@user, @username, N'ძველი სახელი', N'scrypt$16384$8$1$c2FsdA==$aGFzaA==', N'viewer')
            """, new { user, username = "audit." + user.ToString("N")[..8] }));
        await db.AsUserAsync(adminId, tx => tx.ExecuteAsync("update core.app_user set full_name = N'ახალი სახელი', password_hash = N'scrypt$16384$8$1$bmV3$bmV3aGFzaA==' where id = @user", new { user }));
        async Task<string> Log()
        {
            await using var c = await site.Db.OpenAdminAsync();
            return await c.ExecuteScalarAsync<string>("select concat(count(*), N'/', max(id), N'/', checksum_agg(checksum(id, table_name, row_id, action, user_id, old_row, new_row))) from core.audit_log") ?? "";
        }
        var before = await Log();
        var html = WebUtility.HtmlDecode(await PageAsync(admin, $"/audit?table=app_user&user={adminId}"));
        Assert.Contains("full_name", html);
        Assert.Contains("ძველი სახელი → ახალი სახელი", html);
        Assert.DoesNotContain("password_hash", html);
        Assert.DoesNotContain("scrypt$", html);
        Assert.Equal(before, await Log());
    }
}
