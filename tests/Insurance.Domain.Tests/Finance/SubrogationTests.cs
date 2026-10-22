using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>
/// subrogation.test.ts. Regress rules of docs/08 version 8, offline (Codex INS-MOTOR-REGRESS-CORE-20261009). The scenario
/// numbers are those of docs/08 §5. Every rule is also run against weakened variants (negative
/// controls): each variant must break at least one scenario. Fictional amounts only.
/// </summary>
public class SubrogationTests
{
    /// <summary>The rules a scenario uses (the old test's Pick of the module): the real ones or weakened ones.</summary>
    internal sealed record Rules(
        Func<IReadOnlyList<Movement>, NewMovement, ClaimMoney, MovementError?> CheckMovement,
        Func<IReadOnlyList<ClaimPayment>, DateOnly, BigInteger> Reimbursed,
        Func<IReadOnlyList<Movement>, DateOnly?, CaseTotals> Totals,
        Func<CaseStatus, CaseTotals, CaseStatus> StatusAfterMoney,
        Func<ClaimFacts, Ineligible?> Ineligibility,
        Func<BigInteger, InsurerShare?, ExpectedSplit> ExpectedShares);

    internal static readonly Rules Real = new(Subrogation.CheckMovement, Subrogation.Reimbursed, Subrogation.Totals, Subrogation.StatusAfterMoney, Subrogation.Ineligibility, Subrogation.ExpectedShares);

    /// <summary>A payment as payClaim stores it: the net and the set-off in GEL at the payment day's rate (1 for GEL).</summary>
    private static ClaimPayment Pay(decimal gross, decimal withheld = 0, decimal offset = 0, string paidOn = "2026-10-01", decimal rate = 1) => new(
        Lari(gross), Lari(withheld), Lari(offset),
        Lari(Math.Floor((gross - withheld - offset) * rate * 100 + 0.5m) / 100), Lari(Math.Floor(offset * rate * 100 + 0.5m) / 100), D(paidOn));

    private static Movement Mv(string id, MovementKind kind, decimal amount, string bookedOn = "2026-10-10", string? reversesId = null) =>
        new(id, kind, Lari(amount), D(bookedOn), reversesId);

    private static readonly CourtDocument Court = new("სასამართლო 2/123-26", D("2026-10-20"));

    /// <summary>A claim with `gel` paid early in the year and no other regress money.</summary>
    private static ClaimMoney CapOf(BigInteger gel) => new([new ClaimPayment(gel, 0, 0, gel, 0, D("2026-01-01"))], []);

    private static readonly ClaimMoney None = new([], []);

    private static NewMovement New(MovementKind kind, BigInteger amount, string bookedOn, CourtDocument? court = null) => NewMovement.Original(kind, amount, D(bookedOn), court);

    private static NewMovement Undo(string reversesId, string bookedOn) => NewMovement.Reversal(reversesId, D(bookedOn));

    /// <summary>
    /// The rule as it was at head d5c6db3, before H81 / H82: today's totals only, and the cap of the
    /// case alone on the movement's day. Kept as a negative control: the H81 / H82 scenarios must fail it.
    /// </summary>
    private static MovementError? OldCheckMovement(IReadOnlyList<Movement> movements, NewMovement next, ClaimMoney claim)
    {
        var now = Subrogation.Totals(movements);
        if (next.Kind == MovementKind.Reversal)
        {
            var target = movements.FirstOrDefault(m => m.Id == next.ReversesId);
            if (target is null || target.Kind == MovementKind.Reversal) return MovementError.Target;
            if (movements.Any(m => m.ReversesId == target.Id)) return MovementError.Reversed;
            if (next.BookedOn < target.BookedOn) return MovementError.Date;
            if (target.Kind == MovementKind.Confirmed && now.Balance - target.Amount < 0) return MovementError.Balance;
            return null;
        }
        if (!(next.Amount > 0)) return MovementError.Amount;
        var cap = Subrogation.Reimbursed(claim.Payments, next.BookedOn);
        switch (next.Kind)
        {
            case MovementKind.Claimed: return now.Claimed + next.Amount > cap ? MovementError.Cap : null;
            case MovementKind.Confirmed: return now.Confirmed + next.Amount > cap ? MovementError.Cap : null;
            case MovementKind.Cancelled:
                if (next.Court is null || next.Court.Number.Trim().Length == 0) return MovementError.Document;
                return next.Amount > now.Balance ? MovementError.Balance : null;
            default: return next.Amount > now.Balance ? MovementError.Balance : null;
        }
    }

