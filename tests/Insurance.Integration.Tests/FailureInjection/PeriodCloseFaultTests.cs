using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// The month close's binding permission check and its reads failing once, and a first policy on a year's last day, on the real
/// SQL Server (legacy scripts/integration/period-close-permission.test.ts). Which months may close depends on every policy and
/// period of the database, so each test has a database of its own: one policy from 01.09.2026 (its premium and instalment),
/// September closed, October open, "today" 03.11.2026 — closed months are written as a close leaves them, never reopened.
/// </summary>
public sealed class PeriodCloseFaultTests
{
    private const string October = "2026-10-01";
    private static readonly DateOnly Today = new(2026, 11, 3);

    private sealed class Site(TestDatabase db, Faults faults) : IAsyncDisposable
    {
        public TestDatabase Db { get; } = db;
        public Faults Faults { get; } = faults;
        public Db App { get; } = new(new DbOptions { ConnectionString = db.AppConnection, Interceptor = faults });
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();

        public async Task<List<(DateOnly Start, string Status, Guid? By)>> PeriodsAsync()
        {
            await using var c = await Db.OpenAppAsync();
            return (await c.QueryAsync<(DateOnly, string, Guid?)>("select period_start, status, closed_by from core.accounting_period order by period_start")).ToList();
        }

        public void Exec(string sql, object param)
        {
            using var c = new SqlConnection(Db.AppConnection);
            c.Open();
            c.Execute(sql, param);
        }
    }

    private static async Task<Site> SiteAsync(DateOnly firstPolicy, DateOnly closed)
    {
        var db = new TestDatabase();
        await db.InitializeAsync();
        var site = new Site(db, new Faults());
        var admin = await Fixtures.UserAsync(db, "admin");
        await db.App.AsUserAsync(admin, tx => Fixtures.ActivePolicyAsync(tx, admin, firstPolicy));
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.accounting_period (period_start, status, closed_at, closed_by) values (@closed, N'closed', sysdatetimeoffset(), @admin)", new { closed, admin });
        return site;
    }

    private static async Task<(CurrentUser Sales, Guid Group)> GroupedSalesAsync(Site s)
    {
        var id = await Fixtures.UserAsync(s.Db, "sales");
        var group = Guid.NewGuid();
        await using var c = await s.Db.OpenAppAsync();
        await c.ExecuteAsync("""
            insert core.user_group (id, name) values (@group, @name);
            insert core.user_group_permission (group_id, permission) values (@group, N'accounting.period.close');
            insert core.user_group_member (group_id, user_id) values (@group, @id);
            """, new { group, name = "დახურვა " + group.ToString("N")[..6], id });
        return (new CurrentUser(id, $"test.{id:N}"[..30], "სატესტო", "sales", "sales"), group);
    }

    [Fact]
    public async Task Month_close_between_the_quick_check_and_the_transaction_the_groups_grant_goes_refused_before_the_lock_nothing_written()
    {
        await using var s = await SiteAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1));
        var (sales, group) = await GroupedSalesAsync(s);
        var before = await s.PeriodsAsync();
        s.Faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => s.Exec("delete from core.user_group_permission where group_id = @group", new { group }));
        Assert.Equal(new PeriodClose.Answer("forbidden", October), await PeriodClose.CloseAsync(s.App, sales, October, Today));
        Assert.True(s.Faults.AllFired);
        s.Faults.AssertOnlyThePermissionWasRead();
        Assert.DoesNotContain(s.Faults.Log, c => c.Sql.Contains("core.xact_lock", StringComparison.Ordinal));
        Assert.Equal(before, await s.PeriodsAsync());
    }

    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Month_close_the_checks_read_fails_the_same_error_nothing_written_the_retry_closes_October_once(string label, string name)
    {
        await using var s = await SiteAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1));
        var (sales, _) = await GroupedSalesAsync(s);
        var before = await s.PeriodsAsync();
        var sentinel = new InjectedFault($"CLOSE_{label}_{name}");
        s.Faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PeriodClose.CloseAsync(s.App, sales, October, Today)));
        Assert.Equal(label == "binding", s.Faults.BegunATransaction);
        Assert.DoesNotContain(s.Faults.Log, c => c.Sql.Contains("core.xact_lock", StringComparison.Ordinal));
        Assert.Equal(before, await s.PeriodsAsync());
        Assert.Equal(new PeriodClose.Answer("done", October), await PeriodClose.CloseAsync(s.App, sales, October, Today));
        Assert.Equal([.. before, (new DateOnly(2026, 10, 1), "closed", (Guid?)sales.Id)], await s.PeriodsAsync());
        Assert.Equal(new PeriodClose.Answer("already", October), await PeriodClose.CloseAsync(s.App, sales, October, Today));
        Assert.Equal(2, (await s.PeriodsAsync()).Count);
    }

    [Fact]
    public async Task Month_close_a_months_last_day_and_a_new_year_first_policy_31_12_December_closed_today_03_03_February_asked_directly_is_earlier_open_January_then_February()
    {
        // The only policy from 31.12.2026 (its premium booked that day, before December was closed); December closed, January open.
        await using var s = await SiteAsync(new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 1));
        var accountant = await Fixtures.UserAsync(s.Db, "accountant");
        var a = new CurrentUser(accountant, $"test.{accountant:N}"[..30], "სატესტო", "accountant", "accountant");
        var march3 = new DateOnly(2027, 3, 3);
        var december = await s.PeriodsAsync();
        Assert.Equal([(new DateOnly(2026, 12, 1), "closed")], december.Select(p => (p.Start, p.Status)));
        Assert.Equal(new PeriodClose.Answer("earlier-open", "2027-02-01"), await PeriodClose.CloseAsync(s.App, a, "2027-02-01", march3));
        Assert.Equal(december, await s.PeriodsAsync());
        Assert.Equal(new PeriodClose.Answer("done", "2027-01-01"), await PeriodClose.CloseAsync(s.App, a, "2027-01-01", march3));
        Assert.Equal(new PeriodClose.Answer("done", "2027-02-01"), await PeriodClose.CloseAsync(s.App, a, "2027-02-01", march3));
        Assert.Equal([.. december, (new DateOnly(2027, 1, 1), "closed", (Guid?)accountant), (new DateOnly(2027, 2, 1), "closed", (Guid?)accountant)], await s.PeriodsAsync());
    }
}
