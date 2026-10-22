using System.Text;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>A product's coverage as the import reads it (policy-issue.ts ProductCoverage: its code and whether it is mandatory).</summary>
public sealed record ProductCoverage(string Code, bool IsMandatory);

/// <summary>A row of the upload: its 1-based number in the sheet (for messages) and its values by the template's headers.</summary>
public sealed record ImportRow(int Row, IReadOnlyDictionary<string, string> Values);

/// <summary>The Excel part of the corporate policy import (policy-import.ts): reading an upload into rows, and its template.</summary>
public static class PolicyImport
{
    /// <summary>Columns of the upload template; coverage columns are added per product coverage code.</summary>
    public static readonly IReadOnlyList<string> BaseColumns =
    [
        "make", "model", "year", "plate", "vin", "value", "vehicle_type", "steering", "usage",
        "effective_from", "effective_to", "currency", "installments",
    ];

    public static List<string> CoverageColumns(IEnumerable<ProductCoverage> coverages) =>
        coverages.SelectMany(c => new[] { $"premium_{c.Code}", $"limit_{c.Code}", $"deductible_{c.Code}" }).ToList();

    /// <summary>Serial dates from Excel become ISO dates; everything else is trimmed text.</summary>
    private static string CellText(object? value) => value switch
    {
        null => "",
        JsDate d => JsSemantics.Slice(d.ToIsoString(), 0, 10),
        _ => JsSemantics.Trim(JsSemantics.ToJsString(value)),
    };

    /// <summary>A workbook file: .xlsx is a zip (PK), the old .xls a compound file (D0 CF 11 E0). Anything else is read as text.</summary>
    private static bool IsWorkbook(byte[] b) =>
        (b.Length > 1 && b[0] == 0x50 && b[1] == 0x4b) || (b.Length > 3 && b[0] == 0xd0 && b[1] == 0xcf && b[2] == 0x11 && b[3] == 0xe0);

    /// <summary>
    /// Reads the first sheet of an .xlsx/.xls/.csv upload into rows keyed by the template headers.
    /// Empty cells fall back to the defaults given on the import form (dates, currency, instalments).
    /// A CSV is text: it is decoded as UTF-8 (a byte-order mark, if any, dropped) before it is read, so Georgian text comes
    /// through as written (review PR220-F2). A workbook is read as it is. Any failure to read throws (the page says "unreadable").
    /// </summary>
    public static List<ImportRow> ParseImport(byte[] buffer, IReadOnlyList<ProductCoverage> coverages, IReadOnlyDictionary<string, string> defaults)
    {
        ReadSheet? sheet;
        if (IsWorkbook(buffer))
        {
            if (buffer[0] == 0x50)
            {
                var book = XlsxReader.Read(buffer, new XlsxReader.Options(CellDates: true));
                sheet = book.SheetNames.Count > 0 ? book.Sheets[book.SheetNames[0]] : null;
            }
            else sheet = XlsReader.ReadFirstSheet(buffer);
        }
        else
        {
            var text = new UTF8Encoding(false, false).GetString(buffer);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            sheet = DsvReader.Read(text);
        }
        if (sheet is null) return [];
        var records = SheetToJson.ToRecords(sheet, "");
        var columns = BaseColumns.Concat(CoverageColumns(coverages)).ToList();
        var rows = new List<ImportRow>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in columns) values[column] = CellText(record.TryGetValue(column, out var v) ? v : null);
            foreach (var (key, value) in defaults) if (!values.TryGetValue(key, out var have) || have.Length == 0) values[key] = value;
            // Map coverage columns to the field names the policy validator expects.
            foreach (var c in coverages)
            {
                values[$"cov_{c.Code}_premium"] = values.GetValueOrDefault($"premium_{c.Code}") ?? "";
                values[$"cov_{c.Code}_limit"] = values.GetValueOrDefault($"limit_{c.Code}") ?? "";
                values[$"cov_{c.Code}_deductible"] = values.GetValueOrDefault($"deductible_{c.Code}") ?? "";
            }
            if (values.Values.Any(x => x.Length > 0)) rows.Add(new ImportRow(index + 2, values));
        }
        return rows;
    }

    /// <summary>An empty template with the headers and one example row, as an .xlsx file.</summary>
    public static byte[] BuildTemplate(IReadOnlyList<ProductCoverage> coverages)
    {
        var headers = BaseColumns.Concat(CoverageColumns(coverages)).ToList();
        var example = new List<KeyValuePair<string, object?>>
        {
            new("make", "Toyota"), new("model", "Corolla"), new("year", 2021d), new("plate", "AA-001-AA"), new("vin", ""), new("value", 35000d),
            new("vehicle_type", "car"), new("steering", "left"), new("usage", "commercial"),
            new("effective_from", ""), new("effective_to", ""), new("currency", ""), new("installments", ""),
        };
        foreach (var c in coverages)
        {
            example.Add(new($"premium_{c.Code}", c.IsMandatory ? 700d : ""));
            example.Add(new($"limit_{c.Code}", c.IsMandatory ? 35000d : ""));
            example.Add(new($"deductible_{c.Code}", ""));
        }
        var sheet = XlsxWriter.JsonToSheet([example], headers);
        var workbook = new XlsxBook();
        workbook.AppendSheet(sheet, "policies");
        return workbook.Write();
    }
}
