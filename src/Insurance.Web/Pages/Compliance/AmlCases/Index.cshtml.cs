using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;
using Insurance.Web.Pages.Compliance;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Compliance.AmlCases;

/// <summary>Unusual and suspicious transactions (legacy compliance/aml-cases/page.tsx) and the register of suspensions in force.</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Row(Guid Id, string Number, DateOnly OpenedOn, Guid PartyId, string? PartyName, string Source, string Status, string? StrSentAt, bool Late);

    public string Status { get; private set; } = "";
    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public IReadOnlyList<Aml.SuspensionRow> InForce { get; private set; } = [];

    public SuspensionSectionView Section => new(AmlTexts.Suspension.InForceTitle, null, null, InForce, Aml.IsComplianceRole(Me), "/compliance/aml-cases", true, false, Db.Now());

    private async Task LoadAsync()
    {
        var requested = Query("status") ?? "";
        Status = AmlTexts.Cases.Statuses.ContainsKey(requested) ? requested : "";
        (Rows, InForce) = await Db.ReadTxAsync(async tx => (
            (IReadOnlyList<Row>)(await tx.QueryAsync<Row>("""
                select top (300) c.id as Id, c.number as Number, c.opened_on as OpenedOn, c.party_id as PartyId, pa.display_name as PartyName, c.source as Source,
                       c.status as Status, format(switchoffset(c.str_sent_at, '+04:00'), 'dd.MM.yyyy HH:mm') as StrSentAt,
                       convert(bit, case when c.str_sent_at is not null
                            and convert(date, switchoffset(c.str_sent_at, '+04:00')) > convert(date, switchoffset(c.suspicion_at, '+04:00')) then 1 else 0 end) as Late
                from core.aml_case c join core.party pa on pa.id = c.party_id
                where @status = N'' or c.status = @status
                order by c.opened_on desc, c.created_at desc, c.id
                """, new { status = Status })).ToList(),
            await Aml.SuspensionsOfAsync(tx, inForce: true)));
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Aml.CanSeeAml(Me)) return Redirect("/");
        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ShowAsync()
    {
        if (!Aml.CanSeeAml(Me)) return Redirect("/");
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateSuspensionAsync() =>
        await ComplianceActions.UpdateSuspensionAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync();
}
