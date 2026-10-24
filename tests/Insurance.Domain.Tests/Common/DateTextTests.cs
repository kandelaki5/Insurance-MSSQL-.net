// Ported from src/lib/date-text.test.ts ("dates in the forms: dd/mm/yyyy shown, ISO sent").
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class DateTextTests
{
    [Fact(DisplayName = "shows an ISO date as dd/mm/yyyy, and nothing for anything else")]
    public void IsoToText()
    {
        Assert.Equal(
            ["14/10/2026", "05/01/2026", "", "", ""],
            new[] { "2026-10-14", "2026-01-05", "", "14/10/2026", "2026-1-5" }.Select(DateText.IsoToText));
    }

    [Fact(DisplayName = "reads dd/mm/yyyy — day first — and the dot or dash forms, one-digit day or month too")]
    public void TextToIso()
    {
        Assert.Equal(
            ["2026-10-14", "2026-01-05", "2026-01-05", "2026-10-14", "2026-10-14", "2026-09-01"],
            new[] { "14/10/2026", "05/01/2026", "5/1/2026", "14.10.2026", "14-10-2026", " 01/09/2026 " }.Select(DateText.TextToIso));
    }

    [Fact(DisplayName = "refuses what is not a real day: month 13, 31 September, 29 February outside a leap year, short or American forms")]
    public void RefusesUnrealDays()
    {
        Assert.Equal(
            [null, null, null, "2028-02-29", null, null, null, null],
            new[] { "01/13/2026", "31/09/2026", "29/02/2026", "29/02/2028", "14/10/26", "10/14/2026", "", "2026-10-14" }.Select(DateText.TextToIso));
    }

    [Fact(DisplayName = "groups typed digits as dd/mm/yyyy and keeps typed separators as slashes")]
    public void TypingMask()
    {
        Assert.Equal(
            ["1", "14", "14/1", "14/10", "14/10/2", "14/10/2026", "14/10/2026", "14/10/2026", "5/1/2026", "14/10/2026"],
            new[] { "1", "14", "141", "1410", "14102", "14102026", "141020269", "14.10.2026", "5/1/2026", "14a10b2026" }.Select(DateText.TypingMask));
    }
}
