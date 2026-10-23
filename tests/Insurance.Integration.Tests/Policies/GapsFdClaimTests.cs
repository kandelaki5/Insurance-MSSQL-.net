using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The fictional claim world of legacy fd-net-transfer-chain.test.ts on a real database: a claim approved for 20 000.00
/// on a CASCO policy from 01.10.2026, the class deductible as given, the reserve 20 000.00 booked 02.10; official rates
/// USD 02.11 2.8, 03.11 2.7, 04.11 2.8, EUR 03.11 3.1234. The claim actions take "today" as the legacy tests did, so the
/// legacy days are kept (today 03.11.2026).
/// </summary>
public sealed class FdClaimWorld(TestDatabase db)
{
    public ClaimWorld C { get; } = new(db);
    public World W { get; } = new(db);
    public static readonly DateOnly Today = new(2026, 11, 3);
    public static readonly DateOnly Start = new(2026, 10, 1);
    public const string Note = "ნაწილობრივი ანაზღაურება";

    public sealed record Claim(CurrentUser Claims, ClaimWorld.Policy Policy, Guid Id);

    public async Task RatesAsync()
    {
        await W.RateAsync("USD", new DateOnly(2026, 11, 2), 2.8m);
        await W.RateAsync("USD", Today, 2.7m);
        await W.RateAsync("USD", new DateOnly(2026, 11, 4), 2.8m);
        await W.RateAsync("EUR", Today, 3.1234m);
        await W.RateAsync("USD", new DateOnly(2026, 10, 20), 2.7m);
    }

    /// <summary>The claim, its policy (an unpaid instalment of <paramref name="owed"/> due 01.10 when given), approved for <paramref name="approved"/>.</summary>
    public async Task<Claim> ClaimAsync(string currency = "GEL", decimal deductible = 1000m, decimal? owed = null, decimal approved = 20000m, bool totalLoss = false, DateOnly? occurred = null)
    {
        var claims = await C.UserAsync("claims");
        // The legacy cases describe old claims: a USD/EUR policy's claim in its currency (H46 setting claim.currency = policy).
        if (currency != "GEL") await C.OldClaimsAsync();
        var policy = await C.PolicyAsync(claims.Id, Start, [new ClaimWorld.Line("damage", 50000m, deductible, owed ?? 0m)],
            owed is { } o ? [(Start, o)] : null, currency, 1m);
        // Reported on 02.10 (or the day after a later loss), approved that day.
        var reported = occurred is { } late && late >= new DateOnly(2026, 10, 2) ? late.AddDays(1) : new DateOnly(2026, 10, 2);
        var claim = (await C.RegisterAsync(claims, policy, occurred ?? Start, reported, ("damage", "20000")))[0];
        await C.ApproveAsync(claims, claim, approved.ToString(System.Globalization.CultureInfo.InvariantCulture), reported, totalLoss);
        Assert.Equal("approved", await C.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim }));
        return new Claim(claims, policy, claim);
    }

    public Task<FormOutcome> PayAsync(CurrentUser user, Guid claim, string amount, DateOnly? paidOn = null, params (string, string)[] more) =>
        C.PayAsync(user, claim, amount, paidOn ?? Today, Today, [("note", Note), .. more]);

    public Task<string?> AuthoriseAsync(CurrentUser user, Guid claim, Guid request) =>
        ClaimActions.ApproveClaimPaymentRequestAsync(C.App, C.Rates, user, ClaimWorld.Form(("claim_id", claim.ToString()), ("request_id", request.ToString())));

    public sealed record PaymentRow(DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel,
        decimal Rate, string PayeeKind, Guid? PayeePartyId, Guid? OffsetPaymentId, Guid? CreatedBy, Guid? RequestId);

    public Task<List<PaymentRow>> PaymentsAsync(Guid claim) => C.RowsAsync<PaymentRow>("""
        select paid_on as PaidOn, amount as Amount, deductible_withheld as DeductibleWithheld, premium_offset as PremiumOffset, net_paid as NetPaid,
               net_paid_gel as NetPaidGel, rate as Rate, payee_kind as PayeeKind, payee_party_id as PayeePartyId, offset_payment_id as OffsetPaymentId,
               created_by as CreatedBy, request_id as RequestId
        from core.claim_payment where claim_id = @claim order by created_at
        """, new { claim });

    public sealed record RequestRow(DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel,
        decimal Rate, string PayeeKind, Guid PayeePartyId, bool OffsetPremium, string TotalLossMode, string? Note, Guid CreatedBy);

    public Task<List<RequestRow>> RequestsAsync(Guid claim) => C.RowsAsync<RequestRow>("""
        select paid_on as PaidOn, amount as Amount, deductible_withheld as DeductibleWithheld, premium_offset as PremiumOffset, net_paid as NetPaid,
               net_paid_gel as NetPaidGel, rate as Rate, payee_kind as PayeeKind, payee_party_id as PayeePartyId, offset_premium as OffsetPremium,
               total_loss_mode as TotalLossMode, note as Note, created_by as CreatedBy
        from core.claim_payment_request where claim_id = @claim order by created_at
        """, new { claim });

    public Task<Guid> RequestIdAsync(Guid claim, int n = 0) => C.ScalarAsync<Guid>(
        "select id from core.claim_payment_request where claim_id = @claim order by created_at offset @n rows fetch next 1 rows only", new { claim, n });

    public Task<List<(DateOnly BookedOn, decimal Change, Guid? CreatedBy)>> ReservesAsync(Guid claim) => C.RowsAsync<(DateOnly, decimal, Guid?)>(
        "select booked_on, change, created_by from core.claim_reserve where claim_id = @claim order by created_at", new { claim });

    /// <summary>Everything financial a claim payment writes, counted, with the claim's status: what a refusal must leave as it was.</summary>
    public Task<string> LedgerAsync(Claim c) => C.ScalarAsync<string>("""
        select concat((select count(*) from core.claim_payment where claim_id = @claim), '/', (select count(*) from core.claim_reserve where claim_id = @claim), '/',
                      (select count(*) from core.payment where payer_party_id = @party), '/',
                      (select count(*) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policy), '/',
                      (select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policy), '/',
                      (select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @policy), '/',
                      (select status from core.claim where id = @claim), '/', (select status from core.policy where id = @policy))
        """, new { claim = c.Id, party = c.Policy.PartyId, policy = c.Policy.Id });

    public static PaymentRow Paid(DateOnly on, decimal gross, decimal withheld, decimal offset, decimal net, decimal netGel, decimal rate, Guid payee, Guid by, Guid? receipt = null, Guid? request = null) =>
        new(on, gross, withheld, offset, net, netGel, rate, "policyholder", payee, receipt, by, request);
}

