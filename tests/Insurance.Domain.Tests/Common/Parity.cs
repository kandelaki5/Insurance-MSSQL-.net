using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Insurance.Domain.Tests.Common;

/// <summary>
/// The parity cases written by tests/parity/generate (the old TypeScript code's answers), copied next to the tests as
/// parity/*.json.
/// </summary>
public static class Parity
{
    private static readonly Dictionary<string, JsonDocument> Loaded = new();

    public static JsonElement Load(string file)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(file, out var doc))
            {
                var path = Path.Combine(AppContext.BaseDirectory, "parity", file);
                doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 256 });
                Loaded[file] = doc;
            }
            return doc.RootElement;
        }
    }

    public static IReadOnlyList<JsonElement> Cases(string file, string section) => Load(file).GetProperty(section).EnumerateArray().ToList();

    /// <summary>A JSON string as the old code had it, lone surrogates included (System.Text.Json refuses those).</summary>
    public static string Str(this JsonElement e)
    {
        try
        {
            return e.GetString()!;
        }
        catch (InvalidOperationException)
        {
            var raw = e.GetRawText();
            var sb = new StringBuilder();
            for (int i = 1; i < raw.Length - 1; i++)
            {
                char c = raw[i];
                if (c != '\\') { sb.Append(c); continue; }
                char k = raw[++i];
                switch (k)
                {
                    case 'u': sb.Append((char)int.Parse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    default: sb.Append(k); break;
                }
            }
            return sb.ToString();
        }
    }

    public static string? StrOrNull(this JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.Str();

    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? v : null;

    public static DateOnly Date(this JsonElement e) => DateOnly.ParseExact(e.Str(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? DateOrNull(this JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.Date();

    public static List<string> Strings(this JsonElement e) => e.EnumerateArray().Select(x => x.Str()).ToList();

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Runs every case and fails once with the first differences, so one run shows how many cases differ.</summary>
    public static void All(IReadOnlyList<JsonElement> cases, Func<JsonElement, string?> mismatch)
    {
        Assert.NotEmpty(cases);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            string? problem;
            try { problem = mismatch(c); }
            catch (Exception ex) { problem = $"threw {ex.GetType().Name}: {ex.Message}"; }
            if (problem is not null) failures.Add($"{Shorten(c.GetRawText())} → {problem}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Count} cases differ:\n" + string.Join("\n", failures.Take(15)));
    }

    private static string Shorten(string s) => s.Length > 400 ? s[..400] + "…" : s;

    /// <summary>null when equal, otherwise "expected … got …".</summary>
    public static string? Diff<T>(T expected, T actual) => EqualityComparer<T>.Default.Equals(expected, actual) ? null : $"expected {expected}, got {actual}";
}
