using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insurance.Domain.Finance;

namespace Insurance.Domain.Tests.Finance;

/// <summary>One parity case: the old code's input and its output (or the message it threw).</summary>
internal sealed record ParityCase(int Index, JsonObject In, JsonNode? Out, string? Error, JsonObject Raw)
{
    public override string ToString() => $"#{Index} {In.ToJsonString()}";
}

/// <summary>The parity cases written by tests/parity/generate/finance.ts (copied next to the test assembly).</summary>
internal sealed class ParityCases
{
    private static readonly ConcurrentDictionary<string, ParityCases> Cache = new();
    private readonly JsonObject cases;

    private ParityCases(JsonObject root)
    {
        cases = root["cases"]!.AsObject();
        Counts = root["counts"]?.AsObject();
    }

    public JsonObject? Counts { get; }

    public static ParityCases Load(string file) => Cache.GetOrAdd(file, f =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "parity", f);
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
        return new ParityCases(root);
    });

    public IEnumerable<ParityCase> Cases(string fn)
    {
        var list = cases[fn]?.AsArray() ?? throw new InvalidOperationException($"no parity cases for {fn}");
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i]!.AsObject();
            yield return new ParityCase(i, c["in"]!.AsObject(), c["out"], c.ContainsKey("error") ? (string)c["error"]! : null, c);
        }
    }
}

/// <summary>The fictional rate table of finance-rates.json; a day without a rate gives 0, as in the generator.</summary>
internal static class ParityRates
{
    private static readonly Lazy<Dictionary<(Currency, DateOnly), decimal>> Table = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "parity", "finance-rates.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!["rates"]!.AsObject();
        var table = new Dictionary<(Currency, DateOnly), decimal>();
        foreach (var (currency, byDate) in root)
        {
            foreach (var (date, rate) in byDate!.AsObject()) table[(Enum.Parse<Currency>(currency), Dates.ParseIsoDate(date))] = rate!.GetValue<decimal>();
        }
        return table;
    });

    public static readonly RateProvider Provider = (currency, date) => Table.Value.TryGetValue((currency, date), out var r) ? r : 0m;
}

/// <summary>
/// Reading the old code's values into C# and writing C# results in the old code's shape: camelCase names, enums as
/// their snake_case text, dates as 'YYYY-MM-DD', big integers as decimal text, 'unknown' for an unknown value.
/// </summary>
internal static class ParityJson
{
    public static BigInteger Big(JsonNode node) => BigInteger.Parse((string)node!, CultureInfo.InvariantCulture);

    public static BigInteger[] BigInts(JsonNode node) => node.AsArray().Select(n => Big(n!)).ToArray();

    public static DateOnly Date(JsonNode node) => Dates.ParseIsoDate((string)node!);

    public static long Long(JsonNode node) => node.GetValue<long>();

    public static decimal Dec(JsonNode node) => node.GetValue<decimal>();

    public static string Str(JsonNode node) => (string)node!;

    // ---- enums: PascalCase ↔ the old code's text ----

    public static string EnumText(Enum value)
    {
        var name = value.ToString();
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        var text = sb.ToString();
        return value switch
        {
            CessionProblem => text.Replace('_', '-'),
            Currency => name,
            _ => text,
        };
    }

    public static object ParseEnum(Type type, string text)
    {
        foreach (var value in Enum.GetValues(type))
        {
            if (EnumText((Enum)value) == text) return value;
        }
        throw new InvalidOperationException($"no {type.Name} for '{text}'");
    }

    public static T E<T>(JsonNode node) where T : struct, Enum => (T)ParseEnum(typeof(T), (string)node!);

    // ---- reading ----

    public static T Read<T>(JsonNode? node) => (T)Read(typeof(T), node)!;

