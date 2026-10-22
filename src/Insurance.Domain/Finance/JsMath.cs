using System.Globalization;

namespace Insurance.Domain.Finance;

/// <summary>
/// The few JavaScript number and string rules the old TypeScript finance core relied on, reproduced exactly so the
/// port gives the same results: Math.round, Math.floor of a quotient, String.prototype.trim and the text of a number.
/// </summary>
internal static class JsMath
{
    /// <summary>Math.round: half toward +infinity (Math.round(-2.5) is -2), not banker's rounding.</summary>
    public static long Round(decimal x) => (long)Math.Floor(x + 0.5m);

    /// <summary>Math.floor(n / d) on whole numbers: toward −infinity, unlike C# integer division.</summary>
    public static long FloorDiv(long n, long d)
    {
        var q = n / d;
        return (n % d != 0 && (n < 0) != (d < 0)) ? q - 1 : q;
    }

    /// <summary>n − d × FloorDiv(n, d): never negative for a positive d.</summary>
    public static long FloorMod(long n, long d) => n - d * FloorDiv(n, d);

    /// <summary>Number.MAX_SAFE_INTEGER, 2^53 − 1.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>Number.isSafeInteger for a whole number.</summary>
    public static bool IsSafeInteger(long value) => value >= -MaxSafeInteger && value <= MaxSafeInteger;

    /// <summary>
    /// String.prototype.trim: removes the ECMAScript WhiteSpace and LineTerminator characters at both ends. This differs
    /// from string.Trim(), which also removes U+0085 and does not remove U+FEFF.
    /// </summary>
    public static string Trim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsJsWhiteSpace(s[start])) start++;
        while (end > start && IsJsWhiteSpace(s[end - 1])) end--;
        return s[start..end];
    }

    private static bool IsJsWhiteSpace(char c) => (int)c switch
    {
        0x0009 or 0x000A or 0x000B or 0x000C or 0x000D or 0x0020 or 0x00A0 or 0x1680 or 0x2028 or 0x2029
            or 0x202F or 0x205F or 0x3000 or 0xFEFF => true,
        >= 0x2000 and <= 0x200A => true,
        _ => false,
    };

    /// <summary>ASCII digit, as \d in a JavaScript regular expression without the u flag.</summary>
    public static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>The text of a whole number as a template literal prints it.</summary>
    public static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
