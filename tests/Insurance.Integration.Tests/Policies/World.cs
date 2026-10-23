using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Finance;

namespace Insurance.Integration.Tests.Policies;

/// <summary>An HTTP client factory whose clients never reach the network: a rate that is not stored is "not published" (no NBG call in a test).</summary>
public sealed class NoNetwork : IHttpClientFactory
{
    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in tests");
    }

    public HttpClient CreateClient(string name) => new(new Refuse());
}

/// <summary>
/// Fictional rows and readers for the policy and payment tests (the legacy regression scripts' fixtures: a client, a
/// CASCO policy of 365.00 in four instalments of 91.25 approved through the application, receipts written directly).
/// Everything is written through the application role; settings and closed months through the admin connection, as the
/// legacy scripts wrote them with the admin pool.
/// </summary>
public sealed class World(TestDatabase db)
{
    public TestDatabase Database { get; } = db;
    public Db App => Database.App;
    public Rates Rates { get; } = new(db.App, new NoNetwork());
    public static DateOnly Today => Rates.TodayInTbilisi();
    private static int _seq;

    public async Task<CurrentUser> UserAsync(string role = "admin")
    {
        var id = await Fixtures.UserAsync(Database, role);
        return new CurrentUser(id, $"test.{id:N}"[..30], "სატესტო მომხმარებელი", role, role);
    }

