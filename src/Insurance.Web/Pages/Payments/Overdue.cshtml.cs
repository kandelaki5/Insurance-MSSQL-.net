using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Payments;

/// <summary>Overdue premium as of today (legacy src/app/(app)/payments/overdue/page.tsx), the longest overdue first.</summary>
public sealed class OverdueModel(Rates rates) : AppPageModel
{
    public sealed record Row(Guid Id, string Number, string Policyholder, string Currency, long Overdue, long Outstanding, long OverdueGel, DateOnly OldestDue, int DaysLate);

    public DateOnly Today { get; private set; }
    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public long TotalGel { get; private set; }

    public async Task OnGetAsync()
    {
        Today = Rates.TodayInTbilisi();
        var today = Today;
        var (policies, installments, allocations) = await Db.ReadTxAsync(async tx =>
        {
            // Only policies with at least one instalment already past its due date can be overdue.
            var policies = (await tx.QueryAsync<(Guid Id, string Number, string Policyholder, string Currency)>("""
                select p.id, p.number, h.display_name, p.premium_currency
                from core.policy p
                join core.contract c on c.id = p.contract_id
                join core.party h on h.id = c.policyholder_id
                where p.status <> N'draft'
                  and exists (select 1 from core.installment i where i.policy_id = p.id and i.due_on < @today)
                order by p.created_at, p.id
                """, new { today })).ToList();
            // Adjustments booked by today only, as on the card (H23).
            var installments = (await tx.QueryAsync<(Guid PolicyId, Guid Id, DateOnly DueOn, decimal Amount)>("""
                select i.policy_id, i.id, i.due_on,
                       i.amount + coalesce((select sum(j.change) from core.installment_adjustment j
                                            where j.installment_id = i.id and j.booked_on <= @today), 0)
                from core.installment i join core.policy p on p.id = i.policy_id
                where p.status <> N'draft'
                """, new { today })).ToList();
            var allocations = (await tx.QueryAsync<(Guid PolicyId, decimal Amount, DateOnly ReceivedOn, string Method)>("""
                select i.policy_id, a.amount, pay.received_on, pay.method
                from core.payment_allocation a
                join core.payment pay on pay.id = a.payment_id
                join core.installment i on i.id = a.installment_id
                """)).ToList();
            return (policies, installments, allocations);
        });
        var installmentsOf = installments.ToLookup(i => i.PolicyId);
        var allocationsOf = allocations.ToLookup(a => a.PolicyId);
        var rateCache = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var policy in policies)
        {
            var code = policy.Currency.Trim();
            if (!rateCache.TryGetValue(code, out var rate)) rateCache[code] = rate = await rates.RateOnAsync(code, today) ?? 0;
            var currency = Billing.ParseCurrency(code);
            var position = Domain.Finance.Receivable.ReceivableAsOf(
                Billing.ToInstallments(installmentsOf[policy.Id].Select(i => new Billing.InstallmentRow(i.Id, i.DueOn, i.Amount)), currency),
                Billing.ToCredits(allocationsOf[policy.Id].Select(a => new Billing.AllocationRow(a.Amount, a.ReceivedOn, a.Method)), currency),
                today, (_, _) => rate);
            if (position.Overdue.Minor <= 0) continue;
            var oldest = position.Balances.First(b => b.Outstanding > 0 && b.Installment.DueOn < today);
            rows.Add(new Row(policy.Id, policy.Number, policy.Policyholder, code, position.Overdue.Minor, position.Outstanding.Minor,
                Money.ToGel(position.Overdue, today, (_, _) => rate), oldest.Installment.DueOn, Dates.DaysBetween(oldest.Installment.DueOn, today)));
        }
        Rows = rows.OrderByDescending(r => r.DaysLate).ToList();
        TotalGel = Rows.Sum(r => r.OverdueGel);
    }
}
