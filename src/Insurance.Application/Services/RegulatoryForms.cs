using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Regulatory;
using Forms = Insurance.Domain.Regulatory;

namespace Insurance.Application.Services;

/// <summary>
/// The supervisory forms filled with the program's data (legacy accounting/regulatory page and its form1 / form6 / reserves xlsx routes,
/// INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019): the month report and the impairment report's exact totals given to the
/// regulatory forms port (Insurance.Domain.Regulatory) as whole tetri.
/// </summary>
public static class RegulatoryForms
{
    /// <summary>The month report as the forms read it.</summary>
    public static Forms.PremiumReport ToForms(PremiumReport.Report r) => new(
        Iso(r.To),
        r.Classes.Select(c => new PremiumClassFigures(c.ClassCode, (decimal)c.UnearnedGel, (decimal)c.CededUnearnedGel)).ToList(),
        new PremiumTotals((decimal)r.Totals.UnearnedGel, (decimal)r.Totals.CededUnearnedGel),
        r.Claims.Select(c => new ClaimsClassFigures(c.ClassCode, (decimal)c.RbnsGel, (decimal)c.ReinsurerRbnsGel, (decimal)c.IbnrGel)).ToList(),
        new Forms.ClaimsTotals((decimal)r.ClaimsTotals.RbnsGel, (decimal)r.ClaimsTotals.ReinsurerRbnsGel, (decimal)r.ClaimsTotals.IbnrGel));

    /// <summary>The impairment report's N1-4 totals as the forms read them.</summary>
    public static ImpairmentForN14 ToForms(ImpairmentReport.ReportExact r) =>
        new(Iso(r.AsOf), r.Totals.Select(t => new Forms.ImpairmentTotalsExact(t.Kind, t.Policies, (decimal)t.UnpaidPremiumGel, (decimal)t.ReserveGel)).ToList());

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The settings as the forms read them.</summary>
    public static IReadOnlyDictionary<string, string?> SettingsOf(IReadOnlyDictionary<string, string> settings) =>
        settings.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal);

    /// <summary>The month's figures for form 1 and the reserves placement: the month report, then the impairment totals of its last day.</summary>
    public static async Task<(PremiumReport.Report Report, ImpairmentReport.ReportExact Impairment, IReadOnlyDictionary<string, string> Settings)> MonthAsync(
        Tx db, Valuation valuation, string month)
    {
        var report = await PremiumReport.BuildAsync(db, valuation, month);
        var settings = await Settings.LoadSettingsAsync(db);
        var impairment = await ImpairmentReport.BuildTotalsExactAsync(db, valuation, report.To);
        return (report, impairment, settings);
    }

    /// <summary>The official financial reporting annex N1 ("Form 1") filled with the month's insurance figures.</summary>
    public static TemplateWorkbook Form1(PremiumReport.Report report, ImpairmentReport.ReportExact impairment, IReadOnlyDictionary<string, string> settings) =>
        FinancialAnnex.FillFinancialAnnex(Templates.FinancialAnnexTemplate(), ToForms(report), ToForms(impairment), SettingsOf(settings));

    /// <summary>The official reserves placement workbook filled with the month's figures.</summary>
    public static ReservesPlacementResult Reserves(PremiumReport.Report report, IReadOnlyDictionary<string, string> settings) =>
        ReservesPlacement.FillReservesPlacement(Templates.ReservesTemplate(), ToForms(report), settings.TryGetValue("company_name", out var n) ? n : "");

    /// <summary>Form 6's rows for a quarter's last day.</summary>
    public static async Task<List<Form6Row>> Form6RowsAsync(Tx db, string quarterDay) =>
        Form6.Form6Rows(await Form6SourceReader.ReadAsync(db, DateOnly.ParseExact(quarterDay, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), quarterDay, ReinsuranceTexts.Kinds);

    /// <summary>The quarterly statistical forms workbook with form 6 filled for the quarter's last day.</summary>
    public static async Task<TemplateWorkbook> Form6WorkbookAsync(Tx db, string quarterDay)
    {
        var rows = await Form6RowsAsync(db, quarterDay);
        var settings = await Settings.LoadSettingsAsync(db);
        return Form6.FillForm6(Templates.OtherQuarterlyTemplate(), rows, quarterDay, settings.TryGetValue("company_name", out var n) ? n : "");
    }
}
