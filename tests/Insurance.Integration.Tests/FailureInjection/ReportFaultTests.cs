using System.Numerics;
using System.Text.Json;
using Insurance.Application.Data;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// The monthly report's reads failing once and the small world of its bulk reads, on the real SQL Server through the command
/// interceptor, each in a report world (database) of its own (legacy scripts/integration/claim-payment-permission.test.ts "the
/// monthly report valued at the latest stored rate before the month's end", report-bulk-reads.test.ts, and fd-panel.test.ts "over a
/// thousand policies: read in as many statements as over three — none per policy").
/// </summary>
public sealed class ReportFaultTests
{
    private static (Db Db, Faults Faults) Intercepted(ReportWorld w)
    {
        var faults = new Faults();
        return (new Db(new DbOptions { ConnectionString = w.Database.AppConnection, Interceptor = faults }), faults);
    }

    private static string Json(object report) => JsonSerializer.Serialize(report, new JsonSerializerOptions { Converters = { new BigIntegerText() } });

    private sealed class BigIntegerText : System.Text.Json.Serialization.JsonConverter<BigInteger>
    {
        public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    // ================================================================ claim-payment-permission.test.ts (the month report's rate)

    [Fact]
    public async Task The_latest_before_read_fails_once_the_report_rejects_with_that_error_nothing_written_the_retry_gives_the_same_report()
    {
        // The legacy USD world, no payment: claim USD 1 000.00 with its reserve booked 02.10; premium USD 90.00 at the fixed 2.50
        // (GEL 225.00); one instalment USD 90.00 due 01.10. No USD rate on 31.10; the latest before it is 30.10's 2.60.
        await using var w = await CreateAsync();
        var p = await w.PolicyAsync("P-USD", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 90m, 225m)], [("2026-10-01", 90m, "client")], "USD", 2.5m);
        var claim = await w.ClaimAsync(p.Id, p.Lines[0], "USD", "2026-10-01", "2026-10-02", status: "approved");
        await w.ReserveAsync(claim.Id, "2026-10-02", 1000m);
        foreach (var (currency, day, rate) in new[] { ("USD", "2026-10-29", 2.55m), ("USD", "2026-10-30", 2.6m), ("USD", "2026-11-01", 2.65m), ("USD", "2026-11-03", 2.7m), ("USD", "2026-11-30", 2.8m), ("EUR", "2026-10-31", 9m) })
            await w.RateAsync(currency, day, rate);
        var (db, faults) = Intercepted(w);
        var (rates, nbg) = RefusingNbg.For(db);
        Task<PremiumReport.Report> October() => db.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-10"));
        var good = await October();
        // By hand at 30.10's 2.60: RBNS USD 1 000 = GEL 2 600.00; receivable USD 90.00 = GEL 234.00.
        var usd = good.Receivables.Single(r => r.Currency == "USD");
        Assert.Equal((new BigInteger(9000), new BigInteger(23400), 2.6m, D("2026-10-30")), (usd.Outstanding, usd.OutstandingGel, usd.Rate, usd.RateDate));
        Assert.Equal(new BigInteger(260000), good.Claims.Single(c => c.ClassCode == "5.1").RbnsGel);
        var before = await w.FingerprintAsync();
        nbg.Requests.Clear();
        var sentinel = new InjectedFault("RATE_BEFORE_SENTINEL");
        faults.FailOnce(c => Faults.Has(c, "select top (1) rate, rate_date from core.exchange_rate") && Equals(c.Parameter("date"), D("2026-10-31")), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(October));
        // The day's own read and the bank came first (the fallback's order); then the latest-before read failed.
        Assert.Single(nbg.Requests);
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Equal(Json(good), Json(await October()));
        Assert.Equal(before, await w.FingerprintAsync());
    }

    // ================================================================ report-bulk-reads.test.ts

    private sealed record Small(ReportWorld W, Guid A, Guid B, Guid C, Guid A1, Guid A2, Guid A3, Guid B1, Guid B2, Guid C1);

    /// <summary>
    /// A from 01.10 with paid, part-paid and reduced instalments; B from 15.11 with a raised instalment and a part payment; C booked only
    /// in December. A3 reduced by 100.00 on 15.11 and by 50.00 more on 05.12; B2 raised by 30.00 on 20.11; 200.00 on A1 (05.10), 80.00 on
    /// A2 (10.11), 50.00 on A2 received 28.11 but allocated 02.12, 40.00 on B1 (20.11).
    /// </summary>
    private static async Task<Small> SmallAsync()
    {
        var w = await CreateAsync();
        var a = await w.PolicyAsync("P-SMALL-1", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 600m, 600m)], [("2026-10-01", 200m, "client"), ("2026-11-01", 200m, "client"), ("2026-12-01", 200m, "client")]);
        var b = await w.PolicyAsync("P-SMALL-2", "2026-11-15", "2027-11-15", [new LineSpec("5.1", 300m, 300m)], [("2026-11-15", 150m, "client"), ("2027-02-15", 150m, "client")]);
        var c = await w.PolicyAsync("P-SMALL-3", "2026-12-01", "2027-12-01", [new LineSpec("5.1", 100m, 100m)], [("2026-12-01", 100m, "client")], bookedOn: "2026-12-05");
        await w.AdjustAsync(a.Installments[2], "2026-11-15", -100m);
        await w.AdjustAsync(a.Installments[2], "2026-12-05", -50m);
        await w.AdjustAsync(b.Installments[1], "2026-11-20", 30m);
        await w.AllocateAsync(await w.ReceiptAsync("2026-10-05", 200m, a.PartyId), a.Installments[0], 200m, 200m, 1m, "2026-10-05");
        await w.AllocateAsync(await w.ReceiptAsync("2026-11-10", 80m, a.PartyId), a.Installments[1], 80m, 80m, 1m, "2026-11-10");
        await w.AllocateAsync(await w.ReceiptAsync("2026-11-28", 50m, a.PartyId), a.Installments[1], 50m, 50m, 1m, "2026-12-02");
        await w.AllocateAsync(await w.ReceiptAsync("2026-11-20", 40m, b.PartyId), b.Installments[0], 40m, 40m, 1m, "2026-11-20");
        return new Small(w, a.Id, b.Id, c.Id, a.Installments[0], a.Installments[1], a.Installments[2], b.Installments[0], b.Installments[1], c.Installments[0]);
    }

    private static (BigInteger, BigInteger, BigInteger) Gel(PremiumReport.Report r) =>
        r.Receivables.Single(x => x.Currency == "GEL") is var g ? (g.Outstanding, g.Overdue, g.OutstandingGel) : default;

    // By hand, at 30.11: A — 200.00 + 80.00 settle A1 and 80.00 of A2: A2 owes 120.00 (due 01.11, overdue), A3 200.00 − 100.00 = 100.00
    // (not yet due); B — 40.00 on B1: 110.00 (overdue), B2 150.00 + 30.00 = 180.00; C not owed in November. 510.00 owed, 230.00 overdue.
    private static readonly (BigInteger, BigInteger, BigInteger) NovemberGel = (51000, 23000, 51000);

    private static List<string> Schedule(Dictionary<Guid, List<Billing.InstallmentRow>> all, Guid policy) =>
        (all.TryGetValue(policy, out var rows) ? rows : []).Select(r => $"{r.Id}|{r.DueOn:yyyy-MM-dd}|{r.Amount:0.00}").ToList();

    [Fact]
    public async Task Bulk_reads_a_small_world_by_hand_owed_510_overdue_230_each_policys_schedule_its_own()
    {
        var s = await SmallAsync();
        await using var w = s.W;
        var before = await w.FingerprintAsync();
        var november = await w.MonthAsync("2026-11");
        Assert.Equal(NovemberGel, Gel(november));
        // The schedules by policy at 30.11, by hand — and as each policy's own schedule gives them.
        string R(Guid id, string due, string amount) => $"{id}|{due}|{amount}";
        List<string>[] hand = [
            [R(s.A1, "2026-10-01", "200.00"), R(s.A2, "2026-11-01", "200.00"), R(s.A3, "2026-12-01", "100.00")],
            [R(s.B1, "2026-11-15", "150.00"), R(s.B2, "2027-02-15", "180.00")],
            [R(s.C1, "2026-12-01", "100.00")],
        ];
        var at = D("2026-11-30");
        var all = await w.App.ReadTxAsync(tx => Billing.LoadSchedulesAsync(tx, [s.A, s.B, s.C], at));
        Assert.Equal(hand, new[] { s.A, s.B, s.C }.Select(p => Schedule(all, p)).ToArray());
        foreach (var (policy, expected) in new[] { s.A, s.B, s.C }.Zip(hand))
            Assert.Equal(expected, (await w.App.ReadTxAsync(tx => Billing.LoadScheduleAsync(tx, policy, at))).Select(r => $"{r.Id}|{r.DueOn:yyyy-MM-dd}|{r.Amount:0.00}").ToList());
        // At 31.12 A3's second reduction counts as well; nothing of B or C moves.
        var december = await w.App.ReadTxAsync(tx => Billing.LoadSchedulesAsync(tx, [s.A, s.B, s.C], D("2026-12-31")));
        Assert.Equal([hand[0][0], hand[0][1], R(s.A3, "2026-12-01", "50.00")], Schedule(december, s.A));
        Assert.Equal((hand[1], hand[2]), (Schedule(december, s.B), Schedule(december, s.C)), new ListPair());
        Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadSchedulesAsync(tx, [], at)));
        // Read again: the same report; nothing stored changed.
        Assert.Equal(Json(november), Json(await w.MonthAsync("2026-11")));
        Assert.Equal(before, await w.FingerprintAsync());
        // A change on B alone (another 20.00 off B2 on 25.11) moves only B's schedule and the owed total by 20.00.
        await w.AdjustAsync(s.B2, "2026-11-25", -20m);
        var changed = await w.App.ReadTxAsync(tx => Billing.LoadSchedulesAsync(tx, [s.A, s.B, s.C], at));
        Assert.Equal([hand[0], [R(s.B1, "2026-11-15", "150.00"), R(s.B2, "2027-02-15", "160.00")], hand[2]], new[] { s.A, s.B, s.C }.Select(p => Schedule(changed, p)).ToArray());
        Assert.Equal((new BigInteger(49000), new BigInteger(23000), new BigInteger(49000)), Gel(await w.MonthAsync("2026-11")));
    }

    private sealed class ListPair : IEqualityComparer<(List<string>, List<string>)>
    {
        public bool Equals((List<string>, List<string>) x, (List<string>, List<string>) y) => x.Item1.SequenceEqual(y.Item1) && x.Item2.SequenceEqual(y.Item2);
        public int GetHashCode((List<string>, List<string>) obj) => 0;
    }

    /// <summary>The report's three bulk reads (Billing.LoadSchedulesAsync, PremiumReport's allocations), by a stable fragment of each.</summary>
    public static TheoryData<string, string> BulkReads() => new()
    {
        { "the instalments", "select i.policy_id, i.id, i.due_on, i.amount" },
        { "the adjustments", "select j.installment_id, coalesce(sum(j.change), 0)" },
        { "the allocations", "select i.policy_id, a.amount, a.booked_on, p.method" },
    };

    [Theory]
    [MemberData(nameof(BulkReads))]
    public async Task Bulk_reads_the_read_fails_once_the_report_gives_that_error_never_a_report_with_zeros_nothing_stored_changes_the_retry_gives_the_figures(string name, string fragment)
    {
        var s = await SmallAsync();
        await using var w = s.W;
        var before = await w.FingerprintAsync();
        var (db, faults) = Intercepted(w);
        var sentinel = new InjectedFault($"BULK_{name}");
        faults.FailOnce(Faults.Any(fragment), sentinel);
        Task<PremiumReport.Report> November() => db.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates().ValueAsync, "2026-11"));
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(November));
        Assert.True(faults.AllFired);
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Equal(NovemberGel, Gel(await November()));
    }

    // ================================================================ fd-panel.test.ts (the report over a thousand policies)

    [Fact]
    public async Task Over_a_thousand_policies_the_same_report_read_in_as_many_statements_as_over_one_none_per_policy()
    {
        async Task<(int Statements, PremiumReport.Report Report)> CountAsync(ReportWorld w)
        {
            var (db, faults) = Intercepted(w);
            var report = await db.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates().ValueAsync, "2026-11"));
            return (faults.Log.Count, report);
        }
        await using var thousand = await ThousandWorld.CreateAsync();
        await using var one = await CreateAsync();
        var q = await one.PolicyAsync("P-TEST-Q", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 120m, 120m)], [("2026-10-01", 120m, "client")]);
        await one.PayAsync(q.Installments[0], "2026-10-05", 120m);
        await one.PolicyAsync("P-TEST-0000", "2026-11-01", "2027-11-01", [new LineSpec("5.1", 300m, 300m), new LineSpec("5.1", 100m, 100m)],
            [("2026-11-01", 100m, "client"), ("2027-02-01", 100m, "client"), ("2027-05-01", 100m, "client"), ("2027-08-01", 100m, "client")]);
        var small = await CountAsync(one);
        var big = await CountAsync(thousand);
        Assert.Equal(small.Statements, big.Statements);
        var c = Assert.Single(big.Report.Classes);
        Assert.Equal((1000, new BigInteger(3_288_986), new BigInteger(36_721_995)), (c.PoliciesIssued, c.EarnedGel, c.UnearnedGel));
        Assert.Equal((new BigInteger(30_000_000), BigInteger.Zero, new BigInteger(30_000_000)), Gel(big.Report));
    }
}
