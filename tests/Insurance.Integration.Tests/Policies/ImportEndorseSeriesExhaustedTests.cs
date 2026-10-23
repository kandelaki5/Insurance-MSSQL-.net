using Dapper;
using Insurance.Application.Texts;
using Import = Insurance.Application.Services.PolicyImport;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/number-limit-check.ts, the import and change actions with the policy series run out (Codex H40, PR #34):
/// the form says the series ran out, nothing is written — not even a policy or contract numbered before the refusal — and
/// no counter moves. Its own database: the policy series is changed.
/// </summary>
public sealed class ImportEndorseSeriesExhaustedTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private const string Csv = "make,model,plate,value,installments,premium_damage,limit_damage,deductible_damage\n" +
                               "Toyota,Prius,NL-001-D,20000,1,365,20000,0\nToyota,Prius,NL-002-E,20000,1,365,20000,0\n";

    private Task<string> SeriesStateAsync() => w.ScalarAsync<string>("""
        select string_agg(concat(code, N'=', prefix, N':', next_value), N',') within group (order by code) from core.number_sequence
        """);

    private async Task SetPolicySeriesAsync(long nextValue, string? prefix = null)
    {
        await using var c = await w.Database.OpenAdminAsync();
        await c.ExecuteAsync("update core.number_sequence set next_value = @nextValue, prefix = coalesce(@prefix, prefix) where code = N'policy:CASCO'", new { nextValue, prefix });
    }

    private async Task<(Insurance.Application.Services.CurrentUser User, string TaxId, Guid Holder)> ClientAsync()
    {
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (await w.UserAsync("underwriter"), taxId, await w.ClientAsync(taxId));
    }

    private Task<int> ContractsOf(Guid holder) => w.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @holder", new { holder });
    private Task<int> PoliciesOf(Guid holder) => w.ScalarAsync<int>("select count(*) from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder", new { holder });

    private static Dictionary<string, string> FormFor(string taxId) =>
        World.Form(("policyholder_id_number", taxId), ("effective_from", "2026-10-01"), ("effective_to", "2027-10-01"), ("currency", "GEL"), ("installments", "1"));

    [Fact]
    public async Task ConfirmImport_with_the_policy_series_run_out_writes_no_contract_and_moves_no_counter()
    {
        var (user, taxId, holder) = await ClientAsync();
        var preview = await Import.PreviewAsync(w.App, user, FormFor(taxId), System.Text.Encoding.UTF8.GetBytes(Csv));
        Assert.Null(preview.Message);
        var padding = await w.ScalarAsync<int>("select padding from core.number_sequence where code = N'policy:CASCO'");
        var saved = await w.ScalarAsync<long>("select next_value from core.number_sequence where code = N'policy:CASCO'");
        await SetPolicySeriesAsync((long)Math.Pow(10, padding));
        try
        {
            var before = await SeriesStateAsync();
            var r = await Import.ConfirmAsync(w.App, w.Rates, user, preview.Payload, preview);
            Assert.Equal((Ka.Common.SeriesExhausted, null), (r.Message, r.Redirect));
            Assert.Equal(before, await SeriesStateAsync());
            Assert.Equal((0, 0), (await ContractsOf(holder), await PoliciesOf(holder)));
        }
        finally { await SetPolicySeriesAsync(saved); }
    }

    [Fact]
    public async Task ConfirmImport_of_two_policies_with_one_number_left_rolls_the_first_policy_and_the_contract_back()
    {
        var (user, taxId, holder) = await ClientAsync();
        var preview = await Import.PreviewAsync(w.App, user, FormFor(taxId), System.Text.Encoding.UTF8.GetBytes(Csv));
        Assert.Null(preview.Message);
        var (saved, prefix, padding) = (await w.QueryAsync<(long, string, int)>("select next_value, prefix, padding from core.number_sequence where code = N'policy:CASCO'")).Single();
        var last = (long)Math.Pow(10, padding) - 1;
        await SetPolicySeriesAsync(last, "NLP");
        try
        {
            var before = await SeriesStateAsync();
            var r = await Import.ConfirmAsync(w.App, w.Rates, user, preview.Payload, preview);
            Assert.Equal((Ka.Common.SeriesExhausted, null), (r.Message, r.Redirect));
            Assert.Equal(before, await SeriesStateAsync());
            Assert.Equal((0, 0), (await ContractsOf(holder), await PoliciesOf(holder)));
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.policy where number = @number", new { number = "NLP" + last }));
        }
        finally { await SetPolicySeriesAsync(saved, prefix); }
    }

    [Fact]
    public async Task EndorsePolicy_with_the_policy_series_run_out_issues_no_change_and_moves_no_counter()
    {
        var holder = await w.ClientAsync();
        var original = await w.ActivePolicyAsync(await w.UserAsync("operations_head"), holder, new DateOnly(2026, 10, 1));
        var user = await w.UserAsync("underwriter");
        var padding = await w.ScalarAsync<int>("select padding from core.number_sequence where code = N'policy:CASCO'");
        var saved = await w.ScalarAsync<long>("select next_value from core.number_sequence where code = N'policy:CASCO'");
        await SetPolicySeriesAsync((long)Math.Pow(10, padding));
        try
        {
            var before = await SeriesStateAsync();
            var r = await w.EndorseAsync(user, original.Id, new DateOnly(2026, 10, 11), "10");
            Assert.Equal((Ka.Common.SeriesExhausted, null), (r.Message, r.Redirect));
            Assert.Equal(before, await SeriesStateAsync());
            Assert.Null(await w.ChangeOfAsync(original.Id));
        }
        finally { await SetPolicySeriesAsync(saved); }
        // Negative control of the fixture: with numbers left the same change is issued.
        var issued = await w.EndorseAsync(user, original.Id, new DateOnly(2026, 10, 11), "10");
        Assert.NotNull(issued.Redirect);
        Assert.NotNull(await w.ChangeOfAsync(original.Id));
    }
}
