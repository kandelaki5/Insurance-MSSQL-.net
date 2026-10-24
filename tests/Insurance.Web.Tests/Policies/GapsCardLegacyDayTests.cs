using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using Insurance.Web.Tests.FailureInjection;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// A site of its own on the legacy day of payment-shared-page.test.ts: the database's clock (DbOptions.Clock) stands on
/// 03.11.2026 in Tbilisi (the legacy TODAY), the National Bank unreachable and every request to it counted (FaultSite's
/// RefusingNbg). Fictional rows are written directly with the legacy dates (no month is closed in this database, so no
/// trigger refuses them, and none is bypassed); the pages are the real ones over HTTP.
/// </summary>
public sealed class LegacyDaySite : IAsyncLifetime
{
    public static readonly DateOnly Today = new(2026, 11, 3);
    public FaultSite Site { get; } = new();
    public HttpClient Accountant { get; private set; } = null!;
    public HttpClient Viewer { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;
    public Guid AdminId { get; private set; }
    public Guid Product { get; private set; }
    public Guid Damage { get; private set; }
    private static int seq;

    public async Task InitializeAsync()
    {
        await Site.InitializeAsync();
        Site.Db.Clock.SetToday(Today);
        (Accountant, _) = await Site.SignedInAsync("accountant");
        (Viewer, _) = await Site.SignedInAsync("viewer");
        (Admin, AdminId) = await Site.SignedInAsync("admin");
        await using var c = await Site.Db.OpenAdminAsync();
        (Product, Damage) = await c.QuerySingleAsync<(Guid, Guid)>(
            "select p.id, c.id from core.product p join core.product_coverage c on c.product_id = p.id where p.code = N'CASCO' and c.code = N'damage'");
    }

    public async Task DisposeAsync() => await Site.DisposeAsync();

    public static DateOnly D(string s) => DateOnly.Parse(s, CultureInfo.InvariantCulture);

    public async Task<Guid> PartyAsync(string? name = null)
    {
        var id = Guid.NewGuid();
        var n = Interlocked.Increment(ref seq);
        await Site.ExecAsync("insert core.party (id, kind, name, tax_id, risk_level) values (@id, N'organization', @name, @tax, N'low')",
            new { id, name = name ?? $"შპს ტესტი {id.ToString("N")[..6]}", tax = (400_000_000 + n * 7919 + Random.Shared.Next(100_000)).ToString(CultureInfo.InvariantCulture) });
        return id;
    }

    public sealed record Policy(Guid Id, Guid Holder, Guid Line);

    /// <summary>An active CASCO policy T from 01.10.2026 for a year, one damage line, no ledger and no schedule (the tests write those).</summary>
    public async Task<Policy> PolicyAsync(string currency = "GEL", decimal rate = 1m, int? grace = 30, Guid? holder = null)
    {
        var party = holder ?? await PartyAsync();
        var (contract, policy, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var number = "LD-" + policy.ToString("N")[..12].ToUpperInvariant();
        await Site.ExecAsync("""
            insert core.contract (id, number, signed_on, policyholder_id) values (@contract, @contractNo, '2026-10-01', @party);
            insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment, product_id,
                                created_by, approved_at, approved_by, cancel_grace_days)
            values (@policy, @contract, @number, '2026-10-01', '2026-10-01', '2027-10-01', N'active', @currency, @rate, N'corporate', @product,
                    @admin, sysdatetimeoffset(), @admin, @grace);
            insert core.policy_line (id, policy_id, class_code, premium, premium_gel, product_coverage_id) values (@line, @policy, N'5.1', 0, 0, @damage);
            """, new { contract, contractNo = "C-" + number, policy, number, currency, rate, product = Product, admin = AdminId, grace, line, damage = Damage, party });
        return new Policy(policy, party, line);
    }

    public sealed record Tx(int N, string Amount, string Booked, string From, string To, int? Cancels = null);

    /// <summary>
    /// The policy's ledger exactly as given and read in this order (created_at one second apart in the given order); each row's
    /// GEL column as the legacy fixture wrote it (= its amount). Originals are inserted before the rows that cancel them.
    /// A cancellation of a row that is not in <paramref name="rows"/> points at <paramref name="foreign"/>.
    /// </summary>
    public async Task WriteLedgerAsync(Policy p, Tx[] rows, Guid? foreign = null)
    {
        var ids = rows.ToDictionary(r => r.N, _ => Guid.NewGuid());
        var start = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(4));
        var order = rows.Select((r, i) => (Row: r, At: start.AddSeconds(i))).ToList();
        var written = new HashSet<int>();
        while (written.Count < rows.Length)
            foreach (var (r, at) in order.Where(o => !written.Contains(o.Row.N)))
            {
                if (r.Cancels is { } c && ids.ContainsKey(c) && !written.Contains(c)) continue;
                Guid? cancels = r.Cancels is { } k ? (ids.TryGetValue(k, out var own) ? own : foreign) : null;
                await Site.ExecAsync("""
                    insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_at, created_by, cancels_transaction_id)
                    values (@id, @line, @kind, @amount, @amount, @booked, @from, @to, @at, @admin, @cancels)
                    """, new { id = ids[r.N], line = p.Line, kind = cancels is null ? "issue" : "cancellation", amount = M(r.Amount), booked = D(r.Booked), from = D(r.From), to = D(r.To), at, admin = AdminId, cancels });
                written.Add(r.N);
            }
    }

    public static decimal M(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    public async Task<Guid> InstallmentAsync(Policy p, string due, string amount)
    {
        var id = Guid.NewGuid();
        await Site.ExecAsync("insert core.installment (id, policy_id, due_on, amount) values (@id, @p, @due, @amount)", new { id, p = p.Id, due = D(due), amount = M(amount) });
        return id;
    }

    /// <summary>A payment of the amount received on the day (a refund when negative) allocated whole to the instalment at 1, booked that day.</summary>
    public async Task<Guid> HeldAsync(Policy p, Guid installment, string amount, string on)
    {
        var id = Guid.NewGuid();
        var a = M(amount);
        await Site.ExecAsync("""
            insert core.payment (id, received_on, amount_gel, method, kind, payer_party_id) values (@id, @on, @a, N'bank', @kind, @holder);
            insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@id, @installment, @a, @a, 1, @on);
            """, new { id, on = D(on), a, kind = a < 0 ? "refund" : "receipt", holder = p.Holder, installment });
        return id;
    }

    public Task AdjustAsync(Guid installment, string change, string on) =>
        Site.ExecAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@installment, @on, @change, N'შემცირება')",
            new { installment, on = D(on), change = M(change) });

