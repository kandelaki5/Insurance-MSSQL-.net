namespace Insurance.Domain.Common;

/// <summary>
/// The two date steps of the old src/finance/dates.ts that these modules use, kept here (internal) so they do not wait
/// for the finance port: whole days, and the same day of a later month clamped to that month's last day.
/// </summary>
internal static class DateMath
{
    public static DateOnly AddDays(DateOnly date, int days) => date.AddDays(days);

    /// <summary>Same day of month `months` later; clamps to the last day when that month is shorter.</summary>
    public static DateOnly AddMonths(DateOnly date, int months) => date.AddMonths(months);
}
