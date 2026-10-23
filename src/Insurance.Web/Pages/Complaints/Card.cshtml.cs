using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Complaints;

/// <summary>One complaint (legacy complaints/[id]/page.tsx, respond-form.tsx; actions acknowledgeComplaint, respondComplaint).</summary>
public sealed class CardModel : AppPageModel
{
    public sealed record Complaint(
        Guid Id, string Number, DateOnly ReceivedOn, string Channel, string CustomerName, string? CustomerContact, Guid? PartyId, Guid? PolicyId,
        string? PolicyNumber, Guid? ClaimId, string? ClaimNumber, short Category, string Description, string Status, string? AcknowledgedAt,
        DateOnly? RespondedOn, string? ResponseText, string? Measures, string? Outcome, string? Note);

    public Complaint C { get; private set; } = null!;
    public DateOnly Today { get; private set; }
    public DateOnly Deadline { get; private set; }
    public bool Overdue { get; private set; }
    public bool Registered { get; private set; }

    /// <summary>The respond form's values: the complaint as stored, or what was posted (legacy useActionState).</summary>
    public IReadOnlyDictionary<string, string> Respond { get; private set; } = new Dictionary<string, string>();

    private async Task<bool> LoadAsync(string id)
    {
        if (PermissionPanel.Uuid(id) is not { } key) return false;
        var c = await Db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<Complaint>("""
            select c.id as Id, c.number as Number, c.received_on as ReceivedOn, c.channel as Channel, c.customer_name as CustomerName,
                   c.customer_contact as CustomerContact, c.party_id as PartyId, c.policy_id as PolicyId, p.number as PolicyNumber, c.claim_id as ClaimId,
                   cl.number as ClaimNumber, c.category as Category, c.description as Description, c.status as Status,
                   format(switchoffset(c.acknowledged_at, '+04:00'), 'dd.MM.yyyy HH:mm') as AcknowledgedAt,
                   c.responded_on as RespondedOn, c.response_text as ResponseText, c.measures as Measures, c.outcome as Outcome, c.note as Note
            from core.complaint c
            left join core.policy p on p.id = c.policy_id
            left join core.claim cl on cl.id = c.claim_id
            where c.id = @key
            """, new { key }));
        if (c is null) return false;
        C = c;
        Today = Rates.TodayInTbilisi();
        Deadline = c.ReceivedOn.AddDays(ComplaintTexts.ResponseDays);
        Overdue = c.Status == "open" && Deadline < Today;
        Registered = !string.IsNullOrEmpty(Query("registered"));
        Respond = new Dictionary<string, string>
        {
            ["id"] = c.Id.ToString(), ["status"] = c.Status, ["responded_on"] = Fmt.Iso(c.RespondedOn ?? Today), ["response_text"] = c.ResponseText ?? "",
            ["measures"] = c.Measures ?? "", ["outcome"] = c.Outcome ?? "", ["note"] = c.Note ?? "",
        };
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id) => await LoadAsync(id) ? Page() : NotFound();

    /// <summary>Records that receipt of the complaint was confirmed to the customer (art. 6.3 "დ").</summary>
    public async Task<IActionResult> OnPostAcknowledgeAsync(string id)
    {
        if (Me.CanWrite && PermissionPanel.Uuid(Form("id")) is { } complaintId)
        {
            await Db.AsUserAsync(Me.Id, tx => tx.ExecuteAsync(
                "update core.complaint set acknowledged_at = sysdatetimeoffset() where id = @complaintId and acknowledged_at is null", new { complaintId }));
            return Redirect($"/complaints/{complaintId}");
        }
        return await LoadAsync(id) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRespondAsync(string id)
    {
        var values = TrimmedForm();
        async Task<IActionResult> Show()
        {
            if (!await LoadAsync(id)) return NotFound();
            Respond = new Dictionary<string, string>(values) { ["id"] = C.Id.ToString() };
            return Page();
        }
        if (!Me.CanWrite)
        {
            Notice = Ka.Common.ReadOnly;
            return await Show();
        }
        if (PermissionPanel.Uuid(values.GetValueOrDefault("id")) is not { } complaintId) return await Show();
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;
        var status = V("status") is { } s && ComplaintTexts.Statuses.ContainsKey(s) ? s : "open";
        if (status != "open" && (!Dates.IsIsoDate(values.GetValueOrDefault("responded_on") ?? "") || V("response_text") is null))
            Errors["response_text"] = ComplaintTexts.Errors.Response;
        if (Errors.Count > 0) return await Show();
        await Db.AsUserAsync(Me.Id, tx => tx.ExecuteAsync("""
            update core.complaint set status = @status, responded_on = @respondedOn, response_text = @responseText, measures = @measures,
                   outcome = @outcome, note = @note
            where id = @complaintId
            """, new
        {
            status, respondedOn = status == "open" ? (DateOnly?)null : Dates.ParseIsoDate(values["responded_on"]), responseText = V("response_text"),
            measures = V("measures"), outcome = V("outcome"), note = V("note"), complaintId,
        }));
        Notice = ComplaintTexts.Saved;
        return await Show();
    }
}
