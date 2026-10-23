using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The shared payment page and the card's buttons by permission, over HTTP on a real database (legacy
/// receipt-permission.test.ts "the payment page and the card's buttons" and payment-shared-page.test.ts scenarios 3–5,
/// "the receipt permission does not open the other parts of the page"): a new receipt is premium.receipt.create; using
/// an earlier receipt's advance or another policy's overpayment keeps canWrite. The legacy worlds had September and
/// October closed and "today" 03.11.2026; here the two policies start on the first of today's month (S, then T) and the
/// receipt is received on S's start — the closed-month history ("October as it was") is not reproduced.
/// </summary>
public sealed partial class GapsPaymentPageTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;
    private const string Receipt = "premium.receipt.create";

    private async Task<(HttpClient Client, CurrentUser User)> AsAsync(string role)
    {
        var (client, id) = await app.SignedInAsync(role);
        return (client, await app.UserOfAsync(id));
    }

    [GeneratedRegex(@"<form\b[^>]*>([\s\S]*?)</form>")]
    private static partial Regex Forms();

    [GeneratedRegex(@"<input\b([^>]*)>")]
    private static partial Regex Inputs();

    private static string? Attr(string attrs, string name) => Regex.Match(attrs, $@"\b{name}=""([^""]*)""") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    /// <summary>The hidden and text inputs of each form, name → value.</summary>
    private static List<Dictionary<string, string>> FormFields(string html) => Forms().Matches(html).Select(f => Inputs().Matches(f.Groups[1].Value)
        .Select(i => (Name: Attr(i.Groups[1].Value, "name"), Value: Attr(i.Groups[1].Value, "value") ?? ""))
        .Where(x => x.Name is not null && x.Name != "__RequestVerificationToken").ToDictionary(x => x.Name!, x => x.Value)).ToList();

    /// <summary>The page's part under a section heading, up to the next heading ("" when there is none).</summary>
    private static string Part(string html, string title)
    {
        var at = html.IndexOf($">{title}</h2>", StringComparison.Ordinal);
        if (at < 0) return "";
        var next = html.IndexOf("<h2", at + title.Length, StringComparison.Ordinal);
        return next < 0 ? html[at..] : html[at..next];
    }

    private static bool HasReceiptForm(string html) => html.Contains("name=\"amount_gel\"", StringComparison.Ordinal);

    private static async Task<string> RawAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The card's button row: the links it offers, relative to the card.</summary>
    private static List<string> Buttons(string html, Guid id)
    {
        var row = Regex.Match(html, @"<p class=""row-actions""[\s\S]*?</p>");
        return row.Success ? Regex.Matches(row.Value, @"href=""([^""]+)""").Select(m => m.Groups[1].Value.Replace($"/policies/{id}", "").Replace($"/print/policies/{id}", "/print")).ToList() : [];
    }

    // ---------------------------------------------------------------- receipt-permission: the page and the card's buttons

    /// <summary>One GEL policy: instalments 200.00 (its start) and 100.00 (a month later).</summary>
    private async Task<World.Policy> PolicyAsync(CurrentUser admin)
    {
        var p = await W.PendingPolicyAsync(await W.ClientAsync(), Gaps.MonthStart, premium: 300m, schedule: [(0, 200m), (Gaps.MonthStart.AddMonths(1).DayNumber - Gaps.MonthStart.DayNumber, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, p.Id.ToString())).Outcome);
        return p;
    }

    [Fact]
    public async Task The_accountant_sees_the_receipt_form_the_underwriter_who_may_still_write_sees_the_page_without_it_a_viewer_is_sent_back()
    {
        var (_, admin) = await AsAsync("admin");
        var p = await PolicyAsync(admin);
        var (accountant, _) = await AsAsync("accountant");
        var (underwriter, _) = await AsAsync("underwriter");
        var (viewer, _) = await AsAsync("viewer");
        Assert.True(HasReceiptForm(await RawAsync(accountant, $"/policies/{p.Id}/payment")));
        Assert.False(HasReceiptForm(await RawAsync(underwriter, $"/policies/{p.Id}/payment")));
        Assert.Equal($"/policies/{p.Id}", Location(await viewer.GetAsync($"/policies/{p.Id}/payment")));
    }

    [Fact]
    public async Task A_viewer_given_the_permission_sees_the_form_its_own_fields_sent_record_exactly_150()
    {
        var (_, admin) = await AsAsync("admin");
        var p = await PolicyAsync(admin);
        var (viewer, v) = await AsAsync("viewer");
        await Gaps.DecideAsync(W, v.Id, Receipt, "allow");
        var html = await RawAsync(viewer, $"/policies/{p.Id}/payment");
        Assert.True(HasReceiptForm(html));
        var sent = FormFields(html).Single(f => f.ContainsKey("amount_gel"));
        Assert.Equal(p.Id.ToString(), sent["policy_id"]);
        sent["amount_gel"] = "150.00";
        sent["received_on"] = Insurance.Domain.Finance.Dates.ToIso(World.Today);
        sent["method"] = "bank";
        sent["reference"] = "R-150";
        Assert.Equal($"/policies/{p.Id}?paid=0", Location(await WebApp.PostFormAsync(viewer, $"/policies/{p.Id}/payment", sent)));
        Assert.Equal([(150m, 150m, 1m, World.Today, v.Id)], await W.QueryAsync<(decimal, decimal, decimal, DateOnly, Guid)>("""
            select a.amount, a.amount_gel, a.rate, a.booked_on, r.created_by from core.payment_allocation a join core.payment r on r.id = a.payment_id
            join core.installment i on i.id = a.installment_id where i.policy_id = @id and i.due_on = @start
            """, new { id = p.Id, start = Gaps.MonthStart }));
        Assert.Equal(150m, await W.OwedAsync(p.Id));
    }

    [Fact]
    public async Task The_card_record_a_payment_only_by_the_permission_print_change_and_cancel_only_by_canWrite()
    {
        var (_, admin) = await AsAsync("admin");
        var p = await PolicyAsync(admin);
        var (accountant, _) = await AsAsync("accountant");
        var (underwriter, _) = await AsAsync("underwriter");
        var (viewer, v) = await AsAsync("viewer");
        var (plainViewer, _) = await AsAsync("viewer");
        await Gaps.DecideAsync(W, v.Id, Receipt, "allow");
        Assert.Equal(["/payment", "/print", "/endorse", "/cancel"], Buttons(await RawAsync(accountant, $"/policies/{p.Id}"), p.Id));
        Assert.Equal(["/print", "/endorse", "/cancel"], Buttons(await RawAsync(underwriter, $"/policies/{p.Id}"), p.Id));
        Assert.Equal(["/payment"], Buttons(await RawAsync(viewer, $"/policies/{p.Id}"), p.Id));
        Assert.Empty(Buttons(await RawAsync(plainViewer, $"/policies/{p.Id}"), p.Id));
        // Nothing owed: no "record a payment".
        await W.PayAsync(admin, p.Id, "300.00", World.Today);
        Assert.Equal(["/print", "/endorse", "/cancel"], Buttons(await RawAsync(accountant, $"/policies/{p.Id}"), p.Id));
        // A draft: no button at all.
        var draft = await W.PendingPolicyAsync(await W.ClientAsync(), Gaps.MonthStart);
        await W.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("update core.policy set status = N'draft' where id = @id", new { id = draft.Id }));
        Assert.Empty(Buttons(await RawAsync(accountant, $"/policies/{draft.Id}"), draft.Id));
    }

    // ---------------------------------------------------------------- payment-shared-page: S and T

    /// <summary>S (150.00, one instalment) and T (100.00, one instalment) of one client; a receipt R of 150.00 received on S's start.</summary>
    private async Task<(World.Policy S, World.Policy T, Guid S1, Guid T1, Guid R)> SharedAsync(CurrentUser admin, bool allocatedToS)
    {
        var holder = await W.ClientAsync();
        var s = await W.PendingPolicyAsync(holder, Gaps.MonthStart, premium: 150m, schedule: [(0, 150m)]);
        var t = await W.PendingPolicyAsync(holder, Gaps.MonthStart, premium: 100m, schedule: [(0, 100m)]);
        foreach (var x in new[] { s, t }) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, x.Id.ToString())).Outcome);
        var s1 = await W.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = s.Id });
        var t1 = await W.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = t.Id });
        var r = await W.ReceiptAsync(holder, Gaps.MonthStart, 150m, reference: "R-0907");
        if (allocatedToS)
        {
            await W.App.AsUserAsync(admin.Id, async tx =>
            {
                await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @s1, 150, 150, 1, @on)", new { r, s1, on = Gaps.MonthStart });
                // S's premium reduced by 50.00 today: the receipt holds 50.00 too much on S.
                await tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@s1, @today, -50, N'შემცირება')", new { s1, today = World.Today });
                return 0;
            });
        }
        return (s, t, s1, t1, r);
    }

    private Task<List<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>> LinesAsync(Guid r) =>
        W.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly)>("select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @r order by created_at, amount", new { r });

    private static string? OwedOn(string html) => Regex.Match(WebUtility.HtmlDecode(html), @"<p class=""note"">[^<]*?:\s*([\d.,-]+) GEL") is { Success: true } m ? m.Groups[1].Value : null;

    [Fact]
    public async Task Scenario_3_the_accountant_without_the_receipt_permission_no_receipt_form_the_advance_form_100_of_the_receipt_settles_T_50_left()
    {
        var (_, admin) = await AsAsync("admin");
        var (accountant, acc) = await AsAsync("accountant");
        await Gaps.DecideAsync(W, acc.Id, Receipt, "deny");
        var x = await SharedAsync(admin, allocatedToS: false);
        var page = await RawAsync(accountant, $"/policies/{x.T.Id}/payment");
        Assert.False(HasReceiptForm(page));
        var forms = FormFields(Part(page, BillingTexts.Payment.Advances.Title));
        Assert.Equal([new Dictionary<string, string> { ["policy_id"] = x.T.Id.ToString(), ["payment_id"] = x.R.ToString() }], forms);
        Assert.Contains("150.00", Insurance.Web.Tests.Html.Text(Part(page, BillingTexts.Payment.Advances.Title)));
        Assert.Equal($"/policies/{x.T.Id}?advance=allocated&gel=10000&left=5000",
            Location(await WebApp.PostFormAsync(accountant, $"/policies/{x.T.Id}/payment?handler=Allocate", forms[0], $"/policies/{x.T.Id}/payment")));
        Assert.Equal([(x.T1, 100m, 100m, 1m, World.Today)], await LinesAsync(x.R));
        var after = await RawAsync(accountant, $"/policies/{x.T.Id}/payment");
        Assert.Equal("0.00", OwedOn(after));
        Assert.Equal(forms, FormFields(Part(after, BillingTexts.Payment.Advances.Title)));
        Assert.Contains("50.00", Insurance.Web.Tests.Html.Text(Part(after, BillingTexts.Payment.Advances.Title)));
    }

    [Fact]
    public async Task Scenario_4_a_reductions_overpayment_moved_to_the_other_policy_minus_50_on_S_and_plus_50_on_T_the_receipt_unchanged_T_owes_50()
    {
        var (_, admin) = await AsAsync("admin");
        var (accountant, acc) = await AsAsync("accountant");
        await Gaps.DecideAsync(W, acc.Id, Receipt, "deny");
        var x = await SharedAsync(admin, allocatedToS: true);
        var page = await RawAsync(accountant, $"/policies/{x.T.Id}/payment");
        Assert.False(HasReceiptForm(page));
        var forms = FormFields(Part(page, BillingTexts.Payment.Advances.OverpaidTitle));
        Assert.Equal([new Dictionary<string, string> { ["policy_id"] = x.T.Id.ToString(), ["payment_id"] = x.R.ToString() }], forms);
        Assert.Contains(x.S.Number, Part(page, BillingTexts.Payment.Advances.OverpaidTitle));
        Assert.Equal($"/policies/{x.T.Id}?advance=transferred&gel=5000&left=0&released=5000",
            Location(await WebApp.PostFormAsync(accountant, $"/policies/{x.T.Id}/payment?handler=Transfer", forms[0], $"/policies/{x.T.Id}/payment")));
        Assert.Equal([(x.S1, -50m, -50m, 1m, World.Today), (x.S1, 150m, 150m, 1m, Gaps.MonthStart), (x.T1, 50m, 50m, 1m, World.Today)],
            (await LinesAsync(x.R)).OrderBy(l => l.Installment == x.T1).ThenBy(l => l.Amount).ToList());
        Assert.Equal(150m, await W.ScalarAsync<decimal>("select sum(amount_gel) from core.payment_allocation where payment_id = @r", new { r = x.R }));
        Assert.Equal([(Gaps.MonthStart, 150m, "receipt")], await W.QueryAsync<(DateOnly, decimal, string)>("select received_on, amount_gel, kind from core.payment where id = @r", new { r = x.R }));
        var after = await RawAsync(accountant, $"/policies/{x.T.Id}/payment");
        Assert.Equal("50.00", OwedOn(after));
        Assert.Equal("", Part(after, BillingTexts.Payment.Advances.OverpaidTitle));
    }

    [Fact]
    public async Task Scenario_4_the_plus_50_on_T_failing_after_the_minus_50_on_S_neither_stays_the_retry_moves_it_exactly_once()
    {
        var (_, admin) = await AsAsync("admin");
        var accountant = await W.UserAsync("accountant");
        var x = await SharedAsync(admin, allocatedToS: true);
        var before = await LinesAsync(x.R);
        await Gaps.ArmAsync(app.Db, "payment_allocation", 2);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => PaymentActions.TransferOverpaymentAsync(W.App, W.Rates, accountant, x.T.Id.ToString(), x.R.ToString()));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await Gaps.DisarmAsync(app.Db);
        }
        Assert.Equal(before, await LinesAsync(x.R));
        Assert.Equal($"/policies/{x.T.Id}?advance=transferred&gel=5000&left=0&released=5000", await PaymentActions.TransferOverpaymentAsync(W.App, W.Rates, accountant, x.T.Id.ToString(), x.R.ToString()));
        Assert.Equal([(x.S1, -50m), (x.T1, 50m)], (await LinesAsync(x.R)).Where(l => l.BookedOn == World.Today && l.Amount != 150m).Select(l => (l.Installment, l.Amount)).OrderBy(l => l.Amount));
        Assert.Equal($"/policies/{x.T.Id}?advance=nothing-left", await PaymentActions.TransferOverpaymentAsync(W.App, W.Rates, accountant, x.T.Id.ToString(), x.R.ToString()));
        Assert.Equal(3, (await LinesAsync(x.R)).Count);
    }

    [Theory]
    [InlineData(false, "personal")]
    [InlineData(false, "group")]
    [InlineData(true, "personal")]
    [InlineData(true, "group")]
    public async Task A_viewer_given_the_receipt_permission_sees_the_receipt_form_not_the_advance_or_the_overpayment_a_forged_submit_is_refused_read_only(bool overpayment, string grant)
    {
        var (_, admin) = await AsAsync("admin");
        var (viewer, v) = await AsAsync("viewer");
        if (grant == "personal") await Gaps.DecideAsync(W, v.Id, Receipt, "allow");
        else await Gaps.GroupAsync(W, Receipt, v.Id);
        var x = await SharedAsync(admin, allocatedToS: overpayment);
        var page = await RawAsync(viewer, $"/policies/{x.T.Id}/payment");
        Assert.True(HasReceiptForm(page));
        var title = overpayment ? BillingTexts.Payment.Advances.OverpaidTitle : BillingTexts.Payment.Advances.Title;
        Assert.Equal("", Part(page, title));
        var before = await LinesAsync(x.R);
        var handler = overpayment ? "Transfer" : "Allocate";
        Assert.Equal($"/policies/{x.T.Id}?advance=read-only", Location(await WebApp.PostFormAsync(viewer, $"/policies/{x.T.Id}/payment?handler={handler}",
            new() { ["policy_id"] = x.T.Id.ToString(), ["payment_id"] = x.R.ToString() }, $"/policies/{x.T.Id}/payment")));
        Assert.Equal(before, await LinesAsync(x.R));
    }

    [Fact]
    public async Task Scenario_5_deny_then_inherit_by_the_admins_panel_the_receipt_form_and_the_cards_record_a_payment_follow_the_advance_form_does_not()
    {
        var (adminClient, admin) = await AsAsync("admin");
        var (accountant, acc) = await AsAsync("accountant");
        await Gaps.DecideAsync(W, acc.Id, Receipt, "deny");
        var x = await SharedAsync(admin, allocatedToS: false);
        var deniedPage = await RawAsync(accountant, $"/policies/{x.T.Id}/payment");
        var deniedCard = Insurance.Web.Tests.Html.Text(await RawAsync(accountant, $"/policies/{x.T.Id}"));
        var answer = await WebApp.PostFormAsync(adminClient, "/users/permissions?handler=SetUserDecision",
            new() { ["user_id"] = acc.Id.ToString(), ["permission"] = Receipt, ["decision"] = "inherit" }, "/users/permissions");
        Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, answer.StatusCode.ToString());
        Assert.Equal(0, await W.ScalarAsync<int>("select count(*) from core.user_permission where user_id = @id", new { id = acc.Id }));
        var inheritedPage = await RawAsync(accountant, $"/policies/{x.T.Id}/payment");
        var inheritedCard = Insurance.Web.Tests.Html.Text(await RawAsync(accountant, $"/policies/{x.T.Id}"));
        Assert.Equal((false, true), (HasReceiptForm(deniedPage), HasReceiptForm(inheritedPage)));
        Assert.Equal((false, true), (deniedCard.Contains(BillingTexts.Payment.Record, StringComparison.Ordinal), inheritedCard.Contains(BillingTexts.Payment.Record, StringComparison.Ordinal)));
        Assert.Equal(FormFields(Part(deniedPage, BillingTexts.Payment.Advances.Title)), FormFields(Part(inheritedPage, BillingTexts.Payment.Advances.Title)));
        Assert.Equal((true, true), (deniedCard.Contains(BillingTexts.Payment.Advances.CardNote, StringComparison.Ordinal), inheritedCard.Contains(BillingTexts.Payment.Advances.CardNote, StringComparison.Ordinal)));
        Assert.Equal($"/policies/{x.T.Id}?advance=allocated&gel=10000&left=5000",
            Location(await WebApp.PostFormAsync(accountant, $"/policies/{x.T.Id}/payment?handler=Allocate", new() { ["policy_id"] = x.T.Id.ToString(), ["payment_id"] = x.R.ToString() }, $"/policies/{x.T.Id}/payment")));
        Assert.Equal([(x.T1, 100m, 100m, 1m, World.Today)], await LinesAsync(x.R));
    }

    // ---------------------------------------------------------------- an empty USD or EUR policy without a rate today

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    public async Task An_empty_foreign_policy_without_a_rate_today_shows_0_00_both_buttons_disabled_and_the_receipt_form_nothing_written(string currency)
    {
        var (_, admin) = await AsAsync("admin");
        var (accountant, _) = await AsAsync("accountant");
        var holder = await W.ClientAsync();
        // A policy with no schedule and no credit; an advance of the client (so the allocate button shows), no rate for today.
        var day = new DateOnly(2025, 2, 3);
        await W.RateAsync(currency, day, 2.7m);
        var p = await W.PendingPolicyAsync(holder, day, currency, 2.7m, premium: 0m, schedule: []);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, p.Id.ToString())).Outcome);
        await W.ReceiptAsync(holder, day, 10m);
        var ledger = await Gaps.LedgerAsync(W);
        var page = await RawAsync(accountant, $"/policies/{p.Id}/payment");
        Assert.Contains($"{BillingTexts.Payment.OutstandingNow}: 0.00 {currency}", WebUtility.HtmlDecode(page));
        Assert.True(HasReceiptForm(page));
        var buttons = Regex.Matches(Part(page, BillingTexts.Payment.Advances.Title), @"<button class=""btn""([^>]*)>").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Contains("disabled", b));
        Assert.Equal(ledger, await Gaps.LedgerAsync(W));
    }
}

