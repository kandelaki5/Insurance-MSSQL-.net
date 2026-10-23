using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests;

/// <summary>
/// Legacy scripts/claim-payee-regression.ts K1–K14 (stage 4 item 7, PR #45; Codex H62/H63/H64; AML law art. 36, 41(7)) in one
/// chain on one claim, as the script ran them, through the real PayClaimAsync on a real database: a payee other than the
/// policyholder comes from the client register by ID number and is recorded on the payment; a sanctioned or FMS-suspended
/// recipient is refused with every ledger of the claim and the policy untouched (claim payments, reserve, the policy's
/// allocations, the claim's status — the script's <c>ledgers()</c>); a lapsed suspension, a foreign ID and the client named as
/// "other" are paid; a closed month, a suspended or sanctioned policyholder are refused. The script's world: a GEL policy, one
/// 5.1 line without a deductible (so gross = net), 365.00 unpaid; a claim approved for 5 000.00 (room for every case); the
/// month before "today" closed. Here: the policy from 01.10 of a year of its own, the loss on 05.10, October closed, "today" 03.11.
/// </summary>
public sealed class LegacyClaimPayeeRegressionTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);

    [Fact]
    public async Task K1_to_K14_a_payee_from_the_register_sanctioned_suspended_lapsed_foreign_the_client_as_other_a_closed_month_the_policyholder_suspended_or_sanctioned()
    {
        var y = 2700 + Random.Shared.Next(200);
        var today = new DateOnly(y, 11, 3);
        var claims = await w.UserAsync("claims");
        var admin = await w.UserAsync("admin");
        var clientTax = Fixtures.RandomTaxId();
        var client = await w.PartyAsync(clientTax, "კლიენტი");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 20000m, null, 365m)], [(new DateOnly(y, 10, 1), 365m)], party: client);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 5), new DateOnly(y, 10, 6), ("damage", "5000")))[0];
        await w.ApproveAsync(claims, claim, "5000", new DateOnly(y, 10, 6));
        Assert.Equal("approved", await w.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim }));
        await w.CloseMonthAsync(claims.Id, new DateOnly(y, 10, 1));
        var serviceTax = Fixtures.RandomTaxId();
        var service = await w.PartyAsync(serviceTax, "სერვისცენტრი");
        string unknownTax;
        do unknownTax = Fixtures.RandomTaxId();
        while (await w.ScalarAsync<int>("select count(*) from core.party where tax_id = @t or foreign_id = @t", new { t = unknownTax }) > 0);

        Task<FormOutcome> Pay(string amount, params (string, string)[] more) => ClaimActions.PayClaimAsync(w.App, w.Rates, admin,
            ClaimWorld.Form([("claim_id", claim.ToString()), ("amount", amount), ("paid_on", ClaimWorld.D(today)), ("note", $"K {amount}"), ("total_loss_mode", "none"), .. more]), today);
        Task<List<(decimal Amount, string Kind, Guid? Party)>> Rows() => w.RowsAsync<(decimal, string, Guid?)>(
            "select amount, payee_kind, payee_party_id from core.claim_payment where claim_id = @claim order by created_at", new { claim });
        Task<string> Ledgers() => w.ScalarAsync<string>("""
            select concat((select count(*) from core.claim_payment where claim_id = @claim), N'/', (select count(*) from core.claim_reserve where claim_id = @claim), N'/',
                          (select count(*) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policy), N'/',
                          (select status from core.claim where id = @claim))
            """, new { claim, policy = policy.Id });
        async Task Refused(string message, params (string, string)[] fields)
        {
            var before = await Ledgers();
            var answer = await Pay("100", fields);
            Assert.True(answer.RedirectTo is null && answer.Message == message, $"{answer.RedirectTo} {answer.Message}");
            Assert.Equal(before, await Ledgers());
        }
        async Task Paid(Guid party, params (string, string)[] fields)
        {
            var answer = await Pay("100", fields);
            Assert.Equal($"/claims/{claim}?paid=1", answer.RedirectTo);
            Assert.Equal((100m, party), ((await Rows())[^1].Amount, (await Rows())[^1].Party!.Value));
        }
        async Task<Guid> Suspend(Guid party, bool lapsed = false)
        {
            var id = Guid.NewGuid();
            await w.ExecAsync($"""
                insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at, note)
                values (@id, @party, N'written', N'all', dateadd(day, -1, sysdatetimeoffset()), {(lapsed ? "dateadd(hour, -1, sysdatetimeoffset())" : "dateadd(day, 3, sysdatetimeoffset())")}, N'ტესტი')
                """, new { id, party });
            return id;
        }

        // K1: a workshop without an ID number — refused on the field, no payment row.
        var k1 = await Pay("100", ("payee_kind", "workshop"));
        Assert.Equal(ClaimTexts.Errors.Payee, k1.Errors["payee_id_number"]);
        Assert.Empty(await Rows());
        // K2: an ID number not in the register — refused on the field, no payment row.
        var k2 = await Pay("100", ("payee_kind", "workshop"), ("payee_id_number", unknownTax));
        Assert.Equal(ClaimTexts.Errors.Payee, k2.Errors["payee_id_number"]);
        Assert.Empty(await Rows());
        // K3: the service centre's ID number — paid 300.00, the payment names the service centre.
        Assert.Equal($"/claims/{claim}?paid=1", (await Pay("300", ("payee_kind", "workshop"), ("payee_id_number", serviceTax))).RedirectTo);
        Assert.Equal([(300m, "workshop", (Guid?)service)], await Rows());
        // K4: the policyholder (unchanged path) — paid 200.00 to the policy's client.
        Assert.Equal($"/claims/{claim}?paid=1", (await Pay("200", ("payee_kind", "policyholder"))).RedirectTo);
        Assert.Equal((200m, "policyholder", (Guid?)client), (await Rows())[^1]);

        // K5: a workshop under sanctions.
        var sanctionedTax = Fixtures.RandomTaxId();
        var sanctioned = await w.PartyAsync(sanctionedTax, "სანქცირებული");
        await w.ExecAsync("update core.party set sanctions_blocked = 1 where id = @sanctioned", new { sanctioned });
        await Refused(ClaimTexts.Errors.PayeeBlocked, ("payee_kind", "workshop"), ("payee_id_number", sanctionedTax));
        // K6: a workshop with an FMS suspension in force over all operations.
        var suspendedTax = Fixtures.RandomTaxId();
        await Suspend(await w.PartyAsync(suspendedTax, "შეჩერებული"));
        await Refused(ClaimTexts.Errors.PayeeBlocked, ("payee_kind", "workshop"), ("payee_id_number", suspendedTax));
        // K7: a workshop whose suspension has run out — paid.
        var lapsedTax = Fixtures.RandomTaxId();
        var lapsed = await w.PartyAsync(lapsedTax, "ვადაგასული");
        await Suspend(lapsed, lapsed: true);
        await Paid(lapsed, ("payee_kind", "workshop"), ("payee_id_number", lapsedTax));
        // K8: an "other" payee found by a foreign registration number — paid to that party.
        var foreignId = $"DE-{Guid.NewGuid().ToString("N")[..10]}";
        var foreign = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.party (id, kind, first_name, last_name, foreign_id, is_resident, country_code) values (@foreign, N'person', N'ტესტი', N'უცხოელი', @foreignId, 0, N'DE')
            """, new { foreign, foreignId });
        await Paid(foreign, ("payee_kind", "other"), ("payee_id_number", foreignId));
        // K9: an "other" payee who is the policyholder (the same person by ID) — paid to the client.
        await Paid(client, ("payee_kind", "other"), ("payee_id_number", clientTax));
        // K10: the policyholder, the payment dated in the closed October — refused on paid_on, nothing written.
        var before = await Ledgers();
        var k10 = await ClaimActions.PayClaimAsync(w.App, w.Rates, admin, ClaimWorld.Form(("claim_id", claim.ToString()), ("amount", "100"),
            ("paid_on", ClaimWorld.D(new DateOnly(y, 10, 20))), ("payee_kind", "policyholder"), ("total_loss_mode", "none"), ("note", "დახურული თვე")), today);
        Assert.Equal(ClaimTexts.Errors.PeriodClosed, k10.Errors["paid_on"]);
        Assert.Equal(before, await Ledgers());
        // K11: the policyholder has an FMS suspension in force (the existing check) — the AML message, nothing written; then lifted.
        var onClient = await Suspend(client);
        await Refused(AmlTexts.Suspension.Blocked, ("payee_kind", "workshop"), ("payee_id_number", serviceTax));
        await w.ExecAsync("update core.fms_suspension set lifted_at = sysdatetimeoffset(), lift_reason = N'ტესტი' where id = @onClient", new { onClient });
        // K12: the policyholder under sanctions, the payee itself.
        await w.ExecAsync("update core.party set sanctions_blocked = 1 where id = @client", new { client });
        await Refused(ClaimTexts.Errors.PayeeBlocked, ("payee_kind", "policyholder"));
        // K13 (H64): the policyholder under sanctions, paid through a clean workshop.
        await Refused(ClaimTexts.Errors.PayeeBlocked, ("payee_kind", "workshop"), ("payee_id_number", serviceTax));
        // K14: both the policyholder and the workshop under sanctions.
        await Refused(ClaimTexts.Errors.PayeeBlocked, ("payee_kind", "workshop"), ("payee_id_number", sanctionedTax));

        // In all: 300 + 200 + 100 × 3, nothing else.
        Assert.Equal([300m, 200m, 100m, 100m, 100m], (await Rows()).Select(r => r.Amount));
        Assert.Equal(0, w.Network.Requests);
    }
}
