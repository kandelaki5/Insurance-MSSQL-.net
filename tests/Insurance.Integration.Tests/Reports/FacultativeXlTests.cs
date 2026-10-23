using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// A facultative placement and the excess-of-loss treaties around the policy, all at once (legacy facultative-xl.test.ts, Codex
/// INS-AUTOGRAPH-FACULTATIVE-XL-ELIGIBILITY-OFFLINE-20261010): the real placement (PolicyActions.PlaceFacultativeAsync) refuses when an active
/// XL covers the policy's risk through the class's parent (5 for 5.1) and writes nothing (H08); the five XL that do not cover this policy
/// together — closed, for another class (4), ended before the policy started, starting on its (exclusive) end, covering only another
/// policy's class (3.2) — do not stop it. Each decoy alone is a row of ReinsuranceChainTests' theory. Worked by hand: 365.00 × 30% = 109.50.
/// </summary>
public sealed class FacultativeXlTests
{
    private const string Placed = "30.0000|109.50|109.50|0.00|0.00|2026-07-01|2026-07-01|2027-07-01|-";

    private sealed record World(ReportWorld W, Guid Policy, Guid Facultative, Guid IssueRow);

    private static async Task<World> WorldAsync(bool parent)
    {
        var w = await CreateAsync();
        var p = await w.PolicyAsync("P-XL", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 365m, 365m, 20000m, "GEL")]);
        await w.PolicyAsync("P-OTHER", "2026-07-01", "2027-07-01", [new LineSpec("3.2", 365m, 365m, 20000m, "GEL")]);
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m);
        async Task Xl(string number, string from, string to, string cls, string status = "active") =>
            await w.TreatyAsync(number, "excess_of_loss", from, to, [cls], priority: 500m, limit: 5000m, status: status);
        if (parent) await Xl("RX005", "2026-01-01", "2026-12-31", "5");
        await Xl("RX051", "2026-01-01", "2026-12-31", "5.1", "closed");
        await Xl("RX004", "2026-01-01", "2026-12-31", "4");
        await Xl("RX052", "2025-07-01", "2026-06-30", "5.1");
        await Xl("RX053", "2027-07-01", "2028-06-30", "5.1");
        await Xl("RX032", "2026-01-01", "2026-12-31", "3.2");
        return new World(w, p.Id, fac, p.IssueRows[0]);
    }

    private static Task<string?> PlaceAsync(World x) =>
        FacultativeConcurrencyTests.PlaceAsync(x.W, new CurrentUser(x.W.Admin, "admin", "ადმინი", "admin", "ადმინისტრატორი"), x.Policy.ToString(), x.Facultative.ToString());

    private static async Task<List<string>> CessionsAsync(ReportWorld w)
    {
        await using var c = await w.Database.OpenAdminAsync();
        return (await c.QueryAsync<string>("""
            select concat(premium_transaction_id, '|', treaty_id, '|', created_by, '|', share_percent, '|', amount, '|', amount_gel, '|', commission, '|', commission_gel, '|',
                          convert(nchar(10), booked_on, 23), '|', convert(nchar(10), cover_from, 23), '|', convert(nchar(10), cover_to, 23), '|', coalesce(convert(nvarchar(36), cancels_cession_id), '-'))
            from core.premium_cession order by created_at
            """)).ToList();
    }

    /// <summary>What a placement may not change: the policies, their lines and premium rows, the treaties and their classes, the claims.</summary>
    private static async Task<string> FixedAsync(ReportWorld w)
    {
        await using var c = await w.Database.OpenAdminAsync();
        var parts = new List<string>();
        foreach (var sql in new[]
                 {
                     "select concat(number, '|', status, '|', effective_from, '|', effective_to) from core.policy order by number",
                     "select concat(class_code, '|', premium, '|', premium_gel, '|', sum_insured) from core.policy_line order by class_code",
                     "select concat(kind, '|', amount, '|', amount_gel, '|', booked_on) from core.premium_transaction order by created_at",
                     "select concat(number, '|', kind, '|', status, '|', period_from, '|', period_to, '|', priority, '|', treaty_limit, '|', ceded_percent) from core.ri_treaty order by number",
                     "select concat(t.number, '|', c.class_code) from core.ri_treaty_class c join core.ri_treaty t on t.id = c.treaty_id order by t.number",
                     "select concat(count(*), '') from core.claim",
                     "select concat(count(*), '') from core.claim_cession",
                 })
            parts.Add(string.Join(";", await c.QueryAsync<string>(sql)));
        return string.Join("\n", parts);
    }

    [Fact]
    public async Task An_active_XL_on_the_parent_class_5_in_force_over_the_policy_with_all_five_decoys_refuses_and_writes_nothing()
    {
        var x = await WorldAsync(parent: true);
        await using var _ = x.W;
        var before = await FixedAsync(x.W);
        Assert.Equal("xol-with-proportional", await PlaceAsync(x));
        Assert.Empty(await CessionsAsync(x.W));
        Assert.Equal(before, await FixedAsync(x.W));
    }

    [Fact]
    public async Task Without_it_all_five_XL_that_do_not_cover_this_policy_together_the_placement_is_made_once_109_50()
    {
        var x = await WorldAsync(parent: false);
        await using var _ = x.W;
        var before = await FixedAsync(x.W);
        Assert.Equal("placed", await PlaceAsync(x));
        var cession = Assert.Single(await CessionsAsync(x.W));
        Assert.Equal($"{x.IssueRow}|{x.Facultative}|{x.W.Admin}|{Placed}".ToUpperInvariant(), cession.ToUpperInvariant());
        Assert.Equal(before, await FixedAsync(x.W));
        // The same placement again is not made twice.
        Assert.NotEqual("placed", await PlaceAsync(x));
        Assert.Single(await CessionsAsync(x.W));
    }
}
