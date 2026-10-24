using System.Text.RegularExpressions;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>
/// A text upload read as SheetJS (community, 0.18.5) read it (XLSX.read of a string, cellDates): delimiter-separated values — the
/// separator from a "sep=" line, else the most frequent of , tab ; | in the first 1024 characters (outside quotes; a tie goes to , then
/// tab then ;) — and every field typed as SheetJS types it: TRUE / FALSE booleans, anything Number() or SheetJS's looser reading takes
/// ("35,000", "$12", "50%", "(7)") a number, anything V8 reads as a date a date (new Date: "1/15/2026", "2026-01-15", even "Toyota 2"),
/// a formula ("=…") no value, the rest text.
/// </summary>
public static class DsvReader
{
    private const RegexOptions O = RegexOptions.CultureInvariant;

    /// <summary>The single sheet of a text upload; SheetJS's other text formats (HTML/XML tables, DIF, DBF, RTF, BIFF) are refused.</summary>
    public static ReadSheet Read(string text)
    {
        var first = text.Length > 0 ? text[0] : '\0';
        var lead = 0;
        while (lead < text.Length && text[lead] is '\n' or '\r' or ' ') lead++;
        if (first == '<' || (first is '\n' or '\r' or ' ' && lead < text.Length && text[lead] == '<'))
            throw new InvalidDataException("An HTML or XML spreadsheet is not read as a text upload");
        if (text.StartsWith("TABL", StringComparison.Ordinal)) throw new InvalidDataException("A DIF file is not read as a text upload");
        if (text.StartsWith("{\\rt", StringComparison.Ordinal)) throw new InvalidDataException("An RTF file is not read as a text upload");
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Length > 8 ? text[..8] : text);
        byte B(int i) => i < bytes.Length ? bytes[i] : (byte)0;
        if (B(0) == 0x09 && B(1) <= 0x08 && bytes.Length > 1) throw new InvalidDataException("A BIFF stream is not read as a text upload");
        if (B(0) is 0x03 or 0x83 or 0x8B or 0x8C || (B(0) is 0x02 or 0x30 or 0x31 or 0xF5 && B(2) <= 12 && B(3) <= 31 && bytes.Length > 3))
            throw new InvalidDataException("A DBF file is not read as a text upload");
        if (bytes.Length > 3 && B(0) == 0 && B(1) == 0) throw new InvalidDataException("A Lotus file is not read as a text upload");
        return DsvToSheet(text);
    }

    private static readonly Dictionary<char, int> SepWeights = new() { [','] = 3, ['\t'] = 2, [';'] = 1, ['|'] = 0 };
    private static char GuessSep(string str)
    {
        var cnt = new Dictionary<char, int>();
        var order = new List<char>();
        var instr = false;
        foreach (var cc in str)
        {
            if (cc == '"') instr = !instr;
            else if (!instr && SepWeights.ContainsKey(cc)) { if (!cnt.ContainsKey(cc)) { cnt[cc] = 0; order.Add(cc); } cnt[cc]++; }
        }
        // JavaScript's integer-like keys iterate in ascending order: tab, ",", ";", "|".
        var keys = cnt.Count > 0 ? cnt.Keys.OrderBy(k => (int)k).ToList() : SepWeights.Keys.OrderBy(k => (int)k).ToList();
        var pairs = keys.Select(k => (Count: cnt.Count > 0 ? cnt[k] : SepWeights[k], Key: k)).ToList();
        var sorted = pairs.OrderBy(p => p.Count).ThenBy(p => SepWeights[p.Key]).ToList();
        return sorted[^1].Key;
    }

    private static ReadSheet DsvToSheet(string str)
    {
        var ws = new ReadSheet();
        int ec = 0, er = 0;
        char sep;
        if (JsSemantics.Slice(str, 0, 4) == "sep=")
        {
            if (Ch(str, 5) == '\r' && Ch(str, 6) == '\n') { sep = Ch(str, 4); str = JsSemantics.Slice(str, 7); }
            else if (Ch(str, 5) is '\r' or '\n') { sep = Ch(str, 4); str = JsSemantics.Slice(str, 6); }
            else sep = GuessSep(JsSemantics.Slice(str, 0, 1024));
        }
        else sep = GuessSep(JsSemantics.Slice(str, 0, 1024));
        int r = 0, c = 0, start = 0, end = 0;
        var instr = false;
        int cc = 0, startcc = str.Length > 0 ? str[0] : -1;
        str = str.Replace("\r\n", "\n");
        void FinishCell()
        {
            var s = JsSemantics.Slice(str, start, end);
            var cell = Typed(s);
            if (cell is not null) ws.Cells[XlsxWriter.EncodeCell(c, r)] = cell;
            start = end + 1;
            startcc = start < str.Length ? str[start] : -1;
            if (ec < c) ec = c;
            if (er < r) er = r;
            if (cc == sep) ++c; else { c = 0; ++r; }
        }
        for (; end < str.Length; ++end)
        {
            cc = str[end];
            if (cc == '"') { if (startcc == '"') instr = !instr; }
            else if (cc == sep || cc == '\n' || cc == '\r') { if (!instr) FinishCell(); }
        }
        if (end - start > 0) FinishCell();
        ws.Ref = XlsxWriter.EncodeRange(0, 0, ec, er);
        return ws;
    }
    private static char Ch(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>A field typed as SheetJS's dsv reader types it (null: an empty field, no cell).</summary>
    internal static ReadCell? Typed(string s)
    {
        if (Ch(s, 0) == '"' && s.Length > 0 && s[^1] == '"') s = JsSemantics.Slice(s, 1, -1).Replace("\"\"", "\"");
        if (s.Length == 0) return null;
        if (JsSemantics.Trim(s).Length == 0) return new ReadCell('s', s);
        if (s[0] == '=')
        {
            if (Ch(s, 1) == '"' && s[^1] == '"') return new ReadCell('s', JsSemantics.Slice(s, 2, -1).Replace("\"\"", "\""));
            if (s.Length > 1) return new ReadCell('n', null, s[1..]);
            return new ReadCell('s', s);
        }
        if (s == "TRUE") return new ReadCell('b', true);
        if (s == "FALSE") return new ReadCell('b', false);
        var v = FuzzyNum(s);
        if (!double.IsNaN(v)) return new ReadCell('n', v, W: s);
        var date = FuzzyDate(s);
        if (date.IsValid)
        {
            var parsed = JsDate.Parse(s);
            return new ReadCell('d', parsed, W: parsed.IsValid ? SheetToJson.ShortDate(parsed) : null);
        }
        return new ReadCell('s', s);
    }

    private static readonly Regex Digit = new("[0-9]", O);
    private static readonly Regex Thousands = new("([0-9]),([0-9])", O);
    private static readonly Regex Parens = new("[(]([^\\n\\r\\u2028\\u2029]*)[)]", O);
    /// <summary>SheetJS fuzzynum: Number(text), else without thousands commas, "$" and "%" (each % a hundredth), else "(x)" as −x.</summary>
    public static double FuzzyNum(string s)
    {
        var v = JsSemantics.ParseNumber(s);
        if (!double.IsNaN(v)) return double.IsFinite(v) ? v : double.NaN;
        if (!Digit.IsMatch(s)) return v;
        double wt = 1;
        var ss = Thousands.Replace(s, "$1$2").Replace("$", "");
        ss = Regex.Replace(ss, "[%]", _ => { wt *= 100; return ""; }, O);
        if (!double.IsNaN(v = JsSemantics.ParseNumber(ss))) return v / wt;
        ss = Parens.Replace(ss, m => { wt = -wt; return m.Groups[1].Value; }, 1);
        if (!double.IsNaN(v = JsSemantics.ParseNumber(ss))) return v / wt;
        return v;
    }

    private static readonly Regex MonthAbbr = new("jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec", O);
    private static readonly Regex NotLetter = new("[^a-z]", O);
    private static readonly Regex AmPm = new("([^a-z]|^)[ap]m?([^a-z]|\\z)", O);
    private static readonly Regex AsciiLetter = new("[a-z]", O);
    private static readonly Regex NotDateChar = new("[^-0-9:,/\\\\]", O);
    private static readonly string[] LowerMonths = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
    /// <summary>SheetJS fuzzydate: new Date(text) when it is a plausible date — no letters but month names, a year 1900–9999, and only
    /// digits and - : , / \ when it would be 1 January or a day of 2001 (V8's default year).</summary>
    public static JsDate FuzzyDate(string s)
    {
        var o = JsDate.Parse(s);
        var invalid = new JsDate(double.NaN);
        if (!o.IsValid) return invalid;
        long y = o.Year - 1900;
        int m = o.Month, d = o.Date;
        var lower = s.ToLowerInvariant();
        if (MonthAbbr.IsMatch(lower))
        {
            lower = JsSemantics.ReplaceFirst(NotLetter.Replace(lower, ""), AmPm, "");
            if (lower.Length > 3 && Array.IndexOf(LowerMonths, lower) == -1) return invalid;
        }
        else if (AsciiLetter.IsMatch(lower)) return invalid;
        if (y < 0 || y > 8099) return invalid;
        if ((m > 0 || d > 1) && y != 101) return o;
        if (NotDateChar.IsMatch(s)) return invalid;
        return o;
    }
}
