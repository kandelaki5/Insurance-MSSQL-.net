using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Compliance;

namespace Insurance.Integration.Tests;

/// <summary>
/// The test clock seam (DbOptions.Clock, docs/DEVELOPMENT.md "Test clock"): "now" and Tbilisi's "today" come from the clock of the Db's
/// options, carried to every Tx it opens; the system clock unless a test sets one. Business rules that read the current day or instant
/// take it from there — the month close's "has the month ended", an FMS suspension's 24 hours and 72 hours — never from the SQL
/// server's own clock, which keeps stamping created_at, the audit and closed_at in real time.
/// </summary>
public sealed class ClockTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    [Fact]
    public void Tbilisi_day_of_an_instant_UTC_plus_4_the_day_turns_at_20_00_UTC()
    {
        Assert.Equal(new DateOnly(2026, 10, 31), TbilisiClock.Day(DateTimeOffset.Parse("2026-10-31T19:59:59Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(new DateOnly(2026, 11, 1), TbilisiClock.Day(DateTimeOffset.Parse("2026-10-31T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(new DateOnly(2027, 1, 1), TbilisiClock.Day(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(4))));
        Assert.Equal(Rates.TodayInTbilisi(DateTimeOffset.Parse("2026-10-31T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void Options_without_a_clock_use_the_system_clock()
    {
        var options = new DbOptions { ConnectionString = "Server=not_used" };
        Assert.Same(TimeProvider.System, options.Clock);
        Assert.Same(TimeProvider.System, new Db(options).Clock);
        Assert.Same(TimeProvider.System, new Tx(null!, null).Clock);
    }

    [Fact]
    public async Task A_set_clock_is_today_for_the_Db_and_every_Tx_it_opens_another_Db_on_the_same_database_keeps_the_system_clock()
    {
        var clock = new TestClock();
        var set = new Db(new DbOptions { ConnectionString = db.AppConnection, Clock = clock });
        var plain = new Db(new DbOptions { ConnectionString = db.AppConnection });
        // Not set: it follows the system clock.
        var before = Rates.TodayInTbilisi();
        var day = set.Today();
        Assert.True(day == before || day == Rates.TodayInTbilisi());
        clock.SetToday(new DateOnly(2026, 11, 7));
        Assert.Equal(new DateOnly(2026, 11, 7), set.Today());
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(4)), set.Now());
        Assert.Equal(new DateOnly(2026, 11, 7), await set.ReadTxAsync(tx => Task.FromResult(tx.Today())));
        Assert.Equal(new DateOnly(2026, 11, 7), await set.SnapshotAsync(tx => Task.FromResult(tx.Today())));
        Assert.Equal(new DateOnly(2026, 11, 7), await set.InTransactionAsync(tx => Task.FromResult(tx.Today())));
        Assert.Same(clock, await set.AsUserAsync(await Fixtures.UserAsync(db, "admin"), tx => Task.FromResult(tx.Clock)));
        // A Tbilisi day starts at 20:00 UTC of the day before.
        clock.Set(DateTimeOffset.Parse("2026-10-31T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(new DateOnly(2026, 11, 1), set.Today());
        Assert.Same(TimeProvider.System, plain.Clock);
        var real = plain.Today();
        Assert.True(real == before || real == Rates.TodayInTbilisi());
        // The SQL stamps stay the server's real time whatever the clock says.
        var stamped = await set.ReadTxAsync(tx => tx.ExecuteScalarAsync<DateTimeOffset>("select sysdatetimeoffset()"));
        Assert.True(Math.Abs((stamped - DateTimeOffset.UtcNow).TotalHours) < 1);
        clock.Release();
        Assert.False(clock.IsSet);
    }

    [Fact]
    public async Task The_month_close_asks_the_clocks_day_whether_a_month_has_ended()
    {
        var own = new TestDatabase();
        await own.InitializeAsync();
        try
        {
            var id = await Fixtures.UserAsync(own, "accountant");
            var accountant = new CurrentUser(id, $"test.{id:N}"[..30], "სატესტო", "accountant", "accountant");
            // No policy: the series is the asked month itself. On 15.11.2026 November has not ended; October has.
            own.Clock.SetToday(new DateOnly(2026, 11, 15));
            Assert.Equal(new PeriodClose.Answer("not-ended", "2026-11-01"), await PeriodClose.CloseAsync(own.App, accountant, "2026-11-01"));
            Assert.Equal(new PeriodClose.Answer("done", "2026-10-01"), await PeriodClose.CloseAsync(own.App, accountant, "2026-10-01"));
            own.Clock.SetToday(new DateOnly(2026, 12, 1));
            Assert.Equal(new PeriodClose.Answer("done", "2026-11-01"), await PeriodClose.CloseAsync(own.App, accountant, "2026-11-01"));
            await using var c = await own.OpenAdminAsync();
            Assert.Equal([(new DateOnly(2026, 10, 1), "closed"), (new DateOnly(2026, 11, 1), "closed")],
                (await c.QueryAsync<(DateOnly, string)>("select period_start, status from core.accounting_period order by period_start")).ToList());
        }
        finally
        {
            await own.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_oral_suspensions_24_and_72_hours_are_counted_on_the_clock_awaiting_then_unconfirmed_then_lapsed()
    {
        var clock = new TestClock();
        var app = new Db(new DbOptions { ConnectionString = db.AppConnection, Clock = clock });
        var party = Guid.NewGuid();
        // An oral FMS instruction given on 07.11.2026 at 10:00 Tbilisi time (after the real day: the SQL server's clock would still call it awaiting).
        var instructed = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(4));
        await using (var c = await db.OpenAppAsync())
            await c.ExecuteAsync("""
                insert core.party (id, kind, first_name, last_name, tax_id, risk_level) values (@party, N'person', N'ა', N'საათი', @taxId, N'low');
                insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at) values (newid(), @party, N'oral', N'all', @instructed, @ends);
                """, new { party, taxId = Fixtures.RandomTaxId(), instructed, ends = Suspension.SuspensionEnd(instructed) });
        async Task<string?> StateAt(DateTimeOffset at)
        {
            clock.Set(at);
            return (await app.ReadTxAsync(tx => Aml.ActiveSuspensionAsync(tx, party)))?.State;
        }
        Assert.Equal("awaiting", await StateAt(instructed.AddHours(23)));
        Assert.Equal("unconfirmed", await StateAt(instructed.AddHours(25)));
        Assert.Null(await StateAt(Suspension.SuspensionEnd(instructed).AddMinutes(1)));
        // Every reader of the suspension states agrees with the clock.
        clock.Set(instructed.AddHours(25));
        Assert.Equal(["unconfirmed"], (await app.ReadTxAsync(tx => Aml.SuspensionsOfAsync(tx, party))).Select(r => r.State));
        Assert.NotNull(await app.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, party)));
    }
}
