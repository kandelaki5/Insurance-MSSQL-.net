using Insurance.Application.Data;
using Insurance.Application.Ports;
using Insurance.Application.Services;
using Insurance.Domain.Common;

namespace Insurance.Infrastructure.Policies;

/// <summary>
/// The policy list on SQL Server, the page, its count and its amounts in one snapshot transaction (Db.SnapshotAsync: every statement
/// sees the same committed state, so the count and the rows cannot disagree — Codex R006-03). The
/// search and the rows are the old list's (Pages/Policies before B2); the order is newest first with the id breaking a tie, so that a
/// page boundary never repeats or skips a policy.
/// </summary>
public sealed class PolicyListQuery(Db db) : IPolicyListQuery
{
    private sealed record Row(Guid Id, string Number, string Policyholder, string? Product, string? Objects, DateOnly EffectiveFrom, DateOnly EffectiveTo,
        decimal Premium, string Currency, string Status);

    private sealed record Credit(Guid PolicyId, decimal Amount, DateOnly ReceivedOn, string Method);

    /// <summary>A LIKE pattern of the text as PostgreSQL read it: % and _ stay wildcards, [ is literal.</summary>
    public static string Pattern(string q) => "%" + q.ToLowerInvariant().Replace("[", "[[]", StringComparison.Ordinal) + "%";

    // A change approved in its original's open month corrects the original itself (owner, 13.10.2026; docs/04 r10, docs/05 a15;
    // legacy PR #225): it is no policy of its own, so its record is not listed — known by what its approval wrote, an approved change
    // whose own lines carry no premium row (its premium was booked on the original's lines). The original is listed once, at its
    // premium as corrected; the record stays, with its approval and number, in the original's history.
    private const string Where = """
        from core.policy p
        join core.contract c on c.id = p.contract_id
        join core.party h on h.id = c.policyholder_id
        left join core.product pr on pr.id = p.product_id
        where not (p.replaces_policy_id is not null and p.status = N'active'
                   and exists (select 1 from core.policy_line l where l.policy_id = p.id)
                   and not exists (select 1 from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                                   where l.policy_id = p.id))
          and (@all = 1 or lower(p.number) like @pattern or lower(c.number) like @pattern
               or lower(h.display_name) like @pattern or h.tax_id like @pattern
               or (@objects = 1 and exists (select 1 from core.insured_object o where o.policy_id = p.id and lower(o.description) like @pattern)))
        """;

