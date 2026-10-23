using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Insurance.Application.Excel;
using Insurance.Domain.Regulatory;
using static Insurance.Domain.Regulatory.IncomeStatement;

namespace Insurance.Domain.Tests.Regulatory;

// „ფორმა 1"'s profit and loss (owner, chat 20.10.2026; docs/05 ბ6) filled into the official N1 template, every expected figure worked
// out by hand below. The reserve and UPR changes are entered as closing − opening, so the template's own formulas give the earned
// premium and the incurred claims (answer 3 as read: written 1000, UPR 0 → 600 gives earned 400, not 1600).
// (Port of the old program's src/lib/regulatory/income-statement.test.ts, PR #366 head d251848: its 15 tests, the same numbers, on the
// same template; "SheetJS" is Insurance.Application.Excel.XlsxReader, its port.)
public class IncomeStatementTests
{
    private static readonly byte[] Template = Templates.FinancialAnnexTemplate();
    private static BigInteger T(string s) => ExcelCentsTests.TextTetri(s);
    private static decimal D(string s) => (decimal)T(s);

    /// <summary>The month report at 31.10.2026: UPR 600.00 on 5.1 and 30.00 on 5 (ceded 60.00), RBNS 120.00, IBNR 40.00, the reinsurer's RBNS 36.00.</summary>
    private static PremiumReport Report() => new("2026-10-31",
        [new PremiumClassFigures("5", D("30.00"), 0), new PremiumClassFigures("5.1", D("600.00"), D("60.00"))],
        new PremiumTotals(D("630.00"), D("60.00")),
        [new ClaimsClassFigures("5.1", D("120.00"), D("36.00"), D("40.00"))],
        new ClaimsTotals(D("120.00"), D("36.00"), D("40.00")));

    /// <summary>The impairment at 31.10.2026: client 12.50, broker 0, agent 1.00 — 13.50.</summary>
    private static ImpairmentForN14 Impairment() => new("2026-10-31",
        [new("client", 1, D("500.00"), D("12.50")), new("broker", 1, D("300.00"), 0), new("agent", 1, D("20.00"), D("1.00")), new("all", 1, D("820.00"), D("13.50"))]);

    private static FlowItem Item(string classCode, string segment, string id, string amount, bool counted = true) => new(classCode, segment, id, T(amount), counted);

    /// <summary>The year's figures: policies P1–P5 and claims C1–C3 (see each line).</summary>
    private static IncomeStatementSource Source() => new(
        "2026-01-01", "2026-10-31",
        // At 31.12.2025: UPR 200.00, the reinsurer's 20.00; RBNS 50.00, IBNR 10.00, the reinsurer's RBNS 15.00; impairment 5.00.
        new Balances("2025-12-31", T("200.00"), T("20.00"), T("50.00"), T("10.00"), T("15.00"), T("5.00")),
        [
            Item("5.1", "corporate", "P1", "1000.00"), // P1 written in 2026 on 5.1 …
            Item("5", "corporate", "P1", "50.00"), // … and on 5, which goes to 5.1: P1 is counted once there
            Item("5.1", "retail", "P2", "300.00"),
            Item("5.1", "corporate", "P3", "-40.00", false), // P3, written in 2025, cancelled in 2026: its refund, not counted
            Item("6", "retail", "P4", "70.00"), // class 6 has no row of its own (06.1–06.3): not placed
            Item("13", "state", "P5", "200.00"),
        ],
        [Item("5.1", "corporate", "P1", "300.00"), Item("5.1", "retail", "P2", "90.00"), Item("5.1", "corporate", "P3", "-12.00", false)],
        [Item("5.1", "corporate", "C1", "400.00"), Item("5.1", "retail", "C2", "100.00"), Item("5", "corporate", "C3", "25.00")],
        [Item("5.1", "corporate", "C1", "120.00", false), Item("5.1", "retail", "C2", "30.00", false)],
        T("35.00"),
        new CommissionFlow(T("75.60"), T("8.00"), T("30.00")),
        new IntermediaryCommission(
            Agent: new CommissionFlow(T("20.00"), T("5.00"), T("12.00")),
            Broker: new CommissionFlow(T("100.00"), 0, T("60.00")),
            Bank: CommissionFlow.Zero),
        []);

