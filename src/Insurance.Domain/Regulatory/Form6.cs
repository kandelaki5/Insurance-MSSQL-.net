using System.Globalization;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Regulatory;

/// <summary>A treaty with its parties, as the reader gives it (form6-source.ts: core.ri_treaty joined with its reinsurer and broker).</summary>
public sealed record Form6Treaty(
    string Id, string Number, string Kind, string PeriodFrom, string PeriodTo, string? ReinsurerRating, string? RatingAgency,
    string Reinsurer, string? ReinsurerCountry, string? Broker, string? BrokerCountry);
/// <summary>A class a treaty lists (ri_treaty_class), or one placed with it by the day (premium_cession → policy_line).</summary>
public sealed record Form6TreatyClass(string TreatyId, string ClassCode);
/// <summary>A class of the reference (insurance_class).</summary>
public sealed record Form6Class(string Code, string NameKa, int SortOrder);
/// <summary>The raw rows the reader gives: treaties with their parties, the classes each lists, the classes placed facultatively, the classes.</summary>
public sealed record Form6Source(IReadOnlyList<Form6Treaty> Treaties, IReadOnlyList<Form6TreatyClass> TreatyClasses, IReadOnlyList<Form6TreatyClass> PlacedClasses, IReadOnlyList<Form6Class> Classes);
/// <summary>A row of the form: its class and the values of columns B–I.</summary>
public sealed record Form6Row(string ClassCode, string ClassLabel, IReadOnlyDictionary<string, string> Cells);

/// <summary>
/// „ფორმა N 6. გადამზღვევლები და ბროკერები" of the quarterly statistical forms (INS-REGULATORY-STATISTICAL-FORM6-EXISTING-DATA-20261019;
/// docs/regulaciebi/07 §3.5): the official workbook „sxva kvartaluri formebi" as the owner gave it, its first sheet filled for a
/// quarter's last day with the treaties in force then, one row per class × reinsurer × treaty:
///   I the class (code and name), II the reinsurer, III the treaty's kind, IV its period, V/VI the reinsurer's rating and agency,
///   VII the reinsurer's country, VIII/IX the broker and its country — each from the treaty and its parties as stored; a value
///   not kept is left empty, never made up (no "GE", no "NR").
/// A treaty counts on the day by its period (from ≤ day ≤ to), whatever its status now: closing a treaty only stops new cessions,
/// the booked ones stay (reinsurance/treaties/actions.ts), and when it was closed is not kept. Its classes: those the treaty lists
/// (ri_treaty_class), and for a facultative one the classes of the policy lines placed with it by the day. The template's grid has
/// six classes of five rows each (A9:A13 … A34:A38): the supervisor's starting form, not a limit (owner, 20.10.2026: „თუ მეტი
/// სახეობაა გადაზღვეული, ემატება უჯრები ქვევით"; „9 განსხვავებული ვადა თუ არის ამ 9 ხელშეკრულებაზე, დაგვჭირდება 9 raw"). What does
/// not fit goes into rows added below the grid, each with its class; no row is merged with another or left out. Every filled cell
/// wraps its text, so a long name is seen in full on screen and in print.
/// </summary>
public static class Form6
{
    public const string OtherQuarterlyTemplateFile = "other-quarterly-forms.xlsx";
    public const string OtherQuarterlyTemplateSha256 = "9d8c656e7e6d05a73a476291480e148234bd3e3c80a80473de4641be433fdab6";
    public const string Form6Sheet = "გადამზღვევლები და ბროკერები";
    public const int Groups = 6, RowsPerGroup = 5, FirstRow = 9;
    public static readonly IReadOnlyList<string> Columns = ["B", "C", "D", "E", "F", "G", "H", "I"];

