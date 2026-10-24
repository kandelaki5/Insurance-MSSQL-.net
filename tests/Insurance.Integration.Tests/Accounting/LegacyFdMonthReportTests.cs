using System.Globalization;
using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Xunit.Sdk;
using static Insurance.Integration.Tests.Accounting.MonthReportOracle;

namespace Insurance.Integration.Tests.Accounting;

/// <summary>
/// Legacy scripts/fd-month-report-db-check.ts with scripts/integration/month-report-scenario.ts (Codex
/// INS-FD-MONTH-REPORT-SQL-CHECK-PREP-OFFLINE-20261018; "PREPARED, NOT RUN" in the old program): the month report the financial
/// director's panel and the detailed report show (PremiumReport.BuildAsync), read as the application role over fictional rows of two
/// open months — the current one M and the next N — in one insurance class that holds no line (K = 2), GEL and USD, in a database of its
/// own that also holds rows of another class and currency and a month closed by the real close before them. The checks are the
/// scenario's: K's row, its claims row and the receivable's change per currency exactly what the rows imply by hand; every other class
/// and claims row, the treaty premium and the totals but K as before; each read writes nothing (a fingerprint of every ledger just
/// before and just after it); M read twice the same, and again after N's movements the very same; a pending claim payment request and a
/// pending premium refund request are no movement; the closed month's report the very same before and after all of it. The National
/// Bank is not asked (the stored rates only).
/// </summary>
public sealed class LegacyFdMonthReportTests
{
    private const string Tag = "check-month-report";
    private const string K = "2";
    private static readonly string[] Ledgers =
    [
        "party", "contract", "policy", "policy_line", "installment", "installment_adjustment", "premium_transaction", "premium_cession", "payment", "payment_allocation",
        "loss_event", "claim", "claim_reserve", "claim_payment", "claim_recovery", "claim_cession", "claim_payment_request", "premium_refund_request", "premium_refund_request_line",
        "ri_treaty", "exchange_rate", "accounting_period", "setting",
    ];

    private static async Task<string> FingerprintAsync(ReportWorld w)
    {
        await using var c = await w.Database.OpenAdminAsync();
        var parts = new List<string>();
        foreach (var t in Ledgers)
            parts.Add($"{t}:{await c.QuerySingleAsync<string>($"select concat(count(*), '/', checksum_agg(binary_checksum(*))) from core.{t}")}");
        parts.Add($"audit_log:{await c.QuerySingleAsync<string>("select concat(count(*), '/', max(id)) from core.audit_log")}");
        parts.Add($"schema_migration:{await c.QuerySingleAsync<string>("select concat(count(*), '/', checksum_agg(binary_checksum(*))) from core.schema_migration")}");
        return string.Join(" ", parts);
    }

    private static readonly List<string> Checked = [];

    private static void Same(string label, string expected, string actual)
    {
        Checked.Add(label);
        if (expected != actual) throw new XunitException($"{label}\n  expected: {expected}\n  actual:   {actual}");
    }

    private static string Rows<T>(IEnumerable<T> rows) => string.Join("\n", rows.Select(r => r!.ToString()));

