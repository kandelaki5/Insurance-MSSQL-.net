using System.Text.Json.Nodes;
using Insurance.Application.Excel;
using static Insurance.Domain.Tests.Regulatory.ParitySupport;

namespace Insurance.Domain.Tests.Excel;

/// <summary>
/// The policy import's reading of an upload against the old code (tests/parity/generate/regulatory-import.ts → regulatory-import.json):
/// CSV texts, .xlsx and .xls files give the same rows and values; a file the old code could not read is not read here either (the page
/// says "unreadable" either way; the reader's own message is not compared). And V8's reading of date texts, which decides what a CSV
/// field becomes.
/// </summary>
public class ExcelImportParityTests
{
    [Fact(DisplayName = "parity: parseImport — CSV, .xlsx and .xls uploads read into the same rows")]
    public void ImportParity()
    {
        var data = Load("regulatory-import.json");
        var n = 0;
        var wrong = new List<string>();
        foreach (var c in data["cases"]!.AsArray())
        {
            var bytes = Convert.FromBase64String((string)c!["bytes"]!);
            var coverages = c["coverages"]!.AsArray().Select(x => new ProductCoverage((string)x!["code"]!, (bool)x["is_mandatory"]!)).ToList();
            var defaults = c["defaults"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);
            JsonNode actual;
            try
            {
                actual = new JsonArray(PolicyImport.ParseImport(bytes, coverages, defaults)
                    .Select(r => (JsonNode)Arr(JsonValue.Create(r.Row), new JsonArray(r.Values.Select(v => (JsonNode)Arr(S(v.Key), S(v.Value))).ToArray()))).ToArray());
            }
            catch (Exception e) { actual = Failure(e); }
            var expected = c["rows"];
            var where = $"import {n} ({(string)c["kind"]!})";
            if (expected is JsonObject) { if (actual is not JsonObject) wrong.Add($"{where}: expected a refusal ({Stringify(expected)}), got {Stringify(actual)[..Math.Min(200, Stringify(actual).Length)]}"); }
            else
            {
                try { AssertSame(expected, actual, where); } catch (Exception e) { wrong.Add(e.Message); }
            }
            n++;
        }
        Assert.Equal(169, n);
        Assert.True(wrong.Count == 0, $"{wrong.Count} cases differ:\n" + string.Join("\n", wrong.Take(15)));
    }

    [Fact(DisplayName = "parity: new Date(text) — V8's date reading, ISO and legacy forms")]
    public void DateParity()
    {
        var wrong = new List<string>();
        foreach (var d in Load("regulatory-import.json")["dates"]!.AsArray())
        {
            var text = (string)d![0]!;
            var expected = d[1] is null ? (double?)null : (double)d[1]!;
            var got = JsDate.Parse(text);
            double? actual = got.IsValid ? got.Ms : null;
            if (expected != actual) wrong.Add($"{Stringify(JsonValue.Create(text))}: expected {expected}, got {actual}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