    private static string Ddmmyyyy(string iso) => $"{JsSemantics.Slice(iso, 8, 10)}.{JsSemantics.Slice(iso, 5, 7)}.{JsSemantics.Slice(iso, 0, 4)}";
    /// <summary>The class code as the forms write it: '5.1' → '05.1', '13' → '13'.</summary>
    public static string FormCode(string code)
    {
        var parts = code.Split('.');
        var part = parts.Length > 1 ? parts[1] : "";
        return $"{parts[0].PadLeft(2, '0')}{(part.Length > 0 ? "." + part : "")}";
    }

    /// <summary>The form's rows for a day, in the order the form shows them: by the class's place in the reference, then reinsurer, then treaty number.</summary>
    public static List<Form6Row> Form6Rows(Form6Source source, string asOf, IReadOnlyDictionary<string, string> kindLabels)
    {
        var inForce = source.Treaties.Where(t => string.CompareOrdinal(t.PeriodFrom, asOf) <= 0 && string.CompareOrdinal(asOf, t.PeriodTo) <= 0);
        var classOf = new Dictionary<string, Form6Class>();
        foreach (var c in source.Classes) classOf[c.Code] = c;
        var pairs = new OrderedMap<string, (string ClassCode, Form6Treaty Treaty)>();
        foreach (var t in inForce)
        {
            var codes = source.TreatyClasses.Where(x => x.TreatyId == t.Id).Select(x => x.ClassCode)
                .Concat(t.Kind == "facultative" ? source.PlacedClasses.Where(x => x.TreatyId == t.Id).Select(x => x.ClassCode) : []);
            foreach (var code in codes) pairs.Set($"{code} {t.Id}", (code, t));
        }
        static string Text(string? v) => JsSemantics.Trim(v ?? "");
        var rows = pairs.Values.Select(p =>
        {
            var (classCode, t) = p;
            var cls = classOf.GetValueOrDefault(classCode);
            return new
            {
                Row = new Form6Row(classCode, JsSemantics.Trim($"{FormCode(classCode)} {cls?.NameKa ?? ""}"), new Dictionary<string, string>
                {
                    ["B"] = Text(t.Reinsurer), ["C"] = kindLabels.TryGetValue(t.Kind, out var label) ? label : t.Kind, ["D"] = $"{Ddmmyyyy(t.PeriodFrom)} – {Ddmmyyyy(t.PeriodTo)}",
                    ["E"] = Text(t.ReinsurerRating), ["F"] = Text(t.RatingAgency), ["G"] = Text(t.ReinsurerCountry), ["H"] = Text(t.Broker), ["I"] = Text(t.BrokerCountry),
                }),
                Sort = (Order: cls is null ? 9007199254740991d : cls.SortOrder, Code: classCode, Reinsurer: t.Reinsurer, Number: t.Number),
            };
        }).ToList();
        // Two classes may share a place in the reference (sort_order is not unique): the class code keeps each class together
        // (Codex PR358_FORM6_EQUAL_SORT_ORDER_GROUPING_20261020).
        return rows.OrderBy(r => r.Sort, Comparer<(double Order, string Code, string Reinsurer, string Number)>.Create((a, b) =>
        {
            var c = a.Order.CompareTo(b.Order);
            if (c != 0) return c;
            c = JsSemantics.CompareOrdinal(a.Code, b.Code);
            if (c != 0) return c;
            c = JsSemantics.LocaleCompare(a.Reinsurer, b.Reinsurer);
            return c != 0 ? c : JsSemantics.LocaleCompare(a.Number, b.Number);
        })).Select(r => r.Row).ToList();
    }

    /// <summary>The rows by class, each class once, in the order its first row comes — what the template's groups are filled with, and what
    /// the overflow counts (the same grouping for both, whatever order the rows come in).</summary>
    public static List<List<Form6Row>> Form6Groups(IReadOnlyList<Form6Row> rows)
    {
        var byClass = new OrderedMap<string, List<Form6Row>>();
        foreach (var r in rows)
        {
            if (!byClass.TryGet(r.ClassCode, out var list)) byClass.Set(r.ClassCode, list = []);
            list.Add(r);
        }
        return byClass.Values.ToList();
    }

