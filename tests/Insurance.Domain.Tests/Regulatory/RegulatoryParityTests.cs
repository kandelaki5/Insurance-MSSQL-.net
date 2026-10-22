using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Insurance.Domain.Regulatory;
using static Insurance.Domain.Tests.Regulatory.ParitySupport;

namespace Insurance.Domain.Tests.Regulatory;

/// <summary>
/// The regulatory forms against the old TypeScript code (tests/parity/generate/regulatory.ts → tests/parity/cases/regulatory-*.json): the
/// same inputs give the same values, refusals, precision problems and the same file — part for part, cell for cell.
/// </summary>
public class RegulatoryParityTests
{
    private static readonly byte[] Reserves = Templates.ReservesTemplate(), N1 = Templates.FinancialAnnexTemplate(), Quarterly = Templates.OtherQuarterlyTemplate();
    private static byte[] TemplateOf(string which) => which switch { "reserves" => Reserves, "n1" => N1, _ => Quarterly };

    private static JsonArray ValueView(Value v) => Arr(S(v switch { NumValue => "num", StrValue => "str", BoolValue => "bool", _ => "empty" }),
        S(v switch { EmptyValue => null, NumValue => Formula.DecimalText(v), StrValue s => s.S, BoolValue b => b.B ? "true" : "false", _ => null }));
    private static JsonNode? XView(XValue? x) => x switch
    {
        null => null,
        XNum n => Arr(S("number"), S(JsSemantics.NumberToString(n.V))),
        XStr s => Arr(S("string"), S(s.V)),
        XBool b => Arr(S("boolean"), S(b.V ? "true" : "false")),
        _ => null,
    };
    private static JsonNode Try(Func<JsonNode?> f) { try { return f() ?? JsonValue.Create((string?)null)!; } catch (Exception e) { return Failure(e); } }

    private static JsonObject FormulaValues(TemplateWorkbook wb)
    {
        var all = new JsonArray();
        var shown = new JsonArray();
        foreach (var name in wb.SheetNames())
        {
            foreach (var reference in wb.CellRefs(name))
            {
                if (!wb.IsFormula(name, reference)) continue;
                JsonNode? exact, excel;
                try { exact = ValueView(wb.ValueOf(name, reference)); } catch (Exception e) { exact = Failure(e); }
                try { excel = XView(wb.ExcelValue(name, reference)); } catch (Exception e) { excel = Failure(e); }
                all.Add(Arr(S($"{name}!{reference}"), exact, excel));
                var zero = exact is JsonArray ea && (ea[1] is null || (string?)ea[1] == "0");
                if (!zero) shown.Add(Arr(S($"{name}!{reference}"), exact?.DeepClone(), excel?.DeepClone()));
            }
        }
        return new JsonObject { ["count"] = all.Count, ["sha"] = Sha(Stringify(all)), ["shown"] = shown };
    }

    private static JsonNode ProblemsNode(TemplateWorkbook wb)
    {
        try { return new JsonArray(wb.PrecisionProblems().Select(p => (JsonNode)new JsonObject { ["sheet"] = p.Sheet, ["ref"] = p.Ref, ["exact"] = p.Exact, ["excel"] = p.Excel }).ToArray()); }
        catch (Exception e) { return Failure(e); }
    }

    /// <summary>outputs() of the generator.</summary>
    private static void AssertOutputs(JsonNode expected, TemplateWorkbook wb, byte[] template, string where)
    {
        AssertSame(expected["problems"], ProblemsNode(wb), $"{where} problems");
        JsonNode workbook;
        try { workbook = Normalize(wb.ToBuffer(), template); } catch (Exception e) { workbook = Failure(e); }
        AssertSame(expected["workbook"], workbook, $"{where} workbook");
        JsonNode response;
        try
        {
            var r = RegulatoryExport.XlsxResponse(wb, "f.xlsx");
            response = new JsonObject { ["status"] = r.Status, ["type"] = r.ContentType, ["disposition"] = r.ContentDisposition };
        }
        catch (Exception e) { response = Failure(e); }
        AssertSame(expected["response"], response, $"{where} response");
        AssertSame(expected["values"], FormulaValues(wb), $"{where} formula values");
    }

