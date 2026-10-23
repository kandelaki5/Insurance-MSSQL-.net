using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Intermediaries;

/// <summary>Agents, brokers, banks and employees selling policies (legacy intermediaries/page.tsx).</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Row(Guid Id, string? PartyName, string? IdNumber, string Kind, string? ContractNo, string? ValidTo, decimal? CommissionPercent, string Status, string? Phone);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync() =>
        Rows = (await Db.ReadTxAsync(tx => tx.QueryAsync<Row>("""
            select i.id as Id, p.display_name as PartyName, coalesce(p.tax_id, p.foreign_id) as IdNumber, i.kind as Kind, i.contract_no as ContractNo,
                   format(i.valid_to, 'dd.MM.yyyy') as ValidTo, i.commission_percent as CommissionPercent, i.status as Status, p.phone as Phone
            from core.intermediary i join core.party p on p.id = i.party_id
            order by p.display_name_lower, i.id
            """))).ToList();
}
