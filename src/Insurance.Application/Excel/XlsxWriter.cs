using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>JavaScript's undefined in a row of cells: no cell at all (null is a cell left empty that still counts for the sheet's extent).</summary>
public sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();
    private JsUndefined() { }
}

/// <summary>A cell as SheetJS keeps it: its type (s text, n number, b boolean, z none), its value and its number format.</summary>
public sealed record SheetCell(char T, object? V, string? Z = null);

/// <summary>Page margins in inches, in the order they are written.</summary>
public sealed record PageMargins(double Left, double Right, double Top, double Bottom, double Header, double Footer);

/// <summary>A defined name of the workbook (as SheetJS's wb.Workbook.Names: a print title, a print area).</summary>
public sealed record DefinedName(string Name, int? Sheet, string Ref);

/// <summary>A sheet built from rows (SheetJS's worksheet object): its cells by reference, its extent, column widths and margins.</summary>
public sealed class WorkSheet
{
    internal Dictionary<string, SheetCell> Cells { get; } = new(StringComparer.Ordinal);
    /// <summary>The sheet's extent ("A1:C3"), or null when nothing was ever put in it.</summary>
    public string? Ref { get; internal set; }
    /// <summary>Column widths in characters of the default font (SheetJS's !cols wch), by column from 0; null leaves a column as it is.</summary>
    public IReadOnlyList<double?>? Cols { get; set; }
    public PageMargins? Margins { get; set; }
    public SheetCell? this[string reference] => Cells.TryGetValue(reference, out var c) ? c : null;
    public IReadOnlyDictionary<string, SheetCell> AllCells => Cells;
}

/// <summary>
/// The generic Excel writer the old program had from SheetJS (community, 0.18.5): a sheet from rows of values (aoa_to_sheet) or from
/// records (json_to_sheet), workbooks of such sheets written as .xlsx exactly as SheetJS wrote them — the same parts, the same XML, the
/// same cells, types and styles (only the ZIP's compression bytes are this program's own). Used by the reports' exports, the AML and
/// bordereau workbooks and the policy import template; the regulator forms are filled in their own templates (Insurance.Domain.Regulatory).
/// </summary>
public static class XlsxWriter
{
    /// <summary>Excel's built-in date format 14, as SheetJS names it.</summary>
    public const string DateFormat = "m/d/yy";
    private const double DayMs = 24 * 60 * 60 * 1000;
    /// <summary>new Date(1899, 11, 30) in a UTC process (the old server's zone): the day before Excel's day 1.</summary>
    private const double BaseDateMs = -2209161600000;

