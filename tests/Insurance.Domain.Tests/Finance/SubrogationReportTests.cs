using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>
/// subrogation-report.test.ts. The regress read model (card and "regress claims on a date"), offline (Codex
/// INS-MOTOR-REGRESS-READONLY-REPORT-PREP-20261009). Scenario names follow docs/08 §5. Each rule is
/// also run against a weakened projection, which must break at least one scenario. Fictional data only.
/// </summary>
public class SubrogationReportTests
{
    private static int n;

    private static MovementRecord Mv(MovementKind kind, decimal amount, string bookedOn, string? reversesId = null, string? payerId = null) => new(
        $"m{Interlocked.Increment(ref n)}", kind, Lari(amount), D(bookedOn), reversesId,
        kind switch
        {
            MovementKind.Claimed => "demand_letter",
            MovementKind.Confirmed => "debtor_acceptance",
            MovementKind.Received => "bank_statement",
            MovementKind.Cancelled => "court_decision",
            _ => "correction",
        },
        kind == MovementKind.Cancelled ? "2/123-26" : null, D(bookedOn), payerId, null);

    private static CaseRecord Record(string caseNumber, Func<CaseRecord, CaseRecord>? change = null)
    {
        var r = new CaseRecord($"case-{caseNumber}", caseNumber, $"claim-{caseNumber}", $"CL-{caseNumber}", $"MHL-{caseNumber}", "5.1", D("2026-10-01"), CaseStatus.Accepted, new CaseBasis("police_report", "ოქმი 7", D("2026-09-30")));
        return change is null ? r : change(r);
    }

    /// <summary>The business day the card treats as today, unless a test sets its own.</summary>
    private static readonly DateOnly TODAY = D("2026-12-31");

    private static readonly PartyRecord person = new("p1", "ბრალეული პირი", PartyRole.LiablePerson, null, null, null);

    private static PartyTerms Terms(PartyResponse? response, string? responseOn, BigInteger? limit) => new(response, responseOn is null ? null : D(responseOn), limit);

    private static readonly PartyRecord insurer = new("p2", "მზღვეველი", PartyRole.LiabilityInsurer, Lari(700), PartyResponse.Accepted, D("2026-11-02"),
        // As the audit recorded it: added with the limit on 01.11, the answer on 02.11 (Q-R3).
        [
            new("2026-11-01T08:00:00Z", Terms(null, null, Lari(700))),
            new("2026-11-02T08:00:00Z", Terms(PartyResponse.Accepted, "2026-11-02", Lari(700))),
        ]);

    /// <summary>The functions a scenario uses: the real ones or weakened ones.</summary>
    internal sealed record Projection(
        Func<CaseRecord, IReadOnlyList<PartyRecord>, IReadOnlyList<MovementRecord>, DateOnly, DateOnly, CaseCard> CaseCard,
        Func<IReadOnlyList<ReportCase>, DateOnly, ReportOptions?, Report> ReportOnDate,
        Func<IReadOnlyList<Movement>, DateOnly, DateOnly, PeriodMovement> MovedIn);

    private static readonly Projection Real = new(SubrogationReport.CaseCardOf, SubrogationReport.ReportOnDate, SubrogationReport.MovedIn);

    private static OrUnknown<CaseStatus> S(CaseStatus status) => status;

    private static OrUnknown<PartyTermsOnDate> T(PartyTermsOnDate terms) => terms;

    private static ReportRow? Row(Report report) => report.Rows.FirstOrDefault();

    private static ExpectedSplit Split(BigInteger? insurer, BigInteger? person) => new(insurer, person);

    private static ExpectedNow Now(string day, BigInteger? insurer, BigInteger? person) => new(D(day), insurer, person);

