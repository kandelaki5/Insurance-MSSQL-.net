// Phone numbers of clients: stored as "+<country code> <digits>" (E.164 digits, with one space
// after the code so the two parts can be shown and edited separately). The country code is
// +995 unless another is entered; Georgian numbers have 9 digits after it.
// Ported from src/lib/phone.ts.
using System.Text.RegularExpressions;

namespace Insurance.Domain.Common;

public sealed record PhoneParts(string Code, string Number);

public static partial class Phone
{
    public const string DefaultCountryCode = "+995";

    private static string DigitsOf(string value) => new(value.Where(c => c is >= '0' and <= '9').ToArray());

    /// <summary>
    /// Normalises a country code and a number as typed (spaces, dashes, brackets allowed; a number
    /// pasted with its own +995 / 995 in front is accepted). Returns null when the result is not a
    /// possible number: the code has 1–3 digits, the number 4–14, together at most 15 (E.164), and a
    /// Georgian number exactly 9.
    ///
    /// With the code left empty, a number written in full international form ("+49301234567") is
    /// kept as it is, without a space: where its code ends cannot be known from the digits alone, so
    /// it is not guessed (review H15 — numbers saved before this format must save again unchanged).
    /// </summary>
    public static string? NormalizePhone(string code, string number)
    {
        var codeDigits = DigitsOf(code);
        var numberDigits = DigitsOf(number);
        var international = JsText.Trim(number).StartsWith('+');
        if (codeDigits == "" && international)
        {
            // +995 is Georgia's alone (country codes are prefix-free), so a number starting with it
            // obeys the 9-digit rule whether or not the code field was filled (review H16).
            if (numberDigits.StartsWith("995", StringComparison.Ordinal)) return numberDigits.Length == 12 ? $"+995 {numberDigits[3..]}" : null;
            return numberDigits.Length is >= 8 and <= 15 ? $"+{numberDigits}" : null;
        }
        if (codeDigits == "") codeDigits = DigitsOf(DefaultCountryCode);
        if (international)
        {
            if (!numberDigits.StartsWith(codeDigits, StringComparison.Ordinal)) return null;
            numberDigits = numberDigits[codeDigits.Length..];
        }
        else if (codeDigits == "995" && numberDigits.Length == 12 && numberDigits.StartsWith("995", StringComparison.Ordinal))
        {
            numberDigits = numberDigits[3..];
        }
        if (codeDigits.Length is < 1 or > 3 || numberDigits.Length < 4 || codeDigits.Length + numberDigits.Length > 15) return null;
        if (codeDigits == "995" && numberDigits.Length != 9) return null;
        return $"+{codeDigits} {numberDigits}";
    }

    [GeneratedRegex(@"^\+([0-9]{1,3}) ([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Spaced();

    // JavaScript's \s and its `.` (anything but a line terminator).
    [GeneratedRegex(@"^\+([0-9]{1,3})[" + JsText.WhiteSpaceClass + @"-]+([^\n\r\u2028\u2029]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Grouped();

    /// <summary>
    /// The two parts of a stored number, also for numbers saved before this format: "595534555",
    /// "+995 555 00 00 00", "+49 30 1234567" (the first group after + is the code). A number in
    /// full international form without any space ("+49301234567") comes back whole, with an empty
    /// code, because its code cannot be told apart from the rest (review H15).
    /// </summary>
    public static PhoneParts SplitPhone(string? stored)
    {
        var value = JsText.Trim(stored ?? "");
        if (value == "") return new PhoneParts(DefaultCountryCode, "");
        var spaced = Spaced().Match(value);
        if (spaced.Success) return new PhoneParts($"+{spaced.Groups[1].Value}", spaced.Groups[2].Value);
        var digits = DigitsOf(value);
        if (digits.Length == 9 && !value.StartsWith('+')) return new PhoneParts(DefaultCountryCode, digits);
        if (digits.StartsWith("995", StringComparison.Ordinal) && digits.Length == 12) return new PhoneParts(DefaultCountryCode, digits[3..]);
        if (value.StartsWith(DefaultCountryCode, StringComparison.Ordinal)) return new PhoneParts(DefaultCountryCode, JsText.Trim(value[DefaultCountryCode.Length..]));
        var grouped = Grouped().Match(value);
        if (grouped.Success) return new PhoneParts($"+{grouped.Groups[1].Value}", DigitsOf(grouped.Groups[2].Value));
        if (value.StartsWith('+')) return new PhoneParts("", value);
        return new PhoneParts(DefaultCountryCode, value);
    }

    /// <summary>How a number is shown: "+995 595 53 45 55" for Georgian numbers, "+&lt;code&gt; &lt;digits&gt;" otherwise.</summary>
    public static string FormatPhone(string? stored)
    {
        if (JsText.Trim(stored ?? "") == "") return "";
        var (code, number) = SplitPhone(stored);
        if (code == "+995" && number.Length == 9 && DigitsOf(number) == number) return $"+995 {number[..3]} {number[3..5]} {number[5..7]} {number[7..]}";
        return code != "" ? $"{code} {number}" : number;
    }
}
