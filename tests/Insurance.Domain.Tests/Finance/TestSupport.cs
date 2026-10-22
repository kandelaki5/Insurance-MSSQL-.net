using System.Numerics;
using Insurance.Domain.Finance;

namespace Insurance.Domain.Tests.Finance;

/// <summary>Short helpers so the ported tests read like the TypeScript ones.</summary>
internal static class TestSupport
{
    /// <summary>An IsoDate literal of the old tests.</summary>
    public static DateOnly D(string iso) => Dates.ParseIsoDate(iso);

    public static Money M(long minor, Currency currency) => new(minor, currency);

    /// <summary>money(amount, currency) of the old tests.</summary>
    public static Money Money(decimal amount, Currency currency) => Insurance.Domain.Finance.Money.Of(amount, currency);

    /// <summary>A rate table by date for one currency; anything else has no rate (0).</summary>
    public static RateProvider Rates(Currency currency, Dictionary<string, decimal> byDate) =>
        (c, date) => c == currency && byDate.TryGetValue(Dates.ToIso(date), out var r) ? r : 0m;

    /// <summary>BigInt(Math.round(n * 100)) of the old tests: lari to tetri.</summary>
    public static BigInteger Lari(decimal n) => new(Math.Floor(n * 100 + 0.5m));

    public static PremiumTransaction Tx(long minor, Currency currency, string bookedOn, string coverFrom, string coverTo, PremiumTransaction? cancels = null) =>
        new(new Money(minor, currency), D(bookedOn), D(coverFrom), D(coverTo), cancels);
}
