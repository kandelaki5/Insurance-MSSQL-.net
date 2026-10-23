using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.Data.SqlClient;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// Two facultative placements of 60 % on one policy at the same time (the legacy two-session test behind placeFacultative's policy lock):
/// the second waits for the first and then sees it, so it is refused as over-ceded; never 120 %.
/// </summary>
public sealed class FacultativeConcurrencyTests
{
    private static CurrentUser Writer(Guid id) => new(id, "test.sales", "სატესტო", "sales", "sales");

    /// <summary>The policy card's placement (PolicyActions.PlaceFacultativeAsync): the outcome code it sends the card back with, or null when nothing is done.</summary>
    internal static async Task<string?> PlaceAsync(ReportWorld w, CurrentUser user, string policy, string treaty)
    {
        var back = await PolicyActions.PlaceFacultativeAsync(w.App, user, policy, treaty);
        if (back is null) return null;
        Assert.StartsWith($"/policies/{policy}?facultative=", back);
        return back[(back.IndexOf('=') + 1)..];
    }

    [Fact]
    public async Task Two_60_percent_placements_at_once_the_second_waits_and_is_refused()
    {
        await using var w = await CreateAsync();
        var p = await w.PolicyAsync("P-FAC2", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 365m, 365m)]);
        var first = await w.TreatyAsync("RF060-A", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 60m);
        var second = await w.TreatyAsync("RF060-B", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 60m);

        await using var c = await w.Database.OpenAppAsync();
        await using var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = w.Admin.ToString() }, t);
        Assert.Equal(ReinsuranceStore.FacultativeOutcome.Placed, await ReinsuranceStore.PlaceFacultativeAsync(new Tx(c, t), p.Id, first, w.Admin));

        var racing = PlaceAsync(w, Writer(w.Admin), p.Id.ToString(), second.ToString());
        Assert.NotSame(racing, await Task.WhenAny(racing, Task.Delay(1500)));
        await t.CommitAsync();
        Assert.Equal("over-ceded", await racing);

        await using var check = await w.Database.OpenAdminAsync();
        Assert.Equal([(first, 60m, 219.00m)], (await check.QueryAsync<(Guid, decimal, decimal)>("select treaty_id, share_percent, amount_gel from core.premium_cession")).ToList());
    }

    [Fact]
    public async Task Run_together_one_is_placed_and_the_other_refused()
    {
        await using var w = await CreateAsync();
        var p = await w.PolicyAsync("P-FAC3", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 365m, 365m)]);
        var a = await w.TreatyAsync("RF060-A", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 60m);
        var b = await w.TreatyAsync("RF060-B", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 60m);
        var outcomes = await Task.WhenAll(
            PlaceAsync(w, Writer(w.Admin), p.Id.ToString(), a.ToString()),
            PlaceAsync(w, Writer(w.Admin), p.Id.ToString(), b.ToString()));
        Assert.Equal(["over-ceded", "placed"], outcomes.Order(StringComparer.Ordinal));
        await using var check = await w.Database.OpenAdminAsync();
        Assert.Equal(60m, await check.ExecuteScalarAsync<decimal>("select sum(share_percent) from core.premium_cession"));
        // A viewer and a malformed id do nothing; a closed month gives period-closed.
        Assert.Null(await PlaceAsync(w, new CurrentUser(w.Admin, "v", "v", "viewer", "viewer"), p.Id.ToString(), a.ToString()));
        Assert.Null(await PlaceAsync(w, Writer(w.Admin), "not-a-uuid", a.ToString()));
        var c = await w.TreatyAsync("RF010", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 10m);
        await w.ClosePeriodAsync("2026-07");
        Assert.Equal("period-closed", await PlaceAsync(w, Writer(w.Admin), p.Id.ToString(), c.ToString()));
    }
}
