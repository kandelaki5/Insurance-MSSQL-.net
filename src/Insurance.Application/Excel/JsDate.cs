using System.Globalization;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Excel;

/// <summary>
/// A JavaScript Date as the old program had them while reading spreadsheets: a time value in milliseconds (NaN for an invalid date),
/// in a UTC process (the old server's zone — local time and UTC are the same there). Parse is V8's own date parser (new Date(text)):
/// the ES5 ISO form first, else V8's legacy rules ("1/15/2026", "Jan 15 2026", "2026/01/15", "5/6" = 6 May 2001 …), which SheetJS used
/// to decide whether a CSV field is a date.
/// </summary>
public readonly record struct JsDate(double Ms)
{
    private const double MsPerDay = 86_400_000;
    public bool IsValid => !double.IsNaN(Ms);

    /// <summary>TimeClip: beyond ±8.64e15 ms invalid, else the integer part (toward zero).</summary>
    public static double TimeClip(double t) => !double.IsFinite(t) || Math.Abs(t) > 8.64e15 ? double.NaN : Math.Truncate(t) + 0.0;

    public static double Day(double t) => Math.Floor(t / MsPerDay);
    public static double MakeTime(double h, double m, double s, double ms) => h * 3_600_000 + m * 60_000 + s * 1000 + ms;
    /// <summary>MakeDay(year, month 0-based, date): the day number, months beyond 11 carried into the year, days beyond the month's into the next.</summary>
    public static double MakeDay(double year, double month, double date)
    {
        if (!double.IsFinite(year) || !double.IsFinite(month) || !double.IsFinite(date)) return double.NaN;
        var ym = year + Math.Floor(month / 12);
        var mn = ((month % 12) + 12) % 12;
        if (Math.Abs(ym) > 400_000) return double.NaN;
        var days = DaysFromCivil((long)ym, (int)mn + 1, 1);
        return days + date - 1;
    }
    public static double MakeDate(double day, double time) => day * MsPerDay + time;

    /// <summary>Days from 1970-01-01 to a proleptic Gregorian date.</summary>
    private static long DaysFromCivil(long y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }
    private static (long Y, int M, int D) CivilFromDays(long z)
    {
        z += 719468;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var doe = z - era * 146097;
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var y = yoe + era * 400;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var d = (int)(doy - (153 * mp + 2) / 5 + 1);
        var m = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (y + (m <= 2 ? 1 : 0), m, d);
    }

    public long Year => CivilFromDays((long)Day(Ms)).Y;
    /// <summary>getMonth(): 0-based.</summary>
    public int Month => CivilFromDays((long)Day(Ms)).M - 1;
    /// <summary>getDate(): the day of the month.</summary>
    public int Date => CivilFromDays((long)Day(Ms)).D;

    /// <summary>Date.prototype.toISOString(): "2026-01-15T00:00:00.000Z" (years beyond 0–9999 as ±YYYYYY); an invalid date throws, as in JavaScript.</summary>
    public string ToIsoString()
    {
        if (!IsValid) throw new FormatException("Invalid time value");
        var (y, m, d) = CivilFromDays((long)Day(Ms));
        var inDay = Ms - Day(Ms) * MsPerDay;
        var h = (int)(inDay / 3_600_000); var mi = (int)(inDay / 60_000 % 60); var s = (int)(inDay / 1000 % 60); var ms = (int)(inDay % 1000);
        var year = y is >= 0 and <= 9999 ? y.ToString("0000", CultureInfo.InvariantCulture) : (y < 0 ? "-" : "+") + Math.Abs(y).ToString("000000", CultureInfo.InvariantCulture);
        return $"{year}-{m:00}-{d:00}T{h:00}:{mi:00}:{s:00}.{ms:000}Z";
    }

    public static JsDate FromUtc(DateTime t) => new(TimeClip(XlsxWriter.UnixMs(t)));

    // ---- V8's date parser (src/date/dateparser*.h), as far as a date in a text decides it.

    private const int None = int.MaxValue;
    private enum Tok { Invalid, Unknown, Number, Symbol, WhiteSpace, Keyword, End }
    private enum Kw { Invalid, MonthName, AmPm, TimeZoneName, TimeSeparator }
    private readonly record struct Token(Tok Tag, int Length, int Value = 0, char Symbol = '\0', Kw Kind = Kw.Invalid)
    {
        public bool IsNumber => Tag == Tok.Number;
        public bool IsSymbol(char c) => Tag == Tok.Symbol && Symbol == c;
        public bool IsAsciiSign => Tag == Tok.Symbol && Symbol is '+' or '-';
        public int AsciiSign => 44 - Symbol;
        public bool IsWhiteSpace => Tag == Tok.WhiteSpace;
        public bool IsEnd => Tag == Tok.End;
        public bool IsFixedLengthNumber(int n) => IsNumber && Length == n;
        public bool IsKeywordZ => Tag == Tok.Keyword && Kind == Kw.TimeZoneName && Length == 1 && Value == 0;
    }

    private static readonly (string Prefix, Kw Kind, int Value)[] Keywords =
    [
        ("jan", Kw.MonthName, 1), ("feb", Kw.MonthName, 2), ("mar", Kw.MonthName, 3), ("apr", Kw.MonthName, 4), ("may", Kw.MonthName, 5), ("jun", Kw.MonthName, 6),
        ("jul", Kw.MonthName, 7), ("aug", Kw.MonthName, 8), ("sep", Kw.MonthName, 9), ("oct", Kw.MonthName, 10), ("nov", Kw.MonthName, 11), ("dec", Kw.MonthName, 12),
        ("am\0", Kw.AmPm, 0), ("pm\0", Kw.AmPm, 12), ("ut\0", Kw.TimeZoneName, 0), ("utc", Kw.TimeZoneName, 0), ("z\0\0", Kw.TimeZoneName, 0), ("gmt", Kw.TimeZoneName, 0),
        ("cdt", Kw.TimeZoneName, -5), ("cst", Kw.TimeZoneName, -6), ("edt", Kw.TimeZoneName, -4), ("est", Kw.TimeZoneName, -5), ("mdt", Kw.TimeZoneName, -6),
        ("mst", Kw.TimeZoneName, -7), ("pdt", Kw.TimeZoneName, -7), ("pst", Kw.TimeZoneName, -8), ("t\0\0", Kw.TimeSeparator, 0),
    ];

    private sealed class Tokenizer(string s)
    {
        private int i;
        private Token next = default;
        private bool peeked;
        private bool IsAsciiAlphaOrAbove(int at) => at < s.Length && s[at] >= 'A';
        private bool IsWs(int at) => at < s.Length && JsSemantics.IsWhiteSpace(s[at]);

        private Token Scan()
        {
            if (i >= s.Length) return new Token(Tok.End, 0);
            var start = i;
            var c = s[i];
            if (c is >= '0' and <= '9')
            {
                var n = 0; var digits = 0;
                while (i < s.Length && s[i] == '0') i++;
                while (i < s.Length && s[i] is >= '0' and <= '9') { if (digits < 9) n = n * 10 + (s[i] - '0'); digits++; i++; }
                return new Token(Tok.Number, i - start, n);
            }
            if (c is ':' or '-' or '+' or '.' or ')') { i++; return new Token(Tok.Symbol, 1, Symbol: c); }
            if (IsAsciiAlphaOrAbove(i) && !IsWs(i))
            {
                var prefix = new char[3];
                var len = 0;
                for (; IsAsciiAlphaOrAbove(i) && !IsWs(i); i++, len++) if (len < 3) prefix[len] = (char)(s[i] | 0x20);
                for (var k = len; k < 3; k++) prefix[k] = '\0';
                var p = new string(prefix);
                foreach (var (pre, kind, value) in Keywords)
                    if (pre == p && (len <= 3 || kind == Kw.MonthName)) return new Token(Tok.Keyword, len, value, Kind: kind);
                return new Token(Tok.Keyword, len, 0, Kind: Kw.Invalid);
            }
            if (IsWs(i)) { while (IsWs(i)) i++; return new Token(Tok.WhiteSpace, i - start); }
            if (c == '(')
            {
                var balance = 0;
                do
                {
                    if (s[i] == ')') --balance; else if (s[i] == '(') ++balance;
                    i++;
                } while (balance > 0 && i < s.Length && s[i] != '\0');
                return new Token(Tok.Unknown, i - start);
            }
            i++;
            return new Token(Tok.Unknown, 1);
        }
        public Token Next() { if (peeked) { peeked = false; return next; } return Scan(); }
        public Token Peek() { if (!peeked) { next = Scan(); peeked = true; } return next; }
        public bool SkipSymbol(char c) { if (Peek().IsSymbol(c)) { Next(); return true; } return false; }
    }

    private static bool Between(int x, int lo, int hi) => x >= lo && x <= hi;
    private static bool IsMonth(int x) => Between(x, 1, 12);
    private static bool IsDay(int x) => Between(x, 1, 31);
    private static bool IsHour(int x) => Between(x, 0, 23);
    private static bool IsMinute(int x) => Between(x, 0, 59);
    private static bool IsSecond(int x) => Between(x, 0, 59);
    private static bool IsHour12(int x) => Between(x, 0, 12);
    private static bool IsMillisecond(int x) => Between(x, 0, 999);

    private sealed class DayComposer
    {
        private readonly int[] comp = new int[3];
        private int index;
        private int namedMonth = None;
        private bool isoDate;
        public bool IsEmpty => index == 0;
        public bool Add(int n) { if (index == 3) return false; comp[index++] = n; return true; }
        public void SetNamedMonth(int n) => namedMonth = n;
        public void SetIsoDate() => isoDate = true;
        public bool Write(out int year, out int month, out int day)
        {
            year = 0; month = None; day = None;
            if (index < 1) return false;
            while (index < 3) comp[index++] = 1;
            if (namedMonth == None)
            {
                if (isoDate || (index == 3 && !IsDay(comp[0]))) { year = comp[0]; month = comp[1]; day = comp[2]; }
                else { month = comp[0]; day = comp[1]; if (index == 3) year = comp[2]; }
            }
            else
            {
                month = namedMonth;
                if (index == 1) day = comp[0];
                else if (!IsDay(comp[0])) { year = comp[0]; day = comp[1]; }
                else { day = comp[0]; year = comp[1]; }
            }
            if (!isoDate)
            {
                if (Between(year, 0, 49)) year += 2000;
                else if (Between(year, 50, 99)) year += 1900;
            }
            return IsMonth(month) && IsDay(day);
        }
    }

    private sealed class TimeComposer
    {
        private readonly int[] comp = new int[4];
        private int index;
        private int hourOffset = None;
        public bool IsEmpty => index == 0;
        public bool IsExpecting(int n) => (index == 1 && IsMinute(n)) || (index == 2 && IsSecond(n)) || (index == 3 && IsMillisecond(n));
        public bool Add(int n) { if (index >= 4) return false; comp[index++] = n; return true; }
        public bool AddFinal(int n) { if (!Add(n)) return false; while (index < 4) comp[index++] = 0; return true; }
        public void SetHourOffset(int n) => hourOffset = n;
        public bool Write(out int h, out int m, out int s, out int ms)
        {
            while (index < 4) comp[index++] = 0;
            (h, m, s, ms) = (comp[0], comp[1], comp[2], comp[3]);
            if (hourOffset != None)
            {
                if (!IsHour12(h)) return false;
                h %= 12;
                h += hourOffset;
            }
            if (!IsHour(h) || !IsMinute(m) || !IsSecond(s) || !IsMillisecond(ms))
                if (h != 24 || m != 0 || s != 0 || ms != 0) return false;
            return true;
        }
    }

    private sealed class TimeZoneComposer
    {
        private int sign = None, hour = None, minute = None;
        public void Set(int offsetHours) { sign = offsetHours < 0 ? -1 : 1; hour = Math.Abs(offsetHours); minute = 0; }
        public void SetSign(int s) => sign = s < 0 ? -1 : 1;
        public void SetAbsoluteHour(int h) => hour = h;
        public void SetAbsoluteMinute(int m) => minute = m;
        public bool IsExpecting(int n) => hour != None && minute == None && IsMinute(n);
        public bool IsUtc => hour == 0 && minute == 0;
        public bool IsEmpty => sign == None && hour == None && minute == None;
        public bool Write(out double? offsetSeconds)
        {
            offsetSeconds = null;
            if (sign == None) return true;
            if (hour == None) hour = 0;
            if (minute == None) minute = 0;
            var total = (double)sign * (hour * 3600.0 + minute * 60.0);
            if (Math.Abs(total) > int.MaxValue / 2) return false;
            offsetSeconds = total;
            return true;
        }
    }

    private static int ReadMilliseconds(Token number)
    {
        var n = number.Value;
        var length = number.Length;
        if (length < 3) { if (length == 1) n *= 100; else if (length == 2) n *= 10; }
        else if (length > 3)
        {
            if (length > 9) length = 9;
            var factor = 1;
            do { factor *= 10; length--; } while (length > 3);
            n /= factor;
        }
        return n;
    }

    /// <summary>new Date(text) in a UTC process.</summary>
    public static JsDate Parse(string text)
    {
        var scanner = new Tokenizer(text);
        var day = new DayComposer();
        var time = new TimeComposer();
        var tz = new TimeZoneComposer();
        var nextToken = ParseEs5(scanner, day, time, tz);
        if (nextToken.Tag == Tok.Invalid) return new JsDate(double.NaN);
        var hasReadNumber = !day.IsEmpty;
        for (var token = nextToken; !token.IsEnd; token = scanner.Next())
        {
            if (token.IsNumber)
            {
                hasReadNumber = true;
                var n = token.Value;
                if (scanner.SkipSymbol(':'))
                {
                    if (scanner.SkipSymbol(':'))
                    {
                        if (!time.IsEmpty) return new JsDate(double.NaN);
                        time.Add(n);
                        time.Add(0);
                    }
                    else
                    {
                        if (!time.Add(n)) return new JsDate(double.NaN);
                        if (scanner.Peek().IsSymbol('.')) scanner.Next();
                    }
                }
                else if (scanner.SkipSymbol('.') && time.IsExpecting(n))
                {
                    time.Add(n);
                    if (!scanner.Peek().IsNumber) return new JsDate(double.NaN);
                    var ms = ReadMilliseconds(scanner.Next());
                    if (ms < 0) return new JsDate(double.NaN);
                    time.AddFinal(ms);
                }
                else if (tz.IsExpecting(n)) tz.SetAbsoluteMinute(n);
                else if (time.IsExpecting(n))
                {
                    time.AddFinal(n);
                    var peek = scanner.Peek();
                    if (!peek.IsEnd && !peek.IsWhiteSpace && !peek.IsKeywordZ && !peek.IsAsciiSign) return new JsDate(double.NaN);
                }
                else
                {
                    if (!day.Add(n)) return new JsDate(double.NaN);
                    scanner.SkipSymbol('-');
                }
            }
            else if (token.Tag == Tok.Keyword)
            {
                if (token.Kind == Kw.AmPm && !time.IsEmpty) time.SetHourOffset(token.Value);
                else if (token.Kind == Kw.MonthName) { day.SetNamedMonth(token.Value); scanner.SkipSymbol('-'); }
                else if (token.Kind == Kw.TimeZoneName && hasReadNumber) tz.Set(token.Value);
                else
                {
                    // Garbage words are illegal if a number has been read; the first number must be set apart from them.
                    if (hasReadNumber) return new JsDate(double.NaN);
                    if (scanner.Peek().IsNumber) return new JsDate(double.NaN);
                }
            }
            else if (token.IsAsciiSign && (tz.IsUtc || !time.IsEmpty))
            {
                tz.SetSign(token.AsciiSign);
                int n = 0, length = 0;
                if (scanner.Peek().IsNumber) { var t = scanner.Next(); length = t.Length; n = t.Value; }
                hasReadNumber = true;
                if (scanner.Peek().IsSymbol(':')) { tz.SetAbsoluteHour(n); tz.SetAbsoluteMinute(None); }
                else if (length is 1 or 2) { tz.SetAbsoluteHour(n); tz.SetAbsoluteMinute(0); }
                else if (length is 3 or 4) { tz.SetAbsoluteHour(n / 100); tz.SetAbsoluteMinute(n % 100); }
                else return new JsDate(double.NaN);
            }
            else if ((token.IsAsciiSign || token.IsSymbol(')')) && hasReadNumber) return new JsDate(double.NaN);
        }
        if (!day.Write(out var year, out var month, out var date) || !time.Write(out var h, out var mi, out var s, out var msec) || !tz.Write(out var offset))
            return new JsDate(double.NaN);
        var t0 = MakeDate(MakeDay(year, month - 1, date), MakeTime(h, mi, s, msec));
        if (offset is { } off) t0 -= off * 1000;
        else if (Math.Abs(t0) > 8.64e15 + 864e6) return new JsDate(double.NaN);
        return new JsDate(TimeClip(t0));
    }

    private static Token ParseEs5(Tokenizer scanner, DayComposer day, TimeComposer time, TimeZoneComposer tz)
    {
        if (scanner.Peek().IsAsciiSign)
        {
            var signToken = scanner.Next();
            if (!scanner.Peek().IsFixedLengthNumber(6)) return signToken;
            var sign = signToken.AsciiSign;
            var year = scanner.Next().Value;
            if (sign < 0 && year == 0) return signToken;
            day.Add(sign * year);
        }
        else if (scanner.Peek().IsFixedLengthNumber(4)) day.Add(scanner.Next().Value);
        else return scanner.Next();
        if (scanner.SkipSymbol('-'))
        {
            if (!scanner.Peek().IsFixedLengthNumber(2) || !IsMonth(scanner.Peek().Value)) return scanner.Next();
            day.Add(scanner.Next().Value);
            if (scanner.SkipSymbol('-'))
            {
                if (!scanner.Peek().IsFixedLengthNumber(2) || !IsDay(scanner.Peek().Value)) return scanner.Next();
                day.Add(scanner.Next().Value);
            }
        }
        if (!(scanner.Peek().Tag == Tok.Keyword && scanner.Peek().Kind == Kw.TimeSeparator))
        {
            if (!scanner.Peek().IsEnd) return scanner.Next();
        }
        else
        {
            scanner.Next();
            if (!scanner.Peek().IsFixedLengthNumber(2) || !Between(scanner.Peek().Value, 0, 24)) return new Token(Tok.Invalid, 0);
            var hourIs24 = scanner.Peek().Value == 24;
            time.Add(scanner.Next().Value);
            if (!scanner.SkipSymbol(':')) return new Token(Tok.Invalid, 0);
            if (!scanner.Peek().IsFixedLengthNumber(2) || !IsMinute(scanner.Peek().Value) || (hourIs24 && scanner.Peek().Value > 0)) return new Token(Tok.Invalid, 0);
            time.Add(scanner.Next().Value);
            if (scanner.SkipSymbol(':'))
            {
                if (!scanner.Peek().IsFixedLengthNumber(2) || !IsSecond(scanner.Peek().Value) || (hourIs24 && scanner.Peek().Value > 0)) return new Token(Tok.Invalid, 0);
                time.Add(scanner.Next().Value);
                if (scanner.SkipSymbol('.'))
                {
                    if (!scanner.Peek().IsNumber || (hourIs24 && scanner.Peek().Value > 0)) return new Token(Tok.Invalid, 0);
                    time.Add(ReadMilliseconds(scanner.Next()));
                }
            }
            if (scanner.Peek().IsKeywordZ) { scanner.Next(); tz.Set(0); }
            else if (scanner.Peek().IsSymbol('+') || scanner.Peek().IsSymbol('-'))
            {
                tz.SetSign(scanner.Next().Symbol == '+' ? 1 : -1);
                if (scanner.Peek().IsFixedLengthNumber(4))
                {
                    var hourmin = scanner.Next().Value;
                    int hour = hourmin / 100, min = hourmin % 100;
                    if (!IsHour(hour) || !IsMinute(min)) return new Token(Tok.Invalid, 0);
                    tz.SetAbsoluteHour(hour);
                    tz.SetAbsoluteMinute(min);
                }
                else
                {
                    if (!scanner.Peek().IsFixedLengthNumber(2) || !IsHour(scanner.Peek().Value)) return new Token(Tok.Invalid, 0);
                    tz.SetAbsoluteHour(scanner.Next().Value);
                    if (!scanner.SkipSymbol(':')) return new Token(Tok.Invalid, 0);
                    if (!scanner.Peek().IsFixedLengthNumber(2) || !IsMinute(scanner.Peek().Value)) return new Token(Tok.Invalid, 0);
                    tz.SetAbsoluteMinute(scanner.Next().Value);
                }
            }
            if (!scanner.Peek().IsEnd) return new Token(Tok.Invalid, 0);
        }
        // A date-only form is UTC, a date-time without an offset local time — the same in a UTC process.
        if (tz.IsEmpty && time.IsEmpty) tz.Set(0);
        day.SetIsoDate();
        return new Token(Tok.End, 0);
    }
}