    public async Task<PolicyList> ListAsync(PolicyListRequest request, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Limit, PolicyListRequest.MaxLimit);
        if (request.Status is { } st && !PolicyListRequest.Statuses.Contains(st)) throw new ArgumentOutOfRangeException(nameof(request), st, "unknown policy status");
        var q = JsText.Trim(request.Search);
        // Paid and unpaid are said of an active policy only (what its schedule still asks), so either narrows the list to active ones.
        var status = request.Payment == PolicyPayment.All ? request.Status : request.Status is null or "active" ? "active" : null;
        var none = request.Payment != PolicyPayment.All && status is null;
        var filter = new { all = q == "", pattern = Pattern(q), status, objects = request.SearchObjects };
        var order = request.Oldest ? "order by p.created_at, p.id" : "order by p.created_at desc, p.id desc";
        var today = db.Today();
        return await db.SnapshotAsync(async tx =>
        {
            var counts = (await tx.QueryAsync<(string Status, int N)>("select p.status, count(*) " + Where + " group by p.status", filter))
                .ToDictionary(c => c.Status, c => c.N, StringComparer.Ordinal);
            int total;
            IReadOnlyList<Guid> page;
            if (none)
            {
                (total, page) = (0, []);
            }
            else if (request.Payment == PolicyPayment.All)
            {
                total = (await tx.QueryAsync<int>("select count(*) " + Where + StatusFilter, filter)).Single();
                page = (await tx.QueryAsync<Guid>($"select p.id {Where}{StatusFilter} {order} offset @offset rows fetch next @limit rows only",
                    new { filter.all, filter.pattern, filter.status, filter.objects, offset = request.Offset, limit = request.Limit })).ToList();
            }
            else
            {
                // Every active policy the search finds, by what it still owes as of the day (the same rule as its row's amount).
                var candidates = (await tx.QueryAsync<Guid>($"select p.id {Where}{StatusFilter} {order}", filter)).ToList();
                var owed = await OutstandingAsync(tx, candidates, today);
                var kept = candidates.Where(id => request.Payment == PolicyPayment.Paid ? owed[id] == 0 : owed[id] > 0).ToList();
                (total, page) = (kept.Count, kept.Skip(request.Offset).Take(request.Limit).ToList());
            }
            return new PolicyList(await ItemsAsync(tx, page, today), total, counts, today);
        }, ct);
    }

    private const string StatusFilter = " and (@status is null or p.status = @status)";

    /// <summary>What each policy's schedule still asks as of the day, read a thousand policies at a time (SQL Server's parameter limit).</summary>
    private static async Task<Dictionary<Guid, System.Numerics.BigInteger>> OutstandingAsync(Tx tx, IReadOnlyList<Guid> ids, DateOnly today)
    {
        var owed = new Dictionary<Guid, System.Numerics.BigInteger>();
        foreach (var chunk in ids.Chunk(1000))
        {
            var schedules = await Billing.LoadSchedulesAsync(tx, chunk, today);
            var credits = await CreditsAsync(tx, chunk);
            foreach (var id in chunk)
                owed[id] = PolicyCard.ExactOutstanding(schedules.GetValueOrDefault(id) ?? [], credits[id].Select(c => new Billing.AllocationRow(c.Amount, c.ReceivedOn, c.Method)), today);
        }
        return owed;
    }

    private static async Task<ILookup<Guid, Credit>> CreditsAsync(Tx tx, IReadOnlyCollection<Guid> ids) =>
        (ids.Count == 0 ? [] : await tx.QueryAsync<Credit>("""
            select i.policy_id as PolicyId, a.amount as Amount, p.received_on as ReceivedOn, p.method as Method
            from core.payment_allocation a join core.payment p on p.id = a.payment_id
            join core.installment i on i.id = a.installment_id where i.policy_id in @ids
            """, new { ids })).ToLookup(c => c.PolicyId);

    /// <summary>The rows of one page (at most 200 policies), in the page's order.</summary>
    private static async Task<IReadOnlyList<PolicyListItem>> ItemsAsync(Tx tx, IReadOnlyList<Guid> ids, DateOnly today)
    {
        if (ids.Count == 0) return [];
        var rows = (await tx.QueryAsync<Row>("""
            select p.id as Id, p.number as Number, h.display_name as Policyholder, pr.name_ka as Product,
                   (select string_agg(o.description, '; ') from core.insured_object o where o.policy_id = p.id) as Objects,
                   p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id)
                   + (select coalesce(sum(l.premium), 0) from core.policy_line l join core.policy r on r.id = l.policy_id
                      where r.replaces_policy_id = p.id and r.status = N'active'
                        and not exists (select 1 from core.premium_transaction t join core.policy_line rl on rl.id = t.policy_line_id
                                        where rl.policy_id = r.id)) as Premium,
                   p.premium_currency as Currency, p.status as Status
            from core.policy p
            join core.contract c on c.id = p.contract_id
            join core.party h on h.id = c.policyholder_id
            left join core.product pr on pr.id = p.product_id
            where p.id in @ids
            """, new { ids })).ToDictionary(r => r.Id);
        var schedules = await Billing.LoadSchedulesAsync(tx, ids.ToArray(), today);
        var credits = await CreditsAsync(tx, ids.ToArray());
        var reinsured = (await tx.QueryAsync<Guid>("""
            select distinct l.policy_id from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id in @ids and c.booked_on <= @today
            """, new { ids, today })).ToHashSet();
        return ids.Select(id =>
        {
            var r = rows[id];
            var mine = credits[r.Id];
            var paid = mine.Where(c => c.ReceivedOn <= today).Aggregate(System.Numerics.BigInteger.Zero, (s, c) => s + PaymentActions.Exact(c.Amount));
            System.Numerics.BigInteger? outstanding = r.Status is "draft" or "pending_approval" ? null
                : PolicyCard.ExactOutstanding(schedules.GetValueOrDefault(r.Id) ?? [], mine.Select(c => new Billing.AllocationRow(c.Amount, c.ReceivedOn, c.Method)), today);
            return new PolicyListItem(r.Id, r.Number, r.Policyholder, r.Product, r.Objects, r.EffectiveFrom, r.EffectiveTo, r.Status, r.Currency.Trim(),
                PaymentActions.Exact(r.Premium), paid, outstanding, reinsured.Contains(r.Id));
        }).ToList();
    }
}
