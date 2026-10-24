using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests;

/// <summary>The impairment worlds of the legacy tests (impairment-debtors.test.ts world()).</summary>
public static class ImpairmentWorld
{
    public sealed class Debtors
    {
        public required PolicyRef Client, Broker, Agent, Mixed;
        public required Guid Holder;
        public required Guid MixedClientInstallment;
    }

    /// <summary>
    /// The four policies (impairment-debtors.test.ts world()); <paramref name="variant"/>: the mixed client instalment owed by an agent;
    /// <paramref name="usdBroker"/>: the broker's policy in USD (impairment-usd.test.ts world()), 730.00 USD fixed at 2.7000 = 1971.00 GEL,
    /// its 100.00 paid as 100.00 USD = 270.00 GEL at 2.7000.
    /// </summary>
    public static async Task<Debtors> DebtorsAsync(ReportWorld w, bool variant = false, bool usdBroker = false)
    {
        var holder = await w.PartyAsync("ტესტაძე ნინო", "person", taxId: "01001000400");
        async Task<PolicyRef> P(string number, decimal premium, params (string, decimal, string)[] insts) =>
            await w.PolicyAsync(number, "2026-01-01", "2027-01-01", [new LineSpec("5.1", premium, premium)], insts.ToList(), holder: holder);
        var client = await P("P-A-CLIENT", 365m, ("2026-01-01", 365m, "client"));
        var broker = usdBroker
            ? await w.PolicyAsync("P-B-BROKER", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 730m, 1971m)], [("2026-01-01", 730m, "broker")], "USD", 2.7m, holder: holder)
            : await P("P-B-BROKER", 730m, ("2026-01-01", 730m, "broker"));
        var agent = await P("P-C-AGENT", 1095m, ("2026-01-01", 1095m, "agent"));
        var mixed = await P("P-D-MIXED", 1460m, ("2026-01-01", 486.67m, "agent"), ("2026-01-01", 486.67m, "broker"), ("2026-01-01", 486.66m, variant ? "agent" : "client"));
        await w.PayAsync(broker.Installments[0], "2026-01-15", 100m, usdBroker ? 2.7m : 1m);
        await w.PayAsync(agent.Installments[0], "2026-01-15", 200m);
        await w.PayAsync(mixed.Installments[2], "2026-01-15", 50m);
        return new Debtors { Client = client, Broker = broker, Agent = agent, Mixed = mixed, Holder = holder, MixedClientInstallment = mixed.Installments[2] };
    }

}