    public Task RateAsync(string currency, DateOnly day, decimal rate) =>
        Site.ExecAsync("insert core.exchange_rate (currency, rate_date, rate) values (@currency, @day, @rate)", new { currency, day, rate });

    public Task<string> LedgerAsync() => Site.ScalarAsync<string>("""
        select concat((select count(*) from core.policy), '/', (select count(*) from core.premium_transaction), '/', (select count(*) from core.installment), '/',
                      (select count(*) from core.installment_adjustment), '/', (select count(*) from core.payment), '/', (select count(*) from core.payment_allocation), '/',
                      (select count(*) from core.exchange_rate), '/', (select count(*) from core.premium_refund_request), '/', (select count(*) from core.audit_log))
        """);

    public static async Task<string> RawAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadAsStringAsync();
    }

    public static List<string[]> BodyRows(string table) => Regex.Matches(Regex.Match(table, @"<tbody>([\s\S]*?)</tbody>").Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr>")
        .Select(r => Regex.Matches(r.Groups[1].Value, @"<td\b[^>]*>([\s\S]*?)</td>").Select(c => Html.Text(c.Groups[1].Value)).ToArray()).ToList();

    public static List<string> Tables(string html) => Regex.Matches(html, @"<table\b[^>]*>([\s\S]*?)</table>").Select(t => t.Groups[1].Value).ToList();

    /// <summary>The card's figures table (the one with the written premium's row): label → the cells after it.</summary>
    public static Dictionary<string, string[]> Figures(string html)
    {
        var rows = Tables(html).Select(BodyRows).Single(t => t.Any(r => r.Length > 0 && r[0] == PolicyTexts.Finance.Written));
        return rows.Where(r => r.Length > 0).ToDictionary(r => r[0], r => r[1..]);
    }
}

/// <summary>
/// The policy card over HTTP on the legacy day 03.11.2026 (legacy payment-shared-page.test.ts, POLICY_CARD_RECEIVABLE,
/// POLICY_CARD_PREMIUM_POSITION and POLICY_CARD_COMMISSION_*_20261019): the receivable's five cases, the premium position's
/// seven, and the commission at page level ("0 % shows no rows" and the intermediaries' label included), each with the legacy
/// ledger, days and expected figures by hand (the arithmetic in the comments is the legacy test's). The calculation itself
/// with the same ledgers: Insurance.Integration.Tests GapsCardPositionTests.
/// </summary>
public sealed class GapsCardLegacyDayTests(LegacyDaySite s) : IClassFixture<LegacyDaySite>
{
    private static string Overdue => PolicyTexts.Overdue;
    private static LegacyDaySite.Tx Tx(int n, string amount, string booked, string from, string to, int? cancels = null) => new(n, amount, booked, from, to, cancels);

    private sealed record Receivable(string[]? Owed, string[]? Overpaid, List<string[]> Schedule, string? Cancel, string? Refund, bool Record);