    [Fact]
    public async Task Two_open_months_of_one_new_class_the_figures_by_hand_everything_else_as_before_and_reading_writes_nothing()
    {
        await using var w = await ReportWorld.CreateAsync();
        var today = Rates.TodayInTbilisi();
        var m = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var n = NextMonth(m);
        var first = Day(m + "-01");
        var prev = first.AddMonths(-1);
        var closed = prev.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        DateOnly DM(int d) => first.AddDays(d - 1);
        DateOnly DN(int d) => first.AddMonths(1).AddDays(d - 1);

        // ---- The database's own rows before the check: another class (5.1) in GEL and EUR from the month before M, a claim; that month closed.
        await w.RateAsync("USD", Iso(prev), 2.6m);
        await w.RateAsync("EUR", Iso(prev.AddDays(14)), 3.0m);
        var other = await w.PolicyAsync("P-OTHER", Iso(prev), Iso(prev.AddDays(365)), [new ReportWorld.LineSpec("5.1", 365m, 365m)],
            [(Iso(prev), 100m, "client"), (Iso(DM(1)), 165m, "client"), (Iso(DN(1)), 100m, "client")]);
        await w.PolicyAsync("P-OTHER-EUR", Iso(prev.AddDays(9)), Iso(prev.AddDays(374)), [new ReportWorld.LineSpec("5.1", 200m, 600m)],
            [(Iso(prev.AddDays(9)), 200m, "client")], "EUR", 3m);
        await w.PayAsync(other.Installments[0], Iso(prev.AddDays(4)), 100m);
        var oc = await w.ClaimAsync(other.Id, other.Lines[0], "GEL", Iso(prev.AddDays(2)), Iso(prev.AddDays(3)));
        await w.ReserveAsync(oc.Id, Iso(prev.AddDays(5)), 250m);
        await w.ClaimPaymentAsync(oc.Id, Iso(prev.AddDays(6)), 50m);
        Assert.Equal("done", (await PeriodClose.CloseAsync(w.App, new CurrentUser(w.Accountant, "test.accountant", "სატესტო", "accountant", "accountant"), prev.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Outcome);
        // Rows of the other class in M and N (they must stay exactly as they are in the report).
        await w.PayAsync(other.Installments[1], Iso(DM(7)), 165m);
        await w.ReserveAsync(oc.Id, Iso(DN(2)), -20m);

        // ---- The stored USD rates the K rows are valued at (the last on or before each month's end).
        await w.RateAsync("USD", Iso(DM(1)), 2.654321m);
        await w.RateAsync("USD", Iso(DN(1)), 2.712345m);
        const string rateM = "2.654321", rateN = "2.712345";

        async Task<PremiumReport.Report> Read(string label, string month)
        {
            var before = await FingerprintAsync(w);
            var report = await w.MonthAsync(month);
            Same($"{label}: reading the report writes nothing (the ledgers' fingerprint just before and just after)", before, await FingerprintAsync(w));
            return report;
        }

        var beforeM = await Read($"{m}, before the rows", m);
        var beforeN = await Read($"{n}, before the rows", n);
        Same($"class {K} has no figures before the rows", "none",
            beforeM.Classes.Concat(beforeN.Classes).Any(c => c.ClassCode == K) || beforeM.Claims.Concat(beforeN.Claims).Any(c => c.ClassCode == K) ? "it has" : "none");
        var closedBefore = await Read($"{closed} (closed), before the rows", closed);
        Assert.NotEmpty(closedBefore.Classes);

        // ---- M's rows (month-report-scenario.ts fixture().m), written as plain rows by the admin.
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        Guid Id(string name) => ids.TryGetValue(name, out var id) ? id : ids[name] = Guid.NewGuid();
        var admin = w.Admin;
        var party = Id("party");
        var cover = (DateOnly from) => (From: from, To: from.AddDays(365));
        var g = cover(DM(1));
        var h = cover(DN(1));
        await w.ExecAsync("insert core.party (id, kind, name, foreign_id, is_resident, country_code) values (@party, N'organization', @name, @tag, 0, N'GE')", new { party, name = $"{Tag} (გამოგონილი)", tag = Tag });
        async Task PolicyAsync(string key, string currency, decimal rate, (DateOnly From, DateOnly To) c, decimal premium, decimal premiumGel, DateTimeOffset at, params (int N, DateOnly Due, decimal Amount)[] insts)
        {
            await w.ExecAsync("insert core.contract (id, number, signed_on, policyholder_id, created_by) values (@id, @number, @on, @party, @admin)",
                new { id = Id($"{key}:contract"), number = $"{Tag}-C-{key}", on = c.From, party, admin });
            await w.ExecAsync("""
                insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment, approved_at, approved_by, created_by)
                values (@id, @contract, @number, @from, @from, @to, N'active', @currency, @rate, N'corporate', @approvedAt, @admin, @admin)
                """, new { id = Id(key), contract = Id($"{key}:contract"), number = $"{Tag}-{key}", c.From, c.To, currency, rate, approvedAt = new DateTimeOffset(c.From.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(4)), admin });
            await w.ExecAsync("insert core.policy_line (id, policy_id, class_code, premium, premium_gel) values (@id, @policy, @K, @premium, @premiumGel)",
                new { id = Id($"{key}:line"), policy = Id(key), K, premium, premiumGel });
            await w.ExecAsync("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
                values (@id, @line, N'issue', @premium, @premiumGel, @from, @from, @to, null, @at, @admin)
                """, new { id = Id($"{key}:issue"), line = Id($"{key}:line"), premium, premiumGel, c.From, c.To, at, admin });
            foreach (var (i, due, amount) in insts)
                await w.ExecAsync("insert core.installment (id, policy_id, due_on, amount) values (@id, @policy, @due, @amount)", new { id = Id($"{key}:i{i}"), policy = Id(key), due, amount });
        }
        async Task ReceiptAsync(string key, DateOnly on, decimal gel, Guid installment)
        {
            await w.ExecAsync("insert core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by) values (@id, @on, @gel, N'bank', N'receipt', @party, @tag, @admin)",
                new { id = Id(key), on, gel, party, tag = Tag, admin });
            await w.ExecAsync("insert core.payment_allocation (id, payment_id, installment_id, amount, amount_gel, rate, booked_on, created_by) values (@id, @payment, @installment, @gel, @gel, 1, @on, @admin)",
                new { id = Id($"{key}:a"), payment = Id(key), installment, gel, on, admin });
        }
        async Task EventAndClaimAsync(string key, string policyKey, string currency, DateOnly on)
        {
            await w.ExecAsync("insert core.loss_event (id, number, policy_id, occurred_on, reported_on, description, created_by) values (@id, @number, @policy, @on, @on, @tag, @admin)",
                new { id = Id($"{key}:event"), number = $"{Tag}-E-{key}", policy = Id(policyKey), on, tag = Tag, admin });
            await w.ExecAsync("""
                insert core.claim (id, number, loss_event_id, policy_id, policy_line_id, currency, occurred_on, reported_on, description, status, created_by)
                values (@id, @number, @event, @policy, @line, @currency, @on, @on, @tag, N'approved', @admin)
                """, new { id = Id(key), number = $"{Tag}-L-{key}", @event = Id($"{key}:event"), policy = Id(policyKey), line = Id($"{policyKey}:line"), currency, on, tag = Tag, admin });
        }
        async Task ReserveAsync(string key, int i, DateOnly on, decimal change) =>
            await w.ExecAsync("insert core.claim_reserve (id, claim_id, booked_on, change, reason, created_by) values (@id, @claim, @on, @change, @tag, @admin)",
                new { id = Id($"{key}:r{i}"), claim = Id(key), on, change, tag = Tag, admin });
        // A payment of the claim and the reserve it releases, as the application books it (the gross).
        async Task PayAsync(string key, int i, DateOnly on, decimal gross, decimal withheld, decimal net)
        {
            await w.ExecAsync("""
                insert core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_payment_id, note, created_by)
                values (@id, @claim, @on, @gross, @withheld, 0, @net, @net, 1, N'policyholder', @party, null, @tag, @admin)
                """, new { id = Id($"{key}:p{i}"), claim = Id(key), on, gross, withheld, net, party, tag = Tag, admin });
            await ReserveAsync(key, 100 + i, on, -gross);
        }
        async Task SalvageAsync(string key, int i, DateOnly on, decimal gel) =>
            await w.ExecAsync("insert core.claim_recovery (id, claim_id, kind, received_on, amount_gel, note, created_by) values (@id, @claim, N'salvage', @on, @gel, @tag, @admin)",
                new { id = Id($"{key}:s{i}"), claim = Id(key), on, gel, tag = Tag, admin });

        // G: GEL 365.00 from M's first day, two instalments of 182.50 (M-01 and N-01); 100.00 received on M-10.
        await PolicyAsync("G", "GEL", 1m, g, 365m, 365m, new DateTimeOffset(DM(1).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(4)), (1, DM(1), 182.50m), (2, DN(1), 182.50m));
        await ReceiptAsync("G:pay1", DM(10), 100m, Id("G:i1"));
        // U: USD 146.00 fixed at 2.500000 = GEL 365.00 from M's first day, one instalment of 146.00 on M-01, unpaid.
        await PolicyAsync("U", "USD", 2.5m, g, 146m, 365m, new DateTimeOffset(DM(1).ToDateTime(new TimeOnly(10, 0, 1)), TimeSpan.FromHours(4)), (1, DM(1), 146m));
        // C (GEL, on G): reserve 500.00 on M-05; paid 200.00 gross (10.00 withheld, 190.00 to the bank) on M-10; salvage 30.00 on M-20.
        await EventAndClaimAsync("C", "G", "GEL", DM(3));
        await ReserveAsync("C", 1, DM(5), 500m);
        await PayAsync("C", 1, DM(10), 200m, 10m, 190m);
        await SalvageAsync("C", 1, DM(20), 30m);
        // D (USD, on U): reserve 100.00 USD on M-05.
        await EventAndClaimAsync("D", "U", "USD", DM(4));
        await ReserveAsync("D", 1, DM(5), 100m);

        int dM = MonthEnd(m).Day, dN = MonthEnd(n).Day;
        var sameYear = m[..4] == n[..4];
        var wantM = (Cls: new BigInteger[] { 2, 73_000, 73_000, 2 * dM * 100, 2 * (365 - dM) * 100, 0, 0, 0 },
            Claims: new BigInteger[] { 20_000, 30_000 + AtRateUp(10_000, rateM), 3_650, 3_000, 0, 0 },
            Gel: new BigInteger[] { 26_500, 8_250, 26_500 }, Usd: new BigInteger[] { 14_600, 14_600, AtRateUp(14_600, rateM) }, Rate: (2.654321m, DM(1)));
        var wantN = (Cls: new BigInteger[] { 1, 36_500, 36_500, 3 * dN * 100, (2 * (365 - dM - dN) + (365 - dN)) * 100, 0, 0, 0 },
            Claims: new BigInteger[] { 10_000, 20_000 + AtRateUp(15_000, rateN), sameYear ? 5_475 : 1_825, 1_000, 0, 0 },
            Gel: new BigInteger[] { 54_750, 54_750, 54_750 }, Usd: new BigInteger[] { 14_600, 14_600, AtRateUp(14_600, rateN) }, Rate: (2.712345m, DN(1)));

        static BigInteger[] Cls(PremiumReport.ClassRow? c) => c is null ? new BigInteger[8]
            : [c.PoliciesIssued, c.StatisticalWrittenGel, c.FinancialWrittenGel, c.EarnedGel, c.UnearnedGel, c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel];
        static BigInteger[] Tot(PremiumReport.Totals t) => [t.PoliciesIssued, t.StatisticalWrittenGel, t.FinancialWrittenGel, t.EarnedGel, t.UnearnedGel, t.CededWrittenGel, t.CededUnearnedGel, t.RiCommissionGel];
        static BigInteger[] Clm(PremiumReport.ClaimsRow? c) => c is null ? new BigInteger[6] : [c.PaidGel, c.RbnsGel, c.IbnrGel, c.RecoveredGel, c.ReinsurerPaidGel, c.ReinsurerRbnsGel];
        static BigInteger[] ClmT(PremiumReport.ClaimsTotals t) => [t.PaidGel, t.RbnsGel, t.IbnrGel, t.RecoveredGel, t.ReinsurerPaidGel, t.ReinsurerRbnsGel];
        static BigInteger[] Owe(PremiumReport.Report r, string cur) => r.Receivables.FirstOrDefault(x => x.Currency == cur) is { } x ? [x.Outstanding, x.Overdue, x.OutstandingGel] : new BigInteger[3];
        static string Txt(IEnumerable<BigInteger> v) => string.Join(", ", v.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        static BigInteger[] Plus(BigInteger[] a, BigInteger[] b) => [.. a.Zip(b, (x, y) => x + y)];
        static BigInteger[] Minus(BigInteger[] a, BigInteger[] b) => [.. a.Zip(b, (x, y) => x - y)];

        void Month(string label, PremiumReport.Report before, PremiumReport.Report after,
            (BigInteger[] Cls, BigInteger[] Claims, BigInteger[] Gel, BigInteger[] Usd, (decimal Rate, DateOnly Date) Rate) want)
        {
            Same($"{label}: class {K} — issued, written (statistical, financial), earned, UPR, ceded — exactly the rows' own", Txt(want.Cls), Txt(Cls(after.Classes.FirstOrDefault(c => c.ClassCode == K))));
            Same($"{label}: class {K} claims — paid gross, RBNS (the USD at the month-end rate), IBNR, recovered, reinsurer", Txt(want.Claims), Txt(Clm(after.Claims.FirstOrDefault(c => c.ClassCode == K))));
            Same($"{label}: every other class row as before the rows", Rows(before.Classes.Where(c => c.ClassCode != K)), Rows(after.Classes.Where(c => c.ClassCode != K)));
            Same($"{label}: every other claims row as before the rows", Rows(before.Claims.Where(c => c.ClassCode != K)), Rows(after.Claims.Where(c => c.ClassCode != K)));
            Same($"{label}: the totals — as before, plus class {K}", Txt(Plus(Tot(before.Totals), want.Cls)), Txt(Tot(after.Totals)));
            Same($"{label}: the claims totals — as before, plus class {K}", Txt(Plus(ClmT(before.ClaimsTotals), want.Claims)), Txt(ClmT(after.ClaimsTotals)));
            Same($"{label}: the treaty premium as before", before.TreatyPremiumGel.ToString(CultureInfo.InvariantCulture), after.TreatyPremiumGel.ToString(CultureInfo.InvariantCulture));
            Same($"{label}: the GEL receivable at the month's end — its change is the rows' own (owed, overdue, in GEL)", Txt(want.Gel), Txt(Minus(Owe(after, "GEL"), Owe(before, "GEL"))));
            Same($"{label}: the USD receivable at the month's end — its change is the rows' own (owed, overdue, in GEL)", Txt(want.Usd), Txt(Minus(Owe(after, "USD"), Owe(before, "USD"))));
            var usd = after.Receivables.Single(x => x.Currency == "USD");
            Same($"{label}: the USD receivable valued at the last rate stored on or before the month's end, with its date", $"{want.Rate.Rate} {want.Rate.Date}", $"{usd.Rate} {usd.RateDate}");
            Same($"{label}: every other currency's receivable as before", Rows(before.Receivables.Where(r => r.Currency is not ("GEL" or "USD"))), Rows(after.Receivables.Where(r => r.Currency is not ("GEL" or "USD"))));
        }

        var afterM = await Read($"{m}, after its rows", m);
        var againM = await Read($"{m}, read again", m);
        Same($"{m}: read twice — the same report", MonthReportCase.ReportText(afterM), MonthReportCase.ReportText(againM));
        Month(m, beforeM, afterM, wantM);

        // ---- N's rows: G's 82.50 on N-05; C paid 100.00 on N-03, salvage 10.00 on N-15; D's reserve +50.00 USD on N-07; H; two pending requests.
        await ReceiptAsync("G:pay2", DN(5), 82.50m, Id("G:i1"));
        await PayAsync("C", 2, DN(3), 100m, 0m, 100m);
        await SalvageAsync("C", 2, DN(15), 10m);
        await ReserveAsync("D", 2, DN(7), 50m);
        await PolicyAsync("H", "GEL", 1m, h, 365m, 365m, new DateTimeOffset(DN(1).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(4)), (1, DN(1), 365m));
        // Pending, never carried out: a claim payment of 70.00 on C and a refund of 10.00 on G.
        await w.ExecAsync("""
            insert core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
                                               offset_premium, total_loss_mode, note, created_by)
            values (@id, @claim, @on, 70, 0, 0, 70, 70, 1, N'policyholder', @party, 0, N'none', @tag, @admin)
            """, new { id = Id("C:request"), claim = Id("C"), on = DN(20), party, tag = Tag, admin });
        await w.ExecAsync("""
            insert core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_by)
            values (@id, @policy, @on, 10, N'GEL', 1, N'bank', @tag, @party, @admin);
            insert core.premium_refund_request_line (id, request_id, position, installment_id, amount, amount_gel) values (@line, @id, 1, @i2, 10, 10);
            """, new { id = Id("G:refund"), policy = Id("G"), on = DN(20), tag = Tag, party, admin, line = Id("G:refund:1"), i2 = Id("G:i2") });

        var laterM = await Read($"{m}, after {n}'s movements", m);
        Same($"{m}: read again after {n}'s movements — the very same report (a later month does not rewrite it)", MonthReportCase.ReportText(afterM), MonthReportCase.ReportText(laterM));
        var afterN = await Read($"{n}, after its rows", n);
        Month(n, beforeN, afterN, wantN);
        Same($"{n}: the pending claim payment request (70.00) is no payment — paid gross is the one carried out", "10000",
            afterN.Claims.Single(c => c.ClassCode == K).PaidGel.ToString(CultureInfo.InvariantCulture));
        var closedAfter = await Read($"{closed} (closed), after both months' rows", closed);
        Same($"{closed} (closed): the very same report after {m}'s and {n}'s rows", MonthReportCase.ReportText(closedBefore), MonthReportCase.ReportText(closedAfter));
        Assert.True(Checked.Count >= 30);
    }
}
