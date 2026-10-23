using System.Globalization;
using System.Text;
using Insurance.Domain.Regulatory;

namespace Insurance.Domain.Tests.Regulatory;

// Excel keeps numbers as doubles (INS-REGULATORY-RESERVES-XLSX-NATIVE-PRECISION-FIX-20261019; Codex PR349_REGULATORY_XLSX_NATIVE_PRECISION_
// 20261019): 90071992547408.01 is …408.02 to it once it recalculates. The filled form is given out only when Excel keeps every figure
// of it — the filled amounts and every formula over them — to the figure's own last decimal place; else a plain refusal (422).
// The doubles below are what Excel 16 itself held after CalculateFullRebuild of these very workbooks (COM, 19.10.2026, read as
// Value2 to its last bit): the program's own evaluation in doubles (Formula.EvaluateDouble) gives each of them bit for bit.
// (Port of src/lib/regulatory/excel-precision.test.ts.)
public class ExcelPrecisionTests
{
    private static readonly byte[] Template = Templates.ReservesTemplate();
    private const string F11 = ReservesPlacement.Form11, F12 = ReservesPlacement.Form12, F13 = "დასაშვები აქტივების სტრუქტურა";

    private sealed record Case((string Ref, string Value)[] Inputs, (string Sheet, string Ref, string Held)[] Excel, string[] Refused);
    private static readonly Dictionary<string, Case> Cases = new()
    {
        // Codex's example: the amount and the total each a tetri off in Excel.
        ["big"] = new([("C19", "90071992547408.01"), ("C35", "252.01")], [(F11, "C19", "90071992547408.016"), (F11, "C48", "90071992547660.031"), (F12, "D7", "0")], [$"{F11}!C19", $"{F11}!C48"]),
        // numeric(18, 2)'s largest: 1E+16 to Excel.
        ["max"] = new([("C19", "9999999999999999.99")], [(F11, "C19", "1E+16"), (F11, "C48", "1E+16")], [$"{F11}!C19", $"{F11}!C48"]),
        // The boundary's two sides: 9 999 999 999 999.99 kept (and its sum with 0.01); 99 999 999 999 999.99 not.
        ["fifteenSum"] = new([("C19", "9999999999999.99"), ("C35", "0.01")], [(F11, "C48", "10000000000000")], []),
        ["edgeSum"] = new([("C19", "99999999999999.99"), ("C35", "0.01")], [(F11, "C19", "99999999999999.984"), (F11, "C48", "100000000000000")], [$"{F11}!C19"]),
        // Each amount kept, their total not: 50 000 000 000 000.01 + …03 = …04, held as …03.
        ["totalOnly"] = new([("C19", "50000000000000.01"), ("C35", "50000000000000.03")], [(F11, "C19", "50000000000000.008"), (F11, "C35", "50000000000000.031"), (F11, "C48", "100000000000000.03")], [$"{F11}!C48"]),
        // A negative and a positive: 0.01, held as 0.010009765625 — the tetri kept.
        ["negative"] = new([("C19", "-1234567890123.45"), ("C35", "1234567890123.46")], [(F11, "C48", "0.010009765625")], []),
        // Form 12's 75 % and form 13's shares to four places, over a net UPR of 123 456 789 012.38.
        ["quarter"] = new([("D19", "123456789012.37"), ("D35", "0.01")], [(F13, "E11", "13888888763.892748"), (F13, "G12", "2314814793.9821248"), (F12, "D7", "92592591759.284988")], []),
        // Ordinary amounts: Excel's 45.599999999991269 is 45.60 to the tetri.
        ["ordinary"] = new([("C19", "88127.62"), ("D19", "88082.32"), ("C35", "0.10"), ("C36", "0.20")], [(F11, "C48", "88127.92"), (F12, "D9", "45.599999999991269"), (F12, "D7", "66061.74")], []),
    };
    private static TemplateWorkbook Filled((string Ref, string Value)[] inputs)
    {
        var wb = TemplateWorkbook.Open(Template);
        foreach (var (reference, v) in inputs) wb.SetNumber(F11, reference, v);
        return wb;
    }

