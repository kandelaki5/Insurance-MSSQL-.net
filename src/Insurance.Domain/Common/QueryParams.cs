// Ported from src/lib/known.ts: helpers for values that came from the address bar.
namespace Insurance.Domain.Common;

public static class QueryParams
{
    /// <summary>
    /// The address bar's parameters as plain strings: a key repeated in the URL (?approve=x&amp;approve=y)
    /// arrives as several values, which pages treated as text and crashed on (Codex, PR #21). Such values,
    /// and anything else that is not a single string, are dropped (null).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> SingleParams(IEnumerable<KeyValuePair<string, IReadOnlyList<string>?>> parameters)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, values) in parameters) result[key] = values is { Count: 1 } ? values[0] : null;
        return result;
    }

    /// <summary>
    /// The text for a value that came from the address bar, only when it is one of the map's own
    /// keys. (In the old code a plain lookup also found what every object inherits — `?approve=__proto__`
    /// gave Object.prototype and a 500 page, Codex H26; a .NET dictionary has only its own keys.)
    /// </summary>
    public static T? Known<T>(IReadOnlyDictionary<string, T> map, string? key) =>
        key is not null && map.TryGetValue(key, out var value) ? value : default;
}
