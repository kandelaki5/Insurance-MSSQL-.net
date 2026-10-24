using System.Net;
using System.Text.RegularExpressions;
using Insurance.Application.Texts;
using RegulatoryTexts = Insurance.Application.Texts.RegulatoryTexts;
using Insurance.Domain.Regulatory;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.FailureInjection;
using Insurance.Integration.Tests.Reports;
using Insurance.Web.Tests.FailureInjection;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// Form 1's profit and loss on the supervisory forms page and in the Form 1 file (old program PR #366, the two cases it added to
/// scripts/integration/regulatory-page.test.ts). The old cases answered the page's profit-and-loss read with a hand-written source;
/// here the page reads real rows on SQL Server: a one-policy year for the IS lines, and the reader test's fictional book
/// (IncomeStatementBook.BookAsync) for the owner's answers of 20.10.2026 — so the figures are the ones that book works out by
/// hand, not the old stand-ins. And Codex's follow-up on #363 (INS-FORM1-LATE-SOURCE-READ-FAILURE-RETRY-OFFLINE-20261021): a late read
/// failing comes out as that error on the page and on the file, never a zero or partial report, and the retry is whole.
/// </summary>
public sealed class IncomeStatementPageTests
{
    private const string Page = "/accounting/regulatory?month=2026-10";
    private const string File = "/accounting/regulatory/form1/xlsx?month=2026-10";

    /// <summary>The value in the page's row of the label (the row "label | value" of the profit-and-loss tables and the balance).</summary>
    private static string Row(string html, string label)
    {
        var m = Regex.Match(html, $"<td>{Regex.Escape(label)}</td>\\s*<td class=\"num\">([^<]*)</td>");
        Assert.True(m.Success, $"no row \"{label}\"");
        return m.Groups[1].Value.Trim();
    }

    private static async Task<string?> FileValueAsync(HttpClient client, string sheet, string cell)
    {
        var r = await client.GetAsync(File);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return ReservesPlacement.Shown(TemplateWorkbook.Open(await r.Content.ReadAsByteArrayAsync()).ValueOf(sheet, cell));
    }

    [Fact]
    public async Task The_years_figures_to_the_month_end_the_page_and_the_file_show_the_same_IS_lines()
    {
        // One 5.1 policy of 2026, 1 000.00 written on 01.01; at 31.10 earned round(100000 × 304 / 365) = 83288, so UPR 0 → 167.12 and
        // the template's 00050 = 1 000.00 − 0 − 167.12 + 0 = 832.88.
        await using var site = await ReportSite.CreateAsync();
        await site.World.PolicyAsync("P-IS", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 1000m, 1000m)]);
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, Page));
        Assert.Contains(RegulatoryTexts.IncomeTitle("01.01.2026", "31.10.2026"), html);
        Assert.Equal("1,000.00", Row(html, RegulatoryTexts.IsLines["E9"]));
        Assert.Equal("167.12", Row(html, RegulatoryTexts.IsLines["E11"]));
        Assert.Equal("832.88", Row(html, RegulatoryTexts.IsLines["E13"]));
        Assert.Equal("—", Row(html, RegulatoryTexts.IsLines["E20"]));
        Assert.Equal("832.88", await FileValueAsync(client, "IS", "E13"));
    }

    [Fact]
    public async Task The_owners_answers_a_loss_on_two_classes_two_cases_the_bank_in_N2_6_N1_8_and_the_balance_an_XL_of_two_classes_apart_with_why()
    {
        var book = await IncomeStatementBook.BookAsync();
        await using var site = await ReportSite.CreateAsync(() => Task.FromResult(book.W));
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, Page));
        // EV1 hit 5.1 and 13 (two cases), EV2 and EV3 one each: 4 cases, 3 losses (the reader test's book).
        Assert.Contains(RegulatoryTexts.ClaimCountCheck(4, 3), html);
        Assert.DoesNotContain("ფორმის ჯამი კი", html);
        // N2-6 00160: the bank's 30.00, accrued, not deferred. N1-8: brokers 67.68, agents with the employee 6.05, reinsurance 13.54;
        // the balance's 00130 and 00270 by the template's formulas from them.
        Assert.Equal("30.00", Row(html, RegulatoryTexts.OtherExpensesLines["E31"]));
        Assert.Equal(("67.68", "6.05", "13.54"), (Row(html, RegulatoryTexts.DeferredLines["E11"]), Row(html, RegulatoryTexts.DeferredLines["E12"]), Row(html, RegulatoryTexts.DeferredLines["E16"])));
        Assert.Equal(("73.73", "13.54"), (Row(html, $"00130 {RegulatoryTexts.BalanceLines["00130"]}"), Row(html, $"00270 {RegulatoryTexts.BalanceLines["00270"]}")));
        // Not placed, with why: XL-2 on 13 and 5.1 (each class is to have its own XL); the note; a bank's commission is never apart.
        Assert.Contains($"{RegulatoryTexts.IncomeParts["treaty"]} — XL-2 ({RegulatoryTexts.TreatyReasons["several-classes"]})", html);
        Assert.Contains(RegulatoryTexts.IncomeUnplacedNote, html);
        Assert.DoesNotContain("ბანკის გადავადებული", html);
        Assert.Equal("1926.07", await FileValueAsync(client, "IS", "E13"));
    }

    [Fact]
    public async Task A_late_read_the_intermediaries_ledger_failing_once_comes_out_as_that_error_on_the_page_and_the_file_never_a_partial_report_the_retry_is_whole()
    {
        var book = await IncomeStatementBook.BookAsync();
        await using var w = book.W;
        await using var site = FaultSite.Over(w.Database);
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        // The commission's ledger is the reader's last read, after the month report, the impairment, the reinsurer's commission and the
        // flows were read.
        var sentinel = new InjectedFault("INCOME_INTERMEDIARY_READ_UNAVAILABLE");
        site.Faults.FailOnce(Faults.Any("select pi.policy_id as PolicyId, i.kind as Kind"), sentinel);
        var body = await FaultSite.FailedAsync(await client.GetAsync(Page), sentinel);
        Assert.DoesNotContain(RegulatoryTexts.IsTitle, body);
        site.Faults.FailOnce(Faults.Any("select pi.policy_id as PolicyId, i.kind as Kind"), sentinel);
        await FaultSite.FailedAsync(await client.GetAsync(File), sentinel);
        Assert.Equal(before, await w.FingerprintAsync());
        // The retry on the same rows: the whole report, the bank's 30.00 among it.
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, Page));
        Assert.Equal("30.00", Row(html, RegulatoryTexts.OtherExpensesLines["E31"]));
        Assert.Equal("1926.07", await FileValueAsync(client, "IS", "E13"));
    }
}
