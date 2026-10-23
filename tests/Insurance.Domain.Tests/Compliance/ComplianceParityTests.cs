// Parity with the old src/compliance/*.ts: tests/parity/cases/compliance.json holds its answers to many deterministic
// inputs (tests/parity/generate/compliance.ts); every one must come out the same here.
using System.Globalization;
using System.Text.Json;
using Insurance.Domain.Common;
using Insurance.Domain.Compliance;
using Insurance.Domain.Tests.Common;

namespace Insurance.Domain.Tests.Compliance;

public class ComplianceParityTests
{
    private const string File = "compliance.json";

    private static IReadOnlyList<JsonElement> Cases(string section) => Parity.Cases(File, section);

    /// <summary>A JavaScript number as a decimal; beyond decimal's range it is held at decimal's largest value, as the port does.</summary>
    private static decimal Dec(JsonElement e) =>
        e.TryGetDecimal(out var d) ? d : e.GetDouble() > 0 ? decimal.MaxValue : decimal.MinValue;

    private static decimal? DecOrNull(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Dec(e);

    /// <summary>
    /// The port reads settings as exact decimals where JavaScript rounds to a double: the same value means the C# decimal
    /// rounds to the old double (or both are beyond decimal's range).
    /// </summary>
    private static bool SameNumber(JsonElement expected, decimal actual)
    {
        var e = expected.GetDouble();
        if (e >= (double)decimal.MaxValue) return actual == decimal.MaxValue;
        if (e <= (double)decimal.MinValue) return actual == decimal.MinValue;
        return (double)actual == e;
    }

    private static RiskPolicy? Policy(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : new RiskPolicy(
        e.GetProperty("highRiskCountries").Strings(),
        Dec(e.GetProperty("premiumPointOneGel")),
        Dec(e.GetProperty("premiumPointTwoGel")),
        Dec(e.GetProperty("sanctionsCheckMaxAgeDays")),
        Dec(e.GetProperty("mediumScore")),
        Dec(e.GetProperty("highScore")),
        new ReviewMonths(Dec(e.GetProperty("reviewMonths").GetProperty("high")), Dec(e.GetProperty("reviewMonths").GetProperty("medium")), Dec(e.GetProperty("reviewMonths").GetProperty("low"))));

    private static T Code<T>(JsonElement e) where T : struct, Enum =>
        EnumCode.TryParseSnakeCode<T>(e.Str(), out var v) ? v : throw new InvalidOperationException($"unknown {typeof(T).Name} {e}");

    /// <summary>Compares an expected outcome ({out} or {throws}) with what the C# code does.</summary>
    private static string? Outcome<T>(JsonElement c, Func<T> run, Func<JsonElement, T, string?> compare)
    {
        if (c.TryGetProperty("throws", out _))
        {
            try { run(); } catch (Exception) { return null; }
            return "expected an error, got a result";
        }
        return compare(c.GetProperty("out"), run());
    }

    [Fact(DisplayName = "parity: riskPolicyFromSettings")]
    public void RiskPolicyFromSettings() => Parity.All(Cases("riskPolicyFromSettings"), c =>
    {
        var settings = c.GetProperty("in").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Str());
        var p = ClientRisk.RiskPolicyFromSettings(settings);
        var e = c.GetProperty("out");
        if (!e.GetProperty("highRiskCountries").Strings().SequenceEqual(p.HighRiskCountries)) return $"countries {string.Join(",", p.HighRiskCountries)}";
        var pairs = new (string, decimal)[]
        {
            ("premiumPointOneGel", p.PremiumPointOneGel), ("premiumPointTwoGel", p.PremiumPointTwoGel), ("sanctionsCheckMaxAgeDays", p.SanctionsCheckMaxAgeDays),
            ("mediumScore", p.MediumScore), ("highScore", p.HighScore),
        };
        foreach (var (name, value) in pairs) if (!SameNumber(e.GetProperty(name), value)) return $"{name} {value}";
        var months = e.GetProperty("reviewMonths");
        if (!SameNumber(months.GetProperty("high"), p.ReviewMonths.High) || !SameNumber(months.GetProperty("medium"), p.ReviewMonths.Medium) || !SameNumber(months.GetProperty("low"), p.ReviewMonths.Low)) return $"months {p.ReviewMonths}";
        return null;
    });

    [Fact(DisplayName = "parity: assessClientRisk")]
    public void AssessClientRisk() => Parity.All(Cases("assessClientRisk"), c =>
    {
        var i = c.GetProperty("in");
        var f = i.GetProperty("facts");
        var facts = new ClientFacts(
            Code<ClientKind>(f.GetProperty("kind")),
            f.GetProperty("isResident").GetBoolean(),
            f.GetProperty("countryCode").Str(),
            f.GetProperty("isPep").GetBoolean(),
            f.GetProperty("isPublicEntity").GetBoolean(),
            DecOrNull(f.GetProperty("stateSharePercent")),
            f.GetProperty("verifiedOn").DateOrNull(),
            f.GetProperty("sanctionsCheckedOn").DateOrNull(),
            Dec(f.GetProperty("annualPremiumGel")),
            Dec(f.GetProperty("previousYearPremiumGel")));
        var asOf = i.GetProperty("asOf").Date();
        var policy = Policy(i.GetProperty("policy"));
        return Outcome(c, () => ClientRisk.AssessClientRisk(facts, asOf, policy), (e, r) =>
        {
            var factors = e.GetProperty("factors").EnumerateArray().Select(x => new RiskFactor(Code<FactorCode>(x.GetProperty("code")), x.GetProperty("points").GetInt32(), x.GetProperty("decisive").GetBoolean())).ToList();
            if (Code<RiskLevel>(e.GetProperty("level")) != r.Level) return $"level {r.Level}";
            if (e.GetProperty("score").GetInt32() != r.Score) return $"score {r.Score}";
            if (!factors.SequenceEqual(r.Factors)) return $"factors {string.Join(",", r.Factors)}";
            return Parity.Diff(e.GetProperty("reviewDueOn").Str(), Parity.Iso(r.ReviewDueOn));
        });
    });

    [Fact(DisplayName = "parity: reviewDueFor")]
    public void ReviewDueFor() => Parity.All(Cases("reviewDueFor"), c =>
    {
        var i = c.GetProperty("in");
        var level = Code<RiskLevel>(i.GetProperty("level"));
        var on = i.GetProperty("on").Date();
        var policy = Policy(i.GetProperty("policy"));
        return Outcome(c, () => ClientRisk.ReviewDueFor(level, on, policy), (e, r) => Parity.Diff(e.Str(), Parity.Iso(r)));
    });

    [Fact(DisplayName = "parity: kycStatus")]
    public void KycStatus() => Parity.All(Cases("kycStatus"), c =>
    {
        var i = c.GetProperty("in");
        var x = i.GetProperty("input");
        var input = new KycInput(
            Code<ClientKind>(x.GetProperty("kind")),
            Dec(x.GetProperty("annualPremiumGel")),
            x.GetProperty("hasLifePolicy").GetBoolean(),
            x.GetProperty("identifiedOn").DateOrNull(),
            x.GetProperty("verifiedOn").DateOrNull(),
            x.GetProperty("relationshipFrom").DateOrNull(),
            x.GetProperty("owners").EnumerateArray().Select(o => new KycOwner(o.GetProperty("verifiedOn").DateOrNull(), o.GetProperty("identifiedOn").DateOrNull())).ToList(),
            x.GetProperty("selfDeclarationOn").DateOrNull());
        var t = i.GetProperty("thresholds");
        KycThresholds? thresholds = t.ValueKind == JsonValueKind.Null ? null : new KycThresholds(
            Dec(t.GetProperty("reducedMaxGel")),
            new VerificationFreeMaxGel(Dec(t.GetProperty("verificationFreeMaxGel").GetProperty("person")), Dec(t.GetProperty("verificationFreeMaxGel").GetProperty("organization"))),
            t.GetProperty("ownerVerificationDeferralDays").GetInt32());
        var s = Kyc.KycStatus(input, i.GetProperty("asOf").Date(), thresholds);
        var e = c.GetProperty("out");
        if (Code<KycTier>(e.GetProperty("tier")) != s.Tier) return $"tier {s.Tier}";
        if (DecOrNull(e.GetProperty("nextThresholdGel")) != s.NextThresholdGel) return $"next {s.NextThresholdGel}";
        if (e.GetProperty("clientVerificationRequired").GetBoolean() != s.ClientVerificationRequired) return "client verification";
        if (e.GetProperty("ownerVerificationRequired").GetBoolean() != s.OwnerVerificationRequired) return "owner verification";
        if (e.GetProperty("ownerVerificationDueOn").DateOrNull() != s.OwnerVerificationDueOn) return $"due {s.OwnerVerificationDueOn}";
        var issues = e.GetProperty("issues").EnumerateArray().Select(Code<KycIssue>).ToList();
        return issues.SequenceEqual(s.Issues) ? null : $"issues {string.Join(",", s.Issues)}";
    });

    [Fact(DisplayName = "parity: transliterateGeorgian")]
    public void TransliterateGeorgian() => Parity.All(Cases("transliterateGeorgian"), c =>
        Parity.Diff(c.GetProperty("out").Str(), Sanctions.TransliterateGeorgian(c.GetProperty("in").Str())));

    [Fact(DisplayName = "parity: nameTokens")]
    public void NameTokens() => Parity.All(Cases("nameTokens"), c =>
        Parity.Diff(string.Join("|", c.GetProperty("out").Strings()), string.Join("|", Sanctions.NameTokens(c.GetProperty("in").Str()))));

    [Fact(DisplayName = "parity: normalizeDocument")]
    public void NormalizeDocument() => Parity.All(Cases("normalizeDocument"), c =>
        Parity.Diff(c.GetProperty("out").Str(), Sanctions.NormalizeDocument(c.GetProperty("in").Str())));

    private static string? CompareEntry(JsonElement e, SanctionEntry a)
    {
        var expected = new[]
        {
            e.GetProperty("dataId").Str(), e.GetProperty("kind").Str(), e.GetProperty("name").Str(), string.Join("|", e.GetProperty("aliases").Strings()),
            e.GetProperty("listType").Str(), e.GetProperty("reference").Str(), e.GetProperty("listedOn").StrOrNull() ?? "(null)",
            string.Join("|", e.GetProperty("nationalities").Strings()), string.Join("|", e.GetProperty("birthYears").Strings()), string.Join("|", e.GetProperty("documents").Strings()),
        };
        var actual = new[]
        {
            a.DataId, a.Kind.ToSnakeCode(), a.Name, string.Join("|", a.Aliases), a.ListType, a.Reference, a.ListedOn ?? "(null)",
            string.Join("|", a.Nationalities), string.Join("|", a.BirthYears), string.Join("|", a.Documents),
        };
        for (int k = 0; k < expected.Length; k++) if (expected[k] != actual[k]) return $"field {k}: expected {expected[k]}, got {actual[k]}";
        return null;
    }

    private static SanctionEntry Entry(JsonElement e) => new(
        e.GetProperty("dataId").Str(), Code<SanctionKind>(e.GetProperty("kind")), e.GetProperty("name").Str(), e.GetProperty("aliases").Strings(),
        e.GetProperty("listType").Str(), e.GetProperty("reference").Str(), e.GetProperty("listedOn").StrOrNull(),
        e.GetProperty("nationalities").Strings(), e.GetProperty("birthYears").Strings(), e.GetProperty("documents").Strings());

    [Fact(DisplayName = "parity: parseUnList")]
    public void ParseUnList() => Parity.All(Cases("parseUnList"), c => Outcome(c, () => Sanctions.ParseUnList(c.GetProperty("in").Str()), (e, list) =>
    {
        if (e.GetProperty("generatedAt").StrOrNull() != list.GeneratedAt) return $"generatedAt {list.GeneratedAt}";
        var entries = e.GetProperty("entries").EnumerateArray().ToList();
        if (entries.Count != list.Entries.Count) return $"{list.Entries.Count} entries";
        for (int k = 0; k < entries.Count; k++) if (CompareEntry(entries[k], list.Entries[k]) is { } d) return $"entry {k}: {d}";
        return null;
    }));

    [Fact(DisplayName = "parity: screenSubject")]
    public void ScreenSubject()
    {
        var lists = Cases("parseUnList").Select(c => c.TryGetProperty("out", out var o) ? o.GetProperty("entries").EnumerateArray().Select(Entry).ToList() : []).ToList();
        Parity.All(Cases("screenSubject"), c =>
        {
            var i = c.GetProperty("in");
            var s = i.GetProperty("subject");
            var subject = new ScreeningSubject(
                s.GetProperty("name").Str(),
                Code<SanctionKind>(s.GetProperty("kind")),
                s.Prop("aliases")?.Strings(),
                s.Prop("birthYear")?.Str(),
                s.Prop("documents")?.Strings());
            var threshold = i.GetProperty("threshold");
            var matches = threshold.ValueKind == JsonValueKind.Null
                ? Sanctions.ScreenSubject(subject, lists[i.GetProperty("list").GetInt32()])
                : Sanctions.ScreenSubject(subject, lists[i.GetProperty("list").GetInt32()], threshold.GetDouble());
            var expected = c.GetProperty("out").EnumerateArray().Select(m => string.Join("~",
                m.GetProperty("dataId").Str(), m.GetProperty("name").Str(), m.GetProperty("reference").Str(), m.GetProperty("listType").Str(),
                m.GetProperty("score").GetDecimal().ToString("0.##", CultureInfo.InvariantCulture), m.GetProperty("matchedName").Str(), m.GetProperty("strong").GetBoolean(),
                string.Join(",", m.GetProperty("reasons").Strings()))).ToList();
            var actual = matches.Select(m => string.Join("~",
                m.DataId, m.Name, m.Reference, m.ListType, m.Score.ToString("0.##", CultureInfo.InvariantCulture), m.MatchedName, m.Strong,
                string.Join(",", m.Reasons.Select(x => x.ToSnakeCode())))).ToList();
            return Parity.Diff(string.Join("\n", expected), string.Join("\n", actual));
        });
    }

    [Fact(DisplayName = "parity: orthodoxEaster")]
    public void OrthodoxEaster() => Parity.All(Cases("orthodoxEaster"), c =>
        Parity.Diff(c.GetProperty("out").Str(), Parity.Iso(Suspension.OrthodoxEaster(c.GetProperty("in").GetInt32()))));

    [Fact(DisplayName = "parity: georgianHolidays")]
    public void GeorgianHolidays() => Parity.All(Cases("georgianHolidays"), c =>
        Parity.Diff(string.Join(",", c.GetProperty("out").Strings()), string.Join(",", Suspension.GeorgianHolidays(c.GetProperty("in").GetInt32()).Order().Select(Parity.Iso))));

    [Fact(DisplayName = "parity: isNonWorkingDay")]
    public void IsNonWorkingDay() => Parity.All(Cases("isNonWorkingDay"), c =>
        Parity.Diff(c.GetProperty("out").GetBoolean(), Suspension.IsNonWorkingDay(c.GetProperty("in").Date())));

    /// <summary>
    /// The old code took the Tbilisi day of an instant from the system's time-zone data and its next midnight at +04:00; the
    /// rewrite takes both at Georgia's +04:00 (TbilisiTime), so the answer no longer hangs on the operating system (Codex
    /// R04: on Windows 12 of these cases came out otherwise). The two agree on every case from 27 March 2005, when +04:00
    /// came into force. Before it the old answers mixed that day's offset (+03, or +05 in summer) with a +04:00 midnight —
    /// one 48-hour suspension of 18.03.2005 ended after 7 hours — so there each answer of the rewrite is instead checked to
    /// hold exactly the hours asked of working time (or to stop where the old walk's 100-day limit stops it), and the cases
    /// where the two part are counted and all lie before that day.
    /// </summary>
    [Fact(DisplayName = "parity: suspensionEnd, equal from 27.03.2005 on; earlier ones hold their working hours (R04)")]
    public void SuspensionEnd()
    {
        var parted = new List<string>();
        Parity.All(Cases("suspensionEnd"), c =>
        {
            var i = c.GetProperty("in");
            var start = DateTimeOffset.Parse(i.GetProperty("startedAt").Str(), CultureInfo.InvariantCulture);
            var hours = i.GetProperty("hours");
            var h = hours.ValueKind == JsonValueKind.Null ? 72 : hours.GetInt32();
            var end = hours.ValueKind == JsonValueKind.Null ? Suspension.SuspensionEnd(start) : Suspension.SuspensionEnd(start, h);
            if (Parity.Diff(c.GetProperty("out").Str(), Parity.Iso(end)) is not { } difference) return null;
            if (start >= TbilisiTime.Since) return difference;
            parted.Add($"{Parity.Iso(start)}/{h}");
            // The walk looks at 100 days at most, as the old one did: hours it cannot find in them end at that 100th day's end.
            var horizon = TbilisiTime.Midnight(TbilisiTime.Date(start).AddDays(100));
            if (TimeSpan.FromHours(h) > WorkingTime(start, horizon)) return Parity.Diff(Parity.Iso(horizon), Parity.Iso(end));
            return Parity.Diff(TimeSpan.FromHours(Math.Max(h, 0)), WorkingTime(start, end));
        });
        Assert.Equal(12, parted.Count);
        Assert.All(parted, p => Assert.True(string.CompareOrdinal(p, "2005-03-27") < 0, p));
    }

    /// <summary>Working time between two instants, summed day by day at +04:00 (an oracle independent of SuspensionEnd's walk).</summary>
    private static TimeSpan WorkingTime(DateTimeOffset from, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;
        for (var day = TbilisiTime.Date(from); TbilisiTime.Midnight(day) < to; day = day.AddDays(1))
        {
            if (Suspension.IsNonWorkingDay(day)) continue;
            var a = TbilisiTime.Midnight(day) > from ? TbilisiTime.Midnight(day) : from;
            var b = TbilisiTime.Midnight(day.AddDays(1)) < to ? TbilisiTime.Midnight(day.AddDays(1)) : to;
            if (b > a) total += b - a;
        }
        return total;
    }

    [Fact(DisplayName = "parity: writtenConfirmationDeadline")]
    public void WrittenConfirmationDeadline() => Parity.All(Cases("writtenConfirmationDeadline"), c =>
        Parity.Diff(c.GetProperty("out").Str(), Parity.Iso(Suspension.WrittenConfirmationDeadline(DateTimeOffset.Parse(c.GetProperty("in").Str(), CultureInfo.InvariantCulture)))));

    [Fact(DisplayName = "parity: suspensionBlocks")]
    public void SuspensionBlocks() => Parity.All(Cases("suspensionBlocks"), c =>
    {
        var i = c.GetProperty("in");
        var s = i.GetProperty("s");
        var targeting = new SuspensionTargeting(
            Code<SuspensionState>(s.GetProperty("state")),
            Code<SuspensionScope>(s.GetProperty("scope")),
            s.GetProperty("target_policy_id").StrOrNull(),
            s.GetProperty("target_claim_id").StrOrNull(),
            s.Prop("legacy_unmapped")?.GetBoolean() ?? false,
            s.Prop("transaction_ref")?.Str());
        var t = i.GetProperty("targets");
        List<string?>? List(string name) => t.Prop(name)?.EnumerateArray().Select(x => x.StrOrNull()).ToList();
        var targets = new OperationTargets(List("policyIds"), List("claimIds"), List("numbers"));
        return Parity.Diff(c.GetProperty("out").GetBoolean(), Suspension.SuspensionBlocks(targeting, targets));
    });
}