/// <summary>
/// A claim payment's financial-director check takes the net actually transferred, in GEL as recorded (owner 17.10.2026,
/// docs/05 ვ8), through the real PayClaimAsync and ApproveClaimPaymentRequestAsync (legacy fd-net-transfer-chain.test.ts:
/// the net-transfer variants, the half tetri, a request and its approval, who authorises, the approval's re-checks, a
/// total loss above the threshold). Threshold: the default setting 10 000.00. Every figure by hand, as in the legacy test.
/// </summary>
public sealed class GapsFdClaimTests(TestDatabase db) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private readonly FdClaimWorld f = new(db);
    private static readonly DateOnly Today = FdClaimWorld.Today;
    private const string Requested = "?payment=requested";

    public Task InitializeAsync() => f.RatesAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static void IsRequested(FdClaimWorld.Claim c, FormOutcome o) => Assert.True(o.RedirectTo == $"/claims/{c.Id}{Requested}", $"{o.RedirectTo} {o.Message} {string.Join(",", o.Errors)}");

    private static void IsPaid(FdClaimWorld.Claim c, FormOutcome o) => Assert.True(o.RedirectTo == $"/claims/{c.Id}?paid=1", $"{o.RedirectTo} {o.Message} {string.Join(",", o.Errors)}");

    // ---------------------------------------------------------------- the net actually transferred

    [Fact]
    public async Task Gross_12000_deductible_1000_premium_2000_set_off_9000_transferred_not_above_10000()
    {
        var c = await f.ClaimAsync(owed: 2000m);
        var a = await f.C.UserAsync("accountant");
        IsPaid(c, await f.PayAsync(a, c.Id, "12000.00", null, ("offset_premium", "on")));
        var receipt = Assert.Single(await f.C.RowsAsync<(Guid Id, DateOnly On, decimal Gel, string Method, string Kind)>(
            "select id, received_on, amount_gel, method, kind from core.payment where payer_party_id = @p", new { p = c.Policy.PartyId }));
        Assert.Equal((Today, 2000m, "claim_offset", "receipt"), (receipt.On, receipt.Gel, receipt.Method, receipt.Kind));
        Assert.Equal([(c.Policy.Installments[0], 2000m, 2000m, 1m, Today)], await f.C.RowsAsync<(Guid, decimal, decimal, decimal, DateOnly)>(
            "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id", new { id = receipt.Id }));
        Assert.Equal([FdClaimWorld.Paid(Today, 12000m, 1000m, 2000m, 9000m, 9000m, 1m, c.Policy.PartyId, a.Id, receipt.Id)], await f.PaymentsAsync(c.Id));
        Assert.Equal([(new DateOnly(2026, 10, 2), 20000m, (Guid?)c.Claims.Id), (Today, -12000m, a.Id)], await f.ReservesAsync(c.Id));
        Assert.Empty(await f.RequestsAsync(c.Id));
    }

    [Theory]
    [InlineData("10999.99", "9999.99", false)]
    [InlineData("11000.00", "10000.00", false)]
    [InlineData("11000.01", "10000.01", true)]
    public async Task Gross_with_deductible_1000_the_net_transferred_is_above_10000_or_not(string gross, string net, bool needed)
    {
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        var admin = await f.C.UserAsync("admin");
        var outcome = await f.PayAsync(a, c.Id, gross);
        Guid? request = null;
        if (needed)
        {
            IsRequested(c, outcome);
            Assert.Empty(await f.PaymentsAsync(c.Id));
            request = await f.RequestIdAsync(c.Id);
            Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request.Value));
        }
        else IsPaid(c, outcome);
        var by = needed ? admin.Id : a.Id;
        Assert.Equal([FdClaimWorld.Paid(Today, decimal.Parse(gross), 1000m, 0m, decimal.Parse(net), decimal.Parse(net), 1m, c.Policy.PartyId, by, request: request)], await f.PaymentsAsync(c.Id));
    }

    [Fact]
    public async Task Gross_1000_all_withheld_as_the_deductible_nothing_transferred_nothing_to_authorise()
    {
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        IsPaid(c, await f.PayAsync(a, c.Id, "1000.00"));
        Assert.Equal([FdClaimWorld.Paid(Today, 1000m, 1000m, 0m, 0m, 0m, 1m, c.Policy.PartyId, a.Id)], await f.PaymentsAsync(c.Id));
        Assert.Empty(await f.RequestsAsync(c.Id));
    }

    [Theory]
    [InlineData("2026-11-02", "2.8", "10360.00", true)]
    [InlineData("2026-11-03", "2.7", "9990.00", false)]
    public async Task USD_4000_deductible_300_3700_at_the_payment_days_rate_no_second_conversion(string day, string rate, string gel, bool needed)
    {
        var paidOn = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);
        var c = await f.ClaimAsync("USD", 300m);
        var a = await f.C.UserAsync("accountant");
        var admin = await f.C.UserAsync("admin");
        var outcome = await f.PayAsync(a, c.Id, "4000.00", paidOn);
        Guid? request = null;
        if (needed)
        {
            IsRequested(c, outcome);
            request = await f.RequestIdAsync(c.Id);
            Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request.Value));
        }
        else IsPaid(c, outcome);
        Assert.Equal([FdClaimWorld.Paid(paidOn, 4000m, 300m, 0m, 3700m, decimal.Parse(gel, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), c.Policy.PartyId, needed ? admin.Id : a.Id, request: request)], await f.PaymentsAsync(c.Id));
        Assert.Equal(0, f.C.Network.Requests);
    }

    [Fact]
    public async Task EUR_3501_64_deductible_300_3201_64_at_3_1234_is_10000_0002_recorded_10000_00_not_above()
    {
        var c = await f.ClaimAsync("EUR", 300m);
        var a = await f.C.UserAsync("accountant");
        IsPaid(c, await f.PayAsync(a, c.Id, "3501.64"));
        Assert.Equal([FdClaimWorld.Paid(Today, 3501.64m, 300m, 0m, 3201.64m, 10000.00m, 3.1234m, c.Policy.PartyId, a.Id)], await f.PaymentsAsync(c.Id));
    }

    [Fact]
    public async Task Two_payments_of_one_claim_6000_net_each_each_on_its_own_not_above()
    {
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        IsPaid(c, await f.PayAsync(a, c.Id, "7000.00"));
        IsPaid(c, await f.PayAsync(a, c.Id, "6000.00"));
        Assert.Equal([(7000m, 1000m, 6000m, 6000m), (6000m, 0m, 6000m, 6000m)], (await f.PaymentsAsync(c.Id)).Select(p => (p.Amount, p.DeductibleWithheld, p.NetPaid, p.NetPaidGel)));
        Assert.Empty(await f.RequestsAsync(c.Id));
    }

    [Theory]
    [InlineData("2026-11-05", "2.002", "2.49", "4.98")]
    [InlineData("2026-11-05", "2.002", "2.50", "5.01")]
    [InlineData("2026-11-05", "2.002", "2.51", "5.03")]
    [InlineData("2026-11-06", "2.0018", "25.00", "50.05")]
    public async Task A_claim_paid_in_USD_net_at_a_rate_is_recorded_in_GEL_half_away_from_zero(string day, string rate, string amount, string gel)
    {
        var on = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);
        await f.W.RateAsync("USD", on, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture));
        var c = await f.ClaimAsync("USD", 0m);
        var a = await f.C.UserAsync("accountant");
        IsPaid(c, await f.C.PayAsync(a, c.Id, amount, on, on));
        var p = Assert.Single(await f.PaymentsAsync(c.Id));
        Assert.Equal((decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(gel, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)),
            (p.NetPaid, p.NetPaidGel, p.Rate));
    }

    [Fact]
    public async Task A_claim_of_USD_100_with_USD_2_50_premium_set_off_at_2_002_the_receipt_5_01_net_97_50_is_195_20()
    {
        var on = new DateOnly(2026, 11, 5);
        await f.W.RateAsync("USD", on, 2.002m);
        var c = await f.ClaimAsync("USD", 0m, owed: 2.50m);
        var a = await f.C.UserAsync("accountant");
        IsPaid(c, await f.C.PayAsync(a, c.Id, "100.00", on, on, ("offset_premium", "on")));
        var receipt = Assert.Single(await f.C.RowsAsync<(Guid Id, DateOnly On, decimal Gel, string Method, string Kind, Guid By)>(
            "select id, received_on, amount_gel, method, kind, created_by from core.payment where payer_party_id = @p", new { p = c.Policy.PartyId }));
        Assert.Equal((on, 5.01m, "claim_offset", "receipt", a.Id), (receipt.On, receipt.Gel, receipt.Method, receipt.Kind, receipt.By));
        Assert.Equal([(c.Policy.Installments[0], 2.50m, 5.01m, 2.002m, on)], await f.C.RowsAsync<(Guid, decimal, decimal, decimal, DateOnly)>(
            "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id", new { id = receipt.Id }));
        Assert.Equal([FdClaimWorld.Paid(on, 100m, 0m, 2.50m, 97.50m, 195.20m, 2.002m, c.Policy.PartyId, a.Id, receipt.Id)], await f.PaymentsAsync(c.Id));
    }

    // ---------------------------------------------------------------- a request, and its approval

    [Fact]
    public async Task Net_10000_01_a_request_with_its_exact_quote_by_the_accountant_nothing_else_written()
    {
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        var before = await f.LedgerAsync(c);
        IsRequested(c, await f.PayAsync(a, c.Id, "11000.01"));
        Assert.Equal([new FdClaimWorld.RequestRow(Today, 11000.01m, 1000m, 0m, 10000.01m, 10000.01m, 1m, "policyholder", c.Policy.PartyId, false, "none", FdClaimWorld.Note, a.Id)],
            await f.RequestsAsync(c.Id));
        Assert.Equal(before, await f.LedgerAsync(c));
    }

    [Fact]
    public async Task Gross_14000_deductible_1000_premium_2000_set_off_net_11000_a_request_the_set_off_rolled_back_approved_written_once_with_the_request()
    {
        var c = await f.ClaimAsync(owed: 2000m);
        var a = await f.C.UserAsync("accountant");
        var admin = await f.C.UserAsync("admin");
        var before = await f.LedgerAsync(c);
        IsRequested(c, await f.PayAsync(a, c.Id, "14000.00", null, ("offset_premium", "on")));
        Assert.Equal(before, await f.LedgerAsync(c));
        Assert.Equal([new FdClaimWorld.RequestRow(Today, 14000m, 1000m, 2000m, 11000m, 11000m, 1m, "policyholder", c.Policy.PartyId, true, "none", FdClaimWorld.Note, a.Id)],
            await f.RequestsAsync(c.Id));
        var request = await f.RequestIdAsync(c.Id);
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request));
        var receipt = Assert.Single(await f.C.RowsAsync<(Guid Id, decimal Gel, string Method, string Kind, Guid By)>(
            "select id, amount_gel, method, kind, created_by from core.payment where payer_party_id = @p", new { p = c.Policy.PartyId }));
        Assert.Equal((2000m, "claim_offset", "receipt", admin.Id), (receipt.Gel, receipt.Method, receipt.Kind, receipt.By));
        Assert.Equal([FdClaimWorld.Paid(Today, 14000m, 1000m, 2000m, 11000m, 11000m, 1m, c.Policy.PartyId, admin.Id, receipt.Id, request)], await f.PaymentsAsync(c.Id));
        Assert.Equal([(new DateOnly(2026, 10, 2), 20000m, (Guid?)c.Claims.Id), (Today, -14000m, admin.Id)], await f.ReservesAsync(c.Id));
    }

    [Fact]
    public async Task A_forged_form_a_net_a_flag_a_request_of_its_own_changes_nothing_still_a_request()
    {
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        IsRequested(c, await f.PayAsync(a, c.Id, "11000.01", null, ("net_paid_gel", "1.00"), ("needs_authorization", "no"), ("approved", "yes"), ("request_id", Guid.NewGuid().ToString())));
        Assert.Equal([new FdClaimWorld.RequestRow(Today, 11000.01m, 1000m, 0m, 10000.01m, 10000.01m, 1m, "policyholder", c.Policy.PartyId, false, "none", FdClaimWorld.Note, a.Id)],
            await f.RequestsAsync(c.Id));
        Assert.Empty(await f.PaymentsAsync(c.Id));
    }

    // ---------------------------------------------------------------- who authorises

    private async Task<(FdClaimWorld.Claim C, CurrentUser Accountant, Guid Request)> AskedAsync()
    {
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        IsRequested(c, await f.PayAsync(a, c.Id, "11000.01"));
        return (c, a, await f.RequestIdAsync(c.Id));
    }

    private FdClaimWorld.PaymentRow Approved(FdClaimWorld.Claim c, Guid by, Guid request) =>
        FdClaimWorld.Paid(Today, 11000.01m, 1000m, 0m, 10000.01m, 10000.01m, 1m, c.Policy.PartyId, by, request: request);

    [Fact]
    public async Task The_accountant_and_a_viewer_are_refused_nothing_written_the_request_stays()
    {
        var (c, a, request) = await AskedAsync();
        var before = await f.LedgerAsync(c);
        Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(a, c.Id, request));
        Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(await f.C.UserAsync("viewer"), c.Id, request));
        Assert.Equal(before, await f.LedgerAsync(c));
        Assert.Single(await f.RequestsAsync(c.Id));
    }

    [Fact]
    public async Task A_group_gives_it_to_an_underwriter_who_cannot_record_payments_approves_the_payment_by_the_underwriter_the_request_by_the_accountant()
    {
        var (c, a, request) = await AskedAsync();
        var underwriter = await f.C.UserAsync("underwriter");
        await f.C.GroupAsync("payment.large.authorize", underwriter.Id);
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(underwriter, c.Id, request));
        Assert.Equal([Approved(c, underwriter.Id, request)], await f.PaymentsAsync(c.Id));
        Assert.Equal([a.Id], (await f.RequestsAsync(c.Id)).Select(r => r.CreatedBy));
        // The authorisation is not a right to pay.
        Assert.Equal(ClaimTexts.PaymentNotAllowed, (await f.PayAsync(underwriter, c.Id, "250.00")).Message);
        Assert.Single(await f.PaymentsAsync(c.Id));
    }

    [Fact]
    public async Task A_personal_grant_gives_it_to_a_viewer_the_admins_own_removal_takes_it_from_the_admin()
    {
        var (c, _, request) = await AskedAsync();
        var admin = await f.C.UserAsync("admin");
        await f.C.GrantAsync(admin.Id, "payment.large.authorize", "deny");
        Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Empty(await f.PaymentsAsync(c.Id));
        var viewer = await f.C.UserAsync("viewer");
        await f.C.GrantAsync(viewer.Id, "payment.large.authorize", "allow");
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(viewer, c.Id, request));
        Assert.Equal([Approved(c, viewer.Id, request)], await f.PaymentsAsync(c.Id));
    }

    [Fact]
    public async Task Ones_own_request_the_accountant_given_the_authorisation_approves_it_without_it_refused_and_the_admin_approves_its_own()
    {
        var (c, a, request) = await AskedAsync();
        Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(a, c.Id, request));
        await f.C.GrantAsync(a.Id, "payment.large.authorize", "allow");
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(a, c.Id, request));
        Assert.Equal([Approved(c, a.Id, request)], await f.PaymentsAsync(c.Id));
        Assert.Equal([a.Id], (await f.RequestsAsync(c.Id)).Select(r => r.CreatedBy));

        var own = await f.ClaimAsync();
        var admin = await f.C.UserAsync("admin");
        IsRequested(own, await f.PayAsync(admin, own.Id, "11000.01"));
        var ownRequest = await f.RequestIdAsync(own.Id);
        Assert.Equal($"/claims/{own.Id}?payment=approved", await f.AuthoriseAsync(admin, own.Id, ownRequest));
        Assert.Equal([Approved(own, admin.Id, ownRequest)], await f.PaymentsAsync(own.Id));
    }

    [Fact]
    public async Task A_right_withdrawn_after_the_request_the_groups_grant_taken_away_then_the_admins_own_removal_refused_nothing_paid()
    {
        var (c, _, request) = await AskedAsync();
        var underwriter = await f.C.UserAsync("underwriter");
        var group = await f.C.GroupAsync("payment.large.authorize", underwriter.Id);
        await f.C.ExecAsync("delete from core.user_group_permission where group_id = @group", new { group });
        Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(underwriter, c.Id, request));
        var admin = await f.C.UserAsync("admin");
        await f.C.GrantAsync(admin.Id, "payment.large.authorize", "deny");
        Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Empty(await f.PaymentsAsync(c.Id));
        Assert.Single(await f.RequestsAsync(c.Id));
    }

    // ---------------------------------------------------------------- the approval works the payment out again

    [Fact]
    public async Task A_payment_of_500_recorded_after_the_request_withholds_500_of_the_deductible_stale_a_new_request_10500_01_is_approved()
    {
        var (c, a, request) = await AskedAsync();
        var admin = await f.C.UserAsync("admin");
        IsPaid(c, await f.PayAsync(a, c.Id, "500.00"));
        var before = await f.LedgerAsync(c);
        Assert.Equal($"/claims/{c.Id}?payment=stale", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Equal(before, await f.LedgerAsync(c));
        Assert.Equal([(500m, 0m, (Guid?)null)], (await f.PaymentsAsync(c.Id)).Select(p => (p.Amount, p.NetPaid, p.RequestId)));
        IsRequested(c, await f.PayAsync(a, c.Id, "11000.01"));
        Assert.Equal([(1000m, 10000.01m), (500m, 10500.01m)], (await f.RequestsAsync(c.Id)).Select(r => (r.DeductibleWithheld, r.NetPaidGel)));
        var second = await f.RequestIdAsync(c.Id, 1);
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, second));
        Assert.Equal([(500m, 0m, (Guid?)null), (11000.01m, 10500.01m, second)], (await f.PaymentsAsync(c.Id)).Select(p => (p.Amount, p.NetPaidGel, p.RequestId)));
    }

    [Fact]
    public async Task The_stored_rate_of_the_payment_day_corrected_after_the_request_USD_2_7_to_2_8_stale_nothing_written()
    {
        var day = new DateOnly(2026, 11, 9);
        await f.W.RateAsync("USD", day, 2.7m);
        var c = await f.ClaimAsync("USD", 300m);
        var a = await f.C.UserAsync("accountant");
        IsRequested(c, await f.C.PayAsync(a, c.Id, "5000.00", day, day));
        Assert.Equal([(4700m, 12690m, 2.7m)], (await f.RequestsAsync(c.Id)).Select(r => (r.NetPaid, r.NetPaidGel, r.Rate)));
        await f.W.RateAsync("USD", day, 2.8m);
        Assert.Equal($"/claims/{c.Id}?payment=stale", await f.AuthoriseAsync(await f.C.UserAsync("admin"), c.Id, await f.RequestIdAsync(c.Id)));
        Assert.Empty(await f.PaymentsAsync(c.Id));
    }

    [Fact]
    public async Task The_request_of_another_claim_or_a_made_up_one_state_nothing_written()
    {
        var (c, _, request) = await AskedAsync();
        var other = await f.ClaimAsync();
        var admin = await f.C.UserAsync("admin");
        Assert.Equal($"/claims/{other.Id}?payment=state", await f.AuthoriseAsync(admin, other.Id, request));
        Assert.Equal($"/claims/{c.Id}?payment=state", await f.AuthoriseAsync(admin, c.Id, Guid.NewGuid()));
        Assert.Empty(await f.PaymentsAsync(c.Id));
        Assert.Empty(await f.PaymentsAsync(other.Id));
    }

    [Fact]
    public async Task The_client_suspended_FMS_after_the_request_suspended_nothing_written()
    {
        var (c, _, request) = await AskedAsync();
        var admin = await f.C.UserAsync("admin");
        var suspension = await f.W.SuspendAsync(c.Policy.PartyId, admin.Id);
        Assert.Equal($"/claims/{c.Id}?payment=suspended", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Empty(await f.PaymentsAsync(c.Id));
        await f.W.LiftAsync(suspension);
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request));
    }

    [Fact]
    public async Task A_failure_after_the_payment_row_at_the_reserve_the_same_error_nothing_of_it_stays_the_retry_approves_once()
    {
        var (c, _, request) = await AskedAsync();
        var admin = await f.C.UserAsync("admin");
        var before = await f.LedgerAsync(c);
        await ArmAsync(f.C.Database, "claim_reserve");
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => f.AuthoriseAsync(admin, c.Id, request));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await DisarmAsync(f.C.Database);
        }
        Assert.Equal(before, await f.LedgerAsync(c));
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Equal([Approved(c, admin.Id, request)], await f.PaymentsAsync(c.Id));
        Assert.Equal($"/claims/{c.Id}?payment=already", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Single(await f.PaymentsAsync(c.Id));
    }

    // ---------------------------------------------------------------- a total loss above the threshold

    /// <summary>
    /// By hand: premium 3 650.00 for 01.10.2026–01.10.2027 (365 days), unpaid; loss 01.11 → 31 days kept, 3 650 × 31 / 365 =
    /// 310.00, cancellation −3 340.00, schedule −3 340.00; earned unpaid 310.00 set off; gross 14 000.00 − deductible
    /// 1 000.00 − 310.00 = net 12 690.00 &gt; 10 000.
    /// </summary>
    private async Task<FdClaimWorld.Claim> LossAsync() => await f.ClaimAsync(owed: 3650m, totalLoss: true, occurred: new DateOnly(2026, 11, 1));

    private async Task PostedAsync(FdClaimWorld.Claim c, Guid by, Guid request)
    {
        var loss = new DateOnly(2026, 11, 1);
        Assert.Equal([("cancelled", (DateOnly?)loss, ClaimTexts.Fields.IsTotalLoss)], await f.C.RowsAsync<(string, DateOnly?, string)>(
            "select status, cancelled_on, cancel_reason from core.policy where id = @id", new { id = c.Policy.Id }));
        Assert.Equal([("issue", 3650m, 3650m, FdClaimWorld.Start, FdClaimWorld.Start), ("cancellation", -3340m, -3340m, loss, loss)],
            await f.C.RowsAsync<(string, decimal, decimal, DateOnly, DateOnly)>("""
                select t.kind, t.amount, t.amount_gel, t.booked_on, t.cover_from from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                where l.policy_id = @id order by t.booked_on
                """, new { id = c.Policy.Id }));
        Assert.Equal([(c.Policy.Installments[0], loss, -3340m)], await f.C.RowsAsync<(Guid, DateOnly, decimal)>(
            "select installment_id, booked_on, change from core.installment_adjustment where installment_id = @i", new { i = c.Policy.Installments[0] }));
        var receipt = Assert.Single(await f.C.RowsAsync<(Guid Id, decimal Gel, string Method, string Kind)>(
            "select id, amount_gel, method, kind from core.payment where payer_party_id = @p", new { p = c.Policy.PartyId }));
        Assert.Equal((310m, "claim_offset", "receipt"), (receipt.Gel, receipt.Method, receipt.Kind));
        Assert.Equal([(c.Policy.Installments[0], 310m, 310m)], await f.C.RowsAsync<(Guid, decimal, decimal)>(
            "select installment_id, amount, amount_gel from core.payment_allocation where payment_id = @id", new { id = receipt.Id }));
        Assert.Equal([FdClaimWorld.Paid(Today, 14000m, 1000m, 310m, 12690m, 12690m, 1m, c.Policy.PartyId, by, receipt.Id, request)], await f.PaymentsAsync(c.Id));
    }

    [Fact]
    public async Task A_total_loss_above_the_threshold_asked_nothing_of_the_cancellation_schedule_or_set_off_stays_approved_each_once_again_already()
    {
        var c = await LossAsync();
        var a = await f.C.UserAsync("accountant");
        var admin = await f.C.UserAsync("admin");
        var before = await f.LedgerAsync(c);
        IsRequested(c, await f.PayAsync(a, c.Id, "14000.00", null, ("total_loss_mode", "earned_unpaid")));
        Assert.Equal(before, await f.LedgerAsync(c));
        Assert.Equal("active", await f.C.ScalarAsync<string>("select status from core.policy where id = @id", new { id = c.Policy.Id }));
        Assert.Equal([new FdClaimWorld.RequestRow(Today, 14000m, 1000m, 310m, 12690m, 12690m, 1m, "policyholder", c.Policy.PartyId, false, "earned_unpaid", FdClaimWorld.Note, a.Id)],
            await f.RequestsAsync(c.Id));
        var request = await f.RequestIdAsync(c.Id);
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request));
        await PostedAsync(c, admin.Id, request);
        var after = await f.LedgerAsync(c);
        Assert.Equal($"/claims/{c.Id}?payment=already", await f.AuthoriseAsync(admin, c.Id, request));
        Assert.Equal(after, await f.LedgerAsync(c));
    }

    [Fact]
    public async Task A_total_loss_approval_failing_at_the_reserve_after_the_cancellation_the_set_off_and_the_payment_rolls_all_back_the_retry_writes_each_once()
    {
        var c = await LossAsync();
        var a = await f.C.UserAsync("accountant");
        var admin = await f.C.UserAsync("admin");
        IsRequested(c, await f.PayAsync(a, c.Id, "14000.00", null, ("total_loss_mode", "earned_unpaid")));
        var request = await f.RequestIdAsync(c.Id);
        var before = await f.LedgerAsync(c);
        await ArmAsync(f.C.Database, "claim_reserve");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => f.AuthoriseAsync(admin, c.Id, request));
        }
        finally
        {
            await DisarmAsync(f.C.Database);
        }
        Assert.Equal(before, await f.LedgerAsync(c));
        Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(admin, c.Id, request));
        await PostedAsync(c, admin.Id, request);
    }
}

