using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>
/// SheetJS's sheet_to_json as the policy import called it (defval '', raw values): the sheet's first row is the header — each cell's
/// text, an empty one "__EMPTY", a repeated one "name_1", "name_2"… — and every later row that has a value is a record of the header's
/// keys, a missing cell given ''. Also the "General" text of a number (SSF), which a header cell holding a number reads as.
/// </summary>
public static class SheetToJson
{
    public static List<Dictionary<string, object?>> ToRecords(ReadSheet sheet, object? defval)
    {
        var outp = new List<Dictionary<string, object?>>();
        if (sheet.Ref is null) return outp;
        var (sc, sr, ec, er) = XlsxWriter.DecodeRange(sheet.Ref);
        var hdr = new string[Math.Max(ec - sc + 1, 0)];
        var headerCount = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = sc; c <= ec; c++)
        {
            var val = sheet[XlsxWriter.EncodeCell(c, sr)];
            var v = val is null ? "__EMPTY" : FormatCell(val);
            var vv = v;
            var counter = headerCount.GetValueOrDefault(v);
            if (counter == 0) headerCount[v] = 1;
            else
            {
                do { vv = v + "_" + (counter++).ToString(CultureInfo.InvariantCulture); } while (headerCount.GetValueOrDefault(vv) != 0);
                headerCount[v] = counter;
                headerCount[vv] = 1;
            }
            hdr[c - sc] = vv;
        }
        for (var r = sr + 1; r <= er; r++)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            var isEmpty = true;
            for (var c = sc; c <= ec; c++)
            {
                var key = hdr[c - sc];
                var val = sheet[XlsxWriter.EncodeCell(c, r)];
                if (val is null) { row[key] = defval; continue; }
                var v = val.V;
                if (val.T == 'e') { row[key] = v is 0 ? null : defval; continue; }
                if (val.T is not ('s' or 'd' or 'b' or 'n')) throw new InvalidDataException($"unrecognized type {val.T}");
                if (v is null) { row[key] = defval; continue; }
                row[key] = v;
                isEmpty = false;
            }
            if (!isEmpty) outp.Add(row);
        }
        return outp;
    }

    /// <summary>sheet_to_json with header: 1 (raw values, blank rows kept): each row of the sheet's extent as an array of its values from
    /// its first column; a missing cell is a hole (null here) unless a later one is set.</summary>
    public static List<List<object?>> ToArrays(ReadSheet sheet, bool blankRows = true)
    {
        var outp = new List<List<object?>>();
        if (sheet.Ref is null) return outp;
        var (sc, sr, ec, er) = XlsxWriter.DecodeRange(sheet.Ref);
        for (var r = sr; r <= er; r++)
        {
            var row = new List<object?>();
            var isEmpty = true;
            for (var c = sc; c <= ec; c++)
            {
                var val = sheet[XlsxWriter.EncodeCell(c, r)];
                if (val is null) continue;
                object? v = val.T == 'e' ? null : val.V;
                if (v is null && !(val.T == 'e' && val.V is 0)) continue;
                while (row.Count < c - sc) row.Add(null);
                row.Add(v);
                if (v is not null) isEmpty = false;
            }
            if (!isEmpty || blankRows) outp.Add(row);
        }
        return outp;
    }

    /// <summary>SheetJS format_cell: the cell's text — its own (.w), an error's name, else its value in General format.</summary>
    public static string FormatCell(ReadCell cell)
    {
        if (cell.W is not null) return cell.W;
        return cell.T switch
        {
            'e' => cell.V is int code ? ErrorName(code) ?? code.ToString(CultureInfo.InvariantCulture) : "",
            's' => cell.V as string ?? "",
            'b' => cell.V is true ? "TRUE" : "FALSE",
            'n' => cell.V is double d ? GeneralNumber(d) : "",
            'd' => cell.V is JsDate date && date.IsValid ? ShortDate(date) : "",
            _ => "",
        };
    }
    private static string? ErrorName(int code) => code switch { 0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?", 0x24 => "#NUM!", 0x2A => "#N/A", 0x2B => "#GETTING_DATA", 0xFF => "#WTF?", _ => null };
    /// <summary>Excel's format 14 (m/d/yy) of a date: "1/15/26".</summary>
    internal static string ShortDate(JsDate date) => $"{date.Month + 1}/{date.Date}/{(date.Year % 100).ToString("00", CultureInfo.InvariantCulture)}";

    // ---- SSF General for numbers, with JavaScript's toFixed / toPrecision / toExponential worked out exactly.

    private static readonly Regex StripDecimalRe = new("(?:\\.0*|(\\.[0-9]*[1-9])0+)\\z", RegexOptions.CultureInvariant);
    private static string StripDecimal(string o) => o.IndexOf('.') == -1 ? o : JsSemantics.ReplaceFirst(o, StripDecimalRe, "$1");
    private static readonly Regex NormExp1 = new("(?:\\.0*|(\\.[0-9]*[1-9])0+)[Ee]", RegexOptions.CultureInvariant);
    private static readonly Regex NormExp2 = new("(E[+-])([0-9])\\z", RegexOptions.CultureInvariant);
    private static string NormalizeExp(string o) => o.IndexOf('E') == -1 ? o : JsSemantics.ReplaceFirst(JsSemantics.ReplaceFirst(o, NormExp1, "$1E"), NormExp2, "$10$2");

    /// <summary>SSF_general for a number: an integer as it is, else at most 11 characters (SheetJS SSF_general_num).</summary>
    public static string GeneralNumber(double v)
    {
        if (v == Math.Truncate(v) && v >= -2147483648 && v <= 2147483647) return JsSemantics.NumberToString(v);
        var big = Math.Floor(Math.Log(Math.Abs(v)) * 0.4342944819032518);
        if (double.IsInfinity(big) || double.IsNaN(big)) return JsSemantics.NumberToString(v);
        var V = (int)big;
        string o;
        if (V >= -4 && V <= -1) o = ToPrecision(v, 10 + V);
        else if (Math.Abs(V) <= 9) o = SmallExp(v);
        else if (V == 10) o = JsSemantics.Slice(ToFixed(v, 10), 0, 12);
        else o = LargeExp(v);
        return StripDecimal(NormalizeExp(o.ToUpperInvariant()));
    }
    private static string SmallExp(double v)
    {
        var w = v < 0 ? 12 : 11;
        var o = StripDecimal(ToFixed(v, 12)); if (o.Length <= w) return o;
        o = ToPrecision(v, 10); if (o.Length <= w) return o;
        return ToExponential(v, 5);
    }
    private static string LargeExp(double v)
    {
        var o = StripDecimal(ToFixed(v, 11));
        return o.Length > (v < 0 ? 12 : 11) || o == "0" || o == "-0" ? ToPrecision(v, 6) : o;
    }

    /// <summary>The integer n nearest to x·10^k (x = num/den ≥ 0), the larger on a tie.</summary>
    private static BigInteger Scaled(BigInteger num, BigInteger den, int k)
    {
        if (k >= 0) num *= BigInteger.Pow(10, k); else den *= BigInteger.Pow(10, -k);
        return (2 * num + den) / (2 * den);
    }
    public static string ToFixed(double x, int f)
    {
        if (double.IsNaN(x)) return "NaN";
        if (Math.Abs(x) >= 1e21 || double.IsInfinity(x)) return JsSemantics.NumberToString(x);
        var neg = x < 0;
        var (n, d) = TemplateWorkbook.DoubleRational(Math.Abs(x));
        var digits = Scaled(n, d, f).ToString(CultureInfo.InvariantCulture);
        if (f > 0)
        {
            digits = digits.PadLeft(f + 1, '0');
            digits = digits[..^f] + "." + digits[^f..];
        }
        return (neg && x != 0 ? "-" : "") + digits;
    }
    private static (BigInteger N, int E) Significant(double ax, int p)
    {
        var (n, d) = TemplateWorkbook.DoubleRational(ax);
        var e = (int)Math.Floor(Math.Log10(ax));
        for (; ; )
        {
            var m = Scaled(n, d, p - 1 - e);
            if (m >= BigInteger.Pow(10, p)) { e++; continue; }
            if (m < BigInteger.Pow(10, p - 1)) { e--; continue; }
            return (m, e);
        }
    }
    public static string ToPrecision(double x, int p)
    {
        if (!double.IsFinite(x)) return JsSemantics.NumberToString(x);
        var neg = x < 0;
        var sign = neg ? "-" : "";
        if (x == 0) return p == 1 ? "0" : "0." + new string('0', p - 1);
        var (m, e) = Significant(Math.Abs(x), p);
        var ds = m.ToString(CultureInfo.InvariantCulture);
        if (e < -6 || e >= p)
        {
            var mant = p == 1 ? ds : ds[..1] + "." + ds[1..];
            return sign + mant + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }
        if (e == p - 1) return sign + ds;
        if (e >= 0) return sign + ds[..(e + 1)] + "." + ds[(e + 1)..];
        return sign + "0." + new string('0', -(e + 1)) + ds;
    }
    public static string ToExponential(double x, int f)
    {
        if (!double.IsFinite(x)) return JsSemantics.NumberToString(x);
        var sign = x < 0 ? "-" : "";
        if (x == 0) return (f == 0 ? "0" : "0." + new string('0', f)) + "e+0";
        var (m, e) = Significant(Math.Abs(x), f + 1);
        var ds = m.ToString(CultureInfo.InvariantCulture);
        var mant = f == 0 ? ds : ds[..1] + "." + ds[1..];
        return sign + mant + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
    }
}