/// <summary>
/// A USD or EUR schedule row without today's rate is the error it was (legacy payment-shared-page.test.ts,
/// PAYMENT_PAGE_EMPTY_FOREIGN_MISSING_RATE_REGRESSION_20261019): a zero row, one reduced to zero today, a positive one, one
/// with only a credit after today — the page fails, nothing is written; with today's rate stored, the page and its debt.
/// Its own site and database: today's rates are stored at the end. The National Bank is unreachable (PolicyWebApp);
/// that it is asked exactly once is not counted here.
/// </summary>
public sealed class GapsPaymentPageForeignRowTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;

    [Fact]
    public async Task A_USD_or_EUR_schedule_row_without_a_rate_today_is_an_error_the_same_row_with_todays_rate_shows_its_debt()
    {
        var admin = await W.UserAsync();
        var (acc, _) = await app.SignedInAsync("accountant");
        var b = Gaps.MonthStart;
        var cases = new List<(string Currency, Guid Policy, string Owed)>();
        foreach (var currency in new[] { "USD", "EUR" })
        {
            async Task<(Guid Id, List<Guid> Inst)> PolicyAsync(params (int Day, decimal Amount)[] schedule)
            {
                var p = await W.PendingPolicyAsync(await W.ClientAsync(), b, currency, 2.7m, premium: 100.01m, schedule: [.. schedule]);
                Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, p.Id.ToString())).Outcome);
                return (p.Id, await W.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id }));
            }
            var zero = await PolicyAsync((0, 0m));
            cases.Add((currency, zero.Id, "0.00"));
            var reduced = await PolicyAsync((0, 100m));
            await W.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@i, @today, -100, N'შემცირება')", new { i = reduced.Inst[0], today = World.Today }));
            cases.Add((currency, reduced.Id, "0.00"));
            var positive = await PolicyAsync((0, 100m), (b.AddMonths(2).DayNumber - b.DayNumber, 0.01m));
            cases.Add((currency, positive.Id, "100.01"));
            // Only a credit after today: a receipt dated ten days on, 10.00 of it on the row.
            var later = await PolicyAsync((0, 100m));
            var holder = await W.ScalarAsync<Guid>("select c.policyholder_id from core.policy p join core.contract c on c.id = p.contract_id where p.id = @id", new { id = later.Id });
            var receipt = await W.ReceiptAsync(holder, World.Today.AddDays(10), 27m);
            await W.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @i, 10, 27, 2.7, @on)",
                new { receipt, i = later.Inst[0], on = World.Today.AddDays(10) }));
            cases.Add((currency, later.Id, "100.00"));
        }
        var ledger = await Gaps.LedgerAsync(W);
        foreach (var (currency, policy, _) in cases)
        {
            var response = await acc.GetAsync($"/policies/{policy}/payment");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains($"No {currency} rate for {Gaps.Iso(World.Today)}", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }
        Assert.Equal(ledger, await Gaps.LedgerAsync(W));
        foreach (var currency in new[] { "USD", "EUR" }) await W.RateAsync(currency, World.Today, 2.7m);
        foreach (var (currency, policy, owed) in cases)
            Assert.Contains($"{BillingTexts.Payment.OutstandingNow}: {owed} {currency}", await HtmlAsync(acc, $"/policies/{policy}/payment"));
    }
}
