using System.Globalization;

namespace Insurance.Web.Infrastructure;

/// <summary>Numbers and dates as the legacy pages showed them.</summary>
public static class Fmt
{
    /// <summary>JavaScript's Number(x).toFixed(n): the nearest value of the double, half away from zero.</summary>
    public static string Fixed(decimal value, int digits) => ((double)value).ToString("F" + digits, CultureInfo.InvariantCulture);

    /// <summary>JavaScript's Number(x) printed as is (no trailing zeros): 5.00 → "5".</summary>
    public static string Plain(decimal value) => ((double)value).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>dd.mm.yyyy (legacy to_char(…, 'DD.MM.YYYY')).</summary>
    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
