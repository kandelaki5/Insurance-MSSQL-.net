using System.Globalization;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests;

/// <summary>
/// A fresh test database (its own ins_it_* database, dropped afterwards) and a builder of fictional report worlds in it, written as
/// plain rows through the application role, as the legacy offline tests wrote theirs into their stores: parties, policies with lines,
/// premium ledger rows and instalments, treaties and cessions, claims with reserves, payments and recoveries, receipts and allocations,
/// rates and closed months. Every guard of the schema applies (a closed month refuses a row dated in it: close months after writing);
/// only the claim-payment authorisation threshold is raised, so large amounts need no financial director's request.
/// Rows are written with increasing created_at (one second apart), so "order by created_at" orders them as written.
/// </summary>
public sealed class ReportWorld : IAsyncDisposable
{
    public TestDatabase Database { get; } = new();
    public Db App => Database.App;
    public Guid Admin { get; private set; }
    public Guid Accountant { get; private set; }
    private DateTimeOffset _clock = new(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(4));
    private int _seq;

    public static async Task<ReportWorld> CreateAsync()
    {
        var w = new ReportWorld();
        await w.Database.InitializeAsync();
        w.Admin = await Fixtures.UserAsync(w.Database, "admin");
        w.Accountant = await Fixtures.UserAsync(w.Database, "accountant");
        await using var c = await w.Database.OpenAdminAsync();
        await c.ExecuteAsync("update core.setting set value = N'9999999999999.99' where [key] = N'payment.fd_threshold_gel'");
        return w;
    }

    public async ValueTask DisposeAsync() => await Database.DisposeAsync();

    /// <summary>The next created_at (one second after the last), or the one given.</summary>
    public DateTimeOffset At(DateTimeOffset? at = null) => at ?? (_clock = _clock.AddSeconds(1));

    public string Next(string prefix) => $"{prefix}-{++_seq:D4}";

    public Task<T> WriteAsync<T>(Func<Tx, Task<T>> work) => App.AsUserAsync(Admin, work);
    public Task ExecAsync(string sql, object? param = null) => App.AsUserAsync(Admin, tx => tx.ExecuteAsync(sql, param));

    public static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- reference

    public async Task<Guid> PartyAsync(string name, string kind = "organization", string? taxId = null, string? foreignId = null, bool publicEntity = false, string country = "GE")
    {
        var id = Guid.NewGuid();
        if (taxId is null && foreignId is null) foreignId = "F-" + id.ToString("N")[..12];
        await ExecAsync("""
            insert core.party (id, kind, tax_id, foreign_id, first_name, last_name, name, is_public_entity, is_resident, country_code, created_at)
            values (@id, @kind, @taxId, @foreignId, @first, @last, @name, @publicEntity, 1, @country, @at)
            """, new
        {
            id, kind, taxId, foreignId, publicEntity, country, at = At(),
            first = kind == "person" ? name.Split(' ').Last() : null, last = kind == "person" ? name.Split(' ').First() : null,
            name = kind == "organization" ? name : null,
        });
        return id;
    }

    public async Task RateAsync(string currency, string date, decimal rate) =>
        await ExecAsync("insert core.exchange_rate (currency, rate_date, rate) values (@currency, @date, @rate)", new { currency, date = D(date), rate });

    /// <summary>A closed month, as a close would leave it (the guard then refuses any movement dated in it).</summary>
    public async Task ClosePeriodAsync(string month) =>
        await ExecAsync("insert core.accounting_period (period_start, status, closed_at, closed_by) values (@start, N'closed', sysdatetimeoffset(), @by)",
            new { start = D(month + "-01"), by = Admin });

    // ---------------------------------------------------------------- policies and the premium ledger

    public sealed record LineSpec(string ClassCode, decimal Premium, decimal PremiumGel, decimal? SumInsured = null, string? SumInsuredCurrency = null);

    public sealed record PolicyRef(Guid Id, string Number, Guid PartyId, IReadOnlyList<Guid> Lines, IReadOnlyList<Guid> IssueRows, IReadOnlyList<Guid> Installments);

