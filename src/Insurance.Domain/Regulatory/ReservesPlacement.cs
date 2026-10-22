using System.Text.RegularExpressions;

namespace Insurance.Domain.Regulatory;

/// <summary>Form 11's five figures of a class, in whole tetri: C UPR gross, D UPR net, E RBNS gross, F RBNS net, G IBNR.</summary>
public sealed record Form11Figures(decimal C, decimal D, decimal E, decimal F, decimal G)
{
    public decimal this[string column] => column switch { "C" => C, "D" => D, "E" => E, "F" => F, "G" => G, _ => throw new ArgumentOutOfRangeException(nameof(column)) };
    public static Form11Figures operator +(Form11Figures a, Form11Figures b) => new(a.C + b.C, a.D + b.D, a.E + b.E, a.F + b.F, a.G + b.G);
}

/// <summary>The workbook filled and its reporting date (the month's last day).</summary>
public sealed record ReservesPlacementResult(TemplateWorkbook Workbook, string Date);

/// <summary>
/// „სადაზღვევო რეზერვების განთავსება" (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019): the official workbook as the owner gave
/// it (docs/regulaciebi/07-angarishgebis-formebi.md §3.8–3.9; confirmed current on 04.10.2026), filled with what the program already
/// keeps, for a month's end. Form 11 („რეზერვები") takes every non-life class's UPR, ceded UPR, RBNS, the reinsurer's RBNS share and
/// IBNR from the monthly report (buildPremiumReport) — the very figures its page shows; a class without any is a real 0.00. Its life
/// and pension rows and columns stay empty (no such business). Forms 12–14 have no input this program has a source for — receivables,
/// reinsurance settlements and invested assets are not kept — so their input cells stay empty; their formulas, and form 12's lines
/// that come from form 11, are the template's own and are worked out as Excel would.
/// </summary>
public static class ReservesPlacement
{
    public const string ReservesTemplateFile = "reserves-placement.xlsx";
    public const string ReservesTemplateSha256 = "13d0753322ecf89dd1a47a953641f561a58c044df5d766ea8db86a2cfbf9b9c0";
    public const string Form11 = "რეზერვები";
    public const string Form12 = "ინვ. დაქვ. რეზერვ. ოდენობა";
    /// <summary>Form 11's rows that are filled in, by class code (the template's subtotal rows of a class with subclasses are formulas).</summary>
    public static readonly IReadOnlyList<(string Code, int Row)> Form11Rows =
    [
        ("2", 13), ("3.1", 15), ("3.2", 16), ("4", 17), ("5.1", 19), ("5.2", 20), ("6.1", 22), ("6.2", 23), ("6.3", 24), ("7", 25), ("8", 26),
        ("9.1", 28), ("9.2", 29), ("10", 30), ("11.1", 32), ("11.2", 33), ("12", 34), ("13", 35), ("14", 36), ("15.1", 38), ("15.2", 39), ("15.3", 40),
        ("16", 41), ("17.1", 43), ("17.2", 44), ("17.3", 45), ("18", 46),
    ];
    /// <summary>Every class row of form 11 (with its parent and life rows), for showing it as the template has it.</summary>
    public static readonly IReadOnlyList<(string Code, int Row)> Form11AllRows =
    [
        ("1", 8), ("1.1", 9), ("1.2", 10), ("1.3", 11), ("1.4", 12), ("2", 13), ("3", 14), ("3.1", 15), ("3.2", 16), ("4", 17), ("5", 18), ("5.1", 19), ("5.2", 20),
        ("6", 21), ("6.1", 22), ("6.2", 23), ("6.3", 24), ("7", 25), ("8", 26), ("9", 27), ("9.1", 28), ("9.2", 29), ("10", 30), ("11", 31), ("11.1", 32), ("11.2", 33),
        ("12", 34), ("13", 35), ("14", 36), ("15", 37), ("15.1", 38), ("15.2", 39), ("15.3", 40), ("16", 41), ("17", 42), ("17.1", 43), ("17.2", 44), ("17.3", 45),
        ("18", 46), ("19", 47),
    ];
    public const int Form11TotalRow = 48;
    /// <summary>Form 11's columns: C UPR gross, D UPR net, E RBNS gross, F RBNS net, G IBNR (H–J: life and pension reserves, not kept).</summary>
    public static readonly IReadOnlyList<string> Form11Columns = ["C", "D", "E", "F", "G"];
    /// <summary>Form 12's lines worked out from form 11 by the template: the investable UPR, RBNS, IBNR and the reserves to be invested.</summary>
    public static readonly IReadOnlyList<(string Ref, string Line)> Form12Lines =
        [("D8", "UPR"), ("D9", "UPR share"), ("D7", "UPR to invest"), ("D16", "RBNS"), ("D17", "RBNS share"), ("D15", "RBNS to invest"), ("D24", "IBNR"), ("D23", "IBNR to invest"), ("D35", "to invest")];
    /// <summary>Form 12's input cells with no source in the program (left empty).</summary>
    public static readonly IReadOnlyList<string> Form12EmptyInputs = ["D10", "D11", "D12", "D18", "D19", "D20", "D21", "D25", "D26"];

