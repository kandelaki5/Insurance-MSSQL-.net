using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Finance;

/// <summary>
/// The financial director's panel (legacy src/app/(app)/finance/panel/page.tsx; owner, 17.10.2026, docs/05 ვ8): the
/// requests above the threshold not yet carried out, by kind, and the chosen month's figures from the monthly report.
/// Who may see it is finance.panel.view, read before any request or report is read: without it the page says so and
/// reads nothing else. Read-only: each request links to its card, where the existing action authorises it.
/// </summary>
public sealed class PanelModel(Rates rates, ILogger<PanelModel> log) : AppPageModel
{
    public bool Allowed { get; private set; }
    public IReadOnlyDictionary<string, string?> Cursor { get; private set; } = new Dictionary<string, string?>();
    public string Month { get; private set; } = "";
    public IReadOnlyDictionary<string, FdPanel.QueueRead> Reads { get; private set; } = new Dictionary<string, FdPanel.QueueRead>();

    /// <summary>The month's report, or null when it could not be built — never figures made up in its place.</summary>
    public PremiumReport.Report? Report { get; private set; }

    /// <summary>The address of the panel for the month shown, with one list moved to another page and the others where they are.</summary>
    public string Href(string queue, string? after)
    {
        var q = new List<string> { "month=" + Uri.EscapeDataString(Month) };
        foreach (var k in FdPanel.Queues)
        {
            var v = k == queue ? after : Cursor[k];
            if (!string.IsNullOrEmpty(v)) q.Add($"{k}={Uri.EscapeDataString(v)}");
        }
        return "/finance/panel?" + string.Join("&", q);
    }

    public async Task OnGetAsync()
    {
        var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
        Allowed = Permissions.EvaluatePermission(Me.Subject, "finance.panel.view", inputs.Overrides, inputs.Groups).Allowed;
        if (!Allowed) return;
        Cursor = FdPanel.ParseCursor(Query);
        Month = PremiumReport.ReportMonth(Query("month"), Db.Today());
        var reads = new Dictionary<string, FdPanel.QueueRead>(StringComparer.Ordinal);
        foreach (var queue in FdPanel.Queues)
            reads[queue] = await Db.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, queue, Cursor[queue],
                (q, e) => log.LogError(e, "fd panel: the {Queue} list could not be read", q)));
        Reads = reads;
        try
        {
            Report = await Db.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, Month));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "fd panel: the monthly report could not be built");
            Report = null;
        }
    }
}
