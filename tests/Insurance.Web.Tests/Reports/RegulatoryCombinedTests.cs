using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Domain.Regulatory;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.Reports;
using RegulatoryTexts = Insurance.Application.Texts.RegulatoryTexts;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.Reports.RegulatoryCombinedBooks;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The old program's combined reporting check (Insurance-Software PR #363, scripts/integration/regulatory-combined.test.ts; Codex
/// INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019 and the follow-ups it names; INS-DOTNET-MSSQL-005): the supervisory forms
/// page /accounting/regulatory and its Form 1 and Form 6 downloads over HTTP, every reader real — the month report (also the opening
/// balances), the impairment report, the reinsurer's commission receivable, the profit and loss and Form 6's source — over six fictional
/// books written as rows on SQL Server (RegulatoryCombinedBooks). Every figure is the old test's, worked out by hand from the raw rows.
/// <para>What differs from the old test, and why. The old test answered every statement from raw rows in memory (a strict SQL stand-in,
/// a stubbed user, settings and rates); here the rows are real and the National Bank is refused and counted (no request is asserted
/// where the old test asserted no socket). The old test computed its opening balances from the same rows with the real month report,
/// as here, so no opening figure differs. The old log of which reader answered each statement is not ported: the page and the file call
/// the readers through RegulatoryForms.MonthAsync, whose order RiCommission/IncomeStatement tests hold. The old controls that patched a
/// copy of a reader or of the route cannot be imported in C#: a control that changed one raw row is the same book with that row changed
/// (RegulatoryCombinedBooks' options); a control that broke a rule in a copy of the reader works the wrong rule's figure out from the same
/// rows and fills it into the real Form 1 (RegulatoryForms.Form1), showing the expected figures tell the rules apart, as
/// IncomeStatementReaderTests does. The route's two parameters are the real RegulatoryForms.Form1 called without one or the other.
/// The old control that recorded L1's 13 claim on PA's 5.1 line is adapted: the schema allows one claim per loss event and line, so the
/// 13 claim's reserve and payment are K1's (the refusal itself is shown). The old late-read failure section is
/// IncomeStatementPageTests.A_late_read_… (already ported).</para>
/// </summary>
public sealed class RegulatoryCombinedTests
{
    private const string Page = "/accounting/regulatory?month=2026-10";
    private const string Sheet6 = "გადამზღვევლები და ბროკერები";
    private const string Unplaced = "ეს თანხები პროგრამაშია, მაგრამ ფორმის არცერთ სტრიქონში არ შევიდა";
    private const string P = IncomeStatement.PnlSheet;

    private static int R(string code, string part) => IncomeStatement.PnlRow(code) + IncomeStatement.PnlPartOffset[part];

    private static string? V(TemplateWorkbook wb, string sheet, string cell) => ReservesPlacement.Shown(wb.ValueOf(sheet, cell));

    /// <summary>The cells' values as text, an empty cell "(empty)".</summary>
    private static string[] Vs(TemplateWorkbook wb, string sheet, params string[] cells) => cells.Select(c => V(wb, sheet, c) ?? "(empty)").ToArray();

    private static async Task<TemplateWorkbook> Form1Async(HttpClient client, string month = "2026-10")
    {
        var r = await client.GetAsync($"/accounting/regulatory/form1/xlsx?month={month}");
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return TemplateWorkbook.Open(await r.Content.ReadAsByteArrayAsync());
    }

