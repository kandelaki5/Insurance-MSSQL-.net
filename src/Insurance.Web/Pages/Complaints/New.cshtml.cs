using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Complaints;

/// <summary>Registering a complaint (legacy complaints/new/page.tsx, complaint-form.tsx; action registerComplaint).</summary>
public sealed class NewModel : AppPageModel
{
    public IActionResult OnGet()
    {
        if (!Me.CanWrite) return Redirect("/complaints");
        Values["received_on"] = Fmt.Iso(Db.Today());
        Values["channel"] = "written";
        return Page();
    }

    private sealed record PartyRow(Guid Id, string? DisplayName, string? Phone, string? Email);

    public async Task<IActionResult> OnPostAsync()
    {
        var values = TrimmedForm();
        foreach (var (k, v) in values) Values[k] = v;
        if (!Me.CanWrite)
        {
            FormError = Ka.Common.ReadOnly;
            return Page();
        }
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;
        var e = ComplaintTexts.Errors.ByKey;
        var today = Db.Today();
        var receivedOn = values.GetValueOrDefault("received_on") ?? "";
        if (!Dates.IsIsoDate(receivedOn) || string.CompareOrdinal(receivedOn, Fmt.Iso(today)) > 0) Errors["received_on"] = e["receivedOn"];
        var channel = V("channel") is { } ch && ComplaintTexts.Channels.ContainsKey(ch) ? ch : "written";
        var category = JsNumber.Parse(values.GetValueOrDefault("category") ?? "undefined");
        if (!(category >= 1 && category <= 4)) Errors["category"] = e["category"];
        if (V("description") is null) Errors["description"] = e["description"];

        var party = V("party_id_number") is { } number
            ? await Db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<PartyRow>(
                "select top (1) id as Id, display_name as DisplayName, phone as Phone, email as Email from core.party where tax_id = @number or foreign_id = @number", new { number }))
            : null;
        var customerName = V("customer_name") ?? (string.IsNullOrEmpty(party?.DisplayName) ? null : party.DisplayName) ?? "";
        if (customerName == "") Errors["customer_name"] = e["customer"];
        Guid? policyId = null, claimId = null;
        if (V("policy_number") is { } policyNumber)
        {
            policyId = await Db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>("select top (1) id from core.policy where upper(number) = @n", new { n = JsText.ToUpperCase(policyNumber) }));
            if (policyId is null) Errors["policy_number"] = e["policy"];
        }
        if (V("claim_number") is { } claimNumber)
        {
            claimId = await Db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>("select top (1) id from core.claim where upper(number) = @n", new { n = JsText.ToUpperCase(claimNumber) }));
            if (claimId is null) Errors["claim_number"] = e["claim"];
        }
        if (Errors.Count > 0) return Page();
        // A category such as 2.5 passed the range check before and was refused by the database's integer column.
        if (category != Math.Floor(category)) throw new InvalidOperationException($"complaint category {category} is not a whole number");

        var acknowledged = Posted("acknowledged");
        var contact = V("customer_contact") ?? (Phone.FormatPhone(party?.Phone) is { Length: > 0 } p ? p : null) ?? (string.IsNullOrEmpty(party?.Email) ? null : party.Email);
        var id = Guid.NewGuid();
        try
        {
            await Db.AsUserAsync(Me.Id, async tx =>
            {
                var complaintNumber = await tx.NextNumberAsync("complaint");
                await tx.ExecuteAsync("""
                    insert into core.complaint (id, number, received_on, channel, party_id, customer_name, customer_contact, policy_id, claim_id,
                                                category, description, acknowledged_at, created_by)
                    values (@id, @complaintNumber, @receivedOn, @channel, @partyId, @customerName, @contact, @policyId, @claimId,
                            @category, @description, case when @acknowledged = 1 then sysdatetimeoffset() end, @userId)
                    """, new
                {
                    id, complaintNumber, receivedOn = Dates.ParseIsoDate(receivedOn), channel, partyId = party?.Id, customerName, contact, policyId, claimId,
                    category = (short)category, description = V("description"), acknowledged, userId = Me.Id,
                });
                return 0;
            });
        }
        catch (Exception ex) when (DbError.From(ex)?.Code == Numbering.SeriesExhaustedCode)
        {
            FormError = Ka.Common.SeriesExhausted;
            return Page();
        }
        return Redirect($"/complaints/{id}?registered=1");
    }
}