    [Fact(DisplayName = "parity: formula — exact numbers, decimal texts, parsing and both evaluations, JavaScript number texts")]
    public void FormulaParity()
    {
        var data = Load("regulatory-formula.json");
        foreach (var f in data["fractions"]!.AsArray())
            Assert.Equal((string)f![2]!, Formula.DecimalText(Formula.Num(BigInteger.Parse((string)f[0]!, CultureInfo.InvariantCulture), BigInteger.Parse((string)f[1]!, CultureInfo.InvariantCulture))));
        foreach (var d in data["decimals"]!.AsArray())
        {
            var text = (string)d![0]!;
            AssertSame(d[1], Try(() => Formula.DecimalText(Formula.Decimal(text))), $"decimal({Stringify(JsonValue.Create(text))})");
        }
        var grid = data["grid"]!.AsObject();
        var exactCells = new GridCells(grid);
        var count = 0;
        foreach (var c in data["formulas"]!.AsArray())
        {
            var f = (string)c!["f"]!;
            int dr = (int)c["dr"]!, dc = (int)c["dc"]!;
            Node node;
            try { node = Formula.ParseFormula(f); }
            catch (Exception e) { AssertSame(c["parse"], Failure(e), $"parse {f}"); continue; }
            Assert.True(c["parse"] is null, $"parse {f}: expected {Stringify(c["parse"])}");
            AssertSame(c["exact"], Try(() => ValueView(Formula.Evaluate(node, "S", exactCells, dr, dc))), $"exact {f}");
            AssertSame(c["excel"], Try(() => XView(Formula.EvaluateDouble(node, "S", exactCells, dr, dc))), $"excel {f}");
            count++;
        }
        Assert.True(count > 400);
        foreach (var c in data["cols"]!.AsArray())
        {
            Assert.Equal((string)c![1]!, Formula.ColLetters((int)c[0]!));
            Assert.Equal((int)c[2]!, Formula.ColNumber((string)c[1]!));
        }
        foreach (var d in data["doubles"]!.AsArray())
        {
            var x = BitConverter.ToDouble(Convert.FromHexString((string)d![0]!));
            Assert.Equal((string)d[1]!, JsSemantics.NumberToString(x));
        }
        foreach (var n in data["numbers"]!.AsArray()) Assert.Equal((string)n![1]!, JsSemantics.NumberToString(JsSemantics.ParseNumber((string)n[0]!)));
    }

    private sealed class GridCells(JsonObject grid) : ICells, IXCells
    {
        private JsonNode? At(string sheet, int col, int row) => grid[$"{sheet}!{col},{row}"];
        Value ICells.Value(string sheet, int col, int row) => At(sheet, col, row) switch
        {
            null => Formula.Empty,
            var g when (string)g["t"]! == "num" => Formula.Decimal((string)g["text"]!),
            var g when (string)g["t"]! == "str" => new StrValue((string)g["s"]!),
            var g => new BoolValue((bool)g["b"]!),
        };
        XValue? IXCells.Value(string sheet, int col, int row) => At(sheet, col, row) switch
        {
            null => null,
            var g when (string)g["t"]! == "num" => new XNum(JsSemantics.ParseNumber((string)g["text"]!)),
            var g when (string)g["t"]! == "str" => new XStr((string)g["s"]!),
            var g => new XBool((bool)g["b"]!),
        };
    }

    [Fact(DisplayName = "parity: the workbook — random fillings, row insertions, print titles, heights and wrapping of the three templates")]
    public void WorkbookParity()
    {
        var cases = Load("regulatory-workbook.json").AsArray();
        Assert.Equal(48, cases.Count);
        var n = 0;
        foreach (var c in cases)
        {
            var which = (string)c!["template"]!;
            var wb = TemplateWorkbook.Open(TemplateOf(which));
            foreach (var op in c["ops"]!.AsArray())
            {
                var sheet = (string)op!["sheet"]!;
                Exception? error = null;
                try
                {
                    switch ((string)op["kind"]!)
                    {
                        case "num": wb.SetNumber(sheet, (string)op["ref"]!, (string)op["text"]!); break;
                        case "text":
                            wb.SetText(sheet, (string)op["ref"]!, (string)op["text"]!);
                            if (op["wrap"] is not null) wb.WrapText(sheet, (string)op["ref"]!);
                            break;
                        case "create": wb.SetText(sheet, (string)op["ref"]!, (string)op["text"]!, create: true); break;
                        case "wrap": wb.WrapText(sheet, (string)op["ref"]!); break;
                        case "height": wb.SetRowHeight(sheet, (int)op["row"]!, (double)op["points"]!); break;
                        case "insert": wb.InsertRows(sheet, (int)op["after"]!, (int)op["count"]!, (int)op["styleRow"]!, (bool)op["growMerges"]!); break;
                        default: wb.SetPrintTitles(sheet, (int)op["first"]!, (int)op["last"]!); break;
                    }
                }
                catch (Exception e) { error = e; }
                AssertSame(op["result"], error is null ? null : Failure(error), $"case {n} op {Stringify(op)}");
            }
            AssertOutputs(c, wb, TemplateOf(which), $"workbook case {n} ({which})");
            n++;
        }
    }

