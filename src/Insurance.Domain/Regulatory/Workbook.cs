using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Regulatory;

/// <summary>A number of the filled workbook Excel would not keep as it is: where, the exact figure and what Excel would hold.</summary>
public sealed record PrecisionProblem(string Sheet, string Ref, string Exact, string Excel);

/// <summary>
/// A supervisory template filled in place (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019): the official workbook as it
/// was given, with only its input cells given values. Every other part of the file — styles, sheets, print settings, the formulas
/// themselves — is written back byte for byte; a formula cell keeps its formula and only its cached result is worked out again
/// (exactly, Formula), and the workbook asks the spreadsheet to calculate everything once more on opening.
/// </summary>
public sealed class TemplateWorkbook
{
    private sealed record Cell(string Ref, int Col, int Row, string Attrs, string? Type = null, string? F = null, string? FText = null, string? Shared = null, string? V = null, string? Inline = null);
    private sealed class Sheet(string name, string path, string xml, OrderedMap<string, Cell> cells)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public string Xml { get; set; } = xml;
        public OrderedMap<string, Cell> Cells { get; set; } = cells;
        public string Original { get; } = xml;
    }
    private sealed record Given(bool IsNumber, string Text);

    private const RegexOptions O = RegexOptions.CultureInvariant;
    private const RegexOptions C = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static string Unescape(string s) => s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static readonly Regex CellRe = new("<c\\b([^>]*?)(?:/>|>([\\s\\S]*?)</c>)", C);
    private static readonly Regex RowRe = new("<row\\b([^>]*?)(?:/>|>([\\s\\S]*?)</row>)", C);
    private static readonly Regex FormulaRe = new("<f\\b([^>]*?)(?:/>|>([\\s\\S]*?)</f>)", C);
    private static readonly Regex VRe = new("<v(?:\\s[^>]*)?>([\\s\\S]*?)</v>", C);
    private static readonly Regex IsRe = new("<is>([\\s\\S]*?)</is>", C);
    private static readonly Regex TRe = new("<t\\b[^>]*>([\\s\\S]*?)</t>", C);
    private static readonly Dictionary<string, Regex> AttrRes = [];
    private static string? Attr(string attrs, string name)
    {
        Regex re;
        lock (AttrRes) { if (!AttrRes.TryGetValue(name, out re!)) AttrRes[name] = re = new Regex($"\\b{name}=\"([^\"]*)\"", C); }
        var m = re.Match(attrs);
        return m.Success ? m.Groups[1].Value : null;
    }
    private static string? G(Match m, int g) => m.Groups[g].Success ? m.Groups[g].Value : null;
    /// <summary>Number(text) of a row's r attribute (NaN when there is none, as JavaScript reads it).</summary>
    private static double RowOf(string attrs) => JsSemantics.ParseNumber(Attr(attrs, "r"));

    /// <summary>The cells of a sheet's XML, by reference.</summary>
    private static OrderedMap<string, Cell> ParseCells(string xml, string path)
    {
        var cells = new OrderedMap<string, Cell>();
        foreach (Match c in CellRe.Matches(xml))
        {
            var attrs = c.Groups[1].Value;
            var body = c.Groups[2].Success ? c.Groups[2].Value : "";
            var r = Attr(attrs, "r");
            if (string.IsNullOrEmpty(r)) throw new InvalidDataException($"A cell without its reference in {path}");
            var (col, row) = SplitRef(r);
            var fm = body.Length == 0 ? Match.Empty : FormulaRe.Match(body);
            var v = body.Length == 0 ? Match.Empty : VRe.Match(body);
            var inline = body.Length == 0 ? Match.Empty : IsRe.Match(body);
            cells.Set(r, new Cell(r, col, row, attrs, Attr(attrs, "t"),
                fm.Success ? fm.Value : null,
                fm.Success && fm.Groups[2].Success ? Unescape(fm.Groups[2].Value) : null,
                fm.Success && Attr(fm.Groups[1].Value, "t") == "shared" ? Attr(fm.Groups[1].Value, "si") : null,
                v.Success ? v.Groups[1].Value : null,
                inline.Success ? inline.Groups[1].Value : null));
        }
        return cells;
    }

    private static readonly Regex XfOpen = new("^<xf\\b[^>]*>", O);
    private static readonly Regex ApplyAlignment = new("\\bapplyAlignment=\"[^\"]*\"", O);
    private static readonly Regex XfStart = new("^<xf\\b", O);
    private static readonly Regex AlignmentTag = new("<alignment\\b", O);
    private static readonly Regex AlignmentRe = new("<alignment\\b([^>]*?)(/?)>", O);
    private static readonly Regex WrapTextAttr = new(JsSemantics.Ws + "*\\bwrapText=\"[^\"]*\"", O);
    private static readonly Regex SelfClose = new(JsSemantics.Ws + "*/>\\z", O);
    private static readonly Regex EndsSelfClose = new("/>\\z", O);
    /// <summary>An xf of the styles with its text wrapped (its other alignment kept), for a cell whose text must be seen in full.</summary>
    private static string WrappedXf(string xf)
    {
        static string WithApply(string open) => ApplyAlignment.IsMatch(open)
            ? JsSemantics.ReplaceFirst(open, ApplyAlignment, "applyAlignment=\"1\"")
            : JsSemantics.ReplaceFirst(open, XfStart, "<xf applyAlignment=\"1\"");
        if (AlignmentTag.IsMatch(xf))
        {
            var opened = XfOpen.Replace(xf, m => WithApply(m.Value), 1);
            return AlignmentRe.Replace(opened, m => $"<alignment{WrapTextAttr.Replace(m.Groups[1].Value, "", 1)} wrapText=\"1\"{m.Groups[2].Value}>", 1);
        }
        if (EndsSelfClose.IsMatch(xf)) return $"{WithApply(SelfClose.Replace(xf, ">", 1))}<alignment wrapText=\"1\"/></xf>";
        return XfOpen.Replace(xf, m => $"{WithApply(m.Value)}<alignment wrapText=\"1\"/>", 1);
    }

    /// <summary>A double's exact value as a fraction (its mantissa over a power of two).</summary>
    public static (BigInteger N, BigInteger D) DoubleRational(double x)
    {
        var bits = BitConverter.DoubleToInt64Bits(x);
        var sign = bits < 0 ? BigInteger.MinusOne : BigInteger.One;
        var exp = (int)((bits >> 52) & 0x7ff);
        var mantissa = new BigInteger(bits & 0xfffffffffffffL);
        int e;
        if (exp == 0) e = -1074; else { mantissa |= BigInteger.One << 52; e = exp - 1075; }
        return e >= 0 ? (sign * (mantissa << e), BigInteger.One) : (sign * mantissa, BigInteger.One << -e);
    }
    /// <summary>n/d to `places` decimal places, half away from zero, as an integer of that scale.</summary>
    private static BigInteger RoundTo(BigInteger n, BigInteger d, int places)
    {
        var p = n * BigInteger.Pow(10, places);
        var a = BigInteger.Abs(p);
        var q = (a * 2 + d) / (2 * d);
        return p.Sign < 0 ? -q : q;
    }
    /// <summary>An integer of scale 10^k as its decimal text with all k places (…408.10, never …408.1).</summary>
    private static string WithPlaces(BigInteger i, int k)
    {
        var neg = i.Sign < 0;
        var digits = BigInteger.Abs(i).ToString(CultureInfo.InvariantCulture).PadLeft(k + 1, '0');
        return $"{(neg ? "-" : "")}{digits[..(digits.Length - k)]}{(k > 0 ? "." + digits[^k..] : "")}";
    }
    /// <summary>
    /// The places a figure is held to: never fewer than two — every figure of these forms is money, and 90071992547408.10 is still a
    /// tetri amount, not one of 0.1 (Codex PR349_REGULATORY_XLSX_NATIVE_PRECISION_20261019: .10 held as .09 passed at one place) — and up
    /// to four for a formula's own product (form 12's 75 %, form 13's shares); beyond four, a double's last bits, not a figure of the form.
    /// </summary>
    private static int PlacesOf(NumValue v)
    {
        for (var k = 2; k <= 4; k++) if ((v.N * BigInteger.Pow(10, k) % v.D).IsZero) return k;
        return 4;
    }
    private static readonly Regex SplitRefRe = new("^([A-Z]+)([0-9]+)\\z", C);
    public static (int Col, int Row) SplitRef(string? reference)
    {
        var m = SplitRefRe.Match(reference ?? "undefined");
        if (!m.Success) throw new FormatException($"Not a cell: {reference ?? "undefined"}");
        return (Formula.ColNumber(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private readonly List<ZipEntry> entries;
    private readonly OrderedMap<string, Sheet> sheets = new();
    private readonly List<string> strings;
    private readonly Dictionary<string, Given> set = [];
    private readonly Dictionary<string, Value> memo = [];
    private readonly Dictionary<string, XValue?> xmemo = [];
    private readonly OrderedSet<string> created = new();
    private readonly Dictionary<string, Node> parsed = [];
    /// <summary>The workbook part once a structural change (rows inserted) moved a defined name; else the template's own.</summary>
    private string? workbookXml;
    /// <summary>Cells whose text must be seen in full: their style wraps the text (wrapText).</summary>
    private readonly OrderedSet<string> wrapped = new();
    /// <summary>Rows given a height of their own (points), set by hand in the file (customHeight).</summary>
    private readonly OrderedMap<string, double> heights = new();

    private static readonly Regex RelRe = new("<Relationship\\b[^>]*\\bId=\"([^\"]+)\"[^>]*\\bTarget=\"([^\"]+)\"", O);
    private static readonly Regex SiRe = new("<si>([\\s\\S]*?)</si>", O);
    private static readonly Regex SheetTagRe = new("<sheet\\b[^>]*\\bname=\"([^\"]*)\"[^>]*\\br:id=\"([^\"]+)\"", O);
    private static readonly Regex SheetNameRe = new("<sheet\\b[^>]*\\bname=\"([^\"]*)\"", O);

    private string EntryText(string name)
    {
        var e = entries.Find(x => x.Name == name) ?? throw new InvalidDataException($"The template has no {name}");
        return Encoding.UTF8.GetString(Zip.EntryData(e));
    }

    private TemplateWorkbook(List<ZipEntry> entries)
    {
        this.entries = entries;
        var rels = new Dictionary<string, string>();
        foreach (Match m in RelRe.Matches(EntryText("xl/_rels/workbook.xml.rels"))) rels[m.Groups[1].Value] = m.Groups[2].Value;
        var shared = entries.Exists(e => e.Name == "xl/sharedStrings.xml") ? EntryText("xl/sharedStrings.xml") : "";
        strings = SiRe.Matches(shared).Select(m => string.Concat(TRe.Matches(m.Groups[1].Value).Select(t => Unescape(t.Groups[1].Value)))).ToList();
        foreach (Match m in SheetTagRe.Matches(EntryText("xl/workbook.xml")))
        {
            var target = rels.TryGetValue(m.Groups[2].Value, out var t) ? t : throw new InvalidDataException($"The template has no relationship {m.Groups[2].Value}");
            var path = target.StartsWith('/') ? target[1..] : $"xl/{target}";
            var xml = EntryText(path);
            var name = Unescape(m.Groups[1].Value);
            sheets.Set(name, new Sheet(name, path, xml, ParseCells(xml, path)));
        }
    }

    public static TemplateWorkbook Open(byte[] buf) => new(Zip.ReadZip(buf));
    public IReadOnlyList<string> SheetNames() => sheets.Keys.ToList();
    private Sheet GetSheet(string name) => sheets.TryGet(name, out var s) ? s : throw new KeyNotFoundException($"The template has no sheet {name}");

    /// <summary>The references of a sheet's cells, in the order its XML holds them (the old code's Map of cells).</summary>
    public IReadOnlyList<string> CellRefs(string sheet) => GetSheet(sheet).Cells.Keys.ToList();
    /// <summary>Whether a cell holds a formula in the template.</summary>
    public bool IsFormula(string sheet, string reference) => GetSheet(sheet).Cells.TryGet(reference, out var c) && c.F is not null;
    /// <summary>A cell's formula as it applies to the cell (a shared one's master text, moved), or null.</summary>
    public string? FormulaOf(string sheet, string reference)
    {
        if (!GetSheet(sheet).Cells.TryGet(reference, out var c) || c.F is null) return null;
        if (!string.IsNullOrEmpty(c.FText)) return c.FText;
        var master = GetSheet(sheet).Cells.Values.FirstOrDefault(x => x.Shared == c.Shared && !string.IsNullOrEmpty(x.FText));
        return master is not null ? $"{master.FText} @ {master.Ref}" : null;
    }
    /// <summary>The template's own text of a cell (a shared string, inline text or number as stored).</summary>
    public string TemplateText(string sheet, string reference)
    {
        if (!GetSheet(sheet).Cells.TryGet(reference, out var c)) return "";
        if (c.Type == "s" && c.V is not null)
        {
            var i = JsSemantics.ParseNumber(c.V);
            return i >= 0 && i < strings.Count && i == Math.Floor(i) ? strings[(int)i] : "";
        }
        if (c.Type == "inlineStr" && c.Inline is not null) return string.Concat(TRe.Matches(c.Inline).Select(t => Unescape(t.Groups[1].Value)));
        return c.V is not null ? Unescape(c.V) : "";
    }

    private void ClearMemo() { memo.Clear(); xmemo.Clear(); }

    /// <summary>Gives an input cell a number, from its exact decimal text. A formula cell is never overwritten.</summary>
    public void SetNumber(string sheet, string reference, string text)
    {
        if (!GetSheet(sheet).Cells.TryGet(reference, out var c)) throw new InvalidOperationException($"The template has no cell {sheet}!{reference} to fill");
        if (c.F is not null) throw new InvalidOperationException($"{sheet}!{reference} is a formula cell; it is not filled");
        Formula.Decimal(text);
        set[$"{sheet}!{reference}"] = new Given(true, text);
        ClearMemo();
    }
    /// <summary>Gives a cell a text (its style kept). A formula cell is never overwritten.</summary>
    public void SetText(string sheet, string reference, string text, bool create = false)
    {
        var found = GetSheet(sheet).Cells.TryGet(reference, out var c);
        if (!found && create)
        {
            // A cell the template leaves out entirely (no style, no value): written into its row, in column order, with no style of its own.
            var (_, row) = SplitRef(reference);
            if (!Regex.IsMatch(GetSheet(sheet).Xml, $"<row r=\"{row}\"[ >]", O)) throw new InvalidOperationException($"The template has no row {row} on {sheet} to put {reference} in");
            created.Add($"{sheet}!{reference}");
            set[$"{sheet}!{reference}"] = new Given(false, text);
            ClearMemo();
            return;
        }
        if (!found) throw new InvalidOperationException($"The template has no cell {sheet}!{reference} to fill");
        if (c!.F is not null) throw new InvalidOperationException($"{sheet}!{reference} is a formula cell; it is not filled");
        set[$"{sheet}!{reference}"] = new Given(false, text);
        ClearMemo();
    }

    private static readonly Regex PartsRe = new("<(conditionalFormatting|dataValidations|hyperlinks|rowBreaks|tableParts|drawing|legacyDrawing)\\b", O);
    private static readonly Regex RowHeightAttrs = new(JsSemantics.Ws + "*\\b(ht|customHeight|thickBot|thickTop)=\"[^\"]*\"", O);
    private static readonly Regex RAttr = new("\\br=\"[0-9]+\"", O);
    private static readonly Regex CellRefAttr = new("(<c\\b[^>]*?\\br=\")([A-Z]+[0-9]+)(\")", O);
    private static readonly Regex MergeRe = new("<mergeCell\\b([^>]*?)\\bref=\"([A-Z]+)([0-9]+):([A-Z]+)([0-9]+)\"", O);
    private static readonly Regex DimensionRe = new("<dimension ref=\"([A-Z]+)([0-9]+):([A-Z]+)([0-9]+)\"", O);
    private static readonly Regex FormulaBodyRe = new("(<f\\b[^>]*>)([\\s\\S]*?)(</f>)", O);
    private static readonly Regex DefinedNameRe = new("<definedName\\b([^>]*)>([\\s\\S]*?)</definedName>", O);
    private static readonly Regex NameRefRe = new("(\\$?[A-Z]{1,3}\\$?)([0-9]+)", O);

    private string Num(double x) => JsSemantics.NumberToString(x);

    /// <summary>
    /// Inserts `count` empty rows after row `after` of a sheet, each styled as the template's row `styleRow` (its cells' styles and the
    /// row's style, without its height, its thick borders or any value). The rows below move down with their cells, as do the merges below,
    /// the sheet's dimension and its print area (owner, 20.10.2026: form 6's rows that do not fit its grid are added below it). Only before
    /// any cell is filled, and only where this moves nothing it does not know: no formula on the sheet or naming it, no conditional format,
    /// data validation, hyperlink, table, drawing or page break on it, and no merge across the insertion (unless `growMerges`: then such a
    /// merge grows over the new rows) — any of these is refused.
    /// </summary>
    public void InsertRows(string sheet, int after, int count, int styleRow, bool growMerges = false)
    {
        if (count < 0 || after < 1) throw new ArgumentException($"Not a row insertion: {count} after {after}");
        if (count == 0) return;
        if (set.Count > 0 || created.Count > 0 || heights.Count > 0) throw new InvalidOperationException("Rows are inserted before any cell is filled");
        var s = GetSheet(sheet);
        if (s.Cells.Values.Any(c => c.F is not null)) throw new InvalidOperationException($"{sheet} has formulas; its rows are not moved");
        // Another sheet's formula may name this one (form 7 takes form 6's company and date, A4 and A5, and its signature lines, A42, A43,
        // A46, A47): a reference to a row that moves moves with it, as the spreadsheet itself does on inserting rows; one above stays.
        // Only plain cell references ('sheet'!A42, $A$42) are moved; a shared formula or any other way of naming the sheet is refused.
        var quoted = Regex.Escape(sheet.Replace("'", "''"));
        var reference = new Regex($"('{quoted}'!\\$?[A-Z]{{1,3}}\\$?)([0-9]+)", O);
        var movedFormulas = new OrderedMap<Sheet, OrderedMap<string, string>>();
        foreach (var other in sheets.Values)
        {
            if (other == s) continue;
            foreach (var c in other.Cells.Values)
            {
                if (c.F is null || !(c.FText ?? c.F).Contains(sheet, StringComparison.Ordinal)) continue;
                if (c.FText is null || c.Shared is not null) throw new InvalidOperationException($"A shared formula on {other.Name} ({c.Ref}) names {sheet}; its rows are not moved");
                var rest = reference.Replace(c.FText, "");
                if (rest.Contains(sheet, StringComparison.Ordinal)) throw new InvalidOperationException($"A formula on {other.Name} ({c.Ref}) names {sheet} in a way that cannot be moved");
                var next = reference.Replace(c.FText, m => long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > after ? $"{m.Groups[1].Value}{long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + count}" : m.Value);
                if (next != c.FText)
                {
                    if (!movedFormulas.TryGet(other, out var map)) movedFormulas.Set(other, map = new OrderedMap<string, string>());
                    map.Set(c.Ref, next);
                }
            }
        }
        if (PartsRe.IsMatch(s.Xml)) throw new InvalidOperationException($"{sheet} has parts its rows cannot be moved under");
        var rows = RowRe.Matches(s.Xml).ToList();
        var model = rows.Find(m => RowOf(m.Groups[1].Value) == styleRow) ?? throw new InvalidOperationException($"{sheet} has no row {styleRow} to style the new rows as");
        if (!rows.Exists(m => RowOf(m.Groups[1].Value) == after)) throw new InvalidOperationException($"{sheet} has no row {after} to insert after");
        var modelAttrs = RowHeightAttrs.Replace(model.Groups[1].Value, "");
        var modelCells = CellRe.Matches(G(model, 2) ?? "").Select(c => (Col: SplitRef(Attr(c.Groups[1].Value, "r")).Col, S: Attr(c.Groups[1].Value, "s"))).ToList();
        var fresh = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var r = after + 1 + i;
            fresh.Append("<row").Append(JsSemantics.ReplaceFirst(modelAttrs, RAttr, $"r=\"{r}\"")).Append('>');
            foreach (var c in modelCells) fresh.Append($"<c r=\"{Formula.ColLetters(c.Col)}{r}\"{(c.S is not null ? $" s=\"{c.S}\"" : "")}/>");
            fresh.Append("</row>");
        }
        string Moved(string cellRef) { var (col, row) = SplitRef(cellRef); return row > after ? $"{Formula.ColLetters(col)}{row + count}" : cellRef; }
        var xml = RowRe.Replace(s.Xml, m =>
        {
            var attrs = m.Groups[1].Value;
            var r = RowOf(attrs);
            if (r == after) return m.Value + fresh;
            if (r < after) return m.Value;
            var a = JsSemantics.ReplaceFirst(attrs, RAttr, $"r=\"{Num(r + count)}\"");
            return !m.Groups[2].Success ? $"<row{a}/>" : $"<row{a}>{CellRefAttr.Replace(m.Groups[2].Value, c => c.Groups[1].Value + Moved(c.Groups[2].Value) + c.Groups[3].Value)}</row>";
        });
        xml = MergeRe.Replace(xml, m =>
        {
            string pre = m.Groups[1].Value, c1 = m.Groups[2].Value, r1 = m.Groups[3].Value, c2 = m.Groups[4].Value, r2 = m.Groups[5].Value;
            long top = long.Parse(r1, CultureInfo.InvariantCulture), bottom = long.Parse(r2, CultureInfo.InvariantCulture);
            // A merge the insertion falls inside grows over the new rows when asked (as the spreadsheet does: form 6's class label over its
            // group, the group made taller by a record that takes several rows); otherwise it is refused.
            if (top <= after && bottom > after)
            {
                if (!growMerges) throw new InvalidOperationException($"{sheet}: a merge across row {after} ({c1}{r1}:{c2}{r2})");
                return $"<mergeCell{pre}ref=\"{c1}{r1}:{c2}{bottom + count}\"";
            }
            return top > after ? $"<mergeCell{pre}ref=\"{c1}{top + count}:{c2}{bottom + count}\"" : m.Value;
        });
        xml = DimensionRe.Replace(xml, m => long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) > after
            ? $"<dimension ref=\"{m.Groups[1].Value}{m.Groups[2].Value}:{m.Groups[3].Value}{long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) + count}\"" : m.Value, 1);
        s.Xml = xml;
        s.Cells = ParseCells(xml, s.Path);
        // The other sheets' references to the rows that moved: their formula text, nothing else of the cell.
        foreach (var (other, formulas) in movedFormulas.Pairs)
        {
            other.Xml = CellRe.Replace(other.Xml, m =>
            {
                var r = Attr(m.Groups[1].Value, "r");
                if (r is null || !formulas.TryGet(r, out var next)) return m.Value;
                return FormulaBodyRe.Replace(m.Value, f => $"{f.Groups[1].Value}{Escape(next)}{f.Groups[3].Value}", 1);
            });
            other.Cells = ParseCells(other.Xml, other.Path);
        }
        // The defined names on the sheet (its print area): every row below the insertion moves down.
        var book = workbookXml ?? EntryText("xl/workbook.xml");
        var index = SheetNameRe.Matches(book).ToList().FindIndex(m => Unescape(m.Groups[1].Value) == sheet);
        workbookXml = DefinedNameRe.Replace(book, m =>
        {
            string attrs = m.Groups[1].Value, body = m.Groups[2].Value;
            var local = Attr(attrs, "localSheetId");
            var names = Unescape(body).Contains($"'{sheet}'!", StringComparison.Ordinal) || Unescape(body).Contains($"{sheet}!", StringComparison.Ordinal);
            if (!names && local != index.ToString(CultureInfo.InvariantCulture)) return m.Value;
            return $"<definedName{attrs}>{NameRefRe.Replace(body, n => long.Parse(n.Groups[2].Value, CultureInfo.InvariantCulture) > after ? $"{n.Groups[1].Value}{long.Parse(n.Groups[2].Value, CultureInfo.InvariantCulture) + count}" : n.Value)}</definedName>";
        });
        ClearMemo();
    }

    /// <summary>
    /// Prints rows `first`–`last` of a sheet at the top of every page (its print titles, `_xlnm.Print_Titles`), so a table that runs over
    /// several pages keeps its column headings on each. A template that already has print titles on the sheet is refused, not overwritten.
    /// </summary>
    public void SetPrintTitles(string sheet, int first, int last)
    {
        if (first < 1 || last < first) throw new ArgumentException($"Not print titles: rows {first}–{last}");
        GetSheet(sheet);
        var book = workbookXml ?? EntryText("xl/workbook.xml");
        var index = SheetNameRe.Matches(book).ToList().FindIndex(m => Unescape(m.Groups[1].Value) == sheet);
        if (Regex.IsMatch(book, $"<definedName\\b[^>]*name=\"_xlnm\\.Print_Titles\"[^>]*localSheetId=\"{index}\"", O)) throw new InvalidOperationException($"{sheet} already has print titles");
        var name = $"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{index}\">'{Escape(sheet.Replace("'", "''"))}'!${first}:${last}</definedName>";
        // After the sheet's print area when it has one (the names of a sheet together), else at the end of the defined names.
        var area = Regex.Match(book, $"<definedName\\b[^>]*name=\"_xlnm\\.Print_Area\"[^>]*localSheetId=\"{index}\"[^>]*>[\\s\\S]*?</definedName>", O);
        workbookXml = area.Success ? JsSemantics.ReplaceFirst(book, area.Value, area.Value + name)
            : book.Contains("</definedNames>", StringComparison.Ordinal) ? JsSemantics.ReplaceFirst(book, "</definedNames>", $"{name}</definedNames>")
            : JsSemantics.ReplaceFirst(book, new Regex("(</sheets>)", O), $"$1<definedNames>{name}</definedNames>");
    }
    /// <summary>Gives a row a height of its own, in points (the spreadsheet does not change it): for text the automatic height cuts short.</summary>
    public void SetRowHeight(string sheet, int row, double points)
    {
        if (row < 1 || !(points > 0 && points <= 409)) throw new ArgumentException($"Not a row height: {row} {Num(points)}");
        if (!Regex.IsMatch(GetSheet(sheet).Xml, $"<row r=\"{row}\"[ >/]", O)) throw new InvalidOperationException($"{sheet} has no row {row}");
        heights.Set($"{sheet}!{row}", points);
    }
    /// <summary>Makes a cell's text wrap within its column, so a long text is seen in full on screen and in print; its other style kept.</summary>
    public void WrapText(string sheet, string reference)
    {
        if (!GetSheet(sheet).Cells.ContainsKey(reference)) throw new InvalidOperationException($"The template has no cell {sheet}!{reference} to wrap");
        wrapped.Add($"{sheet}!{reference}");
    }

    /// <summary>Whether a cell was given a value by the filling.</summary>
    public bool Filled(string sheet, string reference) => set.ContainsKey($"{sheet}!{reference}");
    /// <summary>Whether the template has the cell at all (styled, valued or a formula).</summary>
    public bool HasCell(string sheet, string reference) => GetSheet(sheet).Cells.ContainsKey(reference);

    /// <summary>A cell's value now: what the filling gave it, its formula worked out, or the template's own.</summary>
    public Value ValueOf(string sheet, string reference)
    {
        var key = $"{sheet}!{reference}";
        if (set.TryGetValue(key, out var given)) return given.IsNumber ? Formula.Decimal(given.Text) : new StrValue(given.Text);
        if (memo.TryGetValue(key, out var m)) return m;
        if (!GetSheet(sheet).Cells.TryGet(reference, out var c)) return Formula.Empty;
        if (c.F is not null)
        {
            memo[key] = Formula.Empty; // a cycle reads as empty rather than looping
            var v = EvaluateCell(sheet, c);
            memo[key] = v;
            return v;
        }
        if (c.V is null && c.Inline is null) return Formula.Empty;
        if (c.Type is "s" or "inlineStr" or "str") return new StrValue(TemplateText(sheet, reference));
        if (c.Type == "b") return new BoolValue(c.V == "1");
        if (c.Type == "e") throw new InvalidOperationException($"{sheet}!{reference} holds an error in the template");
        // A number of the template itself is what Excel holds of it — its double, read as its shortest decimal (the 2.5000000000000001E-2
        // stored for form 13's 2.5 % is 0.025), so that a template constant's last bits do not pass for a figure of the form.
        return Formula.Decimal(JsSemantics.NumberToString(JsSemantics.ParseNumber(c.V)));
    }
    private (Cell Source, int Dr, int Dc) FormulaSource(string sheet, Cell c, bool exact)
    {
        if (!string.IsNullOrEmpty(c.FText)) return (c, 0, 0);
        var master = GetSheet(sheet).Cells.Values.FirstOrDefault(x => x.Shared == c.Shared && !string.IsNullOrEmpty(x.FText))
            ?? throw new InvalidOperationException(exact ? $"{sheet}!{c.Ref}: a shared formula without its master" : "Cannot read properties of undefined (reading 'row')");
        return (master, c.Row - master.Row, c.Col - master.Col);
    }
    private Node ParsedOf(string sheet, Cell source)
    {
        var key = $"{sheet}!{source.Ref}";
        if (!parsed.TryGetValue(key, out var node)) parsed[key] = node = Formula.ParseFormula(source.FText!);
        return node;
    }
    private Value EvaluateCell(string sheet, Cell c)
    {
        var (source, dr, dc) = FormulaSource(sheet, c, exact: true);
        return Formula.Evaluate(ParsedOf(sheet, source), sheet, new ExactCells(this), dr, dc);
    }
    private sealed class ExactCells(TemplateWorkbook wb) : ICells
    {
        public Value Value(string sheet, int col, int row) => wb.ValueOf(sheet, $"{Formula.ColLetters(col)}{row}");
    }
    private sealed class DoubleCells(TemplateWorkbook wb) : IXCells
    {
        public XValue? Value(string sheet, int col, int row) => wb.ExcelValue(sheet, $"{Formula.ColLetters(col)}{row}");
    }

    /// <summary>A cell's value as Excel will hold it: a number as the nearest double, each formula over doubles (Formula.EvaluateDouble).</summary>
    public XValue? ExcelValue(string sheet, string reference)
    {
        var key = $"{sheet}!{reference}";
        if (set.TryGetValue(key, out var given)) return given.IsNumber ? new XNum(JsSemantics.ParseNumber(given.Text)) : new XStr(given.Text);
        if (xmemo.TryGetValue(key, out var memoised)) return memoised;
        if (!GetSheet(sheet).Cells.TryGet(reference, out var c)) return null;
        if (c.F is not null)
        {
            xmemo[key] = null;
            var (source, dr, dc) = FormulaSource(sheet, c, exact: false);
            var v = Formula.EvaluateDouble(ParsedOf(sheet, source), sheet, new DoubleCells(this), dr, dc);
            xmemo[key] = v;
            return v;
        }
        if (c.V is null && c.Inline is null) return null;
        if (c.Type is "s" or "inlineStr" or "str") return new XStr(TemplateText(sheet, reference));
        if (c.Type == "b") return new XBool(c.V == "1");
        return new XNum(JsSemantics.ParseNumber(c.V));
    }

    /// <summary>
    /// Every number of the filled workbook that Excel would not keep as it is (INS-REGULATORY-RESERVES-XLSX-NATIVE-PRECISION-FIX-20261019): a
    /// filled amount, or a formula's result over them (a subtotal, a total, a net, form 12's lines and on), whose double — exactly as
    /// Excel holds it — is half a unit of the exact figure's last decimal place — at least the tetri, at most the fourth place — or more
    /// away from it. 90071992547408.01 is held as …408.015625: to the tetri …408.02, a problem; 45.30 held as 45.2999999999884 is 45.30 to
    /// the tetri, kept. A file with any problem is not given out; the exact figures are shown on the page instead.
    /// </summary>
    public IReadOnlyList<PrecisionProblem> PrecisionProblems()
    {
        var outp = new List<PrecisionProblem>();
        foreach (var (name, sheet) in sheets.Pairs)
        {
            foreach (var reference in sheet.Cells.Keys)
            {
                if (ValueOf(name, reference) is not NumValue exact) continue;
                var x = ExcelValue(name, reference);
                if (x is not XNum xn || !double.IsFinite(xn.V))
                {
                    var shownX = x switch { null => "null", XNum n => JsSemantics.NumberToString(n.V), XStr s => s.V, XBool b => b.V ? "true" : "false", _ => "" };
                    outp.Add(new PrecisionProblem(name, reference, Formula.DecimalText(exact), shownX));
                    continue;
                }
                var places = PlacesOf(exact);
                var held = DoubleRational(xn.V);
                // Held within half a unit of the figure's last place: it reads back as the figure; at half a unit or more, as another.
                var gap = exact.N * held.D - held.N * exact.D;
                if (BigInteger.Abs(gap) * 2 * BigInteger.Pow(10, places) >= exact.D * held.D)
                {
                    // Both to the figure's places (a tetri amount as 90071992547408.10, not .1); a figure with more places, exactly as it is.
                    var exactText = (exact.N * BigInteger.Pow(10, places) % exact.D).IsZero ? WithPlaces(RoundTo(exact.N, exact.D, places), places) : Formula.DecimalText(exact);
                    outp.Add(new PrecisionProblem(name, reference, exactText, WithPlaces(RoundTo(held.N, held.D, places), places)));
                }
            }
        }
        return outp;
    }

    private static readonly Regex CalcPrRe = new("<calcPr\\b([^>]*?)/>", O);
    private static readonly Regex FullCalcAttr = new(JsSemantics.Ws + "*fullCalcOnLoad=\"[^\"]*\"", O);

    /// <summary>The filled workbook: input cells with their values, formula cells with their results worked out again, the rest as it was.</summary>
    public byte[] ToBuffer()
    {
        var styles = WrappedStyles();
        var outp = entries.Select(e =>
        {
            var sheet = sheets.Values.FirstOrDefault(s => s.Path == e.Name);
            // A sheet whose cells all stay as they were is written back as it was, byte for byte.
            if (sheet is not null) { var xml = SheetXml(sheet, styles.Map); return xml == sheet.Original ? e : Zip.Replaced(e, Encoding.UTF8.GetBytes(xml)); }
            if (e.Name == "xl/styles.xml" && styles.Xml is not null) return Zip.Replaced(e, Encoding.UTF8.GetBytes(styles.Xml));
            if (e.Name == "xl/workbook.xml")
            {
                var xml = workbookXml ?? Encoding.UTF8.GetString(Zip.EntryData(e));
                var calc = CalcPrRe.Match(xml);
                var next = calc.Success
                    ? JsSemantics.ReplaceFirst(xml, calc.Value, $"<calcPr{FullCalcAttr.Replace(calc.Groups[1].Value, "", 1)} fullCalcOnLoad=\"1\"/>")
                    : JsSemantics.ReplaceFirst(xml, "</workbook>", "<calcPr fullCalcOnLoad=\"1\"/></workbook>");
                return Zip.Replaced(e, Encoding.UTF8.GetBytes(next));
            }
            return e;
        }).ToList();
        return Zip.WriteZip(outp);
    }

    private static readonly Regex CellXfsRe = new("<cellXfs\\b([^>]*)>([\\s\\S]*?)</cellXfs>", O);
    private static readonly Regex XfRe = new("<xf\\b[^>]*?(?:/>|>[\\s\\S]*?</xf>)", O);
    private static readonly Regex CountAttr = new("\\bcount=\"[0-9]+\"", O);
    /// <summary>The styles with a wrapping copy of every style a wrapped cell has, and which copy each takes (none wrapped: as they are).</summary>
    private (string? Xml, Dictionary<string, string> Map) WrappedStyles()
    {
        var map = new Dictionary<string, string>();
        if (wrapped.Count == 0) return (null, map);
        var entry = entries.Find(e => e.Name == "xl/styles.xml") ?? throw new InvalidDataException("The template has no styles to wrap text with");
        var styles = Encoding.UTF8.GetString(Zip.EntryData(entry));
        var block = CellXfsRe.Match(styles);
        if (!block.Success) throw new InvalidDataException("The template's styles have no cell formats");
        var xfs = XfRe.Matches(block.Groups[2].Value).Select(m => m.Value).ToList();
        var added = new List<string>();
        foreach (var key in wrapped)
        {
            var bang = key.LastIndexOf('!');
            var (name, reference) = (key[..bang], key[(bang + 1)..]);
            var s = Attr(GetSheet(name).Cells.Get(reference).Attrs, "s") ?? "0";
            if (map.ContainsKey(s)) continue;
            var i = JsSemantics.ParseNumber(s);
            var xf = i >= 0 && i < xfs.Count && i == Math.Floor(i) ? xfs[(int)i] : throw new InvalidDataException($"{name}!{reference} has a style the template does not have ({s})");
            map[s] = (xfs.Count + added.Count).ToString(CultureInfo.InvariantCulture);
            added.Add(WrappedXf(xf));
        }
        var count = xfs.Count + added.Count;
        var next = JsSemantics.ReplaceFirst(styles, block.Value, $"<cellXfs{JsSemantics.ReplaceFirst(block.Groups[1].Value, CountAttr, $"count=\"{count}\"")}>{block.Groups[2].Value}{string.Concat(added)}</cellXfs>");
        return (next, map);
    }

    private static readonly Regex RowOpenRe = new("<row\\b([^>]*?)(/?)>", O);
    private static readonly Regex HtCustomAttrs = new(JsSemantics.Ws + "*\\b(ht|customHeight)=\"[^\"]*\"", O);
    private static readonly Regex HtAttr = new(JsSemantics.Ws + "*\\bht=\"[^\"]*\"", O);
    private static readonly Regex SAttrPresent = new("\\bs=\"", O);
    private static readonly Regex SAttr = new("\\bs=\"[^\"]*\"", O);
    private static readonly Regex TAttr = new(JsSemantics.Ws + "*\\bt=\"[^\"]*\"", O);
    private static readonly Regex EdgeSpace = new("^" + JsSemantics.Ws + "|" + JsSemantics.Ws + "\\z", O);

    private string SheetXml(Sheet sheet, Dictionary<string, string> wrapStyle)
    {
        // Cells the filling creates go into their rows first, each before the row's first cell of a later column.
        var xml = sheet.Xml;
        foreach (var key in created)
        {
            var bang = key.LastIndexOf('!');
            var (name, reference) = (key[..bang], key[(bang + 1)..]);
            if (name != sheet.Name) continue;
            var (col, row) = SplitRef(reference);
            xml = new Regex($"(<row r=\"{row}\"[^>]*>)([\\s\\S]*?)(</row>)", O).Replace(xml, m =>
            {
                string open = m.Groups[1].Value, body = m.Groups[2].Value, close = m.Groups[3].Value;
                var after = CellRe.Matches(body).FirstOrDefault(c => SplitRef(Attr(c.Groups[1].Value, "r")).Col > col);
                var placeholder = $"<c r=\"{reference}\"/>";
                if (after is null) return open + body + placeholder + close;
                var at = body.IndexOf(after.Value, StringComparison.Ordinal);
                return open + body[..at] + placeholder + body[at..] + close;
            }, 1);
        }
        // A row with a wrapped cell takes the height its text needs: a height the template only records (not set by hand) is left out.
        var prefix = $"{sheet.Name}!";
        var wrappedRows = wrapped.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => (double)SplitRef(k[prefix.Length..]).Row).ToHashSet();
        var sized = new Dictionary<double, double>();
        foreach (var (k, h) in heights.Pairs) if (k.StartsWith(prefix, StringComparison.Ordinal)) sized[JsSemantics.ParseNumber(k[prefix.Length..])] = h;
        if (wrappedRows.Count > 0 || sized.Count > 0)
        {
            xml = RowOpenRe.Replace(xml, m =>
            {
                string attrs = m.Groups[1].Value, close = m.Groups[2].Value;
                var r = RowOf(attrs);
                if (sized.TryGetValue(r, out var h)) return $"<row{HtCustomAttrs.Replace(attrs, "")} ht=\"{Num(h)}\" customHeight=\"1\"{close}>";
                if (!wrappedRows.Contains(r) || Attr(attrs, "customHeight") == "1") return m.Value;
                return $"<row{HtAttr.Replace(attrs, "", 1)}{close}>";
            });
        }
        return CellRe.Replace(xml, m =>
        {
            var whole = m.Value;
            var cellAttrs = m.Groups[1].Value;
            var reference = Attr(cellAttrs, "r")!;
            var key = $"{sheet.Name}!{reference}";
            var wrapTo = wrapped.Contains(key) && wrapStyle.TryGetValue(Attr(cellAttrs, "s") ?? "0", out var w) ? w : null;
            var attrs = wrapTo is null ? cellAttrs : SAttrPresent.IsMatch(cellAttrs) ? JsSemantics.ReplaceFirst(cellAttrs, SAttr, $"s=\"{wrapTo}\"") : $"{cellAttrs} s=\"{wrapTo}\"";
            if (attrs != cellAttrs && !set.ContainsKey(key)) whole = JsSemantics.ReplaceFirst(whole, cellAttrs, attrs);
            var keep = TAttr.Replace(attrs, "", 1);
            if (set.TryGetValue(key, out var given))
            {
                return given.IsNumber
                    ? $"<c{keep}><v>{given.Text}</v></c>"
                    : $"<c{keep} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(given.Text)}</t></is></c>";
            }
            var c = sheet.Cells.TryGet(reference, out var cell) ? cell : new Cell(reference, 0, 0, attrs);
            if (c.F is null) return whole;
            var v = ValueOf(sheet.Name, reference);
            // A formula whose result is the one the template already holds is written back as it was.
            var cached = c.V is null ? null : Unescape(c.V);
            if (cached is not null && ((v is StrValue sv && c.Type == "str" && sv.S == cached)
                || (v is NumValue && c.Type is null && Formula.DecimalText(v) == Formula.DecimalText(Formula.Decimal(cached)))
                || (v is BoolValue bv && c.Type == "b" && cached == (bv.B ? "1" : "0")))) return whole;
            return v switch
            {
                EmptyValue => $"<c{keep}>{c.F}</c>",
                NumValue => $"<c{keep}>{c.F}<v>{Formula.DecimalText(v)}</v></c>",
                BoolValue b => $"<c{keep} t=\"b\">{c.F}<v>{(b.B ? 1 : 0)}</v></c>",
                StrValue s => $"<c{keep} t=\"str\">{c.F}<v{(EdgeSpace.IsMatch(s.S) ? " xml:space=\"preserve\"" : "")}>{Escape(s.S)}</v></c>",
                _ => whole,
            };
        });
    }
}

/// <summary>A JavaScript Map: keys in the order they were first set.</summary>
internal sealed class OrderedMap<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> map = [];
    private readonly List<TKey> order = [];
    public int Count => order.Count;
    public void Set(TKey key, TValue value) { if (!map.ContainsKey(key)) order.Add(key); map[key] = value; }
    public bool TryGet(TKey key, out TValue value) => map.TryGetValue(key, out value!);
    public TValue Get(TKey key) => map[key];
    public bool ContainsKey(TKey key) => map.ContainsKey(key);
    public IEnumerable<TKey> Keys => order;
    public IEnumerable<TValue> Values => order.Select(k => map[k]);
    public IEnumerable<(TKey Key, TValue Value)> Pairs => order.Select(k => (k, map[k]));
}

/// <summary>A JavaScript Set: values in the order they were first added.</summary>
internal sealed class OrderedSet<T> : IEnumerable<T> where T : notnull
{
    private readonly HashSet<T> set = [];
    private readonly List<T> order = [];
    public int Count => order.Count;
    public void Add(T value) { if (set.Add(value)) order.Add(value); }
    public bool Contains(T value) => set.Contains(value);
    public IEnumerator<T> GetEnumerator() => order.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
