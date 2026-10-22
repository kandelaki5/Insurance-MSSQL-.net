using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>A sheet of a report workbook: its rows exactly as they go in, and which of them is the header (rows above it are a title).</summary>
public sealed record SheetRows(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows, int Header);

/// <summary>A column's width (characters of the default font) and whether its body text is longer than MaxWidth, so its cells wrap.</summary>
public sealed record ColumnFit(double Width, bool Wrap);

/// <summary>
/// A plain report workbook (one written from rows, not from an official template) laid out to be read on screen and on paper
/// (owner 20.10.2026: the impairment and premium-by-policy exports printed one column per sheet, every text cut). The rows go
/// into the sheet exactly as given — same values, types and order; only the layout around them is added: column widths
/// fitted to the header and the cells below it, a wrapped bold header, the header frozen on screen and repeated on every
/// printed page, landscape A4 one page wide with gridlines. The writer (SheetJS's, XlsxWriter) writes widths, margins and print
/// titles itself; the pane, the page setup and the two cell styles are added to its XML afterwards.
/// </summary>
public static class XlsxLayout
{
    /// <summary>Width limits, in characters of the default font: a column never narrower or wider than this.</summary>
    public const double MinWidth = 4;
    public const double MaxWidth = 45;
    /// <summary>A header wraps into at most about this many lines before its column is made wider for it.</summary>
    private const double HeaderLines = 3;
    /// <summary>Room for the cell's inner margins and for an estimate that came out a little short.</summary>
    private const double Padding = 2;

