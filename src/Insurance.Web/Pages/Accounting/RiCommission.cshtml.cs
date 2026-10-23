using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Accounting;

/// <summary>
/// The reinsurer's commission receivable at a day per treaty (N1-4 00140) and its settlements (docs/05 3ე; legacy PR #362
/// accounting/ri-commission/page.tsx and actions.ts): the admin and the accountant see it; recording and reversing a settlement is the
/// permission reinsurance.commission.settle. A press is answered as the old actions did: a redirect to the page with ?date=…&amp;result=….
/// </summary>
public sealed class RiCommissionModel(Rates rates) : AppPageModel
{
    public DateOnly AsOfDate { get; private set; }
    public RiCommissionLedger.Report Report { get; private set; } = null!;
    public bool Settles { get; private set; }

    /// <summary>The answer to the last press, for an outcome the texts know (legacy t.outcomes[result]).</summary>
    public string? Message { get; private set; }

    /// <summary>The day asked for when it is a real date not after today; else today.</summary>
    public static DateOnly AsOf(string? requested, DateOnly today) =>
        requested is not null && Dates.IsIsoDate(requested) && !requested.StartsWith("0000", StringComparison.Ordinal) && Dates.ParseIsoDate(requested) <= today
            ? Dates.ParseIsoDate(requested) : today;

    /// <summary>A treaty as the settlements table names it: its number and name, else its id.</summary>
    public string TreatyLabel(Guid id) => Report.Positions.FirstOrDefault(p => p.TreatyId == id) is { } p ? $"{p.Number} {p.Name}" : Uuid.Text(id);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Me.RoleCode is not ("admin" or "accountant")) return Redirect("/");
        var today = Db.Today();
        AsOfDate = AsOf(Query("date"), today);
        Report = await Db.ReadTxAsync(tx => RiCommissionLedger.BuildReportAsync(tx, rates.LatestRateOnOrBeforeAsync, AsOfDate));
        var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
        Settles = Permissions.EvaluatePermission(Me.Subject, RiCommissionLedger.Permission, inputs.Overrides, inputs.Groups).Allowed;
        var result = Query("result");
        Message = string.IsNullOrEmpty(result) ? null : QueryParams.Known(RiCommissionTexts.Outcomes, result);
        return Page();
    }

    public async Task<IActionResult> OnPostRecordAsync() =>
        Redirect(RiCommissionLedger.BackTo(await RiCommissionLedger.RecordAsync(Db, Me, Form("treaty_id"), Form("settled_on"), Form("kind"), Form("currency"),
            Form("amount"), Form("reference"), Form("note"))));

    public async Task<IActionResult> OnPostReverseAsync() =>
        Redirect(RiCommissionLedger.BackTo(await RiCommissionLedger.ReverseAsync(Db, Me, Form("settlement_id"), Form("reversed_on"))));
}