    /// <summary>Every scenario as (name, holds); a correct implementation holds them all.</summary>
    internal static List<(string Name, bool Holds)> Scenarios(Rules r)
    {
        var output = new List<(string, bool)>();
        void Expect(string name, bool ok) => output.Add((name, ok));
        List<Movement> Add(IReadOnlyList<Movement> ms, string id, NewMovement next) => [.. ms, Subrogation.AsMovement(id, next, ms)];
        const string day = "2026-10-10";

        // G7 / G7ა / G13 / R1 — the cap is the GEL actually paid: net in GEL plus the set-off in GEL.
        var g7 = r.Reimbursed([Pay(1000, 200)], D(day));
        Expect("G7 reimbursed 1000 − 200 = 800", g7 == Lari(800));
        Expect("G7 claim of 1000 refused", r.CheckMovement([], New(MovementKind.Claimed, Lari(1000), day), CapOf(g7)) == MovementError.Cap);
        Expect("G7 claim of 800 accepted", r.CheckMovement([], New(MovementKind.Claimed, Lari(800), day), CapOf(g7)) is null);
        Expect("G7ა with a set-off: 700 net + 100 offset = 800", r.Reimbursed([Pay(1000, 200, 100)], D(day)) == Lari(800));
        Expect("G13 nothing reimbursed yet: any claim refused",
            r.CheckMovement([], New(MovementKind.Claimed, Lari(1), day), None) == MovementError.Cap);
        var usd = r.Reimbursed([Pay(1000, 200, 100, "2026-10-01", 2.7m)], D(day));
        Expect("R1 USD claim: 700 × 2.70 + 100 × 2.70 = 2160 GEL paid, no new rate", usd == Lari(2160));
        Expect("R1 USD claim of 2160.01 GEL refused", r.CheckMovement([], New(MovementKind.Claimed, Lari(2160.01m), day), CapOf(usd)) == MovementError.Cap);
        Expect("R1 two USD payments at their own rates: 500 × 2.70 + 300 × 2.75 = 2175",
            r.Reimbursed([Pay(500, 0, 0, "2026-10-01", 2.7m), Pay(300, 0, 0, "2026-10-05", 2.75m)], D(day)) == Lari(2175));
        Expect("a payment after the day does not count yet", r.Reimbursed([Pay(1000, 0, 0, "2026-10-11")], D(day)) == 0);
        Expect("confirmed beyond the reimbursement refused",
            r.CheckMovement([], New(MovementKind.Confirmed, Lari(801), day), CapOf(Lari(800))) == MovementError.Cap);
        // A back-dated claim cannot slip under a later one.
        Movement[] claimedLater = [Mv("c1", MovementKind.Claimed, 800, "2026-10-20")];
        Expect("back-dated claim checked against all claims", r.CheckMovement(claimedLater, New(MovementKind.Claimed, Lari(100), "2026-10-05"), CapOf(Lari(800))) == MovementError.Cap);

        // G1 / G23 / G24 / G25 — one claim, one balance.
        Movement[] confirmed = [Mv("c1", MovementKind.Claimed, 1000), Mv("k1", MovementKind.Confirmed, 1000)];
        var g23 = Add(confirmed, "r1", New(MovementKind.Received, Lari(600), day));
        Expect("G23 1000 − 600 = 400", r.Totals(g23, null).Balance == Lari(400));
        var g24 = Add(g23, "r2", New(MovementKind.Received, Lari(200), day));
        Expect("G24 then 200 more: 200", r.Totals(g24, null).Balance == Lari(200));
        Expect("G25 reversal of the first 600 accepted", r.CheckMovement(g24, Undo("r1", day), None) is null);
        var g25 = Add(g24, "x1", Undo("r1", day));
        Expect("G25 balance back to 800", r.Totals(g25, null).Balance == Lari(800));
        Expect("G25 the reversal is minus the original", g25[^1].Amount == -Lari(600));
        Expect("G10 a second reversal of the same receipt refused", r.CheckMovement(g25, Undo("r1", day), None) == MovementError.Reversed);
        Expect("a reversal of a reversal refused", r.CheckMovement(g25, Undo("x1", day), None) == MovementError.Target);
        Expect("a reversal of an unknown movement refused", r.CheckMovement(g25, Undo("nope", day), None) == MovementError.Target);
        Expect("a reversal dated before its original refused", r.CheckMovement(g24, Undo("r2", "2026-10-09"), None) == MovementError.Date);

        // G27 — more than the balance refused.
        Expect("G27 receipt above the balance refused", r.CheckMovement(g24, New(MovementKind.Received, Lari(200.01m), day), None) == MovementError.Balance);
        Expect("G27 receipt equal to the balance accepted", r.CheckMovement(g24, New(MovementKind.Received, Lari(200), day), None) is null);
        Expect("no receipt before a confirmation", r.CheckMovement([Mv("c1", MovementKind.Claimed, 1000)], New(MovementKind.Received, Lari(1), day), None) == MovementError.Balance);
        Expect("zero or negative amounts refused", r.CheckMovement(confirmed, New(MovementKind.Received, 0, day), None) == MovementError.Amount
            && r.CheckMovement(confirmed, New(MovementKind.Claimed, -1, day), CapOf(Lari(1000))) == MovementError.Amount);
        Expect("undoing a confirmation already received refused", r.CheckMovement(g23, Undo("k1", day), None) == MovementError.Balance);

        // H81 — the cap is the claim's: its regress money outside the case takes its share.
        var legacy = new ClaimMoney([Pay(500, 0, 0, "2026-09-01")], [new OtherRecovery(Lari(50), D("2026-09-10"))]);
        Expect("H81 paid 500, old receipt 50: a claim of 450.01 refused", r.CheckMovement([], New(MovementKind.Claimed, Lari(450.01m), day), legacy) == MovementError.Cap);
        Expect("H81 paid 500, old receipt 50: a claim of 450 accepted", r.CheckMovement([], New(MovementKind.Claimed, Lari(450), day), legacy) is null);
        Expect("H81 a confirmation above 450 refused too", r.CheckMovement([], New(MovementKind.Confirmed, Lari(450.01m), day), legacy) == MovementError.Cap);
        var recoveredElsewhere = new ClaimMoney([Pay(1000, 0, 0, "2026-09-01")], [new OtherRecovery(Lari(1000), D("2026-09-20"))]);
        Expect("H81 1000 paid and 1000 already recovered: nothing more to claim", r.CheckMovement([], New(MovementKind.Claimed, Lari(0.01m), day), recoveredElsewhere) == MovementError.Cap);
        var returned = new ClaimMoney([Pay(1000, 0, 0, "2026-09-01")], [new OtherRecovery(Lari(50), D("2026-09-10")), new OtherRecovery(-Lari(50), D("2026-09-15"))]);
        Expect("H81 an old receipt that was reversed frees its share", r.CheckMovement([], New(MovementKind.Claimed, Lari(1000), day), returned) is null);
        Expect("H81 a claim dated before an old receipt still leaves room for it on the later day",
            r.CheckMovement([], New(MovementKind.Claimed, Lari(500), "2026-09-05"), legacy) == MovementError.Cap);

        // H82 — every day holds, not only today.
        Movement[] h82a = [Mv("k1", MovementKind.Confirmed, 1000, "2026-10-01"), Mv("r1", MovementKind.Received, 600, "2026-10-10"), Mv("x1", MovementKind.Reversal, -600, "2026-10-20", "r1")];
        Expect("H82 a receipt of 600 on 11.10, while the first 600 is still in, refused", r.CheckMovement(h82a, New(MovementKind.Received, Lari(600), "2026-10-11"), None) == MovementError.Balance);
        Expect("H82 the same receipt on 20.10, after the reversal, accepted", r.CheckMovement(h82a, New(MovementKind.Received, Lari(600), "2026-10-20"), None) is null);
        Expect("H82 a receipt of 400 on 11.10 (exactly the balance then) accepted", r.CheckMovement(h82a, New(MovementKind.Received, Lari(400), "2026-10-11"), None) is null);
        Movement[] h82b = [Mv("k1", MovementKind.Confirmed, 1000, "2026-10-20")];
        Expect("H82 a receipt on 10.10 against a confirmation of 20.10 refused", r.CheckMovement(h82b, New(MovementKind.Received, Lari(600), "2026-10-10"), None) == MovementError.Balance);
        Expect("H82 a cancellation on 10.10 against a confirmation of 20.10 refused", r.CheckMovement(h82b, New(MovementKind.Cancelled, Lari(400), "2026-10-10", Court), None) == MovementError.Balance);
        Expect("H82 the receipt on 20.10 accepted", r.CheckMovement(h82b, New(MovementKind.Received, Lari(600), "2026-10-20"), None) is null);
        Movement[] h82c = [Mv("k1", MovementKind.Confirmed, 1000, "2026-10-01"), Mv("r1", MovementKind.Received, 600, "2026-10-10")];
        Expect("H82 undoing the confirmation on 05.10, before the receipt of 10.10, refused", r.CheckMovement(h82c, Undo("k1", "2026-10-05"), None) == MovementError.Balance);
        Movement[] h82d = [Mv("k1", MovementKind.Confirmed, 1000, "2026-10-01"), Mv("r1", MovementKind.Received, 600, "2026-10-10"), Mv("x1", MovementKind.Reversal, -600, "2026-10-12", "r1"), Mv("r2", MovementKind.Received, 300, "2026-10-15")];
        Expect("H82 a receipt reversal is fine on its own day and after", r.Totals(h82d, D("2026-10-12")).Balance == Lari(1000) && r.Totals(h82d, D("2026-10-15")).Balance == Lari(700));
        Expect("H82 500 on 11.10 (before the reversal of 12.10) refused, though 700 is open today", r.CheckMovement(h82d, New(MovementKind.Received, Lari(500), "2026-10-11"), None) == MovementError.Balance);
        var later = new ClaimMoney([Pay(1000, 0, 0, "2026-10-15")], []);
        Expect("H82 a confirmation dated before the payment refused", r.CheckMovement([], New(MovementKind.Confirmed, Lari(1000), "2026-10-05"), later) == MovementError.Cap);
        Expect("H82 the same confirmation on the payment day accepted", r.CheckMovement([], New(MovementKind.Confirmed, Lari(1000), "2026-10-15"), later) is null);

        // G26 — closed only when something was confirmed and nothing is owed.
        var g26 = Add(confirmed, "r1", New(MovementKind.Received, Lari(1000), day));
        Expect("G26 full receipt: balance 0", r.Totals(g26, null).Balance == 0);
        Expect("G26 full receipt closes the case", r.StatusAfterMoney(CaseStatus.Accepted, r.Totals(g26, null)) == CaseStatus.Closed);
        Expect("G26 an empty new case is not closed", r.StatusAfterMoney(CaseStatus.Opened, r.Totals([], null)) == CaseStatus.Opened);
        Expect("a partial receipt: partially received", r.StatusAfterMoney(CaseStatus.Accepted, r.Totals(g23, null)) == CaseStatus.PartiallyReceived);
        Expect("a reversed full receipt reopens the case", r.StatusAfterMoney(CaseStatus.Closed, r.Totals(Add(g26, "x", Undo("r1", day)), null)) == CaseStatus.Accepted);

        // G14 / G16 — only a court cancels, with its decision.
        var g14base = Add(confirmed, "r1", New(MovementKind.Received, Lari(600), day));
        Expect("G14 cancellation by a court accepted", r.CheckMovement(g14base, New(MovementKind.Cancelled, Lari(400), day, Court), None) is null);
        var g14 = Add(g14base, "z1", New(MovementKind.Cancelled, Lari(400), day));
        Expect("G14 balance 0, case closed", r.Totals(g14, null).Balance == 0 && r.StatusAfterMoney(CaseStatus.PartiallyReceived, r.Totals(g14, null)) == CaseStatus.Closed);
        Expect("G14 / G16 cancellation without a court decision refused",
            r.CheckMovement(g14base, New(MovementKind.Cancelled, Lari(400), day), None) == MovementError.Document
            && r.CheckMovement(g14base, New(MovementKind.Cancelled, Lari(400), day, new CourtDocument(" ", D(day))), None) == MovementError.Document);
        Expect("cancellation above the balance refused", r.CheckMovement(g14base, New(MovementKind.Cancelled, Lari(401), day, Court), None) == MovementError.Balance);

        // G15 — a wrong confirmation is reversed and entered again.
        Movement[] g15a = [Mv("c1", MovementKind.Claimed, 1200), Mv("k1", MovementKind.Confirmed, 1200)];
        var g15b = Add(g15a, "x1", Undo("k1", day));
        Expect("G15 reversal of 1200 accepted", r.CheckMovement(g15a, Undo("k1", day), None) is null);
        Expect("G15 then 1000 confirmed", r.CheckMovement(g15b, New(MovementKind.Confirmed, Lari(1000), day), CapOf(Lari(1500))) is null);
        var g15 = Add(g15b, "k2", New(MovementKind.Confirmed, Lari(1000), day));
        Expect("G15 confirmed 1000, the original kept", r.Totals(g15, null).Confirmed == Lari(1000) && g15.Any(m => m.Id == "k1" && m.Amount == Lari(1200)));

        // G10 / G10ა / G12 — on a date.
        Movement[] g10a = [Mv("k1", MovementKind.Confirmed, 1000, "2026-10-01"), Mv("r1", MovementKind.Received, 600, "2026-10-20"), Mv("x1", MovementKind.Reversal, -600, "2026-11-05", "r1")];
        Expect("G10ა October still shows the 600 received", r.Totals(g10a, D("2026-10-31")).Received == Lari(600));
        Expect("G10ა November nets it to 0", r.Totals(g10a, D("2026-11-30")).Received == 0);
        Movement[] g12 = [Mv("k1", MovementKind.Confirmed, 1000, "2026-11-01"), Mv("r1", MovementKind.Received, 600, "2026-11-15")];
        var on10 = r.Totals(g12, D("2026-11-10"));
        var on30 = r.Totals(g12, D("2026-11-30"));
        Expect("G12 on 10.11: 1000 / 0 / 1000", on10.Confirmed == Lari(1000) && on10.Received == 0 && on10.Balance == Lari(1000));
        Expect("G12 on 30.11: 1000 / 600 / 400", on30.Confirmed == Lari(1000) && on30.Received == Lari(600) && on30.Balance == Lari(400));

        // G3 / G4 / G5 / G6 — one balance whoever pays; the limit only splits what is expected.
        Expect("G3 the insurer pays 1000: nothing left on anyone",
            r.Totals(Add(confirmed, "r1", New(MovementKind.Received, Lari(1000), day)), null).Balance == 0);
        var g4 = r.ExpectedShares(Lari(1000), new InsurerShare(Lari(700), PartyResponse.Accepted));
        Expect("G4 expected: insurer 700, person 300", g4.Insurer == Lari(700) && g4.Person == Lari(300));
        Expect("G4 after the insurer paid 700, 300 open on the case",
            r.Totals(Add(confirmed, "r1", New(MovementKind.Received, Lari(700), day)), null).Balance == Lari(300));
        var g6 = r.ExpectedShares(Lari(1000), new InsurerShare(Lari(700), PartyResponse.Rejected));
        Expect("G6 insurer rejected: the person owes all", g6.Insurer == 0 && g6.Person == Lari(1000));
        var unknownLimit = r.ExpectedShares(Lari(1000), new InsurerShare(null, null));
        Expect("insurer with an unknown limit: no split yet", unknownLimit.Insurer is null && unknownLimit.Person is null);
        Expect("limit above the debt: the insurer owes all", r.ExpectedShares(Lari(500), new InsurerShare(Lari(700), null)).Insurer == Lari(500));
        Expect("G5 disputed: nothing confirmed, status stays", r.Totals([Mv("c1", MovementKind.Claimed, 1000)], null).Balance == 0
            && r.StatusAfterMoney(CaseStatus.Disputed, r.Totals([Mv("c1", MovementKind.Claimed, 1000)], null)) == CaseStatus.Disputed);

        // G17ა / G17ბ — only 5.1, not theft; the currency of the claim does not matter (R1).
        Expect("5.1 damage accepted", r.Ineligibility(new ClaimFacts("5.1", "damage")) is null);
        Expect("5.1 equipment accepted", r.Ineligibility(new ClaimFacts("5.1", "equipment")) is null);
        Expect("G17ა class 14 refused", r.Ineligibility(new ClaimFacts("14", "assistance")) == Ineligible.Class);
        Expect("G17ა class 3.2 refused", r.Ineligibility(new ClaimFacts("3.2", "accident")) == Ineligible.Class);
        Expect("G17ბ theft refused", r.Ineligibility(new ClaimFacts("5.1", "theft")) == Ineligible.Theft);
        Expect("unknown coverage refused", r.Ineligibility(new ClaimFacts("5.1", null)) == Ineligible.Coverage);
        return output;
    }

