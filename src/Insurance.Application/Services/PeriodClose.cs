using Insurance.Application.Data;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>
/// Closing an accounting month (legacy src/app/(app)/accounting/periods/actions.ts closePeriod). Only the oldest open month that has
/// already ended can be closed, so months close in order and nothing can be booked into them afterwards; there is no reopen. Every
/// press gets an answer (Codex H25): who may close, whether the month is a real one that has ended, whether an earlier month is still
/// open, already closed, done. Who may close is the permission accounting.period.close, for the user of the session — a quick check
/// before anything else, the binding one first in the transaction, before the period lock.
/// </summary>
public static class PeriodClose
{
    public const string Permission = "accounting.period.close";

    /// <summary>What one press of "close month" got: the outcome and the month it named (when it was a real one).</summary>
    public sealed record Answer(string Outcome, string Month);

    public static async Task<Answer> CloseAsync(Db db, CurrentUser user, string? periodStartText, DateOnly? today = null)
    {
        var periodStart = periodStartText ?? "";
        var month = Month.IsMonthStart(periodStart) ? periodStart : "";
        Answer Of(string outcome) => new(outcome, month);
        if (!await PermissionStore.HasPermissionAsync(db, user, Permission)) return Of("forbidden");
        // A malformed or impossible month (2026-00-01, 2026-13-01) never reaches the database.
        if (!Month.IsMonthStart(periodStart)) return Of("invalid");
        var start = Dates.ParseIsoDate(periodStart);
        var now = today ?? Rates.TodayInTbilisi();
        if (start >= new DateOnly(now.Year, now.Month, 1)) return Of("not-ended");

        var outcome = await db.AsUserAsync(user.Id, async tx =>
        {
            // The binding permission check: in this transaction, before the lock and anything read or written.
            if (!await PermissionStore.HasPermissionAsync(tx, user, Permission)) return "forbidden";
            // Exclusive period lock (review H06): waits for financial movements in flight, and any movement that starts now waits for
            // this close and then sees the month closed.
            await tx.LockAsync("core.accounting_period");
            // Every month from the first policy's month up to the one before this: the series starts on the 1st of that month, as the
            // page counts it, so a first policy from mid-month cannot step over a month that is still open (PR249).
            var first = await tx.ExecuteScalarAsync<DateOnly?>("select min(effective_from) from core.policy");
            var closed = (await tx.QueryAsync<DateOnly>("select period_start from core.accounting_period where status = N'closed'")).ToHashSet();
            var series = first is { } f ? new DateOnly(f.Year, f.Month, 1) : start;
            for (var m = series; m <= start.AddMonths(-1); m = m.AddMonths(1))
                if (!closed.Contains(m)) return "earlier-open";

            // "Done" only when this request actually closed the month; a month closed before stays as it was.
            var changed = await tx.ExecuteScalarAsync<int>("""
                if not exists (select 1 from core.accounting_period with (updlock, holdlock) where period_start = @start)
                begin
                  insert into core.accounting_period (period_start, status, closed_at, closed_by) values (@start, N'closed', sysdatetimeoffset(), @userId);
                  select 1;
                end
                else
                begin
                  update core.accounting_period set status = N'closed', closed_at = sysdatetimeoffset(), closed_by = @userId
                  where period_start = @start and status = N'open';
                  select @@rowcount;
                end
                """, new { start, userId = user.Id });
            return changed > 0 ? "done" : "already";
        });
        return Of(outcome);
    }
}
