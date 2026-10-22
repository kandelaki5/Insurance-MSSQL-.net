using System.Text;

namespace Insurance.Application.Data;

/// <summary>Enum values as the database stores them (snake_case text: TreatyKind.QuotaShare ↔ 'quota_share').</summary>
public static class Codes
{
    public static string Of<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var b = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) b.Append('_');
            b.Append(char.ToLowerInvariant(name[i]));
        }
        return b.ToString();
    }

    public static T Parse<T>(string code) where T : struct, Enum
    {
        var pascal = string.Concat(code.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
        return Enum.TryParse<T>(pascal, ignoreCase: false, out var value)
            ? value
            : throw new ArgumentException($"{code} is not a {typeof(T).Name}");
    }
}
