using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests;

/// <summary>An HTTP client factory whose every request fails at once: the National Bank is never asked in a test.</summary>
public sealed class NoNetwork : IHttpClientFactory
{
    public int Requests;

    private sealed class Refuse(NoNetwork owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.Requests);
            throw new HttpRequestException("the test has no network");
        }
    }

    public HttpClient CreateClient(string name) => new(new Refuse(this));
}

/// <summary>
/// Fictional policies, users and claims for the claim tests, written through the application role. Claims are registered and
/// decided through the real ClaimActions, as the old tests drove the real actions. With an <paramref name="interceptor"/>
/// (FailureInjection.Faults), every command the actions send through <see cref="App"/> passes it; the readers below use
/// their own connections and are never intercepted.
/// </summary>
public sealed class ClaimWorld(TestDatabase db, ICommandInterceptor? interceptor = null)
{
    public Db App { get; } = interceptor is null ? db.App : new(new DbOptions { ConnectionString = db.AppConnection, Interceptor = interceptor, Clock = db.Clock });
    public NoNetwork Network { get; } = new();
    public Rates Rates => new(App, Network);
    public TestDatabase Database => db;

    public sealed record Line(string Coverage, decimal? SumInsured = 20000m, decimal? Deductible = null, decimal Premium = 0m);

    public sealed record Policy(Guid Id, string Number, Guid PartyId, Guid ContractId, IReadOnlyDictionary<string, Guid> Lines, IReadOnlyList<Guid> Installments)
    {
        public Guid LineOf(string coverage) => Lines[coverage];
    }

    private static int counter;

    public static string Tag() => $"{DateTime.UtcNow:HHmmss}{Interlocked.Increment(ref counter):D4}{Random.Shared.Next(1000):D3}";

