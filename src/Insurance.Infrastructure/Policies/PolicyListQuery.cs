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
               or lower(h.display_name) like @pattern or h.tax_id like @pattern)
        """;

    public async Task<PolicyList> ListAsync(PolicyListRequest request, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Limit, PolicyListRequest.MaxLimit);
        var q = JsText.Trim(request.Search);
        var filter = new { all = q == "", pattern = Pattern(q) };
        var today = db.Today();
        return await db.SnapshotAsync(async tx =>
        {
            var total = (await tx.QueryAsync<int>("select count(*) " + Where, filter)).Single();
            var rows = (await tx.QueryAsync<Row>($"""
                select p.id as Id, p.number as Number, h.display_name as Policyholder, pr.name_ka as Product,
                       (select string_agg(o.description, '; ') from core.insured_object o where o.policy_id = p.id) as Objects,
                       p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                       (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id)
                       + (select coalesce(sum(l.premium), 0) from core.policy_line l join core.policy r on r.id = l.policy_id
                          where r.replaces_policy_id = p.id and r.status = N'active'
                            and not exists (select 1 from core.premium_transaction t join core.policy_line rl on rl.id = t.policy_line_id
                                            where rl.policy_id = r.id)) as Premium,
                       p.premium_currency as Currency, p.status as Status
                {Where}
                order by p.created_at desc, p.id desc
                offset @offset rows fetch next @limit rows only
                """, new { filter.all, filter.pattern, offset = request.Offset, limit = request.Limit })).ToList();
            var ids = rows.Select(r => r.Id).ToArray();
            var schedules = await Billing.LoadSchedulesAsync(tx, ids, today);
            var credits = (ids.Length == 0 ? [] : await tx.QueryAsync<Credit>("""
                select i.policy_id as PolicyId, a.amount as Amount, p.received_on as ReceivedOn, p.method as Method
                from core.payment_allocation a join core.payment p on p.id = a.payment_id
                join core.installment i on i.id = a.installment_id where i.policy_id in @ids
                """, new { ids })).ToLookup(c => c.PolicyId);
            var reinsured = ids.Length == 0 ? [] : (await tx.QueryAsync<Guid>("""
                select distinct l.policy_id from core.premium_cession c
                join core.premium_transaction t on t.id = c.premium_transaction_id
                join core.policy_line l on l.id = t.policy_line_id
                where l.policy_id in @ids and c.booked_on <= @today
                """, new { ids, today })).ToHashSet();
            var items = rows.Select(r =>
            {
                var mine = credits[r.Id];
                var paid = mine.Where(c => c.ReceivedOn <= today).Aggregate(System.Numerics.BigInteger.Zero, (s, c) => s + PaymentActions.Exact(c.Amount));
                System.Numerics.BigInteger? outstanding = r.Status is "draft" or "pending_approval" ? null
                    : PolicyCard.ExactOutstanding(schedules.GetValueOrDefault(r.Id) ?? [], mine.Select(c => new Billing.AllocationRow(c.Amount, c.ReceivedOn, c.Method)), today);
                return new PolicyListItem(r.Id, r.Number, r.Policyholder, r.Product, r.Objects, r.EffectiveFrom, r.EffectiveTo, r.Status, r.Currency.Trim(),
                    PaymentActions.Exact(r.Premium), paid, outstanding, reinsured.Contains(r.Id));
            }).ToList();
            return new PolicyList(items, total, today);
        }, ct);
    }
}