    private static decimal T(JsonNode? s) => decimal.Parse((string)s!, CultureInfo.InvariantCulture);
    private static PremiumReport ReportOf(JsonNode r) => new(
        (string)r["to"]!,
        r["classes"]!.AsArray().Select(c => new PremiumClassFigures((string)c!["classCode"]!, T(c["unearnedGel"]), T(c["cededUnearnedGel"]))).ToList(),
        new PremiumTotals(T(r["totals"]!["unearnedGel"]), T(r["totals"]!["cededUnearnedGel"])),
        r["claims"]!.AsArray().Select(c => new ClaimsClassFigures((string)c!["classCode"]!, T(c["rbnsGel"]), T(c["reinsurerRbnsGel"]), T(c["ibnrGel"]))).ToList(),
        new ClaimsTotals(T(r["claimsTotals"]!["rbnsGel"]), T(r["claimsTotals"]!["reinsurerRbnsGel"]), T(r["claimsTotals"]!["ibnrGel"])));
    private static string Tx(decimal t) => t.ToString(CultureInfo.InvariantCulture);
    private static JsonObject FiguresView(Form11Figures f) => new() { ["C"] = Tx(f.C), ["D"] = Tx(f.D), ["E"] = Tx(f.E), ["F"] = Tx(f.F), ["G"] = Tx(f.G) };

    [Fact(DisplayName = "parity: reserves placement — form 11 from the month reports, its totals, form 12, the file and the refusals")]
    public void ReservesParity()
    {
        var cases = Load("regulatory-reserves.json").AsArray();
        Assert.Equal(40, cases.Count);
        var n = 0;
        foreach (var c in cases)
        {
            var report = ReportOf(c!["report"]!);
            var company = (string)c["company"]!;
            AssertSame(c["figures"], new JsonArray(ReservesPlacement.Form11FiguresOf(report).Select(f => (JsonNode)Arr(S(f.Code), FiguresView(f.Figures))).ToArray()), $"reserves {n} figures");
            AssertSame(c["unplaced"], new JsonArray(ReservesPlacement.UnplacedClasses(report).Select(f => (JsonNode)Arr(S(f.Code), FiguresView(f.Figures))).ToArray()), $"reserves {n} unplaced");
            var filled = ReservesPlacement.FillReservesPlacement(Reserves, report, company);
            Assert.Equal((string)c["date"]!, filled.Date);
            var form11 = new JsonArray(ReservesPlacement.Form11AllRows.Select(r => (JsonNode)new JsonArray(ReservesPlacement.Form11Columns.Select(col => S(ReservesPlacement.Shown(filled.Workbook.ValueOf(ReservesPlacement.Form11, $"{col}{r.Row}")))).ToArray())).ToArray());
            AssertSame(c["form11"], form11, $"reserves {n} form 11");
            AssertOutputs(c, filled.Workbook, Reserves, $"reserves {n}");
            n++;
        }
    }

    [Fact(DisplayName = "parity: financial annex N1 — N1-7, N1-4, Info and the printed headers, the file and the refusals")]
    public void AnnexParity()
    {
        var cases = Load("regulatory-annex.json").AsArray();
        Assert.Equal(24, cases.Count);
        var n = 0;
        foreach (var c in cases)
        {
            var report = ReportOf(c!["report"]!);
            var imp = c["impairment"]!;
            var impairment = new ImpairmentForN14((string)imp["asOf"]!, imp["totals"]!.AsArray().Select(t => new ImpairmentTotalsExact((string)t!["kind"]!, (int)t["policies"]!, T(t["unpaidPremiumGel"]), T(t["reserveGel"]))).ToList());
            var settings = c["settings"]!.AsObject().ToDictionary(p => p.Key, p => (string?)p.Value);
            AssertSame(c["n14"], new JsonArray(FinancialAnnex.N14Figures(impairment).Select(f => (JsonNode)Arr(S(f.Ref), S(Tx(f.Tetri)))).ToArray()), $"annex {n} N1-4");
            AssertSame(c["n17"], new JsonArray(FinancialAnnex.N17Figures(report).Select(f => (JsonNode)Arr(S(f.Ref), S(Tx(f.Tetri)))).ToArray()), $"annex {n} N1-7");
            TemplateWorkbook wb;
            try { wb = FinancialAnnex.FillFinancialAnnex(N1, report, impairment, settings); }
            catch (Exception e) { AssertSame(c["fill"], Failure(e), $"annex {n} fill"); n++; continue; }
            Assert.True(c["fill"] is null, $"annex {n}: expected {Stringify(c["fill"])}");
            AssertOutputs(c, wb, N1, $"annex {n}");
            n++;
        }
    }

