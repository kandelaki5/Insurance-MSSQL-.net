using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Reinsurance.Treaties;

/// <summary>One treaty, its cessions and its close (legacy reinsurance/treaties/[id]/page.tsx, actions.ts closeTreaty).</summary>
public sealed class DetailModel : AppPageModel
{
    public sealed record Treaty(Guid Id, string Number, string Name, string Reinsurer, Guid ReinsurerId, string? Broker, string Kind, DateOnly PeriodFrom, DateOnly PeriodTo,
        string Currency, decimal? CededPercent, decimal? Retention, int? Lines, decimal? Priority, decimal? TreatyLimit, decimal? TreatyPremium,
        decimal CommissionPercent, decimal BrokerPercent, string? ReinsurerRating, string? RatingAgency, string Status, string? Note, string? Classes);

    public sealed record Cession(Guid Id, Guid PolicyId, string PolicyNumber, string ClassCode, decimal SharePercent, decimal Amount, string Currency,
        decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, bool IsCancellation);

    public Treaty Tr { get; private set; } = null!;
    public IReadOnlyList<Cession> Cessions { get; private set; } = [];
    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var treatyId)) return NotFound();
        var treaty = await Db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<Treaty>("""
            select t.id as Id, t.number as Number, t.name as Name, r.display_name as Reinsurer, r.id as ReinsurerId, b.display_name as Broker, t.kind as Kind,
                   t.period_from as PeriodFrom, t.period_to as PeriodTo, rtrim(t.currency) as Currency, t.ceded_percent as CededPercent, t.retention as Retention,
                   t.lines as Lines, t.priority as Priority, t.treaty_limit as TreatyLimit, t.treaty_premium as TreatyPremium,
                   t.commission_percent as CommissionPercent, t.broker_percent as BrokerPercent, t.reinsurer_rating as ReinsurerRating,
                   t.rating_agency as RatingAgency, t.status as Status, t.note as Note,
                   (select string_agg(c.class_code + N'. ' + ic.name_ka, N'; ') within group (order by ic.sort_order)
                    from core.ri_treaty_class c join core.insurance_class ic on ic.code = c.class_code where c.treaty_id = t.id) as Classes
            from core.ri_treaty t join core.party r on r.id = t.reinsurer_party_id left join core.party b on b.id = t.broker_party_id
            where t.id = @treatyId
            """, new { treatyId }));
        if (treaty is null) return NotFound();
        Tr = treaty;
        Cessions = (await Db.ReadTxAsync(tx => tx.QueryAsync<Cession>("""
            select top (300) c.id as Id, p.id as PolicyId, p.number as PolicyNumber, l.class_code as ClassCode, c.share_percent as SharePercent, c.amount as Amount,
                   rtrim(p.premium_currency) as Currency, c.amount_gel as AmountGel, c.commission_gel as CommissionGel, c.booked_on as BookedOn,
                   c.cover_from as CoverFrom, c.cover_to as CoverTo, convert(bit, case when c.cancels_cession_id is not null then 1 else 0 end) as IsCancellation
            from core.premium_cession c
            join core.premium_transaction tx on tx.id = c.premium_transaction_id
            join core.policy_line l on l.id = tx.policy_line_id
            join core.policy p on p.id = l.policy_id
            where c.treaty_id = @treatyId order by c.created_at desc, c.id desc
            """, new { treatyId }))).ToList();
        Saved = !string.IsNullOrEmpty(Query("saved"));
        return Page();
    }

    /// <summary>Closes the treaty (no new cessions; existing ones stay as booked) and shows the page again.</summary>
    public async Task<IActionResult> OnPostCloseAsync(string id)
    {
        var formId = Form("id") ?? "";
        if (Me.CanWrite && Guid.TryParseExact(formId, "D", out var treatyId))
            await Insurance.Application.Services.Treaties.CloseAsync(Db, Me, treatyId);
        return Redirect($"/reinsurance/treaties/{id}");
    }
}
