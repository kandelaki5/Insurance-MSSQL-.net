using System.Text;
using System.Text.RegularExpressions;
using Insurance.Application.Excel;
using Insurance.Domain.Regulatory;
using static Insurance.Domain.Regulatory.Form6;

namespace Insurance.Domain.Tests.Regulatory;

// Form 6 (INS-REGULATORY-STATISTICAL-FORM6-EXISTING-DATA-20261019): the treaties in force on the quarter's last day, one row per class ×
// reinsurer × treaty, from raw rows as the reader gives them; the official template filled as text. Each expected cell is spelled
// out here from the raw rows below. (Port of src/lib/regulatory/form6.test.ts.)
public class Form6Tests
{
    private static readonly byte[] Template = Templates.OtherQuarterlyTemplate();
    /// <summary>The treaty kinds' labels (the old src/i18n/reinsurance.ts reinsuranceTexts.kinds).</summary>
    private static readonly Dictionary<string, string> K = new()
    {
        ["quota_share"] = "კვოტური", ["surplus"] = "ექსცედენტური", ["excess_of_loss"] = "ზარალის ექსცედენტი (XL)", ["facultative"] = "ფაკულტატური",
    };
    private static Form6Treaty Treaty(string id, string number, string kind, string from, string to, string reinsurer,
        string? rating = null, string? agency = null, string? country = null, string? broker = null, string? brokerCountry = null) =>
        new(id, number, kind, from, to, rating, agency, reinsurer, country, broker, brokerCountry);

