using System.Text;
using System.Text.RegularExpressions;
using Insurance.Application.Excel;
using Insurance.Domain.Regulatory;
using static Insurance.Application.Excel.XlsxLayout;

namespace Insurance.Domain.Tests.Excel;

// The report workbook laid out for reading and printing (port of src/lib/xlsx-layout.test.ts; "SheetJS reads them back" is
// Insurance.Application.Excel.XlsxReader, SheetJS's port).
public class XlsxLayoutTests
{
    private static Dictionary<string, string> Files(byte[] buf) => Zip.ReadZip(buf).ToDictionary(e => e.Name, e => Encoding.UTF8.GetString(Zip.EntryData(e)));
    private static IReadOnlyList<object?> R(params object?[] cells) => cells;

    [Fact(DisplayName = "textWidth (Excel 16 AutoFit widths, Calibri 12, in digit widths): counts digits as one, narrow and wide Latin and Georgian letters by their measured width")]
    public void TextWidths()
    {
        static void Near(double actual, double expected) => Assert.True(Math.Abs(actual - expected) < 1e-9, $"{actual} ≠ {expected}");
        Near(TextWidth("2026-10-20"), 9.5);
        Near(TextWidth("iW"), 2.6);
        Near(TextWidth("ლა"), 2.85);
        Near(TextWidth("ab", true), 2.18);
        // Not measured: ASCII as a digit, anything else as a wide letter.
        Assert.Equal(1, TextWidth("~"));
        Assert.Equal(1.5, TextWidth("Ა"));
    }

    [Fact(DisplayName = "columnFits: fits the widest cell, ignores rows above the header, and pads by 2")]
    public void FitsTheWidestCell()
    {
        var fits = ColumnFits([R("a title far wider than any column below it"), R(), R("#", "x"), R(1d, "0123456789")], 2);
        Assert.Equal([new ColumnFit(MinWidth + 2, false), new ColumnFit(10 + 2, false)], fits);
    }

    [Fact(DisplayName = "columnFits: lets a header wrap to its longest word or a third of its width, whichever is wider")]
    public void HeaderWraps()
    {
        var fits = ColumnFits([R("Wide WWWWWWWWWW", "aa aa aa aa aa aa aa aa aa aa aa aa"), R(1d, 1d)], 0);
        Assert.Equal(Math.Ceiling(TextWidth("WWWWWWWWWW", true)) + 2, fits[0].Width);
        Assert.Equal(Math.Ceiling(TextWidth("aa aa aa aa aa aa aa aa aa aa aa aa", true) / 3) + 2, fits[1].Width);
    }

    [Fact(DisplayName = "columnFits: measures every row of a report longer than a function call takes arguments, the widest cell last")]
    public void MeasuresEveryRow()
    {
        // Codex PR365_XLSX_LAYOUT_SPREAD_ARGUMENT_LIMIT_20261020: spread into Math.max, 140 000 rows threw a RangeError.
        var rows = new List<IReadOnlyList<object?>> { R("#", "policy", "name") };
        for (var i = 1; i <= 200_000; i++) rows.Add(R((double)i, $"P-{i}", "ა"));
        rows.Add(R(200_001d, "P-".PadRight(30, '0'), new string('ლ', 40)));
        Assert.Equal([
            new ColumnFit(Math.Ceiling(TextWidth("200001")) + 2, false),
            new ColumnFit(Math.Ceiling(TextWidth("P-".PadRight(30, '0'))) + 2, false),
            new ColumnFit(MaxWidth + 2, true),
        ], ColumnFits(rows, 0));
    }

    [Fact(DisplayName = "columnFits: gives a column to every cell of the widest row, and none for an empty sheet")]
    public void ColumnForEveryCell()
    {
        Assert.Equal([MinWidth + 2, MinWidth + 2, MinWidth + 2], ColumnFits([R("a"), R(1d, 2d, "xyz")], 0).Select(f => f.Width));
        Assert.Empty(ColumnFits([], 0));
    }

    [Fact(DisplayName = "columnFits: stops at the cap and wraps that column instead of cutting it")]
    public void StopsAtTheCap()
    {
        Assert.Equal(new ColumnFit(MaxWidth + 2, true), ColumnFits([R("h"), R(new string('0', (int)MaxWidth + 1))], 0)[0]);
        Assert.Equal(new ColumnFit(MaxWidth + 2, false), ColumnFits([R("h"), R(new string('0', (int)MaxWidth))], 0)[0]);
    }

    private static readonly IReadOnlyList<IReadOnlyList<object?>> Rows = [R("period"), R(), R("it's", "b"), R(1.5, new string('x', 60)), R(2d, "")];
    private static readonly byte[] Buf = ReadableWorkbook([new SheetRows("O'Brien", Rows, 2)]);
    private static readonly Dictionary<string, string> All = Files(Buf);
    private static readonly string Sheet = All["xl/worksheets/sheet1.xml"];

    [Fact(DisplayName = "readableWorkbook: keeps the rows as SheetJS reads them back")]
    public void KeepsTheRows()
    {
        var wb = XlsxReader.Read(Buf);
        Assert.Equal(["O'Brien"], wb.SheetNames);
        var back = SheetToJson.ToArrays(wb.Sheets["O'Brien"]);
        Assert.Equal(Rows.Count, back.Count);
        for (var i = 0; i < Rows.Count; i++) Assert.Equal(Rows[i], back[i]);
    }

    [Fact(DisplayName = "readableWorkbook: freezes and prints the header, landscape A4 one page wide")]
    public void FreezesAndPrints()
    {
        Assert.Contains("<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">&apos;O&apos;&apos;Brien&apos;!$3:$3</definedName>", All["xl/workbook.xml"]);
        Assert.True(Sheet.StartsWith("<?xml", StringComparison.Ordinal) && Sheet.Contains("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><dimension ", StringComparison.Ordinal));
        Assert.Contains("<pane ySplit=\"3\" topLeftCell=\"A4\" activePane=\"bottomLeft\" state=\"frozen\"/>", Sheet);
        Assert.Contains("<printOptions gridLines=\"1\"/><pageMargins left=\"0.25\" right=\"0.25\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/><pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>", Sheet);
    }

    [Fact(DisplayName = "readableWorkbook: styles the header bold and wrapped and wraps only the over-long column below it")]
    public void StylesTheHeader()
    {
        var styles = All["xl/styles.xml"];
        Assert.Contains("<fonts count=\"2\">", styles);
        Assert.Contains("<cellXfs count=\"3\">", styles);
        Assert.Matches("<c r=\"A3\" s=\"1\" t=\"str\">", Sheet);
        Assert.Matches("<c r=\"B3\" s=\"1\" t=\"str\">", Sheet);
        Assert.Matches("<c r=\"A4\">", Sheet);
        Assert.Matches("<c r=\"B4\" s=\"2\" t=\"str\">", Sheet);
        Assert.Matches("<c r=\"A1\" t=\"str\">", Sheet);
        Assert.Matches("<xf numFmtId=\"0\" fontId=\"1\"[^>]*><alignment vertical=\"top\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\"[^>]*><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>", styles);
    }
}
