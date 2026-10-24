using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests;

/// <summary>
/// H46 on a real SQL Server: a claim recorded in GEL on a USD policy (old program docs/07 §4.5 and §4.1, owner 06.10.2026:
/// "ზარალი აღირიცხება ლარში"; the limit and deductible "ზარალის თარიღით"). The old program never built it (its claims took the
/// policy's currency); the rewrite has the company setting claim.currency, "gel" when none is saved, "policy" for the old
/// behaviour. The world: a USD policy of the year with one coverage, 10 000.00 USD insured, 100.00 USD deductible, one instalment
/// of 100.00 USD due 01.01 and unpaid; the official USD rate 2.60 on the occurrence day 01.03 and 2.70 on the payment day 10.03.
/// </summary>
public sealed class ClaimGelOnForeignPolicyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2300;

    private sealed record World(CurrentUser Claims, CurrentUser Accountant, ClaimWorld.Policy Policy, Guid Claim, int Y)
    {
        public DateOnly Occurred => new(Y, 3, 1);
        public DateOnly Paid => new(Y, 3, 10);
    }

    private async Task<World> WorldAsync(decimal unpaidUsd = 100m, string reserve = "1000", bool paymentRate = true)
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        await w.RateAsync("USD", new DateOnly(y, 3, 1), 2.60m);
        if (paymentRate) await w.RateAsync("USD", new DateOnly(y, 3, 10), 2.70m);
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 1, 1), [new ClaimWorld.Line("damage", 10000m, 100m, unpaidUsd)],
            [(new DateOnly(y, 1, 1), unpaidUsd)], "USD", 2.5m);
        db.Clock.SetToday(new DateOnly(y, 3, 2));
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 3, 1), new DateOnly(y, 3, 2), ("damage", reserve)))[0];
        return new World(claims, await w.UserAsync("accountant"), policy, claim, y);
    }

    private async Task<(string Currency, decimal? LimitGel, decimal? DeductibleGel, decimal? Rate, DateOnly? On)> SnapshotAsync(Guid claim) =>
        Assert.Single(await w.RowsAsync<(string, decimal?, decimal?, decimal?, DateOnly?)>("select currency, limit_gel, deductible_gel, limit_rate, limit_rate_on from core.claim where id = @claim", new { claim }));

    private Task<List<(decimal Amount, decimal Deductible, decimal Offset, decimal Net, decimal NetGel, decimal Rate, decimal? OffsetRate, string? OffsetCurrency)>> PaymentsAsync(Guid claim) =>
        w.RowsAsync<(decimal, decimal, decimal, decimal, decimal, decimal, decimal?, string?)>("""
            select amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, offset_rate, offset_currency from core.claim_payment where claim_id = @claim order by created_at
            """, new { claim });

    private Task<List<(decimal Amount, decimal AmountGel, decimal Rate)>> SetOffLinesAsync(Guid claim) =>
        w.RowsAsync<(decimal, decimal, decimal)>("""
            select a.amount, a.amount_gel, a.rate from core.claim_payment cp join core.payment_allocation a on a.payment_id = cp.offset_payment_id
            where cp.claim_id = @claim and cp.kind = N'payment' order by a.created_at
            """, new { claim });

    private Task<decimal> UnpaidUsdAsync(ClaimWorld.Policy p) => w.ScalarAsync<decimal>("""
        select sum(i.amount) - coalesce((select sum(a.amount) from core.payment_allocation a join core.installment j on j.id = a.installment_id where j.policy_id = @id), 0)
        from core.installment i where i.policy_id = @id
        """, new { id = p.Id });

    private Task<FormOutcome> PayAsync(World x, string amount, bool offset = true) =>
        offset ? w.PayAsync(x.Accountant, x.Claim, amount, x.Paid, x.Paid, ("offset_premium", "on")) : w.PayAsync(x.Accountant, x.Claim, amount, x.Paid, x.Paid);

    private async Task ApproveAsync(World x, string amount)
    {
        db.Clock.SetToday(x.Occurred.AddDays(2));
        await w.ApproveAsync(x.Claims, x.Claim, amount, x.Occurred.AddDays(2));
        db.Clock.SetToday(x.Paid);
    }

    [Fact]
    public async Task Docs07_6_g_a_GEL_claim_of_1000_after_the_deductible_sets_off_100_USD_at_2_70_as_270_GEL_and_pays_730()
    {
        var x = await WorldAsync();
        // Registered in GEL, the limit and deductible at the occurrence day's 2.60: 26 000.00 and 260.00.
        Assert.Equal(("GEL", 26000m, 260m, 2.6m, x.Occurred), await SnapshotAsync(x.Claim));
        await ApproveAsync(x, "1260");
        Assert.Equal((1260m, 260m), Assert.Single(await w.RowsAsync<(decimal, decimal)>("select approved_amount, deductible_amount from core.claim where id = @c", new { c = x.Claim })));

        Assert.NotNull((await PayAsync(x, "1260")).RedirectTo);
        // The claim's money in GEL at 1; the set-off 100.00 USD × 2.70 = 270.00 GEL, its rate and currency kept on the payment.
        Assert.Equal([(1260m, 260m, 270m, 730m, 730m, 1m, (decimal?)2.7m, "USD")], (await PaymentsAsync(x.Claim)).Select(p => p with { OffsetCurrency = p.OffsetCurrency?.Trim() }));
        // The receipt records the GEL; its allocation is in USD, with its GEL and the rate; the USD debt is settled.
        Assert.Equal([(100m, 270m, 2.7m)], await SetOffLinesAsync(x.Claim));
        Assert.Equal(270m, await w.ScalarAsync<decimal>("select p.amount_gel from core.claim_payment cp join core.payment p on p.id = cp.offset_payment_id where cp.claim_id = @c", new { c = x.Claim }));
        Assert.Equal(0m, await UnpaidUsdAsync(x.Policy));
        // The month report: paid in GEL at rate 1 — the gross, not 1 260 × 2.70.
        var report = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, $"{x.Y}-03"));
        Assert.Equal(126000L, (long)report.ClaimsTotals.PaidGel);
    }

    [Fact]
    public async Task The_occurrence_days_snapshot_caps_the_approval_in_GEL_and_does_not_move_when_that_days_rate_is_stored_again()
    {
        var x = await WorldAsync();
        db.Clock.SetToday(x.Occurred.AddDays(2));
        // Above the limit in GEL (26 000.00): approved at the limit, as the old program caps an approval at the sum insured; the
        // deductible is the GEL one, 260.00 — not 10 000.00 USD or 100.00 as numbers in GEL.
        await w.ApproveAsync(x.Claims, x.Claim, "26000.01", x.Occurred.AddDays(2));
        Assert.Equal(("approved", 26000m, 260m), Assert.Single(await w.RowsAsync<(string, decimal, decimal)>("select status, approved_amount, deductible_amount from core.claim where id = @c", new { c = x.Claim })));
        // core.exchange_rate can be stored again; the claim keeps its own rate and amounts.
        await w.AdminAsync("update core.exchange_rate set rate = 3.10 where currency = N'USD' and rate_date = @d", new { d = x.Occurred });
        Assert.Equal(("GEL", 26000m, 260m, 2.6m, x.Occurred), await SnapshotAsync(x.Claim));
        // Neither the currency nor the snapshot changes afterwards: the snapshot's check refuses USD with a GEL rate, and [CL001]
        // the rest, the currency of a claim without a snapshot too.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("update core.claim set currency = N'USD' where id = @c", new { c = x.Claim }));
        Assert.Contains("claim_limit_snapshot", e.Message);
        e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("update core.claim set currency = N'USD', limit_gel = null, deductible_gel = null, limit_rate = null, limit_rate_on = null where id = @c", new { c = x.Claim }));
        Assert.Contains("CL001", e.Message);
        Assert.Equal(("GEL", 26000m, 260m, 2.6m, x.Occurred), await SnapshotAsync(x.Claim));
        e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("update core.claim set limit_rate = 2.9 where id = @c", new { c = x.Claim }));
        Assert.Contains("CL001", e.Message);
    }

    [Fact]
    public async Task Without_the_occurrence_days_rate_nothing_is_registered_and_no_number_is_taken()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 1, 1), [new ClaimWorld.Line("damage", 10000m, 100m, 100m)], [(new DateOnly(y, 1, 1), 100m)], "USD", 2.5m);
        db.Clock.SetToday(new DateOnly(y, 3, 2));
        var counter = await w.ScalarAsync<long>("select next_value from core.number_sequence where code = N'claim'");
        var asked = w.Network.Requests;
        var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, claims, ClaimWorld.Form(("policy_number", policy.Number), ("occurred_on", $"{y}-03-01"),
            ("reported_on", $"{y}-03-02"), ("description", "ავარია"), ($"line:{policy.LineOf("damage")}", "on"), ($"reserve:{policy.LineOf("damage")}", "1000")));
        Assert.Null(outcome.RedirectTo);
        Assert.Equal(ClaimTexts.Errors.Rate, outcome.Errors["occurred_on"]);
        // The official rate was asked of the bank (which the test never reaches), then the registration stopped.
        Assert.True(w.Network.Requests > asked);
        Assert.Equal((0, 0), Assert.Single(await w.RowsAsync<(int, int)>("""
            select (select count(*) from core.loss_event where policy_id = @id), (select count(*) from core.claim where policy_id = @id)
            """, new { id = policy.Id })));
        Assert.Equal(counter, await w.ScalarAsync<long>("select next_value from core.number_sequence where code = N'claim'"));
    }

    [Fact]
    public async Task The_set_off_is_rounded_down_to_the_cent_so_100_GEL_at_2_70_sets_off_37_03_USD_as_99_98_GEL_and_pays_0_02()
    {
        var x = await WorldAsync();
        await ApproveAsync(x, "360");
        Assert.NotNull((await PayAsync(x, "360")).RedirectTo);
        Assert.Equal([(360m, 260m, 99.98m, 0.02m, 0.02m, 1m, (decimal?)2.7m, "USD")], (await PaymentsAsync(x.Claim)).Select(p => p with { OffsetCurrency = p.OffsetCurrency?.Trim() }));
        Assert.Equal([(37.03m, 99.98m, 2.7m)], await SetOffLinesAsync(x.Claim));
        Assert.Equal(62.97m, await UnpaidUsdAsync(x.Policy));
    }

    [Fact]
    public async Task A_debt_smaller_than_the_claim_is_set_off_whole_50_USD_as_135_GEL_and_865_is_paid()
    {
        var x = await WorldAsync(unpaidUsd: 50m);
        await ApproveAsync(x, "1260");
        Assert.NotNull((await PayAsync(x, "1260")).RedirectTo);
        Assert.Equal([(1260m, 260m, 135m, 865m, 865m, 1m, (decimal?)2.7m, "USD")], (await PaymentsAsync(x.Claim)).Select(p => p with { OffsetCurrency = p.OffsetCurrency?.Trim() }));
        Assert.Equal(0m, await UnpaidUsdAsync(x.Policy));
    }

    [Fact]
    public async Task Without_the_payment_days_rate_a_set_off_is_refused_and_a_payment_without_one_needs_no_USD_rate()
    {
        var x = await WorldAsync(paymentRate: false);
        await ApproveAsync(x, "1260");
        var refused = await PayAsync(x, "1260");
        Assert.Null(refused.RedirectTo);
        Assert.Equal(ClaimTexts.Errors.Rate, refused.Errors["paid_on"]);
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId }));
        // Without a set-off the GEL claim is paid at 1: no USD rate is needed.
        Assert.NotNull((await PayAsync(x, "1260", offset: false)).RedirectTo);
        Assert.Equal([(1260m, 260m, 0m, 1000m, 1000m, 1m, (decimal?)null, (string?)null)], await PaymentsAsync(x.Claim));
    }

    [Fact]
    public async Task A_reversal_keeps_the_set_offs_rate_and_currency_gives_the_USD_back_and_the_database_refuses_another_rate()
    {
        var x = await WorldAsync();
        await ApproveAsync(x, "1260");
        Assert.NotNull((await PayAsync(x, "1260")).RedirectTo);
        var payment = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @c", new { c = x.Claim });
        Assert.Equal($"/claims/{x.Claim}?reversal=ok", await ClaimActions.ReverseClaimPaymentAsync(w.App, x.Accountant, ClaimWorld.Form(("claim_id", x.Claim.ToString()),
            ("claim_payment_id", payment.ToString()), ("reason", "correction"), ("reversed_on", ClaimWorld.D(x.Paid))), x.Paid));
        var rows = (await PaymentsAsync(x.Claim)).Select(p => p with { OffsetCurrency = p.OffsetCurrency?.Trim() }).ToList();
        Assert.Equal([(1260m, 260m, 270m, 730m, 730m, 1m, (decimal?)2.7m, "USD"), (-1260m, -260m, -270m, -730m, -730m, 1m, (decimal?)2.7m, "USD")], rows);
        Assert.Equal(100m, await UnpaidUsdAsync(x.Policy));
        // [CP002] by hand: a reversal at another set-off rate is refused by the database.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("""
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
                                       kind, reverses_claim_payment_id, reversal_reason, returned_on, offset_rate, offset_currency, created_by)
            select claim_id, paid_on, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate, payee_kind, payee_party_id,
                   N'reversal', id, N'x', paid_on, 2.8, offset_currency, created_by from core.claim_payment where id = @payment
            """, new { payment }));
        Assert.Contains("CP002", e.Message);
    }

    [Fact]
    public async Task A_total_loss_across_currencies_is_refused_until_its_rule_is_given_nothing_written()
    {
        var x = await WorldAsync();
        db.Clock.SetToday(x.Occurred.AddDays(2));
        await w.ApproveAsync(x.Claims, x.Claim, "1260", x.Occurred.AddDays(2), totalLoss: true);
        db.Clock.SetToday(x.Paid);
        var refused = await w.PayAsync(x.Accountant, x.Claim, "1260", x.Paid, x.Paid, ("total_loss_mode", "all_unpaid"));
        Assert.Null(refused.RedirectTo);
        Assert.Equal(ClaimTexts.Errors.TotalLossCurrency, refused.Message);
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.Equal("active", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = x.Policy.Id }));
    }

    [Fact]
    public async Task With_the_setting_policy_a_new_claim_keeps_the_old_programs_currency_USD_and_no_snapshot()
    {
        await w.OldClaimsAsync();
        try
        {
            var x = await WorldAsync();
            Assert.Equal(("USD", (decimal?)null, (decimal?)null, (decimal?)null, (DateOnly?)null), await SnapshotAsync(x.Claim));
        }
        finally
        {
            await w.ClaimCurrencyAsync("gel");
        }
    }
    /// <summary>
    /// Codex R005-02 on the real action: 12 unpaid instalments of 0.01 USD, a GEL claim of 1 000.00 after the deductible, paid with the
    /// set-off at 2.70. The set-off is 0.12 USD = 0.32 GEL, written as twelve allocations one by one under the receipt's guard (PA002):
    /// each 0.01 USD with a GEL share of 0.02 or 0.03, never negative; 999.68 is paid. Its reversal gives the 0.12 USD back.
    /// </summary>
    [Fact]
    public async Task A_set_off_over_twelve_instalments_of_a_cent_is_written_share_by_share_and_its_reversal_gives_them_back()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        await w.RateAsync("USD", new DateOnly(y, 3, 1), 2.60m);
        await w.RateAsync("USD", new DateOnly(y, 3, 10), 2.70m);
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 1, 1), [new ClaimWorld.Line("damage", 10000m, 100m, 0.12m)],
            Enumerable.Range(0, 12).Select(m => (new DateOnly(y, 1, 1).AddMonths(m), 0.01m)).ToArray(), "USD", 2.5m);
        db.Clock.SetToday(new DateOnly(y, 3, 2));
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 3, 1), new DateOnly(y, 3, 2), ("damage", "1000")))[0];
        var x = new World(claims, await w.UserAsync("accountant"), policy, claim, y);
        await ApproveAsync(x, "1260");

        var paid = await PayAsync(x, "1260");
        Assert.True(paid.RedirectTo is not null, $"{paid.Message} {string.Join("; ", paid.Errors.Select(e => $"{e.Key}={e.Value}"))}");
        Assert.Equal([(1260m, 260m, 0.32m, 999.68m, 999.68m, 1m, (decimal?)2.7m, "USD")], (await PaymentsAsync(x.Claim)).Select(p => p with { OffsetCurrency = p.OffsetCurrency?.Trim() }));
        var lines = await w.RowsAsync<(decimal Amount, decimal AmountGel)>("""
            select a.amount, a.amount_gel from core.claim_payment cp join core.payment_allocation a on a.payment_id = cp.offset_payment_id
            join core.installment i on i.id = a.installment_id where cp.claim_id = @c and cp.kind = N'payment' order by i.due_on
            """, new { c = x.Claim });
        Assert.Equal(Enumerable.Repeat(0.01m, 12), lines.Select(l => l.Amount));
        Assert.Equal([0.02m, 0.03m, 0.03m, 0.02m, 0.03m, 0.03m, 0.02m, 0.03m, 0.03m, 0.03m, 0.02m, 0.03m], lines.Select(l => l.AmountGel));
        Assert.Equal(0m, await UnpaidUsdAsync(x.Policy));

        var payment = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @c", new { c = x.Claim });
        Assert.Equal($"/claims/{x.Claim}?reversal=ok", await ClaimActions.ReverseClaimPaymentAsync(w.App, x.Accountant, ClaimWorld.Form(("claim_id", x.Claim.ToString()),
            ("claim_payment_id", payment.ToString()), ("reason", "correction"), ("reversed_on", ClaimWorld.D(x.Paid))), x.Paid));
        Assert.Equal(0.12m, await UnpaidUsdAsync(x.Policy));
        // The receipt's mirror gives back each allocation exactly: nothing of the set-off is left in either currency.
        Assert.Equal((0m, 0m), Assert.Single(await w.RowsAsync<(decimal, decimal)>("""
            select coalesce(sum(a.amount), 0), coalesce(sum(a.amount_gel), 0) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id
            """, new { id = x.Policy.Id })));
    }
    /// <summary>
    /// Codex R005-03 as the application role (core_app, which may update core.claim): the occurrence day's snapshot of a GEL claim on a
    /// USD policy cannot be wiped with its currency kept — all four fields to NULL satisfy the snapshot's check (its all-null branch),
    /// so only a NULL-safe trigger catches it — nor one field to NULL, nor be given to a claim that had none (a GEL policy's).
    /// </summary>
    [Fact]
    public async Task The_snapshot_is_neither_wiped_nor_given_nor_nulled_field_by_field_by_the_application_role_its_currency_kept()
    {
        var x = await WorldAsync();
        Assert.Equal(("GEL", 26000m, 260m, 2.6m, x.Occurred), await SnapshotAsync(x.Claim));
        foreach (var set in new[]
        {
            "limit_gel = null, deductible_gel = null, limit_rate = null, limit_rate_on = null",
            "limit_gel = null", "deductible_gel = null",
        })
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync($"update core.claim set {set} where id = @c", new { c = x.Claim }));
            Assert.Contains("CL001", e.Message);
            Assert.Equal(("GEL", 26000m, 260m, 2.6m, x.Occurred), await SnapshotAsync(x.Claim));
        }
        // A GEL policy's claim has no snapshot; none can be given to it afterwards (NULL to a value).
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var gel = await w.PolicyAsync(claims.Id, new DateOnly(y, 1, 1), [new ClaimWorld.Line("damage", 10000m, 100m, 100m)], [(new DateOnly(y, 1, 1), 100m)]);
        db.Clock.SetToday(new DateOnly(y, 3, 2));
        var plain = (await w.RegisterAsync(claims, gel, new DateOnly(y, 3, 1), new DateOnly(y, 3, 2), ("damage", "1000")))[0];
        Assert.Equal(("GEL", (decimal?)null, (decimal?)null, (decimal?)null, (DateOnly?)null), await SnapshotAsync(plain));
        var given = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync(
            "update core.claim set limit_gel = 1, deductible_gel = 0, limit_rate = 1, limit_rate_on = occurred_on where id = @c", new { c = plain }));
        Assert.Contains("CL001", given.Message);
        Assert.Equal(("GEL", (decimal?)null, (decimal?)null, (decimal?)null, (DateOnly?)null), await SnapshotAsync(plain));
        // What the claim's own actions change still goes through (the guard looks only at the currency and the snapshot).
        await w.ExecAsync("update core.claim set description = N'განახლებული აღწერა' where id = @c", new { c = x.Claim });
    }

    /// <summary>
    /// Codex R006-01 as the application role: a reversal written by hand, with everything else the guard asks for in place (the
    /// set-off receipt reversed by the program's own MirrorReceiptAsync on the same day, the exact opposite amounts), cannot drop or
    /// change the set-off's rate or currency, nor the payee: [CP002] for NULL/NULL, one NULL, another rate, another currency and
    /// another payee — "not (a = b or both null)" was UNKNOWN for a NULL against a value, so 0012 let the first ones through. The
    /// exact mirror, the program's own reversal, still goes through.
    /// </summary>
    [Fact]
    public async Task R006_01_a_reversal_cannot_drop_or_change_the_set_offs_rate_currency_or_payee_only_the_exact_mirror_goes_through()
    {
        var x = await WorldAsync();
        await ApproveAsync(x, "1260");
        Assert.NotNull((await PayAsync(x, "1260")).RedirectTo);
        var payment = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @c", new { c = x.Claim });
        var receipt = await w.ScalarAsync<Guid>("select offset_payment_id from core.claim_payment where id = @payment", new { payment });
        foreach (var (offsetRate, offsetCurrency, payee) in new[]
        {
            ("null", "null", "payee_party_id"), ("null", "offset_currency", "payee_party_id"), ("offset_rate", "null", "payee_party_id"),
            ("2.8", "offset_currency", "payee_party_id"), ("offset_rate", "N'EUR'", "payee_party_id"),
            ("offset_rate", "offset_currency", "case when payee_party_id is null then @party else null end"),
        })
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => w.App.AsUserAsync(x.Accountant.Id, async tx =>
            {
                var r = (await tx.QuerySingleOrDefaultAsync<(decimal AmountGel, string Method, Guid? Payer)?>("select amount_gel, method, payer_party_id from core.payment where id = @receipt", new { receipt }))!.Value;
                var back = await ReceiptReversal.MirrorReceiptAsync(tx, new ReceiptReversal.ReceiptToReverse(receipt, r.AmountGel, r.Method, r.Payer), x.Paid, "R006-01", x.Accountant.Id);
                return await tx.ExecuteAsync($"""
                    insert into core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
                                                    offset_payment_id, kind, reverses_claim_payment_id, reversal_reason, offset_rate, offset_currency, created_by)
                    select claim_id, paid_on, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate, payee_kind, {payee},
                           @back, N'reversal', id, N'correction', {offsetRate}, {offsetCurrency}, created_by from core.claim_payment where id = @payment
                    """, new { payment, back, party = x.Policy.PartyId });
            }));
            Assert.Contains("CP002", e.Message);
            Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @c", new { c = x.Claim }));
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @receipt", new { receipt }));
        }
        // The exact mirror: the program's own reversal.
        Assert.Equal($"/claims/{x.Claim}?reversal=ok", await ClaimActions.ReverseClaimPaymentAsync(w.App, x.Accountant, ClaimWorld.Form(("claim_id", x.Claim.ToString()),
            ("claim_payment_id", payment.ToString()), ("reason", "correction"), ("reversed_on", ClaimWorld.D(x.Paid))), x.Paid));
        var rows = (await PaymentsAsync(x.Claim)).Select(p => (p.OffsetRate, p.OffsetCurrency?.Trim())).ToList();
        Assert.Equal([((decimal?)2.7m, "USD"), ((decimal?)2.7m, "USD")], rows);
    }

    /// <summary>
    /// Codex R006-01: a CHECK refuses only FALSE, so 0012's checks let a partial row in where one of their comparisons was UNKNOWN.
    /// Now refused as the application role: a set-off currency without its rate on a payment request, and a claim whose snapshot
    /// has its rate without the rate's day, or the day without the rate. The all-null request (no set-off) and a whole snapshot still go in.
    /// </summary>
    [Fact]
    public async Task R006_01_a_partial_set_off_or_snapshot_is_refused_by_the_checks_and_the_all_null_rows_still_go_in()
    {
        var x = await WorldAsync();
        await ApproveAsync(x, "1260");
        const string request = """
            insert into core.claim_payment_request (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                    payee_kind, payee_party_id, offset_premium, total_loss_mode, offset_rate, offset_currency, created_by)
            values (@claim, @paid, 1260, 260, {0}, {1}, {1}, 1, N'policyholder', @holder, {2}, N'none', {3}, {4}, @by)
            """;
        var args = new { claim = x.Claim, paid = x.Paid, by = x.Accountant.Id, holder = x.Policy.PartyId };
        foreach (var (rate, currency) in new[] { ("null", "N'USD'"), ("null", "N'GEL'") })
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync(string.Format(request, "270", "730", "1", rate, currency), args));
            Assert.Contains("claim_payment_request_offset_rate", e.Message);
        }
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment_request where claim_id = @c", new { c = x.Claim }));
        await w.ExecAsync(string.Format(request, "0", "1000", "0", "null", "null"), args);

        // A claim copied from this one, column by column, on an event of its own, with its snapshot made partial.
        var columns = string.Join(", ", await w.RowsAsync<string>("""
            select quotename(name) from sys.columns where object_id = object_id(N'core.claim') and is_computed = 0
              and name not in (N'id', N'number', N'loss_event_id', N'limit_rate', N'limit_rate_on')
            order by column_id
            """));
        var lossEvent = Guid.NewGuid();
        await w.ExecAsync("""
            insert into core.loss_event (id, number, policy_id, occurred_on, reported_on, description)
            select @lossEvent, number + N'-P', policy_id, occurred_on, reported_on, description from core.loss_event
            where id = (select loss_event_id from core.claim where id = @c)
            """, new { lossEvent, c = x.Claim });
        foreach (var (rate, on) in new[] { ("limit_rate", "null"), ("null", "limit_rate_on") })
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync($"""
                insert into core.claim (id, number, loss_event_id, limit_rate, limit_rate_on, {columns})
                select newid(), number + N'-P', @lossEvent, {rate}, {on}, {columns} from core.claim where id = @c
                """, new { c = x.Claim, lossEvent }));
            Assert.Contains("claim_limit_snapshot", e.Message);
        }
        // The whole snapshot copied goes in (the same rule, nothing else changed).
        await w.ExecAsync($"""
            insert into core.claim (id, number, loss_event_id, limit_rate, limit_rate_on, {columns})
            select newid(), number + N'-W', @lossEvent, limit_rate, limit_rate_on, {columns} from core.claim where id = @c
            """, new { c = x.Claim, lossEvent });
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim where number = (select number from core.claim where id = @c) + N'-P'", new { c = x.Claim }));
    }
}
