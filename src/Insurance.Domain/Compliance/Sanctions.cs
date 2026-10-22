// Screening against the UN Security Council consolidated sanctions list.
//
// AML law art. 10 p. 7, art. 41; FMS order №1 art. 4; order №55 art. 1 p. 5–6: the client, its
// representatives and beneficial owners are checked against the UN list before the relationship
// starts and again whenever the list changes; a hit is examined at once, and on a confirmed
// match service stops and the FMS is informed. Insurer fines: 3 000 GEL per operation with a
// listed person, 5 000 GEL for not freezing assets (order №02 art. 3 "თ", "ი").
//
// This module is pure: it parses the UN XML and compares names. Georgian names are
// transliterated to Latin before comparison, since the list is in Latin script.
// Ported from src/compliance/sanctions.ts.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Domain.Common;

namespace Insurance.Domain.Compliance;

/// <summary>Codes 'individual', 'entity'.</summary>
public enum SanctionKind { Individual, Entity }

/// <param name="ListedOn">The date part of LISTED_ON as written in the list (not checked to be a real date), or null.</param>
public sealed record SanctionEntry(
    string DataId,
    SanctionKind Kind,
    string Name,
    IReadOnlyList<string> Aliases,
    string ListType,
    string Reference,
    string? ListedOn,
    IReadOnlyList<string> Nationalities,
    IReadOnlyList<string> BirthYears,
    IReadOnlyList<string> Documents);

/// <param name="Aliases">Other spellings known for the subject (e.g. the Latin name of a foreign client).</param>
public sealed record ScreeningSubject(
    string Name,
    SanctionKind Kind,
    IReadOnlyList<string>? Aliases = null,
    string? BirthYear = null,
    IReadOnlyList<string>? Documents = null);

/// <summary>Codes 'name', 'birth_year', 'document'.</summary>
public enum MatchReason { Name, BirthYear, Document }

/// <param name="Score">0–1, two decimals: share of the subject's name tokens found in the listed name or an alias.</param>
public sealed record ScreeningMatch(
    string DataId,
    string Name,
    string Reference,
    string ListType,
    decimal Score,
    string MatchedName,
    bool Strong,
    IReadOnlyList<MatchReason> Reasons);

public sealed record UnList(string? GeneratedAt, IReadOnlyList<SanctionEntry> Entries);