/// <summary>
/// The claim cases of fd-net-transfer-chain.test.ts that change the company's threshold or close a month: each on a
/// database of its own (a setting and a closed month are company-wide). The threshold is a setting, not a rule: the
/// test settings 5 000.00 and "10000" are the owner's test matrix (OWNER-TEST-LIMITS-…-20261017).
/// </summary>
public sealed class GapsFdClaimSettingTests
{
    private static async Task WithAsync(Func<FdClaimWorld, Task> body) => await OwnDatabaseAsync(async w =>
    {
        var f = new FdClaimWorld(w.Database);
        await f.RatesAsync();
        await body(f);
    });

    [Fact]
    public async Task The_threshold_is_the_companys_setting_at_5000_a_net_of_6000_is_a_request_10000_without_decimals_works_missing_nothing_written() =>
        await WithAsync(async f =>
        {
            var a = await f.C.UserAsync("accountant");
            await f.W.SettingAsync("payment.fd_threshold_gel", "5000.00");
            var c = await f.ClaimAsync();
            Assert.Equal($"/claims/{c.Id}?payment=requested", (await f.PayAsync(a, c.Id, "7000.00")).RedirectTo);
            Assert.Equal([(7000m, 1000m, 0m, 6000m, 6000m)], (await f.RequestsAsync(c.Id)).Select(r => (r.Amount, r.DeductibleWithheld, r.PremiumOffset, r.NetPaid, r.NetPaidGel)));
            Assert.Empty(await f.PaymentsAsync(c.Id));

            await f.W.SettingAsync("payment.fd_threshold_gel", "10000");
            var whole = await f.ClaimAsync();
            Assert.Equal($"/claims/{whole.Id}?payment=requested", (await f.PayAsync(a, whole.Id, "11000.01")).RedirectTo);
            var below = await f.ClaimAsync();
            Assert.Equal($"/claims/{below.Id}?paid=1", (await f.PayAsync(a, below.Id, "11000.00")).RedirectTo);

            await f.W.SettingAsync("payment.fd_threshold_gel", null);
            var none = await f.ClaimAsync();
            var before = await f.LedgerAsync(none);
            var e = await Assert.ThrowsAnyAsync<Exception>(() => f.PayAsync(a, none.Id, "2000.00"));
            Assert.Contains("payment.fd_threshold_gel", e.Message);
            Assert.Equal(before, await f.LedgerAsync(none));
            Assert.Empty(await f.RequestsAsync(none.Id));
        });