    private static Form6Source SourceOf(JsonNode s) => new(
        s["treaties"]!.AsArray().Select(t => new Form6Treaty((string)t!["id"]!, (string)t["number"]!, (string)t["kind"]!, (string)t["period_from"]!, (string)t["period_to"]!,
            (string?)t["reinsurer_rating"], (string?)t["rating_agency"], (string)t["reinsurer"]!, (string?)t["reinsurer_country"], (string?)t["broker"], (string?)t["broker_country"])).ToList(),
        s["treatyClasses"]!.AsArray().Select(x => new Form6TreatyClass((string)x!["treaty_id"]!, (string)x["class_code"]!)).ToList(),
        s["placedClasses"]!.AsArray().Select(x => new Form6TreatyClass((string)x!["treaty_id"]!, (string)x["class_code"]!)).ToList(),
        s["classes"]!.AsArray().Select(x => new Form6Class((string)x!["code"]!, (string)x["name_ka"]!, (int)x["sort_order"]!)).ToList());
    private static JsonObject RowView(Form6Row r) => new()
    {
        ["classCode"] = r.ClassCode, ["classLabel"] = r.ClassLabel,
        ["cells"] = new JsonObject(Form6.Columns.Select(c => KeyValuePair.Create(c, (JsonNode?)JsonValue.Create(r.Cells[c])))),
    };

    [Fact(DisplayName = "parity: form 6 — rows from the raw rows, the grid and the rows below it, long texts continued, the file")]
    public void Form6Parity()
    {
        var data = Load("regulatory-form6.json");
        var kinds = data["kinds"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);
        var n = 0;
        foreach (var c in data["cases"]!.AsArray())
        {
            var rows = Form6.Form6Rows(SourceOf(c!["source"]!), (string)c["asOf"]!, kinds);
            AssertSame(c["rows"], new JsonArray(rows.Select(r => (JsonNode)RowView(r)).ToArray()), $"form 6 case {n} rows");
            var (classes, count) = Form6.Form6Overflow(rows);
            AssertSame(c["overflow"], new JsonObject { ["classes"] = new JsonArray(classes.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()), ["rows"] = count }, $"form 6 case {n} overflow");
            var wb = Form6.FillForm6(Quarterly, rows, (string)c["asOf"]!, (string)c["company"]!);
            AssertOutputs(c, wb, Quarterly, $"form 6 case {n}");
            n++;
        }
        Assert.Equal(32, n);
        foreach (var t in data["texts"]!.AsArray())
        {
            var text = (string)t!["text"]!;
            var width = (double)t["width"]!;
            Assert.Equal((int)t["lines"]!, Form6.WrappedLines(text, width));
            AssertSame(t["pieces"], new JsonArray(Form6.TextPieces(text, width, (int)t["maxLines"]!).Select(p => (JsonNode)JsonValue.Create(p)).ToArray()), $"pieces of {text.Length} at {width}");
        }
        foreach (var q in data["quarters"]!.AsArray()) Assert.Equal((string?)q![1], Form6.QuarterEnd((string)q[0]!));
        foreach (var m in data["months"]!.AsArray()) Assert.Equal((string)m![1]!, Form6.QuarterOf((string)m[0]!));
        foreach (var c in data["codes"]!.AsArray()) Assert.Equal((string)c![1]!, Form6.FormCode((string)c[0]!));
        var names = data["names"]!.AsArray().Select(x => (string)x!).ToList();
        var order = data["order"]!.AsArray();
        var wrong = new List<string>();
        for (var i = 0; i < names.Count; i++)
            for (var j = 0; j < names.Count; j++)
                if ((int)order[i]![j]! != JsSemantics.LocaleCompare(names[i], names[j])) wrong.Add($"{Stringify(JsonValue.Create(names[i]))} vs {Stringify(JsonValue.Create(names[j]))}: {(int)order[i]![j]!}");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
