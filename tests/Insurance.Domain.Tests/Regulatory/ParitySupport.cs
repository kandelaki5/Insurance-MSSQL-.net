using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insurance.Domain.Regulatory;

namespace Insurance.Domain.Tests.Regulatory;

/// <summary>
/// The C# side of tests/parity/generate/regulatory-common.ts: the parity cases' files, JSON written as JavaScript's JSON.stringify writes
/// it (for the hashes the generator took), and the normalized view of a workbook, computed exactly as the generator computes it.
/// </summary>
public static class ParitySupport
{
    public static JsonNode Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "parity", file);
        return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!;
    }

    public static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));
    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static JsonArray Arr(params JsonNode?[] items) => new(items);
    public static JsonNode? S(string? s) => s is null ? null : JsonValue.Create(s);
    public static JsonObject Failure(Exception e) => new() { ["error"] = e.Message };

    /// <summary>JSON.stringify of JavaScript: no spaces, strings escaped as JavaScript escapes them, numbers as JavaScript writes them.</summary>
    public static string Stringify(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }
    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(sb, a[i]); }
                sb.Append(']');
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o) { if (!first) sb.Append(','); first = false; WriteString(sb, k); sb.Append(':'); Write(sb, v); }
                sb.Append('}');
                break;
            case JsonValue v when v.TryGetValue<string>(out var s): WriteString(sb, s); break;
            case JsonValue v when v.TryGetValue<bool>(out var b): sb.Append(b ? "true" : "false"); break;
            case JsonValue v when v.TryGetValue<double>(out var d): sb.Append(JsSemantics.NumberToString(d)); break;
            case JsonValue v when v.TryGetValue<int>(out var i): sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case JsonValue v when v.TryGetValue<long>(out var l): sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case JsonValue v: sb.Append(v.ToJsonString()); break;
        }
    }
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                    else if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static readonly Regex Cell = new("<c\\b([^>]*?)(?:/>|>([\\s\\S]*?)</c>)", RegexOptions.CultureInvariant);
    private static string? Attr(string attrs, string name) { var m = Regex.Match(attrs, $"\\b{name}=\"([^\"]*)\"", RegexOptions.CultureInvariant); return m.Success ? m.Groups[1].Value : null; }

    private sealed record SheetView(List<(string Ref, string?[] C)> Cells, List<(string R, string Attrs)> Rows, List<string> Merges, string? Dimension);
    private static SheetView View(string xml)
    {
        var cells = new List<(string, string?[])>();
        foreach (Match m in Cell.Matches(xml))
        {
            var a = m.Groups[1].Value;
            var body = m.Groups[2].Success ? m.Groups[2].Value : "";
            var f = Regex.Match(body, "<f\\b[^>]*?(?:/>|>[\\s\\S]*?</f>)", RegexOptions.CultureInvariant);
            var v = Regex.Match(body, "<v(?:\\s[^>]*)?>([\\s\\S]*?)</v>", RegexOptions.CultureInvariant);
            var inline = Regex.Match(body, "<is>([\\s\\S]*?)</is>", RegexOptions.CultureInvariant);
            cells.Add((Attr(a, "r") ?? "", [Attr(a, "s"), Attr(a, "t"), f.Success ? f.Value : null, v.Success ? v.Groups[1].Value : null, inline.Success ? inline.Groups[1].Value : null]));
        }
        var rows = Regex.Matches(xml, "<row\\b([^>]*?)(/?)>", RegexOptions.CultureInvariant).Select(m => (Attr(m.Groups[1].Value, "r") ?? "", JsSemantics.Trim(m.Groups[1].Value))).ToList();
        var merges = Regex.Matches(xml, "<mergeCell\\b[^>]*\\bref=\"([^\"]+)\"", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToList();
        var dim = Regex.Match(xml, "<dimension ref=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        return new SheetView(cells, rows, merges, dim.Success ? dim.Groups[1].Value : null);
    }
    private static JsonArray CellNode(string reference, string?[] c) => Arr(S(reference), new JsonArray(c.Select(S).ToArray()));
    private static bool Same(string?[] a, string?[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);

    /// <summary>normalize() of regulatory-common.ts: the workbook against its template (null: written from nothing).</summary>
    public static JsonObject Normalize(byte[] buf, byte[]? template)
    {
        var entries = Zip.ReadZip(buf);
        var before = template is null ? [] : Zip.ReadZip(template);
        static string? Text(List<ZipEntry> es, string name) { var e = es.Find(x => x.Name == name); return e is null ? null : Encoding.UTF8.GetString(Zip.EntryData(e)); }
        var parts = new JsonArray(entries.Select(e => (JsonNode)Arr(S(e.Name), S(Sha(Zip.EntryData(e))))).ToArray());
        var book = Text(entries, "xl/workbook.xml") ?? "";
        var rels = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Text(entries, "xl/_rels/workbook.xml.rels") ?? "", "<Relationship\\b[^>]*\\bId=\"([^\"]+)\"[^>]*\\bTarget=\"([^\"]+)\"", RegexOptions.CultureInvariant))
            rels[m.Groups[1].Value] = m.Groups[2].Value;
        var definedNames = new JsonArray(Regex.Matches(book, "<definedName\\b([^>]*)>([\\s\\S]*?)</definedName>", RegexOptions.CultureInvariant).Select(m => (JsonNode)Arr(S(m.Groups[1].Value), S(m.Groups[2].Value))).ToArray());
        var calc = Regex.Match(book, "<calcPr\\b[^>]*/>", RegexOptions.CultureInvariant);
        static List<string> Xfs(string? x)
        {
            if (x is null) return [];
            var block = Regex.Match(x, "<cellXfs\\b[^>]*>([\\s\\S]*?)</cellXfs>", RegexOptions.CultureInvariant);
            return Regex.Matches(block.Success ? block.Groups[1].Value : "", "<xf\\b[^>]*?(?:/>|>[\\s\\S]*?</xf>)", RegexOptions.CultureInvariant).Select(m => m.Value).ToList();
        }
        var xfNow = Xfs(Text(entries, "xl/styles.xml"));
        var xfWas = template is null ? [] : Xfs(Text(before, "xl/styles.xml"));
        var sheets = new JsonArray();
        foreach (Match m in Regex.Matches(book, "<sheet\\b[^>]*\\bname=\"([^\"]*)\"[^>]*\\br:id=\"([^\"]+)\"", RegexOptions.CultureInvariant))
        {
            var name = m.Groups[1].Value;
            var target = rels.GetValueOrDefault(m.Groups[2].Value) ?? "";
            var path = target.StartsWith('/') ? target[1..] : $"xl/{target}";
            var xml = Text(entries, path) ?? "";
            var was = template is null ? null : Text(before, path);
            if (was is not null && was == xml) { sheets.Add(new JsonObject { ["name"] = name, ["path"] = path, ["changed"] = false }); continue; }
            var now = View(xml);
            var old = was is null ? null : View(was);
            var oldCells = new Dictionary<string, string?[]>();
            foreach (var (r, c) in old?.Cells ?? []) oldCells[r] = c;
            var oldRows = new Dictionary<string, string>();
            foreach (var (r, a) in old?.Rows ?? []) oldRows[r] = a;
            var nowRefs = now.Cells.Select(c => c.Ref).ToHashSet();
            sheets.Add(new JsonObject
            {
                ["name"] = name, ["path"] = path, ["changed"] = true,
                ["cells"] = new JsonArray(now.Cells.Where(c => !(oldCells.TryGetValue(c.Ref, out var o) && Same(o, c.C))).Select(c => (JsonNode)CellNode(c.Ref, c.C)).ToArray()),
                ["removedCells"] = new JsonArray(oldCells.Keys.Where(r => !nowRefs.Contains(r)).Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
                ["cellOrder"] = Sha(string.Join(",", now.Cells.Select(c => c.Ref))),
                ["rows"] = new JsonArray(now.Rows.Where(r => !(oldRows.TryGetValue(r.R, out var a) && a == r.Attrs)).Select(r => (JsonNode)Arr(S(r.R), S(r.Attrs))).ToArray()),
                ["rowOrder"] = Sha(string.Join(",", now.Rows.Select(r => r.R))),
                ["merges"] = new JsonArray(now.Merges.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["dimension"] = S(now.Dimension),
            });
        }
        return new JsonObject
        {
            ["parts"] = parts, ["sheets"] = sheets, ["definedNames"] = definedNames, ["calcPr"] = calc.Success ? calc.Value : null,
            ["addedXfs"] = new JsonArray(xfNow.Skip(xfWas.Count).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()), ["xfCount"] = xfNow.Count,
        };
    }

    /// <summary>Asserts two JSON trees equal, naming the first place they differ.</summary>
    public static void AssertSame(JsonNode? expected, JsonNode? actual, string where)
    {
        // The old code crashed (a TypeError) where the new one refuses with a message of its own: both fail, nothing is given out.
        if (expected is JsonObject { Count: 1 } crash && crash.ContainsKey("crash") && actual is JsonObject failed && failed.ContainsKey("error")) return;
        var diff = FirstDifference(expected, actual, "$");
        if (diff is not null) Assert.Fail($"{where}: {diff}");
    }
    private static string? FirstDifference(JsonNode? e, JsonNode? a, string path)
    {
        if (e is null || a is null) return e is null && a is null ? null : $"{path}: expected {Stringify(e)}, got {Stringify(a)}";
        if (e is JsonArray ea)
        {
            if (a is not JsonArray aa) return $"{path}: expected an array {Short(e)}, got {Short(a)}";
            for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++) { var d = FirstDifference(ea[i], aa[i], $"{path}[{i}]"); if (d is not null) return d; }
            return ea.Count == aa.Count ? null : $"{path}: expected {ea.Count} items, got {aa.Count} ({Short(ea.Count > aa.Count ? ea[aa.Count] : aa[ea.Count])})";
        }
        if (e is JsonObject eo)
        {
            if (a is not JsonObject ao) return $"{path}: expected an object {Short(e)}, got {Short(a)}";
            foreach (var (k, v) in eo) { if (!ao.ContainsKey(k)) return $"{path}.{k}: missing (expected {Short(v)})"; var d = FirstDifference(v, ao[k], $"{path}.{k}"); if (d is not null) return d; }
            foreach (var (k, v) in ao) if (!eo.ContainsKey(k)) return $"{path}.{k}: not expected ({Short(v)})";
            return null;
        }
        return Stringify(e) == Stringify(a) ? null : $"{path}: expected {Stringify(e)}, got {Stringify(a)}";
    }
    private static string Short(JsonNode? n) { var s = Stringify(n); return s.Length > 300 ? s[..300] + "…" : s; }
}
