using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The policy card and the payment page on a later day than the receipt (legacy fd-net-transfer-chain.test.ts "a new receipt,
/// its reversal asked for and approved by the financial director, on the card" and "its advance taken by a second policy",
/// precision-fd-panel-chain.test.ts "b's card"): the receipt and the request on 03.11.2026, the approval and the card on 04.11.2026,
/// the database's clock (DbOptions.Clock) standing on each day as the legacy holder.__today did. The card values a foreign debt
/// at its own day's rate (04.11's 2.8), never at the mirror's stored 2.7. Each test on a site of its own (its own database).
/// </summary>
public sealed partial class GapsLaterDayCardTests
{
    [GeneratedRegex(@"<table\b[^>]*>(.*?)</table>", RegexOptions.Singleline)] private static partial Regex TableTag();
    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)] private static partial Regex Row();
    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)] private static partial Regex Cell();
    [GeneratedRegex(@"<p class=""note"">[^<]*?:\s*([-\d.]+ (?:GEL|USD|EUR))")] private static partial Regex Note();

    private static List<List<string>> Rows(string html) =>
        [.. TableTag().Matches(html).SelectMany(t => Row().Matches(t.Groups[1].Value)).Select(r => Cell().Matches(r.Groups[1].Value).Select(c => Html.Text(c.Groups[1].Value)).ToList())];

    /// <summary>The card's receivable row as shown (in the policy's currency, and in GEL for a foreign one).</summary>
    private static List<string> ReceivableOf(string html) => Rows(html).FirstOrDefault(r => r.Count > 0 && r[0] == PolicyTexts.Finance.Receivable)?.Skip(1).ToList() ?? ["no receivable row"];

    private static string NoteOf(string html) => Note().Match(html) is { Success: true } m ? m.Groups[1].Value : "no note";

    private static async Task<T> OnSiteAsync<T>(Func<PolicyWebApp, Task<T>> body)
    {
        var app = new PolicyWebApp();
        await app.InitializeAsync();
        try { return await body(app); }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task USD_the_card_on_04_11_values_the_restored_5100_USD_at_04_11s_2_8_14280_GEL_not_the_mirrors_2_7() =>
        await OnSiteAsync(async app =>
        {
            var w = app.W;
            var (admin, accountant, p, _) = await LegacyDays.ChainAsync(w, "USD", "5000.00", "100.00");
            Assert.Equal($"/policies/{p.Id}?paid=0", (await w.PayAsync(accountant, p.Id, "10000.01", D(2026, 11, 3), reference: "R-USD")).Redirect);
            var r = await w.ScalarAsync<Guid>("select id from core.payment where reference = N'R-USD'");
            Assert.Equal($"/policies/{p.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, accountant, r.ToString(), p.Id.ToString(), "შეცდომით ჩაირიცხა"));
            var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @r", new { r });
            var (client, _) = await app.SignedInAsync("accountant");
            // 03.11, while it waits (the receipt still stands), by hand: 5 100.00 − 3 703.71 = 1 396.29 USD at 03.11's 2.7 = 3 769.98.
            Assert.Equal(["1,396.29", "3,769.98"], ReceivableOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{p.Id}")));
            On(w, D(2026, 11, 4));
            Assert.Equal($"/policies/{p.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), request.ToString()));
            Assert.Equal([(D(2026, 11, 3), -10000.01m)], await w.QueryAsync<(DateOnly, decimal)>("select received_on, amount_gel from core.payment where reverses_payment_id = @r", new { r }));
            // The card on 04.11 values the restored debt — 5 000.00 + 100.00 USD — at 04.11's 2.8: 14 280.00 GEL.
            Assert.Equal(["5,100.00", "14,280.00"], ReceivableOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{p.Id}")));
            return 0;
        });

    [Fact]
    public async Task GEL_90071992547408_01_the_card_and_the_payment_page_on_04_11_the_debt_back_to_the_tetri() =>
        await OnSiteAsync(async app =>
        {
            var w = app.W;
            var (admin, accountant, p, _) = await LegacyDays.ChainAsync(w, "GEL", "90071992547408.00", "0.01");
            Assert.Equal($"/policies/{p.Id}?paid=0", (await w.PayAsync(accountant, p.Id, "90071992547408.01", D(2026, 11, 3), reference: "R-NEW")).Redirect);
            var r = await w.ScalarAsync<Guid>("select id from core.payment where reference = N'R-NEW'");
            Assert.Equal($"/policies/{p.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, accountant, r.ToString(), p.Id.ToString(), "შეცდომით ჩაირიცხა"));
            var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @r", new { r });
            var (client, _) = await app.SignedInAsync("accountant");
            Assert.Equal(["0.00"], ReceivableOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{p.Id}")));
            // The admin approves the next day, 04.11; the card and the payment page that day.
            On(w, D(2026, 11, 4));
            Assert.Equal($"/policies/{p.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), request.ToString()));
            var card = await PolicyWebApp.HtmlAsync(client, $"/policies/{p.Id}");
            Assert.Equal(["90,071,992,547,408.01"], ReceivableOf(card));
            // The schedule: 01.11 overdue on 04.11, 01.12 not yet.
            var due = Rows(card).Where(x => x.Count >= 4 && (x[0] == "01.11.2026" || x[0] == "01.12.2026")).Select(x => string.Join(" | ", x.Take(4))).ToList();
            Assert.Equal([$"01.11.2026 | 90,071,992,547,408.00 | 90,071,992,547,408.00 | {PolicyTexts.Overdue}", "01.12.2026 | 0.01 | 0.01 | "], due);
            var payment = await PolicyWebApp.HtmlAsync(client, $"/policies/{p.Id}/payment");
            Assert.DoesNotContain($">{BillingTexts.Payment.Advances.Title}</h2>", payment);
            Assert.Contains("name=\"amount_gel\"", payment);
            Assert.Equal("90071992547408.01 GEL", NoteOf(payment));
            return 0;
        });

    [Fact]
    public async Task USD_cross_B_takes_270_at_20_10s_2_7_the_reversal_approved_04_11_A_owes_10000_and_Bs_card_values_USD_100_at_04_11s_2_8_280_not_270() =>
        await OnSiteAsync(async app =>
        {
            var w = app.W;
            var (admin, a, b, _, _, r, request) = await LegacyDays.CrossUsdAsync(w);
            On(w, D(2026, 11, 4));
            Assert.Equal($"/policies/{a.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, a.Id.ToString(), request.ToString()));
            Assert.Equal([(D(2026, 11, 3), -10270m)], await w.QueryAsync<(DateOnly, decimal)>("select received_on, amount_gel from core.payment where reverses_payment_id = @r", new { r }));
            var (client, _) = await app.SignedInAsync("accountant");
            // B's card values its restored USD 100.00 at its own day's rate (04.11, 2.8): 280.00 GEL — not the mirror's 270.00.
            Assert.Equal(["10,000.00"], ReceivableOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{a.Id}")));
            Assert.Equal(["100.00", "280.00"], ReceivableOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{b.Id}")));
            Assert.Equal("10000.00 GEL", NoteOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{a.Id}/payment")));
            Assert.Equal("100.00 USD", NoteOf(await PolicyWebApp.HtmlAsync(client, $"/policies/{b.Id}/payment")));
            return 0;
        });
}