    public static TheoryData<string> ScenarioNames() => new(Scenarios(Real).Select(s => s.Name));

    [Theory(DisplayName = "regress rules (docs/08 v8)")]
    [MemberData(nameof(ScenarioNames))]
    public void RegressRule(string name)
    {
        var all = Scenarios(Real);
        Assert.Equal(71, all.Count);
        Assert.True(all.Single(s => s.Name == name).Holds, name);
    }

    [Fact(DisplayName = "amounts in tetri reads and writes exact amounts")]
    public void ReadsAndWritesExactAmounts()
    {
        Assert.Equal(100000, Subrogation.ToTetri("1000"));
        Assert.Equal(10, Subrogation.ToTetri("0.1"));
        Assert.Equal(123456, Subrogation.ToTetri("1234.56"));
        Assert.Equal(BigInteger.Parse("999999999999999999"), Subrogation.ToTetri("9999999999999999.99"));
        foreach (var bad in new[] { "", "-1", "1.234", "1,5", "1e3", "abc", ".5" }) Assert.Null(Subrogation.ToTetri(bad));
        Assert.Equal("1234.56", Subrogation.FromTetri(123456));
        Assert.Equal("-600.00", Subrogation.FromTetri(-60000));
        Assert.Equal("0.05", Subrogation.FromTetri(5));
    }

