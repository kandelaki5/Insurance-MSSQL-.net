using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// Legacy scripts/loss-event-check.ts N, O, P and Q (Codex rollback task 7efb619, H54, H56; INS-CLAIM-OFFSET-ROLLBACK-EVIDENCE;
/// INS-MHL-MULTICOVERAGE-OFFSET-REGRESSION), case by case, on the real SQL Server: the payment's reserve insert fails once —
/// the legacy script held core.claim_reserve from another connection and cancelled the payment's own backend while it waited
/// there (57014); here the command interceptor fails that very statement (the insert into core.claim_reserve for that claim)
/// after the rows before it were sent, which the log shows. Each time the action fails with that error, every row is as before
/// (ids included), the retry writes each row once, and the repeat writes nothing. The legacy world: a CASCO policy issued with
/// damage 300 (deductible 200), equipment 50 (200), theft 40, assistance 15 — 405.00 in two instalments of 202.50, nothing paid;
/// here from 01.10 of a year of its own, the loss on 03.10, "today" 03.11.
/// </summary>
public sealed class LegacyLossEventFailureTests : IClassFixture<TestDatabase>
{
    private readonly Faults faults = new();
    private readonly ClaimWorld w;
    private static int year = 3100;

    public LegacyLossEventFailureTests(TestDatabase db) => w = new ClaimWorld(db, faults);

    private sealed record Event(ClaimWorld.Policy Policy, IReadOnlyList<Guid> Claims, CurrentUser Accountant, DateOnly Today);

