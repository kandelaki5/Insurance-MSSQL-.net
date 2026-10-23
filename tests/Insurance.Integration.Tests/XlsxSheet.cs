using System.Globalization;
using Insurance.Application.Excel;

namespace Insurance.Integration.Tests;

/// <summary>
/// A workbook's sheet read back as rows of cell values (legacy sheet_to_json(…, { header: 1, blankrows: true, defval: '' })): every row of
/// its extent, each cell its value as text — a number as JavaScript prints it (365, -1.6, 109.5), a string as it is, an empty cell ''.
/// The cell values are compared, never the bytes.
/// </summary>
public static class XlsxSheet
{
    public static ReadBook Read(byte[] file) => XlsxReader.Read(file);

    public static string Text(ReadCell? cell) => cell?.V switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public static List<string[]> Rows(ReadBook book, string sheet)
    {
        var s = book.Sheets[sheet];
        if (s.Ref is null) return [];
        var parts = s.Ref.Split(':');
        var (c1, r1) = Split(parts[0]);
        var (c2, r2) = Split(parts.Length > 1 ? parts[1] : parts[0]);
        var rows = new List<string[]>();
        for (var r = r1; r <= r2; r++)
        {
            var row = new string[c2 - c1 + 1];
            for (var c = c1; c <= c2; c++) row[c - c1] = Text(s[$"{Col(c)}{r}"]);
            rows.Add(row);
        }
        return rows;
    }

    public static List<string[]> Rows(byte[] file, string sheet) => Rows(Read(file), sheet);

    private static (int Col, int Row) Split(string reference)
    {
        int c = 0, r = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') c = c * 26 + (ch - 'A' + 1);
            else if (ch is >= '0' and <= '9') r = r * 10 + (ch - '0');
        }
        return (c, r);
    }

    private static string Col(int c)
    {
        var s = "";
        for (; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
        return s;
    }
}
