using System.Numerics;
using System.Text;
using Insurance.Domain.Regulatory;

namespace Insurance.Domain.Tests.Regulatory;

// Money is held to the tetri, whatever its last digit (Codex PR349_REGULATORY_XLSX_NATIVE_PRECISION_20261019, INS-REGULATORY-RESERVES-
// XLSX-NATIVE-PRECISION-FIX-20261019): 90071992547408.10 is a tetri amount — Excel holds it as …408.09375, …408.09 to the tetri — and
// must be refused like …408.01, not judged to one place as "0.1". The expected values are worked here from the IEEE double's own bits
// (an independent decoder below), never from the program's. (Port of src/lib/regulatory/excel-cents.test.ts.)
public class ExcelCentsTests
{
    private static readonly byte[] Reserves = Templates.ReservesTemplate();
    private static readonly byte[] N1 = Templates.FinancialAnnexTemplate();

    /// <summary>The double nearest to a decimal text, as an exact fraction — decoded from its bits here, independently.</summary>
    private static (BigInteger N, BigInteger D) HeldAs(string text)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        var sign = bits >> 63 != 0 ? BigInteger.MinusOne : BigInteger.One;
        var exp = (int)((bits >> 52) & 0x7ff);
        var frac = new BigInteger(bits & ((1UL << 52) - 1));
        var mantissa = exp == 0 ? frac : frac | (BigInteger.One << 52);
        var e = (exp == 0 ? 1 : exp) - 1075;
        return e >= 0 ? (sign * (mantissa << e), BigInteger.One) : (sign * mantissa, BigInteger.One << -e);
    }
    /// <summary>Whole tetri of a fraction, half away from zero.</summary>
    private static BigInteger TetriOf((BigInteger N, BigInteger D) x)
    {
        var p = x.N * 100;
        var a = BigInteger.Abs(p);
        var q = (a * 2 + x.D) / (2 * x.D);
        return p.Sign < 0 ? -q : q;
    }
    internal static BigInteger TextTetri(string text)
    {
        var neg = text.StartsWith('-');
        var parts = text.Replace("-", "").Split('.');
        var t = BigInteger.Parse(parts[0] + (parts.Length > 1 ? parts[1] : "").PadRight(2, '0'), System.Globalization.CultureInfo.InvariantCulture);
        return neg ? -t : t;
    }
    /// <summary>Whether Excel's double of this amount is not strictly within half a tetri of it — read back, it is another tetri or a tie (the oracle).</summary>
    private static bool ChangedByExcel(string text)
    {
        var h = HeldAs(text);
        var e = TextTetri(text);
        var gap = h.N * 100 - e * h.D;
        return BigInteger.Abs(gap) * 2 >= h.D;
    }
    private static bool Refused(string text)
    {
        var wb = TemplateWorkbook.Open(Reserves);
        wb.SetNumber(ReservesPlacement.Form11, "C35", text);
        return wb.PrecisionProblems().Any(p => p.Sheet == ReservesPlacement.Form11 && p.Ref == "C35");
    }

    [Theory(DisplayName = "every tetri of a scale: refused exactly when Excel holds it as another tetri")]
    [InlineData("90071992547408", "")]
    [InlineData("90071992547408", "-")]
    [InlineData("35184372088832", "")]
    [InlineData("9007199254740", "")]
    [InlineData("70368744177664", "-")]
    public void EveryTetriOfAScale(string scale, string sign)
    {
        var wrong = new List<string>();
        var changed = 0;
        for (var c = 0; c < 100; c++)
        {
            var text = $"{sign}{scale}.{c:00}";
            var expect = ChangedByExcel(text);
            if (expect) changed++;
            if (Refused(text) != expect) wrong.Add($"{text}: oracle {(expect ? "changed" : "kept")}");
        }
        Assert.Empty(wrong);
        // The scales: at 2⁵³ tetri 40 of the 100 cents move (the double's step there is 1/64 lari); at 2⁵³ / 10 (13 whole digits) none does.
        if (scale == "9007199254740") Assert.Equal(0, changed);
        if (scale == "90071992547408") Assert.Equal(40, changed);
    }

    [Fact(DisplayName = "the cents ending in 0 among them: .10, .40, .60, .90 at 90071992547408 (both signs) are refused — each held as another tetri")]
    public void CentsEndingInZero()
    {
        foreach (var cents in new[] { "10", "40", "60", "90" })
        {
            foreach (var sign in new[] { "", "-" })
            {
                var text = $"{sign}90071992547408.{cents}";
                Assert.True(ChangedByExcel(text), $"{text}: the oracle says Excel keeps it");
                Assert.True(Refused(text), $"{text} not refused");
            }
        }
        Assert.Equal(BigInteger.Parse("9007199254740809"), TetriOf(HeldAs("90071992547408.10")));
        // A tie is refused too: …408.13 is held as …408.125, as near …408.12 as …408.13.
        Assert.True(ChangedByExcel("90071992547408.13") && Refused("90071992547408.13"));
    }

    [Fact(DisplayName = "a total ending in 0 that Excel moves while its parts are kept: refused at the total")]
    public void TotalEndingInZero()
    {
        var wb = TemplateWorkbook.Open(Reserves);
        wb.SetNumber(ReservesPlacement.Form11, "C19", "45035996273704.05");
        wb.SetNumber(ReservesPlacement.Form11, "C35", "45035996273704.05");
        var at = wb.PrecisionProblems().Select(p => $"{p.Sheet}!{p.Ref}").ToList();
        Assert.False(ChangedByExcel("45035996273704.05"));
        Assert.True(ChangedByExcel("90071992547408.10"));
        Assert.Contains($"{ReservesPlacement.Form11}!C48", at);
        Assert.True(!at.Contains($"{ReservesPlacement.Form11}!C19") && !at.Contains($"{ReservesPlacement.Form11}!C35"), string.Join(", ", at));
    }

    private static PremiumReport Report(decimal upr) => new("2026-10-31", [new PremiumClassFigures("13", upr, 0)], new PremiumTotals(upr, 0), [], new ClaimsTotals(0, 0, 0));
    private static ImpairmentForN14 Impairment(decimal gross) => new("2026-10-31", [new ImpairmentTotalsExact("client", 1, gross, 0)]);

    [Theory(DisplayName = "both forms' downloads: refused (422, plain text, no file) or the file (200) — the reserves form and form 1 alike")]
    [InlineData("90071992547408.10", 422)]
    [InlineData("-90071992547408.90", 422)]
    [InlineData("1234.10", 200)]
    [InlineData("12345678.90", 200)]
    public void BothFormsDownloads(string text, int status)
    {
        var t = (decimal)TextTetri(text);
        foreach (var (name, wb) in new[] { ("reserves", ReservesPlacement.FillReservesPlacement(Reserves, Report(t), "x").Workbook), ("form 1 (N1-4)", FinancialAnnex.FillFinancialAnnex(N1, Report(0), Impairment(t), new Dictionary<string, string?>())) })
        {
            var r = RegulatoryExport.XlsxResponse(wb, "f.xlsx");
            Assert.True(r.Status == status, $"{name}: {Encoding.UTF8.GetString(r.Body)[..Math.Min(200, r.Body.Length)]}");
            if (status == 422)
            {
                Assert.Null(r.ContentDisposition);
                Assert.Contains(name == "reserves" ? $"{ReservesPlacement.Form11}!C35" : $"{FinancialAnnex.ReceivablesSheet}!E10", Encoding.UTF8.GetString(r.Body));
            }
        }
    }
}