    /// <summary>Every scenario as (name, holds); the real projection holds them all.</summary>
    internal static List<(string Name, bool Holds)> Scenarios(Projection r)
    {
        var output = new List<(string, bool)>();
        void Expect(string name, bool ok) => output.Add((name, ok));

        // G12 — accrued on a date: 01.11 confirmed 1000, 15.11 received 600.
        var g12c = Mv(MovementKind.Confirmed, 1000, "2026-11-01");
        MovementRecord[] g12 = [Mv(MovementKind.Claimed, 1000, "2026-10-20"), g12c, Mv(MovementKind.Received, 600, "2026-11-15", null, "p1")];
        var g12case = new ReportCase(Record("RG1", x => x with { StatusNow = CaseStatus.PartiallyReceived }), [person], g12);
        var on10 = Row(r.ReportOnDate([g12case], D("2026-11-10"), null));
        var on30 = Row(r.ReportOnDate([g12case], D("2026-11-30"), null));
        Expect("G12 10.11: accrued 1000, received 0, balance 1000", on10?.Accrued == Lari(1000) && on10.Received == 0 && on10.Balance == Lari(1000));
        Expect("G12 30.11: accrued 1000, received 600, balance 400", on30?.Accrued == Lari(1000) && on30.Received == Lari(600) && on30.Balance == Lari(400));
        Expect("claimed, accrued and received are three columns", on30?.Claimed == Lari(1000) && on30.Accrued == Lari(1000) && on30.Received == Lari(600));
        Expect("a movement after the date does not count (claimed on 20.10 counts on 25.10, the confirmation does not)",
            Row(r.ReportOnDate([g12case], D("2026-10-25"), null))?.Claimed == Lari(1000) && Row(r.ReportOnDate([g12case], D("2026-10-25"), null))?.Accrued == 0);

        // G10 / G10ა — a receipt in October reversed in November.
        var r10 = Mv(MovementKind.Received, 600, "2026-10-20", null, "p1");
        MovementRecord[] g10 = [Mv(MovementKind.Confirmed, 1000, "2026-10-01"), r10, Mv(MovementKind.Reversal, -600, "2026-11-05", r10.Id)];
        var oct = r.MovedIn(g10, D("2026-10-01"), D("2026-10-31"));
        var nov = r.MovedIn(g10, D("2026-11-01"), D("2026-11-30"));
        Expect("G10ა October: +600 received, as it was", oct.Received == Lari(600));
        Expect("G10ა November: −600 received (the reversal), balance +600", nov.Received == -Lari(600) && nov.BalanceChange == Lari(600));
        Expect("G10 one month with both: net 0", r.MovedIn(g10, D("2026-10-01"), D("2026-11-30")).Received == 0);

        // G23–G25 on the card: 400 → 200 → 800.
        var a600 = Mv(MovementKind.Received, 600, "2026-10-10", null, "p1");
        MovementRecord[] g23 = [Mv(MovementKind.Claimed, 1000, "2026-10-03"), Mv(MovementKind.Confirmed, 1000, "2026-10-04"), a600, Mv(MovementKind.Received, 200, "2026-10-11", null, "p1"), Mv(MovementKind.Reversal, -600, "2026-10-20", a600.Id)];
        var card = r.CaseCard(Record("RG2"), [person], g23, D("2026-10-31"), TODAY);
        Expect("G23–G25 balances line by line: 0, 1000, 400, 200, 800", card.History.Select(h => h.BalanceAfter).SequenceEqual([0, Lari(1000), Lari(400), Lari(200), Lari(800)]));
        Expect("the card totals on 31.10: balance 800", card.Totals.Balance == Lari(800));
        var rev = card.History.LastOrDefault();
        Expect("a reversal shows −600, undoes the receipt of 10.10, and raises the balance by 600",
            rev?.Amount == -Lari(600) && rev.Undoes?.Kind == MovementKind.Received && rev.Undoes.BookedOn == D("2026-10-10") && rev.BalanceEffect == Lari(600));
        Expect("a receipt shows its payer", card.History.ElementAtOrDefault(2)?.Payer == "ბრალეული პირი");
        Expect("the card on 10.10 ends with the first receipt: balance 400", r.CaseCard(Record("RG2"), [person], g23, D("2026-10-10"), TODAY).Totals.Balance == Lari(400)
            && r.CaseCard(Record("RG2"), [person], g23, D("2026-10-10"), TODAY).History.Count == 3);

        // G14 — a court cancellation: positive, lowers the balance.
        MovementRecord[] g14 = [Mv(MovementKind.Confirmed, 1000, "2026-10-04"), Mv(MovementKind.Received, 600, "2026-10-10", null, "p1"), Mv(MovementKind.Cancelled, 400, "2026-10-20")];
        var c14 = r.CaseCard(Record("RG3", x => x with { StatusNow = CaseStatus.Closed }), [person], g14, D("2026-10-31"), TODAY);
        Expect("G14 the court cancels 400: +400 in its column, −400 on the balance, balance 0, with the decision number",
            c14.Totals.Cancelled == Lari(400) && c14.History.ElementAtOrDefault(2)?.BalanceEffect == -Lari(400) && c14.Totals.Balance == 0 && c14.History.ElementAtOrDefault(2)?.Document.Number == "2/123-26");

        // A case closed today still shows what it owed on an earlier date.
        var closedNow = new ReportCase(Record("RG3", x => x with { StatusNow = CaseStatus.Closed }), [person], g14);
        var back = Row(r.ReportOnDate([closedNow], D("2026-10-15"), null));
        Expect("a case closed today is in the report of 15.10 with 400 owed", back?.Balance == Lari(400) && back.StatusNow == CaseStatus.Closed);
        Expect("a case opened after the date is not in its report", r.ReportOnDate([closedNow with { Record = Record("RG4", x => x with { OpenedOn = D("2026-11-01") }) }], D("2026-10-31"), null).Rows.Count == 0);

        // G3 / G4 — the person and the insurer share one balance; their rows never double it.
        MovementRecord[] g4 = [Mv(MovementKind.Confirmed, 1000, "2026-11-01"), Mv(MovementKind.Received, 700, "2026-11-10", null, "p2")];
        var g4case = new ReportCase(Record("RG5"), [person, insurer], g4);
        var g4row = Row(r.ReportOnDate([g4case], D("2026-11-30"), null));
        Expect("G4 insurer paid 700 of 1000: one balance of 300, both named once", g4row?.Balance == Lari(300) && string.Join("|", g4row.Debtors) == "ბრალეული პირი|მზღვეველი");
        var g4card = r.CaseCard(Record("RG5"), [person, insurer], g4, D("2026-11-30"), TODAY);
        Expect("G4 the expected split on the card, with today's terms: insurer 700, person 300", g4card.ExpectedNow.Insurer == Lari(700) && g4card.ExpectedNow.Person == Lari(300));
        Expect("G4 on a past date the limit has no business day: no split shown as known", g4card.Expected.Insurer is null && g4card.Expected.Person is null);
        var twice = r.ReportOnDate([g4case with { Parties = [person, insurer, insurer with { Id = "p3" }] }], D("2026-11-30"), null);
        Expect("a party listed twice is named once and changes no amount", Row(twice)?.Debtors.Count == 2 && Row(twice)?.Balance == Lari(300) && twice.Total.Balance == Lari(300));

        // G23 / G27 — another claim's case stays its own row; the totals add rows, never movements twice.
        var other = new ReportCase(Record("RG6"), [person], [Mv(MovementKind.Confirmed, 500, "2026-10-05")]);
        var both = r.ReportOnDate([g12case, other], D("2026-11-30"), null);
        Expect("two cases: two rows, each with its own balance (400 and 500), total 900",
            both.Rows.Count == 2 && both.Rows.FirstOrDefault(x => x.CaseNumber == "RG6")?.Balance == Lari(500) && both.Total.Balance == Lari(900));
        Expect("rows by case number", string.Join(",", both.Rows.Select(x => x.CaseNumber)) == "RG1,RG6");

        // Tetri stay exact.
        var cents = new ReportCase(Record("RG7"), [person], [Mv(MovementKind.Confirmed, 0.3m, "2026-10-01"), Mv(MovementKind.Received, 0.1m, "2026-10-02", null, "p1"), Mv(MovementKind.Received, 0.2m, "2026-10-03", null, "p1")]);
        Expect("0.30 − 0.10 − 0.20 = 0.00 exactly", Row(r.ReportOnDate([cents], D("2026-10-31"), null))?.Balance == 0);
        return output;
    }

    public static TheoryData<string> ScenarioNames() => new(Scenarios(Real).Select(s => s.Name));

    [Theory(DisplayName = "regress read model (card and report on a date)")]
    [MemberData(nameof(ScenarioNames))]
    public void ReadModelScenario(string name)
    {
        var all = Scenarios(Real);
        Assert.Equal(22, all.Count);
        Assert.True(all.Single(s => s.Name == name).Holds, name);
    }

    [Fact(DisplayName = "the read model only reads what it is given old regress money and salvage of the claim are not inputs: no case, no row is made of them")]
    public void NoCaseNoRow()
    {
        // The input is a case and its own movements; a claim_recovery row without a case has no place in it.
        var report = SubrogationReport.ReportOnDate([], D("2026-10-31"));
        Assert.Empty(report.Rows);
        Assert.Equal(0, report.Total.Balance);
    }

    [Fact(DisplayName = "the read model only reads what it is given the reader text of an amount is exact")]
    public void ReaderTextIsExact()
    {
        Assert.Equal("1000.50", SubrogationReport.Lari(Lari(1000.5m)));
        Assert.Equal("-600.00", SubrogationReport.Lari(-Lari(600)));
    }

