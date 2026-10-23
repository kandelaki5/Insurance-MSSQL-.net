using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Domain.Regulatory;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The fictional year of the profit-and-loss reader test (old program PR #366, scripts/integration/income-statement-reader.test.ts),
/// as rows on SQL Server: IncomeStatementReaderTests reads it, and the supervisory forms page's tests show it over HTTP.
/// </summary>
public static class IncomeStatementBook
{
    /// <summary>The fictional book and the names of its ids.</summary>
    public sealed class Book
    {
        public required ReportWorld W { get; init; }
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Guid> Ids { get; } = new(StringComparer.Ordinal);
        public void Name(string name, Guid id) { Names[id.ToString("D")] = name; Ids[name] = id; }
        public string Of(string id) => Names.TryGetValue(id, out var n) ? n : id;
    }

    /// <summary>
    /// The fictional book (GEL unless said). Policies: PA corporate 5.1, issued 31.12.2025 (730.00, 2.00 a day), cancelled 01.03.2026
    /// (refund 610.00), an agent's 10 %; PB retail 5.1, USD 1000.00 at its fixed 2.70 (2700.00), issued 01.01.2026, a broker's 15 %;
    /// PC retail 5.1, PB's change /1 — a new policy (owner) — issued 01.06.2026 at −100.00 USD (−270.00); PD state 13, issued 01.11.2026
    /// (after the period); PE corporate 13, 600.00 issued 01.02.2026, a bank's 5 % and an employee's 4 %; PF corporate, one policy on 5.1
    /// (365.00) and 13 (73.00), issued 15.01.2026. Cessions: PA 30 % (219.00, commission 21.93) on 31.12.2025, cancelled with it (−183.00,
    /// −18.33); PB 30 % (810.00, 81.00). Losses: event EV1 on PF hit 5.1 (K1: 300.00 + 200.00, and 10.00 paid in 2025; a reserve of 80.00
    /// since 2025) and 13 (K2: 50.00); EV2 on PB (K3: USD 100.00 at 2.75, a quota share's 30 %; a reserve of USD 50.00 from 10.05.2026);
    /// EV3 on PB (K4: USD 1000.00 at 2.70, an XL's 400.00 USD); EV4 on PE paid 05.11.2026 (after). Recoveries on K1: 5.00 (2025), 40.00
    /// (05.08.2026), 15.00 (after). Treaty premium: XL-1 on 5.1 365.00 for 2026 (1.00 a day), XL-2 on 13 and 5.1 USD 730.00 for 2026 (2.00
    /// USD a day), QS-D, a quota share's deposit premium 36.50 on 5.1, XL-0 of 2025. USD rates: 2.65 at 31.12.2025, 2.70 from 31.01, 2.72
    /// from 31.05, 2.80 from 31.07, 2.75 from 31.10.2026.
    /// </summary>
    public static async Task<Book> BookAsync()
    {
        var w = await CreateAsync();
        var b = new Book { W = w };
        foreach (var (on, rate) in new[] { ("2025-12-31", 2.65m), ("2026-01-31", 2.70m), ("2026-05-31", 2.72m), ("2026-07-31", 2.80m), ("2026-10-31", 2.75m) })
            await w.RateAsync("USD", on, rate);

        // Treaties: the premium ones read by the reader, and the ones the cessions and claim cessions are on (no premium of their own).
        var x0 = await w.TreatyAsync("XL-0", "excess_of_loss", "2025-01-01", "2025-12-31", ["5.1"], priority: 1000m, limit: 10000m, treatyPremium: 100m);
        var x1 = await w.TreatyAsync("XL-1", "excess_of_loss", "2026-01-01", "2026-12-31", ["5.1"], priority: 1000m, limit: 10000m, treatyPremium: 365m);
        var x2 = await w.TreatyAsync("XL-2", "excess_of_loss", "2026-01-01", "2026-12-31", ["5.1", "13"], currency: "USD", priority: 1000m, limit: 10000m, treatyPremium: 730m);
        var q1 = await w.TreatyAsync("QS-D", "quota_share", "2026-01-01", "2026-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 10m, treatyPremium: 36.50m);
        var q2 = await w.TreatyAsync("QS-P", "quota_share", "2025-01-01", "2026-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 10m);
        var qc = await w.TreatyAsync("QS-C", "quota_share", "2026-01-01", "2026-12-31", ["5.1"], currency: "USD", cededPercent: 30m);
        var xc = await w.TreatyAsync("XL-C", "excess_of_loss", "2026-01-01", "2026-12-31", ["5.1"], currency: "USD", priority: 600m, limit: 1000m);
        foreach (var (n, id) in new[] { ("X0", x0), ("X1", x1), ("X2", x2), ("Q1", q1), ("Q2", q2), ("QC", qc), ("XC", xc) }) b.Name(n, id);

        async Task<PolicyRef> Policy(string name, string segment, string from, string to, LineSpec[] lines, string currency = "GEL", decimal rate = 1m, string? bookedOn = null)
        {
            var p = await w.PolicyAsync(name, from, to, lines, currency: currency, rate: rate, bookedOn: bookedOn);
            await w.ExecAsync("update core.policy set segment = @segment where id = @id", new { segment, id = p.Id });
            b.Name(name, p.Id);
            return p;
        }
        var pa = await Policy("PA", "corporate", "2025-12-31", "2026-12-31", [new LineSpec("5.1", 730m, 730m)]);
        var pb = await Policy("PB", "retail", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 1000m, 2700m)], "USD", 2.7m);
        var pf = await Policy("PF", "corporate", "2026-01-15", "2027-01-15", [new LineSpec("5.1", 365m, 365m), new LineSpec("13", 73m, 73m)]);
        var pe = await Policy("PE", "corporate", "2026-02-01", "2027-02-01", [new LineSpec("13", 600m, 600m)]);
        var ta2 = await w.TxAsync(pa.Lines[0], "cancellation", -610m, -610m, "2026-03-01", "2026-03-01", "2026-12-31", cancels: pa.IssueRows[0]);
        await Policy("PC", "retail", "2026-06-01", "2027-01-01", [new LineSpec("5.1", -100m, -270m)], "USD", 2.7m);
        var pd = await Policy("PD", "state", "2026-11-01", "2027-11-01", [new LineSpec("13", 500m, 500m)]);

        var ca1 = await w.CessionAsync(pa.IssueRows[0], q2, 30m, 219m, 219m, "2025-12-31", "2025-12-31", "2026-12-31", 21.93m, 21.93m);
        await w.CessionAsync(pb.IssueRows[0], q1, 30m, 300m, 810m, "2026-01-01", "2026-01-01", "2027-01-01", 30m, 81m);
        await w.CessionAsync(ta2, q2, 30m, -183m, -183m, "2026-03-01", "2026-03-01", "2026-12-31", -18.33m, -18.33m, cancels: ca1);

        // The intermediaries: PA's agent 10 %, PB's broker 15 %, PE's bank 5 % and employee 4 %.
        foreach (var (policy, kind, percent) in new[] { (pa.Id, "agent", 10m), (pb.Id, "broker", 15m), (pe.Id, "bank", 5m), (pe.Id, "employee", 4m) })
        {
            var party = await w.PartyAsync($"შუამავალი {kind} (გამოგონილი)");
            var intermediary = Guid.NewGuid();
            await w.ExecAsync("insert core.intermediary (id, party_id, kind) values (@intermediary, @party, @kind)", new { intermediary, party, kind });
            await w.ExecAsync("insert core.policy_intermediary (policy_id, intermediary_id, commission_percent) values (@policy, @intermediary, @percent)", new { policy, intermediary, percent });
        }

        // Claims: K1 and K2 one event (EV1) on PF's two lines; K3, K4 on PB; K5 on PE.
        var k1 = await w.ClaimAsync(pf.Id, pf.Lines[0], "GEL", "2025-12-10", "2025-12-12", number: "K1");
        var ev1 = await w.App.ReadAsync(c => c.ExecuteScalarAsync<Guid>("select loss_event_id from core.claim where id = @id", new { id = k1.Id }));
        var k2 = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.claim (id, number, policy_id, policy_line_id, currency, occurred_on, reported_on, description, status, loss_event_id, created_at, created_by)
            values (@k2, N'K2', @policy, @line, N'GEL', '2025-12-10', '2025-12-12', N'სატესტო ზარალი', N'in_review', @ev1, @at, @user)
            """, new { k2, policy = pf.Id, line = pf.Lines[1], ev1, at = w.At(), user = w.Admin });
        var k3 = await w.ClaimAsync(pb.Id, pb.Lines[0], "USD", "2026-05-01", "2026-05-02", number: "K3");
        var k4 = await w.ClaimAsync(pb.Id, pb.Lines[0], "USD", "2026-07-01", "2026-07-02", number: "K4");
        var k5 = await w.ClaimAsync(pe.Id, pe.Lines[0], "GEL", "2026-11-01", "2026-11-02", number: "K5");
        foreach (var (n, id) in new[] { ("K1", k1.Id), ("K2", k2), ("K3", k3.Id), ("K4", k4.Id), ("K5", k5.Id) })
        {
            b.Name(n, id);
            var evt = await w.App.ReadAsync(c => c.ExecuteScalarAsync<Guid>("select loss_event_id from core.claim where id = @id", new { id }));
            b.Names[evt.ToString("D")] = n switch { "K1" or "K2" => "EV1", "K3" => "EV2", "K4" => "EV3", _ => "EV4" };
        }
        await w.ClaimCessionAsync(k3.Id, qc, 30m);
        await w.ClaimCessionAsync(k4.Id, xc, 100m, 600m, 1000m);
        await w.ReserveAsync(k1.Id, "2025-12-12", 80m);
        await w.ReserveAsync(k3.Id, "2026-05-10", 50m);
        await w.ClaimPaymentAsync(k1.Id, "2025-12-20", 10m);
        await w.ClaimPaymentAsync(k1.Id, "2026-03-10", 300m);
        await w.ClaimPaymentAsync(k2, "2026-03-10", 50m);
        await w.ClaimPaymentAsync(k1.Id, "2026-04-10", 200m);
        await w.ClaimPaymentAsync(k3.Id, "2026-05-15", 100m, 2.75m);
        await w.ClaimPaymentAsync(k4.Id, "2026-07-20", 1000m, 2.70m);
        await w.ClaimPaymentAsync(k5.Id, "2026-11-05", 70m);
        await w.RecoveryAsync(k1.Id, "salvage", "2025-12-31", 5m);
        await w.RecoveryAsync(k1.Id, "salvage", "2026-08-05", 40m);
        await w.RecoveryAsync(k1.Id, "salvage", "2026-11-02", 15m);
        _ = pd;
        return b;
    }
}
