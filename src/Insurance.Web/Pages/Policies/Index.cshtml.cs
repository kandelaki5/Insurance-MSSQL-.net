using Dapper;
using Insurance.Web.Infrastructure;
using Insurance.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>The policy list (legacy src/app/(app)/policies/page.tsx): the latest 200, searched by number, contract, client or id number.</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record CreditRow(Guid PolicyId, decimal Amount, DateOnly ReceivedOn, string Method);

    // The Angular view uses the same authenticated page and the existing exact billing rules.
    // No financial write operation is exposed by this read-only handler.
    public async Task<IActionResult> OnGetDataAsync()
    {
        await OnGetAsync();
        var today = Db.Today();
        var data = await Db.ReadTxAsync(async tx =>
        {
            var ids = Rows.Select(p => p.Id).ToArray();
            var schedules = await Billing.LoadSchedulesAsync(tx, ids, today);
            var credits = ids.Length == 0 ? [] : (await tx.QueryAsync<CreditRow>("""
                select i.policy_id as PolicyId, a.amount as Amount, p.received_on as ReceivedOn, p.method as Method
                from core.payment_allocation a join core.payment p on p.id=a.payment_id
                join core.installment i on i.id=a.installment_id where i.policy_id in @ids
                """, new { ids })).ToList();
            var reinsured = ids.Length == 0 ? [] : (await tx.QueryAsync<Guid>("""
                select distinct l.policy_id from core.premium_cession c
                join core.premium_transaction t on t.id=c.premium_transaction_id
                join core.policy_line l on l.id=t.policy_line_id
                where l.policy_id in @ids and c.booked_on <= @today
                """, new { ids, today })).ToHashSet();
            return Rows.Select(p =>
            {
                var paid = credits.Where(c => c.PolicyId == p.Id && c.ReceivedOn <= today).Sum(c => c.Amount);
                var balance = PolicyCard.ExactOutstanding(schedules.GetValueOrDefault(p.Id) ?? [],
                    credits.Where(c => c.PolicyId == p.Id).Select(c => new Billing.AllocationRow(c.Amount,c.ReceivedOn,c.Method)), today);
                return new { p.Id, p.Number, Client=p.Policyholder, p.Product, Vehicle=p.Object ?? "—", Plate="",
                    p.Premium, Currency=p.PremiumCurrency.Trim(), Paid=paid,
                    Outstanding=p.Status is "draft" or "pending_approval" ? (decimal?)null : (decimal)balance/100m,
                    Status=p.Status, From=p.EffectiveFrom, To=p.EffectiveTo, Reinsured=reinsured.Contains(p.Id) };
            }).ToArray();
        });
        return new JsonResult(new { Rows=data, Limit=200, AsOf=today, Me.CanWrite });
    }
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
