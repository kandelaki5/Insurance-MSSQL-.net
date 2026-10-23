using System.Net;
using Dapper;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>The closed month over HTTP (docs/04 r14): a receipt dated in it is refused on its date; a late approval is booked on the first open day. Its own database: December 2025 is closed.</summary>
public sealed class ClosedMonthWebTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    [Fact]
    public async Task A_receipt_in_a_closed_month_is_refused_and_a_late_approval_is_booked_in_the_first_open_month()
    {
        var (accountant, _) = await app.SignedInAsync("accountant");
        var (head, headId) = await app.SignedInAsync("operations_head");
        var december = new DateOnly(2025, 12, 1);
        var p = await app.W.PendingPolicyAsync(await app.W.ClientAsync(), december.AddDays(3));
        await app.W.CloseMonthAsync(december, headId);
        var answer = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(head, "/policies/pending?handler=Approve", new() { ["id"] = p.Id.ToString() }, "/policies/pending")).Content.ReadAsStringAsync());
        Assert.Contains($"{p.Number}: {PolicyTexts.ApproveOutcomes["done"]}", answer);
        Assert.Equal(new DateOnly(2026, 1, 1), (await app.W.PremiumRowsAsync(p.Id)).Single().BookedOn);

        // The allocation would be booked today (open), so the closed month is reached by closing today's month too.
        await app.W.CloseMonthAsync(World.Today, headId);
        var refused = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", new()
        {
            ["policy_id"] = p.Id.ToString(), ["amount_gel"] = "10.00", ["received_on"] = "2025-12-15", ["method"] = "bank", ["reference"] = "",
        })).Content.ReadAsStringAsync());
        Assert.Contains(BillingTexts.Payment.Errors.PeriodClosed, refused);
        Assert.Equal(0m, await app.W.PaidAsync(p.Id));
        // A cancellation then is booked after the closed months.
        var cancel = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(head, $"/policies/{p.Id}/cancel", new() { ["policy_id"] = p.Id.ToString(), ["cancel_on"] = "2026-02-01", ["reason"] = "x" })).Content.ReadAsStringAsync());
        Assert.DoesNotContain(PolicyTexts.Cancel.Errors.PeriodClosed, cancel);
        var first = new DateOnly(World.Today.Year, World.Today.Month, 1).AddMonths(1);
        Assert.Equal(first, (await app.W.PremiumRowsAsync(p.Id)).Single(r => r.Kind == "cancellation").BookedOn);
    }
}

/// <summary>Legacy scripts/integration/print-policy-money.test.ts over HTTP: the printed policy shows stored amounts exactly.</summary>
public sealed class PrintPolicyTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    [Fact]
    public async Task The_printed_policy_shows_the_largest_amounts_to_the_tetri_and_only_instalments_above_zero()
    {
        var (viewer, _) = await app.SignedInAsync("viewer");
        var holder = await app.W.ClientAsync();
        const decimal max = 9999999999999999.99m;
        var p = await app.W.PendingPolicyAsync(holder, new DateOnly(2026, 3, 1), premium: max, schedule: [(0, max), (31, 0m)]);
        await using (var c = await app.Db.OpenAppAsync())
        {
            await c.ExecuteAsync("update core.policy_line set sum_insured = @max, deductible = 1234.5 where policy_id = @id", new { max, id = p.Id });
            await c.ExecuteAsync("insert core.insured_object (policy_id, object_type, description, attributes) values (@id, N'vehicle', N'Opel Astra', N'{\"make\":\"Opel\",\"model\":\"Astra\",\"year\":2019,\"plate\":null,\"value\":35000.5,\"usage\":\"taxi\"}')", new { id = p.Id });
            await c.ExecuteAsync("insert core.policy_line (policy_id, class_code, premium, premium_gel) values (@id, N'6.2', 0.01, 0.01)", new { id = p.Id });
        }
        var html = await HtmlAsync(viewer, $"/print/policies/{p.Id}");
        Assert.Contains("9,999,999,999,999,999.99", html);
        Assert.Contains("1,234.50", html);
        Assert.Contains("10,000,000,000,000,000.00 GEL", html); // the total: max + 0.01, exactly
        Assert.Contains("35,000.50 GEL", html);
        Assert.Contains(PolicyTexts.Usages["taxi"], html);
        Assert.Contains("2019", html);
        Assert.Contains(PolicyTexts.Status["pending_approval"], html);
        Assert.Contains("data-print", html);
        Assert.DoesNotContain("<nav>", html);
        // The 0.00 instalment is not printed: one schedule row.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<td>01\\.0[34]\\.2026</td>"));
    }
}
