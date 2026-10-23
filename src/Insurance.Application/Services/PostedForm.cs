using System.Globalization;
using System.Text.RegularExpressions;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>
/// The fields of one posted form, as a server action of the old program received them (FormData): a field sent once has
/// its value; a field sent more than once has none (legacy singleParams, docs/DEVELOPMENT.md) but still counts as sent
/// (form.has) and is listed among the keys as often as it was sent (form.keys()).
/// </summary>
public sealed class PostedForm
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> sent = new(StringComparer.Ordinal);
    private readonly List<string> keys = [];

    public PostedForm(IEnumerable<KeyValuePair<string, string?>> fields)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            keys.Add(key);
            sent.Add(key);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            first.TryAdd(key, value ?? "");
        }
        foreach (var (key, count) in counts)
            if (count == 1) values[key] = first[key];
    }

    public static PostedForm Of(IReadOnlyDictionary<string, string> fields) => new(fields.Select(f => new KeyValuePair<string, string?>(f.Key, f.Value)));

    /// <summary>String(form.get(name) ?? ''): the value as sent, or null when it was not sent once.</summary>
    public string? Get(string name) => values.TryGetValue(name, out var v) ? v : null;

    /// <summary>form.has(name).</summary>
    public bool Has(string name) => sent.Contains(name);

    /// <summary>form.keys(): every field's name, in the order sent, repeated as often as it was sent.</summary>
    public IReadOnlyList<string> Keys => keys;

    /// <summary>The legacy actions' trimmed(form): every value trimmed as JavaScript trims.</summary>
    public IReadOnlyDictionary<string, string> Trimmed() => values.ToDictionary(v => v.Key, v => JsText.Trim(v.Value), StringComparer.Ordinal);
}

/// <summary>What an action that answers a form ends in: a redirect, or the form shown again with its errors and a message.</summary>
public sealed record FormOutcome(string? RedirectTo, IReadOnlyDictionary<string, string> Errors, string? Message)
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    public static FormOutcome Redirect(string url) => new(url, None, null);

    public static FormOutcome Refused(IReadOnlyDictionary<string, string>? errors = null, string? message = null) => new(null, errors ?? None, message);
}

/// <summary>How the old actions read numbers, dates and ids from a form (JavaScript's Number, toFixed, Math.round, the uuid pattern).</summary>
public static partial class FormValues
{
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    /// <summary>The old /^[0-9a-f]{8}-…$/i test.</summary>
    public static bool IsUuid(string? text) => text is not null && Uuid().IsMatch(text);

    public static Guid? UuidOf(string? text) => IsUuid(text) ? Guid.Parse(text!) : null;

    /// <summary>amount(value): Number(value.replace(',', '.')) — only the first comma — and NaN for a missing or empty value.</summary>
    public static double Amount(string? value)
    {
        if (string.IsNullOrEmpty(value)) return double.NaN;
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        return JsNumber.Parse(comma < 0 ? value : string.Concat(value.AsSpan(0, comma), ".", value.AsSpan(comma + 1)));
    }

    /// <summary>JavaScript's Math.round on a double, held within the range of a long.</summary>
    public static long JsRound(double x)
    {
        if (double.IsNaN(x)) return 0;
        var floor = Math.Floor(x);
        var rounded = x - floor >= 0.5 ? floor + 1 : floor;
        if (rounded >= 9.2e18) return long.MaxValue;
        if (rounded <= -9.2e18) return long.MinValue;
        return (long)rounded;
    }

    /// <summary>Number.isFinite(x) ? Math.round(x * 100) : 0 — the minor units the old actions took from a typed amount.</summary>
    public static long MinorOf(double x) => double.IsFinite(x) ? JsRound(x * 100) : 0;

    /// <summary>JavaScript's x.toFixed(2) for a finite double, as the exact decimal it denotes.</summary>
    public static decimal Fixed2(double x) => decimal.Parse(x.ToString("F2", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>isIsoDate(text), as a date; null for anything else (year 0000 included, which .NET has no date for).</summary>
    public static DateOnly? DateOf(string? text) =>
        text is not null && Dates.IsIsoDate(text) && !text.StartsWith("0000", StringComparison.Ordinal) ? Dates.ParseIsoDate(text) : null;

    public static string Iso(DateOnly date) => Dates.ToIso(date);
}