    public async Task<Guid> ClientAsync(string? taxId = null)
    {
        var id = Guid.NewGuid();
        taxId ??= (100_000_000 + (Interlocked.Increment(ref _seq) * 104_729L + Random.Shared.Next(1_000_000)) % 899_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var c = await Database.OpenAppAsync();
        await c.ExecuteAsync("insert core.party (id, kind, name, tax_id, risk_level) values (@id, N'organization', @name, @taxId, N'low')",
            new { id, name = $"შპს ტესტი {id.ToString("N")[..6]}", taxId });
        return id;
    }

    public async Task<(Guid Product, Guid Damage)> CascoAsync()
    {
        await using var c = await Database.OpenAppAsync();
        return await c.QuerySingleAsync<(Guid, Guid)>("""
            select p.id, c.id from core.product p join core.product_coverage c on c.product_id = p.id where p.code = N'CASCO' and c.code = N'damage'
            """);
    }

    public sealed record Policy(Guid Id, string Number, Guid ContractId, Guid Holder);

    /// <summary>A CASCO policy awaiting approval: one damage line, the schedule as given (default 365.00 in four 91.25 from <paramref name="from"/>).</summary>
    public async Task<Policy> PendingPolicyAsync(Guid holder, DateOnly from, string currency = "GEL", decimal rate = 1m, decimal premium = 365m,
        IReadOnlyList<(int Day, decimal Amount)>? schedule = null, int days = 365, Guid? createdBy = null)
    {
        var (product, damage) = await CascoAsync();
        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        var number = "T-" + policy.ToString("N")[..12].ToUpperInvariant();
        schedule ??= [(0, premium / 4), (91, premium / 4), (182, premium / 4), (273, premium - 3 * (premium / 4))];
        await using var c = await Database.OpenAppAsync();
        await c.ExecuteAsync("""
            insert core.contract (id, number, signed_on, policyholder_id) values (@contract, @contractNo, @from, @holder);
            insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment, product_id, created_by)
            values (@policy, @contract, @number, @from, @from, @to, N'pending_approval', @currency, @rate, N'corporate', @product, @createdBy);
            insert core.policy_line (policy_id, class_code, premium, premium_gel, product_coverage_id) values (@policy, N'5.1', @premium, round(@premium * @rate, 2), @damage);
            """, new { contract, contractNo = "C-" + number, policy, number, from, to = from.AddDays(days), currency, rate, product, damage, holder, premium, createdBy });
        foreach (var (day, amount) in schedule)
            await c.ExecuteAsync("insert core.installment (policy_id, due_on, amount) values (@policy, @due, @amount)", new { policy, due = from.AddDays(day), amount });
        return new Policy(policy, number, contract, holder);
    }

    /// <summary>A policy approved through PolicyActions.ApprovePolicyAsync (the fixture fails if it is not "done").</summary>
    public async Task<Policy> ActivePolicyAsync(CurrentUser approver, Guid holder, DateOnly from, string currency = "GEL", decimal rate = 1m, decimal premium = 365m)
    {
        var p = await PendingPolicyAsync(holder, from, currency, rate, premium);
        var answer = await PolicyActions.ApprovePolicyAsync(App, approver, p.Id.ToString());
        Assert.Equal("done", answer.Outcome);
        return p;
    }

    /// <summary>A receipt registered earlier and not allocated to any instalment.</summary>
    public async Task<Guid> ReceiptAsync(Guid payer, DateOnly on, decimal amountGel, Guid? createdBy = null, string? reference = null)
    {
        var id = Guid.NewGuid();
        await using var c = await Database.OpenAppAsync();
        await c.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, payer_party_id, reference, created_by) values (@id, @on, @amountGel, N'bank', @payer, @reference, @createdBy)",
            new { id, on, amountGel, payer, reference, createdBy });
        return id;
    }

    /// <summary>A written FMS suspension of all the client's operations, in force for 72 hours from now (registered under the client's exclusive lock).</summary>
    public Task<Guid> SuspendAsync(Guid partyId, Guid by, Guid? policyId = null, string? transactionRef = null) =>
        App.AsUserAsync(by, async tx =>
        {
            await Aml.LockForSuspensionChangeAsync(tx, partyId, policyId);
            var id = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert core.fms_suspension (id, party_id, kind, scope, transaction_ref, instructed_at, ends_at, target_policy_id, created_by)
                values (@id, @partyId, N'written', @scope, @transactionRef, sysdatetimeoffset(), dateadd(hour, 72, sysdatetimeoffset()), @policyId, @by)
                """, new { id, partyId, scope = policyId is null && transactionRef is null ? "all" : "transaction", transactionRef, policyId, by });
            return id;
        });

    /// <summary>Lifts a suspension now.</summary>
    public async Task LiftAsync(Guid suspensionId)
    {
        await using var c = await Database.OpenAppAsync();
        await c.ExecuteAsync("update core.fms_suspension set lifted_at = sysdatetimeoffset(), lift_reason = N'მოხსნა' where id = @suspensionId", new { suspensionId });
    }

    /// <summary>An obligatory (or facultative) treaty on class 5.1 for 2020–2035.</summary>
    public async Task<Guid> TreatyAsync(string kind = "quota_share", decimal cededPercent = 60m, decimal commissionPercent = 0m, string currency = "GEL", string classCode = "5.1")
    {
        var id = Guid.NewGuid();
        await using var c = await Database.OpenAppAsync();
        var reinsurer = Guid.NewGuid();
        await c.ExecuteAsync("insert core.party (id, kind, name, tax_id, risk_level) values (@reinsurer, N'organization', N'გადამზღვევი', @tax, N'low')",
            new { reinsurer, tax = (200_000_000 + Random.Shared.Next(700_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await c.ExecuteAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, commission_percent)
            values (@id, @number, N'ხელშეკრულება', @reinsurer, @kind, '2020-01-01', '2035-12-31', @currency, @cededPercent, @commissionPercent);
            insert core.ri_treaty_class (treaty_id, class_code) values (@id, @classCode);
            """, new { id, number = "RI-" + id.ToString("N")[..8], reinsurer, kind, currency, cededPercent, commissionPercent, classCode });
        return id;
    }

    public async Task RateAsync(string currency, DateOnly date, decimal rate)
    {
        await using var c = await Database.OpenAppAsync();
        await c.ExecuteAsync("""
            merge core.exchange_rate with (holdlock) as t using (select @currency as currency, @date as rate_date) as s
            on t.currency = s.currency and t.rate_date = s.rate_date
            when matched then update set rate = @rate when not matched then insert (currency, rate_date, rate) values (@currency, @date, @rate);
            """, new { currency, date, rate });
    }

    public async Task CloseMonthAsync(DateOnly month, Guid by)
    {
        await using var c = await Database.OpenAdminAsync();
        await c.ExecuteAsync("insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @by)",
            new { month = new DateOnly(month.Year, month.Month, 1), by });
    }

    public async Task SettingAsync(string key, string? value)
    {
        await using var c = await Database.OpenAdminAsync();
        if (value is null) await c.ExecuteAsync("delete from core.setting where [key] = @key", new { key });
        else await c.ExecuteAsync("""
            merge core.setting with (holdlock) as t using (select @key as [key]) as s on t.[key] = s.[key]
            when matched then update set value = @value when not matched then insert ([key], value) values (@key, @value);
            """, new { key, value });
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await Database.OpenAppAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var c = await Database.OpenAppAsync();
        return (await c.QueryAsync<T>(sql, param)).ToList();
    }

    /// <summary>What the policy owes: the whole schedule with every adjustment, less everything allocated.</summary>
    public Task<decimal> OwedAsync(Guid policyId) => ScalarAsync<decimal>("""
        select coalesce((select sum(i.amount) from core.installment i where i.policy_id = @policyId), 0)
             + coalesce((select sum(j.change) from core.installment_adjustment j join core.installment k on k.id = j.installment_id where k.policy_id = @policyId), 0)
             - coalesce((select sum(a.amount) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policyId), 0)
        """, new { policyId });

    /// <summary>The whole schedule with every adjustment.</summary>
    public Task<decimal> ScheduledAsync(Guid policyId) => ScalarAsync<decimal>("""
        select coalesce((select sum(i.amount) from core.installment i where i.policy_id = @policyId), 0)
             + coalesce((select sum(j.change) from core.installment_adjustment j join core.installment k on k.id = j.installment_id where k.policy_id = @policyId), 0)
        """, new { policyId });

    public Task<decimal> PaidAsync(Guid policyId) => ScalarAsync<decimal>("""
        select coalesce(sum(a.amount), 0) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policyId
        """, new { policyId });

    /// <summary>What is left of a receipt to allocate.</summary>
    public Task<decimal> AvailableAsync(Guid paymentId) => ScalarAsync<decimal>("""
        select p.amount_gel - coalesce((select sum(a.amount_gel) from core.payment_allocation a where a.payment_id = p.id), 0) from core.payment p where p.id = @paymentId
        """, new { paymentId });

    /// <summary>The schedule instalment by instalment with every adjustment, as text (91.25).</summary>
    public async Task<string> ScheduleTextAsync(Guid policyId) => string.Join("/", (await QueryAsync<decimal>("""
        select i.amount + coalesce((select sum(j.change) from core.installment_adjustment j where j.installment_id = i.id), 0)
        from core.installment i where i.policy_id = @policyId order by i.due_on
        """, new { policyId })).Select(a => a.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The debt as the card computes it on a day: the schedule as of that day, every allocation dated by its booking day.</summary>
    public async Task<decimal> OwedOnAsync(Guid policyId, DateOnly asOf)
    {
        var schedule = await App.ReadTxAsync(tx => Billing.LoadScheduleAsync(tx, policyId, asOf));
        var allocations = await QueryAsync<Billing.AllocationRow>("""
            select a.amount as Amount, a.booked_on as ReceivedOn, p.method as Method from core.payment_allocation a
            join core.payment p on p.id = a.payment_id join core.installment i on i.id = a.installment_id where i.policy_id = @policyId
            """, new { policyId });
        var position = Receivable.ReceivableAsOf(Billing.ToInstallments(schedule, Currency.GEL), Billing.ToCredits(allocations, Currency.GEL), asOf, (_, _) => 1m);
        return (position.Outstanding.Minor - position.Overpaid.Minor) / 100m;
    }

    /// <summary>A form as the legacy actions received it: field → value.</summary>
    public static Dictionary<string, string> Form(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    public Task<PaymentActions.FormAnswer> PayAsync(CurrentUser user, Guid policyId, string amount, DateOnly on, string method = "bank", string? reference = null) =>
        PaymentActions.RecordPaymentAsync(App, Rates, user, Form(("policy_id", policyId.ToString()), ("amount_gel", amount), ("received_on", Dates.ToIso(on)), ("method", method), ("reference", reference ?? "")));

    public Task<PaymentActions.FormAnswer> CancelAsync(CurrentUser user, Guid policyId, DateOnly on, string reason = "გაუქმება") =>
        PolicyActions.CancelPolicyAsync(App, user, Form(("policy_id", policyId.ToString()), ("cancel_on", Dates.ToIso(on)), ("reason", reason)));

    public Task<PaymentActions.FormAnswer> EndorseAsync(CurrentUser user, Guid policyId, DateOnly on, string delta, string reason = "ცვლილება", string coverage = "damage") =>
        PolicyActions.EndorsePolicyAsync(App, Rates, user, Form(("policy_id", policyId.ToString()), ("change_on", Dates.ToIso(on)), ("reason", reason),
            ($"cov_{coverage}_on", "on"), ($"cov_{coverage}_delta", delta)), code => code == coverage);

    /// <summary>The change issued for an original, if any.</summary>
    public async Task<(Guid Id, string Number, string Status, Guid ContractId, DateOnly EffectiveFrom, DateOnly EffectiveTo, decimal PremiumRate, string PremiumCurrency)?> ChangeOfAsync(Guid originalId) =>
        (await QueryAsync<(Guid, string, string, Guid, DateOnly, DateOnly, decimal, string)>(
            "select id, number, status, contract_id, effective_from, effective_to, premium_rate, premium_currency from core.policy where replaces_policy_id = @originalId",
            new { originalId })).Cast<(Guid, string, string, Guid, DateOnly, DateOnly, decimal, string)?>().FirstOrDefault();

    public Task<List<(string Kind, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)>> PremiumRowsAsync(Guid policyId) =>
        QueryAsync<(string, decimal, decimal, DateOnly, DateOnly, DateOnly)>("""
            select t.kind, t.amount, t.amount_gel, t.booked_on, t.cover_from, t.cover_to
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policyId order by t.created_at
            """, new { policyId });

    /// <summary>The query parameter of a redirect target (/policies/x?paid=0 → "0").</summary>
    public static string? Param(string? url, string name)
    {
        if (url is null || url.IndexOf('?', StringComparison.Ordinal) is var q && q < 0) return null;
        foreach (var part in url[(q + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && part[..eq] == name) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }
}
