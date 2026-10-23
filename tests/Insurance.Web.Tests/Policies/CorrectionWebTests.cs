using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The old program's PRs #211/#225 over HTTP: after an open-month correction the policies list shows the original once, at its
/// corrected premium, and not the correction's record (Codex PR225-CORRECTION-LIST); the card's coverage table shows the premium
/// a coverage was issued with and, beside it, what it carries now (L225:3495, L225:3680).
/// </summary>
public sealed class CorrectionWebTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private static List<string[]> TableWith(string html, string heading) => GapsCardDisplayTests.TableWith(html, heading);

    [Fact]
    public async Task After_a_correction_the_list_shows_the_original_once_at_320_and_the_card_400_beside_320()
    {
        var head = await app.W.UserAsync("operations_head");
        var october = new DateOnly(2026, 10, 1);
        var o = await app.W.PendingPolicyAsync(await app.W.ClientAsync(), october, premium: 400m, schedule: [(0, 100m), (31, 100m), (61, 100m), (92, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(app.W.App, head, o.Id.ToString())).Outcome);
        var (viewer, _) = await app.SignedInAsync("viewer");

        Assert.NotNull((await app.W.EndorseAsync(head, o.Id, october.AddDays(6), "-80")).Redirect);
        var change = (await app.W.ChangeOfAsync(o.Id))!.Value;
        // Pending, the request is listed as before, under its request number.
        var pendingList = await GapsCardDisplayTests.RawAsync(viewer, $"/policies?q={Uri.EscapeDataString(o.Number)}");
        Assert.Contains($"/policies/{change.Id}", pendingList);
        Assert.Contains($"{o.Number}/მოთხოვნა-1", pendingList);

        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(app.W.App, await app.W.UserAsync("operations_head"), change.Id.ToString())).Outcome);
        var list = await GapsCardDisplayTests.RawAsync(viewer, $"/policies?q={Uri.EscapeDataString(o.Number)}");
        Assert.DoesNotContain($"/policies/{change.Id}", list);
        var row = Assert.Single(TableWith(list, PolicyTexts.Fields.Number), r => r[0] == o.Number);
        Assert.Contains("320.00 GEL", string.Join("|", row));

        // The card: issued 400.00 (and its GEL), now 320.00; the coverage headers of PR #211.
        var card = await GapsCardDisplayTests.RawAsync(viewer, $"/policies/{o.Id}");
        var coverage = Assert.Single(TableWith(card, PolicyTexts.CoverageColumns.CurrentPremium));
        Assert.Equal(["400.00", "400.00", "320.00"], coverage[^3..]);
        Assert.Contains($"{PolicyTexts.CoverageColumns.InitialPremium}, GEL", Html.Text(card));
        // The correction's record keeps its own card: −80.00 issued, nothing of its own now (its money is on the original).
        var record = Assert.Single(TableWith(await GapsCardDisplayTests.RawAsync(viewer, $"/policies/{change.Id}"), PolicyTexts.CoverageColumns.CurrentPremium));
        Assert.Equal(["-80.00", "-80.00", "0.00"], record[^3..]);
    }
}
