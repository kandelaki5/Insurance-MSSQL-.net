// Codex R04: the rules give the same answer whatever the machine's culture, time zone or case tables (docs/RUNTIME.md).
// The CI runs this project on Windows with ka-GE and Tbilisi time, and on Linux.
using System.Globalization;
using System.Text.Json;
using Insurance.Domain.Common;
using Insurance.Domain.Compliance;
using Insurance.Domain.Regulatory;

namespace Insurance.Domain.Tests.Common;

public class RuntimeIndependenceTests
{
    [Fact(DisplayName = "the upper-case table is the old program's reference runtime's, the one the parity cases were taken from (R04)")]
    public void UpperTableSource()
    {
        Assert.Equal("JavaScript String.prototype.toUpperCase, Node v24.21.0, Unicode 17.0, ICU 78.3", JsText.UpperSource);
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity", "common.json")));
        Assert.Equal("Node v24.21.0, Unicode 17.0", cases.RootElement.GetProperty("toUpperCaseRuntime").GetString());
        // Letters of Unicode 17 that .NET's own tables (and Node 22) leave alone.
        Assert.Equal("꟎", JsText.ToUpperCase("꟏"));
        Assert.Equal(char.ConvertFromUtf32(0x16EA0), JsText.ToUpperCase(char.ConvertFromUtf32(0x16EBB)));
    }

    [Fact(DisplayName = "Tbilisi time is UTC+04:00 on every system: dates, midnights and suspension deadlines (R04)")]
    public void TbilisiTimeIsFixed()
    {
        Assert.Equal(new DateOnly(2026, 10, 24), TbilisiTime.Date(new DateTimeOffset(2026, 10, 23, 20, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 23), TbilisiTime.Date(new DateTimeOffset(2026, 10, 23, 19, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 7, 1), TbilisiTime.Date(new DateTimeOffset(2026, 6, 30, 20, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 20, 0, 0, TimeSpan.Zero), TbilisiTime.Midnight(new DateOnly(2026, 3, 29)));
        // Friday 23.10.2026 10:00 Tbilisi, 72 working hours: the weekend skipped, ends Wednesday 28.10 10:00 Tbilisi.
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 6, 0, 0, TimeSpan.Zero), Suspension.SuspensionEnd(new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero)));
        // The case Codex found on Windows (18.03.2005, 48 hours): now four calendar days later, with 48 working hours in it.
        Assert.Equal(new DateTimeOffset(2005, 3, 22, 13, 10, 27, 339, TimeSpan.Zero), Suspension.SuspensionEnd(new DateTimeOffset(2005, 3, 18, 13, 10, 27, 339, TimeSpan.Zero), 48));
    }

    [Fact(DisplayName = "sorting, upper case and Tbilisi dates do not change with the thread's culture (R04)")]
    public void CultureDoesNotMatter()
    {
        string Run()
        {
            var names = new[] { "ბ", "Munich Re", "ა", "apple", "Zeta", "10", "2", "Ärzte", "ı", "i" };
            var sorted = names.OrderBy(n => n, Comparer<string>.Create(JsSemantics.LocaleCompare)).ToList();
            return string.Join("|", sorted) + "#" + JsText.ToUpperCase("istanbul ıi ß") + "#" + TbilisiTime.Date(new DateTimeOffset(2026, 10, 23, 21, 0, 0, TimeSpan.Zero)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var invariant = Run();
            // Latin before Georgian and digits first, as the old program's server (en-US) sorted them.
            Assert.Equal("10|2|apple|Ärzte|i|ı|Munich Re|Zeta|ა|ბ#ISTANBUL II SS#2026-10-24", invariant);
            foreach (var name in new[] { "ka-GE", "tr-TR", "en-US", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
                Assert.Equal(invariant, Run());
            }
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui); }
    }
}
