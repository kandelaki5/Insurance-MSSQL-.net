using Dapper;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Policies;

/// <summary>The policy list (legacy src/app/(app)/policies/page.tsx): the latest 200, searched by number, contract, client or id number.</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Row(Guid Id, string Number, string Policyholder, string? Product, string? Object, DateOnly EffectiveFrom, DateOnly EffectiveTo,
        decimal Premium, string PremiumCurrency, string Status);

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public string Q { get; private set; } = "";

    /// <summary>A LIKE pattern of the text as PostgreSQL read it: % and _ stay wildcards, [ is literal.</summary>
    public static string Pattern(string q) => "%" + q.ToLowerInvariant().Replace("[", "[[]", StringComparison.Ordinal) + "%";

    public async Task OnGetAsync()
    {
        Q = Insurance.Domain.Common.JsText.Trim(Query("q") ?? "");
        var pattern = Pattern(Q);
        // A change approved in its original's open month corrects the original itself (owner, 13.10.2026; docs/04 r10, docs/05
        // a15; legacy PR #225): it is no policy of its own, so its record is not listed — known by what its approval wrote, an
        // approved change whose own lines carry no premium row (its premium was booked on the original's lines). The original
        // is listed once, at its premium as corrected; the record stays, with its approval and number, in the original's history.
        Rows = (await Db.ReadAsync(c => c.QueryAsync<Row>("""
            select top (200) p.id as Id, p.number as Number, h.display_name as Policyholder, pr.name_ka as Product,
                   (select string_agg(o.description, '; ') from core.insured_object o where o.policy_id = p.id) as Object,
                   p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id)
                   + (select coalesce(sum(l.premium), 0) from core.policy_line l join core.policy r on r.id = l.policy_id
                      where r.replaces_policy_id = p.id and r.status = N'active'
                        and not exists (select 1 from core.premium_transaction t join core.policy_line rl on rl.id = t.policy_line_id
                                        where rl.policy_id = r.id)) as Premium,
                   p.premium_currency as PremiumCurrency, p.status as Status
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
            order by p.created_at desc
            """, new { all = Q == "", pattern }))).ToList();
    }
}
