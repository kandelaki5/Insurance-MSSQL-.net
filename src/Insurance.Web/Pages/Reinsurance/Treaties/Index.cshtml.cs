using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Reinsurance.Treaties;

/// <summary>The list of reinsurance treaties (legacy reinsurance/treaties/page.tsx).</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Row(Guid Id, string Number, string Name, string Reinsurer, string Kind, DateOnly PeriodFrom, DateOnly PeriodTo, string Currency,
        string Status, string? Terms, string? Classes, decimal CededGel);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync() =>
        Rows = (await Db.ReadTxAsync(tx => tx.QueryAsync<Row>("""
            select t.id as Id, t.number as Number, t.name as Name, r.display_name as Reinsurer, t.kind as Kind, t.period_from as PeriodFrom, t.period_to as PeriodTo,
                   rtrim(t.currency) as Currency, t.status as Status,
                   case t.kind
                     when N'surplus' then convert(nvarchar(50), t.retention) + N' × ' + convert(nvarchar(20), t.lines)
                     when N'excess_of_loss' then convert(nvarchar(50), t.priority) + N' xs ' + coalesce(convert(nvarchar(50), t.treaty_limit), N'∞')
                     else convert(nvarchar(50), t.ceded_percent) + N' %'
                   end as Terms,
                   (select string_agg(c.class_code, N', ') within group (order by c.class_code) from core.ri_treaty_class c where c.treaty_id = t.id) as Classes,
                   (select coalesce(sum(pc.amount_gel), 0) from core.premium_cession pc where pc.treaty_id = t.id) as CededGel
            from core.ri_treaty t join core.party r on r.id = t.reinsurer_party_id
            order by t.status, t.period_from desc, t.number
            """))).ToList();
}