    private async Task<Receivable> ReceivableOfAsync(LegacyDaySite.Policy p, HttpClient? who = null)
    {
        var html = await LegacyDaySite.RawAsync(who ?? s.Accountant, $"/policies/{p.Id}");
        var figures = LegacyDaySite.Figures(html);
        var schedule = LegacyDaySite.Tables(html).Where(t => Html.Text(Regex.Match(t, @"<thead>([\s\S]*?)</thead>").Groups[1].Value).Contains(PolicyTexts.Fields.Outstanding, StringComparison.Ordinal))
            .Select(LegacyDaySite.BodyRows).Single().Select(r => r[..4]).ToList();
        var text = Html.Text(html);
        var cancel = Regex.Match(text, $@"{Regex.Escape(PolicyTexts.Finance.CancellableFrom)}: (\d\d\.\d\d\.\d{{4}})") is { Success: true } c ? c.Groups[1].Value : null;
        var refund = Regex.Match(text, $@"{Regex.Escape(BillingTexts.Payment.Refund)}: ([-\d,.]+) ") is { Success: true } r ? r.Groups[1].Value : null;
        return new Receivable(figures.GetValueOrDefault(PolicyTexts.Finance.Receivable), figures.GetValueOrDefault(PolicyTexts.Cancel.Overpaid), schedule, cancel, refund,
            html.Contains($">{BillingTexts.Payment.Record}</a>", StringComparison.Ordinal));
    }

    /// <summary>T's instalments (due, amount), receipts each allocated whole to one instalment (index, amount, received), reductions (index, change, booked).</summary>
    private async Task<LegacyDaySite.Policy> ReceivableWorldAsync((string Due, string Amount)[] insts, (int I, string Amount, string On)[] held,
        (int I, string Change, string On)[]? adjust = null, string currency = "GEL", int? grace = 30)
    {
        var p = await s.PolicyAsync(currency, currency == "GEL" ? 1m : 2.7m, grace);
        var ids = new List<Guid>();
        foreach (var (due, amount) in insts) ids.Add(await s.InstallmentAsync(p, due, amount));
        foreach (var (i, amount, on) in held) await s.HeldAsync(p, ids[i], amount, on);
        foreach (var (i, change, on) in adjust ?? []) await s.AdjustAsync(ids[i], change, on);
        return p;
    }

    private async Task UsdTodayAsync()
    {
        if (await s.Site.ScalarAsync<int>("select count(*) from core.exchange_rate where currency = N'USD' and rate_date = @d", new { d = LegacyDaySite.Today }) == 0)
            await s.RateAsync("USD", LegacyDaySite.Today, 2.7m);
    }

    // ---------------------------------------------------------------- the receivable (five cases)

