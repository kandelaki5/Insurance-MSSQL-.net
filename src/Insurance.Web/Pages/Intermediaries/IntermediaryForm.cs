using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;

namespace Insurance.Web.Pages.Intermediaries;

public sealed record IntermediaryFormView(IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, string> Errors, string? Message, bool ReadOnly);

/// <summary>The intermediary form's action (legacy intermediaries/actions.ts saveIntermediary), shared by the new and the card page.</summary>
public static class IntermediaryForm
{
    private static readonly string[] Kinds = ["agent", "broker", "bank", "employee"];

    public sealed record Outcome(Guid? SavedId, Dictionary<string, string> Errors, string? Message);

    /// <summary>
    /// Saves a new or changed agent, broker, bank or employee. A new one must already be in the client register (found by
    /// its personal number or identification code); the type is required; the commission is 0–100.
    /// </summary>
    public static async Task<Outcome> SaveAsync(Db db, CurrentUser user, IReadOnlyDictionary<string, string> values)
    {
        if (!user.CanWrite) return new Outcome(null, [], Ka.Common.ReadOnly);
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;
        var id = V("id");
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var kind = values.GetValueOrDefault("kind") ?? "";
        if (!Kinds.Contains(kind)) errors["kind"] = Ka.Intermediary.Errors.Kind;
        double? commission = V("commission_percent") is { } text ? JsNumber.Parse(text) : null;
        if (commission is { } cm && !(cm >= 0 && cm <= 100)) errors["commission_percent"] = Ka.Intermediary.Errors.Commission;

        Guid? partyId = null;
        if (id is null)
        {
            var number = values.GetValueOrDefault("party_id_number") ?? "";
            partyId = await db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>(
                "select top (1) id from core.party where tax_id = @number or foreign_id = @number", new { number }));
            if (partyId is null) errors["party_id_number"] = Ka.Intermediary.Errors.PartyNotFound;
        }
        if (errors.Count > 0) return new Outcome(null, errors, null);

        var row = new
        {
            kind,
            contractNo = V("contract_no"),
            // Dates come from the date fields as ISO text; anything else is refused by the database, as before.
            contractDate = V("contract_date"),
            validTo = V("valid_to"),
            status = values.GetValueOrDefault("status") == "terminated" ? "terminated" : "active",
            commission = commission is { } c ? JsNumber.ParseFiniteDecimal(values["commission_percent"]) ?? (decimal)c : (decimal?)null,
        };
        var savedId = await db.AsUserAsync(user.Id, async tx =>
        {
            if (id is not null)
            {
                var key = Guid.Parse(id);
                await tx.ExecuteAsync("""
                    update core.intermediary set kind = @kind, contract_no = @contractNo, contract_date = @contractDate, valid_to = @validTo,
                           status = @status, commission_percent = @commission
                    where id = @key
                    """, new { row.kind, row.contractNo, row.contractDate, row.validTo, row.status, row.commission, key });
                return key;
            }
            var created = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert into core.intermediary (id, kind, contract_no, contract_date, valid_to, status, commission_percent, party_id)
                values (@created, @kind, @contractNo, @contractDate, @validTo, @status, @commission, @partyId)
                """, new { created, row.kind, row.contractNo, row.contractDate, row.validTo, row.status, row.commission, partyId });
            return created;
        });
        return new Outcome(savedId, [], null);
    }
}
