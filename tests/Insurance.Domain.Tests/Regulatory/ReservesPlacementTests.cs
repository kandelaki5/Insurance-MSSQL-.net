using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Application.Excel;
using Insurance.Domain.Regulatory;
using static Insurance.Domain.Regulatory.ReservesPlacement;

namespace Insurance.Domain.Tests.Regulatory;

// The supervisory reserves placement workbook filled from the monthly report (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019),
// checked on the official template itself: its identity, which cells are filled and which keep their formulas, every other byte as
// it was, the totals worked out by the template's own formulas against sums made here, and the file read back by another reader.
// (Port of src/lib/regulatory/reserves-placement.test.ts.)
public class ReservesPlacementTests
{
    private static readonly byte[] Template = Templates.ReservesTemplate();
    private static decimal T(string s) => (decimal)ExcelCentsTests.TextTetri(s);
    private static readonly decimal L = T("90071992547408.01"), X = T("9999999999999999.99");

    /// <summary>A month's report as buildPremiumReport gives it: CASCO (5.1) with the largest amounts, property (13) with ordinary ones and a USD
    /// reserve already at the month-end rate, MTPL (6.1) with a claim only.</summary>
    private static PremiumReport Report(IEnumerable<PremiumClassFigures>? moreClasses = null, IEnumerable<ClaimsClassFigures>? moreClaims = null)
    {
        var classes = new List<PremiumClassFigures> { new("5.1", X, L), new("13", T("300.00"), T("90.01")) };
        var claims = new List<ClaimsClassFigures> { new("5.1", L, T("0.01"), T("35.00")), new("13", T("252.00"), T("75.60"), T("10.50")), new("6.1", T("0.01"), 0, 0) };
        classes.AddRange(moreClasses ?? []);
        claims.AddRange(moreClaims ?? []);
        return new PremiumReport("2026-10-31", classes, new PremiumTotals(classes.Sum(c => c.UnearnedGel), classes.Sum(c => c.CededUnearnedGel)),
            claims, new ClaimsTotals(claims.Sum(c => c.RbnsGel), claims.Sum(c => c.ReinsurerRbnsGel), claims.Sum(c => c.IbnrGel)));
    }
    private static string? Text(TemplateWorkbook wb, string sheet, string reference) => wb.ValueOf(sheet, reference) is EmptyValue ? null : Formula.DecimalText(wb.ValueOf(sheet, reference));
    private static string Amount(decimal t) => Tetri.AmountText(t);
    private static string Hundredths(decimal n) => Formula.DecimalText(Formula.Num(new BigInteger(n), 100));
    private static string AmountPlus(decimal a, decimal b) => Hundredths(a + b);
    private static string Norm(string s) => Regex.Replace(Regex.Replace(s, "\\s+", " ").Trim(), ":$", "");

