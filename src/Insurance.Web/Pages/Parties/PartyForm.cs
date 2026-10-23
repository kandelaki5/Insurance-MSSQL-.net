using System.Globalization;
using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;

namespace Insurance.Web.Pages.Parties;

/// <summary>What a party form shows: its kind, the values, the field errors and a message for the whole form.</summary>
public sealed record PartyFormView(string Kind, IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, string> Errors, string? Message, bool ReadOnly);

/// <summary>The party form's action (legacy parties/actions.ts saveParty), shared by the new-party page and the party card.</summary>
public static partial class PartyForm
{
    private static readonly string[] TextFields =
    [
        "tax_id", "foreign_id", "country_code", "first_name", "last_name", "birth_date", "gender", "name", "legal_form",
        "state_share_percent", "registration_place", "legal_address", "actual_address", "phone", "email",
        "identified_at", "verified_at", "sanctions_checked_at", "self_declaration_on",
    ];
    private static readonly string[] BooleanFields = ["is_resident", "is_public_entity", "is_pep", "is_group_member"];
    private static readonly string[] DateFields = ["birth_date", "identified_at", "verified_at", "sanctions_checked_at", "self_declaration_on"];
    public static readonly string[] OwnerDateFields = ["birth_date", "document_issued_on", "document_valid_to", "identified_at", "verified_at", "sanctions_checked_at"];

    [GeneratedRegex(@"^[0-9]{11}\z")]
    private static partial Regex PersonTaxId();

    [GeneratedRegex(@"^[0-9]{9}\z")]
    private static partial Regex OrganizationTaxId();

    /// <summary>Every filled date must be a real one; a slip such as a five-digit year becomes a form error.</summary>
    public static void CheckDates(IReadOnlyDictionary<string, string> values, IEnumerable<string> fields, Dictionary<string, string> errors)
    {
        foreach (var field in fields)
            if (values.GetValueOrDefault(field) is { Length: > 0 } v && !Dates.IsPlausibleDate(v)) errors[field] = Ka.Party.Errors.Date;
    }

    public sealed record Outcome(Guid? SavedId, Dictionary<string, string> Errors, string? Message);

    /// <summary>
    /// Saves a new or changed party. Refused, with nothing written: the viewer; missing names; a tax id that is not 11
    /// (person) or 9 (organization) digits, or missing for a resident; no identifier for a non-resident; a state share
    /// outside 0–100; an impossible date; a phone that is not a number; a tax id another party has.
    /// </summary>
    public static async Task<Outcome> SaveAsync(Db db, CurrentUser user, IReadOnlyDictionary<string, string> values, Func<string, bool> has)
    {
        if (!user.CanWrite) return new Outcome(null, [], Ka.Common.ReadOnly);
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;

        var id = V("id");
        var kind = values.GetValueOrDefault("kind") == "organization" ? "organization" : "person";
        var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind };
        foreach (var field in TextFields) row[field] = V(field);
        foreach (var field in BooleanFields) row[field] = has(field);
        var country = JsText.ToUpperCase(V("country_code") ?? "GE");
        row["country_code"] = country.Length > 2 ? country[..2] : country;

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var e = Ka.Party.Errors.ByKey;
        decimal? share = null;
        if (kind == "person")
        {
            if (row["first_name"] is null) errors["first_name"] = e["firstName"];
            if (row["last_name"] is null) errors["last_name"] = e["lastName"];
            if (row["tax_id"] is string t && !PersonTaxId().IsMatch(t)) errors["tax_id"] = e["taxIdPerson"];
            foreach (var field in new[] { "name", "legal_form", "state_share_percent", "self_declaration_on" }) row[field] = null;
            row["is_public_entity"] = false;
        }
        else
        {
            if (row["name"] is null) errors["name"] = e["name"];
            if (row["tax_id"] is string t && !OrganizationTaxId().IsMatch(t)) errors["tax_id"] = e["taxIdOrganization"];
            if (row["state_share_percent"] is string s)
            {
                var number = JsNumber.Parse(s);
                if (!(number >= 0 && number <= 100)) errors["state_share_percent"] = e["stateShare"];
                else share = JsNumber.ParseFiniteDecimal(s);
            }
            foreach (var field in new[] { "first_name", "last_name", "birth_date", "gender" }) row[field] = null;
        }
        if ((bool)row["is_resident"]! && row["tax_id"] is null && !errors.ContainsKey("tax_id"))
            errors["tax_id"] = kind == "person" ? e["taxIdPerson"] : e["taxIdOrganization"];
        if (!(bool)row["is_resident"]! && row["foreign_id"] is null && row["tax_id"] is null) errors["foreign_id"] = e["foreignId"];
        CheckDates(values, DateFields.Where(field => row[field] is not null), errors);
        // Phone: country code (+995 unless another is entered) and number, stored in one format.
        if (V("phone") is { } phone)
        {
            row["phone"] = Phone.NormalizePhone(values.GetValueOrDefault("phone_code") ?? "", phone);
            if (row["phone"] is null) errors["phone"] = e["phone"];
        }
        if (errors.Count > 0) return new Outcome(null, errors, null);

        row["state_share_percent"] = share;
        foreach (var field in DateFields)
            if (row[field] is string d) row[field] = Dates.ParseIsoDate(d);
        try
        {
            var savedId = await db.AsUserAsync(user.Id, async tx =>
            {
                var columns = row.Keys.ToList();
                var p = new Dapper.DynamicParameters();
                foreach (var (k, v) in row) p.Add(k, v);
                if (id is not null)
                {
                    // A malformed id is an error, as PostgreSQL refused it (22P02).
                    var key = Guid.Parse(id, CultureInfo.InvariantCulture);
                    p.Add("__id", key);
                    await tx.ExecuteAsync($"update core.party set {string.Join(", ", columns.Select(c => $"{c} = @{c}"))}, updated_at = core.tx_now() where id = @__id", p);
                    return key;
                }
                var created = Guid.NewGuid();
                p.Add("__id", created);
                await tx.ExecuteAsync($"insert into core.party (id, {string.Join(", ", columns)}) values (@__id, {string.Join(", ", columns.Select(c => "@" + c))})", p);
                return created;
            });
            return new Outcome(savedId, [], null);
        }
        catch (Exception ex) when (DbError.From(ex)?.Code == DbError.UniqueViolation)
        {
            return new Outcome(null, new Dictionary<string, string> { ["tax_id"] = e["duplicate"] }, null);
        }
    }
}
