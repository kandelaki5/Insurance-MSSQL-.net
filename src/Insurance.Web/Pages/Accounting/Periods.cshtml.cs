using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting;

/// <summary>
/// Accounting periods (legacy accounting/periods/page.tsx, close-answer.tsx, actions.ts closePeriod). The answer to a press of
/// "close month" is shown on the page that was pressed (Codex H28, H30): nothing in the address bar, so ?close=… shows nothing.
/// </summary>
public sealed class PeriodsModel : AppPageModel
{
    public sealed record ClosedRow(DateOnly PeriodStart, string ClosedAt, string? ClosedBy);

    public sealed record MonthRow(DateOnly Month, ClosedRow? Closed, bool Ended, bool Closable, bool Overdue, bool PreviousOpen);

    public int CloseWithin { get; private set; }
    public IReadOnlyList<MonthRow> Months { get; private set; } = [];
    public PeriodClose.Answer? Answer { get; private set; }

    /// <summary>The answer's text, or null for an outcome the texts do not know (legacy known()).</summary>
    public string? AnswerText => Answer is null ? null : QueryParams.Known(Ka.Periods.CloseOutcomes, Answer.Outcome);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCloseAsync()
    {
        Answer = await PeriodClose.CloseAsync(Db, Me, Form("period_start"));
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var today = Rates.TodayInTbilisi();
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        // The closing deadline is the company's own setting (owner, 06.10.2026); 5 days until it is set.
        var (settings, first, closed) = await Db.ReadTxAsync(async tx =>
        {
            var settings = await Settings.LoadSettingsAsync(tx);
            var first = await tx.ExecuteScalarAsync<DateOnly?>("select min(effective_from) from core.policy");
            var closed = (await tx.QueryAsync<ClosedRow>("""
                select p.period_start as PeriodStart,
                       format(switchoffset(p.closed_at, '+04:00'), 'dd.MM.yyyy HH:mm', 'en-US') as ClosedAt,
                       u.full_name as ClosedBy
                from core.accounting_period p left join core.app_user u on u.id = p.closed_by
                where p.status = N'closed'
                """)).ToList();
            return (settings, first, closed);
        });
        CloseWithin = CloseDeadline.CloseWithinDays(settings);
        var closedByMonth = closed.ToDictionary(c => c.PeriodStart);
        // The close button is the permission accounting.period.close (the action checks it again).
        var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
        var closes = Permissions.EvaluatePermission(Me.Subject, PeriodClose.Permission, inputs.Overrides, inputs.Groups).Allowed;

        // Every month from the first policy up to the current one.
        var months = new List<DateOnly>();
        for (var m = first is { } f ? new DateOnly(f.Year, f.Month, 1) : currentMonth; m <= currentMonth; m = Dates.AddMonths(m, 1)) months.Add(m);
        DateOnly? oldestOpen = months.Cast<DateOnly?>().FirstOrDefault(m => !closedByMonth.ContainsKey(m!.Value));
        Months = months.Select(m =>
        {
            var closedRow = closedByMonth.GetValueOrDefault(m);
            var ended = m < currentMonth;
            return new MonthRow(m, closedRow, ended, ended && m == oldestOpen && closes, CloseDeadline.IsCloseOverdue(m, today, CloseWithin),
                closedRow is null && ended && m != oldestOpen);
        }).ToList();
    }
}
