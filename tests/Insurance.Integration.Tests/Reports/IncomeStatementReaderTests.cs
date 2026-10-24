using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Domain.Regulatory;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.Reports.IncomeStatementBook;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// „ფორმა 1"'s profit and loss from real rows on SQL Server (old program PR #366, scripts/integration/income-statement-reader.test.ts;
/// Codex INS-INCOME-STATEMENT-REAL-READER-OFFLINE-20261020, with the owner's answers of 20.10.2026 and 21.10.2026): the real reader
/// IncomeStatementReader.ReadAsync over a fictional year written as rows through the application role (every guard of the schema
/// applies), then the real filler into the official N1 template. Every expected figure is worked out by hand in the comments.
/// <para>What differs from the old test, and why. The old test answered every statement of the reader from raw tables in memory and stood
/// in for the month report and the impairment report of the opening day, the rates and the reinsurer's claim shares. Here nothing is a
/// stand-in: the opening balances are the real month report of December 2025 and the real impairment report of 31.12.2025 over the same
/// rows, so they are not the old stand-ins (UPR 500.00, ceded 100.00, RBNS 80.00, IBNR 20.00, the reinsurer's RBNS 10.00, impairment
/// 5.50) but what these rows give, worked out by hand below (728.00, 218.40, 80.00, 25.55, 0, 0). The claim shares are the real
/// ReinsuranceStore.ClaimSharesExactAsync over two claim cessions: K3 a quota share's 30 % (30.00 USD of 100.00 USD paid), K4 an excess
/// of loss with priority 600.00 and limit 1 000.00 USD (400.00 USD of 1 000.00 USD paid) — the old stand-in shares, made by real rows.
/// The closing figures of the filled form are the real month report and impairment report of October 2026, not the old stand-in
/// (UPR 2 000.00 …), so IS's inputs differ from the old test's and are worked out here; P&amp;C(NL), IS-COM, N2-6 and N1-8 come from the
/// reader alone and are the old test's figures exactly. Rows the database refuses as the old raw rows had them were adapted: the cession
/// of PA's 2025 row is on QS-P (period 2025–2026, no premium of its own), as a cession needs a treaty; K2 is a second claim of event EV1
/// (core.claim allows one claim per event and line). The old negative controls (a patched copy of the reader) cannot be imported in C#:
/// each is a test below that works the wrong rule's figure out and shows the expected one tells them apart, and each was also run by
/// hand as a mutation of IncomeStatementReader.cs (STATUS/report).</para>
/// </summary>
public sealed class IncomeStatementReaderTests
{
    private static BigInteger T(string s)
    {
        var neg = s.StartsWith('-');
        var parts = s.TrimStart('-').Split('.');
        var t = BigInteger.Parse(parts[0] + (parts.Length > 1 ? parts[1] : "").PadRight(2, '0'), System.Globalization.CultureInfo.InvariantCulture);
        return neg ? -t : t;
    }


    private static Task<IncomeStatementSource> ReadAsync(ReportWorld w, string to, StoredRates? rates = null) =>
        w.App.ReadTxAsync(tx => IncomeStatementReader.ReadAsync(tx, (rates ?? w.Rates()).ValueAsync, D(to)));

    private static List<string> Items(Book b, IEnumerable<FlowItem> xs) =>
        xs.Select(x => $"{x.ClassCode} {x.Segment} {b.Of(x.Id)} {x.AmountGel} {(x.Counted ? "true" : "false")}").Order(StringComparer.Ordinal).ToList();
    private static List<string> Sorted(params string[] xs) => xs.Order(StringComparer.Ordinal).ToList();
    private static CommissionFlow Flow(string written, string opening, string closing) => new(T(written), T(opening), T(closing));

