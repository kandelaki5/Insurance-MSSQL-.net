// Ported from src/lib/numbering.test.ts.
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class NumberingTests
{
    private static Exception Coded(string code)
    {
        var e = new InvalidOperationException("database error");
        e.Data["code"] = code;
        return e;
    }

    [Fact(DisplayName = "a number is padded to the series digits and never cut")]
    public void PaddedNeverCut()
    {
        Assert.Equal("MHL000001", Numbering.FormatNumber("MHL", 6, 1));
        Assert.Equal("MHL999999", Numbering.FormatNumber("MHL", 6, 999999));
        Assert.Null(Numbering.FormatNumber("MHL", 6, 1000000));
        Assert.Equal("1651821", Numbering.FormatNumber("", 7, 1651821));
        Assert.Null(Numbering.FormatNumber("", 7, 10_000_000));
    }

    [Fact(DisplayName = "a series change is checked: prefix, digits, and whether the next number fits")]
    public void SeriesChangeChecked()
    {
        Assert.Null(Numbering.CheckSeries("MHL", 6, 58));
        Assert.Null(Numbering.CheckSeries("", 7, 1));
        Assert.Equal(SeriesProblem.Prefix, Numbering.CheckSeries("mhl", 6, 1));
        Assert.Equal(SeriesProblem.Prefix, Numbering.CheckSeries("MHL 1", 6, 1));
        Assert.Equal(SeriesProblem.Prefix, Numbering.CheckSeries("ABCDEFGHIJK", 6, 1));
        Assert.Equal(SeriesProblem.Digits, Numbering.CheckSeries("MHL", 0, 1));
        Assert.Equal(SeriesProblem.Digits, Numbering.CheckSeries("MHL", 13, 1));
        Assert.Equal(SeriesProblem.Digits, Numbering.CheckSeries("MHL", 2.5m, 1));
        Assert.Equal(SeriesProblem.Fit, Numbering.CheckSeries("MHL", 2, 100));
    }

    [Fact(DisplayName = "only the run-out error is turned into a message; any other error is thrown on")]
    public void OnlyRunOutBecomesMessage()
    {
        Assert.True(Numbering.SeriesExhausted(Coded("NU001")));
        Assert.False(Numbering.SeriesExhausted(Coded("23505")));
        Assert.False(Numbering.SeriesExhausted(null));
        Assert.Null(Numbering.NullIfSeriesExhausted<string>(Coded("NU001")));
        var other = Assert.Throws<InvalidOperationException>(() => Numbering.NullIfSeriesExhausted<string>(Coded("23505")));
        Assert.Equal("23505", other.Data["code"]);
        var lost = Assert.Throws<IOException>(() => Numbering.NullIfSeriesExhausted<string>(new IOException("connection lost")));
        Assert.Contains("connection lost", lost.Message);
    }

    [Fact(DisplayName = "two series overlap when they can ever give the same number from their next values on (Codex H49)")]
    public void FormatsOverlap()
    {
        static SeriesFormat S(string prefix, int digits, long next) => new(prefix, digits, next);
        // Same prefix and digits: always.
        Assert.True(Numbering.FormatsOverlap(S("MHL", 6, 1), S("MHL", 6, 500)));
        // Different prefixes of the same length: never.
        Assert.False(Numbering.FormatsOverlap(S("CAS", 6, 1), S("MHL", 6, 1)));
        // Different total length: never.
        Assert.False(Numbering.FormatsOverlap(S("MHL", 6, 1), S("MHL", 7, 1)));
        // One prefix extends the other with digits: MHL1 + 1 digit (MHL12 … MHL19) inside MHL + 2 digits from 2.
        Assert.True(Numbering.FormatsOverlap(S("MHL1", 1, 2), S("MHL", 2, 2)));
        Assert.True(Numbering.FormatsOverlap(S("MHL", 2, 2), S("MHL1", 1, 2)));
        // … but not once the shorter prefix's series has passed that range.
        Assert.False(Numbering.FormatsOverlap(S("MHL1", 1, 2), S("MHL", 2, 20)));
        // MHL0 + 5 digits from 50000 vs MHL + 6 digits: only while MHL is still below 100000.
        Assert.True(Numbering.FormatsOverlap(S("MHL0", 5, 50000), S("MHL", 6, 1)));
        Assert.False(Numbering.FormatsOverlap(S("MHL0", 5, 50000), S("MHL", 6, 100000)));
        // A prefix extended by a non-digit can never match.
        Assert.False(Numbering.FormatsOverlap(S("MHL-", 5, 1), S("MHL", 6, 1)));
        // Empty prefix (the client's claims) against a numeric prefix of the same length.
        Assert.True(Numbering.FormatsOverlap(S("", 7, 1), S("1", 6, 1)));
    }

    [Fact(DisplayName = "two kinds of document never share a prefix nor a number format (owner, 08.10.2026)")]
    public void KindsClash()
    {
        // UAT 0026–0028 §2.4: claims set to the policies' prefix.
        Assert.True(Numbering.KindsClash(("CAS", 6), ("CAS", 6)));
        // The same prefix with other digits still reads as the same kind.
        Assert.True(Numbering.KindsClash(("CAS", 8), ("CAS", 6)));
        // Two empty prefixes (numbers only) too.
        Assert.True(Numbering.KindsClash(("", 6), ("", 7)));
        // A format that writes the very same numbers: CAS0 + 5 and CAS + 6 both write CAS000068.
        Assert.True(Numbering.KindsClash(("CAS0", 5), ("CAS", 6)));
        Assert.True(Numbering.KindsClash(("", 7), ("1", 6)));
        // Different prefixes that cannot write the same number are fine.
        Assert.False(Numbering.KindsClash(("CLM", 6), ("CAS", 6)));
        Assert.False(Numbering.KindsClash(("C", 6), ("CAS", 6)));
        Assert.False(Numbering.KindsClash(("CAS1", 2), ("CAS", 6)));
        Assert.False(Numbering.KindsClash(("CAS-", 5), ("CAS", 6)));
    }
}
