using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Application.Excel;
using Insurance.Domain.Regulatory;
using static Insurance.Domain.Regulatory.FinancialAnnex;

namespace Insurance.Domain.Tests.Regulatory;

// Form 1 — the financial reporting annex N1, its insurance part — filled from the monthly report and the impairment report
// (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019; owner 19.10.2026), checked on the official template itself.
// (Port of src/lib/regulatory/financial-annex.test.ts; "another reader" is Insurance.Application.Excel.XlsxReader, SheetJS's port.)
public class FinancialAnnexTests
{
    private static readonly byte[] Template = Templates.FinancialAnnexTemplate();
    private static decimal T(string s) => (decimal)ExcelCentsTests.TextTetri(s);
    private static readonly decimal X = T("9999999999999999.99");
    private static PremiumReport Report() => new("2026-10-31", [], new PremiumTotals(X, T("90071992547408.01")), [], new ClaimsTotals(T("252.00"), T("75.60"), T("0.01")));
    /// <summary>The impairment report's exact N1-4 totals (buildImpairmentTotalsExact's shape); the broker's beyond a double's integers.</summary>
    private static ImpairmentForN14 Impairment(string asOf = "2026-10-31") => new(asOf,
        [new("client", 1, 36500, 1825), new("broker", 1, 9007199254740801m, 0), new("agent", 1, 1, 1), new("all", 1, 9007199254777302m, 1826)]);
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["company_name"] = "სატესტო კომპანია (გამოგონილი)", ["company_address"] = "თბილისი, გამოგონილი ქ. 1", ["company_phone"] = "", ["company_email"] = "  ",
    };
    private static string? Text(TemplateWorkbook wb, string sheet, string reference) => wb.ValueOf(sheet, reference) switch
    {
        EmptyValue => null, NumValue v => Formula.DecimalText(v), StrValue s => s.S, BoolValue b => b.B ? "true" : "false", _ => null,
    };
    internal static Dictionary<string, string> CellsOf(string xml)
    {
        var map = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(xml, "<c\\b([^>]*?)(?:/>|>([\\s\\S]*?)</c>)")) map[Regex.Match(m.Groups[1].Value, "\\br=\"([^\"]+)\"").Groups[1].Value] = m.Value;
        return map;
    }
    private static string Tx(decimal t) => Formula.DecimalText(Formula.Num(new BigInteger(t), 100));

    [Fact(DisplayName = "the financial annex N1 template: the official one, as it is — its hash; the sheets; the cells filled are input cells; the balance's insurance lines are the template's formulas from the subforms")]
    public void TheTemplate()
    {
        Assert.Equal(N1TemplateSha256, ReservesPlacement.TemplateHash(Template));
        var wb = TemplateWorkbook.Open(Template);
        Assert.Equal(29, wb.SheetNames().Count);
        foreach (var s in new[] { "Info", BalanceSheet, ReservesSheet, ReceivablesSheet }) Assert.Contains(s, wb.SheetNames());
        foreach (var r in N17Inputs) { Assert.True(wb.HasCell(ReservesSheet, r), r); Assert.False(wb.IsFormula(ReservesSheet, r), r); }
        foreach (var r in N14Inputs) { Assert.True(wb.HasCell(ReceivablesSheet, r), r); Assert.False(wb.IsFormula(ReceivablesSheet, r), r); }
        foreach (var r in new[] { InfoCells.Name, InfoCells.Period, InfoCells.Address, InfoCells.Phone, InfoCells.Email }) { Assert.True(wb.HasCell("Info", r), r); Assert.False(wb.IsFormula("Info", r), r); }
        Assert.Equal(["00010", "00020", "00030", "00050", "00060"], new[] { "B10", "B11", "B12", "B16", "B17" }.Select(r => wb.TemplateText(ReservesSheet, r)));
        Assert.Equal(["00010", "00020", "00040", "00050", "00070", "00080"], new[] { "B10", "B11", "B13", "B14", "B16", "B17" }.Select(r => wb.TemplateText(ReceivablesSheet, r)));
        Assert.Equal("'BS-IR & RR'!E19", wb.FormulaOf(BalanceSheet, "E14"));
        Assert.Equal("'BS-R'!E18+'BS-R'!E38", wb.FormulaOf(BalanceSheet, "E20"));
        Assert.Equal("'BS-R'!E13+'BS-R'!E32", wb.FormulaOf(BalanceSheet, "E30"));
    }

    [Fact(DisplayName = "filled for a month: N1-7 from the month report's totals, N1-4 from the impairment report's; the prior year's column, life and reinsurance empty")]
    public void FilledForAMonth()
    {
        var wb = FillFinancialAnnex(Template, Report(), Impairment(), Settings);
        Assert.Equal(["9999999999999999.99", "252", "0.01", "90071992547408.01", "75.6"], N17Inputs.Select(r => Text(wb, ReservesSheet, r)));
        Assert.Equal(["365", "18.25", "90071992547408.01", "0", "0.01", "0.01"], N14Inputs.Select(r => Text(wb, ReceivablesSheet, r)));
        foreach (var r in new[] { "F10", "F11", "F12", "F16", "F17", "E28", "E31", "E35" }) Assert.True(Text(wb, ReservesSheet, r) is null, $"N1-7 {r}");
        foreach (var r in new[] { "F10", "F13", "E23", "E26", "E29", "E32" }) Assert.True(Text(wb, ReceivablesSheet, r) is null, $"N1-4 {r}");
        // Info: the settings that have a value, and the date; an empty or blank setting is not written.
        Assert.Equal("სატესტო კომპანია (გამოგონილი)", Text(wb, "Info", InfoCells.Name));
        Assert.Equal("31.10.2026", Text(wb, "Info", InfoCells.Period));
        Assert.Equal("თბილისი, გამოგონილი ქ. 1", Text(wb, "Info", InfoCells.Address));
        Assert.Null(Text(wb, "Info", InfoCells.Phone));
        Assert.Null(Text(wb, "Info", InfoCells.Email));
        // The printed headers of the three filled sheets: the label, then the company and the date (PR351_FORM1_PRINT_HEADERS_MISSING_20261019).
        foreach (var sheet in HeaderSheets)
        {
            Assert.False(wb.Filled(sheet, "B1"), sheet);
            Assert.Equal("სატესტო კომპანია (გამოგონილი)", Text(wb, sheet, "D1"));
            Assert.Equal("ანგარიშგების თარიღი: 31.10.2026", Text(wb, sheet, "B2"));
        }
        // Another sheet's header is not touched; a blank company name is not made up.
        Assert.False(wb.Filled("BS-C", "B1"));
        var blank = FillFinancialAnnex(Template, Report(), Impairment(), new Dictionary<string, string?> { ["company_name"] = "  " });
        Assert.False(blank.Filled(ReservesSheet, "D1"));
        Assert.Equal("ანგარიშგების თარიღი: 31.10.2026", Text(blank, ReservesSheet, "B2"));
    }

    // Insurance-Software PR #362 (head 540e81a) scripts/integration/ri-commission.test.ts L160–170: N1-4 00140 (BS-IR & RR E26), an input
    // cell of the template, filled with the reinsurer's commission receivable when given (300.00 USD × 2.7015 = 810.45); empty when not.
    [Fact(DisplayName = "form 1: 00140 (BS-IR & RR E26) filled with the reinsurer's commission receivable; left empty when none is given")]
    public void RiCommission00140()
    {
        Assert.Equal("E26", N14RiCommission);
        var template = TemplateWorkbook.Open(Template);
        Assert.True(template.HasCell(ReceivablesSheet, N14RiCommission));
        Assert.False(template.IsFormula(ReceivablesSheet, N14RiCommission));
        Assert.Equal("00140", template.TemplateText(ReceivablesSheet, "B26"));
        Assert.Equal("810.45", Text(FillFinancialAnnex(Template, Report(), Impairment(), Settings, 81045m), ReceivablesSheet, N14RiCommission));
        Assert.Equal("0", Text(FillFinancialAnnex(Template, Report(), Impairment(), Settings, 0m), ReceivablesSheet, N14RiCommission));
        Assert.Null(Text(FillFinancialAnnex(Template, Report(), Impairment(), Settings), ReceivablesSheet, N14RiCommission));
    }

    [Fact(DisplayName = "filled for a month: the subforms' totals and the balance's insurance lines, by the template's formulas, equal the sums made here")]
    public void SubformTotals()
    {
        var r = Report();
        var wb = FillFinancialAnnex(Template, r, Impairment(), Settings);
        var gross = r.Totals.UnearnedGel + r.ClaimsTotals.RbnsGel + r.ClaimsTotals.IbnrGel;
        var share = r.Totals.CededUnearnedGel + r.ClaimsTotals.ReinsurerRbnsGel;
        Assert.Equal(Tx(gross), Text(wb, ReservesSheet, "E13"));
        Assert.Equal(Tx(share), Text(wb, ReservesSheet, "E18"));
        Assert.Equal(Tx(gross - share), Text(wb, ReservesSheet, "E24"));
        var net = (36500m - 1825m) + 9007199254740801m + 0m;
        Assert.Equal(Tx(net), Text(wb, ReceivablesSheet, "E19"));
        // The balance: 00060 insurance receivables net, 00120 the reinsurer's share, 00200 reserves gross — from the subforms.
        Assert.Equal(Tx(net), Text(wb, BalanceSheet, "E14"));
        Assert.Equal(Tx(share), Text(wb, BalanceSheet, "E20"));
        Assert.Equal(Tx(gross), Text(wb, BalanceSheet, "E30"));
        // Reinsurance receivables (00070) are not kept: the template's formula over empty inputs gives 0, its inputs stay empty.
        Assert.Equal("0", Text(wb, BalanceSheet, "E15"));
        Assert.False(wb.Filled(ReceivablesSheet, "E23"));
    }

    [Fact(DisplayName = "filled for a month: the file — only the filled cells and the formulas' results differ; every formula, style and other part byte for byte; another reader reads the same")]
    public void TheFile()
    {
        var output = FillFinancialAnnex(Template, Report(), Impairment(), Settings).ToBuffer();
        var before = Zip.ReadZip(Template).ToDictionary(e => e.Name);
        var after = Zip.ReadZip(output);
        Assert.Equal(before.Keys, after.Select(e => e.Name));
        foreach (var e in after)
        {
            if (e.Name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) || e.Name == "xl/workbook.xml") continue;
            Assert.True(e.Compressed.AsSpan().SequenceEqual(before[e.Name].Compressed), e.Name);
        }
        static string Strip(string x) => Regex.Replace(x, "<c\\b[^>]*?(?:/>|>[\\s\\S]*?</c>)", "<c/>");
        foreach (var e in after.Where(x => x.Name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)))
        {
            string xmlBefore = Encoding.UTF8.GetString(Zip.EntryData(before[e.Name])), xmlAfter = Encoding.UTF8.GetString(Zip.EntryData(e));
            // BS-R gets one cell the template leaves out (D1, the company's name); every other sheet only changes cells.
            var withoutNew = new Regex("<c r=\"D1\" t=\"inlineStr\"><is>[\\s\\S]*?</is></c>").Replace(xmlAfter, "", 1);
            Assert.True(Strip(withoutNew) == Strip(xmlBefore), $"{e.Name}: only cells change");
            var was = CellsOf(xmlBefore);
            foreach (var (reference, cell) in CellsOf(xmlAfter))
            {
                if (!was.TryGetValue(reference, out var old))
                {
                    Assert.True(reference == "D1" && Regex.IsMatch(cell, "<is><t xml:space=\"preserve\">სატესტო კომპანია") && !Regex.IsMatch(cell, "\\bs=\""), $"{e.Name} {reference}: only the company's name is a new cell");
                    continue;
                }
                if (cell == old) continue;
                var f = Regex.Match(old, "<f\\b[^>]*?(?:/>|>[\\s\\S]*?</f>)");
                if (f.Success) { Assert.True(cell.Contains(f.Value, StringComparison.Ordinal), $"{e.Name} {reference}: its formula kept"); continue; }
                Assert.True(Regex.Match(cell, "\\bs=\"([^\"]+)\"").Groups[1].Value == Regex.Match(old, "\\bs=\"([^\"]+)\"").Groups[1].Value, $"{e.Name} {reference}: its style kept");
            }
        }
        var back = XlsxReader.Read(output);
        Assert.Equal(252d, back.Sheets[ReservesSheet]["E11"]!.V);
        Assert.Equal(18.25, back.Sheets[ReceivablesSheet]["E11"]!.V);
        Assert.Equal("ანგარიშგების თარიღი: 31.10.2026", back.Sheets[ReservesSheet]["B2"]!.V);
        Assert.Equal("'BS-R'!E13+'BS-R'!E32", back.Sheets[BalanceSheet]["E30"]!.F);
        Assert.Null(back.Sheets[ReservesSheet]["F10"]);
    }

    [Fact(DisplayName = "filled for a month: reports of different days, or another template: refused")]
    public void Refused()
    {
        var e = Assert.Throws<InvalidOperationException>(() => FillFinancialAnnex(Template, Report(), Impairment("2026-10-30"), Settings));
        Assert.Contains("impairment report is of 2026-10-30", e.Message);
        var other = (byte[])Template.Clone();
        other[^30] ^= 1;
        Assert.Contains("not the confirmed one", Assert.Throws<InvalidOperationException>(() => FillFinancialAnnex(other, Report(), Impairment(), Settings)).Message);
    }
}