    /// <summary>How wide each character is drawn, in widths of a digit (the unit of a column width): measured with Excel 16's own AutoFit
    /// on 30 copies of each character in the workbook's default font (Calibri 12), each width rounded up to its group's.</summary>
    private static readonly (string Chars, double Width)[] Glyphs =
    [
        ("ijl.,: ", 0.5), ("IJft()/", 0.66), ("rs-„“", 0.75), ("acegzაკჟქყჸ", 0.92),
        ("0123456789Sovxy–#_ბვმნპსშჩძწჰჳჴჷ", 1), ("FLbdhknpquგიჭხჲჵჹ", 1.08), ("BEPTც", 1.17), ("Z₾ჱჺ", 1.25),
        ("CKRXYრღჶ", 1.34), ("ADGNUVოფ", 1.42), ("HOQზტუჯ", 1.51), ("mwდთ", 1.68), ("M", 1.85), ("ლ", 1.93), ("W", 2.1),
    ];
    private static readonly Dictionary<int, double> Glyph = BuildGlyphs();
    private static Dictionary<int, double> BuildGlyphs()
    {
        var map = new Dictionary<int, double>();
        foreach (var (chars, w) in Glyphs) foreach (var rune in chars.EnumerateRunes()) map[rune.Value] = w;
        return map;
    }
    /// <summary>Bold draws every character this much wider (measured the same way).</summary>
    private const double Bold = 0.09;
    /// <summary>A character not measured: other ASCII as a digit, anything else as one of the widest Georgian letters.</summary>
    private static double GlyphOf(Rune ch, string text, int index) => Glyph.TryGetValue(ch.Value, out var w) ? w : text[index] < 128 ? 1 : 1.5;
    public static double TextWidth(string text, bool bold = false)
    {
        double sum = 0;
        for (var i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var used);
            sum = sum + GlyphOf(rune, text, i) + (bold ? Bold : 0);
            i += used;
        }
        return sum;
    }
    /// <summary>String(v) of a cell's value as the old code measured it (null and undefined: nothing).</summary>
    internal static string Shown(object? v) => v switch
    {
        null or JsUndefined => "",
        DateTime t => JsDateString(t),
        DateOnly d => JsDateString(d.ToDateTime(TimeOnly.MinValue)),
        decimal m => JsSemantics.NumberToString(double.Parse(m.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        _ => JsSemantics.ToJsString(v),
    };
    /// <summary>Date.prototype.toString() in a UTC process: "Fri Jan 02 2026 00:00:00 GMT+0000 (Coordinated Universal Time)".</summary>
    private static string JsDateString(DateTime t) => t.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT+0000 (Coordinated Universal Time)";

    private static readonly Regex Spaces = new(JsSemantics.Ws + "+", RegexOptions.CultureInvariant);

    /// <summary>Each column's width from its header (wrapped, so its longest word or a third of it) and the widest cell below it.</summary>
    public static List<ColumnFit> ColumnFits(IReadOnlyList<IReadOnlyList<object?>> rows, int header)
    {
        // One pass over the rows with plain loops: a report can have far more rows than a function takes arguments (Codex
        // PR365_XLSX_LAYOUT_SPREAD_ARGUMENT_LIMIT_20261020: 140 000 rows stopped the export with a RangeError).
        IReadOnlyList<object?> head = header >= 0 && header < rows.Count ? rows[header] : [];
        var bodies = new List<double>();
        for (var r = Math.Max(header, 0); r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var width = r > header ? TextWidth(Shown(row[c])) : 0;
                while (bodies.Count <= c) bodies.Add(double.NaN);
                if (double.IsNaN(bodies[c]) || width > bodies[c]) bodies[c] = width;
            }
        }
        return bodies.Select((body, c) =>
        {
            var title = Shown(c < head.Count ? head[c] : null);
            var words = Spaces.Split(title).Aggregate(0d, (widest, w) => Math.Max(widest, TextWidth(w, true)));
            var titleNeed = Math.Max(words, TextWidth(title, true) / HeaderLines);
            var wanted = Math.Max(Math.Max(MinWidth, titleNeed), body);
            return new ColumnFit(Math.Ceiling(Math.Min(MaxWidth, wanted)) + Padding, body > MaxWidth);
        }).ToList();
    }

    private static string QuoteSheet(string name) => $"'{name.Replace("'", "''")}'";

    /// <summary>The sheets as an .xlsx file, each laid out for reading and printing.</summary>
    public static byte[] ReadableWorkbook(IReadOnlyList<SheetRows> sheets)
    {
        var workbook = new XlsxBook();
        var fits = sheets.Select(s => ColumnFits(s.Rows, s.Header)).ToList();
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = XlsxWriter.AoaToSheet(sheets[i].Rows);
            sheet.Cols = fits[i].Select(f => (double?)f.Width).ToList();
            sheet.Margins = new PageMargins(0.25, 0.25, 0.5, 0.5, 0.3, 0.3);
            workbook.AppendSheet(sheet, sheets[i].Name);
        }
        for (var i = 0; i < sheets.Count; i++)
        {
            var row = (sheets[i].Header + 1).ToString(CultureInfo.InvariantCulture);
            workbook.Names.Add(new DefinedName("_xlnm.Print_Titles", i, $"{QuoteSheet(sheets[i].Name)}!${row}:${row}"));
        }
        return LaidOut(workbook.WriteEntries(), sheets, fits);
    }

    private const string XfHeader = "header";
    private const string XfWrap = "wrap";

    /// <summary>The writer's own file with the frozen header, the page setup and the header / wrapped-cell styles written into its XML.</summary>
    private static byte[] LaidOut(List<ZipEntry> entries, IReadOnlyList<SheetRows> sheets, IReadOnlyList<List<ColumnFit>> fits)
    {
        string Text(string name) => Encoding.UTF8.GetString(Zip.EntryData(entries.Find(x => x.Name == name) ?? throw new InvalidDataException($"The workbook has no {name}")));
        void Put(string name, string xml) { var at = entries.FindIndex(x => x.Name == name); entries[at] = Zip.Replaced(entries[at], Encoding.UTF8.GetBytes(xml)); }
        var xf = Styles(Text("xl/styles.xml"));
        Put("xl/styles.xml", xf.Xml);
        for (var i = 0; i < sheets.Count; i++)
        {
            var name = $"xl/worksheets/sheet{i + 1}.xml";
            Put(name, SheetLayout(Text(name), sheets[i].Header + 1, fits[i], xf.Index));
        }
        return Zip.WriteZip(entries);
    }

    private static readonly Regex FontsRe = new("<fonts count=\"([0-9]+)\">([\\s\\S]*?)</fonts>", RegexOptions.CultureInvariant);
    private static readonly Regex XfsRe = new("<cellXfs count=\"([0-9]+)\">([\\s\\S]*?)</cellXfs>", RegexOptions.CultureInvariant);
    private static readonly Regex FontRe = new("<font>([\\s\\S]*?)</font>", RegexOptions.CultureInvariant);
    /// <summary>Two cell styles added after the writer's own: the header (bold, wrapped, top-aligned) and a wrapped body cell.</summary>
    private static (string Xml, Dictionary<string, int> Index) Styles(string xml)
    {
        var fonts = FontsRe.Match(xml);
        var xfs = XfsRe.Match(xml);
        if (!fonts.Success || !xfs.Success) throw new InvalidDataException("The workbook styles have no fonts or cell formats");
        var firstFont = FontRe.Match(fonts.Groups[2].Value);
        if (!firstFont.Success) throw new InvalidDataException("The workbook styles have no default font");
        var bold = int.Parse(fonts.Groups[1].Value, CultureInfo.InvariantCulture);
        var first = int.Parse(xfs.Groups[1].Value, CultureInfo.InvariantCulture);
        var headerXf = $"<xf numFmtId=\"0\" fontId=\"{bold}\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>";
        const string wrapXf = "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>";
        var next = JsSemantics.ReplaceFirst(xml, fonts.Value, $"<fonts count=\"{bold + 1}\">{fonts.Groups[2].Value}<font><b/>{firstFont.Groups[1].Value}</font></fonts>");
        next = JsSemantics.ReplaceFirst(next, xfs.Value, $"<cellXfs count=\"{first + 2}\">{xfs.Groups[2].Value}{headerXf}{wrapXf}</cellXfs>");
        return (next, new Dictionary<string, int> { [XfHeader] = first, [XfWrap] = first + 1 });
    }

    private static readonly Regex MarginsRe = new("<pageMargins\\b[^>]*/>", RegexOptions.CultureInvariant);
    private static readonly Regex CellStartRe = new("<c r=\"([A-Z]+)([0-9]+)\"", RegexOptions.CultureInvariant);

    /// <summary>One sheet's XML with the header row (1-based) frozen and styled, wrapped long text, and the printed page set up.</summary>
    private static string SheetLayout(string xml, int headerRow, IReadOnlyList<ColumnFit> fits, Dictionary<string, int> xf)
    {
        if (xml.Contains("<sheetPr", StringComparison.Ordinal) || xml.Contains("<pageSetup", StringComparison.Ordinal) || xml.Contains("<printOptions", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected sheet properties from SheetJS");
        const string view = "<sheetView workbookViewId=\"0\"/>";
        if (!xml.Contains(view, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected sheet view from SheetJS");
        var margins = MarginsRe.Match(xml);
        if (!margins.Success) throw new InvalidDataException("The sheet has no page margins");
        var topLeft = $"A{headerRow + 1}";
        var cells = CellStartRe.Replace(xml, m =>
        {
            var row = m.Groups[2].Value;
            if (row == headerRow.ToString(CultureInfo.InvariantCulture)) return $"{m.Value} s=\"{xf[XfHeader]}\"";
            var col = Formula.ColNumber(m.Groups[1].Value) - 1;
            if (long.Parse(row, CultureInfo.InvariantCulture) > headerRow && col < fits.Count && fits[col].Wrap) return $"{m.Value} s=\"{xf[XfWrap]}\"";
            return m.Value;
        });
        cells = JsSemantics.ReplaceFirst(cells, "<dimension ", "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><dimension ");
        cells = JsSemantics.ReplaceFirst(cells, view, $"<sheetView workbookViewId=\"0\"><pane ySplit=\"{headerRow}\" topLeftCell=\"{topLeft}\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\" activeCell=\"{topLeft}\" sqref=\"{topLeft}\"/></sheetView>");
        return JsSemantics.ReplaceFirst(cells, margins.Value, $"<printOptions gridLines=\"1\"/>{margins.Value}<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
    }
}
