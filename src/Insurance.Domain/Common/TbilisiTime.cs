namespace Insurance.Domain.Common;

/// <summary>
/// Georgia's legal time, UTC+04:00, without daylight saving: in force since 27 March 2005 and the only time this program's
/// operations happen in. The old code asked the system's time-zone database (Intl, Asia/Tbilisi) and so did the first port
/// (TimeZoneInfo); the result then hung on the operating system: Linux's IANA data and Windows' own disagree on Georgia's
/// earlier offsets, and on Windows 12 historical suspension deadlines came out otherwise (Codex R04). The fixed offset
/// gives the same answer on every system, equal to the old program's for every instant from 27 March 2005 on. Before that
/// day the old answers mixed an offset of the time with a +04:00 midnight; see docs/RUNTIME.md.
/// </summary>
public static class TbilisiTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(4);

    /// <summary>The first instant of the fixed offset: Georgia went from +03:00 to +04:00 on 27 March 2005 at 02:00 local.</summary>
    public static readonly DateTimeOffset Since = new(2005, 3, 26, 23, 0, 0, TimeSpan.Zero);

    /// <summary>The calendar day in Tbilisi of an instant.</summary>
    public static DateOnly Date(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    /// <summary>The instant a Tbilisi day begins.</summary>
    public static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), Offset);
}