    public static string TemplateHash(byte[] buf) => Templates.Sha256Hex(buf);
    private static string Ddmmyyyy(string iso) => $"{JsSemantics.Slice(iso, 8, 10)}.{JsSemantics.Slice(iso, 5, 7)}.{JsSemantics.Slice(iso, 0, 4)}";

    private static Form11Figures FiguresOf(PremiumReport report, string code)
    {
        var p = report.Classes.FirstOrDefault(x => x.ClassCode == code);
        var c = report.Claims.FirstOrDefault(x => x.ClassCode == code);
        decimal upr = p?.UnearnedGel ?? 0, ceded = p?.CededUnearnedGel ?? 0, rbns = c?.RbnsGel ?? 0, share = c?.ReinsurerRbnsGel ?? 0;
        return new Form11Figures(upr, upr - ceded, rbns, rbns - share, c?.IbnrGel ?? 0);
    }
    /// <summary>A class the template divides into subclasses whose own figures go to one of them (owner, 19.10.2026: what is booked on 5 goes to
    /// 05.1). No other such class is decided.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlacedIn = new Dictionary<string, string> { ["5"] = "5.1" };
    /// <summary>Each class's five form-11 figures at the month end, in tetri: from the report's premium and claims rows, a class with none 0;
    /// a row also takes the figures of the classes placed in it (PlacedIn).</summary>
    public static IReadOnlyList<(string Code, Form11Figures Figures)> Form11FiguresOf(PremiumReport report) =>
        Form11Rows.Select(r => (r.Code, PlacedIn.Where(p => p.Value == r.Code).Aggregate(FiguresOf(report, r.Code), (f, p) => f + FiguresOf(report, p.Key)))).ToList();
    /// <summary>The report's classes that have no input row in form 11 and are not placed in one — a class whose row the template divides into
    /// subclasses, its figures booked on it rather than on one of them, where the owner has not said which. They are not placed: the
    /// page names them, and the form's totals then differ from the report's by exactly them.</summary>
    public static IReadOnlyList<(string Code, Form11Figures Figures)> UnplacedClasses(PremiumReport report)
    {
        var rows = Form11Rows.Select(r => r.Code).Concat(PlacedIn.Keys).ToHashSet();
        var codes = report.Classes.Select(x => x.ClassCode).Concat(report.Claims.Select(x => x.ClassCode)).Distinct().Where(c => !rows.Contains(c)).ToList();
        codes.Sort(string.CompareOrdinal);
        return codes.Select(code => (code, FiguresOf(report, code))).ToList();
    }

    private static readonly Regex Underscores = new("_+", RegexOptions.CultureInvariant);
    private static readonly Regex SpacedUnderscores = new(JsSemantics.Ws + "*_+", RegexOptions.CultureInvariant);

    /// <summary>The template filled for a month's report: form 11's class rows, the company and the date; nothing else is written.</summary>
    public static ReservesPlacementResult FillReservesPlacement(byte[] template, PremiumReport report, string companyName)
    {
        var hash = TemplateHash(template);
        if (hash != ReservesTemplateSha256) throw new InvalidOperationException($"The reserves placement template is not the confirmed one ({hash})");
        var wb = TemplateWorkbook.Open(template);
        foreach (var (code, figures) in Form11FiguresOf(report))
        {
            var row = Form11Rows.First(r => r.Code == code).Row;
            foreach (var col in Form11Columns) wb.SetNumber(Form11, $"{col}{row}", Tetri.AmountText(figures[col]));
        }
        wb.SetText(Form11, "B4", JsSemantics.ReplaceFirst(wb.TemplateText(Form11, "B4"), Underscores, companyName));
        wb.SetText(Form11, "B5", JsSemantics.ReplaceFirst(wb.TemplateText(Form11, "B5"), SpacedUnderscores, $" {Ddmmyyyy(report.To)}"));
        return new ReservesPlacementResult(wb, report.To);
    }

    /// <summary>A value as the preview shows it: empty (no source) as null, a number as its exact decimal text.</summary>
    public static string? Shown(Value v) => v switch { EmptyValue => null, NumValue => Formula.DecimalText(v), StrValue s => s.S, BoolValue b => b.B ? "TRUE" : "FALSE", _ => null };
}