    [Fact(DisplayName = "amounts in tetri 0.1 + 0.2 lari is exactly 0.30 (no float error)")]
    public void NoFloatError() => Assert.Equal("0.30", Subrogation.FromTetri(Subrogation.ToTetri("0.1")!.Value + Subrogation.ToTetri("0.2")!.Value));

    /// <summary>Negative controls: each weakened rule must break at least one scenario above.</summary>
    private static readonly Dictionary<string, Func<Rules, Rules>> Mutants = new()
    {
        ["the cap is ignored"] = r => r with { CheckMovement = (ms, next, _) => Subrogation.CheckMovement(ms, next, CapOf(BigInteger.Pow(10, 18))) },
        ["the old head d5c6db3 (H81 / H82)"] = r => r with { CheckMovement = OldCheckMovement },
        ["the claim's other regress money is ignored (H81)"] = r => r with { CheckMovement = (ms, next, claim) => Subrogation.CheckMovement(ms, next, claim with { Others = [] }) },
        ["only today's totals are checked (H82)"] = r => r with
        {
            CheckMovement = (ms, next, claim) =>
            {
                var e = Subrogation.CheckMovement(ms, next, claim);
                if (e != MovementError.Balance || next.Kind == MovementKind.Reversal) return e;
                return next.Amount > Subrogation.Totals(ms).Balance ? MovementError.Balance : null;
            },
        },
        ["the premium set-off is left out"] = r => r with { Reimbursed = (ps, d) => ps.Where(p => p.PaidOn <= d).Aggregate(BigInteger.Zero, (s, p) => s + p.NetPaidGel) },
        ["the deductible is not taken off"] = r => r with { Reimbursed = (ps, d) => ps.Where(p => p.PaidOn <= d).Aggregate(BigInteger.Zero, (s, p) => s + p.Amount) },
        ["the claim currency is taken as GEL"] = r => r with { Reimbursed = (ps, d) => ps.Where(p => p.PaidOn <= d).Aggregate(BigInteger.Zero, (s, p) => s + p.Amount - p.DeductibleWithheld) },
        ["a receipt is not checked against the balance"] = r => r with { CheckMovement = (ms, next, cap) => next.Kind == MovementKind.Received ? null : Subrogation.CheckMovement(ms, next, cap) },
        ["a movement may be reversed twice"] = r => r with { CheckMovement = (ms, next, cap) => Subrogation.CheckMovement(ms, next, cap) is var e && e == MovementError.Reversed ? null : e },
        ["a reversal may undo a reversal"] = r => r with { CheckMovement = (ms, next, cap) => Subrogation.CheckMovement(ms, next, cap) is var e && e == MovementError.Target ? null : e },
        ["a court decision is not required"] = r => r with { CheckMovement = (ms, next, cap) => Subrogation.CheckMovement(ms, next, cap) is var e && e == MovementError.Document ? null : e },
        ["a confirmation already received may be undone"] = r => r with { CheckMovement = (ms, next, cap) => Subrogation.CheckMovement(ms, next, cap) is var e && e == MovementError.Balance && next.Kind == MovementKind.Reversal ? null : e },
        ["a reversal always counts as money back"] = r => r with
        {
            Totals = (ms, asOf) =>
            {
                var t = Subrogation.Totals(ms.Where(m => m.Kind != MovementKind.Reversal).ToList(), asOf);
                var back = ms.Where(m => m.Kind == MovementKind.Reversal && (asOf is null || m.BookedOn <= asOf)).Aggregate(BigInteger.Zero, (s, m) => s + m.Amount);
                return t with { Received = t.Received + back, Balance = t.Balance - back };
            },
        },
        ["the date is ignored"] = r => r with { Totals = (ms, _) => Subrogation.Totals(ms) },
        ["an empty case counts as settled"] = r => r with { StatusAfterMoney = (cur, t) => t.Balance == 0 ? CaseStatus.Closed : Subrogation.StatusAfterMoney(cur, t) },
        ["theft is allowed"] = r => r with { Ineligibility = c => Subrogation.Ineligibility(c) is var e && e == Ineligible.Theft ? null : e },
        ["any class is allowed"] = r => r with { Ineligibility = c => Subrogation.Ineligibility(c) is var e && e == Ineligible.Class ? null : e },
        ["the limit is ignored"] = r => r with { ExpectedShares = (confirmed, _) => new ExpectedSplit(confirmed, 0) },
    };

