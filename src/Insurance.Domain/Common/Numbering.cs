// Ported from src/lib/numbering.ts.
using System.Globalization;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Common;

/// <summary>Codes 'prefix', 'digits', 'fit'.</summary>
public enum SeriesProblem { Prefix, Digits, Fit }

/// <param name="Next">The counter: the value of the next number the series will give.</param>
public sealed record SeriesFormat(string Prefix, int Digits, long Next);

public static partial class Numbering
{
    /// <summary>
    /// The error core.next_number raises when a number series has run out of digits (migration 0026,
    /// Codex H40): the document is not created and no number is taken. Actions turn it into a message.
    /// In .NET the code travels in the exception's Data["code"] (the old code read the error's `code`).
    /// </summary>
    public const string SeriesExhaustedCode = "NU001";

    public static bool SeriesExhausted(Exception? error) => error?.Data["code"] is SeriesExhaustedCode;

    /// <summary>For the transaction that takes a number: null when the series has run out, otherwise rethrows.</summary>
    public static T? NullIfSeriesExhausted<T>(Exception error) where T : class
    {
        if (SeriesExhausted(error)) return null;
        ExceptionDispatchInfo.Capture(error).Throw();
        throw error;
    }

    /// <summary>A series prefix: capital Latin letters, digits and dashes, at most 10; it may be empty (the client's claims have none).</summary>
    [GeneratedRegex(@"^[A-Z0-9-]{0,10}\z", RegexOptions.CultureInvariant)]
    public static partial Regex SeriesPrefix();

    public const int MinDigits = 1;
    public const int MaxDigits = 12;

    /// <summary>The number core.next_number would give for this value, or null when the value does not fit the digits (as 0026 refuses).</summary>
    public static string? FormatNumber(string prefix, int digits, long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > digits) return null;
        return prefix + text.PadLeft(digits, '0');
    }

    private static bool AllDigits(string text) => text.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// Whether two series can ever give the same number from their next values on (Codex H49). Two
    /// numbers are equal only if they have the same length, one prefix starts the other, and what the
    /// longer prefix adds is digits — then the values have to meet within both remaining ranges.
    /// </summary>
    public static bool FormatsOverlap(SeriesFormat a, SeriesFormat b)
    {
        if (a.Prefix.Length + a.Digits != b.Prefix.Length + b.Digits) return false;
        var (shortF, longF) = a.Prefix.Length <= b.Prefix.Length ? (a, b) : (b, a);
        if (!longF.Prefix.StartsWith(shortF.Prefix, StringComparison.Ordinal)) return false;
        var extra = longF.Prefix[shortF.Prefix.Length..];
        if (!AllDigits(extra)) return false;
        // A number of `long` with value v is, in `short`'s terms, extra·10^long.digits + v.
        var tenLong = BigInteger.Pow(10, longF.Digits);
        var baseValue = BigInteger.Parse(extra == "" ? "0" : extra, CultureInfo.InvariantCulture) * tenLong;
        var longMax = tenLong - 1;
        var shortMax = BigInteger.Pow(10, shortF.Digits) - 1;
        var shortNext = (BigInteger)shortF.Next;
        var longNext = (BigInteger)longF.Next;
        var low = longNext > shortNext - baseValue ? longNext : shortNext - baseValue;
        var high = longMax < shortMax - baseValue ? longMax : shortMax - baseValue;
        return low <= high;
    }

    /// <summary>
    /// Whether series of two different kinds of document (a policy and a claim, say) would be told apart
    /// by their numbers. Owner, 08.10.2026 (UAT 0026–0028 §2.4): different kinds keep different
    /// prefixes, so a claim is never numbered like a policy. They clash when the prefixes are the same,
    /// whatever the digits (CAS + 6 and CAS + 8), or when the two formats can write the very same
    /// number (CAS0 + 5 and CAS + 6 both write CAS000068) — issued numbers included, so the counters
    /// do not matter here.
    /// </summary>
    public static bool KindsClash((string Prefix, int Digits) a, (string Prefix, int Digits) b)
    {
        if (a.Prefix == b.Prefix) return true;
        if (a.Prefix.Length + a.Digits != b.Prefix.Length + b.Digits) return false;
        var (shortP, longP) = a.Prefix.Length <= b.Prefix.Length ? (a.Prefix, b.Prefix) : (b.Prefix, a.Prefix);
        var extra = longP.StartsWith(shortP, StringComparison.Ordinal) ? longP[shortP.Length..] : null;
        return extra is { Length: > 0 } && AllDigits(extra);
    }

    /// <summary>Checks a requested change of a series; `nextValue` is the counter (the next number's value).</summary>
    public static SeriesProblem? CheckSeries(string prefix, decimal digits, long nextValue)
    {
        if (!SeriesPrefix().IsMatch(prefix)) return SeriesProblem.Prefix;
        if (digits != decimal.Truncate(digits) || digits < MinDigits || digits > MaxDigits) return SeriesProblem.Digits;
        if (FormatNumber(prefix, (int)digits, nextValue) is null) return SeriesProblem.Fit;
        return null;
    }
}
