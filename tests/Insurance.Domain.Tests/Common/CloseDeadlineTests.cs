// Ported from src/lib/close-deadline.test.ts.
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class CloseDeadlineTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact(DisplayName = "closing due date: is in time on the last day and overdue from the next")]
    public void InTimeOnLastDay()
    {
        // [month, days, last day in time]
        (string Month, int Days, string Due)[] cases =
        [
            ("2026-09-01", 1, "2026-10-01"), ("2026-09-01", 3, "2026-10-03"), ("2026-09-01", 5, "2026-10-05"),
            ("2026-09-01", 8, "2026-10-08"), ("2026-09-01", 31, "2026-10-31"),
            ("2028-01-01", 1, "2028-02-01"), ("2028-02-01", 1, "2028-03-01"), ("2028-02-01", 3, "2028-03-03"),
            ("2027-02-01", 3, "2027-03-03"), ("2026-12-01", 3, "2027-01-03"), ("2026-12-01", 31, "2027-01-31"),
        ];
        foreach (var (month, days, due) in cases)
        {
            Assert.Equal(D(due), CloseDeadline.CloseDueDate(D(month), days));
            Assert.False(CloseDeadline.IsCloseOverdue(D(month), D(due), days), $"{month} + {days}: {due} is still in time");
            var next = D(due).AddDays(1);
            Assert.True(CloseDeadline.IsCloseOverdue(D(month), next, days), $"{month} + {days}: {next} is overdue");
        }
    }

    [Fact(DisplayName = "closing due date: matches the cases Codex found one day late")]
    public void CodexCases()
    {
        foreach (var (days, today) in new[] { (1, "2026-10-02"), (3, "2026-10-04"), (5, "2026-10-06"), (8, "2026-10-09") })
        {
            Assert.True(CloseDeadline.IsCloseOverdue(D("2026-09-01"), D(today), days), $"{days} days, {today}");
        }
    }

    [Fact(DisplayName = "closing deadline set by the company: uses the days the company set")]
    public void UsesCompanyDays()
    {
        foreach (var (text, days) in new[] { ("3", 3), ("8", 8), (" 6 ", 6), ("1", 1), ("31", 31) })
        {
            Assert.Equal(days, CloseDeadline.CloseWithinDays(new Dictionary<string, string> { [CloseDeadline.CloseDeadlineKey] = text }));
        }
    }

    [Fact(DisplayName = "closing deadline set by the company: keeps 5 days until the company sets a valid number")]
    public void KeepsFiveDays()
    {
        Assert.Equal(5, CloseDeadline.DefaultCloseWithinDays);
        Assert.Equal(5, CloseDeadline.CloseWithinDays(new Dictionary<string, string>()));
        foreach (var bad in new[] { "", "0", "32", "-3", "3.5", "five", "100", "07a" })
        {
            Assert.Null(CloseDeadline.ParseCloseWithinDays(bad));
            Assert.Equal(5, CloseDeadline.CloseWithinDays(new Dictionary<string, string> { [CloseDeadline.CloseDeadlineKey] = bad }));
        }
    }
}
