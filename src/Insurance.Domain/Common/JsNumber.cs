using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Common;

/// <summary>
/// JavaScript's Number(text) for settings typed as text: white space around is ignored, an empty text is 0, and
/// 0x / 0o / 0b integers, exponents and Infinity are read as JavaScript reads them; anything else is NaN.
/// </summary>
public static partial class JsNumber
{
    [GeneratedRegex(@"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalLiteral();

    [GeneratedRegex(@"^0(?:[xX][0-9a-fA-F]+|[oO][0-7]+|[bB][01]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RadixLiteral();

    /// <summary>Number(text): the value, NaN when the text is not a number.</summary>
    public static double Parse(string text)
    {
        var t = JsText.Trim(text);
        if (t.Length == 0) return 0;
        if (t is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;
        if (DecimalLiteral().IsMatch(t)) return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (RadixLiteral().IsMatch(t)) return (double)Radix(t);
        return double.NaN;
    }

    /// <summary>
    /// Number(text) as an exact decimal when it is finite: a decimal literal is read digit for digit (where JavaScript
    /// would round it to a double), and a finite value beyond decimal's range is held at decimal's largest value.
    /// Null for NaN and ±Infinity.
    /// </summary>
    public static decimal? ParseFiniteDecimal(string text)
    {
        var value = Parse(text);
        if (!double.IsFinite(value)) return null;
        var t = JsText.Trim(text);
        if (t.Length == 0) return 0m;
        if (DecimalLiteral().IsMatch(t) && decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)) return exact;
        if (RadixLiteral().IsMatch(t))
        {
            var big = Radix(t);
            return big > (BigInteger)decimal.MaxValue ? decimal.MaxValue : (decimal)big;
        }
        return value >= (double)decimal.MaxValue ? decimal.MaxValue : value <= (double)decimal.MinValue ? decimal.MinValue : (decimal)value;
    }

    private static BigInteger Radix(string t)
    {
        int radix = char.ToLowerInvariant(t[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
        BigInteger value = BigInteger.Zero;
        foreach (char c in t.AsSpan(2)) value = value * radix + Convert.ToInt32(c.ToString(), 16);
        return value;
    }
}
