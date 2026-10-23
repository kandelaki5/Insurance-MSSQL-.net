using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/fd-panel-db-check.ts with scripts/integration/fd-panel-walk.ts (Codex INS-FD-PANEL-SQL-CHECK; "PREPARED, NOT
/// RUN" in the old program — real evidence here for the first time): the financial director's panel's three lists read by
/// the real reader (FdPanel.ReadPendingQueueAsync) on a real database as core_app. Per list 52 tagged requests (the first 40
/// at one instant, the other 12 a second later; one of 90 071 992 547 408.01): the first page, the request its cursor names
/// carried out by its own link, the pages after that cursor — each tagged request once, in (created_at, id) order, 50 a
/// page; read again — the carried-out one off the list, its 51 siblings once each with their card, receipt, requester and
/// stored GEL exactly; a claim payment's later reversal does not bring its request back; and every read writes nothing.
/// Each list runs in one transaction that is rolled back (the legacy savepoint); afterwards nothing of it stays.
/// </summary>
public sealed class LegacyFdPanelReaderTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private const string Tag = "check-fd-panel";
    private const int Requests = 52;
    private const int SameInstant = 40;
    private const decimal Big = 90071992547408.01m;
    private static readonly string[] Ledgers = ["claim_payment", "payment", "payment_allocation", "claim_reserve", "premium_transaction", "installment_adjustment", "policy", "claim",
        "claim_payment_request", "premium_refund_request", "premium_refund_request_line", "receipt_reversal_request", "receipt_reversal_request_line", "schema_migration"];

    private readonly World w = new(db);

    private static async Task<string> FingerprintAsync(Tx tx)
    {
        var parts = new List<string>();
        foreach (var t in Ledgers) parts.Add(t + ":" + await tx.ExecuteScalarAsync<string?>($"select (select * from core.{t} order by 1 for json path)"));
        return string.Join("\n", parts);
    }

    private async Task<string> CommittedFingerprintAsync()
    {
        await using var c = await db.OpenAdminAsync();
        return await FingerprintAsync(new Tx(c, null));
    }

    /// <summary>A list read page after page from <paramref name="from"/> until no next page (legacy walkQueue); what is wrong with the pages, if anything.</summary>
    private static async Task<(List<List<FdPanel.PendingRequest>> Pages, string? Problem)> WalkAsync(Func<string?, Task<FdPanel.QueueRead>> read, string? from = null)
    {
        var pages = new List<List<FdPanel.PendingRequest>>();
        var after = from;
        for (var n = 0; n < 1000; n++)
        {
            var r = await read(after);
            if (r.State != FdPanel.ReadState.Ok) return (pages, $"page {n + 1}: {r.State}");
            pages.Add([.. r.Rows]);
            if (r.Rows.Count > FdPanel.PageSize) return (pages, $"page {n + 1}: {r.Rows.Count} rows");
            if (r.Next is null) return (pages, null);
            if (r.Rows.Count != FdPanel.PageSize) return (pages, $"page {n + 1}: {r.Rows.Count} rows with a next page");
            if (r.Next != r.Rows[^1].Id) return (pages, $"page {n + 1}: the next page starts after {r.Next}, not after its last row");
            after = r.Next.ToString();
        }
        return (pages, "more than 1000 pages");
    }

    private sealed record Scenario(Func<Tx, int, DateTimeOffset, decimal, Task<Guid>> Ask, Func<Tx, Guid, Task> CarryOut, Func<decimal, (Guid CardId, string CardNumber, Guid? ReceiptId)> Shows,
        Func<Tx, Guid, Task>? Afterwards = null);

    /// <summary>One list, in one transaction as core_app (the admin as the session user), rolled back at the end.</summary>
    private async Task RunAsync(string queue, string table, Guid admin, string requester, Func<Tx, Task<Scenario>> build)
    {
        var before = await CommittedFingerprintAsync();
        await using (var c = await db.OpenAppAsync())
        await using (var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            var tx = new Tx(c, t);
            await tx.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = admin.ToString() });
            await tx.ExecuteAsync("update core.setting set value = N'10000.00' where [key] = N'payment.fd_threshold_gel'");
            var s = await build(tx);
            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            t0 = new DateTimeOffset(t0.Year, t0.Month, t0.Day, t0.Hour, t0.Minute, t0.Second, TimeSpan.Zero);
            var ids = new List<Guid>();
            var gel = new Dictionary<Guid, decimal>();
            for (var n = 0; n < Requests; n++)
            {
                var amount = n == 7 ? Big : 10000.01m;
                var id = await s.Ask(tx, n, n < SameInstant ? t0 : t0.AddSeconds(1), amount);
                ids.Add(id);
                gel[id] = amount;
            }
            var order = (await tx.QueryAsync<Guid>($"""
                select id from core.{table} where id in @ids order by created_at, lower(convert(nchar(36), id)) collate Latin1_General_BIN2
                """, new { ids })).ToList();
            Task<FdPanel.QueueRead> Read(string? after) => FdPanel.ReadPendingQueueAsync(tx, queue, after);
            async Task<(List<List<FdPanel.PendingRequest>> Pages, string? Problem)> Walked(string? from)
            {
                var fp = await FingerprintAsync(tx);
                var walk = await WalkAsync(Read, from);
                Assert.Equal(fp, await FingerprintAsync(tx));
                return walk;
            }
            List<Guid> Tagged(IEnumerable<IEnumerable<FdPanel.PendingRequest>> pages) => [.. pages.SelectMany(p => p).Select(r => r.Id).Where(ids.Contains)];

            var first = await Read(null);
            Assert.Equal(FdPanel.ReadState.Ok, first.State);
            Assert.Equal((FdPanel.PageSize, first.Rows[^1].Id), (first.Rows.Count, first.Next));
            var cursor = first.Next!.Value;
            Assert.Contains(cursor, ids);
            await s.CarryOut(tx, cursor);
            var rest = await Walked(cursor.ToString());
            Assert.Null(rest.Problem);
            Assert.Equal(order, Tagged([first.Rows, .. rest.Pages]));
            var again = await Walked(null);
            Assert.Null(again.Problem);
            Assert.True(again.Pages.Count > 1);
            var siblings = order.Where(id => id != cursor).ToList();
            Assert.Equal(siblings, Tagged(again.Pages));
            foreach (var row in again.Pages.SelectMany(p => p).Where(r => ids.Contains(r.Id)))
            {
                var (card, number, receipt) = s.Shows(gel[row.Id]);
                Assert.Equal((card, number, receipt, requester, gel[row.Id]), (row.CardId, row.CardNumber, row.ReceiptId, row.Requester, row.AmountGel));
            }
            Assert.Equal("90,071,992,547,408.01", FdPanel.AmountText(again.Pages.SelectMany(p => p).Single(r => gel.TryGetValue(r.Id, out var g) && g == Big).AmountGel));
            if (s.Afterwards is { } afterwards)
            {
                await afterwards(tx, cursor);
                var after = await Walked(null);
                Assert.Equal(siblings, Tagged(after.Pages));
            }
            await t.RollbackAsync();
        }
        Assert.Equal(before, await CommittedFingerprintAsync());
        Assert.Equal(0, await w.ScalarAsync<int>("""
            select (select count(*) from core.claim_payment_request where note = @tag) + (select count(*) from core.claim_payment where note = @tag)
                 + (select count(*) from core.premium_refund_request where reference = @tag) + (select count(*) from core.receipt_reversal_request where reason = @tag)
                 + (select count(*) from core.payment where reference = @tag)
            """, new { tag = Tag }));
    }

    private async Task<(CurrentUser Admin, string Name)> AdminAsync()
    {
        var u = await new ClaimWorld(db).UserAsync("admin", "ფინანსური დირექტორის ტესტი");
        return (u, u.FullName);
    }

    [Fact]
    public async Task Claim_payments_paged_in_order_carried_out_off_the_list_and_not_brought_back_by_its_reversal()
    {
        var (admin, name) = await AdminAsync();
        var cw = new ClaimWorld(db);
        var p = await cw.PolicyAsync(admin.Id, World.Today.AddDays(-30), [new ClaimWorld.Line("damage", 50000m, 0m)]);
        var claim = (await cw.RegisterAsync(admin, p, World.Today.AddDays(-2), World.Today.AddDays(-1), ("damage", "1000")))[0];
        var number = await w.ScalarAsync<string>("select number from core.claim where id = @claim", new { claim });
        var today = World.Today;
        var paid = new Dictionary<Guid, Guid>();
        await RunAsync("claim", "claim_payment_request", admin.Id, name, _ => Task.FromResult(new Scenario(
            Ask: async (tx, _, at, gel) =>
            {
                var id = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                            payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_by, created_at)
                    values (@id, @claim, @today, @amount, 1.00, 0.00, @gel, @gel, 1, N'policyholder', @holder, 0, N'none', @tag, @by, @at)
                    """, new { id, claim, today, amount = gel + 1m, gel, holder = p.PartyId, tag = Tag, by = admin.Id, at });
                return id;
            },
            CarryOut: async (tx, id) =>
            {
                var pay = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                    payee_kind, payee_party_id, offset_payment_id, note, created_by, request_id)
                    select @pay, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, null, @tag, @by, id
                    from core.claim_payment_request where id = @id
                    """, new { pay, id, tag = Tag, by = admin.Id });
                paid[id] = pay;
            },
            Shows: _ => (claim, number, null),
            Afterwards: (tx, id) => tx.ExecuteAsync("""
                insert into core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                payee_kind, payee_party_id, offset_payment_id, note, created_by, kind, reverses_claim_payment_id, reversal_reason, returned_on, bank_reference)
                select claim_id, @today, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate,
                       payee_kind, payee_party_id, null, @tag, @by, N'reversal', id, N'correction', null, null
                from core.claim_payment where id = @pay
                """, new { today, tag = Tag, by = admin.Id, pay = paid[id] }))));
    }

    [Fact]
    public async Task Premium_refunds_paged_in_order_and_carried_out_off_the_list()
    {
        var (admin, name) = await AdminAsync();
        var pol = await w.ActivePolicyAsync(admin, await w.ClientAsync(), World.Today.AddDays(-30));
        var inst = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @p order by due_on desc", new { p = pol.Id });
        var today = World.Today;
        await RunAsync("refund", "premium_refund_request", admin.Id, name, _ => Task.FromResult(new Scenario(
            Ask: async (tx, _, at, gel) =>
            {
                var id = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_by, created_at)
                    values (@id, @policy, @today, @gel, N'GEL', 1, N'bank', @tag, @holder, @by, @at);
                    insert into core.premium_refund_request_line (request_id, position, installment_id, amount, amount_gel) values (@id, 1, @inst, @gel, @gel);
                    """, new { id, policy = pol.Id, today, gel, tag = Tag, holder = pol.Holder, by = admin.Id, at, inst });
                return id;
            },
            CarryOut: async (tx, id) =>
            {
                var gel = await tx.ExecuteScalarAsync<decimal>("select amount_gel from core.premium_refund_request where id = @id", new { id });
                var pay = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by, refund_request_id)
                    values (@pay, @today, @neg, N'bank', N'refund', @holder, @tag, @by, @id);
                    insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@pay, @inst, @neg, @neg, 1, @today);
                    """, new { pay, today, neg = -gel, holder = pol.Holder, tag = Tag, by = admin.Id, id, inst });
            },
            Shows: _ => (pol.Id, pol.Number, null))));
    }

    [Fact]
    public async Task Receipt_reversals_paged_in_order_and_carried_out_off_the_list()
    {
        var (admin, name) = await AdminAsync();
        var pol = await w.ActivePolicyAsync(admin, await w.ClientAsync(), World.Today.AddDays(-30));
        var inst = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @p order by due_on desc", new { p = pol.Id });
        var today = World.Today;
        var receipt = Guid.NewGuid();
        await RunAsync("reversal", "receipt_reversal_request", admin.Id, name, async tx =>
        {
            await tx.ExecuteAsync("""
                insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by) values (@receipt, @today, 10000.01, N'bank', N'receipt', @holder, @tag, @by);
                insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @inst, 10000.01, 10000.01, 1, @today);
                """, new { receipt, today, holder = pol.Holder, tag = Tag, by = admin.Id, inst });
            return new Scenario(
                Ask: async (t, _, at, gel) =>
                {
                    var id = Guid.NewGuid();
                    await t.ExecuteAsync("""
                        insert into core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by, created_at)
                        values (@id, @receipt, @policy, @today, @gel, N'bank', @holder, @tag, @by, @at);
                        insert into core.receipt_reversal_request_line (request_id, position, installment_id, amount, amount_gel, rate) values (@id, 1, @inst, 10000.01, 10000.01, 1);
                        """, new { id, receipt, policy = pol.Id, today, gel, holder = pol.Holder, tag = Tag, by = admin.Id, at, inst });
                    return id;
                },
                CarryOut: async (t, id) =>
                {
                    Assert.Equal(10000.01m, await t.ExecuteScalarAsync<decimal>("select amount_gel from core.receipt_reversal_request where id = @id", new { id }));
                    var m = Guid.NewGuid();
                    await t.ExecuteAsync("""
                        insert into core.payment (id, received_on, amount_gel, method, kind, reverses_payment_id, payer_party_id, reference, created_by, reversal_request_id)
                        values (@m, @today, -10000.01, N'bank', N'reversal', @receipt, @holder, @tag, @by, @id);
                        insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@m, @inst, -10000.01, -10000.01, 1, @today);
                        """, new { m, today, receipt, holder = pol.Holder, tag = Tag, by = admin.Id, id, inst });
                },
                Shows: _ => (pol.Id, pol.Number, receipt));
        });
    }
}
