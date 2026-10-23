// The fingerprints that decide whether a sanctions screening is the same as an earlier one (owner, 23.10.2026).
using Insurance.Domain.Compliance;

namespace Insurance.Domain.Tests.Compliance;

public class ScreeningFingerprintTests
{
    private static readonly Guid Id = Guid.Parse("6f1c1f7e-2b7a-4d43-9a7e-0e5d1c9a0b11");

    private static ScreeningSubject Subject(string name = "სანქციონელი ზურაბ", string? year = "1980", params string[] documents) =>
        new(name, SanctionKind.Individual, ["ზურაბ სანქციონელი", "Zurab Sankcioneli"], year, documents.Length == 0 ? ["01001012345"] : documents);

    private static ScreeningMatch Match(string id = "1", decimal score = 1m, params MatchReason[] reasons) =>
        new(id, "Zurab Sankcioneli", "T.001", "Test", score, "Zurabi Sanktsioneli", true, reasons.Length == 0 ? [MatchReason.Name, MatchReason.BirthYear] : reasons);

    private static SanctionEntry Entry(string id, string reference = "T.001") =>
        new(id, SanctionKind.Individual, "Zurab Sankcioneli", ["Zurabi Sanktsioneli"], "Test", reference, "2012-12-31", ["Georgia"], ["1980"], []);

    [Fact(DisplayName = "the same JSON with other white space and key order has one canonical text")]
    public void CanonicalText()
    {
        var compact = """[{"dataId":"1","name":"x","reasons":["name","birth_year"],"score":1,"strong":true}]""";
        var jsonb = """[{"name": "x", "score": 1, "dataId": "1", "strong": true, "reasons": ["name", "birth_year"]}]""";
        Assert.Equal(ScreeningFingerprint.Canonical(compact), ScreeningFingerprint.Canonical(jsonb));
        Assert.Equal("""[{"dataId":"1","name":"x","reasons":["name","birth_year"],"score":1,"strong":true}]""", ScreeningFingerprint.Canonical(jsonb));
    }

    [Fact(DisplayName = "the order of matches, of reasons, of aliases and of documents does not count")]
    public void OrderDoesNotCount()
    {
        Assert.Equal(ScreeningFingerprint.Matches([Match("1"), Match("2")]), ScreeningFingerprint.Matches([Match("2"), Match("1")]));
        Assert.Equal(ScreeningFingerprint.Matches([Match(reasons: [MatchReason.Name, MatchReason.BirthYear])]),
            ScreeningFingerprint.Matches([Match(reasons: [MatchReason.BirthYear, MatchReason.Name])]));
        var a = new ScreeningSubject("n", SanctionKind.Individual, ["b", "a"], "1980", ["2", "1"]);
        var b = new ScreeningSubject("n", SanctionKind.Individual, ["a", "b", "a"], "1980", ["1", "2"]);
        Assert.Equal(ScreeningFingerprint.Subject("party", Id, a, null), ScreeningFingerprint.Subject("party", Id, b, null));
        Assert.Equal(ScreeningFingerprint.List("un", null, [Entry("1"), Entry("2")]), ScreeningFingerprint.List("un", null, [Entry("2"), Entry("1")]));
    }

    [Fact(DisplayName = "any change of the subject's screening data, the matches or the list changes its fingerprint")]
    public void ChangesCount()
    {
        var birth = new DateOnly(1980, 5, 1);
        var subject = ScreeningFingerprint.Subject("party", Id, Subject(), birth);
        Assert.All(new[]
        {
            ScreeningFingerprint.Subject("party", Id, Subject(name: "სანქციონელი ზურაბი"), birth),
            ScreeningFingerprint.Subject("party", Id, Subject(year: "1981"), birth),
            ScreeningFingerprint.Subject("party", Id, Subject(documents: ["01001012346"]), birth),
            ScreeningFingerprint.Subject("party", Id, Subject() with { Kind = SanctionKind.Entity }, birth),
            ScreeningFingerprint.Subject("party", Id, Subject() with { Aliases = ["ზურაბ სანქციონელი"] }, birth),
            ScreeningFingerprint.Subject("party", Id, Subject(), birth.AddDays(1)),
            ScreeningFingerprint.Subject("owner", Id, Subject(), birth),
            ScreeningFingerprint.Subject("party", Guid.NewGuid(), Subject(), birth),
        }, other => Assert.NotEqual(subject, other));

        var matches = ScreeningFingerprint.Matches([Match()]);
        Assert.Equal(matches, ScreeningFingerprint.Matches([Match(score: 1.00m)]));
        Assert.NotEqual(matches, ScreeningFingerprint.Matches([Match(score: 0.99m)]));
        Assert.NotEqual(matches, ScreeningFingerprint.Matches([Match(), Match("2")]));
        Assert.NotEqual(matches, ScreeningFingerprint.Matches([]));
        Assert.NotEqual(matches, ScreeningFingerprint.Matches([Match(reasons: [MatchReason.Name])]));

        var at = new DateTimeOffset(2026, 10, 2, 23, 0, 5, 487, TimeSpan.Zero);
        var list = ScreeningFingerprint.List("un", at, [Entry("1")]);
        Assert.NotEqual(list, ScreeningFingerprint.List("un", at.AddDays(1), [Entry("1")]));
        Assert.NotEqual(list, ScreeningFingerprint.List("un", at, [Entry("1", "T.009")]));
        Assert.NotEqual(list, ScreeningFingerprint.List("un", at, [Entry("1"), Entry("2")]));
        Assert.NotEqual(list, ScreeningFingerprint.List("un", null, [Entry("1")]));
    }

    [Fact(DisplayName = "a fixed input gives a fixed fingerprint, so a change of the serializer cannot pass unseen")]
    public void Golden()
    {
        Assert.Equal(64, ScreeningFingerprint.Matches([]).Length);
        // SHA-256 of {"matches":[],"v":1}.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("""{"matches":[],"v":1}"""u8.ToArray())), ScreeningFingerprint.Matches([]));
        // Computed independently (Python: json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False), then SHA-256).
        Assert.Equal("abd9552b76110a2d8fb9d6542f7e24d7540cb4715e4ef82d4f41445b3329a9f0", ScreeningFingerprint.Subject("party", Id, Subject(), new DateOnly(1980, 5, 1)));
    }
}
