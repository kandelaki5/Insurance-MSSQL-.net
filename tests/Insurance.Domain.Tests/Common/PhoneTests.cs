// Ported from src/lib/phone.test.ts ("client phone numbers").
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class PhoneTests
{
    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    // What the client card and the settings page do: split the stored value into the two
    // fields, then save the two fields as they are.
    private static string? Resave(string stored)
    {
        var (code, number) = Phone.SplitPhone(stored);
        return Phone.NormalizePhone(code, number);
    }

    [Fact(DisplayName = "a Georgian number gets +995 and must have 9 digits")]
    public void GeorgianNumber()
    {
        Assert.Equal("+995 595534555", Phone.NormalizePhone("+995", "595 53-45-55"));
        Assert.Equal("+995 595534555", Phone.NormalizePhone("", "(595) 534555"));
        Assert.Equal("+995 595534555", Phone.NormalizePhone("+995", "+995 595 534 555"));
        Assert.Equal("+995 595534555", Phone.NormalizePhone("+995", "995595534555"));
        Assert.Null(Phone.NormalizePhone("+995", "59553455"));
        Assert.Null(Phone.NormalizePhone("+995", "5955345551"));
    }

    [Fact(DisplayName = "another country code is kept as entered")]
    public void OtherCountryCode()
    {
        Assert.Equal("+49 301234567", Phone.NormalizePhone("+49", "30 1234567"));
        Assert.Equal("+1 2025550143", Phone.NormalizePhone("1", "202-555-0143"));
        Assert.Null(Phone.NormalizePhone("+12345", "123456"));
        Assert.Null(Phone.NormalizePhone("+49", "123"));
        Assert.Null(Phone.NormalizePhone("+49", "1234567890123456"));
    }

    [Fact(DisplayName = "numbers saved before the format are read and shown correctly")]
    public void OldNumbers()
    {
        Assert.Equal(new PhoneParts("+995", "595534555"), Phone.SplitPhone("595534555"));
        Assert.Equal(new PhoneParts("+995", "555000000"), Phone.SplitPhone("+995 555 00 00 00"));
        Assert.Equal(new PhoneParts("+49", "301234567"), Phone.SplitPhone("+49 301234567"));
        Assert.Equal(new PhoneParts("+995", ""), Phone.SplitPhone(null));
        // A placeholder typed before the format keeps its code; the admin then enters the number.
        Assert.Equal(new PhoneParts("+995", "..."), Phone.SplitPhone("+995 ..."));
        Assert.Equal("+995 595 53 45 55", Phone.FormatPhone("595534555"));
        Assert.Equal("+995 555 00 00 00", Phone.FormatPhone("+995 555 00 00 00"));
        Assert.Equal("+49 301234567", Phone.FormatPhone("+49 301234567"));
        Assert.Equal("", Phone.FormatPhone(""));
    }

    [Fact(DisplayName = "a number saved earlier saves again unchanged when only another field is edited (review H15)")]
    public void ResavesUnchanged()
    {
        foreach (var stored in new[] { "+49301234567", "+49 30 1234567", "+49 301234567", "+995 595534555", "+995 555 00 00 00", "+995595534555", "+1 202-555-0143" })
        {
            var saved = Resave(stored);
            Assert.True(saved is not null, $"{stored} must save again");
            Assert.Equal(Digits(stored), Digits(saved!));
            Assert.Equal(saved, Resave(saved!));
        }
        Assert.Equal("+49301234567", Resave("+49301234567"));
        Assert.Equal("+49 301234567", Resave("+49 30 1234567"));
        Assert.Equal("+995 595534555", Resave("595534555"));
        Assert.Equal(new PhoneParts("", "+49301234567"), Phone.SplitPhone("+49301234567"));
        Assert.Equal("+49301234567", Phone.FormatPhone("+49301234567"));
        // A newly typed wrong number is still refused.
        Assert.Null(Phone.NormalizePhone("", "+4912"));
        Assert.Null(Phone.NormalizePhone("+995", "+49301234567"));
    }

    [Fact(DisplayName = "a +995 number has 9 digits whether or not the code field is filled (review H16)")]
    public void GeorgianNineDigitsAlways()
    {
        // The client card (saveParty) and the settings page (saveSettings) pass the two fields
        // straight to normalizePhone and save nothing when it returns null.
        var local = new Dictionary<int, string> { [8] = "59553455", [9] = "595534555", [10] = "5955345551" };
        foreach (var code in new[] { "", "+995" })
        {
            foreach (var (length, digits) in local)
            {
                foreach (var number in new[] { $"+995{digits}", $"+995 {digits}", digits })
                {
                    var saved = Phone.NormalizePhone(code, number);
                    if (length == 9)
                    {
                        Assert.Equal("+995 595534555", saved);
                        var again = Phone.SplitPhone(saved);
                        Assert.Equal(saved, Phone.NormalizePhone(again.Code, again.Number));
                    }
                    else
                    {
                        Assert.Null(saved);
                    }
                }
            }
        }
        // Other countries keep their own length rules (H15 unchanged).
        Assert.Equal("+49301234567", Phone.NormalizePhone("", "+49301234567"));
        Assert.Equal("+12025550143", Phone.NormalizePhone("", "+1 202-555-0143"));
    }
}
