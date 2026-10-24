using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting;

/// <summary>The impairment reserve on insurance receivables (legacy accounting/impairment/page.tsx): the admin and the accountant only.</summary>
public sealed class ImpairmentModel(Rates rates) : AppPageModel
{
    /// <summary>Null when a figure overflowed: nothing exact to show (Exact is false).</summary>
    public ImpairmentReport.Report? Report { get; private set; }
    public DateOnly AsOfDate { get; private set; }
    public bool Exact { get; private set; }
    public bool Exportable { get; private set; }

    /// <summary>The last day of the previous month: the usual reporting date.</summary>
    public static DateOnly DefaultDate(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddDays(-1);

    /// <summary>The date asked for when it is a real date not after today; else the default.</summary>
    public static DateOnly AsOf(string? requested, DateOnly today) =>
        requested is not null && Dates.IsIsoDate(requested) && !requested.StartsWith("0000", StringComparison.Ordinal) && Dates.ParseIsoDate(requested) <= today
            ? Dates.ParseIsoDate(requested) : DefaultDate(today);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Redirect("/");
        var asOf = AsOf(Query("date"), Db.Today());
        AsOfDate = asOf;
        var c = await Db.ReadTxAsync(tx => ImpairmentReport.BuildCheckedAsync(tx, rates, asOf));
        (Report, Exact, Exportable) = (c.Report, c.Exact, c.Exportable);
        return Page();
    }
}
