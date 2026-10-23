using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The fictional premium ledger of the premium report by policy (legacy scripts/integration/policy-premium-world.ts, owner 14.10.2026), as
/// rows in SQL Server: policies A–G (the liability line of C in class 6.1, the old fake's "10.1" not being a class of the reference), the
/// history variant (B's cancellation brought forward after October closed), the combined variant (C cancelled too) and the change and
/// reinsurance variant (H a change of A; A ceded 30 % with 20 % commission).
/// <list type="bullet">
/// <item>A: GEL CASCO 01.10.2026–01.10.2027 (365 days), 365.00, booked 01.10.</item>
/// <item>B: GEL CASCO 01.09.2026–01.09.2027, 730.00, booked 01.09; cancelled 16.10 (booked 16.10): refund 640.00.</item>
/// <item>C: USD at 2.50, 15.10.2026–15.10.2027, booked 15.10: CASCO 292.00 USD = 730.00, liability 146.00 USD = 365.00.</item>
/// <item>D: GEL CASCO 01.09.2025–01.09.2026, 365.00 — ended before every period here.</item>
/// <item>E: GEL CASCO 20.10.2026–20.10.2027, 365.00, approved late: booked 02.11.2026.</item>
/// <item>F: GEL CASCO 01.09.2026–01.09.2027, 365.00; cancelled from 21.09 but booked 02.10: refund 345.00.</item>
/// <item>G: GEL CASCO 05.10.2026–05.10.2027, 365.00; cancelled 25.10: refund 345.00.</item>
/// </list>
/// </summary>
public static class PolicyPremiumWorld
{
    public const string Liability = "6.1";

    public sealed class Ids
    {
        public Dictionary<string, Guid> Policies { get; } = [];
        public Dictionary<string, Guid> Lines { get; } = [];
        public Dictionary<string, Guid> Rows { get; } = [];
    }

    public enum Variant { Base, History, Combined, CombinedOctoberOnly, ChangeAndReinsurance }

