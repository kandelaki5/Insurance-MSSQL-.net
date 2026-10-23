using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Helpers for the tests that close the coverage gaps of the legacy integration scripts (docs/legacy-tests-gaps-b.md):
/// a fresh database for a test whose monthly report must hold only its own rows, a fault armed at the n-th insert into a
/// table (a test-only trigger on the test's own database, as ClaimWorld.ArmFaultAsync), and the month helpers for
/// scenarios rewritten relative to the real Tbilisi today.
/// </summary>
public static class Gaps
{
    /// <summary>Runs <paramref name="body"/> on a database of its own, dropped afterwards.</summary>
    public static async Task OwnDatabaseAsync(Func<World, Task> body)
    {
        var db = new TestDatabase();
        await db.InitializeAsync();
        try
        {
            await body(new World(db));
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    /// <summary>The first day of today's month in Tbilisi.</summary>
    public static DateOnly MonthStart => new(World.Today.Year, World.Today.Month, 1);

    public static DateOnly MonthEnd(DateOnly start) => start.AddMonths(1).AddDays(-1);

    public static string Month(DateOnly day) => day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public static Task<PremiumReport.Report> MonthReportAsync(World w, DateOnly month) =>
        w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, Month(month)));

    /// <summary>
    /// Arms a fault: within one transaction, the <paramref name="nth"/> insert statement into core.<paramref name="table"/>
    /// fails with error 50999, after the statements before it (the counter is rolled back with the transaction, so every
    /// attempt fails at the same insert until <see cref="DisarmAsync"/>). Built by the admin on the test database only.
    /// </summary>
    public static async Task ArmAsync(TestDatabase db, string table, int nth = 1)
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("if object_id(N'dbo.gaps_fault') is null create table dbo.gaps_fault (table_name sysname not null primary key, countdown int not null)");
        await c.ExecuteAsync("grant select, update on dbo.gaps_fault to core_app");
        await c.ExecuteAsync($"""
            create or alter trigger core.gaps_fault_{table} on core.{table} after insert as
            begin
              set nocount on;
              declare @left int;
              update dbo.gaps_fault set @left = countdown = countdown - 1 where table_name = N'{table}';
              if @left = 0 throw 50999, N'injected failure', 1;
            end
            """);
        await c.ExecuteAsync("delete from dbo.gaps_fault where table_name = @table; insert dbo.gaps_fault (table_name, countdown) values (@table, @nth)", new { table, nth });
    }

    public static async Task DisarmAsync(TestDatabase db)
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("if object_id(N'dbo.gaps_fault') is not null delete from dbo.gaps_fault");
    }

    /// <summary>A personal decision on a permission (allow / deny), as the admin's panel writes it.</summary>
    public static async Task DecideAsync(World w, Guid userId, string permission, string decision)
    {
        await using var c = await w.Database.OpenAppAsync();
        await c.ExecuteAsync("""
            delete from core.user_permission where user_id = @userId and permission = @permission;
            insert core.user_permission (user_id, permission, decision) values (@userId, @permission, @decision)
            """, new { userId, permission, decision });
    }

    public static async Task InheritAsync(World w, Guid userId, string permission)
    {
        await using var c = await w.Database.OpenAppAsync();
        await c.ExecuteAsync("delete from core.user_permission where user_id = @userId and permission = @permission", new { userId, permission });
    }

    /// <summary>A group granting <paramref name="permission"/> to <paramref name="members"/>.</summary>
    public static async Task<Guid> GroupAsync(World w, string permission, params Guid[] members)
    {
        var id = Guid.NewGuid();
        await using var c = await w.Database.OpenAppAsync();
        await c.ExecuteAsync("insert core.user_group (id, name) values (@id, @name)", new { id, name = "ჯგუფი " + id.ToString("N")[..8] });
        await c.ExecuteAsync("insert core.user_group_permission (group_id, permission) values (@id, @permission)", new { id, permission });
        foreach (var m in members) await c.ExecuteAsync("insert core.user_group_member (group_id, user_id) values (@id, @m)", new { id, m });
        return id;
    }

    /// <summary>A count of every financial row: what a refusal or a rolled-back failure must leave unchanged.</summary>
    public static Task<string> LedgerAsync(World w) => w.ScalarAsync<string>("""
        select concat((select count(*) from core.policy), '/', (select count(*) from core.premium_transaction), '/', (select count(*) from core.premium_cession), '/',
                      (select count(*) from core.installment), '/', (select count(*) from core.installment_adjustment), '/', (select count(*) from core.payment), '/',
                      (select count(*) from core.payment_allocation), '/', (select count(*) from core.claim_payment), '/', (select count(*) from core.claim_reserve), '/',
                      (select count(*) from core.premium_refund_request), '/', (select count(*) from core.receipt_reversal_request), '/', (select count(*) from core.claim_payment_request))
        """);

    public static string Iso(DateOnly d) => Dates.ToIso(d);

    /// <summary>The legacy day of an action: the database's clock (DbOptions.Clock) stands on <paramref name="day"/> in Tbilisi until set again.</summary>
    public static void On(World w, DateOnly day) => w.Database.Clock.SetToday(day);

    public static DateOnly D(int year, int month, int day) => new(year, month, day);

    /// <summary>A whole monthly report as one comparable text (its records' values, in order): "October as reported" is this text unchanged.</summary>
    public static string Shape(PremiumReport.Report r) => string.Join(" | ",
        r.Month, r.From, r.To, string.Join("; ", r.Classes), r.Totals, string.Join("; ", r.Receivables), string.Join("; ", r.Claims), r.ClaimsTotals, r.TreatyPremiumGel);
}
