// Ported from src/lib/known.test.ts.
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class QueryParamsTests
{
    [Fact(DisplayName = "singleParams (Codex, PR #21): keeps single strings and drops repeated keys")]
    public void SingleParams()
    {
        var result = QueryParams.SingleParams(new Dictionary<string, IReadOnlyList<string>?> { ["approve"] = ["x", "y"], ["advance"] = ["allocated"], ["gel"] = null });
        Assert.Equal(new Dictionary<string, string?> { ["approve"] = null, ["advance"] = "allocated", ["gel"] = null }, result);
    }

    private static readonly Dictionary<string, string> Texts = new() { ["done"] = "დაიხურა", ["forbidden"] = "უფლება არ აქვს" };

    [Fact(DisplayName = "known (Codex H26): returns the text of an own key")]
    public void KnownOwnKey()
    {
        Assert.Equal("დაიხურა", QueryParams.Known(Texts, "done"));
    }

    [Fact(DisplayName = "known (Codex H26): ignores what every object inherits and unknown or missing values")]
    public void KnownIgnoresOthers()
    {
        foreach (var key in new[] { "__proto__", "constructor", "toString", "hasOwnProperty", "valueOf", "unknown", "", null }) Assert.Null(QueryParams.Known(Texts, key));
    }
}
