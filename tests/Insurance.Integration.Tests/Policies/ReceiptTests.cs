using System.Globalization;
using System.Numerics;
using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests.Policies;

/// <summary>The receipt's exact arithmetic (legacy payment/actions.ts amountInTetri and allocateExactly), checked against the legacy function's own answers.</summary>
public sealed class ReceiptArithmeticTests
{
    // Each expected value is the legacy amountInTetri's answer for the same text (run under Node on commit 17c1f1f).
    [Theory]
    [InlineData("1e3", "100000")]
    [InlineData("1e0000", "100")]
    [InlineData("+1e+0001", "1000")]
    [InlineData("1,5", "150")]
    [InlineData("1.5", "150")]
    [InlineData("0.005", "1")]
    [InlineData("0.004", "0")]
    [InlineData("0.015", "2")]
    [InlineData("", null)]
    [InlineData("-5", null)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("0x10", null)]
    [InlineData("1.2.3", null)]
    [InlineData("9999999999999999.99", "999999999999999999")]
    [InlineData("10000000000000000", "1000000000000000000")]
    [InlineData("1e17", "1000000000000000000")]
    [InlineData("1e-3", "0")]
    [InlineData("5e-3", "1")]
    [InlineData("1e99999999999999999999999", "1000000000000000000")]
    [InlineData("1e-99999999999999999999999", "0")]
    [InlineData("+.5", "50")]
    [InlineData(".", null)]
    [InlineData("00012.3400", "1234")]
    [InlineData("1.995", "200")]
    [InlineData("1.994999", "199")]
    [InlineData("12e-1", "120")]
    [InlineData("365.00", "36500")]
    [InlineData("1E2", "10000")]
    [InlineData("1e", null)]
    [InlineData("e5", null)]
    [InlineData(" 1", null)]
    [InlineData("1 ", null)]
    [InlineData("١", null)]
    public void Amount_in_tetri_reads_exactly_what_the_legacy_function_read(string text, string? expected) =>
        Assert.Equal(expected, PaymentActions.AmountInTetri(text)?.ToString(CultureInfo.InvariantCulture));

    private static PaymentActions.ExactBalance B(int day, string outstanding) =>
        new(Guid.NewGuid(), new DateOnly(2026, 1, 1).AddDays(day), BigInteger.Parse(outstanding, CultureInfo.InvariantCulture));

    [Fact]
    public void Allocation_is_oldest_first_and_the_last_line_takes_the_rounding()
    {
        // 135.02 GEL at 2.7 = 50.0074 → 5001 cents: 2000 (54.00 GEL) and 3001 (the rest, 81.02).
        var b = new[] { B(31, "10000"), B(0, "2000") };
        var r = PaymentActions.AllocateExactly(13502, "USD", b, 2.7m);
        Assert.Equal([(b[1].Id, (BigInteger)2000, (BigInteger)5400), (b[0].Id, (BigInteger)3001, (BigInteger)8102)], r.Lines.Select(l => (l.InstallmentId, l.Minor, l.Gel)));
        Assert.Equal(BigInteger.Zero, r.UnallocatedGel);
        // More than owed: what is left stays unallocated, no line takes a remainder.
        var over = PaymentActions.AllocateExactly(50000, "GEL", [B(0, "9125"), B(91, "0")], 1m);
        Assert.Equal([(BigInteger)9125], over.Lines.Select(l => l.Gel));
        Assert.Equal((BigInteger)40875, over.UnallocatedGel);
        // numeric(18, 2)'s largest amounts keep their last tetri.
        var max = BigInteger.Parse("999999999999999999", CultureInfo.InvariantCulture);
        var big = PaymentActions.AllocateExactly(max, "GEL", [B(0, "999999999999999999")], 1m);
        Assert.Equal(max, big.Lines.Single().Gel);
        Assert.Throws<ArgumentException>(() => PaymentActions.AllocateExactly(0, "GEL", [B(0, "1")], 1m));
        Assert.Throws<InvalidOperationException>(() => PaymentActions.AllocateExactly(100, "USD", [B(0, "1")], 0m));
    }

