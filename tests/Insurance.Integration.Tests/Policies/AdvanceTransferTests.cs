using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/advance-allocation-regression.ts (owner 05.10.2026, H21) and the transfer cases of
/// scripts/integration/payment-shared-page.test.ts (owner 07.10.2026, TRANSFER_OVERPAYMENT_MONETARY_DECIMAL_PRECISION):
/// an earlier receipt allocated to a policy, and another policy's overpayment moved onto it, on a real database.
/// </summary>
public sealed class AdvanceTransferTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Received = Start.AddDays(4);

    private Task<string> Allocate(CurrentUser user, Guid policy, Guid payment) =>
        PaymentActions.AllocateReceiptAsync(w.App, w.Rates, user, policy.ToString(), payment.ToString());

    private Task<string> Transfer(CurrentUser user, Guid policy, Guid payment) =>
        PaymentActions.TransferOverpaymentAsync(w.App, w.Rates, user, policy.ToString(), payment.ToString());

    [Fact]
    public async Task A_a_whole_receipt_settles_every_instalment_oldest_first_booked_today_at_the_receipt_rate()
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        var pol = await w.ActivePolicyAsync(user, holder, Start);
        var r = await w.ReceiptAsync(holder, Received, 365m, user.Id, "A");
        Assert.Equal((365m, 365m), (await w.AvailableAsync(r), await w.OwedAsync(pol.Id)));
        var receiptBefore = await w.QueryAsync<(DateOnly, decimal, Guid?, DateTimeOffset)>("select received_on, amount_gel, created_by, created_at from core.payment where id = @r", new { r });
        var receipts = await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder });

        Assert.Equal($"/policies/{pol.Id}?advance=allocated&gel=36500&left=0", await Allocate(user, pol.Id, r));
        Assert.Equal((0m, 0m), (await w.AvailableAsync(r), await w.OwedAsync(pol.Id)));
        Assert.Equal(receipts, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder }));
        var lines = await w.QueryAsync<(DateOnly DueOn, decimal Amount, DateOnly BookedOn, decimal Rate, Guid? CreatedBy)>("""
            select i.due_on, a.amount, a.booked_on, a.rate, a.created_by from core.payment_allocation a join core.installment i on i.id = a.installment_id
            where a.payment_id = @r order by i.due_on
            """, new { r });
        Assert.Equal([0, 91, 182, 273], lines.Select(l => l.DueOn.DayNumber - Start.DayNumber));
        Assert.All(lines, l => Assert.Equal((91.25m, World.Today, 1m, (Guid?)user.Id), (l.Amount, l.BookedOn, l.Rate, l.CreatedBy)));
        Assert.Equal(receiptBefore, await w.QueryAsync<(DateOnly, decimal, Guid?, DateTimeOffset)>("select received_on, amount_gel, created_by, created_at from core.payment where id = @r", new { r }));
        // History: as of the receipt month's end the policy still owed 365 (allocations booked by then).
        var monthEnd = new DateOnly(2026, 1, 31);
        Assert.Equal(365m, await w.ScalarAsync<decimal>("""
            select (select sum(i.amount) from core.installment i where i.policy_id = @p)
                 - coalesce((select sum(a.amount) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @p and a.booked_on <= @monthEnd), 0)
            """, new { p = pol.Id, monthEnd }));
        Assert.Equal($"/policies/{pol.Id}?advance=nothing-left", await Allocate(user, pol.Id, r));
        Assert.Equal(4, await w.ScalarAsync<int>("select count(*) from core.payment_allocation where payment_id = @r", new { r }));
    }

    [Fact]
    public async Task B_C_D_E_F_part_remainder_another_client_viewer_reversed()
    {
        var user = await w.UserAsync();
        var viewer = await w.UserAsync("viewer");
        // B: 91.25 received, owed 365.
        var holderB = await w.ClientAsync();
        var polB = await w.ActivePolicyAsync(user, holderB, Start);
        var rB = await w.ReceiptAsync(holderB, Received, 91.25m);
        Assert.Equal($"/policies/{polB.Id}?advance=allocated&gel=9125&left=0", await Allocate(user, polB.Id, rB));
        Assert.Equal((0m, 273.75m), (await w.AvailableAsync(rB), await w.OwedAsync(polB.Id)));

        // C: 500 received, the policy owes 365; the remaining 135 goes to the client's second policy.
        var holderC = await w.ClientAsync();
        var polC1 = await w.ActivePolicyAsync(user, holderC, Start);
        var polC2 = await w.ActivePolicyAsync(user, holderC, Start);
        var rC = await w.ReceiptAsync(holderC, Received, 500m);
        Assert.Equal($"/policies/{polC1.Id}?advance=allocated&gel=36500&left=13500", await Allocate(user, polC1.Id, rC));
        Assert.Equal((0m, 135m), (await w.OwedAsync(polC1.Id), await w.AvailableAsync(rC)));
        Assert.Equal($"/policies/{polC2.Id}?advance=allocated&gel=13500&left=0", await Allocate(user, polC2.Id, rC));
        Assert.Equal((230m, 0m), (await w.OwedAsync(polC2.Id), await w.AvailableAsync(rC)));
        var settled = await w.QueryAsync<decimal>("""
            select coalesce(sum(a.amount), 0) from core.installment i left join core.payment_allocation a on a.installment_id = i.id
            where i.policy_id = @p group by i.id, i.due_on order by i.due_on
            """, new { p = polC2.Id });
        Assert.Equal([91.25m, 43.75m, 0m, 0m], settled);
        Assert.Equal($"/policies/{polC1.Id}?advance=nothing-owed", await Allocate(user, polC1.Id, await w.ReceiptAsync(holderC, Received, 10m)));

        // D: a receipt paid by another client.
        var holderD = await w.ClientAsync();
        var polD = await w.ActivePolicyAsync(user, holderD, Start);
        var foreign = await w.ReceiptAsync(holderB, Received, 50m);
        Assert.Equal($"/policies/{polD.Id}?advance=not-found", await Allocate(user, polD.Id, foreign));
        Assert.Equal((50m, 365m), (await w.AvailableAsync(foreign), await w.OwedAsync(polD.Id)));

        // E: a viewer (read-only role).
        var rViewer = await w.ReceiptAsync(holderD, Received, 91.25m);
        Assert.Equal($"/policies/{polD.Id}?advance=read-only", await Allocate(viewer, polD.Id, rViewer));
        Assert.Equal($"/policies/{polD.Id}?advance=read-only", await Transfer(viewer, polD.Id, rViewer));
        Assert.Equal((91.25m, 365m), (await w.AvailableAsync(rViewer), await w.OwedAsync(polD.Id)));

        // F: a reversed receipt.
        var rRev = await w.ReceiptAsync(holderD, Received, 100m);
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, user, rRev.ToString(), polD.Id.ToString(), "შებრუნება"));
        Assert.Equal($"/policies/{polD.Id}?advance=nothing-left", await Allocate(user, polD.Id, rRev));
        Assert.Equal(365m, await w.OwedAsync(polD.Id));

        // Malformed ids: the policy list, or "not found" on the card.
        Assert.Equal("/policies", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, user, "x", rRev.ToString()));
        Assert.Equal($"/policies/{polD.Id}?advance=not-found", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, user, polD.Id.ToString(), "x"));
    }

    [Fact]
    public async Task G_one_receipt_to_two_policies_or_twice_at_the_same_moment_is_allocated_once()
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        var g1 = await w.ActivePolicyAsync(user, holder, Start);
        var g2 = await w.ActivePolicyAsync(user, holder, Start);
        var rG = await w.ReceiptAsync(holder, Received, 365m);
        var both = await Task.WhenAll(Allocate(user, g1.Id, rG), Allocate(user, g2.Id, rG));
        Assert.Equal(["allocated", "nothing-left"], both.Select(b => World.Param(b, "advance")).Order());
        Assert.Equal([0m, 365m], new[] { await w.OwedAsync(g1.Id), await w.OwedAsync(g2.Id) }.Order());
        Assert.Equal(0m, await w.AvailableAsync(rG));

        var holderH = await w.ClientAsync();
        var h = await w.ActivePolicyAsync(user, holderH, Start);
        var rH = await w.ReceiptAsync(holderH, Received, 91.25m);
        var twice = await Task.WhenAll(Allocate(user, h.Id, rH), Allocate(user, h.Id, rH));
        Assert.Equal(["allocated", "nothing-left"], twice.Select(b => World.Param(b, "advance")).Order());
        Assert.Equal(273.75m, await w.OwedAsync(h.Id));
    }

    [Fact]
    public async Task X_a_usd_policy_takes_a_gel_receipt_at_the_receipt_days_rate()
    {
        var user = await w.UserAsync();
        await w.RateAsync("USD", Start, 2.7m);
        await w.RateAsync("USD", Received, 2.75m);
        var holder = await w.ClientAsync();
        var pol = await w.ActivePolicyAsync(user, holder, Start, "USD", 2.7m);
        var gel = Math.Round(91.25m * 2.75m, 2, MidpointRounding.AwayFromZero);
        var r = await w.ReceiptAsync(holder, Received, gel);
        Assert.Equal("allocated", World.Param(await Allocate(user, pol.Id, r), "advance"));
        var line = Assert.Single(await w.QueryAsync<(decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>("select amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @r", new { r }));
        Assert.Equal((91.25m, gel, 2.75m, World.Today), line);
        Assert.Equal((273.75m, 0m), (await w.OwedAsync(pol.Id), await w.AvailableAsync(r)));
    }

    [Fact]
    public async Task H_the_database_refuses_more_than_the_receipt_an_allocation_from_a_reversed_one_and_one_booked_in_a_closed_month()
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        var pol = await w.ActivePolicyAsync(user, holder, Start);
        var inst = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @p order by due_on", new { p = pol.Id });
        var guard = await w.ReceiptAsync(holder, Received, 50m);
        var reversed = await w.ReceiptAsync(holder, Received, 100m);
        await PaymentActions.ReversePaymentAsync(w.App, user, reversed.ToString(), pol.Id.ToString(), "x");
        async Task<string?> Insert(Guid payment, decimal amount, DateOnly? on = null) => DbError.From(await Record.ExceptionAsync(() => w.App.AsUserAsync(user.Id, tx =>
            tx.ExecuteAsync("insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@payment, @inst, @amount, @amount, 1, @today)",
                new { payment, inst, amount, today = on ?? World.Today }))))?.Code;
        Assert.Equal("PA002", await Insert(guard, 60m));
        Assert.Equal("PA001", await Insert(reversed, 10m));
        // An allocation booked in a closed month (the month before this class's policies, closed here; no row of this class is
        // booked in it): refused 23514 by the period guard, the month not reopened.
        var closed = new DateOnly(2025, 12, 1);
        await w.CloseMonthAsync(closed, user.Id);
        Assert.Equal("23514", await Insert(guard, 10m, closed.AddDays(14)));
        Assert.Equal("closed", await w.ScalarAsync<string>("select status from core.accounting_period where period_start = @closed", new { closed }));
        Assert.Equal((50m, 365m), (await w.AvailableAsync(guard), await w.OwedAsync(pol.Id)));
    }

    // ---------------------------------------------------------------- transfer of an overpayment

    /// <summary>
    /// A receipt R of <paramref name="amount"/> on 07.09 settling S's only instalment of the same amount, reduced by
    /// <paramref name="reduction"/> today; T owes <paramref name="owed"/> (the payment-shared-page worlds).
    /// </summary>
    private async Task<(CurrentUser User, Guid Holder, World.Policy S, World.Policy T, Guid R, Guid S1)> OverpaidAsync(decimal amount, decimal reduction, decimal owed = 100m, string tCurrency = "GEL")
    {
        var user = await w.UserAsync("accountant");
        var approver = await w.UserAsync();
        var holder = await w.ClientAsync();
        var received = new DateOnly(2026, 9, 7);
        var s = await w.PendingPolicyAsync(holder, new DateOnly(2026, 9, 1), premium: amount, schedule: [(0, amount)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, approver, s.Id.ToString())).Outcome);
        if (tCurrency != "GEL") await w.RateAsync(tCurrency, new DateOnly(2026, 10, 1), 2.7m);
        var t = await w.PendingPolicyAsync(holder, new DateOnly(2026, 10, 1), tCurrency, tCurrency == "GEL" ? 1m : 2.7m, premium: owed, schedule: [(0, owed)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, approver, t.Id.ToString())).Outcome);
        var r = await w.ReceiptAsync(holder, received, amount, user.Id, "R-0907");
        var s1 = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @p", new { p = s.Id });
        await w.App.AsUserAsync(user.Id, async tx =>
        {
            await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @s1, @amount, @amount, 1, @received)", new { r, s1, amount, received });
            await tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@s1, @today, @change, N'reduction')", new { s1, today = World.Today, change = -reduction });
            return 0;
        });
        return (user, holder, s, t, r, s1);
    }

    private Task<List<(Guid InstallmentId, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>> LinesAsync(Guid r) =>
        w.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly)>("select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @r order by created_at, amount desc", new { r });

    [Theory]
    [InlineData("90071992547408.01", "/policies/{T}?advance=transferred&gel=10000&left=9007199254730801&released=9007199254740801")]
    [InlineData("9999999999999999.99", "/policies/{T}?advance=transferred&gel=10000&left=999999999999989999&released=999999999999999999")]
    public async Task A_transfer_moves_the_largest_overpayments_exactly(string amountText, string expected)
    {
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var (user, _, _, t, r, s1) = await OverpaidAsync(amount, amount);
        Assert.Equal(expected.Replace("{T}", t.Id.ToString()), await Transfer(user, t.Id, r));
        var t1 = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @p", new { p = t.Id });
        var lines = await LinesAsync(r);
        Assert.Equal(3, lines.Count);
        Assert.Contains((s1, -amount, -amount, 1m, World.Today), lines);
        Assert.Contains((t1, 100m, 100m, 1m, World.Today), lines);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = (select payer_party_id from core.payment where id = @r)", new { r }));
        // Again: nothing left, nothing written.
        Assert.Equal($"/policies/{t.Id}?advance=nothing-left", await Transfer(user, t.Id, r));
        Assert.Equal(3, (await LinesAsync(r)).Count);
    }

    [Fact]
    public async Task A_usd_target_takes_the_released_gel_at_the_receipt_days_rate_the_last_line_taking_the_rounding()
    {
        // S's reduction leaves 135.02 GEL too much; T owes 20.00 + 100.00 USD. 13502 / 2.7 = 5000.74 → 5001 cents: 2000 to
        // T1 (54.00 GEL), 3001 to T2, which takes the rest, 81.02 GEL.
        var user = await w.UserAsync("accountant");
        var approver = await w.UserAsync();
        var holder = await w.ClientAsync();
        var received = new DateOnly(2026, 9, 7);
        await w.RateAsync("USD", received, 2.7m);
        await w.RateAsync("USD", new DateOnly(2026, 10, 1), 2.7m);
        var s = await w.PendingPolicyAsync(holder, new DateOnly(2026, 9, 1), premium: 150m, schedule: [(0, 150m)]);
        await PolicyActions.ApprovePolicyAsync(w.App, approver, s.Id.ToString());
        var t = await w.PendingPolicyAsync(holder, new DateOnly(2026, 10, 1), "USD", 2.7m, premium: 120m, schedule: [(0, 20m), (31, 100m)]);
        await PolicyActions.ApprovePolicyAsync(w.App, approver, t.Id.ToString());
        var r = await w.ReceiptAsync(holder, received, 150m);
        var s1 = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @p", new { p = s.Id });
        await w.App.AsUserAsync(user.Id, async tx =>
        {
            await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @s1, 150, 150, 1, @received)", new { r, s1, received });
            await tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@s1, @today, -135.02, N'reduction')", new { s1, today = World.Today });
            return 0;
        });
        Assert.Equal($"/policies/{t.Id}?advance=transferred&gel=13502&left=0&released=13502", await Transfer(user, t.Id, r));
        var lines = (await LinesAsync(r)).Skip(1).Select(l => (l.Amount, l.AmountGel, l.Rate)).ToList();
        Assert.Equal([(-135.02m, -135.02m, 1m), (20m, 54m, 2.7m), (30.01m, 81.02m, 2.7m)], lines.OrderBy(l => l.Amount).ToList());
    }

    [Fact]
    public async Task Two_sources_an_earlier_release_netted_off_and_the_rest_staying_on_the_receipt()
    {
        var user = await w.UserAsync("accountant");
        var approver = await w.UserAsync();
        var holder = await w.ClientAsync();
        var received = new DateOnly(2026, 9, 7);
        var s = await w.PendingPolicyAsync(holder, new DateOnly(2026, 9, 1), premium: 150m, schedule: [(0, 150m)]);
        var u = await w.PendingPolicyAsync(holder, new DateOnly(2026, 9, 1), premium: 80m, schedule: [(0, 80m)]);
        var t = await w.PendingPolicyAsync(holder, new DateOnly(2026, 10, 1), premium: 100m, schedule: [(0, 100m)]);
        foreach (var p in new[] { s, u, t }) await PolicyActions.ApprovePolicyAsync(w.App, approver, p.Id.ToString());
        var r = await w.ReceiptAsync(holder, received, 230m);
        Guid I(World.Policy p) => w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = p.Id }).Result;
        var (s1, u1, t1) = (I(s), I(u), I(t));
        var earlier = new DateOnly(2026, 10, 15);
        await w.App.AsUserAsync(user.Id, async tx =>
        {
            const string insert = "insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @i, @a, @a, 1, @on)";
            await tx.ExecuteAsync(insert, new { r, i = s1, a = 150m, on = received });
            await tx.ExecuteAsync(insert, new { r, i = u1, a = 80m, on = received });
            await tx.ExecuteAsync(insert, new { r, i = s1, a = -20m, on = earlier });
            await tx.ExecuteAsync(insert, new { r, i = t1, a = 20m, on = earlier });
            await tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@s1, @today, -50, N'r'), (@u1, @today, -80, N'r')", new { s1, u1, today = World.Today });
            return 0;
        });
        // Released 30.00 + 80.00 = 110.00; T1 owes 100.00 − 20.00 = 80.00; 30.00 left.
        Assert.Equal($"/policies/{t.Id}?advance=transferred&gel=8000&left=3000&released=11000", await Transfer(user, t.Id, r));
        var today = (await LinesAsync(r)).Where(l => l.BookedOn == World.Today).Select(l => (l.InstallmentId, l.Amount)).ToHashSet();
        Assert.Equal(new HashSet<(Guid, decimal)> { (s1, -30m), (u1, -80m), (t1, 80m) }, today);
    }

    [Fact]
    public async Task Nothing_owed_on_the_target_writes_nothing()
    {
        var (user, _, _, t, r, _) = await OverpaidAsync(150m, 150m);
        await PaymentActions.TransferOverpaymentAsync(w.App, w.Rates, user, t.Id.ToString(), r.ToString());
        var lines = (await LinesAsync(r)).Count;
        // T is now paid; the other excess is gone too: a second request finds nothing more to release.
        Assert.Equal($"/policies/{t.Id}?advance=nothing-left", await Transfer(user, t.Id, r));
        Assert.Equal(lines, (await LinesAsync(r)).Count);
        // A receipt of another client: not found.
        var other = await w.ReceiptAsync(await w.ClientAsync(), new DateOnly(2026, 9, 7), 10m);
        Assert.Equal($"/policies/{t.Id}?advance=not-found", await Transfer(user, t.Id, other));
    }
}
