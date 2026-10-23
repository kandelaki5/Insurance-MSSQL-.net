using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Parties;

/// <summary>The client register (legacy parties/page.tsx): search by name or number, filter by risk state.</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Row(Guid Id, string Kind, string DisplayName, string? IdNumber, string Segment, string? RiskLevel, DateOnly? RiskReviewOn, string? Phone);

    public string Q { get; private set; } = "";
    public string Filter { get; private set; } = "all";
    public DateOnly Today { get; private set; }
    public DateOnly Soon { get; private set; }
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Q = JsText.Trim(Query("q") ?? "");
        var filter = Query("filter");
        Filter = filter is not null && Ka.Party.Filters.ContainsKey(filter) ? filter : "all";
        // % and _ match anything, as in PostgreSQL's like; [ is only literal there, so it is escaped here.
        var pattern = $"%{Q.ToLowerInvariant().Replace("[", "[[]", StringComparison.Ordinal)}%";
        Today = Rates.TodayInTbilisi();
        Soon = Today.AddDays(30);
        Rows = (await Db.ReadTxAsync(tx => tx.QueryAsync<Row>("""
            select top (200) id as Id, kind as Kind, display_name as DisplayName, coalesce(tax_id, foreign_id) as IdNumber, segment as Segment,
                   risk_level as RiskLevel, risk_review_on as RiskReviewOn, phone as Phone
            from core.party
            where (@q = N'' or display_name_lower like @pattern or tax_id like @pattern or lower(foreign_id) like @pattern)
              and (@filter = N'all'
                   or (@filter = N'unrated' and risk_level is null)
                   or (@filter = N'reviewDue' and risk_review_on <= @soon))
            order by display_name_sort, display_name_lower, id
            """, new { q = Q, pattern, filter = Filter, soon = Soon }))).ToList();
    }
}
