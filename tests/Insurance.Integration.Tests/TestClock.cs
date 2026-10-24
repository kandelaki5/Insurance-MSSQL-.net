namespace Insurance.Integration.Tests;

/// <summary>
/// The tests' clock (DbOptions.Clock): it follows the system clock until a test sets a day, then stands still at that
/// instant until it is set again or <see cref="Release"/>d. Every TestDatabase has one, carried by its Db (TestDatabase.App)
/// and by the web hosts over that database, so a test runs an action on a legacy day ("received 31.10, entered 07.11") as the
/// legacy tests did with their fixed "today". The SQL stamps (created_at, audit, closed_at) stay the server's real time.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset? fixedNow;

    /// <summary>Whether a test has set the clock.</summary>
    public bool IsSet => fixedNow is not null;

    public override DateTimeOffset GetUtcNow() => fixedNow ?? System.GetUtcNow();

    /// <summary>Stands still at <paramref name="instant"/>.</summary>
    public void Set(DateTimeOffset instant) => fixedNow = instant.ToUniversalTime();

    /// <summary>Stands still on <paramref name="day"/> in Tbilisi, at noon (or <paramref name="at"/>) Tbilisi time (UTC+4).</summary>
    public void SetToday(DateOnly day, TimeOnly? at = null) =>
        Set(new DateTimeOffset(day.ToDateTime(at ?? new TimeOnly(12, 0)), TimeSpan.FromHours(4)));

    /// <summary>Follows the system clock again.</summary>
    public void Release() => fixedNow = null;
}
