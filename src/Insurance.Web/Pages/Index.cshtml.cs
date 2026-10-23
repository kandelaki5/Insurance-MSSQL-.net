using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Dapper;

namespace Insurance.Web.Pages;

/// <summary>The home page with the reminders (legacy (app)/page.tsx).</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Portfolio(int Active, int AllPolicies, decimal ActivePremiumGel, decimal CededGel);
    public async Task<IActionResult> OnGetDataAsync()
    {
        await OnGetAsync();
        var today=Db.Today();
        var portfolio=await Db.ReadAsync(c=>c.QuerySingleAsync<Portfolio>("""
            select (select count(*) from core.policy where status=N'active') as Active,
                   (select count(*) from core.policy) as AllPolicies,
                   (select coalesce(sum(l.premium_gel),0) from core.policy_line l join core.policy p on p.id=l.policy_id where p.status=N'active') as ActivePremiumGel,
                   (select coalesce(sum(amount_gel),0) from core.premium_cession where booked_on<=@today) as CededGel
            """,new {today}));
        return new JsonResult(new { Totals, LastRates, Reminders, Portfolio=portfolio, AsOf=today });
    }
    public sealed record Counts(int Parties, int Classes, int Unrated, int Reviews, int Complaints, int Pending);

    public sealed record RateRow(string Currency, decimal Rate, DateOnly RateDate);

    public sealed record Reminder(int Count, string Label, string Href);

    public Counts Totals { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<RateRow> LastRates { get; private set; } = [];
    public IReadOnlyList<Reminder> Reminders { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var today = Db.Today();
        var soon = today.AddDays(30);
        await Db.ReadTxAsync(async tx =>
        {
            Totals = await tx.QuerySingleOrDefaultAsync<Counts>("""
                select (select count(*) from core.party) as Parties,
                       (select count(*) from core.insurance_class where parent_code is null) as Classes,
                       (select count(*) from core.party where risk_level is null) as Unrated,
                       (select count(*) from core.party where risk_review_on <= @soon) as Reviews,
                       (select count(*) from core.complaint where status = N'open') as Complaints,
                       (select count(*) from core.policy where status = N'pending_approval') as Pending
                """, new { soon }) ?? Totals;
            LastRates = (await tx.QueryAsync<RateRow>("""
                select x.currency as Currency, x.rate as Rate, x.rate_date as RateDate
                from (select currency, rate, rate_date, row_number() over (partition by currency order by rate_date desc) as n from core.exchange_rate) x
                where x.n = 1
                order by x.currency
                """)).ToList();
            var kycIssues = await PartyKyc.PartiesWithKycIssuesAsync(tx, today);
            // Oral FMS instructions past their 24 hours without the written one: the compliance officer decides whether to
            // resume (confidential, so only the compliance roles see the count).
            var unconfirmed = Aml.CanSeeAml(Me)
                ? await tx.QuerySingleOrDefaultAsync<int>("""
                    select count(*) from core.fms_suspension
                    where kind = N'oral' and written_confirmed_at is null and lifted_at is null
                      and ends_at > @now and @now >= dateadd(hour, 24, instructed_at)
                    """, new { now = tx.Now() })
                : 0;
            Reminders = new[]
            {
                new Reminder(unconfirmed, AmlTexts.Suspension.UnconfirmedReminder, "/compliance/aml-cases"),
                new Reminder(Totals.Unrated, Ka.Home.UnratedParties, "/parties?filter=unrated"),
                new Reminder(Totals.Reviews, Ka.Home.RiskReviewsDue, "/parties?filter=reviewDue"),
                new Reminder(kycIssues, Ka.Home.KycIssues, "/parties"),
                new Reminder(Totals.Complaints, Ka.Home.OpenComplaints, "/complaints?status=open"),
                new Reminder(Totals.Pending, Ka.Home.PendingPolicies, "/policies/pending"),
            }.Where(r => r.Count > 0).ToList();
            return 0;
        });
    }
}
