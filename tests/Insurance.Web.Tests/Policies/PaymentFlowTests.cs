using System.Net;
using System.Text;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The payment pages over HTTP on a real database: a receipt in GEL and in USD at the day's rate, an advance allocated,
/// an overpayment moved, a reversal and a refund with and without the financial director's request, a suspension
/// stopping a receipt, the overdue list, the financial director's panel, the corporate import.
/// </summary>
public sealed class PaymentFlowTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;
    private static readonly DateOnly Start = new(2026, 1, 1);

    private async Task<(HttpClient Client, Application.Services.CurrentUser User)> AsAsync(string role)
    {
        var (client, id) = await app.SignedInAsync(role);
        return (client, await app.UserOfAsync(id));
    }

    private static Dictionary<string, string> Receipt(Guid policyId, string amount, DateOnly on, string reference = "") => new()
    {
        ["policy_id"] = policyId.ToString(), ["amount_gel"] = amount, ["received_on"] = Insurance.Domain.Finance.Dates.ToIso(on), ["method"] = "bank", ["reference"] = reference,
    };

    [Fact]
    public async Task A_receipt_in_gel_settles_the_oldest_instalments_and_the_card_shows_it()
    {
        var (accountant, _) = await AsAsync("accountant");
        var (_, admin) = await AsAsync("admin");
        var p = await W.ActivePolicyAsync(admin, await W.ClientAsync(), Start);
        var page = await HtmlAsync(accountant, $"/policies/{p.Id}/payment");
        Assert.Contains($"{BillingTexts.Payment.OutstandingNow}: 365.00 GEL", page);
        var refused = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", Receipt(p.Id, "abc", World.Today.AddDays(2)))).Content.ReadAsStringAsync());
        Assert.Contains(BillingTexts.Payment.Errors.Amount, refused);
        Assert.Contains(BillingTexts.Payment.Errors.Date, refused);
        var to = Location(await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", Receipt(p.Id, "100.00", Start.AddDays(4), "ქვითარი 1")));
        Assert.Equal($"/policies/{p.Id}?paid=0", to);
        var card = await HtmlAsync(accountant, to);
        Assert.Contains(BillingTexts.Payment.Saved, card);
        Assert.Contains("ქვითარი 1", card);
        Assert.Contains($"{Insurance.Domain.Finance.Dates.ToIso(World.Today)[8..10]}.", card);
        Assert.Equal("91.25/8.75", string.Join("/", (await W.QueryAsync<decimal>("""
            select a.amount from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id order by i.due_on
            """, new { id = p.Id })).Select(a => a.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(265m, await W.OwedAsync(p.Id));
        // More than owed: the rest stays an advance and the card says how much.
        var advance = Location(await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", Receipt(p.Id, "300.00", Start.AddDays(5))));
        Assert.Equal($"/policies/{p.Id}?paid=3500", advance);
        Assert.Contains($"{BillingTexts.Payment.Advance}: 35.00", await HtmlAsync(accountant, advance));
    }

    [Fact]
    public async Task A_receipt_on_a_usd_policy_is_at_the_receipt_days_rate()
    {
        var (accountant, _) = await AsAsync("accountant");
        var (_, admin) = await AsAsync("admin");
        await W.RateAsync("USD", Start, 2.7m);
        await W.RateAsync("USD", Start.AddDays(6), 2.75m);
        await W.RateAsync("USD", World.Today, 2.8m);
        var p = await W.ActivePolicyAsync(admin, await W.ClientAsync(), Start, "USD", 2.7m);
        Assert.Contains($"{BillingTexts.Payment.OutstandingNow}: 365.00 USD", await HtmlAsync(accountant, $"/policies/{p.Id}/payment"));
        Assert.NotNull(Location(await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", Receipt(p.Id, "275.00", Start.AddDays(6)))));
        var lines = await W.QueryAsync<(decimal Amount, decimal AmountGel, decimal Rate)>("""
            select a.amount, a.amount_gel, a.rate from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id order by i.due_on
            """, new { id = p.Id });
        // 275.00 GEL at 2.75 = 100.00 USD: 91.25 (250.94 GEL) and 8.75 (the rest, 24.06 GEL).
        Assert.Equal([(91.25m, 250.94m, 2.75m), (8.75m, 24.06m, 2.75m)], lines);
        var card = await HtmlAsync(accountant, $"/policies/{p.Id}");
        Assert.Contains($"{PolicyTexts.Finance.ValuationRate}: 2.8000", card);
        Assert.Contains("742.00", card); // 265.00 USD × 2.80
        // A day without a stored rate (the bank unreachable): the date's message, nothing written.
        var refused = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", Receipt(p.Id, "10", Start.AddDays(7)))).Content.ReadAsStringAsync());
        Assert.Contains(BillingTexts.Payment.Errors.Rate, refused);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task An_advance_is_allocated_and_an_overpayment_is_moved_through_the_payment_page()
    {
        var (accountant, acc) = await AsAsync("accountant");
        var (_, admin) = await AsAsync("admin");
        var holder = await W.ClientAsync();
        var p = await W.ActivePolicyAsync(admin, holder, Start);
        var r = await W.ReceiptAsync(holder, Start.AddDays(4), 400m, acc.Id, "ავანსი");
        var page = await HtmlAsync(accountant, $"/policies/{p.Id}/payment");
        Assert.Contains(BillingTexts.Payment.Advances.Title, page);
        Assert.Contains("400.00", page);
        Assert.Contains($"{BillingTexts.Payment.Advances.CardNote}: 400.00 ₾", await HtmlAsync(accountant, $"/policies/{p.Id}"));
        var to = Location(await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment?handler=Allocate", new() { ["policy_id"] = p.Id.ToString(), ["payment_id"] = r.ToString() }, $"/policies/{p.Id}/payment"));
        Assert.Equal($"/policies/{p.Id}?advance=allocated&gel=36500&left=3500", to);
        Assert.Contains($"{BillingTexts.Payment.Advances.Allocated}: 365.00 ₾. {BillingTexts.Payment.Advances.Left}: 35.00 ₾", await HtmlAsync(accountant, to));
        Assert.Equal(0m, await W.OwedAsync(p.Id));

        // An approved reduction of P leaves 50.00 overpaid; it is moved to the client's other policy Q.
        var q = await W.ActivePolicyAsync(admin, holder, Start);
        var inst = await W.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @id order by due_on desc", new { id = p.Id });
        await W.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@inst, @today, -50, N'შემცირება')", new { inst, today = World.Today }));
        var qPage = await HtmlAsync(accountant, $"/policies/{q.Id}/payment");
        Assert.Contains(BillingTexts.Payment.Advances.OverpaidTitle, qPage);
        Assert.Contains(p.Number, qPage);
        Assert.Contains($"{BillingTexts.Payment.Advances.CardOverpaid}: 50.00 ₾", await HtmlAsync(accountant, $"/policies/{q.Id}"));
        var moved = Location(await WebApp.PostFormAsync(accountant, $"/policies/{q.Id}/payment?handler=Transfer", new() { ["policy_id"] = q.Id.ToString(), ["payment_id"] = r.ToString() }, $"/policies/{q.Id}/payment"));
        Assert.Equal($"/policies/{q.Id}?advance=transferred&gel=5000&left=0&released=5000", moved);
        Assert.Contains($"{BillingTexts.Payment.Advances.Transferred}: 50.00 ₾ ({BillingTexts.Payment.Advances.Released}: 50.00 ₾)", await HtmlAsync(accountant, moved));
        Assert.Equal(315m, await W.OwedAsync(q.Id));
        Assert.Equal(0m, await W.OwedAsync(p.Id));
        // A malformed answer in the address shows no notice; an unknown outcome neither.
        var odd = await HtmlAsync(accountant, $"/policies/{q.Id}?advance=allocated&gel=1e3&left=-1");
        Assert.DoesNotContain(BillingTexts.Payment.Advances.Allocated, odd);
        Assert.DoesNotContain("alert error", await HtmlAsync(accountant, $"/policies/{q.Id}?advance=__proto__"));
    }

    [Fact]
    public async Task A_reversal_and_a_refund_above_the_threshold_wait_for_the_financial_director()
    {
        await W.SettingAsync("payment.fd_threshold_gel", "150.00");
        try
        {
            var (accountant, acc) = await AsAsync("accountant");
            var (admin, adminUser) = await AsAsync("admin");
            var holder = await W.ClientAsync();
            var p = await W.ActivePolicyAsync(adminUser, holder, Start);
            await W.PayAsync(acc, p.Id, "100.00", Start.AddDays(4), reference: "R100");
            await W.PayAsync(acc, p.Id, "265.00", Start.AddDays(5), reference: "R265");
            var r100 = await W.ScalarAsync<Guid>("select id from core.payment where reference = N'R100' and payer_party_id = @holder", new { holder });
            var r265 = await W.ScalarAsync<Guid>("select id from core.payment where reference = N'R265' and payer_party_id = @holder", new { holder });
            var card = $"/policies/{p.Id}";

            // At or below the threshold: reversed at once.
            Assert.Equal(card, Location(await WebApp.PostFormAsync(accountant, $"{card}?handler=ReversePayment", new() { ["payment_id"] = r100.ToString(), ["policy_id"] = p.Id.ToString(), ["reason"] = "მცდარი" }, card)));
            Assert.Equal(1, await W.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @r100", new { r100 }));
            Assert.Contains(BillingTexts.Payment.Reversed, await HtmlAsync(accountant, card));
            // Above: a request; the accountant sees it waiting, the admin may approve.
            var requested = Location(await WebApp.PostFormAsync(accountant, $"{card}?handler=ReversePayment", new() { ["payment_id"] = r265.ToString(), ["policy_id"] = p.Id.ToString(), ["reason"] = "ორჯერ" }, card));
            Assert.Equal($"{card}?reversal=requested", requested);
            var waiting = await HtmlAsync(accountant, requested);
            Assert.Contains(BillingTexts.Payment.ReversalOutcomes["requested"], waiting);
            Assert.Contains(BillingTexts.Payment.ReversalRequests.Pending, waiting);
            Assert.DoesNotContain("handler=ApproveReversalRequest", waiting);
            var adminCard = await HtmlAsync(admin, card);
            Assert.Contains("handler=ApproveReversalRequest", adminCard);
            var request = await W.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @r265", new { r265 });
            Assert.Equal($"{card}?reversal=not-allowed", Location(await WebApp.PostFormAsync(accountant, $"{card}?handler=ApproveReversalRequest", new() { ["policy_id"] = p.Id.ToString(), ["request_id"] = request.ToString() }, card)));
            var approved = Location(await WebApp.PostFormAsync(admin, $"{card}?handler=ApproveReversalRequest", new() { ["policy_id"] = p.Id.ToString(), ["request_id"] = request.ToString() }, card));
            Assert.Equal($"{card}?reversal=approved", approved);
            var done = await HtmlAsync(accountant, approved);
            Assert.Contains(BillingTexts.Payment.ReversalRequests.Approved("სატესტო admin"), done);
            Assert.Equal(365m, await W.OwedAsync(p.Id));

            // A refund: 365.00 paid again, cancelled from day 181 → 184.00 overpaid, above the threshold.
            await W.PayAsync(acc, p.Id, "365.00", Start.AddDays(6));
            await W.CancelAsync(adminUser, p.Id, Start.AddDays(181));
            var overpaidCard = await HtmlAsync(accountant, card);
            Assert.Contains($"{BillingTexts.Payment.Refund}: 184.00 GEL", overpaidCard);
            Assert.Equal($"{card}?refund=requested", Location(await WebApp.PostFormAsync(accountant, $"{card}?handler=RefundOverpayment", new() { ["policy_id"] = p.Id.ToString(), ["method"] = "bank", ["reference"] = "IBAN" }, card)));
            Assert.Contains(BillingTexts.Payment.RefundRequests.Title, await HtmlAsync(accountant, card));
            var refund = await W.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @id", new { id = p.Id });
            Assert.Equal($"{card}?refund=approved", Location(await WebApp.PostFormAsync(admin, $"{card}?handler=ApproveRefundRequest", new() { ["policy_id"] = p.Id.ToString(), ["request_id"] = refund.ToString() }, card)));
            Assert.Equal(-184m, await W.ScalarAsync<decimal>("select amount_gel from core.payment where refund_request_id = @refund", new { refund }));
            Assert.Equal($"{card}?refund=already", Location(await WebApp.PostFormAsync(admin, $"{card}?handler=ApproveRefundRequest", new() { ["policy_id"] = p.Id.ToString(), ["request_id"] = refund.ToString() }, card)));
            Assert.Contains(BillingTexts.Payment.RefundOutcomes["already"], await HtmlAsync(accountant, $"{card}?refund=already"));
        }
        finally
        {
            await W.SettingAsync("payment.fd_threshold_gel", "10000.00");
        }
    }

    [Fact]
    public async Task A_refund_below_the_threshold_is_paid_at_once()
    {
        var (accountant, acc) = await AsAsync("accountant");
        var (_, admin) = await AsAsync("admin");
        var p = await W.ActivePolicyAsync(admin, await W.ClientAsync(), Start);
        await W.PayAsync(acc, p.Id, "365.00", Start.AddDays(4));
        await W.CancelAsync(admin, p.Id, Start.AddDays(181));
        Assert.Equal($"/policies/{p.Id}", Location(await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}?handler=RefundOverpayment", new() { ["policy_id"] = p.Id.ToString(), ["method"] = "cash", ["reference"] = "" }, $"/policies/{p.Id}")));
        Assert.Equal((-184m, "cash"), (await W.QueryAsync<(decimal, string)>("select amount_gel, method from core.payment where kind = N'refund' and payer_party_id = @h", new { h = p.Holder })).Single());
        Assert.Equal(0m, await W.OwedAsync(p.Id));
    }

    [Fact]
    public async Task A_suspension_stops_a_receipt_on_the_payment_page()
    {
        var (accountant, acc) = await AsAsync("accountant");
        var (_, admin) = await AsAsync("admin");
        var holder = await W.ClientAsync();
        var p = await W.ActivePolicyAsync(admin, holder, Start);
        await W.SuspendAsync(holder, admin.Id);
        var html = WebUtility.HtmlDecode(await (await WebApp.PostFormAsync(accountant, $"/policies/{p.Id}/payment", Receipt(p.Id, "50.00", Start.AddDays(4)))).Content.ReadAsStringAsync());
        Assert.Contains(AmlTexts.Suspension.Blocked, html);
        Assert.Contains("value=\"50.00\"", html);
        Assert.Equal(0, await W.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder }));
        _ = acc;
    }

    [Fact]
    public async Task The_overdue_list_names_the_policy_and_its_oldest_unpaid_instalment()
    {
        var (accountant, _) = await AsAsync("accountant");
        var (viewer, _) = await AsAsync("viewer");
        var (_, admin) = await AsAsync("admin");
        var from = World.Today.AddDays(-100);
        var p = await W.ActivePolicyAsync(admin, await W.ClientAsync(), from);
        await W.PayAsync(admin, p.Id, "91.25", from);
        var html = await HtmlAsync(accountant, "/payments/overdue");
        Assert.Contains($"/policies/{p.Id}", html);
        Assert.Contains(Fmt(from.AddDays(91)), html);
        Assert.Contains(">9<", html.Replace(" ", "")); // 100 − 91 days late
        Assert.Contains("91.25 GEL", html);
        Assert.Contains($"/policies/{p.Id}/payment", html);
        Assert.DoesNotContain($"/policies/{p.Id}/payment", await HtmlAsync(viewer, "/payments/overdue"));
    }

    private static string Fmt(DateOnly d) => d.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_financial_directors_panel_is_seen_by_its_permission_only()
    {
        var (admin, adminUser) = await AsAsync("admin");
        var (viewer, viewerUser) = await AsAsync("viewer");
        var p = await W.ActivePolicyAsync(adminUser, await W.ClientAsync(), Start);
        var refusedPage = await HtmlAsync(viewer, "/finance/panel");
        Assert.Contains(FdPanelTexts.NotAllowed, refusedPage);
        Assert.DoesNotContain("data-queue", refusedPage);
        Assert.DoesNotContain("href=\"/finance/panel\"", refusedPage);
        var panel = await HtmlAsync(admin, "/finance/panel?month=2026-01&claim=x");
        Assert.Contains("href=\"/finance/panel\"", panel);
        Assert.Contains("data-queue=\"refund\"", panel);
        Assert.Contains("data-section=\"month\"", panel);
        Assert.Contains("01.01.2026 – 31.01.2026", panel);
        Assert.Contains("/accounting/premium-report?month=2026-01", panel);
        Assert.Contains("data-figure=\"financialWritten\"", panel);
        var report = await W.App.ReadTxAsync(tx => Application.Services.PremiumReport.BuildAsync(tx, W.Rates, "2026-01"));
        Assert.Contains($"<td class=\"num\">{Application.Services.PremiumReport.Money(report.Totals.FinancialWrittenGel)}</td>", panel);
        Assert.True(report.Totals.FinancialWrittenGel >= 36500);
        // Given the permission personally, the viewer sees it (seeing is not authorising).
        await W.App.AsUserAsync(adminUser.Id, tx => Application.Services.PermissionStore.SetUserDecisionAsync(tx, viewerUser.Id, "finance.panel.view", "allow"));
        Assert.Contains("data-queue=\"reversal\"", await HtmlAsync(viewer, "/finance/panel"));
        _ = p;
    }

    [Fact]
    public async Task The_import_is_previewed_and_confirmed_through_its_forms_and_the_template_downloads()
    {
        var (sales, _) = await AsAsync("sales");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await W.ClientAsync(taxId);
        Assert.Contains(PolicyTexts.Import.Title, await HtmlAsync(sales, "/policies/import"));
        var csv = Encoding.UTF8.GetBytes("make,model,plate,value,installments,premium_damage\nტესტი,იმპორტი,IM-011-PT,30000,4,400\n");
        var fields = new Dictionary<string, string> { ["policyholder_id_number"] = taxId, ["effective_from"] = "2026-10-01", ["effective_to"] = "2027-10-01", ["currency"] = "GEL", ["installments"] = "1" };
        var preview = await PostMultipartAsync(sales, "/policies/import?handler=Preview", fields, ("file", "policies.csv", csv), "/policies/import");
        var html = await preview.Content.ReadAsStringAsync();
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Contains("ტესტი იმპორტი IM-011-PT", decoded);
        Assert.Contains(PolicyTexts.Import.Confirm(1), decoded);
        var payload = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"payload\" value=\"([^\"]+)\"").Groups[1].Value);
        var state = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"preview_state\" value=\"([^\"]+)\"").Groups[1].Value);
        // A changed plan in the payload: refused, the preview shown again with the message, nothing written.
        var changed = await WebApp.PostFormAsync(sales, "/policies/import?handler=Confirm", new() { ["payload"] = payload.Replace("\"installmentCount\":4", "\"installmentCount\":2"), ["preview_state"] = state }, "/policies/import");
        var changedHtml = WebUtility.HtmlDecode(await changed.Content.ReadAsStringAsync());
        Assert.Contains(PolicyTexts.Errors.Installments, changedHtml);
        Assert.Contains("ტესტი იმპორტი IM-011-PT", changedHtml);
        Assert.Equal(0, await W.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @holder", new { holder }));
        var to = Location(await WebApp.PostFormAsync(sales, "/policies/import?handler=Confirm", new() { ["payload"] = payload, ["preview_state"] = state }, "/policies/import"));
        var contract = await W.ScalarAsync<string>("select number from core.contract where policyholder_id = @holder", new { holder });
        Assert.Equal($"/policies?q={Uri.EscapeDataString(contract)}", to);
        Assert.Contains("ტესტი იმპორტი IM-011-PT", await HtmlAsync(sales, to));

        var template = await sales.GetAsync("/policies/import/template");
        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", template.Content.Headers.ContentType?.MediaType);
        Assert.Equal("policies-template.xlsx", template.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal((byte)'P', (await template.Content.ReadAsByteArrayAsync())[0]);
    }
}
