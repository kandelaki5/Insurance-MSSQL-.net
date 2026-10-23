using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Complaints;

/// <summary>The complaints register (legacy complaints/page.tsx; order №18/2017: 30-day answer).</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Row(Guid Id, string Number, DateOnly ReceivedOn, string Channel, string CustomerName, string? PolicyNumber, short Category, string Status, DateOnly? RespondedOn);

    public string Status { get; private set; } = "";
    public DateOnly Today { get; private set; }
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var requested = Query("status") ?? "";
        Status = ComplaintTexts.Statuses.ContainsKey(requested) ? requested : "";
        Today = Db.Today();
        Rows = (await Db.ReadTxAsync(tx => tx.QueryAsync<Row>("""
            select top (300) c.id as Id, c.number as Number, c.received_on as ReceivedOn, c.channel as Channel, c.customer_name as CustomerName,
                   p.number as PolicyNumber, c.category as Category, c.status as Status, c.responded_on as RespondedOn
            from core.complaint c left join core.policy p on p.id = c.policy_id
            where @status = N'' or c.status = @status
            order by c.received_on desc, c.created_at desc, c.id
            """, new { status = Status }))).ToList();
    }
}
