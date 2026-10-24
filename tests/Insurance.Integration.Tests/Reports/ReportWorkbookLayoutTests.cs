using System.IO.Compression;
using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The two plain report exports laid out for reading (legacy report-xlsx-layout.test.ts; owner 20.10.2026): the impairment calculation and the
/// premium by policy, over hand-made fictional reports — every cell's value as the builders wrote it before the layout, written out by hand,
/// then the layout itself (print titles on the header row, the widest text column at the cap) and a 140 000-row export written whole.
/// The layout's own rules (widths, panes, wrapping, page setup) are the shared XlsxLayout's, tested in Insurance.Domain.Tests/Excel.
/// </summary>
public sealed class ReportWorkbookLayoutTests
{
    private const string LongHolder = "სააქციო საზოგადოება „ძალიან გრძელი სახელის მქონე სატესტო კომპანია“";

    private static ByBand B(long over12, long m9to12, long m6to9, long m3to6, long m0to3) => new(over12, m9to12, m6to9, m3to6, m0to3);

    private static readonly ImpairmentReport.Report Impairment = new(D("2026-10-20"),
    [
        new(Guid.NewGuid(), "TEST-LAYOUT-0001", D("2026-01-15"), D("2027-01-15"), null, ["broker"], "შპს სატესტო ბროკერი", LongHolder, "corporate",
            "05.1 სახმელეთო სატრანსპორტო საშუალებები", null, "სატესტო ბანკი", "USD", 123456, 50000, 2.7183m, 135915,
            new ImpairmentResult(B(0, 0, 10000, 20000, 30000), 60000, 73456, 0, B(0, 0, 0, 0, 0), B(0, 0, 0, 0, 0), 0), 0),
        new(Guid.NewGuid(), "TEST-LAYOUT-0002", D("2025-03-01"), D("2026-03-01"), D("2025-12-10"), ["client"], null, "გიორგი სატესტო", "retail",
            "05.1", "გიორგი სატესტო", null, "GEL", 90000, 25000, 1m, 25000,
            new ImpairmentResult(B(0, 15000, 20000, 25000, 0), 60000, 65000, 25000, B(0, 5000, 20000, 0, 0), B(0, 2500, 5000, 0, 0), 7500), 7500),
    ],
    [
        new("client", 1, 25000, 25000, 7500), new("broker", 1, 135915, 0, 0), new("all", 2, 160915, 25000, 7500),
    ], []);

    private static readonly PolicyPremiumReport.Row Long = new("TEST-C-0001", "TEST-LAYOUT-0001", LongHolder, false, "05.1", "სახმელეთო სატრანსპორტო საშუალებები", "USD",
        123456, 335592, 0, 335592, D("2026-10-05"), D("2026-10-05"), D("2027-10-05"), null, 24814, 310778, 100000, 7397, 92603, 1479, 18521);
    private static readonly PolicyPremiumReport.Row Short = new("TEST-C-0002", "TEST-LAYOUT-0002", "გიორგი სატესტო", true, "01", "უბედური შემთხვევა", "GEL",
        12000, 12000, 3000, -3000, D("2026-01-01"), D("2026-06-01"), D("2027-01-01"), D("2026-10-10"), 300, 0, 0, 0, 0, 0, 0);
    private static readonly PolicyPremiumReport.Totals PremiumTotals = new(347592, 3000, 332592, 25114, 310778, 100000, 7397, 92603, 1479, 18521);
    private static readonly PolicyPremiumReport.Report Premium = new(D("2026-10-01"), D("2026-10-31"), [Long, Short], PremiumTotals);

