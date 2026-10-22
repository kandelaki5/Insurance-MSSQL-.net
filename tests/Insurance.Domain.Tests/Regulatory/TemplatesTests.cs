using Insurance.Domain.Regulatory;

namespace Insurance.Domain.Tests.Regulatory;

// The three official templates embedded as the owner gave them (templates.ts and templates/SHA256SUMS): each file's SHA-256 is the one
// its form checks before filling it.
public class TemplatesTests
{
    [Fact(DisplayName = "templates: the embedded files are the confirmed ones — SHA256SUMS and every form's own hash")]
    public void EmbeddedTemplatesAreTheConfirmedOnes()
    {
        var sums = Templates.Sha256Sums().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ")).ToDictionary(p => p[1].Trim(), p => p[0]);
        Assert.Equal(3, sums.Count);
        Assert.Equal(sums[ReservesPlacement.ReservesTemplateFile], Templates.Sha256Hex(Templates.ReservesTemplate()));
        Assert.Equal(sums[FinancialAnnex.N1TemplateFile], Templates.Sha256Hex(Templates.FinancialAnnexTemplate()));
        Assert.Equal(sums[Form6.OtherQuarterlyTemplateFile], Templates.Sha256Hex(Templates.OtherQuarterlyTemplate()));
        Assert.Equal(ReservesPlacement.ReservesTemplateSha256, sums[ReservesPlacement.ReservesTemplateFile]);
        Assert.Equal(FinancialAnnex.N1TemplateSha256, sums[FinancialAnnex.N1TemplateFile]);
        Assert.Equal(Form6.OtherQuarterlyTemplateSha256, sums[Form6.OtherQuarterlyTemplateFile]);
    }
}