    private static async Task<TemplateWorkbook> Form6Async(HttpClient client)
    {
        var r = await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=2026-Q4");
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return TemplateWorkbook.Open(await r.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The value in the page's row of the label (the row "label | value").</summary>
    private static string Row(string html, string label)
    {
        var m = Regex.Match(html, $"<td>{Regex.Escape(label)}</td>\\s*<td class=\"num\">([^<]*)</td>");
        Assert.True(m.Success, $"no row \"{label}\"");
        return m.Groups[1].Value.Trim();
    }

    private static Task<IncomeStatementSource> ReadAsync(ReportWorld w, string to) =>
        w.App.ReadTxAsync(tx => IncomeStatementReader.ReadAsync(tx, w.Rates().ValueAsync, D(to)));

    /// <summary>The month's figures as the page and the file read them (the stored rates only).</summary>
    private static Task<(Insurance.Application.Services.PremiumReport.Report Report, ImpairmentReport.ReportExact Impairment, IReadOnlyDictionary<string, string> Settings, BigInteger RiCommission, IncomeStatementSource Income)> MonthAsync(ReportWorld w, string month = "2026-10") =>
        w.App.ReadTxAsync(tx => RegulatoryForms.MonthAsync(tx, w.Rates().ValueAsync, month));

    private static async Task<string> ClassNameAsync(ReportWorld w, string code) =>
        await w.App.ReadAsync(c => c.ExecuteScalarAsync<string>("select name_ka from core.insurance_class where code = @code", new { code })) ?? "";

    private static async Task<List<PolicyCard.LedgerRow>> LedgerAsync(ReportWorld w, Guid policy) =>
        (await w.App.ReadAsync(c => c.QueryAsync<(Guid Id, Guid? CancelsId, decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)>("""
            select t.id, t.cancels_transaction_id, t.amount, t.booked_on, t.cover_from, t.cover_to
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policy order by t.created_at, t.id
            """, new { policy }))).Select(r => new PolicyCard.LedgerRow(r.Id, r.CancelsId, r.Amount, r.BookedOn, r.CoverFrom, r.CoverTo)).ToList();

    // ================================================================ the first book: one book, every real reader (31.10.2026; form 6 at 31.12.2026)

    [Fact]
    public async Task Book1_form_1_N1_7_N1_4_with_the_commission_receivable_IS_IS_COM_N2_6_N1_8_the_balance_and_PnC_NL_each_figure_as_worked_out_by_hand()
    {
        var book = await Book1Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await book.W.FingerprintAsync();
        var wb = await Form1Async(client);
        // N1-7 at 31.10.2026: UPR — PA 5.1 730.00 less 304 days (608.00) = 122.00 and 13 73.00 × 61/365 = 12.20; PB 365.00 less 245 days =
        // 120.00; PC at its fixed GEL 270.00 less 123 days (27000 × 123/365 = 9098.63 → 90.99) = 179.01: 433.21. RBNS 40.00 + 10.00 = 50.00.
        // IBNR 5 % of the year's net: 5.1 (730.00 + 270.00 − 219.00) × 5 % = 39.05; 13 (73.00 + 365.00 − 365.00) × 5 % = 3.65: 42.70. The
        // reinsurer's UPR: CA 219.00 × 61/365 = 36.60, CB 365.00 × 120/365 = 120.00: 156.60. No claim ceded: 0.
        Assert.Equal(["433.21", "50", "42.7", "156.6", "0"], Vs(wb, "BS-R", "E10", "E11", "E12", "E16", "E17"));
        // N1-4: unpaid PA 803.00 − 400.00 = 403.00, PB 365.00, PC 100.00 USD at 31.10's 2.75 = 275.00: 1043.00, all the client's. Impairment
        // by age (bands to 31.01, 30.04, 31.07): PA earned 2.20 a day — 68.20 / 195.80 / 202.40 / 202.40, paid 400 → unpaid 202.40 (0 %) +
        // 66.40 (10 % → 6.64); PB 61 / 92 / 92 unpaid → 9.20 + 18.30 = 27.50; PC in USD: earned by 31.07 10000 × 31/365 → 8.49, by 31.10
        // 3369.86 → 33.70, so 8.49 (10 % → 0.85) + 25.21 (0 %): 0.85 USD at 2.75 = 2.3375 → 2.34. Together 6.64 + 27.50 + 2.34 = 36.48.
        // 00140: QS-2's commission 73.00 (CB, 100 % ceded) less 20.00 settled = 53.00; QS-1's 30 % is not wholly ceded.
        Assert.Equal(["1043", "36.48", "53"], Vs(wb, "BS-IR & RR", "E10", "E11", "E26"));
        // P&C(NL) part I: 5.1 corporate PA 730.00 (1), retail PC 270.00 (1, at its fixed rate); 13 corporate PA 73.00 (1), retail PB 365.00
        // (1): 1438.00, 4 policies. Part II: 5.1 CA 219.00 (PA) + XL-1's 365.00 × 304/365 = 304.00 = 523.00; 13 CB 365.00: 888.00. Part III:
        // 5.1 corporate 60.00, 13 corporate 20.00 — loss L1 on two classes, two cases (owner).
        Assert.Equal(["1438", "4", "888", "80", "2", "0"], Vs(wb, P, "L45", "K45", "I91", "L139", "K139", "K186"));
        // IS: written 1438; ceded 888; ΔUPR 433.21 − 0; Δ the reinsurer's 156.60 − 0; earned 1438 − 888 − 433.21 + 156.60 = 273.39. Paid 80;
        // Δ(RBNS + IBNR) 92.70 − 0; recovered 10.00; incurred 80 + 92.70 − 10 = 162.70. 00500: 36.48 − 0.
        Assert.Equal(["1438", "888", "433.21", "156.6", "273.39", "80", "92.7", "10", "162.7", "36.48"], Vs(wb, "IS", "E9", "E10", "E11", "E12", "E13", "E14", "E16", "E18", "E19", "E67"));
        // IS-COM: reinsurance commission 21.90 + 73.00 = 94.90, deferred at 31.10 CA 21.90 × 61/365 = 3.66 + CB 73.00 × 120/365 = 24.00 =
        // 27.66, net 67.24; the agent's 10 % of PA's 730.00 and 73.00 = 80.30, deferred 7300 × 61/365 = 12.20 + 730 × 61/365 = 1.22 = 13.42,
        // net 66.88; together 67.24 − 66.88 = 0.36.
        Assert.Equal(["94.9", "27.66", "67.24", "80.3", "13.42", "66.88", "0.36"], Vs(wb, "IS-COM", "E11", "E12", "E13", "E23", "E24", "E25", "E26"));
        // N2-6 00160: the bank's 18.25 in full — accrued in the period, not deferred (owner, 21.10.2026).
        Assert.Equal("18.25", V(wb, "IS-Ex.S & Ex.Ad", "E31"));
        // N1-8 and the balance: the agent's 13.42 (00130), the reinsurance commission's 27.66 (00270).
        Assert.Equal(["13.42", "27.66"], Vs(wb, "BS-DC", "E12", "E16"));
        Assert.Equal(["13.42", "27.66"], Vs(wb, "BS", "E21", "E37"));
        // The insurance result 273.39 − 162.70 + 0.36 = 111.05; before tax 111.05 − 18.25 (N2-6) − 36.48 (00500) = 56.32.
        Assert.Equal(["111.05", "56.32"], Vs(wb, "IS", "E22", "E72"));
        Assert.Empty(wb.PrecisionProblems());
        // Read only, every rate from the book: no National Bank request.
        Assert.Equal(before, await book.W.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book1_the_page_shows_the_same_figures_from_the_same_book()
    {
        var book = await Book1Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var raw = await ReportSite.PageAsync(client, Page);
        var html = ReportSite.Decode(raw);
        Assert.Equal("273.39", Row(html, RegulatoryTexts.IsLines["E13"]));
        Assert.Equal("162.70", Row(html, RegulatoryTexts.IsLines["E19"]));
        Assert.Equal("36.48", Row(html, RegulatoryTexts.IsLines["E67"]));
        Assert.Equal("0.36", Row(html, RegulatoryTexts.ComLines["E26"]));
        Assert.Equal("18.25", Row(html, RegulatoryTexts.OtherExpensesLines["E31"]));
        Assert.Equal("13.42", Row(html, $"00130 {RegulatoryTexts.BalanceLines["00130"]}"));
        Assert.Equal("27.66", Row(html, $"00270 {RegulatoryTexts.BalanceLines["00270"]}"));
        // Two cases (loss × class), one loss.
        Assert.Contains(RegulatoryTexts.ClaimCountCheck(2, 1), html);
        // Nothing of this book is unplaced, so the page shows no unplaced list.
        Assert.DoesNotContain(Unplaced, html);
        Assert.Contains("href=\"/accounting/regulatory/form1/xlsx?month=2026-10\"", raw);
        Assert.Contains("href=\"/accounting/regulatory/form6/xlsx?quarter=2026-Q4\"", raw);
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book1_form_6_from_the_same_book_the_treaties_in_force_at_31_12_2026_by_class_reinsurer_and_number()
    {
        var book = await Book1Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var wb = await Form6Async(client);
        var n51 = await ClassNameAsync(book.W, "5.1");
        var n13 = await ClassNameAsync(book.W, "13");
        Assert.Equal([$"05.1 {n51}", "Re One (გამოგონილი)", "კვოტური", "01.01.2026 – 31.12.2026", "A", "AM Best", "DE", "Broker One (გამოგონილი)", "GB"],
            Vs(wb, Sheet6, "A9", "B9", "C9", "D9", "E9", "F9", "G9", "H9", "I9"));
        Assert.Equal(["Re One (გამოგონილი)", "ზარალის ექსცედენტი (XL)"], Vs(wb, Sheet6, "B10", "C10"));
        Assert.Equal([$"13 {n13}", "Re One (გამოგონილი)", "კვოტური"], Vs(wb, Sheet6, "A14", "B14", "C14"));
        Assert.Null(V(wb, Sheet6, "B11"));
    }

    // ---------------------------------------------------------------- the first book's controls: each fails on its own figure

    [Fact]
    public async Task Book1_control_the_two_parameters_each_on_its_own_without_the_receivable_only_00140_is_lost_without_the_profit_and_loss_only_it()
    {
        var book = await Book1Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        // The baseline: the real download gives both.
        var same = await Form1Async(client);
        Assert.Equal(["53", "273.39"], [V(same, "BS-IR & RR", "E26") ?? "(empty)", V(same, "IS", "E13") ?? "(empty)"]);
        var (report, impairment, settings, riCommission, income) = await MonthAsync(book.W);
        var both = RegulatoryForms.Form1(report, impairment, settings, riCommission, income);
        Assert.Equal(["53", "273.39"], [V(both, "BS-IR & RR", "E26") ?? "(empty)", V(both, "IS", "E13") ?? "(empty)"]);
        // The commission receivable left out, the profit and loss given: 00140 lost, the profit and loss still 273.39.
        var noReceivable = RegulatoryForms.Form1(report, impairment, settings, null, income);
        Assert.NotEqual("53", V(noReceivable, "BS-IR & RR", "E26"));
        Assert.Equal("273.39", V(noReceivable, "IS", "E13"));
        // The profit and loss left out, the receivable given: earned lost, 00140 still 53.00.
        var noIncome = RegulatoryForms.Form1(report, impairment, settings, riCommission, null);
        Assert.NotEqual("273.39", V(noIncome, "IS", "E13"));
        Assert.Equal("53", V(noIncome, "BS-IR & RR", "E26"));
    }

    [Fact]
    public async Task Book1_control_the_banks_intermediary_recorded_as_an_agent_N2_6_00160_0_the_agents_commission_98_55_deferred_19_42()
    {
        var book = await Book1Async(new Book1Options(BankAsAgent: true));
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var wb = await Form1Async(client);
        // N2-6 0 (not 18.25); the agents' commission 80.30 + 18.25 = 98.55 (not 80.30), deferred 13.42 + PB's 18.25 × 120/365 = 6.00 = 19.42.
        Assert.Equal("0", V(wb, "IS-Ex.S & Ex.Ad", "E31"));
        Assert.Equal(["98.55", "19.42", "19.42"], [V(wb, "IS-COM", "E23") ?? "", V(wb, "IS-COM", "E24") ?? "", V(wb, "BS-DC", "E12") ?? ""]);
    }

    [Fact]
    public async Task Book1_control_PAs_400_allocated_on_05_11_2026_instead_N1_4_owes_1443_not_1043_the_impairment_and_00500_follow()
    {
        var book = await Book1Async(new Book1Options(AllocatedOn: "2026-11-05"));
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var wb = await Form1Async(client);
        Assert.Equal("1443", V(wb, "BS-IR & RR", "E10"));
        // PA unpaid earned 668.80: 202.40 (0 %), 202.40 (10 % → 20.24), 195.80 (30 % → 58.74), 68.20 (50 % → 34.10) = 113.08; with PB's 27.50
        // and PC's 2.34: 142.92.
        Assert.Equal("142.92", V(wb, "BS-IR & RR", "E11"));
        Assert.NotEqual("36.48", V(wb, "IS", "E67"));
    }

    [Fact]
    public async Task Book1_control_the_NBG_rate_on_31_10_2026_2_80_instead_PC_owed_280_N1_4_1048_its_written_premium_still_270_at_its_fixed_rate()
    {
        var book = await Book1Async(new Book1Options(UsdOctober: 2.80m));
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var wb = await Form1Async(client);
        Assert.Equal("1048", V(wb, "BS-IR & RR", "E10"));
        Assert.NotEqual("1043", V(wb, "BS-IR & RR", "E10"));
        Assert.Equal("1438", V(wb, P, "L45"));
    }

    [Fact]
    public async Task Book1_control_L1s_13_claim_on_PAs_5_1_line_instead_one_class_one_case_K139_1_not_2_a_second_claim_of_L1_on_that_line_is_refused()
    {
        // The schema refuses L1 a second claim on PA's 5.1 line (core.claim: one claim per loss event and line).
        var normal = await Book1Async();
        await using (normal.W)
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => SameEventClaimAsync(normal.W, normal.K1, normal.PolicyA, normal.LineA51, "GEL", "K-X"));
            Assert.Contains("claim_event_coverage", refused.Message);
        }
        // So the 13 claim's reserve and payment are K1's: L1 on 5.1 alone, one case; paid the same 80.00.
        var book = await Book1Async(new Book1Options(ThirteenOnFiveOne: true));
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var wb = await Form1Async(client);
        Assert.Equal("1", V(wb, P, "K139"));
        Assert.NotEqual("2", V(wb, P, "K139"));
        Assert.Equal("80", V(wb, P, "L139"));
    }

    // ================================================================ the second book: USD, ceded claims through the real claim shares

    [Fact]
    public async Task Book2_form_1_and_PnC_NL_each_figure_as_worked_out_by_hand_the_shares_their_rates_the_cancellation_the_opening_the_segment_as_issued()
    {
        var w = await Book2Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(w));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        var wb = await Form1Async(client);
        // N1-7 at 31.10.2026. UPR: QA's cover ended 01.07.2026 (0); QB 270.00 less 214 days (27000 × 214/365 = 15830.14 → 158.30) = 111.70.
        // The reinsurer's UPR 0. RBNS at 31.10's 2.75: KQ1 200 − 120 = 80.00 USD (the 02.11 payment after the day) → 220.00; KQ2 400 − 300
        // = 100.00 USD → 275.00: 495.00. IBNR: 2026's written −164.70 + 270.00 = 105.30, ceded −82.35, net 187.65 × 5 % = 9.3825 → 9.38. The
        // reinsurer's RBNS: KQ1 50 % of 80.00 = 40.00 USD → 110.00; KQ2 XL on the whole loss 400 − 100 = 300, less its paid share 200 =
        // 100.00 USD → 275.00: 385.00.
        Assert.Equal(["111.7", "495", "9.38", "0", "385"], Vs(wb, "BS-R", "E10", "E11", "E12", "E16", "E17"));
        // N1-4: QA 365 − 61 − 304 paid = 0; QB 100.00 USD at 2.75 = 275.00. QB's impairment in USD: earned by 30.04 822 (30 days), by 31.07
        // 3342 (122), by 31.10 5863 (214); unpaid 5863: 2521 (0 %), 2520 (10 % → 252), 822 (30 % → 247) = 4.99 USD at 2.75 → 13.72.
        Assert.Equal(["275", "13.72"], Vs(wb, "BS-IR & RR", "E10", "E11"));
        // Nothing wholly ceded: 00140 0.
        Assert.Contains(V(wb, "BS-IR & RR", "E26"), new string?[] { null, "0" });
        // Part I, 5.1 corporate (the policies' segment at issue, not the policyholder's current retail): QA's refund −164.70 (no policy) +
        // QB 270.00 (1) = 105.30; retail empty. Part II: QA's cession refund −82.35, no policy. Part III, corporate: KQ1 326.40 + KQ2 834.00
        // = 1160.40, two losses on one class each, two cases. Part IV: KQ1 March — 326.40 × 60.00/120.00 = 163.20 (the payment's rate);
        // KQ2 July — 200.00 USD at July's end 2.80 = 560.00: 723.20.
        Assert.Equal(["1", "105.3", "0", "0"], new[] { "E", "F", "G", "H" }.Select(c => V(wb, P, $"{c}{R("5.1", "written")}") ?? "(empty)"));
        Assert.Equal(["0", "-82.35"], new[] { "E", "I" }.Select(c => V(wb, P, $"{c}{R("5.1", "ceded")}") ?? "(empty)"));
        Assert.Equal(["2", "1160.4", "0", "0"], new[] { "E", "F", "G", "H" }.Select(c => V(wb, P, $"{c}{R("5.1", "paid")}") ?? "(empty)"));
        Assert.Equal(["723.2", "0"], new[] { "E", "G" }.Select(c => V(wb, P, $"{c}{R("5.1", "reinsurerPaid")}") ?? "(empty)"));
        Assert.Equal(["105.3", "-82.35", "1160.4", "2", "723.2"], Vs(wb, P, "L45", "I91", "L139", "K139", "K186"));
        // IS against the opening at 31.12.2025: UPR QA 985.50 less 184 days (496.80) = 488.70, the reinsurer's 492.75 less 184 days (248.40) =
        // 244.35; RBNS 0; IBNR 2025's net 985.50 − 492.75 = 492.75 × 5 % = 24.6375 → 24.64; impairment QA: 184 earned USD, nothing paid by
        // then — 92 (0 %) + 92 (10 % → 9.20 USD) at 2.65 = 24.38.
        // Earned 105.30 + 82.35 − (111.70 − 488.70) + (0 − 244.35) = 320.30. Incurred 1160.40 − 723.20 + (495.00 + 9.38 − 24.64) − 385.00 =
        // 531.94. 00500 13.72 − 24.38 = −10.66.
        Assert.Equal(["105.3", "-82.35", "-377", "-244.35", "320.3", "1160.4", "723.2", "479.74", "385", "0", "531.94", "-10.66"],
            Vs(wb, "IS", "E9", "E10", "E11", "E12", "E13", "E14", "E15", "E16", "E17", "E18", "E19", "E67"));
        // No commission of any kind: IS-COM 0, N2-6 0, the balance's 00130 / 00270 0. The result 320.30 − 531.94 = −211.64; before tax
        // −211.64 − (−10.66) = −200.98.
        Assert.Equal(["0", "0", "0", "0"], [V(wb, "IS-COM", "E26") ?? "(empty)", V(wb, "IS-Ex.S & Ex.Ad", "E31") ?? "(empty)", V(wb, "BS", "E21") ?? "(empty)", V(wb, "BS", "E37") ?? "(empty)"]);
        Assert.Equal(["-211.64", "-200.98"], Vs(wb, "IS", "E22", "E72"));
        Assert.Empty(wb.PrecisionProblems());
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book2_the_page_shows_the_same_form_6_lists_QS_U_and_XL_U_from_the_same_book()
    {
        var w = await Book2Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(w));
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, Page));
        Assert.Equal("320.30", Row(html, RegulatoryTexts.IsLines["E13"]));
        Assert.Equal("531.94", Row(html, RegulatoryTexts.IsLines["E19"]));
        Assert.Equal("723.20", Row(html, RegulatoryTexts.IsLines["E15"]));
        Assert.Contains(RegulatoryTexts.ClaimCountCheck(2, 2), html);
        var wb = await Form6Async(client);
        Assert.Equal([$"05.1 {await ClassNameAsync(w, "5.1")}", "Re USD (გამოგონილი)", "კვოტური", "CH", "Re USD (გამოგონილი)", "ზარალის ექსცედენტი (XL)"],
            Vs(wb, Sheet6, "A9", "B9", "C9", "G9", "B10", "C10"));
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book2_controls_the_XL_shares_month_end_rate_a_payment_over_the_day_the_quota_shares_percent_each_moves_its_own_figures()
    {
        // July's rate 2.90: the XL share 200.00 USD → 580.00, part IV 743.20 (the RBNS at October's rate unchanged).
        await using (var site = await ReportSite.CreateAsync(() => Book2Async(new Book2Options(UsdJuly: 2.90m))))
        {
            var wb = await Form1Async((await site.SignedInAsync("accountant")).Client);
            Assert.Equal(["743.2", "385"], [V(wb, P, "K186") ?? "", V(wb, "BS-R", "E17") ?? ""]);
        }
        // KQ1's 02.11 payment (and its reserve change) on 30.10 instead: paid 1160.40 + 27.50 = 1187.90; RBNS 70.00 USD → 192.50 + 275.00 =
        // 467.50; the quota share's paid share of 130.00 is 65.00 USD — March 163.20, October 5.00 USD of 10.00 paid (27.50) → 13.75: 736.95.
        await using (var site = await ReportSite.CreateAsync(() => Book2Async(new Book2Options(Kq1LastPaidOn: "2026-10-30"))))
        {
            var wb = await Form1Async((await site.SignedInAsync("accountant")).Client);
            Assert.Equal(["1187.9", "467.5", "736.95"], [V(wb, "IS", "E14") ?? "", V(wb, "BS-R", "E11") ?? "", V(wb, P, "K186") ?? ""]);
        }
        // QS-U's share of KQ1 40 % instead of 50 %: paid share 48.00 USD → 326.40 × 48/120 = 130.56, part IV 690.56; the reinsurer's RBNS
        // 32.00 USD → 88.00 + 275.00 = 363.00.
        await using (var site = await ReportSite.CreateAsync(() => Book2Async(new Book2Options(Kq1Share: 40m))))
        {
            var wb = await Form1Async((await site.SignedInAsync("accountant")).Client);
            Assert.Equal(["690.56", "363"], [V(wb, P, "K186") ?? "", V(wb, "BS-R", "E17") ?? ""]);
        }
    }

    // ================================================================ the third book: the prior year's ceded claims carried over (USD)

    [Fact]
    public async Task Book3_N1_7_PnC_NL_and_IS_the_opening_shares_taken_off_2025s_paid_share_not_counted_again()
    {
        var book = await Book3Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await book.W.FingerprintAsync();
        var wb = await Form1Async(client);
        // Closing (31.10.2026, 2.75): RBNS Z1 300 − 100 − 80 = 120.00 USD → 330.00, Z2 400 − 250 − 100 = 50.00 USD → 137.50: 467.50. The
        // reinsurer's: Z1 50 % of 120 = 60.00 USD → 165.00; Z2 XL on the whole loss 350 + 50 = 400 → 300, less its paid share 350 − 100 =
        // 250: 50.00 USD → 137.50; together 302.50. UPR 0 (PZ's cover ended 01.03.2026); IBNR 0 (nothing written in 2026).
        Assert.Equal(["0", "467.5", "0", "0", "302.5"], Vs(wb, "BS-R", "E10", "E11", "E12", "E16", "E17"));
        // 2026 only: paid Z1 80 × 2.72 = 217.60 + Z2 100 × 2.73 = 273.00 = 490.60, two cases. The reinsurer's paid share of 2026 only: Z1's
        // cumulative share 90.00 USD by February less 50.00 at the opening = 40.00 → 217.60 × 40/80 = 108.80 (the payment's rate); Z2's
        // 250.00 USD by April less 150.00 at the opening = 100.00 at April's end 2.74 = 274.00: 382.80.
        Assert.Equal(["0", "0", "490.6", "2", "382.8"], Vs(wb, P, "L45", "I91", "L139", "K139", "K186"));
        // The opening (31.12.2025, 2.65): UPR PZ 985.50 less 306 days (826.20) = 159.30; IBNR 2025's 985.50 × 5 % = 49.275 → 49.28; RBNS Z1
        // 200.00 USD → 530.00 + Z2 150.00 USD → 397.50 = 927.50; the reinsurer's Z1 100.00 USD → 265.00 + Z2 (400 → 300, less 150 paid)
        // 150.00 USD → 397.50 = 662.50; no impairment (PZ paid in full).
        // IS: earned 0 − 0 − (0 − 159.30) + 0 = 159.30; incurred 490.60 − 382.80 + (467.50 − 927.50 − 49.28) − (302.50 − 662.50) = −41.48;
        // the result 159.30 + 41.48 = 200.78, before tax the same.
        Assert.Equal(["-159.3", "159.3", "490.6", "382.8", "-509.28", "-360", "-41.48", "200.78", "0", "200.78"],
            Vs(wb, "IS", "E11", "E13", "E14", "E15", "E16", "E17", "E19", "E22", "E67", "E72"));
        Assert.Empty(wb.PrecisionProblems());
        Assert.Equal(before, await book.W.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book3_the_page_shows_the_same()
    {
        var book = await Book3Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, Page));
        Assert.Equal("490.60", Row(html, RegulatoryTexts.IsLines["E14"]));
        Assert.Equal("382.80", Row(html, RegulatoryTexts.IsLines["E15"]));
        Assert.Equal("-41.48", Row(html, RegulatoryTexts.IsLines["E19"]));
        Assert.Equal("200.78", Row(html, RegulatoryTexts.IsLines["E22"]));
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book3_control_without_the_opening_shares_2025s_paid_share_counts_again_922_80_not_382_80()
    {
        var book = await Book3Async();
        await using var w = book.W;
        var s = await ReadAsync(w, "2026-10-31");
        var real = s.ReinsurerPaid.Aggregate(BigInteger.Zero, (x, i) => x + i.AmountGel);
        Assert.Equal(new BigInteger(38280), real);
        // Without the opening shares, January takes the whole cumulative share at 31.12.2025 as if paid in it, at January's 2.70 (no payment
        // in January, so at the month-end rate whatever the kind): Z1 50.00 USD → 135.00, Z2 150.00 USD → 405.00 — with February's 108.80 and
        // April's 274.00: 922.80.
        var opening = new List<BigInteger>();
        foreach (var claim in new[] { book.Z1, book.Z2 })
            opening.AddRange((await w.App.ReadTxAsync(tx => ReinsuranceStore.ClaimSharesExactAsync(tx, claim, D("2025-12-31")))).Select(x => x.InPaid));
        Assert.Equal([new BigInteger(5000), new BigInteger(15000)], opening);
        var wrong = real + opening.Aggregate(BigInteger.Zero, (x, share) => x + ReportMath.AtRateHalfUp(share, 2.70m));
        Assert.Equal(new BigInteger(92280), wrong);
        Assert.NotEqual(real, wrong);
    }

    [Fact]
    public async Task Book3_control_Z1s_2025_payment_dated_05_01_2026_instead_the_years_boundary_moves_it_into_2026()
    {
        await using var site = await ReportSite.CreateAsync(async () => (await Book3Async(new Book3Options(Z1FirstPaidOn: "2026-01-05"))).W);
        var wb = await Form1Async((await site.SignedInAsync("accountant")).Client);
        // 2026 paid 270.00 + 217.60 + 273.00 = 760.60; the opening RBNS Z1 300.00 USD → 795.00 + 397.50 = 1192.50, so Δ 467.50 − 1192.50 −
        // 49.28 = −774.28; the reinsurer's paid share of 2026: Z1 90.00 USD (none at the opening) — January 50.00 at its payment's 2.70 →
        // 135.00, February 40.00 → 108.80 — + Z2 274.00 = 517.80.
        Assert.Equal(["760.6", "-774.28", "517.8"], [V(wb, "IS", "E14") ?? "", V(wb, "IS", "E16") ?? "", V(wb, P, "K186") ?? ""]);
        Assert.Empty(site.Nbg.Requests);
    }

    // ================================================================ the fourth book: a bank's commission across the year's boundary

    private static readonly CommissionFlow OctoberBank = new(4928, 0, 0);

    /// <summary>October's intended form figures: N2-6 00160, its total and IS 00480, each KB's 49.28.</summary>
    private static string[] OctoberForm(TemplateWorkbook wb) => [V(wb, "IS-Ex.S & Ex.Ad", "E31") ?? "", V(wb, "IS-Ex.S & Ex.Ad", "E41") ?? "", V(wb, "IS", "E65") ?? ""];

    [Fact]
    public async Task Book4_the_real_reader_18_25_for_2025_then_49_28_in_January_and_in_October_85_78_in_November()
    {
        var book = await Book4Async();
        await using var w = book.W;
        // The boundary from its other side: 2025's year to date (its opening 31.12.2024) has KA's 18.25, in full.
        Assert.Equal(new CommissionFlow(1825, 0, 0), (await ReadAsync(w, "2025-12-31")).Commission.Bank);
        Assert.Equal(OctoberBank, (await ReadAsync(w, "2026-10-31")).Commission.Bank);
        Assert.Equal(OctoberBank, (await ReadAsync(w, "2026-01-31")).Commission.Bank);
        // KC booked on 01.11.2026: November's year to date takes its 730.00 × 5 % = 36.50 too — 49.28 + 36.50 = 85.78 (the reader does see
        // it; October leaves it out by its date only).
        Assert.Equal(new CommissionFlow(8578, 0, 0), (await ReadAsync(w, "2026-11-30")).Commission.Bank);
    }

    [Fact]
    public async Task Book4_form_1_of_October_N2_6_00160_49_28_not_67_53_once_in_IS_00480_and_the_profit_before_tax_the_agents_lines_and_N1_8_empty_of_it()
    {
        var book = await Book4Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await book.W.FingerprintAsync();
        var wb = await Form1Async(client);
        Assert.Equal(["49.28", "49.28", "49.28"], OctoberForm(wb));
        // IS by hand. Written 985.50 (KB; KA is 2025's, KC November's). UPR: the opening (31.12.2025) KA 365.00 × 181/365 = 181.00; the
        // closing (31.10.2026) KB 365.00 USD × 61/365 = 61.00 USD × 2.70 = 164.70 (KA's cover ended 01.07.2026): E11 164.70 − 181.00 =
        // −16.30, earned 985.50 + 16.30 = 1001.80. IBNR 5 %: the opening 365.00 → 18.25, the closing 985.50 → 49.275 → 49.28: E16 31.03,
        // incurred 31.03. No claim, no reinsurance, no agent or broker: the insurance result 1001.80 − 31.03 = 970.77; no impairment (all
        // written paid in full); before tax 970.77 − 49.28 (00480) = 921.49 — the bank's 49.28 taken once.
        Assert.Equal(["-16.3", "1001.8", "0", "31.03", "31.03", "970.77", "49.28", "0", "921.49"], Vs(wb, "IS", "E11", "E13", "E14", "E16", "E19", "E22", "E65", "E67", "E72"));
        Assert.Equal(["985.5", "0"], Vs(wb, P, "L45", "I91"));
        // N2-1 (IS-COM): no agent's or broker's commission; the bank is not there. N1-8: nothing deferred.
        Assert.Equal(["0", "0", "0", "0", "0", "0", "0"], new[] { "E19", "E20", "E21", "E23", "E24", "E25", "E26" }.Select(x => V(wb, "IS-COM", x) ?? "0"));
        Assert.Equal(["0", "0", "0"], new[] { "E11", "E12", "E13" }.Select(x => V(wb, "BS-DC", x) ?? "0"));
        Assert.Empty(wb.PrecisionProblems());
        Assert.Equal(before, await book.W.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book4_form_1_of_January_N2_6_00160_the_same_49_28_at_the_policys_fixed_2_70_while_Januarys_rate_is_2_90_before_tax_34_39()
    {
        var book = await Book4Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var wb = await Form1Async((await site.SignedInAsync("accountant")).Client, "2026-01");
        // The closing UPR (31.01.2026): KB 334.00 USD × 2.70 = 901.80 and KA, still running, 365.00 × 150/365 = 150.00 — 1051.80: E11
        // 1051.80 − 181.00 = 870.80, earned 985.50 − 870.80 = 114.70; IBNR 49.28 − 18.25 = 31.03; result 114.70 − 31.03 = 83.67; before
        // tax 83.67 − 49.28 = 34.39. (At January's 2.90 the bank's would be 18.25 × 2.90 = 52.93.)
        Assert.Equal(["49.28", "870.8", "114.7", "31.03", "83.67", "49.28", "34.39"],
            new[] { V(wb, "IS-Ex.S & Ex.Ad", "E31") }.Concat(new[] { "E11", "E13", "E16", "E22", "E65", "E72" }.Select(x => V(wb, "IS", x))).Select(x => x ?? "(empty)"));
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book4_the_page_of_October_shows_the_same_49_28()
    {
        var book = await Book4Async();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var html = ReportSite.Decode(await ReportSite.PageAsync((await site.SignedInAsync("accountant")).Client, Page));
        Assert.Equal("49.28", Row(html, RegulatoryTexts.OtherExpensesLines["E31"]));
        Assert.DoesNotContain(Unplaced, html);
    }

    [Fact]
    public async Task Book4_control_without_the_openings_written_taken_off_KAs_2025_18_25_counts_again_67_53_in_00160_both_assertions_fail()
    {
        var book = await Book4Async();
        await using var w = book.W;
        var (report, impairment, settings, riCommission, s) = await MonthAsync(w);
        Assert.Equal(OctoberBank, s.Commission.Bank);
        // KA's commission written by the opening (31.12.2025), by the policy card's rule: 18.25; KB's none yet.
        var ka = PolicyCard.ExactPremiumPosition(await LedgerAsync(w, book.Ka), D("2025-12-31"), "GEL", 1m, 500).PositionGel.Written;
        var kb = PolicyCard.ExactPremiumPosition(await LedgerAsync(w, book.Kb), D("2025-12-31"), "USD", 2.7m, 500).PositionGel.Written;
        Assert.Equal((new BigInteger(1825), BigInteger.Zero), (ka, kb));
        var wrong = s with { Commission = s.Commission with { Bank = s.Commission.Bank with { WrittenGel = s.Commission.Bank.WrittenGel + ka + kb } } };
        Assert.Equal(new BigInteger(6753), wrong.Commission.Bank.WrittenGel);
        var wb = RegulatoryForms.Form1(report, impairment, settings, riCommission, wrong);
        // 00160, its total and 00480 67.53; before tax 970.77 − 67.53 = 903.24 (the insurance result unchanged).
        Assert.Equal(["67.53", "67.53", "67.53", "970.77", "903.24"], [.. OctoberForm(wb), V(wb, "IS", "E22") ?? "", V(wb, "IS", "E72") ?? ""]);
        Assert.NotEqual(OctoberBank, wrong.Commission.Bank);
        Assert.NotEqual(["49.28", "49.28", "49.28"], OctoberForm(wb));
    }

    [Fact]
    public async Task Book4_control_at_the_days_rate_instead_of_the_policys_fixed_2_70_18_25_USD_at_3_00_54_75_in_00160_both_assertions_fail()
    {
        var book = await Book4Async();
        await using var w = book.W;
        var (report, impairment, settings, riCommission, s) = await MonthAsync(w);
        // KB's 18.25 USD at 31.10's 3.00 = 54.75 instead of 49.28 at its fixed 2.70; KA's none in the year whatever the rate.
        var kbLedger = await LedgerAsync(w, book.Kb);
        var at = (decimal rate) => PolicyCard.ExactPremiumPosition(kbLedger, D("2026-10-31"), "USD", rate, 500).PositionGel.Written
            - PolicyCard.ExactPremiumPosition(kbLedger, D("2025-12-31"), "USD", rate, 500).PositionGel.Written;
        Assert.Equal(new BigInteger(4928), at(2.7m));
        Assert.Equal(new BigInteger(5475), at(3.0m));
        var wrong = s with { Commission = s.Commission with { Bank = s.Commission.Bank with { WrittenGel = s.Commission.Bank.WrittenGel - at(2.7m) + at(3.0m) } } };
        var wb = RegulatoryForms.Form1(report, impairment, settings, riCommission, wrong);
        // Before tax 970.77 − 54.75 = 916.02.
        Assert.Equal(["54.75", "54.75", "54.75", "970.77", "916.02"], [.. OctoberForm(wb), V(wb, "IS", "E22") ?? "", V(wb, "IS", "E72") ?? ""]);
        Assert.NotEqual(OctoberBank, wrong.Commission.Bank);
        Assert.NotEqual(["49.28", "49.28", "49.28"], OctoberForm(wb));
    }

    // ================================================================ the fifth and sixth books: a quota share's deposit premium split by the ceded premium earned

    private static string[] Ceded(TemplateWorkbook wb) =>
        [V(wb, P, $"I{R("5.1", "ceded")}") ?? "", V(wb, P, $"I{R("13", "ceded")}") ?? "", V(wb, P, "I91") ?? "", V(wb, "IS", "E10") ?? ""];

    private static string Treaties(IncomeStatementSource s) => string.Join("; ", s.Treaties.Select(t =>
        $"{t.Number} {t.AmountGel} {{{string.Join(", ", (t.EarnedCeded ?? new Dictionary<string, BigInteger>()).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}: {e.Value}"))}}}"));

    // The deposit earned to 31.10: 365.00 × 304 / 365 = 304.00. Its key: 5.1 152.00, 13 61.50 (213.50). 30400 × 15200 / 21350 = 21643 rest
    // 1950; 30400 × 6150 / 21350 = 8756 rest 19400; the tetri left over to 13 (the larger rest): 216.43 / 87.57 — 304.00 exactly. Part II:
    // 5.1 182.50 + 216.43 = 398.93; 13 182.50 + 87.57 = 270.07; total 365.00 + 304.00 = 669.00 (IS 00020).
    private static readonly string[] Book5Ceded = ["398.93", "270.07", "669", "669"];

    [Fact]
    public async Task Book5_the_real_reader_form_1_and_the_page_the_deposits_304_split_216_43_87_57_nothing_apart()
    {
        var w = await DepositBookAsync(priorYear: false);
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(w));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        Assert.Equal("QS-Z 30400 {13: 6150, 5.1: 15200}", Treaties(await ReadAsync(w, "2026-10-31")));
        var wb = await Form1Async(client);
        Assert.Equal(Book5Ceded, Ceded(wb));
        Assert.Empty(wb.PrecisionProblems());
        Assert.DoesNotContain(Unplaced, ReportSite.Decode(await ReportSite.PageAsync(client, Page)));
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book5_control_the_key_by_the_ceded_premium_written_not_earned_152_152_and_the_assertion_fails()
    {
        await using var w = await DepositBookAsync(priorYear: false);
        var (report, impairment, settings, riCommission, s) = await MonthAsync(w);
        // The key by the premium ceded under QS-Z written (182.50 / 182.50), from the same rows.
        var written = (await w.App.ReadAsync(c => c.QueryAsync<(string ClassCode, decimal AmountGel)>("""
            select l.class_code, sum(c.amount_gel) from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id group by l.class_code
            """))).ToDictionary(r => r.ClassCode, r => ReportMath.Tetri(r.AmountGel), StringComparer.Ordinal);
        Assert.Equal((new BigInteger(18250), new BigInteger(18250)), (written["5.1"], written["13"]));
        var wrong = s with { Treaties = s.Treaties.Select(t => t with { EarnedCeded = written }).ToList() };
        // Written 182.50 / 182.50: 304.00 split 152.00 / 152.00 — 5.1 334.50, 13 334.50.
        var wb = RegulatoryForms.Form1(report, impairment, settings, riCommission, wrong);
        Assert.Equal(["334.5", "334.5", "669", "669"], Ceded(wb));
        Assert.NotEqual(Book5Ceded, Ceded(wb));
        Assert.Equal(Book5Ceded, Ceded(RegulatoryForms.Form1(report, impairment, settings, riCommission, s)));
    }

    // By hand. The deposit earned in 2026 to 31.10: 304 days of 1.00 = 304.00. The key: 5.1 — 182.50 earned by 31.10 (all) less the 92.00
    // earned by 31.12.2025 (18250 × 184 / 365 = 9200) = 90.50; 13 — 61.50 (18250 × 123 / 365 = 6150). 304.00 × 90.50 / 152.00 = 181.00 and
    // 304.00 × 61.50 / 152.00 = 123.00, exactly. Part II: 5.1 — no cession booked in 2026, 181.00; 13 — 182.50 + 123.00 = 305.50; total
    // 486.50 (IS 00020).
    private static readonly string[] Book6Ceded = ["181", "305.5", "486.5", "486.5"];

    [Fact]
    public async Task Book6_the_deposit_across_the_years_boundary_key_90_50_61_50_the_304_split_181_123_2025s_cession_read_its_182_50_not_ceded_again()
    {
        var w = await DepositBookAsync(priorYear: true);
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(w));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        var s = await ReadAsync(w, "2026-10-31");
        Assert.Equal("QS-Z 30400 {13: 6150, 5.1: 9050}", Treaties(s));
        // 2025's cession is read for the key (its 2026 earning, 90.50), yet the year's ceded premium is 2026's cession alone.
        var p5b = await w.App.ReadAsync(c => c.ExecuteScalarAsync<Guid>("select id from core.policy where number = N'P-5B'"));
        Assert.Equal([("13", p5b.ToString("D"), new BigInteger(18250))], s.Ceded.Select(c => (c.ClassCode, c.Id, c.AmountGel)));
        var wb = await Form1Async(client);
        Assert.Equal(Book6Ceded, Ceded(wb));
        // No policy counted by the deposit: part II's counts are 2026's cessions' policies (5.1 none, 13 one).
        Assert.Equal(["0", "1"], [V(wb, P, $"E{R("5.1", "ceded")}") ?? "(empty)", V(wb, P, $"E{R("13", "ceded")}") ?? "(empty)"]);
        Assert.Empty(wb.PrecisionProblems());
        Assert.DoesNotContain(Unplaced, ReportSite.Decode(await ReportSite.PageAsync(client, Page)));
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task Book6_control_the_key_earned_since_the_treaty_began_not_since_the_year_began_227_38_259_12_the_class_cells_fail_the_total_does_not()
    {
        await using var w = await DepositBookAsync(priorYear: true);
        var (report, impairment, settings, riCommission, s) = await MonthAsync(w);
        // The key earned to 31.10 since each cession began, from the same rows: 5.1 182.50, 13 61.50.
        var cessions = await w.App.ReadAsync(c => c.QueryAsync<(string ClassCode, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)>("""
            select l.class_code, c.amount_gel, c.booked_on, c.cover_from, c.cover_to from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id join core.policy_line l on l.id = t.policy_line_id
            """));
        var cumulative = cessions.GroupBy(c => c.ClassCode, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Aggregate(BigInteger.Zero, (x, c) => x + EarnedPremium.EarnedAsOfExact(new ExactPremiumTx(ReportMath.Tetri(c.AmountGel), c.BookedOn, c.CoverFrom, c.CoverTo), D("2026-10-31"))),
            StringComparer.Ordinal);
        Assert.Equal((new BigInteger(18250), new BigInteger(6150)), (cumulative["5.1"], cumulative["13"]));
        var wrong = s with { Treaties = s.Treaties.Select(t => t with { EarnedCeded = cumulative }).ToList() };
        var wb = RegulatoryForms.Form1(report, impairment, settings, riCommission, wrong);
        // Key 182.50 / 61.50 (244.00): 30400 × 18250 / 24400 = 22737 rest 17200, 30400 × 6150 / 24400 = 7662 rest 7200; the tetri left over to
        // 5.1: 227.38 / 76.62 — 13 182.50 + 76.62 = 259.12. The total is the same 486.50, so only the class cells tell.
        Assert.Equal(["227.38", "259.12", "486.5", "486.5"], Ceded(wb));
        Assert.NotEqual(Book6Ceded, Ceded(wb));
        Assert.Equal(Book6Ceded[2], V(wb, P, "I91"));
    }

    [Fact]
    public async Task Book6_the_fifth_book_still_splits_216_43_87_57()
    {
        await using var site = await ReportSite.CreateAsync(() => DepositBookAsync(priorYear: false));
        var wb = await Form1Async((await site.SignedInAsync("accountant")).Client);
        Assert.Equal(Book5Ceded, Ceded(wb));
        Assert.Empty(site.Nbg.Requests);
    }
}
