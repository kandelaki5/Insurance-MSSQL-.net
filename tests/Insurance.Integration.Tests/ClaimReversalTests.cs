using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests;

/// <summary>
/// The reversal of a recorded claim payment on the real SQL Server (legacy scripts/integration/claim-payment-reversal.test.ts,
/// scripts/claim-reversal-db-check.ts): who may do it, the exact mirror at the original's rate, the reserve, the status by
/// the signed sum, "returned" with its day and bank reference, the set-off receipt reversed with it, closed months, refusals
/// with nothing written, and the reinsurer's share following it. "Today" is 03.11 of the test's own year; October is closed
/// after the October payment where the old test closed it.
/// </summary>
public sealed class ClaimReversalTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2060;

    private sealed record World(CurrentUser Claims, CurrentUser Accountant, ClaimWorld.Policy Policy, Guid Claim, Guid Paid, DateOnly Today, int Year);

    private sealed record Row(DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel, decimal Rate,
        string PayeeKind, Guid? PayeePartyId, Guid? OffsetPaymentId, string Kind, Guid? ReversesClaimPaymentId, string? ReversalReason, DateOnly? ReturnedOn,
        string? BankReference, Guid? CreatedBy);

    /// <summary>An approved GEL claim of 1 000.00 (deductible 100.00, reserve 1 000.00 booked 02.10) paid 250.00 by the accountant.</summary>
    private async Task<World> PaidAsync(bool closeOctober = true, string paidOn = "11-03", string amount = "250.00", (DateOnly, decimal)[]? installments = null,
        decimal premium = 0m, bool offset = false, Guid? payee = null, string? payeeTax = null)
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 20000m, 100m, premium)],
            installments?.Select(i => (new DateOnly(y, i.Item1.Month, i.Item1.Day), i.Item2)).ToArray());
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(y, 10, 2));
        var accountant = await w.UserAsync("accountant");
        var on = DateOnly.ParseExact($"{y}-{paidOn}", "yyyy-MM-dd");
        var today = new DateOnly(y, 11, 3);
        (string, string)[] more = payeeTax is null ? [("payee_kind", "policyholder")] : [("payee_kind", "workshop"), ("payee_id_number", payeeTax)];
        if (offset) more = [.. more, ("offset_premium", "on")];
        var paid = await ClaimActions.PayClaimAsync(w.App, w.Rates, accountant, ClaimWorld.Form([("claim_id", claim.ToString()), ("amount", amount), ("paid_on", ClaimWorld.D(on)),
            ("note", "ნაწილობრივი ანაზღაურება"), .. more]), on > today ? on : today);
        Assert.NotNull(paid.RedirectTo);
        if (closeOctober) await w.CloseMonthAsync(claims.Id, new DateOnly(y, 10, 1));
        var paidId = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @claim", new { claim });
        return new World(claims, accountant, policy, claim, paidId, today, y);
    }

    private static (string, string)[] Correction(World x, string? on = null) =>
        [("claim_id", x.Claim.ToString()), ("claim_payment_id", x.Paid.ToString()), ("reason", "correction"), ("reversed_on", on ?? ClaimWorld.D(x.Today)), ("note", "შეცდომით ჩაიწერა")];

    private Task<string?> ReverseAsync(World x, CurrentUser who, params (string, string)[] fields) =>
        ClaimActions.ReverseClaimPaymentAsync(w.App, who, ClaimWorld.Form(fields.Length == 0 ? Correction(x) : fields), x.Today);

    private static string Back(World x, string outcome) => $"/claims/{x.Claim}?reversal={outcome}";

    private Task<List<Row>> RowsAsync(Guid claim) => w.RowsAsync<Row>("""
        select paid_on as PaidOn, amount as Amount, deductible_withheld as DeductibleWithheld, premium_offset as PremiumOffset, net_paid as NetPaid, net_paid_gel as NetPaidGel,
               rate as Rate, payee_kind as PayeeKind, payee_party_id as PayeePartyId, offset_payment_id as OffsetPaymentId, kind as Kind,
               reverses_claim_payment_id as ReversesClaimPaymentId, reversal_reason as ReversalReason, returned_on as ReturnedOn, bank_reference as BankReference, created_by as CreatedBy
        from core.claim_payment where claim_id = @claim order by created_at
        """, new { claim });

    private Task<List<(DateOnly BookedOn, decimal Change)>> ReservesAsync(Guid claim) =>
        w.RowsAsync<(DateOnly, decimal)>("select booked_on, change from core.claim_reserve where claim_id = @claim order by created_at", new { claim });

    private Task<string> StatusAsync(Guid claim) => w.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim });

    private static Row Paid(World x, Guid by, DateOnly? on = null) =>
        new(on ?? x.Today, 250m, 100m, 0m, 150m, 150m, 1m, "policyholder", x.Policy.PartyId, null, "payment", null, null, null, null, by);

    private static Row Reversed(World x, Guid by, string reason = "correction", DateOnly? returnedOn = null, string? bank = null) =>
        new(x.Today, -250m, -100m, 0m, -150m, -150m, 1m, "policyholder", x.Policy.PartyId, null, "reversal", x.Paid, reason, returnedOn, bank, by);

    [Theory]
    [InlineData("accountant", true)]
    [InlineData("admin", true)]
    [InlineData("underwriter", false)]
    [InlineData("sales", false)]
    [InlineData("claims", false)]
    [InlineData("operations_head", false)]
    [InlineData("viewer", false)]
    public async Task The_roles_starting_set_reverses_or_is_refused_with_nothing_written(string role, bool may)
    {
        var x = await PaidAsync();
        var who = await w.UserAsync(role);
        var outcome = await ReverseAsync(x, who);
        if (may)
        {
            Assert.Equal(Back(x, "ok"), outcome);
            Assert.Equal([Paid(x, x.Accountant.Id), Reversed(x, who.Id)], await RowsAsync(x.Claim));
            Assert.Equal([(new DateOnly(x.Year, 10, 2), 1000m), (x.Today, -250m), (x.Today, 250m)], await ReservesAsync(x.Claim));
        }
        else
        {
            Assert.Equal(Back(x, "not-allowed"), outcome);
            Assert.Equal([Paid(x, x.Accountant.Id)], await RowsAsync(x.Claim));
            Assert.Equal(2, (await ReservesAsync(x.Claim)).Count);
        }
    }

    [Fact]
    public async Task A_group_gives_it_a_personal_removal_beats_the_group_and_recording_payments_does_not_give_it()
    {
        var x = await PaidAsync();
        var underwriter = await w.UserAsync("underwriter");
        var beka = await w.UserAsync("underwriter");
        await w.GroupAsync("claim.payment.reverse", underwriter.Id);
        await w.GrantAsync(beka.Id, "claim.payment.record", "allow");
        Assert.Equal(Back(x, "not-allowed"), await ReverseAsync(x, beka));
        await w.GrantAsync(underwriter.Id, "claim.payment.reverse", "deny");
        Assert.Equal(Back(x, "not-allowed"), await ReverseAsync(x, underwriter));
        await w.ExecAsync("delete from core.user_permission where user_id = @id", new { id = underwriter.Id });
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, underwriter));
        Assert.Equal([Paid(x, x.Accountant.Id), Reversed(x, underwriter.Id)], await RowsAsync(x.Claim));
    }

    [Fact]
    public async Task Reversed_250_is_the_exact_opposite_the_reserve_gets_it_back_and_November_shows_it_while_October_stays()
    {
        var x = await PaidAsync(closeOctober: false, paidOn: "10-20");
        await w.CloseMonthAsync(x.Claims.Id, new DateOnly(x.Year, 10, 1));
        var october = await w.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @c and paid_on <= @d", new { c = x.Claim, d = new DateOnly(x.Year, 10, 31) });
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        var rows = await RowsAsync(x.Claim);
        Assert.Equal(Paid(x, x.Accountant.Id, new DateOnly(x.Year, 10, 20)), rows[0]);
        Assert.Equal(Reversed(x, x.Accountant.Id), rows[1]);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId }));
        Assert.Equal("approved", await StatusAsync(x.Claim));
        Assert.Equal(october, await w.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @c and paid_on <= @d", new { c = x.Claim, d = new DateOnly(x.Year, 10, 31) }));
        Assert.Equal(-250m, await w.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @c and paid_on >= @d", new { c = x.Claim, d = new DateOnly(x.Year, 11, 1) }));
        Assert.Equal(1000m, (await ReservesAsync(x.Claim)).Sum(r => r.Change));
    }

    [Fact]
    public async Task The_deductible_is_given_back_and_the_next_payment_withholds_it_again()
    {
        var x = await PaidAsync();
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        Assert.NotNull((await w.PayAsync(x.Accountant, x.Claim, "250.00", x.Today, x.Today, ("note", "ხელახლა"))).RedirectTo);
        Assert.Equal([(250m, 100m, 150m), (-250m, -100m, -150m), (250m, 100m, 150m)], (await RowsAsync(x.Claim)).Select(r => (r.Amount, r.DeductibleWithheld, r.NetPaid)));
    }

    [Fact]
    public async Task The_full_1000_paid_then_reversed_goes_back_to_approved_and_can_be_paid_again()
    {
        var x = await PaidAsync(amount: "1000.00");
        Assert.Equal("paid", await StatusAsync(x.Claim));
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        Assert.Equal("approved", await StatusAsync(x.Claim));
        Assert.Equal(0m, (await RowsAsync(x.Claim)).Sum(r => r.Amount));
        Assert.Equal(1000m, (await ReservesAsync(x.Claim)).Sum(r => r.Change));
        Assert.NotNull((await w.PayAsync(x.Accountant, x.Claim, "1000.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal("paid", await StatusAsync(x.Claim));
    }

    [Fact]
    public async Task Returned_keeps_its_day_and_bank_reference_and_needs_both()
    {
        var x = await PaidAsync();
        (string, string)[] Returned(string on, string bank) => [.. Correction(x).Where(f => f.Item1 != "reason"), ("reason", "returned"), ("returned_on", on), ("bank_reference", bank)];
        Assert.Equal(Back(x, "invalid"), await ReverseAsync(x, x.Accountant, Returned("", "TBC-1")));
        Assert.Equal(Back(x, "invalid"), await ReverseAsync(x, x.Accountant, Returned(ClaimWorld.D(x.Today), "")));
        Assert.Equal(Back(x, "invalid"), await ReverseAsync(x, x.Accountant, Returned(ClaimWorld.D(x.Today), new string('x', 101))));
        Assert.Single(await RowsAsync(x.Claim));
        Assert.Equal(Back(x, "returned-before"), await ReverseAsync(x, x.Accountant, Returned(ClaimWorld.D(x.Today.AddDays(-1)), "TBC-2026-1102-17")));
        Assert.Single(await RowsAsync(x.Claim));
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant, Returned(ClaimWorld.D(x.Today), "TBC-2026-1103-17")));
        Assert.Equal(Reversed(x, x.Accountant.Id, "returned", x.Today, "TBC-2026-1103-17"), (await RowsAsync(x.Claim))[1]);
    }

    [Fact]
    public async Task Returned_on_a_day_in_the_closed_October_after_the_October_payment_is_only_recorded()
    {
        var x = await PaidAsync(closeOctober: false, paidOn: "10-20");
        await w.CloseMonthAsync(x.Claims.Id, new DateOnly(x.Year, 10, 1));
        var returnedOn = new DateOnly(x.Year, 10, 25);
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant, [.. Correction(x).Where(f => f.Item1 != "reason"), ("reason", "returned"),
            ("returned_on", ClaimWorld.D(returnedOn)), ("bank_reference", "TBC-1025-03")]));
        Assert.Equal([(new DateOnly(x.Year, 10, 20), 250m, null, null), (x.Today, -250m, "returned", returnedOn)],
            (await RowsAsync(x.Claim)).Select(r => (r.PaidOn, r.Amount, r.ReversalReason, r.ReturnedOn)));
    }

    [Fact]
    public async Task A_second_reversal_is_already_and_a_reversal_of_the_reversal_is_refused()
    {
        var x = await PaidAsync();
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        Assert.Equal(Back(x, "already"), await ReverseAsync(x, x.Accountant));
        var reversal = (await w.RowsAsync<Guid>("select id from core.claim_payment where claim_id = @c and kind = N'reversal'", new { c = x.Claim })).Single();
        Assert.Equal(Back(x, "state"), await ReverseAsync(x, x.Accountant, [.. Correction(x).Where(f => f.Item1 != "claim_payment_id"), ("claim_payment_id", reversal.ToString())]));
        Assert.Equal(2, (await RowsAsync(x.Claim)).Count);
    }

    [Fact]
    public async Task A_date_after_today_not_a_date_or_before_the_payment_is_refused()
    {
        var x = await PaidAsync();
        Assert.Equal(Back(x, "invalid"), await ReverseAsync(x, x.Accountant, Correction(x, ClaimWorld.D(x.Today.AddDays(1)))));
        Assert.Equal(Back(x, "invalid"), await ReverseAsync(x, x.Accountant, Correction(x, x.Today.ToString("dd.MM.yyyy"))));
        Assert.Equal(Back(x, "invalid"), await ReverseAsync(x, x.Accountant, [.. Correction(x).Where(f => f.Item1 != "reason"), ("reason", "lost")]));
        Assert.Equal(Back(x, "before"), await ReverseAsync(x, x.Accountant, Correction(x, ClaimWorld.D(x.Today.AddDays(-1)))));
        Assert.Single(await RowsAsync(x.Claim));
    }

    [Fact]
    public async Task An_October_payment_reversed_in_the_closed_October_is_period_closed_twice_then_reversed_in_November()
    {
        var x = await PaidAsync(closeOctober: false, paidOn: "10-20");
        await w.CloseMonthAsync(x.Claims.Id, new DateOnly(x.Year, 10, 1));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(Back(x, "period-closed"), await ReverseAsync(x, x.Accountant, Correction(x, ClaimWorld.D(new DateOnly(x.Year, 10, 25)))));
            Assert.Single(await RowsAsync(x.Claim));
            Assert.Equal(2, (await ReservesAsync(x.Claim)).Count);
        }
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
    }

    [Fact]
    public async Task A_closed_claim_another_claims_payment_or_a_made_up_id_is_refused_as_state()
    {
        var x = await PaidAsync();
        Assert.Equal(Back(x, "state"), await ReverseAsync(x, x.Accountant, [.. Correction(x).Where(f => f.Item1 != "claim_payment_id"), ("claim_payment_id", Guid.NewGuid().ToString())]));
        var other = Guid.NewGuid();
        Assert.Equal($"/claims/{other}?reversal=state", await ReverseAsync(x, x.Accountant, [.. Correction(x).Where(f => f.Item1 != "claim_id"), ("claim_id", other.ToString())]));
        await ClaimActions.DecideClaimAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("decision", "close")), x.Today);
        Assert.Equal(Back(x, "state"), await ReverseAsync(x, x.Accountant));
        Assert.Single(await RowsAsync(x.Claim));
        Assert.Null(await ClaimActions.ReverseClaimPaymentAsync(w.App, x.Accountant, ClaimWorld.Form(("claim_id", "not-a-uuid")), x.Today));
    }

    [Fact]
    public async Task The_client_sanctioned_or_the_third_payee_sanctioned_or_suspended_refuses_the_reversal()
    {
        var x = await PaidAsync();
        await w.ExecAsync("update core.party set sanctions_blocked = 1 where id = @p", new { p = x.Policy.PartyId });
        Assert.Equal(Back(x, "payee"), await ReverseAsync(x, x.Accountant));
        await w.ExecAsync("update core.party set sanctions_blocked = 0 where id = @p", new { p = x.Policy.PartyId });
        await w.ExecAsync("""
            insert core.fms_suspension (party_id, kind, scope, instructed_at, ends_at) values (@p, N'written', N'all', dateadd(day, -1, sysdatetimeoffset()), dateadd(day, 5, sysdatetimeoffset()))
            """, new { p = x.Policy.PartyId });
        Assert.Equal(Back(x, "suspended"), await ReverseAsync(x, x.Accountant));
        Assert.Single(await RowsAsync(x.Claim));

        var tax = Fixtures.RandomTaxId();
        var service = await w.PartyAsync(tax, "სერვისცენტრი");
        var y = await PaidAsync(payeeTax: tax);
        Assert.Equal(service, (await RowsAsync(y.Claim))[0].PayeePartyId);
        await w.ExecAsync("update core.party set sanctions_blocked = 1 where id = @service", new { service });
        Assert.Equal(Back(y, "payee"), await ReverseAsync(y, y.Accountant));
        Assert.Single(await RowsAsync(y.Claim));
    }

    [Theory]
    [InlineData("claim_reserve")]
    [InlineData("claim")]
    public async Task A_failure_after_the_reversal_row_leaves_nothing_and_the_retry_reverses_once(string table)
    {
        var x = await PaidAsync();
        await w.ArmFaultAsync(table);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ReverseAsync(x, x.Accountant));
        }
        finally
        {
            await w.DisarmAsync();
        }
        Assert.Single(await RowsAsync(x.Claim));
        Assert.Equal(2, (await ReservesAsync(x.Claim)).Count);
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        Assert.Equal([Paid(x, x.Accountant.Id), Reversed(x, x.Accountant.Id)], await RowsAsync(x.Claim));
    }

    [Fact]
    public async Task With_the_set_off_the_receipt_is_reversed_with_it_and_the_premium_is_owed_again()
    {
        var x = await PaidAsync(installments: [(new DateOnly(2000, 10, 1), 90m)], premium: 90m, offset: true);
        var receipt = (await RowsAsync(x.Claim))[0].OffsetPaymentId!.Value;
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        var payments = await w.RowsAsync<(Guid Id, DateOnly On, decimal Gel, string Method, string Kind, Guid? Reverses)>(
            "select id, received_on, amount_gel, method, kind, reverses_payment_id from core.payment where payer_party_id = @p order by created_at", new { p = x.Policy.PartyId });
        Assert.Equal(2, payments.Count);
        Assert.Equal((x.Today, -90m, "claim_offset", "reversal", (Guid?)receipt), (payments[1].On, payments[1].Gel, payments[1].Method, payments[1].Kind, payments[1].Reverses));
        Assert.Equal([(x.Policy.Installments[0], -90m, -90m, 1m, x.Today)], await w.RowsAsync<(Guid, decimal, decimal, decimal, DateOnly)>(
            "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id", new { id = payments[1].Id }));
        var rows = await RowsAsync(x.Claim);
        Assert.Equal((-250m, -100m, -90m, -60m, (Guid?)payments[1].Id, "reversal"), (rows[1].Amount, rows[1].DeductibleWithheld, rows[1].PremiumOffset, rows[1].NetPaid, rows[1].OffsetPaymentId, rows[1].Kind));
        Assert.Equal(0m, await w.ScalarAsync<decimal>("select sum(a.amount) from core.payment_allocation a where a.installment_id = @i", new { i = x.Policy.Installments[0] }));
    }

    [Theory]
    [InlineData("claim_reserve")]
    [InlineData("claim")]
    public async Task A_failure_after_the_set_off_receipt_is_mirrored_leaves_nothing_and_the_retry_writes_each_once(string table)
    {
        var x = await PaidAsync(installments: [(new DateOnly(2000, 10, 1), 90m)], premium: 90m, offset: true);
        await w.ArmFaultAsync(table);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ReverseAsync(x, x.Accountant));
        }
        finally
        {
            await w.DisarmAsync();
        }
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId }));
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, x.Accountant));
        Assert.Equal((2, 2, 2, 3), (
            await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId }),
            await w.ScalarAsync<int>("select count(*) from core.payment_allocation where installment_id = @i", new { i = x.Policy.Installments[0] }),
            (await RowsAsync(x.Claim)).Count, (await ReservesAsync(x.Claim)).Count));
    }

    [Fact]
    public async Task A_set_off_beyond_two_to_the_53_tetri_is_mirrored_exactly()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        const decimal big = 90071992547408.01m;
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 99999999999999m, 0m, big)], [(new DateOnly(y, 10, 1), big)]);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        var today = new DateOnly(y, 11, 3);
        // 90 071 992 547 500.00 is an even number of tetri above 2^53, exact as a double; the set-off is the whole instalment.
        await w.ApproveAsync(claims, claim, "90071992547500.00", today);
        var a = await w.UserAsync("accountant");
        Assert.NotNull((await w.PayAsync(a, claim, "90071992547500.00", today, today, ("offset_premium", "on"))).RedirectTo);
        var row = (await RowsAsync(claim)).Single();
        Assert.Equal((big, 91.99m), (row.PremiumOffset, row.NetPaid));
        var x = new World(claims, a, policy, claim, await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @claim", new { claim }), today, y);
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, a));
        Assert.Equal([big, -big], await w.RowsAsync<decimal>("select amount_gel from core.payment where payer_party_id = @p order by created_at", new { p = policy.PartyId }));
        Assert.Equal([(big, big), (-big, -big)], await w.RowsAsync<(decimal, decimal)>("select amount, amount_gel from core.payment_allocation where installment_id = @i order by created_at", new { i = policy.Installments[0] }));
    }

    [Fact]
    public async Task A_set_off_receipt_already_reversed_alone_is_offset_reversed_and_a_set_off_without_its_receipt_is_snapshot()
    {
        var x = await PaidAsync(installments: [(new DateOnly(2000, 10, 1), 90m)], premium: 90m, offset: true);
        var receipt = (await RowsAsync(x.Claim))[0].OffsetPaymentId!.Value;
        await w.App.AsUserAsync(x.Accountant.Id, tx => ReceiptReversal.MirrorReceiptAsync(tx, new ReceiptReversal.ReceiptToReverse(receipt, 90m, "claim_offset", x.Policy.PartyId), x.Today, "ცალკე", x.Accountant.Id));
        Assert.Equal(Back(x, "offset-reversed"), await ReverseAsync(x, x.Accountant));
        Assert.Single(await RowsAsync(x.Claim));

        var y = await PaidAsync();
        // An old row: a set-off recorded without its receipt.
        var old = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id)
            values (@old, @claim, @today, 100, 0, 40, 60, 60, 1, N'policyholder', @party)
            """, new { old, claim = y.Claim, today = y.Today, party = y.Policy.PartyId });
        Assert.Equal(Back(y, "snapshot"), await ReverseAsync(y, y.Accountant, [.. Correction(y).Where(f => f.Item1 != "claim_payment_id"), ("claim_payment_id", old.ToString())]));
        Assert.Equal(2, (await RowsAsync(y.Claim)).Count);
    }

    [Fact]
    public async Task On_a_reinsured_claim_the_reinsurers_share_follows_the_reversal_and_October_stays()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var reinsurer = await w.PartyAsync();
        var treaty = Guid.NewGuid();
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 20000m, 100m)]);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        await w.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent)
            values (@treaty, @number, N'კვოტა 30 %', @reinsurer, N'quota_share', @from, @to, N'GEL', 30);
            insert core.claim_cession (claim_id, treaty_id, share_percent) values (@claim, @treaty, 30);
            """, new { treaty, number = "QS-" + ClaimWorld.Tag(), reinsurer, from = new DateOnly(y, 1, 1), to = new DateOnly(y, 12, 31), claim });
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(y, 10, 2));
        var a = await w.UserAsync("accountant");
        var oct20 = new DateOnly(y, 10, 20);
        await w.PayAsync(a, claim, "250.00", oct20, oct20);
        await w.CloseMonthAsync(claims.Id, oct20);
        var today = new DateOnly(y, 11, 3);
        var octoberEnd = new DateOnly(y, 10, 31);
        async Task<(long InPaid, long InReserve)> Share(DateOnly? asOf) =>
            await w.App.ReadTxAsync(async tx => (await ReinsuranceStore.ClaimSharesAsync(tx, claim, asOf)).Select(s => (s.InPaid, s.InReserve)).Single());
        Assert.Equal((7500, 22500), await Share(octoberEnd));
        var x = new World(claims, a, policy, claim, await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @claim", new { claim }), today, y);
        Assert.Equal(Back(x, "ok"), await ReverseAsync(x, a));
        Assert.Equal((0, 30000), await Share(null));
        Assert.Equal((7500, 22500), await Share(octoberEnd));
        Assert.Equal(Back(x, "already"), await ReverseAsync(x, await w.UserAsync("admin")));
        // A closed treaty and a new one change neither the stored 30 % nor October.
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @treaty", new { treaty });
        Assert.Equal((7500, 22500), await Share(octoberEnd));
    }
}
