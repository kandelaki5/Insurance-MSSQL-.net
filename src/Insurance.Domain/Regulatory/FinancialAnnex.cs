namespace Insurance.Domain.Regulatory;

/// <summary>
/// „ფორმა 1" — the financial reporting annex N1 (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019; owner 19.10.2026: Form 1 is
/// this annex, its insurance part): the official workbook as the owner gave it, filled for a month's end with what the program keeps.
///   - N1-7 (sheet BS-R), non-life reserves: UPR, RBNS, IBNR gross and the reinsurer's share in UPR and RBNS — the monthly report's
///     totals (buildPremiumReport), the same figures as form 11's total. Life rows: not kept, empty.
///   - N1-4 (sheet BS-IR &amp; RR), insurance receivables direct / from brokers / from agents and their impairment — the impairment report's
///     totals per receivable type (owner 19.10.2026), exactly from the stored amounts (buildImpairmentTotalsExact; Codex
///     PR351_N14_SOURCE_DECIMAL_PRECISION_20261019). Reinsurance receivables: not kept, empty.
///   - Info: the company's name, address, phone and e-mail from the settings, and the reporting date; the rest is not kept.
///   - The printed headers of the three sheets filled (BS, BS-IR &amp; RR, BS-R): the company's name beside „მზღვეველი:" (D1) and the date
///     after „ანგარიშგების თარიღი:" (B2:F2) (Codex PR351_FORM1_PRINT_HEADERS_MISSING_20261019); merges and styles as they are.
///   - The balance (BS) and every other sheet: the template's own formulas; their inputs (cash, investments, capital…) are not kept.
/// The column „გასული წლის შესაბამისი პერიოდი" stays empty (owner 19.10.2026: the program does not know when the company's records
/// begin, so a zero there could be "not kept" rather than a real zero).
/// </summary>
public static class FinancialAnnex
{
    public const string N1TemplateFile = "financial-annex-n1.xlsx";
    public const string N1TemplateSha256 = "d9b29acc7f6238bbf76b30e3357cd22f79b3ba2a3053298d1280f2b836905dca";
    public const string ReservesSheet = "BS-R";
    public const string ReceivablesSheet = "BS-IR & RR";
    public const string BalanceSheet = "BS";
    /// <summary>N1-7's input cells this program fills (column E, the reporting period).</summary>
    public static readonly IReadOnlyList<string> N17Inputs = ["E10", "E11", "E12", "E16", "E17"];
    /// <summary>N1-4's input cells this program fills: direct, brokers, agents — each gross and its impairment.</summary>
    public static readonly IReadOnlyList<string> N14Inputs = ["E10", "E11", "E13", "E14", "E16", "E17"];
    /// <summary>The sheets whose printed header gets the company and the date.</summary>
    public static readonly IReadOnlyList<string> HeaderSheets = [BalanceSheet, ReceivablesSheet, ReservesSheet];
    /// <summary>Info's cells filled from the settings and the date.</summary>
    public static class InfoCells { public const string Name = "D6", Period = "D7", Address = "D11", Phone = "D12", Email = "D14"; }
    /// <summary>The balance's insurance lines (formulas from the subforms), for the preview: code, row, and whether the program fills what it sums
    /// (00070 reinsurance receivables, 00130 and 00270 deferred commission, 00210 other insurance liabilities: not kept — their formula's 0
    /// is the template's over empty inputs, not a figure of the program).</summary>
    public static readonly IReadOnlyList<(string Code, int Row, bool Filled)> BalanceLines =
        [("00060", 14, true), ("00070", 15, false), ("00120", 20, true), ("00130", 21, false), ("00200", 30, true), ("00210", 31, false), ("00270", 37, false)];

    private static string Ddmmyyyy(string iso) => $"{JsSemantics.Slice(iso, 8, 10)}.{JsSemantics.Slice(iso, 5, 7)}.{JsSemantics.Slice(iso, 0, 4)}";

    /// <summary>N1-4's six figures, in tetri, from the impairment report's exact totals per receivable type.</summary>
    public static IReadOnlyList<(string Ref, decimal Tetri)> N14Figures(ImpairmentForN14 impairment)
    {
        ImpairmentTotalsExact Of(string kind) => impairment.Totals.FirstOrDefault(t => t.Kind == kind) ?? new ImpairmentTotalsExact(kind, 0, 0, 0);
        var (c, b, a) = (Of("client"), Of("broker"), Of("agent"));
        return [("E10", c.UnpaidPremiumGel), ("E11", c.ReserveGel), ("E13", b.UnpaidPremiumGel), ("E14", b.ReserveGel), ("E16", a.UnpaidPremiumGel), ("E17", a.ReserveGel)];
    }
    /// <summary>N1-7's five figures, in tetri, from the monthly report's totals.</summary>
    public static IReadOnlyList<(string Ref, decimal Tetri)> N17Figures(PremiumReport report) =>
        [("E10", report.Totals.UnearnedGel), ("E11", report.ClaimsTotals.RbnsGel), ("E12", report.ClaimsTotals.IbnrGel), ("E16", report.Totals.CededUnearnedGel), ("E17", report.ClaimsTotals.ReinsurerRbnsGel)];

    /// <summary>The annex filled for a month: N1-7, N1-4 and Info; nothing else is written. Both reports must be of the same day.</summary>
    public static TemplateWorkbook FillFinancialAnnex(byte[] template, PremiumReport report, ImpairmentForN14 impairment, IReadOnlyDictionary<string, string?> settings)
    {
        var hash = ReservesPlacement.TemplateHash(template);
        if (hash != N1TemplateSha256) throw new InvalidOperationException($"The financial annex template is not the confirmed one ({hash})");
        if (impairment.AsOf != report.To) throw new InvalidOperationException($"The impairment report is of {impairment.AsOf}, the month report of {report.To}");
        var wb = TemplateWorkbook.Open(template);
        foreach (var (reference, tetri) in N17Figures(report)) wb.SetNumber(ReservesSheet, reference, Tetri.AmountText(tetri));
        foreach (var (reference, tetri) in N14Figures(impairment)) wb.SetNumber(ReceivablesSheet, reference, Tetri.AmountText(tetri));
        string? Setting(string key) => settings.TryGetValue(key, out var v) ? v : null;
        (string, string?)[] info =
        [
            (InfoCells.Name, Setting("company_name")), (InfoCells.Period, Ddmmyyyy(report.To)), (InfoCells.Address, Setting("company_address")),
            (InfoCells.Phone, Setting("company_phone")), (InfoCells.Email, Setting("company_email")),
        ];
        foreach (var (reference, text) in info) if (!string.IsNullOrEmpty(text) && JsSemantics.Trim(text).Length > 0) wb.SetText("Info", reference, JsSemantics.Trim(text));
        // The printed headers: the label kept, the value after it (a blank company name is not made up).
        var company = Setting("company_name") is { } name ? JsSemantics.Trim(name) : null;
        foreach (var sheet in HeaderSheets)
        {
            // The name in D1, the wide column beside the merged label B1:C1 (in which it would be cut off in print); BS-R has no D1 cell, so it
            // is made, unstyled. The label itself is kept as it is.
            if (!string.IsNullOrEmpty(company)) wb.SetText(sheet, "D1", company, create: true);
            wb.SetText(sheet, "B2", $"{JsSemantics.TrimEnd(wb.TemplateText(sheet, "B2"))} {Ddmmyyyy(report.To)}");
        }
        return wb;
    }
}
