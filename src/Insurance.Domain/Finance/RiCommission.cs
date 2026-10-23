// The reinsurer's commission receivable (form N1-4 00140, „სხვა მოთხოვნები გადაზღვევიდან"; owner, 20.10.2026, docs/05 3ე).
// On a 100% reinsured policy the commission (the treaty's percent of the premium) is accrued with the premium, and stays a
// receivable until it is offset against what is owed to the reinsurer or received from it. Per treaty and currency: what was
// accrued by the day (the cessions' commission, cancellations included) less what was settled by then, all in whole minor
// units exactly; in GEL at the reporting date's rate (the rate to the millionth, half away from zero, as money.toGel).
// Only premium rows ceded 100% count for now (owner: „ჯერ მხოლოდ 100%-ზე გავაკეთოთ"), in total over their treaties (owner,
// 20.10.2026: several reinsurers covering 100% together count — „კი, მთავარია ჯამში რამდენი %-ია").
// Ported from Insurance-Software PR #362 (head 540e81a) src/finance/ri-commission.ts; big integers and decimal rates, never a double.
using System.Globalization;
using System.Numerics;

namespace Insurance.Domain.Finance;

public static class RiCommission
{
    /// <summary>One cession's commission, in minor units of the policy's currency (a cancellation negative).</summary>
    public sealed record Accrual(string TreatyId, string Currency, BigInteger Commission);

    /// <summary>One settlement's amount (a reversal negative).</summary>
    public sealed record Settlement(string TreatyId, string Currency, BigInteger Amount);

    /// <summary>What is accrued, settled and left on one treaty in one currency.</summary>
    public sealed record Position(string TreatyId, string Currency, BigInteger Accrued, BigInteger Settled, BigInteger Balance);

    /// <summary>One cession of a premium row, its share as stored (numeric(7, 4) text, '60.0000').</summary>
    public sealed record Share(string PremiumTransactionId, string SharePercent);

    /// <summary>A dated amount of one treaty and currency: an accrual (the cessions' commission, a cancellation negative) or a settlement.</summary>
    public sealed record DatedAmount(DateOnly On, BigInteger Amount);

    /// <summary>What is accrued, settled and left per treaty and currency, in the order the treaties and currencies first appear.</summary>
    public static IReadOnlyList<Position> CommissionPositions(IEnumerable<Accrual> accruals, IEnumerable<Settlement> settlements)
    {
        var keys = new List<(string TreatyId, string Currency)>();
        var accrued = new Dictionary<(string, string), BigInteger>();
        var settled = new Dictionary<(string, string), BigInteger>();
        void Add(Dictionary<(string, string), BigInteger> to, string treatyId, string currency, BigInteger amount)
        {
            var key = (treatyId, currency);
            if (!accrued.ContainsKey(key)) { keys.Add(key); accrued[key] = 0; settled[key] = 0; }
            to[key] += amount;
        }
        foreach (var a in accruals) Add(accrued, a.TreatyId, a.Currency, a.Commission);
        foreach (var s in settlements) Add(settled, s.TreatyId, s.Currency, s.Amount);
        return keys.Select(k => new Position(k.TreatyId, k.Currency, accrued[k], settled[k], accrued[k] - settled[k])).ToList();
    }

    /// <summary>A share's text in ten-thousandths of a percent, exactly ('60.0000' → 600000; legacy /^(\d+)(?:\.(\d{1,4}))?$/).</summary>
    public static BigInteger ShareUnits(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (whole.Length == 0 || !whole.All(JsMath.IsDigit) || (dot >= 0 && (fraction.Length is < 1 or > 4 || !fraction.All(JsMath.IsDigit))))
            throw new ArgumentException($"not a share: {text}");
        return BigInteger.Parse(whole, CultureInfo.InvariantCulture) * 10_000 + BigInteger.Parse(fraction.PadRight(4, '0'), CultureInfo.InvariantCulture);
    }

    /// <summary>The premium rows whose cessions together take exactly 100% of them (one treaty of 100%, or 60% + 40%, …).</summary>
    public static IReadOnlySet<string> FullyCededRows(IEnumerable<Share> cessions)
    {
        var total = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        foreach (var c in cessions) total[c.PremiumTransactionId] = total.GetValueOrDefault(c.PremiumTransactionId) + ShareUnits(c.SharePercent);
        return total.Where(t => t.Value == 1_000_000).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The least of the balances (accrued less settled, by the end of each day) from a day on, over every event known — the balance
    /// on that day itself and after each later accrual or settlement already recorded (Codex PR362_RI_COMMISSION_BACKDATED_SETTLEMENT_OVERDRAW_20261020).
    /// A new settlement on that day may take no more than this: on its own day's balance alone, one dated before a settlement already
    /// recorded could use the same commission twice.
    /// </summary>
    public static BigInteger LeastBalanceFrom(IReadOnlyList<DatedAmount> accruals, IReadOnlyList<DatedAmount> settlements, DateOnly from)
    {
        BigInteger By(DateOnly day) =>
            accruals.Where(a => a.On <= day).Aggregate(BigInteger.Zero, (s, a) => s + a.Amount)
            - settlements.Where(x => x.On <= day).Aggregate(BigInteger.Zero, (s, x) => s + x.Amount);
        var days = accruals.Concat(settlements).Select(e => e.On).Where(on => on > from).Append(from).Distinct().Order();
        return days.Aggregate(By(from), (least, day) => BigInteger.Min(least, By(day)));
    }

    /// <summary>Minor units of a currency in tetri at a rate to the millionth, half away from zero (money.toGel's rule), exactly.</summary>
    public static BigInteger InGelExact(BigInteger minor, string currency, decimal rate)
    {
        if (currency == "GEL") return minor;
        if (!(rate > 0)) throw new InvalidOperationException($"No {currency} rate");
        // Math.round(rate × 1 000 000): a stored rate has six places, so this is exact; a longer one rounds half up (it is positive).
        var micro = rate * 1_000_000m;
        var rateMicro = micro == decimal.Truncate(micro) ? new BigInteger(micro) : new BigInteger(Math.Floor(micro + 0.5m));
        BigInteger n = minor * rateMicro, d = 1_000_000;
        var q = (BigInteger.Abs(n) * 2 + d) / (2 * d);
        return n.Sign < 0 ? -q : q;
    }

    /// <summary>
    /// N1-4 00140 in tetri: every position's balance still owed by the reinsurer, at its currency's rate. A position settled beyond
    /// what was accrued (a later cancellation can bring that about) is no receivable and adds nothing; what it means is the owner's
    /// question, not decided here, so it is returned apart for the page to show.
    /// </summary>
    public static (BigInteger ReceivableGel, IReadOnlyList<Position> OverSettled) CommissionReceivableGel(IEnumerable<Position> positions, Func<string, decimal> rateOf)
    {
        var receivableGel = BigInteger.Zero;
        var overSettled = new List<Position>();
        foreach (var p in positions)
        {
            if (p.Balance.Sign > 0) receivableGel += InGelExact(p.Balance, p.Currency, rateOf(p.Currency));
            else if (p.Balance.Sign < 0) overSettled.Add(p);
        }
        return (receivableGel, overSettled);
    }
}