    private static readonly Dictionary<string, string?> Settings = new() { ["company_name"] = "სატესტო კომპანია (გამოგონილი)" };
    private static string? Text(TemplateWorkbook wb, string sheet, string reference) => wb.ValueOf(sheet, reference) switch
    {
        EmptyValue => null, NumValue v => Formula.DecimalText(v), StrValue s => s.S, BoolValue b => b.B ? "true" : "false", _ => null,
    };
    private static int RowOf(string code) => PnlRows.First(r => r.Code == code).Row;
    private static TemplateWorkbook Fill(IncomeStatementSource? s = null, PremiumReport? r = null, ImpairmentForN14? i = null) =>
        FinancialAnnex.FillFinancialAnnex(Template, r ?? Report(), i ?? Impairment(), Settings, income: s ?? Source());

    /// <summary>Treaty premium booked in 2026: XL-1 on 5.1 alone (placed), XL-2 on two classes, XL-3 on none, a quota share's deposit premium on
    /// 5.1 with no cession of its own yet (so on its one class, placed), XL-4 on class 6 (no row of its own), XL-5 with nothing booked.</summary>
    private static List<TreatyPremium> Treaties() =>
    [
        new("x1", "XL-1", "excess_of_loss", ["5.1"], T("12.34")),
        new("x2", "XL-2", "excess_of_loss", ["13", "5.1"], T("20.00")),
        new("x3", "XL-3", "excess_of_loss", [], T("3.00")),
        new("d1", "QS-1", "quota_share", ["5.1"], T("4.00")),
        new("x4", "XL-4", "excess_of_loss", ["6"], T("5.00")),
        new("x5", "XL-5", "excess_of_loss", ["13"], 0),
    ];

    // ---------------------------------------------------------------- the template

