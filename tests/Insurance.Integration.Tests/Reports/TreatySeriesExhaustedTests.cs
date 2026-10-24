using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// Legacy scripts/number-limit-check.ts, its createTreaty row: with the "treaty" number series run out, the real createTreaty (Treaties.CreateAsync)
/// answers the series-exhausted message, writes no treaty (nor its classes) and moves no counter; negative control: with numbers left the very
/// same form creates the treaty.
/// </summary>
public sealed class TreatySeriesExhaustedTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    [Fact]
    public async Task CreateTreaty_with_the_treaty_series_run_out_writes_nothing_and_moves_no_counter()
    {
        var w = new World(db);
        var user = await w.UserAsync("admin");
        var taxId = "4" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await w.ClientAsync(taxId);
        const string name = "number-limit-check ხელშეკრულება";
        var values = new Dictionary<string, string>
        {
            ["name"] = name, ["kind"] = "facultative", ["reinsurer_id_number"] = taxId, ["period_from"] = "2026-01-01", ["period_to"] = "2026-12-31",
            ["currency"] = "GEL", ["ceded_percent"] = "10", ["commission_percent"] = "0",
        };
        Task<string> Counters() => w.ScalarAsync<string>("select string_agg(concat(code, ' ', next_value), ', ') within group (order by code) from core.number_sequence");
        Task<int> Written() => w.ScalarAsync<int>("select (select count(*) from core.ri_treaty where name = @name) + (select count(*) from core.ri_treaty_class tc join core.ri_treaty t on t.id = tc.treaty_id where t.name = @name)", new { name });
        var (next, padding) = (await w.QueryAsync<(long, int)>("select next_value, padding from core.number_sequence where code = N'treaty'")).Single();
        await using (var c = await db.OpenAdminAsync())
            await c.ExecuteAsync("update core.number_sequence set next_value = power(convert(bigint, 10), padding) where code = N'treaty'");
        try
        {
            var before = await Counters();
            var r = await Treaties.CreateAsync(w.App, user, values, ["5.1"]);
            Assert.Equal((Ka.Common.SeriesExhausted, (Guid?)null), (r.Message, r.Id));
            Assert.Empty(r.Errors);
            Assert.Equal(0, await Written());
            Assert.Equal(before, await Counters());
        }
        finally
        {
            await using var c = await db.OpenAdminAsync();
            await c.ExecuteAsync("update core.number_sequence set next_value = @next, padding = @padding where code = N'treaty'", new { next, padding });
        }
        // Negative control: the same form with numbers left creates the treaty with its class.
        var created = await Treaties.CreateAsync(w.App, user, values, ["5.1"]);
        Assert.NotNull(created.Id);
        Assert.Equal(2, await Written());
    }
}
