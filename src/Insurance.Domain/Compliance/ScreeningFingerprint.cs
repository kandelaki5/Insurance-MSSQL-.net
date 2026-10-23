// Fingerprints that tell whether a sanctions screening is the same as an earlier one: the same subject with the same
// screening data, the same list content and the same matches (the owner's decision of 23.10.2026 on reusing a false
// positive). Each is the SHA-256 of a canonical JSON text: object keys in ordinal order, no white space, sets sorted and
// without repeats, numbers as fixed decimal text. The text is built from the values themselves, never from stored JSON,
// so white space or key order of a stored text can never count as a change.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Insurance.Domain.Common;

namespace Insurance.Domain.Compliance;

public static class ScreeningFingerprint
{
    /// <summary>The version of the fields hashed; a change of the fields changes it, so older fingerprints no longer match.</summary>
    public const int Version = 1;

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The subject as screened: what the matcher reads (name, kind, aliases, birth year, documents) and the full birth date,
    /// of which the matcher uses the year only. Any change of these asks for a new review.
    /// </summary>
    public static string Subject(string subjectKind, Guid subjectId, ScreeningSubject subject, DateOnly? birthDate) =>
        Hash(Obj(
            ("v", Version),
            ("subjectKind", subjectKind),
            ("subjectId", subjectId.ToString("D")),
            ("kind", subject.Kind.ToSnakeCode()),
            ("name", subject.Name),
            ("aliases", Set(subject.Aliases ?? [])),
            ("birthYear", subject.BirthYear),
            ("birthDate", birthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("documents", Set(subject.Documents ?? []))));

    /// <summary>The set of matches: their order, and the order of each one's reasons, do not count.</summary>
    public static string Matches(IEnumerable<ScreeningMatch> matches) =>
        Hash(Obj(
            ("v", Version),
            ("matches", new JsonArray(matches
                .OrderBy(m => m.DataId, StringComparer.Ordinal).ThenBy(m => m.MatchedName, StringComparer.Ordinal)
                .Select(m => (JsonNode)Obj(
                    ("dataId", m.DataId),
                    ("name", m.Name),
                    ("reference", m.Reference),
                    ("listType", m.ListType),
                    ("score", m.Score.ToString("0.00", CultureInfo.InvariantCulture)),
                    ("matchedName", m.MatchedName),
                    ("strong", m.Strong),
                    ("reasons", Set(m.Reasons.Select(r => r.ToSnakeCode())))))
                .ToArray()))));

    /// <summary>The list's whole content: every entry with every field, in data id order, and the list's own date.</summary>
    public static string List(string source, DateTimeOffset? generatedAt, IEnumerable<SanctionEntry> entries) =>
        Hash(Obj(
            ("v", Version),
            ("source", source),
            ("generatedAt", generatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)),
            ("entries", new JsonArray(entries.OrderBy(e => e.DataId, StringComparer.Ordinal).Select(e => (JsonNode)Obj(
                ("dataId", e.DataId),
                ("kind", e.Kind.ToSnakeCode()),
                ("name", e.Name),
                ("aliases", List(e.Aliases)),
                ("listType", e.ListType),
                ("reference", e.Reference),
                ("listedOn", e.ListedOn),
                ("nationalities", List(e.Nationalities)),
                ("birthYears", List(e.BirthYears)),
                ("documents", List(e.Documents)))).ToArray()))));

    /// <summary>The canonical text of any JSON text: keys sorted at every level, no white space (arrays keep their order).</summary>
    public static string Canonical(string json) => Sorted(JsonNode.Parse(json))?.ToJsonString(Json) ?? "null";

    public static string Hash(JsonNode node) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sorted(node)!.ToJsonString(Json))));

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    private static JsonObject Obj(params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields)
            o[key] = value switch
            {
                null => null,
                JsonNode n => n,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                _ => throw new ArgumentException($"no canonical form for {value.GetType().Name}"),
            };
        return o;
    }

    private static JsonArray Set(IEnumerable<string> items) =>
        new(items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(s)).ToArray());

    private static JsonArray List(IEnumerable<string> items) => new(items.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
}
