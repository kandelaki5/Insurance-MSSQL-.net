// Parity with the old pure modules of src/lib and with JavaScript's string behaviour: tests/parity/cases/common.json holds
// the old code's answers to many deterministic inputs (tests/parity/generate/common.ts); every one must come out the same.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Insurance.Application.Texts;
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class CommonParityTests
{
    private const string File = "common.json";

    private static IReadOnlyList<JsonElement> Cases(string section) => Parity.Cases(File, section);

    [Fact(DisplayName = "parity: parseCloseWithinDays")]
    public void ParseCloseWithinDays() => Parity.All(Cases("parseCloseWithinDays"), c =>
    {
        var e = c.GetProperty("out");
        return Parity.Diff(e.ValueKind == JsonValueKind.Null ? (int?)null : e.GetInt32(), CloseDeadline.ParseCloseWithinDays(c.GetProperty("in").StrOrNull()));
    });

    [Fact(DisplayName = "parity: closeWithinDays")]
    public void CloseWithinDays() => Parity.All(Cases("closeWithinDays"), c =>
        Parity.Diff(c.GetProperty("out").GetInt32(), CloseDeadline.CloseWithinDays(c.GetProperty("in").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Str()))));

    [Fact(DisplayName = "parity: closeDueDate and isCloseOverdue")]
    public void CloseDueDate() => Parity.All(Cases("closeDueDate"), c =>
    {
        var i = c.GetProperty("in");
        var month = i.GetProperty("month").Date();
        var days = i.GetProperty("days").GetInt32();
        var e = c.GetProperty("out");
        return Parity.Diff(e.GetProperty("due").Str(), Parity.Iso(CloseDeadline.CloseDueDate(month, days)))
            ?? Parity.Diff(e.GetProperty("overdue").GetBoolean(), CloseDeadline.IsCloseOverdue(month, i.GetProperty("today").Date(), days));
    });

    [Fact(DisplayName = "parity: isoToText")]
    public void IsoToText() => Parity.All(Cases("isoToText"), c => Parity.Diff(c.GetProperty("out").Str(), DateText.IsoToText(c.GetProperty("in").Str())));

    [Fact(DisplayName = "parity: textToIso")]
    public void TextToIso() => Parity.All(Cases("textToIso"), c => Parity.Diff(c.GetProperty("out").StrOrNull(), DateText.TextToIso(c.GetProperty("in").Str())));

    [Fact(DisplayName = "parity: typingMask")]
    public void TypingMask() => Parity.All(Cases("typingMask"), c => Parity.Diff(c.GetProperty("out").Str(), DateText.TypingMask(c.GetProperty("in").Str())));

    [Fact(DisplayName = "parity: caretAfter")]
    public void CaretAfter() => Parity.All(Cases("caretAfter"), c =>
    {
        var i = c.GetProperty("in");
        return Parity.Diff(c.GetProperty("out").GetInt32(), DateText.CaretAfter(i.GetProperty("shown").Str(), i.GetProperty("typed").Str(), i.GetProperty("caret").GetInt32()));
    });

    [Fact(DisplayName = "parity: isMonthStart")]
    public void IsMonthStart() => Parity.All(Cases("isMonthStart"), c => Parity.Diff(c.GetProperty("out").GetBoolean(), Month.IsMonthStart(c.GetProperty("in").StrOrNull())));

    [Fact(DisplayName = "parity: monthNameKa")]
    public void MonthNameKa() => Parity.All(Cases("monthNameKa"), c => Parity.Diff(c.GetProperty("out").Str(), Month.MonthNameKa(c.GetProperty("in").Str())));

    [Fact(DisplayName = "parity: formatNumber")]
    public void FormatNumber() => Parity.All(Cases("formatNumber"), c =>
    {
        var i = c.GetProperty("in");
        return Parity.Diff(c.GetProperty("out").StrOrNull(), Numbering.FormatNumber(i.GetProperty("prefix").Str(), i.GetProperty("digits").GetInt32(), i.GetProperty("value").GetInt64()));
    });

    [Fact(DisplayName = "parity: checkSeries")]
    public void CheckSeries() => Parity.All(Cases("checkSeries"), c =>
    {
        var i = c.GetProperty("in");
        var problem = Numbering.CheckSeries(i.GetProperty("prefix").Str(), i.GetProperty("digits").GetDecimal(), i.GetProperty("value").GetInt64());
        return Parity.Diff(c.GetProperty("out").StrOrNull(), problem?.ToSnakeCode());
    });

    private static SeriesFormat Format(JsonElement e) => new(e.GetProperty("prefix").Str(), e.GetProperty("digits").GetInt32(), e.TryGetProperty("next", out var n) ? n.GetInt64() : 0);

    [Fact(DisplayName = "parity: formatsOverlap")]
    public void FormatsOverlap() => Parity.All(Cases("formatsOverlap"), c =>
    {
        var i = c.GetProperty("in");
        return Parity.Diff(c.GetProperty("out").GetBoolean(), Numbering.FormatsOverlap(Format(i.GetProperty("a")), Format(i.GetProperty("b"))));
    });

    [Fact(DisplayName = "parity: kindsClash")]
    public void KindsClash() => Parity.All(Cases("kindsClash"), c =>
    {
        var a = Format(c.GetProperty("in").GetProperty("a"));
        var b = Format(c.GetProperty("in").GetProperty("b"));
        return Parity.Diff(c.GetProperty("out").GetBoolean(), Numbering.KindsClash((a.Prefix, a.Digits), (b.Prefix, b.Digits)));
    });

    [Fact(DisplayName = "parity: normalizePhone")]
    public void NormalizePhone() => Parity.All(Cases("normalizePhone"), c =>
    {
        var i = c.GetProperty("in");
        return Parity.Diff(c.GetProperty("out").StrOrNull(), Phone.NormalizePhone(i.GetProperty("code").Str(), i.GetProperty("number").Str()));
    });

    [Fact(DisplayName = "parity: splitPhone")]
    public void SplitPhone() => Parity.All(Cases("splitPhone"), c =>
    {
        var e = c.GetProperty("out");
        return Parity.Diff(new PhoneParts(e.GetProperty("code").Str(), e.GetProperty("number").Str()), Phone.SplitPhone(c.GetProperty("in").StrOrNull()));
    });

    [Fact(DisplayName = "parity: formatPhone")]
    public void FormatPhone() => Parity.All(Cases("formatPhone"), c => Parity.Diff(c.GetProperty("out").Str(), Phone.FormatPhone(c.GetProperty("in").StrOrNull())));

    [Fact(DisplayName = "parity: known")]
    public void Known() => Parity.All(Cases("known"), c =>
    {
        var map = c.GetProperty("in").GetProperty("map").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Str());
        return Parity.Diff(c.GetProperty("out").StrOrNull(), QueryParams.Known(map, c.GetProperty("in").GetProperty("key").StrOrNull()));
    });

    [Fact(DisplayName = "parity: singleParams")]
    public void SingleParams() => Parity.All(Cases("singleParams"), c =>
    {
        // The old code got a string for a key given once and an array for a repeated key; here both are lists of values.
        var input = c.GetProperty("in").EnumerateObject().Select(p => new KeyValuePair<string, IReadOnlyList<string>?>(p.Name,
            p.Value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.Array => p.Value.Strings(), _ => [p.Value.Str()] })).ToList();
        var actual = QueryParams.SingleParams(input);
        var expected = c.GetProperty("out").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.StrOrNull());
        return Parity.Diff(string.Join(";", expected.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value ?? "(null)"}")),
            string.Join(";", actual.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value ?? "(null)"}")));
    });

    private static string Show(PermissionResult r) =>
        $"{r.Permission}|{r.Allowed}|{r.Source.ToKebabCode()}|{(r.Reason is { } reason ? reason.ToKebabCode() : "-")}|{(r.Groups is null ? "-" : string.Join(",", r.Groups))}";

    private static string Show(JsonElement e) =>
        $"{e.GetProperty("permission").Str()}|{e.GetProperty("allowed").GetBoolean()}|{e.GetProperty("source").Str()}|{e.Prop("reason")?.Str() ?? "-"}|{(e.Prop("groups") is { } g ? string.Join(",", g.Strings()) : "-")}";

    private static PermissionGroups? Groups(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : new PermissionGroups(
        e.GetProperty("memberships").EnumerateArray().Select(m => new GroupMembership(m.GetProperty("userId").Str(), m.GetProperty("groupId").Str())).ToList(),
        e.GetProperty("grants").EnumerateArray().Select(g => new GroupGrant(g.GetProperty("groupId").Str(), g.GetProperty("permission").Str())).ToList());

    private static List<PermissionOverride> Overrides(JsonElement e) =>
        e.EnumerateArray().Select(o => new PermissionOverride(o.GetProperty("userId").Str(), o.GetProperty("permission").Str(), o.GetProperty("decision").Str())).ToList();

    private static PermissionSubject Subject(JsonElement e) => new(e.GetProperty("userId").Str(), e.GetProperty("roleCode").Str());

    [Fact(DisplayName = "parity: evaluatePermission")]
    public void EvaluatePermission() => Parity.All(Cases("evaluatePermission"), c =>
    {
        var i = c.GetProperty("in");
        var r = Permissions.EvaluatePermission(Subject(i.GetProperty("subject")), i.GetProperty("permission").Str(), Overrides(i.GetProperty("overrides")), Groups(i.GetProperty("groups")));
        return Parity.Diff(Show(c.GetProperty("out")), Show(r));
    });

    [Fact(DisplayName = "parity: evaluateAll")]
    public void EvaluateAll() => Parity.All(Cases("evaluateAll"), c =>
    {
        var i = c.GetProperty("in");
        var r = Permissions.EvaluateAll(Subject(i.GetProperty("subject")), Overrides(i.GetProperty("overrides")), Groups(i.GetProperty("groups")));
        return Parity.Diff(string.Join("\n", c.GetProperty("out").EnumerateArray().Select(Show)), string.Join("\n", r.Select(Show)));
    });

    [Fact(DisplayName = "parity: catalogue")]
    public void Catalogue()
    {
        var expected = Cases("catalogue").Select(e => $"{e.GetProperty("key").Str()}|{e.GetProperty("name").Str()}|{e.GetProperty("section").Str()}|{string.Join(",", e.GetProperty("roles").Strings())}");
        var actual = Permissions.Catalogue(PermissionTexts.Permissions, PermissionTexts.Sections).Select(i => $"{i.Key}|{i.Name}|{i.Section}|{string.Join(",", i.Roles)}");
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "parity: hashes made by the old code verify here, and only for their own password")]
    public void PasswordHashes() => Parity.All(Cases("passwordHashes"), c =>
    {
        var password = c.GetProperty("password").Str();
        var stored = c.GetProperty("stored").Str();
        return Parity.Diff(c.GetProperty("verifies").GetBoolean(), Password.VerifyPassword(password, stored))
            ?? Parity.Diff(c.GetProperty("otherVerifies").GetBoolean(), Password.VerifyPassword(password + "x", stored));
    });

    [Fact(DisplayName = "parity: scrypt keys equal Node's byte for byte, so hashes made here verify in the old code")]
    public void ScryptKeys() => Parity.All(Cases("scryptKeys"), c =>
    {
        var salt = Convert.FromHexString(c.GetProperty("saltHex").Str());
        var key = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(c.GetProperty("password").Str()), salt, 16384, 8, 1, c.GetProperty("keylen").GetInt32());
        return Parity.Diff(c.GetProperty("key").Str(), Convert.ToHexStringLower(key));
    });

    [Fact(DisplayName = "parity: verifyPassword on altered and malformed stored values")]
    public void VerifyPassword() => Parity.All(Cases("verifyPassword"), c =>
        Parity.Diff(c.GetProperty("verifies").GetBoolean(), Password.VerifyPassword(c.GetProperty("password").Str(), c.GetProperty("stored").Str())));

    [Fact(DisplayName = "parity: toUpperCase of every code point that changes")]
    public void ToUpperCase() => Parity.All(Cases("toUpperCase"), c =>
        Parity.Diff(c[1].Str(), JsText.ToUpperCase(char.ConvertFromUtf32(c[0].GetInt32()))));

    [Fact(DisplayName = "toUpperCase leaves every other code point as it is")]
    public void ToUpperCaseUnchanged()
    {
        var changing = Cases("toUpperCase").Select(c => c[0].GetInt32()).ToHashSet();
        var wrong = new List<string>();
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF || changing.Contains(cp)) continue;
            var s = char.ConvertFromUtf32(cp);
            if (JsText.ToUpperCase(s) != s) wrong.Add($"U+{cp:X4}");
        }
        Assert.Empty(wrong);
    }

    [Fact(DisplayName = "parity: trim")]
    public void Trim() => Parity.All(Cases("trim"), c => Parity.Diff(c.GetProperty("out").Str(), JsText.Trim(c.GetProperty("in").Str())));

    [Fact(DisplayName = "parity: Number")]
    public void Number() => Parity.All(Cases("number"), c =>
    {
        var v = JsNumber.Parse(c.GetProperty("in").Str());
        var shown = double.IsNaN(v) ? "NaN" : double.IsPositiveInfinity(v) ? "Infinity" : double.IsNegativeInfinity(v) ? "-Infinity" : v == 0 && double.IsNegative(v) ? "-0" : null;
        var expected = c.GetProperty("out").Str();
        if (shown is not null) return Parity.Diff(expected, shown);
        return v == double.Parse(expected, CultureInfo.InvariantCulture) ? null : $"expected {expected}, got {v.ToString("R", CultureInfo.InvariantCulture)}";
    });
}