    /// <summary>The class reference's names (the old db/migrations/0001_foundation.sql, insurance_class), word for word.</summary>
    private static readonly Dictionary<string, string> ClassNames = new()
    {
        ["1"] = "სიცოცხლის დაზღვევა", ["1.1"] = "სიცოცხლის ვადიანი დაზღვევა", ["1.2"] = "სიცოცხლის უვადო დაზღვევა", ["1.3"] = "სიცოცხლის დაზღვევის მაგროვებადი და დაბრუნებადი სახეობები",
        ["1.4"] = "სიცოცხლის დაზღვევის სხვა ფორმები", ["2"] = "სამოგზაურო დაზღვევა", ["3"] = "უბედური შემთხვევის დაზღვევა", ["3.1"] = "დაზღვევა უბედური შემთხვევებისაგან",
        ["3.2"] = "მგზავრების, მძღოლის ან ეკიპაჟის დაზღვევა უბედური შემთხვევებისაგან", ["4"] = "სამედიცინო (ჯანმრთელობის) დაზღვევა",
        ["5"] = "სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა (გარდა სარკინიგზო ტრანსპორტისა)", ["5.1"] = "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)",
        ["5.2"] = "სხვა სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა", ["6"] = "სახმელეთო ტრანსპორტის გამოყენებასთან დაკავშირებული სამოქალაქო პასუხისმგებლობის დაზღვევა",
        ["6.1"] = "ავტომოტოტრანსპორტის მფლობელთა სამოქალაქო პასუხისმგებლობის სავალდებულო დაზღვევა", ["6.2"] = "სახმელეთო ტრანსპორტის გამოყენებასთან დაკავშირებული სამოქალაქო პასუხისმგებლობის დაზღვევა",
        ["6.3"] = "სახმელეთო გადამზიდველის სამოქალაქო პასუხისმგებლობის დაზღვევა", ["7"] = "სარკინიგზო სატრანსპორტო საშუალებათა დაზღვევა", ["8"] = "საჰაერო სატრანსპორტო საშუალებათა დაზღვევა (კორპუსის დაზღვევა)",
        ["9"] = "საჰაერო სატრანსპორტო საშუალებათა გამოყენებასთან დაკავშირებული პასუხისმგებლობის დაზღვევა", ["9.1"] = "საჰაერო გადამზიდველის პასუხისმგებლობის დაზღვევა",
        ["9.2"] = "სხვა პასუხისმგებლობის დაზღვევა, რომელიც დაკავშირებულია საჰაერო ტრანსპორტის გამოყენებასთან", ["10"] = "მცურავი სატრანსპორტო საშუალებების დაზღვევა (კორპუსის დაზღვევა)",
        ["11"] = "მცურავ სატრანსპორტო საშუალებათა გამოყენებასთან დაკავშირებული პასუხისმგებლობის დაზღვევა", ["11.1"] = "საზღვაო გადამზიდველის პასუხისმგებლობის დაზღვევა",
        ["11.2"] = "სხვა პასუხისმგებლობის დაზღვევა, რომელიც დაკავშირებულია მცურავი ტრანსპორტის გამოყენებასთან", ["12"] = "ტვირთების დაზღვევა",
        ["13"] = "ქონების დაზღვევა (გარდა პპ. (5), (7), (8), (10) და (12)-ში ჩამოთვლილი ქონებისა)", ["14"] = "დაზღვევა საფინანსო დანაკარგებისაგან", ["15"] = "ვალდებულებათა შესრულების დაზღვევა",
        ["15.1"] = "საბაჟო ვალდებულებების შესრულების გარანტიები/ფინანსური რისკის დაზღვევის პოლისები", ["15.2"] = "სახელმწიფო შესყიდვებთან დაკავშირებული გარანტიები", ["15.3"] = "სხვა სახის გარანტიები",
        ["16"] = "საკრედიტო ვალდებულებათა დაზღვევა", ["17"] = "სამოქალაქო პასუხისმგებლობის დაზღვევა (გარდა პპ. (6), (9), (11) სახეობებში ჩამოთვლილი პასუხისმგებლობისა)",
        ["17.1"] = "პროფესიული პასუხისმგებლობის დაზღვევა", ["17.2"] = "დამქირავებლის პასუხისმგებლობის დაზღვევა", ["17.3"] = "სხვა სახის პასუხისმგებლობის დაზღვევა", ["18"] = "იურიდიული ხარჯების დაზღვევა",
    };

