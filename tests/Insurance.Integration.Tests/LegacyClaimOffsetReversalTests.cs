using Insurance.Application.Services;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Integration.Tests;

/// <summary>
/// Legacy scripts/claim-offset-reversal-regression.ts (docs/07 §4.3a, found 08.10.2026): a claim set-off receipt is not
/// reversed on its own. O0 the fixture through the real actions — a policy of 365.00 unpaid, a claim approved for 1 000.00,
/// 100.00 paid with the premium set off: a claim_offset receipt of 100.00 allocated to the instalment, the payment recording
/// premium_offset 100.00 and net 0.00; O1 that receipt reversed by a direct call to ReversePaymentAsync — nothing written: no
/// reversal, every money row of the policy and the claim (payments, allocations, claim payments, reserve, status) and the
/// instalment's outstanding as they were; O2 an ordinary bank receipt of 50.00 on the same policy, recorded through the real
/// action, still reverses: −50.00 with mirror lines summing −50.00. The real day in Tbilisi (the reversal is dated today).
/// </summary>
public sealed class LegacyClaimOffsetReversalTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld c = new(db);
    private readonly World w = new(db);

    [Fact]
    public async Task O0_a_set_off_of_100_O1_its_receipt_reversed_directly_writes_nothing_O2_an_ordinary_receipt_of_50_still_reverses()
    {
        var today = World.Today;
        var claims = await c.UserAsync("claims");
        var admin = await w.UserAsync();
        var policy = await c.PolicyAsync(claims.Id, today.AddDays(-40), [new ClaimWorld.Line("damage", 20000m, null, 365m)], [(today.AddDays(-40), 365m)]);
        var claim = (await c.RegisterAsync(claims, policy, today.AddDays(-10), today.AddDays(-9), ("damage", "1000")))[0];
        await c.ApproveAsync(claims, claim, "1000", today);

        // O0: 100.00 paid with the set-off — a claim_offset receipt of 100.00; the payment 100.00 / set-off 100.00 / net 0.00.
        Assert.Equal($"/claims/{claim}?paid=1", (await c.PayAsync(admin, claim, "100", today, today, ("offset_premium", "on"), ("note", "ჩათვლით"))).RedirectTo);
        var (offset, net, receipt) = (await c.RowsAsync<(decimal, decimal, Guid?)>("select premium_offset, net_paid, offset_payment_id from core.claim_payment where claim_id = @claim", new { claim })).Single();
        Assert.Equal((100m, 0m), (offset, net));
        Assert.NotNull(receipt);
        Assert.Equal([("claim_offset", "receipt", 100m)], await c.RowsAsync<(string, string, decimal)>("select method, kind, amount_gel from core.payment where id = @receipt", new { receipt }));
        Assert.Equal([(policy.Installments[0], 100m)], await c.RowsAsync<(Guid, decimal)>("select installment_id, amount from core.payment_allocation where payment_id = @receipt", new { receipt }));

        // Every money row of the policy and the claim, so that a refusal can be shown to write nothing.
        Task<string> Picture() => c.ScalarAsync<string>("""
            select concat(
              (select string_agg(concat(convert(nvarchar(36), p.id), N':', p.kind, N':', p.method, N':', p.amount_gel, N':', convert(nvarchar(36), p.reverses_payment_id)), N'|') within group (order by p.id)
                 from core.payment p where p.payer_party_id = @party), N'#',
              (select string_agg(concat(convert(nvarchar(36), a.id), N':', convert(nvarchar(36), a.payment_id), N':', a.amount_gel), N'|') within group (order by a.id)
                 from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policy), N'#',
              (select string_agg(concat(convert(nvarchar(36), x.id), N':', x.amount, N':', x.premium_offset, N':', convert(nvarchar(36), x.offset_payment_id)), N'|') within group (order by x.id)
                 from core.claim_payment x where x.claim_id = @claim), N'#',
              (select coalesce(sum(change), 0) from core.claim_reserve where claim_id = @claim), N'#', (select status from core.claim where id = @claim))
            """, new { party = policy.PartyId, policy = policy.Id, claim });
        Task<decimal> Outstanding() => c.ScalarAsync<decimal>(
            "select i.amount - coalesce((select sum(a.amount) from core.payment_allocation a where a.installment_id = i.id), 0) from core.installment i where i.id = @i", new { i = policy.Installments[0] });

        // O1: the set-off receipt reversed by a direct request — nothing written.
        var before = await Picture();
        Assert.Equal(265m, await Outstanding());
        await PaymentActions.ReversePaymentAsync(w.App, admin, receipt.Value.ToString(), policy.Id.ToString(), "პირდაპირი მოთხოვნა");
        Assert.Equal(0, await c.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @receipt", new { receipt }));
        Assert.Equal(0, await c.ScalarAsync<int>("select count(*) from core.receipt_reversal_request where receipt_id = @receipt", new { receipt }));
        Assert.Equal(before, await Picture());
        Assert.Equal(265m, await Outstanding());

        // O2: an ordinary bank receipt of 50.00 on the same policy, reversed: −50.00 with mirror lines summing −50.00.
        Assert.NotNull((await w.PayAsync(admin, policy.Id, "50", today, reference: "ბანკი")).Redirect);
        var bank = await w.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @party and method = N'bank' and kind = N'receipt' and reference = N'ბანკი'", new { party = policy.PartyId });
        await PaymentActions.ReversePaymentAsync(w.App, admin, bank.ToString(), policy.Id.ToString(), "შეცდომა");
        var mirror = Assert.Single(await w.QueryAsync<(Guid Id, decimal Gel)>("select id, amount_gel from core.payment where reverses_payment_id = @bank", new { bank }));
        Assert.Equal(-50m, mirror.Gel);
        Assert.Equal(-50m, await w.ScalarAsync<decimal>("select sum(amount_gel) from core.payment_allocation where payment_id = @id", new { id = mirror.Id }));
    }
}
