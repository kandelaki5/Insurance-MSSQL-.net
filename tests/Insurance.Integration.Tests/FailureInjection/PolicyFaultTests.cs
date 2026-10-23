using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// The premium receipt's and the policy approval's binding permission check and their permission reads failing once, on the
/// real SQL Server through the command interceptor (legacy scripts/integration/receipt-permission.test.ts and
/// policy-approve-permission.test.ts, "the check that binds is the one in the transaction; a failed read stops it"). The
/// fixtures are World's: a CASCO policy of 365.00 in four instalments of 91.25; a receipt of 150.00.
/// </summary>
public sealed class PolicyFaultTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase db;
    private readonly World w;
    private readonly Faults faults = new();
    private readonly Db app;
    private readonly Rates rates;
    private static readonly DateOnly Start = new(2026, 3, 1);

    public PolicyFaultTests(TestDatabase db)
    {
        this.db = db;
        w = new World(db);
        app = new Db(new DbOptions { ConnectionString = db.AppConnection, Interceptor = faults });
        rates = new Rates(app, new Policies.NoNetwork());
    }

    private void Exec(string sql, object param)
    {
        using var c = new SqlConnection(db.AppConnection);
        c.Open();
        c.Execute(sql, param);
    }

    private Task<int> ReceiptsAsync(Guid holder) => w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder });

    // ================================================================ receipt-permission.test.ts

    private Task<PaymentActions.FormAnswer> ReceiveAsync(CurrentUser who, Guid policyId) =>
        PaymentActions.RecordPaymentAsync(app, rates, who, World.Form(("policy_id", policyId.ToString()), ("amount_gel", "150.00"), ("received_on", ClaimWorld.D(Start.AddDays(6))),
            ("method", "bank"), ("reference", "R-150")));

    [Theory]
    [InlineData("deny")]
    [InlineData("ungrant")]
    public async Task Receipt_the_permission_taken_away_between_the_quick_check_and_the_transaction_refused_nothing_written_the_transaction_kept_nothing(string change)
    {
        var admin = await w.UserAsync();
        var underwriter = await w.UserAsync("underwriter");
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        await app.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, underwriter.Id, "premium.receipt.create", "allow"));
        faults.Reset();
        faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => Exec(change == "deny"
            ? "update core.user_permission set decision = N'deny' where user_id = @u and permission = N'premium.receipt.create'"
            : "delete from core.user_permission where user_id = @u and permission = N'premium.receipt.create'", new { u = underwriter.Id }));
        var answer = await ReceiveAsync(underwriter, p.Id);
        Assert.True(faults.AllFired);
        Assert.Equal((BillingTexts.Payment.NotAllowed, (string?)null), (answer.Message, answer.Redirect));
        Assert.Equal((0, 0m), (await ReceiptsAsync(p.Holder), await w.PaidAsync(p.Id)));
        faults.AssertOnlyThePermissionWasRead(grants: false);
    }

    [Theory]
    [InlineData("own decisions")]
    [InlineData("memberships")]
    public async Task Receipt_the_transactions_read_fails_the_same_error_nothing_written_the_retry_records_it_once(string name)
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        faults.Reset();
        var sentinel = new InjectedFault("RECEIPT_PERMISSION_SENTINEL");
        faults.FailOnce(Faults.Read("binding", name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => ReceiveAsync(accountant, p.Id)));
        Assert.Equal((0, 0m), (await ReceiptsAsync(p.Holder), await w.PaidAsync(p.Id)));
        Assert.Equal($"/policies/{p.Id}?paid=0", (await ReceiveAsync(accountant, p.Id)).Redirect);
        Assert.Equal((1, 150m), (await ReceiptsAsync(p.Holder), await w.PaidAsync(p.Id)));
        Assert.Equal([(Start.AddDays(6), 150m, "bank", "R-150", accountant.Id)], await w.QueryAsync<(DateOnly, decimal, string, string, Guid)>(
            "select received_on, amount_gel, method, reference, created_by from core.payment where payer_party_id = @h", new { h = p.Holder }));
    }

    [Fact]
    public async Task Receipt_the_quick_checks_read_fails_the_same_error_no_transaction_at_all()
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        faults.Reset();
        var sentinel = new InjectedFault("RECEIPT_QUICK_SENTINEL");
        faults.FailOnce(Faults.Read("quick", "own decisions"), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => ReceiveAsync(accountant, p.Id)));
        Assert.False(faults.BegunATransaction);
        Assert.Equal(0, await ReceiptsAsync(p.Holder));
    }

    // ================================================================ policy-approve-permission.test.ts

    private async Task<(CurrentUser Sales, Guid Group)> GroupedSalesAsync(CurrentUser admin)
    {
        var sales = await w.UserAsync("sales");
        var group = (await w.App.AsUserAsync(admin.Id, tx => PermissionStore.CreateGroupAsync(tx, "დამმტკიცებლები " + Guid.NewGuid().ToString("N")[..6]))).Id!.Value;
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupGrantAsync(tx, group, "policy.approve", true));
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupMemberAsync(tx, group, sales.Id, true));
        return (sales, group);
    }

    private async Task<string> LedgerAsync(Guid policyId) =>
        await w.ScalarAsync<string>("select concat(status, N'/', approved_by) from core.policy where id = @policyId", new { policyId }) + "/"
        + string.Join(";", (await w.PremiumRowsAsync(policyId)).Select(r => r.ToString())) + "/" + await w.ScheduleTextAsync(policyId);

    [Fact]
    public async Task Policy_approval_between_the_quick_check_and_the_transaction_the_groups_grant_goes_the_transaction_refuses_before_reading_the_policy_writes_nothing()
    {
        var admin = await w.UserAsync();
        var (sales, group) = await GroupedSalesAsync(admin);
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        var pending = await LedgerAsync(p.Id);
        faults.Reset();
        faults.BeforeOnce(Faults.Binding(Faults.Overrides), () => Exec("delete from core.user_group_permission where group_id = @group", new { group }));
        var answer = await PolicyActions.ApprovePolicyAsync(app, sales, p.Id.ToString());
        Assert.True(faults.AllFired);
        Assert.Equal(("forbidden", ""), (answer.Outcome, answer.Number));
        Assert.Equal(pending, await LedgerAsync(p.Id));
        faults.AssertOnlyThePermissionWasRead();
    }

    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Policy_approval_the_checks_read_fails_the_same_error_nothing_written_the_retry_approves_once(string label, string name)
    {
        var admin = await w.UserAsync();
        var (sales, _) = await GroupedSalesAsync(admin);
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        var pending = await LedgerAsync(p.Id);
        faults.Reset();
        var sentinel = new InjectedFault($"APPROVE_{label}_{name}");
        faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PolicyActions.ApprovePolicyAsync(app, sales, p.Id.ToString())));
        Assert.Equal(label == "binding", faults.BegunATransaction);
        Assert.Equal(pending, await LedgerAsync(p.Id));
        var answer = await PolicyActions.ApprovePolicyAsync(app, sales, p.Id.ToString());
        Assert.Equal(("done", p.Number), (answer.Outcome, answer.Number));
        Assert.Equal([("issue", 365m, 365m, Start, Start, Start.AddDays(365))], await w.PremiumRowsAsync(p.Id));
        Assert.Equal(sales.Id, await w.ScalarAsync<Guid>("select approved_by from core.policy where id = @id", new { id = p.Id }));
        Assert.Equal(("already", p.Number), ((await PolicyActions.ApprovePolicyAsync(app, sales, p.Id.ToString())).Outcome, p.Number));
        Assert.Single(await w.PremiumRowsAsync(p.Id));
    }
}
