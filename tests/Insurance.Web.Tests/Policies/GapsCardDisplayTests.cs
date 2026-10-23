using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;
using A = Insurance.Application.Texts.BillingTexts.Payment.Advances;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The payment page and the policy card show money exactly, in whole tetri, over HTTP on a real database (legacy
/// payment-shared-page.test.ts: PAYMENT_PAGE_MONETARY_DECIMAL_PRECISION, POLICY_CARD_ADVANCE_TRANSFER_DISPLAY,
/// POLICY_CARD_REQUEST_HISTORY_MONEY_DISPLAY, POLICY_CARD_RECEIVABLE, PAYMENT_PAGE_OUTSTANDING — the cases the other web
/// tests leave out). The legacy rows were dated September–November 2026 with "today" 03.11.2026; here the receipts are
/// received on the first of today's month (or as stated) and the reductions booked today, the money exactly as there.
/// The card's position and commission arithmetic with the legacy days: GapsCardPositionTests.
/// </summary>
public sealed partial class GapsCardDisplayTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;
    private static DateOnly B => Gaps.MonthStart;
    private static DateOnly Today => World.Today;
    private static decimal M(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
    private static string Dmy(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    private static string Bank => BillingTexts.Payment.MethodLabels["bank"];

    [GeneratedRegex(@"<form\b[^>]*>([\s\S]*?)</form>")] private static partial Regex Forms();
    [GeneratedRegex(@"<input\b([^>]*)>")] private static partial Regex Inputs();

    private static string? Attr(string attrs, string name) => Regex.Match(attrs, $@"\b{name}=""([^""]*)""") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static List<Dictionary<string, string>> FormFields(string html) => Forms().Matches(html).Select(f => Inputs().Matches(f.Groups[1].Value)
        .Select(i => (Name: Attr(i.Groups[1].Value, "name"), Value: Attr(i.Groups[1].Value, "value") ?? ""))
        .Where(x => x.Name is not null && x.Name != "__RequestVerificationToken").ToDictionary(x => x.Name!, x => x.Value)).ToList();

    private static string Part(string html, string title)
    {
        var at = html.IndexOf($">{title}</h2>", StringComparison.Ordinal);
        if (at < 0) return "";
        var next = html.IndexOf("<h2", at + title.Length, StringComparison.Ordinal);
        return next < 0 ? html[at..] : html[at..next];
    }

    private static List<string[]> Cells(string html) => Regex.Matches(Regex.Match(html, @"<tbody>([\s\S]*?)</tbody>").Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr>")
        .Select(r => Regex.Matches(r.Groups[1].Value, @"<td\b[^>]*>([\s\S]*?)</td>").Select(c => Html.Text(c.Groups[1].Value)).ToArray()).ToList();

    /// <summary>The body rows of the table whose head contains the heading.</summary>
    internal static List<string[]> TableWith(string html, string heading)
    {
        var table = Regex.Matches(html, @"<table\b[^>]*>([\s\S]*?)</table>").Select(t => t.Groups[1].Value)
            .FirstOrDefault(t => Html.Text(Regex.Match(t, @"<thead>([\s\S]*?)</thead>").Groups[1].Value).Contains(heading, StringComparison.Ordinal));
        Assert.True(table is not null, $"no table with {heading}");
        return Cells(table!);
    }

    private static List<string> Alerts(string html, string kind) => Regex.Matches(html, $@"<div class=""alert {kind}"">([\s\S]*?)</div>").Select(m => Html.Text(m.Groups[1].Value)).ToList();

    internal static async Task<string> RawAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<CurrentUser> AdminAsync() => await W.UserAsync();

    /// <summary>An approved policy of the client in the currency, its instalments (day after B, amount); the instalment ids in order.</summary>
    private async Task<(World.Policy P, List<Guid> Inst)> PolicyAsync(CurrentUser admin, Guid holder, string currency, params (int Day, string Amount)[] schedule)
    {
        var p = await W.PendingPolicyAsync(holder, B, currency, currency == "GEL" ? 1m : 2.7m, schedule.Sum(s => M(s.Amount)) is var sum && sum > 0 ? sum : 1m, [.. schedule.Select(s => (s.Day, M(s.Amount)))]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, p.Id.ToString())).Outcome);
        return (p, await W.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id }));
    }

    private async Task<Guid> PaymentAsync(Guid holder, string gel, DateOnly on, string method = "bank", string? reference = null, string kind = "receipt")
    {
        var id = Guid.NewGuid();
        await using var c = await app.Db.OpenAppAsync();
        await c.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference) values (@id, @on, @gel, @method, @kind, @holder, @reference)",
            new { id, on, gel = M(gel), method, kind, holder, reference });
        return id;
    }

    private async Task AllocateAsync(Guid payment, Guid installment, string amount, DateOnly on, string? gel = null, decimal rate = 1m)
    {
        await using var c = await app.Db.OpenAppAsync();
        await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@payment, @installment, @amount, @gel, @rate, @on)",
            new { payment, installment, amount = M(amount), gel = M(gel ?? amount), rate, on });
    }

    private async Task ReduceAsync(Guid installment, string change, DateOnly? on = null)
    {
        await using var c = await app.Db.OpenAppAsync();
        await c.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@installment, @on, @change, N'შემცირება')",
            new { installment, on = on ?? Today, change = M(change) });
    }

    // ---------------------------------------------------------------- the payment page's advances and overpayments

    [Fact]
    public async Task Advances_the_receipts_amount_and_what_is_left_of_it_numeric_18_2s_largest_included()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (s, sInst) = await PolicyAsync(admin, holder, "GEL", (0, "150.00"));
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        var r = await PaymentAsync(holder, "9999999999999999.99", B, reference: "R-0907");
        var r2 = await PaymentAsync(holder, "90071992547408.02", B, reference: "R-0908");
        await AllocateAsync(r2, sInst[0], "0.01", B);
        var (acc, _) = await app.SignedInAsync("accountant");
        var page = await RawAsync(acc, $"/policies/{t.Id}/payment");
        Assert.Equal([$"{Dmy(B)}|{Bank}|9,999,999,999,999,999.99|9,999,999,999,999,999.99|R-0907", $"{Dmy(B)}|{Bank}|90,071,992,547,408.02|90,071,992,547,408.01|R-0908"],
            Cells(Part(page, BillingTexts.Payment.Advances.Title)).Select(c => string.Join("|", c.Take(5))));
        Assert.Equal([new Dictionary<string, string> { ["policy_id"] = t.Id.ToString(), ["payment_id"] = r.ToString() }, new Dictionary<string, string> { ["policy_id"] = t.Id.ToString(), ["payment_id"] = r2.ToString() }],
            FormFields(Part(page, BillingTexts.Payment.Advances.Title)));
        _ = s;
    }

    [Theory]
    [InlineData("1234.5")]
    [InlineData("1234.50")]
    public async Task Ordinary_advances_look_as_before_1234_5_and_1234_50_show_the_same(string amount)
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        await PaymentAsync(holder, amount, B);
        var (acc, _) = await app.SignedInAsync("accountant");
        Assert.Equal(["1,234.50", "1,234.50"], Cells(Part(await RawAsync(acc, $"/policies/{t.Id}/payment"), BillingTexts.Payment.Advances.Title))[0][2..4]);
    }

    [Fact]
    public async Task Overpayments_one_receipt_over_two_reduced_instalments_is_one_row_with_their_exact_sum_another_receipt_keeps_its_own()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (s, sInst) = await PolicyAsync(admin, holder, "GEL", (0, "45035996273704.00"), (1, "45035996273704.01"), (2, "0.01"));
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        var r = await PaymentAsync(holder, "90071992547408.01", B, reference: "R-0907");
        var r3 = await PaymentAsync(holder, "0.01", B, method: "cash", reference: "R-0909");
        await AllocateAsync(r, sInst[0], "45035996273704.00", B);
        await AllocateAsync(r, sInst[1], "45035996273704.01", B);
        await AllocateAsync(r3, sInst[2], "0.01", B);
        await ReduceAsync(sInst[0], "-45035996273704.00");
        await ReduceAsync(sInst[1], "-45035996273704.01");
        await ReduceAsync(sInst[2], "-0.01");
        var (acc, _) = await app.SignedInAsync("accountant");
        var page = await RawAsync(acc, $"/policies/{t.Id}/payment");
        var part = Part(page, BillingTexts.Payment.Advances.OverpaidTitle);
        Assert.Equal([$"{Dmy(B)}|{Bank}|90,071,992,547,408.01|90,071,992,547,408.01|{s.Number}", $"{Dmy(B)}|{BillingTexts.Payment.MethodLabels["cash"]}|0.01|0.01|{s.Number}"],
            Cells(part).Select(c => string.Join("|", c.Take(5))));
        Assert.Equal([r.ToString(), r3.ToString()], FormFields(part).Select(f => f["payment_id"]));
        Assert.Contains($"{BillingTexts.Payment.OutstandingNow}: 100.00 GEL", WebUtility.HtmlDecode(page));
        Assert.DoesNotMatch(@"<button[^>]*disabled", part);
    }

    [Fact]
    public async Task The_largest_overpayment_pair_on_one_receipt_reaches_the_page_to_the_tetri_and_the_page_only_reads()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (s, sInst) = await PolicyAsync(admin, holder, "GEL", (0, "90071992547408.01"), (1, "0.01"));
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        var r = await PaymentAsync(holder, "90071992547408.02", B, reference: "R-0907");
        await AllocateAsync(r, sInst[0], "90071992547408.01", B);
        await AllocateAsync(r, sInst[1], "0.01", B);
        await ReduceAsync(sInst[0], "-90071992547408.01");
        await ReduceAsync(sInst[1], "-0.01");
        var (acc, _) = await app.SignedInAsync("accountant");
        var ledger = await Gaps.LedgerAsync(W);
        var part = Part(await RawAsync(acc, $"/policies/{t.Id}/payment"), BillingTexts.Payment.Advances.OverpaidTitle);
        Assert.Equal([$"{Dmy(B)}|{Bank}|90,071,992,547,408.02|90,071,992,547,408.02|{s.Number}"], Cells(part).Select(c => string.Join("|", c.Take(5))));
        Assert.Equal(ledger, await Gaps.LedgerAsync(W));
    }

    // ---------------------------------------------------------------- the card's hints and notices

    [Fact]
    public async Task The_cards_advance_hint_adds_every_receipts_remainder_in_tetri_and_the_largest_advance_shows_whole()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        await PaymentAsync(holder, "90071992547408.01", B);
        await PaymentAsync(holder, "0.01", B);
        var (acc, _) = await app.SignedInAsync("accountant");
        var p = BillingTexts.Payment.Advances.CardNote;
        Assert.Equal([$"{p}: 90,071,992,547,408.02 ₾. {BillingTexts.Payment.Advances.Open}"], Alerts(await RawAsync(acc, $"/policies/{t.Id}"), "warn").Where(a => a.StartsWith(p, StringComparison.Ordinal)));
        var other = await W.ClientAsync();
        var (u, _) = await PolicyAsync(admin, other, "GEL", (0, "100.00"));
        await PaymentAsync(other, "9999999999999999.99", B);
        Assert.Equal([$"{p}: 9,999,999,999,999,999.99 ₾. {BillingTexts.Payment.Advances.Open}"], Alerts(await RawAsync(acc, $"/policies/{u.Id}"), "warn").Where(a => a.StartsWith(p, StringComparison.Ordinal)));
        // A viewer: no hints.
        var (viewer, _) = await app.SignedInAsync("viewer");
        Assert.DoesNotContain(Alerts(await RawAsync(viewer, $"/policies/{u.Id}"), "warn"), a => a.StartsWith(p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_cards_overpayment_hint_adds_the_exact_rows_and_the_real_transfers_answer_is_carried_to_the_card()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (_, sInst) = await PolicyAsync(admin, holder, "GEL", (0, "90071992547408.01"), (1, "0.01"));
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        var r = await PaymentAsync(holder, "90071992547408.02", B);
        await AllocateAsync(r, sInst[0], "90071992547408.01", B);
        await AllocateAsync(r, sInst[1], "0.01", B);
        await ReduceAsync(sInst[0], "-90071992547408.01");
        await ReduceAsync(sInst[1], "-0.01");
        var (acc, accId) = await app.SignedInAsync("accountant");
        var o = BillingTexts.Payment.Advances.CardOverpaid;
        Assert.Equal([$"{o}: 90,071,992,547,408.02 ₾. {BillingTexts.Payment.Advances.Open}"], Alerts(await RawAsync(acc, $"/policies/{t.Id}"), "warn").Where(a => a.StartsWith(o, StringComparison.Ordinal)));

        // The real transfer (S holding 90071992547408.01, reduced to nothing): released 90,071,992,547,408.01, 100.00 moved, the rest left.
        var other = await W.ClientAsync();
        var (_, oInst) = await PolicyAsync(admin, other, "GEL", (0, "90071992547408.01"));
        var (u, _) = await PolicyAsync(admin, other, "GEL", (0, "100.00"));
        var ro = await PaymentAsync(other, "90071992547408.01", B);
        await AllocateAsync(ro, oInst[0], "90071992547408.01", B);
        await ReduceAsync(oInst[0], "-90071992547408.01");
        var to = await PaymentActions.TransferOverpaymentAsync(W.App, W.Rates, await app.UserOfAsync(accId), u.Id.ToString(), ro.ToString());
        Assert.Equal($"/policies/{u.Id}?advance=transferred&gel=10000&left=9007199254730801&released=9007199254740801", to);
        Assert.Equal([$"{A.Transferred}: 100.00 ₾ ({A.Released}: 90,071,992,547,408.01 ₾). {A.Left}: 90,071,992,547,308.01 ₾"], Alerts(await RawAsync(acc, to), "ok"));
        _ = r;
    }

    [Fact]
    public async Task Notices_the_largest_amounts_shown_whole_the_real_allocations_answer_malformed_amounts_show_no_notice_and_give_no_form()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        var r = await PaymentAsync(holder, "150.00", B);
        var (acc, accId) = await app.SignedInAsync("accountant");
        Assert.Equal([$"{A.Transferred}: 9,999,999,999,999,999.99 ₾ ({A.Released}: 9,999,999,999,999,999.99 ₾)"],
            Alerts(await RawAsync(acc, $"/policies/{t.Id}?advance=transferred&gel=999999999999999999&left=0&released=999999999999999999"), "ok"));
        var plainForms = FormFields(await RawAsync(acc, $"/policies/{t.Id}"));
        foreach (var advance in new[] { "allocated", "transferred" })
            foreach (var v in new[] { "", "abc", "1.5", "1e3", "-5", "+5", "NaN", "Infinity", " 5", "0x10", new string('9', 31) })
                foreach (var key in new[] { "gel", "left", "released" })
                {
                    var q = new Dictionary<string, string> { ["advance"] = advance, ["gel"] = "10000", ["left"] = "5000", ["released"] = "10000" };
                    q[key] = v;
                    var html = await RawAsync(acc, $"/policies/{t.Id}?{string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}");
                    Assert.True(Alerts(html, "ok").Count == (advance == "allocated" && key == "released" ? 1 : 0), $"{advance} {key}={v}");
                    Assert.DoesNotMatch("NaN|Infinity", Html.Text(html));
                    Assert.Equal(plainForms, FormFields(html));
                }
        foreach (var advance in new[] { "allocated", "transferred" })
            Assert.Empty(Alerts(await RawAsync(acc, $"/policies/{t.Id}?advance={advance}&gel=10000&gel=20000&left=0&released=10000"), "ok"));
        Assert.Empty(Alerts(await RawAsync(acc, $"/policies/{t.Id}?advance=nonsense&gel=10000"), "ok"));
        // The real allocation's answer on the card: 100.00 allocated, 50.00 left.
        var to = await PaymentActions.AllocateReceiptAsync(W.App, W.Rates, await app.UserOfAsync(accId), t.Id.ToString(), r.ToString());
        Assert.Equal($"/policies/{t.Id}?advance=allocated&gel=10000&left=5000", to);
        Assert.Equal([$"{A.Allocated}: 100.00 ₾. {A.Left}: 50.00 ₾"], Alerts(await RawAsync(acc, to), "ok"));
        // A viewer: a notice in the address gives no form or right.
        var (viewer, _) = await app.SignedInAsync("viewer");
        Assert.Equal(FormFields(await RawAsync(viewer, $"/policies/{t.Id}")), FormFields(await RawAsync(viewer, $"/policies/{t.Id}?advance=transferred&gel=10000&left=0&released=10000")));
    }

    // ---------------------------------------------------------------- the card's requests, history and receivable

    [Fact]
    public async Task The_cards_refund_and_reversal_requests_the_largest_2_53s_neighbour_and_0_01_approve_forms_only_for_the_admin()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (t, _) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"));
        var r = await PaymentAsync(holder, "150.00", B);
        var accountant = await W.UserAsync("accountant");
        var approved = Guid.NewGuid();
        await W.App.AsUserAsync(accountant.Id, async tx =>
        {
            foreach (var (id, day, gel) in new[] { (Guid.NewGuid(), Today, 90071992547408.01m), (approved, Today, 9999999999999999.99m), (Guid.NewGuid(), Today, 0.01m) })
                await tx.ExecuteAsync("insert core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, payer_party_id) values (@id, @t, @day, @gel, N'GEL', 1, N'bank', @holder)",
                    new { id, t = t.Id, day, gel, holder });
            foreach (var (gel, reason) in new[] { (90071992547408.01m, "შეცდომით"), (9999999999999999.99m, "ორჯერ") })
                await tx.ExecuteAsync("insert core.receipt_reversal_request (receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason) values (@r, @t, @today, @gel, N'bank', @holder, @reason)",
                    new { r, t = t.Id, today = Today, gel, holder, reason });
            return 0;
        });
        // The second refund request carried out by the admin.
        await W.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("insert core.payment (received_on, amount_gel, method, kind, payer_party_id, refund_request_id, created_by) values (@today, -9999999999999999.99, N'bank', N'refund', @holder, @approved, @admin)",
            new { today = Today, holder, approved, admin = admin.Id }));
        var ledger = await Gaps.LedgerAsync(W);
        var (acc, _) = await app.SignedInAsync("accountant");
        var (adm, _) = await app.SignedInAsync("admin");
        foreach (var (client, forms) in new[] { (acc, 0), (adm, 4) })
        {
            var html = await RawAsync(client, $"/policies/{t.Id}");
            Assert.Equal(new[] { "0.01", "9,999,999,999,999,999.99", "90,071,992,547,408.01" }.Order(StringComparer.Ordinal),
                TableWith(html, BillingTexts.Payment.RefundRequests.RefundedOn).Select(c => c[1]).Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "90,071,992,547,408.01|შეცდომით", "9,999,999,999,999,999.99|ორჯერ" }.Order(StringComparer.Ordinal),
                TableWith(html, BillingTexts.Payment.ReversalRequests.Reason).Select(c => $"{c[1]}|{c[2]}").Order(StringComparer.Ordinal));
            Assert.Contains(BillingTexts.Payment.RefundRequests.Approved(await W.ScalarAsync<string>("select full_name from core.app_user where id = @id", new { id = admin.Id })), WebUtility.HtmlDecode(html));
            Assert.Equal(forms, FormFields(html).Count(f => f.ContainsKey("request_id")));
        }
        Assert.Equal(ledger, await Gaps.LedgerAsync(W));
    }

    [Fact]
    public async Task The_cards_payment_history_the_whole_receipt_and_the_part_on_this_policy_summed_in_tetri_another_policys_part_not_counted()
    {
        var admin = await AdminAsync();
        var holder = await W.ClientAsync();
        var (s, sInst) = await PolicyAsync(admin, holder, "GEL", (0, "150.00"));
        var (t, tInst) = await PolicyAsync(admin, holder, "GEL", (0, "100.00"), (1, "90071992547408.01"));
        var r2 = await PaymentAsync(holder, "90071992547408.02", B);
        var r3 = await PaymentAsync(holder, "9999999999999999.99", B.AddDays(1) <= Today ? B.AddDays(1) : B);
        await AllocateAsync(r2, tInst[0], "45035996273704.00", B);
        await AllocateAsync(r2, tInst[1], "45035996273704.02", B);
        await AllocateAsync(r2, tInst[1], "-0.01", Today);
        await AllocateAsync(r2, sInst[0], "0.01", B);
        await AllocateAsync(r3, tInst[1], "9999999999999999.99", B);
        var (acc, _) = await app.SignedInAsync("accountant");
        var rows = TableWith(await RawAsync(acc, $"/policies/{t.Id}"), BillingTexts.Payment.HerePolicy);
        var whole = rows.Single(x => x[4] == "90,071,992,547,408.02");
        Assert.Equal("90,071,992,547,408.01", whole[5]);
        Assert.Equal(B == Today ? Dmy(B) : $"{Dmy(B)}, {Dmy(Today)}", whole[1]);
        Assert.Equal("9,999,999,999,999,999.99", rows.Single(x => x[4] == "9,999,999,999,999,999.99")[5]);
        _ = s;
    }

    [Fact]
    public async Task The_cards_receivable_0_01_owed_with_its_overdue_mark_and_record_a_payment_0_01_beyond_with_the_refund_button_USD_at_todays_2_7_exactly()
    {
        var admin = await AdminAsync();
        var (acc, _) = await app.SignedInAsync("accountant");
        var fin = PolicyTexts.Finance.Receivable;
        string[] Summary(string html, string label) => Regex.Matches(html, @"<table\b[^>]*>([\s\S]*?)</table>").Select(t => Cells(t.Groups[1].Value))
            .Single(rows => rows.Any(r => r.Length > 0 && r[0] == fin)).SingleOrDefault(r => r[0] == label)?[1..] ?? [];
        // Owed .02, held .01, due a day before today: 0.01 owed, overdue, "record a payment" offered.
        var h1 = await W.ClientAsync();
        var (p1, i1) = await PolicyAsync(admin, h1, "GEL", (0, "90071992547408.02"));
        await AllocateAsync(await PaymentAsync(h1, "90071992547408.01", B), i1[0], "90071992547408.01", B);
        var card = await RawAsync(acc, $"/policies/{p1.Id}");
        Assert.Equal(["0.01"], Summary(card, fin));
        var schedule = TableWith(card, PolicyTexts.Fields.Outstanding)[0];
        Assert.Equal([Dmy(B), "90,071,992,547,408.02", "0.01"], schedule[..3]);
        Assert.Equal(B < Today ? PolicyTexts.Overdue : "", schedule[3]);
        Assert.Contains($">{BillingTexts.Payment.Record}</a>", card);
        // Owed .00, held .01: 0.01 paid beyond — the summary row and the refund button say 0.01.
        var h2 = await W.ClientAsync();
        var (p2, i2) = await PolicyAsync(admin, h2, "GEL", (0, "90071992547408.00"));
        await AllocateAsync(await PaymentAsync(h2, "90071992547408.01", B), i2[0], "90071992547408.01", B);
        card = await RawAsync(acc, $"/policies/{p2.Id}");
        Assert.Equal(["0.00"], Summary(card, fin));
        Assert.Equal(["0.01"], Summary(card, PolicyTexts.Cancel.Overpaid));
        Assert.Contains($"{BillingTexts.Payment.Refund}: 0.01 GEL", Html.Text(card));
        Assert.DoesNotContain($">{BillingTexts.Payment.Record}</a>", card);
        // USD revalued at today's rate: 33359997239780.01 × 2.7 = 90071992547406.027 → .03.
        await W.RateAsync("USD", Today, 2.7m);
        var (p3, _) = await PolicyAsync(admin, await W.ClientAsync(), "USD", (0, "33359997239780.01"));
        Assert.Equal(["33,359,997,239,780.01", "90,071,992,547,406.03"], Summary(await RawAsync(acc, $"/policies/{p3.Id}"), fin));
    }

    // ---------------------------------------------------------------- the payment page's debt and its buttons

    private static (string? Note, string Allocate, string Transfer) Owed(string html)
    {
        var note = Regex.Match(WebUtility.HtmlDecode(html), @"<p class=""note"">[^<]*?:\s*([-\d.]+) (GEL|USD|EUR)");
        string Button(string title) => Regex.Match(Part(html, title), @"<button class=""btn""([^>]*)>") is { Success: true } b ? (b.Groups[1].Value.Contains("disabled", StringComparison.Ordinal) ? "disabled" : "enabled") : "none";
        return (note.Success ? $"{note.Groups[1].Value} {note.Groups[2].Value}" : null, Button(BillingTexts.Payment.Advances.Title), Button(BillingTexts.Payment.Advances.OverpaidTitle));
    }

    [Fact]
    public async Task The_payment_pages_debt_90071992547408_00_and_0_01_owed_is_90071992547408_01_the_cards_figure_too()
    {
        var admin = await AdminAsync();
        var (t, _) = await PolicyAsync(admin, await W.ClientAsync(), "GEL", (0, "90071992547408.00"), (31, "0.01"));
        var (acc, _) = await app.SignedInAsync("accountant");
        Assert.Equal("90071992547408.01 GEL", Owed(await RawAsync(acc, $"/policies/{t.Id}/payment")).Note);
        Assert.Contains($"{PolicyTexts.Finance.Receivable} 90,071,992,547,408.01", Html.Text(await RawAsync(acc, $"/policies/{t.Id}")));
    }

    [Fact]
    public async Task The_payment_pages_debt_0_01_both_buttons_enabled_the_real_allocation_takes_it_then_0_00_both_disabled_the_same_by_the_real_transfer()
    {
        foreach (var byTransfer in new[] { false, true })
        {
            var admin = await AdminAsync();
            var holder = await W.ClientAsync();
            var (_, sInst) = await PolicyAsync(admin, holder, "GEL", (0, "150.00"));
            var (t, tInst) = await PolicyAsync(admin, holder, "GEL", (0, "90071992547408.02"));
            var r = await PaymentAsync(holder, "150.00", B);
            await AllocateAsync(r, sInst[0], "150.00", B);
            await ReduceAsync(sInst[0], "-50.00");
            await AllocateAsync(await PaymentAsync(holder, "90071992547408.01", B), tInst[0], "90071992547408.01", B);
            var r4 = await PaymentAsync(holder, "50.00", B);
            var (acc, accId) = await app.SignedInAsync("accountant");
            var page = await RawAsync(acc, $"/policies/{t.Id}/payment");
            Assert.Equal(("0.01 GEL", "enabled", "enabled"), Owed(page));
            var user = await app.UserOfAsync(accId);
            if (!byTransfer)
            {
                Assert.Equal(new Dictionary<string, string> { ["policy_id"] = t.Id.ToString(), ["payment_id"] = r4.ToString() }, FormFields(Part(page, BillingTexts.Payment.Advances.Title))[0]);
                Assert.Equal($"/policies/{t.Id}?advance=allocated&gel=1&left=4999", await PaymentActions.AllocateReceiptAsync(W.App, W.Rates, user, t.Id.ToString(), r4.ToString()));
                Assert.Equal(("0.00 GEL", "disabled", "disabled"), Owed(await RawAsync(acc, $"/policies/{t.Id}/payment")));
            }
            else
            {
                Assert.Equal(new Dictionary<string, string> { ["policy_id"] = t.Id.ToString(), ["payment_id"] = r.ToString() }, FormFields(Part(page, BillingTexts.Payment.Advances.OverpaidTitle))[0]);
                Assert.Equal($"/policies/{t.Id}?advance=transferred&gel=1&left=4999&released=5000", await PaymentActions.TransferOverpaymentAsync(W.App, W.Rates, user, t.Id.ToString(), r.ToString()));
                Assert.Equal(("0.00 GEL", "disabled", "none"), Owed(await RawAsync(acc, $"/policies/{t.Id}/payment")));
            }
        }
    }
}

