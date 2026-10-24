using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/fd-concurrency-db-check.ts (race-engine.ts) and scripts/fd-app-concurrency-db-check.ts (app-race.ts), both
/// "PREPARED, NOT RUN" in the old program — real evidence here for the first time: the financial director's requests carried
/// out twice at the same moment on a real database. Two sessions of their own, the second SEEN waiting on the first by a
/// monitor connection (sys.dm_exec_requests) — never assumed from a sleep —; the first commits or is rolled back; exactly
/// one carried-out row, the winner's, with its link, amounts, author and allocations; the loser's nowhere; the change log
/// exactly the race's own events by their author, its older rows unchanged. Each race on its own fresh client, policy,
/// claim and receipt in this class's own database (the legacy check needed a throwaway copy for the committed rows).
/// </summary>
public sealed class LegacyFdRaceTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private readonly ClaimWorld cw = new(db);

    private async Task<(CurrentUser A, CurrentUser B)> UsersAsync() => (await cw.UserAsync("admin", "ფინანსური დირექტორი A"), await cw.UserAsync("admin", "ფინანსური დირექტორი B"));

    private async Task<long> AuditMarkAsync() => await w.ScalarAsync<long?>("select max(id) from core.audit_log") ?? 0;

    private async Task<string> AuditUpToAsync(long mark)
    {
        await using var c = await db.OpenAdminAsync();
        return await c.ExecuteScalarAsync<string?>("select (select * from core.audit_log where id <= @mark order by id for json path)", new { mark }) ?? "";
    }

    private Task<List<(string Table, string Row, string Action, Guid? User)>> AuditSinceAsync(long mark) =>
        w.QueryAsync<(string, string, string, Guid?)>("select table_name, row_id, action, user_id from core.audit_log where id > @mark order by id", new { mark });

    private static string? Code(Exception? e) => e is null ? null : DbError.From(e) is { } d ? d.Code + (d.Constraint is null ? "" : " " + d.Constraint) : e.GetType().Name + " " + e.Message;

    // ================================================================ the rows that carry the link (race-engine.ts)

    /// <summary>A session as core_app: its own connection and transaction, the user set.</summary>
    private async Task<(SqlConnection C, SqlTransaction T, int Session)> SessionAsync(Guid user)
    {
        var c = await db.OpenAppAsync();
        var session = await c.ExecuteScalarAsync<int>("select @@spid");
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = user.ToString() }, t);
        return (c, t, session);
    }

    private sealed record Write(string Sql, object Param);

    private sealed record Race(string Label, IReadOnlyList<Write> Setup, Func<Guid, IReadOnlyList<Write>> Rows, string[] Conflict,
        string LinkedSql, Guid LinkedValue, Func<char, Guid> WinnerId, string? Allocated, IReadOnlyList<(string Table, Guid Row)> SetupAudited);

    private async Task RunRaceAsync(Race r, bool commit, CurrentUser a, CurrentUser b)
    {
        var mark = await AuditMarkAsync();
        var oldAudit = await AuditUpToAsync(mark);
        await w.App.AsUserAsync(a.Id, async tx => { foreach (var s in r.Setup) await tx.ExecuteAsync(s.Sql, s.Param); return 0; });
        await using var overlap = new Overlap(db);
        var (ca, ta, sa) = await SessionAsync(a.Id);
        var (cb, tb, sb) = await SessionAsync(b.Id);
        try
        {
            foreach (var s in r.Rows(a.Id)) await ca.ExecuteAsync(s.Sql, s.Param, ta);
            var bWrite = Task.Run(async () => { foreach (var s in r.Rows(b.Id)) await cb.ExecuteAsync(s.Sql, s.Param, tb); });
            Assert.True(await overlap.SeenBlockedAsync(sb, sa, bWrite), $"{r.Label}: B is not seen waiting on A");
            if (commit) await ta.CommitAsync(); else await ta.RollbackAsync();
            var error = await Record.ExceptionAsync(() => bWrite.WaitAsync(TimeSpan.FromSeconds(20)));
            if (commit)
            {
                var code = Code(error);
                Assert.True(r.Conflict.Any(x => code == $"23505 {x}"), $"{r.Label}: after A's commit B met {code ?? "nothing"}, expected 23505 on {string.Join(" or ", r.Conflict)}");
                await tb.RollbackAsync();
            }
            else
            {
                Assert.Null(error);
                await tb.CommitAsync();
            }
        }
        finally
        {
            foreach (var (c, t) in new[] { (ca, ta), (cb, tb) })
            {
                try { if (t.Connection is not null) await t.RollbackAsync(); } catch (InvalidOperationException) { }
                await c.DisposeAsync();
            }
        }
        var winner = commit ? 'A' : 'B';
        var winnerUser = commit ? a.Id : b.Id;
        var rows = await w.QueryAsync<(Guid Id, Guid? CreatedBy)>(r.LinkedSql, new { value = r.LinkedValue });
        Assert.Equal([(r.WinnerId(winner), (Guid?)winnerUser)], rows);
        var loser = r.WinnerId(commit ? 'B' : 'A');
        Assert.Equal(0, await w.ScalarAsync<int>("select (select count(*) from core.claim_payment where id = @loser) + (select count(*) from core.payment where id = @loser)", new { loser }));
        if (r.Allocated is { } allocated)
        {
            var lines = await w.QueryAsync<(Guid Id, decimal Amount, decimal Gel, decimal Rate, DateOnly BookedOn, Guid? By)>(
                "select id, amount, amount_gel, rate, booked_on, created_by from core.payment_allocation where payment_id = @id", new { id = r.WinnerId(winner) });
            var want = decimal.Parse(allocated, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal([(AllocationId(r.WinnerId(winner)), want, want, 1m, World.Today, (Guid?)winnerUser)], lines);
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment_allocation where payment_id = @loser", new { loser }));
        }
        // The change log: exactly the setup's request rows, inserted by A's user, and none else; its older rows as they were.
        var events = await AuditSinceAsync(mark);
        Assert.Equal(r.SetupAudited.Select(x => (x.Table, x.Row.ToString(), "insert", (Guid?)a.Id)).OrderBy(x => x.Item2), events.OrderBy(x => x.Row));
        Assert.Equal(oldAudit, await AuditUpToAsync(mark));
    }

    /// <summary>Each race's allocation id is derived from its payment's (so the read back can name it).</summary>
    private static Guid AllocationId(Guid payment) { var b = payment.ToByteArray(); b[0] ^= 0x5a; return new Guid(b); }

    // Each race is built per run: fresh ids and rows of its own.
    private async Task<Race> ClaimRaceAsync(CurrentUser a, CurrentUser b)
    {
        var today = World.Today;
        var p = await cw.PolicyAsync(a.Id, today.AddDays(-30), [new ClaimWorld.Line("damage", 50000m, 0m)]);
        var claim = (await cw.RegisterAsync(a, p, today.AddDays(-2), today.AddDays(-1), ("damage", "1000")))[0];
        var request = Guid.NewGuid();
        var pay = new Dictionary<char, Guid> { ['A'] = Guid.NewGuid(), ['B'] = Guid.NewGuid() };
        return new Race("claim payment",
            [new Write("""
                insert into core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                        payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_by)
                values (@request, @claim, @today, 10001.01, 1.00, 0.00, 10000.01, 10000.01, 1, N'policyholder', @holder, 0, N'none', N'check-fd-concurrency', @by)
                """, new { request, claim, today, holder = p.PartyId, by = a.Id })],
            user => [new Write("""
                insert into core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                payee_kind, payee_party_id, offset_payment_id, note, created_by, kind, request_id)
                values (@pid, @claim, @today, 10001.01, 1.00, 0.00, 10000.01, 10000.01, 1, N'policyholder', @holder, null, N'check-fd-concurrency', @user, N'payment', @request)
                """, new { pid = pay[user == a.Id ? 'A' : 'B'], claim, today, holder = p.PartyId, user, request })],
            ["claim_payment_request_once"], "select id, created_by from core.claim_payment where request_id = @value", request, x => pay[x], null,
            [("claim_payment_request", request)]);
    }

    private async Task<(World.Policy Policy, Guid Installment)> LineAsync(CurrentUser a)
    {
        var pol = await w.ActivePolicyAsync(a, await w.ClientAsync(), World.Today.AddDays(-30));
        return (pol, await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @p order by due_on desc", new { p = pol.Id }));
    }

    private async Task<Race> RefundRaceAsync(CurrentUser a)
    {
        var (pol, inst) = await LineAsync(a);
        var today = World.Today;
        var request = Guid.NewGuid();
        var line = Guid.NewGuid();
        var pay = new Dictionary<char, Guid> { ['A'] = Guid.NewGuid(), ['B'] = Guid.NewGuid() };
        return new Race("premium refund",
            [new Write("""
                insert into core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_by)
                values (@request, @policy, @today, 10000.01, N'GEL', 1, N'bank', N'check-fd-concurrency', @holder, @by);
                insert into core.premium_refund_request_line (id, request_id, position, installment_id, amount, amount_gel) values (@line, @request, 1, @inst, 10000.01, 10000.01);
                """, new { request, policy = pol.Id, today, holder = pol.Holder, by = a.Id, line, inst })],
            user =>
            {
                var pid = pay[user == a.Id ? 'A' : 'B'];
                return [new Write("""
                    insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by, refund_request_id)
                    values (@pid, @today, -10000.01, N'bank', N'refund', @holder, N'check-fd-concurrency', @user, @request);
                    insert into core.payment_allocation (id, payment_id, installment_id, amount, amount_gel, rate, booked_on, created_by)
                    values (@aid, @pid, @inst, -10000.01, -10000.01, 1, @today, @user);
                    """, new { pid, aid = AllocationId(pid), today, holder = pol.Holder, user, request, inst })];
            },
            ["payment_refund_request_once"], "select id, created_by from core.payment where refund_request_id = @value", request, x => pay[x], "-10000.01",
            [("premium_refund_request", request), ("premium_refund_request_line", line)]);
    }

    private async Task<Race> ReversalRaceAsync(CurrentUser a, bool twoRequests)
    {
        var (pol, inst) = await LineAsync(a);
        var today = World.Today;
        var receipt = Guid.NewGuid();
        var requests = new Dictionary<char, Guid> { ['A'] = Guid.NewGuid(), ['B'] = Guid.NewGuid() };
        if (!twoRequests) requests['B'] = requests['A'];
        var lines = new Dictionary<char, Guid> { ['A'] = Guid.NewGuid(), ['B'] = Guid.NewGuid() };
        var pay = new Dictionary<char, Guid> { ['A'] = Guid.NewGuid(), ['B'] = Guid.NewGuid() };
        Write Request(char k) => new("""
            insert into core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by)
            values (@request, @receipt, @policy, @today, 10000.01, N'bank', @holder, N'check-fd-concurrency', @by);
            insert into core.receipt_reversal_request_line (id, request_id, position, installment_id, amount, amount_gel, rate) values (@line, @request, 1, @inst, 10000.01, 10000.01, 1);
            """, new { request = requests[k], receipt, policy = pol.Id, today, holder = pol.Holder, by = a.Id, line = lines[k], inst });
        var setup = new List<Write>
        {
            new("""
                insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by) values (@receipt, @today, 10000.01, N'bank', N'receipt', @holder, N'check-fd-concurrency', @by);
                insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on, created_by) values (@receipt, @inst, 10000.01, 10000.01, 1, @today, @by);
                """, new { receipt, today, holder = pol.Holder, by = a.Id, inst }),
            Request('A'),
        };
        var audited = new List<(string, Guid)> { ("receipt_reversal_request", requests['A']), ("receipt_reversal_request_line", lines['A']) };
        if (twoRequests) { setup.Add(Request('B')); audited.Add(("receipt_reversal_request", requests['B'])); audited.Add(("receipt_reversal_request_line", lines['B'])); }
        return new Race(twoRequests ? "receipt reversal, two requests on one receipt" : "receipt reversal, one request twice", setup,
            user =>
            {
                var k = user == a.Id ? 'A' : 'B';
                return [new Write("""
                    insert into core.payment (id, received_on, amount_gel, method, kind, reverses_payment_id, payer_party_id, reference, created_by, reversal_request_id)
                    values (@pid, @today, -10000.01, N'bank', N'reversal', @receipt, @holder, N'check-fd-concurrency', @user, @request);
                    insert into core.payment_allocation (id, payment_id, installment_id, amount, amount_gel, rate, booked_on, created_by)
                    values (@aid, @pid, @inst, -10000.01, -10000.01, 1, @today, @user);
                    """, new { pid = pay[k], aid = AllocationId(pay[k]), today, receipt, holder = pol.Holder, user, request = requests[k], inst })];
            },
            // One request twice: B repeats both the original (0010) and the request (0038) — which index SQL Server names first is its own order.
            twoRequests ? ["payment_reversal_once"] : ["payment_reversal_once", "payment_reversal_request_once"],
            "select id, created_by from core.payment where reverses_payment_id = @value", receipt, x => pay[x], "-10000.01", audited);
    }

    [Theory]
    [InlineData("claim", true)]
    [InlineData("claim", false)]
    [InlineData("refund", true)]
    [InlineData("refund", false)]
    [InlineData("reversal-one", true)]
    [InlineData("reversal-one", false)]
    [InlineData("reversal-two", true)]
    [InlineData("reversal-two", false)]
    public async Task The_row_that_carries_the_link_written_twice_at_once_is_written_once(string race, bool commit)
    {
        var (a, b) = await UsersAsync();
        var r = race switch
        {
            "claim" => await ClaimRaceAsync(a, b),
            "refund" => await RefundRaceAsync(a),
            "reversal-one" => await ReversalRaceAsync(a, false),
            _ => await ReversalRaceAsync(a, true),
        };
        await RunRaceAsync(r, commit, a, b);
    }

    // ================================================================ the approvals themselves (app-race.ts)

    /// <summary>
    /// The one place the harness acts, as the legacy child's stand-in at the transaction's end: a test-only trigger on the
    /// table the approval writes last holds the transaction of the user named in dbo.test_hold there — with every lock the
    /// approval took — until the test releases it, and then lets it commit or throws to roll it back (HarnessRollback). Other
    /// users pass. Nothing of the business code is changed; the trigger and the table live in this test database only.
    /// </summary>
    private async Task ArmHoldAsync(string table)
    {
        await cw.AdminAsync("if object_id(N'dbo.test_hold') is null create table dbo.test_hold (table_name sysname not null, user_id nvarchar(36) not null, mode nvarchar(10) not null)");
        await cw.AdminAsync("grant select on dbo.test_hold to core_app");
        await cw.AdminAsync($"""
            create or alter trigger core.test_hold_{table} on core.{table} after insert, update as
            begin
              set nocount on;
              declare @actor nvarchar(36) = lower(convert(nvarchar(36), session_context(N'core.user_id')));
              if not exists (select 1 from dbo.test_hold where table_name = N'{table}' and user_id = @actor) return;
              declare @r int;
              exec @r = sp_getapplock @Resource = N'test_hold', @LockMode = N'Shared', @LockOwner = N'Transaction', @LockTimeout = 60000;
              if exists (select 1 from dbo.test_hold where table_name = N'{table}' and user_id = @actor and mode = N'rollback')
                throw 50998, N'HarnessRollback: the test rolls A back at its transaction''s end', 1;
            end
            """);
    }

    private static string Answer(Task<string?> t) =>
        t.IsCompletedSuccessfully ? $"redirect {t.Result}" : t.Exception?.InnerException is { } e && e.Message.StartsWith("HarnessRollback", StringComparison.Ordinal) ? "thrown HarnessRollback" : $"thrown {t.Exception?.InnerException?.Message}";

    /// <summary>A held at its last write; B started and seen waiting on A (in the application's own lock); A committed or rolled back; both answers.</summary>
    private async Task<(string A, string B)> AppRaceAsync(string table, CurrentUser a, bool commit, Func<Task<string?>> runA, Func<Task<string?>> runB)
    {
        await ArmHoldAsync(table);
        await cw.AdminAsync("delete from dbo.test_hold; insert dbo.test_hold (table_name, user_id, mode) values (@table, @user, N'commit')", new { table, user = a.Id.ToString().ToLowerInvariant() });
        await using var overlap = new Overlap(db);
        await using var harness = await db.OpenAdminAsync();
        var harnessSession = await harness.ExecuteScalarAsync<int>("select @@spid");
        await harness.ExecuteAsync("exec sp_getapplock @Resource = N'test_hold', @LockMode = N'Exclusive', @LockOwner = N'Session'");
        try
        {
            var taskA = runA();
            var sessionA = await overlap.NewWaiterAsync(harnessSession, new HashSet<int>(), taskA);
            Assert.True(sessionA != 0, "A's approval is not held at the end of its transaction");
            var taskB = runB();
            var sessionB = await overlap.NewWaiterAsync(harnessSession, new HashSet<int> { sessionA }, taskB);
            Assert.True(sessionB != 0 && await overlap.SeenBlockedAsync(sessionB, sessionA, taskB), "B's approval is not seen waiting on A");
            await cw.AdminAsync("update dbo.test_hold set mode = @mode", new { mode = commit ? "commit" : "rollback" });
            await harness.ExecuteAsync("exec sp_releaseapplock @Resource = N'test_hold', @LockOwner = N'Session'");
            await Task.WhenAny(Task.WhenAll(taskA, taskB), Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(taskA.IsCompleted && taskB.IsCompleted, "the approvals did not answer in time");
            return (Answer(taskA), Answer(taskB));
        }
        finally
        {
            await cw.AdminAsync("delete from dbo.test_hold");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_approvals_of_one_claim_payment_request_pay_it_once(bool commit)
    {
        var (a, b) = await UsersAsync();
        var today = World.Today;
        var u = await cw.UserAsync("claims");
        var p = await cw.PolicyAsync(u.Id, today.AddDays(-30), [new ClaimWorld.Line("damage", 50000m, 0m)]);
        var claim = (await cw.RegisterAsync(u, p, today.AddDays(-2), today.AddDays(-1), ("damage", "10000.01")))[0];
        await cw.ApproveAsync(u, claim, "10000.01", today);
        Assert.EndsWith("payment=requested", (await cw.PayAsync(await cw.UserAsync("accountant"), claim, "10000.01", today, today)).RedirectTo);
        var request = await w.ScalarAsync<Guid>("select id from core.claim_payment_request where claim_id = @claim", new { claim });
        var reserves = await w.ScalarAsync<int>("select count(*) from core.claim_reserve where claim_id = @claim", new { claim });
        var mark = await AuditMarkAsync();
        var oldAudit = await AuditUpToAsync(mark);
        var form = ClaimWorld.Form(("claim_id", claim.ToString()), ("request_id", request.ToString()));
        var (answerA, answerB) = await AppRaceAsync("claim", a, commit,
            () => ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, a, form), () => ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, b, form));
        var back = (string o) => $"redirect /claims/{claim}?payment={o}";
        Assert.Equal(commit ? (back("approved"), back("already")) : ("thrown HarnessRollback", back("approved")), (answerA, answerB));
        var winner = commit ? a.Id : b.Id;
        Assert.Equal([(10000.01m, 0m, 10000.01m, (Guid?)winner)], await w.QueryAsync<(decimal, decimal, decimal, Guid?)>(
            "select amount, deductible_withheld, net_paid, created_by from core.claim_payment where request_id = @request", new { request }));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @claim", new { claim }));
        Assert.Equal([(-10000.01m, (Guid?)winner)], await w.QueryAsync<(decimal, Guid?)>(
            "select change, created_by from core.claim_reserve where claim_id = @claim order by created_at offset @reserves rows", new { claim, reserves }));
        Assert.Equal("paid", await w.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim }));
        var events = await AuditSinceAsync(mark);
        Assert.Equal([("claim", claim.ToString(), "update", (Guid?)winner)], events);
        Assert.Equal(oldAudit, await AuditUpToAsync(mark));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_approvals_of_one_premium_refund_request_refund_once(bool commit)
    {
        var (a, b) = await UsersAsync();
        var today = World.Today;
        var pol = await w.PendingPolicyAsync(await w.ClientAsync(), today.AddDays(-30), premium: 100m, schedule: [(0, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, a, pol.Id.ToString())).Outcome);
        var inst = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @p", new { p = pol.Id });
        // 10 100.01 received on a 100.00 instalment: 10 000.01 overpaid, above the threshold — what the request refunds.
        await w.App.AsUserAsync(a.Id, tx => tx.ExecuteAsync("""
            declare @r uniqueidentifier = newid();
            insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by) values (@r, @today, 10100.01, N'bank', N'receipt', @holder, N'check-fd-app-concurrency', @by);
            insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @inst, 10100.01, 10100.01, 1, @today);
            """, new { today, holder = pol.Holder, by = a.Id, inst }));
        Assert.Equal($"/policies/{pol.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, a, pol.Id.ToString(), "bank", "check-fd-app-concurrency"));
        var request = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @p", new { p = pol.Id });
        var mark = await AuditMarkAsync();
        var oldAudit = await AuditUpToAsync(mark);
        var (answerA, answerB) = await AppRaceAsync("payment_allocation", a, commit,
            () => PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, a, pol.Id.ToString(), request.ToString()),
            () => PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, b, pol.Id.ToString(), request.ToString()));
        var back = (string o) => $"redirect /policies/{pol.Id}?refund={o}";
        Assert.Equal(commit ? (back("approved"), back("already")) : ("thrown HarnessRollback", back("approved")), (answerA, answerB));
        var winner = commit ? a.Id : b.Id;
        var refund = Assert.Single(await w.QueryAsync<(Guid Id, string Kind, DateOnly On, decimal Gel, string Method, string Reference, Guid Payer, Guid? By)>(
            "select id, kind, received_on, amount_gel, method, reference, payer_party_id, created_by from core.payment where refund_request_id = @request", new { request }));
        Assert.Equal(("refund", today, -10000.01m, "bank", "check-fd-app-concurrency", pol.Holder, (Guid?)winner), (refund.Kind, refund.On, refund.Gel, refund.Method, refund.Reference, refund.Payer, refund.By));
        Assert.Equal([(inst, -10000.01m, -10000.01m, 1m, today, (Guid?)winner)], await w.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly, Guid?)>(
            "select installment_id, amount, amount_gel, rate, booked_on, created_by from core.payment_allocation where payment_id = @id", new { id = refund.Id }));
        Assert.Equal(2, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @h", new { h = pol.Holder }));
        Assert.Empty(await AuditSinceAsync(mark));
        Assert.Equal(oldAudit, await AuditUpToAsync(mark));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Two_approvals_of_a_receipts_reversal_mirror_it_once(bool twoRequests, bool commit)
    {
        var (a, b) = await UsersAsync();
        var today = World.Today;
        var pol = await w.PendingPolicyAsync(await w.ClientAsync(), today.AddDays(-30), premium: 10000.01m, schedule: [(0, 10000.01m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, a, pol.Id.ToString())).Outcome);
        Assert.NotNull((await w.PayAsync(a, pol.Id, "10000.01", today)).Redirect);
        var receipt = await w.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @h", new { h = pol.Holder });
        foreach (var _ in Enumerable.Range(0, twoRequests ? 2 : 1))
            Assert.Equal($"/policies/{pol.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, a, receipt.ToString(), pol.Id.ToString(), "check-fd-app-concurrency"));
        var requests = await w.QueryAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @receipt order by created_at", new { receipt });
        var (requestA, requestB) = (requests[0], requests[^1]);
        var mark = await AuditMarkAsync();
        var oldAudit = await AuditUpToAsync(mark);
        var (answerA, answerB) = await AppRaceAsync("payment_allocation", a, commit,
            () => PaymentActions.ApproveReceiptReversalRequestAsync(w.App, a, pol.Id.ToString(), requestA.ToString()),
            () => PaymentActions.ApproveReceiptReversalRequestAsync(w.App, b, pol.Id.ToString(), requestB.ToString()));
        var back = (string o) => $"redirect /policies/{pol.Id}?reversal={o}";
        Assert.Equal(commit ? (back("approved"), back(twoRequests ? "state" : "already")) : ("thrown HarnessRollback", back("approved")), (answerA, answerB));
        var winner = commit ? a.Id : b.Id;
        var mirror = Assert.Single(await w.QueryAsync<(Guid Id, string Kind, DateOnly On, decimal Gel, string Reference, Guid? Request, Guid? By)>(
            "select id, kind, received_on, amount_gel, reference, reversal_request_id, created_by from core.payment where reverses_payment_id = @receipt", new { receipt }));
        Assert.Equal(("reversal", today, -10000.01m, "check-fd-app-concurrency", (Guid?)(commit ? requestA : requestB), (Guid?)winner),
            (mirror.Kind, mirror.On, mirror.Gel, mirror.Reference, mirror.Request, mirror.By));
        Assert.Equal([(-10000.01m, -10000.01m, 1m, today, (Guid?)winner)], await w.QueryAsync<(decimal, decimal, decimal, DateOnly, Guid?)>(
            "select amount, amount_gel, rate, booked_on, created_by from core.payment_allocation where payment_id = @id", new { id = mirror.Id }));
        Assert.Empty(await AuditSinceAsync(mark));
        Assert.Equal(oldAudit, await AuditUpToAsync(mark));
    }
}