    /// <summary>An Excel serial date of a moment (SheetJS datenum, the process in UTC): days since 30.12.1899, the time as a fraction.</summary>
    public static double DateNum(DateTime value) => (UnixMs(value) - BaseDateMs) / DayMs;
    internal static double UnixMs(DateTime value) => (DateTime.SpecifyKind(value, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>A value of a row as SheetJS types it: text, number, boolean, a date (a number with format 14), or null/undefined for none.</summary>
    internal static SheetCell? Typed(object? v) => v switch
    {
        null or JsUndefined => null,
        string s => new SheetCell('s', s),
        bool b => new SheetCell('b', b),
        double d => new SheetCell('n', d),
        float f => new SheetCell('n', (double)f),
        int i => new SheetCell('n', (double)i),
        long l => new SheetCell('n', (double)l),
        decimal m => new SheetCell('n', double.Parse(m.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        DateTime t => new SheetCell('n', DateNum(t), DateFormat),
        DateOnly d => new SheetCell('n', DateNum(d.ToDateTime(TimeOnly.MinValue)), DateFormat),
        _ => throw new ArgumentException($"A cell value of type {v.GetType().Name} is not supported"),
    };

    public static string EncodeCol(int col)
    {
        if (col < 0) throw new ArgumentException($"invalid column {col}");
        var s = "";
        for (++col; col > 0; col = (col - 1) / 26) s = (char)((col - 1) % 26 + 65) + s;
        return s;
    }
    public static string EncodeCell(int c, int r) => EncodeCol(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
    internal static string EncodeRange(int sc, int sr, int ec, int er) { string a = EncodeCell(sc, sr), b = EncodeCell(ec, er); return a == b ? a : a + ":" + b; }
    internal static (int sc, int sr, int ec, int er) DecodeRange(string range)
    {
        var parts = range.Split(':');
        var (sc, sr) = DecodeCell(parts[0]);
        var (ec, er) = parts.Length > 1 ? DecodeCell(parts[1]) : (sc, sr);
        return (sc, sr, ec, er);
    }
    internal static (int c, int r) DecodeCell(string cell)
    {
        int c = 0, r = 0;
        foreach (var ch in cell)
        {
            if (ch >= '0' && ch <= '9') r = 10 * r + (ch - '0');
            else if (ch >= 'A' && ch <= 'Z') c = 26 * c + (ch - 64);
        }
        return (c - 1, r - 1);
    }

    /// <summary>A sheet of rows of values, cell for cell (SheetJS aoa_to_sheet): an undefined value is no cell; null is none, though it
    /// counts for the sheet's extent.</summary>
    public static WorkSheet AoaToSheet(IEnumerable<IReadOnlyList<object?>?> rows)
    {
        var ws = new WorkSheet();
        int sc = 10_000_000, sr = 10_000_000, ec = 0, er = 0;
        var r = 0;
        foreach (var row in rows)
        {
            if (row is not null)
            {
                for (var c = 0; c < row.Count; c++)
                {
                    var v = row[c];
                    if (v is JsUndefined) continue;
                    if (sr > r) sr = r;
                    if (sc > c) sc = c;
                    if (er < r) er = r;
                    if (ec < c) ec = c;
                    var cell = Typed(v);
                    if (cell is null) continue;
                    ws.Cells[EncodeCell(c, r)] = cell;
                }
            }
            r++;
        }
        if (sc < 10_000_000) ws.Ref = EncodeRange(sc, sr, ec, er);
        return ws;
    }

    /// <summary>
    /// A sheet of records (SheetJS json_to_sheet): a header row of the keys — the given header first, then each new key as it comes —
    /// and a row per record. A record's keys are in JavaScript's order: index-like keys ("2021") first, by their number, then the rest as given.
    /// </summary>
    public static WorkSheet JsonToSheet(IReadOnlyList<IEnumerable<KeyValuePair<string, object?>>> records, IReadOnlyList<string>? header = null)
    {
        var ws = new WorkSheet();
        var hdr = new List<string>(header ?? []);
        int ec = 0, er = records.Count - 1 + 1;
        for (var r = 0; r < records.Count; r++)
        {
            foreach (var (k, v) in JsObjectOrder(records[r]))
            {
                var c = hdr.IndexOf(k);
                if (c == -1) { c = hdr.Count; hdr.Add(k); }
                ws.Cells[EncodeCell(c, r + 1)] = Typed(v) ?? new SheetCell('z', v is JsUndefined ? null : v);
            }
        }
        ec = Math.Max(ec, hdr.Count - 1);
        for (var c = 0; c < hdr.Count; c++) ws.Cells[EncodeCol(c) + "1"] = new SheetCell('s', hdr[c]);
        ws.Ref = EncodeRange(0, 0, ec, er);
        return ws;
    }

    /// <summary>The keys of a JavaScript object literal in the order Object.keys gives them (one value per key, the last one set).</summary>
    internal static List<KeyValuePair<string, object?>> JsObjectOrder(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var order = new List<string>();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) { if (!values.ContainsKey(k)) order.Add(k); values[k] = v; }
        static bool IsIndex(string k, out uint n) => uint.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n != uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == k;
        var indexes = order.Where(k => IsIndex(k, out _)).OrderBy(k => { IsIndex(k, out var n); return n; });
        return indexes.Concat(order.Where(k => !IsIndex(k, out _))).Select(k => new KeyValuePair<string, object?>(k, values[k])).ToList();
    }

    // ---- Writing, as SheetJS's write_zip_xlsx.

    internal static readonly Regex Wt = new("(^" + JsSemantics.Ws + "|" + JsSemantics.Ws + "\\z|\\n)", RegexOptions.CultureInvariant);
    private static readonly Regex XmlSpecial = new("[&<>'\"]", RegexOptions.CultureInvariant);
    private static readonly Regex ControlChars = new("[\\u0000-\\u0008\\u000b-\\u001f]", RegexOptions.CultureInvariant);
    /// <summary>SheetJS escapexml: the five XML characters as entities, other control characters as _xHHHH_.</summary>
    public static string EscapeXml(string text) =>
        ControlChars.Replace(XmlSpecial.Replace(text, m => m.Value switch { "&" => "&amp;", "<" => "&lt;", ">" => "&gt;", "'" => "&apos;", _ => "&quot;" }),
            m => "_x" + ((int)m.Value[0]).ToString("x4", CultureInfo.InvariantCulture) + "_");
    /// <summary>SheetJS writetag: an element with text, marked to keep its spaces when they would be lost.</summary>
    internal static string WriteTag(string f, string g) => $"<{f}{(Wt.IsMatch(g) ? " xml:space=\"preserve\"" : "")}>{g}</{f}>";
    /// <summary>SheetJS writextag: an element with attributes, and text or none.</summary>
    internal static string WriteXTag(string f, string? g, IEnumerable<(string Key, string Value)>? h = null)
    {
        var attrs = h is null ? "" : string.Concat(h.Select(a => $" {a.Key}=\"{a.Value}\""));
        return g is null ? $"<{f}{attrs}/>" : $"<{f}{attrs}{(Wt.IsMatch(g) ? " xml:space=\"preserve\"" : "")}>{g}</{f}>";
    }
    private static string N(double x) => JsSemantics.NumberToString(x);

