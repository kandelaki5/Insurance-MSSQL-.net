using System.Net;
using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// Legacy scripts/address-guard-check.ts (Codex H26, H28, H30) over HTTP against the real application on a test database:
/// the answer to "approve" is never taken from the address bar — whatever is typed into the address of the pending list or a
/// policy card (unknown or inherited codes, repeated keys, every real answer code, a number, a forged "ack") the page
/// answers 200 and shows no approval answer, while the other address messages of the card (advance) still show; and the real
/// approve forms, posted as a browser without JavaScript posts them, show their own answer in their own response, with no
/// cookie set to carry it. The answer texts are the current ones (legacy i18n at 17c1f1f; the script's own snippets were
/// older wordings of the same answers).
/// </summary>
public sealed partial class LegacyAddressGuardTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private static readonly string[] Codes = ["done", "already", "forbidden", "own", "invalid", "ri-over-ceded", "suspended", "reduction-exceeds"];

    private static IReadOnlyDictionary<string, string> Texts() =>
        Codes.Select(c => (c, PolicyActions.AnswerText(c))).Where(x => x.Item2 is not null).ToDictionary(x => x.c, x => x.Item2!, StringComparer.Ordinal);

    private static List<string> Shown(string html) => [.. Texts().Where(t => html.Contains(t.Value, StringComparison.Ordinal)).Select(t => t.Key)];

    [GeneratedRegex("<form\\b([^>]*)>(.*?)</form>", RegexOptions.Singleline)]
    private static partial Regex FormTag();

    [GeneratedRegex("action=\"([^\"]+)\"")]
    private static partial Regex Action();

    [GeneratedRegex("<input[^>]*name=\"([^\"]+)\"[^>]*value=\"([^\"]*)\"")]
    private static partial Regex Input();

    /// <summary>The approve form on a page (the one carrying the policy's id): its action and its fields, as the browser would post them.</summary>
    private static (string Action, Dictionary<string, string> Fields) ApproveForm(string html, Guid id)
    {
        foreach (Match f in FormTag().Matches(html))
        {
            if (!f.Groups[2].Value.Contains($"value=\"{id}\"", StringComparison.Ordinal)) continue;
            var fields = Input().Matches(f.Groups[2].Value).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value), StringComparer.Ordinal);
            return (WebUtility.HtmlDecode(Action().Match(f.Groups[1].Value).Groups[1].Value), fields);
        }
        throw new Xunit.Sdk.XunitException("no approve form for the run policy");
    }

    [Fact]
    public async Task An_approval_answer_is_never_taken_from_the_address_bar_and_each_real_press_shows_its_own()
    {
        var (admin, _) = await app.SignedInAsync("admin");
        var (operatorClient, _) = await app.SignedInAsync("sales");
        var start = new DateOnly(2026, 1, 1);
        var p = await app.W.PendingPolicyAsync(await app.W.ClientAsync(), start, schedule: [(0, 365m)]);
        var card = $"/policies/{p.Id}";
        var issues = async () => (await app.W.PremiumRowsAsync(p.Id)).Count;

        string[] odd = ["__proto__", "constructor", "toString", "hasOwnProperty", "nonsense"];
        var typed = odd.Concat(odd.Select(o => $"ri-{o}")).Select(v => $"approve={Uri.EscapeDataString(v)}")
            .Concat(["approve=done&approve=ri-x"]).Concat(Codes.Select(c => $"approve={c}"))
            .Concat([$"approve=done&number={Uri.EscapeDataString(p.Number)}", "approve=forbidden&ack=AbCdEfGh12345_-x"]).ToList();
        foreach (var path in new[] { "/policies/pending", card })
            foreach (var q in typed)
            {
                var response = await admin.GetAsync($"{path}?{q}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                Assert.True(Shown(html).Count == 0, $"typed {path}?{q} shows {string.Join(", ", Shown(html))}");
            }
        // The other address messages of the card are unchanged (not part of H28).
        Assert.Contains(BillingTexts.Payment.Advances.Outcomes["nothing-left"], await PolicyWebApp.HtmlAsync(admin, $"{card}?advance=nothing-left"));

        var list = ApproveForm(await PolicyWebApp.HtmlAsync(admin, "/policies/pending"), p.Id);
        var onCard = ApproveForm(await PolicyWebApp.HtmlAsync(admin, card), p.Id);
        var presses = new (string What, HttpClient Client, string Page, (string Action, Dictionary<string, string> Fields) Form, string? Id, string Expect, int Issues)[]
        {
            ("operator presses approve on the list", operatorClient, "/policies/pending", list, null, "forbidden", 0),
            ("operator presses approve on the card", operatorClient, card, onCard, null, "forbidden", 0),
            ("admin presses approve with a malformed id", admin, "/policies/pending", list, "abc", "invalid", 0),
            ("admin approves on the card", admin, card, onCard, null, "done", 1),
            ("admin presses approve again from the list it had open", admin, "/policies/pending", list, null, "already", 1),
        };
        foreach (var press in presses)
        {
            var fields = new Dictionary<string, string>(press.Form.Fields);
            fields.Remove("__RequestVerificationToken");
            if (press.Id is not null) fields["id"] = press.Id;
            var response = await WebApp.PostFormAsync(press.Client, press.Form.Action, fields, press.Page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Equal([press.Expect], Shown(html));
            // On the list an approval or a repeat names the policy; a refusal is given before the policy is read.
            if (press.Page != card && press.Expect is "done" or "already") Assert.Contains($"{p.Number}: ", html);
            Assert.Equal(press.Issues, await issues());
            Assert.False(response.Headers.Contains("Set-Cookie"), $"{press.What}: a cookie was set");
        }
    }
}
