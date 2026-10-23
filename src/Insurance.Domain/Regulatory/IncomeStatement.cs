using System.Numerics;

namespace Insurance.Domain.Regulatory;

/// <summary>One policy's (or one claim's) amount in the period on one class and segment, in whole tetri of GEL; <c>Counted</c> — it is
/// counted in the section's number (a policy written, a policy ceded, a loss paid). The id is the policy's, the loss event's or the
/// claim's, by section.</summary>
public sealed record FlowItem(string ClassCode, string Segment, string Id, BigInteger AmountGel, bool Counted);

/// <summary>Commission in the period: on the premium booked in it, and the deferred commission at its start and end (tetri).</summary>
public sealed record CommissionFlow(BigInteger WrittenGel, BigInteger DeferredOpeningGel, BigInteger DeferredClosingGel)
{
    public static readonly CommissionFlow Zero = new(0, 0, 0);
}

/// <summary>The balances a change is taken from, of one day (tetri): non-life UPR and the reinsurer's share, RBNS, IBNR and the reinsurer's
/// share in RBNS (N1-7's figures), and the receivables' impairment (N1-4's).</summary>
public sealed record Balances(string Date, BigInteger UprGel, BigInteger CededUprGel, BigInteger RbnsGel, BigInteger IbnrGel, BigInteger ReinsurerRbnsGel, BigInteger ImpairmentGel);

/// <summary>A treaty's premium booked in the period, with what places it: its kind and the classes it lists (core.ri_treaty_class); for a
/// quota share, also the premium ceded under it and earned in the period, by class — the key its premium is split by.</summary>
public sealed record TreatyPremium(string Id, string Number, string Kind, IReadOnlyList<string> Classes, BigInteger AmountGel, IReadOnlyDictionary<string, BigInteger>? EarnedCeded = null);

/// <summary>The intermediaries' commission: agents' with employees' (N2-1 00130–00150), brokers' (N2-1 00090–00110), banks' (N2-6 00160).</summary>
public sealed record IntermediaryCommission(CommissionFlow Agent, CommissionFlow Broker, CommissionFlow Bank)
{
    public static readonly IntermediaryCommission Zero = new(CommissionFlow.Zero, CommissionFlow.Zero, CommissionFlow.Zero);
}

/// <summary>What the program keeps for the period from the year's start to the reporting date (legacy IncomeStatementSource); the opening
/// balances are those at the end of the previous year.</summary>
public sealed record IncomeStatementSource(
    string From, string To, Balances Opening,
    IReadOnlyList<FlowItem> Written, IReadOnlyList<FlowItem> Ceded, IReadOnlyList<FlowItem> Paid, IReadOnlyList<FlowItem> ReinsurerPaid,
    BigInteger RecoveredGel, CommissionFlow RiCommission, IntermediaryCommission Commission, IReadOnlyList<TreatyPremium> Treaties);

/// <summary>A section's figures of one placed class and segment: the number counted (distinct ids) and the amount.</summary>
public sealed record SectionCell(int Count, BigInteger AmountGel);

/// <summary>Where a treaty's premium (or a part of it) goes: a class's row, or why it is not placed.</summary>
public sealed record TreatyPlace(string? Code, string? Reason);

/// <summary>A treaty's premium allocated: the parts placed on their class's row, the parts apart with why.</summary>
public sealed record TreatyAllocationResult(IReadOnlyList<(string Code, BigInteger AmountGel)> Placed, IReadOnlyList<(string ClassCode, BigInteger AmountGel, string Reason)> Unplaced);

/// <summary>A figure the program keeps that no row of the form takes: a class with no row (part, code, distinct count, amount), or a
/// treaty's premium it cannot place (part "treaty", the treaty's number or "number / class", count 0, the reason).</summary>
public sealed record UnplacedIncomeRow(string Part, string Code, int Count, BigInteger AmountGel, string? Reason = null);