    public static object? Read(Type type, JsonNode? node)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (node is null) return underlying is not null || !type.IsValueType ? null : throw new InvalidOperationException($"null for {type.Name}");
        if (underlying is not null) type = underlying;
        if (type == typeof(string)) return (string)node!;
        if (type == typeof(long)) return node.GetValue<long>();
        if (type == typeof(int)) return node.GetValue<int>();
        if (type == typeof(decimal)) return node.GetValue<decimal>();
        if (type == typeof(bool)) return node.GetValue<bool>();
        if (type == typeof(BigInteger)) return Big(node);
        if (type == typeof(DateOnly)) return Date(node);
        if (type.IsEnum) return ParseEnum(type, (string)node!);
        if (type == typeof(NewMovement)) return ReadNewMovement(node.AsObject());
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var g && (g == typeof(IReadOnlyList<>) || g == typeof(List<>) || g == typeof(IEnumerable<>) || g == typeof(IReadOnlyCollection<>)))
        {
            var item = type.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(item))!;
            foreach (var n in node.AsArray()) list.Add(Read(item, n));
            return list;
        }
        if (type.IsArray)
        {
            var item = type.GetElementType()!;
            var arr = node.AsArray();
            var result = Array.CreateInstance(item, arr.Count);
            for (var i = 0; i < arr.Count; i++) result.SetValue(Read(item, arr[i]), i);
            return result;
        }
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            var (k, v) = (type.GetGenericArguments()[0], type.GetGenericArguments()[1]);
            var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(k, v))!;
            foreach (var (key, value) in node.AsObject()) dict[Read(k, JsonValue.Create(key))!] = Read(v, value);
            return dict;
        }
        // A record: its widest constructor, parameters by name.
        var obj = node.AsObject();
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p =>
        {
            var key = char.ToLowerInvariant(p.Name![0]) + p.Name[1..];
            if (obj.TryGetPropertyValue(key, out var value)) return Read(p.ParameterType, value);
            return p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null ? Activator.CreateInstance(p.ParameterType) : null;
        }).ToArray();
        return ctor.Invoke(args);
    }

    private static NewMovement ReadNewMovement(JsonObject o)
    {
        var kind = E<MovementKind>(o["kind"]!);
        var bookedOn = Date(o["bookedOn"]!);
        if (kind == MovementKind.Reversal) return NewMovement.Reversal((string)o["reversesId"]!, bookedOn);
        return NewMovement.Original(kind, Big(o["amount"]!), bookedOn, o["court"] is { } court ? Read<CourtDocument>(court) : null);
    }

    // ---- writing ----

    public static JsonNode? Write(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case int i: return JsonValue.Create(i);
            case long l: return JsonValue.Create(l);
            case decimal d: return JsonValue.Create(d);
            case BigInteger big: return JsonValue.Create(big.ToString(CultureInfo.InvariantCulture));
            case DateOnly date: return JsonValue.Create(Dates.ToIso(date));
            case Enum e: return JsonValue.Create(EnumText(e));
            case IDictionary dict:
                {
                    var o = new JsonObject();
                    foreach (DictionaryEntry entry in dict) o[entry.Key is Enum ek ? EnumText(ek) : Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = Write(entry.Value);
                    return o;
                }
            case IEnumerable list:
                {
                    var a = new JsonArray();
                    foreach (var item in list) a.Add(Write(item));
                    return a;
                }
        }
        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OrUnknown<>))
        {
            var unknown = (bool)type.GetProperty("IsUnknown")!.GetValue(value)!;
            return unknown ? JsonValue.Create(SubrogationReport.UnknownText) : Write(type.GetProperty("Value")!.GetValue(value));
        }
        var obj = new JsonObject();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            obj[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = Write(p.GetValue(value));
        }
        return obj;
    }

    private static decimal Num(JsonValue v) => decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Where two values differ (null when they are the same); a missing property counts as null, numbers by value.</summary>
    public static string? Diff(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}";
        }
        switch (expected)
        {
            case JsonObject eo:
                {
                    if (actual is not JsonObject ao) return $"{path}: expected an object {eo.ToJsonString()}, got {actual.ToJsonString()}";
                    foreach (var key in eo.Select(p => p.Key).Union(ao.Select(p => p.Key)))
                    {
                        var d = Diff(eo[key], ao[key], $"{path}.{key}");
                        if (d is not null) return d;
                    }
                    return null;
                }
            case JsonArray ea:
                {
                    if (actual is not JsonArray aa) return $"{path}: expected an array {ea.ToJsonString()}, got {actual.ToJsonString()}";
                    if (ea.Count != aa.Count) return $"{path}: expected {ea.Count} items {ea.ToJsonString()}, got {aa.Count} {aa.ToJsonString()}";
                    for (var i = 0; i < ea.Count; i++)
                    {
                        var d = Diff(ea[i], aa[i], $"{path}[{i}]");
                        if (d is not null) return d;
                    }
                    return null;
                }
            default:
                {
                    var ev = expected.AsValue();
                    if (actual is not JsonValue av) return $"{path}: expected {ev.ToJsonString()}, got {actual.ToJsonString()}";
                    var ek = ev.GetValueKind();
                    var ak = av.GetValueKind();
                    if (ek == JsonValueKind.Number && ak == JsonValueKind.Number)
                    {
                        return Num(ev) == Num(av) ? null : $"{path}: expected {ev.ToJsonString()}, got {av.ToJsonString()}";
                    }
                    return ek == ak && ev.ToJsonString() == av.ToJsonString() ? null : $"{path}: expected {ev.ToJsonString()}, got {av.ToJsonString()}";
                }
        }
    }
}

/// <summary>Runs every case of one old function against its C# port.</summary>
internal static class Parity
{
    /// <summary>
    /// For each case: the C# result written in the old shape must equal the old output; where the old code threw, the
    /// C# code must throw with the same message. Returns the number of cases checked.
    /// </summary>
    public static int Check(string file, string fn, Func<ParityCase, object?> compute, int minCases = 1)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var c in ParityCases.Load(file).Cases(fn))
        {
            count++;
            object? result;
            try
            {
                result = compute(c);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                if (c.Error is null) failures.Add($"{c}: threw {e.GetType().Name}: {e.Message}, expected {c.Out?.ToJsonString() ?? "null"}");
                else if (e.Message != c.Error) failures.Add($"{c}: threw '{e.Message}', expected '{c.Error}'");
                continue;
            }
            if (c.Error is not null)
            {
                failures.Add($"{c}: returned {ParityJson.Write(result)?.ToJsonString() ?? "null"}, expected the error '{c.Error}'");
                continue;
            }
            var diff = ParityJson.Diff(c.Out, ParityJson.Write(result));
            if (diff is not null) failures.Add($"{c}: {diff}");
        }
        Assert.True(count >= minCases, $"{fn}: {count} cases, expected at least {minCases}");
        Assert.True(failures.Count == 0, $"{fn}: {failures.Count} of {count} cases differ:\n" + string.Join("\n", failures.Take(15)));
        return count;
    }
}
