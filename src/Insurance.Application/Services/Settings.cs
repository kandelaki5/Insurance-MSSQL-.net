using System.Text.RegularExpressions;
using Insurance.Application.Data;

namespace Insurance.Application.Services;

/// <summary>Company settings and product texts (legacy src/lib/settings.ts) and the payment authorisation threshold (src/lib/payment-threshold.ts).</summary>
public static partial class Settings
{
    /// <summary>Company and regulator details used on printed documents, as a key/value map.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadSettingsAsync(Tx db) =>
        (await db.QueryAsync<(string Key, string Value)>("select [key], value from core.setting")).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

    /// <summary>Product texts for printed documents (terms version, exclusions, procedures...).</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadProductTextsAsync(Tx db, Guid productId) =>
        (await db.QueryAsync<(string Key, string Value)>("select [key], value from core.product_text where product_id = @productId", new { productId }))
        .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

    [GeneratedRegex(@"^(\d{1,13})(?:\.(\d{1,2}))?$")]
    private static partial Regex ThresholdText();

    /// <summary>
    /// A threshold as entered: up to 13 digits and 2 decimals, in GEL → tetri; anything else null. The company's
    /// threshold above which one payment needs the financial director's authorisation (owner, 17.10.2026, docs/05 ვ8:
    /// 10 000 GEL to start with; the setting payment.fd_threshold_gel). Company-specific, so a setting, not code.
    /// </summary>
    public static long? ParseThreshold(string value)
    {
        var m = ThresholdText().Match(value.Trim());
        return m.Success ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 100 + long.Parse(m.Groups[2].Value.PadRight(2, '0'), System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The stored threshold in tetri. Missing or not an amount is an error, never "no threshold" (the database says the same, CQ004).</summary>
    public static async Task<long> AuthorisationThresholdAsync(Tx db)
    {
        var value = await db.QuerySingleOrDefaultAsync<string>("select value from core.setting where [key] = N'payment.fd_threshold_gel'");
        return (value is null ? null : ParseThreshold(value))
            ?? throw new InvalidOperationException("The payment authorisation threshold (payment.fd_threshold_gel) is not set as an amount");
    }
}