    [Fact(DisplayName = "the reserves placement template: the official one, as it is — its hash, its sixteen sheets; form 11's class rows in the order and with the names of the class reference")]
    public void TheTemplate()
    {
        Assert.Equal(ReservesTemplateSha256, TemplateHash(Template));
        var wb = TemplateWorkbook.Open(Template);
        Assert.Equal([Form11, Form12, "დასაშვები აქტივების სტრუქტურა", "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9", "A10", "A11", "A12", "ინვესტირება"], wb.SheetNames());
        foreach (var (code, row) in Form11AllRows)
        {
            if (code == "19") { Assert.Equal("საპენსიო დაზღვევა", Norm(wb.TemplateText(Form11, $"B{row}"))); continue; }
            // The one difference: the template has a comma after "(10)" that the class reference does not.
            if (code == "13") { Assert.Equal(Norm(ClassNames[code]).Replace("(10) და", "(10), და"), Norm(wb.TemplateText(Form11, $"B{row}"))); continue; }
            Assert.True(Norm(ClassNames[code]) == Norm(wb.TemplateText(Form11, $"B{row}")), $"row {row}, class {code}");
            if (!code.Contains('.')) Assert.True(code.PadLeft(2, '0') == wb.TemplateText(Form11, $"A{row}"), $"row {row}");
        }
    }

    [Fact(DisplayName = "the reserves placement template: the filled rows are the classes without subclasses: their cells C–G are input cells; every parent row and the total are formulas")]
    public void FilledRowsAreInputCells()
    {
        var wb = TemplateWorkbook.Open(Template);
        var filled = Form11Rows.Select(r => r.Row).ToHashSet();
        foreach (var (code, row) in Form11AllRows)
        {
            var parent = Form11AllRows.Any(r => r.Code.StartsWith($"{code}.", StringComparison.Ordinal));
            foreach (var col in Form11Columns) Assert.True(wb.IsFormula(Form11, $"{col}{row}") == parent, $"{col}{row}");
            Assert.True(filled.Contains(row) == (!parent && !code.StartsWith("1.", StringComparison.Ordinal) && code != "19"), $"row {row}");
        }
        foreach (var col in Form11Columns) Assert.True(wb.IsFormula(Form11, $"{col}{Form11TotalRow}"));
        foreach (var r in Form12EmptyInputs) Assert.False(wb.IsFormula(Form12, r), r);
    }

    [Fact(DisplayName = "filled from the month's report — form 11: each class's five figures to the tetri, a class with none 0.00, the life and pension rows and columns empty")]
    public void Form11Figures()
    {
        var (wb, date) = FillReservesPlacement(Template, Report(), "სატესტო კომპანია (გამოგონილი)");
        Assert.Equal("2026-10-31", date);
        Assert.Equal(["9999999999999999.99", Amount(X - L), "90071992547408.01", "90071992547408", "35"], new[] { "C19", "D19", "E19", "F19", "G19" }.Select(r => Text(wb, Form11, r)));
        Assert.Equal(["300", "209.99", "252", "176.4", "10.5"], new[] { "C35", "D35", "E35", "F35", "G35" }.Select(r => Text(wb, Form11, r)));
        Assert.Equal(["0", "0", "0.01", "0.01", "0"], new[] { "C22", "D22", "E22", "F22", "G22" }.Select(r => Text(wb, Form11, r)));
        // A class the program has nothing for: a real zero, written as 0.00.
        Assert.Equal(["0", "0"], new[] { "C13", "G46" }.Select(r => Text(wb, Form11, r)));
        Assert.True(wb.Filled(Form11, "C13"));
        // Life (rows 9–12), pension (47) and the life and pension reserve columns (H–J): not kept — left empty, not 0.
        foreach (var r in new[] { "C9", "G12", "C47", "H19", "I35", "J47" }) { Assert.False(wb.Filled(Form11, r), r); Assert.True(Text(wb, Form11, r) is null, r); }
        foreach (var r in Form12EmptyInputs) Assert.True(Text(wb, Form12, r) is null, r);
        Assert.Equal("კომპანიის დასახელება: სატესტო კომპანია (გამოგონილი)", ((StrValue)wb.ValueOf(Form11, "B4")).S);
        Assert.Equal("საანგარიშო თარიღი: 31.10.2026", ((StrValue)wb.ValueOf(Form11, "B5")).S);
    }

