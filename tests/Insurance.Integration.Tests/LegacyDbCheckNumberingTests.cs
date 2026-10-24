using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// Document numbers on the real SQL Server, ported from the legacy real-database checks scripts/number-concurrency-regression.ts
/// (the SQL level: core.next_number as core_app, 20 requests at once over 8 connections, several rounds, two series, a rolled-back
/// transaction, the padding boundary, a negative control without the row lock), scripts/number-limit-check.ts (the SQL level: an
/// exhausted series refuses NU001, nothing is written, the counter does not move, also under concurrency; and registerClaim with
/// the claim series run out) and scripts/verify-0026-0028.ts parts 1 and 2 (every series fits; a prefix change is audited, a
/// counter move is not). Each series is the test's own.
/// </summary>
public sealed class LegacyDbCheckNumberingTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private const int Connections = 8;
    private const int Parallel = 20;

    private static string Series(string name) => $"it-num-{name}-{Guid.NewGuid():N}"[..40];

    private async Task<string> NewSeriesAsync(string name, string prefix, int padding = 6, long next = 1)
    {
        var code = Series(name);
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("insert core.number_sequence (code, prefix, next_value, padding) values (@code, @prefix, @next, @padding)", new { code, prefix, next, padding });
        return code;
    }

    private async Task<long> CounterAsync(string code)
    {
        await using var c = await db.OpenAdminAsync();
        return await c.ExecuteScalarAsync<long>("select next_value from core.number_sequence where code = @code", new { code });
    }

    /// <summary>Separate sessions of the application role: requests on different connections really overlap, those on one queue.</summary>
    private async Task<SqlConnection[]> AppsAsync()
    {
        var apps = new SqlConnection[Connections];
        for (var i = 0; i < Connections; i++) apps[i] = await db.OpenAppAsync();
        return apps;
    }

    private static async Task DisposeAllAsync(SqlConnection[] apps)
    {
        foreach (var a in apps) await a.DisposeAsync();
    }

    /// <summary>One request: the number, or the code it was refused with; in its own transaction that holds briefly (legacy pg_sleep(0.02)).</summary>
    private static async Task<(string? N, string? Error)> TakeAsync(SqlConnection c, string code, bool hold = true)
    {
        await using var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await c.ExecuteAsync("set xact_abort on", transaction: t);
            var n = await new Tx(c, t).NextNumberAsync(code);
            if (hold) await c.ExecuteAsync("waitfor delay '00:00:00.020'", transaction: t);
            await t.CommitAsync();
            return (n, null);
        }
        catch (SqlException e)
        {
            return (null, DbError.FromSql(e)?.Code ?? e.Message);
        }
    }

    /// <summary>`count` requests sent at once, spread over the connections; each connection runs its share one after the other.</summary>
    private static async Task<List<(string? N, string? Error)>> BurstAsync(SqlConnection[] apps, Func<int, string> codeOf, int count = Parallel)
    {
        var lanes = Enumerable.Range(0, apps.Length).Select(async lane =>
        {
            var mine = new List<(string?, string?)>();
            for (var i = lane; i < count; i += apps.Length) mine.Add(await TakeAsync(apps[lane], codeOf(i)));
            return mine;
        }).ToArray();
        return (await Task.WhenAll(lanes)).SelectMany(x => x).ToList();
    }

    private static void AssertDistinct(IReadOnlyCollection<string?> numbers)
    {
        Assert.All(numbers, n => Assert.False(string.IsNullOrEmpty(n)));
        Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------- number-concurrency-regression.ts, SQL level

    [Fact]
    public async Task Twenty_requests_at_once_over_eight_connections_get_1_to_20_and_three_more_rounds_reach_80_without_a_gap()
    {
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var s1 = await NewSeriesAsync("a", $"RNA{tag}-");
        var apps = await AppsAsync();
        try
        {
            var round1 = (await BurstAsync(apps, _ => s1)).Select(r => r.N).ToList();
            AssertDistinct(round1);
            Assert.Equal(Enumerable.Range(1, Parallel).Select(i => $"RNA{tag}-{i:D6}"), round1.Order(StringComparer.Ordinal));
            Assert.Equal(21, await CounterAsync(s1));

            var all = new List<string?>(round1);
            for (var r = 0; r < 3; r++) all.AddRange((await BurstAsync(apps, _ => s1)).Select(x => x.N));
            AssertDistinct(all);
            Assert.Equal(Enumerable.Range(1, 80).Select(i => $"RNA{tag}-{i:D6}"), all.Order(StringComparer.Ordinal));
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task Negative_control_read_then_write_numbering_without_a_lock_gives_duplicates_under_the_same_load()
    {
        var sN = await NewSeriesAsync("n", "RNN-");
        var apps = await AppsAsync();
        try
        {
            // A mistaken implementation: read the counter, hold, write it back plus one — no row lock.
            var lanes = apps.Select(async c =>
            {
                var got = new List<long>();
                for (var i = 0; i < Parallel / Connections + 1; i++)
                {
                    await using var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                    var v = await c.ExecuteScalarAsync<long>("select next_value from core.number_sequence where code = @sN", new { sN }, t);
                    await c.ExecuteAsync("waitfor delay '00:00:00.020'", transaction: t);
                    await c.ExecuteAsync("update core.number_sequence set next_value = @v where code = @sN", new { v = v + 1, sN }, t);
                    await t.CommitAsync();
                    got.Add(v);
                }
                return got;
            }).ToArray();
            var naive = (await Task.WhenAll(lanes)).SelectMany(x => x).ToList();
            Assert.True(naive.Distinct().Count() < naive.Count, "the race shows: duplicates appear, so the check above can see them");
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task Two_series_at_the_same_time_each_get_their_own_1_to_10()
    {
        var sB = await NewSeriesAsync("b", "RNB-");
        var sC = await NewSeriesAsync("c", "RNC-");
        var apps = await AppsAsync();
        try
        {
            var mixed = (await BurstAsync(apps, i => i % 2 == 1 ? sC : sB)).Select(r => r.N).ToList();
            var bs = mixed.Where(n => n!.StartsWith("RNB-", StringComparison.Ordinal)).ToList();
            var cs = mixed.Where(n => n!.StartsWith("RNC-", StringComparison.Ordinal)).ToList();
            AssertDistinct(bs);
            AssertDistinct(cs);
            Assert.Equal(Enumerable.Range(1, 10).Select(i => $"RNB-{i:D6}"), bs.Order(StringComparer.Ordinal));
            Assert.Equal(Enumerable.Range(1, 10).Select(i => $"RNC-{i:D6}"), cs.Order(StringComparer.Ordinal));
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task A_number_taken_in_a_rolled_back_transaction_is_given_again_once_and_five_requests_at_once_get_1_to_5()
    {
        var sR = await NewSeriesAsync("r", "RNR-");
        var apps = await AppsAsync();
        try
        {
            string? lost;
            await using (var t = (SqlTransaction)await apps[0].BeginTransactionAsync())
            {
                lost = await new Tx(apps[0], t).NextNumberAsync(sR);
                await t.RollbackAsync();
            }
            Assert.Equal("RNR-000001", lost);
            var after = (await BurstAsync(apps, _ => sR, 5)).Select(r => r.N).ToList();
            AssertDistinct(after);
            Assert.Equal(Enumerable.Range(1, 5).Select(i => $"RNR-{i:D6}"), after.Order(StringComparer.Ordinal));
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task A_two_digit_series_after_99_numbers_refuses_the_100th_with_NU001_and_never_gives_an_earlier_number()
    {
        var sP = await NewSeriesAsync("p", "RNP-", padding: 2);
        var apps = await AppsAsync();
        try
        {
            var upTo99 = new List<string?>();
            for (var i = 0; i < 99; i++) upTo99.Add((await TakeAsync(apps[i % Connections], sP, hold: false)).N);
            AssertDistinct(upTo99);
            Assert.Equal("RNP-99", upTo99[^1]);
            Assert.Equal((null, "NU001"), await TakeAsync(apps[0], sP));
            Assert.Equal(100, await CounterAsync(sP));
            // A series that does not exist gives no number, not an error (the insert that uses it then fails on "number not null").
            Assert.Equal((null, null), await TakeAsync(apps[0], Series("missing")));
        }
        finally { await DisposeAllAsync(apps); }
    }

    // ---------------------------------------------------------------- number-limit-check.ts, SQL level

    [Fact]
    public async Task The_100th_and_101st_request_of_a_two_digit_series_are_both_refused_and_the_counter_stays_at_100()
    {
        var two = await NewSeriesAsync("two", "NL-", padding: 2);
        var apps = await AppsAsync();
        try
        {
            string? last = null;
            for (var i = 0; i < 99; i++) last = (await TakeAsync(apps[i % Connections], two, hold: false)).N;
            Assert.Equal("NL-99", last);
            Assert.Equal((null, "NU001"), await TakeAsync(apps[0], two));
            Assert.Equal((null, "NU001"), await TakeAsync(apps[1], two));
            Assert.Equal(100, await CounterAsync(two));
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task A_six_digit_series_at_999999_gives_NS999999_then_refuses_and_the_counter_stays_at_1000000()
    {
        var six = await NewSeriesAsync("six", "NS", padding: 6, next: 999999);
        var apps = await AppsAsync();
        try
        {
            Assert.Equal(("NS999999", (string?)null), await TakeAsync(apps[0], six));
            Assert.Equal((null, "NU001"), await TakeAsync(apps[1], six));
            Assert.Equal(1000000, await CounterAsync(six));
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task Twenty_requests_at_once_nine_numbers_before_the_limit_give_91_to_99_once_each_and_refuse_the_other_eleven()
    {
        var edge = await NewSeriesAsync("edge", "NE-", padding: 2, next: 91);
        var apps = await AppsAsync();
        try
        {
            var burst = await BurstAsync(apps, _ => edge);
            var got = burst.Where(b => b.N is not null).Select(b => b.N).ToList();
            AssertDistinct(got);
            Assert.Equal(Enumerable.Range(91, 9).Select(i => $"NE-{i}"), got.Order(StringComparer.Ordinal));
            Assert.Equal(11, burst.Count(b => b.Error == "NU001"));
            Assert.Equal(20, burst.Count);
            Assert.Equal(100, await CounterAsync(edge));
        }
        finally { await DisposeAllAsync(apps); }
    }

    [Fact]
    public async Task An_exhausted_series_called_outside_a_transaction_also_leaves_the_counter_where_it_was()
    {
        // The legacy check called core.next_number as a single autocommitted statement: the refusal rolled its update back. In
        // SQL Server a procedure called outside a transaction commits each statement on its own, so the procedure must refuse
        // before it moves the counter (negative control: the procedure as first ported moved it to 101).
        var two = await NewSeriesAsync("auto", "NA-", padding: 2, next: 100);
        await using var c = await db.OpenAppAsync();
        var p = new DynamicParameters();
        p.Add("seq", two);
        p.Add("number", dbType: DbType.String, size: 200, direction: ParameterDirection.Output);
        var e = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync("core.next_number", p, commandType: CommandType.StoredProcedure));
        Assert.Equal("NU001", DbError.FromSql(e)!.Code);
        Assert.Equal(100, await CounterAsync(two));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select @@trancount"));
    }

    // ---------------------------------------------------------------- number-limit-check.ts, registerClaim

    [Fact]
    public async Task RegisterClaim_with_the_claim_series_run_out_on_one_or_several_coverages_writes_nothing_and_moves_no_counter()
    {
        var w = new ClaimWorld(db);
        var u = await w.UserAsync("claims");
        var p = await w.PolicyAsync(u.Id, new DateOnly(2701, 1, 1), [new("damage", 20000m, 0m, 365m), new("equipment", 2000m, 0m, 50m)]);
        async Task<string> Counters() => string.Join(", ", await w.RowsAsync<string>(
            "select code + N' ' + convert(nvarchar(20), next_value) from core.number_sequence where code in (N'contract', N'policy:CASCO', N'claim', N'complaint', N'aml_case', N'treaty', N'subrogation') order by code"));
        async Task<int> Written() => await w.ScalarAsync<int>(
            "select (select count(*) from core.claim where policy_id = @id) + (select count(*) from core.loss_event where policy_id = @id)", new { id = p.Id });
        var (next, padding) = (await w.RowsAsync<(long, int)>("select next_value, padding from core.number_sequence where code = N'claim'")).Single();
        await w.AdminAsync("update core.number_sequence set next_value = power(convert(bigint, 10), padding) where code = N'claim'");
        try
        {
            foreach (var form in new[]
                     {
                         ClaimWorld.Form(("policy_number", p.Number), ("policy_line_id", p.LineOf("damage").ToString()), ("occurred_on", "2701-02-01"),
                             ("reported_on", "2701-02-02"), ("description", "ზარალი"), ("initial_reserve", "100")),
                         ClaimWorld.Form(("policy_number", p.Number), ("occurred_on", "2701-02-01"), ("reported_on", "2701-02-02"), ("description", "ზარალი"),
                             ($"line:{p.LineOf("damage")}", "on"), ($"reserve:{p.LineOf("damage")}", "100"),
                             ($"line:{p.LineOf("equipment")}", "on"), ($"reserve:{p.LineOf("equipment")}", "100")),
                     })
            {
                var before = await Counters();
                var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, form);
                Assert.Null(outcome.RedirectTo);
                Assert.Equal(Ka.Common.SeriesExhausted, outcome.Message);
                Assert.Equal(0, await Written());
                Assert.Equal(before, await Counters());
            }
        }
        finally
        {
            await w.AdminAsync("update core.number_sequence set next_value = @next, padding = @padding where code = N'claim'", new { next, padding });
        }
    }

    // ---------------------------------------------------------------- verify-0026-0028.ts, parts 1 and 2

    [Fact]
    public async Task Every_real_series_fits_its_digits_V99_then_NU001_with_the_counter_at_100()
    {
        await using var c = await db.OpenAdminAsync();
        Assert.Empty(await c.QueryAsync<string>("select code from core.number_sequence where code not like N'it-%' and next_value >= power(convert(bigint, 10), padding)"));
        var v = await NewSeriesAsync("v", "V", padding: 2, next: 99);
        var actor = await Fixtures.UserAsync(db);
        Assert.Equal("V99", await db.App.AsUserAsync(actor, tx => tx.NextNumberAsync(v)));
        Assert.Equal("NU001", await LegacyDbCheck.BareCodeOf(() => db.App.AsUserAsync(actor, tx => tx.NextNumberAsync(v))));
        Assert.Equal(100, await CounterAsync(v));
    }

    [Fact]
    public async Task Moving_a_series_counter_is_not_audited_and_changing_its_prefix_is_with_the_old_and_new_value()
    {
        var actor = await Fixtures.UserAsync(db);
        var v = await NewSeriesAsync("audit", "V", padding: 2, next: 5);
        await using var c = await db.OpenAdminAsync();
        async Task<int> AuditRows() => await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name = N'number_sequence' and row_id = @v", new { v });
        var before = await AuditRows();
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("update core.number_sequence set next_value = next_value + 1 where code = @v", new { v }));
        await db.App.AsUserAsync(actor, tx => tx.NextNumberAsync(v));
        Assert.Equal(before, await AuditRows());
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("update core.number_sequence set prefix = N'W' where code = @v", new { v }));
        Assert.Equal(before + 1, await AuditRows());
        var (oldRow, newRow, userId) = (await c.QueryAsync<(string, string, Guid?)>(
            "select top (1) old_row, new_row, user_id from core.audit_log where table_name = N'number_sequence' and row_id = @v order by id desc", new { v })).Single();
        Assert.Contains("\"prefix\":\"V\"", oldRow);
        Assert.Contains("\"prefix\":\"W\"", newRow);
        Assert.Equal(actor, userId);
    }
}
