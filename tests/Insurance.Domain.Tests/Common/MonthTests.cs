// Ported from src/lib/month.test.ts ("isMonthStart (Codex, PR #22)").
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class MonthTests
{
    [Fact(DisplayName = "accepts the first day of a real month")]
    public void AcceptsMonthStart()
    {
        foreach (var v in new[] { "2026-01-01", "2026-12-01", "1900-01-01", "2100-12-01" }) Assert.True(Month.IsMonthStart(v), v);
    }

    [Fact(DisplayName = "rejects anything else before it reaches the database")]
    public void RejectsOthers()
    {
        foreach (var v in new[] { "0000-01-01", "2026-00-01", "2026-13-01", "2026-02-30", "2026-02-02", "1899-12-01", "2101-01-01", "abc", "", null }) Assert.False(Month.IsMonthStart(v), v ?? "null");
    }
}