    [Fact(DisplayName = "the template's profit and loss: IS and IS-COM input cells; IS's formulas from P&C(NL) and IS-COM; P&C(NL)'s class rows the same in all four parts")]
    public void TheTemplate()
    {
        var wb = TemplateWorkbook.Open(Template);
        foreach (var r in IsInputs) { Assert.True(wb.HasCell(IsSheet, r), r); Assert.False(wb.IsFormula(IsSheet, r), r); }
        foreach (var r in CommissionInputs) { Assert.True(wb.HasCell(CommissionSheet, r), r); Assert.False(wb.IsFormula(CommissionSheet, r), r); }
        Assert.Equal(["00030", "00040", "00080", "00090", "00100", "00500"], new[] { "B11", "B12", "B16", "B17", "B18", "B67" }.Select(r => wb.TemplateText(IsSheet, r)));
        Assert.Equal(["00010", "00020", "00030", "00090", "00100", "00110", "00130", "00140", "00150"],
            new[] { "B10", "B11", "B12", "B18", "B19", "B20", "B22", "B23", "B24" }.Select(r => wb.TemplateText(CommissionSheet, r)));
        Assert.Equal("'P&C(NL)'!L45+'P&C(NL)'!I232", wb.FormulaOf(IsSheet, "E9"));
        Assert.Equal("'P&C(NL)'!I91+'P&C(NL)'!I279", wb.FormulaOf(IsSheet, "E10"));
        Assert.Equal("'P&C(NL)'!L139+'P&C(NL)'!I326", wb.FormulaOf(IsSheet, "E14"));
        Assert.Equal("'P&C(NL)'!K186+'P&C(NL)'!I373", wb.FormulaOf(IsSheet, "E15"));
        Assert.Equal("E9-E10-E11+E12", wb.FormulaOf(IsSheet, "E13"));
        Assert.Equal("E14-E15+E16-E17-E18", wb.FormulaOf(IsSheet, "E19"));
        Assert.Equal("'IS-COM'!E26", wb.FormulaOf(IsSheet, "E21"));
        Assert.Equal("E13-E19-E20+E21", wb.FormulaOf(IsSheet, "E22"));
        Assert.False(wb.IsFormula(IsSheet, "E67"));
        foreach (var (part, reference) in PnlTotals) Assert.True(wb.IsFormula(PnlSheet, reference), part);
        // Every class row: its name the same in parts I–IV, its cells inputs; part I's code is form 11's row's class (5.1 = 00070, avtokasko).
        Assert.Equal("00070", wb.TemplateText(PnlSheet, $"B{RowOf("5.1")}"));
        Assert.Equal("სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", wb.TemplateText(PnlSheet, $"D{RowOf("5.1")}"));
        foreach (var (code, row) in PnlRows)
        {
            // The class's number (C) and its name up to the brackets, where a part names its own rows.
            string Name(int r) => $"{wb.TemplateText(PnlSheet, $"C{r}")} {wb.TemplateText(PnlSheet, $"D{r}").Split(" (")[0]}";
            var name = Name(row);
            foreach (var (part, offset) in PnlPartOffset)
            {
                Assert.True(Name(row + offset) == name, $"{code} {part}");
                IEnumerable<string> cols = part == "ceded" ? [CededColumns.Count, CededColumns.Amount] : part == "reinsurerPaid" ? ShareColumns.Values : SegmentColumns.Values.SelectMany(c => new[] { c.Count, c.Amount });
                foreach (var c in cols)
                {
                    Assert.True(wb.HasCell(PnlSheet, $"{c}{row + offset}"), $"{code} {part} {c}");
                    Assert.False(wb.IsFormula(PnlSheet, $"{c}{row + offset}"), $"{code} {part} {c}");
                }
            }
        }
    }

    // ---------------------------------------------------------------- filled for 2026-01-01 – 2026-10-31

    private static readonly Lazy<TemplateWorkbook> Filled = new(() => Fill());

    [Fact(DisplayName = "P&C(NL): each class × segment's number and amount; class 5 in 5.1 (a policy counted once); a class with none 0; inward reinsurance empty")]
    public void PnlFilled()
    {
        var wb = Filled.Value;
        var r = RowOf("5.1");
        // Part I: 5.1 corporate = P1 1000.00 + 50.00 (class 5) − P3's refund 40.00 = 1010.00, one policy written (P1); retail P2 300.00.
        Assert.Equal(["1", "1010", "1", "300", "0", "0"], new[] { "E", "F", "G", "H", "I", "J" }.Select(c => Text(wb, PnlSheet, $"{c}{r}")));
        Assert.Equal(["2", "1310"], new[] { "K", "L" }.Select(c => Text(wb, PnlSheet, $"{c}{r}")));
        Assert.Equal(["1", "200"], new[] { "I", "J" }.Select(c => Text(wb, PnlSheet, $"{c}{RowOf("13")}")));
        Assert.Equal(["0", "0"], new[] { "E", "F" }.Select(c => Text(wb, PnlSheet, $"{c}{RowOf("2")}")));
        Assert.Equal("1510", Text(wb, PnlSheet, PnlTotals["written"]));
        // Part II: P1 300.00 + P2 90.00 − P3 12.00 = 378.00, two policies ceded.
        Assert.Equal(["2", "378"], new[] { $"E{r + 46}", $"I{r + 46}" }.Select(x => Text(wb, PnlSheet, x)));
        Assert.Equal("378", Text(wb, PnlSheet, PnlTotals["ceded"]));
        // Part III: corporate C1 400.00 + C3 25.00 (class 5) = 425.00, two claims; retail C2 100.00.
        Assert.Equal(["2", "425", "1", "100"], new[] { "E", "F", "G", "H" }.Select(c => Text(wb, PnlSheet, $"{c}{r + 94}")));
        Assert.Equal("525", Text(wb, PnlSheet, PnlTotals["paid"]));
        // Part IV: C1 120.00, C2 30.00.
        Assert.Equal(["120", "30", "0"], new[] { "E", "G", "I" }.Select(c => Text(wb, PnlSheet, $"{c}{r + 141}")));
        Assert.Equal("150", Text(wb, PnlSheet, PnlTotals["reinsurerPaid"]));
        // The parent class 6's row is the template's sum of 06.1–06.3 (0); the unplaced 70.00 is in none of them.
        Assert.Equal("0", Text(wb, PnlSheet, $"F{RowOf("6.1") - 1}"));
        foreach (var reference in new[] { "E198", "I198", "I232", "I279", "I326", "I373" }) Assert.True((Text(wb, PnlSheet, reference) ?? "0") == "0", reference);
        Assert.False(wb.Filled(PnlSheet, "I203"));
        // The prior year's columns stay empty.
        foreach (var reference in new[] { $"M{r}", $"N{r}", $"Q{r + 46}", $"M{r + 94}", $"M{r + 141}" }) Assert.True(Text(wb, PnlSheet, reference) is null, reference);
    }

    [Fact(DisplayName = "IS: the changes as closing − opening; earned premium, incurred claims and the insurance result by the template's formulas")]
    public void IsFilled()
    {
        var wb = Filled.Value;
        // 00030 = 630.00 − 200.00; 00040 = 60.00 − 20.00; 00080 = (120.00 + 40.00) − (50.00 + 10.00); 00090 = 36.00 − 15.00.
        Assert.Equal(["430", "40", "100", "21", "35", "8.5"], IsInputs.Select(r => Text(wb, IsSheet, r)));
        Assert.Equal("1510", Text(wb, IsSheet, "E9"));
        Assert.Equal("378", Text(wb, IsSheet, "E10"));
        Assert.Equal("742", Text(wb, IsSheet, "E13")); // 1510 − 378 − 430 + 40
        Assert.Equal("525", Text(wb, IsSheet, "E14"));
        Assert.Equal("150", Text(wb, IsSheet, "E15"));
        Assert.Equal("419", Text(wb, IsSheet, "E19")); // 525 − 150 + 100 − 21 − 35
        Assert.Null(Text(wb, IsSheet, "E20")); // bonuses: not kept
        Assert.Equal("0.6", Text(wb, IsSheet, "E21"));
        Assert.Equal("323.6", Text(wb, IsSheet, "E22")); // 742 − 419 − 0 + 0.60
        // 00540, profit before tax: 323.60 − the impairment's change 8.50 (13.50 − 5.00); N2-7's 01040 empty, not counted twice.
        Assert.Equal("315.1", Text(wb, IsSheet, "E72"));
        Assert.False(wb.Filled("IS-Ex.T & Ex.F & O", "E119"));
        foreach (var reference in new[] { "F9", "F11", "F16", "F67", "E25", "E27", "E32" }) Assert.False(wb.Filled(IsSheet, reference), reference);
    }

    [Fact(DisplayName = "IS-COM: commission booked and the change in the deferred, each net by the template; the cedent's empty")]
    public void CommissionFilled()
    {
        var wb = Filled.Value;
        Assert.Equal(["0", "75.6", "22", "0", "100", "60", "0", "20", "7"], CommissionInputs.Select(r => Text(wb, CommissionSheet, r)));
        Assert.Equal("53.6", Text(wb, CommissionSheet, "E13")); // 75.60 − (30.00 − 8.00)
        Assert.Equal("40", Text(wb, CommissionSheet, "E21")); // 100.00 − 60.00
        Assert.Equal("13", Text(wb, CommissionSheet, "E25")); // 20.00 − (12.00 − 5.00)
        Assert.Equal("0.6", Text(wb, CommissionSheet, "E26")); // 53.60 − 0 − 40.00 − 13.00
        foreach (var reference in new[] { "E14", "E15", "E16", "F11", "E31" }) Assert.False(wb.Filled(CommissionSheet, reference), reference);
    }

    [Fact(DisplayName = "the printed headers: the company and the period; every figure kept by Excel as it is")]
    public void Headers()
    {
        var wb = Filled.Value;
        foreach (var sheet in IncomeHeaderSheets)
        {
            Assert.True(Text(wb, sheet, "D1") == "სატესტო კომპანია (გამოგონილი)", sheet);
            Assert.True(Text(wb, sheet, "B2") == "ანგარიშგების პერიოდი: 01.01.2026 – 31.10.2026", sheet);
        }
        Assert.Empty(wb.PrecisionProblems());
    }

    [Fact(DisplayName = "what no row takes is named: class 6; a treaty's premium it cannot place, with why; a bank's commission never (not deferred)")]
    public void Unplaced()
    {
        var s = Source() with { Treaties = Treaties() };
        s = s with { Commission = s.Commission with { Bank = new CommissionFlow(T("3.00"), T("0.50"), T("1.00")) } };
        Assert.Equal(
            [
                new UnplacedIncomeRow("written", "6", 1, T("70.00")),
                new UnplacedIncomeRow("treaty", "XL-2", 0, T("20.00"), "several-classes"),
                new UnplacedIncomeRow("treaty", "XL-3", 0, T("3.00"), "no-class"),
                new UnplacedIncomeRow("treaty", "XL-4", 0, T("5.00"), "no-row"),
            ],
            UnplacedIncome(s));
        Assert.Equal([new UnplacedIncomeRow("written", "6", 1, T("70.00"))], UnplacedIncome(Source()));
    }

    // ---------------------------------------------------------------- the owner's answers of 20.10.2026 (docs/05 ბ6)

    [Fact(DisplayName = "a case is a loss on a class (owner: „2 სახეობაში 1 ზარალი, 2 შემთხვევაა\"): paid on two classes, two cases, each class's amount; two claims of one loss in one row, one case; the template's total right")]
    public void LossOnTwoClasses()
    {
        var s = Source() with
        {
            Paid =
            [
                Item("5.1", "corporate", "E1", "400.00"), // event E1: its CASCO claim …
                Item("13", "corporate", "E1", "60.00"), // … and its property claim, the same event
                Item("5", "corporate", "E2", "25.00"), // event E2 on class 5 (→ 5.1) …
                Item("5.1", "corporate", "E2", "15.00"), // … and on 5.1: one row, one event
                Item("5.1", "retail", "E3", "100.00"),
            ],
        };
        var wb = Fill(s);
        int r = RowOf("5.1") + PnlPartOffset["paid"], p = RowOf("13") + PnlPartOffset["paid"];
        // 5.1 corporate: E1 and E2, 400.00 + 25.00 + 15.00 = 440.00; retail E3 100.00; 13 corporate: E1 60.00.
        Assert.Equal(["2", "440", "1", "100"], new[] { "E", "F", "G", "H" }.Select(c => Text(wb, PnlSheet, $"{c}{r}")));
        Assert.Equal(["1", "60"], new[] { "E", "F" }.Select(c => Text(wb, PnlSheet, $"{c}{p}")));
        // Every amount is there: 440 + 100 + 60 = 600.00 (IS 00060).
        Assert.Equal("600", Text(wb, IsSheet, "E14"));
        // The template's total adds the rows: 4 cases (E1 on two classes is two); 3 losses.
        Assert.Equal("4", Text(wb, PnlSheet, "K139"));
        Assert.Equal((4, 3), ClaimCounts(s));
        Assert.Equal((3, 3), ClaimCounts(Source()));
    }

    [Fact(DisplayName = "an excess-of-loss treaty of one class: its premium in part II's row of that class, once, no policy counted; the rest named apart; nothing lost")]
    public void XlOfOneClass()
    {
        var s = Source() with { Treaties = Treaties() };
        var wb = Fill(s);
        var r = RowOf("5.1") + PnlPartOffset["ceded"];
        // 5.1: the cessions 378.00 + XL-1 12.34 + QS-1's deposit 4.00 (its one class, nothing earned yet) = 394.34; still two policies.
        Assert.Equal(["2", "394.34"], new[] { $"E{r}", $"I{r}" }.Select(x => Text(wb, PnlSheet, x)));
        Assert.Equal("0", Text(wb, PnlSheet, $"I{RowOf("13") + PnlPartOffset["ceded"]}"));
        Assert.Equal("394.34", Text(wb, IsSheet, "E10"));
        // Placed + not placed = every treaty's premium: (12.34 + 4.00) + (20.00 + 3.00 + 5.00) = 44.34.
        var unplaced = UnplacedIncome(s).Where(u => u.Part == "treaty").Aggregate(BigInteger.Zero, (x, u) => x + u.AmountGel);
        Assert.Equal(s.Treaties.Aggregate(BigInteger.Zero, (x, t) => x + t.AmountGel), T("16.34") + unplaced);
        Assert.Equal(
            [new TreatyPlace("5.1", null), new TreatyPlace(null, "several-classes"), new TreatyPlace(null, "no-class"), new TreatyPlace(null, "not-xl"), new TreatyPlace(null, "no-row"), new TreatyPlace("13", null)],
            new[] { "x1", "x2", "x3", "d1", "x4", "x5" }.Select(id => TreatyPlacement(s.Treaties.First(t => t.Id == id))));
    }

    [Fact(DisplayName = "a quota share's deposit premium (owner, 21.10.2026): split by its own cessions' premium earned in the period, class by class; equally over its classes when none is; exact to the tetri")]
    public void QuotaShareDeposit()
    {
        static TreatyPremium Qs(IReadOnlyDictionary<string, BigInteger>? earnedCeded, IReadOnlyList<string>? classes = null, string amount = "1000.00") =>
            new("q", "QS-9", "quota_share", classes ?? ["13", "5.1"], T(amount), earnedCeded);
        static Dictionary<string, BigInteger> Key(params (string Code, string Amount)[] k) => k.ToDictionary(x => x.Code, x => T(x.Amount));
        static void Is(TreatyAllocationResult a, (string, BigInteger)[] placed, (string, BigInteger, string)[] unplaced)
        {
            Assert.Equal(placed, a.Placed);
            Assert.Equal(unplaced, a.Unplaced);
        }
        // The owner's example: ceded 600 / 400, earned 300 / 300 — the deposit's 1 000.00 earned in the period goes 500 / 500.
        Is(TreatyAllocation(Qs(Key(("5.1", "300.00"), ("13", "300.00")))), [("13", T("500.00")), ("5.1", T("500.00"))], []);
        // Earned 450 / 150: 750.00 / 250.00.
        Assert.Equal([("13", T("250.00")), ("5.1", T("750.00"))], TreatyAllocation(Qs(Key(("5.1", "450.00"), ("13", "150.00")))).Placed);
        // Nothing earned yet (no cession, or all zero): equally over the treaty's classes.
        Assert.Equal([("13", T("500.00")), ("5.1", T("500.00"))], TreatyAllocation(Qs(null)).Placed);
        Assert.Equal([("13", T("500.00")), ("5.1", T("500.00"))], TreatyAllocation(Qs(new Dictionary<string, BigInteger> { ["5.1"] = 0 })).Placed);
        // Exact: 0.10 over three equal keys — 0.04 / 0.03 / 0.03 (the tetri left over to the first key); 100.00 by 1 : 2 — 33.33 / 66.67.
        Assert.Equal([("13", (BigInteger)4), ("5.1", (BigInteger)3), ("9.1", (BigInteger)3)], SplitExact(T("0.10"), [("13", 1), ("5.1", 1), ("9.1", 1)]));
        Assert.Equal([("13", (BigInteger)3333), ("5.1", (BigInteger)6667)], SplitExact(T("100.00"), [("13", 1), ("5.1", 2)]));
        Assert.Equal([("13", (BigInteger)(-3333)), ("5.1", (BigInteger)(-6667))], SplitExact(T("-100.00"), [("13", 1), ("5.1", 2)]));
        // A class with no row of its own: its part apart, with why; the rest placed. Placed + apart = the premium.
        Is(TreatyAllocation(Qs(Key(("5.1", "300.00"), ("6", "100.00")))), [("5.1", T("750.00"))], [("6", T("250.00"), "no-row")]);
        // A negative key is not decided: the whole premium apart. No class and nothing earned: apart.
        Is(TreatyAllocation(Qs(Key(("5.1", "300.00"), ("13", "-10.00")))), [], [("", T("1000.00"), "negative-key")]);
        Is(TreatyAllocation(Qs(null, [])), [], [("", T("1000.00"), "no-class")]);
        // Another kind (a surplus treaty's own premium): not placed.
        Assert.Equal([("", T("1000.00"), "not-xl")], TreatyAllocation(Qs(null) with { Kind = "surplus" }).Unplaced);
        // In the form: part II's rows take the parts; IS 00020 the whole.
        var s = Source() with { Treaties = [Qs(Key(("5.1", "300.00"), ("13", "100.00")))] };
        var wb = Fill(s);
        Assert.Equal(new string?[] { "1128", "250", "1378" },
            new[] { Text(wb, PnlSheet, $"I{RowOf("5.1") + PnlPartOffset["ceded"]}"), Text(wb, PnlSheet, $"I{RowOf("13") + PnlPartOffset["ceded"]}"), Text(wb, IsSheet, "E10") });
        Assert.DoesNotContain(UnplacedIncome(s), u => u.Part == "treaty");
    }

    [Fact(DisplayName = "a bank's commission in N2-6 00160 (accrued in the period, not deferred — owner, 21.10.2026), not in N2-1; the profit before tax lower by it")]
    public void BankCommission()
    {
        // As the reader gives it: booked 3.00, nothing deferred; a deferred figure, were one ever there, would not count.
        var s = Source();
        s = s with { Commission = s.Commission with { Bank = new CommissionFlow(T("3.00"), 0, 0) } };
        var wb = Fill(s);
        Assert.Equal("3", Text(wb, OtherExpensesSheet, BankCommissionCell));
        Assert.Equal("00160", Text(wb, OtherExpensesSheet, "B31"));
        Assert.Equal("3", Text(wb, OtherExpensesSheet, "E41"));
        Assert.Equal("3", Text(wb, IsSheet, "E65"));
        Assert.Equal("312.1", Text(wb, IsSheet, "E72")); // 315.10 − 3.00
        var deferred = s with { Commission = s.Commission with { Bank = new CommissionFlow(T("3.00"), T("0.50"), T("1.00")) } };
        Assert.Equal("3", Text(Fill(deferred), OtherExpensesSheet, BankCommissionCell));
        // N2-1 as without it: the agents' 20.00 / 7.00, the brokers' 100.00 / 60.00.
        Assert.Equal(["0", "75.6", "22", "0", "100", "60", "0", "20", "7"], CommissionInputs.Select(r => Text(wb, CommissionSheet, r)));
        foreach (var reference in new[] { "E22", "E26", "E40" }) Assert.False(wb.Filled(OtherExpensesSheet, reference), reference);
        // No bank: 00160 is 0.
        Assert.Equal("0", Text(Fill(), OtherExpensesSheet, BankCommissionCell));
    }

    [Fact(DisplayName = "the balance's deferred commission: N1-8 the closing deferred IS-COM's change is taken from; 00130 and 00270 by the template's formulas")]
    public void DeferredCommission()
    {
        var wb = Fill();
        // Brokers 60.00, agents 12.00, the reinsurance commission 30.00 (closing, 31.10.2026).
        Assert.Equal(["60", "12", "30"], DeferredInputs.Select(r => Text(wb, DeferredSheet, r)));
        Assert.Equal(["00020", "00030", "00050"], new[] { "B11", "B12", "B16" }.Select(r => Text(wb, DeferredSheet, r)));
        Assert.Equal("72", Text(wb, DeferredSheet, "E13"));
        Assert.Equal("72", Text(wb, "BS", "E21")); // 00130
        Assert.Equal("30", Text(wb, "BS", "E37")); // 00270
        Assert.Equal(["00130", "00270"], new[] { "B21", "B37" }.Select(r => Text(wb, "BS", r)));
        // The same figures as IS-COM: opening + the change = closing, for each (brokers 0 + 60, agents 5 + 7, reinsurance 8 + 22).
        Assert.Equal(T(Text(wb, DeferredSheet, "E11")!), T("0") + T(Text(wb, CommissionSheet, "E20")!));
        Assert.Equal(T(Text(wb, DeferredSheet, "E12")!), T("5.00") + T(Text(wb, CommissionSheet, "E24")!));
        Assert.Equal(T(Text(wb, DeferredSheet, "E16")!), T("8.00") + T(Text(wb, CommissionSheet, "E12")!));
        // The cedent's and life's lines stay empty; the header: the company and the reporting date (a balance form).
        foreach (var reference in new[] { "E10", "E21", "E22", "E23", "E27", "F11" }) Assert.False(wb.Filled(DeferredSheet, reference), reference);
        Assert.Equal("სატესტო კომპანია (გამოგონილი)", Text(wb, DeferredSheet, "D1"));
        Assert.Equal("ანგარიშგების თარიღი: 31.10.2026", Text(wb, DeferredSheet, "B2"));
    }

    // ---------------------------------------------------------------- the owner's example (answer 3)

    [Fact(DisplayName = "the owner's example (answer 3): earned premium 400.00, not 1600.00; a claims reserve 0 → 250.00 with 100.00 paid gives incurred 350.00")]
    public void OwnersExample()
    {
        var r = Report() with
        {
            Classes = [new PremiumClassFigures("5.1", D("600.00"), 0)],
            Claims = [new ClaimsClassFigures("5.1", D("250.00"), 0, 0)],
        };
        var i = Impairment() with { Totals = Impairment().Totals.Select(t => t with { ReserveGel = 0 }).ToList() };
        var s = Source() with
        {
            Opening = new Balances("2025-12-31", 0, 0, 0, 0, 0, 0),
            Written = [Item("5.1", "retail", "A", "1000.00")], Ceded = [], Paid = [Item("5.1", "retail", "K", "100.00")], ReinsurerPaid = [], RecoveredGel = 0,
        };
        var wb = Fill(s, r, i);
        Assert.Equal("600", Text(wb, IsSheet, "E11"));
        Assert.Equal("400", Text(wb, IsSheet, "E13"));
        Assert.Equal("250", Text(wb, IsSheet, "E16"));
        Assert.Equal("350", Text(wb, IsSheet, "E19"));
    }

    // ---------------------------------------------------------------- exact to the tetri; refusals

    [Fact(DisplayName = "a large written premium and a cent survive as they are; the balances are N1-7's and N1-4's")]
    public void Exact()
    {
        var s = Source() with { Written = [Item("5.1", "corporate", "P1", "90071992547408.09"), Item("5.1", "corporate", "P2", "0.01")] };
        var wb = Fill(s);
        Assert.Equal("90071992547408.1", Text(wb, PnlSheet, $"F{RowOf("5.1")}"));
        Assert.True(wb.PrecisionProblems().Any(p => p.Sheet == PnlSheet), "beyond Excel's digits: the download is withheld");
        Assert.Equal(new Balances("2026-10-31", T("630.00"), T("60.00"), T("120.00"), T("40.00"), T("36.00"), T("13.50")), BalancesOf(Report(), Impairment()));
    }

    [Fact(DisplayName = "another day or not from the year's start: refused; without the year's figures the profit and loss stays as the template has it")]
    public void Refusals()
    {
        Assert.Contains("profit and loss is to 2026-10-30", Assert.Throws<InvalidOperationException>(() => Fill(Source() with { To = "2026-10-30" })).Message);
        Assert.Contains("not from the year's start", Assert.Throws<InvalidOperationException>(() => Fill(Source() with { From = "2026-10-01" })).Message);
        var plain = FinancialAnnex.FillFinancialAnnex(Template, Report(), Impairment(), Settings);
        foreach (var reference in IsInputs) Assert.False(plain.Filled(IsSheet, reference), reference);
        Assert.False(plain.Filled(PnlSheet, $"F{RowOf("5.1")}"));
    }

    [Fact(DisplayName = "the file: the profit and loss sheets change only in cells (and D1 where the template has none); every formula kept; another reader reads the same")]
    public void TheFile()
    {
        var output = Fill().ToBuffer();
        var before = Zip.ReadZip(Template).ToDictionary(e => e.Name);
        var after = Zip.ReadZip(output);
        Assert.Equal(before.Keys, after.Select(e => e.Name));
        static string Strip(string x) => Regex.Replace(x, "<c\\b[^>]*?(?:/>|>[\\s\\S]*?</c>)", "<c/>");
        foreach (var e in after.Where(x => x.Name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)))
        {
            string xmlBefore = Encoding.UTF8.GetString(Zip.EntryData(before[e.Name])), xmlAfter = Encoding.UTF8.GetString(Zip.EntryData(e));
            var withoutNew = new Regex("<c r=\"D1\" t=\"inlineStr\"><is>[\\s\\S]*?</is></c>").Replace(xmlAfter, "", 1);
            Assert.True(Strip(withoutNew) == Strip(xmlBefore), $"{e.Name}: only cells change");
            var was = FinancialAnnexTests.CellsOf(xmlBefore);
            foreach (var (reference, cell) in FinancialAnnexTests.CellsOf(xmlAfter))
            {
                if (!was.TryGetValue(reference, out var old)) { Assert.True(reference == "D1", $"{e.Name}: only D1 is new ({reference})"); continue; }
                var f = Regex.Match(old, "<f\\b[^>]*?(?:/>|>[\\s\\S]*?</f>)");
                if (f.Success) Assert.True(cell.Contains(f.Value, StringComparison.Ordinal), $"{e.Name} {reference}: its formula kept");
            }
        }
        var back = XlsxReader.Read(output);
        Assert.Equal(1010d, back.Sheets[PnlSheet][$"F{RowOf("5.1")}"]!.V);
        Assert.Equal(430d, back.Sheets[IsSheet]["E11"]!.V);
        Assert.Equal("E9-E10-E11+E12", back.Sheets[IsSheet]["E13"]!.F);
        Assert.Equal(75.6, back.Sheets[CommissionSheet]["E11"]!.V);
    }
}