    /// <summary>
    /// Where each row goes: the grid's six groups of five — each of the first six classes with its first five rows, its label once over the
    /// group — and below the grid every row that does not fit, in the same order: a class's rows beyond five, then the classes beyond six,
    /// each row with its own class label.
    /// </summary>
    public static (List<List<Form6Row>> Grid, List<Form6Row> Below) Form6Layout(IReadOnlyList<Form6Row> rows)
    {
        var groups = Form6Groups(rows);
        return (
            groups.Take(Groups).Select(g => g.Take(RowsPerGroup).ToList()).ToList(),
            groups.Take(Groups).SelectMany(g => g.Skip(RowsPerGroup)).Concat(groups.Skip(Groups).SelectMany(g => g)).ToList());
    }

    /// <summary>The rows that go below the grid and their classes, for the page to say so (nothing is refused for them any more).</summary>
    public static (List<string> Classes, int Rows) Form6Overflow(IReadOnlyList<Form6Row> rows)
    {
        var below = Form6Layout(rows).Below;
        return (below.Select(r => FormCode(r.ClassCode)).Distinct().ToList(), below.Count);
    }

    public static string TemplateSha(byte[] buf) => Templates.Sha256Hex(buf);
    /// <summary>The template's column widths (its &lt;cols&gt;, in Excel's character units) for A–I, to know how many lines a wrapped text takes.</summary>
    public static readonly IReadOnlyDictionary<string, double> Widths = new Dictionary<string, double>
    {
        ["A"] = 11.7109375, ["B"] = 32.85546875, ["C"] = 17.28515625, ["D"] = 15.5703125, ["E"] = 17.28515625, ["F"] = 17, ["G"] = 19.5703125, ["H"] = 23.28515625, ["I"] = 20,
    };
    /// <summary>
    /// Points per wrapped line: Excel's own line of the data font (Sylfaen 10) is 13.5, and a row of that height prints its top line cut
    /// once the text runs to several lines (native Excel PDF, 6 lines in 81 pt); 15 leaves the room print needs.
    /// </summary>
    public const int LinePoints = 15;

    private static readonly Regex Spaces = new(JsSemantics.Ws + "+", RegexOptions.CultureInvariant);
    private static IEnumerable<string> Words(string text) => Spaces.Split(text).Where(w => w.Length > 0);

    /// <summary>
    /// The lines a text takes wrapped in a column: words laid greedily on lines of 90 % of the column's width in characters — a little
    /// less than Excel fits on screen (e.g. 21 of column H's 23.3), so not fewer lines than the spreadsheet shows.
    /// </summary>
    public static int WrappedLines(string text, double width)
    {
        var room = Math.Max(1, (int)Math.Floor(width * 0.9));
        int lines = 1, used = 0;
        foreach (var word in Words(text))
        {
            var len = word.Length;
            if (used == 0) { lines += (len - 1) / room; used = (len - 1) % room + 1; continue; }
            if (used + 1 + len <= room) { used += 1 + len; continue; }
            lines += 1 + (len - 1) / room;
            used = (len - 1) % room + 1;
        }
        return lines;
    }

    /// <summary>The grid's last row (A34:A38 is its sixth group); the rows that do not fit are added after it, styled as a row of the grid.</summary>
    public const int LastGridRow = FirstRow + Groups * RowsPerGroup - 1;
    /// <summary>The tallest a row of a spreadsheet may be (points), and so the most wrapped lines one row holds (27 of 15 points).</summary>
    public const int MaxRowPoints = 409;
    public const int MaxRowLines = MaxRowPoints / LinePoints;

