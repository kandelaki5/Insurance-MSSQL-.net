using System.Numerics;
using Insurance.Application.Services;
using Insurance.Domain.Regulatory;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting.Regulatory;

/// <summary>
/// The supervisory forms filled with the program's data (legacy accounting/regulatory/page.tsx): each figure shown is read back from the very
/// workbook the Excel download gives — its input cells as filled, its formulas as the template works them out.
/// </summary>
public sealed class IndexModel(Rates rates) : AppPageModel
{
    public string Month { get; private set; } = "";
    public string Quarter { get; private set; } = "";
    public string QuarterDay { get; private set; } = "";
    public Insurance.Application.Services.PremiumReport.Report Report { get; private set; } = null!;
    public TemplateWorkbook N1 { get; private set; } = null!;
    public TemplateWorkbook Wb { get; private set; } = null!;
    public string Date { get; private set; } = "";
    public IReadOnlyList<PrecisionProblem> ReservesProblems { get; private set; } = [];
    public IReadOnlyList<PrecisionProblem> N1Problems { get; private set; } = [];
    public HashSet<int> Filled { get; private set; } = [];
    public bool ControlOk { get; private set; }
    public IReadOnlyList<(string Code, Form11Figures Figures)> Unplaced { get; private set; } = [];
    public BigInteger ReceivableGel { get; private set; }
    public BigInteger N14Gross { get; private set; }
    public IReadOnlyList<Form6Row> Form6 { get; private set; } = [];
    public (List<string> Classes, int Rows) Overflow6 { get; private set; }
    public IReadOnlyList<string> Quarters { get; private set; } = [];

    /// <summary>An exact decimal text grouped for reading (1234567.5 → 1,234,567.50); more than two places as they are; none — "—".</summary>
    public static string Grouped(string? text)
    {
        if (text is null) return Insurance.Application.Texts.RegulatoryTexts.Empty;
        var unsigned = text.Replace("-", "");
        var dot = unsigned.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? unsigned : unsigned[..dot];
        var frac = dot < 0 ? "" : unsigned[(dot + 1)..];
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) b.Append(',');
            b.Append(whole[i]);
        }
        return $"{(text.StartsWith('-') ? "-" : "")}{b}.{frac.PadRight(2, '0')}";
    }

    /// <summary>A shown figure as whole tetri (the grouped text without its commas and point), or null.</summary>
    public static BigInteger? TetriOf(string? text) => text is null ? null : BigInteger.Parse(Grouped(text).Replace(",", "").Replace(".", ""), System.Globalization.CultureInfo.InvariantCulture);

    public static string Amount(BigInteger tetri) => Grouped(ReportMath.TetriText(tetri));

    public string? N1Value(string sheet, string reference) => ReservesPlacement.Shown(N1.ValueOf(sheet, reference));
    public string? WbValue(string sheet, string reference) => ReservesPlacement.Shown(Wb.ValueOf(sheet, reference));

    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Redirect("/");
        Month = Insurance.Application.Services.PremiumReport.ReportMonth(Query("month"), Db.Today());
        // Form 6 is quarterly: the quarter asked for, else the month's own; its date is the quarter's last day.
        var asked = Query("quarter");
        Quarter = !string.IsNullOrEmpty(asked) && Insurance.Domain.Regulatory.Form6.QuarterEnd(asked) is not null ? asked : Insurance.Domain.Regulatory.Form6.QuarterOf(Month);
        QuarterDay = Insurance.Domain.Regulatory.Form6.QuarterEnd(Quarter)!;
        var (report, impairment, settings, riCommission, form6) = await Db.ReadTxAsync(async tx =>
        {
            var (r, i, s, rc) = await RegulatoryForms.MonthAsync(tx, rates.LatestRateOnOrBeforeAsync, Month);
            return (r, i, s, rc, await RegulatoryForms.Form6RowsAsync(tx, QuarterDay));
        });
        Report = report;
        N1 = RegulatoryForms.Form1(report, impairment, settings, riCommission);
        ReceivableGel = report.Receivables.Aggregate(BigInteger.Zero, (s, r) => s + r.OutstandingGel);
        N14Gross = new[] { "E10", "E13", "E16" }.Aggregate(BigInteger.Zero, (s, reference) => s + (TetriOf(N1Value(FinancialAnnex.ReceivablesSheet, reference)) ?? 0));
        var placed = RegulatoryForms.Reserves(report, settings);
        Wb = placed.Workbook;
        Date = placed.Date;
        // The downloads are given only when Excel keeps every figure as it is (INS-REGULATORY-RESERVES-XLSX-NATIVE-PRECISION-FIX-20261019).
        ReservesProblems = Wb.PrecisionProblems();
        N1Problems = N1.PrecisionProblems();
        Filled = ReservesPlacement.Form11Rows.Select(r => r.Row).ToHashSet();
        // The control: the form's totals against the month report's own.
        var totals = ReservesPlacement.Form11Columns.Select(c => TetriOf(WbValue(ReservesPlacement.Form11, $"{c}{ReservesPlacement.Form11TotalRow}"))).ToList();
        BigInteger[] expected =
        [
            report.Totals.UnearnedGel, report.Totals.UnearnedGel - report.Totals.CededUnearnedGel, report.ClaimsTotals.RbnsGel,
            report.ClaimsTotals.RbnsGel - report.ClaimsTotals.ReinsurerRbnsGel, report.ClaimsTotals.IbnrGel,
        ];
        ControlOk = totals.Select((x, i) => x == expected[i]).All(ok => ok);
        Unplaced = ReservesPlacement.UnplacedClasses(RegulatoryForms.ToForms(report));
        Form6 = form6;
        Overflow6 = Insurance.Domain.Regulatory.Form6.Form6Overflow(form6);
        var year = int.Parse(Quarter[..4], System.Globalization.CultureInfo.InvariantCulture);
        Quarters = new[] { year - 1, year, year + 1 }.SelectMany(y => new[] { 1, 2, 3, 4 }.Select(n => $"{y}-Q{n}")).ToList();
        return Page();
    }
}
