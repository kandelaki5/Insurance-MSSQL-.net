using System.Globalization;
using Dapper;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Billing.LoadReleasableAsync's amounts are exact over numeric(18, 2)'s whole range, on rows stored in a real database
/// (legacy releasable-exact.test.ts; owner 07.10.2026 and Codex H57): the excess goes back from the latest allocation
/// first, only from ordinary unreversed receipts; a whole holding returns its exact GEL remainder, a part at the
/// allocation's rate rounded half up, never more than that remainder. One instalment per case, adjusted to its amount
/// by a dated reduction; its allocations as the legacy rows gave them (booked September–October 2026, read today).
/// </summary>
public sealed class GapsReleasableExactTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    private sealed record Line(string Payment, string Amount, string AmountGel, string Rate = "1.000000", string BookedOn = "2026-09-07", bool Offset = false);

    /// <summary>The releasable rows of one instalment now worth <paramref name="amount"/>, held by <paramref name="lines"/> (latest first): payment, excess, its GEL.</summary>
    private async Task<List<(string Payment, decimal Excess, decimal ExcessGel)>> ReleasableAsync(string amount, Line[] lines, string currency = "GEL")
    {
        var holder = await w.ClientAsync();
        var held = lines.Sum(l => D(l.Amount));
        var instalment = held > 0 ? held : 1m;
        var p = await w.PendingPolicyAsync(holder, new DateOnly(2026, 9, 1), currency, currency == "GEL" ? 1m : 2.7m, instalment, [(0, instalment)]);
        var inst = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = p.Id });
        var payments = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using var c = await w.Database.OpenAppAsync();
        if (D(amount) != instalment)
            await c.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@inst, '2026-10-20', @change, N'შემცირება')",
                new { inst, change = D(amount) - instalment });
        foreach (var l in lines.Reverse())
        {
            if (!payments.TryGetValue(l.Payment, out var payment))
            {
                payment = Guid.NewGuid();
                payments[l.Payment] = payment;
                await c.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, payer_party_id) values (@payment, '2026-09-01', 9999999999999999.99, @method, @holder)",
                    new { payment, method = l.Offset ? "claim_offset" : "bank", holder });
            }
            await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@payment, @inst, @amount, @gel, @rate, @on)",
                new { payment, inst, amount = D(l.Amount), gel = D(l.AmountGel), rate = D(l.Rate), on = DateOnly.Parse(l.BookedOn, CultureInfo.InvariantCulture) });
        }
        var rows = await w.App.ReadTxAsync(tx => Billing.LoadReleasableAsync(tx, holder, World.Today));
        return rows.Select(r => (payments.Single(x => x.Value == r.PaymentId).Key, r.Excess, r.ExcessGel)).ToList();
    }

    [Fact]
    public async Task A_whole_holding_of_numeric_18_2s_largest_amounts_returns_exactly_to_the_tetri()
    {
        Assert.Equal([("R", 90071992547408.01m, 90071992547408.01m)], await ReleasableAsync("0.00", [new("R", "90071992547408.01", "90071992547408.01")]));
        Assert.Equal([("R", 9999999999999999.99m, 9999999999999999.99m)], await ReleasableAsync("0.00", [new("R", "9999999999999999.99", "9999999999999999.99")]));
    }

    [Fact]
    public async Task A_one_tetri_excess_on_the_largest_amounts_is_found_not_missed() =>
        Assert.Equal([("R", 0.01m, 0.01m)], await ReleasableAsync("90071992547408.01", [new("R", "90071992547408.02", "90071992547408.02")]));

    [Fact]
    public async Task Ordinary_amounts_as_before_a_whole_150_1234_51_held_on_1234_50_nothing_on_an_equal_amount()
    {
        Assert.Equal([("R", 150m, 150m)], await ReleasableAsync("0.00", [new("R", "150.00", "150.00")]));
        Assert.Equal([("R", 0.01m, 0.01m)], await ReleasableAsync("1234.50", [new("R", "1234.51", "1234.51")]));
        Assert.Empty(await ReleasableAsync("150.00", [new("R", "150.00", "150.00")]));
    }

    [Fact]
    public async Task An_earlier_release_nets_off_a_part_goes_back_at_the_rate_USD_40_at_2_7_is_108() =>
        Assert.Equal([("R", 40m, 108m)], await ReleasableAsync("40.00", [new("R", "-20.00", "-54.00", "2.700000", "2026-10-15"), new("R", "100.00", "270.00", "2.700000", "2026-09-07")], "USD"));

    [Fact]
    public async Task The_latest_receipt_gives_back_first_whole_the_older_one_the_rest_in_part_a_claim_set_off_is_passed_over()
    {
        Assert.Equal([("R2", 30m, 30m), ("R1", 10m, 10m)],
            await ReleasableAsync("50.00", [new("R2", "30.00", "30.00", BookedOn: "2026-10-01"), new("R1", "60.00", "60.00", BookedOn: "2026-09-01")]));
        Assert.Equal([("R1", 40m, 40m)],
            await ReleasableAsync("50.00", [new("OFF", "30.00", "30.00", BookedOn: "2026-10-01", Offset: true), new("R1", "60.00", "60.00", BookedOn: "2026-09-01")]));
    }

    [Fact]
    public async Task A_whole_holding_gives_back_its_exact_GEL_remainder_not_its_amount_at_a_rate() =>
        // 50.00 USD at 2.7 (135.00 GEL) and, later, 50.00 at 2.6998 (134.99 GEL): the whole 100.00 returns 269.99 — at the latest rate 269.98.
        Assert.Equal([("R", 100m, 269.99m)], await ReleasableAsync("0.00", [new("R", "50.00", "134.99", "2.699800", "2026-10-01"), new("R", "50.00", "135.00", "2.700000", "2026-09-07")], "USD"));

    [Fact]
    public async Task A_part_never_gives_back_more_GEL_than_the_holding_has_left_capped() =>
        // Net 5.00 EUR holding 13.40 GEL; 4.99 too much at 2.702 would be 13.48 > 13.40: capped at 13.40.
        Assert.Equal([("R", 4.99m, 13.40m)], await ReleasableAsync("0.01", [new("R", "-5.00", "-13.60", "2.702000", "2026-10-15"), new("R", "10.00", "27.00", "2.700000", "2026-09-07")], "EUR"));

    [Fact]
    public async Task A_part_at_the_rate_is_rounded_half_up_exactly_at_any_size()
    {
        Assert.Equal([("R", 0.01m, 0.03m)], await ReleasableAsync("9.99", [new("R", "10.00", "25.00", "2.500000")], "USD"));
        Assert.Equal([("R", 0.03m, 0.08m)], await ReleasableAsync("9.97", [new("R", "10.00", "25.00", "2.500000")], "USD"));
        Assert.Equal([("R", 0.01m, 0.02m)], await ReleasableAsync("9.99", [new("R", "10.00", "25.00", "2.499900")], "USD"));
        // 9007199254740801 tetri × 2.5 = 22517998136852002.5 → half up 22517998136852003 = 225179981368520.03.
        Assert.Equal([("R", 90071992547408.01m, 225179981368520.03m)], await ReleasableAsync("0.01", [new("R", "90071992547408.02", "300000000000000.00", "2.500000")], "USD"));
    }
}
