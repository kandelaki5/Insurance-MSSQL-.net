// Ported from src/lib/close-deadline.ts.
namespace Insurance.Domain.Common;

/// <summary>
/// How many calendar days after a month ends the company aims to close it. Each company sets its
/// own (owner, 06.10.2026: in practice 3–8 days), in Settings; until it does, 5 days apply, as
/// before. Only a whole number of days from 1 to 31 counts; anything else falls back to 5.
/// </summary>
public static class CloseDeadline
{
    public const string CloseDeadlineKey = "accounting.close_within_days";
    public const int DefaultCloseWithinDays = 5;

    /// <summary>The days as stored, or null when the text is not a whole number from 1 to 31.</summary>
    public static int? ParseCloseWithinDays(string? value)
    {
        var text = JsText.Trim(value ?? "");
        if (text.Length is < 1 or > 2 || !text.All(c => c is >= '0' and <= '9')) return null;
        var days = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return days is >= 1 and <= 31 ? days : null;
    }

    /// <summary>The company's closing deadline in days, from the settings map.</summary>
    public static int CloseWithinDays(IReadOnlyDictionary<string, string> settings) =>
        ParseCloseWithinDays(settings.TryGetValue(CloseDeadlineKey, out var value) ? value : null) ?? DefaultCloseWithinDays;

    /// <summary>
    /// The last day on which a month is closed in time: day N after the month ends, counting the first
    /// day after it as day 1 (September with 3 days: 3 October). From the next day it is overdue
    /// (Codex H32: the badge used to switch one day late).
    /// </summary>
    public static DateOnly CloseDueDate(DateOnly monthStart, int days) => DateMath.AddDays(DateMath.AddMonths(monthStart, 1), days - 1);

    /// <summary>An ended month still open is overdue once today is past its closing due date.</summary>
    public static bool IsCloseOverdue(DateOnly monthStart, DateOnly today, int days) => today > CloseDueDate(monthStart, days);
}
