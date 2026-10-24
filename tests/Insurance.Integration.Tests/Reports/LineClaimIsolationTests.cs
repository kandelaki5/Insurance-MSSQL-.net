using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// Two policy lines of one class, two claims: the reinsurance of one never reaches the other (legacy line-claim-isolation.test.ts, AG-C3,
/// AG-R6; docs/04 rules 16, 16a). The original policy's premium is ceded by the real writer to the quota in force on 01.07 (30 %, 109.50);
/// its replacement's reduction from 01.08 to the one in force then (50 %, −16.70). Each claim of 15.08 takes only its own line's treaty,
/// and the claim's shares read only its own reserve and payments. Worked by hand in the old test.
/// </summary>
public sealed class LineClaimIsolationTests
{
    [Fact]
    public async Task Each_claim_takes_only_its_own_lines_treaty_and_its_own_reserve_and_payments()
    {
        await using var w = await CreateAsync();
        var original = await w.PolicyAsync("P-ORIG", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 365m, 365m, 20000m, "GEL")]);
        var negative = await w.PolicyAsync("P-ORIG-1", "2026-08-01", "2027-07-01", [new LineSpec("5.1", -33.40m, -33.40m, 20000m, "GEL")], holder: original.PartyId, replaces: original.Id);
        var old = await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2026-07-31", ["5.1"], cededPercent: 30m);
        var @new = await w.TreatyAsync("RI050", "quota_share", "2026-08-01", "2027-07-31", ["5.1"], cededPercent: 50m);
        await w.CedeAsync(original.IssueRows[0]);
        await w.CedeAsync(negative.IssueRows[0]);
        var c1 = await w.ClaimAsync(original.Id, original.Lines[0], "GEL", "2026-08-15", "2026-08-15");
        var c2 = await w.ClaimAsync(negative.Id, negative.Lines[0], "GEL", "2026-08-15", "2026-08-15");
        await w.AttachClaimCessionsAsync(c1.Id, original.Lines[0], "5.1", "2026-08-15");
        await w.AttachClaimCessionsAsync(c2.Id, negative.Lines[0], "5.1", "2026-08-15");
        await w.ReserveAsync(c1.Id, "2026-08-15", 1000m);
        await w.ReserveAsync(c2.Id, "2026-08-15", 2000m);

        await using (var c = await w.Database.OpenAdminAsync())
        {
            Assert.Equal([(original.IssueRows[0], old, 30m, 109.50m), (negative.IssueRows[0], @new, 50m, -16.70m)],
                await c.QueryAsync<(Guid, Guid, decimal, decimal)>("select premium_transaction_id, treaty_id, share_percent, amount_gel from core.premium_cession order by created_at"));
            Assert.Equal(new[] { (c1.Id, old, 30m), (c2.Id, @new, 50m) }.OrderBy(x => x.Item1),
                (await c.QueryAsync<(Guid, Guid, decimal)>("select claim_id, treaty_id, share_percent from core.claim_cession")).OrderBy(x => x.Item1));
        }
        Task<IReadOnlyList<ReinsuranceStore.ClaimShare>> Shares(Guid claim, string on) => w.App.ReadTxAsync(tx => ReinsuranceStore.ClaimSharesAsync(tx, claim, D(on)));
        static string Flat(IReadOnlyList<ReinsuranceStore.ClaimShare> s) => string.Join(";", s.Select(x => $"{x.TreatyNumber}|{x.Kind}|{x.SharePercent:0.####}|{x.InReserve}|{x.InPaid}"));
        var aug19 = (await Shares(c1.Id, "2026-08-19"), await Shares(c2.Id, "2026-08-19"));
        Assert.Equal(($"RI030|{TreatyKind.QuotaShare}|30|30000|0", $"RI050|{TreatyKind.QuotaShare}|50|100000|0"), (Flat(aug19.Item1), Flat(aug19.Item2)));
        // 100.00 and 200.00 paid on 20.08: 900 × 30 % = 270.00 / 100 × 30 % = 30.00; 1800 × 50 % = 900.00 / 200 × 50 % = 100.00.
        foreach (var (claim, amount) in new[] { (c1.Id, 100m), (c2.Id, 200m) })
        {
            await w.ClaimPaymentAsync(claim, "2026-08-20", amount);
            await w.ReserveAsync(claim, "2026-08-20", -amount);
        }
        Assert.Equal(($"RI030|{TreatyKind.QuotaShare}|30|27000|3000", $"RI050|{TreatyKind.QuotaShare}|50|90000|10000"), (Flat(await Shares(c1.Id, "2026-08-31")), Flat(await Shares(c2.Id, "2026-08-31"))));
        Assert.Equal((Flat(aug19.Item1), Flat(aug19.Item2)), (Flat(await Shares(c1.Id, "2026-08-19")), Flat(await Shares(c2.Id, "2026-08-19"))));
    }
}