    /// <summary>A user of the role; its CurrentUser as a session would give it.</summary>
    public async Task<CurrentUser> UserAsync(string role, string? name = null)
    {
        var id = Guid.NewGuid();
        var fullName = name ?? $"ტესტი {role}";
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code) values (@id, @username, @fullName, N'scrypt$00$00', @role)
            """, new { id, username = $"t.{id:N}"[..30], fullName, role });
        var roleName = await c.ExecuteScalarAsync<string>("select name_ka from core.role where code = @role", new { role });
        return new CurrentUser(id, $"t.{id:N}"[..30], fullName, role, roleName ?? role);
    }

    public async Task<Guid> PartyAsync(string? taxId = null, string name = "ტესტი")
    {
        var id = Guid.NewGuid();
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.party (id, kind, tax_id, first_name, last_name) values (@id, N'person', @taxId, @name, N'ტესტიშვილი')",
            new { id, taxId = taxId ?? Fixtures.RandomTaxId(), name });
        return id;
    }

    /// <summary>
    /// An approved policy from <paramref name="from"/> for a year (or <paramref name="days"/>), one line per coverage of the CASCO
    /// product, the issue premium of each line on its start day, and the instalments given (due day, amount); premium rows are
    /// ceded to the treaties in force, as on approval.
    /// </summary>
    public async Task<Policy> PolicyAsync(Guid actor, DateOnly from, Line[] lines, (DateOnly Due, decimal Amount)[]? installments = null, string currency = "GEL",
        decimal rate = 1m, int days = 0, Guid? party = null)
    {
        var partyId = party ?? await PartyAsync();
        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        var to = days > 0 ? from.AddDays(days) : from.AddYears(1);
        var number = "CAS-" + Tag();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var instalments = new List<Guid>();
        await App.AsUserAsync(actor, async tx =>
        {
            await tx.ExecuteAsync("""
                insert core.contract (id, number, signed_on, policyholder_id, created_by) values (@contract, @contractNo, @from, @partyId, @actor);
                insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate,
                                    created_by, segment, approved_at, approved_by, product_id)
                values (@policy, @contract, @number, @from, @from, @to, N'active', @currency, @rate, @actor, N'retail', sysdatetimeoffset(), @actor,
                        (select id from core.product where code = N'CASCO'));
                insert core.insured_object (policy_id, object_type, description) values (@policy, N'vehicle', N'ტესტი მანქანა');
                """, new { contract, contractNo = "C-" + Tag(), policy, number, from, to, currency, rate, actor, partyId });
            foreach (var l in lines)
            {
                var lineId = Guid.NewGuid();
                ids[l.Coverage] = lineId;
                await tx.ExecuteAsync("""
                    insert core.policy_line (id, policy_id, class_code, sum_insured, deductible, premium, premium_gel, product_coverage_id)
                    select @lineId, @policy, pc.class_code, @sumInsured, @deductible, @premium, round(@premium * @rate, 2), pc.id
                    from core.product_coverage pc join core.product p on p.id = pc.product_id where p.code = N'CASCO' and pc.code = @coverage
                    """, new { lineId, policy, l.SumInsured, l.Deductible, l.Premium, rate, l.Coverage });
                if (l.Premium != 0)
                {
                    var row = Guid.NewGuid();
                    await tx.ExecuteAsync("""
                        insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
                        values (@row, @lineId, N'issue', @premium, round(@premium * @rate, 2), @from, @from, @to, @actor)
                        """, new { row, lineId, premium = l.Premium, rate, from, to, actor });
                    await ReinsuranceStore.CedeIssuedPremiumAsync(tx, row, actor);
                }
            }
            foreach (var (due, amount) in installments ?? [])
            {
                var id = Guid.NewGuid();
                instalments.Add(id);
                await tx.ExecuteAsync("insert core.installment (id, policy_id, due_on, amount) values (@id, @policy, @due, @amount)", new { id, policy, due, amount });
            }
            return 0;
        });
        return new Policy(policy, number, partyId, contract, ids, instalments);
    }

    /// <summary>A bank receipt of the policyholder allocated to the oldest instalments, as a premium payment would be (GEL policy).</summary>
    public async Task<Guid> PaidAsync(Guid actor, Policy policy, DateOnly on, decimal amountGel)
    {
        var id = Guid.NewGuid();
        await App.AsUserAsync(actor, async tx =>
        {
            await tx.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, payer_party_id, created_by) values (@id, @on, @amountGel, N'bank', @payer, @actor)",
                new { id, on, amountGel, payer = policy.PartyId, actor });
            var left = amountGel;
            foreach (var i in policy.Installments)
            {
                if (left <= 0) break;
                var amount = await tx.QuerySingleOrDefaultAsync<decimal>("select amount from core.installment where id = @i", new { i });
                var take = Math.Min(left, amount);
                left -= take;
                await tx.ExecuteAsync("""
                    insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@id, @i, @take, @take, 1, @on)
                    """, new { id, i, take, on });
            }
            return 0;
        });
        return id;
    }

    public static PostedForm Form(params (string Key, string Value)[] fields) => new(fields.Select(f => new KeyValuePair<string, string?>(f.Key, f.Value)));

    public static string D(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Registers one loss event through registerClaim; returns the claims made, in the event's order.</summary>
    public async Task<IReadOnlyList<Guid>> RegisterAsync(CurrentUser user, Policy policy, DateOnly occurredOn, DateOnly reportedOn, params (string Coverage, string Reserve)[] parts)
    {
        var fields = new List<(string, string)> { ("policy_number", policy.Number), ("occurred_on", D(occurredOn)), ("reported_on", D(reportedOn)), ("description", "ავარია") };
        foreach (var (coverage, reserve) in parts)
        {
            fields.Add(($"line:{policy.LineOf(coverage)}", "on"));
            fields.Add(($"reserve:{policy.LineOf(coverage)}", reserve));
        }
        var outcome = await ClaimActions.RegisterClaimAsync(App, Rates, user, Form([.. fields]));
        Assert.True(outcome.RedirectTo is not null, $"not registered: {string.Join("; ", outcome.Errors.Select(e => $"{e.Key}={e.Value}"))} {outcome.Message}");
        var first = Guid.Parse(outcome.RedirectTo!.Split('/')[2].Split('?')[0]);
        return await ClaimsOfEventAsync(first);
    }

    public async Task<IReadOnlyList<Guid>> ClaimsOfEventAsync(Guid claimId)
    {
        await using var c = await db.OpenAppAsync();
        return (await c.QueryAsync<Guid>("""
            select c.id from core.claim c where c.loss_event_id = (select loss_event_id from core.claim where id = @claimId)
            order by c.created_at, c.number
            """, new { claimId })).ToList();
    }

    /// <summary>Approves a claim through decideClaim.</summary>
    public Task ApproveAsync(CurrentUser user, Guid claimId, string amount, DateOnly today, bool totalLoss = false) =>
        ClaimActions.DecideClaimAsync(App, user, totalLoss
            ? Form(("claim_id", claimId.ToString()), ("decision", "approve"), ("approved_amount", amount), ("is_total_loss", "on"))
            : Form(("claim_id", claimId.ToString()), ("decision", "approve"), ("approved_amount", amount)), today);

    public Task<FormOutcome> PayAsync(CurrentUser user, Guid claimId, string amount, DateOnly paidOn, DateOnly today, params (string, string)[] more) =>
        ClaimActions.PayClaimAsync(App, Rates, user, Form([("claim_id", claimId.ToString()), ("amount", amount), ("paid_on", D(paidOn)),
            .. more.Any(m => m.Item1 == "payee_kind") ? Array.Empty<(string, string)>() : [("payee_kind", "policyholder")],
            .. more.Any(m => m.Item1 == "total_loss_mode") ? Array.Empty<(string, string)>() : [("total_loss_mode", "none")], .. more]), today);

    public async Task CloseMonthAsync(Guid actor, DateOnly month)
    {
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor)",
            new { month = new DateOnly(month.Year, month.Month, 1), actor });
    }

    /// <summary>
    /// The company setting claim.currency (H46): "gel" (the default) records a new claim on a USD/EUR policy in GEL; "policy"
    /// keeps the old program's behaviour, the claim in the policy's currency — the old claims the legacy tests describe.
    /// </summary>
    public Task ClaimCurrencyAsync(string value) => AdminAsync("""
        merge core.setting with (holdlock) as t using (select N'claim.currency' as [key]) as s on t.[key] = s.[key]
        when matched then update set value = @value when not matched then insert ([key], value) values (N'claim.currency', @value);
        """, new { value });

    /// <summary>Old claims: a claim on a foreign-currency policy in the policy's currency, as the old program recorded it (H46).</summary>
    public Task OldClaimsAsync() => ClaimCurrencyAsync("policy");

    public async Task RateAsync(string currency, DateOnly on, decimal rate)
    {
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.exchange_rate (currency, rate_date, rate) values (@currency, @on, @rate)", new { currency, on, rate });
    }

    public async Task GrantAsync(Guid userId, string permission, string decision)
    {
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("""
            delete from core.user_permission where user_id = @userId and permission = @permission;
            insert core.user_permission (user_id, permission, decision) values (@userId, @permission, @decision)
            """, new { userId, permission, decision });
    }

    public async Task<Guid> GroupAsync(string permission, params Guid[] members)
    {
        var id = Guid.NewGuid();
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.user_group (id, name) values (@id, @name)", new { id, name = "ჯგუფი " + Tag() });
        await c.ExecuteAsync("insert core.user_group_permission (group_id, permission) values (@id, @permission)", new { id, permission });
        foreach (var m in members) await c.ExecuteAsync("insert core.user_group_member (group_id, user_id) values (@id, @m)", new { id, m });
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await db.OpenAppAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!;
    }

    public async Task<List<T>> RowsAsync<T>(string sql, object? param = null)
    {
        await using var c = await db.OpenAppAsync();
        return (await c.QueryAsync<T>(sql, param)).ToList();
    }

    public async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync(sql, param);
    }

    public async Task AdminAsync(string sql, object? param = null)
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync(sql, param);
    }

    /// <summary>
    /// A fault armed in the database: the next insert into <paramref name="table"/> (whose column <paramref name="column"/> has the
    /// value) fails once with error 50999, after the rows written before it in the same transaction — a failure after the
    /// money is written, as the legacy tests armed one in their fake database. Built by the admin as an extra trigger on the
    /// test database only; disarmed by <see cref="DisarmAsync"/>.
    /// </summary>
    public async Task ArmFaultAsync(string table, string trigger = "test_fault")
    {
        await AdminAsync($"""
            create or alter trigger core.{trigger}_{table} on core.{table} after insert, update as
            begin
              if exists (select 1 from dbo.test_fault where table_name = N'{table}')
                throw 50999, N'injected failure', 1;
            end
            """);
        await AdminAsync("if object_id(N'dbo.test_fault') is null create table dbo.test_fault (table_name sysname not null)");
        await AdminAsync("grant select on dbo.test_fault to core_app");
        await AdminAsync("insert dbo.test_fault (table_name) values (@table)", new { table });
    }

    public Task DisarmAsync() => AdminAsync("if object_id(N'dbo.test_fault') is not null delete from dbo.test_fault");
}
