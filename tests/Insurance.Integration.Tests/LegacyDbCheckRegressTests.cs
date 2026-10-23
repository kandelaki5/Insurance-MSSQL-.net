using System.Numerics;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using static Insurance.Integration.Tests.LegacyDbCheck;
using PartyRole = Insurance.Domain.Common.PartyRole;

namespace Insurance.Integration.Tests;

/// <summary>
/// The regress core checked on the real SQL Server as the legacy scripts/regress-db/check.ts checked it on PostgreSQL (D2–D20 of
/// docs/tests/2026-10-09-regress-core.md): fictional claims written by plain SQL as core_app, the real operations through the real
/// store, and plain SQL past the operations where only the triggers stand in the way; the races with two real sessions, one held
/// before its commit. Then the read acceptance R1–R3 (scripts/regress-db/acceptance.ts: the reader against hand-worked figures and
/// a plain-SQL oracle over the ledgers) and R5 (scripts/regress-db/isolation.ts: a committed write is not seen in an open snapshot
/// and is seen in a new one). In SQL Server a regress receipt is written with its movement by core.record_case_money
/// (database/README.md, difference 1), so the "plain SQL" receipt is that procedure called directly.
/// </summary>
public sealed class LegacyDbCheckRegressTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2760;
    private static BigInteger Lari(decimal n) => new(decimal.Round(n * 100));
    private static Guid Key() => Guid.NewGuid();

    private sealed class Run(LegacyDbCheckRegressTests t, int y, CurrentUser user, ClaimWorld.Policy gel, ClaimWorld.Policy usd, Guid liable)
    {
        public int Y { get; } = y;
        public CurrentUser User { get; } = user;
        public ClaimWorld.Policy Gel { get; } = gel;
        public ClaimWorld.Policy Usd { get; } = usd;
        public Guid Liable { get; } = liable;
        public SubrogationTransact Plain => SubrogationCases.InDatabase(t.w.App, User.Id);
        public DateOnly D(int month, int day) => new(Y, month, day);
        public List<Guid> Cases { get; } = [];
    }

    private async Task<Run> RunAsync()
    {
        var y = Interlocked.Increment(ref year);
        var user = await w.UserAsync("admin");
        await w.RateAsync("USD", new DateOnly(y, 1, 1), 2.7m);
        var gel = await w.PolicyAsync(user.Id, new DateOnly(y, 1, 1), [new("damage"), new("equipment"), new("theft"), new("assistance"), new("accident")]);
        var usd = await w.PolicyAsync(user.Id, new DateOnly(y, 1, 1), [new("damage")], currency: "USD", rate: 2.7m);
        return new Run(this, y, user, gel, usd, await w.PartyAsync(name: "ბრალეული"));
    }

    /// <summary>An approved claim of 5 000 on the line, in an event of its own or the one given (legacy claim()), written by plain SQL.</summary>
    private async Task<Guid> ClaimAsync(Run r, string coverage, Guid? eventId = null, bool usd = false, Guid? line = null)
    {
        var p = usd ? r.Usd : r.Gel;
        var ev = eventId ?? Guid.NewGuid();
        var id = Guid.NewGuid();
        var actor = r.User.Id;
        await w.App.AsUserAsync(actor, async tx =>
        {
            if (eventId is null)
                await tx.ExecuteAsync("insert core.loss_event (id, number, policy_id, occurred_on, reported_on, description) values (@ev, @n, @policy, @o, @rep, N'RGDB ავარია')",
                    new { ev, n = "RGDB-E-" + ClaimWorld.Tag(), policy = p.Id, o = r.D(6, 20), rep = r.D(6, 21) });
            await tx.ExecuteAsync("""
                insert core.claim (id, number, loss_event_id, policy_id, policy_line_id, currency, occurred_on, reported_on, description, status, approved_amount, created_by)
                values (@id, @n, @ev, @policy, @line, @currency, @o, @rep, N'RGDB ზარალი', N'approved', 5000, @actor)
                """, new { id, n = "RGDB-CL-" + ClaimWorld.Tag(), ev, policy = p.Id, line = line ?? p.LineOf(coverage), currency = usd ? "USD" : "GEL", o = r.D(6, 20), rep = r.D(6, 21), actor });
            return 0;
        });
        return id;
    }

    private async Task<Guid> EventOfAsync(Guid claim) => await w.ScalarAsync<Guid>("select loss_event_id from core.claim where id = @claim", new { claim });

    /// <summary>A claim payment as payClaim stores it: net and set-off in GEL at the rate, the set-off as a claim_offset receipt (legacy pay()).</summary>
    private async Task PayAsync(Run r, Guid claim, DateOnly on, decimal gross, decimal withheld = 0m, decimal offset = 0m, decimal rate = 1m, bool usd = false)
    {
        var party = (usd ? r.Usd : r.Gel).PartyId;
        await w.App.AsUserAsync(r.User.Id, async tx =>
        {
            Guid? receipt = null;
            if (offset > 0)
            {
                receipt = Guid.NewGuid();
                await tx.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference) values (@receipt, @on, @gel, N'claim_offset', N'receipt', @party, N'RGDB ჩათვლა')",
                    new { receipt, on, gel = Math.Round(offset * rate, 2), party });
            }
            var net = gross - withheld - offset;
            await tx.ExecuteAsync("""
                insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_payment_id)
                values (@claim, @on, @gross, @withheld, @offset, @net, @netGel, @rate, N'policyholder', @party, @receipt)
                """, new { claim, on, gross, withheld, offset, net, netGel = Math.Round(net * rate, 2), rate, party, receipt });
            return 0;
        });
    }

    /// <summary>Regress money from before cases: a plain claim_recovery row (legacy legacy()).</summary>
    private async Task<Guid> OldRecoveryAsync(Run r, Guid claim, string kind, DateOnly on, decimal amount)
    {
        var id = Guid.NewGuid();
        await w.App.AsUserAsync(r.User.Id, tx => tx.ExecuteAsync(
            "insert core.claim_recovery (id, claim_id, kind, received_on, amount_gel, note) values (@id, @claim, @kind, @on, @amount, N'RGDB ძველი')", new { id, claim, kind, on, amount }));
        return id;
    }

    /// <summary>A case opened through the service, with the liable person as its party (legacy opened()).</summary>
    private async Task<(Guid CaseId, Guid PayerId)> OpenedAsync(Run r, Guid claim, DateOnly? on = null)
    {
        var c = await SubrogationCases.OpenCaseAsync(r.Plain, claim, on ?? r.D(7, 2), null, Key());
        Assert.True(c.Ok, c.Error);
        var party = await SubrogationCases.AddPartyAsync(r.Plain, c.Id!.Value, r.Liable, PartyRole.LiablePerson, null, null, null);
        Assert.True(party.Ok, party.Error);
        r.Cases.Add(c.Id.Value);
        return (c.Id.Value, party.Id!.Value);
    }

    private Task<SubrogationResult> MoveAsync(Run r, Guid caseId, DocumentedMovement kind, decimal amount, DateOnly on, SubrogationTransact? transact = null) =>
        SubrogationCases.AddMovementAsync(transact ?? r.Plain, caseId, kind, Lari(amount), on,
            kind switch { DocumentedMovement.Claimed => DocumentKind.DemandLetter, DocumentedMovement.Confirmed => DocumentKind.DebtorAcceptance, _ => DocumentKind.CourtDecision },
            kind == DocumentedMovement.Cancelled ? "court-" + ClaimWorld.Tag() : null, on, null, Key());

    private Task<SubrogationResult> TakeAsync(Run r, Guid caseId, Guid payer, decimal amount, DateOnly on, SubrogationTransact? transact = null, string? bank = null) =>
        SubrogationCases.ReceiveAsync(transact ?? r.Plain, caseId, payer, Lari(amount), on, bank, null, Key());

    private Task<SubrogationResult> ReverseAsync(Run r, Guid caseId, Guid movement, DateOnly on, SubrogationTransact? transact = null) =>
        SubrogationCases.ReverseAsync(transact ?? r.Plain, caseId, movement, on, "RGDB დაბრუნება", Key());

    /// <summary>The service in a transaction that stops after the operation, before its commit, until released (legacy transactOn beforeCommit).</summary>
    private SubrogationTransact HeldBeforeCommit(Run r, TaskCompletionSource reached, Task release) => operation => w.App.AsUserAsync(r.User.Id, async tx =>
    {
        var result = await operation(new SqlSubrogationStore(tx));
        reached.TrySetResult();
        await release;
        return result;
    });

    private const string RawMovement = """
        insert core.subrogation_movement (case_id, kind, booked_on, amount_gel, reverses_movement_id, document_kind, document_number, document_date, note, request_key)
        values (@caseId, @kind, @on, @amount, @reverses, @document, @number, @on, @note, @key)
        """;

    private static object Movement(Guid caseId, string kind, decimal amount, DateOnly on, Guid? reverses = null, string? document = null, string? number = null, string? note = null, Guid? key = null) => new
    {
        caseId, kind, amount, on, reverses, number, key = key ?? Guid.NewGuid(), note = note ?? (kind == "reversal" ? "RGDB შესწორება" : null),
        document = document ?? kind switch { "claimed" => "demand_letter", "confirmed" => "debtor_acceptance", "reversal" => "correction", _ => "court_decision" },
    };

    /// <summary>Money of a case with its movement, by core.record_case_money called directly (receipt, or the reversal of one).</summary>
    private static Task RawMoney(Held h, Guid claim, Guid caseId, decimal amount, DateOnly on, Guid? payer = null, string? bank = null, Guid? reversesRecovery = null, Guid? reversesMovement = null) =>
        h.Run("""
            exec core.record_case_money @recovery_id = @rid, @claim_id = @claim, @case_id = @caseId, @row_kind = @rowKind, @reverses_recovery_id = @reversesRecovery,
                 @received_on = @on, @amount_gel = @amount, @bank_reference = @bank, @recovery_note = null, @created_by = null,
                 @movement_id = @mid, @movement_kind = @movementKind, @reverses_movement_id = @reversesMovement, @payer_id = @payer,
                 @document_kind = @document, @document_number = null, @document_date = @on, @movement_note = @note, @request_key = @key
            """, new
        {
            rid = Guid.NewGuid(), claim, caseId, rowKind = reversesRecovery is null ? "receipt" : "reversal", reversesRecovery, on, amount, bank, mid = Guid.NewGuid(),
            movementKind = reversesRecovery is null ? "received" : "reversal", reversesMovement, payer, document = reversesRecovery is null ? "bank_statement" : "correction",
            note = reversesRecovery is null ? null : "RGDB raw", key = Guid.NewGuid(),
        });

    /// <summary>Plain SQL as core_app in one transaction of its own, committed when it goes through.</summary>
    private async Task<string> AsAppAsync(Run r, Func<Held, Task> run)
    {
        await using var h = await HoldAsync(db, r.User.Id);
        var code = await CodeOf(async () => { await run(h); await h.CommitAsync(); });
        return code;
    }

    private async Task<(string Balance, string Status)> TotalsAsync(Guid caseId) =>
        (await w.RowsAsync<(decimal, string)>("""
            select s.confirmed - s.received - s.cancelled, (select status from core.subrogation_case where id = @caseId) from core.subrogation_totals(@caseId, null) s
            """, new { caseId })).Select(x => (x.Item1.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), x.Item2)).Single();

    private async Task<int> RowCountAsync(Guid caseId) => await w.ScalarAsync<int>(
        "select (select count(*) from core.subrogation_movement where case_id = @caseId) + (select count(*) from core.claim_recovery where subrogation_case_id = @caseId)", new { caseId });

    /// <summary>Legacy "every day": on no day of any case of the run was more received or cancelled than confirmed.</summary>
    private async Task AssertEveryDayAsync(Run r)
    {
        if (r.Cases.Count == 0) return;
        Assert.Equal(0, await w.ScalarAsync<int>("""
            select count(*) from core.subrogation_case c
            cross apply (select distinct booked_on as d from core.subrogation_movement where case_id = c.id) days
            cross apply core.subrogation_totals(c.id, days.d) s
            where c.id in @cases and s.confirmed - s.received - s.cancelled < 0
            """, new { cases = r.Cases }));
    }

    // ---------------------------------------------------------------- D2

    [Fact]
    public async Task D2_a_case_by_plain_SQL_on_class_14_class_3_2_theft_or_a_line_without_coverage_is_refused_and_a_USD_damage_claim_opens()
    {
        var r = await RunAsync();
        var bare = Guid.NewGuid();
        await w.ExecAsync("insert core.policy_line (id, policy_id, class_code, sum_insured, premium, premium_gel) values (@bare, @policy, N'5.1', 20000, 0, 0)", new { bare, policy = r.Gel.Id });
        foreach (var (what, claim, expected) in new[]
                 {
                     ("assistance", await ClaimAsync(r, "assistance"), "SG001"), ("accident", await ClaimAsync(r, "accident"), "SG001"),
                     ("theft", await ClaimAsync(r, "theft"), "SG003"), ("no coverage", await ClaimAsync(r, "", line: bare), "SG002"),
                     ("USD damage", await ClaimAsync(r, "damage", usd: true), "ok"),
                 })
        {
            var code = await AsAppAsync(r, h => h.Run("insert core.subrogation_case (number, claim_id, opened_on, request_key) values (@n, @claim, @on, newid())",
                new { n = "RG-" + ClaimWorld.Tag(), claim, on = r.D(7, 2) }));
            Assert.Equal($"{what} {expected}", $"{what} {code}");
            Assert.Equal(expected == "ok" ? 1 : 0, await w.ScalarAsync<int>("select count(*) from core.subrogation_case where claim_id = @claim", new { claim }));
        }
    }

    // ---------------------------------------------------------------- D3–D12 on claim A's case

    private sealed record CaseA(Run R, Guid ClaimA, Guid ClaimB, Guid CaseId, Guid PayerId, Guid FirstReceipt);

    /// <summary>D3: claim A (1 000 paid) and B (500 paid, salvage 20) of one event; A's case 1 000 confirmed, 600 and 200 received, the 600 reversed.</summary>
    private async Task<CaseA> CaseAAsync()
    {
        var r = await RunAsync();
        var claimA = await ClaimAsync(r, "damage");
        var claimB = await ClaimAsync(r, "equipment", await EventOfAsync(claimA));
        await PayAsync(r, claimA, r.D(7, 1), 1000m);
        await PayAsync(r, claimB, r.D(7, 1), 500m);
        await OldRecoveryAsync(r, claimB, "salvage", r.D(7, 5), 20m);
        var (caseId, payer) = await OpenedAsync(r, claimA);
        Assert.True((await MoveAsync(r, caseId, DocumentedMovement.Claimed, 1000m, r.D(7, 3))).Ok);
        Assert.True((await MoveAsync(r, caseId, DocumentedMovement.Confirmed, 1000m, r.D(7, 4))).Ok);
        var r1 = await TakeAsync(r, caseId, payer, 600m, r.D(7, 10), bank: "RGDB-BANK-" + ClaimWorld.Tag());
        Assert.True(r1.Ok, r1.Error);
        Assert.Equal(("400.00", "partially_received"), await TotalsAsync(caseId));
        Assert.True((await TakeAsync(r, caseId, payer, 200m, r.D(7, 11), bank: "RGDB-BANK-" + ClaimWorld.Tag())).Ok);
        Assert.Equal("200.00", (await TotalsAsync(caseId)).Balance);
        Assert.True((await ReverseAsync(r, caseId, r1.Id!.Value, r.D(7, 20))).Ok);
        Assert.Equal(("800.00", "partially_received"), await TotalsAsync(caseId));
        return new CaseA(r, claimA, claimB, caseId, payer, r1.Id.Value);
    }

    [Fact]
    public async Task D3_D4_every_movement_is_audited_with_its_author_and_claim_B_of_the_same_event_is_untouched()
    {
        var a = await CaseAAsync();
        Assert.Equal(["600.00", "200.00", "-600.00"], (await w.RowsAsync<decimal>("select amount_gel from core.claim_recovery where subrogation_case_id = @c order by created_at", new { c = a.CaseId }))
            .Select(x => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(5, await w.ScalarAsync<int>("""
            select count(*) from core.audit_log where table_name = N'subrogation_movement' and action = N'insert' and user_id = @u
              and row_id in (select lower(convert(nchar(36), id)) from core.subrogation_movement where case_id = @c)
            """, new { u = a.R.User.Id, c = a.CaseId }));
        Assert.Equal(5, await w.ScalarAsync<int>("select count(*) from core.subrogation_movement where case_id = @c", new { c = a.CaseId }));
        // D4: claim B keeps exactly its salvage of 20 and has no case.
        Assert.Equal([("salvage", 20m, (Guid?)null)], await w.RowsAsync<(string, decimal, Guid?)>("select kind, amount_gel, subrogation_case_id from core.claim_recovery where claim_id = @b", new { b = a.ClaimB }));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.subrogation_case where claim_id = @b", new { b = a.ClaimB }));
    }

    [Fact]
    public async Task D5_D6_the_same_request_key_or_bank_reference_is_23505_and_claim_Bs_money_on_claim_As_case_is_SG031_with_no_row()
    {
        var a = await CaseAAsync();
        var r = a.R;
        var before = await RowCountAsync(a.CaseId);
        var (key, reference) = (await w.RowsAsync<(Guid, string)>("""
            select top (1) m.request_key, cr.bank_reference from core.subrogation_movement m join core.claim_recovery cr on cr.id = m.claim_recovery_id
            where m.case_id = @c and m.kind = N'received' order by m.created_at
            """, new { c = a.CaseId })).Single();
        // A court cancellation of 1 would pass every rule; only the reused key stops it.
        Assert.Equal("23505", Bare(await AsAppAsync(r, h => h.Run(RawMovement, Movement(a.CaseId, "cancelled", 1m, r.D(7, 21), number: "court-1", key: key)))));
        Assert.Equal("23505", Bare(await AsAppAsync(r, h => RawMoney(h, a.ClaimA, a.CaseId, 1m, r.D(7, 21), a.PayerId, reference))));
        Assert.Equal(before, await RowCountAsync(a.CaseId));
        Assert.Equal("SG031", await AsAppAsync(r, h => h.Run("""
            insert core.claim_recovery (claim_id, kind, subrogation_case_id, row_kind, received_on, amount_gel) values (@b, N'subrogation', @c, N'receipt', @on, 10)
            """, new { b = a.ClaimB, c = a.CaseId, on = r.D(7, 21) })));
        Assert.Equal(before, await RowCountAsync(a.CaseId));
    }

    /// <summary>The code without the constraint's name.</summary>
    private static string Bare(string code) => code.Split(' ')[0];

    [Fact]
    public async Task D7_the_cap_by_plain_SQL_is_SG020_above_what_was_paid_or_before_the_payment_and_a_USD_claim_counts_the_GEL_paid()
    {
        var r = await RunAsync();
        var claimC = await ClaimAsync(r, "damage");
        await PayAsync(r, claimC, r.D(7, 15), 1000m, 200m);
        var (c, _) = await OpenedAsync(r, claimC);
        Task<string> Claimed(Guid caseId, decimal amount, DateOnly on) => AsAppAsync(r, h => h.Run(RawMovement, Movement(caseId, "claimed", amount, on)));
        Assert.Equal("SG020", await Claimed(c, 800.01m, r.D(7, 16)));
        Assert.Equal("SG020", await Claimed(c, 500m, r.D(7, 14)));
        Assert.Equal("ok", await Claimed(c, 800m, r.D(7, 16)));
        // R1: 700 × 2.70 net + 100 × 2.70 set off = 2 160 GEL paid.
        var claimUsd = await ClaimAsync(r, "damage", usd: true);
        await PayAsync(r, claimUsd, r.D(7, 1), 1000m, 200m, 100m, 2.7m, usd: true);
        var (u, _) = await OpenedAsync(r, claimUsd);
        Assert.Equal("cap", (await MoveAsync(r, u, DocumentedMovement.Claimed, 2160.01m, r.D(7, 3))).Error);
        Assert.Equal("SG020", await Claimed(u, 2160.01m, r.D(7, 3)));
        Assert.True((await MoveAsync(r, u, DocumentedMovement.Claimed, 2160m, r.D(7, 3))).Ok);
        await AssertEveryDayAsync(r);
    }

    [Fact]
    public async Task D8_a_movement_and_a_receipt_dated_in_a_closed_month_are_23514_with_no_row()
    {
        var a = await CaseAAsync();
        var r = a.R;
        await w.CloseMonthAsync(r.User.Id, r.D(5, 1));
        var before = await RowCountAsync(a.CaseId);
        Assert.Equal("23514", Bare(await AsAppAsync(r, h => h.Run(RawMovement, Movement(a.CaseId, "claimed", 1m, r.D(5, 20))))));
        Assert.Equal("23514", Bare(await AsAppAsync(r, h => RawMoney(h, a.ClaimA, a.CaseId, 1m, r.D(5, 20), a.PayerId))));
        Assert.Equal(before, await RowCountAsync(a.CaseId));
    }

    [Fact]
    public async Task D9_D10_D11_reversals_that_may_not_be_a_cancellation_without_the_courts_number_and_closing_with_money_owed_are_refused()
    {
        var a = await CaseAAsync();
        var r = a.R;
        var ms = await w.RowsAsync<(Guid Id, string Kind, decimal Amount, Guid? Reverses, Guid? Recovery)>(
            "select id, kind, amount_gel, reverses_movement_id, claim_recovery_id from core.subrogation_movement where case_id = @c order by created_at", new { c = a.CaseId });
        var first = ms.Single(m => m.Id == a.FirstReceipt);
        var mirror = ms.Single(m => m.Reverses == a.FirstReceipt);
        var second = ms.Single(m => m.Kind == "received" && m.Id != a.FirstReceipt);
        Task<string> Rev(Guid target, decimal amount, DateOnly on) => AsAppAsync(r, h => h.Run(RawMovement, Movement(a.CaseId, "reversal", amount, on, reverses: target)));
        Assert.Equal("SG011", await Rev(first.Id, -first.Amount, r.D(7, 21)));
        Assert.Equal("SG010", await Rev(mirror.Id, first.Amount, r.D(7, 21)));
        Assert.Equal("SG012", await Rev(second.Id, -100m, r.D(7, 21)));
        Assert.Equal("SG013", await Rev(second.Id, -second.Amount, r.D(7, 5)));
        Assert.Equal("23505", Bare(await AsAppAsync(r, h => RawMoney(h, a.ClaimA, a.CaseId, -first.Amount, r.D(7, 21), reversesRecovery: first.Recovery, reversesMovement: first.Id))));
        // D10: no write-off — a cancellation needs a court decision with its number.
        Assert.Equal("23514", Bare(await AsAppAsync(r, h => h.Run(RawMovement, Movement(a.CaseId, "cancelled", 100m, r.D(7, 21), document: "court_decision")))));
        Assert.Equal("23514", Bare(await AsAppAsync(r, h => h.Run(RawMovement, Movement(a.CaseId, "cancelled", 100m, r.D(7, 21), document: "correction", number: "N1")))));
        // D11: 800 still owed.
        Assert.Equal("SG006", await AsAppAsync(r, h => h.Run("update core.subrogation_case set status = N'closed' where id = @c", new { c = a.CaseId })));
        Assert.Equal(("800.00", "partially_received"), await TotalsAsync(a.CaseId));
        await AssertEveryDayAsync(r);
    }

    [Fact]
    public async Task Rights_core_app_cannot_change_or_delete_the_ledger_the_case_or_the_money()
    {
        var a = await CaseAAsync();
        var r = a.R;
        foreach (var sql in new[]
                 {
                     "update core.subrogation_movement set note = N'x' where case_id = @c", "delete from core.subrogation_movement where case_id = @c",
                     "delete from core.subrogation_case where id = @c", "update core.claim_recovery set amount_gel = 1 where subrogation_case_id = @c",
                 })
            Assert.Equal($"{sql}: 42501", $"{sql}: {await AsAppAsync(r, h => h.Run(sql, new { c = a.CaseId }))}");
    }

    // ---------------------------------------------------------------- D13 / D14 / D15 / D17 / D19: races on one claim

    [Fact]
    public async Task D13_two_receipts_of_400_at_once_against_700_owed_the_second_waits_on_the_claim_lock_and_is_SG021_balance_300()
    {
        var r = await RunAsync();
        var claim = await ClaimAsync(r, "damage");
        await PayAsync(r, claim, r.D(7, 1), 1000m);
        var (caseId, payer) = await OpenedAsync(r, claim);
        Assert.True((await MoveAsync(r, caseId, DocumentedMovement.Confirmed, 700m, r.D(7, 4))).Ok);
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var a = TakeAsync(r, caseId, payer, 400m, r.D(7, 23), HeldBeforeCommit(r, reached, release.Task));
        await reached.Task;
        var b = AsAppAsync(r, h => RawMoney(h, claim, caseId, 400m, r.D(7, 23), payer));
        await AssertWaitingAsync(b);
        release.SetResult();
        Assert.True((await a).Ok);
        Assert.Equal("SG021", await b);
        Assert.Equal("300.00", (await TotalsAsync(caseId)).Balance);
        await AssertEveryDayAsync(r);
    }

    [Fact]
    public async Task D14_the_same_receipt_reversed_twice_at_once_the_second_waits_and_is_refused_and_the_balance_is_back()
    {
        var r = await RunAsync();
        var claim = await ClaimAsync(r, "damage");
        await PayAsync(r, claim, r.D(7, 1), 1000m);
        var (caseId, payer) = await OpenedAsync(r, claim);
        Assert.True((await MoveAsync(r, caseId, DocumentedMovement.Confirmed, 700m, r.D(7, 4))).Ok);
        var taken = await TakeAsync(r, caseId, payer, 400m, r.D(7, 23));
        var recovery = await w.ScalarAsync<Guid>("select claim_recovery_id from core.subrogation_movement where id = @id", new { id = taken.Id });
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var a = ReverseAsync(r, caseId, taken.Id!.Value, r.D(7, 24), HeldBeforeCommit(r, reached, release.Task));
        await reached.Task;
        var b = AsAppAsync(r, h => RawMoney(h, claim, caseId, -400m, r.D(7, 24), reversesRecovery: recovery, reversesMovement: taken.Id));
        await AssertWaitingAsync(b);
        release.SetResult();
        Assert.True((await a).Ok);
        Assert.Contains((await b).Split(' ')[0], new[] { "23505", "SG011" });
        Assert.Equal("700.00", (await TotalsAsync(caseId)).Balance);
        await AssertEveryDayAsync(r);
    }

    [Fact]
    public async Task D15_one_case_per_claim_a_second_by_plain_SQL_is_23505_and_two_at_once_the_second_waits_and_is_23505()
    {
        var r = await RunAsync();
        var claimF = await ClaimAsync(r, "damage");
        await PayAsync(r, claimF, r.D(7, 1), 300m);
        var (f, payer) = await OpenedAsync(r, claimF);
        Assert.True((await MoveAsync(r, f, DocumentedMovement.Confirmed, 300m, r.D(7, 4))).Ok);
        Assert.True((await TakeAsync(r, f, payer, 300m, r.D(7, 10))).Ok);
        Assert.Equal("closed", (await TotalsAsync(f)).Status);
        Assert.Equal("open", (await SubrogationCases.OpenCaseAsync(r.Plain, claimF, r.D(7, 11), null, Key())).Error);
        const string RawCase = "insert core.subrogation_case (number, claim_id, opened_on, request_key) values (@n, @claim, @on, newid())";
        Assert.Equal("23505", (await AsAppAsync(r, h => h.Run(RawCase, new { n = "RG-" + ClaimWorld.Tag(), claim = claimF, on = r.D(7, 11) }))).Split(' ')[0]);

        var claimG = await ClaimAsync(r, "damage");
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var a = SubrogationCases.OpenCaseAsync(HeldBeforeCommit(r, reached, release.Task), claimG, r.D(7, 11), null, Key());
        await reached.Task;
        var b = AsAppAsync(r, h => h.Run(RawCase, new { n = "RG-" + ClaimWorld.Tag(), claim = claimG, on = r.D(7, 11) }));
        await AssertWaitingAsync(b);
        release.SetResult();
        Assert.True((await a).Ok);
        Assert.Equal("23505", (await b).Split(' ')[0]);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.subrogation_case where claim_id = @claimG", new { claimG }));
    }

    [Fact]
    public async Task D16_old_subrogation_takes_its_share_of_the_cap_salvage_does_not_and_new_caseless_subrogation_is_SG032()
    {
        var r = await RunAsync();
        var claimL = await ClaimAsync(r, "damage");
        await PayAsync(r, claimL, r.D(7, 1), 500m);
        await OldRecoveryAsync(r, claimL, "subrogation", r.D(7, 10), 50m);
        await OldRecoveryAsync(r, claimL, "salvage", r.D(7, 12), 30m);
        var (l, _) = await OpenedAsync(r, claimL, r.D(7, 13));
        Assert.Equal("SG020", await AsAppAsync(r, h => h.Run(RawMovement, Movement(l, "claimed", 450.01m, r.D(7, 20)))));
        Assert.True((await MoveAsync(r, l, DocumentedMovement.Claimed, 450m, r.D(7, 20))).Ok);
        Assert.Equal("SG032", await AsAppAsync(r, h => h.Run("insert core.claim_recovery (claim_id, kind, received_on, amount_gel) values (@claimL, N'subrogation', @on, 5)",
            new { claimL, on = r.D(7, 21) })));
    }

    [Fact]
    public async Task D17_a_confirmation_of_950_while_the_old_50_is_given_back_at_once_the_second_waits_both_go_and_the_cap_follows_the_day()
    {
        var r = await RunAsync();
        var claimM = await ClaimAsync(r, "damage");
        await PayAsync(r, claimM, r.D(7, 1), 1000m);
        var old = await OldRecoveryAsync(r, claimM, "subrogation", r.D(7, 5), 50m);
        var (m, _) = await OpenedAsync(r, claimM, r.D(7, 6));
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var a = MoveAsync(r, m, DocumentedMovement.Confirmed, 950m, r.D(7, 10), HeldBeforeCommit(r, reached, release.Task));
        await reached.Task;
        var b = AsAppAsync(r, h => h.Run("""
            insert core.claim_recovery (claim_id, kind, row_kind, reverses_claim_recovery_id, received_on, amount_gel, note)
            values (@claimM, N'subrogation', N'reversal', @old, @on, -50, N'RGDB ძველის დაბრუნება')
            """, new { claimM, old, on = r.D(7, 25) }));
        await AssertWaitingAsync(b);
        release.SetResult();
        Assert.True((await a).Ok);
        Assert.Equal("ok", await b);
        Assert.Equal("cap", (await MoveAsync(r, m, DocumentedMovement.Confirmed, 50m, r.D(7, 20))).Error);
        Assert.True((await MoveAsync(r, m, DocumentedMovement.Confirmed, 50m, r.D(7, 25))).Ok);
    }

    [Fact]
    public async Task D18_a_later_reversal_or_confirmation_does_not_cover_an_earlier_over_receipt_in_the_service_or_by_plain_SQL()
    {
        var r = await RunAsync();
        var claimN = await ClaimAsync(r, "damage");
        await PayAsync(r, claimN, r.D(7, 1), 1000m);
        var (n, payerN) = await OpenedAsync(r, claimN, r.D(7, 1));
        Assert.True((await MoveAsync(r, n, DocumentedMovement.Confirmed, 1000m, r.D(7, 1))).Ok);
        var r1 = await TakeAsync(r, n, payerN, 600m, r.D(7, 10));
        Assert.True((await ReverseAsync(r, n, r1.Id!.Value, r.D(7, 20))).Ok);
        Assert.Equal("balance", (await TakeAsync(r, n, payerN, 600m, r.D(7, 11))).Error);
        Assert.Equal("SG021", await AsAppAsync(r, h => RawMoney(h, claimN, n, 600m, r.D(7, 11), payerN)));
        Assert.True((await TakeAsync(r, n, payerN, 600m, r.D(7, 20))).Ok);

        var claimO = await ClaimAsync(r, "damage");
        await PayAsync(r, claimO, r.D(7, 1), 1000m);
        var (o, payerO) = await OpenedAsync(r, claimO, r.D(7, 1));
        Assert.True((await MoveAsync(r, o, DocumentedMovement.Confirmed, 1000m, r.D(7, 20))).Ok);
        Assert.Equal("SG021", await AsAppAsync(r, h => RawMoney(h, claimO, o, 600m, r.D(7, 10), payerO)));
        Assert.Equal("SG021", await AsAppAsync(r, h => h.Run(RawMovement, Movement(o, "cancelled", 400m, r.D(7, 10), number: "court-" + ClaimWorld.Tag()))));
        await AssertEveryDayAsync(r);
    }

    [Fact]
    public async Task D19_600_on_10_07_and_600_on_05_07_at_once_against_1000_confirmed_the_second_waits_and_is_SG021()
    {
        var r = await RunAsync();
        var claimQ = await ClaimAsync(r, "damage");
        await PayAsync(r, claimQ, r.D(7, 1), 1000m);
        var (q, payer) = await OpenedAsync(r, claimQ, r.D(7, 1));
        Assert.True((await MoveAsync(r, q, DocumentedMovement.Confirmed, 1000m, r.D(7, 1))).Ok);
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var a = TakeAsync(r, q, payer, 600m, r.D(7, 10), HeldBeforeCommit(r, reached, release.Task));
        await reached.Task;
        var b = AsAppAsync(r, h => RawMoney(h, claimQ, q, 600m, r.D(7, 5), payer));
        await AssertWaitingAsync(b);
        release.SetResult();
        Assert.True((await a).Ok);
        Assert.Equal("SG021", await b);
        await AssertEveryDayAsync(r);
    }

    // ---------------------------------------------------------------- R1–R3: the reader against hand-worked figures and an SQL oracle

    private sealed record Fixture(Run R, Guid D3, Guid D20, Guid D16, Guid ClaimB, Guid ClaimL, Guid ClaimM, Guid D19);

    /// <summary>The fixtures acceptance.ts relied on, as check.ts wrote them: D3 (with D12–D14), D20, D16, D4's salvage, D17's old 50 given back, D19.</summary>
    private async Task<Fixture> FixtureAsync()
    {
        var r = await RunAsync();
        var claimA = await ClaimAsync(r, "damage");
        var claimB = await ClaimAsync(r, "equipment", await EventOfAsync(claimA));
        await PayAsync(r, claimA, r.D(7, 1), 1000m);
        await PayAsync(r, claimB, r.D(7, 1), 500m);
        await OldRecoveryAsync(r, claimB, "salvage", r.D(7, 5), 20m);
        var (d3, p3) = await OpenedAsync(r, claimA);
        Assert.True((await MoveAsync(r, d3, DocumentedMovement.Claimed, 1000m, r.D(7, 3))).Ok);
        Assert.True((await MoveAsync(r, d3, DocumentedMovement.Confirmed, 1000m, r.D(7, 4))).Ok);
        var r600 = await TakeAsync(r, d3, p3, 600m, r.D(7, 10));
        Assert.True((await TakeAsync(r, d3, p3, 200m, r.D(7, 11))).Ok);
        Assert.True((await ReverseAsync(r, d3, r600.Id!.Value, r.D(7, 20))).Ok);
        Assert.True((await TakeAsync(r, d3, p3, 100m, r.D(7, 22))).Ok);
        var r400 = await TakeAsync(r, d3, p3, 400m, r.D(7, 23));
        Assert.True((await ReverseAsync(r, d3, r400.Id!.Value, r.D(7, 24))).Ok);

        var claimR = await ClaimAsync(r, "damage");
        await PayAsync(r, claimR, r.D(7, 1), 1000m);
        var (d20, p20) = await OpenedAsync(r, claimR);
        Assert.True((await MoveAsync(r, d20, DocumentedMovement.Confirmed, 1000m, r.D(7, 1))).Ok);
        var x600 = await TakeAsync(r, d20, p20, 600m, r.D(7, 10));
        Assert.True((await TakeAsync(r, d20, p20, 400m, r.D(7, 15))).Ok);
        Assert.Equal("closed", (await TotalsAsync(d20)).Status);
        Assert.True((await ReverseAsync(r, d20, x600.Id!.Value, r.D(7, 20))).Ok);

        var claimL = await ClaimAsync(r, "damage");
        await PayAsync(r, claimL, r.D(7, 1), 500m);
        await OldRecoveryAsync(r, claimL, "subrogation", r.D(7, 10), 50m);
        await OldRecoveryAsync(r, claimL, "salvage", r.D(7, 12), 30m);
        var (d16, _) = await OpenedAsync(r, claimL, r.D(7, 13));
        Assert.True((await MoveAsync(r, d16, DocumentedMovement.Claimed, 450m, r.D(7, 20))).Ok);

        var claimM = await ClaimAsync(r, "damage");
        await PayAsync(r, claimM, r.D(7, 1), 1000m);
        var old = await OldRecoveryAsync(r, claimM, "subrogation", r.D(7, 5), 50m);
        var (d17, _) = await OpenedAsync(r, claimM, r.D(7, 6));
        Assert.True((await MoveAsync(r, d17, DocumentedMovement.Confirmed, 950m, r.D(7, 10))).Ok);
        Assert.Equal("ok", await AsAppAsync(r, h => h.Run("""
            insert core.claim_recovery (claim_id, kind, row_kind, reverses_claim_recovery_id, received_on, amount_gel) values (@claimM, N'subrogation', N'reversal', @old, @on, -50)
            """, new { claimM, old, on = r.D(7, 25) })));

        var claimQ = await ClaimAsync(r, "damage");
        await PayAsync(r, claimQ, r.D(7, 1), 1000m);
        var (d19, p19) = await OpenedAsync(r, claimQ, r.D(7, 1));
        Assert.True((await MoveAsync(r, d19, DocumentedMovement.Confirmed, 1000m, r.D(7, 1))).Ok);
        Assert.True((await TakeAsync(r, d19, p19, 600m, r.D(7, 10))).Ok);
        return new Fixture(r, d3, d20, d16, claimB, claimL, claimM, d19);
    }

    private const string OracleSums = """
        coalesce(sum(case when coalesce(o.kind, m.kind) = N'claimed' then m.amount_gel end), 0) as Claimed,
        coalesce(sum(case when coalesce(o.kind, m.kind) = N'confirmed' then m.amount_gel end), 0) as Confirmed,
        coalesce(sum(case when coalesce(o.kind, m.kind) = N'received' then m.amount_gel end), 0) as Received,
        coalesce(sum(case when coalesce(o.kind, m.kind) = N'cancelled' then m.amount_gel end), 0) as Cancelled
        """;

    private sealed record Sums(string CaseNumber, decimal Claimed, decimal Confirmed, decimal Received, decimal Cancelled)
    {
        public decimal Balance => Confirmed - Received - Cancelled;
    }

    /// <summary>The oracle on a date: per case of the run opened by then, sums by the kind each movement (or the original of a reversal) has.</summary>
    private Task<List<Sums>> OracleOnAsync(Run r, DateOnly asOf) => w.RowsAsync<Sums>($"""
        select c.number as CaseNumber, {OracleSums}
        from core.subrogation_case c
        left join core.subrogation_movement m on m.case_id = c.id and m.booked_on <= @asOf
        left join core.subrogation_movement o on o.id = m.reverses_movement_id
        where c.opened_on <= @asOf and c.id in @cases
        group by c.number order by c.number
        """, new { asOf, cases = r.Cases });

    private Task<List<Sums>> OraclePeriodAsync(Run r, DateOnly from, DateOnly to) => w.RowsAsync<Sums>($"""
        select c.number as CaseNumber, {OracleSums}
        from core.subrogation_case c
        left join core.subrogation_movement m on m.case_id = c.id and m.booked_on between @from and @to
        left join core.subrogation_movement o on o.id = m.reverses_movement_id
        where c.opened_on <= @to and c.id in @cases
        group by c.number order by c.number
        """, new { from, to, cases = r.Cases });

    private static string Text(BigInteger t) => (t / 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + BigInteger.Abs(t % 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    private static string Line(decimal claimed, decimal confirmed, decimal received, decimal cancelled, decimal balance) =>
        string.Join(" ", new[] { claimed, confirmed, received, cancelled, balance }.Select(v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));

    private static string Line(BigInteger claimed, BigInteger confirmed, BigInteger received, BigInteger cancelled, BigInteger balance) =>
        string.Join(" ", new[] { claimed, confirmed, received, cancelled, balance }.Select(v => (v < 0 ? "-" : "") + Text(BigInteger.Abs(v))));

    [Fact]
    public async Task R1_R2_the_cards_day_by_day_equal_the_hand_worked_figures_and_the_SQL_oracle()
    {
        var f = await FixtureAsync();
        var r = f.R;
        var today = r.D(7, 31);
        // R1: claim A's case on each day (claimed, confirmed, received, cancelled, balance) as worked out by hand.
        var d3Days = new Dictionary<int, string>
        {
            [2] = "0.00 0.00 0.00 0.00 0.00", [3] = "1000.00 0.00 0.00 0.00 0.00", [4] = "1000.00 1000.00 0.00 0.00 1000.00", [10] = "1000.00 1000.00 600.00 0.00 400.00",
            [11] = "1000.00 1000.00 800.00 0.00 200.00", [20] = "1000.00 1000.00 200.00 0.00 800.00", [22] = "1000.00 1000.00 300.00 0.00 700.00",
            [23] = "1000.00 1000.00 700.00 0.00 300.00", [24] = "1000.00 1000.00 300.00 0.00 700.00", [31] = "1000.00 1000.00 300.00 0.00 700.00",
        };
        var d20Days = new Dictionary<int, string>
        {
            [2] = "0.00 1000.00 0.00 0.00 1000.00", [10] = "0.00 1000.00 600.00 0.00 400.00", [15] = "0.00 1000.00 1000.00 0.00 0.00",
            [20] = "0.00 1000.00 400.00 0.00 600.00", [31] = "0.00 1000.00 400.00 0.00 600.00",
        };
        var numbers = await w.RowsAsync<(Guid, string)>("select id, number from core.subrogation_case where id in @cases", new { cases = r.Cases });
        foreach (var (caseId, days) in new[] { (f.D3, d3Days), (f.D20, d20Days) })
            foreach (var (day, want) in days)
            {
                var asOf = r.D(7, day);
                var card = await SubrogationReportStore.CaseCardAsync(w.App, caseId, asOf, today);
                Assert.NotNull(card);
                var t = card.Totals;
                Assert.Equal($"{day}: {want}", $"{day}: {Line(t.Claimed, t.Confirmed, t.Received, t.Cancelled, t.Balance)}");
                var oracle = (await OracleOnAsync(r, asOf)).Single(o => o.CaseNumber == numbers.Single(n => n.Item1 == caseId).Item2);
                Assert.Equal($"{day}: {Line(oracle.Claimed, oracle.Confirmed, oracle.Received, oracle.Cancelled, oracle.Balance)}", $"{day}: {want}");
            }
        // R1: the history on 31.07 in the order shown, reversals after their originals, with the balance after each line.
        var d3 = (await SubrogationReportStore.CaseCardAsync(w.App, f.D3, today, today))!;
        Assert.Equal(["claimed 1000.00 → 0.00", "confirmed 1000.00 → 1000.00", "received 600.00 → 400.00", "received 200.00 → 200.00", "reversal -600.00 → 800.00",
                      "received 100.00 → 700.00", "received 400.00 → 300.00", "reversal -400.00 → 700.00"],
            d3.History.Select(h => $"{h.Kind.ToString().ToLowerInvariant()} {(h.Amount < 0 ? "-" : "")}{Text(BigInteger.Abs(h.Amount))} → {Text(h.BalanceAfter)}"));
        Assert.Equal([Insurance.Domain.Finance.PartyRole.LiablePerson], d3.Parties.Select(p => p.Role));
        // R2: confirmed before its opening day, closed on 15.07, reopened by the reversal of 20.07.
        var d20 = (await SubrogationReportStore.CaseCardAsync(w.App, f.D20, today, today))!;
        Assert.Equal(CaseStatus.PartiallyReceived, d20.Header.StatusNow);
        Assert.Equal([MovementKind.Confirmed, MovementKind.Received, MovementKind.Received, MovementKind.Reversal], d20.History.Select(h => h.Kind));
    }

    [Fact]
    public async Task R3_the_report_and_the_periods_equal_the_SQL_oracle_and_caseless_money_stays_apart()
    {
        var f = await FixtureAsync();
        var r = f.R;
        var mine = (await w.RowsAsync<string>("select number from core.subrogation_case where id in @cases", new { cases = r.Cases })).ToHashSet(StringComparer.Ordinal);
        var claims = (await w.RowsAsync<string>("select number from core.claim where id in @ids", new { ids = new[] { f.ClaimB, f.ClaimL, f.ClaimM } })).ToHashSet(StringComparer.Ordinal);
        foreach (var asOf in new[] { r.D(7, 31), r.D(7, 24) })
        {
            var oracle = await OracleOnAsync(r, asOf);
            foreach (var scope in new[] { SubrogationReportStore.OutsideScope.All, SubrogationReportStore.OutsideScope.ClaimsOfCases, SubrogationReportStore.OutsideScope.None })
            {
                var report = await SubrogationReportStore.ReportAsync(w.App, asOf, outside: scope);
                var rows = report.Rows.Where(x => mine.Contains(x.CaseNumber)).OrderBy(x => x.CaseNumber, StringComparer.Ordinal).ToList();
                Assert.Equal(oracle.Select(o => $"{o.CaseNumber} {Line(o.Claimed, o.Confirmed, o.Received, o.Cancelled, o.Balance)}"),
                    rows.Select(x => $"{x.CaseNumber} {Line(x.Claimed, x.Accrued, x.Received, x.Cancelled, x.Balance)}"));
                // Case-less money by the date, per claim and kind, apart from the totals.
                var outside = await w.RowsAsync<string>("""
                    select cl.number + N' ' + r.kind + N' ' + convert(nvarchar(30), sum(r.amount_gel))
                    from core.claim_recovery r join core.claim cl on cl.id = r.claim_id
                    where r.subrogation_case_id is null and r.received_on <= @asOf and r.claim_id in @ids
                      and (@scope = 0 or r.claim_id in (select claim_id from core.subrogation_case where opened_on <= @asOf))
                    group by cl.number, r.kind order by 1
                    """, new { asOf, ids = new[] { f.ClaimB, f.ClaimL, f.ClaimM }, scope = scope == SubrogationReportStore.OutsideScope.ClaimsOfCases ? 1 : 0 });
                var shown = report.Outside.Where(o => claims.Contains(o.ClaimNumber))
                    .Select(o => $"{o.ClaimNumber} {(o.Kind == OutsideKind.Salvage ? "salvage" : "subrogation")} {(o.Received < 0 ? "-" : "")}{Text(BigInteger.Abs(o.Received))}")
                    .Order(StringComparer.Ordinal).ToList();
                Assert.Equal(scope == SubrogationReportStore.OutsideScope.None ? [] : outside.Order(StringComparer.Ordinal).ToList(), shown);
            }
            // Q-R1: only the cases with a balance.
            var owed = await SubrogationReportStore.ReportAsync(w.App, asOf, onlyWithBalance: true);
            Assert.Equal(oracle.Where(o => o.Balance != 0).Select(o => o.CaseNumber), owed.Rows.Where(x => mine.Contains(x.CaseNumber)).Select(x => x.CaseNumber).Order(StringComparer.Ordinal));
        }
        foreach (var (from, to) in new[] { (r.D(7, 1), r.D(7, 31)), (r.D(7, 1), r.D(7, 15)), (r.D(7, 16), r.D(7, 31)) })
        {
            var oracle = await OraclePeriodAsync(r, from, to);
            var period = await SubrogationReportStore.PeriodAsync(w.App, from, to);
            Assert.Equal(oracle.Select(o => $"{o.CaseNumber} {Line(o.Claimed, o.Confirmed, o.Received, o.Cancelled, o.Balance)}"),
                period.Lines.Where(l => mine.Contains(l.CaseNumber)).OrderBy(l => l.CaseNumber, StringComparer.Ordinal)
                    .Select(l => $"{l.CaseNumber} {Line(l.Moved.Claimed, l.Moved.Confirmed, l.Moved.Received, l.Moved.Cancelled, l.Moved.BalanceChange)}"));
        }
    }

    // ---------------------------------------------------------------- R5: a committed write and an open snapshot

    [Fact]
    public async Task R5_a_claim_of_100_committed_while_a_snapshot_is_open_is_not_seen_in_it_and_is_seen_in_a_new_one_with_nothing_else_changed()
    {
        var f = await FixtureAsync();
        var r = f.R;
        var asOf = r.D(7, 31);
        var number = await w.ScalarAsync<string>("select number from core.subrogation_case where id = @id", new { id = f.D19 });
        string Read(CaseCard card, Report report, SubrogationReportStore.PeriodReport july) =>
            $"{Line(card.Totals.Claimed, card.Totals.Confirmed, card.Totals.Received, card.Totals.Cancelled, card.Totals.Balance)} | " +
            $"{string.Join(";", report.Rows.Where(x => x.CaseNumber == number).Select(x => Line(x.Claimed, x.Accrued, x.Received, x.Cancelled, x.Balance)))} | " +
            $"{string.Join(";", july.Lines.Where(l => l.CaseNumber == number).Select(l => Line(l.Moved.Claimed, l.Moved.Confirmed, l.Moved.Received, l.Moved.Cancelled, l.Moved.BalanceChange)))}";
        var others = async () => string.Join("|", (await OracleOnAsync(r, asOf)).Where(o => o.CaseNumber != number).Select(o => $"{o.CaseNumber} {Line(o.Claimed, o.Confirmed, o.Received, o.Cancelled, o.Balance)}"));
        var othersBefore = await others();
        var counts = async () => await w.ScalarAsync<string>("""
            select concat((select count(*) from core.subrogation_movement where case_id = @c), N'|', (select count(*) from core.claim_recovery where subrogation_case_id = @c),
                          N'|', (select count(*) from core.audit_log where table_name = N'subrogation_case' and row_id = lower(convert(nchar(36), @c))))
            """, new { c = f.D19 });
        var countsBefore = await counts();
        var maxAudit = await w.ScalarAsync<long>("select max(id) from core.audit_log");
        var (a, b) = await w.App.SnapshotAsync(async tx =>
        {
            var a = Read((await SubrogationReportStore.LoadCaseCardAsync(tx, f.D19, asOf, asOf))!, await SubrogationReportStore.LoadReportAsync(tx, asOf),
                await SubrogationReportStore.LoadPeriodAsync(tx, r.D(7, 1), asOf));
            Assert.True((await MoveAsync(r, f.D19, DocumentedMovement.Claimed, 100m, asOf)).Ok);
            var b = Read((await SubrogationReportStore.LoadCaseCardAsync(tx, f.D19, asOf, asOf))!, await SubrogationReportStore.LoadReportAsync(tx, asOf),
                await SubrogationReportStore.LoadPeriodAsync(tx, r.D(7, 1), asOf));
            return (a, b);
        });
        Assert.Equal("0.00 1000.00 600.00 0.00 400.00 | 0.00 1000.00 600.00 0.00 400.00 | 0.00 1000.00 600.00 0.00 400.00", a);
        Assert.Equal(a, b);
        var c = Read((await SubrogationReportStore.CaseCardAsync(w.App, f.D19, asOf, asOf))!, await SubrogationReportStore.ReportAsync(w.App, asOf),
            await SubrogationReportStore.PeriodAsync(w.App, r.D(7, 1), asOf));
        Assert.Equal("100.00 1000.00 600.00 0.00 400.00 | 100.00 1000.00 600.00 0.00 400.00 | 100.00 1000.00 600.00 0.00 400.00", c);
        Assert.Equal(othersBefore, await others());
        // One movement more, no new money row, no new case audit row; the audit since A holds that one insert by the run's user.
        var parts = countsBefore.Split('|');
        Assert.Equal($"{int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) + 1}|{parts[1]}|{parts[2]}", await counts());
        Assert.Equal([("subrogation_movement", "insert", (Guid?)r.User.Id)], await w.RowsAsync<(string, string, Guid?)>(
            "select table_name, action, user_id from core.audit_log where id > @maxAudit", new { maxAudit }));
    }
}
