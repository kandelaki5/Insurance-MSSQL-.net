using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Intermediaries;

/// <summary>One intermediary (legacy intermediaries/[id]/page.tsx; the form's action saveIntermediary).</summary>
public sealed class CardModel : AppPageModel
{
    private sealed record Row(Guid Id, Guid PartyId, string? PartyName, string Kind, string? ContractNo, DateOnly? ContractDate, DateOnly? ValidTo, string Status, decimal? CommissionPercent);

    public string? PartyName { get; private set; }
    public string Kind { get; private set; } = "";
    public bool Saved { get; private set; }
    public IntermediaryFormView FormModel { get; private set; } = null!;

    private async Task<Dictionary<string, string>?> LoadAsync(string id)
    {
        if (PermissionPanel.Uuid(id) is not { } key) return null;
        var row = await Db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<Row>("""
            select i.id as Id, i.party_id as PartyId, p.display_name as PartyName, i.kind as Kind, i.contract_no as ContractNo, i.contract_date as ContractDate,
                   i.valid_to as ValidTo, i.status as Status, i.commission_percent as CommissionPercent
            from core.intermediary i join core.party p on p.id = i.party_id
            where i.id = @key
            """, new { key }));
        if (row is null) return null;
        PartyName = row.PartyName;
        Kind = row.Kind;
        Saved = !string.IsNullOrEmpty(Query("saved"));
        var initial = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = row.Id.ToString(), ["party_id"] = row.PartyId.ToString(), ["kind"] = row.Kind, ["status"] = row.Status,
        };
        if (row.PartyName is not null) initial["party_name"] = row.PartyName;
        if (row.ContractNo is not null) initial["contract_no"] = row.ContractNo;
        if (row.ContractDate is { } cd) initial["contract_date"] = Fmt.Iso(cd);
        if (row.ValidTo is { } vt) initial["valid_to"] = Fmt.Iso(vt);
        if (row.CommissionPercent is { } cp) initial["commission_percent"] = cp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return initial;
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (await LoadAsync(id) is not { } initial) return NotFound();
        FormModel = new IntermediaryFormView(initial, new Dictionary<string, string>(), null, !Me.CanWrite);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string id)
    {
        var values = TrimmedForm();
        var outcome = await IntermediaryForm.SaveAsync(Db, Me, values);
        if (outcome.SavedId is { } saved) return Redirect($"/intermediaries/{saved}?saved=1");
        if (await LoadAsync(id) is not { } initial) return NotFound();
        // The legacy form keeps what it was given as well as what was posted: the party's name and link are not posted.
        var shown = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var key in new[] { "party_id", "party_name" })
            if (initial.TryGetValue(key, out var v)) shown.TryAdd(key, v);
        FormModel = new IntermediaryFormView(shown, outcome.Errors, outcome.Message, !Me.CanWrite);
        return Page();
    }
}