    private static Dictionary<string, string> Parts(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file));
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    private static List<int> Widths(string xml) =>
        Regex.Matches(xml, "<col min=\"(\\d+)\" max=\"\\1\" width=\"([\\d.]+)\" customWidth=\"1\"/>").Select(m => (int)Math.Floor(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();

    [Fact]
    public void The_impairment_export_writes_every_value_as_before_and_is_laid_out_for_reading()
    {
        var file = ReportWorkbooks.Impairment(Impairment);
        var book = XlsxSheet.Read(file);
        Assert.Equal(["ფორმა", "N1-4"], book.SheetNames);
        var t = ImpairmentTexts.Kinds;
        var form = XlsxSheet.Rows(book, "ფორმა");
        Assert.Equal(36, form[0].Length);
        Assert.Equal(string.Join("|", new object[] { 1, "TEST-LAYOUT-0001", "2026-01-15", "2027-01-15", t["broker"], "შპს სატესტო ბროკერი", LongHolder, "05.1 სახმელეთო სატრანსპორტო საშუალებები", "კორპორატიული", "", "სატესტო ბანკი",
            "1234.56", "USD", 0, 0, 100, 200, 300, 600, "734.56", 500, "2.7183", "1359.15", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), string.Join("|", form[1]));
        Assert.Equal(string.Join("|", new object[] { 2, "TEST-LAYOUT-0002", "2025-03-01", "2025-12-10", t["client"], "", "გიორგი სატესტო", "05.1", "საცალო", "გიორგი სატესტო", "",
            900, "GEL", 0, 150, 200, 250, 0, 600, 650, 250, 1, 250, 250, 0, 50, 200, 0, 0, 75, 0, 25, 50, 0, 0, 75 }), string.Join("|", form[2]));
        var c = ImpairmentTexts.Columns;
        Assert.Equal([
            $"{c["debtorKind"]}|{c["policies"]}|{c["unpaidPremiumGel"]}|{c["unpaidEarnedGel"]}|{c["reserveGel"]}",
            $"{t["client"]}|1|250|250|75", $"{t["broker"]}|1|1359.15|0|0", $"{t["all"]}|2|1609.15|250|75",
        ], XlsxSheet.Rows(book, "N1-4").Select(r => string.Join("|", r)));
        // Every number a number, every text a text, as before.
        foreach (var (reference, cell) in book.Sheets["ფორმა"].Cells)
            Assert.True(cell.V is double == (cell.T == 'n'), reference);
        var parts = Parts(file);
        Assert.Contains("<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">&apos;ფორმა&apos;!$1:$1</definedName>", parts["xl/workbook.xml"]);
        var widths = Widths(parts["xl/worksheets/sheet1.xml"]);
        Assert.Equal(47, widths[6]);
        Assert.True(widths[2] >= 11 && widths[3] >= 11, $"date widths {widths[2]} {widths[3]}");
    }

    [Fact]
    public void The_premium_by_policy_export_writes_every_value_as_before_and_its_title_above_the_header_is_not_measured()
    {
        var file = ReportWorkbooks.PolicyPremium(Premium);
        var sheet = XlsxSheet.Rows(file, "NBG list");
        var p = PolicyPremiumTexts.Columns.ByKey;
        Assert.Equal(["01.10.2026 – 31.10.2026"], sheet[0].Where(x => x != ""));
        Assert.All(sheet[1], x => Assert.Equal("", x));
        Assert.Equal(new[] { "contractNumber", "policyNumber", "policyholder", "isModification", "classCode", "className", "currency", "premium", "statistical", "correction", "corrected",
            "startDate", "changeDate", "endDate", "cancelDate", "earned", "unearned", "riPremium", "riEarned", "riUnearned", "commissionEarned", "commissionUnearned" }.Select(k => p[k]), sheet[2]);
        Assert.Equal($"TEST-C-0001|TEST-LAYOUT-0001|{LongHolder}|{PolicyPremiumTexts.No}|05.1|სახმელეთო სატრანსპორტო საშუალებები|USD|1234.56|3355.92|0|3355.92|05.10.2026|05.10.2026|05.10.2027||248.14|3107.78|1000|73.97|926.03|14.79|185.21", string.Join("|", sheet[3]));
        Assert.Equal($"TEST-C-0002|TEST-LAYOUT-0002|გიორგი სატესტო|{PolicyPremiumTexts.Yes}|01|უბედური შემთხვევა|GEL|120|120|30|-30|01.01.2026|01.06.2026|01.01.2027|10.10.2026|3|0|0|0|0|0|0", string.Join("|", sheet[4]));
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "3475.92", "30", "3325.92", "", "", "", "", "251.14", "3107.78", "1000", "73.97", "926.03", "14.79", "185.21"], sheet[5]);
        var parts = Parts(file);
        Assert.Contains("<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">&apos;NBG list&apos;!$3:$3</definedName>", parts["xl/workbook.xml"]);
        var xml = parts["xl/worksheets/sheet1.xml"];
        var widths = Widths(xml);
        Assert.True(widths[1] >= "TEST-LAYOUT-0001".Length + 1, $"policy number width {widths[1]}");
        foreach (var col in new[] { 11, 12, 13, 14 }) Assert.True(widths[col] >= 11, $"date width {widths[col]}");
        Assert.Matches("<c r=\"A1\" t=\"str\">", xml);
    }

    [Fact]
    public void A_premium_by_policy_export_of_140_000_rows_is_written_whole_the_widest_policyholder_last()
    {
        const int n = 140_000;
        var rows = Enumerable.Range(1, n).Select(i => Short with { PolicyNumber = $"TEST-BIG-{i:D6}" }).ToList();
        rows[n - 1] = Long with { PolicyNumber = $"TEST-BIG-{n}" };
        var xml = Parts(ReportWorkbooks.PolicyPremium(Premium with { Rows = rows }))["xl/worksheets/sheet1.xml"];
        var last = 3 + n + 1;
        Assert.Contains($"<dimension ref=\"A1:V{last}\"/>", xml);
        Assert.Equal(1 + 1 + n + 1, Regex.Matches(xml, "<row r=\"").Count);
        Assert.Equal(47, Widths(xml)[2]);
        Assert.Matches($"<c r=\"C{last - 1}\" s=\"\\d+\" t=\"str\">", xml);
        Assert.Contains($"<v>TEST-BIG-{n}</v>", xml);
    }
}