    private async Task<Event> EventAsync(params (string Coverage, string Reserve, string Approved)[] parts)
    {
        var y = Interlocked.Increment(ref year);
        var from = new DateOnly(y, 10, 1);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, from, [new("damage", 20000m, 200m, 300m), new("equipment", 2000m, 200m, 50m), new("theft", 20000m, 200m, 40m),
            new("assistance", 500m, null, 15m)], [(from, 202.50m), (from.AddMonths(6), 202.50m)]);
        var ids = await w.RegisterAsync(claims, policy, from.AddDays(2), from.AddDays(3), [.. parts.Select(p => (p.Coverage, p.Reserve))]);
        return new Event(policy, ids, await w.UserAsync("accountant"), new DateOnly(y, 11, 3));
    }

    private static Func<CommandEvent, bool> ReserveInsertOf(Guid claim) => c => Faults.Has(c, "insert into core.claim_reserve") && Equals(c.Parameter("claimId"), claim);

    /// <summary>The commands sent before the failed one, from the action's transaction.</summary>
    private List<string> SentBefore(Func<CommandEvent, bool> failed)
    {
        var log = faults.Log.ToList();
        var at = log.FindIndex(c => failed(c));
        Assert.True(at >= 0, "the failing statement was never sent");
        var begin = log.FindLastIndex(at, c => c.Sql == "set xact_abort on");
        return log.Skip(begin).Take(at - begin).Select(c => c.Sql).ToList();
    }

    private static bool Inserts(IEnumerable<string> sql, string table) => sql.Any(s => s.Contains($"insert into core.{table} ", StringComparison.Ordinal) || s.Contains($"insert into core.{table}(", StringComparison.Ordinal) || s.Contains($"insert core.{table} ", StringComparison.Ordinal));

    /// <summary>Everything the event's payments could write, every column with its id: receipts of the holder, the policy's allocations, payments, reserves, claims, the policy, the schedule.</summary>
    private Task<string> LedgerAsync(Event e) => w.ScalarAsync<string>("""
        select concat(
          (select string_agg(concat(convert(nvarchar(36), p.id), N':', p.kind, N':', p.method, N':', p.amount_gel, N':', p.reference), N'|') within group (order by p.id) from core.payment p where p.payer_party_id = @party), N'#',
          (select string_agg(concat(convert(nvarchar(36), a.id), N':', convert(nvarchar(36), a.payment_id), N':', convert(nvarchar(36), i.id), N':', i.due_on, N':', a.amount), N'|') within group (order by i.due_on, a.id)
             from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policy), N'#',
          (select string_agg(concat(convert(nvarchar(36), x.id), N':', convert(nvarchar(36), x.claim_id), N':', x.amount, N':', x.deductible_withheld, N':', x.premium_offset, N':', x.net_paid, N':', convert(nvarchar(36), x.offset_payment_id)), N'|') within group (order by x.id)
             from core.claim_payment x where x.claim_id in @claims), N'#',
          (select string_agg(concat(convert(nvarchar(36), r.id), N':', convert(nvarchar(36), r.claim_id), N':', r.change), N'|') within group (order by r.id) from core.claim_reserve r where r.claim_id in @claims), N'#',
          (select string_agg(concat(convert(nvarchar(36), c.id), N':', c.status, N':', c.approved_amount, N':', c.is_total_loss), N'|') within group (order by c.id) from core.claim c where c.id in @claims), N'#',
          (select concat(status, N':', effective_from, N':', effective_to, N':', cancelled_on) from core.policy where id = @policy), N'#',
          (select string_agg(concat(convert(nvarchar(36), i.id), N':', i.due_on, N':', i.amount), N'|') within group (order by i.due_on) from core.installment i where i.policy_id = @policy), N'#',
          (select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @policy), N'#',
          (select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policy))
        """, new { party = e.Policy.PartyId, policy = e.Policy.Id, claims = e.Claims });

    private sealed record Paid(Guid Claim, decimal Amount, decimal Withheld, decimal Offset, decimal Net, Guid? Receipt);

    private Task<List<Paid>> PaymentsAsync(Event e) => w.RowsAsync<Paid>("""
        select claim_id as Claim, amount as Amount, deductible_withheld as Withheld, premium_offset as Offset, net_paid as Net, offset_payment_id as Receipt
        from core.claim_payment where claim_id in @claims order by created_at
        """, new { claims = e.Claims });

    private Task<decimal> ReserveAsync(Guid claim) => w.ScalarAsync<decimal>("select coalesce(sum(change), 0) from core.claim_reserve where claim_id = @claim", new { claim });

    private Task<string> StatusAsync(Guid claim) => w.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim });

    // ---------------------------------------------------------------- N

    [Fact]
    public async Task N_equipment_100_paid_while_damage_100_was_paid_before_an_error_after_its_payment_row_leaves_nothing_the_retry_withholds_200_in_all_the_repeat_writes_nothing()
    {
        var e = await EventAsync(("damage", "100", "100"), ("equipment", "100", "100"));
        var (dmg, eqp) = (e.Claims[0], e.Claims[1]);
        var claims = await w.UserAsync("claims");
        foreach (var c in e.Claims) await w.ApproveAsync(claims, c, "100", e.Today);
        Assert.NotNull((await w.PayAsync(e.Accountant, dmg, "100", e.Today, e.Today)).RedirectTo);
        var before = await LedgerAsync(e);
        faults.Reset();
        var sentinel = new InjectedFault("N_RESERVE");
        faults.FailOnce(ReserveInsertOf(eqp), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => w.PayAsync(e.Accountant, eqp, "100", e.Today, e.Today)));
        Assert.True(Inserts(SentBefore(ReserveInsertOf(eqp)), "claim_payment"), "the payment row was not written before the failure");
        Assert.Equal(before, await LedgerAsync(e));
        Assert.Equal($"/claims/{eqp}?paid=1", (await w.PayAsync(e.Accountant, eqp, "100", e.Today, e.Today)).RedirectTo);
        var paid = await PaymentsAsync(e);
        Assert.Equal((2, 200m, 0m), (paid.Count, paid.Sum(p => p.Withheld), paid.Sum(p => p.Net)));
        Assert.Equal("paid", await StatusAsync(eqp));
        var settled = await LedgerAsync(e);
        Assert.Null((await w.PayAsync(e.Accountant, eqp, "100", e.Today, e.Today)).RedirectTo);
        Assert.Equal(settled, await LedgerAsync(e));
    }

    // ---------------------------------------------------------------- L3

    /// <summary>
    /// Legacy L3: a payment cancelled while it waits for the event's lock writes nothing; sent again it withholds as usual. The legacy
    /// script cancelled the waiting backend (pg_cancel_backend); here the statement taking the event's lock fails once with the
    /// interceptor's error (the wait itself: ClaimConcurrencyTests L1/L2). Damage and equipment, deductible 200 / 200, 100 each.
    /// </summary>
    [Fact]
    public async Task L3_a_payment_whose_event_lock_fails_writes_nothing_sent_again_it_withholds_as_usual()
    {
        var e = await EventAsync(("damage", "100", "100"), ("equipment", "100", "100"));
        var claims = await w.UserAsync("claims");
        foreach (var c in e.Claims) await w.ApproveAsync(claims, c, "100", e.Today);
        var before = await LedgerAsync(e);
        static bool EventLock(CommandEvent c) => Faults.Has(c, "select id from core.loss_event") && c.Isolation == System.Data.IsolationLevel.ReadCommitted;
        faults.Reset();
        var sentinel = new InjectedFault("L3_EVENT_LOCK");
        faults.FailOnce(EventLock, sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => w.PayAsync(e.Accountant, e.Claims[0], "100", e.Today, e.Today)));
        Assert.True(faults.AllFired);
        Assert.Equal(before, await LedgerAsync(e));
        foreach (var c in e.Claims) Assert.Equal($"/claims/{c}?paid=1", (await w.PayAsync(e.Accountant, c, "100", e.Today, e.Today)).RedirectTo);
        var paid = await PaymentsAsync(e);
        Assert.Equal((2, 200m, 0m), (paid.Count, paid.Sum(p => p.Withheld), paid.Sum(p => p.Net)));
    }

    // ---------------------------------------------------------------- O and P

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task O_P_an_ordinary_claim_with_the_set_off_or_a_total_loss_with_all_unpaid_set_off_an_error_after_the_receipt_its_allocation_and_the_payment_row_leaves_nothing_the_retry_once(bool totalLoss)
    {
        var e = await EventAsync(("damage", "1000", "1000"));
        var claim = e.Claims[0];
        await w.ApproveAsync(await w.UserAsync("claims"), claim, "1000", e.Today, totalLoss);
        var marker = $"LEV {(totalLoss ? "P" : "O")} ჩათვლა";
        Task<FormOutcome> Pay() => w.PayAsync(e.Accountant, claim, "1000", e.Today, e.Today, ("note", marker),
            totalLoss ? ("total_loss_mode", "all_unpaid") : ("offset_premium", "on"));
        var before = await LedgerAsync(e);
        var policyBefore = await w.ScalarAsync<string>("select concat(status, N':', effective_to, N':', cancelled_on) from core.policy where id = @id", new { id = e.Policy.Id });
        faults.Reset();
        var sentinel = new InjectedFault(totalLoss ? "P_RESERVE" : "O_RESERVE");
        faults.FailOnce(ReserveInsertOf(claim), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(Pay));
        var sent = SentBefore(ReserveInsertOf(claim));
        Assert.True(Inserts(sent, "payment") && Inserts(sent, "payment_allocation") && Inserts(sent, "claim_payment"), string.Join("\n", sent));
        Assert.Equal(before, await LedgerAsync(e));

        // The retry: one receipt claim_offset 405.00, two allocations of 202.50 to the two instalments, earliest first, one payment
        // 1000 / 200 / 405 / 395 linked to it, one new reserve row −1000 (reserve 0), the claim paid; policy and schedule unchanged.
        Assert.Equal($"/claims/{claim}?paid=1", (await Pay()).RedirectTo);
        var receipt = Assert.Single(await w.RowsAsync<(Guid Id, string Kind, string Method, decimal Gel, string Reference)>(
            "select id, kind, method, amount_gel, reference from core.payment where payer_party_id = @p", new { p = e.Policy.PartyId }));
        Assert.Equal(("receipt", "claim_offset", 405m, marker), (receipt.Kind, receipt.Method, receipt.Gel, receipt.Reference));
        Assert.Equal([(e.Policy.Installments[0], 202.50m), (e.Policy.Installments[1], 202.50m)], await w.RowsAsync<(Guid, decimal)>("""
            select a.installment_id, a.amount from core.payment_allocation a join core.installment i on i.id = a.installment_id where a.payment_id = @r order by i.due_on
            """, new { r = receipt.Id }));
        Assert.Equal([new Paid(claim, 1000m, 200m, 405m, 395m, receipt.Id)], await PaymentsAsync(e));
        Assert.Equal([1000m, -1000m], await w.RowsAsync<decimal>("select change from core.claim_reserve where claim_id = @claim order by created_at", new { claim }));
        Assert.Equal("paid", await StatusAsync(claim));
        Assert.Equal(policyBefore, await w.ScalarAsync<string>("select concat(status, N':', effective_to, N':', cancelled_on) from core.policy where id = @id", new { id = e.Policy.Id }));
        Assert.StartsWith("active:", policyBefore, StringComparison.Ordinal);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id", new { id = e.Policy.Id }));
        var settled = await LedgerAsync(e);
        Assert.Null((await Pay()).RedirectTo);
        Assert.Equal(settled, await LedgerAsync(e));
    }

    // ---------------------------------------------------------------- Q

    [Fact]
    public async Task Q_two_coverages_of_one_class_paid_with_the_set_off_an_error_after_the_seconds_payment_row_leaves_the_first_untouched_the_retry_pays_500_0_0_500_without_a_receipt()
    {
        var e = await EventAsync(("damage", "1000", "1000"), ("equipment", "500", "500"));
        var (dmg, eqp) = (e.Claims[0], e.Claims[1]);
        var claims = await w.UserAsync("claims");
        await w.ApproveAsync(claims, dmg, "1000", e.Today);
        await w.ApproveAsync(claims, eqp, "500", e.Today);
        const string marker = "LEV Q ჩათვლა";
        Task<FormOutcome> Pay(Guid claim, string amount) => w.PayAsync(e.Accountant, claim, amount, e.Today, e.Today, ("note", marker), ("offset_premium", "on"));

        // Q1: damage 1000 paid with the set-off — 1000 / 200 / 405 / 395 linked to one receipt of 405 with two allocations of 202.50.
        Assert.Equal($"/claims/{dmg}?paid=1", (await Pay(dmg, "1000")).RedirectTo);
        var receipt = Assert.Single(await w.RowsAsync<(Guid Id, decimal Gel)>("select id, amount_gel from core.payment where payer_party_id = @p", new { p = e.Policy.PartyId }));
        Assert.Equal(405m, receipt.Gel);
        Assert.Equal([new Paid(dmg, 1000m, 200m, 405m, 395m, receipt.Id)], await PaymentsAsync(e));
        Assert.Equal([202.50m, 202.50m], await w.RowsAsync<decimal>("select amount from core.payment_allocation where payment_id = @r", new { r = receipt.Id }));
        var before = await LedgerAsync(e);

        // Q2: equipment 500 with the set-off asked for; an error after its payment row — no receipt or allocation on this path.
        faults.Reset();
        var sentinel = new InjectedFault("Q_RESERVE");
        faults.FailOnce(ReserveInsertOf(eqp), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => Pay(eqp, "500")));
        var sent = SentBefore(ReserveInsertOf(eqp));
        Assert.True(Inserts(sent, "claim_payment") && !Inserts(sent, "payment") && !Inserts(sent, "payment_allocation"), string.Join("\n", sent));
        Assert.Equal(before, await LedgerAsync(e));

        // The retry: 500 / 0 / 0 / 500 with no receipt, reserve −500, both claims paid; in all 1500 / 200 / 405 / 895, one receipt, two allocations.
        Assert.Equal($"/claims/{eqp}?paid=1", (await Pay(eqp, "500")).RedirectTo);
        var paid = await PaymentsAsync(e);
        Assert.Equal([new Paid(dmg, 1000m, 200m, 405m, 395m, receipt.Id), new Paid(eqp, 500m, 0m, 0m, 500m, null)], paid);
        Assert.Equal((1500m, 200m, 405m, 895m), (paid.Sum(p => p.Amount), paid.Sum(p => p.Withheld), paid.Sum(p => p.Offset), paid.Sum(p => p.Net)));
        Assert.Single(await w.RowsAsync<Guid>("select id from core.payment where payer_party_id = @p", new { p = e.Policy.PartyId }));
        Assert.Equal(2, await w.ScalarAsync<int>("select count(*) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id", new { id = e.Policy.Id }));
        Assert.Equal((0m, 0m), (await ReserveAsync(dmg), await ReserveAsync(eqp)));
        Assert.Equal(("paid", "paid"), (await StatusAsync(dmg), await StatusAsync(eqp)));
        Assert.Equal("active", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = e.Policy.Id }));
        var settled = await LedgerAsync(e);
        Assert.Null((await Pay(eqp, "500")).RedirectTo);
        Assert.Equal(settled, await LedgerAsync(e));
    }
}
