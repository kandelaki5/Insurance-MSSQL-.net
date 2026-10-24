using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.LegacyDbCheck;

namespace Insurance.Integration.Tests;

/// <summary>
/// The claim side of the legacy real-database checks on the real SQL Server: the database guards of a claim-payment reversal
/// (scripts/claim-reversal-db-check.ts, migrations 0033/0034), of a reserve request and its approval (scripts/claim-reserve-db-check.ts,
/// 0035, the parts not already in ClaimReserveTests), the payout racing an FMS suspension or a sanctions flag on the policyholder or
/// the payee (scripts/claim-payee-race.ts R1–R8), and the claim and reinsurance races of scripts/db-acceptance.ts (two payments of one
/// claim, a suspension aimed at the claim, two writers of one claim's reinsurance shares, two facultative placements through the
/// application). Every race uses two real sessions in a forced interleaving and, where the legacy check had one, a negative control.
/// </summary>
public sealed class LegacyDbCheckClaimTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2740;

    private sealed record Paid(CurrentUser Claims, CurrentUser Accountant, ClaimWorld.Policy Policy, Guid Claim, Guid Payment, DateOnly Today, int Year);

    /// <summary>An approved GEL claim of 1 000 (deductible 100) paid 250 today (03.11), with September closed; optionally with the premium set off.</summary>
    private async Task<Paid> PaidAsync(bool offset = false)
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new("damage", 20000m, 100m, offset ? 90m : 0m)],
            offset ? [(new DateOnly(y, 10, 1), 90m)] : null);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(y, 10, 2));
        var accountant = await w.UserAsync("accountant");
        var today = new DateOnly(y, 11, 3);
        var paid = offset
            ? await w.PayAsync(accountant, claim, "250.00", today, today, ("offset_premium", "on"))
            : await w.PayAsync(accountant, claim, "250.00", today, today);
        Assert.NotNull(paid.RedirectTo);
        await w.CloseMonthAsync(claims.Id, new DateOnly(y, 9, 1));
        return new Paid(claims, accountant, policy, claim, await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @claim", new { claim }), today, y);
    }

    /// <summary>Runs the statements in a transaction of the application role that is always rolled back; "ok" or the refusal's code.</summary>
    private async Task<string> AttemptAsync(Guid actor, Func<Held, Task> run)
    {
        await using var h = await HoldAsync(db, actor);
        return await CodeOf(() => run(h));
    }

    // ---------------------------------------------------------------- claim-reversal-db-check.ts (0033, 0034)

    /// <summary>
    /// The mirror of a payment written by the database from the stored row, as reverseClaimPayment writes it — the set-off's rate and
    /// currency (H46, 0012) included, which 0015 (Codex R006-01) compares NULL-safely.
    /// </summary>
    private static Task<Guid> MirrorAsync(Held h, Guid paymentId, DateOnly on, Guid by, decimal? amount = null, decimal? rate = null, Guid? offset = null,
        string reason = "correction", DateOnly? returned = null, string? bank = null) => MirrorCoreAsync(h, paymentId, on, by, amount, rate, offset, reason, returned, bank);

    private static async Task<Guid> MirrorCoreAsync(Held h, Guid paymentId, DateOnly on, Guid by, decimal? amount, decimal? rate, Guid? offset, string reason, DateOnly? returned, string? bank)
    {
        var id = Guid.NewGuid();
        await h.Run("""
            insert core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
                                       offset_payment_id, note, created_by, kind, reverses_claim_payment_id, reversal_reason, returned_on, bank_reference,
                                       offset_rate, offset_currency)
            select @id, claim_id, @on, coalesce(@amount, -amount), -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, coalesce(@rate, rate), payee_kind,
                   payee_party_id, @offset, N'check-0033', @by, N'reversal', id, @reason, @returned, @bank, offset_rate, offset_currency
            from core.claim_payment where id = @paymentId
            """, new { id, paymentId, on, amount, rate, offset, by, reason, returned, bank });
        return id;
    }

    [Fact]
    public async Task R0033_the_application_may_add_claim_payments_but_never_change_or_remove_them()
    {
        await using var c = await db.OpenAppAsync();
        Assert.Equal("100", await c.ExecuteScalarAsync<string>("""
            select concat(has_perms_by_name(N'core.claim_payment', N'OBJECT', N'INSERT'), has_perms_by_name(N'core.claim_payment', N'OBJECT', N'UPDATE'),
                          has_perms_by_name(N'core.claim_payment', N'OBJECT', N'DELETE'))
            """));
    }

    [Fact]
    public async Task R0033_the_exact_mirror_dated_today_is_accepted_and_every_wrong_reversal_is_refused_by_its_own_guard()
    {
        var x = await PaidAsync();
        var by = x.Accountant.Id;
        var accepted = await AttemptAsync(by, async h =>
        {
            var r = await MirrorAsync(h, x.Payment, x.Today, by);
            Assert.True(await h.Scalar<bool>("""
                select convert(bit, case when r.amount = -o.amount and r.deductible_withheld = -o.deductible_withheld and r.premium_offset = -o.premium_offset
                    and r.net_paid = -o.net_paid and r.net_paid_gel = -o.net_paid_gel and r.rate = o.rate then 1 else 0 end)
                from core.claim_payment r join core.claim_payment o on o.id = r.reverses_claim_payment_id where r.id = @r
                """, new { r }));
        });
        Assert.Equal("ok", accepted);
        Assert.Equal("23505 claim_payment_reversal_once", await AttemptAsync(by, async h => { await MirrorAsync(h, x.Payment, x.Today, by); await MirrorAsync(h, x.Payment, x.Today, by); }));
        Assert.Equal("CP001", await AttemptAsync(by, async h => await MirrorAsync(h, await MirrorAsync(h, x.Payment, x.Today, by), x.Today, by)));
        Assert.Equal("CP002", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today, by, rate: 9.999999m)));
        Assert.Equal("CP003", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today, by, amount: -0.01m)));
        Assert.Equal("CP004", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today.AddDays(-1), by)));
        // Dated in the closed September: the month guard answers first (no constraint name).
        Assert.Equal("23514", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, new DateOnly(x.Year, 9, 30), by)));
        Assert.Equal("23514 claim_payment_kind_shape", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today, by, reason: "returned", returned: x.Today)));
        Assert.Equal("ok", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today, by, reason: "returned", returned: x.Today, bank: "CHECK-0033")));
        Assert.Equal("CP007", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today, by, reason: "returned", returned: x.Today.AddDays(-1), bank: "CHECK-0033")));
        Assert.Equal("23514 claim_payment_kind_shape", await AttemptAsync(by, h => h.Run("""
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, created_by)
            values (@claim, @on, -1, 0, 0, -1, -1, 1, N'policyholder', @by)
            """, new { claim = x.Claim, on = x.Today, by })));
        // Nothing of the check stays.
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where note = N'check-0033'"));
    }

    [Fact]
    public async Task R0033_a_set_off_goes_back_only_by_its_own_receipts_reversal_on_the_same_day()
    {
        var x = await PaidAsync(offset: true);
        var by = x.Accountant.Id;
        var receipt = await w.ScalarAsync<Guid>("select offset_payment_id from core.claim_payment where id = @id", new { id = x.Payment });
        Assert.Equal(90m, await w.ScalarAsync<decimal>("select amount_gel from core.payment where id = @receipt", new { receipt }));
        async Task<Guid> BackReceipt(Held h, DateOnly on)
        {
            var id = Guid.NewGuid();
            await h.Run("""
                insert core.payment (id, received_on, amount_gel, method, kind, reverses_payment_id, payer_party_id, reference, created_by)
                select @id, @on, -amount_gel, method, N'reversal', id, payer_party_id, N'check-0033', @by from core.payment where id = @receipt
                """, new { id, on, receipt, by });
            return id;
        }
        Assert.Equal("CP005", await AttemptAsync(by, h => MirrorAsync(h, x.Payment, x.Today, by)));
        Assert.Equal("ok", await AttemptAsync(by, async h => await MirrorAsync(h, x.Payment, x.Today, by, offset: await BackReceipt(h, x.Today))));
        Assert.Equal("CP005", await AttemptAsync(by, async h => await MirrorAsync(h, x.Payment, x.Today.AddDays(1), by, offset: await BackReceipt(h, x.Today))));
        // A payment without a set-off gives none back.
        var y = await PaidAsync();
        Assert.Equal("CP005", await AttemptAsync(by, async h => await MirrorAsync(h, y.Payment, y.Today, by, offset: await BackReceipt(h, x.Today))));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where reference = N'check-0033'"));
    }

    [Fact]
    public async Task R0033_a_reversal_that_would_leave_regress_above_what_was_paid_is_CP006_and_the_case_claiming_what_stays_is_accepted()
    {
        var x = await PaidAsync();
        var by = x.Accountant.Id;
        // Paid on the claim: net 150; a second payment of 300 (net 300) — what stays after reversing the first is 300.
        Assert.NotNull((await w.PayAsync(x.Accountant, x.Claim, "300.00", x.Today, x.Today)).RedirectTo);
        async Task<string> Case(decimal claimed) => await AttemptAsync(by, async h =>
        {
            var caseId = Guid.NewGuid();
            await h.Run("insert core.subrogation_case (id, number, claim_id, opened_on, request_key) values (@caseId, N'CHK-0033', @claim, @on, newid())",
                new { caseId, claim = x.Claim, on = x.Today });
            await h.Run("""
                insert core.subrogation_movement (case_id, kind, amount_gel, booked_on, document_kind, document_date, request_key)
                values (@caseId, N'claimed', @claimed, @on, N'demand_letter', @on, newid())
                """, new { caseId, claimed, on = x.Today });
            await MirrorAsync(h, x.Payment, x.Today, by);
        });
        Assert.Equal("CP006", await Case(450m));
        Assert.Equal("ok", await Case(300m));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.subrogation_case where number = N'CHK-0033'"));
    }

    // ---------------------------------------------------------------- claim-reserve-db-check.ts (0035), what ClaimReserveTests does not hold

    [Fact]
    public async Task R0035_rights_triggers_and_the_requests_own_checks_with_its_asker_from_the_session()
    {
        await using (var c = await db.OpenAppAsync())
            Assert.Equal("110000", await c.ExecuteScalarAsync<string>("""
                select concat(has_perms_by_name(N'core.claim_reserve_request', N'OBJECT', N'SELECT'), has_perms_by_name(N'core.claim_reserve_request', N'OBJECT', N'INSERT'),
                              has_perms_by_name(N'core.claim_reserve_request', N'OBJECT', N'UPDATE'), has_perms_by_name(N'core.claim_reserve_request', N'OBJECT', N'DELETE'),
                              has_perms_by_name(N'core.claim_reserve', N'OBJECT', N'UPDATE'), has_perms_by_name(N'core.claim_reserve', N'OBJECT', N'DELETE'))
                """));
        // The request's audit, and the movement's guard (the month guard and the request guard of 0011/0035 in one trigger).
        Assert.Equal(["claim_reserve.claim_reserve_guard", "claim_reserve_request.claim_reserve_request_audit"], await w.RowsAsync<string>("""
            select convert(nvarchar(200), object_name(parent_id) + N'.' + name collate database_default) from sys.triggers
            where object_name(parent_id) in (N'claim_reserve', N'claim_reserve_request') and is_disabled = 0 order by 1
            """));

        var x = await PaidAsync();
        var asker = x.Claims.Id;
        var admin = await w.UserAsync("admin");
        var level = await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @c", new { c = x.Claim });
        Task<string> Ask(Guid by, decimal @base, decimal newLevel, decimal change, string reason = "check-0035", DateOnly? on = null, bool withAsker = true) =>
            AttemptAsync(by, h => h.Run(withAsker
                ? "insert core.claim_reserve_request (claim_id, booked_on, base_level, new_level, change, reason, created_by) values (@claim, @on, @base, @newLevel, @change, @reason, @by)"
                : "insert core.claim_reserve_request (claim_id, booked_on, base_level, new_level, change, reason) values (@claim, @on, @base, @newLevel, @change, @reason)",
                new { claim = x.Claim, on = on ?? x.Today, @base, newLevel, change, reason, by }));
        Assert.Equal("23514 claim_reserve_request_difference", await Ask(asker, level, level + 200.50m, 200.49m));
        Assert.Equal("23514 claim_reserve_request_change_check", await Ask(asker, level, level, 0m));
        Assert.Equal("23514 claim_reserve_request_new_level_check", await Ask(asker, level, -1m, -1m - level));
        Assert.Equal("23514 claim_reserve_request_reason_check", await Ask(asker, level, level + 200.50m, 200.50m, " "));
        // Who asked is filled from the session when not given.
        Assert.Equal("ok", await AttemptAsync(asker, async h =>
        {
            var id = Guid.NewGuid();
            await h.Run("insert core.claim_reserve_request (id, claim_id, booked_on, base_level, new_level, change, reason) values (@id, @claim, @on, @level, @level + 200.50, 200.50, N'check-0035')",
                new { id, claim = x.Claim, on = x.Today, level });
            Assert.Equal(asker, await h.Scalar<Guid>("select created_by from core.claim_reserve_request where id = @id", new { id }));
        }));

        // The admin approving its own request is refused too; a second approval meets the stale guard before the unique index.
        async Task<string> Approve(Guid askedBy, Guid approver, bool twice = false, DateOnly? on = null) => await AttemptAsync(approver, async h =>
        {
            var id = Guid.NewGuid();
            await h.Run("""
                insert core.claim_reserve_request (id, claim_id, booked_on, base_level, new_level, change, reason, created_by)
                values (@id, @claim, @on, @level, @level + 200.50, 200.50, N'check-0035', @askedBy)
                """, new { id, claim = x.Claim, on = on ?? x.Today, level, askedBy });
            const string Carry = "insert core.claim_reserve (claim_id, booked_on, change, reason, created_by, request_id) select claim_id, booked_on, change, reason, @approver, id from core.claim_reserve_request where id = @id";
            await h.Run(Carry, new { id, approver });
            if (twice) await h.Run(Carry, new { id, approver });
        });
        Assert.Equal("RR003", await Approve(admin.Id, admin.Id));
        Assert.Equal("ok", await Approve(asker, admin.Id));
        Assert.Equal("RR004", await Approve(asker, admin.Id, twice: true));
        // A request dated in a closed month: the month guard refuses first (no constraint), the day is not moved.
        Assert.Equal("23514", await Approve(asker, admin.Id, on: new DateOnly(x.Year, 9, 16)));
        Assert.Equal(0, await w.ScalarAsync<int>("select (select count(*) from core.claim_reserve_request where reason = N'check-0035') + (select count(*) from core.claim_reserve where reason = N'check-0035')"));
    }

    // ---------------------------------------------------------------- claim-payee-race.ts R1–R8

    private sealed record Race(CurrentUser Accountant, ClaimWorld.Policy Policy, Guid Claim, string HolderTax, DateOnly Today);

    /// <summary>A client with an approved policy and a claim approved for 5 000 (legacy fixture()).</summary>
    private async Task<Race> RaceAsync()
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("claims");
        var holderTax = Fixtures.RandomTaxId();
        var p = await w.PolicyAsync(u.Id, new DateOnly(y, 4, 1), [new("damage", 20000m, 0m, 365m)], [(new DateOnly(y, 4, 1), 365m)], party: await w.PartyAsync(holderTax, "კლიენტი"));
        var claim = (await w.RegisterAsync(u, p, new DateOnly(y, 4, 11), new DateOnly(y, 4, 12), ("damage", "5000")))[0];
        await w.ApproveAsync(u, claim, "5000", new DateOnly(y, 4, 12));
        return new Race(await w.UserAsync("accountant"), p, claim, holderTax, new DateOnly(y, 5, 1));
    }

    /// <summary>
    /// Every row of the claim's and its policy's financial picture, in full (legacy financial-picture.ts): payments and reserve of the
    /// claim, the claim and its event, the policy, contract, lines, schedule and adjustments, allocations on the schedule, every receipt
    /// tied to it, and the audit rows of the audited records among them.
    /// </summary>
    private async Task<string> PictureAsync(Race r) => await w.ScalarAsync<string>("""
        declare @inst table (id uniqueidentifier primary key);
        insert @inst select id from core.installment where policy_id = @policy;
        select concat(
          (select * from core.claim_payment where claim_id = @claim order by id for json path),
          (select * from core.claim_reserve where claim_id = @claim order by id for json path),
          (select * from core.claim where id = @claim for json path),
          (select * from core.loss_event where id = (select loss_event_id from core.claim where id = @claim) for json path),
          (select * from core.policy where id = @policy for json path),
          (select * from core.contract where id = (select contract_id from core.policy where id = @policy) for json path),
          (select * from core.policy_line where policy_id = @policy order by id for json path),
          (select * from core.installment where policy_id = @policy order by id for json path),
          (select * from core.installment_adjustment where installment_id in (select id from @inst) order by id for json path),
          (select * from core.payment_allocation where installment_id in (select id from @inst) order by id for json path),
          (select * from core.payment t where t.id in (select payment_id from core.payment_allocation where installment_id in (select id from @inst))
             or t.payer_party_id = @party or t.id in (select offset_payment_id from core.claim_payment where claim_id = @claim) order by id for json path),
          (select * from core.audit_log where (table_name = N'claim' and row_id = lower(convert(nchar(36), @claim)))
             or (table_name = N'policy' and row_id = lower(convert(nchar(36), @policy)))
             or (table_name = N'installment' and row_id in (select lower(convert(nchar(36), id)) from @inst)) order by id for json path))
        """, new { claim = r.Claim, policy = r.Policy.Id, party = r.Policy.PartyId });

    private static Task Suspend(Held h, Guid party) => h.Run("""
        insert core.fms_suspension (party_id, kind, scope, instructed_at, ends_at, note)
        values (@party, N'written', N'all', sysdatetimeoffset(), dateadd(day, 3, sysdatetimeoffset()), N'race')
        """, new { party });

    /// <summary>The legacy sanctions decision: the party row is locked and its flag set (decideScreening, 'confirm').</summary>
    private static async Task Sanction(Held h, Guid party)
    {
        await h.Run("select id from core.party with (updlock, rowlock, holdlock) where id = @party", new { party });
        await h.Run("update core.party set sanctions_blocked = 1 where id = @party", new { party });
    }

    /// <summary>A writer of the kind given, holding its uncommitted change on the party.</summary>
    private async Task<Held> WriterAsync(bool fms, Guid party, Guid actor)
    {
        var h = await HoldAsync(db, actor);
        if (fms)
        {
            await Aml.LockForSuspensionChangeAsync(h.Tx, party);
            await Suspend(h, party);
        }
        else await Sanction(h, party);
        return h;
    }

    [Theory]
    [InlineData("R1", true, "payee", "third")]
    [InlineData("R2", false, "payee", "third")]
    [InlineData("R4", true, "holder", "third")]
    [InlineData("R5", false, "holder", "third")]
    [InlineData("R6", true, "holder", "holder")]
    [InlineData("R7", false, "holder", "holder-as-other")]
    public async Task R1_to_R7_a_writer_first_holds_the_payout_on_its_participant_which_is_then_refused_with_nothing_written(string id, bool fms, string target, string payee)
    {
        var r = await RaceAsync();
        var thirdTax = Fixtures.RandomTaxId();
        var third = await w.PartyAsync(thirdTax, "სერვისი");
        (string, string)[] payeeFields = payee switch
        {
            "third" => [("payee_kind", "workshop"), ("payee_id_number", thirdTax)],
            "holder" => [("payee_kind", "policyholder")],
            _ => [("payee_kind", "other"), ("payee_id_number", r.HolderTax)],
        };
        var expected = fms && target == "holder" ? AmlTexts.Suspension.Blocked : ClaimTexts.Errors.PayeeBlocked;
        var before = await PictureAsync(r);
        await using var writer = await WriterAsync(fms, target == "payee" ? third : r.Policy.PartyId, r.Accountant.Id);
        var payout = w.PayAsync(r.Accountant, r.Claim, "100", r.Today, r.Today, payeeFields);
        await AssertWaitingAsync(payout);
        await writer.CommitAsync();
        var answer = await payout;
        Assert.Equal($"{id} {expected}", $"{id} {answer.Message}");
        Assert.Null(answer.RedirectTo);
        Assert.Equal(before, await PictureAsync(r));
    }

    [Theory]
    [InlineData("R3", true)]
    [InlineData("R8", false)]
    public async Task R3_R8_a_payout_first_makes_the_writer_on_the_third_payee_wait_pays_once_and_the_next_payout_is_refused(string id, bool fms)
    {
        var r = await RaceAsync();
        var thirdTax = Fixtures.RandomTaxId();
        var third = await w.PartyAsync(thirdTax, "სერვისი");
        (string, string)[] payee = [("payee_kind", "workshop"), ("payee_id_number", thirdTax)];
        var before = await PictureAsync(r);
        // The payout is held at its first write: a gate holds the claim payments' table shared.
        await using var gate = await HoldAsync(db, null);
        await gate.Run("select top (0) id from core.claim_payment with (tablock, holdlock)");
        var payout = w.PayAsync(r.Accountant, r.Claim, "100", r.Today, r.Today, payee);
        await AssertWaitingAsync(payout);
        await using var writer = await HoldAsync(db, r.Accountant.Id);
        var writing = fms
            ? Task.Run(async () => { await Aml.LockForSuspensionChangeAsync(writer.Tx, third); await Suspend(writer, third); })
            : Sanction(writer, third);
        await AssertWaitingAsync(writing);
        await gate.CommitAsync();
        var first = await payout;
        Assert.True(first.RedirectTo?.EndsWith("paid=1", StringComparison.Ordinal), $"{id}: {first.Message}");
        await writing;
        await writer.CommitAsync();
        // Exactly one payment and one reserve row were added; nothing else of the picture moved but the claim's status audit.
        Assert.Equal([(100m, 0m, 0m, 100m, 100m, 1m, "workshop", (Guid?)third, (Guid?)null)], await w.RowsAsync<(decimal, decimal, decimal, decimal, decimal, decimal, string, Guid?, Guid?)>(
            "select amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_payment_id from core.claim_payment where claim_id = @c", new { c = r.Claim }));
        Assert.Equal((r.Today, -100m, ClaimTexts.Actions.Pay), (await w.RowsAsync<(DateOnly, decimal, string)>(
            "select top (1) booked_on, change, reason from core.claim_reserve where claim_id = @c order by created_at desc", new { c = r.Claim })).Single());
        Assert.NotEqual(before, await PictureAsync(r));
        var middle = await PictureAsync(r);
        var second = await w.PayAsync(r.Accountant, r.Claim, "100", r.Today, r.Today, payee);
        Assert.Equal(ClaimTexts.Errors.PayeeBlocked, second.Message);
        Assert.Equal(middle, await PictureAsync(r));
    }

    // ---------------------------------------------------------------- db-acceptance.ts: claim payments, claim-targeted suspension, XL, H08

    private async Task<(CurrentUser Accountant, ClaimWorld.Policy Policy, Guid Claim, DateOnly Today)> ApprovedAsync(decimal approved = 1000m)
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("claims");
        var p = await w.PolicyAsync(u.Id, new DateOnly(y, 8, 1), [new("damage", 20000m, 0m)]);
        var claim = (await w.RegisterAsync(u, p, new DateOnly(y, 9, 30), new DateOnly(y, 10, 1), ("damage", "1000")))[0];
        await w.ApproveAsync(u, claim, approved.ToString(System.Globalization.CultureInfo.InvariantCulture), new DateOnly(y, 10, 1));
        return (await w.UserAsync("accountant"), p, claim, new DateOnly(y, 10, 2));
    }

    [Fact]
    public async Task Acceptance_negative_control_two_payments_of_one_claim_without_the_claim_lock_pay_1200_on_a_1000_approval()
    {
        var (a, p, claim, today) = await ApprovedAsync();
        const string PaidSql = "select coalesce(sum(amount), 0) from core.claim_payment where claim_id = @claim";
        await using var s1 = await HoldAsync(db, a.Id);
        await using var s2 = await HoldAsync(db, a.Id);
        // Both read what is paid before either writes.
        var seen = new[] { await s1.Scalar<decimal>(PaidSql, new { claim }), await s2.Scalar<decimal>(PaidSql, new { claim }) };
        Assert.All(seen, s => Assert.True(s + 600m <= 1000m));
        const string Pay = """
            insert core.claim_payment (claim_id, paid_on, amount, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, created_by)
            values (@claim, @today, 600, 600, 600, 1, N'policyholder', @party, @by)
            """;
        await s1.Run(Pay, new { claim, today, party = p.PartyId, by = a.Id });
        await s2.Run(Pay, new { claim, today, party = p.PartyId, by = a.Id });
        await s1.CommitAsync();
        await s2.CommitAsync();
        Assert.Equal(1200m, await w.ScalarAsync<decimal>(PaidSql, new { claim }));
    }

    [Fact]
    public async Task Acceptance_two_payments_of_600_of_one_claim_at_once_wait_for_each_other_and_the_second_is_refused_600_paid()
    {
        var (a, _, claim, today) = await ApprovedAsync();
        var eventId = await w.ScalarAsync<Guid>("select loss_event_id from core.claim where id = @claim", new { claim });
        await using var holder = await HoldAsync(db, a.Id);
        await holder.Run("select id from core.loss_event with (updlock, rowlock, holdlock) where id = @eventId", new { eventId });
        var both = new[] { w.PayAsync(a, claim, "600", today, today), w.PayAsync(a, claim, "600", today, today) };
        await AssertWaitingAsync(both);
        await holder.CommitAsync();
        var outcomes = await Task.WhenAll(both);
        Assert.Single(outcomes, o => o.RedirectTo is not null);
        Assert.Single(outcomes, o => o.Errors.GetValueOrDefault("amount") == ClaimTexts.Errors.Exceeds);
        Assert.Equal(600m, await w.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @claim", new { claim }));
    }

    private static Task SuspendClaim(Held h, Guid party, Guid claim, string number) => h.Run("""
        insert core.fms_suspension (party_id, kind, scope, transaction_ref, target_claim_id, instructed_at, ends_at, created_by)
        values (@party, N'written', N'transaction', @number, @claim, sysdatetimeoffset(), dateadd(day, 3, sysdatetimeoffset()), null)
        """, new { party, claim, number });

    [Fact]
    public async Task Acceptance_a_suspension_aimed_at_the_claim_waits_for_the_payment_in_flight_which_pays_and_the_next_payment_is_refused()
    {
        var (a, p, claim, today) = await ApprovedAsync();
        var number = await w.ScalarAsync<string>("select number from core.claim where id = @claim", new { claim });
        // The payment is held after it locked its event and claim, waiting for the policy (a gate holds it).
        await using var gate = await HoldAsync(db, a.Id);
        await gate.Run("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = p.Id });
        var paying = w.PayAsync(a, claim, "100", today, today);
        await AssertWaitingAsync(paying);
        // The suspension locks the claim first, then the client (lockForSuspensionChange): it waits for the payment.
        await using var suspension = await HoldAsync(db, a.Id);
        var suspending = Task.Run(async () => { await Aml.LockForSuspensionChangeAsync(suspension.Tx, p.PartyId, claimId: claim); await SuspendClaim(suspension, p.PartyId, claim, number); });
        await AssertWaitingAsync(suspending);
        await gate.CommitAsync();
        Assert.NotNull((await paying).RedirectTo);
        await suspending;
        await suspension.CommitAsync();
        Assert.Equal(AmlTexts.Suspension.Blocked, (await w.PayAsync(a, claim, "100", today, today)).Message);
        Assert.Equal(100m, await w.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @claim", new { claim }));
    }

    [Fact]
    public async Task Acceptance_negative_control_a_suspension_that_locks_the_client_before_the_claim_overtakes_the_payment_in_flight()
    {
        // Legacy: the opposite lock orders deadlocked (40P01). Here the foreign key's shared read of the claim does not conflict with the
        // payment's update lock, so the suspension does not wait: it commits first, and the payment already in flight is refused —
        // the order the claim lock first is there to keep (the payment in flight goes through, the suspension after it).
        var (a, p, claim, today) = await ApprovedAsync();
        var number = await w.ScalarAsync<string>("select number from core.claim where id = @claim", new { claim });
        await using var gate = await HoldAsync(db, a.Id);
        await gate.Run("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = p.Id });
        var paying = w.PayAsync(a, claim, "100", today, today);
        await AssertWaitingAsync(paying);
        await using var suspension = await HoldAsync(db, a.Id);
        var suspending = Task.Run(async () => { await Aml.LockForSuspensionChangeAsync(suspension.Tx, p.PartyId); await SuspendClaim(suspension, p.PartyId, claim, number); });
        await AssertNotWaitingAsync(suspending);
        await suspending;
        await suspension.CommitAsync();
        await gate.CommitAsync();
        Assert.Equal(AmlTexts.Suspension.Blocked, (await paying).Message);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @claim", new { claim }));
    }

    private async Task<Guid> TreatyAsync(string kind, int y)
    {
        var id = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, priority, treaty_limit)
            values (@id, @number, N'სატესტო', @reinsurer, @kind, @from, @to, N'GEL', @ceded, @priority, @limit)
            """, new
        {
            id, number = "T-" + ClaimWorld.Tag(), reinsurer = await w.PartyAsync(), kind, from = new DateOnly(y, 1, 1), to = new DateOnly(y, 12, 31),
            ceded = kind == "excess_of_loss" ? (decimal?)null : kind == "facultative" ? 60m : 30m,
            priority = kind == "excess_of_loss" ? 500m : (decimal?)null, limit = kind == "excess_of_loss" ? 5000m : (decimal?)null,
        });
        return id;
    }

    private static Task Cede(Held h, Guid claim, Guid treaty, bool xl) => h.Run(xl
        ? "insert core.claim_cession (claim_id, treaty_id, share_percent, priority, treaty_limit) values (@claim, @treaty, 0, 500, 5000)"
        : "insert core.claim_cession (claim_id, treaty_id, share_percent) values (@claim, @treaty, 30)", new { claim, treaty });

    [Theory]
    [InlineData(true, "RI004")]
    [InlineData(false, "RI002")]
    public async Task Acceptance_two_writers_of_one_claims_shares_the_second_waits_and_is_refused_and_only_the_excess_of_loss_stays(bool secondXl, string code)
    {
        var (a, _, claim, today) = await ApprovedAsync();
        var first = await TreatyAsync("excess_of_loss", today.Year);
        var second = await TreatyAsync(secondXl ? "excess_of_loss" : "quota_share", today.Year);
        await using var s1 = await HoldAsync(db, a.Id);
        await Cede(s1, claim, first, xl: true);
        await using var s2 = await HoldAsync(db, a.Id);
        var ceding = Cede(s2, claim, second, secondXl);
        await AssertWaitingAsync(ceding);
        await s1.CommitAsync();
        Assert.Equal(code, await BareCodeOf(() => ceding));
        Assert.Equal(["excess_of_loss"], await w.RowsAsync<string>("select t.kind from core.claim_cession c join core.ri_treaty t on t.id = c.treaty_id where c.claim_id = @claim", new { claim }));
    }

    [Fact]
    public async Task Acceptance_H08_two_facultative_placements_through_the_application_the_second_waits_for_the_policy_and_its_plan_check_refuses()
    {
        var y = Interlocked.Increment(ref year);
        var actor = await Fixtures.UserAsync(db);
        var policy = await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(y, 8, 1)));
        var (facA, facB) = (await TreatyAsync("facultative", y), await TreatyAsync("facultative", y));
        await using var first = await HoldAsync(db, actor);
        Assert.Equal(ReinsuranceStore.FacultativeOutcome.Placed, await ReinsuranceStore.PlaceFacultativeAsync(first.Tx, policy.PolicyId, facA, actor));
        var second = db.App.AsUserAsync(actor, tx => ReinsuranceStore.PlaceFacultativeAsync(tx, policy.PolicyId, facB, actor));
        await AssertWaitingAsync(second);
        await first.CommitAsync();
        Assert.Equal(ReinsuranceStore.FacultativeOutcome.OverCeded, await second);
        Assert.Equal(60m, await w.ScalarAsync<decimal>("""
            select sum(c.share_percent) from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id where t.policy_line_id = @line
            """, new { line = policy.LineId }));
    }
}
