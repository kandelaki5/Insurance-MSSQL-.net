// Dates as people type and read them in the forms: dd/mm/yyyy, whatever the browser's own language. The forms still send
// the ISO date (yyyy-mm-dd) the server expects; these helpers only translate between the two.
// Ported from src/lib/date-text.ts.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Common;

public static partial class DateText
{
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Iso();

    [GeneratedRegex(@"^([0-9]{1,2})[/.-]([0-9]{1,2})[/.-]([0-9]{4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Typed();

    /// <summary>2026-10-14 → 14/10/2026; anything that is not an ISO date → ''.</summary>
    public static string IsoToText(string iso)
    {
        var m = Iso().Match(iso);
        return m.Success ? $"{m.Groups[3].Value}/{m.Groups[2].Value}/{m.Groups[1].Value}" : "";
    }

    /// <summary>
    /// 14/10/2026 (also 14.10.2026, 14-10-2026 and one-digit day or month) → 2026-10-14; not a real calendar day → null.
    /// A year below 0100 is refused too: the old check went through Date.UTC, which reads such a year as 19xx.
    /// </summary>
    public static string? TextToIso(string text)
    {
        var m = Typed().Match(JsText.Trim(text));
        if (!m.Success) return null;
        var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return $"{m.Groups[3].Value}-{month:D2}-{day:D2}";
    }

    private static readonly int[] Sizes = [2, 2, 4];

    [GeneratedRegex("[.-]", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    [GeneratedRegex("[^0-9/]", RegexOptions.CultureInvariant)]
    private static partial Regex NotDigitOrSlash();

    /// <summary>
    /// What the field shows while typing, from what is in it now: the digits fill day, month and year in turn — two, two and
    /// four — so 14102026 typed at once or one digit at a time (14/1 then 14/10 then 14/102 …) reads 14/10/2026. A separator
    /// the person types (/ . or -) ends the day or month early (5/1/2026) and shows as a slash; other characters are dropped,
    /// and digits past the year's four.
    /// </summary>
    public static string TypingMask(string raw)
    {
        var parts = NotDigitOrSlash().Replace(Separator().Replace(raw, "/"), "").Split('/');
        var fields = new[] { new StringBuilder(), new StringBuilder(), new StringBuilder() };
        var f = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            foreach (var digit in parts[i])
            {
                while (f < 2 && fields[f].Length >= Sizes[f]) f++;
                if (fields[f].Length < Sizes[f]) fields[f].Append(digit);
            }
            if (i < parts.Length - 1 && fields[f].Length != 0 && f < 2) f++;
        }
        return string.Join('/', fields.Take(f + 1).Select(x => x.ToString()));
    }

    /// <summary>
    /// Where the caret goes once the field is regrouped: after as many digits as stood before it in what was typed, and past a
    /// slash when a separator was typed just before it — so typing at the end stays at the end, and a digit changed in the
    /// middle keeps its place.
    /// </summary>
    public static int CaretAfter(string shown, string typed, int caret)
    {
        var digits = JsSlice(typed, caret).Count(c => c is >= '0' and <= '9');
        var pos = 0;
        for (var seen = 0; pos < shown.Length && seen < digits; pos++) if (shown[pos] is >= '0' and <= '9') seen++;
        if (caret > 0 && caret - 1 < typed.Length && typed[caret - 1] is '/' or '.' or '-') while (pos < shown.Length && shown[pos] == '/') pos++;
        return pos;
    }

    // typed.slice(0, end): a negative end counts from the end of the text.
    private static string JsSlice(string text, int end)
    {
        var e = end < 0 ? Math.Max(text.Length + end, 0) : Math.Min(end, text.Length);
        return text[..e];
    }
}
