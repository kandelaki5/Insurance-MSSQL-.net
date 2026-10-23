using Dapper;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The action level of legacy scripts/number-concurrency-regression.ts (Codex INS-DOCUMENT-NUMBER-CONCURRENCY, acceptance
/// AG-T1/AG-T4 and the second half of AG-P6): the application's own IssuePolicy 20 times at once — every policy and contract
/// gets its own number and each policy points at its own contract — and a client flagged sanctions_blocked refused, judged
/// by its own rows only, while another client's policy is issued at the same moment (Codex H41: the shared counters move
/// with anyone's issue, so they are only observed). The SQL level of that script (core.next_number) is ported elsewhere.
/// </summary>
public sealed class LegacyIssueConcurrencyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private const int Parallel = 20;
    private readonly World w = new(db);

    private static Dictionary<string, string> IssueForm(Guid product, string taxId, string plate) => World.Form(
        ("product_id", product.ToString()), ("policyholder_id_number", taxId), ("make", "Toyota"), ("model", "Prius"), ("plate", plate), ("value", "20000"),
        ("effective_from", "2026-01-01"), ("effective_to", "2027-01-01"), ("currency", "GEL"), ("cov_damage_premium", "365"), ("installments", "1"));

    private static string TaxId() => (400_000_000 + Random.Shared.Next(500_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Twenty_issues_at_once_get_twenty_policy_and_contract_numbers_each_policy_on_its_own_contract()
    {
        var admin = await w.UserAsync();
        var (product, _) = await w.CascoAsync();
        var taxId = TaxId();
        var holder = await w.ClientAsync(taxId);
        var issued = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i =>
            PolicyActions.IssuePolicyAsync(w.App, w.Rates, admin, IssueForm(product, taxId, $"RN{i:D2}"), _ => false)));
        Assert.All(issued, a => Assert.Matches("^/policies/[0-9a-f-]{36}\\?issued=1$", a.Redirect ?? string.Join(",", a.Errors.Values) + a.Message));
        var rows = await w.QueryAsync<(Guid Id, string Number, Guid Contract, string ContractNumber)>(
            "select p.id, p.number, c.id, c.number from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder", new { holder });
        Assert.Equal(Parallel, rows.Count);
        Assert.Equal(Parallel, rows.Select(r => r.Number).Distinct().Count());
        Assert.Equal(Parallel, rows.Select(r => r.ContractNumber).Distinct().Count());
        Assert.Equal(Parallel, rows.Select(r => r.Contract).Distinct().Count());
        Assert.All(rows, r => Assert.True(r.Number.StartsWith("CAS", StringComparison.Ordinal) && r.ContractNumber.StartsWith('C'), $"{r.Number} on {r.ContractNumber}"));
        Assert.Equal(rows.Select(r => r.Id).Order(), issued.Select(a => Guid.Parse(a.Redirect![10..46])).Order());
    }

    [Fact]
    public async Task A_sanctions_blocked_client_is_refused_by_its_own_rows_while_another_clients_policy_is_issued_at_the_same_moment()
    {
        var admin = await w.UserAsync();
        var (product, _) = await w.CascoAsync();
        var otherTax = TaxId();
        var other = await w.ClientAsync(otherTax);
        var sanctionedTax = TaxId();
        var sanctioned = await w.ClientAsync(sanctionedTax);
        await using (var c = await db.OpenAppAsync())
            await c.ExecuteAsync("update core.party set sanctions_blocked = 1 where id = @sanctioned", new { sanctioned });
        var counters = () => w.QueryAsync<(string, long)>("select code, next_value from core.number_sequence where code in (N'contract', N'policy:CASCO') order by code");
        var before = await counters();
        var answers = await Task.WhenAll(
            PolicyActions.IssuePolicyAsync(w.App, w.Rates, admin, IssueForm(product, sanctionedTax, "RNSX"), _ => false),
            PolicyActions.IssuePolicyAsync(w.App, w.Rates, admin, IssueForm(product, otherTax, "RNOT"), _ => false));
        var (refused, issued) = (answers[0], answers[1]);
        Assert.Null(refused.Redirect);
        Assert.Equal(Insurance.Application.Texts.PolicyTexts.Errors.PolicyholderSanctioned, refused.Errors["policyholder_id_number"]);
        Assert.Equal((0, 0), (await w.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @sanctioned", new { sanctioned }),
            await w.ScalarAsync<int>("select count(*) from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @sanctioned", new { sanctioned })));
        Assert.NotNull(issued.Redirect);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @other", new { other }));
        // The shared counters are only observed: they moved by the other client's issue (the old 3a0299f condition would fail here).
        Assert.NotEqual(before, await counters());
    }
}
