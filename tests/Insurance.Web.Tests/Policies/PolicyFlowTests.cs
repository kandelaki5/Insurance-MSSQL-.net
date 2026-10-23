using System.Net;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The policy pages over HTTP on a real database: the forms are opened, posted with their anti-forgery tokens, and the
/// redirect, the page and the rows written are checked — issue, approve, cancel, endorse, the card, print, the list.
/// </summary>
public sealed class PolicyFlowTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;

    private async Task<string> ClientTaxIdAsync()
    {
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await W.ClientAsync(taxId);
        return taxId;
    }

    private static Dictionary<string, string> IssueFields(string productId, string taxId) => new()
    {
        ["product_id"] = productId, ["policyholder_id_number"] = taxId, ["make"] = "Toyota", ["model"] = "Corolla", ["year"] = "2021", ["plate"] = "AA-001-AA",
        ["value"] = "35000", ["vehicle_type"] = "car", ["steering"] = "left", ["usage"] = "personal", ["effective_from"] = "2026-10-01", ["effective_to"] = "2027-10-01",
        ["currency"] = "GEL", ["cov_damage_premium"] = "700", ["cov_damage_limit"] = "35000", ["cov_theft_on"] = "on", ["cov_theft_premium"] = "300", ["installments"] = "4",
        ["intermediary_id"] = "", ["commission_percent"] = "", ["cancel_grace_days"] = "",
    };

    [Fact]
    public async Task Issue_then_approve_from_the_pending_list_writes_the_ledger_schedule_and_cession()
    {
        var (sales, _) = await app.SignedInAsync("sales");
        var (head, _) = await app.SignedInAsync("operations_head");
        var taxId = await ClientTaxIdAsync();
        var (product, _) = await W.CascoAsync();
        var form = await HtmlAsync(sales, "/policies/new");
        Assert.Contains(PolicyTexts.New, form);
        Assert.Contains($"value=\"{product}\"", form);

        // A refused form shows the values as typed and each field's message.
        var refused = await WebApp.PostFormAsync(sales, "/policies/new", new(IssueFields(product.ToString(), taxId)) { ["make"] = "", ["installments"] = "2" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var refusedHtml = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Assert.Contains(PolicyTexts.Errors.Make, refusedHtml);
        Assert.Contains(PolicyTexts.Errors.Installments, refusedHtml);
        Assert.Contains("value=\"Corolla\"", refusedHtml);

        var issued = Location(await WebApp.PostFormAsync(sales, "/policies/new", IssueFields(product.ToString(), taxId)));
        Assert.Matches("^/policies/[0-9a-f-]{36}\\?issued=1$", issued);
        var id = Guid.Parse(issued[10..46]);
        var card = await HtmlAsync(sales, issued);
        Assert.Contains(PolicyTexts.Issued, card);
        Assert.Contains(PolicyTexts.AwaitingApproval, card);
        Assert.Equal("pending_approval", await W.ScalarAsync<string>("select status from core.policy where id = @id", new { id }));

        var pending = await HtmlAsync(head, "/policies/pending");
        Assert.Contains($"/policies/{id}", pending);
        var answer = await WebApp.PostFormAsync(head, "/policies/pending?handler=Approve", new() { ["id"] = id.ToString() }, "/policies/pending");
        var answerHtml = WebUtility.HtmlDecode(await answer.Content.ReadAsStringAsync());
        var number = await W.ScalarAsync<string>("select number from core.policy where id = @id", new { id });
        Assert.Contains($"{number}: {PolicyTexts.ApproveOutcomes["done"]}", answerHtml);
        var rows = await W.PremiumRowsAsync(id);
        Assert.Equal([300m, 700m], rows.Select(r => r.Amount).Order());
        Assert.All(rows, r => Assert.Equal((new DateOnly(2026, 10, 1), "issue"), (r.BookedOn, r.Kind)));
        Assert.Equal("250.00/250.00/250.00/250.00", await W.ScheduleTextAsync(id));
        // Pressed again (a stale list): the answer, nothing more written.
        var again = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(head, "/policies/pending?handler=Approve", new() { ["id"] = id.ToString() }, "/policies/pending")).Content.ReadAsStringAsync());
        Assert.Contains($"{number}: {PolicyTexts.ApproveOutcomes["already"]}", again);
        Assert.Equal(2, (await W.PremiumRowsAsync(id)).Count);
        // A typed address shows no answer.
        Assert.DoesNotContain(PolicyTexts.ApproveOutcomes["already"], await HtmlAsync(head, "/policies/pending?approve=already"));

        // The card: active, the figures, the schedule, the print page.
        var active = await HtmlAsync(head, $"/policies/{id}");
        Assert.Contains(PolicyTexts.Status["active"], active);
        Assert.Contains("1,000.00", active);
        var print = await HtmlAsync(sales, $"/print/policies/{id}");
        Assert.Contains($"№ {number}", print);
        Assert.Contains("Toyota Corolla", print);
        Assert.Contains("35,000.00 GEL", print);
        Assert.Contains("01.10.2026 00:00 – 30.09.2027 24:00 (365 დღე)", print);
        Assert.Contains("1,000.00 GEL", print);
        Assert.Contains(PolicyTexts.Usages["personal"], print);
        // The list finds it by its number.
        Assert.Contains($"/policies/{id}", await HtmlAsync(sales, $"/policies?q={number}"));
        Assert.DoesNotContain($"/policies/{id}", await HtmlAsync(sales, "/policies?q=nothing-like-it"));
    }

    [Fact]
    public async Task Approval_on_the_card_answers_on_the_card()
    {
        var (head, _) = await app.SignedInAsync("operations_head");
        var p = await W.PendingPolicyAsync(await W.ClientAsync(), new DateOnly(2026, 4, 1));
        var html = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(head, $"/policies/{p.Id}?handler=Approve", new() { ["id"] = p.Id.ToString() }, $"/policies/{p.Id}")).Content.ReadAsStringAsync());
        Assert.Contains(PolicyTexts.ApproveOutcomes["done"], html);
        Assert.DoesNotContain($"{p.Number}: {PolicyTexts.ApproveOutcomes["done"]}", html);
        Assert.Single(await W.PremiumRowsAsync(p.Id));
    }

    [Fact]
    public async Task Cancel_through_the_form_books_the_reversal_and_reduces_the_schedule()
    {
        var (admin, adminId) = await app.SignedInAsync("admin");
        var user = await app.UserOfAsync(adminId);
        var p = await W.ActivePolicyAsync(user, await W.ClientAsync(), new DateOnly(2026, 1, 1));
        var page = await HtmlAsync(admin, $"/policies/{p.Id}/cancel");
        Assert.Contains(PolicyTexts.Cancel.PreviewTitle, page);
        Assert.Contains("data-previews", page);
        var refused = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(admin, $"/policies/{p.Id}/cancel", new() { ["policy_id"] = p.Id.ToString(), ["cancel_on"] = "2027-01-01", ["reason"] = "x" })).Content.ReadAsStringAsync());
        Assert.Contains(PolicyTexts.Cancel.Errors.OutsideTerm, refused);
        var to = Location(await WebApp.PostFormAsync(admin, $"/policies/{p.Id}/cancel", new() { ["policy_id"] = p.Id.ToString(), ["cancel_on"] = "2026-07-01", ["reason"] = "კლიენტის მოთხოვნა" }));
        Assert.Equal($"/policies/{p.Id}?cancelled=1", to);
        Assert.Equal(-184m, (await W.PremiumRowsAsync(p.Id)).Single(r => r.Kind == "cancellation").Amount);
        Assert.Equal(181m, await W.ScheduledAsync(p.Id));
        var card = await HtmlAsync(admin, to);
        Assert.Contains(PolicyTexts.Cancel.Done, card);
        Assert.Contains($"{PolicyTexts.Cancel.CancelledOn}: 01.07.2026", card);
        // A cancelled policy's cancel and change pages go back to the card.
        Assert.Equal($"/policies/{p.Id}", Location(await admin.GetAsync($"/policies/{p.Id}/cancel")));
        Assert.Equal($"/policies/{p.Id}", Location(await admin.GetAsync($"/policies/{p.Id}/endorse")));
    }

    [Fact]
    public async Task Endorse_through_the_form_issues_the_change_as_a_new_pending_policy()
    {
        var (admin, adminId) = await app.SignedInAsync("admin");
        var user = await app.UserOfAsync(adminId);
        var p = await W.ActivePolicyAsync(user, await W.ClientAsync(), new DateOnly(2026, 1, 1));
        var page = await HtmlAsync(admin, $"/policies/{p.Id}/endorse");
        Assert.Contains(PolicyTexts.Endorse.Title, page);
        var to = Location(await WebApp.PostFormAsync(admin, $"/policies/{p.Id}/endorse", new()
        {
            ["policy_id"] = p.Id.ToString(), ["change_on"] = "2026-04-10", ["reason"] = "ფრანშიზის ცვლილება", ["cov_damage_on"] = "on", ["cov_damage_delta"] = "-50", ["cov_damage_deductible"] = "500",
        }));
        var change = (await W.ChangeOfAsync(p.Id))!.Value;
        Assert.Equal($"/policies/{change.Id}?issued=1", to);
        Assert.Equal((new DateOnly(2026, 4, 10), "pending_approval"), (change.EffectiveFrom, change.Status));
        Assert.Equal([(-50m, (decimal?)500m)], await W.QueryAsync<(decimal, decimal?)>("select premium, deductible from core.policy_line where policy_id = @id", new { id = change.Id }));
        var card = await HtmlAsync(admin, $"/policies/{change.Id}");
        Assert.Contains(PolicyTexts.Endorse.Replaces, card);
        Assert.Contains(p.Number, card);
        var original = await HtmlAsync(admin, $"/policies/{p.Id}");
        Assert.Contains(PolicyTexts.Endorse.Changes, original);
        Assert.Contains("ფრანშიზის ცვლილება", original);
    }

    [Fact]
    public async Task A_viewer_is_refused_on_every_write()
    {
        var (viewer, viewerId) = await app.SignedInAsync("viewer");
        var (admin, adminId) = await app.SignedInAsync("admin");
        var user = await app.UserOfAsync(adminId);
        var holder = await W.ClientAsync();
        var p = await W.ActivePolicyAsync(user, holder, new DateOnly(2026, 1, 1));
        var pending = await W.PendingPolicyAsync(holder, new DateOnly(2026, 1, 1));
        var receipt = await W.ReceiptAsync(holder, new DateOnly(2026, 1, 5), 50m);
        var paid = await W.PayAsync(user, p.Id, "400.00", new DateOnly(2026, 1, 5));
        var (product, _) = await W.CascoAsync();

        // Pages that only write send the viewer back; the card shows no write button.
        Assert.Equal("/policies", Location(await viewer.GetAsync("/policies/new")));
        Assert.Equal("/policies", Location(await viewer.GetAsync("/policies/import")));
        Assert.Equal($"/policies/{p.Id}", Location(await viewer.GetAsync($"/policies/{p.Id}/cancel")));
        Assert.Equal($"/policies/{p.Id}", Location(await viewer.GetAsync($"/policies/{p.Id}/endorse")));
        Assert.Equal($"/policies/{p.Id}", Location(await viewer.GetAsync($"/policies/{p.Id}/payment")));
        var card = await HtmlAsync(viewer, $"/policies/{p.Id}");
        Assert.DoesNotContain("handler=ReversePayment", card);
        Assert.DoesNotContain($"/policies/{p.Id}/cancel", card);
        Assert.DoesNotContain(PolicyTexts.Import.Button, await HtmlAsync(viewer, "/policies"));

        // The writes posted anyway (the anti-forgery token from a page the viewer may open).
        string Html(HttpResponseMessage r) => WebUtility.HtmlDecode(r.Content.ReadAsStringAsync().Result);
        var cardUrl = $"/policies/{p.Id}";
        var viewerIssue = await WebApp.PostFormAsync(viewer, "/policies/new", IssueFields(product.ToString(), "x"), "/policies/pending");
        Assert.Contains(Ka.Common.ReadOnly, Html(viewerIssue));
        Assert.Contains(PolicyTexts.ApproveOutcomes["forbidden"], Html(await WebApp.PostFormAsync(viewer, "/policies/pending?handler=Approve", new() { ["id"] = pending.Id.ToString() }, "/policies/pending")));
        // The receipt is refused (premium.receipt.create); the page itself is not the viewer's, so it goes back to the card.
        Assert.Equal(cardUrl, Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}/payment", new() { ["policy_id"] = p.Id.ToString(), ["amount_gel"] = "10", ["received_on"] = "2026-01-05", ["method"] = "bank" }, cardUrl)));
        // Cancel and change are refused (read-only) and, the pages not being the viewer's, go back to the card.
        Assert.Equal(cardUrl, Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}/cancel", new() { ["policy_id"] = p.Id.ToString(), ["cancel_on"] = "2026-03-01", ["reason"] = "x" }, cardUrl)));
        Assert.Equal(cardUrl, Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}/endorse", new() { ["policy_id"] = p.Id.ToString(), ["change_on"] = "2026-03-01", ["reason"] = "x", ["cov_damage_on"] = "on", ["cov_damage_delta"] = "5" }, cardUrl)));
        Assert.Equal($"/policies/{p.Id}?advance=read-only", Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}/payment?handler=Allocate", new() { ["policy_id"] = p.Id.ToString(), ["payment_id"] = receipt.ToString() }, cardUrl)));
        Assert.Equal($"/policies/{p.Id}?advance=read-only", Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}/payment?handler=Transfer", new() { ["policy_id"] = p.Id.ToString(), ["payment_id"] = receipt.ToString() }, cardUrl)));
        var firstReceipt = await W.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @holder and amount_gel = 400", new { holder });
        Assert.Equal(cardUrl, Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}?handler=ReversePayment", new() { ["policy_id"] = p.Id.ToString(), ["payment_id"] = firstReceipt.ToString(), ["reason"] = "x" }, cardUrl)));
        Assert.Equal(cardUrl, Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}?handler=RefundOverpayment", new() { ["policy_id"] = p.Id.ToString(), ["method"] = "bank" }, cardUrl)));
        Assert.Equal(cardUrl, Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}?handler=PlaceFacultative", new() { ["policy_id"] = p.Id.ToString(), ["treaty_id"] = Guid.NewGuid().ToString() }, cardUrl)));
        Assert.Equal($"/policies/{p.Id}?refund=not-allowed", Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}?handler=ApproveRefundRequest", new() { ["policy_id"] = p.Id.ToString(), ["request_id"] = Guid.NewGuid().ToString() }, cardUrl)));
        Assert.Equal($"/policies/{p.Id}?reversal=not-allowed", Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}?handler=ApproveReversalRequest", new() { ["policy_id"] = p.Id.ToString(), ["request_id"] = Guid.NewGuid().ToString() }, cardUrl)));
        var import = await PostMultipartAsync(viewer, "/policies/import?handler=Preview", new() { ["policy_holder_id_number"] = "x" }, ("file", "p.csv", "make\nA\n"u8.ToArray()), cardUrl);
        Assert.Contains(Ka.Common.ReadOnly, WebUtility.HtmlDecode(await import.Content.ReadAsStringAsync()));

        // Nothing was written by any of them.
        Assert.Equal(("pending_approval", 1, 0, 0, 0, 0), (await W.ScalarAsync<string>("select status from core.policy where id = @id", new { id = pending.Id }),
            await W.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder and kind = N'receipt' and amount_gel = 400", new { holder }),
            await W.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder and kind <> N'receipt'", new { holder }),
            await W.ScalarAsync<int>("select count(*) from core.payment_allocation where payment_id = @receipt", new { receipt }),
            await W.ScalarAsync<int>("select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id and t.kind <> N'issue'", new { id = p.Id }),
            await W.ScalarAsync<int>("select count(*) from core.policy where replaces_policy_id = @id", new { id = p.Id })));
        Assert.NotNull(paid.Redirect);
        _ = viewerId;
    }

    [Fact]
    public async Task Unknown_or_malformed_ids_are_not_found()
    {
        var (admin, _) = await app.SignedInAsync("admin");
        foreach (var url in new[] { $"/policies/{Guid.NewGuid()}", $"/policies/{Guid.NewGuid()}/payment", $"/policies/{Guid.NewGuid()}/cancel", $"/policies/{Guid.NewGuid()}/endorse",
                     $"/print/policies/{Guid.NewGuid()}", "/policies/not-a-policy", $"/policies/{Guid.NewGuid():N}", "/print/policies/x" })
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(url)).StatusCode);
    }
}