    private const int Mdw = 6;
    /// <summary>A width in characters as the column width written (SheetJS char2width at its default 6-pixel digit).</summary>
    public static double Char2Width(double chr) => Math.Floor((chr * Mdw + 5) / Mdw * 256 + 0.5) / 256;

    internal const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static string CellXml(SheetCell cell, string reference, List<string> xfFormats)
    {
        if (cell.V is null || cell.T == 'z') return "";
        var vv = cell.T switch { 'b' => (bool)cell.V ? "1" : "0", 'n' => N((double)cell.V), _ => (string)cell.V };
        var v = WriteTag("v", EscapeXml(vv));
        var attrs = new List<(string, string)> { ("r", reference) };
        var format = cell.Z ?? "General";
        var s = xfFormats.IndexOf(format);
        if (s < 0) { s = xfFormats.Count; xfFormats.Add(format); }
        if (s != 0) attrs.Add(("s", s.ToString(CultureInfo.InvariantCulture)));
        if (cell.T == 'b') attrs.Add(("t", "b"));
        else if (cell.T == 's')
        {
            if (((string)cell.V).Length > 32767) throw new ArgumentException("Text length must not exceed 32767 characters");
            attrs.Add(("t", "str"));
        }
        return WriteXTag("c", v, attrs);
    }

    internal static string SheetXml(WorkSheet ws, List<string> xfFormats)
    {
        var reference = ws.Ref ?? "A1";
        var o = new StringBuilder(SheetJsParts.XmlHeader);
        o.Append($"<worksheet xmlns=\"{NsMain}\" xmlns:r=\"{NsR}\">");
        o.Append(WriteXTag("dimension", null, [("ref", reference)]));
        o.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
        if (ws.Cols is { Count: > 0 } cols)
        {
            o.Append("<cols>");
            for (var i = 0; i < cols.Count; i++)
            {
                if (cols[i] is not { } wch) continue;
                o.Append(WriteXTag("col", null, [("min", N(i + 1)), ("max", N(i + 1)), ("width", N(Char2Width(wch))), ("customWidth", "1")]));
            }
            o.Append("</cols>");
        }
        var data = new StringBuilder();
        if (ws.Ref is not null)
        {
            var (sc, sr, ec, er) = DecodeRange(ws.Ref);
            for (var r = sr; r <= er; r++)
            {
                var cells = new StringBuilder();
                var any = false;
                for (var c = sc; c <= ec; c++)
                {
                    var cref = EncodeCell(c, r);
                    if (!ws.Cells.TryGetValue(cref, out var cell)) continue;
                    var x = CellXml(cell, cref, xfFormats);
                    cells.Append(x);
                    any = true;
                }
                if (any) data.Append(WriteXTag("row", cells.ToString(), [("r", (r + 1).ToString(CultureInfo.InvariantCulture))]));
            }
        }
        o.Append(data.Length > 0 ? $"<sheetData>{data}</sheetData>" : "<sheetData/>");
        if (ws.Margins is { } m)
            o.Append(WriteXTag("pageMargins", null, [("left", N(m.Left)), ("right", N(m.Right)), ("top", N(m.Top)), ("bottom", N(m.Bottom)), ("header", N(m.Header)), ("footer", N(m.Footer))]));
        o.Append(WriteTag("ignoredErrors", WriteXTag("ignoredError", null, [("numberStoredAsText", "1"), ("sqref", reference)])));
        o.Append("</worksheet>");
        return o.ToString();
    }
}

