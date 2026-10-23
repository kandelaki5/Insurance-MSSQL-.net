using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// Parity of the S3 reports with the old program (tests/parity/cases/reports.json, made by tests/parity/generate/reports.ts): each
/// fictional world's rows — the same ones the old code read from its own PostgreSQL schema — are inserted into a fresh SQL Server
/// database in the order they were made, and every report the old code gave over them (the monthly premium report for 13 months,
/// the impairment report and its exact N1-4 totals on six dates, the premium report by policy for six periods, the bordereau of
/// each of five treaties for five periods) must come out of the new code with the same figures: every field the old report has,
/// money to the tetri, rates and shares to their last digit, texts and dates as written. The National Bank is refused on both sides.
/// </summary>
public sealed class ReportParityTests
{
    private static readonly Lazy<JsonObject> Cases = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity", "reports.json")), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject());

    public static TheoryData<int> Worlds()
    {
        var data = new TheoryData<int>();
        foreach (var w in Cases.Value["worlds"]!.AsArray()) data.Add(w!["seed"]!.GetValue<int>());
        return data;
    }

    private sealed class BigIntegerConverter : JsonConverter<BigInteger>
    {
        public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new BigIntegerConverter() } };

    private static JsonNode? Canonical(object? report) => JsonSerializer.SerializeToNode(report, Options);

    private static bool Number(JsonNode? n, out decimal value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.GetValueKind() == JsonValueKind.Number) return decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return v.GetValueKind() == JsonValueKind.String && decimal.TryParse(v.GetValue<string>(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Every field of the old value, compared with the new one's: a number with a number or a number's text by value, the rest as written.</summary>
    private static void Compare(JsonNode? old, JsonNode? now, string path, List<string> differences)
    {
        if (differences.Count > 40) return;
        switch (old)
        {
            case null:
                if (now is not null) differences.Add($"{path}: old null, new {now.ToJsonString()}");
                return;
            case JsonObject o:
                if (now is not JsonObject n) { differences.Add($"{path}: old object, new {now?.ToJsonString() ?? "null"}"); return; }
                foreach (var (key, value) in o)
                {
                    if (!n.ContainsKey(key)) { differences.Add($"{path}.{key}: missing in the new report"); continue; }
                    Compare(value, n[key], $"{path}.{key}", differences);
                }
                return;
            case JsonArray a:
                if (now is not JsonArray b) { differences.Add($"{path}: old array, new {now?.ToJsonString() ?? "null"}"); return; }
                if (path.EndsWith(".receivables", StringComparison.Ordinal))
                {
                    // The old read of the receivable policies has no ORDER BY, so the order of its currency rows is whatever PostgreSQL's plan
                    // gave; the new one reads them by creation. The rows are compared by currency.
                    a = new JsonArray([.. a.OrderBy(x => x!["currency"]!.GetValue<string>(), StringComparer.Ordinal).Select(x => x!.DeepClone())]);
                    b = new JsonArray([.. b.OrderBy(x => x!["currency"]!.GetValue<string>(), StringComparer.Ordinal).Select(x => x!.DeepClone())]);
                }
                if (a.Count != b.Count) { differences.Add($"{path}: {a.Count} old items, {b.Count} new"); return; }
                for (var i = 0; i < a.Count; i++) Compare(a[i], b[i], $"{path}[{i}]", differences);
                return;
            case JsonValue v:
                if (now is not JsonValue w) { differences.Add($"{path}: old {v.ToJsonString()}, new {now?.ToJsonString() ?? "null"}"); return; }
                if (v.GetValueKind() == JsonValueKind.String && w.GetValueKind() == JsonValueKind.String)
                {
                    if (v.GetValue<string>() != w.GetValue<string>()) differences.Add($"{path}: old {v.ToJsonString()}, new {w.ToJsonString()}");
                    return;
                }
                if (Number(v, out var x) && Number(w, out var y))
                {
                    if (x != y) differences.Add($"{path}: old {v.ToJsonString()}, new {w.ToJsonString()}");
                    return;
                }
                if (v.ToJsonString() != w.ToJsonString()) differences.Add($"{path}: old {v.ToJsonString()}, new {w.ToJsonString()}");
                return;
        }
    }

    private static object? Value(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
        _ => throw new InvalidOperationException($"unexpected value {n.ToJsonString()}"),
    };

    [Theory]
    [MemberData(nameof(Worlds))]
    public async Task Every_report_over_the_world_gives_the_old_programs_figures(int seed)
    {
        var world = Cases.Value["worlds"]!.AsArray().Single(w => w!["seed"]!.GetValue<int>() == seed)!;
        await using var w = await CreateAsync();
        await w.WriteAsync(async tx =>
        {
            foreach (var item in world["rows"]!.AsArray())
            {
                var table = item!["t"]!.GetValue<string>();
                var row = item["r"]!.AsObject();
                var p = new DynamicParameters();
                var names = new List<string>();
                foreach (var (column, value) in row)
                {
                    names.Add(column);
                    p.Add("p" + names.Count, Value(value));
                }
                await tx.ExecuteAsync($"insert core.{table} ({string.Join(", ", names)}) values ({string.Join(", ", names.Select((_, i) => "@p" + (i + 1)))})", p);
            }
            return 0;
        });

        var (rates, nbg) = RefusingNbg.For(w.App);
        var differences = new List<string>();
        var count = 0;
        foreach (var call in world["calls"]!.AsArray())
        {
            var fn = call!["fn"]!.GetValue<string>();
            var args = call["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
            object? report = fn switch
            {
                "premiumReport" => await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, args[0])),
                "impairment" => await w.App.ReadTxAsync(tx => ImpairmentReport.BuildAsync(tx, rates, D(args[0]))),
                "impairmentExact" => await w.App.ReadTxAsync(tx => ImpairmentReport.BuildTotalsExactAsync(tx, rates, D(args[0]))),
                "policyPremium" => await w.App.ReadTxAsync(tx => PolicyPremiumReport.BuildAsync(tx, D(args[0]), D(args[1]))),
                "bordereau" => await w.App.ReadTxAsync(tx => Bordereau.BuildAsync(tx, rates, Guid.Parse(args[0]), D(args[1]), D(args[2]))),
                _ => throw new InvalidOperationException(fn),
            };
            Compare(call["result"], Canonical(report), $"{fn}({string.Join(", ", args)})", differences);
            count++;
        }
        Assert.Equal(56, count);
        Assert.True(differences.Count == 0, string.Join(" || ", differences));
        Assert.All(nbg.Requests, r => Assert.Contains("nbg.gov.ge", r));
    }
}