    public static TheoryData<string> MutantNames() => new(Mutants.Keys);

    [Theory(DisplayName = "negative controls: weakened rules fail")]
    [MemberData(nameof(MutantNames))]
    public void WeakenedRuleFails(string name)
    {
        var broken = Scenarios(Mutants[name](Real)).Where(s => !s.Holds).Select(s => s.Name).ToList();
        Assert.True(broken.Count > 0, $"no scenario caught \"{name}\"");
    }

    public static TheoryData<string> OldHeadRegressions() => new(
        "H81 paid 500, old receipt 50: a claim of 450.01 refused",
        "H81 1000 paid and 1000 already recovered: nothing more to claim",
        "H82 a receipt of 600 on 11.10, while the first 600 is still in, refused",
        "H82 a receipt on 10.10 against a confirmation of 20.10 refused",
        "H82 a cancellation on 10.10 against a confirmation of 20.10 refused",
        "H82 500 on 11.10 (before the reversal of 12.10) refused, though 700 is open today");

    [Theory(DisplayName = "old head d5c6db3 fails the H81 / H82 regressions")]
    [MemberData(nameof(OldHeadRegressions))]
    public void OldHeadFails(string name)
    {
        var broken = Scenarios(Real with { CheckMovement = OldCheckMovement }).Where(s => !s.Holds).Select(s => s.Name).ToHashSet();
        Assert.True(broken.Contains(name), $"the old rule passes \"{name}\"");
    }
}
