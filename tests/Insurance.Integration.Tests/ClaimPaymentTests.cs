using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests;

/// <summary>
/// Claim payments on the real SQL Server (legacy scripts/integration/claim-payment-permission.test.ts, scripts/loss-event-check.ts E–K,
/// scripts/claim-payee-regression.ts): who may pay, the deductible withheld once over partial payments and over the coverages of
/// one class in a loss event, the premium set-off, a USD claim at the payment day's rate, the third payee, closed months and a
/// failure after the money is written. Each test has its own year, so months closed by one do not touch another. The legacy
/// "today" 03.11 is passed to the actions; October is closed where the old test closed it.
/// </summary>
public sealed class ClaimPaymentTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2040;

    private sealed record World(CurrentUser Claims, ClaimWorld.Policy Policy, Guid Claim, DateOnly Today, int Year);

    private sealed record PaymentRow(DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel,
        decimal Rate, string PayeeKind, Guid? PayeePartyId, Guid? OffsetPaymentId, Guid? CreatedBy);

    private sealed record ReserveRow(DateOnly BookedOn, decimal Change, Guid? CreatedBy);

    /// <summary>
    /// The legacy world: an approved GEL claim of 1 000.00, the class's agreed deductible 100.00, reserve 1 000.00 booked 02.10,
    /// nothing paid; October closed, "today" 03.11.
    /// </summary>
    private async Task<World> WorldAsync(bool closeOctober = true, decimal deductible = 100m, string currency = "GEL", (DateOnly, decimal)[]? installments = null,
        decimal premium = 0m, decimal policyRate = 1m)
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 20000m, deductible, premium)], installments, currency, policyRate);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(y, 10, 2));
        if (closeOctober) await w.CloseMonthAsync(claims.Id, new DateOnly(y, 10, 1));
        return new World(claims, policy, claim, new DateOnly(y, 11, 3), y);
    }

    private Task<List<PaymentRow>> PaymentsAsync(Guid claim) => w.RowsAsync<PaymentRow>("""
        select paid_on as PaidOn, amount as Amount, deductible_withheld as DeductibleWithheld, premium_offset as PremiumOffset, net_paid as NetPaid,
               net_paid_gel as NetPaidGel, rate as Rate, payee_kind as PayeeKind, payee_party_id as PayeePartyId, offset_payment_id as OffsetPaymentId, created_by as CreatedBy
        from core.claim_payment where claim_id = @claim order by created_at
        """, new { claim });

    private Task<List<ReserveRow>> ReservesAsync(Guid claim) => w.RowsAsync<ReserveRow>(
        "select booked_on as BookedOn, change as Change, created_by as CreatedBy from core.claim_reserve where claim_id = @claim order by created_at", new { claim });

    private Task<string> StatusAsync(Guid claim) => w.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim });

    /// <summary>What the monthly report takes for these claims: paid in the month (gross) and the reserve at its end, in tetri.</summary>
    private async Task<(long Paid, long Rbns)> MonthAsync(DateOnly month, params Guid[] claims)
    {
        var from = new DateOnly(month.Year, month.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var paid = await w.ScalarAsync<decimal>("select coalesce(sum(amount), 0) from core.claim_payment where claim_id in @claims and paid_on between @from and @to", new { claims, from, to });
        var rbns = await w.ScalarAsync<decimal>("select coalesce(sum(change), 0) from core.claim_reserve where claim_id in @claims and booked_on <= @to", new { claims, to });
        return (Billing.Tetri(paid), Billing.Tetri(rbns));
    }

    private static PaymentRow Row(DateOnly on, decimal gross, decimal withheld, decimal offset, decimal net, decimal netGel, Guid payee, Guid by, string kind = "policyholder", decimal rate = 1m, Guid? receipt = null) =>
        new(on, gross, withheld, offset, net, netGel, rate, kind, payee, receipt, by);

    [Theory]
    [InlineData("accountant", true)]
    [InlineData("admin", true)]
    [InlineData("underwriter", false)]
    [InlineData("sales", false)]
    [InlineData("claims", false)]
    [InlineData("operations_head", false)]
    [InlineData("viewer", false)]
    public async Task The_roles_starting_set_pays_250_with_100_withheld_or_is_refused_in_Georgian_with_nothing_written(string role, bool may)
    {
        var x = await WorldAsync();
        var user = await w.UserAsync(role);
        var outcome = await w.PayAsync(user, x.Claim, "250.00", x.Today, x.Today, ("note", "ნაწილობრივი ანაზღაურება"));
        if (may)
        {
            Assert.Equal($"/claims/{x.Claim}?paid=1", outcome.RedirectTo);
            Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, x.Policy.PartyId, user.Id)], await PaymentsAsync(x.Claim));
            Assert.Equal([new ReserveRow(new DateOnly(x.Year, 10, 2), 1000m, x.Claims.Id), new ReserveRow(x.Today, -250m, user.Id)], await ReservesAsync(x.Claim));
            Assert.Equal("approved", await StatusAsync(x.Claim));
        }
        else
        {
            Assert.Null(outcome.RedirectTo);
            Assert.Equal(ClaimTexts.PaymentNotAllowed, outcome.Message);
            Assert.Empty(await PaymentsAsync(x.Claim));
            Assert.Single(await ReservesAsync(x.Claim));
        }
    }

    [Fact]
    public async Task A_group_gives_it_a_personal_removal_beats_the_group_and_a_viewer_allowed_personally_pays()
    {
        var x = await WorldAsync();
        var underwriter = await w.UserAsync("underwriter");
        var beka = await w.UserAsync("underwriter");
        await w.GroupAsync("claim.payment.record", underwriter.Id);
        Assert.Equal(ClaimTexts.PaymentNotAllowed, (await w.PayAsync(beka, x.Claim, "250.00", x.Today, x.Today)).Message);
        Assert.NotNull((await w.PayAsync(underwriter, x.Claim, "250.00", x.Today, x.Today)).RedirectTo);
        await w.GrantAsync(underwriter.Id, "claim.payment.record", "deny");
        Assert.Equal(ClaimTexts.PaymentNotAllowed, (await w.PayAsync(underwriter, x.Claim, "250.00", x.Today, x.Today)).Message);
        Assert.Single(await PaymentsAsync(x.Claim));

        var viewer = await w.UserAsync("viewer");
        await w.GrantAsync(viewer.Id, "claim.recovery.receive", "allow");
        Assert.Equal(ClaimTexts.PaymentNotAllowed, (await w.PayAsync(viewer, x.Claim, "250.00", x.Today, x.Today)).Message);
        await w.GrantAsync(viewer.Id, "claim.payment.record", "allow");
        Assert.NotNull((await w.PayAsync(viewer, x.Claim, "250.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal(2, (await PaymentsAsync(x.Claim)).Count);
    }

    [Fact]
    public async Task A_forged_form_naming_a_role_or_another_actor_gives_nothing()
    {
        var x = await WorldAsync();
        var underwriter = await w.UserAsync("underwriter");
        var accountant = await w.UserAsync("accountant");
        var outcome = await w.PayAsync(underwriter, x.Claim, "250.00", x.Today, x.Today, ("role", "accountant"), ("role_code", "admin"), ("allowed", "true"),
            ("permission", "claim.payment.record"), ("actor", accountant.Id.ToString()), ("created_by", accountant.Id.ToString()));
        Assert.Equal(ClaimTexts.PaymentNotAllowed, outcome.Message);
        Assert.Empty(await PaymentsAsync(x.Claim));
    }

    [Fact]
    public async Task Two_payments_of_250_withhold_100_once_October_stays_and_November_moves()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        var october = new DateOnly(x.Year, 10, 1);
        var november = new DateOnly(x.Year, 11, 1);
        Assert.Equal((0, 100000), await MonthAsync(october, x.Claim));
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal((0, 100000), await MonthAsync(october, x.Claim));
        Assert.Equal((25000, 75000), await MonthAsync(november, x.Claim));
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("note", "მეორე ნაწილი"))).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, x.Policy.PartyId, a.Id), Row(x.Today, 250m, 0m, 0m, 250m, 250m, x.Policy.PartyId, a.Id)], await PaymentsAsync(x.Claim));
        Assert.Equal(500m, (await ReservesAsync(x.Claim)).Sum(r => r.Change));
        Assert.Equal("approved", await StatusAsync(x.Claim));
        Assert.Equal((0, 100000), await MonthAsync(october, x.Claim));
        Assert.Equal((50000, 50000), await MonthAsync(november, x.Claim));
    }

    [Theory]
    [InlineData("claim_reserve")]
    [InlineData("claim")]
    public async Task The_final_payment_failing_after_its_money_is_written_leaves_nothing_and_the_retry_pays_750_once(string failingTable)
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today)).RedirectTo);
        var next = x.Today.AddDays(1);
        await w.ArmFaultAsync(failingTable);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => w.PayAsync(a, x.Claim, "750.00", next, next));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await w.DisarmAsync();
        }
        Assert.Single(await PaymentsAsync(x.Claim));
        Assert.Equal(2, (await ReservesAsync(x.Claim)).Count);
        Assert.Equal("approved", await StatusAsync(x.Claim));
        Assert.Equal($"/claims/{x.Claim}?paid=1", (await w.PayAsync(a, x.Claim, "750.00", next, next)).RedirectTo);
        var rows = await PaymentsAsync(x.Claim);
        Assert.Equal(Row(next, 750m, 0m, 0m, 750m, 750m, x.Policy.PartyId, a.Id), rows[1]);
        Assert.Equal((1000m, 100m, 900m), (rows.Sum(r => r.Amount), rows.Sum(r => r.DeductibleWithheld), rows.Sum(r => r.NetPaid)));
        Assert.Equal(0m, (await ReservesAsync(x.Claim)).Sum(r => r.Change));
        Assert.Equal("paid", await StatusAsync(x.Claim));
    }

    [Fact]
    public async Task A_third_payee_comes_from_the_register_and_is_refused_when_sanctioned_suspended_or_unknown()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        var serviceTax = Fixtures.RandomTaxId();
        var service = await w.PartyAsync(serviceTax, "სერვისცენტრი");
        (string, string)[] To(string id) => [("payee_kind", "workshop"), ("payee_id_number", id), ("note", "სერვისცენტრს")];
        async Task<FormOutcome> Pay(params (string, string)[] more) =>
            await ClaimActions.PayClaimAsync(w.App, w.Rates, a, ClaimWorld.Form([("claim_id", x.Claim.ToString()), ("amount", "250.00"), ("paid_on", ClaimWorld.D(x.Today)), .. more]), x.Today);

        // K1, K2: no ID number, or one not in the register: refused on the field, nothing written.
        Assert.Equal(ClaimTexts.Errors.Payee, (await Pay(("payee_kind", "workshop"))).Errors["payee_id_number"]);
        Assert.Equal(ClaimTexts.Errors.Payee, (await Pay(To("99999999999"))).Errors["payee_id_number"]);
        Assert.Equal(ClaimTexts.Errors.State, (await Pay(("payee_kind", "nobody"))).Errors["payee_kind"]);
        // The service sanctioned, or the policyholder sanctioned with a clean service: refused.
        await w.ExecAsync("update core.party set sanctions_blocked = 1 where id = @service", new { service });
        Assert.Equal(ClaimTexts.Errors.PayeeBlocked, (await Pay(To(serviceTax))).Message);
        await w.ExecAsync("update core.party set sanctions_blocked = 0 where id = @service; update core.party set sanctions_blocked = 1 where id = @client", new { service, client = x.Policy.PartyId });
        Assert.Equal(ClaimTexts.Errors.PayeeBlocked, (await Pay(To(serviceTax))).Message);
        Assert.Equal(ClaimTexts.Errors.PayeeBlocked, (await Pay(("payee_kind", "policyholder"))).Message);
        await w.ExecAsync("update core.party set sanctions_blocked = 0 where id = @client", new { client = x.Policy.PartyId });
        // An FMS suspension in force on the service: refused; on another party: no effect.
        var suspension = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at, note)
            values (@suspension, @service, N'written', N'all', dateadd(day, -1, sysdatetimeoffset()), dateadd(day, 5, sysdatetimeoffset()), N'ტესტი')
            """, new { suspension, service });
        Assert.Equal(ClaimTexts.Errors.PayeeBlocked, (await Pay(To(serviceTax))).Message);
        Assert.Empty(await PaymentsAsync(x.Claim));
        await w.ExecAsync("update core.fms_suspension set lifted_at = sysdatetimeoffset(), lift_reason = N'ტესტი' where id = @suspension", new { suspension });
        // K3: clean: the payment names the service.
        Assert.Equal($"/claims/{x.Claim}?paid=1", (await Pay(To(serviceTax))).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, service, a.Id, "workshop")], await PaymentsAsync(x.Claim));
        Assert.Equal((25000, 75000), await MonthAsync(new DateOnly(x.Year, 11, 1), x.Claim));
    }

    [Fact]
    public async Task A_suspension_on_the_policyholder_stops_the_payment_with_the_AML_message()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        await w.ExecAsync("""
            insert core.fms_suspension (party_id, kind, scope, instructed_at, ends_at, note)
            values (@client, N'written', N'all', dateadd(day, -1, sysdatetimeoffset()), dateadd(day, 5, sysdatetimeoffset()), N'ტესტი')
            """, new { client = x.Policy.PartyId });
        Assert.Equal(AmlTexts.Suspension.Blocked, (await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today)).Message);
        Assert.Empty(await PaymentsAsync(x.Claim));
    }

    [Fact]
    public async Task With_the_premium_set_off_250_withholds_100_sets_off_90_through_an_internal_receipt_and_pays_60()
    {
        var y = year + 1;
        var x = await WorldAsync(installments: [(new DateOnly(y, 10, 1), 90m)], premium: 90m);
        var a = await w.UserAsync("accountant");
        await w.GrantAsync(a.Id, "premium.receipt.create", "deny");
        Assert.Equal($"/claims/{x.Claim}?paid=1", (await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("offset_premium", "on"), ("note", "ჩათვლით"))).RedirectTo);
        var receipt = (await w.RowsAsync<(Guid Id, DateOnly On, decimal Gel, string Method, string Kind, Guid Payer, Guid By)>(
            "select id, received_on, amount_gel, method, kind, payer_party_id, created_by from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId })).Single();
        Assert.Equal((x.Today, 90m, "claim_offset", "receipt", x.Policy.PartyId, a.Id), (receipt.On, receipt.Gel, receipt.Method, receipt.Kind, receipt.Payer, receipt.By));
        Assert.Equal([(x.Policy.Installments[0], 90m, 90m, 1m, x.Today)], await w.RowsAsync<(Guid, decimal, decimal, decimal, DateOnly)>(
            "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id", new { id = receipt.Id }));
        Assert.Equal([Row(x.Today, 250m, 100m, 90m, 60m, 60m, x.Policy.PartyId, a.Id, receipt: receipt.Id)], await PaymentsAsync(x.Claim));
        Assert.Equal("ჩათვლით", await w.ScalarAsync<string>("select reference from core.payment where id = @id", new { id = receipt.Id }));
    }

    [Fact]
    public async Task The_claim_payment_failing_after_the_set_off_receipt_and_its_allocation_leaves_none_of_the_three()
    {
        var y = year + 1;
        var x = await WorldAsync(installments: [(new DateOnly(y, 10, 1), 90m)], premium: 90m);
        var a = await w.UserAsync("accountant");
        await w.ArmFaultAsync("claim_payment");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("offset_premium", "on")));
        }
        finally
        {
            await w.DisarmAsync();
        }
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId }));
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("offset_premium", "on"))).RedirectTo);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId }));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment_allocation a join core.payment p on p.id = a.payment_id where p.payer_party_id = @p", new { p = x.Policy.PartyId }));
    }

    [Fact]
    public async Task An_old_USD_claim_with_the_set_off_pays_net_60_USD_as_162_GEL_at_the_payment_days_rate()
    {
        var y = year + 1;
        await w.RateAsync("USD", new DateOnly(y, 10, 31), 2.6m);
        await w.RateAsync("USD", new DateOnly(y, 11, 3), 2.7m);
        await w.RateAsync("USD", new DateOnly(y, 11, 30), 2.8m);
        var x = await WorldAsync(currency: "USD", installments: [(new DateOnly(y, 10, 1), 90m)], premium: 90m, policyRate: 2.5m);
        var a = await w.UserAsync("accountant");
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("offset_premium", "on"))).RedirectTo);
        var receipt = await w.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId });
        Assert.Equal(243m, await w.ScalarAsync<decimal>("select amount_gel from core.payment where id = @receipt", new { receipt }));
        Assert.Equal([(90m, 243m, 2.7m)], await w.RowsAsync<(decimal, decimal, decimal)>("select amount, amount_gel, rate from core.payment_allocation where payment_id = @receipt", new { receipt }));
        Assert.Equal([Row(x.Today, 250m, 100m, 90m, 60m, 162m, x.Policy.PartyId, a.Id, rate: 2.7m, receipt: receipt)], await PaymentsAsync(x.Claim));
        Assert.Equal(0, w.Network.Requests);
        // A later rate changes nothing stored.
        await w.RateAsync("USD", new DateOnly(y, 12, 3), 3.1m);
        Assert.Equal(162m, (await PaymentsAsync(x.Claim))[0].NetPaidGel);
    }

    [Fact]
    public async Task A_USD_claim_without_the_payment_days_rate_is_refused_on_the_date_after_the_bank_is_asked()
    {
        var x = await WorldAsync(currency: "USD", policyRate: 2.5m);
        var a = await w.UserAsync("accountant");
        var outcome = await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today);
        Assert.Equal(ClaimTexts.Errors.Rate, outcome.Errors["paid_on"]);
        Assert.True(w.Network.Requests > 0);
        Assert.Empty(await PaymentsAsync(x.Claim));
    }

    [Fact]
    public async Task A_payment_in_a_closed_month_is_refused_twice_then_paid_in_the_open_month_with_the_rest_of_the_deductible()
    {
        var x = await WorldAsync(closeOctober: false);
        var a = await w.UserAsync("accountant");
        var oct10 = new DateOnly(x.Year, 10, 10);
        Assert.NotNull((await w.PayAsync(a, x.Claim, "50.00", oct10, oct10)).RedirectTo);
        await w.CloseMonthAsync(a.Id, oct10);
        var viewer = await w.UserAsync("viewer");
        await w.GrantAsync(viewer.Id, "claim.payment.record", "allow");
        foreach (var who in new[] { a, viewer })
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var refused = await w.PayAsync(who, x.Claim, "250.00", new DateOnly(x.Year, 10, 31), x.Today, ("note", "დახურულ თვეში"));
                Assert.Equal(ClaimTexts.Errors.PeriodClosed, refused.Errors["paid_on"]);
                Assert.Single(await PaymentsAsync(x.Claim));
            }
        var nov1 = new DateOnly(x.Year, 11, 1);
        Assert.NotNull((await w.PayAsync(viewer, x.Claim, "250.00", nov1, x.Today)).RedirectTo);
        Assert.Equal([Row(oct10, 50m, 50m, 0m, 0m, 0m, x.Policy.PartyId, a.Id), Row(nov1, 250m, 50m, 0m, 200m, 200m, x.Policy.PartyId, viewer.Id)], await PaymentsAsync(x.Claim));
        Assert.Equal(700m, (await ReservesAsync(x.Claim)).Sum(r => r.Change));
        Assert.Equal((5000, 95000), await MonthAsync(oct10, x.Claim));
        Assert.Equal((25000, 70000), await MonthAsync(nov1, x.Claim));
    }

    [Fact]
    public async Task Form_checks_a_future_day_no_amount_more_than_approved_and_a_claim_not_approved()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        Assert.Equal(ClaimTexts.Errors.Date, (await w.PayAsync(a, x.Claim, "250.00", x.Today.AddDays(1), x.Today)).Errors["paid_on"]);
        Assert.Equal(ClaimTexts.Errors.Amount, (await w.PayAsync(a, x.Claim, "0", x.Today, x.Today)).Errors["amount"]);
        Assert.Equal(ClaimTexts.Errors.Amount, (await w.PayAsync(a, x.Claim, "0.004", x.Today, x.Today)).Errors["amount"]);
        Assert.Equal(ClaimTexts.Errors.Exceeds, (await w.PayAsync(a, x.Claim, "1000.01", x.Today, x.Today)).Errors["amount"]);
        Assert.Equal(ClaimTexts.Errors.State, (await ClaimActions.PayClaimAsync(w.App, w.Rates, a, ClaimWorld.Form(("claim_id", "x")), x.Today)).Message);
        var other = (await w.RegisterAsync(x.Claims, x.Policy, new DateOnly(x.Year, 11, 1), x.Today, ("damage", "100")))[0];
        Assert.Equal(ClaimTexts.Errors.State, (await w.PayAsync(a, other, "10", x.Today, x.Today)).Message);
        // "250,50" is read as 250.50 (the first comma).
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250,50", x.Today, x.Today)).RedirectTo);
        Assert.Equal(250.50m, (await PaymentsAsync(x.Claim))[0].Amount);
        Assert.Empty(await PaymentsAsync(other));
    }

    // ---- one deductible for a class in a loss event (claim-payment-permission.test.ts; loss-event-check.ts E, G, H, J, K)

    private async Task<(World X, IReadOnlyList<Guid> Claims, CurrentUser A)> EventAsync(decimal d1, decimal d2, decimal d3 = 50m, (string, string)[]? parts = null)
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [
            new("damage", 20000m, d1), new("equipment", 20000m, d2), new("theft", 20000m, d1), new("assistance", 1000m, d3)]);
        var ids = await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), parts ?? [("damage", "2000"), ("equipment", "500"), ("assistance", "150")]);
        return (new World(claims, policy, ids[0], new DateOnly(y, 11, 3), y), ids, await w.UserAsync("accountant"));
    }

    [Fact]
    public async Task One_class_deductible_in_an_event_2000_withholds_200_500_none_150_its_own_50_net_2450()
    {
        var (x, c, a) = await EventAsync(200m, 200m);
        Assert.Equal(3, c.Count);
        var numbers = await w.RowsAsync<string>("select number from core.claim where id in @c order by created_at", new { c });
        var ev = numbers[0];
        Assert.Equal([ev, $"{ev}-2", $"{ev}-3"], numbers);
        foreach (var (claim, amount) in c.Zip(new[] { "2000", "500", "150" })) await w.ApproveAsync(x.Claims, claim, amount, x.Today);
        foreach (var (claim, amount) in c.Zip(new[] { "2000.00", "500.00", "150.00" })) Assert.NotNull((await w.PayAsync(a, claim, amount, x.Today, x.Today)).RedirectTo);
        Assert.Equal(Row(x.Today, 2000m, 200m, 0m, 1800m, 1800m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[0])).Single());
        Assert.Equal(Row(x.Today, 500m, 0m, 0m, 500m, 500m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[1])).Single());
        Assert.Equal(Row(x.Today, 150m, 50m, 0m, 100m, 100m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[2])).Single());
        foreach (var claim in c) Assert.Equal("paid", await StatusAsync(claim));
        Assert.Equal(0m, await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id in @c", new { c }));
    }

    [Fact]
    public async Task Different_deductibles_of_one_class_in_an_event_refuse_both_payments()
    {
        var (x, c, a) = await EventAsync(200m, 50m, parts: [("damage", "1000"), ("equipment", "1000")]);
        foreach (var claim in c) await w.ApproveAsync(x.Claims, claim, "1000", x.Today);
        foreach (var claim in c) Assert.Equal(ClaimTexts.Errors.DeductibleDiffers, (await w.PayAsync(a, claim, "500", x.Today, x.Today)).Message);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id in @c", new { c }));
    }

    [Fact]
    public async Task The_same_coverage_in_another_event_withholds_its_own_deductible()
    {
        var (x, c, a) = await EventAsync(200m, 200m, parts: [("damage", "2000")]);
        var second = (await w.RegisterAsync(x.Claims, x.Policy, new DateOnly(x.Year, 10, 5), new DateOnly(x.Year, 10, 6), ("equipment", "500")))[0];
        await w.ApproveAsync(x.Claims, c[0], "2000", x.Today);
        await w.ApproveAsync(x.Claims, second, "500", x.Today);
        await w.PayAsync(a, c[0], "2000", x.Today, x.Today);
        await w.PayAsync(a, second, "500", x.Today, x.Today);
        Assert.Equal(200m, (await PaymentsAsync(second)).Single().DeductibleWithheld);
    }

    /// <summary>
    /// Legacy loss-event-check.ts J1–J4 (Codex H51) with their numbers: deductible 200 on the 5.1 coverages of one event, each claim
    /// approved and paid in full — J1 100 + 100: withheld 200, net 0; J2 50 + 50 + 50 on three coverages: withheld 150, net 0;
    /// J3 a single 100: withheld 100, net 0; J4 a single 500: withheld 200, net 300.
    /// </summary>
    [Theory]
    [InlineData("damage:100,equipment:100", 200, 0)]
    [InlineData("damage:50,equipment:50,theft:50", 150, 0)]
    [InlineData("damage:100", 100, 0)]
    [InlineData("damage:500", 200, 300)]
    public async Task J1_to_J4_small_and_single_payments_withhold_the_class_deductible_at_most_once(string paid, int withheld, int net)
    {
        var parts = paid.Split(',').Select(x => x.Split(':')).Select(x => (x[0], x[1])).ToArray();
        var (x, c, a) = await EventAsync(200m, 200m, parts: parts);
        foreach (var (claim, (_, amount)) in c.Zip(parts)) await w.ApproveAsync(x.Claims, claim, amount, x.Today);
        foreach (var (claim, (_, amount)) in c.Zip(parts)) Assert.NotNull((await w.PayAsync(a, claim, amount, x.Today, x.Today)).RedirectTo);
        Assert.Equal([(parts.Length, (decimal)withheld, (decimal)net)], await w.RowsAsync<(int, decimal, decimal)>(
            "select count(*), sum(deductible_withheld), sum(net_paid) from core.claim_payment where claim_id in @c", new { c }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_small_first_claim_withholds_100_and_the_next_claim_the_other_100_in_either_order(bool otherOrder)
    {
        var (x, c, a) = await EventAsync(200m, 200m, parts: [("damage", "100"), ("equipment", "900")]);
        await w.ApproveAsync(x.Claims, c[0], "100", x.Today);
        await w.ApproveAsync(x.Claims, c[1], "900", x.Today);
        if (!otherOrder)
        {
            await w.PayAsync(a, c[0], "100", x.Today, x.Today);
            Assert.Equal(Row(x.Today, 100m, 100m, 0m, 0m, 0m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[0])).Single());
            await w.PayAsync(a, c[1], "250", x.Today, x.Today);
            Assert.Equal(Row(x.Today, 250m, 100m, 0m, 150m, 150m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[1])).Single());
        }
        else
        {
            await w.PayAsync(a, c[1], "250", x.Today, x.Today);
            Assert.Equal(Row(x.Today, 250m, 200m, 0m, 50m, 50m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[1])).Single());
            await w.PayAsync(a, c[0], "100", x.Today, x.Today);
            Assert.Equal(Row(x.Today, 100m, 0m, 0m, 100m, 100m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[0])).Single());
        }
        await w.PayAsync(a, c[1], "650", x.Today, x.Today);
        Assert.Equal(0m, (await PaymentsAsync(c[1]))[1].DeductibleWithheld);
        var totals = await w.RowsAsync<(decimal, decimal, decimal)>("select sum(amount), sum(deductible_withheld), sum(net_paid) from core.claim_payment where claim_id in @c", new { c });
        Assert.Equal([(1000m, 200m, 800m)], totals);
        Assert.Equal((100m, 100m), (await w.RowsAsync<(decimal, decimal)>("select approved_amount, deductible_amount from core.claim where id = @id", new { id = c[0] })).Single());
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reject")]
    public async Task A_claim_closed_or_rejected_after_its_payment_keeps_the_deductible_it_withheld(string decision)
    {
        var (x, c, a) = await EventAsync(200m, 200m, parts: [("damage", "500"), ("equipment", "500")]);
        await w.ApproveAsync(x.Claims, c[0], "500", x.Today);
        await w.ApproveAsync(x.Claims, c[1], "500", x.Today);
        await w.PayAsync(a, c[0], "500", x.Today, x.Today);
        var first = await PaymentsAsync(c[0]);
        foreach (var d in decision == "close" ? new[] { "close" } : ["close", "reopen", "reject"])
            await ClaimActions.DecideClaimAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", c[0].ToString()), ("decision", d), ("rejection_reason", "უარი")), x.Today);
        Assert.Equal(decision == "close" ? "closed" : "rejected", await StatusAsync(c[0]));
        await w.PayAsync(a, c[1], "500", x.Today, x.Today);
        Assert.Equal(first, await PaymentsAsync(c[0]));
        Assert.Equal([(200m, 800m)], await w.RowsAsync<(decimal, decimal)>("select sum(deductible_withheld), sum(net_paid) from core.claim_payment where claim_id in @c", new { c }));
    }

    [Fact]
    public async Task Partial_payments_out_of_order_withhold_200_in_all_and_a_coverage_added_later_joins_its_class()
    {
        var (x, c, a) = await EventAsync(200m, 200m, parts: [("damage", "1000"), ("equipment", "500")]);
        await w.ApproveAsync(x.Claims, c[0], "1000", x.Today);
        await w.ApproveAsync(x.Claims, c[1], "500", x.Today);
        foreach (var (claim, amount) in new[] { (c[0], "120"), (c[0], "80"), (c[1], "500"), (c[0], "800") }) await w.PayAsync(a, claim, amount, x.Today, x.Today);
        Assert.Equal([(4, 200m, 1300m)], await w.RowsAsync<(int, decimal, decimal)>("select count(*), sum(deductible_withheld), sum(net_paid) from core.claim_payment where claim_id in @c", new { c }));

        var added = await ClaimActions.AddEventCoverageAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", c[0].ToString()), ("policy_line_id", x.Policy.LineOf("theft").ToString()),
            ("reported_on", ClaimWorld.D(x.Today)), ("initial_reserve", "400")));
        var third = Guid.Parse(added.RedirectTo!.Split('/')[2].Split('?')[0]);
        var ev = await w.ScalarAsync<string>("select v.number from core.loss_event v join core.claim c on c.loss_event_id = v.id where c.id = @id", new { id = c[0] });
        Assert.Equal($"{ev}-3", await w.ScalarAsync<string>("select number from core.claim where id = @third", new { third }));
        await w.ApproveAsync(x.Claims, third, "400", x.Today);
        await w.PayAsync(a, third, "400", x.Today, x.Today);
        Assert.Equal(0m, (await PaymentsAsync(third)).Single().DeductibleWithheld);
    }

    [Fact]
    public async Task A_read_only_user_calling_the_actions_directly_writes_nothing()
    {
        var x = await WorldAsync();
        var viewer = await w.UserAsync("viewer");
        var before = await w.ScalarAsync<int>("select (select count(*) from core.claim_payment) + (select count(*) from core.claim_reserve) + (select count(*) from core.claim_reserve_request) + (select count(*) from core.loss_event)");
        await ClaimActions.DecideClaimAsync(w.App, viewer, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("decision", "close")), x.Today);
        Assert.Equal($"/claims/{x.Claim}?reserve=not-allowed", await ClaimActions.ChangeReserveAsync(w.App, viewer,
            ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("booked_on", ClaimWorld.D(x.Today)), ("new_level", "5"), ("reason", "x")), x.Today));
        Assert.Equal(ClaimTexts.PaymentNotAllowed, (await w.PayAsync(viewer, x.Claim, "10", x.Today, x.Today)).Message);
        Assert.Equal(Ka.Common.ReadOnly, (await ClaimActions.RegisterClaimAsync(w.App, viewer, ClaimWorld.Form(("policy_number", x.Policy.Number)))).Message);
        Assert.Equal(Ka.Common.ReadOnly, (await ClaimActions.AddEventCoverageAsync(w.App, viewer, ClaimWorld.Form(("claim_id", x.Claim.ToString())))).Message);
        Assert.Equal($"/claims/{x.Claim}?reversal=not-allowed", await ClaimActions.ReverseClaimPaymentAsync(w.App, viewer, ClaimWorld.Form(("claim_id", x.Claim.ToString())), x.Today));
        Assert.Equal($"/claims/{x.Claim}?recovery=not-allowed", await ClaimActions.AddRecoveryAsync(w.App, viewer, ClaimWorld.Form(("claim_id", x.Claim.ToString()))));
        Assert.Equal("approved", await StatusAsync(x.Claim));
        Assert.Equal(before, await w.ScalarAsync<int>("select (select count(*) from core.claim_payment) + (select count(*) from core.claim_reserve) + (select count(*) from core.claim_reserve_request) + (select count(*) from core.loss_event)"));
    }

    // ---- the financial director's threshold (docs/05 ვ8)

    [Fact]
    public async Task A_net_above_the_threshold_becomes_a_request_and_the_financial_director_pays_it_exactly_once()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 50000m, 100m)]);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "30000")))[0];
        var today = new DateOnly(y, 11, 3);
        await w.ApproveAsync(claims, claim, "30000", today);
        var a = await w.UserAsync("accountant");
        // 10 100.00 − 100.00 = 10 000.00 net: the threshold itself is not above it.
        Assert.NotNull((await w.PayAsync(a, claim, "10100.00", today, today)).RedirectTo);
        // 10 000.01 net is: nothing is paid, a request is kept.
        var asked = await w.PayAsync(a, claim, "10000.01", today, today, ("note", "დიდი"));
        Assert.True(asked.RedirectTo == $"/claims/{claim}?payment=requested", $"{asked.Message} {string.Join(",", asked.Errors)}");
        Assert.Single(await PaymentsAsync(claim));
        var request = await w.ScalarAsync<Guid>("select id from core.claim_payment_request where claim_id = @claim", new { claim });
        Assert.Equal([(10000.01m, 0m, 0m, 10000.01m, 10000.01m, false, "none", a.Id)], await w.RowsAsync<(decimal, decimal, decimal, decimal, decimal, bool, string, Guid)>(
            "select amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, offset_premium, total_loss_mode, created_by from core.claim_payment_request where id = @request", new { request }));
        var form = ClaimWorld.Form(("claim_id", claim.ToString()), ("request_id", request.ToString()));
        // The accountant may not authorise; the admin may, once.
        Assert.Equal($"/claims/{claim}?payment=not-allowed", await ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, a, form));
        var admin = await w.UserAsync("admin");
        Assert.Equal($"/claims/{claim}?payment=approved", await ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, admin, form));
        Assert.Equal($"/claims/{claim}?payment=already", await ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, admin, form));
        var paid = await PaymentsAsync(claim);
        Assert.Equal(2, paid.Count);
        Assert.Equal((10000.01m, admin.Id), (paid[1].NetPaidGel, paid[1].CreatedBy!.Value));
        Assert.Equal(request, await w.ScalarAsync<Guid>("select request_id from core.claim_payment where request_id is not null"
            + " and claim_id = @claim", new { claim }));
        Assert.Equal($"/claims/{claim}?payment=state", await ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, admin,
            ClaimWorld.Form(("claim_id", claim.ToString()), ("request_id", Guid.NewGuid().ToString()))));
    }

    [Fact]
    public async Task A_request_whose_payment_would_now_be_different_is_stale_and_writes_nothing()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 50000m, 100m), new ClaimWorld.Line("equipment", 50000m, 100m)]);
        var ids = await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "20000"), ("equipment", "500"));
        var today = new DateOnly(y, 11, 3);
        await w.ApproveAsync(claims, ids[0], "20000", today);
        await w.ApproveAsync(claims, ids[1], "500", today);
        var a = await w.UserAsync("accountant");
        Assert.Equal($"/claims/{ids[0]}?payment=requested", (await w.PayAsync(a, ids[0], "15000", today, today)).RedirectTo);
        // The class's deductible is taken by the other coverage meanwhile: the request's 100.00 would now be 0.00.
        Assert.NotNull((await w.PayAsync(a, ids[1], "500", today, today)).RedirectTo);
        var request = await w.ScalarAsync<Guid>("select id from core.claim_payment_request where claim_id = @c", new { c = ids[0] });
        var admin = await w.UserAsync("admin");
        Assert.Equal($"/claims/{ids[0]}?payment=stale", await ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, admin,
            ClaimWorld.Form(("claim_id", ids[0].ToString()), ("request_id", request.ToString()))));
        Assert.Empty(await PaymentsAsync(ids[0]));
    }

    [Fact]
    public async Task Negative_control_the_database_refuses_a_payment_above_the_threshold_without_its_request()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        var e = await Assert.ThrowsAnyAsync<Exception>(() => w.App.AsUserAsync(a.Id, tx => tx.ExecuteAsync("""
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id)
            values (@claim, @today, 20000, 0, 0, 20000, 20000, 1, N'policyholder', @party)
            """, new { claim = x.Claim, today = x.Today, party = x.Policy.PartyId })));
        Assert.Equal("CQ003", Insurance.Application.Data.DbError.From(e)?.Code);
    }
}
