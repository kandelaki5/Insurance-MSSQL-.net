using Dapper;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The time a row is stamped with is its transaction's (0009, core.tx_now()), as PostgreSQL's now() was: rows written
/// together share created_at, so the legacy reads that order by created_at and then by a second key see the legacy order.
/// </summary>
public sealed class TransactionTimeTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static string OrgTaxId() => (100_000_000 + Random.Shared.Next(899_999_999)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Rows_of_one_transaction_share_its_time_and_the_session_forgets_it_afterwards()
    {
        var user = await w.UserAsync("sales");
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await db.App.AsUserAsync(user.Id, async tx =>
        {
            await tx.ExecuteAsync("insert core.party (id, kind, name, tax_id) values (@a, N'organization', N'შპს ერთი', @t)", new { a, t = OrgTaxId() });
            await tx.ExecuteAsync("waitfor delay '00:00:00.020'");
            await tx.ExecuteAsync("insert core.party (id, kind, name, tax_id) values (@b, N'organization', N'შპს ორი', @t)", new { b, t = OrgTaxId() });
            return 0;
        });
        var stamps = await w.QueryAsync<DateTimeOffset>("select created_at from core.party where id in (@a, @b)", new { a, b });
        Assert.Single(stamps.Distinct());
        // The application's next reads and writes on the same pooled connection see no transaction time left behind.
        Assert.Null(await db.App.ReadAsync(c => c.ExecuteScalarAsync<object?>("select session_context(N'core.tx_at')")));
        // Two transactions: two times.
        var c2 = Guid.NewGuid();
        await Task.Delay(20);
        await db.App.AsUserAsync(user.Id, tx => tx.ExecuteAsync("insert core.party (id, kind, name, tax_id) values (@c2, N'organization', N'შპს სამი', @t)", new { c2, t = OrgTaxId() }));
        Assert.True(await w.ScalarAsync<DateTimeOffset>("select created_at from core.party where id = @c2", new { c2 }) > stamps[0]);
    }

    [Fact]
    public async Task A_receipt_allocated_in_one_transaction_gives_its_lines_in_the_legacy_order_created_at_then_amount_gel()
    {
        var head = await w.UserAsync("operations_head");
        var holder = await w.ClientAsync();
        var from = World.Today.AddDays(-5);
        var a = await w.ActivePolicyAsync(head, holder, from, premium: 28000m);
        var b = await w.ActivePolicyAsync(head, holder, from, premium: 20000m);
        var instA = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @id order by due_on", new { id = a.Id });
        var instB = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @id order by due_on", new { id = b.Id });
        var receipt = Guid.NewGuid();
        var accountant = await w.UserAsync("accountant");
        // As recordPayment writes them: the receipt, then its lines one statement after another, the larger first.
        await db.App.AsUserAsync(accountant.Id, async tx =>
        {
            await tx.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, payer_party_id) values (@receipt, @on, 12000.00, N'bank', @holder)",
                new { receipt, on = World.Today, holder });
            await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @instA, 7000.00, 7000.00, 1, @on)",
                new { receipt, instA, on = World.Today });
            await tx.ExecuteAsync("waitfor delay '00:00:00.020'");
            await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @instB, 5000.00, 5000.00, 1, @on)",
                new { receipt, instB, on = World.Today });
            return 0;
        });
        // Legacy: one transaction, one now(); created_at ties and amount_gel decides — 5 000 before 7 000. With each
        // statement's own time (before 0009) the 7 000 written first came first.
        var lines = await db.App.ReadTxAsync(tx => ReceiptReversal.ReceiptLinesAsync(tx, receipt));
        Assert.Equal([(instB, 5000.00m), (instA, 7000.00m)], lines.Select(l => (l.InstallmentId, l.AmountGel)));
    }
}
