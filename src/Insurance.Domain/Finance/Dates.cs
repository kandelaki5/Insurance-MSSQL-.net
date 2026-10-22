using System.Globalization;

namespace Insurance.Domain.Finance;

/// <summary>A period of calendar days: `{ from, to }` of the old code; what each end means is said where it is used.</summary>
public sealed record DateRange(DateOnly From, DateOnly To);

/// <summary>
/// Calendar dates without time (IsoDate 'YYYY-MM-DD' of the old code is <see cref="DateOnly"/> here). All finance logic
/// works on whole days. Text typed on a form is checked with <see cref="IsIsoDate"/> / <see cref="IsPlausibleDate"/>.
/// </summary>
public static class Dates
{
    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    /// <summary>Days since 1970-01-01 (UTC calendar days, as Date.parse(date) / 86 400 000).</summary>
    public static int DayNumber(DateOnly date) => date.DayNumber - EpochDayNumber;

    /// <summary>The date as 'YYYY-MM-DD'.</summary>
    public static string ToIso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The date of a 'YYYY-MM-DD' text; anything else is refused with the old dayNumber's message. Year 0000, which the
    /// old code accepted as a date, cannot be a <see cref="DateOnly"/> and is refused too.
    /// </summary>
    public static DateOnly ParseIsoDate(string value)
    {
        if (!IsIsoDate(value) || value.StartsWith("0000", StringComparison.Ordinal)) throw new ArgumentException($"Invalid date: {value}");
        return new DateOnly(int.Parse(value[..4], CultureInfo.InvariantCulture), int.Parse(value[5..7], CultureInfo.InvariantCulture), int.Parse(value[8..10], CultureInfo.InvariantCulture));
    }

    /// <summary>True only for a real calendar date in 'YYYY-MM-DD' form (so 2026-02-30 is rejected).</summary>
    public static bool IsIsoDate(string value)
    {
        // /^\d{4}-\d{2}-\d{2}$/ with ASCII digits only, nothing after the day (not even a line break).
        if (value.Length != 10 || value[4] != '-' || value[7] != '-') return false;
        for (var i = 0; i < 10; i++)
        {
            if (i is 4 or 7) continue;
            if (!JsMath.IsDigit(value[i])) return false;
        }
        var year = (value[0] - '0') * 1000 + (value[1] - '0') * 100 + (value[2] - '0') * 10 + (value[3] - '0');
        var month = (value[5] - '0') * 10 + (value[6] - '0');
        var day = (value[8] - '0') * 10 + (value[9] - '0');
        return month is >= 1 and <= 12 && day >= 1 && day <= DaysInMonth(year, month);
    }

    /// <summary>
    /// A date a person can mean on a form: a real calendar date with a year between 1900 and 2100.
    /// A browser date field accepts years of five or six digits (a slip of the keyboard), which the
    /// database would refuse; such a value is caught here and shown as a form error instead.
    /// </summary>
    public static bool IsPlausibleDate(string value) =>
        IsIsoDate(value) && string.CompareOrdinal(value, "1900-01-01") >= 0 && string.CompareOrdinal(value, "2100-12-31") <= 0;

    /// <summary>Days from `from` up to but not including `to`: 2026-01-01..2027-01-01 is 365 days.</summary>
    public static int DaysBetween(DateOnly from, DateOnly to) => DayNumber(to) - DayNumber(from);

    public static DateOnly AddDays(DateOnly date, int days) => date.AddDays(days);

    /// <summary>Same day of month `months` later; clamps to the last day when that month is shorter.</summary>
    public static DateOnly AddMonths(DateOnly date, int months)
    {
        var (year, month, _) = JsDateUtc(date.Year, date.Month - 1L + months, 1);
        var (_, _, lastDay) = JsDateUtc(year, month, 0);
        return new DateOnly((int)year, (int)month, Math.Min(date.Day, (int)lastDay));
    }

    /// <summary>
    /// JavaScript's Date.UTC(year, monthIndex, day) as a calendar date (year, month 1–12, day): months and days out of
    /// range roll over, and — as in JavaScript — a year from 0 to 99 is taken as 1900 + year.
    /// </summary>
    internal static (long Year, long Month, long Day) JsDateUtc(long year, long monthIndex, long day)
    {
        if (year is >= 0 and <= 99) year += 1900;
        year += JsMath.FloorDiv(monthIndex, 12);
        var month = JsMath.FloorMod(monthIndex, 12) + 1;
        return CivilFromDays(DaysFromCivil(year, month, 1) + day - 1);
    }

    private static bool IsLeap(long year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    private static int DaysInMonth(long year, long month) =>
        month == 2 ? (IsLeap(year) ? 29 : 28) : month is 4 or 6 or 9 or 11 ? 30 : 31;

    // Proleptic Gregorian day count from 1970-01-01 (H. Hinnant's algorithms), valid for any year.
    private static long DaysFromCivil(long y, long m, long d)
    {
        y -= m <= 2 ? 1 : 0;
        var era = JsMath.FloorDiv(y, 400);
        var yoe = y - era * 400;
        var doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    private static (long Year, long Month, long Day) CivilFromDays(long z)
    {
        z += 719468;
        var era = JsMath.FloorDiv(z, 146097);
        var doe = z - era * 146097;
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var y = yoe + era * 400;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var d = doy - (153 * mp + 2) / 5 + 1;
        var m = mp < 10 ? mp + 3 : mp - 9;
        return (m <= 2 ? y + 1 : y, m, d);
    }
}