/// <summary>
/// The card's stored money to the tetri (legacy payment-shared-page.test.ts, POLICY_CARD_STORED_MONEY_DISPLAY_DECIMAL_PRECISION_). It has
/// its own database: the reinsurance treaty it writes for class 5.1 would otherwise take a share of every policy the other
/// card tests approve, and two such treaties cede more than 100 %.
/// </summary>
public sealed class GapsCardStoredMoneyTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;
    private static DateOnly B => Gaps.MonthStart;
    private static DateOnly Today => World.Today;
    private async Task<CurrentUser> AdminAsync() => await W.UserAsync();
    private static List<string[]> TableWith(string html, string heading) => GapsCardDisplayTests.TableWith(html, heading);
    private static Task<string> RawAsync(HttpClient client, string url) => GapsCardDisplayTests.RawAsync(client, url);

    [Theory]
    [InlineData("GEL")]
    [InlineData("USD")]
    [InlineData("EUR")]
    public async Task The_cards_coverages_cessions_and_related_policy_to_the_tetri_the_largest_2_53s_neighbour_negative_zero_empty(string currency)
    {
        var admin = await AdminAsync();
        await W.RateAsync("USD", Today, 2.7m);
        await W.RateAsync("EUR", Today, 3.1234m);
        var holder = await W.ClientAsync();
        var t = await W.PendingPolicyAsync(holder, B, currency, currency == "GEL" ? 1m : 2.7m, premium: 1m, schedule: []);
        var (_, damage) = await W.CascoAsync();
        var treaty = await W.TreatyAsync();
        await using (var c = await app.Db.OpenAppAsync())
        {
            await c.ExecuteAsync("delete from core.policy_line where policy_id = @id", new { id = t.Id });
            var line = Guid.NewGuid();
            await c.ExecuteAsync("""
                insert core.policy_line (id, policy_id, class_code, sum_insured, deductible, premium, premium_gel, product_coverage_id)
                values (@line, @id, N'5.1', 9999999999999999.99, 90071992547408.01, 90071992547408.01, 9999999999999999.99, @damage);
                insert core.policy_line (policy_id, class_code, sum_insured, deductible, premium, premium_gel, product_coverage_id)
                values (@id, N'5.1', null, 0.00, -0.01, -1234567.89, @damage);
                """, new { line, id = t.Id, damage });
            // S, the related policy of the same contract: its premium is the sum of its lines, as stored.
            var s = Guid.NewGuid();
            await c.ExecuteAsync("""
                insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment, created_by)
                values (@s, @contract, @number, @b, @b, @to, N'pending_approval', @currency, 1, N'corporate', @admin);
                insert core.policy_line (policy_id, class_code, premium, premium_gel) values (@s, N'5.1', 90071992547408.01, 90071992547408.01);
                insert core.policy_line (policy_id, class_code, sum_insured, premium, premium_gel) values (@s, N'5.1', 1.00, 0.01, 0.01);
                """, new { s, contract = t.ContractId, number = "S-" + s.ToString("N")[..10], b = B, to = B.AddYears(1), currency, admin = admin.Id });
            // One cession of T's issue and its cancellation.
            var issue = Guid.NewGuid();
            await c.ExecuteAsync("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to)
                values (@issue, @line, N'issue', 90071992547408.01, 9999999999999999.99, @b, @b, @to)
                """, new { issue, line, b = B, to = B.AddYears(1) });
            // Its cancellation from the next day, and the cession mirrored on it (the database keeps a row's cessions within 100 %).
            var cancel = Guid.NewGuid();
            await c.ExecuteAsync("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id)
                values (@cancel, @line, N'cancellation', -90071992547408.01, -9999999999999999.99, @b, @b, @to, @issue)
                """, new { cancel, line, issue, b = B, to = B.AddYears(1) });
            var first = Guid.NewGuid();
            await c.ExecuteAsync("""
                insert core.premium_cession (id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel, booked_on, cover_from, cover_to)
                values (@first, @issue, @treaty, 50, 90071992547408.01, 9999999999999999.99, 0.01, 0.01, @b, @b, @to);
                insert core.premium_cession (premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel, booked_on, cover_from, cover_to, cancels_cession_id)
                values (@cancel, @treaty, 50, -90071992547408.01, -9999999999999999.99, -0.01, -0.01, @b, @b, @to, @first);
                """, new { first, issue, cancel, treaty, b = B, to = B.AddYears(1) });
        }
        var (acc, _) = await app.SignedInAsync("accountant");
        var ledger = await Gaps.LedgerAsync(W);
        var html = await RawAsync(acc, $"/policies/{t.Id}");
        Assert.Equal(new[] { "9,999,999,999,999,999.99|90,071,992,547,408.01|90,071,992,547,408.01|9,999,999,999,999,999.99", "|0.00|-0.01|-1,234,567.89" }.Order(StringComparer.Ordinal),
            TableWith(html, PolicyTexts.Fields.SumInsured).Select(r => string.Join("|", r[2..])).Order(StringComparer.Ordinal));
        // The card lists cessions by created_at; the two rows are written in one batch and may share it, so their order is not asserted.
        Assert.Equal(new[] { "90,071,992,547,408.01|9,999,999,999,999,999.99|0.01", "-90,071,992,547,408.01|-9,999,999,999,999,999.99|-0.01" }.Order(StringComparer.Ordinal),
            TableWith(html, ReinsuranceTexts.Cessions.CededPremiumGel).Select(r => string.Join("|", r[^3..])).Order(StringComparer.Ordinal));
        var related = Regex.Matches(Regex.Match(html, @"<ul class=""changes"">([\s\S]*?)</ul>").Groups[1].Value, @"<li>([\s\S]*?)</li>").Select(m => Html.Text(m.Groups[1].Value)).ToList();
        Assert.Contains($"· 90,071,992,547,408.02 {currency} ·", Assert.Single(related));
        Assert.Equal(ledger, await Gaps.LedgerAsync(W));
        // A viewer sees the same stored cells.
        var (viewer, _) = await app.SignedInAsync("viewer");
        Assert.Equal(TableWith(html, PolicyTexts.Fields.SumInsured).Select(r => string.Join("|", r)), TableWith(await RawAsync(viewer, $"/policies/{t.Id}"), PolicyTexts.Fields.SumInsured).Select(r => string.Join("|", r)));
    }
}
