namespace Insurance.Domain.Regulatory;

// The figures of the old program's reports that the forms are filled with — only the fields they read (premium-report.ts PremiumReport,
// impairment-report.ts ImpairmentTotalsExact). Every amount is whole tetri, exactly (the old bigint); the reports themselves belong to
// the finance port.

/// <summary>A class's premium figures at the month end (premium-report.ts ClassRow): UPR and the reinsurer's share in it.</summary>
public sealed record PremiumClassFigures(string ClassCode, decimal UnearnedGel, decimal CededUnearnedGel);
/// <summary>A class's claims figures at the month end (premium-report.ts ClaimsRow): RBNS, the reinsurer's share in it, IBNR.</summary>
public sealed record ClaimsClassFigures(string ClassCode, decimal RbnsGel, decimal ReinsurerRbnsGel, decimal IbnrGel);
/// <summary>The premium totals of the month report.</summary>
public sealed record PremiumTotals(decimal UnearnedGel, decimal CededUnearnedGel);
/// <summary>The claims totals of the month report.</summary>
public sealed record ClaimsTotals(decimal RbnsGel, decimal ReinsurerRbnsGel, decimal IbnrGel);
/// <summary>The month report as the forms read it: its last day (`to`), its class rows and its totals.</summary>
public sealed record PremiumReport(string To, IReadOnlyList<PremiumClassFigures> Classes, PremiumTotals Totals, IReadOnlyList<ClaimsClassFigures> Claims, ClaimsTotals ClaimsTotals);

/// <summary>The impairment report's exact totals of one receivable type ('client', 'broker', 'agent' or 'all').</summary>
public sealed record ImpairmentTotalsExact(string Kind, int Policies, decimal UnpaidPremiumGel, decimal ReserveGel);
/// <summary>The impairment report's N1-4 totals, exactly, of a day.</summary>
public sealed record ImpairmentForN14(string AsOf, IReadOnlyList<ImpairmentTotalsExact> Totals);

/// <summary>Whole tetri as the forms write them: 12345 → "123.45", -5 → "-0.05".</summary>
public static class Tetri
{
    public static string AmountText(decimal tetri)
    {
        if (tetri != decimal.Truncate(tetri)) throw new ArgumentException($"Not whole tetri: {tetri}");
        var neg = tetri < 0;
        var s = System.Numerics.BigInteger.Abs(new System.Numerics.BigInteger(tetri)).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3, '0');
        return $"{(neg ? "-" : "")}{s[..^2]}.{s[^2..]}";
    }
}
