using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Insurance.Domain.Regulatory;

/// <summary>A cell's exact value: a fraction of two integers, a text, a boolean or nothing.</summary>
public abstract record Value;
public sealed record NumValue(BigInteger N, BigInteger D) : Value;
public sealed record StrValue(string S) : Value;
public sealed record BoolValue(bool B) : Value;
public sealed record EmptyValue : Value { public static readonly EmptyValue Instance = new(); }

/// <summary>A cell's value as Excel holds it (number | string | boolean; null for none).</summary>
public abstract record XValue;
public sealed record XNum(double V) : XValue;
public sealed record XStr(string V) : XValue;
public sealed record XBool(bool V) : XValue;

/// <summary>A cell reference of a formula.</summary>
public sealed record Ref(string? Sheet, int Col, int Row, bool ColAbs, bool RowAbs);

public abstract record Node;
public sealed record NumNode(Value V) : Node;
public sealed record StrNode(string S) : Node;
public sealed record RefNode(Ref R) : Node;
public sealed record RangeNode(Ref A, Ref B) : Node;
public sealed record NegNode(Node X) : Node;
public sealed record BinNode(string Op, Node A, Node B) : Node;
public sealed record CallNode(string Name, IReadOnlyList<Node> Args) : Node;

/// <summary>The cells a formula reads, exactly.</summary>
public interface ICells { Value Value(string sheet, int col, int row); }
/// <summary>The cells a formula reads, as Excel holds them.</summary>
public interface IXCells { XValue? Value(string sheet, int col, int row); }

/// <summary>
/// The formulas of a supervisory template, worked out exactly (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019): numbers,
/// cell references (on the sheet or another, $-fixed or not), ranges, + − × ÷, comparisons, &amp; and the two functions the templates
/// use, SUM and IF. Every number is a fraction of two integers, so a sum of amounts beyond a double's 15 digits stays to its last
/// tetri. Anything else is an error — never a guess.
/// </summary>
public static class Formula
{
    public static readonly Value Empty = EmptyValue.Instance;

    private static BigInteger Gcd(BigInteger a, BigInteger b)
    {
        a = BigInteger.Abs(a); b = BigInteger.Abs(b);
        while (!b.IsZero) (a, b) = (b, a % b);
        return a.IsZero ? BigInteger.One : a;
    }

    public static NumValue Num(BigInteger n) => Num(n, BigInteger.One);
    public static NumValue Num(BigInteger n, BigInteger d)
    {
        if (d.IsZero) throw new InvalidOperationException("#DIV/0!");
        if (d.Sign < 0) { n = -n; d = -d; }
        var g = Gcd(n, d);
        return new NumValue(n / g, d / g);
    }

    private static readonly Regex DecimalRe = new("^(-?)([0-9]+)(?:\\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?\\z", RegexOptions.CultureInvariant);

    /// <summary>A decimal text ("-1234.56") exactly.</summary>
    public static NumValue Decimal(string text)
    {
        var m = DecimalRe.Match(JsSemantics.Trim(text));
        if (!m.Success) throw new FormatException($"Not a number: {text}");
        var frac = m.Groups[3].Success ? m.Groups[3].Value : "";
        var n = BigInteger.Parse(m.Groups[2].Value + frac, CultureInfo.InvariantCulture) * (m.Groups[1].Value.Length > 0 ? -1 : 1);
        var d = BigInteger.Pow(10, frac.Length);
        var e = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        if (e > 0) n *= BigInteger.Pow(10, e); else if (e < 0) d *= BigInteger.Pow(10, -e);
        return Num(n, d);
    }

