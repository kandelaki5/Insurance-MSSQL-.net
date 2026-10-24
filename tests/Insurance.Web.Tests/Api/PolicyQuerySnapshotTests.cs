using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Infrastructure.Policies;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.FailureInjection;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Web.Tests.Api;

/// <summary>
/// Codex R006-03, with two sessions: the list's and the card's several reads are one committed state. A second session commits between
/// two of the query's statements (the test's interceptor runs it just before the later one is sent): a new policy between the list's
/// count and its rows, a receipt allocated to the policy between the card's payments and its allocations. The answer describes the state
/// before that commit, whole — never a total of one state with the rows of another, or a receivable that counts a payment the card's
/// payments do not show — and the next read describes the state after it.
/// </summary>
public sealed class PolicyQuerySnapshotTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    private (Db Db, Faults Faults) Intercepted()
    {
        var faults = new Faults();
        return (new Db(new DbOptions { ConnectionString = db.AppConnection, Clock = db.Clock, Interceptor = faults }), faults);
    }

    [Fact]
    public async Task A_policy_committed_between_the_lists_count_and_its_rows_is_in_neither_and_the_next_read_has_it_in_both()
    {
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        var from = World.Today.AddDays(-10);
        await w.ActivePolicyAsync(await w.UserAsync("operations_head"), holder, from);
        var (d, faults) = Intercepted();
        faults.BeforeOnce(c => c.Sql.Contains("offset @offset rows", StringComparison.Ordinal),
            () => w.PendingPolicyAsync(holder, from).GetAwaiter().GetResult());
        var query = new PolicyListQuery(d);
        var list = await query.ListAsync(new(taxId));
        Assert.True(faults.AllFired);
        Assert.Equal((1, 1), (list.Total, list.Items.Count));
        var after = await query.ListAsync(new(taxId));
        Assert.Equal((2, 2), (after.Total, after.Items.Count));
    }

    [Fact]
    public async Task A_receipt_allocated_between_the_cards_payments_and_its_allocations_is_in_neither_and_the_next_read_has_it_in_both()
    {
        var holder = await w.ClientAsync();
        var from = World.Today.AddDays(-10);
        var p = await w.ActivePolicyAsync(await w.UserAsync("operations_head"), holder, from);
        var admin = await w.UserAsync("admin");
        var (d, faults) = Intercepted();
        faults.BeforeOnce(c => c.Sql.Contains("select a.amount as Amount, p.received_on as ReceivedOn, p.method as Method", StringComparison.Ordinal), () =>
        {
            var receipt = w.ReceiptAsync(holder, from.AddDays(1), 100m).GetAwaiter().GetResult();
            Assert.NotNull(PaymentActions.AllocateReceiptAsync(w.App, w.Rates, admin, p.Id.ToString(), receipt.ToString()).GetAwaiter().GetResult());
        });
        var cards = new PolicyCardQuery(d, w.Rates);
        var card = (await cards.ReadAsync(p.Id))!;
        Assert.True(faults.AllFired);
        Assert.Empty(card.Payments);
        Assert.Equal(36500, (long)card.Receivable.Outstanding);
        var after = (await cards.ReadAsync(p.Id))!;
        Assert.Single(after.Payments);
        Assert.Equal(26500, (long)after.Receivable.Outstanding);
    }
}
