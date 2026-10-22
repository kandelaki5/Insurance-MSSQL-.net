using System.Collections.Concurrent;
using System.Text;

namespace Insurance.Domain.Common;

/// <summary>
/// The text codes the old program stores and sends for a closed set of values ('high_risk_country', 'not-in-role'):
/// the C# enum member's name in snake_case or kebab-case.
/// </summary>
public static class EnumCode
{
    private static readonly ConcurrentDictionary<(Type, string, char), string> Cache = new();

    /// <summary>HighRiskCountry → "high_risk_country".</summary>
    public static string ToSnakeCode<T>(this T value) where T : struct, Enum => Code(value, '_');

    /// <summary>NotInRole → "not-in-role".</summary>
    public static string ToKebabCode<T>(this T value) where T : struct, Enum => Code(value, '-');

    /// <summary>The member whose snake_case code is `code`; false for any other text.</summary>
    public static bool TryParseSnakeCode<T>(string? code, out T value) where T : struct, Enum => TryParse(code, '_', out value);

    /// <summary>The member whose kebab-case code is `code`; false for any other text.</summary>
    public static bool TryParseKebabCode<T>(string? code, out T value) where T : struct, Enum => TryParse(code, '-', out value);

    private static bool TryParse<T>(string? code, char separator, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Code(candidate, separator), code, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string Code<T>(T value, char separator) where T : struct, Enum
    {
        var name = value.ToString();
        return Cache.GetOrAdd((typeof(T), name, separator), key =>
        {
            var sb = new StringBuilder();
            foreach (char c in key.Item2)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append(key.Item3);
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        });
    }
}
