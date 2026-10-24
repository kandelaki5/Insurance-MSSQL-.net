using Insurance.Domain.Regulatory;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Infrastructure;

/// <summary>The answers of the legacy route handlers that give a file: a plain-text refusal with its status, or the file itself.</summary>
public static class Downloads
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>new Response(text, { status }) of the old routes: plain text, UTF-8.</summary>
    public static ContentResult Text(string text, int status) => new() { Content = text, ContentType = "text/plain; charset=utf-8", StatusCode = status };

    /// <summary>An .xlsx attachment under the old file name.</summary>
    public static IActionResult Xlsx(HttpResponse response, byte[] body, string filename)
    {
        response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        return new FileContentResult(body, XlsxType);
    }

    /// <summary>A filled form (legacy regulatory/export.ts xlsxResponse): the file, or a 422 plain refusal naming the cells Excel would change.</summary>
    public static IActionResult Form(HttpResponse response, TemplateWorkbook workbook, string filename)
    {
        var answer = RegulatoryExport.XlsxResponse(workbook, filename);
        if (answer.Status != 200) return new ContentResult { Content = System.Text.Encoding.UTF8.GetString(answer.Body), ContentType = answer.ContentType, StatusCode = answer.Status };
        response.Headers.ContentDisposition = answer.ContentDisposition;
        return new FileContentResult(answer.Body, answer.ContentType);
    }
}
