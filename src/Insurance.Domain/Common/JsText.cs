using System.Text;

namespace Insurance.Domain.Common;

/// <summary>
/// The string operations of the old JavaScript code, where .NET's own differ. JavaScript's trim() and its regular
/// expressions' \s know a different set of white space than .NET (U+FEFF yes, U+0085 no), and its toUpperCase() maps
/// full Unicode case (ß → SS, ﬁ → FI, ı → I) where .NET maps one character to one. The rules ported from it use these so
/// that the same text gives the same answer.
/// </summary>
public static class JsText
{
    /// <summary>JavaScript's white space and line terminators (what trim() removes and \s matches), as a regex class body.</summary>
    public const string WhiteSpaceClass = "\\t\\n\\v\\f\\r \\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF";

    public static bool IsWhiteSpace(char c) => c switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        >= '\u2000' and <= '\u200A' => true,
        _ => false,
    };

    /// <summary>String.prototype.trim().</summary>
    public static string Trim(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsWhiteSpace(text[start])) start++;
        while (end > start && IsWhiteSpace(text[end - 1])) end--;
        return text[start..end];
    }

    /// <summary>String.prototype.toUpperCase(): full Unicode case mapping, lone surrogates kept as they are.</summary>
    public static string ToUpperCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append(c);
                continue;
            }
            else cp = c;
            if (Upper.TryGetValue(cp, out var upper)) sb.Append(upper);
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every code point JavaScript's toUpperCase() changes, with what it gives, from the old program's runtime (the header of
    /// JsUpper.txt names it; tests/parity/generate/js-upper-table.ts writes it). Nothing comes from .NET's or the operating
    /// system's case tables, so the answer is the same on Windows and Linux and does not move with a .NET update (Codex R04).
    /// </summary>
    // Before the table: static fields are set in the order written, and reading the table sets this one.
    /// <summary>The Node version, Unicode and ICU the upper-case table was taken from.</summary>
    public static string UpperSource { get; private set; } = "";

    private static readonly Dictionary<int, string> Upper = ReadUpper();

    private static Dictionary<int, string> ReadUpper()
    {
        using var stream = typeof(JsText).Assembly.GetManifestResourceStream("Insurance.Domain.Common.JsUpper.txt")!;
        using var reader = new StreamReader(stream);
        var map = new Dictionary<int, string>();
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith('#')) { UpperSource = line.TrimStart('#', ' '); continue; }
            var parts = line.Split(' ');
            map[Convert.ToInt32(parts[0], 16)] = string.Concat(parts.Skip(1).Select(h => char.ConvertFromUtf32(Convert.ToInt32(h, 16))));
        }
        return map;
    }
}