public static partial class Sanctions
{
    [GeneratedRegex("&#x([0-9a-f]+);", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HexReference();

    [GeneratedRegex("&#([0-9]+);", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalReference();

    [GeneratedRegex("dateGenerated=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex DateGenerated();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex DatePart();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotTokenCharacters();

    [GeneratedRegex("[^A-Z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NotDocumentCharacters();

    // String.fromCodePoint: a lone surrogate is kept as one; beyond U+10FFFF it throws (RangeError in the old code).
    private static string FromCodePoint(BigInteger cp)
    {
        if (cp > 0x10FFFF) throw new ArgumentOutOfRangeException(nameof(cp), $"Invalid code point {cp}");
        var value = (int)cp;
        return value is >= 0xD800 and <= 0xDFFF ? ((char)value).ToString() : char.ConvertFromUtf32(value);
    }

    private static string Decode(string text)
    {
        var s = HexReference().Replace(text, m => FromCodePoint(BigInteger.Parse("0" + m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        s = DecimalReference().Replace(s, m => FromCodePoint(BigInteger.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        s = s.Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
        return JsText.Trim(s);
    }

    private static readonly Dictionary<string, Regex> TagPatterns = new();

    private static Regex Pattern(string name, bool section)
    {
        var key = (section ? "s:" : "t:") + name;
        lock (TagPatterns)
        {
            if (!TagPatterns.TryGetValue(key, out var regex))
            {
                regex = new Regex(section ? $"<{name}>([\\s\\S]*?)</{name}>" : $"<{name}>([^<]*)</{name}>", RegexOptions.CultureInvariant);
                TagPatterns[key] = regex;
            }
            return regex;
        }
    }

    private static string? Tag(string block, string name)
    {
        var m = Pattern(name, false).Match(block);
        return m.Success && JsText.Trim(m.Groups[1].Value) != "" ? Decode(m.Groups[1].Value) : null;
    }

    private static List<string> Tags(string block, string name) =>
        Pattern(name, false).Matches(block).Select(m => Decode(m.Groups[1].Value)).Where(v => v != "").ToList();

    private static List<string> Sections(string block, string name) =>
        Pattern(name, true).Matches(block).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Parses the UN consolidated list XML into entries; returns the list's generation time too.</summary>
    public static UnList ParseUnList(string xml)
    {
        var generated = DateGenerated().Match(xml);
        var generatedAt = generated.Success ? generated.Groups[1].Value : null;
        var entries = new List<SanctionEntry>();
        SanctionEntry Read(string block, SanctionKind kind)
        {
            var listedOn = Tag(block, "LISTED_ON");
            // A few entries carry a timezone suffix ("2015-03-27-04:00"); the date part is enough.
            var listedDate = listedOn is null ? null : DatePart().Match(listedOn) is { Success: true } d ? d.Value : null;
            return new SanctionEntry(
                DataId: Tag(block, "DATAID") ?? "",
                Kind: kind,
                Name: string.Join(' ', new[] { "FIRST_NAME", "SECOND_NAME", "THIRD_NAME", "FOURTH_NAME" }.Select(n => Tag(block, n)).Where(n => !string.IsNullOrEmpty(n))),
                Aliases: Tags(block, "ALIAS_NAME"),
                ListType: Tag(block, "UN_LIST_TYPE") ?? "",
                Reference: Tag(block, "REFERENCE_NUMBER") ?? "",
                ListedOn: listedDate,
                Nationalities: Sections(block, "NATIONALITY").SelectMany(s => Tags(s, "VALUE")).ToList(),
                BirthYears: Sections(block, kind == SanctionKind.Individual ? "INDIVIDUAL_DATE_OF_BIRTH" : "NEVER")
                    .SelectMany(s => Tags(s, "YEAR").Concat(Tags(s, "DATE").Select(x => x.Length > 4 ? x[..4] : x))).ToList(),
                Documents: Sections(block, "INDIVIDUAL_DOCUMENT").SelectMany(s => Tags(s, "NUMBER")).Select(NormalizeDocument).ToList());
        }
        foreach (var block in Sections(xml, "INDIVIDUAL")) entries.Add(Read(block, SanctionKind.Individual));
        foreach (var block in Sections(xml, "ENTITY")) entries.Add(Read(block, SanctionKind.Entity));
        return new UnList(generatedAt, entries.Where(e => e.DataId != "" && e.Name != "").ToList());
    }

    private static readonly Dictionary<char, string> Georgian = new()
    {
        ['ა'] = "a", ['ბ'] = "b", ['გ'] = "g", ['დ'] = "d", ['ე'] = "e", ['ვ'] = "v", ['ზ'] = "z", ['თ'] = "t", ['ი'] = "i", ['კ'] = "k", ['ლ'] = "l", ['მ'] = "m", ['ნ'] = "n", ['ო'] = "o", ['პ'] = "p",
        ['ჟ'] = "zh", ['რ'] = "r", ['ს'] = "s", ['ტ'] = "t", ['უ'] = "u", ['ფ'] = "p", ['ქ'] = "k", ['ღ'] = "gh", ['ყ'] = "q", ['შ'] = "sh", ['ჩ'] = "ch", ['ც'] = "ts", ['ძ'] = "dz", ['წ'] = "ts",
        ['ჭ'] = "ch", ['ხ'] = "kh", ['ჯ'] = "j", ['ჰ'] = "h",
    };

    /// <summary>National transliteration of Georgian script to Latin.</summary>
    public static string TransliterateGeorgian(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (Georgian.TryGetValue(c, out var latin)) sb.Append(latin);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Words that say what a company is, not who it is.
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "shps", "ss", "llc", "ltd", "jsc", "inc", "co", "company", "corp", "corporation", "limited", "gmbh", "ag", "sa", "the", "of", "and", "al",
    };

    /// <summary>Lowercase Latin tokens without diacritics, punctuation or generic company words.</summary>
    public static IReadOnlyList<string> NameTokens(string name)
    {
        var text = TransliterateGeorgian(name);
        // .NET refuses to normalise a lone surrogate; JavaScript keeps it, and it ends up a separator either way.
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) { i++; continue; }
            if (char.IsSurrogate(chars[i])) chars[i] = ' ';
        }
        var decomposed = new string(chars).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed) if (c is < '\u0300' or > '\u036f') sb.Append(c);
        // Only a-z and 0-9 survive the next step, so lowering ASCII is all of toLowerCase() that matters here
        // (letters that lower to ASCII outside it, like the Kelvin sign, were decomposed above).
        var lowered = sb.ToString().ToLowerInvariant();
        return NotTokenCharacters().Replace(lowered, " ").Split(' ').Where(t => t.Length >= 2 && !Generic.Contains(t)).ToList();
    }

    public static string NormalizeDocument(string value) => NotDocumentCharacters().Replace(JsText.ToUpperCase(value), "");

    /// <summary>True for equal tokens, or tokens of four letters or more that differ by one edit.</summary>
    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 4 || b.Length < 4 || Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (b.Length > a.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    // The ratio is computed in double, as the old code did, so that its rounding to two decimals is the same.
    private static double NameScore(IReadOnlyList<string> subjectTokens, string listedName)
    {
        var listed = NameTokens(listedName);
        if (subjectTokens.Count == 0 || listed.Count == 0) return 0;
        var used = new HashSet<int>();
        int common = 0;
        foreach (var token in subjectTokens)
        {
            int at = -1;
            for (int idx = 0; idx < listed.Count; idx++)
            {
                if (!used.Contains(idx) && Similar(token, listed[idx])) { at = idx; break; }
            }
            if (at >= 0) { used.Add(at); common++; }
        }
        // A single shared token is not a match unless it is the whole of both names.
        if (common < 2 && !(subjectTokens.Count == 1 && listed.Count == 1 && common == 1)) return 0;
        return (double)common / Math.Max(subjectTokens.Count, Math.Min(listed.Count, subjectTokens.Count + 1));
    }

    /// <summary>Compares one subject with the list; matches at or above the threshold, strongest first.</summary>
    public static IReadOnlyList<ScreeningMatch> ScreenSubject(ScreeningSubject subject, IReadOnlyList<SanctionEntry> entries, double threshold = 0.6)
    {
        var names = new[] { subject.Name }.Concat(subject.Aliases ?? []).Select(NameTokens).Where(t => t.Count > 0).ToList();
        var documents = (subject.Documents ?? []).Select(NormalizeDocument).Where(d => d.Length >= 4).ToList();
        var matches = new List<ScreeningMatch>();
        foreach (var entry in entries)
        {
            if (entry.Kind != subject.Kind) continue;
            double best = 0;
            var matchedName = entry.Name;
            foreach (var tokens in names)
            {
                foreach (var candidate in new[] { entry.Name }.Concat(entry.Aliases))
                {
                    var score = NameScore(tokens, candidate);
                    if (score > best) { best = score; matchedName = candidate; }
                }
            }
            var reasons = new List<MatchReason>();
            if (best >= threshold) reasons.Add(MatchReason.Name);
            if (documents.Any(d => entry.Documents.Contains(d, StringComparer.Ordinal))) reasons.Add(MatchReason.Document);
            if (best >= threshold && !string.IsNullOrEmpty(subject.BirthYear) && entry.BirthYears.Contains(subject.BirthYear, StringComparer.Ordinal)) reasons.Add(MatchReason.BirthYear);
            if (reasons.Count == 0) continue;
            matches.Add(new ScreeningMatch(
                DataId: entry.DataId,
                Name: entry.Name,
                Reference: entry.Reference,
                ListType: entry.ListType,
                // Number(best.toFixed(2)): .NET's "F2" rounds the exact double value as toFixed does.
                Score: decimal.Parse(best.ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                MatchedName: matchedName,
                Strong: reasons.Contains(MatchReason.Document) || (reasons.Contains(MatchReason.BirthYear) && best >= 0.8) || best >= 0.95,
                Reasons: reasons));
        }
        // A stable sort, as Array.prototype.sort is: strong first, then the higher score.
        return matches.OrderByDescending(m => m.Strong).ThenByDescending(m => m.Score).ToList();
    }
}