    /// <summary>
    /// A text in pieces of at most `maxLines` wrapped lines each, cut only between words (a word longer than that, by its characters): the
    /// pieces, joined by a space, are the text again (its words in order, each once). A text that fits is one piece.
    /// </summary>
    public static List<string> TextPieces(string text, double width, int maxLines = MaxRowLines)
    {
        if (WrappedLines(text, width) <= maxLines) return [text];
        var room = Math.Max(1, (int)Math.Floor(width * 0.9));
        var pieces = new List<string>();
        var piece = "";
        foreach (var w in Words(text))
        {
            var word = w;
            // A word that alone takes more lines than a row holds: whole rows of its characters first.
            while (WrappedLines(word, width) > maxLines)
            {
                if (piece.Length > 0) { pieces.Add(piece); piece = ""; }
                pieces.Add(JsSemantics.Slice(word, 0, room * maxLines));
                word = JsSemantics.Slice(word, room * maxLines);
            }
            var next = piece.Length > 0 ? $"{piece} {word}" : word;
            if (piece.Length > 0 && WrappedLines(next, width) > maxLines) { pieces.Add(piece); piece = word; } else piece = next;
        }
        if (piece.Length > 0) pieces.Add(piece);
        return pieces;
    }

    private static readonly Regex Underscores = new("_+", RegexOptions.CultureInvariant);
    private static readonly Regex SpacedUnderscores = new(JsSemantics.Ws + "*_+", RegexOptions.CultureInvariant);