    [Fact(DisplayName = "filled from the month's report — the template's totals and form 12's lines, worked out from its own formulas, equal the sums made here and the report's totals")]
    public void TotalsAndForm12()
    {
        var r = Report();
        var wb = FillReservesPlacement(Template, r, "x").Workbook;
        var figures = Form11FiguresOf(r);
        decimal Sum(string k) => figures.Sum(f => f.Figures[k]);
        Assert.Equal(Form11Columns.Select(c => Hundredths(Sum(c))), Form11Columns.Select(c => Text(wb, Form11, $"{c}{Form11TotalRow}")));
        Assert.Equal(r.Totals.UnearnedGel, Sum("C"));
        Assert.Equal(r.Totals.CededUnearnedGel, Sum("C") - Sum("D"));
        Assert.Equal(r.ClaimsTotals.RbnsGel, Sum("E"));
        Assert.Equal(r.ClaimsTotals.ReinsurerRbnsGel, Sum("E") - Sum("F"));
        Assert.Equal(r.ClaimsTotals.IbnrGel, Sum("G"));
        // A parent row: CASCO's 5 = 5.1 + 5.2.
        Assert.Equal("9999999999999999.99", Text(wb, Form11, "C18"));
        // Form 12: the investable UPR is 75 % of the net UPR (nothing else is deducted: no source), RBNS and IBNR net as they are.
        var netUpr = new BigInteger(Sum("D"));
        Assert.Equal(Formula.DecimalText(Formula.Num(netUpr * 75, 10_000)), Text(wb, Form12, "D7"));
        Assert.Equal(Hundredths(Sum("F")), Text(wb, Form12, "D15"));
        Assert.Equal(Hundredths(Sum("G")), Text(wb, Form12, "D23"));
        Assert.Equal(Formula.DecimalText(Formula.Num(netUpr * 75 + (new BigInteger(Sum("F")) + new BigInteger(Sum("G"))) * 100, 10_000)), Text(wb, Form12, "D35"));
    }