    private static NumValue AsNum(Value v) => v switch
    {
        NumValue x => x,
        EmptyValue => new NumValue(0, 1),
        BoolValue b => new NumValue(b.B ? 1 : 0, 1),
        StrValue s => JsSemantics.Trim(s.S).Length == 0 ? new NumValue(0, 1) : Decimal(s.S),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>A number's decimal text: exact when its denominator is a power of ten up to 10⁻¹⁰, else to 10 places, half away from zero.</summary>
    public static string DecimalText(Value v)
    {
        var (n, d) = AsNum(v);
        for (var k = 0; k <= 10; k++)
        {
            var scale = BigInteger.Pow(10, k);
            if ((n * scale % d).IsZero) return WithPoint(n * scale / d, k);
        }
        var p = n * BigInteger.Pow(10, 10);
        var a = BigInteger.Abs(p);
        var q = (a * 2 + d) / (2 * d);
        return TrailingZeros.Replace(WithPoint(p.Sign < 0 ? -q : q, 10), "", 1);
    }
    private static readonly Regex TrailingZeros = new("\\.?0+\\z", RegexOptions.CultureInvariant);

    private static string WithPoint(BigInteger i, int k)
    {
        if (k == 0) return i.ToString(CultureInfo.InvariantCulture);
        var neg = i.Sign < 0;
        var s = BigInteger.Abs(i).ToString(CultureInfo.InvariantCulture).PadLeft(k + 1, '0');
        return $"{(neg ? "-" : "")}{s[..^k]}.{s[^k..]}";
    }

    public static int ColNumber(string letters) { var n = 0; foreach (var ch in letters) n = n * 26 + ch - 64; return n; }
    public static string ColLetters(int n)
    {
        var s = "";
        while (n > 0) { var m = (n - 1) % 26; s = (char)(65 + m) + s; n = (n - 1) / 26; }
        return s;
    }

    private static readonly Regex RefRe = new("^(\\$?)([A-Z]{1,3})(\\$?)([0-9]+)", RegexOptions.CultureInvariant);
    private static readonly Regex NumberRe = new("^[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
    private static readonly Regex SheetRe = new("^([^" + JsSemantics.WhiteSpaceChars + "!(),+\\-*/&=<>^\"':]+)!", RegexOptions.CultureInvariant);
    private static readonly Regex FnRe = new("^([A-Z][A-Z0-9.]*)\\(", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingEq = new("^=", RegexOptions.CultureInvariant);

    public static Node ParseFormula(string text) => new Parser(LeadingEq.Replace(JsSemantics.Trim(text), "", 1)).Parse();

    private sealed class Parser(string src)
    {
        private int i;
        private char? Peek() => i < src.Length ? src[i] : null;
        private void Skip() { while (i < src.Length && src[i] == ' ') i++; }
        private FormatException Fail(string what) => new($"Formula {src}: {what} at {i}");

        public Node Parse()
        {
            var node = Compare();
            Skip();
            if (i != src.Length) throw Fail("unexpected text");
            return node;
        }

        private Ref? RefAt(string? sheet)
        {
            var m = RefRe.Match(src[i..]);
            if (!m.Success) return null;
            i += m.Length;
            return new Ref(sheet, ColNumber(m.Groups[2].Value), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), m.Groups[1].Value == "$", m.Groups[3].Value == "$");
        }

        private Node Primary()
        {
            Skip();
            var ch = Peek();
            if (ch is null) throw Fail("an operand expected");
            if (ch == '(') { i++; var x = Compare(); Skip(); if (Peek() != ')') throw Fail(") expected"); i++; return x; }
            if (ch == '-') { i++; return new NegNode(Primary()); }
            if (ch == '+') { i++; return Primary(); }
            if (ch == '"')
            {
                var end = src.IndexOf('"', i + 1);
                if (end < 0) throw Fail("unclosed text");
                var s = src[(i + 1)..end];
                i = end + 1;
                return new StrNode(s);
            }
            var n = NumberRe.Match(src[i..]);
            if (n.Success) { i += n.Length; return new NumNode(Decimal(n.Value)); }
            // A sheet name: quoted, or any run of characters up to "!".
            string? sheet = null;
            if (ch == '\'')
            {
                var end = src.IndexOf("'!", i + 1, StringComparison.Ordinal);
                if (end < 0) throw Fail("unclosed sheet name");
                sheet = src[(i + 1)..end].Replace("''", "'");
                i = end + 2;
            }
            else
            {
                var m = SheetRe.Match(src[i..]);
                if (m.Success) { sheet = m.Groups[1].Value; i += m.Length; }
            }
            var r = RefAt(sheet);
            if (r is not null)
            {
                if (Peek() == ':') { i++; var b = RefAt(sheet) ?? throw Fail("a range's end expected"); return new RangeNode(r, b); }
                return new RefNode(r);
            }
            if (sheet is not null) throw Fail("a cell expected after the sheet name");
            var fn = FnRe.Match(src[i..]);
            if (!fn.Success) throw Fail("an unknown token");
            i += fn.Length;
            var args = new List<Node>();
            Skip();
            if (Peek() == ')') { i++; return new CallNode(fn.Groups[1].Value, args); }
            for (; ; )
            {
                args.Add(Compare());
                Skip();
                if (Peek() == ',') { i++; continue; }
                if (Peek() == ')') { i++; break; }
                throw Fail(", or ) expected");
            }
            return new CallNode(fn.Groups[1].Value, args);
        }

        private Node Binary(Func<Node> next, string[] ops)
        {
            var a = next();
            for (; ; )
            {
                Skip();
                var op = ops.FirstOrDefault(o => src.AsSpan(i).StartsWith(o, StringComparison.Ordinal));
                if (op is null) return a;
                i += op.Length;
                a = new BinNode(op, a, next());
            }
        }
        private static readonly string[] PowerOps = ["^"], MulOps = ["*", "/"], AddOps = ["+", "-"], ConcatOps = ["&"], CompareOps = ["<=", ">=", "<>", "<", ">", "="];
        private Node Power() => Binary(Primary, PowerOps);
        private Node Mul() => Binary(Power, MulOps);
        private Node Add() => Binary(Mul, AddOps);
        private Node Concat() => Binary(Add, ConcatOps);
        private Node Compare() => Binary(Concat, CompareOps);
    }

    private static Ref Shift(Ref r, int dr, int dc) => r with { Row = r.RowAbs ? r.Row : r.Row + dr, Col = r.ColAbs ? r.Col : r.Col + dc };
    private static readonly HashSet<string> Comparisons = ["<", ">", "<=", ">=", "=", "<>"];

    public static Value Evaluate(Node node, string sheet, ICells cells, int dr = 0, int dc = 0)
    {
        Value Ev(Node x) => Evaluate(x, sheet, cells, dr, dc);
        Value Cell(Ref r) { var s = Shift(r, dr, dc); return cells.Value(r.Sheet ?? sheet, s.Col, s.Row); }
        List<Value> Range(Ref a, Ref b)
        {
            Ref x = Shift(a, dr, dc), y = Shift(b, dr, dc);
            var outv = new List<Value>();
            for (var row = Math.Min(x.Row, y.Row); row <= Math.Max(x.Row, y.Row); row++)
                for (var col = Math.Min(x.Col, y.Col); col <= Math.Max(x.Col, y.Col); col++) outv.Add(cells.Value(a.Sheet ?? sheet, col, row));
            return outv;
        }
        switch (node)
        {
            case NumNode n: return n.V;
            case StrNode s: return new StrValue(s.S);
            case RefNode r: return Cell(r.R);
            case RangeNode: throw new InvalidOperationException("A range outside a function");
            case NegNode ng: { var x = AsNum(Ev(ng.X)); return Num(-x.N, x.D); }
            case BinNode bin:
            {
                Value a = Ev(bin.A), b = Ev(bin.B);
                if (bin.Op == "&") return new StrValue(Text(a) + Text(b));
                if (Comparisons.Contains(bin.Op))
                {
                    var c = CompareValues(a, b);
                    var r = bin.Op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, ">=" => c >= 0, "=" => c == 0, _ => c != 0 };
                    return new BoolValue(r);
                }
                NumValue x = AsNum(a), y = AsNum(b);
                return bin.Op switch
                {
                    "+" => Num(x.N * y.D + y.N * x.D, x.D * y.D),
                    "-" => Num(x.N * y.D - y.N * x.D, x.D * y.D),
                    "*" => Num(x.N * y.N, x.D * y.D),
                    "/" => Num(x.N * y.D, x.D * y.N),
                    _ => throw new InvalidOperationException($"Operator {bin.Op} not supported"),
                };
            }
            case CallNode call:
            {
                if (call.Name == "SUM")
                {
                    BigInteger n = 0, d = 1;
                    foreach (var arg in call.Args)
                    {
                        var values = arg is RangeNode rg ? Range(rg.A, rg.B).Where(v => v is NumValue).ToList() : [Ev(arg)];
                        foreach (var v in values) { var x = AsNum(v); n = n * x.D + x.N * d; d *= x.D; var g = Gcd(n, d); n /= g; d /= g; }
                    }
                    return Num(n, d);
                }
                if (call.Name == "IF")
                {
                    if (call.Args.Count < 2 || call.Args.Count > 3) throw new InvalidOperationException("IF takes two or three arguments");
                    return Truthy(Ev(call.Args[0])) ? Ev(call.Args[1]) : call.Args.Count > 2 ? Ev(call.Args[2]) : new BoolValue(false);
                }
                throw new InvalidOperationException($"Function {call.Name} not supported");
            }
            default: throw new InvalidOperationException();
        }
    }

    private static string Text(Value v) => v switch { StrValue s => s.S, EmptyValue => "", BoolValue b => b.B ? "TRUE" : "FALSE", _ => DecimalText(v) };
    private static bool Truthy(Value v) => v is BoolValue b ? b.B : !AsNum(v).N.IsZero;
    private static int CompareValues(Value a, Value b)
    {
        if (a is StrValue || b is StrValue) return JsSemantics.CompareOrdinal(Text(a), Text(b));
        NumValue x = AsNum(a), y = AsNum(b);
        return (x.N * y.D).CompareTo(y.N * x.D) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    // ---- Excel's own arithmetic (INS-REGULATORY-RESERVES-XLSX-NATIVE-PRECISION-FIX-20261019): the same formulas over IEEE doubles, as
    // Excel keeps and adds numbers, so that a figure Excel cannot keep — 90071992547408.01 is 90071992547408.02 to it — is found before
    // the file is given out. A number is the nearest double to its text; SUM adds its range left to right, row by row.

    private static double XNumber(XValue? v) => v switch
    {
        null => 0,
        XNum n => n.V,
        XBool b => b.V ? 1 : 0,
        XStr s => JsSemantics.Trim(s.V).Length == 0 ? 0 : JsSemantics.ParseNumber(s.V),
        _ => throw new InvalidOperationException(),
    };
    /// <summary>String(v ?? '') of JavaScript.</summary>
    private static string XText(XValue? v) => v switch
    {
        null => "",
        XNum n => JsSemantics.NumberToString(n.V),
        XBool b => b.V ? "true" : "false",
        XStr s => s.V,
        _ => throw new InvalidOperationException(),
    };

    public static XValue? EvaluateDouble(Node node, string sheet, IXCells cells, int dr = 0, int dc = 0)
    {
        XValue? Ev(Node x) => EvaluateDouble(x, sheet, cells, dr, dc);
        XValue? At(Ref r) { var s = Shift(r, dr, dc); return cells.Value(r.Sheet ?? sheet, s.Col, s.Row); }
        switch (node)
        {
            case NumNode n: return new XNum(double.Parse(DecimalText(n.V), CultureInfo.InvariantCulture));
            case StrNode s: return new XStr(s.S);
            case RefNode r: return At(r.R);
            case RangeNode: throw new InvalidOperationException("A range outside a function");
            case NegNode ng: return new XNum(-XNumber(Ev(ng.X)));
            case BinNode bin:
            {
                XValue? a = Ev(bin.A), b = Ev(bin.B);
                if (bin.Op == "&") return new XStr(XText(a) + XText(b));
                if (Comparisons.Contains(bin.Op))
                {
                    if (a is XStr || b is XStr)
                    {
                        var c = JsSemantics.CompareOrdinal(XText(a), XText(b));
                        return new XBool(bin.Op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, ">=" => c >= 0, "=" => c == 0, _ => c != 0 });
                    }
                    double p = XNumber(a), q = XNumber(b);
                    return new XBool(bin.Op switch { "<" => p < q, ">" => p > q, "<=" => p <= q, ">=" => p >= q, "=" => p == q, _ => p != q });
                }
                double x = XNumber(a), y = XNumber(b);
                return new XNum(bin.Op switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, _ => double.NaN });
            }
            case CallNode call:
            {
                if (call.Name == "SUM")
                {
                    double sum = 0;
                    foreach (var arg in call.Args)
                    {
                        if (arg is not RangeNode rg) { sum += XNumber(Ev(arg)); continue; }
                        Ref x = Shift(rg.A, dr, dc), y = Shift(rg.B, dr, dc);
                        for (var row = Math.Min(x.Row, y.Row); row <= Math.Max(x.Row, y.Row); row++)
                            for (var col = Math.Min(x.Col, y.Col); col <= Math.Max(x.Col, y.Col); col++)
                                if (cells.Value(rg.A.Sheet ?? sheet, col, row) is XNum v) sum += v.V;
                    }
                    return new XNum(sum);
                }
                if (call.Name == "IF")
                {
                    if (call.Args.Count < 2) throw new InvalidOperationException("IF takes two or three arguments");
                    var c = Ev(call.Args[0]);
                    return (c is XBool cb ? cb.V : XNumber(c) != 0) ? Ev(call.Args[1]) : call.Args.Count > 2 ? Ev(call.Args[2]) : new XBool(false);
                }
                throw new InvalidOperationException($"Function {call.Name} not supported");
            }
            default: throw new InvalidOperationException();
        }
    }
}