    [Fact]
    public async Task Receivable_Codexs_four_cases_0_01_owed_or_paid_beyond_never_0_00_or_0_02_with_the_marks_that_follow()
    {
        // Owed .02, held .01: 0.01 owed, overdue, cancellable from 31.10 (30 days' grace), "record a payment" offered.
        var r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "90071992547408.02")], [(0, "90071992547408.01", "2026-10-02")]));
        Assert.Equal(new Receivable(["0.01"], null, [["01.10.2026", "90,071,992,547,408.02", "0.01", Overdue]], "31.10.2026", null, true), r, new ReceivableComparer());
        // Owed .01, held .00: 0.01, not 0.02.
        r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "90071992547408.01")], [(0, "90071992547408.00", "2026-10-02")]));
        Assert.Equal(("0.01", "0.01"), (J(r.Owed), r.Schedule[0][2]));
        // Owed .00, held .01: 0.01 paid beyond — the summary row and the refund button say 0.01, nothing owed, no mark.
        r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "90071992547408.00")], [(0, "90071992547408.01", "2026-10-02")]));
        Assert.Equal(new Receivable(["0.00"], ["0.01"], [["01.10.2026", "90,071,992,547,408.00", "0.00", ""]], null, "0.01", false), r, new ReceivableComparer());
        // Owed .01, held .02: 0.01 paid beyond, the refund button there.
        r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "90071992547408.01")], [(0, "90071992547408.02", "2026-10-02")]));
        Assert.Equal(("0.01", "0.01"), (J(r.Overpaid), r.Refund));
        Assert.Empty(s.Site.Nbg.Requests);
    }

    [Fact]
    public async Task Receivable_USD_revalued_at_the_reporting_days_rate_exactly_33359997239780_01_at_2_7_and_0_01_paid_beyond_is_0_03_GEL()
    {
        await UsdTodayAsync();
        var r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "33359997239780.01")], [], currency: "USD"));
        Assert.Equal(["33,359,997,239,780.01", "90,071,992,547,406.03"], r.Owed!);
        // 0.01 × 2.7 = 0.027 → 0.03.
        r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "100.00")], [(0, "100.01", "2026-10-02")], currency: "USD"));
        Assert.Equal(("0.00|0.00", "0.01|0.03"), (J(r.Owed), J(r.Overpaid)));
        Assert.Empty(s.Site.Nbg.Requests);
    }

    [Fact]
    public async Task Receivable_oldest_due_first_a_credit_received_after_today_not_yet_the_schedule_as_it_stands_today_H23_the_largest_instalment()
    {
        // 120.00 by today settles T1 (100.00) and 20.00 of T2 (40.00 after its reduction); T3's reduction is not in force yet.
        var r = await ReceivableOfAsync(await ReceivableWorldAsync(
            [("2026-10-01", "100.00"), ("2026-11-01", "50.00"), ("2026-12-01", "50.00")],
            [(0, "120.00", "2026-10-05"), (1, "30.00", "2026-11-10")],
            [(1, "-10.00", "2026-11-01"), (2, "-50.00", "2026-11-20")]));
        Assert.Equal([["01.10.2026", "100.00", "0.00", ""], ["01.11.2026", "40.00", "20.00", Overdue], ["01.12.2026", "50.00", "50.00", ""]], r.Schedule);
        Assert.Equal(("70.00", "01.12.2026"), (J(r.Owed), r.Cancel));
        r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "9999999999999999.99")], [(0, "0.01", "2026-10-02")]));
        Assert.Equal([["01.10.2026", "9,999,999,999,999,999.99", "9,999,999,999,999,999.98", Overdue]], r.Schedule);
        // 90071992547508.01 held on 100.00: the refund button and the summary say exactly 90,071,992,547,408.01 back.
        r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "100.00")], [(0, "90071992547508.01", "2026-10-02")]));
        Assert.Equal(("90,071,992,547,408.01", "90,071,992,547,408.01"), (J(r.Overpaid), r.Refund));
    }

    [Fact]
    public async Task Receivable_a_credit_and_a_refund_net_off_100_01_in_and_0_01_back_on_100_00_nothing_owed_nothing_beyond_no_refund_button()
    {
        var r = await ReceivableOfAsync(await ReceivableWorldAsync([("2026-10-01", "100.00")], [(0, "100.01", "2026-10-02"), (0, "-0.01", "2026-10-20")]));
        Assert.Equal(("0.00", (string?)null, (string?)null, false), (J(r.Owed), J(r.Overpaid), r.Refund, r.Record));
    }

    [Fact]
    public async Task Receivable_the_grace_none_no_date_0_the_due_date_itself_a_viewer_sees_the_same_figures_without_the_refund_button_nothing_written_no_network()
    {
        var p = await ReceivableWorldAsync([("2026-10-01", "90071992547408.02")], [(0, "90071992547408.01", "2026-10-02")], grace: null);
        Assert.Null((await ReceivableOfAsync(p)).Cancel);
        await s.Site.ExecAsync("update core.policy set cancel_grace_days = 0 where id = @id", new { id = p.Id });
        Assert.Equal("01.10.2026", (await ReceivableOfAsync(p)).Cancel);
        var q = await ReceivableWorldAsync([("2026-10-01", "90071992547408.00")], [(0, "90071992547408.01", "2026-10-02")]);
        var before = await s.LedgerAsync();
        var v = await ReceivableOfAsync(q, s.Viewer);
        Assert.Equal(("0.01", (string?)null), (J(v.Overpaid), v.Refund));
        Assert.Equal(before, await s.LedgerAsync());
        Assert.Empty(s.Site.Nbg.Requests);
    }

    // ---------------------------------------------------------------- the premium position (seven cases)

    private async Task<Dictionary<string, string[]>> PositionOfAsync(LegacyDaySite.Tx[] rows, string currency = "GEL", decimal rate = 1m)
    {
        if (currency != "GEL") await UsdTodayAsync();
        var p = await s.PolicyAsync(currency, rate);
        await s.WriteLedgerAsync(p, rows);
        var figures = LegacyDaySite.Figures(await LegacyDaySite.RawAsync(s.Accountant, $"/policies/{p.Id}"));
        return new[] { PolicyTexts.Finance.Written, PolicyTexts.Finance.Earned, PolicyTexts.Finance.Unearned }.ToDictionary(k => k, k => figures[k]);
    }

    private static Dictionary<string, string[]> Expect(string[] written, string[] earned, string[] unearned) =>
        new() { [PolicyTexts.Finance.Written] = written, [PolicyTexts.Finance.Earned] = earned, [PolicyTexts.Finance.Unearned] = unearned };

    [Theory]
    [InlineData("90071992547408.01", "90,071,992,547,408.01")]
    [InlineData("9999999999999999.99", "9,999,999,999,999,999.99")]
    public async Task Position_Codexs_cases_a_one_day_GEL_movement_of_the_largest_amounts_all_earned_to_the_tetri(string amount, string shown) =>
        Assert.Equal(Expect([shown], [shown], ["0.00"]), await PositionOfAsync([Tx(0, amount, "2026-10-01", "2026-10-01", "2026-10-02")]));

    [Fact]
    public async Task Position_USD_at_the_fixed_rate_stays_as_it_was_33359997239780_01_at_2_7_is_90071992547406_03() =>
        Assert.Equal(Expect(["33,359,997,239,780.01", "90,071,992,547,406.03"], ["33,359,997,239,780.01", "90,071,992,547,406.03"], ["0.00", "0.00"]),
            await PositionOfAsync([Tx(0, "33359997239780.01", "2026-10-01", "2026-10-01", "2026-10-02")], "USD", 2.7m));

    [Fact]
    public async Task Position_pro_rata_over_a_year_as_of_the_34th_day_of_365_the_largest_amounts_in_the_currency_and_in_GEL_each_from_the_whole_movement()
    {
        // 9007199254740801 × 34 / 365 = 839026779893663.65… → 839026779893664.
        Assert.Equal(Expect(["90,071,992,547,408.01"], ["8,390,267,798,936.64"], ["81,681,724,748,471.37"]),
            await PositionOfAsync([Tx(0, "90071992547408.01", "2026-10-01", "2026-10-01", "2027-10-01")]));
        // USD: 3335999723978001 × 34 / 365 → 310750659219869; GEL 9007199254740603 × 34 / 365 → 839026779893645 (the GEL movement first).
        Assert.Equal(Expect(["33,359,997,239,780.01", "90,071,992,547,406.03"], ["3,107,506,592,198.69", "8,390,267,798,936.45"], ["30,252,490,647,581.32", "81,681,724,748,469.58"]),
            await PositionOfAsync([Tx(0, "33359997239780.01", "2026-10-01", "2026-10-01", "2027-10-01")], "USD", 2.7m));
    }

    [Fact]
    public async Task Position_each_movement_rounded_on_its_own_half_away_from_zero_three_0_01_and_one_minus_0_01_over_two_days_on_their_first_day() =>
        // Each: 1 × 1 / 2 = 0.5 → 1; −0.5 → −1. Earned 1 + 1 + 1 − 1 = 2 → 0.02 of the 0.02 written (rounded once on the sum: 0.01).
        Assert.Equal(Expect(["0.02"], ["0.02"], ["0.00"]), await PositionOfAsync([Tx(0, "0.01", "2026-11-03", "2026-11-03", "2026-11-05"), Tx(1, "0.01", "2026-11-03", "2026-11-03", "2026-11-05"),
            Tx(2, "0.01", "2026-11-03", "2026-11-03", "2026-11-05"), Tx(3, "-0.01", "2026-11-03", "2026-11-03", "2026-11-05")]));

    [Fact]
    public async Task Position_a_cancellation_read_before_its_original_linked_a_cancellation_booked_after_today_not_yet_its_GEL_from_the_originals_GEL() =>
        // USD 100.00 for 365 days from 01.10 at 2.7 (270.00 GEL); cancelled from 21.10: 100.00 − 100.00 × 20 / 365 (5.48) = −94.52; in GEL from the
        // original's: 270.00 − 270.00 × 20 / 365 (14.79) = −255.21 (−94.52 × 2.7 would be −255.20).
        Assert.Equal(Expect(["5.48", "14.79"], ["5.48", "14.79"], ["0.00", "0.00"]), await PositionOfAsync([
            Tx(1, "-94.52", "2026-10-21", "2026-10-21", "2027-10-01", 0), Tx(0, "100.00", "2026-10-01", "2026-10-01", "2027-10-01"),
            Tx(2, "-50.00", "2026-11-10", "2026-11-10", "2027-10-01", 0)], "USD", 2.7m));

    [Fact]
    public async Task Position_a_cancellation_undone_the_same_day_the_pair_nets_to_nothing_from_then_on_written_is_earned_plus_unearned() =>
        // GEL 365.00 from 01.10; cancelled from 21.10 (−345.00), undone on 25.10 (+345.00): as of 03.11 the original alone.
        Assert.Equal(Expect(["365.00"], ["34.00"], ["331.00"]), await PositionOfAsync([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01"),
            Tx(1, "-345.00", "2026-10-21", "2026-10-21", "2027-10-01", 0), Tx(2, "345.00", "2026-10-25", "2026-10-21", "2027-10-01", 1)]));

    [Fact]
    public async Task Position_a_cancellation_whose_original_is_not_read_is_refused_the_page_fails_never_a_plain_negative_premium()
    {
        // Its original is a row of another policy: the card reads only this policy's ledger, so the original is not among the rows read.
        var other = await s.PolicyAsync();
        var foreign = Guid.NewGuid();
        await s.Site.ExecAsync("""
            insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
            values (@foreign, @line, N'issue', 100, 100, '2026-10-01', '2026-10-01', '2027-10-01', @admin)
            """, new { foreign, line = other.Line, admin = s.AdminId });
        var p = await s.PolicyAsync();
        await s.WriteLedgerAsync(p, [Tx(1, "-94.52", "2026-10-21", "2026-10-21", "2027-10-01", 0)], foreign);
        var response = await s.Accountant.GetAsync($"/policies/{p.Id}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Matches(@"cancels [0-9a-fA-F-]{36}, which is not among the rows read", body);
        Assert.DoesNotContain("-94.52", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the commission (page level)

    private sealed record Commission(string? Written, string? Deferred, string Html);

    private async Task<Commission> CommissionOfAsync(LegacyDaySite.Tx[] rows, string[] percents, string currency = "GEL", decimal rate = 1m)
    {
        if (currency != "GEL") await UsdTodayAsync();
        var p = await s.PolicyAsync(currency, rate);
        await s.WriteLedgerAsync(p, rows);
        for (var n = 0; n < percents.Length; n++)
        {
            var party = await s.PartyAsync($"ტესტი აგენტი {n + 1}");
            var intermediary = Guid.NewGuid();
            await s.Site.ExecAsync("""
                insert core.intermediary (id, party_id, kind) values (@intermediary, @party, N'agent');
                insert core.policy_intermediary (policy_id, intermediary_id, commission_percent) values (@policy, @intermediary, @percent);
                """, new { intermediary, party, policy = p.Id, percent = LegacyDaySite.M(percents[n]) });
        }
        var html = await LegacyDaySite.RawAsync(s.Accountant, $"/policies/{p.Id}");
        var figures = LegacyDaySite.Figures(html);
        return new Commission(figures.GetValueOrDefault(PolicyTexts.Finance.Commission)?[^1], figures.GetValueOrDefault(PolicyTexts.Finance.DeferredCommission)?[^1], WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData("90071992547408.01", "90,071,992,547,408.01")]
    [InlineData("9999999999999999.99", "9,999,999,999,999,999.99")]
    public async Task Commission_Codexs_cases_a_one_day_movement_of_the_largest_amounts_at_100_percent_to_the_tetri(string amount, string shown)
    {
        var c = await CommissionOfAsync([Tx(0, amount, "2026-10-01", "2026-10-01", "2026-10-02")], ["100.00"]);
        Assert.Equal((shown, "0.00"), (c.Written, c.Deferred));
    }

    [Fact]
    public async Task Commission_a_cancellation_cancels_the_commissions_original_not_the_gross_premium_365_at_10_percent_cancelled_from_01_11_is_3_10_and_0_00()
    {
        // Commission 36.50 over 365 days; cancelled from 01.11 (−334.00 → −33.40): written 3.10, earned 3.40 − 0.30 = 3.10, nothing deferred.
        var c = await CommissionOfAsync([Tx(1, "-334.00", "2026-11-01", "2026-11-01", "2027-10-01", 0), Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01")], ["10.00"]);
        Assert.Equal(("3.10", "0.00"), (c.Written, c.Deferred));
    }

    [Fact]
    public async Task Commission_a_cancellation_booked_later_and_a_cancellation_undone_leave_the_original_commission_alone()
    {
        // As of 03.11 a cancellation booked 10.11 does not count yet: written 36.50, earned 3.40, deferred 33.10.
        var c = await CommissionOfAsync([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01"), Tx(1, "-334.00", "2026-11-10", "2026-11-01", "2027-10-01", 0)], ["10.00"]);
        Assert.Equal(("36.50", "33.10"), (c.Written, c.Deferred));
        // Cancelled from 21.10 (−34.50) and undone on 25.10 (+34.50): the original's 36.50 / 33.10 again.
        c = await CommissionOfAsync([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01"), Tx(1, "-345.00", "2026-10-21", "2026-10-21", "2027-10-01", 0),
            Tx(2, "345.00", "2026-10-25", "2026-10-21", "2027-10-01", 1)], ["10.00"]);
        Assert.Equal(("36.50", "33.10"), (c.Written, c.Deferred));
    }

    [Fact]
    public async Task Commission_USD_at_the_policys_fixed_rate_the_cancellations_GEL_from_the_commission_originals_GEL_1_48_and_0_00()
    {
        // USD 100.00 at 2.7, 10 %: commission 27.00 GEL. Cancelled from 21.10: written 27.00 × 20 / 365 = 1.48; earned 2.52 − 1.04 = 1.48; deferred 0.00.
        var c = await CommissionOfAsync([Tx(0, "100.00", "2026-10-01", "2026-10-01", "2027-10-01"), Tx(1, "-94.52", "2026-10-21", "2026-10-21", "2027-10-01", 0)], ["10.00"], "USD", 2.7m);
        Assert.Equal(("1.48", "0.00"), (c.Written, c.Deferred));
    }

    [Fact]
    public async Task Commission_several_intermediaries_the_percents_summed_exactly_each_movement_rounded_on_its_own_0_percent_shows_no_rows_and_the_label()
    {
        // 1000.00 × 13.25 % = 132.50; the label names each intermediary with its percent as the old page wrote it.
        var c = await CommissionOfAsync([Tx(0, "1000.00", "2026-10-01", "2026-10-01", "2026-10-02")], ["12.50", "0.75"]);
        Assert.Equal(("132.50", "0.00"), (c.Written, c.Deferred));
        // The legacy card reads the intermediaries without an order (no ORDER BY there either), so either order is the old page's.
        Assert.True(c.Html.Contains("ტესტი აგენტი 1 (12.5%), ტესტი აგენტი 2 (0.75%)", StringComparison.Ordinal)
            || c.Html.Contains("ტესტი აგენტი 2 (0.75%), ტესტი აგენტი 1 (12.5%)", StringComparison.Ordinal), "the label names each intermediary with its own percent");
        // 0.03 × 33.33 % = 0.009999 → 0.01.
        Assert.Equal("0.01", (await CommissionOfAsync([Tx(0, "0.03", "2026-10-01", "2026-10-01", "2026-10-02")], ["33.33"])).Written);
        // Three 0.01 at 50 %: 0.005 → 0.01 each, 0.03 in all (rounded once on the 0.03 it would be 0.02).
        Assert.Equal("0.03", (await CommissionOfAsync([Tx(0, "0.01", "2026-10-01", "2026-10-01", "2026-10-02"), Tx(1, "0.01", "2026-10-01", "2026-10-01", "2026-10-02"),
            Tx(2, "0.01", "2026-10-01", "2026-10-01", "2026-10-02")], ["50.00"])).Written);
        // 0 %: no commission rows at all, the intermediary still named with 0%.
        c = await CommissionOfAsync([Tx(0, "1000.00", "2026-10-01", "2026-10-01", "2026-10-02")], ["0.00"]);
        Assert.Equal(((string?)null, (string?)null), (c.Written, c.Deferred));
        Assert.DoesNotContain(PolicyTexts.Finance.Commission, c.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(PolicyTexts.Finance.DeferredCommission, c.Html, StringComparison.Ordinal);
        Assert.Contains("ტესტი აგენტი 1 (0%)", c.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commission_linked_not_a_plain_negative_0_12_over_7_days_at_12_50_percent_cancelled_from_02_11_is_0_00_written_0_00_deferred()
    {
        var c = await CommissionOfAsync([Tx(0, "0.12", "2026-11-01", "2026-11-01", "2026-11-08"), Tx(1, "-0.10", "2026-11-02", "2026-11-02", "2026-11-08", 0)], ["12.50"]);
        Assert.Equal(("0.00", "0.00"), (c.Written, c.Deferred));
    }

    [Fact]
    public async Task Commission_nothing_written_no_network_the_premium_position_and_receivable_rows_as_before()
    {
        var before = await s.LedgerAsync();
        var c = await CommissionOfAsync([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01")], ["10.00"]);
        Assert.Equal(("36.50", "33.10"), (c.Written, c.Deferred));
        var figures = LegacyDaySite.Figures(c.Html);
        Assert.Equal(("365.00", "34.00", "331.00", "0.00"), (J(figures[PolicyTexts.Finance.Written]), J(figures[PolicyTexts.Finance.Earned]), J(figures[PolicyTexts.Finance.Unearned]), J(figures[PolicyTexts.Finance.Receivable])));
        // Only the fixture's own rows were added (a policy, its ledger row, the intermediary's party): the page wrote nothing.
        var after = await s.LedgerAsync();
        Assert.Equal(Count(before, 0) + 1, Count(after, 0));
        Assert.Equal(Count(before, 1) + 1, Count(after, 1));
        Assert.Equal(before.Split('/')[2..8], after.Split('/')[2..8]);
        Assert.Empty(s.Site.Nbg.Requests);
    }

    private static string? J(string[]? cells) => cells is null ? null : string.Join("|", cells);

    private static int Count(string ledger, int i) => int.Parse(ledger.Split('/')[i], CultureInfo.InvariantCulture);

    /// <summary>Compares two receivables cell by cell.</summary>
    private sealed class ReceivableComparer : IEqualityComparer<Receivable>
    {
        private static string Show(Receivable r) => string.Join("§", [string.Join("|", r.Owed ?? ["∅"]), string.Join("|", r.Overpaid ?? ["∅"]),
            string.Join("¶", r.Schedule.Select(x => string.Join("|", x))), r.Cancel ?? "∅", r.Refund ?? "∅", r.Record.ToString()]);

        public bool Equals(Receivable? x, Receivable? y) => x is not null && y is not null && Show(x) == Show(y);
        public int GetHashCode(Receivable obj) => Show(obj).GetHashCode(StringComparison.Ordinal);
    }
}

/// <summary>
/// Legacy payment-shared-page.test.ts "an empty USD or EUR policy without a rate today" (PAYMENT_PAGE_EMPTY_FOREIGN_MISSING_RATE_
/// REGRESSION_20261019), the part left open: with no schedule, no credit and no rate stored, the payment page of a USD or EUR
/// policy shows 0.00 in its currency, both buttons disabled and the receipt form — and the National Bank is asked exactly
/// once, for today (03.11.2026, the database's clock), its refusal not stored; a GEL policy asks nothing. For the accountant
/// and the admin. The bank is the counting stand-in (RefusingNbg) of a site of its own, where no rate is ever stored.
/// </summary>
public sealed class GapsPaymentPageBankOnceTests(LegacyDaySite s) : IClassFixture<LegacyDaySite>
{
    private static string Part(string html, string title)
    {
        var at = html.IndexOf($">{title}</h2>", StringComparison.Ordinal);
        if (at < 0) return "";
        var next = html.IndexOf("<h2", at + title.Length, StringComparison.Ordinal);
        return next < 0 ? html[at..] : html[at..next];
    }

    private static string[] Buttons(string html) => [.. new[] { BillingTexts.Payment.Advances.Title, BillingTexts.Payment.Advances.OverpaidTitle }
        .Select(t => Regex.Match(Part(html, t), @"<button class=""btn""([^>]*)>") is { Success: true } b ? (b.Groups[1].Value.Contains("disabled", StringComparison.Ordinal) ? "disabled" : "enabled") : "none")];

    private static string? Note(string html) => Regex.Match(WebUtility.HtmlDecode(html), @"<p class=""note"">[^<]*?:\s*([-\d.]+) (GEL|USD|EUR)") is { Success: true } m ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : null;

    /// <summary>T in the currency, no schedule; the client's advance of 50.00 (10.10) and S overpaid by 50.00 (150.00 held on 01.09's 150.00 reduced by 50.00 today), so both buttons are there.</summary>
    private async Task<LegacyDaySite.Policy> WorldAsync(string currency)
    {
        var holder = await s.PartyAsync();
        var t = await s.PolicyAsync(currency, currency == "GEL" ? 1m : 2.7m, holder: holder);
        var other = await s.PolicyAsync(holder: holder);
        var s1 = await s.InstallmentAsync(other, "2026-09-01", "150.00");
        await s.HeldAsync(other, s1, "150.00", "2026-09-07");
        await s.AdjustAsync(s1, "-50.00", "2026-11-03");
        await s.Site.ExecAsync("insert core.payment (received_on, amount_gel, method, kind, payer_party_id) values ('2026-10-10', 50, N'bank', N'receipt', @holder)", new { holder });
        return t;
    }

    [Theory]
    [InlineData("accountant")]
    [InlineData("admin")]
    public async Task No_schedule_no_credit_no_rate_today_0_00_in_USD_or_EUR_both_buttons_disabled_the_receipt_form_the_bank_asked_once_for_today_nothing_written(string who)
    {
        var client = who == "admin" ? s.Admin : s.Accountant;
        lock (s.Site.Nbg.Requests) s.Site.Nbg.Requests.Clear();
        var gel = await LegacyDaySite.RawAsync(client, $"/policies/{(await WorldAsync("GEL")).Id}/payment");
        Assert.Equal(("0.00 GEL", "disabled|disabled", true), (Note(gel), string.Join("|", Buttons(gel)), gel.Contains("name=\"amount_gel\"", StringComparison.Ordinal)));
        Assert.Empty(s.Site.Nbg.Requests);
        foreach (var currency in new[] { "USD", "EUR" })
        {
            var t = await WorldAsync(currency);
            var before = await s.LedgerAsync();
            lock (s.Site.Nbg.Requests) s.Site.Nbg.Requests.Clear();
            var html = await LegacyDaySite.RawAsync(client, $"/policies/{t.Id}/payment");
            Assert.Equal(($"0.00 {currency}", "disabled|disabled", true), (Note(html), string.Join("|", Buttons(html)), html.Contains("name=\"amount_gel\"", StringComparison.Ordinal)));
            // Today's rate read as before: the bank asked once, for today; its refusal not stored, nothing written.
            var asked = Assert.Single(s.Site.Nbg.Requests);
            Assert.EndsWith("currencies=USD&currencies=EUR&date=2026-11-03", asked, StringComparison.Ordinal);
            Assert.Equal(before, await s.LedgerAsync());
            Assert.Equal(0, await s.Site.ScalarAsync<int>("select count(*) from core.exchange_rate"));
        }
    }
}
