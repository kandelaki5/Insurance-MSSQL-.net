using System.Security.Cryptography;

namespace Insurance.Domain.Regulatory;

/// <summary>The three official regulator workbooks, embedded as the owner gave them; their hashes are checked when they are filled.</summary>
public static class Templates
{
    private static byte[] Read(string file)
    {
        using var stream = typeof(Templates).Assembly.GetManifestResourceStream($"Insurance.Domain.Regulatory.Templates.{file}")
            ?? throw new FileNotFoundException($"The template {file} is not embedded");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The official reserves placement workbook as the owner gave it (its hash is checked when it is filled).</summary>
    public static byte[] ReservesTemplate() => Read("reserves-placement.xlsx");
    /// <summary>The official financial reporting annex N1 workbook as the owner gave it ("Form 1"; its hash is checked when it is filled).</summary>
    public static byte[] FinancialAnnexTemplate() => Read("financial-annex-n1.xlsx");
    /// <summary>The official quarterly statistical forms workbook („sxva kvartaluri formebi", form 6 first; its hash is checked when it is filled).</summary>
    public static byte[] OtherQuarterlyTemplate() => Read("other-quarterly-forms.xlsx");
    /// <summary>The templates' SHA256SUMS as the old program kept it beside them (hash, two spaces, file name per line).</summary>
    public static string Sha256Sums() => System.Text.Encoding.UTF8.GetString(Read("SHA256SUMS"));

    /// <summary>A file's SHA-256, lowercase hex (node:crypto createHash('sha256').digest('hex')).</summary>
    public static string Sha256Hex(byte[] buf) => Convert.ToHexStringLower(SHA256.HashData(buf));
}