    /// <summary>Negative controls: each weakened projection must break at least one scenario.</summary>
    private static readonly Dictionary<string, Projection> Mutants = new()
    {
        ["the date is ignored (today's totals)"] = Real with { ReportOnDate = (cases, asOf, _) => SubrogationReport.ReportOnDate(cases, D("9999-12-31") > asOf ? D("9999-12-31") : asOf) },
        ["cases closed today are left out"] = Real with { ReportOnDate = (cases, asOf, _) => SubrogationReport.ReportOnDate(cases.Where(c => c.Record.StatusNow != CaseStatus.Closed).ToList(), asOf) },
        ["the balance is counted per party"] = Real with
        {
            ReportOnDate = (cases, asOf, _) =>
            {
                var rep = SubrogationReport.ReportOnDate(cases, asOf);
                var rows = rep.Rows.Select(row => row with { Balance = row.Balance * Math.Max(1, row.Debtors.Count) }).ToList();
                return rep with { Rows = rows, Total = rep.Total with { Balance = rows.Aggregate(BigInteger.Zero, (s, x) => s + x.Balance) } };
            },
        },
        ["a reversal counts as more money received"] = Real with
        {
            MovedIn = (ms, from, to) => SubrogationReport.MovedIn(ms.Select(m => m.Kind == MovementKind.Reversal ? m with { Kind = MovementKind.Received, Amount = -m.Amount, ReversesId = null } : m).ToList(), from, to),
        },
        ["claimed is shown as accrued"] = Real with
        {
            ReportOnDate = (cases, asOf, _) => { var rep = SubrogationReport.ReportOnDate(cases, asOf); return rep with { Rows = rep.Rows.Select(x => x with { Accrued = x.Claimed }).ToList() }; },
        },
        ["the card ignores the date"] = Real with { CaseCard = (c, p, ms, _, today) => SubrogationReport.CaseCardOf(c, p, ms, D("9999-12-31"), today) },
        ["a cancellation raises the balance"] = Real with
        {
            CaseCard = (c, p, ms, asOf, today) => { var card = SubrogationReport.CaseCardOf(c, p, ms, asOf, today); return card with { Totals = card.Totals with { Balance = card.Totals.Balance + 2 * card.Totals.Cancelled } }; },
        },
    };

    public static TheoryData<string> MutantNames() => new(Mutants.Keys);

    [Theory(DisplayName = "negative controls: weakened projections fail")]
    [MemberData(nameof(MutantNames))]
    public void WeakenedProjectionFails(string name)
    {
        var broken = Scenarios(Mutants[name]).Where(s => !s.Holds).Select(s => s.Name).ToList();
        Assert.True(broken.Count > 0, $"no scenario caught \"{name}\"");
    }

    [Fact(DisplayName = "negative controls: weakened projections fail a sanity check: totals() alone agrees with the report")]
    public void TotalsAgreeWithTheReport()
    {
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-11-01"), Mv(MovementKind.Received, 600, "2026-11-15", null, "p1")];
        Assert.Equal(Subrogation.Totals(ms, D("2026-11-30")).Balance, Row(SubrogationReport.ReportOnDate([new ReportCase(Record("RG9"), [person], ms)], D("2026-11-30")))?.Balance);
    }

    // ---- H87: a same-day reversal read before its original ----

    private const string Day = "2026-10-10";
    private static readonly MovementRecord confirmedC1 = Mv(MovementKind.Confirmed, 1000, "2026-10-01") with { Id = "c-1" };
    private static readonly MovementRecord receipt = Mv(MovementKind.Received, 600, Day, null, "p1") with { Id = "f-receipt" };
    // The reversal's transaction began first (it waited on the claim's lock), so it is read first; its id sorts first too.
    private static readonly MovementRecord reversal = Mv(MovementKind.Reversal, -600, Day, "f-receipt") with { Id = "a-reversal" };
    private static readonly MovementRecord[] readOrder = [confirmedC1, reversal, receipt];

    /// <summary>CaseCard's history as it was at head d455733: by day and read order, the balance from Totals(seen).</summary>
    private static List<BigInteger> OldHistory(IReadOnlyList<MovementRecord> ms, DateOnly asOf)
    {
        var sorted = ms.Where(m => m.BookedOn <= asOf).OrderBy(m => m.BookedOn).ToList();
        var seen = new List<Movement>();
        return sorted.Select(m => { seen.Add(m); return Subrogation.Totals(seen).Balance; }).ToList();
    }

    private const string H87 = "H87: a same-day reversal read before its original ";

    [Fact(DisplayName = H87 + "the old card stopped with an error (negative control)")]
    public void OldCardStopped() =>
        Assert.Contains("reverses nothing it can", Assert.Throws<InvalidOperationException>(() => OldHistory(readOrder, D("2026-10-31"))).Message);

    [Fact(DisplayName = H87 + "now: the reversal follows its original, and every line balance is right")]
    public void ReversalFollowsItsOriginal()
    {
        var card = SubrogationReport.CaseCardOf(Record("RG10"), [person], readOrder, D("2026-10-31"), TODAY);
        Assert.Equal(["c-1", "f-receipt", "a-reversal"], card.History.Select(h => h.Id));
        Assert.Equal([Lari(1000), Lari(400), Lari(1000)], card.History.Select(h => h.BalanceAfter));
        Assert.Equal(Lari(1000), card.Totals.Balance);
    }

    [Fact(DisplayName = H87 + "a same-day cancellation reversed, read first, is shown after it too")]
    public void SameDayCancellationReversed()
    {
        var cancel = Mv(MovementKind.Cancelled, 100, Day) with { Id = "z-cancel" };
        var undo = Mv(MovementKind.Reversal, -100, Day, "z-cancel") with { Id = "b-undo" };
        var card = SubrogationReport.CaseCardOf(Record("RG11"), [person], [confirmedC1, undo, cancel], D("2026-10-31"), TODAY);
        Assert.Equal([("c-1", Lari(1000)), ("z-cancel", Lari(900)), ("b-undo", Lari(1000))], card.History.Select(h => (h.Id, h.BalanceAfter)));
        Assert.Equal(MovementKind.Cancelled, card.History[2].Undoes?.Kind);
    }

    private static IEnumerable<List<T>> Permutations<T>(List<T> xs) =>
        xs.Count <= 1 ? [xs] : xs.SelectMany((x, i) => Permutations(xs.Where((_, j) => j != i).ToList()).Select(p => (List<T>)[x, .. p]));

    [Fact(DisplayName = H87 + "every read order of one day gives a card: originals first, the last balance is the total")]
    public void EveryReadOrderGivesACard()
    {
        var r2 = Mv(MovementKind.Received, 200, Day, null, "p1") with { Id = "r2" };
        var c1 = Mv(MovementKind.Cancelled, 100, Day) with { Id = "c-cancel" };
        var x1 = Mv(MovementKind.Reversal, -600, Day, "f-receipt") with { Id = "x1" };
        var x2 = Mv(MovementKind.Reversal, -100, Day, "c-cancel") with { Id = "x2" };
        List<MovementRecord> sameDay = [receipt, r2, c1, x1, x2];
        var count = 0;
        foreach (var order in Permutations(sameDay))
        {
            List<MovementRecord> ms = [confirmedC1, .. order];
            var card = SubrogationReport.CaseCardOf(Record("RG12"), [person], ms, D("2026-10-31"), TODAY);
            var at = card.History.Select((h, i) => (h.Id, i)).ToDictionary(p => p.Id, p => p.i);
            Assert.True(at["x1"] > at["f-receipt"] && at["x2"] > at["c-cancel"], string.Join(",", order.Select(m => m.Id)));
            Assert.Equal(Subrogation.Totals(ms).Balance, card.History[^1].BalanceAfter);
            Assert.Equal(Subrogation.Totals(ms).Balance, card.History.Aggregate(BigInteger.Zero, (s, h) => s + h.BalanceEffect));
            count++;
        }
        Assert.Equal(120, count);
    }

