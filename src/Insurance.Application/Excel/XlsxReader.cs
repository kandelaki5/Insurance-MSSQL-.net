using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>
/// A cell as SheetJS reads it: its type (s text, n number, b boolean, e error, d date), its value (string, double, bool, the error's
/// code, or a JsDate), its formula and its formatted text.
/// </summary>
public sealed record ReadCell(char T, object? V, string? F = null, string? W = null);

/// <summary>A sheet as read: its extent and its cells by reference.</summary>
public sealed class ReadSheet
{
    public string? Ref { get; internal set; }
    public Dictionary<string, ReadCell> Cells { get; } = new(StringComparer.Ordinal);
    public ReadCell? this[string reference] => Cells.TryGetValue(reference, out var c) ? c : null;
}

/// <summary>A workbook as read: its sheets in order.</summary>
public sealed class ReadBook
{
    public List<string> SheetNames { get; } = [];
    public Dictionary<string, ReadSheet> Sheets { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The .xlsx reader the old program had from SheetJS (community, 0.18.5, XLSX.read): the same cells, types and values for what it read
/// — the policy import's uploads and, in the tests, the forms' files read back by "another reader". Dates come as dates only when asked
/// (cellDates), from the cell's number format as SheetJS decides it.
/// </summary>
public static class XlsxReader
{
    private const RegexOptions O = RegexOptions.CultureInvariant;
    private static readonly Regex Encoded = new("&(?:quot|apos|gt|lt|amp|#x?([0-9a-fA-F]+));", O | RegexOptions.IgnoreCase);
    private static readonly Regex CodeRe = new("_x([0-9a-fA-F]{4})_", O | RegexOptions.IgnoreCase);
    /// <summary>SheetJS unescapexml: XML entities and Excel's _xHHHH_ escapes.</summary>
    public static string UnescapeXml(string text)
    {
        var i = text.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (i == -1)
        {
            var s = Encoded.Replace(text, m => m.Value switch
            {
                "&quot;" => "\"", "&apos;" => "'", "&gt;" => ">", "&lt;" => "<", "&amp;" => "&",
                _ => m.Groups[1].Success ? FromCodePoint(m.Groups[1].Value, m.Value.Contains('x', StringComparison.OrdinalIgnoreCase) ? 16 : 10) : "\0",
            });
            return CodeRe.Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }
        var j = text.IndexOf("]]>", StringComparison.Ordinal);
        return UnescapeXml(text[..i]) + text[(i + 9)..j] + UnescapeXml(text[(j + 3)..]);
    }
    /// <summary>String.fromCharCode(parseInt(digits, radix)): one UTF-16 unit (the code taken modulo 2^16); digits not a number give "\0".</summary>
    private static string FromCodePoint(string digits, int radix)
    {
        long cp = 0;
        foreach (var ch in digits)
        {
            var d = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : 99;
            if (d >= radix) break;
            cp = (cp * radix + d) & 0xFFFFFFFF;
        }
        return ((char)(cp & 0xFFFF)).ToString();
    }

    private static readonly Regex AttRe = new("([^\"\\s?>/]+)\\s*=\\s*((?:\")([^\"]*)(?:\")|(?:')([^']*)(?:')|([^'\">\\s]+))", O);
    /// <summary>SheetJS parsexmltag: a tag's attributes by name (the namespace dropped; the lower-case name too).</summary>
    internal static Dictionary<string, string> ParseXmlTag(string tag)
    {
        var z = new Dictionary<string, string>(StringComparer.Ordinal);
        var eq = 0;
        for (; eq != tag.Length; ++eq) if (tag[eq] is ' ' or '\n' or '\r') break;
        z["0"] = tag[..eq];
        if (eq == tag.Length) return z;
        foreach (Match m in AttRe.Matches(tag))
        {
            var cc = m.Value;
            var c = cc.IndexOf('=');
            var q = JsSemantics.Trim(cc[..c]);
            while (c + 1 < cc.Length && cc[c + 1] == ' ') ++c;
            var quot = c + 1 < cc.Length && cc[c + 1] is '"' or '\'' ? 1 : 0;
            var v = JsSemantics.Slice(cc, c + 1 + quot, cc.Length - quot);
            var j = q.IndexOf(':');
            if (j < 0)
            {
                if (q.IndexOf('_') > 0) q = q[..q.IndexOf('_')];
                z[q] = v;
                z[q.ToLowerInvariant()] = v;
            }
            else
            {
                var k = (j == 5 && q[..5] == "xmlns" ? "xmlns" : "") + q[(j + 1)..];
                if (z.ContainsKey(k) && j >= 3 && q[(j - 3)..j] == "ext") continue;
                z[k] = v;
                z[k.ToLowerInvariant()] = v;
            }
        }
        return z;
    }

    private static readonly Regex TagRe = new("<[/?]?[a-zA-Z0-9:_-]+(?:\\s+[^\"\\s?>/]+\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^'\">\\s=]+))*\\s*[/?]?>", O | RegexOptions.Multiline);
    private static readonly Regex SiT = new("^\\s*<(?:\\w+:)?t[^>]*>", O);
    private static readonly Regex TClose = new("</(?:\\w+:)?t>", O);
    private static readonly Regex SiR = new("<(?:\\w+:)?r>", O);
    private static readonly Regex SiRPh = new("<(?:\\w+:)?rPh.*?>([\\s\\S]*?)</(?:\\w+:)?rPh>", O);
    private static readonly Regex SiTRun = new("<(?:\\w+:)?t[^>]*>([^<]*)</(?:\\w+:)?t>", O);
    /// <summary>SheetJS parse_si: a shared (or inline) string's text — a plain one, or the runs of a rich one without its phonetic runs.</summary>
    internal static string? ParseSi(string x)
    {
        if (x.Length == 0) return "";
        if (SiT.IsMatch(x)) return UnescapeXml(TClose.Split(x[(x.IndexOf('>') + 1)..])[0]);
        if (SiR.IsMatch(x)) return UnescapeXml(TagRe.Replace(string.Concat(SiTRun.Matches(SiRPh.Replace(x, "")).Select(m => m.Value)), ""));
        return null;
    }

    private static readonly Regex Sst0 = new("<(?:\\w+:)?sst([^>]*)>([\\s\\S]*)</(?:\\w+:)?sst>", O);
    private static readonly Regex Sst1 = new("<(?:\\w+:)?(?:si|sstItem)>", O);
    private static readonly Regex Sst2 = new("</(?:\\w+:)?(?:si|sstItem)>", O);
    private static List<string?> ParseSst(string? data)
    {
        var s = new List<string?>();
        if (string.IsNullOrEmpty(data)) return s;
        var sst = Sst0.Match(data);
        if (sst.Success) foreach (var piece in Sst2.Split(Sst1.Replace(sst.Groups[2].Value, ""))) s.Add(ParseSi(JsSemantics.Trim(piece)));
        return s;
    }

    /// <summary>Excel's built-in number formats as SheetJS keeps them (table_fmt), and those it takes for the implicit ones.</summary>
    internal static readonly IReadOnlyDictionary<int, string> TableFmt = new Dictionary<int, string>
    {
        [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??",
        [14] = "m/d/yy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yy h:mm",
        [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;[Red](#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)", [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mmss.0",
        [48] = "##0.0E+0", [49] = "@", [56] = "\"上午/下午 \"hh\"時\"mm\"分\"ss\"秒 \"",
    };
    private static readonly IReadOnlyDictionary<int, string> SsfImplicit = new Dictionary<int, string>
    {
        [5] = "\"$\"#,##0_);\\(\"$\"#,##0\\)", [6] = "\"$\"#,##0_);[Red]\\(\"$\"#,##0\\)", [7] = "\"$\"#,##0.00_);\\(\"$\"#,##0.00\\)", [8] = "\"$\"#,##0.00_);[Red]\\(\"$\"#,##0.00\\)",
        [23] = "General", [24] = "General", [25] = "General", [26] = "General", [27] = "m/d/yy", [28] = "m/d/yy", [29] = "m/d/yy", [30] = "m/d/yy", [31] = "m/d/yy",
        [32] = "h:mm:ss", [33] = "h:mm:ss", [34] = "h:mm:ss", [35] = "h:mm:ss", [36] = "m/d/yy",
        [41] = "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)", [42] = "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)",
        [43] = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)", [44] = "_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)",
        [50] = "m/d/yy", [51] = "m/d/yy", [52] = "m/d/yy", [53] = "m/d/yy", [54] = "m/d/yy", [55] = "m/d/yy", [56] = "m/d/yy", [57] = "m/d/yy", [58] = "m/d/yy",
        [59] = "0", [60] = "0.00", [61] = "#,##0", [62] = "#,##0.00", [63] = "\"$\"#,##0_);\\(\"$\"#,##0\\)", [64] = "\"$\"#,##0_);[Red]\\(\"$\"#,##0\\)",
        [65] = "\"$\"#,##0.00_);\\(\"$\"#,##0.00\\)", [66] = "\"$\"#,##0.00_);[Red]\\(\"$\"#,##0.00\\)", [67] = "0%", [68] = "0.00%", [69] = "# ?/?", [70] = "# ??/??", [71] = "m/d/yy", [72] = "m/d/yy",
    };

    private static readonly Regex AbsTime = new("\\[[HhMmSs\\u0E0A\\u0E19\\u0E17]*\\]", O);
    /// <summary>SheetJS fmt_is_date: whether a number format shows a date or a time.</summary>
    public static bool FmtIsDate(string fmt)
    {
        var i = 0;
        while (i < fmt.Length)
        {
            var c = fmt[i];
            switch (c)
            {
                case 'G': if (fmt.Length >= 7 + i && string.Equals(fmt.Substring(i, 7), "general", StringComparison.OrdinalIgnoreCase)) i += 6; i++; break;
                case '"': for (; ++i < fmt.Length && fmt[i] != '"';) { } ++i; break;
                case '\\': i += 2; break;
                case '_': i += 2; break;
                case '@': ++i; break;
                case 'B' or 'b' when i + 1 < fmt.Length && fmt[i + 1] is '1' or '2': return true;
                case 'B' or 'b' or 'M' or 'D' or 'Y' or 'H' or 'S' or 'E' or 'm' or 'd' or 'y' or 'h' or 's' or 'e' or 'g': return true;
                case 'A' or 'a' or '上':
                    if (Sub(fmt, i, 3).ToUpperInvariant() == "A/P") return true;
                    if (Sub(fmt, i, 5).ToUpperInvariant() == "AM/PM") return true;
                    if (Sub(fmt, i, 5).ToUpperInvariant() == "上午/下午") return true;
                    ++i; break;
                case '[':
                {
                    var o = c.ToString();
                    while (Ch(fmt, i++) != ']' && i < fmt.Length) o += Ch(fmt, i);
                    if (AbsTime.IsMatch(o)) return true;
                    break;
                }
                case '.' or '0' or '#':
                {
                    char d;
                    while (i < fmt.Length && ("0#?.,E+-%".IndexOf(d = Ch(fmt, ++i)) > -1 && d != '\0' || (d == '\\' && Ch(fmt, i + 1) == '-' && "0#".IndexOf(Ch(fmt, i + 2)) > -1 && Ch(fmt, i + 2) != '\0'))) { }
                    break;
                }
                case '?': while (Ch(fmt, ++i) == c) { } break;
                case '*': ++i; if (Ch(fmt, i) is ' ' or '*') ++i; break;
                case '(' or ')': ++i; break;
                case >= '1' and <= '9': while (i < fmt.Length && "0123456789".IndexOf(Ch(fmt, ++i)) > -1 && Ch(fmt, i) != '\0') { } break;
                default: ++i; break;
            }
        }
        return false;
    }
    private static char Ch(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';
    private static string Sub(string s, int i, int n) => i >= s.Length ? "" : s.Substring(i, Math.Min(n, s.Length - i));

    /// <summary>Options of a read: dates as dates (cellDates), formulas kept (cellFormula, on as in SheetJS).</summary>
    public sealed record Options(bool CellDates = false, bool CellFormula = true);

    private static readonly Regex RelRe = new("<Relationship\\b[^>]*>", O);
    private static readonly Regex SheetRe = new("<(?:\\w+:)?sheet\\b[^>]*>", O);

    /// <summary>XLSX.read of an .xlsx file.</summary>
    public static ReadBook Read(byte[] buf, Options? options = null)
    {
        var opts = options ?? new Options();
        var entries = Zip.ReadZip(buf);
        string? Text(string name)
        {
            var e = entries.Find(x => x.Name == name) ?? entries.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return e is null ? null : Encoding.UTF8.GetString(Zip.EntryData(e));
        }
        var book = Text("xl/workbook.xml") ?? throw new InvalidDataException("Could not find file");
        var rels = new Dictionary<string, (string Target, string Type)>(StringComparer.Ordinal);
        foreach (Match m in RelRe.Matches(Text("xl/_rels/workbook.xml.rels") ?? ""))
        {
            var a = ParseXmlTag(m.Value);
            if (a.TryGetValue("Id", out var id) && a.TryGetValue("Target", out var target)) rels[id] = (target, a.GetValueOrDefault("Type") ?? "");
        }
        string Resolve(string target) => target.StartsWith('/') ? target[1..] : "xl/" + Regex.Replace(target, "^/?xl/", "", O);
        var sstPath = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target;
        var strings = ParseSst(Text(sstPath is null ? "xl/sharedStrings.xml" : Resolve(sstPath)));
        var stylesPath = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target;
        var (formats, xfFormat) = ParseStyles(Text(stylesPath is null ? "xl/styles.xml" : Resolve(stylesPath)));
        var result = new ReadBook();
        var index = 0;
        foreach (Match m in SheetRe.Matches(book))
        {
            var a = ParseXmlTag(m.Value);
            var name = UnescapeXml(a.GetValueOrDefault("name") ?? "");
            var rid = a.GetValueOrDefault("id");
            var path = rid is not null && rels.TryGetValue(rid, out var rel) ? Resolve(rel.Target) : $"xl/worksheets/sheet{index + 1}.xml";
            result.SheetNames.Add(name);
            result.Sheets[name] = ParseSheet(Text(path) ?? "", strings, formats, xfFormat, opts);
            index++;
        }
        return result;
    }

    private static readonly Regex NumFmtsRe = new("<(?:\\w+:)?numFmts([^>]*)>[\\S\\s]*?</(?:\\w+:)?numFmts>", O);
    private static readonly Regex CellXfsRe = new("<(?:\\w+:)?cellXfs([^>]*)>[\\S\\s]*?</(?:\\w+:)?cellXfs>", O);
    private static (Dictionary<int, string> Formats, List<int?>? XfFormat) ParseStyles(string? data)
    {
        var formats = new Dictionary<int, string>(TableFmt);
        if (string.IsNullOrEmpty(data)) return (formats, null);
        data = Regex.Replace(data, "<!--([\\s\\S]*?)-->", "", O | RegexOptions.Multiline);
        var nf = NumFmtsRe.Match(data);
        if (nf.Success)
        {
            foreach (Match t in TagRe.Matches(nf.Value))
            {
                var y = ParseXmlTag(t.Value);
                if (StripNs(y["0"]) != "<numFmt") continue;
                var f = UnescapeXml(y.GetValueOrDefault("formatCode") ?? "");
                var j = JsParseInt(y.GetValueOrDefault("numFmtId") ?? "");
                if (j > 0) formats[j] = f;
            }
        }
        var xfs = CellXfsRe.Match(data);
        if (!xfs.Success) return (formats, null);
        var list = new List<int?>();
        foreach (Match t in TagRe.Matches(xfs.Value))
        {
            var y = ParseXmlTag(t.Value);
            var root = StripNs(y["0"]);
            if (root is not ("<xf" or "<xf/>")) continue;
            list.Add(y.TryGetValue("numFmtId", out var id) && int.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null);
        }
        return (formats, list);
    }
    private static string StripNs(string x) => Regex.Replace(x, "<(/?)\\w+:", "<$1", O);

    private static readonly Regex SheetDataRe = new("<(?:\\w+:)?sheetData[^>]*>([\\s\\S]*)</(?:\\w+:)?sheetData>", O);
    private static readonly Regex DimRe = new("\"(\\w*:\\w*)\"", O);
    private static readonly Regex RowEnd = new("</(?:\\w+:)?row>", O);
    private static readonly Regex CellStart = new("<(?:\\w+:)?c[ />]", O);
    private static readonly Regex RAttrRe = new("r=[\"']([^\"']*)[\"']", O);
    private static readonly Regex VRe = new("<(?:\\w+:)?v(?: xml:space=\"preserve\")?(?:[^>]*)>([\\s\\S]*?)</(?:\\w+:)?v>", O);
    private static readonly Regex FRe = new("<(?:\\w+:)?f(?: xml:space=\"preserve\")?(?:[^>]*)>([\\s\\S]*?)</(?:\\w+:)?f>", O);
    private static readonly Regex FEmptyRe = new("<f[^>]*/>", O);
    private static readonly Regex IsRe = new("<(?:\\w+:)?is>([\\S\\s]*?)</(?:\\w+:)?is>", O);
    private static readonly Regex RefAttrRe = new("ref=[\"']([^\"']*)[\"']", O);
    private static readonly Regex CrefRe = new("(^|[^._A-Z0-9])([$]?)([A-Z]{1,2}|[A-W][A-Z]{2}|X[A-E][A-Z]|XF[A-D])([$]?)(10[0-3][0-9]{4}|104[0-7][0-9]{3}|1048[0-4][0-9]{2}|10485[0-6][0-9]|104857[0-6]|[1-9][0-9]{0,5})(?![_.(A-Za-z0-9])", O);
    private static readonly Dictionary<string, int> RbErr = new() { ["#NULL!"] = 0x00, ["#DIV/0!"] = 0x07, ["#VALUE!"] = 0x0F, ["#REF!"] = 0x17, ["#NAME?"] = 0x1D, ["#NUM!"] = 0x24, ["#N/A"] = 0x2A, ["#GETTING_DATA"] = 0x2B, ["#WTF?"] = 0xFF };

    private static int DecodeCol(string s) { var d = 0; foreach (var ch in s.TrimStart('$')) d = 26 * d + ch - 64; return d - 1; }
    private static string ShiftFormula(string f, int dr, int dc) => CrefRe.Replace(f, m =>
        m.Groups[1].Value + (m.Groups[2].Value == "$" ? "$" + m.Groups[3].Value : XlsxWriter.EncodeCol(DecodeCol(m.Groups[3].Value) + dc))
        + (m.Groups[4].Value == "$" ? "$" + m.Groups[5].Value : (int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) + dr).ToString(CultureInfo.InvariantCulture)));

    private static ReadSheet ParseSheet(string data, List<string?> strs, Dictionary<int, string> formats, List<int?>? xfFormat, Options opts)
    {
        var s = new ReadSheet();
        int gsr = 2_000_000, gsc = 2_000_000, ger = 0, gec = 0;
        var mtch = SheetDataRe.Match(data);
        var data1 = mtch.Success ? data[..mtch.Index] : data;
        var ridx = Regex.Match(data1, "<(?:\\w*:)?dimension", O);
        if (ridx.Success && ridx.Index > 0)
        {
            var dim = DimRe.Match(JsSemantics.Slice(data1, ridx.Index, ridx.Index + 50));
            if (dim.Success)
            {
                var (sc, sr, ec, er) = SafeDecodeRange(dim.Groups[1].Value);
                if (sr <= er && sc <= ec && sr >= 0 && sc >= 0) s.Ref = XlsxWriter.EncodeRange(sc, sr, ec, er);
            }
        }
        if (mtch.Success)
        {
            var sharedf = new Dictionary<int, (string F, string Ref)>();
            int tagr = 0, tagc = 0;
            foreach (var chunk in RowEnd.Split(mtch.Groups[1].Value))
            {
                var x = JsSemantics.Trim(chunk);
                var xlen = x.Length;
                if (xlen == 0) continue;
                int rstarti = 0, ri;
                for (ri = 0; ri < xlen; ++ri)
                {
                    if (x[ri] == '>') { if (ri == 0 || x[ri - 1] != '/') { ++ri; break; } }
                    else if (x[ri] == '<') rstarti = ri;
                }
                if (rstarti >= ri) break;
                var rowTag = ParseXmlTag(x[rstarti..ri]);
                tagr = rowTag.TryGetValue("r", out var rv) ? JsParseInt(rv) : tagr + 1;
                tagc = -1;
                if (gsr > tagr - 1) gsr = tagr - 1;
                if (ger < tagr - 1) ger = tagr - 1;
                var cells = CellStart.Split(x[ri..]).ToList();
                var rslice = 0;
                for (; rslice != cells.Count; ++rslice) if (Ch(JsSemantics.Trim(cells[rslice]), 0) != '<') break;
                cells = cells.Skip(rslice).ToList();
                for (var ci = 0; ci != cells.Count; ++ci)
                {
                    var cx = JsSemantics.Trim(cells[ci]);
                    if (cx.Length == 0) continue;
                    var cref = RAttrRe.Match(cx);
                    if (cref.Success)
                    {
                        var idx = 0;
                        foreach (var ch in cref.Groups[1].Value) { var cc = ch - 64; if (cc < 1 || cc > 26) break; idx = 26 * idx + cc; }
                        tagc = idx - 1;
                    }
                    else ++tagc;
                    cx = "<c " + (Ch(cx, 0) == '<' ? ">" : "") + cx;
                    var gt = cx.IndexOf('>');
                    var i = gt < 0 ? cx.Length : gt + 1;
                    var tag = ParseXmlTag(cx[..Math.Min(i, cx.Length)]);
                    var reference = tag.TryGetValue("r", out var tr) && tr.Length > 0 ? tr : XlsxWriter.EncodeCell(tagc, tagr - 1);
                    var d = JsSemantics.Slice(cx, i);
                    string? pv = null, pf = null;
                    var vm = VRe.Match(d);
                    if (vm.Success && vm.Groups[1].Value.Length > 0) pv = UnescapeXml(vm.Groups[1].Value);
                    if (opts.CellFormula)
                    {
                        var fm = FRe.Match(d);
                        if (fm.Success && fm.Groups[1].Value.Length > 0)
                        {
                            pf = UnescapeXml(fm.Groups[1].Value).Replace("\r\n", "\n").Replace("_xlfn.", "");
                            if (fm.Value.Contains("t=\"shared\"", StringComparison.Ordinal))
                            {
                                var ftag = ParseXmlTag(fm.Value);
                                if (ftag.TryGetValue("si", out var si)) sharedf[JsParseInt(si)] = (pf, reference);
                            }
                        }
                        else if (FEmptyRe.Match(d) is { Success: true } fe)
                        {
                            var ftag = ParseXmlTag(fe.Value);
                            if (ftag.TryGetValue("si", out var si) && sharedf.TryGetValue(JsParseInt(si), out var master))
                            {
                                var (mc, mr) = XlsxWriter.DecodeCell(master.Ref);
                                var (cc2, cr2) = XlsxWriter.DecodeCell(reference);
                                pf = ShiftFormula(master.F, cr2 - mr, cc2 - mc);
                            }
                        }
                    }
                    var t = tag.GetValueOrDefault("t");
                    object? v = pv;
                    string type;
                    if (t is null && pv is null)
                    {
                        if (pf is null) continue;
                        type = "n"; v = 0d;
                    }
                    else type = string.IsNullOrEmpty(t) ? "n" : t;
                    if (gsc > tagc) gsc = tagc;
                    if (gec < tagc) gec = tagc;
                    string? w = null;
                    switch (type)
                    {
                        case "n":
                            if (v is string sv) v = JsParseFloat(sv);
                            else if (v is null) continue;
                            break;
                        case "s":
                            if (pv is null) continue;
                            var k = JsParseInt(pv);
                            if (k < 0 || k >= strs.Count) throw new InvalidDataException("Cannot read properties of undefined (reading 't')");
                            v = strs[k];
                            break;
                        case "str": type = "s"; v = pv ?? ""; break;
                        case "inlineStr":
                        {
                            type = "s";
                            var im = IsRe.Match(d);
                            v = im.Success ? ParseSi(im.Groups[1].Value) : "";
                            break;
                        }
                        case "b": v = pv is "1" or "true" or "TRUE"; break;
                        case "d":
                        {
                            var date = JsDate.Parse(pv ?? "undefined");
                            if (opts.CellDates) v = date; else { type = "n"; v = DateNum(date); }
                            break;
                        }
                        case "e": w = pv; v = pv is not null && RbErr.TryGetValue(pv, out var code) ? code : null; break;
                    }
                    var fmtid = 0;
                    if (xfFormat is not null && tag.TryGetValue("s", out var styleText))
                    {
                        var sIndex = JsParseInt(styleText);
                        if (sIndex >= 0 && sIndex < xfFormat.Count && xfFormat[sIndex] is { } nf) fmtid = nf;
                    }
                    if (!formats.ContainsKey(fmtid)) formats[fmtid] = SsfImplicit.GetValueOrDefault(fmtid) ?? "General";
                    w ??= FormatText(type, v, fmtid);
                    if (opts.CellDates && xfFormat is not null && type == "n" && v is double num && FmtIsDate(formats[fmtid])) { type = "d"; v = NumDate(num); }
                    s.Cells[reference] = new ReadCell(type.Length == 1 ? type[0] : '?', v, pf, w);
                }
            }
        }
        if (s.Ref is null && gec >= gsc && ger >= gsr) s.Ref = XlsxWriter.EncodeRange(gsc, gsr, gec, ger);
        return s;
    }

    /// <summary>The cell's formatted text (SheetJS .w) for the General format; other formats are not rendered (null).</summary>
    private static string? FormatText(string type, object? v, int fmtid) => type switch
    {
        "s" when v is string s => s,
        "n" when v is double d && fmtid == 0 => SheetToJson.GeneralNumber(d),
        "b" when v is bool b && fmtid == 0 => b ? "TRUE" : "FALSE",
        _ => null,
    };

    /// <summary>SheetJS numdate in a UTC process: an Excel serial day as a date (30.12.1899 + v days; no 1900 leap-day correction).</summary>
    public static JsDate NumDate(double v) => new(JsDate.TimeClip(v * 24 * 60 * 60 * 1000 + -2209161600000));
    private static double DateNum(JsDate d) => (d.Ms - -2209161600000) / 86400000;

    private static (int sc, int sr, int ec, int er) SafeDecodeRange(string range)
    {
        int i = 0, idx, cc = 0;
        var len = range.Length;
        for (idx = 0; i < len; ++i) { if ((cc = range[i] - 64) < 1 || cc > 26) break; idx = 26 * idx + cc; }
        var sc = --idx;
        for (idx = 0; i < len; ++i) { if ((cc = range[i] - 48) < 0 || cc > 9) break; idx = 10 * idx + cc; }
        var sr = --idx;
        if (i == len || cc != 10) return (sc, sr, sc, sr);
        ++i;
        for (idx = 0; i != len; ++i) { if ((cc = range[i] - 64) < 1 || cc > 26) break; idx = 26 * idx + cc; }
        var ec = --idx;
        for (idx = 0; i != len; ++i) { if ((cc = range[i] - 48) < 0 || cc > 9) break; idx = 10 * idx + cc; }
        return (sc, sr, ec, --idx);
    }

    /// <summary>parseInt(text, 10) of JavaScript (NaN as -1: never a valid index or row here).</summary>
    internal static int JsParseInt(string text)
    {
        var m = Regex.Match(JsSemantics.TrimStart(text), "^[+-]?[0-9]+", O);
        return m.Success && int.TryParse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }
    private static readonly Regex FloatPrefix = new("^[+-]?(?:Infinity|(?:[0-9]+\\.?[0-9]*|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)", O);
    /// <summary>parseFloat(text) of JavaScript: the longest number at the start, white space before it skipped.</summary>
    public static double JsParseFloat(string text)
    {
        var m = FloatPrefix.Match(JsSemantics.TrimStart(text));
        if (!m.Success) return double.NaN;
        return m.Value.EndsWith("Infinity", StringComparison.Ordinal) ? (m.Value[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity) : double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
