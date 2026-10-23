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
        Rows = (await Db.ReadAsync(c => c.QueryAsync<Row>("""
            select top (200) p.id as Id, p.number as Number, h.display_name as Policyholder, pr.name_ka as Product,
                   (select string_agg(o.description, '; ') from core.insured_object o where o.policy_id = p.id) as Object,
                   p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id) as Premium,
                   p.premium_currency as PremiumCurrency, p.status as Status
            from core.policy p
            join core.contract c on c.id = p.contract_id
            join core.party h on h.id = c.policyholder_id
            left join core.product pr on pr.id = p.product_id
            where @all = 1 or lower(p.number) like @pattern or lower(c.number) like @pattern
               or lower(h.display_name) like @pattern or h.tax_id like @pattern
            order by p.created_at desc
            """, new { all = Q == "", pattern }))).ToList();
    }
}