    [Fact(DisplayName = H87 + "a reversal of a movement that is not on the case is refused, not shown")]
    public void OrphanReversalRefused()
    {
        var orphan = Mv(MovementKind.Reversal, -600, Day, "missing") with { Id = "orphan" };
        var e = Assert.Throws<InvalidOperationException>(() => SubrogationReport.CaseCardOf(Record("RG13"), [person], [confirmedC1, orphan], D("2026-10-31"), TODAY));
        Assert.Contains("reverses missing, which is not on the case", e.Message);
    }

    [Fact(DisplayName = H87 + "the report and the period need no order: same totals for the read order and the write order")]
    public void ReportNeedsNoOrder()
    {
        var a = Row(SubrogationReport.ReportOnDate([new ReportCase(Record("RG14"), [person], readOrder)], D("2026-10-31")))!;
        var b = Row(SubrogationReport.ReportOnDate([new ReportCase(Record("RG14"), [person], [confirmedC1, receipt, reversal])], D("2026-10-31")))!;
        Assert.Equal(a with { Debtors = [] }, b with { Debtors = [] });
        Assert.Equal(a.Debtors, b.Debtors);
        Assert.Equal(0, SubrogationReport.MovedIn(readOrder, D(Day), D(Day)).Received);
    }

    // ---- owner answers Q-R1–Q-R5 (09.10.2026) ----

    private static MovementRecord Cm(string bookedOn, MovementKind kind, decimal amount) => Mv(kind, amount, bookedOn, null, kind == MovementKind.Received ? "p1" : null);

    private static readonly ReportCase settled = new(Record("RG20", x => x with { StatusNow = CaseStatus.Closed }), [person], [Cm("2026-10-02", MovementKind.Confirmed, 500), Cm("2026-10-05", MovementKind.Received, 500)]);
    private static readonly ReportCase open = new(Record("RG21"), [person], [Cm("2026-10-02", MovementKind.Confirmed, 800), Cm("2026-10-05", MovementKind.Received, 300)]);
    private static readonly ReportCase empty = new(Record("RG22", x => x with { StatusNow = CaseStatus.Opened }), [person], []);

    private static Recorded<CaseStatus> At(string at, CaseStatus value) => new(at, value);

    private const string Q = "owner answers Q-R1–Q-R5 (09.10.2026) ";

    [Fact(DisplayName = Q + "Q-R1: every case opened by the date, zero balances included; the filter keeps only those with something owed")]
    public void QR1()
    {
        var all = SubrogationReport.ReportOnDate([settled, open, empty], D("2026-10-31"));
        Assert.Equal(["RG20", "RG21", "RG22"], all.Rows.Select(r => r.CaseNumber));
        var owed = SubrogationReport.ReportOnDate([settled, open, empty], D("2026-10-31"), new ReportOptions(OnlyWithBalance: true));
        Assert.Equal(["RG21"], owed.Rows.Select(r => r.CaseNumber));
        Assert.Equal(Lari(500), owed.Total.Balance);
        Assert.Equal(Lari(800), owed.Total.Accrued); // the totals are those of the rows shown
    }

