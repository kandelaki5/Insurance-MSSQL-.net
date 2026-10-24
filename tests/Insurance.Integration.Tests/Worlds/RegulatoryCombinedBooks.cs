using Dapper;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The fictional books of the old program's combined reporting check (Insurance-Software PR #363, scripts/integration/regulatory-combined.test.ts),
/// written as real rows on SQL Server through the application role (every guard of the schema applies). Each book is a database of its
/// own; "today" is 15.11.2026 (the old test's), the company is „სატესტო კომპანია (გამოგონილი)". The old test's own controls that changed
/// one raw row (a payment's day, a rate, a share, an intermediary's kind) are the options of each book: the same rows with that one
/// changed. Where the schema refuses an old raw row as it was, the adaptation is named at the option.
/// </summary>
public static class RegulatoryCombinedBooks
{
    private static async Task<ReportWorld> NewAsync()
    {
        var w = await CreateAsync();
        w.Database.Clock.SetToday(D("2026-11-15"));
        await using var c = await w.Database.OpenAdminAsync();
        await c.ExecuteAsync("update core.setting set value = N'სატესტო კომპანია (გამოგონილი)' where [key] = N'company_name'");
        return w;
    }

    private static Task SegmentAsync(ReportWorld w, Guid policy, string segment) =>
        w.ExecAsync("update core.policy set segment = @segment where id = @policy", new { segment, policy });

    /// <summary>An intermediary of a kind (its own party) on policies, each at its percent.</summary>
    private static async Task IntermediaryAsync(ReportWorld w, Guid party, string kind, params (Guid Policy, decimal Percent)[] on)
    {
        var intermediary = Guid.NewGuid();
        await w.ExecAsync("insert core.intermediary (id, party_id, kind) values (@intermediary, @party, @kind)", new { intermediary, party, kind });
        foreach (var (policy, percent) in on)
            await w.ExecAsync("insert core.policy_intermediary (policy_id, intermediary_id, commission_percent) values (@policy, @intermediary, @percent)", new { policy, intermediary, percent });
    }

