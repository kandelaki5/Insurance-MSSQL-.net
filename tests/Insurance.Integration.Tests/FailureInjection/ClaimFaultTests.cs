using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// The claim actions' binding permission check and their reads failing once, on the real SQL Server through the command
/// interceptor (legacy scripts/integration/claim-payment-permission.test.ts, claim-payment-reversal.test.ts,
/// claim-reserve-approval.test.ts, recovery-permission.test.ts — their "the check that binds is in the transaction; a failed
/// read stops it" cases, the third payee's register and FMS reads, the payment day's rate and the shared deductible's second
/// payment failing at its reserve). The legacy world: an approved GEL claim of 1 000.00, the class's deductible 100.00,
/// reserve 1 000.00 booked 02.10, October closed, "today" 03.11 — each test in its own year.
/// </summary>
public sealed class ClaimFaultTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase db;
    private readonly Faults faults = new();
    private readonly ClaimWorld w;
    private static int year = 2500;

    public ClaimFaultTests(TestDatabase db)
    {
        this.db = db;
        w = new ClaimWorld(db, faults);
    }

    private sealed record World(CurrentUser Claims, ClaimWorld.Policy Policy, Guid Claim, DateOnly Today, int Year);

    private sealed record PaymentRow(DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel,
        decimal Rate, string PayeeKind, Guid? PayeePartyId, Guid? OffsetPaymentId, Guid? CreatedBy);

    private async Task<World> WorldAsync(string currency = "GEL", (DateOnly, decimal)[]? installments = null, decimal premium = 0m, decimal policyRate = 1m, int? y = null)
    {
        var at = y ?? Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(at, 10, 1), [new ClaimWorld.Line("damage", 20000m, 100m, premium)], installments, currency, policyRate);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(at, 10, 1), new DateOnly(at, 10, 2), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(at, 10, 2));
        await w.CloseMonthAsync(claims.Id, new DateOnly(at, 10, 1));
        return new World(claims, policy, claim, new DateOnly(at, 11, 3), at);
    }

    private Task<List<PaymentRow>> PaymentsAsync(Guid claim) => w.RowsAsync<PaymentRow>("""
        select paid_on as PaidOn, amount as Amount, deductible_withheld as DeductibleWithheld, premium_offset as PremiumOffset, net_paid as NetPaid,
               net_paid_gel as NetPaidGel, rate as Rate, payee_kind as PayeeKind, payee_party_id as PayeePartyId, offset_payment_id as OffsetPaymentId, created_by as CreatedBy
        from core.claim_payment where claim_id = @claim order by created_at
        """, new { claim });

    private Task<List<(DateOnly BookedOn, decimal Change, Guid? CreatedBy)>> ReservesAsync(Guid claim) => w.RowsAsync<(DateOnly, decimal, Guid?)>(
        "select booked_on, change, created_by from core.claim_reserve where claim_id = @claim order by created_at", new { claim });

    private static PaymentRow Row(DateOnly on, decimal gross, decimal withheld, decimal offset, decimal net, decimal netGel, Guid payee, Guid by, string kind = "policyholder", decimal rate = 1m, Guid? receipt = null) =>
        new(on, gross, withheld, offset, net, netGel, rate, kind, payee, receipt, by);

    /// <summary>An underwriter in a group of its own that is given <paramref name="permission"/>; the group's id.</summary>
    private async Task<(CurrentUser User, Guid Group)> GroupedUnderwriterAsync(string permission)
    {
        var u = await w.UserAsync("underwriter");
        return (u, await w.GroupAsync(permission, u.Id));
    }

    /// <summary>The group's grants taken away, on a connection of its own (the admin's change landing between two statements).</summary>
    private void Ungrant(Guid group)
    {
        using var c = new SqlConnection(db.AppConnection);
        c.Open();
        c.Execute("delete from core.user_group_permission where group_id = @group", new { group });
    }

    // ================================================================ claim-payment-permission.test.ts

    [Fact]
    public async Task Claim_payment_between_the_quick_check_and_the_transaction_the_grant_goes_the_transaction_refuses_before_any_read_of_the_claim_writes_nothing()
    {
        var x = await WorldAsync();
        var (u, group) = await GroupedUnderwriterAsync("claim.payment.record");
        faults.Reset();
        faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => Ungrant(group));
        var outcome = await w.PayAsync(u, x.Claim, "250.00", x.Today, x.Today, ("note", "ნაწილობრივი ანაზღაურება"));
        Assert.True(faults.AllFired);
        Assert.Equal((null, ClaimTexts.PaymentNotAllowed), (outcome.RedirectTo, outcome.Message));
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.Single(await ReservesAsync(x.Claim));
        faults.AssertOnlyThePermissionWasRead();
    }

    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Claim_payment_the_checks_read_fails_the_same_error_nothing_written_the_retry_pays_250_once(string label, string name)
    {
        var x = await WorldAsync();
        var (u, _) = await GroupedUnderwriterAsync("claim.payment.record");
        faults.Reset();
        var sentinel = new InjectedFault($"PAYMENT_{label}_{name}");
        faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => w.PayAsync(u, x.Claim, "250.00", x.Today, x.Today)));
        Assert.Equal(label == "binding", faults.BegunATransaction);
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.Single(await ReservesAsync(x.Claim));
        Assert.Equal($"/claims/{x.Claim}?paid=1", (await w.PayAsync(u, x.Claim, "250.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, x.Policy.PartyId, u.Id)], await PaymentsAsync(x.Claim));
        Assert.Equal(0, w.Network.Requests);
    }

    [Fact]
    public async Task Claim_payment_a_viewer_with_only_the_recovery_and_the_premium_receipt_cannot_pay()
    {
        var x = await WorldAsync();
        var viewer = await w.UserAsync("viewer");
        await w.GrantAsync(viewer.Id, "claim.recovery.receive", "allow");
        await w.GrantAsync(viewer.Id, "premium.receipt.create", "allow");
        Assert.Equal(ClaimTexts.PaymentNotAllowed, (await w.PayAsync(viewer, x.Claim, "250.00", x.Today, x.Today)).Message);
        Assert.Empty(await PaymentsAsync(x.Claim));
    }

    private const string PayeeLookup = "from core.party where tax_id = @idNumber or foreign_id = @idNumber";
    private const string FmsRead = "from core.fms_suspension s";

    private async Task<(World X, CurrentUser A, Guid Service, string ServiceTax)> ThirdPayeeWorldAsync()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        var tax = Fixtures.RandomTaxId();
        return (x, a, await w.PartyAsync(tax, "სერვისცენტრი"), tax);
    }

    private Task<FormOutcome> PayServiceAsync(World x, CurrentUser a, string tax) =>
        w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("payee_kind", "workshop"), ("payee_id_number", tax), ("note", "სერვისცენტრს"));

    [Fact]
    public async Task Claim_payment_to_a_third_payee_the_register_lookup_fails_once_the_same_error_no_payment_transaction_the_retry_pays_250_once()
    {
        var (x, a, service, tax) = await ThirdPayeeWorldAsync();
        faults.Reset();
        var sentinel = new InjectedFault("PAYEE_LOOKUP_SENTINEL");
        faults.FailOnce(Faults.Any(PayeeLookup), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PayServiceAsync(x, a, tax)));
        Assert.False(faults.BegunATransaction);
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.Equal($"/claims/{x.Claim}?paid=1", (await PayServiceAsync(x, a, tax)).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, service, a.Id, "workshop")], await PaymentsAsync(x.Claim));
        Assert.Equal(2, (await ReservesAsync(x.Claim)).Count);
    }

    [Fact]
    public async Task Claim_payment_to_a_third_payee_the_services_own_FMS_read_fails_once_after_the_policyholders_not_taken_as_clean_nothing_written_the_retry_pays_once()
    {
        var (x, a, service, tax) = await ThirdPayeeWorldAsync();
        faults.Reset();
        var sentinel = new InjectedFault("PAYEE_FMS_SENTINEL");
        faults.FailOnce(c => Faults.Has(c, FmsRead) && Equals(c.Parameter("partyId"), service), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PayServiceAsync(x, a, tax)));
        // In the payment's transaction: the policyholder's suspensions were read first, then the service's (which failed).
        var fms = faults.Log.Where(c => Faults.Has(c, FmsRead) && c.Isolation == IsolationLevel.ReadCommitted).Select(c => c.Parameter("partyId")).ToList();
        Assert.Equal([x.Policy.PartyId, service], fms);
        Assert.Empty(await PaymentsAsync(x.Claim));
        Assert.Single(await ReservesAsync(x.Claim));
        Assert.Equal("approved", await w.ScalarAsync<string>("select status from core.claim where id = @c", new { c = x.Claim }));
        Assert.Equal($"/claims/{x.Claim}?paid=1", (await PayServiceAsync(x, a, tax)).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, service, a.Id, "workshop")], await PaymentsAsync(x.Claim));
    }

    [Fact]
    public async Task Claim_payment_an_old_USD_claim_the_payment_days_stored_rate_read_fails_once_the_same_error_nothing_written_the_retry_writes_each_row_once()
    {
        var y = Interlocked.Increment(ref year);
        await w.RateAsync("USD", new DateOnly(y, 10, 31), 2.6m);
        await w.RateAsync("USD", new DateOnly(y, 11, 3), 2.7m);
        await w.RateAsync("USD", new DateOnly(y, 11, 30), 2.8m);
        var x = await WorldAsync(currency: "USD", installments: [(new DateOnly(y, 10, 1), 90m)], premium: 90m, policyRate: 2.5m, y: y);
        var a = await w.UserAsync("accountant");
        await w.GrantAsync(a.Id, "premium.receipt.create", "deny");
        async Task<int> Receipts() => await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId });
        faults.Reset();
        var sentinel = new InjectedFault("PAYMENT_DAY_RATE_SENTINEL");
        faults.FailOnce(c => Faults.Has(c, "from core.exchange_rate where currency = @currency and rate_date = @date") && Equals(c.Parameter("date"), x.Today), sentinel);
        Task<FormOutcome> Pay() => w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("offset_premium", "on"), ("note", "ჩათვლით"));
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(Pay));
        Assert.Equal((0, 0), (await Receipts(), (await PaymentsAsync(x.Claim)).Count));
        Assert.Single(await ReservesAsync(x.Claim));
        Assert.Equal(3, await w.ScalarAsync<int>("select count(*) from core.exchange_rate where currency = N'USD' and year(rate_date) = @y", new { y }));
        Assert.NotNull((await Pay()).RedirectTo);
        var receipt = await w.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @p", new { p = x.Policy.PartyId });
        Assert.Equal(1, await Receipts());
        Assert.Equal(243m, await w.ScalarAsync<decimal>("select amount_gel from core.payment where id = @receipt", new { receipt }));
        Assert.Equal([(x.Policy.Installments[0], 90m, 243m, 2.7m, x.Today)], await w.RowsAsync<(Guid, decimal, decimal, decimal, DateOnly)>(
            "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @receipt", new { receipt }));
        Assert.Equal([Row(x.Today, 250m, 100m, 90m, 60m, 162m, x.Policy.PartyId, a.Id, rate: 2.7m, receipt: receipt)], await PaymentsAsync(x.Claim));
        Assert.Equal(0, w.Network.Requests);
    }

    // ---- one deductible for a class in a loss event: the second claim's payment failing at its reserve INSERT

    private static Func<CommandEvent, bool> ReserveInsertOf(Guid claim) => c => Faults.Has(c, "insert into core.claim_reserve") && Equals(c.Parameter("claimId"), claim);

    private async Task<(World X, IReadOnlyList<Guid> Claims, CurrentUser A)> EventAsync(params (string, string)[] parts)
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [
            new("damage", 20000m, 200m), new("equipment", 20000m, 200m), new("assistance", 1000m, 50m)]);
        var ids = await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), parts);
        return (new World(claims, policy, ids[0], new DateOnly(y, 11, 3), y), ids, await w.UserAsync("accountant"));
    }

    /// <summary>A claim's payments and reserve movements, every column, as one text (to compare before and after).</summary>
    private async Task<string> HistoryAsync(Guid claim) =>
        string.Join("|", (await PaymentsAsync(claim)).Select(p => p.ToString())) + "#" + string.Join("|", (await ReservesAsync(claim)).Select(r => r.ToString()))
        + "#" + await w.ScalarAsync<string>("select concat(status, N'/', closed_on, N'/', rejection_reason) from core.claim where id = @claim", new { claim });

    private async Task<(decimal Gross, decimal Withheld, decimal Net, decimal Reserve)> TotalsAsync(IReadOnlyList<Guid> c) =>
        (await w.RowsAsync<(decimal, decimal, decimal, decimal)>("""
            select coalesce((select sum(amount) from core.claim_payment where claim_id in @c), 0), coalesce((select sum(deductible_withheld) from core.claim_payment where claim_id in @c), 0),
                   coalesce((select sum(net_paid) from core.claim_payment where claim_id in @c), 0), coalesce((select sum(change) from core.claim_reserve where claim_id in @c), 0)
            """, new { c })).Single();

    [Fact]
    public async Task Shared_deductible_C2s_reserve_insert_fails_once_after_its_payment_row_C1s_200_and_every_row_as_they_were_the_retry_pays_C2_500_once_with_nothing_withheld()
    {
        var (x, c, a) = await EventAsync(("damage", "2000"), ("equipment", "500"), ("assistance", "150"));
        foreach (var (claim, amount) in c.Zip(new[] { "2000", "500", "150" })) await w.ApproveAsync(x.Claims, claim, amount, x.Today);
        Assert.NotNull((await w.PayAsync(a, c[0], "2000.00", x.Today, x.Today)).RedirectTo);
        var c1 = await HistoryAsync(c[0]);
        faults.Reset();
        var sentinel = new InjectedFault("SHARED_DEDUCTIBLE_C2_RESERVE");
        faults.FailOnce(ReserveInsertOf(c[1]), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => w.PayAsync(a, c[1], "500.00", x.Today, x.Today)));
        // The payment row was sent before the reserve that failed: a failure after the money is written.
        Assert.Contains(faults.Log, e => Faults.Has(e, "insert into core.claim_payment") && Equals(e.Parameter("claimId"), c[1]));
        Assert.Equal(c1, await HistoryAsync(c[0]));
        Assert.Empty(await PaymentsAsync(c[1]));
        Assert.Single(await ReservesAsync(c[1]));
        Assert.Equal("approved", await w.ScalarAsync<string>("select status from core.claim where id = @id", new { id = c[1] }));
        Assert.NotNull((await w.PayAsync(a, c[1], "500.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal([Row(x.Today, 500m, 0m, 0m, 500m, 500m, x.Policy.PartyId, a.Id)], await PaymentsAsync(c[1]));
        Assert.Equal(c1, await HistoryAsync(c[0]));
        Assert.Equal((2500m, 200m, 2300m, 150m), await TotalsAsync(c));
    }

    [Fact]
    public async Task Small_first_claim_C1_paid_then_C2_250_fails_once_at_its_reserve_insert_C1s_100_as_it_was_the_retry_withholds_the_other_100_once_then_650_none()
    {
        var (x, c, a) = await EventAsync(("damage", "100"), ("equipment", "900"));
        await w.ApproveAsync(x.Claims, c[0], "100", x.Today);
        await w.ApproveAsync(x.Claims, c[1], "900", x.Today);
        Assert.NotNull((await w.PayAsync(a, c[0], "100.00", x.Today, x.Today)).RedirectTo);
        var c1 = await HistoryAsync(c[0]);
        Assert.Equal([Row(x.Today, 100m, 100m, 0m, 0m, 0m, x.Policy.PartyId, a.Id)], await PaymentsAsync(c[0]));
        faults.Reset();
        var sentinel = new InjectedFault("SMALL_FIRST_C2_RESERVE");
        faults.FailOnce(ReserveInsertOf(c[1]), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => w.PayAsync(a, c[1], "250.00", x.Today, x.Today)));
        Assert.Equal(c1, await HistoryAsync(c[0]));
        Assert.Empty(await PaymentsAsync(c[1]));
        Assert.NotNull((await w.PayAsync(a, c[1], "250.00", x.Today, x.Today)).RedirectTo);
        Assert.NotNull((await w.PayAsync(a, c[1], "650.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, x.Policy.PartyId, a.Id), Row(x.Today, 650m, 0m, 0m, 650m, 650m, x.Policy.PartyId, a.Id)], await PaymentsAsync(c[1]));
        Assert.Equal((1000m, 200m, 800m, 0m), await TotalsAsync(c));
        Assert.Equal(c1, await HistoryAsync(c[0]));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("rejected")]
    public async Task A_claim_closed_or_rejected_after_its_payment_C2_250_fails_once_at_its_reserve_insert_C1s_October_rows_as_they_were_the_retry_withholds_the_other_100_once_then_650_none(string now)
    {
        var (x, c, a) = await EventAsync(("damage", "100"), ("equipment", "900"));
        var oct2 = new DateOnly(x.Year, 10, 2);
        var oct10 = new DateOnly(x.Year, 10, 10);
        await w.ApproveAsync(x.Claims, c[0], "100", oct2);
        await w.ApproveAsync(x.Claims, c[1], "900", oct2);
        // C1 paid 10.10 in October: 100.00 gross, 100.00 withheld, 0.00 net; then October closes and C1 is closed (or rejected) on 01.11.
        Assert.NotNull((await w.PayAsync(a, c[0], "100.00", oct10, oct10)).RedirectTo);
        await w.CloseMonthAsync(a.Id, oct10);
        var nov1 = new DateOnly(x.Year, 11, 1);
        foreach (var d in now == "closed" ? new[] { "close" } : ["close", "reopen", "reject"])
            await ClaimActions.DecideClaimAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", c[0].ToString()), ("decision", d), ("rejection_reason", "ტესტი: დაზღვევით გაუთვალისწინებელი")), nov1);
        Assert.Equal(now, await w.ScalarAsync<string>("select status from core.claim where id = @id", new { id = c[0] }));
        var c1 = await HistoryAsync(c[0]);
        Assert.Equal([Row(oct10, 100m, 100m, 0m, 0m, 0m, x.Policy.PartyId, a.Id)], await PaymentsAsync(c[0]));
        faults.Reset();
        var sentinel = new InjectedFault($"HISTORICAL_{now}_C2_RESERVE");
        faults.FailOnce(ReserveInsertOf(c[1]), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => w.PayAsync(a, c[1], "250.00", x.Today, x.Today)));
        Assert.Equal(c1, await HistoryAsync(c[0]));
        Assert.Empty(await PaymentsAsync(c[1]));
        Assert.Equal([(oct2, 900m, (Guid?)x.Claims.Id)], await ReservesAsync(c[1]));
        Assert.NotNull((await w.PayAsync(a, c[1], "250.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal([Row(x.Today, 250m, 100m, 0m, 150m, 150m, x.Policy.PartyId, a.Id)], await PaymentsAsync(c[1]));
        Assert.NotNull((await w.PayAsync(a, c[1], "650.00", x.Today, x.Today)).RedirectTo);
        Assert.Equal(Row(x.Today, 650m, 0m, 0m, 650m, 650m, x.Policy.PartyId, a.Id), (await PaymentsAsync(c[1]))[1]);
        Assert.Equal("paid", await w.ScalarAsync<string>("select status from core.claim where id = @id", new { id = c[1] }));
        Assert.Equal(c1, await HistoryAsync(c[0]));
        Assert.Equal((1000m, 200m, 800m, 0m), await TotalsAsync(c));
    }

    // ================================================================ claim-payment-reversal.test.ts

    private async Task<(World X, Guid Paid)> PaidWorldAsync()
    {
        var x = await WorldAsync();
        var a = await w.UserAsync("accountant");
        Assert.NotNull((await w.PayAsync(a, x.Claim, "250.00", x.Today, x.Today, ("note", "ნაწილობრივი ანაზღაურება"))).RedirectTo);
        return (x, await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @c", new { c = x.Claim }));
    }

    private Task<string?> ReverseAsync(World x, Guid paid, CurrentUser who) =>
        ClaimActions.ReverseClaimPaymentAsync(w.App, who, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("claim_payment_id", paid.ToString()), ("reason", "correction"),
            ("reversed_on", ClaimWorld.D(x.Today)), ("note", "შეცდომით ჩაიწერა")), x.Today);

    private Task<List<(string Kind, decimal Amount, Guid? Reverses, Guid? By)>> ReversalRowsAsync(Guid claim) => w.RowsAsync<(string, decimal, Guid?, Guid?)>(
        "select kind, amount, reverses_claim_payment_id, created_by from core.claim_payment where claim_id = @claim order by created_at", new { claim });

    [Fact]
    public async Task Claim_payment_reversal_between_the_quick_check_and_the_transaction_the_grant_goes_the_transaction_refuses_before_reading_the_payment_writes_nothing()
    {
        var (x, paid) = await PaidWorldAsync();
        var (u, group) = await GroupedUnderwriterAsync("claim.payment.reverse");
        var before = await ReversalRowsAsync(x.Claim);
        faults.Reset();
        faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => Ungrant(group));
        Assert.Equal($"/claims/{x.Claim}?reversal=not-allowed", await ReverseAsync(x, paid, u));
        Assert.True(faults.AllFired);
        Assert.Equal(before, await ReversalRowsAsync(x.Claim));
        Assert.Equal(2, (await ReservesAsync(x.Claim)).Count);
        faults.AssertOnlyThePermissionWasRead();
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("binding")]
    public async Task Claim_payment_reversal_the_checks_read_of_the_group_grants_fails_the_same_error_nothing_written_the_retry_reverses_once(string label)
    {
        var (x, paid) = await PaidWorldAsync();
        var (u, _) = await GroupedUnderwriterAsync("claim.payment.reverse");
        var before = await ReversalRowsAsync(x.Claim);
        faults.Reset();
        var sentinel = new InjectedFault($"REVERSAL_{label}");
        faults.FailOnce(Faults.Read(label, "group grants"), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => ReverseAsync(x, paid, u)));
        Assert.Equal(before, await ReversalRowsAsync(x.Claim));
        Assert.Equal($"/claims/{x.Claim}?reversal=ok", await ReverseAsync(x, paid, u));
        Assert.Equal([.. before, ("reversal", -250m, (Guid?)paid, (Guid?)u.Id)], await ReversalRowsAsync(x.Claim));
        Assert.Equal(1000m, (await ReservesAsync(x.Claim)).Sum(r => r.Change));
    }

    // ================================================================ claim-reserve-approval.test.ts

    private async Task<(World X, Guid Request)> AskedAsync()
    {
        var x = await WorldAsync();
        Assert.Equal($"/claims/{x.Claim}?reserve=requested", await ClaimActions.ChangeReserveAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", x.Claim.ToString()),
            ("booked_on", ClaimWorld.D(x.Today)), ("new_level", "1200.50"), ("reason", "ხარჯთაღრიცხვა გაიზარდა")), x.Today));
        return (x, await w.ScalarAsync<Guid>("select id from core.claim_reserve_request where claim_id = @c", new { c = x.Claim }));
    }

    private Task<string?> ApproveAsync(World x, Guid request, CurrentUser who) =>
        ClaimActions.ApproveReserveRequestAsync(w.App, who, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("request_id", request.ToString())));

    private Task<List<(DateOnly BookedOn, decimal Change, Guid? CreatedBy, Guid? Request)>> MovementsAsync(World x) => w.RowsAsync<(DateOnly, decimal, Guid?, Guid?)>(
        "select booked_on, change, created_by, request_id from core.claim_reserve where claim_id = @c order by created_at", new { c = x.Claim });

    [Fact]
    public async Task Reserve_approval_between_the_quick_check_and_the_transaction_the_grant_goes_the_transaction_refuses_before_reading_the_request_writes_nothing()
    {
        var (x, request) = await AskedAsync();
        var (u, group) = await GroupedUnderwriterAsync("claim.reserve.approve");
        faults.Reset();
        faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => Ungrant(group));
        Assert.Equal($"/claims/{x.Claim}?reserve=approve-not-allowed", await ApproveAsync(x, request, u));
        Assert.True(faults.AllFired);
        Assert.Equal([(new DateOnly(x.Year, 10, 2), 1000m, (Guid?)x.Claims.Id, (Guid?)null)], await MovementsAsync(x));
        faults.AssertOnlyThePermissionWasRead();
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("binding")]
    public async Task Reserve_approval_the_checks_read_of_the_group_grants_fails_the_same_error_nothing_written_the_retry_approves_once(string label)
    {
        var (x, request) = await AskedAsync();
        var (u, _) = await GroupedUnderwriterAsync("claim.reserve.approve");
        faults.Reset();
        var sentinel = new InjectedFault($"RESERVE_{label}");
        faults.FailOnce(Faults.Read(label, "group grants"), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => ApproveAsync(x, request, u)));
        Assert.Single(await MovementsAsync(x));
        Assert.Equal($"/claims/{x.Claim}?reserve=approved", await ApproveAsync(x, request, u));
        Assert.Equal([(new DateOnly(x.Year, 10, 2), 1000m, (Guid?)x.Claims.Id, (Guid?)null), (x.Today, 200.50m, (Guid?)u.Id, (Guid?)request)], await MovementsAsync(x));
    }

    // ================================================================ recovery-permission.test.ts

    private async Task<(Guid Claim, DateOnly Today)> RecoveryWorldAsync()
    {
        var x = await WorldAsync();
        var admin = await w.UserAsync("admin");
        Assert.NotNull((await w.PayAsync(admin, x.Claim, "900", x.Today, x.Today)).RedirectTo);
        Assert.Null(await AddRecoveryAsync(admin, x.Claim, x.Today, "7.50", "ოქტომბრის რეგრესი"));
        return (x.Claim, x.Today);
    }

    private Task<string?> AddRecoveryAsync(CurrentUser who, Guid claim, DateOnly on, string amount = "25.00", string note = "ნოემბრის რეგრესი") =>
        ClaimActions.AddRecoveryAsync(w.App, who, ClaimWorld.Form(("claim_id", claim.ToString()), ("kind", "subrogation"), ("received_on", ClaimWorld.D(on)), ("amount_gel", amount), ("note", note)));

    private Task<List<(decimal Amount, string Note, Guid By)>> RecoveriesAsync(Guid claim) => w.RowsAsync<(decimal, string, Guid)>(
        "select amount_gel, note, created_by from core.claim_recovery where claim_id = @claim order by created_at", new { claim });

    [Fact]
    public async Task Recovery_between_the_quick_check_and_the_transaction_the_grant_goes_the_transaction_refuses_writes_nothing()
    {
        var (claim, today) = await RecoveryWorldAsync();
        var (u, group) = await GroupedUnderwriterAsync("claim.recovery.receive");
        faults.Reset();
        faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => Ungrant(group));
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", await AddRecoveryAsync(u, claim, today));
        Assert.True(faults.AllFired);
        Assert.Equal([7.50m], (await RecoveriesAsync(claim)).Select(r => r.Amount));
        faults.AssertOnlyThePermissionWasRead();
    }

    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Recovery_the_checks_read_fails_the_same_error_nothing_written_the_retry_records_25_once(string label, string name)
    {
        var (claim, today) = await RecoveryWorldAsync();
        var (u, _) = await GroupedUnderwriterAsync("claim.recovery.receive");
        faults.Reset();
        var sentinel = new InjectedFault($"RECOVERY_{label}_{name}");
        faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => AddRecoveryAsync(u, claim, today)));
        Assert.Equal(label == "binding", faults.BegunATransaction);
        Assert.Single(await RecoveriesAsync(claim));
        Assert.Null(await AddRecoveryAsync(u, claim, today));
        Assert.Equal([7.50m, 25.00m], (await RecoveriesAsync(claim)).Select(r => r.Amount));
        Assert.Equal(("ნოემბრის რეგრესი", u.Id), ((await RecoveriesAsync(claim))[1].Note, (await RecoveriesAsync(claim))[1].By));
    }
}
