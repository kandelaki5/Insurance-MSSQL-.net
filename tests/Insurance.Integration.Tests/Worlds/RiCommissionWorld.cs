using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests;

/// <summary>
/// The fictional world of Insurance-Software PR #362's scripts/integration/ri-commission.test.ts (head 540e81a), as real rows: treaty
/// T-FRONT (quota share, 100%, 3.5%) with "Fictional Re"; a USD policy's 10 000.00 premium booked 01.10.2026, its 100% cession's
/// commission 350.00 USD; a partial cancellation booked 15.11.2026 returns −50.00 of it. A second treaty T-QUOTA ("Other Re") cedes 30%
/// of a GEL policy (commission 30.00) — not 100%, so not counted. USD 2.7015 (a made-up rate, stored for 31.10 and 31.12.2026; the old
/// test's stand-in gave it on any day). "Today" is 31.12.2026 (the test clock); October and November are closed, December open (the old
/// test's "open from 2026-12-01").
/// </summary>
public sealed record RiCommissionWorld(Guid Front, Guid Quota, Guid UsdIssue, Guid UsdLine)
{
    public static async Task<RiCommissionWorld> BuildAsync(ReportWorld w)
    {
        w.Database.Clock.SetToday(D("2026-12-31"));
        var re = await w.PartyAsync("Fictional Re", foreignId: "RE-FRONT");
        var re2 = await w.PartyAsync("Other Re", foreignId: "RE-QUOTA");
        var front = await w.TreatyAsync("T-FRONT", "quota_share", "2026-01-01", "2026-12-31", ["13"], currency: "USD", cededPercent: 100m, commissionPercent: 3.5m, reinsurer: re);
        var quota = await w.TreatyAsync("T-QUOTA", "quota_share", "2026-01-01", "2026-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 10m, reinsurer: re2);
        var usd = await w.PolicyAsync("P-USD", "2026-10-01", "2027-10-01", [new LineSpec("13", 10000m, 27015m)], currency: "USD", rate: 2.7015m);
        await w.CessionAsync(usd.IssueRows[0], front, 100m, 10000m, 27015m, "2026-10-01", "2026-10-01", "2027-10-01", commission: 350m, commissionGel: 945.53m);
        var cancel = await w.TxAsync(usd.Lines[0], "cancellation", -1428.57m, -3859.28m, "2026-11-15", "2026-11-15", "2027-10-01", cancels: usd.IssueRows[0]);
        await w.CessionAsync(cancel, front, 100m, -1428.57m, -3859.28m, "2026-11-15", "2026-11-15", "2027-10-01", commission: -50m, commissionGel: -135.08m);
        var gel = await w.PolicyAsync("P-GEL", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 1000m, 1000m)]);
        await w.CessionAsync(gel.IssueRows[0], quota, 30m, 300m, 300m, "2026-10-01", "2026-10-01", "2027-10-01", commission: 30m, commissionGel: 30m);
        await w.RateAsync("USD", "2026-10-31", 2.7015m);
        await w.RateAsync("USD", "2026-12-31", 2.7015m);
        await w.ClosePeriodAsync("2026-10");
        await w.ClosePeriodAsync("2026-11");
        return new RiCommissionWorld(front, quota, usd.IssueRows[0], usd.Lines[0]);
    }
}