/// <summary>A workbook of sheets built from rows (SheetJS book_new / book_append_sheet / write).</summary>
public sealed class XlsxBook
{
    private readonly List<string> names = [];
    private readonly Dictionary<string, WorkSheet> sheets = new(StringComparer.Ordinal);
    public IReadOnlyList<string> SheetNames => names;
    public WorkSheet Sheet(string name) => sheets[name];
    /// <summary>The workbook's defined names (print titles), written in this order.</summary>
    public List<DefinedName> Names { get; } = [];

    private static readonly char[] BadChars = [']', '[', '*', '?', '/', '\\'];
    /// <summary>Adds a sheet under a name (SheetJS's checks: at most 31 characters, none of ] [ * ? / \, not twice); no name: Sheet1, Sheet2….</summary>
    public string AppendSheet(WorkSheet ws, string? name = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            for (var i = 1; i <= 0xFFFF; i++) if (!names.Contains(name = $"Sheet{i}")) break;
        }
        if (string.IsNullOrEmpty(name) || names.Count >= 0xFFFF) throw new InvalidOperationException("Too many worksheets");
        if (name.Length > 31) throw new ArgumentException("Sheet names cannot exceed 31 chars");
        if (name.IndexOfAny(BadChars) >= 0) throw new ArgumentException("Sheet name cannot contain : \\ / ? * [ ]");
        if (names.Contains(name)) throw new ArgumentException($"Worksheet with name |{name}| already exists!");
        names.Add(name);
        sheets[name] = ws;
        return name;
    }

    private const string RelWs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";

    /// <summary>The workbook's parts, in the order SheetJS's ZIP holds them, each stored uncompressed (as SheetJS writes them).</summary>
    public List<ZipEntry> WriteEntries()
    {
        if (names.Count == 0) throw new InvalidOperationException("Workbook is empty");
        var xfFormats = new List<string> { "General" };
        var sheetXml = names.Select(n => XlsxWriter.SheetXml(sheets[n], xfFormats)).ToList();
        var n = names.Count;
        var h = SheetJsParts.XmlHeader;
        var rels = new StringBuilder(h).Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= n; i++) rels.Append($"<Relationship Id=\"rId{i}\" Type=\"{RelWs}\" Target=\"worksheets/sheet{i}.xml\"/>");
        rels.Append($"<Relationship Id=\"rId{n + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>");
        rels.Append($"<Relationship Id=\"rId{n + 2}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        rels.Append($"<Relationship Id=\"rId{n + 3}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sheetMetadata\" Target=\"metadata.xml\"/></Relationships>");
        var xfs = string.Concat(xfFormats.Select(f => $"<xf numFmtId=\"{(f == "General" ? 0 : 14)}\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"));
        var styles = h + $"<styleSheet xmlns=\"{XlsxWriter.NsMain}\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">"
            + "<numFmts count=\"1\"><numFmt numFmtId=\"56\" formatCode=\"&quot;上午/下午 &quot;hh&quot;時&quot;mm&quot;分&quot;ss&quot;秒 &quot;\"/></numFmts>"
            + "<fonts count=\"1\"><font><sz val=\"12\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + $"<cellXfs count=\"{xfFormats.Count}\">{xfs}</cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles><dxfs count=\"0\"/>"
            + "<tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium9\" defaultPivotStyle=\"PivotStyleMedium4\"/></styleSheet>";
        var book = new StringBuilder(h).Append($"<workbook xmlns=\"{XlsxWriter.NsMain}\" xmlns:r=\"{XlsxWriter.NsR}\"><workbookPr codeName=\"ThisWorkbook\"/><sheets>");
        for (var i = 0; i < n; i++)
        {
            var name = names[i].Length > 31 ? names[i][..31] : names[i];
            book.Append(XlsxWriter.WriteXTag("sheet", null, [("name", XlsxWriter.EscapeXml(name)), ("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture)), ("r:id", $"rId{i + 1}")]));
        }
        book.Append("</sheets>");
        if (Names.Count > 0)
        {
            book.Append("<definedNames>");
            foreach (var dn in Names)
            {
                if (string.IsNullOrEmpty(dn.Ref)) continue;
                var attrs = new List<(string, string)> { ("name", dn.Name) };
                if (dn.Sheet is { } s) attrs.Add(("localSheetId", s.ToString(CultureInfo.InvariantCulture)));
                book.Append(XlsxWriter.WriteXTag("definedName", XlsxWriter.EscapeXml(dn.Ref), attrs));
            }
            book.Append("</definedNames>");
        }
        book.Append("</workbook>");
        var rootRels = h + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
            + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
        var titles = string.Concat(names.Select(s => $"<vt:lpstr>{XlsxWriter.EscapeXml(s)}</vt:lpstr>"));
        var app = h + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">"
            + XlsxWriter.WriteTag("Application", "SheetJS")
            + XlsxWriter.WriteTag("HeadingPairs", XlsxWriter.WriteXTag("vt:vector", XlsxWriter.WriteTag("vt:variant", "<vt:lpstr>Worksheets</vt:lpstr>") + XlsxWriter.WriteTag("vt:variant", XlsxWriter.WriteTag("vt:i4", n.ToString(CultureInfo.InvariantCulture))), [("size", "2"), ("baseType", "variant")]))
            + XlsxWriter.WriteTag("TitlesOfParts", XlsxWriter.WriteXTag("vt:vector", titles, [("size", n.ToString(CultureInfo.InvariantCulture)), ("baseType", "lpstr")]))
            + "</Properties>";
        var core = h + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"/>";
        var types = new StringBuilder(h).Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"bin\" ContentType=\"application/vnd.ms-excel.sheet.binary.macroEnabled.main\"/><Default Extension=\"vml\" ContentType=\"application/vnd.openxmlformats-officedocument.vmlDrawing\"/><Default Extension=\"data\" ContentType=\"application/vnd.openxmlformats-officedocument.model+data\"/><Default Extension=\"bmp\" ContentType=\"image/bmp\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"gif\" ContentType=\"image/gif\"/><Default Extension=\"emf\" ContentType=\"image/x-emf\"/><Default Extension=\"wmf\" ContentType=\"image/x-wmf\"/><Default Extension=\"jpg\" ContentType=\"image/jpeg\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Default Extension=\"tif\" ContentType=\"image/tiff\"/><Default Extension=\"tiff\" ContentType=\"image/tiff\"/><Default Extension=\"pdf\" ContentType=\"application/pdf\"/><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        for (var i = 1; i <= n; i++) types.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        types.Append("<Override PartName=\"/xl/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/><Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/><Override PartName=\"/xl/metadata.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheetMetadata+xml\"/></Types>");

        var parts = new List<(string, string)> { ("xl/_rels/workbook.xml.rels", rels.ToString()), ("xl/theme/theme1.xml", SheetJsParts.Theme), ("xl/styles.xml", styles) };
        for (var i = 0; i < n; i++) parts.Add(($"xl/worksheets/sheet{i + 1}.xml", sheetXml[i]));
        parts.AddRange([("xl/metadata.xml", SheetJsParts.Metadata), ("xl/workbook.xml", book.ToString()), ("_rels/.rels", rootRels), ("docProps/app.xml", app), ("docProps/core.xml", core), ("[Content_Types].xml", types.ToString())]);
        return parts.Select(p => Stored(p.Item1, p.Item2)).ToList();
    }

    private static ZipEntry Stored(string name, string xml)
    {
        var data = Encoding.UTF8.GetBytes(xml);
        return new ZipEntry(name, 0, Zip.Crc32(data), data.Length, data, 0, 0, 0);
    }

    /// <summary>The .xlsx file (SheetJS XLSX.write type buffer, bookType xlsx).</summary>
    public byte[] Write() => Zip.WriteZip(WriteEntries());
}