    [Fact(DisplayName = Q + "Q-R2: the status on the date — from the ledger once something is confirmed, else as people set it; today apart; unknown otherwise")]
    public void QR2()
    {
        // settled: confirmed 500 on 02.10, received 500 on 05.10; the audit recorded these statuses at these times.
        Recorded<CaseStatus>[] history = [At("2026-10-01T06:00:00Z", CaseStatus.Opened), At("2026-10-02T06:00:00Z", CaseStatus.Accepted), At("2026-10-05T06:00:00Z", CaseStatus.Closed)];
        var withHistory = settled with { Record = settled.Record with { StatusHistory = history } };
        ReportRow? On(string d) => Row(SubrogationReport.ReportOnDate([withHistory], D(d)));
        Assert.Equal(S(CaseStatus.Opened), On("2026-10-01")?.StatusOnDate); // nothing confirmed yet: the status people set
        Assert.Equal(S(CaseStatus.Accepted), On("2026-10-03")?.StatusOnDate); // confirmed, nothing received: from the ledger
        Assert.Equal(S(CaseStatus.Closed), On("2026-10-31")?.StatusOnDate); // fully received: from the ledger
        Assert.Equal(CaseStatus.Closed, On("2026-10-31")?.StatusNow); // the status of today is kept apart
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, Row(SubrogationReport.ReportOnDate([settled with { Movements = [] }], D("2026-10-31")))?.StatusOnDate); // no history and nothing confirmed: unknown, never the status of today
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, SubrogationReport.StatusOn(history, [], D("2026-09-30"), D("2026-10-01"))); // before the case was opened: unknown
        // People's statuses follow the Tbilisi day of the record.
        Recorded<CaseStatus>[] disputedLate = [At("2026-10-01T06:00:00Z", CaseStatus.Opened), At("2026-10-31T21:30:00Z", CaseStatus.Disputed)];
        Assert.Equal(S(CaseStatus.Opened), SubrogationReport.StatusOn(disputedLate, [], D("2026-10-31"), D("2026-10-01"))); // a change begun after midnight in Tbilisi is no doubt on 31.10
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, SubrogationReport.StatusOn(disputedLate, [], D("2026-11-01"), D("2026-10-01"))); // from the day a change began, the status is not certain (H88)
        Assert.Equal(S(CaseStatus.Accepted), SubrogationReport.CaseCardOf(withHistory.Record, [person], withHistory.Movements.Cast<MovementRecord>().ToList(), D("2026-10-03"), TODAY).Header.StatusOnDate);
    }

    [Fact(DisplayName = Q + "Q-R3: the answer and limit of the insurer on the date; today apart; unknown terms give no split")]
    public void QR3()
    {
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-11-01")];
        var late = SubrogationReport.CaseCardOf(Record("RG23"), [person, insurer], ms, D("2026-11-30"), TODAY);
        Assert.Equal(T(new PartyTermsOnDate(PartyResponse.Accepted, D("2026-11-02"), OrUnknown<BigInteger?>.Unknown)), late.Parties[1].OnDate); // the answer by its day; the limit has no business day
        Assert.Equal(Split(null, null), late.Expected); // no split on a date without a known limit
        Assert.Equal(new ExpectedNow(TODAY, Lari(700), Lari(300)), late.ExpectedNow); // today's split with today's terms
        var before = SubrogationReport.CaseCardOf(Record("RG23"), [person, insurer], ms, D("2026-11-01"), TODAY);
        Assert.Equal(T(new PartyTermsOnDate(null, null, OrUnknown<BigInteger?>.Unknown)), before.Parties[1].OnDate); // on 01.11 there was no answer yet
        Assert.Equal(PartyResponse.Accepted, before.Parties[1].Now.Response); // the answer of today is shown apart
        var noHistory = insurer with { History = null };
        var unknown = SubrogationReport.CaseCardOf(Record("RG23"), [person, noHistory], ms, D("2026-11-30"), TODAY);
        Assert.Equal(OrUnknown<PartyTermsOnDate>.Unknown, unknown.Parties[1].OnDate);
        Assert.Equal(Split(null, null), unknown.Expected); // no split from unknown terms
        Assert.Equal(OrUnknown<PartyTermsOnDate>.Unknown, unknown.Parties[0].OnDate); // the person without history is unknown too
        Assert.Equal(Split(0, Lari(1000)), SubrogationReport.CaseCardOf(Record("RG24"), [person], ms, D("2026-11-30"), TODAY).Expected); // no insurer at all: the person owes all
    }

    [Fact(DisplayName = Q + "Q-R4: old regress money and salvage apart, by claim and kind, by the date; never in the regress totals")]
    public void QR4()
    {
        OutsideRecovery[] outside =
        [
            new("CL-9", OutsideKind.Subrogation, D("2026-07-10"), Lari(50)),
            new("CL-9", OutsideKind.Salvage, D("2026-07-12"), Lari(30)),
            new("CL-9", OutsideKind.Subrogation, D("2026-11-05"), -Lari(50)),
        ];
        var rep = SubrogationReport.ReportOnDate([open], D("2026-10-31"), new ReportOptions(Outside: outside));
        Assert.Equal([("CL-9", OutsideKind.Salvage, Lari(30)), ("CL-9", OutsideKind.Subrogation, Lari(50))], rep.Outside.Select(o => (o.ClaimNumber, o.Kind, o.Received)));
        Assert.Equal(new OutsideTotal(Lari(50), Lari(30)), rep.OutsideTotal);
        Assert.Equal(Lari(300), rep.Total.Received); // the regress totals are those of the case
        Assert.Single(rep.Rows); // no case is made of old money
        Assert.Equal(0, SubrogationReport.ReportOnDate([open], D("2026-11-30"), new ReportOptions(Outside: outside)).OutsideTotal.Subrogation); // a later reversal of the old 50 counts on its own day
    }

    [Fact(DisplayName = Q + "Q-R5: month, quarter and year around a day; a free period is checked")]
    public void QR5()
    {
        Assert.Equal(new DateRange(D("2026-02-01"), D("2026-02-28")), SubrogationReport.PeriodOf(PeriodKind.Month, D("2026-02-14")));
        Assert.Equal(new DateRange(D("2028-02-01"), D("2028-02-29")), SubrogationReport.PeriodOf(PeriodKind.Month, D("2028-02-14")));
        Assert.Equal(new DateRange(D("2026-10-01"), D("2026-12-31")), SubrogationReport.PeriodOf(PeriodKind.Quarter, D("2026-11-30")));
        Assert.Equal(new DateRange(D("2026-04-01"), D("2026-06-30")), SubrogationReport.PeriodOf(PeriodKind.Quarter, D("2026-04-01")));
        Assert.Equal(new DateRange(D("2026-01-01"), D("2026-12-31")), SubrogationReport.PeriodOf(PeriodKind.Year, D("2026-06-15")));
        Assert.Null(SubrogationReport.CheckPeriod("2026-10-01", "2026-10-31"));
        Assert.Equal(PeriodProblem.Order, SubrogationReport.CheckPeriod("2026-10-31", "2026-10-01"));
        Assert.Equal(PeriodProblem.Date, SubrogationReport.CheckPeriod("2026-02-30", "2026-03-01"));
        var q4 = SubrogationReport.PeriodOf(PeriodKind.Quarter, D("2026-11-15"));
        var r10 = Mv(MovementKind.Received, 600, "2026-10-20", null, "p1");
        Assert.Equal(0, SubrogationReport.MovedIn([Mv(MovementKind.Confirmed, 1000, "2026-10-01"), r10, Mv(MovementKind.Reversal, -600, "2026-11-05", r10.Id)], q4.From, q4.To).Received);
    }

    [Fact(DisplayName = Q + "negative controls: the old behaviour fails these answers")]
    public void QNegativeControls()
    {
        // Before these answers the report used the status of today for the date, and had no filter or outside section.
        CaseStatus OldStatusOnDate(ReportCase c) => c.Record.StatusNow;
        var withHistory = settled with { Record = settled.Record with { StatusHistory = [At("2026-10-01T06:00:00Z", CaseStatus.Opened)] } };
        Assert.NotEqual(OrUnknown<CaseStatus>.Known(OldStatusOnDate(withHistory)), Row(SubrogationReport.ReportOnDate([withHistory], D("2026-10-03")))?.StatusOnDate);
        // A day taken in UTC instead of Tbilisi moves a change at 21:30 UTC to the wrong day.
        Assert.NotEqual("2026-10-31T21:30:00Z"[..10], Dates.ToIso(SubrogationReport.TbilisiDay("2026-10-31T21:30:00Z")));
        // Taking the insurer terms of today for an earlier date would give an answer that did not exist yet.
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-11-01")];
        Assert.NotEqual(insurer.Response, SubrogationReport.CaseCardOf(Record("RG25"), [person, insurer], ms, D("2026-11-01"), TODAY).Parties[1].OnDate.Value.Response);
    }

    // ---- H88: no later status or answer on an earlier day ----

    private const string H88 = "H88: no later status or answer on an earlier day ";

    [Fact(DisplayName = H88 + "a receipt dated 01.11 but written on 31.10: the old reading showed the case closed on 31.10 with 1000 owed")]
    public void H88ReceiptWrittenEarlier()
    {
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-10-01"), Mv(MovementKind.Received, 1000, "2026-11-01", null, "p1")];
        // The receipt's transaction ran on 31.10, so the audit dates the closing then.
        Recorded<CaseStatus>[] history = [At("2026-10-01T06:00:00Z", CaseStatus.Accepted), At("2026-10-31T10:00:00Z", CaseStatus.Closed)];
        Assert.Equal(S(CaseStatus.Closed), SubrogationReport.OnDate(history, D("2026-10-31"))); // negative control: the old reading
        Assert.Equal(Lari(1000), Subrogation.Totals(ms, D("2026-10-31")).Balance);
        Assert.Equal(S(CaseStatus.Accepted), SubrogationReport.StatusOn(history, ms, D("2026-10-31"), D("2026-10-01"))); // now: the money status of 31.10
        Assert.Equal(S(CaseStatus.Closed), SubrogationReport.StatusOn(history, ms, D("2026-11-01"), D("2026-10-01")));
        var row = Row(SubrogationReport.ReportOnDate([new ReportCase(Record("RG30", x => x with { StatusNow = CaseStatus.Closed, StatusHistory = history }), [person], ms)], D("2026-10-31")));
        Assert.Equal(S(CaseStatus.Accepted), row?.StatusOnDate);
        Assert.Equal(Lari(1000), row?.Balance);
    }

    [Fact(DisplayName = H88 + "an answer dated 01.11 but recorded on 31.10: the old reading split 0 / 1000 on 31.10; now no answer yet on 31.10")]
    public void H88AnswerRecordedEarlier()
    {
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-10-01")];
        var rejecting = insurer with
        {
            Response = PartyResponse.Rejected,
            ResponseOn = D("2026-11-01"),
            History = [new("2026-10-01T06:00:00Z", Terms(null, null, Lari(700))), new("2026-10-31T10:00:00Z", Terms(PartyResponse.Rejected, "2026-11-01", Lari(700)))],
        };
        var old = SubrogationReport.OnDate(rejecting.History, D("2026-10-31"));
        Assert.Equal(PartyResponse.Rejected, old.IsUnknown ? null : old.Value.Response); // negative control: the old reading
        var card = SubrogationReport.CaseCardOf(Record("RG31"), [person, rejecting], ms, D("2026-10-31"), TODAY);
        Assert.Equal(T(new PartyTermsOnDate(null, null, OrUnknown<BigInteger?>.Unknown)), card.Parties[1].OnDate);
        Assert.NotEqual(Split(0, Lari(1000)), card.Expected); // the rejection of 01.11 does not show on 31.10
        Assert.Equal(Split(null, null), card.Expected); // and without a known limit there is no split on 31.10
        Assert.Equal(Split(0, Lari(1000)), SubrogationReport.CaseCardOf(Record("RG31"), [person, rejecting], ms, D("2026-11-01"), TODAY).Expected); // rejected from 01.11: the person owes all
    }

    [Fact(DisplayName = H88 + "an answer dated 20.10 but recorded on 02.11 counts from 20.10, its own business day")]
    public void H88AnswerCountsFromItsOwnDay()
    {
        var late = insurer with { History = [new("2026-10-01T06:00:00Z", Terms(null, null, Lari(700))), new("2026-11-02T06:00:00Z", Terms(PartyResponse.Accepted, "2026-10-20", Lari(700)))] };
        Assert.Equal(T(new PartyTermsOnDate(PartyResponse.Accepted, D("2026-10-20"), OrUnknown<BigInteger?>.Unknown)), SubrogationReport.TermsOn(late.History, D("2026-10-25")));
        Assert.Equal(T(new PartyTermsOnDate(null, null, OrUnknown<BigInteger?>.Unknown)), SubrogationReport.TermsOn(late.History, D("2026-10-19")));
    }

    [Fact(DisplayName = H88 + "a money status in the audit before anything was confirmed by business day is unknown, not shown")]
    public void H88MoneyStatusBeforeConfirmation()
    {
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-10-10")];
        Recorded<CaseStatus>[] history = [At("2026-10-01T06:00:00Z", CaseStatus.Opened), At("2026-10-05T06:00:00Z", CaseStatus.Accepted)];
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, SubrogationReport.StatusOn(history, ms, D("2026-10-06"), D("2026-10-01")));
        Assert.Equal(S(CaseStatus.Opened), SubrogationReport.StatusOn(history, ms, D("2026-10-04"), D("2026-10-01")));
        Assert.Equal(S(CaseStatus.Accepted), SubrogationReport.StatusOn(history, ms, D("2026-10-10"), D("2026-10-01")));
    }

    [Fact(DisplayName = H88 + "no history: the terms are unknown; a later answer is not counted; a limit first recorded later does not reach back")]
    public void H88NoHistory()
    {
        Assert.Equal(OrUnknown<PartyTermsOnDate>.Unknown, SubrogationReport.TermsOn(null, D("2026-10-31")));
        Assert.Equal(OrUnknown<PartyTermsOnDate>.Unknown, SubrogationReport.TermsOn([], D("2026-10-31")));
        Recorded<PartyTerms>[] only = [new("2026-11-05T06:00:00Z", Terms(PartyResponse.Accepted, "2026-11-04", Lari(700)))];
        // Head 7bd7aad gave the limit 700 on 31.10 and so a split of 700 / 300 (Codex H88): negative control.
        var oldFirstRecord = only[0].Value.Limit;
        Assert.Equal(Lari(700), oldFirstRecord);
        Assert.Equal(T(new PartyTermsOnDate(null, null, OrUnknown<BigInteger?>.Unknown)), SubrogationReport.TermsOn(only, D("2026-10-31"))); // the answer dated 04.11 is not yet there, and the limit is not known on 31.10
        var card = SubrogationReport.CaseCardOf(Record("RG26"), [person, insurer with { History = only }], [Mv(MovementKind.Confirmed, 1000, "2026-10-01")], D("2026-10-31"), TODAY);
        Assert.Equal(Split(null, null), card.Expected); // no 700 / 300 on 31.10
        // Before the first record, on its own day and across midnight: the same.
        foreach (var d in new[] { "2026-11-04", "2026-11-05", "2026-11-06" }) Assert.Equal(OrUnknown<BigInteger?>.Unknown, SubrogationReport.TermsOn(only, D(d)).Value.Limit);
        Recorded<PartyTerms>[] midnight = [new("2026-10-31T19:59:50Z", Terms(null, null, Lari(700)))];
        Assert.Equal(OrUnknown<BigInteger?>.Unknown, SubrogationReport.TermsOn(midnight, D("2026-10-31")).Value.Limit);
    }

    // ---- H88, the remaining part ----

    // A transaction begun at 23:59:50 on 31.10 in Tbilisi (19:59:50 UTC) waited on the lock and changed the row on 01.11.
    private const string LateStart = "2026-10-31T19:59:50Z";

    /// <summary>TermsOn / StatusOn of head 8314262: the limit and people's status from the last audit record by the day. Negative controls.</summary>
    private static OrUnknown<BigInteger?> OldLimit(IReadOnlyList<Recorded<PartyTerms>> h, DateOnly d)
    {
        var v = SubrogationReport.OnDate(h, d);
        return v.IsUnknown ? OrUnknown<BigInteger?>.Unknown : v.Value.Limit;
    }

    private static OrUnknown<CaseStatus> OldPeopleStatus(IReadOnlyList<Recorded<CaseStatus>> h, DateOnly d) => SubrogationReport.OnDate(h, d);

    private const string H88B = "H88, the remaining part: what people set and the limit, by what the audit can tell for certain ";

    [Fact(DisplayName = H88B + "a limit 700 → 900 begun late on 31.10: the old reading split 900 / 100 on 31.10; now unknown on every past day; today 900 / 100")]
    public void H88BLimitRaisedLate()
    {
        MovementRecord[] ms = [Mv(MovementKind.Confirmed, 1000, "2026-10-01")];
        var raising = insurer with
        {
            Limit = Lari(900),
            Response = PartyResponse.Accepted,
            ResponseOn = D("2026-10-20"),
            History =
            [
                new("2026-10-01T06:00:00Z", Terms(null, null, Lari(700))),
                new("2026-10-20T06:00:00Z", Terms(PartyResponse.Accepted, "2026-10-20", Lari(700))),
                new(LateStart, Terms(PartyResponse.Accepted, "2026-10-20", Lari(900))),
            ],
        };
        Assert.Equal(OrUnknown<BigInteger?>.Known(Lari(900)), OldLimit(raising.History!, D("2026-10-31"))); // negative control: the old reading took 900 on 31.10
        var on31 = SubrogationReport.CaseCardOf(Record("RG40"), [person, raising], ms, D("2026-10-31"), TODAY);
        Assert.Equal(T(new PartyTermsOnDate(PartyResponse.Accepted, D("2026-10-20"), OrUnknown<BigInteger?>.Unknown)), on31.Parties[1].OnDate);
        Assert.Equal(Split(null, null), on31.Expected); // an unknown limit gives no split, never one shown as known
        var on30 = SubrogationReport.CaseCardOf(Record("RG40"), [person, raising], ms, D("2026-10-30"), TODAY);
        Assert.Equal(Split(null, null), on30.Expected); // the limit has no business day: not even 700 is certain on 30.10 (Q-R6)
        Assert.Equal(Lari(900), on30.Parties[1].Now.Limit); // today's limit is shown apart
        Assert.Equal(new ExpectedNow(TODAY, Lari(900), Lari(100)), on30.ExpectedNow); // positive control: today's terms give a certain split for today
    }

    [Fact(DisplayName = H88B + "opened → disputed begun late on 31.10, nothing confirmed: the old reading said disputed on 31.10; now unknown, opened the day before")]
    public void H88BDisputedLate()
    {
        Recorded<CaseStatus>[] history = [At("2026-10-01T06:00:00Z", CaseStatus.Opened), At(LateStart, CaseStatus.Disputed)];
        Assert.Equal(S(CaseStatus.Disputed), OldPeopleStatus(history, D("2026-10-31"))); // negative control: the old reading
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, SubrogationReport.StatusOn(history, [], D("2026-10-31"), D("2026-10-01")));
        Assert.Equal(S(CaseStatus.Opened), SubrogationReport.StatusOn(history, [], D("2026-10-30"), D("2026-10-01"))); // positive control
        var row = Row(SubrogationReport.ReportOnDate([new ReportCase(Record("RG41", x => x with { StatusNow = CaseStatus.Disputed, StatusHistory = history }), [person], [])], D("2026-10-31")));
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, row?.StatusOnDate);
        Assert.Equal(CaseStatus.Disputed, row?.StatusNow);
    }

    [Fact(DisplayName = H88B + "the Tbilisi day decides: a change begun at 00:30 on 01.11 (20:30 UTC on 31.10) leaves 31.10 certain")]
    public void H88BTbilisiDayDecides()
    {
        Recorded<CaseStatus>[] history = [At("2026-10-01T06:00:00Z", CaseStatus.Opened), At("2026-10-31T20:30:00Z", CaseStatus.Disputed)];
        Assert.Equal(S(CaseStatus.Opened), SubrogationReport.StatusOn(history, [], D("2026-10-31"), D("2026-10-01")));
        Assert.Equal(OrUnknown<CaseStatus>.Unknown, SubrogationReport.StatusOn(history, [], D("2026-11-01"), D("2026-10-01")));
    }

    [Fact(DisplayName = H88B + "a record that changes nothing is no change; an insurer that rejected needs no limit; money statuses stay certain")]
    public void H88BNoChangeRejectedMoney()
    {
        Recorded<PartyTerms>[] same = [new("2026-10-01T06:00:00Z", Terms(null, null, Lari(700))), new("2026-10-15T06:00:00Z", Terms(PartyResponse.Rejected, "2026-10-15", Lari(700)))];
        Assert.Equal(T(new PartyTermsOnDate(PartyResponse.Rejected, D("2026-10-15"), OrUnknown<BigInteger?>.Unknown)), SubrogationReport.TermsOn(same, D("2026-10-31")));
        var rejecting = insurer with
        {
            Response = PartyResponse.Rejected,
            ResponseOn = D("2026-10-15"),
            History = [new("2026-10-01T06:00:00Z", Terms(null, null, Lari(700))), new("2026-10-15T06:00:00Z", Terms(PartyResponse.Rejected, "2026-10-15", Lari(900)))],
        };
        Assert.Equal(Split(0, Lari(1000)), SubrogationReport.CaseCardOf(Record("RG42"), [person, rejecting], [Mv(MovementKind.Confirmed, 1000, "2026-10-01")], D("2026-10-31"), TODAY).Expected); // rejected by then: the person owes all, the unknown limit does not matter
        Recorded<CaseStatus>[] statusHistory = [At("2026-10-01T06:00:00Z", CaseStatus.Opened), At(LateStart, CaseStatus.Disputed)];
        Assert.Equal(S(CaseStatus.Accepted), SubrogationReport.StatusOn(statusHistory, [Mv(MovementKind.Confirmed, 1000, "2026-10-01")], D("2026-10-31"), D("2026-10-01"))); // confirmed by business day: certain from the ledger
    }

    // ---- H89: today's split counts only what is in force today ----

    private static readonly MovementRecord half1 = Mv(MovementKind.Confirmed, 500, "2026-10-01");
    private static readonly MovementRecord half2 = Mv(MovementKind.Confirmed, 500, "2026-11-01"); // dated after today
    private static readonly MovementRecord[] halves = [half1, half2];
    private static readonly PartyRecord accepted = insurer with { Response = PartyResponse.Accepted, ResponseOn = D("2026-10-05") };

    /// <summary>ExpectedNow as it was at head c326856: every confirmation, whatever its day. Negative control.</summary>
    private static ExpectedSplit OldNow(IReadOnlyList<Movement> movements, PartyRecord p)
    {
        var confirmed = Subrogation.Totals(movements).Confirmed;
        var share = p.Limit is { } limit && limit < confirmed ? limit : confirmed;
        return Split(share, confirmed - share);
    }

    private const string H89 = "H89: today's split counts only what is in force today ";

    [Fact(DisplayName = H89 + "today 31.10, 500 confirmed on 01.10 and 500 on 01.11: the old split was 700 / 300; now 500 / 0")]
    public void H89Today()
    {
        Assert.Equal(Split(Lari(700), Lari(300)), OldNow(halves, accepted)); // negative control
        var card = SubrogationReport.CaseCardOf(Record("RG50"), [person, accepted], halves, D("2026-10-31"), D("2026-10-31"));
        Assert.Equal(Now("2026-10-31", Lari(500), 0), card.ExpectedNow);
        Assert.Equal(Lari(500), card.Totals.Confirmed);
    }

    [Fact(DisplayName = H89 + "a day later, 01.11, both are in force: 700 / 300")]
    public void H89DayLater() =>
        Assert.Equal(Now("2026-11-01", Lari(700), Lari(300)), SubrogationReport.CaseCardOf(Record("RG50"), [person, accepted], halves, D("2026-10-31"), D("2026-11-01")).ExpectedNow);

    [Fact(DisplayName = H89 + "a reversal of a confirmation dated after today does not touch today")]
    public void H89ReversalAfterToday()
    {
        var undo = Mv(MovementKind.Reversal, -500, "2026-11-02", half1.Id);
        var card = SubrogationReport.CaseCardOf(Record("RG51"), [person, accepted], [half1, undo], D("2026-10-31"), D("2026-10-31"));
        Assert.Equal(Now("2026-10-31", Lari(500), 0), card.ExpectedNow);
        Assert.Equal(Now("2026-11-02", 0, 0), SubrogationReport.CaseCardOf(Record("RG51"), [person, accepted], [half1, undo], D("2026-10-31"), D("2026-11-02")).ExpectedNow);
    }

    [Fact(DisplayName = H89 + "the date asked for and today stay apart: a card of 30.09 seen on 31.10")]
    public void H89DateAndTodayApart()
    {
        var card = SubrogationReport.CaseCardOf(Record("RG52"), [person, accepted], halves, D("2026-09-30"), D("2026-10-31"));
        Assert.Equal(0, card.Totals.Confirmed); // on 30.09 nothing was confirmed
        Assert.Equal(Split(null, null), card.Expected); // on 30.09 the limit is not known (H88): no split shown as known
        Assert.Equal(Now("2026-10-31", Lari(500), 0), card.ExpectedNow); // today is today
    }

    [Fact(DisplayName = H89 + "an answer dated after today is not in force today")]
    public void H89AnswerAfterToday()
    {
        // History and current row agree: added on 01.10 without an answer, the rejection dated 03.11 recorded on 31.10.
        var later = insurer with
        {
            Response = PartyResponse.Rejected,
            ResponseOn = D("2026-11-03"),
            History = [new("2026-10-01T06:00:00Z", Terms(null, null, Lari(700))), new("2026-10-31T06:00:00Z", Terms(PartyResponse.Rejected, "2026-11-03", Lari(700)))],
        };
        Assert.Equal(Now("2026-10-31", Lari(500), 0), SubrogationReport.CaseCardOf(Record("RG53"), [person, later], [half1], D("2026-10-31"), D("2026-10-31")).ExpectedNow);
        Assert.Equal(Now("2026-11-03", 0, Lari(500)), SubrogationReport.CaseCardOf(Record("RG53"), [person, later], [half1], D("2026-10-31"), D("2026-11-03")).ExpectedNow);
    }

    [Fact(DisplayName = H89 + "today is the Tbilisi day: 20:30 UTC on 31.10 is already 01.11")]
    public void H89TodayIsTheTbilisiDay()
    {
        Assert.Equal(D("2026-11-01"), SubrogationReport.TodayInTbilisi(DateTimeOffset.Parse("2026-10-31T20:30:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(D("2026-10-31"), SubrogationReport.TodayInTbilisi(DateTimeOffset.Parse("2026-10-31T19:59:59Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Contains("today must be a business day", Assert.Throws<ArgumentException>(() => SubrogationReport.CaseCardOf(Record("RG54"), [person], halves, D("2026-10-31"), "31.10.2026")).Message);
    }

    [Fact(DisplayName = H89 + "the split is information only: the balance stays one, on the confirmed amount of the date")]
    public void H89SplitIsInformationOnly()
    {
        var card = SubrogationReport.CaseCardOf(Record("RG55"), [person, accepted], [.. halves, Mv(MovementKind.Received, 200, "2026-10-20", null, "p2")], D("2026-10-31"), D("2026-10-31"));
        Assert.Equal(Lari(300), card.Totals.Balance);
        Assert.Equal(Lari(500), card.ExpectedNow.Insurer!.Value + card.ExpectedNow.Person!.Value); // the split shares what was confirmed by today, no more
    }

    // ---- H89, the remaining part ----

    private static readonly MovementRecord[] c500 = [Mv(MovementKind.Confirmed, 500, "2026-10-01")];

    private static string AtDay(string day) => $"{day}T06:00:00Z";

    /// <summary>SplitToday as it was at head 9c6eddc: the current row's answer if dated by today, else none. Negative control.</summary>
    private static ExpectedSplit OldToday(PartyRecord p, DateOnly today)
    {
        var answered = p.Response is not null && p.ResponseOn is { } on && on <= today;
        var response = answered ? p.Response : null;
        if (response == PartyResponse.Rejected) return Split(0, Lari(500));
        var share = p.Limit is { } limit && limit < Lari(500) ? limit : Lari(500);
        return Split(share, Lari(500) - share);
    }

    private const string H89B = "H89, the remaining part: today's answer comes from the answer history, by its own day ";

    [Fact(DisplayName = H89B + "rejected on 15.10, an acceptance dated 03.11 recorded on 31.10: the old split was 500 / 0 on 31.10; now 0 / 500, as the date's split")]
    public void H89BRejectedThenAccepted()
    {
        var p = insurer with
        {
            Response = PartyResponse.Accepted,
            ResponseOn = D("2026-11-03"),
            History =
            [
                new(AtDay("2026-10-01"), Terms(null, null, Lari(700))),
                new(AtDay("2026-10-15"), Terms(PartyResponse.Rejected, "2026-10-15", Lari(700))),
                new(AtDay("2026-10-31"), Terms(PartyResponse.Accepted, "2026-11-03", Lari(700))),
            ],
        };
        Assert.Equal(Split(Lari(500), 0), OldToday(p, D("2026-10-31"))); // negative control
        var card = SubrogationReport.CaseCardOf(Record("RG60"), [person, p], c500, D("2026-10-31"), D("2026-10-31"));
        Assert.Equal(Now("2026-10-31", 0, Lari(500)), card.ExpectedNow);
        Assert.Equal(Split(0, Lari(500)), card.Expected);
        Assert.Equal(card.Expected, Split(card.ExpectedNow.Insurer, card.ExpectedNow.Person)); // on the same day the two splits agree
        Assert.Equal(Now("2026-11-03", Lari(500), 0), SubrogationReport.CaseCardOf(Record("RG60"), [person, p], c500, D("2026-10-31"), D("2026-11-03")).ExpectedNow); // the acceptance from its day
    }

    [Fact(DisplayName = H89B + "the other way round: accepted on 15.10, a rejection dated 03.11 — 500 / 0 today, 0 / 500 from 03.11")]
    public void H89BAcceptedThenRejected()
    {
        var p = insurer with
        {
            Response = PartyResponse.Rejected,
            ResponseOn = D("2026-11-03"),
            History =
            [
                new(AtDay("2026-10-01"), Terms(null, null, Lari(700))),
                new(AtDay("2026-10-15"), Terms(PartyResponse.Accepted, "2026-10-15", Lari(700))),
                new(AtDay("2026-10-31"), Terms(PartyResponse.Rejected, "2026-11-03", Lari(700))),
            ],
        };
        Assert.Equal(Now("2026-10-31", Lari(500), 0), SubrogationReport.CaseCardOf(Record("RG61"), [person, p], c500, D("2026-10-31"), D("2026-10-31")).ExpectedNow);
        Assert.Equal(Now("2026-11-03", 0, Lari(500)), SubrogationReport.CaseCardOf(Record("RG61"), [person, p], c500, D("2026-10-31"), D("2026-11-03")).ExpectedNow);
    }

    [Fact(DisplayName = H89B + "without a history, an answer dated after today hides what was before it: no split; no answer at all is no answer")]
    public void H89BWithoutHistory()
    {
        var futureOnly = insurer with { Response = PartyResponse.Accepted, ResponseOn = D("2026-11-03"), History = null };
        Assert.Equal(Now("2026-10-31", null, null), SubrogationReport.CaseCardOf(Record("RG62"), [person, futureOnly], c500, D("2026-10-31"), D("2026-10-31")).ExpectedNow);
        var none = insurer with { Response = null, ResponseOn = null, History = null };
        Assert.Equal(Now("2026-10-31", Lari(500), 0), SubrogationReport.CaseCardOf(Record("RG62"), [person, none], c500, D("2026-10-31"), D("2026-10-31")).ExpectedNow);
        var pastOnly = insurer with { Response = PartyResponse.Rejected, ResponseOn = D("2026-10-15"), History = null };
        Assert.Equal(Now("2026-10-31", 0, Lari(500)), SubrogationReport.CaseCardOf(Record("RG62"), [person, pastOnly], c500, D("2026-10-31"), D("2026-10-31")).ExpectedNow);
    }
}
