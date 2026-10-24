using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Integration.Tests.FailureInjection;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The worlds of the old program's open PRs #146–#178 (scripts/integration/advance-*.test.ts, client-credit-*.test.ts,
/// refund-*.test.ts on the stacked chain off old main 5cea59d), on a real SQL Server. Those tests ran the real actions over an
/// in-memory store whose answers they wrote by hand; here the same rows are written to a database of the test and the .NET
/// actions run on it, on the legacy days (the database's clock, DbOptions.Clock, standing on "today" 31.01.2027). The legacy
/// suspension rows (written 29.01.2027 10:00, in force until 03.02.2027 10:00) are written with those instants, so the
/// database's own state rule (Aml, "active" on 31.01.2027) decides — not a hand-written answer as in the legacy tests.
/// The actions run through an application whose every command passes <see cref="Faults"/>, so a test can see which rates
/// were asked, whether the schedule was read, and fail or precede one statement.
/// </summary>
public sealed class OpenPrWorld(TestDatabase db)
{
    public World W { get; } = new(db);
    public Faults Faults { get; } = new();
    public RefusingNbg Nbg { get; } = new();

    /// <summary>The application the actions run on: the test's database and clock, every command through <see cref="Faults"/>.</summary>
    public Db App => new(new DbOptions { ConnectionString = db.AppConnection, Interceptor = Faults, Clock = db.Clock });

    /// <summary>The rates over <see cref="App"/>; a rate that is not stored is asked of an unreachable National Bank (counted in <see cref="Nbg"/>).</summary>
    public Rates Rates => new(App, Nbg);

    public static readonly DateOnly Received = new(2026, 12, 20);
    public static readonly DateOnly Today = new(2027, 1, 31);
    public static readonly DateOnly Start = new(2027, 1, 1);

    /// <summary>The legacy suspensions' instants (Tbilisi): ordered 29.01.2027 10:00, ending 03.02.2027 10:00.</summary>
    public static readonly DateTimeOffset Instructed = new(2027, 1, 29, 10, 0, 0, TimeSpan.FromHours(4));
    public static readonly DateTimeOffset Ends = new(2027, 2, 3, 10, 0, 0, TimeSpan.FromHours(4));

    /// <summary>"Today" is 31.01.2027 (noon in Tbilisi) for the database's application and for the actions.</summary>
    public void OnLegacyToday() => On(W, Today);

    /// <summary>The legacy rate module: USD 2.7 on the receipt day 20.12.2026 (unless <paramref name="receiptDay"/> is false) and 3.2 on 31.01.2027 (unless <paramref name="today"/> is false).</summary>
    public async Task RatesAsync(bool receiptDay = true, bool today = true)
    {
        if (receiptDay) await W.RateAsync("USD", Received, 2.7m);
        if (today) await W.RateAsync("USD", Today, 3.2m);
    }

