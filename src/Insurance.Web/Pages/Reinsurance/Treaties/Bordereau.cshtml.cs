using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Reinsurance.Treaties;

/// <summary>The bordereau of a treaty for a period (legacy reinsurance/treaties/[id]/bordereau/page.tsx).</summary>
public sealed class BordereauModel(Rates rates) : AppPageModel
{
    public Insurance.Application.Services.Bordereau.Statement B { get; private set; } = null!;

    /// <summary>The period asked for: a real from date, else the current month's first day; a real to date not before it, else the current month's last day.</summary>
    public static (DateOnly From, DateOnly To) Period(string? from, string? to, DateOnly today)
    {
        var defaultFrom = new DateOnly(today.Year, today.Month, 1);
        static bool Iso(string? v) => v is not null && Dates.IsIsoDate(v) && !v.StartsWith("0000", StringComparison.Ordinal);
        var f = Iso(from) ? Dates.ParseIsoDate(from!) : defaultFrom;
        var t = Iso(to) && Dates.ParseIsoDate(to!) >= f ? Dates.ParseIsoDate(to!) : Dates.AddMonths(defaultFrom, 1).AddDays(-1);
        return (f, t);
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var treatyId)) return NotFound();
        var (from, to) = Period(Query("from"), Query("to"), Db.Today());
        var b = await Db.ReadTxAsync(tx => Insurance.Application.Services.Bordereau.BuildAsync(tx, rates, treatyId, from, to));
        if (b is null) return NotFound();
        B = b;
        return Page();
    }
}