    [Fact(DisplayName = "filled from the month's report — the file: only form 11's filled cells and the formulas' results differ; every formula, style and other part byte for byte; another reader reads the same")]
    public void TheFile()
    {
        var output = FillReservesPlacement(Template, Report(), "x").Workbook.ToBuffer();
        var before = Zip.ReadZip(Template).ToDictionary(e => e.Name);
        var after = Zip.ReadZip(output);
        Assert.Equal(before.Keys, after.Select(e => e.Name));
        foreach (var e in after)
        {
            var was = before[e.Name];
            if (e.Name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) || e.Name == "xl/workbook.xml") continue;
            Assert.True(e.Compressed.AsSpan().SequenceEqual(was.Compressed) && e.Crc == was.Crc, e.Name);
        }
        // workbook.xml: only the calculation on opening is asked for.
        Assert.Equal(Encoding.UTF8.GetString(Zip.EntryData(before["xl/workbook.xml"])), Encoding.UTF8.GetString(Zip.EntryData(after.First(e => e.Name == "xl/workbook.xml"))).Replace(" fullCalcOnLoad=\"1\"", ""));
        var filled = new HashSet<string> { "B4", "B5" };
        foreach (var (_, row) in Form11Rows) foreach (var c in Form11Columns) filled.Add($"{c}{row}");
        static string Strip(string x) => Regex.Replace(x, "<c\\b[^>]*?(?:/>|>[\\s\\S]*?</c>)", "<c/>");
        foreach (var e in after.Where(x => x.Name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)))
        {
            string xmlBefore = Encoding.UTF8.GetString(Zip.EntryData(before[e.Name])), xmlAfter = Encoding.UTF8.GetString(Zip.EntryData(e));
            Assert.True(Strip(xmlAfter) == Strip(xmlBefore), $"{e.Name}: only cells change");
            var was = FinancialAnnexTests.CellsOf(xmlBefore);
            foreach (var (reference, cell) in FinancialAnnexTests.CellsOf(xmlAfter))
            {
                var old = was[reference];
                if (cell == old) continue;
                var f = Regex.Match(old, "<f\\b[^>]*?(?:/>|>[\\s\\S]*?</f>)");
                if (f.Success) { Assert.True(cell.Contains(f.Value, StringComparison.Ordinal), $"{e.Name} {reference}: its formula kept"); continue; }
                Assert.True(e.Name == "xl/worksheets/sheet1.xml" && filled.Contains(reference), $"{e.Name} {reference}: changed but not filled");
                Assert.True(Regex.Match(cell, "\\bs=\"([^\"]+)\"").Groups[1].Value == Regex.Match(old, "\\bs=\"([^\"]+)\"").Groups[1].Value, $"{reference}: its style kept");
            }
        }
        // The exact decimal text as written, and another reader (SheetJS's port) reading the same numbers and formulas.
        var sheet1 = Encoding.UTF8.GetString(Zip.EntryData(after.First(e => e.Name == "xl/worksheets/sheet1.xml")));
        Assert.Matches("<c r=\"C19\" s=\"[0-9]+\"><v>9999999999999999\\.99</v></c>", sheet1);
        var back = XlsxReader.Read(output);
        var ws = back.Sheets[Form11];
        Assert.Equal(300d, ws["C35"]!.V);
        Assert.Equal(176.4, ws["F35"]!.V);
        Assert.Null(ws["C9"]);
        Assert.Equal("C8+C13+C14+C17+C18+C21+C25+C26+C27+C30+C31+C34+C35+C36+C37+C41+C42+C46", ws["C48"]!.F);
        Assert.Equal("რეზერვები!C48", back.Sheets[Form12]["D8"]!.F);
    }

    [Fact(DisplayName = "filled from the month's report — figures booked on 5: in 05.1 (owner, 19.10.2026) — the totals as the report's")]
    public void FiguresBookedOnFive()
    {
        Assert.Equal(new Dictionary<string, string> { ["5"] = "5.1" }, PlacedIn);
        var r = Report([new PremiumClassFigures("5", T("1.00"), T("0.30"))], [new ClaimsClassFigures("5", T("2.00"), 0, T("0.05"))]);
        var wb = FillReservesPlacement(Template, r, "x").Workbook;
        Assert.Empty(UnplacedClasses(r));
        Assert.Equal([AmountPlus(X, T("1.00")), AmountPlus(X - L, T("0.70")), AmountPlus(L, T("2.00")), "35.05"], new[] { "C19", "D19", "E19", "G19" }.Select(x => Text(wb, Form11, x)));
        foreach (var (c, total) in new[] { ("C", r.Totals.UnearnedGel), ("E", r.ClaimsTotals.RbnsGel), ("G", r.ClaimsTotals.IbnrGel) })
            Assert.True(Hundredths(total) == Text(wb, Form11, $"{c}{Form11TotalRow}"), c);
    }

    [Fact(DisplayName = "filled from the month's report — figures booked on another class the template divides (6): not placed — named, and the totals differ from the report's by exactly them")]
    public void FiguresBookedOnSix()
    {
        var r = Report([new PremiumClassFigures("6", T("1.00"), 0)]);
        var wb = FillReservesPlacement(Template, r, "x").Workbook;
        Assert.Equal([("6", T("1.00"), 0m)], UnplacedClasses(r).Select(x => (x.Code, x.Figures.C, x.Figures.E)));
        Assert.Equal("0", Text(wb, Form11, "C21"));
        Assert.Equal(Hundredths(r.Totals.UnearnedGel - T("1.00")), Text(wb, Form11, $"C{Form11TotalRow}"));
        Assert.Empty(UnplacedClasses(Report()));
    }

    [Fact(DisplayName = "filled from the month's report — another template than the confirmed one: refused, never filled")]
    public void AnotherTemplate()
    {
        var other = (byte[])Template.Clone();
        other[^30] ^= 1;
        Assert.Contains("not the confirmed one", Assert.Throws<InvalidOperationException>(() => FillReservesPlacement(other, Report(), "x")).Message);
    }
}