    /// <summary>A second claim of the loss event of <paramref name="first"/>, on another line of the same policy (core.claim allows one claim per event and line).</summary>
    public static async Task<Guid> SameEventClaimAsync(ReportWorld w, Guid first, Guid policy, Guid line, string currency, string number)
    {
        var id = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.claim (id, number, policy_id, policy_line_id, currency, occurred_on, reported_on, description, status, loss_event_id, created_at, created_by)
            select @id, @number, @policy, @line, @currency, occurred_on, reported_on, N'სატესტო ზარალი', N'in_review', loss_event_id, @at, @user from core.claim where id = @first
            """, new { id, number, policy, line, currency, first, at = w.At(), user = w.Admin });
        return id;
    }

    // ---------------------------------------------------------------- the first book

    /// <summary>
    /// The first book's controls. <see cref="BankAsAgent"/>: PB's bank recorded as an agent. <see cref="AllocatedOn"/>: the day PA's 400.00
    /// is allocated (the receipt stays received on 01.02.2026). <see cref="UsdOctober"/>: the NBG USD rate stored for 31.10.2026.
    /// <see cref="ThirteenOnFiveOne"/>: the old control recorded L1's 13 claim on PA's 5.1 line; core.claim allows one claim per loss
    /// event and line (claim_event_coverage), so here the 13 claim's reserve and payment are K1's — L1 with one claim, on 5.1 only.
    /// </summary>
    public sealed record Book1Options(bool BankAsAgent = false, string AllocatedOn = "2026-02-01", decimal UsdOctober = 2.75m, bool ThirteenOnFiveOne = false);

    /// <summary>The first book's ids the tests name.</summary>
    public sealed record Book1(ReportWorld W, Guid PolicyA, Guid LineA51, Guid K1);

    /// <summary>
    /// The book, 2026. PA: corporate, GEL, for 01.01.2026–01.01.2027: 5.1 730.00 (2.00 a day) and 13 73.00 (0.20 a day), one instalment
    /// of 803.00, 400.00 paid on 01.02.2026, an agent's 10 %; its 5.1 premium 30 % ceded to QS-1 (219.00, commission 21.90). PB: retail,
    /// GEL, 13, 365.00 for 01.03.2026–01.03.2027 (1.00 a day), unpaid, a bank's 5 %; 100 % ceded to QS-2 (365.00, commission 73.00), 20.00
    /// of which the reinsurer settled on 01.09.2026. PC: retail, USD, 5.1, 100.00 USD for 01.07.2026–01.07.2027 at its fixed 2.70
    /// (270.00), unpaid; the NBG rate on 31.10.2026 2.75. XL-1: an excess-of-loss treaty on 5.1, its own premium 365.00 for 2026. Loss L1
    /// on PA hit both its classes: 5.1 reserve 100.00 (01.05), 60.00 paid (10.05); 13 reserve 30.00, 20.00 paid; each reserve down by its
    /// payment; 10.00 recovered on 5.1 (01.08). QS-1 has a broker (GB); its reinsurer is rated A by AM Best, in DE.
    /// Adapted to the schema: an excess-of-loss treaty needs a priority (XL-1: 1 000.00, limit 10 000.00 — no claim is ceded to it).
    /// </summary>
    public static async Task<Book1> Book1Async(Book1Options? o = null)
    {
        o ??= new Book1Options();
        var w = await NewAsync();
        var h1 = await w.PartyAsync("კორპორატიული დამზღვევი (გამოგონილი)");
        var h2 = await w.PartyAsync("დამზღვევი საცალო (გამოგონილი)", kind: "person");
        var r1 = await w.PartyAsync("Re One (გამოგონილი)", foreignId: "RE-ONE", country: "DE");
        var b1 = await w.PartyAsync("Broker One (გამოგონილი)", foreignId: "BR-ONE", country: "GB");
        var a1 = await w.PartyAsync("აგენტი გამოგონილი", kind: "person");
        var k1Party = await w.PartyAsync("ბანკი (გამოგონილი)");
        var c2 = await w.ContractAsync("C-2", h2, "2026-03-01");

        var pa = await w.PolicyAsync("P-A", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 730m, 730m), new LineSpec("13", 73m, 73m)], [("2026-01-01", 803m, "client")], holder: h1);
        var pb = await w.PolicyAsync("P-B", "2026-03-01", "2027-03-01", [new LineSpec("13", 365m, 365m)], [("2026-03-01", 365m, "client")], holder: h2, contractId: c2);
        var pc = await w.PolicyAsync("P-C", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 100m, 270m)], [("2026-07-01", 100m, "client")], currency: "USD", rate: 2.7m, holder: h2, contractId: c2);
        await SegmentAsync(w, pb.Id, "retail");
        await SegmentAsync(w, pc.Id, "retail");
        await w.RateAsync("USD", "2026-10-31", o.UsdOctober);
        var pay = await w.ReceiptAsync("2026-02-01", 400m, h1);
        await w.AllocateAsync(pay, pa.Installments[0], 400m, 400m, 1m, o.AllocatedOn);

        var qs1 = await w.TreatyAsync("QS-1", "quota_share", "2026-01-01", "2026-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 10m, reinsurer: r1, broker: b1, rating: "A", agency: "AM Best");
        var qs2 = await w.TreatyAsync("QS-2", "quota_share", "2026-01-01", "2026-12-31", ["13"], cededPercent: 100m, commissionPercent: 20m, reinsurer: r1);
        await w.TreatyAsync("XL-1", "excess_of_loss", "2026-01-01", "2026-12-31", ["5.1"], priority: 1000m, limit: 10000m, treatyPremium: 365m, reinsurer: r1);
        await w.CessionAsync(pa.IssueRows[0], qs1, 30m, 219m, 219m, "2026-01-01", "2026-01-01", "2027-01-01", 21.90m, 21.90m);
        await w.CessionAsync(pb.IssueRows[0], qs2, 100m, 365m, 365m, "2026-03-01", "2026-03-01", "2027-03-01", 73m, 73m);
        await w.ExecAsync("""
            insert core.ri_commission_settlement (treaty_id, settled_on, kind, currency, amount, reference, created_at, created_by)
            values (@qs2, '2026-09-01', N'receipt', N'GEL', 20.00, N'გამოგონილი', @at, @user)
            """, new { qs2, at = w.At(), user = w.Admin });

        await IntermediaryAsync(w, a1, "agent", (pa.Id, 10m));
        await IntermediaryAsync(w, k1Party, o.BankAsAgent ? "agent" : "bank", (pb.Id, 5m));

        var k1 = await w.ClaimAsync(pa.Id, pa.Lines[0], "GEL", "2026-04-30", "2026-05-01", number: "K-1");
        await w.ReserveAsync(k1.Id, "2026-05-01", 100m);
        await w.ReserveAsync(k1.Id, "2026-05-10", -60m);
        await w.ClaimPaymentAsync(k1.Id, "2026-05-10", 60m);
        var k2 = o.ThirteenOnFiveOne ? k1.Id : await SameEventClaimAsync(w, k1.Id, pa.Id, pa.Lines[1], "GEL", "K-2");
        await w.ReserveAsync(k2, "2026-05-01", 30m);
        await w.ReserveAsync(k2, "2026-05-10", -20m);
        await w.ClaimPaymentAsync(k2, "2026-05-10", 20m);
        await w.RecoveryAsync(k1.Id, "subrogation", "2026-08-01", 10m);
        _ = pc;
        return new Book1(w, pa.Id, pa.Lines[0], k1.Id);
    }

    // ---------------------------------------------------------------- the second book

    /// <summary>The second book's controls: July's month-end rate, the day of KQ1's 10.00 USD payment (and its reserve change), QS-U's share of KQ1.</summary>
    public sealed record Book2Options(decimal UsdJuly = 2.80m, string Kq1LastPaidOn = "2026-11-02", decimal Kq1Share = 50m);

    private static readonly (string On, decimal Rate)[] Book2Rates =
    [
        ("2025-12-31", 2.65m), ("2026-01-31", 2.70m), ("2026-02-28", 2.70m), ("2026-03-31", 2.72m), ("2026-04-30", 2.72m), ("2026-05-31", 2.74m),
        ("2026-06-30", 2.76m), ("2026-07-31", 2.80m), ("2026-08-31", 2.80m), ("2026-09-30", 2.78m), ("2026-10-31", 2.75m),
    ];

    /// <summary>
    /// The second book: USD, with claims ceded. Policyholder HQ is a person (retail today); both policies were issued corporate (owner,
    /// 21.10.2026: „ეს მონაცემი არ იცვლება. არის ის, როგორც გაიცა").
    ///   QA: 5.1, 365.00 USD for 01.07.2025–01.07.2026 (1.00 a day) at its fixed 2.70 (985.50); 50 % ceded to QS-U (182.50 USD, 492.75,
    ///       no commission); cancelled from 01.05.2026, linked to its original: refund 61.00 USD (−164.70), its cession −30.50 USD (−82.35);
    ///       one instalment of 365.00 USD, adjusted by −61.00 on the cancellation, 304.00 USD paid on 10.01.2026 (820.80).
    ///   QB: 5.1, 100.00 USD for 01.04.2026–01.04.2027 at its fixed 2.70 (270.00), unpaid.
    ///   Loss M1 on QA (KQ1, USD, QS-U's 50 %): reserve 200.00 (10.02.2026), 120.00 paid on 15.03.2026 at 2.72 (326.40), the reserve down by
    ///       it; 10.00 more paid on 02.11.2026 at 2.75 (after the day), the reserve down by it.
    ///   Loss M2 on QB (KQ2, USD, XL-U: priority 100.00, limit 500.00): reserve 400.00 (01.06.2026), 300.00 paid on 10.07.2026 at 2.78
    ///       (834.00), the reserve down by it.
    ///   NBG USD rates at each month's end, 31.12.2025 2.65 to 31.10.2026 2.75 (31.07 2.80).
    /// </summary>
    public static async Task<ReportWorld> Book2Async(Book2Options? o = null)
    {
        o ??= new Book2Options();
        var w = await NewAsync();
        foreach (var (on, rate) in Book2Rates) await w.RateAsync("USD", on, on == "2026-07-31" ? o.UsdJuly : rate);
        var hq = await w.PartyAsync("დამზღვევი საცალო (გამოგონილი)", kind: "person");
        var ru = await w.PartyAsync("Re USD (გამოგონილი)", foreignId: "RE-USD", country: "CH");
        var cq = await w.ContractAsync("C-Q", hq, "2025-07-01");
        var qa = await w.PolicyAsync("Q-A", "2025-07-01", "2026-07-01", [new LineSpec("5.1", 365m, 985.50m)], [("2025-07-01", 365m, "client")], currency: "USD", rate: 2.7m,
            holder: hq, contractId: cq, status: "cancelled", cancelledOn: "2026-05-01");
        var qb = await w.PolicyAsync("Q-B", "2026-04-01", "2027-04-01", [new LineSpec("5.1", 100m, 270m)], [("2026-04-01", 100m, "client")], currency: "USD", rate: 2.7m, holder: hq, contractId: cq);
        var cancel = await w.TxAsync(qa.Lines[0], "cancellation", -61m, -164.70m, "2026-05-01", "2026-05-01", "2026-07-01", cancels: qa.IssueRows[0]);
        await w.AdjustAsync(qa.Installments[0], "2026-05-01", -61m);
        var pq = await w.ReceiptAsync("2026-01-10", 820.80m, hq);
        await w.AllocateAsync(pq, qa.Installments[0], 304m, 820.80m, 2.7m, "2026-01-10");

        var qsu = await w.TreatyAsync("QS-U", "quota_share", "2025-01-01", "2026-12-31", ["5.1"], currency: "USD", cededPercent: 50m, reinsurer: ru, rating: "AA", agency: "S&P");
        var xlu = await w.TreatyAsync("XL-U", "excess_of_loss", "2026-01-01", "2026-12-31", ["5.1"], currency: "USD", priority: 100m, limit: 500m, reinsurer: ru, rating: "AA", agency: "S&P");
        var cqa1 = await w.CessionAsync(qa.IssueRows[0], qsu, 50m, 182.50m, 492.75m, "2025-07-01", "2025-07-01", "2026-07-01");
        await w.CessionAsync(cancel, qsu, 50m, -30.50m, -82.35m, "2026-05-01", "2026-05-01", "2026-07-01", cancels: cqa1);

        var kq1 = await w.ClaimAsync(qa.Id, qa.Lines[0], "USD", "2026-02-05", "2026-02-10", number: "KQ-1");
        var kq2 = await w.ClaimAsync(qb.Id, qb.Lines[0], "USD", "2026-05-25", "2026-06-01", number: "KQ-2");
        await w.ClaimCessionAsync(kq1.Id, qsu, o.Kq1Share);
        await w.ClaimCessionAsync(kq2.Id, xlu, 0m, 100m, 500m);
        await w.ReserveAsync(kq1.Id, "2026-02-10", 200m);
        await w.ReserveAsync(kq1.Id, "2026-03-15", -120m);
        await w.ClaimPaymentAsync(kq1.Id, "2026-03-15", 120m, 2.72m);
        await w.ReserveAsync(kq2.Id, "2026-06-01", 400m);
        await w.ReserveAsync(kq2.Id, "2026-07-10", -300m);
        await w.ClaimPaymentAsync(kq2.Id, "2026-07-10", 300m, 2.78m);
        await w.ReserveAsync(kq1.Id, o.Kq1LastPaidOn, -10m);
        await w.ClaimPaymentAsync(kq1.Id, o.Kq1LastPaidOn, 10m, 2.75m);
        return w;
    }

    // ---------------------------------------------------------------- the third book

    /// <summary>The third book's control: the day of Z1's 2025 payment of 100.00 USD (and its reserve change).</summary>
    public sealed record Book3Options(string Z1FirstPaidOn = "2025-07-10");

    private static readonly (string On, decimal Rate)[] Book3Rates =
    [
        ("2025-12-31", 2.65m), ("2026-01-31", 2.70m), ("2026-02-28", 2.72m), ("2026-03-31", 2.72m), ("2026-04-30", 2.74m), ("2026-05-31", 2.74m),
        ("2026-06-30", 2.76m), ("2026-07-31", 2.80m), ("2026-08-31", 2.80m), ("2026-09-30", 2.78m), ("2026-10-31", 2.75m),
    ];

    /// <summary>The third book's claims.</summary>
    public sealed record Book3(ReportWorld W, Guid Z1, Guid Z2);

    /// <summary>
    /// The third book: the prior year's ceded claims carried over. PZ: 5.1, corporate, 365.00 USD for 01.03.2025–01.03.2026 at its fixed
    /// 2.70 (985.50), paid in full on issue; nothing of the premium ceded.
    ///   Z1 (loss E1, QS-Z's 50 %): reserve 300.00 (01.06.2025); 100.00 paid on 10.07.2025 at 2.70 (270.00); 80.00 paid on 15.02.2026 at
    ///       2.72 (217.60); each payment takes the reserve down by itself.
    ///   Z2 (loss E2, XL-Z: priority 100.00, limit 500.00): reserve 400.00 (01.09.2025); 250.00 paid on 20.11.2025 at 2.66 (665.00); 100.00
    ///       paid on 10.04.2026 at 2.73 (273.00); the reserve down by each.
    ///   NBG USD rates at 31.12.2025 (2.65) and each month's end of 2026 to 31.10 (2.75; 30.04 2.74).
    /// </summary>
    public static async Task<Book3> Book3Async(Book3Options? o = null)
    {
        o ??= new Book3Options();
        var w = await NewAsync();
        foreach (var (on, rate) in Book3Rates) await w.RateAsync("USD", on, rate);
        var hz = await w.PartyAsync("დამზღვევი Z (გამოგონილი)");
        var rz = await w.PartyAsync("Re Z (გამოგონილი)", foreignId: "RE-Z", country: "DE");
        var pz = await w.PolicyAsync("P-Z", "2025-03-01", "2026-03-01", [new LineSpec("5.1", 365m, 985.50m)], [("2025-03-01", 365m, "client")], currency: "USD", rate: 2.7m, holder: hz);
        var paid = await w.ReceiptAsync("2025-03-01", 985.50m, hz);
        await w.AllocateAsync(paid, pz.Installments[0], 365m, 985.50m, 2.7m, "2025-03-01");
        var qsz = await w.TreatyAsync("QS-Z", "quota_share", "2025-01-01", "2026-12-31", ["5.1"], currency: "USD", cededPercent: 50m, reinsurer: rz);
        var xlz = await w.TreatyAsync("XL-Z", "excess_of_loss", "2025-01-01", "2026-12-31", ["5.1"], currency: "USD", priority: 100m, limit: 500m, reinsurer: rz);
        var z1 = await w.ClaimAsync(pz.Id, pz.Lines[0], "USD", "2025-05-25", "2025-06-01", number: "Z-1");
        var z2 = await w.ClaimAsync(pz.Id, pz.Lines[0], "USD", "2025-08-25", "2025-09-01", number: "Z-2");
        await w.ClaimCessionAsync(z1.Id, qsz, 50m);
        await w.ClaimCessionAsync(z2.Id, xlz, 0m, 100m, 500m);
        await w.ReserveAsync(z1.Id, "2025-06-01", 300m);
        await w.ReserveAsync(z1.Id, o.Z1FirstPaidOn, -100m);
        await w.ClaimPaymentAsync(z1.Id, o.Z1FirstPaidOn, 100m, 2.70m);
        await w.ReserveAsync(z2.Id, "2025-09-01", 400m);
        await w.ReserveAsync(z2.Id, "2025-11-20", -250m);
        await w.ClaimPaymentAsync(z2.Id, "2025-11-20", 250m, 2.66m);
        await w.ReserveAsync(z1.Id, "2026-02-15", -80m);
        await w.ClaimPaymentAsync(z1.Id, "2026-02-15", 80m, 2.72m);
        await w.ReserveAsync(z2.Id, "2026-04-10", -100m);
        await w.ClaimPaymentAsync(z2.Id, "2026-04-10", 100m, 2.73m);
        return new Book3(w, z1.Id, z2.Id);
    }

    // ---------------------------------------------------------------- the fourth book

    private static readonly (string On, decimal Rate)[] Book4Rates =
    [
        ("2025-12-31", 2.65m), ("2026-01-31", 2.90m), ("2026-02-28", 2.95m), ("2026-03-31", 2.95m), ("2026-04-30", 2.95m), ("2026-05-31", 2.95m),
        ("2026-06-30", 2.95m), ("2026-07-31", 2.95m), ("2026-08-31", 2.95m), ("2026-09-30", 2.95m), ("2026-10-31", 3.00m), ("2026-11-30", 3.05m),
    ];

    /// <summary>The fourth book's policies.</summary>
    public sealed record Book4(ReportWorld W, Guid Ka, Guid Kb, Guid Kc);

    /// <summary>
    /// The fourth book: a bank's commission across the year's boundary (owner, 21.10.2026: charged in full as it accrues, never deferred).
    /// One bank (5 %) on three corporate policies of 5.1, each paid in full on issue except the future one:
    ///   KA: GEL 365.00 booked 01.07.2025, cover 01.07.2025–01.07.2026 — the bank's 18.25 is 2025's; no movement in 2026.
    ///   KB: USD 365.00 at its fixed 2.70 (985.50) booked 01.01.2026, cover 01.01.2026–01.01.2027 — the bank's 18.25 USD at 2.70 = 49.275 →
    ///       49.28.
    ///   KC: GEL 730.00 booked 01.11.2026, cover 01.11.2026–01.11.2027, unpaid — after January and October: the bank's 36.50 in neither.
    /// NBG USD rates differ from the policy's (31.12.2025 2.65, 31.01.2026 2.90, February–September 2.95, 31.10.2026 3.00, 30.11.2026 3.05).
    /// </summary>
    public static async Task<Book4> Book4Async()
    {
        var w = await NewAsync();
        foreach (var (on, rate) in Book4Rates) await w.RateAsync("USD", on, rate);
        var hk = await w.PartyAsync("დამზღვევი K (გამოგონილი)");
        var bk = await w.PartyAsync("ბანკი K (გამოგონილი)");
        var ck = await w.ContractAsync("C-K", hk, "2025-07-01");
        var ka = await w.PolicyAsync("P-KA", "2025-07-01", "2026-07-01", [new LineSpec("5.1", 365m, 365m)], [("2025-07-01", 365m, "client")], holder: hk, contractId: ck);
        var kb = await w.PolicyAsync("P-KB", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 985.50m)], [("2026-01-01", 365m, "client")], currency: "USD", rate: 2.7m, holder: hk, contractId: ck);
        var kc = await w.PolicyAsync("P-KC", "2026-11-01", "2027-11-01", [new LineSpec("5.1", 730m, 730m)], [("2026-11-01", 730m, "client")], holder: hk, contractId: ck);
        var pka = await w.ReceiptAsync("2025-07-01", 365m, hk);
        await w.AllocateAsync(pka, ka.Installments[0], 365m, 365m, 1m, "2025-07-01");
        var pkb = await w.ReceiptAsync("2026-01-01", 985.50m, hk);
        await w.AllocateAsync(pkb, kb.Installments[0], 365m, 985.50m, 2.7m, "2026-01-01");
        await IntermediaryAsync(w, bk, "bank", (ka.Id, 5m), (kb.Id, 5m), (kc.Id, 5m));
        return new Book4(w, ka.Id, kb.Id, kc.Id);
    }

    // ---------------------------------------------------------------- the fifth and sixth books

    /// <summary>
    /// The fifth book (owner, 21.10.2026): a quota share's own deposit premium, earned by day and split over its classes in proportion to the
    /// premium ceded under it and earned in the period. QS-Z (quota share, GEL, 2026, classes 5.1 and 13) has a deposit premium of 365.00
    /// (1.00 a day) and 50 % of two policies, each paid in full on issue:
    ///   P5A: 5.1, 365.00 for 01.01.2026–01.01.2027 — ceded 182.50, earned by 31.10 304 of 365 days: 152.00.
    ///   P5B: 13, 365.00 for 01.07.2026–01.07.2027 — ceded 182.50 on 01.07, earned by 31.10 123 of 365 days: 61.50.
    /// The sixth (<paramref name="priorYear"/>, Codex INS-QS-DEPOSIT-PRIOR-YEAR-CEDED-KEY-OFFLINE-20261021): the same with the treaty begun
    /// the year before — QS-Z 01.07.2025–31.12.2026 (549 days), deposit 549.00; P5A for 01.07.2025–01.07.2026, booked, paid and ceded on
    /// 01.07.2025: earned 92.00 of its ceded 182.50 by 31.12.2025, the rest, 90.50, in 2026.
    /// No commission, no claim, no intermediary.
    /// </summary>
    public static async Task<ReportWorld> DepositBookAsync(bool priorYear)
    {
        var w = await NewAsync();
        var h5 = await w.PartyAsync("დამზღვევი 5 (გამოგონილი)");
        var r5 = await w.PartyAsync("Re Five (გამოგონილი)", foreignId: "RE-FIVE", country: "DE");
        var (aFrom, aTo) = priorYear ? ("2025-07-01", "2026-07-01") : ("2026-01-01", "2027-01-01");
        var p5a = await w.PolicyAsync("P-5A", aFrom, aTo, [new LineSpec("5.1", 365m, 365m)], [(aFrom, 365m, "client")], holder: h5);
        var p5b = await w.PolicyAsync("P-5B", "2026-07-01", "2027-07-01", [new LineSpec("13", 365m, 365m)], [("2026-07-01", 365m, "client")], holder: h5);
        await w.PayAsync(p5a.Installments[0], aFrom, 365m);
        await w.PayAsync(p5b.Installments[0], "2026-07-01", 365m);
        var qz = await w.TreatyAsync("QS-Z", "quota_share", priorYear ? "2025-07-01" : "2026-01-01", "2026-12-31", ["5.1", "13"], cededPercent: 50m,
            treatyPremium: priorYear ? 549m : 365m, reinsurer: r5);
        await w.CessionAsync(p5a.IssueRows[0], qz, 50m, 182.50m, 182.50m, aFrom, aFrom, aTo);
        await w.CessionAsync(p5b.IssueRows[0], qz, 50m, 182.50m, 182.50m, "2026-07-01", "2026-07-01", "2027-07-01");
        return w;
    }
}
