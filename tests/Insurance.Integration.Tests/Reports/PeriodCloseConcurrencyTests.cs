using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.Data.SqlClient;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The month close (PeriodClose.CloseAsync) against a financial movement in flight, with two real sessions (docs/04 rule 19, review H04/H06):
/// the close takes the period lock exclusively, a movement shares it, so the close waits for the movement and every movement after it
/// sees the month closed. Months relative to today in Tbilisi.
/// </summary>
public sealed class PeriodCloseConcurrencyTests
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(1500);
    private static readonly DateOnly Current = new(Rates.TodayInTbilisi().Year, Rates.TodayInTbilisi().Month, 1);

    private static CurrentUser User(Guid id, string role) => new(id, "test." + role, "სატესტო " + role, role, role);

    private static async Task<(SqlConnection, SqlTransaction)> BeginAsync(ReportWorld w)
    {
        var c = await w.Database.OpenAppAsync();
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = w.Admin.ToString() }, t);
        return (c, t);
    }

    [Fact]
    public async Task The_close_waits_for_a_movement_in_flight_and_every_movement_after_it_is_refused()
    {
        await using var w = await CreateAsync();
        var month = Current.AddMonths(-1);
        var (c, t) = await BeginAsync(w);
        await using (c)
        {
            // A receipt dated in the month, written but not committed: it holds the period lock shared.
            await c.ExecuteAsync("insert core.payment (received_on, amount_gel, method) values (@on, 50, N'cash')", new { on = month.AddDays(3) }, t);
            var closing = PeriodClose.CloseAsync(w.App, User(w.Accountant, "accountant"), month.ToString("yyyy-MM-dd"));
            Assert.NotSame(closing, await Task.WhenAny(closing, Task.Delay(Blocked)));
            await t.CommitAsync();
            Assert.Equal(new PeriodClose.Answer("done", month.ToString("yyyy-MM-dd")), await closing);
        }
        await using var check = await w.Database.OpenAppAsync();
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("select count(*) from core.payment where received_on = @on", new { on = month.AddDays(3) }));
        var refused = await Assert.ThrowsAsync<SqlException>(() => w.ExecAsync("insert core.payment (received_on, amount_gel, method) values (@on, 50, N'cash')", new { on = month.AddDays(4) }));
        Assert.Equal(DbError.CheckViolation, DbError.From(refused)!.Code);
    }

    [Fact]
    public async Task A_movement_that_starts_while_the_close_holds_the_lock_waits_and_then_sees_the_month_closed()
    {
        await using var w = await CreateAsync();
        var month = Current.AddMonths(-1);
        // The close's own transaction, held open after its lock and insert (as PeriodClose writes it).
        var (c, t) = await BeginAsync(w);
        await using (c)
        {
            await c.ExecuteAsync("exec core.xact_lock N'core.accounting_period'; insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @by)",
                new { month, by = w.Accountant }, t);
            var movement = w.ExecAsync("insert core.payment (received_on, amount_gel, method) values (@on, 50, N'cash')", new { on = month.AddDays(5) });
            Assert.NotSame(movement, await Task.WhenAny(movement, Task.Delay(Blocked)));
            await t.CommitAsync();
            var refused = await Assert.ThrowsAsync<SqlException>(() => movement);
            Assert.Equal(DbError.CheckViolation, DbError.From(refused)!.Code);
        }
        // Negative control: a write that is not a financial movement of a month does not wait for the close.
        var (c2, t2) = await BeginAsync(w);
        await using (c2)
        {
            await c2.ExecuteAsync("exec core.xact_lock N'core.accounting_period'", transaction: t2);
            var party = w.PartyAsync("ტესტი დამოუკიდებელი");
            Assert.Same(party, await Task.WhenAny(party, Task.Delay(Blocked * 4)));
            await t2.RollbackAsync();
        }
    }

    [Fact]
    public async Task Two_closes_of_one_month_at_once_close_it_once()
    {
        await using var w = await CreateAsync();
        var month = Current.AddMonths(-1).ToString("yyyy-MM-dd");
        var answers = await Task.WhenAll(
            PeriodClose.CloseAsync(w.App, User(w.Accountant, "accountant"), month),
            PeriodClose.CloseAsync(w.App, User(w.Admin, "admin"), month));
        Assert.Equal(["already", "done"], answers.Select(a => a.Outcome).Order(StringComparer.Ordinal));
        await using var c = await w.Database.OpenAppAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.accounting_period where status = N'closed'"));
    }

    [Fact]
    public async Task A_read_that_fails_inside_the_close_stops_it_writes_nothing_and_the_next_press_closes_the_month()
    {
        // legacy period-close: "a failed read stops it" (its fake database failed a statement); here SQL Server refuses the app role the
        // policies table for one press, after the lock is taken.
        await using var w = await CreateAsync();
        var month = Current.AddMonths(-1);
        await w.PolicyAsync("P-CLOSE", month.ToString("yyyy-MM-dd"), month.AddYears(1).ToString("yyyy-MM-dd"), [new LineSpec("5.1", 100m, 100m)]);
        var accountant = User(w.Accountant, "accountant");
        await using (var c = await w.Database.OpenAdminAsync()) await c.ExecuteAsync("deny select on core.policy to core_app");
        await Assert.ThrowsAsync<SqlException>(() => PeriodClose.CloseAsync(w.App, accountant, month.ToString("yyyy-MM-dd")));
        await using (var c = await w.Database.OpenAdminAsync())
        {
            await c.ExecuteAsync("grant select on core.policy to core_app");
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.accounting_period"));
        }
        Assert.Equal(new PeriodClose.Answer("done", month.ToString("yyyy-MM-dd")), await PeriodClose.CloseAsync(w.App, accountant, month.ToString("yyyy-MM-dd")));
    }
}