    public static async Task<Ids> WriteAsync(ReportWorld w, Variant variant = Variant.Base)
    {
        var ids = new Ids();
        var h1 = await w.PartyAsync("ტესტი პირველი");
        var h2 = await w.PartyAsync("ტესტი შპს");
        var k1 = await w.ContractAsync("C-T-001", h1);
        var k2 = await w.ContractAsync("C-T-002", h2);
        var k3 = await w.ContractAsync("C-T-003", h1);
        var history = variant is Variant.History or Variant.Combined or Variant.CombinedOctoberOnly;
        async Task Policy(string key, Guid contract, Guid holder, string number, string from, string to, string? cancelled, string currency, decimal rate, params (string Line, string Class)[] lines)
        {
            var p = await w.PolicyAsync(number, from, to, lines.Select(l => new LineSpec(l.Class, 0m, 0m)).ToList(), currency: currency, rate: rate, holder: holder,
                contractId: contract, cancelledOn: cancelled, status: cancelled is null ? "active" : "cancelled", issueRows: false);
            ids.Policies[key] = p.Id;
            for (var i = 0; i < lines.Length; i++) ids.Lines[lines[i].Line] = p.Lines[i];
        }
        await Policy("pA", k1, h1, "T-0001", "2026-10-01", "2027-10-01", null, "GEL", 1m, ("lA", "5.1"));
        await Policy("pB", k2, h2, "T-0002", "2026-09-01", "2027-09-01", history ? "2026-09-21" : "2026-10-16", "GEL", 1m, ("lB", "5.1"));
        var cCancelled = variant switch { Variant.Combined => "2026-10-20", Variant.CombinedOctoberOnly => "2026-10-25", _ => null };
        await Policy("pC", k2, h2, "T-0003", "2026-10-15", "2027-10-15", cCancelled, "USD", 2.5m, ("lC2", Liability), ("lC1", "5.1"));
        await Policy("pD", k3, h1, "T-0004", "2025-09-01", "2026-09-01", null, "GEL", 1m, ("lD", "5.1"));
        await Policy("pE", k3, h1, "T-0005", "2026-10-20", "2027-10-20", null, "GEL", 1m, ("lE", "5.1"));
        await Policy("pF", k3, h1, "T-0006", "2026-09-01", "2027-09-01", "2026-09-21", "GEL", 1m, ("lF", "5.1"));
        await Policy("pG", k1, h1, "T-0007", "2026-10-05", "2027-10-05", "2026-10-25", "GEL", 1m, ("lG", "5.1"));

        async Task Tx(string key, string line, string kind, decimal amount, decimal gel, string booked, string from, string to, string? cancels = null) =>
            ids.Rows[key] = await w.TxAsync(ids.Lines[line], kind, amount, gel, booked, from, to, cancels is null ? null : ids.Rows[cancels]);
        await Tx("tD", "lD", "issue", 365m, 365m, "2025-09-01", "2025-09-01", "2026-09-01");
        await Tx("tB", "lB", "issue", 730m, 730m, "2026-09-01", "2026-09-01", "2027-09-01");
        await Tx("tF", "lF", "issue", 365m, 365m, "2026-09-01", "2026-09-01", "2027-09-01");
        await Tx("tA", "lA", "issue", 365m, 365m, "2026-10-01", "2026-10-01", "2027-10-01");
        await Tx("tFx", "lF", "cancellation", -345m, -345m, "2026-10-02", "2026-09-21", "2027-09-01", "tF");
        await Tx("tG", "lG", "issue", 365m, 365m, "2026-10-05", "2026-10-05", "2027-10-05");
        await Tx("tC1", "lC1", "issue", 292m, 730m, "2026-10-15", "2026-10-15", "2027-10-15");
        await Tx("tC2", "lC2", "issue", 146m, 365m, "2026-10-15", "2026-10-15", "2027-10-15");
        await Tx("tBx", "lB", "cancellation", -640m, -640m, "2026-10-16", "2026-10-16", "2027-09-01", "tB");
        await Tx("tGx", "lG", "cancellation", -345m, -345m, "2026-10-25", "2026-10-25", "2027-10-05", "tG");
        await Tx("tE", "lE", "issue", 365m, 365m, "2026-11-02", "2026-10-20", "2027-10-20");
        if (history)
        {
            await Tx("tBu", "lB", "correction", 640m, 640m, "2026-11-01", "2026-10-16", "2027-09-01", "tBx");
            await Tx("tBy", "lB", "cancellation", -690m, -690m, "2026-11-01", "2026-09-21", "2027-09-01", "tB");
        }
        if (variant is Variant.Combined or Variant.CombinedOctoberOnly)
        {
            await Tx("tCx1", "lC1", "cancellation", -284m, -710m, "2026-10-25", "2026-10-25", "2027-10-15", "tC1");
            await Tx("tCx2", "lC2", "cancellation", -142m, -355m, "2026-10-25", "2026-10-25", "2027-10-15", "tC2");
            if (variant == Variant.Combined)
            {
                await Tx("tCu1", "lC1", "correction", 284m, 710m, "2026-11-01", "2026-10-25", "2027-10-15", "tCx1");
                await Tx("tCu2", "lC2", "correction", 142m, 355m, "2026-11-01", "2026-10-25", "2027-10-15", "tCx2");
                await Tx("tCy1", "lC1", "cancellation", -288m, -720m, "2026-11-01", "2026-10-20", "2027-10-15", "tC1");
                await Tx("tCy2", "lC2", "cancellation", -144m, -360m, "2026-11-01", "2026-10-20", "2027-10-15", "tC2");
            }
        }
        if (variant == Variant.ChangeAndReinsurance)
        {
            var h = await w.PolicyAsync("T-0008", "2026-10-16", "2027-10-01", [new LineSpec("5.1", 0m, 0m)], holder: h1, contractId: k1, replaces: ids.Policies["pA"], issueRows: false);
            ids.Policies["pH"] = h.Id;
            ids.Lines["lH"] = h.Lines[0];
            await Tx("tH", "lH", "issue", -35m, -35m, "2026-10-16", "2026-10-16", "2027-10-01");
            var q = await w.TreatyAsync("RI-T-Q", "quota_share", "2026-01-01", "2027-12-31", ["5"], cededPercent: 30m, commissionPercent: 20m);
            await w.CessionAsync(ids.Rows["tA"], q, 30m, 109.50m, 109.50m, "2026-10-01", "2026-10-01", "2027-10-01", 21.90m, 21.90m);
        }
        return ids;
    }

    /// <summary>A row as [policy, class, currency, premium, statistical, correction, corrected, start, end, cancel, earned, UPR].</summary>
    public static string Flat(Insurance.Application.Services.PolicyPremiumReport.Row r) =>
        string.Join("|", r.PolicyNumber, r.ClassCode, r.Currency, r.Premium, r.StatisticalGel, r.CorrectionGel, r.CorrectedGel, Iso(r.StartDate), Iso(r.EndDate),
            r.CancelDate is { } c ? Iso(c) : "null", r.EarnedGel, r.UnearnedGel);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
