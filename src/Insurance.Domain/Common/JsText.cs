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
            if (UpperExceptions.TryGetValue(cp, out var special)) sb.Append(special);
            else sb.Append(Rune.ToUpperInvariant(new Rune(cp)).ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Where JavaScript (Node 22, Unicode 16) upper-cases differently from .NET's invariant mapping: the full mappings of
    /// SpecialCasing.txt, the dotless ı that .NET leaves alone, and letters newer than .NET's Unicode tables.
    /// </summary>
    private static readonly Dictionary<int, string> UpperExceptions = new()
    {
        [0x00DF] = "\u0053\u0053",
        [0x0131] = "\u0049",
        [0x0149] = "\u02BC\u004E",
        [0x019B] = "\uA7DC",
        [0x01F0] = "\u004A\u030C",
        [0x0264] = "\uA7CB",
        [0x0390] = "\u0399\u0308\u0301",
        [0x03B0] = "\u03A5\u0308\u0301",
        [0x0587] = "\u0535\u0552",
        [0x1C8A] = "\u1C89",
        [0x1E96] = "\u0048\u0331",
        [0x1E97] = "\u0054\u0308",
        [0x1E98] = "\u0057\u030A",
        [0x1E99] = "\u0059\u030A",
        [0x1E9A] = "\u0041\u02BE",
        [0x1F50] = "\u03A5\u0313",
        [0x1F52] = "\u03A5\u0313\u0300",
        [0x1F54] = "\u03A5\u0313\u0301",
        [0x1F56] = "\u03A5\u0313\u0342",
        [0x1F80] = "\u1F08\u0399",
        [0x1F81] = "\u1F09\u0399",
        [0x1F82] = "\u1F0A\u0399",
        [0x1F83] = "\u1F0B\u0399",
        [0x1F84] = "\u1F0C\u0399",
        [0x1F85] = "\u1F0D\u0399",
        [0x1F86] = "\u1F0E\u0399",
        [0x1F87] = "\u1F0F\u0399",
        [0x1F88] = "\u1F08\u0399",
        [0x1F89] = "\u1F09\u0399",
        [0x1F8A] = "\u1F0A\u0399",
        [0x1F8B] = "\u1F0B\u0399",
        [0x1F8C] = "\u1F0C\u0399",
        [0x1F8D] = "\u1F0D\u0399",
        [0x1F8E] = "\u1F0E\u0399",
        [0x1F8F] = "\u1F0F\u0399",
        [0x1F90] = "\u1F28\u0399",
        [0x1F91] = "\u1F29\u0399",
        [0x1F92] = "\u1F2A\u0399",
        [0x1F93] = "\u1F2B\u0399",
        [0x1F94] = "\u1F2C\u0399",
        [0x1F95] = "\u1F2D\u0399",
        [0x1F96] = "\u1F2E\u0399",
        [0x1F97] = "\u1F2F\u0399",
        [0x1F98] = "\u1F28\u0399",
        [0x1F99] = "\u1F29\u0399",
        [0x1F9A] = "\u1F2A\u0399",
        [0x1F9B] = "\u1F2B\u0399",
        [0x1F9C] = "\u1F2C\u0399",
        [0x1F9D] = "\u1F2D\u0399",
        [0x1F9E] = "\u1F2E\u0399",
        [0x1F9F] = "\u1F2F\u0399",
        [0x1FA0] = "\u1F68\u0399",
        [0x1FA1] = "\u1F69\u0399",
        [0x1FA2] = "\u1F6A\u0399",
        [0x1FA3] = "\u1F6B\u0399",
        [0x1FA4] = "\u1F6C\u0399",
        [0x1FA5] = "\u1F6D\u0399",
        [0x1FA6] = "\u1F6E\u0399",
        [0x1FA7] = "\u1F6F\u0399",
        [0x1FA8] = "\u1F68\u0399",
        [0x1FA9] = "\u1F69\u0399",
        [0x1FAA] = "\u1F6A\u0399",
        [0x1FAB] = "\u1F6B\u0399",
        [0x1FAC] = "\u1F6C\u0399",
        [0x1FAD] = "\u1F6D\u0399",
        [0x1FAE] = "\u1F6E\u0399",
        [0x1FAF] = "\u1F6F\u0399",
        [0x1FB2] = "\u1FBA\u0399",
        [0x1FB3] = "\u0391\u0399",
        [0x1FB4] = "\u0386\u0399",
        [0x1FB6] = "\u0391\u0342",
        [0x1FB7] = "\u0391\u0342\u0399",
        [0x1FBC] = "\u0391\u0399",
        [0x1FC2] = "\u1FCA\u0399",
        [0x1FC3] = "\u0397\u0399",
        [0x1FC4] = "\u0389\u0399",
        [0x1FC6] = "\u0397\u0342",
        [0x1FC7] = "\u0397\u0342\u0399",
        [0x1FCC] = "\u0397\u0399",
        [0x1FD2] = "\u0399\u0308\u0300",
        [0x1FD3] = "\u0399\u0308\u0301",
        [0x1FD6] = "\u0399\u0342",
        [0x1FD7] = "\u0399\u0308\u0342",
        [0x1FE2] = "\u03A5\u0308\u0300",
        [0x1FE3] = "\u03A5\u0308\u0301",
        [0x1FE4] = "\u03A1\u0313",
        [0x1FE6] = "\u03A5\u0342",
        [0x1FE7] = "\u03A5\u0308\u0342",
        [0x1FF2] = "\u1FFA\u0399",
        [0x1FF3] = "\u03A9\u0399",
        [0x1FF4] = "\u038F\u0399",
        [0x1FF6] = "\u03A9\u0342",
        [0x1FF7] = "\u03A9\u0342\u0399",
        [0x1FFC] = "\u03A9\u0399",
        [0xA7CD] = "\uA7CC",
        [0xA7DB] = "\uA7DA",
        [0xFB00] = "\u0046\u0046",
        [0xFB01] = "\u0046\u0049",
        [0xFB02] = "\u0046\u004C",
        [0xFB03] = "\u0046\u0046\u0049",
        [0xFB04] = "\u0046\u0046\u004C",
        [0xFB05] = "\u0053\u0054",
        [0xFB06] = "\u0053\u0054",
        [0xFB13] = "\u0544\u0546",
        [0xFB14] = "\u0544\u0535",
        [0xFB15] = "\u0544\u053B",
        [0xFB16] = "\u054E\u0546",
        [0xFB17] = "\u0544\u053D",
        [0x10D70] = "\U00010D50",
        [0x10D71] = "\U00010D51",
        [0x10D72] = "\U00010D52",
        [0x10D73] = "\U00010D53",
        [0x10D74] = "\U00010D54",
        [0x10D75] = "\U00010D55",
        [0x10D76] = "\U00010D56",
        [0x10D77] = "\U00010D57",
        [0x10D78] = "\U00010D58",
        [0x10D79] = "\U00010D59",
        [0x10D7A] = "\U00010D5A",
        [0x10D7B] = "\U00010D5B",
        [0x10D7C] = "\U00010D5C",
        [0x10D7D] = "\U00010D5D",
        [0x10D7E] = "\U00010D5E",
        [0x10D7F] = "\U00010D5F",
        [0x10D80] = "\U00010D60",
        [0x10D81] = "\U00010D61",
        [0x10D82] = "\U00010D62",
        [0x10D83] = "\U00010D63",
        [0x10D84] = "\U00010D64",
        [0x10D85] = "\U00010D65",
    };
}
