using System.Globalization;
using System.Numerics;

namespace Insurance.Api;

/// <summary>
/// An exact amount on the wire (ADR 0004): <see cref="Minor"/> is the whole number of minor units as a decimal string — never a
/// floating-point number — so that no client rounds it; 1 234.50 GEL is { "minor": "123450", "currency": "GEL", "scale": 2 }.
/// </summary>
public sealed record Money(string Minor, string Currency, int Scale)
{
    /// <summary>The program keeps every currency's amounts in hundredths (numeric(18, 2), tetri and cents).</summary>
    public const int ProgramScale = 2;

    public static Money Of(BigInteger minor, string currency) => new(minor.ToString(CultureInfo.InvariantCulture), currency, ProgramScale);
}