/// <summary>
/// „ფორმა 1"'s profit and loss, its non-life insurance part (owner, chat 20.10.2026; docs/05 ბ6; old program PR #366, head d251848,
/// src/lib/regulatory/income-statement.ts — ported statement by statement): the income statement IS (N2) rows 00010–00140 and 00500, its
/// commission subform IS-COM (N2-1) and the premium and claims analysis P&amp;C(NL) (N4), from the start of the year to the reporting date —
/// always cumulative (answer 1).
///   - P&amp;C(NL), part I: direct written premium = the corrected premium (answer 2), by class × the policy's reporting segment, with the
///     number of policies written; part II: reinsurance premium booked, with the number of policies ceded; part III: gross claims paid,
///     with the number of cases paid (a loss on a class, owner 20.10.2026: „2 სახეობაში 1 ზარალი, 2 შემთხვევაა"); part IV: the reinsurer's
///     share of the claims paid, taken when the payment is booked (answer 4). Inward reinsurance (parts V–VIII) is not kept: empty. Class 5
///     goes to 5.1, as in form 11 (owner, 19.10.2026); another class with no row of its own is not placed.
///   - IS 00010, 00020, 00060, 00070: the template's own formulas from P&amp;C(NL) (and the empty P&amp;C(L), no life business).
///   - IS 00030 / 00040 (the change in UPR gross / the reinsurer's share) and 00080 / 00090 (the change in the claims reserve, RBNS + IBNR,
///     answer 8) are entered as CLOSING − OPENING (answer 3, as read, owner „კი, ასეა"): the template subtracts them, so this gives the
///     earned premium and the incurred claims. Opening = the balance at the end of the previous year; closing = N1-7's.
///   - IS 00100: subrogation and salvage received in the period (answer 5), gross. IS 00120 (bonuses): not kept, empty.
///   - IS 00500: the change in the receivables' impairment, closing − opening (answer 7; N1-4's reserves). N2-7's 01040 is left empty.
///   - IS-COM: the reinsurance commission and the agents' (with employees') and brokers' commission, each wholly deferred and earned pro
///     rata like the premium (docs/04 rule 11; answer 6): 00010 / 00090 / 00130 (not deferred) 0, 00020 / 00100 / 00140 the commission on
///     the premium booked in the period, 00030 / 00110 / 00150 the change in the deferred commission, closing − opening.
///   - An excess-of-loss treaty of one class: its premium in part II's row of that class (owner, 21.10.2026: each class its own XL); one of
///     several classes, of none, or of another kind is not placed — shown apart, never split. A quota share's deposit premium is split by
///     the premium ceded under it and earned in the period, class by class, equally over its classes when none is; a negative key puts the
///     whole premium apart (owner „ვამტკიცებ", 21.10.2026).
///   - A bank's commission → N2-6 00160, accrued in the period, never deferred (owner, 21.10.2026: „18.25 მთლიანად").
///   - N1-8 (BS-DC): the brokers' (00020), the agents' and employees' (00030), the reinsurance commission's (00050) closing deferred; the
///     balance's 00130 / 00270 are the template's formulas from there (owner „კი შევსოს").
/// Every amount is exact to the tetri; the prior year's column stays empty (owner, 19.10.2026).
/// </summary>
public static class IncomeStatement
{
    public const string IsSheet = "IS";
    public const string CommissionSheet = "IS-COM";
    public const string PnlSheet = "P&C(NL)";
    /// <summary>N2-6 „სხვა ხარჯები": 00160 „ბანკის მომსახურების საკომისიოები" (E31) takes the banks' commission.</summary>
    public const string OtherExpensesSheet = "IS-Ex.S & Ex.Ad";
    public const string BankCommissionCell = "E31";
    /// <summary>N1-8 „გადავადებული საკომისიო შემოსავალი/ხარჯი": the brokers' (E11), the agents' (E12), the reinsurance commission's (E16).</summary>
    public const string DeferredSheet = "BS-DC";
    public static readonly IReadOnlyList<string> DeferredInputs = ["E11", "E12", "E16"];
    /// <summary>The sheets whose printed header gets the company and the period.</summary>
    public static readonly IReadOnlyList<string> IncomeHeaderSheets = [IsSheet, CommissionSheet, PnlSheet, OtherExpensesSheet];

    public static readonly IReadOnlyList<string> Segments = ["corporate", "retail", "state"];

    /// <summary>The four parts of P&amp;C(NL) this program fills, in their order.</summary>
    public static readonly IReadOnlyList<string> Parts = ["written", "ceded", "paid", "reinsurerPaid"];

    private static bool Life(string code) => code == "1" || code.StartsWith("1.", StringComparison.Ordinal);

