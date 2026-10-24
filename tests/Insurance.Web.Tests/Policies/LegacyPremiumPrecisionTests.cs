using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// Legacy scripts/premium-precision-db-check.ts with scripts/integration/precision-cases.ts (Codex INS-PREMIUM-PRECISION-DB-CHECKER;
/// "PREPARED, NOT RUN" in the old program — real evidence here for the first time): the money of the premium package exactly,
/// through the application itself on a real database — the real RecordPayment, AllocateReceipt, ReversePayment and
/// ApproveReceiptReversalRequest as an accountant and an admin, and the real payment page and policy card over HTTP; after
/// every action the rows it wrote are read back as stored and compared exactly. Cases A1–A3, B, C, D1, D2 with the same
/// amounts.
/// Adaptation: the legacy child process had a test-only clock, so the receipt day d0, the allocation/request day d1 and the
/// approval day d2 were three days. The new program has no clock to set: d0 is the 20th of the previous month (the
/// receipt's own date), and d1 = d2 = today (Tbilisi) — every allocation and every reversal is booked today. The USD rates
/// are the fictional 2.70 on d0 (the receipt's) and 2.80 today (the card's valuation; the legacy 2.90 of d1 did not appear
/// in any expected figure: the allocation takes the receipt day's 2.70).
/// </summary>
public sealed partial class LegacyPremiumPrecisionTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private const string Tag = "check-premium-precision";
    private static readonly DateOnly Today = World.Today;
    private static readonly DateOnly D0 = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1).AddDays(19);
    private World W => app.W;

    private static string Dmy(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    private static string Grouped(decimal amount) => Billing.GroupedTetri(Billing.Tetri(amount));
    private static string Bank => BillingTexts.Payment.MethodLabels["bank"];

    // ---------------------------------------------------------------- what the pages show (precision-cases.ts `shown`)

    [GeneratedRegex(@"<table\b[^>]*>(.*?)</table>", RegexOptions.Singleline)] private static partial Regex TableTag();
    [GeneratedRegex(@"<thead>(.*?)</thead>", RegexOptions.Singleline)] private static partial Regex Head();
    [GeneratedRegex(@"<tbody>(.*?)</tbody>", RegexOptions.Singleline)] private static partial Regex Body();
    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline)] private static partial Regex Row();
    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline)] private static partial Regex Cell();
    [GeneratedRegex(@"<p class=""note"">[^<]*?:\s*([-\d.]+ (?:GEL|USD|EUR))")] private static partial Regex Note();

    private static List<(string Head, List<List<string>> Rows)> Tables(string html) =>
        [.. TableTag().Matches(html).Select(t => (Html.Text(Head().Match(t.Groups[1].Value).Groups[1].Value),
            Row().Matches(Body().Match(t.Groups[1].Value).Groups[1].Value).Select(r => Cell().Matches(r.Groups[1].Value).Select(c => Html.Text(c.Groups[1].Value)).ToList()).ToList()))];

    private static string NoteOf(string html) => Note().Match(html) is { Success: true } m ? m.Groups[1].Value : "no note";

    private static List<string> ReceivableOf(string html) =>
        Tables(html).SelectMany(t => t.Rows).FirstOrDefault(r => r.Count > 0 && r[0] == PolicyTexts.Finance.Receivable)?.Skip(1).ToList() ?? ["no receivable row"];

    private static List<List<string>> HistoryOf(string html) =>
        [.. (Tables(html).FirstOrDefault(t => t.Head.Contains(BillingTexts.Payment.HerePolicy, StringComparison.Ordinal)).Rows ?? []).Select(r => r.Take(7).ToList())];

    private static string Part(string html, string title)
    {
        var at = html.IndexOf($">{title}</h2>", StringComparison.Ordinal);
        if (at < 0) return "";
        var next = html.IndexOf("<h2", at + title.Length, StringComparison.Ordinal);
        return next < 0 ? html[at..] : html[at..next];
    }

    private static List<List<string>> AdvancesOf(string html) =>
        Tables(Part(html, BillingTexts.Payment.Advances.Title)).FirstOrDefault().Rows?.Select(r => r.Take(4).ToList()).ToList() ?? [];

    private static void Same(IEnumerable<IEnumerable<string>> expected, IEnumerable<IEnumerable<string>> actual) =>
        Assert.Equal(expected.Select(r => string.Join(" | ", r)), actual.Select(r => string.Join(" | ", r)));

    // ---------------------------------------------------------------- fixtures: an active policy with its own client, written directly (the legacy setup)

    private sealed record Pol(Guid Id, string Number, Guid Holder, IReadOnlyList<Guid> Instalments);

    private async Task<Guid> ClientAsync() => await W.ClientAsync();

    private async Task<Pol> PolicyAsync(Guid holder, Guid admin, string currency, decimal premium, decimal premiumGel, params (DateOnly Due, decimal Amount)[] instalments)
    {
        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        var number = $"{Tag}-P-{policy.ToString("N")[..8]}";
        var ids = instalments.Select(_ => Guid.NewGuid()).ToList();
        await using var c = await app.Db.OpenAppAsync();
        await c.ExecuteAsync("""
            insert core.contract (id, number, signed_on, policyholder_id, created_by) values (@contract, @contractNo, @d0, @holder, @admin);
            insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment, approved_at, approved_by, created_by)
            values (@policy, @contract, @number, @d0, @d0, @to, N'active', @currency, @rate, N'corporate', sysdatetimeoffset(), @admin, @admin);
            insert core.policy_line (policy_id, class_code, premium, premium_gel) values (@policy, N'5.1', @premium, @premiumGel);
            """, new { contract, contractNo = $"{Tag}-C-{policy.ToString("N")[..8]}", d0 = D0, holder, admin, policy, number, to = D0.AddYears(1), currency, rate = currency == "GEL" ? 1m : 2.7m, premium, premiumGel });
        for (var i = 0; i < instalments.Length; i++)
            await c.ExecuteAsync("insert core.installment (id, policy_id, due_on, amount) values (@id, @policy, @due, @amount)", new { id = ids[i], policy, due = instalments[i].Due, amount = instalments[i].Amount });
        return new Pol(policy, number, holder, ids);
    }

    private async Task<(HttpClient Accountant, CurrentUser Acc, HttpClient AdminClient, CurrentUser Admin)> UsersAsync()
    {
        var (acc, accId) = await app.SignedInAsync("accountant");
        var (adm, admId) = await app.SignedInAsync("admin");
        return (acc, await app.UserOfAsync(accId), adm, await app.UserOfAsync(admId));
    }

    private Task<string> PaymentPage(HttpClient c, Guid policy) => PolicyWebApp.HtmlAsync(c, $"/policies/{policy}/payment");
    private Task<string> Card(HttpClient c, Guid policy) => PolicyWebApp.HtmlAsync(c, $"/policies/{policy}");

    private async Task<string?> ReceiveAsync(CurrentUser acc, Guid policy, string amount, DateOnly on) =>
        (await PaymentActions.RecordPaymentAsync(app.W.App, app.W.Rates, acc, World.Form(("policy_id", policy.ToString()), ("amount_gel", amount),
            ("received_on", on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("method", "bank"), ("reference", Tag)))).Redirect;

    private Task<List<(DateOnly ReceivedOn, decimal AmountGel, string Method, string Kind, Guid? Payer, string? Reference, Guid? By, Guid? Reverses, Guid? Request)>> PaymentsOfAsync(Guid holder) =>
        W.QueryAsync<(DateOnly, decimal, string, string, Guid?, string?, Guid?, Guid?, Guid?)>("""
            select received_on, amount_gel, method, kind, payer_party_id, reference, created_by, reverses_payment_id, reversal_request_id from core.payment
            where payer_party_id = @holder order by created_at, id
            """, new { holder });

    private Task<List<(Guid Installment, decimal Amount, decimal Gel, decimal Rate, DateOnly BookedOn, Guid? By)>> LinesOfAsync(Guid payment) =>
        W.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly, Guid?)>(
            "select installment_id, amount, amount_gel, rate, booked_on, created_by from core.payment_allocation where payment_id = @payment order by created_at, id", new { payment });

    private static string Text(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- A. GEL: the largest amounts, one tetri

    [Fact]
    public async Task A1_GEL_90071992547408_01_over_two_instalments()
    {
        var (accClient, acc, _, admin) = await UsersAsync();
        var p = await PolicyAsync(await ClientAsync(), admin.Id, "GEL", 90071992547408.01m, 90071992547408.01m, (D0, 90071992547408.00m), (Today, 0.01m));
        Assert.Equal("90071992547408.01 GEL", NoteOf(await PaymentPage(accClient, p.Id)));
        Assert.Equal($"/policies/{p.Id}?paid=0", await ReceiveAsync(acc, p.Id, "90071992547408.01", Today));
        var receipt = Assert.Single(await PaymentsOfAsync(p.Holder));
        Assert.Equal((Today, 90071992547408.01m, "bank", "receipt", (Guid?)p.Holder, Tag, (Guid?)acc.Id, (Guid?)null, (Guid?)null), receipt);
        var id = await W.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @h", new { h = p.Holder });
        Assert.Equal([(p.Instalments[0], 90071992547408.00m, 90071992547408.00m, 1m, Today, (Guid?)acc.Id), (p.Instalments[1], 0.01m, 0.01m, 1m, Today, (Guid?)acc.Id)],
            (await LinesOfAsync(id)).OrderByDescending(l => l.Amount));
        var page = await PaymentPage(accClient, p.Id);
        Assert.Equal("0.00 GEL", NoteOf(page));
        Assert.Empty(AdvancesOf(page));
        var card = await Card(accClient, p.Id);
        Assert.Equal(["0.00"], ReceivableOf(card));
        Same([[Dmy(Today), Dmy(Today), BillingTexts.Payment.Kinds["receipt"], Bank, "90,071,992,547,408.01", "90,071,992,547,408.01", Tag]], HistoryOf(card));
    }

    [Fact]
    public async Task A2_GEL_numeric_18_2_largest_9999999999999999_99()
    {
        const decimal max = 9999999999999999.99m;
        var (accClient, acc, _, admin) = await UsersAsync();
        var p = await PolicyAsync(await ClientAsync(), admin.Id, "GEL", max, max, (D0, max));
        Assert.Equal([Grouped(max)], ReceivableOf(await Card(accClient, p.Id)));
        Assert.Equal($"/policies/{p.Id}?paid=0", await ReceiveAsync(acc, p.Id, "9999999999999999.99", Today));
        Assert.Equal(max, Assert.Single(await PaymentsOfAsync(p.Holder)).AmountGel);
        var id = await W.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @h", new { h = p.Holder });
        Assert.Equal([(p.Instalments[0], max, max, 1m, Today, (Guid?)acc.Id)], await LinesOfAsync(id));
        Assert.Equal("0.00 GEL", NoteOf(await PaymentPage(accClient, p.Id)));
    }

    [Fact]
    public async Task A3_GEL_one_tetri_owed_after_100_then_0_02_settles_it_and_leaves_0_01_an_advance()
    {
        var (accClient, acc, _, admin) = await UsersAsync();
        var p = await PolicyAsync(await ClientAsync(), admin.Id, "GEL", 100.01m, 100.01m, (D0, 100m), (Today, 0.01m));
        Assert.Equal($"/policies/{p.Id}?paid=0", await ReceiveAsync(acc, p.Id, "100.00", Today));
        Assert.Equal("0.01 GEL", NoteOf(await PaymentPage(accClient, p.Id)));
        Assert.Equal($"/policies/{p.Id}?paid=1", await ReceiveAsync(acc, p.Id, "0.02", Today));
        Assert.Equal([100m, 0.02m], (await PaymentsOfAsync(p.Holder)).Select(r => r.AmountGel));
        Assert.Equal([(p.Instalments[1], 0.01m, 0.01m, 1m, Today, (Guid?)acc.Id)], await W.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly, Guid?)>(
            "select installment_id, amount, amount_gel, rate, booked_on, created_by from core.payment_allocation where installment_id = @i", new { i = p.Instalments[1] }));
        var page = await PaymentPage(accClient, p.Id);
        Assert.Equal("0.00 GEL", NoteOf(page));
        Same([[Dmy(Today), Bank, "0.02", "0.01"]], AdvancesOf(page));
    }

    // ---------------------------------------------------------------- B, C, D: a new receipt's advance taken by b; its reversal asked for and approved

    private async Task ArmFaultAsync()
    {
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("if object_id(N'dbo.test_fault') is null create table dbo.test_fault (table_name sysname not null)");
        await c.ExecuteAsync("grant select on dbo.test_fault to core_app");
        await c.ExecuteAsync("""
            create or alter trigger core.test_fault_mirror on core.payment_allocation after insert as
            begin
              set nocount on;
              if exists (select 1 from dbo.test_fault where table_name = N'after-first-mirror-line')
                 and exists (select 1 from inserted i join core.payment p on p.id = i.payment_id
                             where p.kind = N'reversal' and (select count(*) from core.payment_allocation a where a.payment_id = i.payment_id) >= 2)
                throw 50999, N'ChildFault: the check''s child fails after the mirror''s first line', 1;
            end
            """);
        await c.ExecuteAsync("insert dbo.test_fault (table_name) values (N'after-first-mirror-line')");
    }

    private async Task DisarmAsync()
    {
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("if object_id(N'dbo.test_fault') is not null delete from dbo.test_fault where table_name = N'after-first-mirror-line'");
    }

    [Theory]
    [InlineData("B", "GEL", "allocate-first", false)]
    [InlineData("C", "USD", "allocate-first", false)]
    [InlineData("D1", "GEL", "request-first", false)]
    [InlineData("D2", "GEL", "allocate-first", true)]
    public async Task Cross_policy_advance_and_the_receipts_reversal_above_the_threshold(string label, string currency, string order, bool fault)
    {
        _ = label;
        var (accClient, acc, _, admin) = await UsersAsync();
        if (currency == "USD")
        {
            await W.RateAsync("USD", D0, 2.7m);
            await W.RateAsync("USD", Today, 2.8m);
        }
        var usd = currency == "USD";
        var bAmount = usd ? 100.00m : 0.01m;
        var received = usd ? 10270.00m : 10000.01m;
        var left = usd ? 27000 : 1;
        // b's line: GEL 0.01 at 1; USD 270.00 GEL into USD at the receipt day's 2.70 = 100.00 USD.
        var bLine = usd ? (100.00m, 270.00m, 2.7m) : (0.01m, 0.01m, 1m);
        var bGel = usd ? 270.00m : 0.01m;
        var holder = await ClientAsync();
        var a = await PolicyAsync(holder, admin.Id, "GEL", 10000m, 10000m, (D0, 10000m));
        var b = await PolicyAsync(holder, admin.Id, currency, bAmount, bGel, (Today, bAmount));
        var back = (string o) => $"/policies/{a.Id}?reversal={o}";

        Assert.Equal($"/policies/{a.Id}?paid={left}", await ReceiveAsync(acc, a.Id, Text(received), D0));
        var receipt = await W.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @holder", new { holder });
        var receiptRow = Assert.Single(await PaymentsOfAsync(holder));
        var lineA = (a.Instalments[0], 10000m, 10000m, 1m, Today, (Guid?)acc.Id);
        Assert.Equal([lineA], await LinesOfAsync(receipt));
        Guid request = default;

        async Task Allocate()
        {
            var page = await PaymentPage(accClient, b.Id);
            Assert.Equal($"{Text(bAmount)} {currency}", NoteOf(page));
            Same([[Dmy(D0), Bank, Grouped(received), Grouped(left / 100m)]], AdvancesOf(page));
            // b's own advance form: its policy and receipt, as the page offers it.
            var form = Regex.Match(Part(page, BillingTexts.Payment.Advances.Title), "name=\"policy_id\" value=\"([^\"]+)\" />\\s*<input type=\"hidden\" name=\"payment_id\" value=\"([^\"]+)\"");
            Assert.Equal((b.Id.ToString(), receipt.ToString()), (form.Groups[1].Value, form.Groups[2].Value));
            Assert.Equal($"/policies/{b.Id}?advance=allocated&gel={left}&left=0", await PaymentActions.AllocateReceiptAsync(W.App, W.Rates, acc, form.Groups[1].Value, form.Groups[2].Value));
        }
        async Task Request()
        {
            Assert.Equal(back("requested"), await PaymentActions.ReversePaymentAsync(W.App, acc, receipt.ToString(), a.Id.ToString(), Tag));
            var q = Assert.Single(await W.QueryAsync<(Guid Id, Guid Receipt, Guid Policy, DateOnly On, decimal Gel, string Method, Guid? Payer, string Reason, Guid By)>(
                "select id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by from core.receipt_reversal_request where receipt_id = @receipt", new { receipt }));
            Assert.Equal((receipt, a.Id, Today, received, "bank", (Guid?)holder, Tag, acc.Id), (q.Receipt, q.Policy, q.On, q.Gel, q.Method, q.Payer, q.Reason, q.By));
            request = q.Id;
        }
        if (order == "allocate-first") { await Allocate(); await Request(); } else { await Request(); await Allocate(); }

        var lineB = (b.Instalments[0], bLine.Item1, bLine.Item2, bLine.Item3, Today, (Guid?)acc.Id);
        Assert.Equal([lineA, lineB], (await LinesOfAsync(receipt)).OrderByDescending(l => l.Item3));
        Assert.Equal([receiptRow], await PaymentsOfAsync(holder));
        var plan = await W.QueryAsync<(int, Guid, decimal, decimal, decimal)>(
            "select position, installment_id, amount, amount_gel, rate from core.receipt_reversal_request_line where request_id = @request order by position", new { request });
        Assert.Equal(order == "request-first" ? [(1, a.Instalments[0], 10000m, 10000m, 1m)] : [(1, a.Instalments[0], 10000m, 10000m, 1m), (2, b.Instalments[0], bLine.Item1, bLine.Item2, bLine.Item3)], plan);
        Assert.Equal(["0.00"], ReceivableOf(await Card(accClient, a.Id)));
        Assert.Equal(back("not-allowed"), await PaymentActions.ApproveReceiptReversalRequestAsync(W.App, acc, a.Id.ToString(), request.ToString()));

        var mirrors = () => W.QueryAsync<(Guid Id, DateOnly On, decimal Gel, string Method, string Kind, Guid? Payer, string? Reference, Guid? By, Guid? Request)>(
            "select id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by, reversal_request_id from core.payment where reverses_payment_id = @receipt", new { receipt });
        if (order == "request-first")
        {
            Assert.Equal(back("stale"), await PaymentActions.ApproveReceiptReversalRequestAsync(W.App, admin, a.Id.ToString(), request.ToString()));
            Assert.Empty(await mirrors());
            Assert.Equal(["0.00"], ReceivableOf(await Card(accClient, a.Id)));
            return;
        }
        if (fault)
        {
            await ArmFaultAsync();
            try
            {
                var e = await Record.ExceptionAsync(() => PaymentActions.ApproveReceiptReversalRequestAsync(W.App, admin, a.Id.ToString(), request.ToString()));
                Assert.Contains("ChildFault: the check's child fails after the mirror's first line", e?.Message);
            }
            finally
            {
                await DisarmAsync();
            }
            Assert.Empty(await mirrors());
            Assert.Equal([lineA, lineB], (await LinesOfAsync(receipt)).OrderByDescending(l => l.Item3));
            Assert.Equal(["0.00"], ReceivableOf(await Card(accClient, b.Id)));
        }
        Assert.Equal(back("approved"), await PaymentActions.ApproveReceiptReversalRequestAsync(W.App, admin, a.Id.ToString(), request.ToString()));
        var mirror = Assert.Single(await mirrors());
        Assert.Equal((Today, -received, "bank", "reversal", (Guid?)holder, Tag, (Guid?)admin.Id, (Guid?)request), (mirror.On, mirror.Gel, mirror.Method, mirror.Kind, mirror.Payer, mirror.Reference, mirror.By, mirror.Request));
        Assert.Equal([(a.Instalments[0], -10000m, -10000m, 1m, Today, (Guid?)admin.Id), (b.Instalments[0], -bLine.Item1, -bLine.Item2, bLine.Item3, Today, (Guid?)admin.Id)],
            (await LinesOfAsync(mirror.Id)).OrderBy(l => l.Item3));
        Assert.Equal(receiptRow, (await PaymentsOfAsync(holder))[0]);
        Assert.Equal(back("already"), await PaymentActions.ApproveReceiptReversalRequestAsync(W.App, admin, a.Id.ToString(), request.ToString()));
        Assert.Single(await mirrors());

        var cardA = await Card(accClient, a.Id);
        Assert.Equal(["10,000.00"], ReceivableOf(cardA));
        Same([[Dmy(D0), Dmy(Today), BillingTexts.Payment.Kinds["receipt"], Bank, Grouped(received), "10,000.00", Tag],
              [Dmy(Today), Dmy(Today), BillingTexts.Payment.Kinds["reversal"], Bank, Grouped(-received), "-10,000.00", Tag]], HistoryOf(cardA));
        var cardB = await Card(accClient, b.Id);
        // b's debt back, valued at its own day's rate (2.80 today): 100.00 USD = 280.00 GEL.
        Assert.Equal(usd ? ["100.00", "280.00"] : ["0.01"], ReceivableOf(cardB));
        Same([[Dmy(D0), Dmy(Today), BillingTexts.Payment.Kinds["receipt"], Bank, Grouped(received), Grouped(bGel), Tag],
              [Dmy(Today), Dmy(Today), BillingTexts.Payment.Kinds["reversal"], Bank, Grouped(-received), Grouped(-bGel), Tag]], HistoryOf(cardB));
        var pageB = await PaymentPage(accClient, b.Id);
        Assert.Equal($"{Text(bAmount)} {currency}", NoteOf(pageB));
        Assert.Empty(AdvancesOf(pageB));
        // The rows of the case in all: exactly its own (legacy caseCounts).
        Assert.Equal((2, 4, 1, 2), (await W.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder }),
            await W.ScalarAsync<int>("select count(*) from core.payment_allocation a join core.payment p on p.id = a.payment_id where p.payer_party_id = @holder", new { holder }),
            await W.ScalarAsync<int>("select count(*) from core.receipt_reversal_request where receipt_id = @receipt", new { receipt }),
            await W.ScalarAsync<int>("select count(*) from core.receipt_reversal_request_line where request_id = @request", new { request })));
    }
}
