using System.Net;
using System.Text.RegularExpressions;

namespace Insurance.Web.Tests;

/// <summary>Reading pages in tests: the HTML, its visible text, and the location of a redirect.</summary>
public static partial class Html
{
    public static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public static string Location(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The visible text, tags removed and white space collapsed.</summary>
    public static string Text(string html) => WebUtility.HtmlDecode(Spaces().Replace(Tags().Replace(html, " "), " ")).Trim();

    /// <summary>The part of the page between a marker and the next one (or the end).</summary>
    public static string Between(string html, string from, string? to = null)
    {
        var start = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{from}' on the page");
        var end = to is null ? -1 : html.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }
}