    /// <summary>N1-7's and N1-4's figures of a day as the balances a change is taken from (non-life only; there is no life business).</summary>
    public static Balances BalancesOf(PremiumReport report, ImpairmentForN14 impairment)
    {
        if (impairment.AsOf != report.To) throw new InvalidOperationException($"The impairment report is of {impairment.AsOf}, the month report of {report.To}");
        static BigInteger B(decimal tetri) => new(tetri);
        var classes = report.Classes.Where(c => !Life(c.ClassCode)).ToList();
        var claims = report.Claims.Where(c => !Life(c.ClassCode)).ToList();
        BigInteger Sum<T>(IEnumerable<T> rows, Func<T, decimal> f) => rows.Aggregate(BigInteger.Zero, (s, r) => s + B(f(r)));
        return new Balances(report.To,
            Sum(classes, c => c.UnearnedGel), Sum(classes, c => c.CededUnearnedGel),
            Sum(claims, c => c.RbnsGel), Sum(claims, c => c.IbnrGel), Sum(claims, c => c.ReinsurerRbnsGel),
            Sum(impairment.Totals.Where(t => t.Kind is "client" or "broker" or "agent"), t => t.ReserveGel));
    }

    /// <summary>P&amp;C(NL) part I's row of each class (part II is 46 rows below, part III 94, part IV 141); a parent row is the template's sum.</summary>
    public static readonly IReadOnlyList<(string Code, int Row)> PnlRows = ReservesPlacement.Form11Rows.Select(r => (r.Code, r.Row - 2)).ToList();
    public static readonly IReadOnlyDictionary<string, int> PnlPartOffset = new Dictionary<string, int>(StringComparer.Ordinal) { ["written"] = 0, ["ceded"] = 46, ["paid"] = 94, ["reinsurerPaid"] = 141 };
    /// <summary>Parts I and III: each segment's number and amount columns.</summary>
    public static readonly IReadOnlyDictionary<string, (string Count, string Amount)> SegmentColumns = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["corporate"] = ("E", "F"), ["retail"] = ("G", "H"), ["state"] = ("I", "J"),
    };
    /// <summary>Part IV: each segment's amount column.</summary>
    public static readonly IReadOnlyDictionary<string, string> ShareColumns = new Dictionary<string, string>(StringComparer.Ordinal) { ["corporate"] = "E", ["retail"] = "G", ["state"] = "I" };
    /// <summary>Part II: the number and the amount (no segments).</summary>
    public static readonly (string Count, string Amount) CededColumns = ("E", "I");
    /// <summary>The total rows the IS formulas read: part I's L45, part II's I91, part III's L139, part IV's K186.</summary>
    public static readonly IReadOnlyDictionary<string, string> PnlTotals = new Dictionary<string, string>(StringComparer.Ordinal) { ["written"] = "L45", ["ceded"] = "I91", ["paid"] = "L139", ["reinsurerPaid"] = "K186" };

    private static readonly Dictionary<string, int> RowOf = PnlRows.ToDictionary(r => r.Code, r => r.Row, StringComparer.Ordinal);

    /// <summary>The row of a class code in part I (only a code of <see cref="PnlRows"/>).</summary>
    public static int PnlRow(string code) => RowOf[code];

    /// <summary>The class code whose row a class's figures go to, or null when it has none (class 5 → 5.1).</summary>
    public static string? PlacedCode(string code) =>
        RowOf.ContainsKey(code) ? code : ReservesPlacement.PlacedIn.TryGetValue(code, out var p) && RowOf.ContainsKey(p) ? p : null;

    private static IReadOnlyList<FlowItem> Section(IncomeStatementSource source, string part) => part switch
    {
        "written" => source.Written, "ceded" => source.Ceded, "paid" => source.Paid, "reinsurerPaid" => source.ReinsurerPaid,
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>A section's figures per placed class (in <see cref="PnlRows"/>' order) and segment: the number counted (distinct) and the amount.</summary>
    public static IReadOnlyList<(string Code, IReadOnlyDictionary<string, SectionCell> Segments)> SectionFigures(IEnumerable<FlowItem> items)
    {
        var amounts = PnlRows.ToDictionary(r => r.Code, _ => Segments.ToDictionary(s => s, _ => BigInteger.Zero, StringComparer.Ordinal), StringComparer.Ordinal);
        var ids = PnlRows.ToDictionary(r => r.Code, _ => Segments.ToDictionary(s => s, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var item in items)
        {
            var code = PlacedCode(item.ClassCode);
            if (code is null) continue;
            amounts[code][item.Segment] += item.AmountGel;
            if (item.Counted) ids[code][item.Segment].Add(item.Id);
        }
        return PnlRows.Select(r => (r.Code, (IReadOnlyDictionary<string, SectionCell>)Segments.ToDictionary(s => s, s => new SectionCell(ids[r.Code][s].Count, amounts[r.Code][s]), StringComparer.Ordinal))).ToList();
    }

    /// <summary>Where a treaty's premium goes: an excess-of-loss treaty of one class with a row → that class's row of part II; else why it is
    /// not placed (no class, several classes — the premium is not split —, another kind, or a class with no row of its own).</summary>
    public static TreatyPlace TreatyPlacement(TreatyPremium t)
    {
        if (t.Kind != "excess_of_loss") return new(null, "not-xl");
        if (t.Classes.Count == 0) return new(null, "no-class");
        if (t.Classes.Count > 1) return new(null, "several-classes");
        var code = PlacedCode(t.Classes[0]);
        return code is null ? new(null, "no-row") : new(code, null);
    }

    /// <summary>An amount split exactly in proportion to positive weights: each share rounded down, the tetri left over going one each to the
    /// largest remainders (equal ones by key, ordinal) — the shares add up to the amount.</summary>
    public static IReadOnlyList<(string Key, BigInteger Share)> SplitExact(BigInteger amount, IReadOnlyList<(string Key, BigInteger Weight)> weights)
    {
        var total = weights.Aggregate(BigInteger.Zero, (s, w) => s + w.Weight);
        if (weights.Count == 0 || total <= 0 || weights.Any(w => w.Weight <= 0)) throw new ArgumentException("splitExact: positive weights needed");
        var sign = amount < 0 ? BigInteger.MinusOne : BigInteger.One;
        var whole = amount * sign;
        var parts = weights.Select(w => new SplitPart(w.Key, whole * w.Weight / total, whole * w.Weight % total)).ToList();
        var left = whole - parts.Aggregate(BigInteger.Zero, (s, p) => s + p.Share);
        foreach (var p in parts.OrderByDescending(p => p.Rest).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (left.IsZero) break;
            p.Share += 1;
            left -= 1;
        }
        return parts.Select(p => (p.Key, p.Share * sign)).ToList();
    }

    private sealed class SplitPart(string key, BigInteger share, BigInteger rest)
    {
        public string Key { get; } = key;
        public BigInteger Share { get; set; } = share;
        public BigInteger Rest { get; } = rest;
    }

    /// <summary>Where a treaty's premium goes: an excess-of-loss treaty by <see cref="TreatyPlacement"/>; a quota share's split by its own
    /// cessions' premium earned in the period, or equally over its classes when none is (owner, 21.10.2026), each part on its class's row;
    /// what has no row, or cannot be decided (a negative key), apart with why.</summary>
    public static TreatyAllocationResult TreatyAllocation(TreatyPremium t)
    {
        if (t.AmountGel.IsZero) return new([], []);
        if (t.Kind == "excess_of_loss")
        {
            var p = TreatyPlacement(t);
            return p.Code is { } code ? new([(code, t.AmountGel)], []) : new([], [("", t.AmountGel, p.Reason!)]);
        }
        if (t.Kind != "quota_share") return new([], [("", t.AmountGel, "not-xl")]);
        var key = (t.EarnedCeded ?? new Dictionary<string, BigInteger>()).Where(e => !e.Value.IsZero).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value)).ToList();
        if (key.Any(k => k.Value < 0)) return new([], [("", t.AmountGel, "negative-key")]);
        var weights = key.Count > 0 ? key : t.Classes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(c => (c, BigInteger.One)).ToList();
        if (weights.Count == 0) return new([], [("", t.AmountGel, "no-class")]);
        var placed = new List<(string, BigInteger)>();
        var unplaced = new List<(string, BigInteger, string)>();
        foreach (var (classCode, amountGel) in SplitExact(t.AmountGel, weights))
        {
            if (amountGel.IsZero) continue;
            var code = PlacedCode(classCode);
            if (code is null) unplaced.Add((classCode, amountGel, "no-row"));
            else placed.Add((code, amountGel));
        }
        return new(placed, unplaced);
    }

    /// <summary>Part II's items: the cessions, and each treaty's placed premium on its class (part II has no segments; no policy is counted).</summary>
    public static IReadOnlyList<FlowItem> CededItems(IncomeStatementSource source) =>
        [.. source.Ceded, .. source.Treaties.SelectMany(t => TreatyAllocation(t).Placed.Select(p => new FlowItem(p.Code, "corporate", $"treaty {t.Id}", p.AmountGel, false)))];

    /// <summary>Part III's numbers: <c>Cases</c> — the losses paid on each class, added up (the template's total, a loss on two classes being two
    /// cases); <c>Losses</c> — how many losses that is, each once (a description beside it, not another count of the form).</summary>
    public static (int Cases, int Losses) ClaimCounts(IncomeStatementSource source)
    {
        var cases = SectionFigures(source.Paid).Sum(f => Segments.Sum(s => f.Segments[s].Count));
        var losses = source.Paid.Where(i => i.Counted && PlacedCode(i.ClassCode) is not null).Select(i => i.Id).Distinct(StringComparer.Ordinal).Count();
        return (cases, losses);
    }

    /// <summary>N2-6 00160: the banks' commission accrued in the period, on the premium booked in it — not deferred (owner, 21.10.2026).</summary>
    public static BigInteger BankCommissionGel(IncomeStatementSource source) => source.Commission.Bank.WrittenGel;

    /// <summary>N1-8's inputs: the closing deferred commission of the brokers, of the agents (with employees) and of the reinsurance commission.</summary>
    public static IReadOnlyList<(string Ref, BigInteger Tetri)> DeferredFigures(IncomeStatementSource source) =>
        [("E11", source.Commission.Broker.DeferredClosingGel), ("E12", source.Commission.Agent.DeferredClosingGel), ("E16", source.RiCommission.DeferredClosingGel)];

    /// <summary>The figures the program keeps that no row of the form takes: a class with no row (each section's amount and number), a treaty's
    /// premium it cannot place (with why). The page names them; a bank's commission is never among them (it is not deferred).</summary>
    public static IReadOnlyList<UnplacedIncomeRow> UnplacedIncome(IncomeStatementSource source)
    {
        var output = new List<UnplacedIncomeRow>();
        foreach (var part in Parts)
        {
            var byCode = new Dictionary<string, (HashSet<string> Ids, BigInteger Amount)>(StringComparer.Ordinal);
            foreach (var item in Section(source, part))
            {
                if (PlacedCode(item.ClassCode) is not null) continue;
                if (!byCode.TryGetValue(item.ClassCode, out var u)) u = (new HashSet<string>(StringComparer.Ordinal), BigInteger.Zero);
                if (item.Counted) u.Ids.Add(item.Id);
                byCode[item.ClassCode] = (u.Ids, u.Amount + item.AmountGel);
            }
            foreach (var (code, u) in byCode.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value))) output.Add(new(part, code, u.Ids.Count, u.Amount));
        }
        foreach (var t in source.Treaties)
            foreach (var u in TreatyAllocation(t).Unplaced)
                output.Add(new("treaty", u.ClassCode.Length > 0 ? $"{t.Number} / {u.ClassCode}" : t.Number, 0, u.AmountGel, u.Reason));
        return output;
    }

    /// <summary>IS's input cells filled (column E): the changes in UPR and the claims reserve, closing − opening; recoveries; the impairment.</summary>
    public static readonly IReadOnlyList<string> IsInputs = ["E11", "E12", "E16", "E17", "E18", "E67"];
    public static IReadOnlyList<(string Ref, BigInteger Tetri)> IsFigures(Balances closing, Balances opening, BigInteger recoveredGel) =>
    [
        ("E11", closing.UprGel - opening.UprGel),
        ("E12", closing.CededUprGel - opening.CededUprGel),
        ("E16", closing.RbnsGel + closing.IbnrGel - (opening.RbnsGel + opening.IbnrGel)),
        ("E17", closing.ReinsurerRbnsGel - opening.ReinsurerRbnsGel),
        ("E18", recoveredGel),
        ("E67", closing.ImpairmentGel - opening.ImpairmentGel),
    ];

    /// <summary>IS-COM's input cells filled: per kind the commission not deferred (0), the commission booked, the change in the deferred.</summary>
    public static readonly IReadOnlyList<string> CommissionInputs = ["E10", "E11", "E12", "E18", "E19", "E20", "E22", "E23", "E24"];
    public static IReadOnlyList<(string Ref, BigInteger Tetri)> CommissionFigures(IncomeStatementSource source)
    {
        static BigInteger[] Three(CommissionFlow c) => [0, c.WrittenGel, c.DeferredClosingGel - c.DeferredOpeningGel];
        var (ri, broker, agent) = (Three(source.RiCommission), Three(source.Commission.Broker), Three(source.Commission.Agent));
        return [("E10", ri[0]), ("E11", ri[1]), ("E12", ri[2]), ("E18", broker[0]), ("E19", broker[1]), ("E20", broker[2]), ("E22", agent[0]), ("E23", agent[1]), ("E24", agent[2])];
    }

    /// <summary>P&amp;C(NL)'s input cells filled, with their figures: every placed class's row of parts I–IV (0 where there is none).</summary>
    public static IReadOnlyList<(string Ref, string Text)> PnlCells(IncomeStatementSource source)
    {
        var cells = new List<(string, string)>();
        string Count(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        void Part(string name, Action<int, IReadOnlyDictionary<string, SectionCell>> put)
        {
            foreach (var (code, figures) in SectionFigures(name == "ceded" ? CededItems(source) : Section(source, name))) put(RowOf[code] + PnlPartOffset[name], figures);
        }
        foreach (var name in new[] { "written", "paid" })
            Part(name, (row, f) =>
            {
                foreach (var s in Segments)
                {
                    var (n, a) = SegmentColumns[s];
                    cells.Add(($"{n}{row}", Count(f[s].Count)));
                    cells.Add(($"{a}{row}", AmountText(f[s].AmountGel)));
                }
            });
        // Part II has no segments: the segments added up (a policy has one segment, so it is counted once).
        Part("ceded", (row, f) =>
        {
            cells.Add(($"{CededColumns.Count}{row}", Count(Segments.Sum(x => f[x].Count))));
            cells.Add(($"{CededColumns.Amount}{row}", AmountText(Segments.Aggregate(BigInteger.Zero, (s, x) => s + f[x].AmountGel))));
        });
        Part("reinsurerPaid", (row, f) => { foreach (var s in Segments) cells.Add(($"{ShareColumns[s]}{row}", AmountText(f[s].AmountGel))); });
        return cells;
    }

    public static string AmountText(BigInteger tetri) => Tetri.AmountText((decimal)tetri);

    private static string Ddmmyyyy(string iso) => $"{JsSemantics.Slice(iso, 8, 10)}.{JsSemantics.Slice(iso, 5, 7)}.{JsSemantics.Slice(iso, 0, 4)}";

    /// <summary>The profit and loss filled into the annex: P&amp;C(NL) parts I–IV, IS's inputs, IS-COM's inputs, N2-6 00160, N1-8 and the printed
    /// headers. Refused when the year's figures are not to the month report's day or not from the year's start.</summary>
    public static void FillIncomeStatement(TemplateWorkbook wb, PremiumReport report, Balances closing, IncomeStatementSource source, string? company)
    {
        if (source.To != report.To || closing.Date != report.To) throw new InvalidOperationException($"The profit and loss is to {source.To}, the month report of {report.To}");
        if (source.From != $"{JsSemantics.Slice(report.To, 0, 4)}-01-01") throw new InvalidOperationException($"The profit and loss is not from the year's start: {source.From}");
        foreach (var (reference, text) in PnlCells(source)) wb.SetNumber(PnlSheet, reference, text);
        foreach (var (reference, tetri) in IsFigures(closing, source.Opening, source.RecoveredGel)) wb.SetNumber(IsSheet, reference, AmountText(tetri));
        foreach (var (reference, tetri) in CommissionFigures(source)) wb.SetNumber(CommissionSheet, reference, AmountText(tetri));
        wb.SetNumber(OtherExpensesSheet, BankCommissionCell, AmountText(BankCommissionGel(source)));
        foreach (var (reference, tetri) in DeferredFigures(source)) wb.SetNumber(DeferredSheet, reference, AmountText(tetri));
        var name = company is null ? null : JsSemantics.Trim(company);
        foreach (var sheet in IncomeHeaderSheets)
        {
            if (!string.IsNullOrEmpty(name)) wb.SetText(sheet, "D1", name, create: true);
            wb.SetText(sheet, "B2", $"{JsSemantics.TrimEnd(wb.TemplateText(sheet, "B2"))} {Ddmmyyyy(source.From)} – {Ddmmyyyy(source.To)}");
        }
        // N1-8 is a balance form: its header has the reporting date, as the annex's other balance sheets.
        if (!string.IsNullOrEmpty(name)) wb.SetText(DeferredSheet, "D1", name, create: true);
        wb.SetText(DeferredSheet, "B2", $"{JsSemantics.TrimEnd(wb.TemplateText(DeferredSheet, "B2"))} {Ddmmyyyy(source.To)}");
    }
}