    private sealed class Source
    {
        public List<Form6Treaty> Treaties = [];
        public List<Form6TreatyClass> TreatyClasses = [];
        public List<Form6TreatyClass> PlacedClasses = [];
        public List<Form6Class> Classes = [];
        public Form6Source Build() => new(Treaties, TreatyClasses, PlacedClasses, Classes);
    }
    /// <summary>Fictional treaties around 31.12.2026.</summary>
    private static Source Fictional() => new()
    {
        Treaties =
        [
            Treaty("t1", "RI-0001", "quota_share", "2026-01-01", "2026-12-31", "Munich Re (გამოგონილი)", "AA-", "S&P", "DE", "Aon & Partners (გამოგონილი)", "GB"),
            Treaty("t2", "RI-0002", "excess_of_loss", "2026-12-31", "2027-12-30", "007 Re \"A&B\" <test> (გამოგონილი)"),
            Treaty("t3", "RI-0003", "facultative", "2026-06-01", "2027-05-31", "=1+1 Re (გამოგონილი)", country: "CH"),
            Treaty("t4", "RI-0004", "surplus", "2025-01-01", "2026-12-30", "Ended Re (გამოგონილი)"),
            Treaty("t5", "RI-0005", "quota_share", "2027-01-01", "2027-12-31", "Future Re (გამოგონილი)"),
        ],
        TreatyClasses = [new("t1", "5.1"), new("t1", "13"), new("t2", "5.1"), new("t3", "13"), new("t4", "5.1"), new("t5", "5.1")],
        // The facultative treaty placed on two 13 lines (and one 6.1): 13 once, whatever the joins.
        PlacedClasses = [new("t3", "13"), new("t3", "6.1"), new("t1", "4")],
        Classes = [new("5.1", "კასკო", 501), new("6.1", "MTPL", 601), new("13", "ქონება", 1300), new("4", "სამედიცინო", 400)],
    };
    private static List<Form6Row> Rows(Source s, string asOf = "2026-12-31") => Form6Rows(s.Build(), asOf, K);
    private static string? T(TemplateWorkbook wb, string reference) => wb.ValueOf(Form6Sheet, reference) switch { StrValue s => s.S, EmptyValue => null, var v => v.ToString() };
    private static string? Ts(TemplateWorkbook wb, string reference) => wb.ValueOf(Form6Sheet, reference) is StrValue s ? s.S : null;
    private static void AssertOverflow(List<Form6Row> rows, string[] classes, int count)
    {
        var (c, n) = Form6Overflow(rows);
        Assert.Equal(classes, c);
        Assert.Equal(count, n);
    }
    private static string SheetXml(byte[] b, string name = "xl/worksheets/sheet1.xml") => Encoding.UTF8.GetString(Zip.EntryData(Zip.ReadZip(b).First(e => e.Name == name)));
    private static string PrintArea(byte[] b) => Regex.Match(SheetXml(b, "xl/workbook.xml"), "<definedName name=\"_xlnm\\.Print_Area\" localSheetId=\"0\">([^<]*)</definedName>").Groups[1].Value;
    private static List<string> Merges(string xml) => Regex.Matches(xml, "<mergeCell ref=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();
    private static List<string> Xfs(byte[] b)
    {
        var x = Encoding.UTF8.GetString(Zip.EntryData(Zip.ReadZip(b).First(e => e.Name == "xl/styles.xml")));
        return Regex.Matches(Regex.Match(x, "<cellXfs\\b[^>]*>([\\s\\S]*?)</cellXfs>").Groups[1].Value, "<xf\\b[^>]*?(?:/>|>[\\s\\S]*?</xf>)").Select(m => m.Value).ToList();
    }

    [Fact(DisplayName = "form 6 rows from the raw rows — 31.12.2026: in force by period (both days included); facultative classes from its placements; each class × treaty once; ordered by class, reinsurer, number")]
    public void RowsOnTheDay()
    {
        var rows = Rows(Fictional());
        Assert.Equal([
            ("05.1 კასკო", "007 Re \"A&B\" <test> (გამოგონილი)", K["excess_of_loss"]), ("05.1 კასკო", "Munich Re (გამოგონილი)", K["quota_share"]),
            ("06.1 MTPL", "=1+1 Re (გამოგონილი)", K["facultative"]),
            ("13 ქონება", "=1+1 Re (გამოგონილი)", K["facultative"]), ("13 ქონება", "Munich Re (გამოგონილი)", K["quota_share"]),
        ], rows.Select(r => (r.ClassLabel, r.Cells["B"], r.Cells["C"])));
        // A quota share's placed class (t1 → 4) is not its class: only a facultative treaty takes its classes from placements.
        Assert.DoesNotContain(rows, r => r.ClassCode == "4");
        var munich = rows.First(r => r.ClassCode == "5.1" && r.Cells["B"].StartsWith("Munich", StringComparison.Ordinal));
        Assert.Equal(new Dictionary<string, string> { ["B"] = "Munich Re (გამოგონილი)", ["C"] = K["quota_share"], ["D"] = "01.01.2026 – 31.12.2026", ["E"] = "AA-", ["F"] = "S&P", ["G"] = "DE", ["H"] = "Aon & Partners (გამოგონილი)", ["I"] = "GB" }, munich.Cells);
        var xl = rows.First(r => r.Cells["C"] == K["excess_of_loss"]);
        Assert.Equal(["", "", "", "", ""], new[] { "E", "F", "G", "H", "I" }.Select(c => xl.Cells[c]));
    }

    [Fact(DisplayName = "form 6 rows from the raw rows — the period boundary: the day before t2 starts and after t4 ends")]
    public void PeriodBoundary()
    {
        Assert.Equal(["Ended Re (გამოგონილი)", "Munich Re (გამოგონილი)"], Rows(Fictional(), "2026-12-30").Where(r => r.ClassCode == "5.1").Select(r => r.Cells["B"]));
        Assert.Equal(["007 Re \"A&B\" <test> (გამოგონილი)", "Future Re (გამოგონილი)"], Rows(Fictional(), "2027-01-01").Where(r => r.ClassCode == "5.1").Select(r => r.Cells["B"]));
    }

    [Fact(DisplayName = "form 6 rows from the raw rows — quarters: their last days, a year's turn and a leap February's quarter; anything else none")]
    public void Quarters()
    {
        Assert.Equal(["2026-03-31", "2026-06-30", "2026-09-30", "2026-12-31", "2028-03-31"], new[] { "2026-Q1", "2026-Q2", "2026-Q3", "2026-Q4", "2028-Q1" }.Select(QuarterEnd));
        foreach (var q in new[] { "2026-Q0", "2026-Q5", "2026Q4", "26-Q4", "2026-q4", "", " 2026-Q4" }) Assert.True(QuarterEnd(q) is null, q);
        Assert.Equal(["2026-Q1", "2026-Q1", "2026-Q2", "2026-Q4"], new[] { "2026-01", "2026-03", "2026-04", "2026-12" }.Select(QuarterOf));
        Assert.Equal(["01", "05.1", "13", "17.3"], new[] { "1", "5.1", "13", "17.3" }.Select(FormCode));
    }

    [Fact(DisplayName = "the template, filled — its hash; form 6 and the court cases sheet; the groups A9:A13 … A34:A38; the input cells there and empty")]
    public void TheTemplate()
    {
        Assert.Equal(OtherQuarterlyTemplateSha256, TemplateSha(Template));
        var wb = TemplateWorkbook.Open(Template);
        Assert.Equal([Form6Sheet, "სასამართლო დავები"], wb.SheetNames());
        for (var row = 9; row <= 38; row++)
            foreach (var col in new[] { "A" }.Concat(Columns))
            {
                if (col == "A" && (row - 9) % 5 != 0) continue;
                Assert.True(wb.HasCell(Form6Sheet, $"{col}{row}"), $"{col}{row}");
                Assert.True(wb.TemplateText(Form6Sheet, $"{col}{row}") == "", $"{col}{row}");
            }
    }

    [Fact(DisplayName = "the template, filled — the rows in their groups as text, the header; empty sources empty; every other part of the file as it was")]
    public void RowsInTheirGroups()
    {
        var rows = Rows(Fictional());
        var wb = FillForm6(Template, rows, "2026-12-31", "სატესტო კომპანია (გამოგონილი)");
        Assert.Equal(["05.1 კასკო", "007 Re \"A&B\" <test> (გამოგონილი)", "Munich Re (გამოგონილი)", "06.1 MTPL", "=1+1 Re (გამოგონილი)", "13 ქონება", "=1+1 Re (გამოგონილი)", "Munich Re (გამოგონილი)", null],
            new[] { "A9", "B9", "B10", "A14", "B14", "A19", "B19", "B20", "A24" }.Select(r => T(wb, r)));
        Assert.Equal(["Munich Re (გამოგონილი)", K["quota_share"], "01.01.2026 – 31.12.2026", "AA-", "S&P", "DE", "Aon & Partners (გამოგონილი)", "GB"], Columns.Select(c => T(wb, $"{c}10")));
        Assert.Equal([null, null, null, null, null], new[] { "E9", "G9", "H9", "I9", "B11" }.Select(r => T(wb, r)));
        Assert.Equal("კომპანიის დასახელება: სატესტო კომპანია (გამოგონილი)", T(wb, "A4"));
        Assert.Equal("საანგარიშო თარიღი: 31.12.2026", T(wb, "A5"));
        // Text stays text: no formula, no number; the XML escapes what it must.
        var output = wb.ToBuffer();
        var before = Zip.ReadZip(Template).ToDictionary(e => e.Name);
        foreach (var e in Zip.ReadZip(output))
            if (!new[] { "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/workbook.xml", "xl/styles.xml" }.Contains(e.Name)) Assert.True(e.Compressed.AsSpan().SequenceEqual(before[e.Name].Compressed), e.Name);
        // The styles: every format of the template as it was, byte for byte; after them only wrapping copies of the filled cells' own
        // formats (the owner, 20.10.2026: every row seen in full on screen, in the file and in print).
        List<string> xfsWas = Xfs(Template), xfsNow = Xfs(output);
        Assert.Equal(xfsWas, xfsNow.Take(xfsWas.Count));
        foreach (var added in xfsNow.Skip(xfsWas.Count)) Assert.Matches("<alignment\\b[^>]*wrapText=\"1\"", added);
        // The court cases sheet: only its header's two formulas (the company and the date of form 6) hold new results; nothing else.
        var was = FinancialAnnexTests.CellsOf(SheetXml(Template, "xl/worksheets/sheet2.xml"));
        var now = FinancialAnnexTests.CellsOf(SheetXml(output, "xl/worksheets/sheet2.xml"));
        Assert.Equal(["A4", "A5"], now.Where(p => was.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key));
        foreach (var r in new[] { "A4", "A5" }) Assert.True(now[r].Contains(Regex.Match(was[r], "<f>[^<]*</f>").Value, StringComparison.Ordinal), r);
        var sheet = SheetXml(output);
        var blank = SheetXml(Template);
        // Form 6's own sheet: its merges, print setup and margins (all outside the cells), its rows and every cell's style as they were —
        // but the filled rows whose text runs to two lines (the dates in IV do), which have the height of their lines.
        static string Outside(string x) => Regex.Replace(x, "<sheetData>[\\s\\S]*</sheetData>", "");
        Assert.True(blank.Contains("<mergeCell ref=\"A9:A13\"/>", StringComparison.Ordinal) && blank.Contains("<pageSetup ", StringComparison.Ordinal));
        Assert.Equal(Outside(blank), Outside(sheet));
        var sized = new[] { 9, 10, 14, 19, 20 }.ToDictionary(r => r, _ => 2 * LinePoints);
        Assert.Equal(Regex.Matches(blank, "<row [^>]*>").Select(m =>
        {
            var r = int.Parse(Regex.Match(m.Value, " r=\"([0-9]+)\"").Groups[1].Value);
            return sized.TryGetValue(r, out var h) ? Regex.Replace(m.Value, ">$", $" ht=\"{h}\" customHeight=\"1\">") : m.Value;
        }), Regex.Matches(sheet, "<row [^>]*>").Select(m => m.Value));
        // Every cell keeps its style but the filled ones, whose style is the wrapping copy of their own.
        static Dictionary<string, string> StyleOf(string x) => FinancialAnnexTests.CellsOf(x).ToDictionary(p => p.Key, p => Regex.Match(p.Value, "\\ss=\"([0-9]+)\"") is { Success: true } m ? m.Groups[1].Value : "0");
        Dictionary<string, string> sWas = StyleOf(blank), sNow = StyleOf(sheet);
        var filled = new HashSet<string> { "A9", "B9", "C9", "D9", "B10", "C10", "D10", "E10", "F10", "G10", "H10", "I10", "A14", "B14", "C14", "D14", "G14", "A19", "B19", "C19", "D19", "G19", "B20", "C20", "D20", "E20", "F20", "G20", "H20", "I20", "A4", "A5" };
        Assert.Equal(sWas.Keys, sNow.Keys);
        static string First(string x, string pattern, string by) => new Regex(pattern).Replace(x, by, 1);
        static string Bare(string xf) => First(First(First(First(xf, "\\s*applyAlignment=\"1\"", ""), "<alignment\\b[^>]*/>|<alignment\\b[^>]*>", ""), "/>$", "></xf>"), "></xf>$", "></xf>");
        foreach (var (reference, s) in sNow)
        {
            if (!filled.Contains(reference) || reference is "A4" or "A5") { Assert.True(s == sWas[reference], reference); continue; }
            string copy = xfsNow[int.Parse(s)], own = xfsWas[int.Parse(sWas[reference])];
            Assert.True(int.Parse(s) >= xfsWas.Count, reference);
            Assert.True(new Regex("<alignment\\b[^>]*/>|<alignment\\b[^>]*>").Replace(new Regex("\\s*applyAlignment=\"1\"").Replace(copy, "", 1), "", 1) == Bare(own), reference);
        }
        Assert.DoesNotMatch("<f>", sheet);
        Assert.Matches("<c r=\"B14\" s=\"[0-9]+\" t=\"inlineStr\"><is><t xml:space=\"preserve\">=1\\+1 Re \\(გამოგონილი\\)</t></is></c>", sheet);
        Assert.Matches("007 Re \"A&amp;B\" &lt;test&gt;", sheet);
        var back = XlsxReader.Read(output);
        Assert.Equal("=1+1 Re (გამოგონილი)", back.Sheets[Form6Sheet]["B14"]!.V);
        Assert.Equal('s', back.Sheets[Form6Sheet]["B14"]!.T);
        Assert.Equal("007 Re \"A&B\" <test> (გამოგონილი)", back.Sheets[Form6Sheet]["B9"]!.V);
        Assert.Empty(wb.PrecisionProblems());
    }

    // The owner (20.10.2026): the grid is the supervisor's starting form, not a limit — „ემატება უჯრები ქვევით"; one row per class ×
    // reinsurer × treaty („9 განსხვავებული ვადა … 9 raw"), none merged or left out.
    [Fact(DisplayName = "the template, filled — a class with more than five: its first five in its group, the rest in rows added below the grid, each with its class; the footer and the print area move down")]
    public void ClassWithMoreThanFive()
    {
        var s = Fictional();
        for (var i = 0; i < 6; i++) s.Treaties.Add(Treaty($"x{i}", $"RI-1{i}", "quota_share", "2026-01-01", "2026-12-31", $"Re {i} (გამოგონილი)"));
        for (var i = 0; i < 6; i++) s.TreatyClasses.Add(new($"x{i}", "5.1"));
        var rows = Rows(s);
        Assert.Equal(8, rows.Count(r => r.ClassCode == "5.1"));
        AssertOverflow(rows, ["05.1"], 3);
        var wb = FillForm6(Template, rows, "2026-12-31", "x");
        var five = rows.Where(r => r.ClassCode == "5.1").ToList();
        Assert.Equal(five.Take(5).Select(r => r.Cells["B"]), new[] { 9, 10, 11, 12, 13 }.Select(r => Ts(wb, $"B{r}")));
        Assert.Equal(five.Skip(5).Select(r => ((string?)"05.1 კასკო", (string?)r.Cells["B"], (string?)r.Cells["C"], (string?)r.Cells["D"])), new[] { 39, 40, 41 }.Select(r => (Ts(wb, $"A{r}"), Ts(wb, $"B{r}"), Ts(wb, $"C{r}"), Ts(wb, $"D{r}"))));
        Assert.Equal(new string?[] { "06.1 MTPL", "13 ქონება" }, new[] { Ts(wb, "A14"), Ts(wb, "A19") });
        // The footer: 42 → 45, 43 → 46, 46 → 49, 47 → 50; the print area A1:I47 → A1:I50; the dimension too; the grid's merges as they were.
        Assert.Equal([wb.TemplateText(Form6Sheet, "A45"), wb.TemplateText(Form6Sheet, "A46"), wb.TemplateText(Form6Sheet, "A49"), wb.TemplateText(Form6Sheet, "A50"), null], new[] { "A45", "A46", "A49", "A50", "A42" }.Select(r => Ts(wb, r)));
        Assert.StartsWith("გენერალური დირექტორი", Ts(wb, "A45"), StringComparison.Ordinal);
        var output = wb.ToBuffer();
        var xml = SheetXml(output);
        Assert.Equal("'გადამზღვევლები და ბროკერები'!$A$1:$I$50", PrintArea(output));
        Assert.Matches("<dimension ref=\"A1:I50\"/>", xml);
        Assert.Equal(["A14:A18", "A19:A23", "A24:A28", "A29:A33", "A34:A38", "A9:A13"], Merges(xml));
        // The added rows styled as a row of the grid (row 37): a cell for every column; the height of their two lines (the dates in IV).
        foreach (var r in new[] { 39, 40, 41 }) Assert.Matches($"<row r=\"{r}\" spans=\"1:9\" s=\"35\" customFormat=\"1\" ht=\"{2 * LinePoints}\" customHeight=\"1\">", xml);
        static List<int> RowsOf(string x) => Regex.Matches(x, "<row r=\"([0-9]+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var templateRows = RowsOf(SheetXml(Template));
        Assert.Equal(templateRows.Where(r => r <= 38).Concat([39, 40, 41]).Concat(templateRows.Where(r => r > 38).Select(r => r + 3)), RowsOf(xml));
        // Form 7 takes form 6's company and date (A4, A5: new results) and its signature lines, which moved with the footer: its references
        // to A42, A43, A46, A47 now name A45, A46, A49, A50 — as the spreadsheet itself moves them on inserting rows; nothing else changed.
        var was = FinancialAnnexTests.CellsOf(SheetXml(Template, "xl/worksheets/sheet2.xml"));
        var now = FinancialAnnexTests.CellsOf(SheetXml(output, "xl/worksheets/sheet2.xml"));
        Assert.Equal(["A4", "A5", "A34", "A35", "A38", "A39"], now.Where(p => was.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key));
        string F(string r) => Regex.Match(now[r], "<f>([^<]*)</f>").Groups[1].Value;
        Assert.Equal(["'გადამზღვევლები და ბროკერები'!A45", "'გადამზღვევლები და ბროკერები'!A46", "'გადამზღვევლები და ბროკერები'!A49", "'გადამზღვევლები და ბროკერები'!A50"], new[] { "A34", "A35", "A38", "A39" }.Select(F));
        foreach (var r in new[] { "A34", "A35", "A38", "A39" }) Assert.True(Regex.Replace(now[r], "<f>[^<]*</f>", "") == Regex.Replace(was[r], "<f>[^<]*</f>", ""), r);
        var back = XlsxReader.Read(output).Sheets[Form6Sheet];
        Assert.Equal(["05.1 კასკო", five[7].Cells["B"], Ts(wb, "A45")], new[] { back["A39"]?.V, back["B41"]?.V, back["A45"]?.V });
        Assert.Empty(wb.PrecisionProblems());
    }

    [Fact(DisplayName = "the template, filled — more than six classes: the six first by the reference's order in the grid, the rest below, each row once with its class")]
    public void MoreThanSixClasses()
    {
        var eight = Fictional();
        foreach (var code in new[] { "1", "2", "3", "7", "8" }) { eight.TreatyClasses.Add(new("t1", code)); eight.Classes.Add(new(code, $"სახეობა {code}", int.Parse(code) * 100)); }
        var rows = Rows(eight);
        AssertOverflow(rows, ["08", "13"], 3);
        var wb = FillForm6(Template, rows, "2026-12-31", "x");
        Assert.Equal(["01 სახეობა 1", "02 სახეობა 2", "03 სახეობა 3", "05.1 კასკო", "06.1 MTPL", "07 სახეობა 7"], new[] { 9, 14, 19, 24, 29, 34 }.Select(r => Ts(wb, $"A{r}")));
        Assert.Equal([("08 სახეობა 8", "Munich Re (გამოგონილი)"), ("13 ქონება", "=1+1 Re (გამოგონილი)"), ("13 ქონება", "Munich Re (გამოგონილი)")], new[] { 39, 40, 41 }.Select(r => (Ts(wb, $"A{r}"), Ts(wb, $"B{r}"))));
        // Every row of the source is in the file once.
        var written = Enumerable.Range(9, 30).Concat([39, 40, 41]).Select(r => Ts(wb, $"B{r}")).Where(x => x is not null).ToList();
        Assert.Equal(rows.Count, written.Count);
        Assert.Equal("'გადამზღვევლები და ბროკერები'!$A$1:$I$50", PrintArea(wb.ToBuffer()));
    }

    /// <summary>`perClass` rows for each class code given, each a treaty with its own reinsurer name (Re &lt;code&gt;/&lt;n&gt;) so every row is told apart.</summary>
    private static Source Many(params (string Code, int N)[] perClass)
    {
        var s = new Source();
        foreach (var (code, n) in perClass)
        {
            s.Classes.Add(new(code, $"სახეობა {code}", int.Parse(code) * 10));
            for (var i = 0; i < n; i++)
            {
                var id = $"t{code}-{i}";
                s.Treaties.Add(Treaty(id, $"RI-{code}-{i:000}", "quota_share", "2026-01-01", "2026-12-31", $"Re {code}/{i:000} (გამოგონილი)"));
                s.TreatyClasses.Add(new(id, code));
            }
        }
        return s;
    }
    private static List<(int Row, string? Cls, string Re)> FilledRows(TemplateWorkbook wb, int last)
    {
        var outp = new List<(int, string?, string)>();
        string? cls = null;
        for (var r = 9; r <= last; r++)
        {
            if (r <= 38 && (r - 9) % 5 == 0) cls = Ts(wb, $"A{r}");
            var re = Ts(wb, $"B{r}");
            if (re is not null) outp.Add((r, r <= 38 ? cls : Ts(wb, $"A{r}"), re));
        }
        return outp;
    }

    [Fact(DisplayName = "the template, filled — several classes with more than five at once: each class's first five in its group, all their extra rows below, in class order")]
    public void SeveralClassesWithMoreThanFive()
    {
        var rows = Rows(Many(("11", 8), ("12", 7), ("13", 6), ("14", 2)));
        AssertOverflow(rows, ["11", "12", "13"], 6);
        var wb = FillForm6(Template, rows, "2026-12-31", "x");
        var got = FilledRows(wb, 44);
        Assert.Equal([
            (39, "11 სახეობა 11", "Re 11/005 (გამოგონილი)"), (40, "11 სახეობა 11", "Re 11/006 (გამოგონილი)"), (41, "11 სახეობა 11", "Re 11/007 (გამოგონილი)"),
            (42, "12 სახეობა 12", "Re 12/005 (გამოგონილი)"), (43, "12 სახეობა 12", "Re 12/006 (გამოგონილი)"), (44, "13 სახეობა 13", "Re 13/005 (გამოგონილი)"),
        ], got.Where(g => g.Row >= 39).Select(g => (g.Row, g.Cls, g.Re)));
        Assert.Equal(rows.Select(r => r.Cells["B"]).Order(StringComparer.Ordinal), got.Select(g => g.Re).Order(StringComparer.Ordinal));
        // Headings on every printed page: rows 7–8, as print titles, beside the moved print area.
        var book = SheetXml(wb.ToBuffer(), "xl/workbook.xml");
        Assert.Matches("<definedName name=\"_xlnm\\.Print_Area\" localSheetId=\"0\">'გადამზღვევლები და ბროკერები'!\\$A\\$1:\\$I\\$53</definedName><definedName name=\"_xlnm\\.Print_Titles\" localSheetId=\"0\">'გადამზღვევლები და ბროკერები'!\\$7:\\$8</definedName>", book);
    }

    [Fact(DisplayName = "the template, filled — several hundred rows: 300 over twelve classes — every row once, by class, the grid then 270 below; the page count of rows = the file's")]
    public void SeveralHundredRows()
    {
        var rows = Rows(Many(Enumerable.Range(0, 12).Select(c => ((20 + c).ToString(), 25)).ToArray()));
        Assert.Equal(300, rows.Count);
        Assert.Equal(270, Form6Overflow(rows).Rows);
        var wb = FillForm6(Template, rows, "2026-12-31", "x");
        var got = FilledRows(wb, 38 + 270);
        Assert.Equal(300, got.Count);
        Assert.Equal(300, got.Select(g => g.Re).Distinct().Count());
        foreach (var g in got) Assert.True(g.Cls!.Split(' ')[0] == g.Re[3..5], $"{g.Row} {g.Re}");
        // Every row of the source in the file once, and nothing else; the footer after the last added row; the print area to it.
        Assert.Equal(rows.Select(r => r.Cells["B"]).Order(StringComparer.Ordinal), got.Select(g => g.Re).Order(StringComparer.Ordinal));
        Assert.StartsWith("გენერალური დირექტორი", Ts(wb, "A312") ?? "", StringComparison.Ordinal);
        Assert.Equal("'გადამზღვევლები და ბროკერები'!$A$1:$I$317", PrintArea(wb.ToBuffer()));
    }

    [Fact(DisplayName = "the template, filled — fits the grid: no rows added, yet the headings repeat on every printed page — fitting the grid is not printing on one page")]
    public void FitsTheGrid()
    {
        // Codex PR358_FORM6_MULTIPAGE_PRINT_HEADERS_20261020: 6 classes × 5 rows printed on two pages in Excel, the second without I–IX.
        var output = FillForm6(Template, Rows(Fictional()), "2026-12-31", "x").ToBuffer();
        string book = SheetXml(output, "xl/workbook.xml"), blank = SheetXml(Template, "xl/workbook.xml");
        const string titles = "<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">'გადამზღვევლები და ბროკერები'!$7:$8</definedName>";
        Assert.Contains(titles, book);
        // The rest of the workbook part as the template has it: the same print areas (not moved: no rows added), form 7's names untouched.
        var calc = new Regex("<calcPr\\b[^>]*/>");
        Assert.Equal(calc.Replace(blank, "", 1), calc.Replace(book.Replace(titles, ""), "", 1));
        Assert.True(!blank.Contains("Print_Titles"), "the official template itself has none");
    }

    [Fact(DisplayName = "the template, filled — the lines of a wrapped text: never fewer than Excel shows (native Excel, column VIII: the broker's name on 6 lines)")]
    public void LinesOfAWrappedText()
    {
        // Native Excel 16 wrapped this name in column H (23.3 wide) on 6 lines; a row of Excel's own height (6 × 13.5 = 81) printed its
        // first line cut (Codex PR358_FORM6_MULTIPAGE_PRINT_HEADERS_20261020, the long-name print check).
        const string broker = "ძალიან გრძელი დასახელების ფიქტიური გადამზღვეველი სააქციო საზოგადოება (გამოგონილი) — სრული სახელი ბროკერი 11/0";
        Assert.True(WrappedLines(broker, Widths["H"]) >= 6);
        Assert.True(WrappedLines(broker, Widths["H"]) * LinePoints > 81);
        Assert.Equal(2, WrappedLines("01.01.2026 – 31.12.2026", Widths["D"]));
        Assert.Equal(1, WrappedLines("GB", Widths["I"]));
        Assert.Equal(1, WrappedLines("", Widths["I"]));
        // A word longer than its line takes the lines it fills.
        Assert.Equal(3, WrappedLines(new string('x', 41), 20));
    }

    [Fact(DisplayName = "the template, filled — a long name wraps in its cell, and its row has the height of its lines: Excel's own height cuts the first line in print")]
    public void LongNameWraps()
    {
        var s = Fictional();
        var name = $"{string.Concat(Enumerable.Repeat("ძალიან გრძელი გადამზღვევლის დასახელება ", 4))}(გამოგონილი)";
        s.Treaties[0] = s.Treaties[0] with { Reinsurer = name };
        var output = FillForm6(Template, Rows(s), "2026-12-31", "x").ToBuffer();
        var xml = SheetXml(output);
        var b = Regex.Match(xml, "<c r=\"B10\" s=\"([0-9]+)\"");
        var styles = Encoding.UTF8.GetString(Zip.EntryData(Zip.ReadZip(output).First(e => e.Name == "xl/styles.xml")));
        var xf = Regex.Matches(styles[styles.IndexOf("<cellXfs", StringComparison.Ordinal)..], "<xf\\b[^>]*?(?:/>|>[\\s\\S]*?</xf>)")[int.Parse(b.Groups[1].Value)].Value;
        Assert.Matches("wrapText=\"1\"", xf);
        var lines = WrappedLines(name, Widths["B"]);
        Assert.True(lines >= 6, lines.ToString());
        Assert.Matches($"<row r=\"10\" spans=\"1:9\" s=\"35\" customFormat=\"1\" ht=\"{lines * LinePoints}\" customHeight=\"1\">", xml);
        // A row of one line keeps Excel's own height: the first row, its texts made short, is given none.
        var shortRows = Rows(Fictional());
        var cells = new Dictionary<string, string>(shortRows[0].Cells) { ["B"] = "Re (გამოგონილი)", ["C"] = "XL", ["D"] = "2026" };
        shortRows[0] = shortRows[0] with { Cells = cells };
        Assert.Matches("<row r=\"9\" spans=\"1:9\" s=\"35\" customFormat=\"1\">", SheetXml(FillForm6(Template, shortRows, "2026-12-31", "x").ToBuffer()));
    }

    private static string BrokerName(int n) => string.Concat(Enumerable.Repeat("Fictional Broker Company ", (n + 24) / 25))[..n].TrimEnd();

    [Fact(DisplayName = "the template, filled — a text in pieces of at most 27 lines, cut between words: joined by a space, the text again; a word longer than a row, by its characters")]
    public void TextInPieces()
    {
        Assert.Equal(27, MaxRowLines);
        Assert.Equal(["Re (გამოგონილი)"], TextPieces("Re (გამოგონილი)", Widths["H"]));
        foreach (var (n, col) in new[] { (539, "H"), (600, "H"), (1200, "H"), (5000, "H"), (3000, "B"), (40000, "H") })
        {
            var pieces = TextPieces(BrokerName(n), Widths[col]);
            Assert.True(pieces.Count > 1, $"{n} {col}");
            Assert.True(string.Join(" ", pieces) == BrokerName(n), $"{n} {col}");
            foreach (var p in pieces) Assert.True(WrappedLines(p, Widths[col]) <= 27 && p == p.Trim() && p != "", $"{n} {col}");
            // Never more pieces than needed: two neighbours together would not fit one row.
            for (var i = 1; i < pieces.Count; i++) Assert.True(WrappedLines($"{pieces[i - 1]} {pieces[i]}", Widths[col]) > 27, $"{n} {col} {i}");
        }
        // One word of 3000 characters (no space to cut at): rows of its characters, in order.
        var word = new string('x', 3000);
        var wordPieces = TextPieces(word, Widths["H"]);
        Assert.Equal(word, string.Concat(wordPieces));
        foreach (var p in wordPieces) Assert.True(WrappedLines(p, Widths["H"]) <= 27);
        // A long word among short ones: the words around it in their places.
        var rejoined = TextPieces($"Re {word} Broker", Widths["H"]);
        Assert.Equal("Re", rejoined[0]);
        Assert.EndsWith(" Broker", rejoined[^1], StringComparison.Ordinal);
        Assert.Equal(word, Regex.Replace(string.Concat(rejoined.Skip(1)), " Broker$", ""));
    }

    [Fact(DisplayName = "the template, filled — a name taller than a row may be (409 pt): continued in the rows under its record, a piece in each, the whole name in order; in the grid its group grows (Codex PR358_FORM6_LONG_TEXT_ROW_HEIGHT_EXPORT_20261020)")]
    public void NameTallerThanARow()
    {
        // 05.1 with five: 007 Re, Munich, Re 0, Re 1, Re 2 (rows 9–13). Re 0's broker (600 characters, column VIII) needs two rows; Re 2's
        // (1200), the group's last, three.
        var s = Fictional();
        for (var i = 0; i < 3; i++) s.Treaties.Add(Treaty($"x{i}", $"RI-1{i}", "quota_share", "2026-01-01", "2026-12-31", $"Re {i} (გამოგონილი)", broker: i == 0 ? BrokerName(600) : i == 2 ? BrokerName(1200) : null));
        for (var i = 0; i < 3; i++) s.TreatyClasses.Add(new($"x{i}", "5.1"));
        var rows = Rows(s);
        Assert.Equal(["007 Re", "Munich", "Re 0 (", "Re 1 (", "Re 2 ("], rows.Where(r => r.ClassCode == "5.1").Select(r => r.Cells["B"][..6]));
        List<string> p600 = TextPieces(BrokerName(600), Widths["H"]), p1200 = TextPieces(BrokerName(1200), Widths["H"]);
        Assert.Equal((2, 3), (p600.Count, p1200.Count));
        var wb = FillForm6(Template, rows, "2026-12-31", "x");
        // Re 0 on 11–12, Re 1 on 13, Re 2 on 14–16 (the group's own last row now 16): the group is 9–16, the next ones three rows down.
        Assert.Equal(["007 Re", "Munich", "Re 0 (", "Re 1 (", "Re 2 ("], new[] { 9, 10, 11, 13, 14 }.Select(r => Ts(wb, $"B{r}")?[..6]));
        // The continuation rows hold the name's next pieces and nothing else.
        foreach (var r in new[] { 12, 15, 16 }) foreach (var c in new[] { "A", "B", "C", "D", "E", "F", "G", "I" }) Assert.True(Ts(wb, $"{c}{r}") is null, $"{c}{r}");
        Assert.Equal(p600, new[] { Ts(wb, "H11"), Ts(wb, "H12") });
        Assert.Equal(p1200, new[] { Ts(wb, "H14"), Ts(wb, "H15"), Ts(wb, "H16") });
        Assert.Equal(BrokerName(600), string.Join(" ", Ts(wb, "H11"), Ts(wb, "H12")));
        Assert.Equal(BrokerName(1200), string.Join(" ", Ts(wb, "H14"), Ts(wb, "H15"), Ts(wb, "H16")));
        Assert.Equal(new string?[] { "05.1 კასკო", "06.1 MTPL", "13 ქონება" }, new[] { Ts(wb, "A9"), Ts(wb, "A17"), Ts(wb, "A22") });
        var output = wb.ToBuffer();
        var xml = SheetXml(output);
        // No cell merged over a record: only the class labels' merges, the first grown by three.
        Assert.Equal(["A17:A21", "A22:A26", "A27:A31", "A32:A36", "A37:A41", "A9:A16"], Merges(xml));
        // Each row as tall as its own piece's lines, never over 409.
        double Ht(int r) => double.Parse(Regex.Match(xml, $"<row r=\"{r}\"[^>]*\\bht=\"([0-9.]+)\" customHeight=\"1\"").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(p600.Concat(p1200).Select(p => (double)(WrappedLines(p, Widths["H"]) * LinePoints)), new[] { 11, 12, 14, 15, 16 }.Select(Ht));
        foreach (var r in new[] { 11, 12, 14, 15, 16 }) Assert.True(Ht(r) <= 409, r.ToString());
        // The group's bottom row (template row 13, its label cell's own style 56) is still its last; the rows added inside take the styles of
        // the row they follow.
        Assert.Matches("<c r=\"A16\" s=\"56\"/>", xml);
        foreach (var r in new[] { 12, 14, 15 }) Assert.Matches($"<c r=\"A{r}\" s=\"54\"/>", xml);
        // The footer and the print area three rows down; form 7's signature lines with them.
        Assert.Equal("'გადამზღვევლები და ბროკერები'!$A$1:$I$50", PrintArea(output));
        Assert.StartsWith("გენერალური დირექტორი", Ts(wb, "A45"), StringComparison.Ordinal);
        Assert.Matches("<f>'გადამზღვევლები და ბროკერები'!A45</f>", SheetXml(output, "xl/worksheets/sheet2.xml"));
        var back = XlsxReader.Read(output).Sheets[Form6Sheet];
        Assert.Equal([p600[0], p1200[2], "Re 1 (გამოგონილი)"], new[] { back["H11"]?.V, back["H16"]?.V, back["B13"]?.V });
    }

    [Fact(DisplayName = "the template, filled — the workbook grows a merge over rows inserted inside it only when asked; otherwise refuses")]
    public void GrowsAMergeOnlyWhenAsked()
    {
        var grow = TemplateWorkbook.Open(Template);
        grow.InsertRows(Form6Sheet, 10, 2, 10, growMerges: true);
        Assert.Equal(["A16:A20", "A21:A25", "A26:A30", "A31:A35", "A36:A40", "A9:A15"], Merges(SheetXml(grow.ToBuffer())));
        Assert.Matches("a merge across row 10", Assert.Throws<InvalidOperationException>(() => TemplateWorkbook.Open(Template).InsertRows(Form6Sheet, 10, 2, 10)).Message);
    }

    [Fact(DisplayName = "the template, filled — the workbook refuses to move what it does not know: a filled sheet, a formula sheet, a merge across")]
    public void RefusesToMoveWhatItDoesNotKnow()
    {
        var wb = TemplateWorkbook.Open(Template);
        Assert.Matches("a merge across row 10", Assert.Throws<InvalidOperationException>(() => wb.InsertRows(Form6Sheet, 10, 1, 37)).Message);
        Assert.Matches("has formulas", Assert.Throws<InvalidOperationException>(() => wb.InsertRows("სასამართლო დავები", 30, 1, 29)).Message);
        wb.SetText(Form6Sheet, "B9", "x");
        Assert.Matches("before any cell is filled", Assert.Throws<InvalidOperationException>(() => wb.InsertRows(Form6Sheet, 38, 1, 37)).Message);
        // A row's height: only a row the sheet has, of a height Excel takes; none set before rows are moved.
        var sized = TemplateWorkbook.Open(Template);
        Assert.Matches("Not a row height", Assert.Throws<ArgumentException>(() => sized.SetRowHeight(Form6Sheet, 9, 0)).Message);
        Assert.Matches("Not a row height", Assert.Throws<ArgumentException>(() => sized.SetRowHeight(Form6Sheet, 9, 410)).Message);
        Assert.Matches("has no row 400", Assert.Throws<InvalidOperationException>(() => sized.SetRowHeight(Form6Sheet, 400, 30)).Message);
        sized.SetRowHeight(Form6Sheet, 9, 30);
        Assert.Matches("before any cell is filled", Assert.Throws<InvalidOperationException>(() => sized.InsertRows(Form6Sheet, 38, 1, 37)).Message);
    }
}