    // The year to 31.10.2026, worked out by hand (the old test's OCTOBER).
    // PA's cancellation (−610.00), no policy; PF on 5.1 365.00 and on 13 73.00; PB 2700.00; PC, the change, −270.00, a new policy; PE 600.00.
    // PA's issue (31.12.2025) and PD's (01.11.2026) are outside.
    private static readonly List<string> OctoberWritten = Sorted("5.1 corporate PA -61000 false", "5.1 corporate PF 36500 true", "5.1 retail PB 270000 true", "5.1 retail PC -27000 true", "13 corporate PF 7300 true", "13 corporate PE 60000 true");
    private static readonly List<string> OctoberCeded = Sorted("5.1 corporate PA -18300 false", "5.1 retail PB 81000 true");
    // EV1: 300.00 + 200.00 on 5.1 (the 2025 10.00 outside), 50.00 on 13; EV2 100.00 × 2.75 = 275.00; EV3 1000.00 × 2.70 = 2700.00.
    private static readonly List<string> OctoberPaid = Sorted("5.1 corporate EV1 50000 true", "13 corporate EV1 5000 true", "5.1 retail EV2 27500 true", "5.1 retail EV3 270000 true");
    // K3, proportional, at its payment's rate: 275.00 × 30.00 / 100.00 = 82.50; K4, XL, at July's end 2.80: 400.00 × 2.80 = 1120.00.
    private static readonly List<string> OctoberReinsurerPaid = Sorted("5.1 retail K3 8250 false", "5.1 retail K4 112000 false");
    // Booked: 81.00 − 18.33 = 62.67. Deferred at 31.12.2025: CA1 21.93 less 1 day of 365 (2193 / 365 = 6.01 → 0.06) = 21.87. At 31.10.2026:
    // CA1 earned 305 days (2193 × 305 / 365 = 1832.51 → 18.33, 3.60 deferred), CA2 mirrors CA1 from 01.03 (−3.60 deferred): 0 together;
    // CB1 81.00 less 304 days (8100 × 304 / 365 = 6746.30 → 67.46) = 13.54.
    private static readonly CommissionFlow OctoberRi = Flow("62.67", "21.87", "13.54");
    // PA's agent: 73.00 at 31.12.2025, 72.80 deferred (0.20 earned); the cancellation leaves 12.00 written, all earned by 31.10: booked in the
    // year −61.00, deferred 0. PE's employee 4 % of 600.00 = 24.00, earned 273 of 365 days (2400 × 273 / 365 = 1795.07 → 17.95): deferred
    // 6.05. Together: −37.00, 72.80 → 6.05. PB's broker: 15 % of 1000.00 USD = 150.00 USD at 2.70 = 405.00, earned 304 of 365 days (40500 ×
    // 304 / 365 = 33731.51 → 337.32): 67.68. PE's bank: 5 % of 600.00 = 30.00, all of it in the period — not deferred (owner, 21.10.2026).
    private static readonly IntermediaryCommission OctoberCommission = new(Flow("-37.00", "72.80", "6.05"), Flow("405.00", "0", "67.68"), Flow("30.00", "0", "0"));
    // Each month's share at its month's end: QS-D 0.10 a day × 304 = 30.40; XL-1 1.00 × 304 = 304.00; XL-2 2.00 USD a day: January 62 ×
    // 2.70, February 56 × 2.70, March 62 × 2.70, April 60 × 2.70, May 62 × 2.72, June 60 × 2.72, July 62 × 2.80, August 62 × 2.80, September
    // 60 × 2.80, October 62 × 2.75 = 1665.54. XL-0 (2025) and QS-P, QS-C, XL-C (no premium of their own) are not read. QS-D's key: its own
    // cession CB1 (810.00 for 2026), earned 304 of 365 days — 81000 × 304 / 365 = 67463.01 → 674.63 — on 5.1.
    private static readonly string[] OctoberTreaties = ["QS-D quota_share [5.1] 3040 {5.1: 67463}", "XL-1 excess_of_loss [5.1] 30400 -", "XL-2 excess_of_loss [13,5.1] 166554 -"];
    // At 31.12.2025, from the real December report and impairment report of these rows: UPR — PA's 730.00 less 1 day = 728.00; the
    // reinsurer's — CA1 219.00 less 1 day (219 / 365 = 0.60) = 218.40; RBNS — K1's 80.00; IBNR — 5 % of 2025's net written premium
    // (730.00 − 219.00 = 511.00): 25.55; the reinsurer's RBNS 0 (no ceded claim had a reserve); impairment 0 (no instalments).
    private static readonly Balances Opening = new("2025-12-31", T("728.00"), T("218.40"), T("80.00"), T("25.55"), 0, 0);

