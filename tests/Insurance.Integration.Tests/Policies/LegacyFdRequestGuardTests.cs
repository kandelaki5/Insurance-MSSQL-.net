using System.Data;
using Dapper;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/fd-requests-db-check.ts (Codex INS-FD-0036-0038-SQL-GUARD-CHECKS; "PREPARED, NOT RUN" in the old program —
/// real evidence here for the first time): the database refuses a claim payment, a premium refund or a receipt reversal
/// above the financial director's threshold (10 000.00) without its request, and one that does not take its request over
/// exactly (the quote, the plan line by line). Everything as core_app; each attempt in a transaction of its own that is
/// rolled back (the legacy savepoint), so every attempt starts from the same rows, and at the end every ledger, request,
/// policy, claim and the journal are as before. The statements are the actions' own. Same cases and amounts. A month
/// closed in 2020 stands in for "a closed month" (the legacy check used the database's latest closed month + 14 days and
/// was SKIP without one). Who may approve is the application's permission, not the database's (see FdRequestTests).
/// </summary>
public sealed class LegacyFdRequestGuardTests(TestDatabase db) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string Tag = "check-fd-0036-0038";
    private static readonly DateOnly Closed = new(2020, 1, 15);
    private static readonly string[] Tables = ["claim_payment_request", "premium_refund_request", "premium_refund_request_line", "receipt_reversal_request", "receipt_reversal_request_line"];
    private static readonly string[] Ledgers = ["claim_payment", "payment", "payment_allocation", "claim_reserve", "premium_transaction", "installment_adjustment", "policy", "claim",
        "claim_payment_request", "premium_refund_request", "premium_refund_request_line", "receipt_reversal_request", "receipt_reversal_request_line", "schema_migration"];

    private readonly World w = new(db);
    private readonly List<string> failures = [];
    private Guid admin;
    private (Guid Id, Guid Holder) claim, otherClaim;
    private (Guid Installment, Guid Policy, Guid Holder) line;
    private Guid otherLine, other;
    private DateOnly today, next;

    public async Task InitializeAsync()
    {
        var cw = new ClaimWorld(db);
        var u = await cw.UserAsync("admin");
        admin = u.Id;
        today = World.Today;
        next = today.AddDays(1);
        foreach (var k in new[] { 0, 1 })
        {
            var p = await cw.PolicyAsync(admin, today.AddDays(-30), [new ClaimWorld.Line("damage", 50000m, 0m)]);
            var c = (await cw.RegisterAsync(u, p, today.AddDays(-2), today.AddDays(-1), ("damage", "1000")))[0];
            if (k == 0) claim = (c, p.PartyId); else otherClaim = (c, p.PartyId);
        }
        var lp = await w.ActivePolicyAsync(u, await w.ClientAsync(), today.AddDays(-30));
        var op = await w.ActivePolicyAsync(u, await w.ClientAsync(), today.AddDays(-30));
        line = (await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @p order by due_on desc", new { p = lp.Id }), lp.Id, lp.Holder);
        otherLine = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @p order by due_on desc", new { p = op.Id });
        other = await w.ClientAsync();
        // Each test of the class gets its own fixture rows; the month is closed once (never reopened).
        if (await w.ScalarAsync<int>("select count(*) from core.accounting_period where period_start = '2020-01-01'") == 0) await w.CloseMonthAsync(Closed, admin);
        await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void Expect(string check, string expected, string actual)
    {
        if (expected != actual) failures.Add($"{check}: expected {expected}, got {actual}");
    }

    private static string Outcome(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is SqlException s && s.Number == 229) return "42501";
        return DbError.From(e) is { } d ? (d.Constraint is null ? d.Code : $"{d.Code} {d.Constraint}") : e.Message;
    }

    /// <summary>One attempt as core_app, the admin as the session user, in its own transaction, always rolled back.</summary>
    private async Task<string> Attempt(Func<Tx, Task> fn)
    {
        await using var c = await db.OpenAppAsync();
        await using var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var tx = new Tx(c, t);
        await tx.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = admin.ToString() });
        try
        {
            await fn(tx);
            return "ok";
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException or Xunit.Sdk.XunitException)
        {
            return e is SqlException ? Outcome(e) : e.Message;
        }
        finally
        {
            try { if (t.Connection is not null) await t.RollbackAsync(); } catch (InvalidOperationException) { }
        }
    }

    private async Task<string> FingerprintAsync()
    {
        await using var c = await db.OpenAdminAsync();
        var parts = new List<string>();
        foreach (var table in Ledgers)
            parts.Add(table + ":" + await c.ExecuteScalarAsync<string?>($"select (select * from core.{table} order by 1 for json path)"));
        parts.Add("threshold:" + await c.ExecuteScalarAsync<string?>("select value from core.setting where [key] = N'payment.fd_threshold_gel'"));
        return string.Join("\n", parts);
    }

    /// <summary>The audit rows of the ids in the attempt's transaction: (table, row, action, user, the fields asked for as stored text).</summary>
    private static async Task Audited(Tx tx, params (string Table, Guid Row, Dictionary<string, string> Fields)[] expected)
    {
        foreach (var (table, row, fields) in expected)
        {
            var rows = (await tx.QueryAsync<(string Action, Guid? UserId, string NewRow)>(
                "select action, user_id, new_row from core.audit_log where table_name = @table and row_id = @row", new { table, row = row.ToString() })).ToList();
            if (rows.Count != 1 || rows[0].Action != "insert") throw new InvalidOperationException($"audit {table} {row}: {rows.Count} rows");
            using var json = System.Text.Json.JsonDocument.Parse(rows[0].NewRow);
            foreach (var (k, v) in fields)
            {
                var got = json.RootElement.GetProperty(k).ToString();
                if (got != v) throw new InvalidOperationException($"audit {table} {row}: {k} {got}, not {v}");
            }
        }
    }

    private static Dictionary<string, string> F(params (string, string)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

    [Fact]
    public async Task Rights_of_core_app_on_the_request_tables()
    {
        await using var c = await db.OpenAppAsync();
        foreach (var t in Tables)
        {
            var r = await c.QuerySingleAsync<(int S, int I, int U, int D, int X)>("""
                select has_perms_by_name(@o, 'OBJECT', 'SELECT'), has_perms_by_name(@o, 'OBJECT', 'INSERT'), has_perms_by_name(@o, 'OBJECT', 'UPDATE'),
                       has_perms_by_name(@o, 'OBJECT', 'DELETE'), has_perms_by_name(@o, 'OBJECT', 'ALTER')
                """, new { o = $"core.{t}" });
            // TRUNCATE needs ALTER on SQL Server.
            Assert.Equal((1, 1, 0, 0, 0), r);
        }
    }

    // ======== 0036 — a claim payment and its request.

    private sealed record Quote(string Amount = "11000.01", string Ded = "1000.00", string Off = "0.00", string Net = "10000.01", string Gel = "10000.01",
        string Rate = "1.000000", DateOnly? On = null, Guid? Payee = null, string Kind = "policyholder", Guid? ClaimId = null);

    private Quote Q(Quote? q = null) => (q ?? new Quote()) with { On = q?.On ?? today, Payee = q?.Payee ?? claim.Holder, ClaimId = q?.ClaimId ?? claim.Id };

    private static object P(Quote q) => new
    {
        claim = q.ClaimId, on = q.On, amount = decimal.Parse(q.Amount, System.Globalization.CultureInfo.InvariantCulture),
        ded = decimal.Parse(q.Ded, System.Globalization.CultureInfo.InvariantCulture), off = decimal.Parse(q.Off, System.Globalization.CultureInfo.InvariantCulture),
        net = decimal.Parse(q.Net, System.Globalization.CultureInfo.InvariantCulture), gel = decimal.Parse(q.Gel, System.Globalization.CultureInfo.InvariantCulture),
        rate = decimal.Parse(q.Rate, System.Globalization.CultureInfo.InvariantCulture), payee = q.Payee, kind = q.Kind,
    };

    private async Task<Guid> AskClaim(Tx tx, Quote q)
    {
        var id = Guid.NewGuid();
        var p = new DynamicParameters(P(q));
        p.Add("id", id);
        p.Add("tag", Tag);
        p.Add("by", admin);
        await tx.ExecuteAsync("""
            insert into core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                    payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_by)
            values (@id, @claim, @on, @amount, @ded, @off, @net, @gel, @rate, @kind, @payee, 0, N'none', @tag, @by)
            """, p);
        return id;
    }

    private async Task<Guid> PayClaim(Tx tx, Quote q, Guid? request)
    {
        var id = Guid.NewGuid();
        var p = new DynamicParameters(P(q));
        p.Add("id", id);
        p.Add("tag", Tag);
        p.Add("by", admin);
        p.Add("request", request);
        await tx.ExecuteAsync("""
            insert into core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                            payee_kind, payee_party_id, offset_payment_id, note, created_by, request_id)
            values (@id, @claim, @on, @amount, @ded, @off, @net, @gel, @rate, @kind, @payee, null, @tag, @by, @request)
            """, p);
        return id;
    }

    [Fact]
    public async Task Migration_0036_a_claim_payment_takes_its_request_over_exactly()
    {
        var start = await FingerprintAsync();
        Expect("0036: a request is stored and audited once, by the session user, with its quote", "ok", await Attempt(async tx =>
        {
            var r = await AskClaim(tx, Q());
            await Audited(tx, ("claim_payment_request", r, F(("claim_id", claim.Id.ToString()), ("amount", "11000.01"), ("net_paid_gel", "10000.01"), ("created_by", admin.ToString()))));
            var actor = await tx.ExecuteScalarAsync<Guid>("select user_id from core.audit_log where table_name = N'claim_payment_request' and row_id = @r", new { r = r.ToString() });
            if (actor != admin) throw new InvalidOperationException($"audited by {actor}");
        }));
        Expect("0036: a request whose net is not gross − deductible − set-off", "23514 claim_payment_request_net", await Attempt(tx => AskClaim(tx, Q(new Quote(Net: "10000.00", Gel: "10000.00")))));
        Expect("0036: a request with no money to transfer (net 0)", "23514 claim_payment_request_net_paid_check", await Attempt(tx => AskClaim(tx, Q(new Quote(Amount: "1000.00", Net: "0.00", Gel: "0.00")))));
        Expect("0036: a request dated in a closed month", "23514", await Attempt(tx => AskClaim(tx, Q(new Quote(On: Closed)))));
        Expect("0036: core_app edits a request (append-only)", "42501", await Attempt(async tx => { var r = await AskClaim(tx, Q()); await tx.ExecuteAsync("update core.claim_payment_request set net_paid_gel = 1 where id = @r", new { r }); }));
        Expect("0036: core_app deletes a request (append-only)", "42501", await Attempt(async tx => { var r = await AskClaim(tx, Q()); await tx.ExecuteAsync("delete from core.claim_payment_request where id = @r", new { r }); }));
        Expect("0036: the payment carrying its request, exactly: written once, linked, by the approver", "ok", await Attempt(async tx =>
        {
            var r = await AskClaim(tx, Q());
            var p = await PayClaim(tx, Q(), r);
            var row = await tx.QuerySingleOrDefaultAsync<(Guid? Request, Guid? By, decimal Gel, string Kind)>("select request_id, created_by, net_paid_gel, kind from core.claim_payment where id = @p", new { p });
            if (row != (r, admin, 10000.01m, "payment")) throw new InvalidOperationException($"written {row}");
        }));
        Expect("0036: a second payment for the same request", "23505 claim_payment_request_once", await Attempt(async tx => { var r = await AskClaim(tx, Q()); await PayClaim(tx, Q(), r); await PayClaim(tx, Q(), r); }));
        Expect("0036: a payment on another claim carrying the request", "CQ001", await Attempt(async tx => { var r = await AskClaim(tx, Q()); await PayClaim(tx, Q(new Quote(ClaimId: otherClaim.Id)), r); }));
        foreach (var (label, change) in new (string, Quote)[]
        {
            ("another gross", new Quote(Amount: "11000.02", Net: "10000.02", Gel: "10000.02")), ("another deductible", new Quote(Ded: "999.99", Net: "10000.02", Gel: "10000.02")),
            ("another set-off", new Quote(Off: "0.01", Net: "10000.00", Gel: "10000.00")), ("another net (the rest as asked)", new Quote(Net: "10000.00")), ("another GEL", new Quote(Gel: "10000.02")),
            ("another rate", new Quote(Rate: "1.000001")), ("another day", new Quote(On: next)), ("another payee", new Quote(Payee: other)), ("another payee kind", new Quote(Kind: "beneficiary")),
        })
            Expect($"0036: the payment with {label} than its request", "CQ002", await Attempt(async tx => { var r = await AskClaim(tx, Q()); await PayClaim(tx, Q(change), r); }));
        Expect("0036: a payment of 10 000.01 net with no request", "CQ003", await Attempt(tx => PayClaim(tx, Q(), null)));
        Expect("0036: a payment of exactly 10 000.00 net with no request (the threshold itself is allowed)", "ok", await Attempt(tx => PayClaim(tx, Q(new Quote(Amount: "11000.00", Net: "10000.00", Gel: "10000.00")), null)));
        foreach (var (label, change) in new[] { ("empty", "value = N''"), ("not a number", "value = N'ათი ათასი'"), ("three decimals", "value = N'10000.001'"), ("missing (its key gone)", $"[key] = N'{Tag}'") })
            Expect($"0036: the threshold {label}: a payment of 1.00 is refused", "CQ004", await Attempt(async tx =>
            {
                await tx.ExecuteAsync($"update core.setting set {change} where [key] = N'payment.fd_threshold_gel'");
                await PayClaim(tx, Q(new Quote(Amount: "1.00", Ded: "0.00", Net: "1.00", Gel: "1.00")), null);
            }));
        Expect("0036: a reversal of a requested payment above the threshold is not a payment: the request guard lets it through", "ok", await Attempt(async tx =>
        {
            var r = await AskClaim(tx, Q());
            var p = await PayClaim(tx, Q(), r);
            await tx.ExecuteAsync("""
                insert into core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                payee_kind, payee_party_id, offset_payment_id, note, created_by, kind, reverses_claim_payment_id, reversal_reason, returned_on, bank_reference)
                select claim_id, @today, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate,
                       payee_kind, payee_party_id, null, @tag, @by, N'reversal', id, N'correction', null, null
                from core.claim_payment where id = @p
                """, new { today, tag = Tag, by = admin, p });
        }));
        Assert.Empty(failures);
        Assert.Equal(start, await FingerprintAsync());
    }

    // ======== 0037 — a premium refund and its plan.

    private async Task<Guid> AskRefund(Tx tx, decimal gel, string currency, decimal rate, string? reference = Tag)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_by)
            values (@id, @policy, @today, @gel, @currency, @rate, N'bank', @reference, @holder, @by)
            """, new { id, policy = line.Policy, today, gel, currency, rate, reference, holder = line.Holder, by = admin });
        return id;
    }

    private static async Task<Guid> RefundLine(Tx tx, Guid request, int position, Guid installment, decimal amount, decimal gel)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("insert into core.premium_refund_request_line (id, request_id, position, installment_id, amount, amount_gel) values (@id, @request, @position, @installment, @amount, @gel)",
            new { id, request, position, installment, amount, gel });
        return id;
    }

    private async Task<Guid> Refund(Tx tx, decimal gel, Guid? request, string method = "bank", string? reference = Tag, bool referenceGiven = true, Guid? payer = null, DateOnly? on = null)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by, refund_request_id)
            values (@id, @on, @gel, @method, N'refund', @payer, @reference, @by, @request)
            """, new { id, on = on ?? today, gel, method, payer = payer ?? line.Holder, reference = referenceGiven ? reference : Tag, by = admin, request });
        return id;
    }

    private async Task<Guid> Allocate(Tx tx, Guid payment, Guid installment, decimal amount, decimal gel, decimal rate)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("insert into core.payment_allocation (id, payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@id, @payment, @installment, @amount, @gel, @rate, @today)",
            new { id, payment, installment, amount, gel, rate, today });
        return id;
    }

    /// <summary>A request for 10 000.01 GEL with its one line.</summary>
    private async Task<Guid> PlannedRefund(Tx tx)
    {
        var r = await AskRefund(tx, 10000.01m, "GEL", 1m);
        await RefundLine(tx, r, 1, line.Installment, 10000.01m, 10000.01m);
        return r;
    }

    [Fact]
    public async Task Migration_0037_a_premium_refund_takes_its_request_and_plan_over_exactly()
    {
        var start = await FingerprintAsync();
        Expect("0037: a request and its line are stored and audited once each, by the session user", "ok", await Attempt(async tx =>
        {
            var r = await AskRefund(tx, 10000.01m, "GEL", 1m);
            var l = await RefundLine(tx, r, 1, line.Installment, 10000.01m, 10000.01m);
            await Audited(tx, ("premium_refund_request", r, F(("policy_id", line.Policy.ToString()), ("amount_gel", "10000.01"), ("created_by", admin.ToString()))),
                ("premium_refund_request_line", l, F(("request_id", r.ToString()), ("installment_id", line.Installment.ToString()), ("amount", "10000.01"))));
        }));
        Expect("0037: a line of nothing", "23514 premium_refund_request_line_amount_check", await Attempt(async tx => { var r = await AskRefund(tx, 1m, "GEL", 1m); await RefundLine(tx, r, 1, line.Installment, 0m, 0m); }));
        Expect("0037: a line at position 0", "23514 premium_refund_request_line_position_check", await Attempt(async tx => { var r = await AskRefund(tx, 1m, "GEL", 1m); await RefundLine(tx, r, 0, line.Installment, 1m, 1m); }));
        Expect("0037: two lines at one position", "23505 premium_refund_request_line_request_id_position_key", await Attempt(async tx =>
        {
            var r = await AskRefund(tx, 2m, "GEL", 1m); await RefundLine(tx, r, 1, line.Installment, 1m, 1m); await RefundLine(tx, r, 1, otherLine, 1m, 1m);
        }));
        Expect("0037: a request dated in a closed month", "23514", await Attempt(tx => tx.ExecuteAsync("""
            insert into core.premium_refund_request (policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_by)
            values (@policy, @closed, 1.00, N'GEL', 1, N'bank', @tag, @holder, @by)
            """, new { policy = line.Policy, closed = Closed, tag = Tag, holder = line.Holder, by = admin })));
        Expect("0037: core_app edits a request (append-only)", "42501", await Attempt(async tx => { var id = await PlannedRefund(tx); await tx.ExecuteAsync("update core.premium_refund_request set amount_gel = 1 where id = @id", new { id }); }));
        Expect("0037: core_app deletes a line (append-only)", "42501", await Attempt(async tx => { var id = await PlannedRefund(tx); await tx.ExecuteAsync("delete from core.premium_refund_request_line where request_id = @id", new { id }); }));
        Expect("0037: the refund carrying its request with the planned line, exactly: written once, linked, by the approver", "ok", await Attempt(async tx =>
        {
            var id = await PlannedRefund(tx);
            var p = await Refund(tx, -10000.01m, id);
            var a = await Allocate(tx, p, line.Installment, -10000.01m, -10000.01m, 1m);
            var pay = await tx.QuerySingleOrDefaultAsync<(string, decimal, Guid?, Guid?)>("select kind, amount_gel, refund_request_id, created_by from core.payment where id = @p", new { p });
            var al = await tx.QuerySingleOrDefaultAsync<(Guid, Guid, decimal, decimal, decimal, Guid?)>("select payment_id, installment_id, amount, amount_gel, rate, created_by from core.payment_allocation where id = @a", new { a });
            if (pay != ("refund", -10000.01m, id, admin) || al != (p, line.Installment, -10000.01m, -10000.01m, 1m, admin)) throw new InvalidOperationException($"written {pay} {al}");
        }));
        foreach (var (label, run) in new (string, Func<Tx, Guid, Task>)[]
        {
            ("another GEL", (tx, id) => Refund(tx, -10000.02m, id)), ("another method", (tx, id) => Refund(tx, -10000.01m, id, method: "cash")),
            ("another reference", (tx, id) => Refund(tx, -10000.01m, id, reference: "other")), ("no reference where the request has one", (tx, id) => Refund(tx, -10000.01m, id, reference: null)),
            ("another payer", (tx, id) => Refund(tx, -10000.01m, id, payer: other)), ("another day", (tx, id) => Refund(tx, -10000.01m, id, on: next)),
        })
            Expect($"0037: the refund with {label} than its request", "RF002", await Attempt(async tx => await run(tx, await PlannedRefund(tx))));
        Expect("0037: a request without a reference and a refund without one: taken over (NULL is NULL)", "ok", await Attempt(async tx =>
        {
            var r = await AskRefund(tx, 10000.01m, "GEL", 1m, null); await RefundLine(tx, r, 1, line.Installment, 10000.01m, 10000.01m);
            await Refund(tx, -10000.01m, r, reference: null);
        }));
        foreach (var (label, inst, amount, gel, rate) in new[]
        {
            ("another instalment", otherLine, -10000.01m, -10000.01m, 1m), ("another amount", line.Installment, -10000.00m, -10000.01m, 1m),
            ("another GEL", line.Installment, -10000.01m, -10000.00m, 1m), ("another rate", line.Installment, -10000.01m, -10000.01m, 1.000001m),
        })
            Expect($"0037: an allocation of the requested refund with {label} than its plan", "RF005", await Attempt(async tx =>
            {
                var id = await PlannedRefund(tx); var p = await Refund(tx, -10000.01m, id); await Allocate(tx, p, inst, amount, gel, rate);
            }));
        Expect("0037: a second refund for the same request", "23505 payment_refund_request_once", await Attempt(async tx => { var id = await PlannedRefund(tx); await Refund(tx, -10000.01m, id); await Refund(tx, -10000.01m, id); }));
        Expect("0037: a refund of 10 000.01 with no request", "RF003", await Attempt(tx => Refund(tx, -10000.01m, null)));
        Expect("0037: a refund of exactly 10 000.00 with no request", "ok", await Attempt(tx => Refund(tx, -10000.00m, null)));
        foreach (var (label, change) in new[] { ("not an amount", "value = N'x'"), ("missing (its key gone)", $"[key] = N'{Tag}'") })
            Expect($"0037: the threshold {label}: a refund of 1.00 is refused", "RF004", await Attempt(async tx => { await tx.ExecuteAsync($"update core.setting set {change} where [key] = N'payment.fd_threshold_gel'"); await Refund(tx, -1m, null); }));
        Expect("0037: a receipt of 20 000.00 is not a refund: the refund guard lets it through", "ok", await Attempt(tx => tx.ExecuteAsync("""
            insert into core.payment (received_on, amount_gel, method, kind, payer_party_id, reference, created_by) values (@today, 20000.00, N'bank', N'receipt', @holder, @tag, @by)
            """, new { today, holder = line.Holder, tag = Tag, by = admin })));
        // Ten lines of USD 0.01 at 2.5: 0.25 GEL, the lines 0.03 / 0.02 in turn.
        async Task<Guid> TenLines(Tx tx)
        {
            var r = await AskRefund(tx, 0.25m, "USD", 2.5m);
            for (var i = 0; i < 10; i++) await RefundLine(tx, r, i + 1, i % 2 == 0 ? line.Installment : otherLine, 0.01m, i % 2 == 0 ? 0.03m : 0.02m);
            return await Refund(tx, -0.25m, r);
        }
        Expect("0037: ten lines of USD 0.01 at 2.5 written as planned (0.03 / 0.02): every running sum within −0.25", "ok", await Attempt(async tx =>
        {
            var p = await TenLines(tx);
            for (var i = 0; i < 10; i++) await Allocate(tx, p, i % 2 == 0 ? line.Installment : otherLine, -0.01m, i % 2 == 0 ? -0.03m : -0.02m, 2.5m);
        }));
        Expect("0037: nine lines of −0.03 (each a line of the plan): the ninth running sum −0.27 exceeds −0.25 — refused by 0024, not by RF005", "PA002", await Attempt(async tx =>
        {
            var p = await TenLines(tx);
            for (var i = 0; i < 9; i++) await Allocate(tx, p, line.Installment, -0.01m, -0.03m, 2.5m);
        }));
        Expect("0037: half a tetri as planned (USD 2.50 at 2.002 = 5.01)", "ok", await Attempt(async tx =>
        {
            var r = await AskRefund(tx, 5.01m, "USD", 2.002m); await RefundLine(tx, r, 1, line.Installment, 2.50m, 5.01m);
            var p = await Refund(tx, -5.01m, r); await Allocate(tx, p, line.Installment, -2.50m, -5.01m, 2.002m);
        }));
        // A refund and a line outside its plan in one go: refused as a whole (the legacy inner savepoint is a transaction of its own here).
        Guid request = Guid.Empty;
        var inner = await Attempt(async tx =>
        {
            request = await PlannedRefund(tx);
            var p = await Refund(tx, -10000.01m, request);
            await Allocate(tx, p, otherLine, -10000.01m, -10000.01m, 1m);
        });
        Expect("0037: a refund and a line outside its plan in one go: refused, and nothing of the refund stays", "RF005 0",
            $"{inner} {await w.ScalarAsync<int>("select count(*) from core.payment where refund_request_id = @request", new { request })}");
        Assert.Empty(failures);
        Assert.Equal(start, await FingerprintAsync());
    }

    // ======== 0038 — a receipt's reversal and its plan.

    private async Task<Guid> Receipt(Tx tx, decimal gel, IReadOnlyList<(Guid Inst, decimal Amount, decimal Gel, decimal Rate)> lines, string method = "bank")
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by) values (@id, @today, @gel, @method, N'receipt', @holder, @tag, @by)
            """, new { id, today, gel, method, holder = line.Holder, tag = Tag, by = admin });
        foreach (var l in lines) await Allocate(tx, id, l.Inst, l.Amount, l.Gel, l.Rate);
        return id;
    }

    private async Task<Guid> AskReversal(Tx tx, Guid receipt, decimal gel, IReadOnlyList<(Guid Inst, decimal Amount, decimal Gel, decimal Rate)> lines, string method = "bank")
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by)
            values (@id, @receipt, @policy, @today, @gel, @method, @holder, @tag, @by)
            """, new { id, receipt, policy = line.Policy, today, gel, method, holder = line.Holder, tag = Tag, by = admin });
        for (var i = 0; i < lines.Count; i++)
            await tx.ExecuteAsync("insert into core.receipt_reversal_request_line (request_id, position, installment_id, amount, amount_gel, rate) values (@id, @position, @inst, @amount, @gel, @rate)",
                new { id, position = i + 1, inst = lines[i].Inst, amount = lines[i].Amount, gel = lines[i].Gel, rate = lines[i].Rate });
        return id;
    }

    private async Task<Guid> Mirror(Tx tx, Guid receipt, decimal gel, Guid? request, string method = "bank", Guid? payer = null, string reason = Tag, DateOnly? on = null)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.payment (id, received_on, amount_gel, method, kind, reverses_payment_id, payer_party_id, reference, created_by, reversal_request_id)
            values (@id, @on, @gel, @method, N'reversal', @receipt, @payer, @reason, @by, @request)
            """, new { id, on = on ?? today, gel, method, receipt, payer = payer ?? line.Holder, reason, by = admin, request });
        return id;
    }

    [Fact]
    public async Task Migration_0038_a_receipts_reversal_takes_its_request_and_plan_over_exactly()
    {
        var start = await FingerprintAsync();
        (Guid, decimal, decimal, decimal)[] one = [(line.Installment, 10000.01m, 10000.01m, 1m)];
        async Task<(Guid Receipt, Guid Request)> Planned(Tx tx) { var r = await Receipt(tx, 10000.01m, one); return (r, await AskReversal(tx, r, 10000.01m, one)); }

        Expect("0038: a request and its line are stored and audited once each, by the session user", "ok", await Attempt(async tx =>
        {
            var (_, request) = await Planned(tx);
            var l = await tx.ExecuteScalarAsync<Guid>("select id from core.receipt_reversal_request_line where request_id = @request", new { request });
            await Audited(tx, ("receipt_reversal_request", request, F(("amount_gel", "10000.01"), ("reason", Tag), ("created_by", admin.ToString()))),
                ("receipt_reversal_request_line", l, F(("request_id", request.ToString()), ("installment_id", line.Installment.ToString()), ("rate", "1.000000"))));
        }));
        Expect("0038: a request dated in a closed month", "23514", await Attempt(async tx =>
        {
            var r = await Receipt(tx, 1m, [(line.Installment, 1m, 1m, 1m)]);
            await tx.ExecuteAsync("""
                insert into core.receipt_reversal_request (receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by)
                values (@r, @policy, @closed, 1.00, N'bank', @holder, @tag, @by)
                """, new { r, policy = line.Policy, closed = Closed, holder = line.Holder, tag = Tag, by = admin });
        }));
        Expect("0038: core_app edits a request (append-only)", "42501", await Attempt(async tx => { var (_, q) = await Planned(tx); await tx.ExecuteAsync("update core.receipt_reversal_request set reason = N'x' where id = @q", new { q }); }));
        Expect("0038: core_app deletes a line (append-only)", "42501", await Attempt(async tx => { var (_, q) = await Planned(tx); await tx.ExecuteAsync("delete from core.receipt_reversal_request_line where request_id = @q", new { q }); }));
        Expect("0038: the mirror carrying its request with the planned line, exactly: written once, linked, by the approver", "ok", await Attempt(async tx =>
        {
            var (r, q) = await Planned(tx);
            var m = await Mirror(tx, r, -10000.01m, q);
            var a = await Allocate(tx, m, line.Installment, -10000.01m, -10000.01m, 1m);
            var pay = await tx.QuerySingleOrDefaultAsync<(string, decimal, Guid?, Guid?, Guid?)>("select kind, amount_gel, reverses_payment_id, reversal_request_id, created_by from core.payment where id = @m", new { m });
            var al = await tx.QuerySingleOrDefaultAsync<(Guid, Guid, decimal, decimal, decimal, Guid?)>("select payment_id, installment_id, amount, amount_gel, rate, created_by from core.payment_allocation where id = @a", new { a });
            if (pay != ("reversal", -10000.01m, r, q, admin) || al != (m, line.Installment, -10000.01m, -10000.01m, 1m, admin)) throw new InvalidOperationException($"written {pay} {al}");
        }));
        Expect("0038: a receipt with no allocation (an advance): its request plans no line; the mirror alone is written", "ok", await Attempt(async tx =>
        {
            var r = await Receipt(tx, 12000m, []); var q = await AskReversal(tx, r, 12000m, []); await Mirror(tx, r, -12000m, q);
        }));
        Expect("0038: the reversal of a 12 000.00 receipt with no allocation and no request", "RV003", await Attempt(async tx => { var r = await Receipt(tx, 12000m, []); await Mirror(tx, r, -12000m, null); }));
        foreach (var (label, run) in new (string, Func<Tx, Guid, Guid, Task>)[]
        {
            ("another GEL", (tx, r, q) => Mirror(tx, r, -10000.02m, q)), ("another method", (tx, r, q) => Mirror(tx, r, -10000.01m, q, method: "cash")),
            ("another payer", (tx, r, q) => Mirror(tx, r, -10000.01m, q, payer: other)), ("another reason", (tx, r, q) => Mirror(tx, r, -10000.01m, q, reason: "other")),
            ("another day", (tx, r, q) => Mirror(tx, r, -10000.01m, q, on: next)),
            ("another receipt", async (tx, _, q) => await Mirror(tx, await Receipt(tx, 10000.01m, one), -10000.01m, q)),
        })
            Expect($"0038: the mirror with {label} than its request", "RV002", await Attempt(async tx => { var (r, q) = await Planned(tx); await run(tx, r, q); }));
        foreach (var (label, inst, amount, gel, rate) in new[]
        {
            ("another instalment", otherLine, -10000.01m, -10000.01m, 1m), ("another amount", line.Installment, -10000.00m, -10000.01m, 1m),
            ("another GEL", line.Installment, -10000.01m, -10000.00m, 1m), ("today's rate instead of the stored one", line.Installment, -10000.01m, -10000.01m, 2.8m),
        })
            Expect($"0038: an allocation of the requested mirror with {label}", "RV005", await Attempt(async tx =>
            {
                var (r, q) = await Planned(tx); var m = await Mirror(tx, r, -10000.01m, q); await Allocate(tx, m, inst, amount, gel, rate);
            }));
        var twice = await Attempt(async tx => { var (r, q) = await Planned(tx); await Mirror(tx, r, -10000.01m, q); await Mirror(tx, r, -10000.01m, q); });
        Assert.True(twice is "23505 payment_reversal_once" or "23505 payment_reversal_request_once", $"0038: a second mirror for the same request and receipt: {twice}");
        Expect("0038: the reversal of a 10 000.01 receipt with no request", "RV003", await Attempt(async tx => { var r = await Receipt(tx, 10000.01m, one); await Mirror(tx, r, -10000.01m, null); }));
        Expect("0038: the reversal of a 10 000.00 receipt with no request", "ok", await Attempt(async tx => { var r = await Receipt(tx, 10000m, [(line.Installment, 10000m, 10000m, 1m)]); await Mirror(tx, r, -10000m, null); }));
        foreach (var (label, change) in new[] { ("not an amount", "value = N'x'"), ("missing (its key gone)", $"[key] = N'{Tag}'") })
            Expect($"0038: the threshold {label}: the reversal of a 1.00 receipt is refused", "RV004", await Attempt(async tx =>
            {
                var r = await Receipt(tx, 1m, [(line.Installment, 1m, 1m, 1m)]);
                await tx.ExecuteAsync($"update core.setting set {change} where [key] = N'payment.fd_threshold_gel'");
                await Mirror(tx, r, -1m, null);
            }));
        Expect("0038: a claim set-off's receipt of 12 000.00 mirrored without a request (part of a claim payment's reversal): not checked", "ok", await Attempt(async tx =>
        {
            var r = await Receipt(tx, 12000m, [(line.Installment, 12000m, 12000m, 1m)], "claim_offset");
            await Mirror(tx, r, -12000m, null, method: "claim_offset");
        }));
        // 90 071 992 547 408.01 on two different policies' instalments, a −0.01 transfer line and a 0-GEL line, in an order
        // whose running sums stay within the receipt; the mirror negates each exactly at its stored rate.
        (Guid Inst, decimal Amount, decimal Gel, decimal Rate)[] big = [(line.Installment, 90071992547400.00m, 90071992547400.00m, 1m), (otherLine, -0.01m, -0.01m, 1m),
            (otherLine, 8.02m, 8.02m, 1m), (line.Installment, 0.01m, 0.00m, 2.7m)];
        Expect("0038: 90 071 992 547 408.01 on two different policies: the request plans every line; the mirror negates each exactly", "ok", await Attempt(async tx =>
        {
            var r = await Receipt(tx, 90071992547408.01m, big);
            var q = await AskReversal(tx, r, 90071992547408.01m, big);
            var m = await Mirror(tx, r, -90071992547408.01m, q);
            var ids = new List<Guid>();
            foreach (var l in big) ids.Add(await Allocate(tx, m, l.Inst, -l.Amount, -l.Gel, l.Rate));
            for (var i = 0; i < big.Length; i++)
            {
                var row = await tx.QuerySingleOrDefaultAsync<(Guid, Guid, decimal, decimal, decimal, Guid?)>("select payment_id, installment_id, amount, amount_gel, rate, created_by from core.payment_allocation where id = @id", new { id = ids[i] });
                if (row != (m, big[i].Inst, -big[i].Amount, -big[i].Gel, big[i].Rate, admin)) throw new InvalidOperationException($"line {i}: {row}");
            }
        }));
        Assert.Empty(failures);
        Assert.Equal(start, await FingerprintAsync());
    }
}
