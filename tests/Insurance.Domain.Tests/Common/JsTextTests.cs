using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class JsTextTests
{
    [Fact(DisplayName = "trim removes JavaScript's white space, not .NET's")]
    public void Trim()
    {
        Assert.Equal("a", JsText.Trim("\ufeff\u00a0 a\u3000\u2028"));
        Assert.Equal("\u0085a\u0085", JsText.Trim(" \u0085a\u0085 "));
        Assert.Equal("\u200ba", JsText.Trim("\u200ba"));
    }

    [Fact(DisplayName = "toUpperCase maps full Unicode case and keeps lone surrogates")]
    public void ToUpperCase()
    {
        Assert.Equal("STRASSE", JsText.ToUpperCase("straße"));
        Assert.Equal("FI", JsText.ToUpperCase("ﬁ"));
        Assert.Equal("IR", JsText.ToUpperCase("ır"));
        Assert.Equal("Ა", JsText.ToUpperCase("ა"));
        Assert.Equal("A\ud800B", JsText.ToUpperCase("a\ud800b"));
        Assert.Equal("😀", JsText.ToUpperCase("😀"));
    }

    [Fact(DisplayName = "Number reads text as JavaScript does")]
    public void Number()
    {
        Assert.Equal(16, JsNumber.Parse(" 0x10 "));
        Assert.Equal(0, JsNumber.Parse(" "));
        Assert.True(double.IsNaN(JsNumber.Parse("1_0")));
        Assert.True(double.IsNaN(JsNumber.Parse("-0x10")));
        Assert.Equal(0.5, JsNumber.Parse(".5"));
        Assert.Equal(1.5m, JsNumber.ParseFiniteDecimal("1.5"));
        Assert.Null(JsNumber.ParseFiniteDecimal("Infinity"));
        Assert.Equal(decimal.MaxValue, JsNumber.ParseFiniteDecimal("1e300"));
    }

    private enum Sample { NotInRole, HighRiskCountry }

    [Fact(DisplayName = "enum codes are the old snake_case and kebab-case texts")]
    public void EnumCodes()
    {
        Assert.Equal("not-in-role", Sample.NotInRole.ToKebabCode());
        Assert.Equal("high_risk_country", Sample.HighRiskCountry.ToSnakeCode());
        Assert.True(EnumCode.TryParseSnakeCode<Sample>("high_risk_country", out var parsed) && parsed == Sample.HighRiskCountry);
        Assert.False(EnumCode.TryParseSnakeCode<Sample>("HighRiskCountry", out _));
        Assert.True(EnumCode.TryParseKebabCode<Sample>("not-in-role", out var kebab) && kebab == Sample.NotInRole);
    }
}