    private static string[] Treaties(IncomeStatementSource s) => s.Treaties.Select(t =>
        $"{t.Number} {t.Kind} [{string.Join(",", t.Classes)}] {t.AmountGel} {(t.EarnedCeded is null ? "-" : "{" + string.Join(", ", t.EarnedCeded.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}: {e.Value}")) + "}")}").ToArray();

    [Fact]
    public async Task The_year_to_31_October_every_figure_as_worked_out_by_hand_the_period_the_change_a_new_policy_a_cancellation_no_policy_the_loss_events_each_FX_rule()
    {
        var b = await BookAsync();
        await using var w = b.W;
        var before = await w.FingerprintAsync();
        var rates = w.Rates();
        var s = await ReadAsync(w, "2026-10-31", rates);
        Assert.Equal(("2026-01-01", "2026-10-31"), (s.From, s.To));
        Assert.Equal(OctoberWritten, Items(b, s.Written));
        Assert.Equal(OctoberCeded, Items(b, s.Ceded));
        Assert.Equal(OctoberPaid, Items(b, s.Paid));
        Assert.Equal(OctoberReinsurerPaid, Items(b, s.ReinsurerPaid));
        Assert.Equal(T("40.00"), s.RecoveredGel);
        Assert.Equal(OctoberRi, s.RiCommission);
        Assert.Equal(OctoberCommission, s.Commission);
        Assert.Equal(OctoberTreaties, Treaties(s));
        Assert.Equal(Opening, s.Opening);
        // The opening is exactly the real reports' of the previous year's last day (the reader reads them, not a figure of its own).
        var december = await w.MonthAsync("2025-12");
        var impairment = await w.ImpairmentExactAsync("2025-12-31");
        Assert.Equal(IncomeStatement.BalancesOf(RegulatoryForms.ToForms(december), RegulatoryForms.ToForms(impairment)), s.Opening);
        // Each month end's USD rate asked (XL-2, K3, K4); nothing written.
        foreach (var m in new[] { "2026-01-31", "2026-05-31", "2026-07-31", "2026-10-31" }) Assert.Contains(("USD", D(m)), rates.Asked);
        Assert.Equal(before, await w.FingerprintAsync());
    }

    [Fact]
    public async Task The_year_to_31_January_January_only_the_first_included_31_December_and_February_on_outside()
    {
        var b = await BookAsync();
        await using var w = b.W;
        var s = await ReadAsync(w, "2026-01-31");
        Assert.Equal(Sorted("5.1 corporate PF 36500 true", "5.1 retail PB 270000 true", "13 corporate PF 7300 true"), Items(b, s.Written));
        Assert.Equal(["5.1 retail PB 81000 true"], Items(b, s.Ceded));
        Assert.Empty(s.Paid);
        Assert.Empty(s.ReinsurerPaid);
        Assert.Equal(BigInteger.Zero, s.RecoveredGel);
        // CA1 earned 32 days (2193 × 32 / 365 = 192.26 → 192): 20.01 deferred; CB1 31 days (8100 × 31 / 365 = 687.95 → 688): 74.12 — 94.13.
        Assert.Equal(Flow("81.00", "21.87", "94.13"), s.RiCommission);
        // PA's agent: 7300 × 32 / 365 = 640 earned, 66.60 deferred; PB's broker 40500 × 31 / 365 = 3439.73 → 3440: 370.60; PE not yet.
        Assert.Equal(new IntermediaryCommission(Flow("0", "72.80", "66.60"), Flow("405.00", "0", "370.60"), Flow("0", "0", "0")), s.Commission);
        Assert.Equal([("QS-D", T("3.10")), ("XL-1", T("31.00")), ("XL-2", T("167.40"))], s.Treaties.Select(t => (t.Number, t.AmountGel)));
        // Not a month's end: refused.
        Assert.Contains("Not a month's end: 2026-01-30", (await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(w, "2026-01-30"))).Message);
    }

    /// <summary>
    /// The same rows in the official template with the real month report and impairment report of October 2026 (RegulatoryForms, as the page
    /// and the download read it). Closing, worked out by hand at 31.10.2026: UPR — 5.1: PB 2700.00 less 304 days (2248.77) = 451.23, PF 365.00
    /// less 290 = 75.00, PC −270.00 less 153 of 214 days (−193.04) = −76.96, PA's issue and cancellation 120.00 − 120.00 = 0: 449.27; 13: PF
    /// 73.00 less 290 = 15.00, PE 600.00 less 273 (448.77) = 151.23: 166.23 — 615.50. The reinsurer's UPR: CB1 810.00 less 674.63 = 135.37
    /// (CA1 and CA2 36.00 − 36.00). RBNS: K1 80.00 + K3 USD 50.00 × 2.75 = 137.50: 217.50; IBNR: 5 % of the year's net written premium,
    /// 5.1 (2185.00 − 627.00) × 5 % = 77.90, 13 673.00 × 5 % = 33.65: 111.55; the reinsurer's RBNS: K3's 30 % of USD 50.00 = 15.00 × 2.75 =
    /// 41.25. Impairment 0 (no instalments).
    /// </summary>
    [Fact]
    public async Task In_the_official_template_PnC_NL_IS_IS_COM_N2_6_N1_8_and_the_balances_deferred_commission_each_cell_as_worked_out_by_hand_what_no_row_takes_named()
    {
        var b = await BookAsync();
        await using var w = b.W;
        await using (var c = await w.Database.OpenAdminAsync())
            await c.ExecuteAsync("update core.setting set value = N'სატესტო კომპანია (გამოგონილი)' where [key] = N'company_name'");
        var (report, impairment, settings, riCommission, s) = await w.App.ReadTxAsync(tx => RegulatoryForms.MonthAsync(tx, w.Rates().ValueAsync, "2026-10"));
        var wb = RegulatoryForms.Form1(report, impairment, settings, riCommission, s);
        string? V(string sheet, string reference) => ReservesPlacement.Shown(wb.ValueOf(sheet, reference));
        const string P = IncomeStatement.PnlSheet;
        int Row(string code, string part) => IncomeStatement.PnlRow(code) + IncomeStatement.PnlPartOffset[part];
        // Part I, 5.1: corporate PF (PA's refund not counted): 1, 365.00 − 610.00 = −245.00; retail PB and PC: 2, 2430.00.
        Assert.Equal(["1", "-245", "2", "2430"], new[] { "E", "F", "G", "H" }.Select(col => V(P, $"{col}{Row("5.1", "written")}")));
        // 13: corporate PE and PF: 2, 673.00. Total 2858.00.
        Assert.Equal(["2", "673"], new[] { "E", "F" }.Select(col => V(P, $"{col}{Row("13", "written")}")));
        Assert.Equal("2858", V(P, "L45"));
        // Part II, 5.1: PB counted; −183.00 + 810.00 + XL-1's 304.00 + QS-D's 30.40 (all its earned ceded premium is on 5.1) = 961.40.
        Assert.Equal(["1", "961.4"], new[] { "E", "I" }.Select(col => V(P, $"{col}{Row("5.1", "ceded")}")));
        Assert.Equal("961.4", V(P, "I91"));
        // Part III, 5.1: corporate EV1 500.00; retail EV2 and EV3: 2, 2975.00. 13 corporate EV1 50.00. 3525.00; cases 4, losses 3.
        Assert.Equal(["1", "500", "2", "2975"], new[] { "E", "F", "G", "H" }.Select(col => V(P, $"{col}{Row("5.1", "paid")}")));
        Assert.Equal(["1", "50"], new[] { "E", "F" }.Select(col => V(P, $"{col}{Row("13", "paid")}")));
        Assert.Equal(["3525", "4"], new[] { V(P, "L139"), V(P, "K139") }.Select(x => x ?? "(empty)"));
        Assert.Equal((4, 3), IncomeStatement.ClaimCounts(s));
        // Part IV, 5.1 retail: 82.50 + 1120.00 = 1202.50.
        Assert.Equal(["1202.5", "1202.5"], new[] { V(P, $"G{Row("5.1", "reinsurerPaid")}"), V(P, "K186") }.Select(x => x ?? "(empty)"));
        // IS: 00030 615.50 − 728.00 = −112.50; 00040 135.37 − 218.40 = −83.03; earned 2858 − 961.40 + 112.50 − 83.03 = 1926.07; 00080 (217.50
        // + 111.55) − (80.00 + 25.55) = 223.50; 00090 41.25 − 0; 00100 40.00; incurred 3525 − 1202.50 + 223.50 − 41.25 − 40 = 2464.75; 00500 0.
        Assert.Equal(["-112.5", "-83.03", "1926.07", "223.5", "41.25", "40", "2464.75", "0"], new[] { "E11", "E12", "E13", "E16", "E17", "E18", "E19", "E67" }.Select(r => V("IS", r)));
        // IS-COM: reinsurance 62.67, change 13.54 − 21.87 = −8.33, net 71.00; brokers 405.00, 67.68, net 337.32; agents with the employee
        // −37.00, 6.05 − 72.80 = −66.75, net 29.75; together 71.00 − 337.32 − 29.75 = −296.07. The bank is not in it.
        Assert.Equal(["62.67", "-8.33", "71", "405", "67.68", "337.32", "-37", "-66.75", "29.75", "-296.07"],
            new[] { "E11", "E12", "E13", "E19", "E20", "E21", "E23", "E24", "E25", "E26" }.Select(r => V("IS-COM", r)));
        // N2-6 00160: the bank's 30.00, accrued in the period, not deferred.
        Assert.Equal("30", V("IS-Ex.S & Ex.Ad", "E31"));
        // N1-8 and the balance: brokers 67.68 + agents 6.05 = 73.73 (00130), reinsurance 13.54 (00270) — IS-COM's closing figures.
        Assert.Equal(["67.68", "6.05", "13.54"], new[] { "E11", "E12", "E16" }.Select(r => V("BS-DC", r)));
        Assert.Equal(["73.73", "13.54"], new[] { V("BS", "E21"), V("BS", "E37") }.Select(x => x ?? "(empty)"));
        Assert.Equal("ანგარიშგების პერიოდი: 01.01.2026 – 31.10.2026", V("IS", "B2"));
        Assert.Equal("სატესტო კომპანია (გამოგონილი)", V("P&C(NL)", "D1"));
        // Not placed: XL-2 1665.54 (two classes — each class is to have its own XL); the bank's commission is not, QS-D is split.
        Assert.Equal([new UnplacedIncomeRow("treaty", "XL-2", 0, T("1665.54"), "several-classes")], IncomeStatement.UnplacedIncome(s));
        Assert.Empty(wb.PrecisionProblems());
    }

    // ---------------------------------------------------------------- negative controls

    // The old test patched a copy of the reader with one rule broken and showed the figure that rule makes changes. C# cannot import a
    // patched copy at test time: each control below works out, from the same rows, the figure the wrong rule would give and shows it is not
    // the expected one — so the expected figures above tell the rules apart. Each was also run by hand as a mutation of
    // IncomeStatementReader.cs, and the tests above failed on it (the report lists them).

    [Fact]
    public async Task Control_year_start_skipped_the_treaties_premium_from_February_differs()
    {
        var b = await BookAsync();
        await using var w = b.W;
        var s = await ReadAsync(w, "2026-10-31");
        // XL-1 from February: 1.00 a day × (304 − 31) = 273.00, not 304.00; QS-D 0.10 × 273 = 27.30, not 30.40.
        var wrong = s.Treaties.Select(t => t.Number == "XL-1" ? T("273.00") : t.Number == "QS-D" ? T("27.30") : t.AmountGel).ToList();
        Assert.NotEqual(s.Treaties.Select(t => t.AmountGel), wrong);
        Assert.Equal(OctoberTreaties, Treaties(s));
    }

    [Fact]
    public async Task Control_opening_day_the_end_of_January_the_opening_balances_differ()
    {
        var b = await BookAsync();
        await using var w = b.W;
        var january = IncomeStatement.BalancesOf(RegulatoryForms.ToForms(await w.MonthAsync("2026-01")), RegulatoryForms.ToForms(await w.ImpairmentExactAsync("2026-01-31")));
        Assert.NotEqual(Opening with { Date = january.Date }, january);
        Assert.Equal(Opening, (await ReadAsync(w, "2026-10-31")).Opening);
    }

    [Fact]
    public async Task Control_cancellation_link_dropped_the_deferred_reinsurance_commission_differs()
    {
        var b = await BookAsync();
        await using var w = b.W;
        // CA2 on its own, not mirroring CA1: −18.33 over 01.03–31.12 (305 days), 245 earned at 31.10: −18.33 × 245 / 305 = −14.72, −3.61
        // deferred — with CA1's 3.60 and CB1's 13.54: 13.53, not 13.54.
        var ca2 = new ExactPremiumTx(T("-18.33"), D("2026-03-01"), D("2026-03-01"), D("2026-12-31"));
        var unlinked = T("-18.33") - EarnedPremium.EarnedAsOfExact(ca2, D("2026-10-31"));
        Assert.Equal(T("-3.61"), unlinked);
        Assert.NotEqual(OctoberRi.DeferredClosingGel, T("3.60") + unlinked + T("13.54"));
        Assert.Equal(OctoberRi, (await ReadAsync(w, "2026-10-31")).RiCommission);
    }

    [Fact]
    public async Task Control_excess_of_loss_rule_dropped_the_reinsurers_share_in_K4_differs()
    {
        var b = await BookAsync();
        await using var w = b.W;
        // K4 valued as a proportional share, at its payment's rate: 2700.00 × 400.00 / 1000.00 = 1080.00, not 1120.00 at July's end.
        Assert.Equal(T("1080.00"), ReportMath.DivHalfUp(T("2700.00") * T("400.00"), T("1000.00")));
        Assert.Equal(T("1120.00"), ReportMath.AtRateHalfUp(T("400.00"), 2.80m));
        Assert.Equal(OctoberReinsurerPaid, Items(b, (await ReadAsync(w, "2026-10-31")).ReinsurerPaid));
    }

    [Fact]
    public async Task Control_the_claim_instead_of_the_loss_event_the_losses_counted_differ()
    {
        var b = await BookAsync();
        await using var w = b.W;
        var s = await ReadAsync(w, "2026-10-31");
        // Counted by claim (K1 and K2 apart), EV1 would be two losses: 4, not 3; the items name claims, not events.
        var byClaim = s with { Paid = s.Paid.Select(i => i with { Id = b.Of(i.Id) == "EV1" ? (i.ClassCode == "13" ? "K2" : "K1") : i.Id }).ToList() };
        Assert.Equal((4, 4), IncomeStatement.ClaimCounts(byClaim));
        Assert.Equal((4, 3), IncomeStatement.ClaimCounts(s));
        Assert.Equal(OctoberPaid, Items(b, s.Paid));
    }

    [Fact]
    public async Task Control_the_employee_as_a_bank_the_agents_and_the_banks_commission_differ()
    {
        var b = await BookAsync();
        await using var w = b.W;
        // PE's employee's 4 %, by the policy card's rule: 24.00 written, 6.05 deferred at 31.10. Moved to the bank: the agents' −61.00 / 0, the
        // bank's 54.00 — not −37.00 / 6.05 and 30.00.
        var ledger = (await w.App.ReadAsync(c => c.QueryAsync<(Guid Id, Guid? CancelsId, decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)>("""
            select t.id, t.cancels_transaction_id, t.amount, t.booked_on, t.cover_from, t.cover_to
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @pe
            """, new { pe = b.Ids["PE"] }))).Select(r => new PolicyCard.LedgerRow(r.Id, r.CancelsId, r.Amount, r.BookedOn, r.CoverFrom, r.CoverTo)).ToList();
        var employee = PolicyCard.ExactPremiumPosition(ledger, D("2026-10-31"), "GEL", 1m, 400).PositionGel;
        Assert.Equal((T("24.00"), T("6.05")), (employee.Written, employee.Unearned));
        var s = await ReadAsync(w, "2026-10-31");
        var wrong = new IntermediaryCommission(
            s.Commission.Agent with { WrittenGel = s.Commission.Agent.WrittenGel - employee.Written, DeferredClosingGel = s.Commission.Agent.DeferredClosingGel - employee.Unearned },
            s.Commission.Broker, s.Commission.Bank with { WrittenGel = s.Commission.Bank.WrittenGel + employee.Written });
        Assert.Equal(new IntermediaryCommission(Flow("-61.00", "72.80", "0"), Flow("405.00", "0", "67.68"), Flow("54.00", "0", "0")), wrong);
        Assert.NotEqual(OctoberCommission, wrong);
        Assert.Equal(OctoberCommission, s.Commission);
    }

    [Fact]
    public async Task Control_the_deposits_key_by_the_ceded_premium_written_not_earned_differs()
    {
        var b = await BookAsync();
        await using var w = b.W;
        var s = await ReadAsync(w, "2026-10-31");
        // QS-D's key by its cession's written premium: 810.00, not the 674.63 earned in the period (owner, 21.10.2026: earned).
        var written = await w.App.ReadAsync(c => c.ExecuteScalarAsync<decimal>("select sum(amount_gel) from core.premium_cession where treaty_id = @q1", new { q1 = b.Ids["Q1"] }));
        Assert.Equal(T("810.00"), ReportMath.Tetri(written));
        Assert.NotEqual(T("810.00"), s.Treaties.Single(t => t.Number == "QS-D").EarnedCeded!["5.1"]);
        Assert.Equal(OctoberTreaties, Treaties(s));
    }
}
