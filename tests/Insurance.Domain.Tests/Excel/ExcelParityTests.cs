using System.Globalization;
using System.Text.Json.Nodes;
using Insurance.Application.Excel;
using static Insurance.Domain.Tests.Regulatory.ParitySupport;

namespace Insurance.Domain.Tests.Excel;

/// <summary>
/// The generic Excel writer against the old code (tests/parity/generate/regulatory-layout.ts → regulatory-layout.json): the report
/// layout (xlsx-layout.ts readableWorkbook, columnFits, textWidth), SheetJS's aoa_to_sheet / json_to_sheet / write as the AML and
/// bordereau exports call them, and the policy import template — each file the same part for part.
/// </summary>
public class ExcelParityTests
{
    public static object? Decode(JsonNode? e) => (string)e!["t"]! switch
    {
        "s" => (string)e["v"]!,
        "n" => BitConverter.ToDouble(Convert.FromHexString((string)e["v"]!)),
        "b" => (bool)e["v"]!,
        "null" => null,
        "d" => DateTime.Parse((string)e["v"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _ => JsUndefined.Value,
    };
    private static List<IReadOnlyList<object?>> Rows(JsonNode? rows) => rows!.AsArray().Select(r => (IReadOnlyList<object?>)r!.AsArray().Select(Decode).ToList()).ToList();
    private static JsonNode Try(Func<JsonNode> f) { try { return f(); } catch (Exception e) { return Failure(e); } }

    [Fact(DisplayName = "parity: readableWorkbook — column fits and the laid-out file, sheet for sheet")]
    public void LayoutParity()
    {
        var data = Load("regulatory-layout.json");
        var n = 0;
        foreach (var c in data["layout"]!.AsArray())
        {
            var sheets = c!["sheets"]!.AsArray().Select(s => new SheetRows((string)s!["name"]!, Rows(s["rows"]), (int)s["header"]!)).ToList();
            var fits = new JsonArray(sheets.Select(s => (JsonNode)new JsonArray(XlsxLayout.ColumnFits(s.Rows, s.Header).Select(f => (JsonNode)new JsonObject { ["width"] = f.Width, ["wrap"] = f.Wrap }).ToArray())).ToArray());
            AssertSame(c["fits"], fits, $"layout {n} fits");
            AssertSame(c["workbook"], Try(() => Normalize(XlsxLayout.ReadableWorkbook(sheets), null)), $"layout {n} workbook");
            n++;
        }
        Assert.Equal(40, n);
        foreach (var w in data["widths"]!.AsArray())
            Assert.Equal((double)w![2]!, XlsxLayout.TextWidth((string)w[0]!, (bool)w[1]!));
    }

    [Fact(DisplayName = "parity: SheetJS aoa_to_sheet / json_to_sheet / write — the AML and bordereau exports' workbooks")]
    public void WriterParity()
    {
        var data = Load("regulatory-layout.json");
        var n = 0;
        foreach (var c in data["writer"]!.AsArray())
        {
            var actual = Try(() =>
            {
                var book = new XlsxBook();
                foreach (var s in c!["sheets"]!.AsArray())
                {
                    WorkSheet ws;
                    if ((string)s!["kind"]! == "aoa") ws = XlsxWriter.AoaToSheet(Rows(s["rows"]));
                    else
                    {
                        var records = s["records"]!.AsArray().Select(r => (IEnumerable<KeyValuePair<string, object?>>)r!.AsArray().Select(p => KeyValuePair.Create((string)p![0]!, Decode(p[1]))).ToList()).ToList();
                        var header = s["header"] is JsonArray h ? h.Select(x => (string)x!).ToList() : null;
                        ws = XlsxWriter.JsonToSheet(records, header);
                    }
                    book.AppendSheet(ws, (string?)s["name"]);
                }
                return Normalize(book.Write(), null);
            });
            AssertSame(c!["workbook"], actual, $"writer {n}");
            n++;
        }
        Assert.Equal(30, n);
        foreach (var t in data["templates"]!.AsArray())
        {
            var coverages = t!["coverages"]!.AsArray().Select(x => new ProductCoverage((string)x!["code"]!, (bool)x["is_mandatory"]!)).ToList();
            AssertSame(t["workbook"], Normalize(PolicyImport.BuildTemplate(coverages), null), $"template {coverages.Count}");
        }
    }
}