    /// <summary>A USD policy of the client from 01.01.2027 for a year, the schedule given (day from the start, USD), approved by an admin.</summary>
    public async Task<World.Policy> UsdPolicyAsync(Guid holder, params (int Day, decimal Amount)[] schedule)
    {
        var admin = await W.UserAsync();
        var p = await W.PendingPolicyAsync(holder, Start, "USD", 2.7m, premium: schedule.Sum(s => s.Amount), schedule: schedule);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, p.Id.ToString())).Outcome);
        return p;
    }

    public Task<List<Guid>> InstalmentsAsync(World.Policy p) =>
        W.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id });

    /// <summary>An allocation written as the legacy fixture wrote it (a receipt's earlier holding), through the application role.</summary>
    public async Task AllocateDirectAsync(Guid payment, Guid instalment, decimal amount, decimal amountGel, decimal rate, DateOnly bookedOn)
    {
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@payment, @instalment, @amount, @amountGel, @rate, @bookedOn)",
            new { payment, instalment, amount, amountGel, rate, bookedOn });
    }

    /// <summary>An approved change of the instalment booked on a day (the legacy fixture's installment_adjustment row).</summary>
    public async Task AdjustDirectAsync(Guid instalment, decimal change, DateOnly bookedOn)
    {
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@instalment, @bookedOn, @change, N'ტესტი: დამტკიცებული შემცირება')",
            new { instalment, bookedOn, change });
    }

    /// <summary>
    /// A written FMS suspension of the client, ordered at <see cref="Instructed"/> and ending at <see cref="Ends"/>: of all its
    /// operations, or of one transaction (the policy) — registered as World.SuspendAsync does, under the locks of a suspension change.
    /// </summary>
    public Task<Guid> SuspendAsync(Guid partyId, Guid by, Guid? policyId = null) =>
        W.App.AsUserAsync(by, async tx =>
        {
            await Aml.LockForSuspensionChangeAsync(tx, partyId, policyId);
            var id = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at, target_policy_id, created_by)
                values (@id, @partyId, N'written', @scope, @Instructed, @Ends, @policyId, @by)
                """, new { id, partyId, scope = policyId is null ? "all" : "transaction", Instructed, Ends, policyId, by });
            return id;
        });

    /// <summary>
    /// A suspension saved before targets existed (legacy migration 0022 marked such rows legacy_unmapped): written, one transaction,
    /// only a reference, no policy or claim. Only that migration step makes such a row, so it is written as
    /// ComplianceFixtures.LegacyUnmappedSuspensionAsync writes it (the admin, the insert trigger set aside for this one row), with
    /// the legacy instants.
    /// </summary>
    public async Task<Guid> LegacyUnmappedSuspensionAsync(Guid partyId, string reference)
    {
        var id = Guid.NewGuid();
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("""
            disable trigger core.fms_suspension_insert on core.fms_suspension;
            insert core.fms_suspension (id, party_id, kind, scope, transaction_ref, legacy_unmapped, instructed_at, ends_at)
            values (@id, @partyId, N'written', N'transaction', @reference, 1, @Instructed, @Ends);
            enable trigger core.fms_suspension_insert on core.fms_suspension;
            """, new { id, partyId, reference, Instructed, Ends });
        return id;
    }

    /// <summary>The state the application gives the suspension now (on the test's clock).</summary>
    public async Task<string> SuspensionStateAsync(Guid partyId, Guid suspensionId) =>
        (await W.App.ReadTxAsync(tx => Aml.SuspensionsOfAsync(tx, partyId))).Single(s => s.Id == suspensionId).State;

    // ---------------------------------------------------------------- what the actions sent

    private const string RateRead = "select rate from core.exchange_rate where currency = @currency and rate_date = @date";

    private static string Day(object? v) => v switch
    {
        DateOnly d => Dates.ToIso(d),
        DateTime t => Dates.ToIso(DateOnly.FromDateTime(t)),
        _ => v?.ToString() ?? "",
    };

    /// <summary>Every stored-rate question the actions asked since the log was cleared ("USD 2026-12-20").</summary>
    public List<string> RateQuestions() =>
        Faults.Log.Where(c => Faults.Has(c, RateRead)).Select(c => $"{c.Parameter("currency")} {Day(c.Parameter("date"))}").ToList();

    /// <summary>Whether a policy's schedule was read (Billing.LoadScheduleAsync) since the log was cleared.</summary>
    public bool ScheduleRead => Faults.Log.Any(c => Faults.Has(c, "from core.installment i") && Faults.Has(c, "core.installment_adjustment j"));

    /// <summary>Whether anything was inserted (a payment, an allocation, an adjustment) since the log was cleared.</summary>
    public bool Inserted => Faults.Log.Any(c => c.Sql.Contains("insert into core.", StringComparison.Ordinal) || c.Sql.Contains("insert core.", StringComparison.Ordinal));

    /// <summary>The allocations of a receipt: (instalment, amount, amount GEL, rate, booked on), by the instalment's due day, then as booked.</summary>
    public Task<List<(Guid InstallmentId, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>> LinesAsync(Guid payment) =>
        W.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly)>("""
            select a.installment_id, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.installment i on i.id = a.installment_id
            where a.payment_id = @payment order by i.due_on, a.booked_on, a.created_at
            """, new { payment });

    /// <summary>A receipt's own row, every column the actions could change.</summary>
    public Task<(DateOnly, decimal, string, string, Guid?, string?, Guid?, DateTimeOffset)> ReceiptRowAsync(Guid payment) =>
        W.ScalarRowAsync<(DateOnly, decimal, string, string, Guid?, string?, Guid?, DateTimeOffset)>(
            "select received_on, amount_gel, method, kind, payer_party_id, reference, created_by, created_at from core.payment where id = @payment", new { payment });

    /// <summary>What is paid on each instalment of the policy, in its currency, oldest first.</summary>
    public Task<List<decimal>> SettledAsync(World.Policy p) => W.QueryAsync<decimal>("""
        select coalesce(sum(a.amount), 0) from core.installment i left join core.payment_allocation a on a.installment_id = i.id
        where i.policy_id = @id group by i.id, i.due_on order by i.due_on
        """, new { id = p.Id });
}

internal static class OpenPrWorldExtensions
{
    public static async Task<T> ScalarRowAsync<T>(this World w, string sql, object? param = null) => Assert.Single(await w.QueryAsync<T>(sql, param));
}
