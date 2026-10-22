// Ported from src/lib/month.ts.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Common;

public static partial class Month
{
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-01\z", RegexOptions.CultureInvariant)]
    private static partial Regex MonthStart();

    /// <summary>
    /// The first day of a real calendar month (YYYY-MM-01, year 1900–2100). The one check for a month
    /// named in a request or in the address bar, so nothing else reaches a date cast in the database:
    /// the periods page let 0000-01-01 through and PostgreSQL rejected year 0 with a 500 (Codex, PR #22).
    /// </summary>
    public static bool IsMonthStart(string? value)
    {
        var m = MonthStart().Match(value ?? "");
        if (!m.Success) return false;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return year is >= 1900 and <= 2100 && month is >= 1 and <= 12;
    }

    private static readonly string[] MonthsKa = ["იანვარი", "თებერვალი", "მარტი", "აპრილი", "მაისი", "ივნისი", "ივლისი", "აგვისტო", "სექტემბერი", "ოქტომბერი", "ნოემბერი", "დეკემბერი"];

    /// <summary>
    /// "აპრილი 2026" for 2026-04-01. As in the old code, characters 6–7 are read as JavaScript's Number() reads them, and a
    /// month that is not 1–12 shows as "undefined".
    /// </summary>
    public static string MonthNameKa(string periodStart)
    {
        var number = JsNumber.Parse(Slice(periodStart, 5, 7));
        var index = number - 1;
        var name = index >= 0 && index <= 11 && Math.Floor(index) == index ? MonthsKa[(int)index] : "undefined";
        return $"{name} {Slice(periodStart, 0, 4)}";
    }

    /// <summary>"აპრილი 2026" for 2026-04-01.</summary>
    public static string MonthNameKa(DateOnly periodStart) => MonthNameKa(periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string Slice(string text, int start, int end)
    {
        start = Math.Min(start, text.Length);
        end = Math.Min(end, text.Length);
        return end > start ? text[start..end] : "";
    }
}