    /// <summary>
    /// A policy with its lines, an issue ledger row per line (booked on <paramref name="bookedOn"/>, else its start, covering its term) and its
    /// instalments (due, amount, debtor). Status active unless given (cancelled needs <paramref name="cancelledOn"/>).
    /// </summary>
    public async Task<PolicyRef> PolicyAsync(string number, string from, string to, IReadOnlyList<LineSpec> lines,
        IReadOnlyList<(string Due, decimal Amount, string Debtor)>? installments = null, string currency = "GEL", decimal rate = 1m,
        string? bookedOn = null, Guid? holder = null, Guid? replaces = null, string? cancelledOn = null, string status = "active", bool issueRows = true,
        string? contractNumber = null)
    {
        var party = holder ?? await PartyAsync($"დამზღვევი {number} (გამოგონილი)");
        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        var lineIds = new List<Guid>();
        var issues = new List<Guid>();
        var insts = new List<Guid>();
        await WriteAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                insert core.contract (id, number, signed_on, policyholder_id, created_at, created_by) values (@contract, @contractNumber, @from, @party, @at, @user)
                """, new { contract, contractNumber = contractNumber ?? "C-" + number, from = D(from), party, at = At(), user = Admin });
            await tx.ExecuteAsync("""
                insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, created_at, created_by,
                                    segment, approved_at, approved_by, replaces_policy_id, cancelled_on)
                values (@policy, @contract, @number, @from, @from, @to, @status, @currency, @rate, @at, @user, N'corporate',
                        case when @status = N'draft' or @status = N'pending_approval' then null else sysdatetimeoffset() end,
                        case when @status = N'draft' or @status = N'pending_approval' then null else @user end, @replaces, @cancelledOn)
                """, new { policy, contract, number, from = D(from), to = D(to), status, currency, rate, at = At(), user = Admin, replaces, cancelledOn = cancelledOn is null ? (DateOnly?)null : D(cancelledOn) });
            foreach (var l in lines)
            {
                var line = Guid.NewGuid();
                lineIds.Add(line);
                await tx.ExecuteAsync("""
                    insert core.policy_line (id, policy_id, class_code, premium, premium_gel, sum_insured, sum_insured_currency)
                    values (@line, @policy, @classCode, @premium, @premiumGel, @sumInsured, @sumInsuredCurrency)
                    """, new { line, policy, l.ClassCode, l.Premium, l.PremiumGel, l.SumInsured, l.SumInsuredCurrency });
                if (!issueRows) continue;
                var row = Guid.NewGuid();
                issues.Add(row);
                await tx.ExecuteAsync("""
                    insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_at, created_by)
                    values (@row, @line, N'issue', @premium, @premiumGel, @bookedOn, @from, @to, @at, @user)
                    """, new { row, line, l.Premium, l.PremiumGel, bookedOn = D(bookedOn ?? from), from = D(from), to = D(to), at = At(), user = Admin });
            }
            foreach (var (due, amount, debtor) in installments ?? [])
            {
                var i = Guid.NewGuid();
                insts.Add(i);
                await tx.ExecuteAsync("insert core.installment (id, policy_id, due_on, amount, debtor_kind) values (@i, @policy, @due, @amount, @debtor)",
                    new { i, policy, due = D(due), amount, debtor });
            }
            return 0;
        });
        return new PolicyRef(policy, number, party, lineIds, issues, insts);
    }

    /// <summary>One premium ledger row (a cancellation or a correction linked to the row it cancels).</summary>
    public async Task<Guid> TxAsync(Guid line, string kind, decimal amount, decimal amountGel, string bookedOn, string coverFrom, string coverTo, Guid? cancels = null, DateTimeOffset? at = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync("""
            insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
            values (@id, @line, @kind, @amount, @amountGel, @bookedOn, @coverFrom, @coverTo, @cancels, @at, @user)
            """, new { id, line, kind, amount, amountGel, bookedOn = D(bookedOn), coverFrom = D(coverFrom), coverTo = D(coverTo), cancels, at = At(at), user = Admin });
        return id;
    }

    public async Task AdjustAsync(Guid installment, string bookedOn, decimal change) =>
        await ExecAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason, created_at, created_by) values (@installment, @bookedOn, @change, N'სატესტო', @at, @user)",
            new { installment, bookedOn = D(bookedOn), change, at = At(), user = Admin });

    public async Task<Guid> ReceiptAsync(string on, decimal amountGel, Guid? payer = null, string method = "bank", string? reference = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync("insert core.payment (id, received_on, amount_gel, method, payer_party_id, reference, created_at, created_by) values (@id, @on, @amountGel, @method, @payer, @reference, @at, @user)",
            new { id, on = D(on), amountGel, method, payer, reference, at = At(), user = Admin });
        return id;
    }

    public async Task<Guid> ReversalAsync(Guid receipt, string on, decimal amountGel, string method = "bank")
    {
        var id = Guid.NewGuid();
        await ExecAsync("""
            insert core.payment (id, received_on, amount_gel, method, kind, reverses_payment_id, reference, created_at, created_by)
            values (@id, @on, @amountGel, @method, N'reversal', @receipt, N'სატესტო შებრუნება', @at, @user)
            """, new { id, on = D(on), amountGel, method, receipt, at = At(), user = Admin });
        return id;
    }

    public async Task AllocateAsync(Guid payment, Guid installment, decimal amount, decimal amountGel, decimal rate, string bookedOn) =>
        await ExecAsync("""
            insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on, created_at)
            values (@payment, @installment, @amount, @amountGel, @rate, @bookedOn, @at)
            """, new { payment, installment, amount, amountGel, rate, bookedOn = D(bookedOn), at = At() });

    /// <summary>A receipt paid against one instalment in full of <paramref name="amount"/> (policy currency) at <paramref name="rate"/>.</summary>
    public async Task<Guid> PayAsync(Guid installment, string on, decimal amount, decimal rate = 1m)
    {
        var gel = Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
        var receipt = await ReceiptAsync(on, gel);
        await AllocateAsync(receipt, installment, amount, gel, rate, on);
        return receipt;
    }

    // ---------------------------------------------------------------- reinsurance

    public async Task<Guid> TreatyAsync(string number, string kind, string from, string to, IReadOnlyList<string> classes, string currency = "GEL",
        decimal? cededPercent = null, decimal? retention = null, int? lines = null, decimal? priority = null, decimal? limit = null, decimal? treatyPremium = null,
        decimal commissionPercent = 0, decimal brokerPercent = 0, Guid? reinsurer = null, Guid? broker = null, string status = "active",
        string? rating = null, string? agency = null)
    {
        var id = Guid.NewGuid();
        var r = reinsurer ?? await PartyAsync($"გადამზღვეველი {number} (გამოგონილი)", foreignId: "RE-" + number);
        await WriteAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                insert core.ri_treaty (id, number, name, reinsurer_party_id, broker_party_id, kind, period_from, period_to, currency, ceded_percent, retention, lines,
                                       priority, treaty_limit, treaty_premium, commission_percent, broker_percent, status, reinsurer_rating, rating_agency, created_at, created_by)
                values (@id, @number, @name, @r, @broker, @kind, @from, @to, @currency, @cededPercent, @retention, @lines, @priority, @limit, @treatyPremium,
                        @commissionPercent, @brokerPercent, @status, @rating, @agency, @at, @user)
                """, new { id, number, name = $"ხელშეკრულება {number}", r, broker, kind, from = D(from), to = D(to), currency, cededPercent, retention, lines, priority, limit, treatyPremium,
                commissionPercent, brokerPercent, status, rating, agency, at = At(), user = Admin });
            foreach (var code in classes)
                await tx.ExecuteAsync("insert core.ri_treaty_class (treaty_id, class_code) values (@id, @code)", new { id, code });
            return 0;
        });
        return id;
    }

    public async Task<Guid> CessionAsync(Guid premiumRow, Guid treaty, decimal share, decimal amount, decimal amountGel, string bookedOn, string coverFrom, string coverTo,
        decimal commission = 0, decimal commissionGel = 0, Guid? cancels = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync("""
            insert core.premium_cession (id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel, booked_on, cover_from, cover_to,
                                         cancels_cession_id, created_at, created_by)
            values (@id, @premiumRow, @treaty, @share, @amount, @amountGel, @commission, @commissionGel, @bookedOn, @coverFrom, @coverTo, @cancels, @at, @user)
            """, new { id, premiumRow, treaty, share, amount, amountGel, commission, commissionGel, bookedOn = D(bookedOn), coverFrom = D(coverFrom), coverTo = D(coverTo), cancels, at = At(), user = Admin });
        return id;
    }

    /// <summary>The obligatory treaties applied to a premium row by the real writer (ReinsuranceStore.CedeIssuedPremiumAsync), as approval does.</summary>
    public Task CedeAsync(Guid premiumRow) => WriteAsync(async tx => { await ReinsuranceStore.CedeIssuedPremiumAsync(tx, premiumRow, Admin); return 0; });

    // ---------------------------------------------------------------- claims

    public sealed record ClaimRef(Guid Id, string Number);

    public async Task<ClaimRef> ClaimAsync(Guid policy, Guid line, string currency, string occurredOn, string reportedOn, string status = "in_review", string? number = null)
    {
        var id = Guid.NewGuid();
        var evt = Guid.NewGuid();
        number ??= Next("CLM");
        await WriteAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                insert core.loss_event (id, number, policy_id, occurred_on, reported_on, description, created_at, created_by)
                values (@evt, @eventNumber, @policy, @occurredOn, @reportedOn, N'სატესტო შემთხვევა', @at, @user)
                """, new { evt, eventNumber = "E-" + number, policy, occurredOn = D(occurredOn), reportedOn = D(reportedOn), at = At(), user = Admin });
            await tx.ExecuteAsync("""
                insert core.claim (id, number, policy_id, policy_line_id, currency, occurred_on, reported_on, description, status, loss_event_id, created_at, created_by)
                values (@id, @number, @policy, @line, @currency, @occurredOn, @reportedOn, N'სატესტო ზარალი', @status, @evt, @at, @user)
                """, new { id, number, policy, line, currency, occurredOn = D(occurredOn), reportedOn = D(reportedOn), status, evt, at = At(), user = Admin });
            return 0;
        });
        return new ClaimRef(id, number);
    }

    public async Task ReserveAsync(Guid claim, string bookedOn, decimal change) =>
        await ExecAsync("insert core.claim_reserve (claim_id, booked_on, change, reason, created_at, created_by) values (@claim, @bookedOn, @change, N'რეზერვი', @at, @user)",
            new { claim, bookedOn = D(bookedOn), change, at = At(), user = Admin });

    /// <summary>
    /// A claim payment of <paramref name="amount"/> (claim currency) at <paramref name="rate"/>, nothing withheld, to the policyholder. Above what
    /// the threshold setting can hold, through an approved request, as the database requires.
    /// </summary>
    public async Task<Guid> ClaimPaymentAsync(Guid claim, string paidOn, decimal amount, decimal rate = 1m)
    {
        var id = Guid.NewGuid();
        var gel = Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
        Guid? request = null;
        if (gel > 9999999999999.99m)
        {
            request = Guid.NewGuid();
            await App.AsUserAsync(Accountant, tx => tx.ExecuteAsync("""
                insert core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind,
                                                   payee_party_id, offset_premium, total_loss_mode, created_at)
                select @request, c.id, @paidOn, @amount, 0, 0, @amount, @gel, @rate, N'policyholder', k.policyholder_id, 0, N'none', @at
                from core.claim c join core.policy p on p.id = c.policy_id join core.contract k on k.id = p.contract_id where c.id = @claim
                """, new { request, claim, paidOn = D(paidOn), amount, gel, rate, at = At() }));
        }
        await ExecAsync("""
            insert core.claim_payment (id, claim_id, paid_on, amount, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, request_id, created_at, created_by)
            select @id, c.id, @paidOn, @amount, @amount, @gel, @rate, N'policyholder', k.policyholder_id, @request, @at, @user
            from core.claim c join core.policy p on p.id = c.policy_id join core.contract k on k.id = p.contract_id where c.id = @claim
            """, new { id, claim, paidOn = D(paidOn), amount, gel, rate, request, at = At(), user = Admin });
        return id;
    }

    /// <summary>The exact mirror of a claim payment (a correction).</summary>
    public async Task ClaimPaymentReversalAsync(Guid payment, string paidOn) =>
        await ExecAsync("""
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
                                       kind, reverses_claim_payment_id, reversal_reason, created_at, created_by)
            select claim_id, @paidOn, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate, payee_kind, payee_party_id,
                   N'reversal', id, N'correction', @at, @user
            from core.claim_payment where id = @payment
            """, new { payment, paidOn = D(paidOn), at = At(), user = Admin });

    public async Task<Guid> RecoveryAsync(Guid claim, string kind, string receivedOn, decimal amountGel, Guid? reverses = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync("""
            insert core.claim_recovery (id, claim_id, kind, received_on, amount_gel, row_kind, reverses_claim_recovery_id, created_at, created_by)
            values (@id, @claim, @kind, @receivedOn, @amountGel, @rowKind, @reverses, @at, @user)
            """, new { id, claim, kind, receivedOn = D(receivedOn), amountGel, rowKind = reverses is null ? "receipt" : "reversal", reverses, at = At(), user = Admin });
        return id;
    }

    public async Task ClaimCessionAsync(Guid claim, Guid treaty, decimal share, decimal? priority = null, decimal? limit = null) =>
        await ExecAsync("insert core.claim_cession (claim_id, treaty_id, share_percent, priority, treaty_limit) values (@claim, @treaty, @share, @priority, @limit)",
            new { claim, treaty, share, priority, limit });

    /// <summary>The claim's treaties fixed by the real writer (ReinsuranceStore.AttachClaimCessionsAsync), as claim registration does.</summary>
    public Task AttachClaimCessionsAsync(Guid claim, Guid line, string classCode, string occurredOn) =>
        WriteAsync(async tx => { await ReinsuranceStore.AttachClaimCessionsAsync(tx, claim, line, classCode, D(occurredOn)); return 0; });

    // ---------------------------------------------------------------- reading

    /// <summary>The stored rates only (legacy latestRateOnOrBefore with the National Bank unreachable): the day's, else the latest earlier one; asked calls counted.</summary>
    public sealed class StoredRates(Db db)
    {
        public List<(string Currency, DateOnly Date)> Asked { get; } = [];

        public async Task<(decimal Rate, DateOnly Date)> ValueAsync(string currency, DateOnly date)
        {
            lock (Asked) Asked.Add((currency, date));
            if (currency == "GEL") return (1m, date);
            var row = await db.ReadAsync(c => c.QuerySingleOrDefaultAsync<(decimal Rate, DateOnly RateDate)?>(
                "select top (1) rate, rate_date from core.exchange_rate where currency = @currency and rate_date <= @date order by rate_date desc", new { currency, date }));
            return row is { } r ? (r.Rate, r.RateDate) : throw new InvalidOperationException($"No {currency} rate on or before {date:yyyy-MM-dd}");
        }
    }

    public StoredRates Rates() => new(App);

    public Task<PremiumReport.Report> MonthAsync(string month, StoredRates? rates = null) =>
        App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, (rates ?? Rates()).ValueAsync, month));

    public Task<PolicyPremiumReport.Report> PolicyPremiumAsync(string from, string to) =>
        App.ReadTxAsync(tx => PolicyPremiumReport.BuildAsync(tx, D(from), D(to)));

    public Task<ImpairmentReport.Report> ImpairmentAsync(string asOf, StoredRates? rates = null) =>
        App.ReadTxAsync(tx => ImpairmentReport.BuildAsync(tx, (rates ?? Rates()).ValueAsync, D(asOf)));

    public Task<ImpairmentReport.ReportExact> ImpairmentExactAsync(string asOf, StoredRates? rates = null) =>
        App.ReadTxAsync(tx => ImpairmentReport.BuildTotalsExactAsync(tx, (rates ?? Rates()).ValueAsync, D(asOf)));

    public Task<Bordereau.Statement?> BordereauAsync(Guid treaty, string from, string to, StoredRates? rates = null) =>
        App.ReadTxAsync(tx => Bordereau.BuildAsync(tx, (rates ?? Rates()).ValueAsync, treaty, D(from), D(to)));

    /// <summary>Every row of the tables a report reads, as text, to show that reading wrote nothing.</summary>
    public async Task<string> FingerprintAsync()
    {
        await using var c = await Database.OpenAdminAsync();
        var parts = new List<string>();
        foreach (var t in new[] { "premium_transaction", "premium_cession", "installment", "installment_adjustment", "payment", "payment_allocation", "claim_reserve", "claim_payment", "claim_recovery", "exchange_rate", "audit_log", "accounting_period" })
            parts.Add($"{t}:{await c.ExecuteScalarAsync<int>($"select count(*) from core.{t}")}");
        return string.Join(",", parts);
    }
}
