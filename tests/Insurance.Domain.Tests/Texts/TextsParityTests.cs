// Parity of the interface texts with the old src/i18n/*.ts: tests/parity/cases/common-texts.json lists every text, number and
// dictionary as the old program has it, and every text function's output on sample arguments
// (tests/parity/generate/common-texts.ts). Each must be found under its C# name with exactly the same characters, and the
// C# classes must hold nothing else.
using System.Reflection;
using System.Text.Json;
using Insurance.Application.Texts;
using Insurance.Domain.Tests.Common;

namespace Insurance.Domain.Tests.Texts;

public class TextsParityTests
{
    private const string File = "common-texts.json";
    private static readonly Assembly TextsAssembly = typeof(TextMap).Assembly;

    private static IReadOnlyList<JsonElement> Items() => Parity.Cases(File, "items");

    private static MemberInfo Resolve(string csPath)
    {
        var segments = csPath.Split('.');
        var type = TextsAssembly.GetType($"Insurance.Application.Texts.{segments[0]}") ?? throw new InvalidOperationException($"no class {segments[0]}");
        for (int i = 1; i < segments.Length - 1; i++)
        {
            type = type.GetNestedType(segments[i], BindingFlags.Public) ?? throw new InvalidOperationException($"no class {segments[i]} in {type.FullName}");
        }
        var last = segments[^1];
        return (MemberInfo?)type.GetField(last, BindingFlags.Public | BindingFlags.Static)
            ?? type.GetMethod(last, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"no member {last} in {type.FullName}");
    }

    private static object? Value(MemberInfo m) => ((FieldInfo)m).GetValue(null);

    private static string? CompareMap(JsonElement entries, IReadOnlyDictionary<string, string> map)
    {
        var expected = entries.EnumerateArray().Select(e => (e[0].Str(), e[1].Str())).ToList();
        var actual = map.Select(p => (p.Key, p.Value)).ToList();
        if (expected.Count != actual.Count) return $"{actual.Count} entries, expected {expected.Count}";
        for (int i = 0; i < expected.Count; i++) if (expected[i] != actual[i]) return $"entry {i}: expected {expected[i]}, got {actual[i]}";
        return null;
    }

    [Fact(DisplayName = "every old text, number and dictionary is in C#, character for character and in its order")]
    public void StaticTexts() => Parity.All(Items().Where(i => i.GetProperty("kind").Str() != "function").ToList(), item =>
    {
        var member = Resolve(item.GetProperty("cs").Str());
        switch (item.GetProperty("kind").Str())
        {
            case "text":
                return Parity.Diff(item.GetProperty("value").Str(), Value(member) as string);
            case "number":
                return Parity.Diff(item.GetProperty("value").GetInt32(), (int)Value(member)!);
            case "map":
                return CompareMap(item.GetProperty("entries"), (IReadOnlyDictionary<string, string>)Value(member)!);
            case "map2":
            {
                var map = (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)Value(member)!;
                var entries = item.GetProperty("entries").EnumerateArray().ToList();
                if (!entries.Select(e => e[0].Str()).SequenceEqual(map.Keys)) return "keys differ";
                foreach (var e in entries) if (CompareMap(e[1], map[e[0].Str()]) is { } d) return $"{e[0].Str()}: {d}";
                return null;
            }
            default:
                return "unknown kind";
        }
    });

    [Fact(DisplayName = "every old text function gives the same text on the sample arguments")]
    public void TextFunctions() => Parity.All(Items().Where(i => i.GetProperty("kind").Str() == "function").ToList(), item =>
    {
        var method = (MethodInfo)Resolve(item.GetProperty("cs").Str());
        var types = item.GetProperty("params").Strings();
        foreach (var call in item.GetProperty("calls").EnumerateArray())
        {
            var args = call.GetProperty("args").EnumerateArray().Select((a, k) => (object)(types[k] switch
            {
                "string" => a.Str(),
                "number" => a.GetInt32(),
                "string[]" => a.Strings(),
                _ => throw new InvalidOperationException(types[k]),
            })).ToArray();
            var result = (string)method.Invoke(null, args)!;
            if (Parity.Diff(call.GetProperty("result").Str(), result) is { } d) return $"{call.GetProperty("args").GetRawText()}: {d}";
        }
        return null;
    });

    [Fact(DisplayName = "the C# text classes hold nothing the old files do not")]
    public void NothingExtra()
    {
        var known = Items().Select(i => i.GetProperty("cs").Str()).ToHashSet(StringComparer.Ordinal);
        var classes = Parity.Load(File).GetProperty("classes").Strings();
        var extra = new List<string>();
        void Walk(Type type, string path)
        {
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) if (!known.Contains($"{path}.{f.Name}")) extra.Add($"{path}.{f.Name}");
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) if (!known.Contains($"{path}.{m.Name}")) extra.Add($"{path}.{m.Name}");
            foreach (var n in type.GetNestedTypes(BindingFlags.Public)) Walk(n, $"{path}.{n.Name}");
        }
        foreach (var c in classes) Walk(TextsAssembly.GetType($"Insurance.Application.Texts.{c}")!, c);
        var all = TextsAssembly.GetTypes().Where(t => t.Namespace == "Insurance.Application.Texts" && !t.IsNested && t.IsPublic).Select(t => t.Name).Order(StringComparer.Ordinal);
        Assert.Equal(classes.Append(nameof(TextMap)).Order(StringComparer.Ordinal), all);
        Assert.Empty(extra);
    }
}
