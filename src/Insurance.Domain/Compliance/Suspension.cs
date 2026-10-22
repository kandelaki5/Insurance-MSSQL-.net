// Suspension of a client's operations on the FMS's instruction (AML law art. 36).
//
// The FMS may order a transaction, or all operations of a client, suspended for up to 72 hours;
// weekends and public holidays do not count towards the 72 hours. An oral instruction is to be
// confirmed in writing within the next 24 hours; if it is not, the insurer is *entitled* to resume
// (art. 36 p. 3) — so the suspension stays in force and the compliance officer decides, it does
// not lapse by itself (the owner's decision, 04.10.2026). The counter needs the Georgian
// public-holiday calendar, including the movable Orthodox Easter days.
// Ported from src/compliance/suspension.ts.
using Insurance.Domain.Common;

namespace Insurance.Domain.Compliance;

/// <summary>Codes 'active', 'awaiting', 'unconfirmed', 'lapsed'.</summary>
public enum SuspensionState { Active, Awaiting, Unconfirmed, Lapsed }

/// <summary>Codes 'all', 'transaction'.</summary>
public enum SuspensionScope { All, Transaction }

/// <param name="LegacyUnmapped">Saved before targets existed and its number matched no single record of the client.</param>
public sealed record SuspensionTargeting(
    SuspensionState State,
    SuspensionScope Scope,
    string? TargetPolicyId,
    string? TargetClaimId,
    bool LegacyUnmapped = false,
    string? TransactionRef = null);

/// <param name="Numbers">Policy and claim numbers of the operation, for suspensions saved before targets existed.</param>
public sealed record OperationTargets(
    IReadOnlyList<string?>? PolicyIds = null,
    IReadOnlyList<string?>? ClaimIds = null,
    IReadOnlyList<string?>? Numbers = null);

public static class Suspension
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan TbilisiOffset = TimeSpan.FromHours(4);
    private static readonly TimeZoneInfo Tbilisi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tbilisi");

    /// <summary>Orthodox Easter Sunday (Julian computus, Meeus) as a Gregorian date.</summary>
    public static DateOnly OrthodoxEaster(int year)
    {
        var a = year % 4;
        var b = year % 7;
        var c = year % 19;
        var d = (19 * c + 15) % 30;
        var e = (2 * a + 4 * b - d + 34) % 7;
        var month = (int)Math.Floor((d + e + 114) / 31.0);
        var day = (d + e + 114) % 31 + 1;
        // Julian calendar date → Gregorian: 13 days later for 1900–2099.
        return new DateOnly(year, 1, 1).AddMonths(month - 1).AddDays(day - 1).AddDays(13);
    }

    /// <summary>Public holidays of Georgia in a year (Labour Code art. 30).</summary>
    public static IReadOnlySet<DateOnly> GeorgianHolidays(int year)
    {
        int[][] fixedDays = [[1, 1], [1, 2], [1, 7], [1, 19], [3, 3], [3, 8], [4, 9], [5, 9], [5, 12], [5, 26], [8, 28], [10, 14], [11, 23]];
        var days = new HashSet<DateOnly>(fixedDays.Select(md => new DateOnly(year, md[0], md[1])));
        var easter = OrthodoxEaster(year);
        foreach (var offset in new[] { -2, -1, 0, 1 }) days.Add(easter.AddDays(offset));
        return days;
    }

    /// <summary>True on Saturdays, Sundays and public holidays.</summary>
    public static bool IsNonWorkingDay(DateOnly date, Func<int, IReadOnlySet<DateOnly>>? holidays = null)
    {
        var day = date.DayOfWeek;
        return day is DayOfWeek.Sunday or DayOfWeek.Saturday || (holidays ?? GeorgianHolidays)(date.Year).Contains(date);
    }

    // The calendar day in Tbilisi of an instant (the old code asked Intl for the Asia/Tbilisi date).
    private static DateOnly TbilisiDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Tbilisi).DateTime);

    /// <summary>
    /// When a suspension that started at `startedAt` ends: `hours` of running time counted only on
    /// working days. Time inside a non-working day is skipped entirely (the clock pauses at the
    /// start of such a day and resumes at its end).
    /// </summary>
    public static DateTimeOffset SuspensionEnd(DateTimeOffset startedAt, int hours = 72, Func<int, IReadOnlySet<DateOnly>>? holidays = null)
    {
        var remaining = hours * Hour;
        var cursor = startedAt.ToUniversalTime();
        for (var guard = 0; guard < 100 && remaining > TimeSpan.Zero; guard++)
        {
            var day = TbilisiDate(cursor);
            // The next midnight is taken at +04:00, as the old code wrote it.
            var nextMidnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TbilisiOffset).AddDays(1).ToUniversalTime();
            if (IsNonWorkingDay(day, holidays)) { cursor = nextMidnight; continue; }
            var untilMidnight = nextMidnight - cursor;
            if (untilMidnight >= remaining) return cursor + remaining;
            remaining -= untilMidnight;
            cursor = nextMidnight;
        }
        return cursor;
    }

    private static string Normalized(string? value) => JsText.ToUpperCase(JsText.Trim(value ?? ""));

    /// <summary>
    /// Whether a suspension blocks an operation touching these policies and claims: one in force
    /// over all operations always does; one over a specific transaction only when the operation
    /// touches its target. A transaction suspension without a target cannot be saved (review H11).
    /// One saved before targets existed whose number could not be mapped keeps the rule it was
    /// saved under — it blocks the operation carrying that number — until someone attaches it to a
    /// record or lifts it; it is never widened to all operations.
    /// </summary>
    public static bool SuspensionBlocks(SuspensionTargeting s, OperationTargets targets)
    {
        if (s.State == SuspensionState.Lapsed) return false;
        if (s.Scope == SuspensionScope.All) return true;
        if (s.TargetPolicyId is not null && (targets.PolicyIds ?? []).Contains(s.TargetPolicyId, StringComparer.Ordinal)) return true;
        if (s.TargetClaimId is not null && (targets.ClaimIds ?? []).Contains(s.TargetClaimId, StringComparer.Ordinal)) return true;
        if (s.LegacyUnmapped && Normalized(s.TransactionRef) != "")
        {
            return (targets.Numbers ?? []).Any(n => Normalized(n) == Normalized(s.TransactionRef));
        }
        return false;
    }

    /// <summary>Deadline for the written confirmation of an oral instruction: 24 clock hours (art. 36 p. 2).</summary>
    public static DateTimeOffset WrittenConfirmationDeadline(DateTimeOffset instructedAt) => instructedAt.ToUniversalTime() + 24 * Hour;
}
