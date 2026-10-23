using System.Numerics;
using System.Text;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using PartyRole = Insurance.Domain.Common.PartyRole;

namespace Insurance.Integration.Tests;

/// <summary>
/// The regress operations of a case on the real SQL Server (legacy src/lib/subrogation.test.ts, its 19 cases, which ran them
/// through an in-memory store; here the real store, core.record_case_money and the triggers of migration 0003 / legacy 0030).
/// The company of the old test, in a year of each test's own: claim A (5.1 damage, paid 1 000), B (5.1 equipment, paid 500, an
/// old regress of 50 and a salvage of 30 in September), A2, NOPAY (nothing paid), DED (1 000 gross, 200 deductible, 100 set
/// off), TOW (14), DRIVER (3.2), THEFT (5.1 theft), USD (DED's payment at 2.7) and IMPORTED (5.1 without a coverage);
/// September is closed.
/// </summary>
public sealed class SubrogationCaseTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2400;
    private static BigInteger Lari(decimal n) => new(decimal.Round(n * 100));

    private sealed class Company(SubrogationCaseTests t, int y, Guid actor, Dictionary<string, Guid> claims)
    {
        public int Y { get; } = y;
        public Guid Actor { get; } = actor;
        public Dictionary<string, Guid> Claims { get; } = claims;
        public SubrogationTransact Transact => SubrogationCases.InDatabase(t.w.App, Actor);
        public DateOnly D(int month, int day) => new(Y, month, day);
        public Guid Person1 { get; set; }
        public Guid Person2 { get; set; }
        public Guid Insurer1 { get; set; }
    }

    private async Task<Company> CompanyAsync()
    {
        var y = Interlocked.Increment(ref year);
        var actor = await w.UserAsync("admin");
        var from = new DateOnly(y, 1, 1);
        var gel = await w.PolicyAsync(actor.Id, from, [new("damage", 99999m, 0m), new("equipment", 99999m, 0m), new("theft", 99999m, 0m), new("assistance", 99999m, 0m), new("accident", 99999m, 0m)]);
        await w.RateAsync("USD", from, 2.7m);
        var usd = await w.PolicyAsync(actor.Id, from, [new("damage", 99999m, 0m)], currency: "USD", rate: 2.7m);
        var bare = Guid.NewGuid();
        await w.ExecAsync("insert core.policy_line (id, policy_id, class_code, sum_insured, premium, premium_gel) values (@bare, @policy, N'5.1', 99999, 0, 0)", new { bare, policy = gel.Id });
        var claims = new Dictionary<string, Guid>();
        async Task<Guid> One(ClaimWorld.Policy p, Guid line)
        {
            var outcome = await ClaimActions.RegisterClaimAsync(w.App, actor, ClaimWorld.Form(("policy_number", p.Number), ("occurred_on", $"{y}-08-01"), ("reported_on", $"{y}-08-02"),
                ("description", "ავარია"), ($"line:{line}", "on"), ($"reserve:{line}", "1000")));
            return Guid.Parse(outcome.RedirectTo!.Split('/')[2].Split('?')[0]);
        }
        foreach (var (name, coverage) in new[] { ("A", "damage"), ("B", "equipment"), ("A2", "damage"), ("NOPAY", "damage"), ("DED", "damage"), ("TOW", "assistance"), ("DRIVER", "accident"), ("THEFT", "theft") })
            claims[name] = await One(gel, gel.LineOf(coverage));
        claims["USD"] = await One(usd, usd.LineOf("damage"));
        claims["IMPORTED"] = await One(gel, bare);
        var paidOn = new DateOnly(y, 10, 1);
        await PayAsync(actor.Id, gel.PartyId, claims["A"], paidOn, 1000m);
        await PayAsync(actor.Id, gel.PartyId, claims["B"], paidOn, 500m);
        await PayAsync(actor.Id, gel.PartyId, claims["A2"], paidOn, 1000m);
        await PayAsync(actor.Id, gel.PartyId, claims["DED"], paidOn, 1000m, 200m, 100m);
        await PayAsync(actor.Id, usd.PartyId, claims["USD"], paidOn, 1000m, 200m, 100m, 2.7m);
        // Old rows of B, from before regress cases (G8, G11): they must stay exactly as they are.
        await w.ExecAsync("""
            insert core.claim_recovery (claim_id, kind, received_on, amount_gel, note) values (@b, N'subrogation', @a, 50, N'ძველი');
            insert core.claim_recovery (claim_id, kind, received_on, amount_gel, note) values (@b, N'salvage', @c, 30, N'ნარჩენი');
            """, new { b = claims["B"], a = new DateOnly(y, 9, 10), c = new DateOnly(y, 9, 12) });
        await w.CloseMonthAsync(actor.Id, new DateOnly(y, 9, 1));
        return new Company(this, y, actor.Id, claims)
        {
            Person1 = await w.PartyAsync(name: "ბრალეული"), Person2 = await w.PartyAsync(name: "მეორე"), Insurer1 = await w.PartyAsync(name: "მზღვეველი"),
        };
    }

    /// <summary>A payment as payClaim stores it: the net and the set-off in GEL at the payment day's rate (1 for GEL), the set-off by its receipt.</summary>
    private async Task PayAsync(Guid actor, Guid party, Guid claim, DateOnly on, decimal gross, decimal withheld = 0m, decimal offset = 0m, decimal rate = 1m)
    {
        Guid? receipt = null;
        if (offset > 0)
        {
            receipt = Guid.NewGuid();
            await w.ExecAsync("insert core.payment (id, received_on, amount_gel, method, kind, payer_party_id) values (@receipt, @on, @gel, N'claim_offset', N'receipt', @party)",
                new { receipt, on, gel = Math.Round(offset * rate, 2), party });
        }
        var net = gross - withheld - offset;
        await w.App.AsUserAsync(actor, tx => tx.ExecuteAsync("""
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_payment_id)
            values (@claim, @on, @gross, @withheld, @offset, @net, @netGel, @rate, N'policyholder', @party, @receipt)
            """, new { claim, on, gross, withheld, offset, net, netGel = Math.Round(net * rate, 2), rate, party, receipt }));
    }

    private static Guid Key() => Guid.NewGuid();

    private async Task<(Guid CaseId, Guid PayerId)> ConfirmedCaseAsync(Company c, string claim, decimal amount)
    {
        var opened = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims[claim], c.D(10, 2), null, Key());
        Assert.True(opened.Ok, opened.Error);
        var person = await SubrogationCases.AddPartyAsync(c.Transact, opened.Id!.Value, c.Person1, PartyRole.LiablePerson, null, null, null);
        Assert.True(person.Ok, person.Error);
        Assert.True((await SubrogationCases.AddMovementAsync(c.Transact, opened.Id.Value, DocumentedMovement.Claimed, Lari(amount), c.D(10, 3), DocumentKind.DemandLetter, null, c.D(10, 3), null, Key())).Ok);
        Assert.True((await SubrogationCases.AddMovementAsync(c.Transact, opened.Id.Value, DocumentedMovement.Confirmed, Lari(amount), c.D(10, 4), DocumentKind.DebtorAcceptance, null, c.D(10, 4), null, Key())).Ok);
        return (opened.Id.Value, person.Id!.Value);
    }

    private Task<SubrogationResult> ReceiveAsync(Company c, Guid caseId, Guid payer, decimal amount, DateOnly on, string? bank = null, Guid? key = null, SubrogationTransact? transact = null) =>
        SubrogationCases.ReceiveAsync(transact ?? c.Transact, caseId, payer, Lari(amount), on, bank, null, key ?? Key());

    private Task<SubrogationResult> ReverseAsync(Company c, Guid caseId, Guid movement, DateOnly on, string reason = "შეცდომა", SubrogationTransact? transact = null) =>
        SubrogationCases.ReverseAsync(transact ?? c.Transact, caseId, movement, on, reason, Key());

    private Task<SubrogationResult> MoveAsync(Company c, Guid caseId, DocumentedMovement kind, decimal amount, DateOnly on, DocumentKind document, string? number = null, DateOnly? date = null) =>
        SubrogationCases.AddMovementAsync(c.Transact, caseId, kind, Lari(amount), on, document, number, date ?? on, null, Key());

    private async Task<BigInteger> BalanceAsync(Guid caseId) =>
        Billing.Tetri(await w.ScalarAsync<decimal>("select confirmed - received - cancelled from core.subrogation_totals(@caseId, null)", new { caseId }));

    private Task<string> StatusAsync(Guid caseId) => w.ScalarAsync<string>("select status from core.subrogation_case where id = @caseId", new { caseId });

    /// <summary>Every row of the company's regress and recoveries, as text: equal before and after means nothing was written.</summary>
    private async Task<string> SnapshotAsync(Company c)
    {
        var ids = c.Claims.Values.ToList();
        var b = new StringBuilder();
        foreach (var sql in new[]
        {
            "select id, kind, received_on, amount_gel, subrogation_case_id, row_kind, reverses_claim_recovery_id, bank_reference, note from core.claim_recovery where claim_id in @ids order by id",
            "select id, status, basis_kind, basis_number from core.subrogation_case where claim_id in @ids order by id",
            "select p.id, p.response, p.response_on from core.subrogation_party p join core.subrogation_case c on c.id = p.case_id where c.claim_id in @ids order by p.id",
            "select m.id, m.kind, m.amount_gel, m.booked_on from core.subrogation_movement m join core.subrogation_case c on c.id = m.case_id where c.claim_id in @ids order by m.id",
        })
            foreach (var row in await w.RowsAsync<dynamic>(sql, new { ids }))
                b.AppendLine(string.Join("|", ((IDictionary<string, object?>)row).Select(kv => $"{kv.Key}={kv.Value}")));
        return b.ToString();
    }

    private async Task<BigInteger> ReportMonthAsync(Company c, int month) =>
        Billing.Tetri(await w.ScalarAsync<decimal>("select coalesce(sum(amount_gel), 0) from core.claim_recovery where claim_id in @ids and year(received_on) = @y and month(received_on) = @month",
            new { ids = c.Claims.Values.ToList(), y = c.Y, month }));

    private Task<List<(string RowKind, decimal Amount, Guid? Reverses, Guid Id, Guid Claim)>> MoneyAsync(Guid caseId) =>
        w.RowsAsync<(string, decimal, Guid?, Guid, Guid)>("select row_kind, amount_gel, reverses_claim_recovery_id, id, claim_id from core.claim_recovery where subrogation_case_id = @caseId order by created_at", new { caseId });

    [Fact]
    public async Task G23_G24_G25_1000_less_600_is_400_then_200_and_the_reversal_of_the_600_gives_800_claim_B_untouched()
    {
        var c = await CompanyAsync();
        var bBefore = await w.RowsAsync<(Guid, decimal)>("select id, amount_gel from core.claim_recovery where claim_id = @b order by id", new { b = c.Claims["B"] });
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var r1 = await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15), "BANK-1");
        Assert.True(r1.Ok, r1.Error);
        Assert.Equal(Lari(400), await BalanceAsync(caseId));
        Assert.Equal("partially_received", await StatusAsync(caseId));
        Assert.True((await ReceiveAsync(c, caseId, payer, 200m, c.D(10, 16), "BANK-2")).Ok);
        Assert.Equal(Lari(200), await BalanceAsync(caseId));
        Assert.True((await ReverseAsync(c, caseId, r1.Id!.Value, c.D(10, 20), "ბანკმა დააბრუნა")).Ok);
        Assert.Equal(Lari(800), await BalanceAsync(caseId));
        var money = await MoneyAsync(caseId);
        Assert.Equal([("receipt", 600m), ("receipt", 200m), ("reversal", -600m)], money.Select(m => (m.RowKind, m.Amount)));
        Assert.Equal(money[0].Id, money[2].Reverses);
        Assert.All(money, m => Assert.Equal(c.Claims["A"], m.Claim));
        Assert.Equal(Lari(200), await ReportMonthAsync(c, 10));
        Assert.Equal(bBefore, await w.RowsAsync<(Guid, decimal)>("select id, amount_gel from core.claim_recovery where claim_id = @b order by id", new { b = c.Claims["B"] }));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.subrogation_case where claim_id = @b", new { b = c.Claims["B"] }));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.case_recovery_without_movement where claim_id in @ids", new { ids = c.Claims.Values.ToList() }));
    }

    [Fact]
    public async Task G26_a_full_receipt_closes_the_case_and_a_new_case_is_not_closed()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A2", 1000m);
        Assert.True((await ReceiveAsync(c, caseId, payer, 1000m, c.D(10, 15))).Ok);
        Assert.Equal(BigInteger.Zero, await BalanceAsync(caseId));
        Assert.Equal("closed", await StatusAsync(caseId));
        var fresh = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["A"], c.D(10, 2), null, Key());
        Assert.True(fresh.Ok);
        Assert.Equal("opened", await StatusAsync(fresh.Id!.Value));
        Assert.StartsWith("RG", await w.ScalarAsync<string>("select number from core.subrogation_case where id = @id", new { id = fresh.Id }));
    }

    [Fact]
    public async Task G2_G27_the_same_form_or_bank_reference_twice_is_refused_with_nothing_written()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var k = Key();
        Assert.True((await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15), "BANK-1", k)).Ok);
        var before = await SnapshotAsync(c);
        Assert.Equal("duplicate", (await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15), "BANK-1", k)).Error);
        Assert.Equal("duplicate", (await ReceiveAsync(c, caseId, payer, 100m, c.D(10, 15), "BANK-9", k)).Error);
        Assert.Equal("duplicate", (await ReceiveAsync(c, caseId, payer, 100m, c.D(10, 15), " BANK-1 ")).Error);
        Assert.Equal(before, await SnapshotAsync(c));
        Assert.Equal(Lari(400), await BalanceAsync(caseId));
        var opened = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["A2"], c.D(10, 2), null, k);
        Assert.True(opened.Ok);
        Assert.Equal("duplicate", (await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["DED"], c.D(10, 2), null, (await w.ScalarAsync<Guid>("select request_key from core.subrogation_case where id = @id", new { id = opened.Id })))).Error);
    }

    [Fact]
    public async Task G27_more_than_the_balance_a_stranger_as_payer_and_a_second_reversal_are_refused_with_nothing_written()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var r1 = await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15));
        Assert.True((await ReverseAsync(c, caseId, r1.Id!.Value, c.D(10, 15))).Ok);
        var before = await SnapshotAsync(c);
        Assert.Equal("balance", (await ReceiveAsync(c, caseId, payer, 1000.01m, c.D(10, 16))).Error);
        Assert.Equal("payer", (await ReceiveAsync(c, caseId, Guid.NewGuid(), 10m, c.D(10, 16))).Error);
        Assert.Equal("reversed", (await ReverseAsync(c, caseId, r1.Id.Value, c.D(10, 16), "კიდევ")).Error);
        Assert.Equal("target", (await ReverseAsync(c, caseId, Guid.NewGuid(), c.D(10, 16))).Error);
        Assert.Equal("document", (await ReverseAsync(c, caseId, r1.Id.Value, c.D(10, 16), "  ")).Error);
        Assert.Equal("missing", (await ReceiveAsync(c, Guid.NewGuid(), payer, 1m, c.D(10, 16))).Error);
        Assert.Equal(before, await SnapshotAsync(c));
    }

    [Fact]
    public async Task G17_other_classes_theft_and_unknown_coverage_open_no_case()
    {
        var c = await CompanyAsync();
        var before = await SnapshotAsync(c);
        foreach (var (claim, error) in new[] { ("TOW", "class"), ("DRIVER", "class"), ("THEFT", "theft"), ("IMPORTED", "coverage") })
            Assert.Equal(error, (await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims[claim], c.D(10, 2), null, Key())).Error);
        Assert.Equal("missing", (await SubrogationCases.OpenCaseAsync(c.Transact, Guid.NewGuid(), c.D(10, 2), null, Key())).Error);
        Assert.Equal(before, await SnapshotAsync(c));
        // The database says the same (SG001–SG003) to a case written past the operation.
        foreach (var (claim, code) in new[] { ("TOW", "SG001"), ("THEFT", "SG003"), ("IMPORTED", "SG002") })
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync(
                "insert core.subrogation_case (number, claim_id, opened_on, request_key) values (@n, @claim, @on, newid())", new { n = "X" + ClaimWorld.Tag(), claim = c.Claims[claim], on = c.D(10, 2) }));
            Assert.Equal(code, DbError.From(e)?.Code);
        }
    }

    [Fact]
    public async Task R1_a_USD_claim_takes_the_GEL_actually_paid_as_its_cap_with_no_new_rate()
    {
        var c = await CompanyAsync();
        var opened = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["USD"], c.D(10, 2), null, Key());
        Assert.True(opened.Ok);
        Assert.Equal("cap", (await MoveAsync(c, opened.Id!.Value, DocumentedMovement.Claimed, 2160.01m, c.D(10, 3), DocumentKind.DemandLetter)).Error);
        Assert.True((await MoveAsync(c, opened.Id.Value, DocumentedMovement.Claimed, 2160m, c.D(10, 3), DocumentKind.DemandLetter)).Ok);
    }

    [Fact]
    public async Task H81_one_case_per_claim_even_after_it_closed_and_a_reversal_reopens_the_same_case()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A2", 1000m);
        var full = await ReceiveAsync(c, caseId, payer, 1000m, c.D(10, 15));
        Assert.Equal("closed", await StatusAsync(caseId));
        var before = await SnapshotAsync(c);
        Assert.Equal("open", (await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["A2"], c.D(10, 16), null, Key())).Error);
        Assert.Equal(before, await SnapshotAsync(c));
        Assert.True((await ReverseAsync(c, caseId, full.Id!.Value, c.D(10, 17), "ბანკმა დააბრუნა")).Ok);
        Assert.Equal(Lari(1000), await BalanceAsync(caseId));
        Assert.Equal("accepted", await StatusAsync(caseId));
        Assert.Equal(0m, await w.ScalarAsync<decimal>("select sum(amount_gel) from core.claim_recovery where claim_id = @c", new { c = c.Claims["A2"] }));
    }

    /// <summary>A store that pauses after its first read of a case, or fails after a case's money is written (legacy Memory.live / failAt).</summary>
    private sealed class TestStore(ISubrogationStore inner) : ISubrogationStore
    {
        public Func<Task>? AfterFirstRead;
        public bool FailAfterMoney;
        private bool paused;

        public Task LockClaimAsync(Guid claimId) => inner.LockClaimAsync(claimId);
        public Task<ClaimFacts?> ClaimFactsAsync(Guid claimId) => inner.ClaimFactsAsync(claimId);
        public Task<bool> HasCaseAsync(Guid claimId) => inner.HasCaseAsync(claimId);
        public Task<Guid?> CaseByKeyAsync(Guid requestKey) => inner.CaseByKeyAsync(requestKey);
        public Task<string?> NextNumberAsync() => inner.NextNumberAsync();
        public Task<Guid> InsertCaseAsync(NewCase row) => inner.InsertCaseAsync(row);

        public async Task<CaseRow?> GetCaseAsync(Guid caseId)
        {
            var c = await inner.GetCaseAsync(caseId);
            if (!paused && AfterFirstRead is { } pause)
            {
                paused = true;
                await pause();
            }
            return c;
        }

        public Task SetStatusAsync(Guid caseId, CaseStatus status) => inner.SetStatusAsync(caseId, status);
        public Task SetEvidenceAsync(Guid caseId, BasisKind kind, string number, DateOnly date) => inner.SetEvidenceAsync(caseId, kind, number, date);
        public Task<IReadOnlyList<CaseParty>> PartiesAsync(Guid caseId) => inner.PartiesAsync(caseId);
        public Task<Guid> InsertPartyAsync(Guid caseId, Guid partyId, PartyRole role, string? policyNumber, string? coverage, long? limit) => inner.InsertPartyAsync(caseId, partyId, role, policyNumber, coverage, limit);
        public Task SetResponseAsync(Guid casePartyId, SubrogationResponse response, DateOnly on) => inner.SetResponseAsync(casePartyId, response, on);
        public Task<IReadOnlyList<StoredMovement>> MovementsAsync(Guid caseId) => inner.MovementsAsync(caseId);
        public Task<IReadOnlyList<Insurance.Domain.Finance.ClaimPayment>> PaymentsAsync(Guid claimId) => inner.PaymentsAsync(claimId);
        public Task<IReadOnlyList<OtherRecovery>> OtherRecoveriesAsync(Guid claimId, Guid caseId) => inner.OtherRecoveriesAsync(claimId, caseId);
        public Task<Guid?> MovementByKeyAsync(Guid requestKey) => inner.MovementByKeyAsync(requestKey);
        public Task<Guid?> RecoveryByReferenceAsync(Guid caseId, string bankReference) => inner.RecoveryByReferenceAsync(caseId, bankReference);
        public Task<Guid> InsertMovementAsync(MovementRow row) => inner.InsertMovementAsync(row);

        public async Task<Guid> RecordCaseMoneyAsync(NewRecovery recovery, MovementRow movement)
        {
            var id = await inner.RecordCaseMoneyAsync(recovery, movement);
            if (FailAfterMoney) throw new InvalidOperationException("injected failure after the money and its movement were written");
            return id;
        }
    }

    private SubrogationTransact With(Company c, Action<TestStore> setUp) => operation => w.App.AsUserAsync(c.Actor, tx =>
    {
        var store = new TestStore(new SqlSubrogationStore(tx));
        setUp(store);
        return operation(store);
    });

    [Fact]
    public async Task H83_the_case_is_read_again_under_the_claim_lock_so_a_receipt_that_closed_it_meanwhile_is_seen()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var r1 = await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 10));
        Assert.Equal("partially_received", await StatusAsync(caseId));
        // A starts the reversal of the 600 and reads the case (partially received) before taking the lock …
        var aRead = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var a = ReverseAsync(c, caseId, r1.Id!.Value, c.D(10, 20), "ბანკმა დააბრუნა", With(c, s => s.AfterFirstRead = async () => { aRead.SetResult(); await gate.Task; }));
        await aRead.Task;
        // … B receives the remaining 400 and closes the case …
        Assert.True((await ReceiveAsync(c, caseId, payer, 400m, c.D(10, 15))).Ok);
        Assert.Equal("closed", await StatusAsync(caseId));
        // … then A goes on: the 600 is reversed, 600 is owed again, and the case is open again.
        gate.SetResult();
        Assert.True((await a).Ok);
        Assert.Equal(Lari(600), await BalanceAsync(caseId));
        Assert.Equal("partially_received", await StatusAsync(caseId));
        // Legacy scripts/regress-db/check.ts D20: the audit shows the case closed by B, then partially received again by A.
        Assert.Equal(["closed", "partially_received"], (await w.RowsAsync<string>("""
            select json_value(new_row, '$.status') from core.audit_log where table_name = N'subrogation_case' and row_id = @id and action = N'update' order by id
            """, new { id = caseId.ToString() }))[^2..]);
    }

    [Fact]
    public async Task H81_old_regress_money_of_the_claim_takes_its_share_of_the_cap_and_salvage_does_not()
    {
        var c = await CompanyAsync();
        var opened = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["B"], c.D(10, 2), null, Key());
        Assert.True(opened.Ok);
        var before = await SnapshotAsync(c);
        Assert.Equal("cap", (await MoveAsync(c, opened.Id!.Value, DocumentedMovement.Claimed, 450.01m, c.D(10, 3), DocumentKind.DemandLetter)).Error);
        Assert.Equal(before, await SnapshotAsync(c));
        Assert.True((await MoveAsync(c, opened.Id.Value, DocumentedMovement.Claimed, 450m, c.D(10, 3), DocumentKind.DemandLetter)).Ok);
        Assert.True((await MoveAsync(c, opened.Id.Value, DocumentedMovement.Confirmed, 450m, c.D(10, 4), DocumentKind.DebtorAcceptance)).Ok);
        Assert.Equal(50m, await w.ScalarAsync<decimal>("select sum(amount_gel) from core.claim_recovery where claim_id = @b and kind = N'subrogation'", new { b = c.Claims["B"] }));
    }

    [Fact]
    public async Task H82_a_later_reversal_or_confirmation_cannot_cover_an_earlier_over_receipt()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var r1 = await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 10));
        Assert.True((await ReverseAsync(c, caseId, r1.Id!.Value, c.D(10, 20))).Ok);
        var before = await SnapshotAsync(c);
        Assert.Equal("balance", (await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 11))).Error);
        Assert.Equal(before, await SnapshotAsync(c));
        Assert.True((await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 20))).Ok);

        var other = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["A2"], c.D(10, 2), null, Key());
        var person = await SubrogationCases.AddPartyAsync(c.Transact, other.Id!.Value, c.Person1, PartyRole.LiablePerson, null, null, null);
        Assert.True((await MoveAsync(c, other.Id.Value, DocumentedMovement.Confirmed, 1000m, c.D(10, 20), DocumentKind.DebtorAcceptance)).Ok);
        var mid = await SnapshotAsync(c);
        Assert.Equal("balance", (await ReceiveAsync(c, other.Id.Value, person.Id!.Value, 600m, c.D(10, 10))).Error);
        Assert.Equal(mid, await SnapshotAsync(c));
    }

    [Fact]
    public async Task G13_G7_nothing_before_the_reimbursement_and_the_cap_is_gross_less_deductible_set_off_included()
    {
        var c = await CompanyAsync();
        var early = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["NOPAY"], c.D(10, 2), null, Key());
        Assert.True(early.Ok, "a case may open early, to collect evidence");
        Assert.Equal("cap", (await MoveAsync(c, early.Id!.Value, DocumentedMovement.Claimed, 1m, c.D(10, 3), DocumentKind.DemandLetter)).Error);
        var ded = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["DED"], c.D(10, 2), null, Key());
        Assert.Equal("cap", (await MoveAsync(c, ded.Id!.Value, DocumentedMovement.Claimed, 800.01m, c.D(10, 3), DocumentKind.DemandLetter)).Error);
        Assert.True((await MoveAsync(c, ded.Id.Value, DocumentedMovement.Claimed, 800m, c.D(10, 3), DocumentKind.DemandLetter)).Ok);
        Assert.Equal("document", (await MoveAsync(c, ded.Id.Value, DocumentedMovement.Claimed, 800m, c.D(10, 3), DocumentKind.DebtorAcceptance)).Error);
        // Before the payment's day the room is nothing.
        Assert.Equal("cap", (await MoveAsync(c, ded.Id.Value, DocumentedMovement.Confirmed, 1m, c.D(9, 30), DocumentKind.DebtorAcceptance)).Error);
    }

    [Fact]
    public async Task G14_G16_a_court_cancels_with_its_decision_and_there_is_no_write_off()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        Assert.True((await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15))).Ok);
        var before = await SnapshotAsync(c);
        Assert.Equal("document", (await SubrogationCases.AddMovementAsync(c.Transact, caseId, DocumentedMovement.Cancelled, Lari(400), c.D(10, 20), DocumentKind.CourtDecision, null, c.D(10, 19), null, Key())).Error);
        Assert.Equal("document", (await SubrogationCases.AddMovementAsync(c.Transact, caseId, DocumentedMovement.Cancelled, Lari(400), c.D(10, 20), DocumentKind.Correction, "N1", c.D(10, 19), null, Key())).Error);
        Assert.Equal("document", (await SubrogationCases.AddMovementAsync(c.Transact, caseId, DocumentedMovement.Cancelled, Lari(400), c.D(10, 20), DocumentKind.DebtorAcceptance, "N1", c.D(10, 19), null, Key())).Error);
        Assert.Equal(before, await SnapshotAsync(c));
        Assert.True((await SubrogationCases.AddMovementAsync(c.Transact, caseId, DocumentedMovement.Cancelled, Lari(400), c.D(10, 20), DocumentKind.CourtDecision, "2/123-26", c.D(10, 19), null, Key())).Ok);
        Assert.Equal(BigInteger.Zero, await BalanceAsync(caseId));
        Assert.Equal("closed", await StatusAsync(caseId));
    }

    [Fact]
    public async Task G15_a_wrong_confirmation_is_reversed_and_entered_again_the_original_stays()
    {
        var c = await CompanyAsync();
        await PayAsync(c.Actor, await w.ScalarAsync<Guid>("select payee_party_id from core.claim_payment where claim_id = @a", new { a = c.Claims["A"] }), c.Claims["A"], c.D(10, 1), 500m);
        var opened = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["A"], c.D(10, 2), null, Key());
        var wrong = await MoveAsync(c, opened.Id!.Value, DocumentedMovement.Confirmed, 1200m, c.D(10, 3), DocumentKind.DebtorAcceptance);
        Assert.True(wrong.Ok);
        Assert.Equal("accepted", await StatusAsync(opened.Id.Value));
        Assert.True((await ReverseAsync(c, opened.Id.Value, wrong.Id!.Value, c.D(10, 4), "არასწორი თანხა")).Ok);
        Assert.Equal("opened", await StatusAsync(opened.Id.Value));
        Assert.True((await MoveAsync(c, opened.Id.Value, DocumentedMovement.Confirmed, 1000m, c.D(10, 4), DocumentKind.DebtorAcceptance)).Ok);
        Assert.Equal(Lari(1000), await BalanceAsync(opened.Id.Value));
        Assert.Equal(1200m, await w.ScalarAsync<decimal>("select amount_gel from core.subrogation_movement where id = @id", new { id = wrong.Id }));
        Assert.Empty(await MoneyAsync(opened.Id.Value));
    }

    [Fact]
    public async Task G3_G4_G5_the_person_and_the_insurer_share_one_balance_and_a_dispute_confirms_nothing()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var insurer = await SubrogationCases.AddPartyAsync(c.Transact, caseId, c.Insurer1, PartyRole.LiabilityInsurer, "MTPL-1", "TPL", 70000);
        Assert.True(insurer.Ok);
        Assert.Equal("kind", (await SubrogationCases.AddPartyAsync(c.Transact, caseId, c.Person2, PartyRole.LiablePerson, "X", null, null)).Error);
        Assert.Equal("amount", (await SubrogationCases.AddPartyAsync(c.Transact, caseId, c.Person2, PartyRole.LiabilityInsurer, null, null, 0)).Error);
        Assert.Equal("missing", (await SubrogationCases.AddPartyAsync(c.Transact, Guid.NewGuid(), c.Person2, PartyRole.LiablePerson, null, null, null)).Error);
        Assert.True((await ReceiveAsync(c, caseId, insurer.Id!.Value, 700m, c.D(10, 15))).Ok);
        Assert.Equal(Lari(300), await BalanceAsync(caseId));
        Assert.True((await ReceiveAsync(c, caseId, payer, 300m, c.D(10, 16))).Ok);
        Assert.Equal(BigInteger.Zero, await BalanceAsync(caseId));
        Assert.Equal("balance", (await ReceiveAsync(c, caseId, insurer.Id.Value, 1m, c.D(10, 17))).Error);

        var other = await SubrogationCases.OpenCaseAsync(c.Transact, c.Claims["A2"], c.D(10, 2), null, Key());
        Assert.True((await SubrogationCases.RecordEvidenceAsync(c.Transact, other.Id!.Value, BasisKind.PoliceReport, " ოქმი 77 ", c.D(10, 1))).Ok);
        Assert.Equal("evidence_confirmed", await StatusAsync(other.Id.Value));
        Assert.Equal(("police_report", "ოქმი 77"), (await w.RowsAsync<(string, string)>("select basis_kind, basis_number from core.subrogation_case where id = @id", new { id = other.Id })).Single());
        Assert.Equal("document", (await SubrogationCases.RecordEvidenceAsync(c.Transact, other.Id.Value, BasisKind.PoliceReport, "  ", c.D(10, 1))).Error);
        var ins = await SubrogationCases.AddPartyAsync(c.Transact, other.Id.Value, c.Insurer1, PartyRole.LiabilityInsurer, null, null, null);
        Assert.Equal("payer", (await SubrogationCases.RecordResponseAsync(c.Transact, other.Id.Value, Guid.NewGuid(), SubrogationResponse.Disputed, c.D(10, 5))).Error);
        Assert.True((await SubrogationCases.RecordResponseAsync(c.Transact, other.Id.Value, ins.Id!.Value, SubrogationResponse.Disputed, c.D(10, 5))).Ok);
        Assert.Equal("disputed", await StatusAsync(other.Id.Value));
        Assert.Equal(BigInteger.Zero, await BalanceAsync(other.Id.Value));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.subrogation_movement where case_id = @id", new { id = other.Id }));
        Assert.Equal(("disputed", c.D(10, 5)), (await w.RowsAsync<(string, DateOnly)>("select response, response_on from core.subrogation_party where id = @id", new { id = ins.Id })).Single());
        // An acceptance is recorded but confirms nothing; the status stays.
        Assert.True((await SubrogationCases.RecordResponseAsync(c.Transact, other.Id.Value, ins.Id.Value, SubrogationResponse.Accepted, c.D(10, 6))).Ok);
        Assert.Equal("disputed", await StatusAsync(other.Id.Value));
    }

    [Fact]
    public async Task G9_a_receipt_in_a_closed_month_is_refused_and_nothing_is_written()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        await w.CloseMonthAsync(c.Actor, c.D(10, 1));
        var before = await SnapshotAsync(c);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => ReceiveAsync(c, caseId, payer, 100m, c.D(10, 20)));
        Assert.Equal(DbError.CheckViolation, DbError.From(e)?.Code);
        Assert.Equal(before, await SnapshotAsync(c));
        Assert.Equal("balance", (await ReceiveAsync(c, caseId, payer, 100m, c.D(9, 20))).Error);
    }

    [Fact]
    public async Task G10_a_reversal_counts_in_its_own_month_and_the_original_month_stays()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var r1 = await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15));
        await w.CloseMonthAsync(c.Actor, c.D(10, 1));
        await Assert.ThrowsAnyAsync<Exception>(() => ReverseAsync(c, caseId, r1.Id!.Value, c.D(10, 31)));
        Assert.True((await ReverseAsync(c, caseId, r1.Id!.Value, c.D(11, 5))).Ok);
        Assert.Equal(Lari(600), await ReportMonthAsync(c, 10));
        Assert.Equal(-Lari(600), await ReportMonthAsync(c, 11));
        Assert.Equal(Lari(1000), await BalanceAsync(caseId));
    }

    [Fact]
    public async Task A_receipt_and_its_movement_are_written_together_and_a_failure_after_them_keeps_neither()
    {
        var c = await CompanyAsync();
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        var before = await SnapshotAsync(c);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15), transact: With(c, s => s.FailAfterMoney = true)));
        Assert.Equal(before, await SnapshotAsync(c));
        var r1 = await ReceiveAsync(c, caseId, payer, 600m, c.D(10, 15));
        Assert.True(r1.Ok);
        var mid = await SnapshotAsync(c);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReverseAsync(c, caseId, r1.Id!.Value, c.D(10, 16), transact: With(c, s => s.FailAfterMoney = true)));
        Assert.Equal(mid, await SnapshotAsync(c));
        // The movement refused inside core.record_case_money takes its money row with it.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => w.App.AsUserAsync(c.Actor, tx => new SqlSubrogationStore(tx).RecordCaseMoneyAsync(
            new NewRecovery(c.Claims["A"], caseId, "receipt", null, c.D(10, 17), Lari(10), null, null),
            new MovementRow(caseId, MovementKind.Received, Lari(10), c.D(10, 17), null, null, Guid.NewGuid(), DocumentKind.BankStatement, null, c.D(10, 17), null, Key()))));
        Assert.Equal("SG015", DbError.From(e)?.Code);
        Assert.Equal(mid, await SnapshotAsync(c));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.case_recovery_without_movement where subrogation_case_id = @caseId", new { caseId }));
    }

    [Fact]
    public async Task Negative_control_a_money_row_written_past_the_procedure_is_refused_and_one_forced_in_shows_in_the_check_view()
    {
        var c = await CompanyAsync();
        var (caseId, _) = await ConfirmedCaseAsync(c, "A", 1000m);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("""
            insert core.claim_recovery (claim_id, kind, subrogation_case_id, received_on, amount_gel) values (@claim, N'subrogation', @caseId, @on, 10)
            """, new { claim = c.Claims["A"], caseId, on = c.D(10, 15) }));
        Assert.Equal("SG034", DbError.From(e)?.Code);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.case_recovery_without_movement where subrogation_case_id = @caseId", new { caseId }));
        // Without the procedure's guarantee (the session key set by hand, no movement): the row stays behind, and the view that must
        // always be empty shows it — the check the reconciliation runs (database/README.md, difference 1).
        var forced = Guid.NewGuid();
        await w.ExecAsync("""
            exec sp_set_session_context N'core.case_recovery', @key;
            insert core.claim_recovery (id, claim_id, kind, subrogation_case_id, received_on, amount_gel) values (@forced, @claim, N'subrogation', @caseId, @on, 10);
            exec sp_set_session_context N'core.case_recovery', null;
            """, new { key = forced.ToString(), forced, claim = c.Claims["A"], caseId, on = c.D(10, 15) });
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.case_recovery_without_movement where subrogation_case_id = @caseId", new { caseId }));
    }

    [Fact]
    public async Task G8_G11_old_receipts_and_salvage_stay_as_they_were_and_count_once()
    {
        var c = await CompanyAsync();
        var old = await w.RowsAsync<(Guid, decimal, Guid?)>("select id, amount_gel, subrogation_case_id from core.claim_recovery where claim_id = @b order by id", new { b = c.Claims["B"] });
        var (caseId, payer) = await ConfirmedCaseAsync(c, "A", 1000m);
        Assert.True((await ReceiveAsync(c, caseId, payer, 100m, c.D(10, 15))).Ok);
        Assert.Equal(old, await w.RowsAsync<(Guid, decimal, Guid?)>("select id, amount_gel, subrogation_case_id from core.claim_recovery where claim_id = @b order by id", new { b = c.Claims["B"] }));
        Assert.Equal(Lari(80), await ReportMonthAsync(c, 9));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.subrogation_case where claim_id in @ids", new { ids = c.Claims.Values.ToList() }));
    }

    [Fact]
    public async Task A_claim_payment_reversal_that_would_leave_regress_above_what_was_paid_is_refused_CP006()
    {
        var c = await CompanyAsync();
        var (caseId, _) = await ConfirmedCaseAsync(c, "A", 1000m);
        var accountant = await w.UserAsync("accountant");
        var paymentId = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @a", new { a = c.Claims["A"] });
        await w.ExecAsync("update core.claim set status = N'paid', approved_amount = 1000 where id = @a", new { a = c.Claims["A"] });
        var outcome = await ClaimActions.ReverseClaimPaymentAsync(w.App, accountant, ClaimWorld.Form(("claim_id", c.Claims["A"].ToString()), ("claim_payment_id", paymentId.ToString()),
            ("reason", "correction"), ("reversed_on", ClaimWorld.D(c.D(10, 20)))), c.D(11, 3));
        Assert.Equal($"/claims/{c.Claims["A"]}?reversal=regress", outcome);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @a", new { a = c.Claims["A"] }));
        Assert.Equal(Lari(1000), await BalanceAsync(caseId));
    }
}