    [Theory(DisplayName = "what Excel keeps of a filled form: the program's doubles are Excel's own; refused exactly where Excel would change a figure")]
    [InlineData("big")]
    [InlineData("max")]
    [InlineData("fifteenSum")]
    [InlineData("edgeSum")]
    [InlineData("totalOnly")]
    [InlineData("negative")]
    [InlineData("quarter")]
    [InlineData("ordinary")]
    public void WhatExcelKeeps(string name)
    {
        var c = Cases[name];
        var wb = Filled(c.Inputs);
        foreach (var (sheet, reference, held) in c.Excel)
            Assert.True(wb.ExcelValue(sheet, reference) is XNum x && x.V == double.Parse(held, CultureInfo.InvariantCulture), $"{sheet}!{reference}");
        var problems = wb.PrecisionProblems().Select(p => $"{p.Sheet}!{p.Ref}").ToList();
        foreach (var cell in c.Refused) Assert.True(problems.Contains(cell), $"{cell} not refused: {string.Join(", ", problems)}");
        if (c.Refused.Length == 0) Assert.Empty(problems);
        // Every refused cell's figure differs from what Excel holds by half a tetri or more; the exact one is the program's.
        foreach (var p in wb.PrecisionProblems()) Assert.True(p.Exact != p.Excel, $"{p.Sheet}!{p.Ref}");
    }

    [Fact(DisplayName = "the refusal names the cells and what Excel would hold: 90071992547408.01 → …408.02, the total …660.02 → …660.03")]
    public void RefusalNamesTheCells()
    {
        var problems = Filled(Cases["big"].Inputs).PrecisionProblems();
        Assert.Equal(new PrecisionProblem(F11, "C19", "90071992547408.01", "90071992547408.02"), problems.First(p => p.Sheet == F11 && p.Ref == "C19"));
        Assert.Equal(new PrecisionProblem(F11, "C48", "90071992547660.02", "90071992547660.03"), problems.First(p => p.Sheet == F11 && p.Ref == "C48"));
    }

    [Fact(DisplayName = "the download — ordinary amounts: the file (200)")]
    public void DownloadOrdinary()
    {
        var r = RegulatoryExport.XlsxResponse(Filled(Cases["ordinary"].Inputs), "x.xlsx");
        Assert.Equal(200, r.Status);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.ContentType);
        Assert.Equal("attachment; filename=\"x.xlsx\"", r.ContentDisposition);
        Assert.Equal("PK", Encoding.ASCII.GetString(r.Body, 0, 2));
    }

    [Theory(DisplayName = "the download: no file — a plain refusal (422) naming the cells, never a file Excel would change")]
    [InlineData("big")]
    [InlineData("max")]
    [InlineData("totalOnly")]
    [InlineData("edgeSum")]
    public void DownloadRefused(string name)
    {
        var r = RegulatoryExport.XlsxResponse(Filled(Cases[name].Inputs), "x.xlsx");
        Assert.Equal(422, r.Status);
        Assert.Equal("text/plain; charset=utf-8", r.ContentType);
        Assert.Null(r.ContentDisposition);
        var body = Encoding.UTF8.GetString(r.Body);
        Assert.StartsWith("Excel-ის ფაილი არ გაიცა", body, StringComparison.Ordinal);
        foreach (var cell in Cases[name].Refused) Assert.Contains(cell, body);
    }

    [Fact(DisplayName = "the two forms filled from the reports — UPR 9 007 199 254 740 801 tetri: both forms refused; 1 234 567.89: both given")]
    public void TwoFormsFromTheReports()
    {
        static PremiumReport Report(decimal upr) => new("2026-10-31", [new PremiumClassFigures("13", upr, 0)], new PremiumTotals(upr, 0),
            [new ClaimsClassFigures("13", 25200, 0, 0)], new ClaimsTotals(25200, 0, 0));
        var impairment = new ImpairmentForN14("2026-10-31", []);
        var n1 = Templates.FinancialAnnexTemplate();
        PremiumReport big = Report(9007199254740801m), small = Report(123456789m);
        var none = new Dictionary<string, string?>();
        Assert.Contains(ReservesPlacement.FillReservesPlacement(Template, big, "x").Workbook.PrecisionProblems(), p => p.Sheet == F11 && p.Ref == "C35");
        Assert.Contains(FinancialAnnex.FillFinancialAnnex(n1, big, impairment, none).PrecisionProblems(), p => p.Sheet == "BS-R" && p.Ref == "E10");
        Assert.Empty(ReservesPlacement.FillReservesPlacement(Template, small, "x").Workbook.PrecisionProblems());
        Assert.Empty(FinancialAnnex.FillFinancialAnnex(n1, small, impairment, none).PrecisionProblems());
    }
}