    /// <summary>The workbook with form 6 filled (texts only: every value stays a text cell), the company and the date in its header.</summary>
    public static TemplateWorkbook FillForm6(byte[] template, IReadOnlyList<Form6Row> rows, string asOf, string companyName)
    {
        var hash = TemplateSha(template);
        if (hash != OtherQuarterlyTemplateSha256) throw new InvalidOperationException($"The quarterly forms template is not the confirmed one ({hash})");
        var (grid, below) = Form6Layout(rows);
        var wb = TemplateWorkbook.Open(template);
        // A text taller than one row may be (409 pt: some 27 lines — a name of several hundred characters; a party's name has no limit) is
        // continued in the rows under its record, a piece of at most 27 lines in each, cut between words (Codex
        // PR358_FORM6_LONG_TEXT_ROW_HEIGHT_EXPORT_20261020). Not one cell merged over the rows: the spreadsheet prints a merged cell that a page
        // break crosses with a blank page and its middle lines cut (native Excel PDF); a row is never split by a page break, so each piece
        // prints whole and the whole text is printed, in order. The record's other values are in its first row.
        List<(string Col, List<string> Pieces)> PiecesOf(Form6Row r, bool withLabel) =>
            (withLabel ? new[] { "A" }.Concat(Columns) : Columns).Select(c =>
            {
                var text = c == "A" ? r.ClassLabel : r.Cells[c];
                return (c, text.Length == 0 ? new List<string>() : TextPieces(text, Widths[c]));
            }).ToList();
        static int SpanOf(List<(string Col, List<string> Pieces)> pieces) => Math.Max(1, pieces.Count == 0 ? 1 : pieces.Max(p => p.Pieces.Count));
        // Where each record goes, the rows first (the footer, the print area and the class labels' merges move down with them), then every
        // value: in the grid a record's group grows by its continuation rows, below the grid it takes them in turn.
        var placed = new List<(int Row, List<(string Col, List<string> Pieces)> Pieces, int Span)>();
        var tops = new List<int>();
        var shift = 0;
        for (var k = 0; k < grid.Count; k++)
        {
            var top = FirstRow + k * RowsPerGroup + shift;
            tops.Add(top);
            var grown = 0;
            for (var i = 0; i < grid[k].Count; i++)
            {
                var pieces = PiecesOf(grid[k][i], false);
                var span = SpanOf(pieces);
                var row = top + i + grown;
                if (span > 1)
                {
                    // Into the group, styled as its own rows: after the record's row, or before it when it is the group's last (the group's bottom
                    // border stays its bottom).
                    var after = row == top + RowsPerGroup - 1 + grown ? row - 1 : row;
                    wb.InsertRows(Form6Sheet, after, span - 1, after, growMerges: true);
                    grown += span - 1;
                }
                placed.Add((row, pieces, span));
            }
            shift += grown;
        }
        var lastGrid = LastGridRow + shift;
        var belowSized = below.Select(r => { var pieces = PiecesOf(r, true); return (Pieces: pieces, Span: SpanOf(pieces)); }).ToList();
        wb.InsertRows(Form6Sheet, lastGrid, belowSized.Sum(b => b.Span), lastGrid - 1);
        var next = lastGrid + 1;
        foreach (var b in belowSized) { placed.Add((next, b.Pieces, b.Span)); next += b.Span; }
        // Every printed page repeats the column headings (rows 7–8: the titles and I–IX). Not only when rows are added: wrapped text makes rows
        // taller, so a form that fits the grid can still print on two pages (Codex PR358_FORM6_MULTIPAGE_PRINT_HEADERS_20261020). The generated
        // file only; the official template is not changed.
        wb.SetPrintTitles(Form6Sheet, 7, 8);
        void Put(string reference, string text) { wb.SetText(Form6Sheet, reference, text); wb.WrapText(Form6Sheet, reference); }
        // The class label spans its group's rows (merged): it does not set a row's height.
        for (var k = 0; k < grid.Count; k++) Put($"A{tops[k]}", grid[k][0].ClassLabel);
        foreach (var p in placed)
        {
            for (var i = 0; i < p.Span; i++)
            {
                var lines = 1;
                foreach (var (col, pieces) in p.Pieces)
                {
                    if (i >= pieces.Count) continue;
                    Put($"{col}{p.Row + i}", pieces[i]);
                    lines = Math.Max(lines, WrappedLines(pieces[i], Widths[col]));
                }
                // A row whose text runs over a line gets its height from the text, not from the spreadsheet's automatic height, which cuts the top
                // line of long Georgian texts in print. A row of one line keeps the automatic height.
                if (lines > 1) wb.SetRowHeight(Form6Sheet, p.Row + i, lines * LinePoints);
            }
        }
        var company = JsSemantics.Trim(companyName);
        if (company.Length > 0) wb.SetText(Form6Sheet, "A4", JsSemantics.ReplaceFirst(wb.TemplateText(Form6Sheet, "A4"), Underscores, company));
        wb.SetText(Form6Sheet, "A5", JsSemantics.ReplaceFirst(wb.TemplateText(Form6Sheet, "A5"), SpacedUnderscores, $" {Ddmmyyyy(asOf)}"));
        return wb;
    }

    private static readonly Regex QuarterRe = new("^([0-9]{4})-Q([1-4])\\z", RegexOptions.CultureInvariant);
    /// <summary>
    /// A quarter's last day: '2026-Q4' → '2026-12-31'; anything else null. The year is the one asked, never another (Codex
    /// PR358_FORM6_QUARTER_YEAR_ALIAS_20261020). Only a date a person can mean on a form (finance/dates.ts isPlausibleDate: 1900–2100) is a
    /// quarter here; '0099-Q4' and the like are refused (null → 400).
    /// </summary>
    public static string? QuarterEnd(string quarter)
    {
        var m = QuarterRe.Match(quarter);
        if (!m.Success) return null;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (year < 1900 || year > 2100) return null;
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 3;
        return new DateOnly(year, month, DateTime.DaysInMonth(year, month)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    /// <summary>The quarter a month is in: '2026-10' → '2026-Q4'.</summary>
    public static string QuarterOf(string month) =>
        $"{JsSemantics.Slice(month, 0, 4)}-Q{JsSemantics.NumberToString(Math.Ceiling(JsSemantics.ParseNumber(JsSemantics.Slice(month, 5, 7)) / 3))}";
}