    [Fact]
    public void Card_figures_are_exact_on_the_largest_amounts()
    {
        Assert.Equal("90,071,992,547,408.02", PolicyCard.Grouped(BigInteger.Parse("9007199254740801", CultureInfo.InvariantCulture) + 1));
        Assert.Equal("9,999,999,999,999,999.99", PolicyCard.Stored(9999999999999999.99m));
        Assert.Equal("-0.05", PolicyCard.Grouped(-5));
        Assert.Null(PolicyCard.TetriParam("1e3"));
        Assert.Null(PolicyCard.TetriParam("-1"));
        Assert.Null(PolicyCard.TetriParam("NaN"));
        Assert.Null(PolicyCard.TetriParam(""));
        Assert.Null(PolicyCard.TetriParam(new string('9', 31)));
        Assert.Equal(BigInteger.Parse("999999999999999999", CultureInfo.InvariantCulture), PolicyCard.TetriParam("999999999999999999"));
    }
}

/// <summary>
/// Legacy scripts/integration/receipt-permission.test.ts and payment-regression's form checks: a new receipt needs
/// premium.receipt.create (the accountant and the admin by role; a group or a personal grant; a personal removal takes
/// it away), the form's refusals, a USD policy at the receipt day's rate, the suspension check.
/// </summary>
public sealed class ReceiptTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 3, 1);

    [Theory]
    [InlineData("accountant", true)]
    [InlineData("admin", true)]
    [InlineData("operations_head", false)]
    [InlineData("underwriter", false)]
    [InlineData("sales", false)]
    [InlineData("claims", false)]
    [InlineData("viewer", false)]
    public async Task The_roles_starting_set(string role, bool may)
    {
        var admin = await w.UserAsync();
        var user = await w.UserAsync(role);
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        var r = await w.PayAsync(user, p.Id, "150.00", Start.AddDays(2));
        if (may) Assert.Equal($"/policies/{p.Id}?paid=0", r.Redirect);
        else Assert.Equal((BillingTexts.Payment.NotAllowed, null), (r.Message, r.Redirect));
        Assert.Equal(may ? 150m : 0m, await w.PaidAsync(p.Id));
    }

    [Fact]
    public async Task Groups_personal_grants_and_removals()
    {
        var admin = await w.UserAsync();
        var underwriter = await w.UserAsync("underwriter");
        var viewer = await w.UserAsync("viewer");
        var accountant = await w.UserAsync("accountant");
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        var group = (await w.App.AsUserAsync(admin.Id, tx => PermissionStore.CreateGroupAsync(tx, "მოლარეები " + Guid.NewGuid().ToString("N")[..6]))).Id!.Value;
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupGrantAsync(tx, group, "premium.receipt.create", true));
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupMemberAsync(tx, group, underwriter.Id, true));
        Assert.NotNull((await w.PayAsync(underwriter, p.Id, "10.00", Start)).Redirect);
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, viewer.Id, "premium.receipt.create", "allow"));
        Assert.NotNull((await w.PayAsync(viewer, p.Id, "150.00", Start)).Redirect);
        Assert.Equal(160m, await w.PaidAsync(p.Id));
        // Out of the group: the next receipt is refused.
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupMemberAsync(tx, group, underwriter.Id, false));
        Assert.Equal(BillingTexts.Payment.NotAllowed, (await w.PayAsync(underwriter, p.Id, "10.00", Start)).Message);
        // The accountant's own removal shuts it; inherit opens it again.
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, accountant.Id, "premium.receipt.create", "deny"));
        Assert.Equal(BillingTexts.Payment.NotAllowed, (await w.PayAsync(accountant, p.Id, "10.00", Start)).Message);
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, accountant.Id, "premium.receipt.create", "inherit"));
        Assert.NotNull((await w.PayAsync(accountant, p.Id, "10.00", Start)).Redirect);
        Assert.Equal(170m, await w.PaidAsync(p.Id));
    }

    [Fact]
    public async Task The_forms_refusals_name_their_fields_and_write_nothing()
    {
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        var bad = await w.PayAsync(admin, p.Id, "-5", World.Today.AddDays(1), "cheque");
        Assert.Equal(BillingTexts.Payment.Errors.Amount, bad.Errors["amount_gel"]);
        Assert.Equal(BillingTexts.Payment.Errors.Date, bad.Errors["received_on"]);
        Assert.Equal(BillingTexts.Payment.Errors.Method, bad.Errors["method"]);
        Assert.Equal(BillingTexts.Payment.Errors.Amount, (await w.PayAsync(admin, p.Id, "10000000000000000", Start)).Errors["amount_gel"]);
        Assert.Equal(BillingTexts.Payment.Errors.Amount, (await w.PayAsync(admin, p.Id, "0.004", Start)).Errors["amount_gel"]);
        Assert.Equal(BillingTexts.Payment.NotPayable, (await PaymentActions.RecordPaymentAsync(w.App, w.Rates, admin, World.Form(("policy_id", "x")))).Message);
        Assert.Equal(BillingTexts.Payment.NotPayable, (await w.PayAsync(admin, Guid.NewGuid(), "1", Start)).Message);
        Assert.Equal(0m, await w.PaidAsync(p.Id));
        // Nothing owed any more: refused, no receipt.
        await w.PayAsync(admin, p.Id, "365", Start);
        Assert.Equal(BillingTexts.Payment.Errors.NothingOwed, (await w.PayAsync(admin, p.Id, "1", Start)).Message);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @h", new { h = p.Holder }));
        // More than owed: the rest stays an advance (paid=…).
        var q = await w.ActivePolicyAsync(admin, p.Holder, Start);
        Assert.Equal($"/policies/{q.Id}?paid=13500", (await w.PayAsync(admin, q.Id, "500", Start)).Redirect);
    }

    [Fact]
    public async Task A_usd_policy_takes_a_gel_receipt_at_the_receipt_days_rate_and_a_day_without_a_rate_is_refused()
    {
        var admin = await w.UserAsync();
        await w.RateAsync("USD", Start, 2.7m);
        await w.RateAsync("USD", Start.AddDays(3), 2.8m);
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start, "USD", 2.7m);
        Assert.Equal($"/policies/{p.Id}?paid=0", (await w.PayAsync(admin, p.Id, "280.00", Start.AddDays(3))).Redirect);
        var lines = await w.QueryAsync<(decimal Amount, decimal AmountGel, decimal Rate)>("select a.amount, a.amount_gel, a.rate from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @p", new { p = p.Id });
        Assert.Equal([(91.25m, 255.50m, 2.8m), (8.75m, 24.50m, 2.8m)], lines.OrderByDescending(l => l.Amount));
        Assert.Equal(BillingTexts.Payment.Errors.Rate, (await w.PayAsync(admin, p.Id, "10", Start.AddDays(5))).Errors["received_on"]);
    }

    [Fact]
    public async Task A_suspension_blocks_a_receipt_of_the_client_or_of_the_suspended_policy_or_number()
    {
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var p = await w.ActivePolicyAsync(admin, holder, Start);
        var q = await w.ActivePolicyAsync(admin, holder, Start);
        var all = await w.SuspendAsync(holder, admin.Id);
        Assert.Equal(AmlTexts.Suspension.Blocked, (await w.PayAsync(admin, p.Id, "10", Start)).Message);
        await w.LiftAsync(all);
        await w.SuspendAsync(holder, admin.Id, policyId: q.Id);
        Assert.Equal(AmlTexts.Suspension.Blocked, (await w.PayAsync(admin, q.Id, "10", Start)).Message);
        Assert.NotNull((await w.PayAsync(admin, p.Id, "10", Start)).Redirect);
        // A transaction suspension given by the policy's number is attached to that policy by the database.
        await w.App.AsUserAsync(admin.Id, async tx =>
        {
            await Aml.LockForSuspensionChangeAsync(tx, holder);
            return await tx.ExecuteAsync("""
                insert core.fms_suspension (party_id, kind, scope, transaction_ref, instructed_at, ends_at, target_policy_id)
                values (@holder, N'written', N'transaction', @number, sysdatetimeoffset(), dateadd(hour, 72, sysdatetimeoffset()), @policy)
                """, new { holder, number = p.Number, policy = p.Id });
        });
        Assert.Equal(AmlTexts.Suspension.Blocked, (await w.PayAsync(admin, p.Id, "10", Start)).Message);
        Assert.Equal(10m, await w.PaidAsync(p.Id));
    }
}
