using System.Collections.ObjectModel;

namespace Insurance.Application.Texts;

/// <summary>
/// A read-only dictionary of texts by code that keeps the order the texts were written in, as the old objects did
/// (a form lists the choices in that order).
/// </summary>
public static class TextMap
{
    public static IReadOnlyDictionary<string, T> Of<T>(params (string Key, T Value)[] entries)
    {
        var map = new OrderedDictionary<string, T>(entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in entries) map.Add(key, value);
        return new ReadOnlyDictionary<string, T>(map);
    }
}
