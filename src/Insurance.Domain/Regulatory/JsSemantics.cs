using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Regulatory;

/// <summary>
/// The few JavaScript rules the old TypeScript code relied on without saying so, kept exactly so that the port gives the same
/// texts: what counts as white space (String.prototype.trim, /\s/), how a number is written (Number.prototype.toString) and read
/// (Number(text)), how a replacement text's "$" patterns are expanded (String.prototype.replace), and string order (localeCompare).
/// </summary>
public static class JsSemantics
{
    /// <summary>JavaScript's white space and line terminators (/\s/ and trim()), as a regex character class body.</summary>
    public const string WhiteSpaceChars = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";
    /// <summary>/\s/ of JavaScript.</summary>
    public const string Ws = "[" + WhiteSpaceChars + "]";
    /// <summary>/\S/ of JavaScript.</summary>
    public const string NotWs = "[^" + WhiteSpaceChars + "]";

    public static bool IsWhiteSpace(char c) => c switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff' => true,
        _ => c >= '\u2000' && c <= '\u200a',
    };

    public static string Trim(string s) => TrimEnd(TrimStart(s));
    public static string TrimStart(string s) { var i = 0; while (i < s.Length && IsWhiteSpace(s[i])) i++; return s[i..]; }
    public static string TrimEnd(string s) { var j = s.Length; while (j > 0 && IsWhiteSpace(s[j - 1])) j--; return s[..j]; }

    /// <summary>String.prototype.slice(start, end): negative positions count from the end, out of range is clamped.</summary>
    public static string Slice(string s, int start, int? end = null)
    {
        var len = s.Length;
        var from = start < 0 ? Math.Max(len + start, 0) : Math.Min(start, len);
        var e = end ?? len;
        var to = e < 0 ? Math.Max(len + e, 0) : Math.Min(e, len);
        return to > from ? s[from..to] : "";
    }

    /// <summary>JavaScript's text of a number (Number.prototype.toString(), String(x)): the shortest digits that read back as it.</summary>
    public static string NumberToString(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (x == 0) return "0";
        if (double.IsPositiveInfinity(x)) return "Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        if (x < 0) return "-" + NumberToString(-x);
        // .NET's "R" gives the shortest round-trip digits (as ECMAScript asks); only the layout differs.
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        var mantissa = e < 0 ? r : r[..e];
        var exp = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var intPart = point < 0 ? mantissa : mantissa[..point];
        var fracPart = point < 0 ? "" : mantissa[(point + 1)..];
        var digits = (intPart + fracPart).TrimStart('0');
        var lead = (intPart + fracPart).Length - digits.Length;
        // value = 0.digits × 10^n
        var n = intPart.Length + exp - lead;
        digits = digits.TrimEnd('0');
        var k = digits.Length;
        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
        var e1 = n - 1;
        var es = (e1 < 0 ? "-" : "+") + Math.Abs(e1).ToString(CultureInfo.InvariantCulture);
        return k == 1 ? digits + "e" + es : digits[..1] + "." + digits[1..] + "e" + es;
    }

    private static readonly Regex DecimalLiteral = new("^[+-]?(?:[0-9]+\\.?[0-9]*|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?\\z", RegexOptions.CultureInvariant);
    private static readonly Regex RadixLiteral = new("^0(?:([xX])([0-9a-fA-F]+)|([oO])([0-7]+)|([bB])([01]+))\\z", RegexOptions.CultureInvariant);

    /// <summary>Number(text) of JavaScript: white space around it ignored, "" is 0, anything not a numeral NaN.</summary>
    public static double ParseNumber(string? text)
    {
        if (text is null) return double.NaN;
        var s = Trim(text);
        if (s.Length == 0) return 0;
        if (s is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (s == "-Infinity") return double.NegativeInfinity;
        var radix = RadixLiteral.Match(s);
        if (radix.Success)
        {
            var (digits, b) = radix.Groups[2].Success ? (radix.Groups[2].Value, 16) : radix.Groups[4].Success ? (radix.Groups[4].Value, 8) : (radix.Groups[6].Value, 2);
            BigInteger v = 0;
            foreach (var ch in digits) v = v * b + Convert.ToInt32(ch.ToString(), 16);
            return (double)v;
        }
        if (!DecimalLiteral.IsMatch(s)) return double.NaN;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>String(x) for the values the old code turned into text: null, a number, a boolean or a text.</summary>
    public static string ToJsString(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => NumberToString(d),
        float f => NumberToString(f),
        int i => NumberToString(i),
        long l => NumberToString(l),
        decimal m => NumberToString((double)m),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// A replacement text expanded as String.prototype.replace does: $$ is "$", $&amp; the match, $` the text before it, $' the text
    /// after it, $n / $nn a capture (an absent one empty; a number beyond the captures stays as it is).
    /// </summary>
    public static string Expand(string replacement, string matched, int position, string input, IReadOnlyList<string?> captures)
    {
        if (replacement.IndexOf('$') < 0) return replacement;
        var sb = new StringBuilder();
        var m = captures.Count;
        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];
            if (c != '$' || i + 1 >= replacement.Length) { sb.Append(c); continue; }
            var next = replacement[i + 1];
            if (next == '$') { sb.Append('$'); i++; continue; }
            if (next == '&') { sb.Append(matched); i++; continue; }
            if (next == '`') { sb.Append(input, 0, position); i++; continue; }
            if (next == '\'') { var tail = position + matched.Length; if (tail < input.Length) sb.Append(input, tail, input.Length - tail); i++; continue; }
            if (next is >= '0' and <= '9')
            {
                var d1 = next - '0';
                if (i + 2 < replacement.Length && replacement[i + 2] is >= '0' and <= '9')
                {
                    var d2 = d1 * 10 + (replacement[i + 2] - '0');
                    if (d2 >= 1 && d2 <= m) { sb.Append(captures[d2 - 1] ?? ""); i += 2; continue; }
                }
                if (d1 >= 1 && d1 <= m) { sb.Append(captures[d1 - 1] ?? ""); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>text.replace(pattern, replacement) with a text pattern: its first occurrence only, the replacement expanded.</summary>
    public static string ReplaceFirst(string input, string pattern, string replacement)
    {
        var at = input.IndexOf(pattern, StringComparison.Ordinal);
        if (at < 0) return input;
        return string.Concat(input.AsSpan(0, at), Expand(replacement, pattern, at, input, []), input.AsSpan(at + pattern.Length));
    }

    /// <summary>text.replace(/re/, replacement) without the g flag: the first match only, the replacement expanded.</summary>
    public static string ReplaceFirst(string input, Regex re, string replacement)
    {
        var m = re.Match(input);
        if (!m.Success) return input;
        var caps = new List<string?>();
        for (var g = 1; g < m.Groups.Count; g++) caps.Add(m.Groups[g].Success ? m.Groups[g].Value : null);
        return string.Concat(input.AsSpan(0, m.Index), Expand(replacement, m.Value, m.Index, input, caps), input.AsSpan(m.Index + m.Length));
    }

    /// <summary>text.replace(/re/, fn) without the g flag: the first match only.</summary>
    public static string ReplaceFirst(string input, Regex re, MatchEvaluator fn) => re.Replace(input, fn, 1);

    /// <summary>a.localeCompare(b) as Node (ICU, its default locale) orders texts.</summary>
    public static int LocaleCompare(string a, string b) => Math.Sign(string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.None));

    /// <summary>JavaScript's &lt; on texts: by UTF-16 code units.</summary>
    public static int CompareOrdinal(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
}