    [Fact]
    public async Task The_owners_test_matrix_at_5000_net_5000_00_gross_6000_is_paid_at_once_net_5000_01_is_a_request() =>
        await WithAsync(async f =>
        {
            await f.W.SettingAsync("payment.fd_threshold_gel", "5000.00");
            var a = await f.C.UserAsync("accountant");
            var at = await f.ClaimAsync();
            Assert.Equal($"/claims/{at.Id}?paid=1", (await f.PayAsync(a, at.Id, "6000.00")).RedirectTo);
            Assert.Equal([(5000m, (Guid?)null)], (await f.PaymentsAsync(at.Id)).Select(p => (p.NetPaidGel, p.RequestId)));
            var above = await f.ClaimAsync();
            Assert.Equal($"/claims/{above.Id}?payment=requested", (await f.PayAsync(a, above.Id, "6000.01")).RedirectTo);
            Assert.Empty(await f.PaymentsAsync(above.Id));
            Assert.Equal([5000.01m], (await f.RequestsAsync(above.Id)).Select(r => r.NetPaidGel));
        });

    [Fact]
    public async Task A_request_for_a_day_of_the_closed_October_is_refused_when_asked_November_closed_after_a_request_period_closed_nothing_written() =>
        await WithAsync(async f =>
        {
            var a = await f.C.UserAsync("accountant");
            var admin = await f.C.UserAsync("admin");
            var october = await f.ClaimAsync();
            var november = await f.ClaimAsync();
            await f.C.CloseMonthAsync(admin.Id, new DateOnly(2026, 10, 1));
            var refused = await f.PayAsync(a, october.Id, "11000.01", new DateOnly(2026, 10, 30));
            Assert.Equal(ClaimTexts.Errors.PeriodClosed, refused.Errors["paid_on"]);
            Assert.Empty(await f.RequestsAsync(october.Id));
            Assert.Equal($"/claims/{november.Id}?payment=requested", (await f.PayAsync(a, november.Id, "11000.01")).RedirectTo);
            await f.C.CloseMonthAsync(admin.Id, new DateOnly(2026, 11, 1));
            var before = await f.LedgerAsync(november);
            Assert.Equal($"/claims/{november.Id}?payment=period-closed", await f.AuthoriseAsync(admin, november.Id, await f.RequestIdAsync(november.Id)));
            Assert.Equal(before, await f.LedgerAsync(november));
        });
}
