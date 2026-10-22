namespace Insurance.Domain.Regulatory;

/// <summary>The answer to a download: the file (200) or a plain-text refusal (422).</summary>
public sealed record XlsxDownload(int Status, string ContentType, string? ContentDisposition, byte[] Body);

/// <summary>
/// The Excel download of a filled form (INS-REGULATORY-RESERVES-XLSX-NATIVE-PRECISION-FIX-20261019): given out only when Excel will keep
/// every figure of it as it is (TemplateWorkbook.PrecisionProblems); otherwise a plain refusal naming the cells, never a file whose
/// amounts Excel would change, and never an unhandled error. The page shows the exact figures either way.
/// </summary>
public static class RegulatoryExport
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static XlsxDownload XlsxResponse(TemplateWorkbook workbook, string filename)
    {
        var problems = workbook.PrecisionProblems();
        if (problems.Count > 0)
        {
            var shown = problems.Take(10).Select(p => $"{p.Sheet}!{p.Ref}: {p.Exact} → {p.Excel}");
            var more = problems.Count > 10 ? [RegulatoryTexts.PrecisionMore(problems.Count - 10)] : Array.Empty<string>();
            var text = string.Join("\n", new[] { RegulatoryTexts.PrecisionRefused }.Concat(shown).Concat(more));
            return new XlsxDownload(422, "text/plain; charset=utf-8", null, System.Text.Encoding.UTF8.GetBytes(text));
        }
        return new XlsxDownload(200, XlsxType, $"attachment; filename=\"{filename}\"", workbook.ToBuffer());
    }
}
