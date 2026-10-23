using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Insurance.Web.Pages.Compliance;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Insurance.Web.Pages.Compliance.Sanctions;

/// <summary>
/// Sanctions screening against the UN list (legacy compliance/sanctions/page.tsx; actions refreshUnList, screenEveryone,
/// decideScreening). Any signed-in user may open it (owner, 18.10.2026: unchanged until the company meeting); the actions
/// need a role that may write.
/// </summary>
public sealed class IndexModel(IHttpClientFactory httpClients, IOptions<SanctionsOptions> options) : AppPageModel
{
    public SanctionsScreening.Version? Version { get; private set; }
    public IReadOnlyList<SanctionsScreening.ScreeningRow> Open { get; private set; } = [];
    public string? Loaded { get; private set; }
    public string? Screened { get; private set; }
    public string? Hits { get; private set; }
    public string? Reused { get; private set; }

    public ScreeningTableView Table => new(Open, Me.CanWrite, "/compliance/sanctions", true);

    private async Task LoadAsync()
    {
        Loaded = Query("loaded");
        Screened = Query("screened");
        Hits = Query("hits");
        Reused = Query("reused");
        (Version, Open) = await Db.ReadTxAsync(async tx => (await SanctionsScreening.ListVersionAsync(tx), await SanctionsScreening.ScreeningsAsync(tx)));
    }

    public async Task OnGetAsync() => await LoadAsync();

    private async Task<IActionResult> ShowAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRefreshAsync()
    {
        if (!Me.CanWrite) return await ShowAsync();
        var result = await SanctionsScreening.LoadUnListAsync(Db, httpClients.CreateClient("sanctions"), options.Value.UnListUrl);
        return Redirect($"/compliance/sanctions?loaded={(result.Ok ? result.Entries.ToString(System.Globalization.CultureInfo.InvariantCulture) : "failed")}");
    }

    public async Task<IActionResult> OnPostScreenAllAsync()
    {
        if (!Me.CanWrite) return await ShowAsync();
        var result = await Db.AsUserAsync(Me.Id, tx => SanctionsScreening.ScreenPartiesAsync(tx, Me.Id));
        return Redirect($"/compliance/sanctions?screened={result.Screened}&hits={result.Hits}&reused={result.Reused}");
    }

    public async Task<IActionResult> OnPostDecideScreeningAsync() =>
        await ComplianceActions.DecideScreeningAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync();
}
