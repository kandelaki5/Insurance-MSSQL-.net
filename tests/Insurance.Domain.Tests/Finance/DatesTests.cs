using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Tests.Finance;

/// <summary>dates.test.ts: dates typed on forms.</summary>
public class DatesTests
{
    [Fact(DisplayName = "dates typed on forms accepts real dates between 1900 and 2100")]
    public void AcceptsRealDatesBetween1900And2100()
    {
        Assert.True(IsPlausibleDate("2026-04-09"));
        Assert.True(IsPlausibleDate("1900-01-01"));
        Assert.True(IsPlausibleDate("2100-12-31"));
    }

    [Fact(DisplayName = "dates typed on forms refuses a five-digit year the browser lets through, and impossible dates")]
    public void RefusesFiveDigitYearsAndImpossibleDates()
    {
        // What a browser date field sends when 20206 is typed as the year.
        Assert.False(IsPlausibleDate("20206-04-09"));
        Assert.False(IsPlausibleDate("+020206-04-09"));
        Assert.False(IsPlausibleDate("1899-12-31"));
        Assert.False(IsPlausibleDate("2101-01-01"));
        Assert.False(IsPlausibleDate("2026-02-30"));
        Assert.True(IsIsoDate("2026-02-28"));
    }
}
