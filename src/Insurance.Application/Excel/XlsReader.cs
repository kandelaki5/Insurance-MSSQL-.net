using System.Text;
using ExcelDataReader;

namespace Insurance.Application.Excel;

/// <summary>
/// The first sheet of an old .xls (BIFF, a compound file) upload, read with ExcelDataReader into the cells SheetJS gave the policy
/// import: texts, numbers, booleans, a date-formatted number as a date (cellDates), an error as an error. The sheet's extent is that of
/// its cells holding a value (SheetJS took the file's own DIMENSIONS record: a file whose DIMENSIONS begins above or left of its first
/// value — never one Excel writes — is read from its first value here). Excel's 29 February 1900 (serial 60) of a date-formatted cell
/// reads as 28 February, where SheetJS read 1 March.
/// </summary>
public static class XlsReader
{
    static XlsReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ReadSheet? ReadFirstSheet(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        var sheet = new ReadSheet();
        int sc = int.MaxValue, sr = int.MaxValue, ec = -1, er = -1;
        var r = 0;
        while (reader.Read())
        {
            for (var c = 0; c < reader.FieldCount; c++)
            {
                var cell = reader.GetCellError(c) is { } error ? new ReadCell('e', ErrorCode(error))
                    : reader.GetValue(c) switch
                    {
                        null => null,
                        string s => new ReadCell('s', s, W: s),
                        double d => new ReadCell('n', d, W: SheetToJson.GeneralNumber(d)),
                        int i => new ReadCell('n', (double)i, W: SheetToJson.GeneralNumber(i)),
                        bool b => new ReadCell('b', b, W: b ? "TRUE" : "FALSE"),
                        // ExcelDataReader already counts Excel's 1900 days as SheetJS's .xls reader did (a serial before 60 one day on);
                        // a negative serial SheetJS left a number.
                        DateTime t => t < new DateTime(1899, 12, 30) ? new ReadCell('n', t.ToOADate(), W: SheetToJson.GeneralNumber(t.ToOADate())) : new ReadCell('d', JsDate.FromUtc(t)),
                        // A duration format ([h]:mm): ExcelDataReader gives the serial as a time span; SheetJS a date of it, as above.
                        TimeSpan span => span.TotalDays < 0 ? new ReadCell('n', span.TotalDays, W: SheetToJson.GeneralNumber(span.TotalDays))
                            : new ReadCell('d', XlsxReader.NumDate(span.TotalDays < 60 ? span.TotalDays + 1 : span.TotalDays == 60 ? 61 : span.TotalDays)),
                        var other => new ReadCell('s', other.ToString() ?? ""),
                    };
                if (cell is null) continue;
                sheet.Cells[XlsxWriter.EncodeCell(c, r)] = cell;
                sc = Math.Min(sc, c); sr = Math.Min(sr, r); ec = Math.Max(ec, c); er = Math.Max(er, r);
            }
            r++;
        }
        if (ec >= 0) sheet.Ref = XlsxWriter.EncodeRange(sc, sr, ec, er);
        return sheet;
    }

    private static int ErrorCode(CellError e) => e switch
    {
        CellError.NULL => 0x00, CellError.DIV0 => 0x07, CellError.VALUE => 0x0F, CellError.REF => 0x17, CellError.NAME => 0x1D, CellError.NUM => 0x24, CellError.NA => 0x2A,
        _ => 0xFF,
    };
}
