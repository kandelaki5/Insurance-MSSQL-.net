// The reinsurer's commission receivable (docs/05 3ე, form N1-4 00140), worked by hand.
// Port of Insurance-Software PR #362 (head 540e81a) src/finance/ri-commission.test.ts, every number as the old test has it.
using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.RiCommission;

namespace Insurance.Domain.Tests.Finance;

public class RiCommissionTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ri-commission.test.ts L7–21.
    [Fact(DisplayName = "accrued less settled per treaty and currency, cancellations and reversals included")]
    public void Positions()
    {
        var positions = CommissionPositions(
            [new("T1", "USD", 35000), new("T1", "USD", -5000), new("T1", "GEL", 350), new("T2", "USD", 1000)],
            [new Settlement("T1", "USD", 10000), new Settlement("T1", "USD", -10000), new Settlement("T1", "USD", 7000), new Settlement("T2", "USD", 1500)]);
        Assert.Equal(
            [new Position("T1", "USD", 30000, 7000, 23000), new Position("T1", "GEL", 350, 0, 350), new Position("T2", "USD", 1000, 1500, -500)],
            positions);
        // 230.00 USD × 2.7015 = 621.345 → 621.35; plus 3.50 GEL; T2 settled beyond its accrual adds nothing and is returned apart.
        var (receivableGel, overSettled) = CommissionReceivableGel(positions, c => c == "USD" ? 2.7015m : 1m);
        Assert.Equal(new BigInteger(62135 + 350), receivableGel);
        Assert.Equal([("T2", new BigInteger(-500))], overSettled.Select(p => (p.TreatyId, p.Balance)));
    }

    // ri-commission.test.ts L22–28.
    [Fact(DisplayName = "in GEL at the rate to the millionth, half away from zero, exactly — beyond what a double holds")]
    public void InGel()
    {
        Assert.Equal(new BigInteger(13508), InGelExact(5000, "USD", 2.7015m));
        Assert.Equal(new BigInteger(-13508), InGelExact(-5000, "USD", 2.7015m));
        Assert.Equal(BigInteger.Parse("9007199254740801"), InGelExact(BigInteger.Parse("9007199254740801"), "GEL", 1m));
        Assert.Equal(BigInteger.Parse("27021597764222403"), InGelExact(BigInteger.Parse("9007199254740801"), "USD", 3m));
        Assert.Equal("No USD rate", Assert.Throws<InvalidOperationException>(() => InGelExact(1, "USD", 0m)).Message);
    }

    // ri-commission.test.ts L29–42 (Codex PR362_RI_COMMISSION_BACKDATED_SETTLEMENT_OVERDRAW_20261020).
    [Fact(DisplayName = "what a new settlement may take: the least balance from its day on over every later event already recorded (Codex PR362 P2)")]
    public void LeastBalance()
    {
        DatedAmount[] accrued = [new(D("2026-12-01"), 10000)];
        // 70.00 recorded on 10.12: on 05.12 the day's balance is 100.00, but 10.12 leaves only 30.00 — a new 40.00 on 05.12 would uncover it.
        Assert.Equal(new BigInteger(3000), LeastBalanceFrom(accrued, [new(D("2026-12-10"), 7000)], D("2026-12-05")));
        // The same 70.00 reversed on 20.12: still only 30.00 between 10.12 and 20.12.
        Assert.Equal(new BigInteger(3000), LeastBalanceFrom(accrued, [new(D("2026-12-10"), 7000), new(D("2026-12-20"), -7000)], D("2026-12-05")));
        // In order: 70.00 on 05.12, then a new one on 10.12 may take 30.00; on the same day as well.
        Assert.Equal(new BigInteger(3000), LeastBalanceFrom(accrued, [new(D("2026-12-05"), 7000)], D("2026-12-10")));
        Assert.Equal(new BigInteger(3000), LeastBalanceFrom(accrued, [new(D("2026-12-10"), 7000)], D("2026-12-10")));
        // A settlement before anything is accrued may take nothing; a later accrual does not lend to an earlier day.
        Assert.Equal(BigInteger.Zero, LeastBalanceFrom(accrued, [], D("2026-11-30")));
        // A later cancellation that leaves less is counted too: 100.00, −50.00 on 15.12 → 50.00 from 05.12.
        Assert.Equal(new BigInteger(5000), LeastBalanceFrom([.. accrued, new(D("2026-12-15"), -5000)], [], D("2026-12-05")));
    }

    // ri-commission.test.ts L43–54 (owner, 20.10.2026: „მთავარია ჯამში რამდენი %-ია").
    [Fact(DisplayName = "a premium row counts when its cessions take 100% in total")]
    public void FullyCeded()
    {
        var full = FullyCededRows([
            new("one", "100.0000"),
            new("two", "60.0000"), new("two", "40.0000"),
            new("three", "33.3333"), new("three", "33.3333"), new("three", "33.3334"),
            new("short", "60.0000"), new("short", "30.0000"),
            new("alone", "30"),
            new("hair", "99.9999"),
        ]);
        Assert.Equal(["one", "three", "two"], full.Order(StringComparer.Ordinal));
        Assert.Equal("not a share: 1e2", Assert.Throws<ArgumentException>(() => FullyCededRows([new("x", "1e2")])).Message);
    }

    // ri-commission.test.ts L55–58.
    [Fact(DisplayName = "nothing accrued, nothing settled: no position, 0")]
    public void Nothing()
    {
        Assert.Empty(CommissionPositions([], []));
        var (receivableGel, overSettled) = CommissionReceivableGel([], _ => 1m);
        Assert.Equal(BigInteger.Zero, receivableGel);
        Assert.Empty(overSettled);
    }
}
