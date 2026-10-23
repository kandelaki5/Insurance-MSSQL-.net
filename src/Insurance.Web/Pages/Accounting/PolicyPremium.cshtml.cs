using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting;

/// <summary>Premium by policy and insurance class (legacy accounting/policy-premium/page.tsx): the admin and the accountant only.</summary>
public sealed class PolicyPremiumModel : AppPageModel
{
    public PolicyPremiumReport.Report Report { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Redirect("/");
        var (from, to) = PolicyPremiumReport.ReportPeriod(Query("from"), Query("to"), Db.Today());
        Report = await Db.ReadTxAsync(tx => PolicyPremiumReport.BuildAsync(tx, from, to));
        return Page();
    }
}
