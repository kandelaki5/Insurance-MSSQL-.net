using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Compliance;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Insurance.Web.Pages.Compliance;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Parties;

/// <summary>
/// The client's card (legacy parties/[id]/page.tsx): the party form, KYC, beneficial owners, sanctions screening,
/// suspensions, the risk assessment and the change history; with the actions saveParty, addBeneficialOwner,
/// updateBeneficialOwner, assessPartyRisk, screenOneParty, decideScreening, createSuspension and updateSuspension.
/// </summary>
public sealed class CardModel : AppPageModel
{
    public sealed record AssessmentRow(Guid Id, DateOnly AssessedOn, string ComputedLevel, string Level, int Score, string Factors, bool IsOverride, string? Reason, DateOnly ReviewDueOn, string? UserName);

    public sealed record HistoryRow(long Id, string At, string Action, string? UserName, string? OldRow, string? NewRow);

    public sealed record Change(string Label, string From, string To);

    public Guid Id { get; private set; }
    public IReadOnlyDictionary<string, string> Initial { get; private set; } = new Dictionary<string, string>();
    public string Kind { get; private set; } = "person";
    public string DisplayName { get; private set; } = "";
    public string Segment { get; private set; } = "";
    public string? Level { get; private set; }
    public string? RiskAssessedOn { get; private set; }
    public DateOnly? ReviewOn { get; private set; }
    public string? ReviewState { get; private set; }
    public bool SanctionsBlocked { get; private set; }
    public string IdNumberForCase { get; private set; } = "";
    public DateOnly Today { get; private set; }
    public PartyRisk.Computed? Risk { get; private set; }
    public PartyKyc.Result? Kyc { get; private set; }
    public bool ComplianceRole { get; private set; }
    public IReadOnlyList<SanctionsScreening.ScreeningRow> ScreeningRows { get; private set; } = [];
    public Aml.SuspensionRow? Suspension { get; private set; }
    public IReadOnlyList<Aml.SuspensionRow> SuspensionRows { get; private set; } = [];
    public IReadOnlyList<AssessmentRow> Assessments { get; private set; } = [];
    public IReadOnlyList<HistoryRow> History { get; private set; } = [];

    // The forms shown again after a refusal: the party form, the owner form and the risk form, each with its own answer.
    public PartyFormView? PartyFormState { get; private set; }
    public IReadOnlyDictionary<string, string> OwnerValues { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> OwnerErrors { get; private set; } = new Dictionary<string, string>();
    public string? OwnerMessage { get; private set; }
    public IReadOnlyDictionary<string, string> RiskErrors { get; private set; } = new Dictionary<string, string>();
    public string? RiskMessage { get; private set; }

    public string? SavedFlag { get; private set; }
    public string? Screened { get; private set; }
    public string? Hits { get; private set; }
    public bool TargetError { get; private set; }

    public PartyFormView PartyFormModel => PartyFormState ?? new PartyFormView(Kind, Initial, new Dictionary<string, string>(), null, !Me.CanWrite);

    public SuspensionSectionView SuspensionSection => new(AmlTexts.Suspension.Title, Id, null, SuspensionRows, Aml.IsComplianceRole(Me), $"/parties/{Id}", false, TargetError);

    public ScreeningTableView ScreeningTable => new(ScreeningRows.Take(20).ToList(), Me.CanWrite, $"/parties/{Id}", false);

    private static string Text(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Reads everything the card shows; false when there is no such client.</summary>
    private async Task<bool> LoadAsync(string id)
    {
        if (PermissionPanel.Uuid(id) is not { } partyId) return false;
        Id = partyId;
        SavedFlag = Query("saved");
        Screened = Query("screened");
        Hits = Query("hits");
        TargetError = Query("suspension") == "target";
        return await Db.ReadTxAsync(async tx =>
        {
            var row = (IDictionary<string, object?>?)await tx.Connection.QueryFirstOrDefaultAsync("""
                select id, kind, is_resident, tax_id, foreign_id, country_code, first_name, last_name, birth_date, gender,
                       name, legal_form, state_share_percent, is_public_entity, registration_place, legal_address,
                       actual_address, phone, email, risk_level, identified_at, verified_at, is_pep,
                       sanctions_checked_at, is_group_member, display_name, segment,
                       risk_assessed_on, risk_review_on, self_declaration_on, sanctions_blocked
                from core.party where id = @partyId
                """, new { partyId });
            if (row is null) return false;
            Initial = row.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => Text(p.Value!), StringComparer.Ordinal);
            Kind = (string)row["kind"]! == "organization" ? "organization" : "person";
            DisplayName = (string?)row["display_name"] ?? "";
            Segment = (string?)row["segment"] ?? "";
            Level = (string?)row["risk_level"];
            SanctionsBlocked = (bool)row["sanctions_blocked"]!;
            RiskAssessedOn = Initial.GetValueOrDefault("risk_assessed_on");
            ReviewOn = row["risk_review_on"] is DateTime r ? DateOnly.FromDateTime(r) : null;
            IdNumberForCase = (string?)row["tax_id"] ?? (string?)row["foreign_id"] ?? "";

            Today = Rates.TodayInTbilisi();
            Risk = await PartyRisk.ComputePartyRiskAsync(tx, partyId, Today);
            Kyc = await PartyKyc.LoadKycStatusAsync(tx, partyId, Today);
            ComplianceRole = Me.RoleCode is "admin" or "operations_head" or "accountant";
            ScreeningRows = ComplianceRole ? await SanctionsScreening.ScreeningsAsync(tx, partyId) : [];
            // Every user sees that operations are blocked; only the compliance roles see the details (art. 28).
            Suspension = await Aml.ActiveSuspensionAsync(tx, partyId);
            SuspensionRows = Aml.CanSeeAml(Me) ? await Aml.SuspensionsOfAsync(tx, partyId: partyId) : [];
            Assessments = (await tx.QueryAsync<AssessmentRow>("""
                select top (50) a.id as Id, a.assessed_on as AssessedOn, a.computed_level as ComputedLevel, a.level as Level, a.score as Score,
                       a.factors as Factors, a.is_override as IsOverride, a.reason as Reason, a.review_due_on as ReviewDueOn, u.full_name as UserName
                from core.party_risk_assessment a left join core.app_user u on u.id = a.assessed_by
                where a.party_id = @partyId order by a.created_at desc, a.id desc
                """, new { partyId })).ToList();
            ReviewState = ReviewOn is not { } on ? null : on < Today ? "overdue" : on <= Today.AddDays(30) ? "soon" : null;
            History = (await tx.QueryAsync<HistoryRow>("""
                select top (100) a.id as Id, format(switchoffset(a.at, '+04:00'), 'dd.MM.yyyy HH:mm') as At, a.action as Action,
                       u.full_name as UserName, a.old_row as OldRow, a.new_row as NewRow
                from core.audit_log a left join core.app_user u on u.id = a.user_id
                where a.table_name = N'party' and a.row_id = @rowId
                order by a.at desc, a.id desc
                """, new { rowId = partyId.ToString() })).ToList();
            return true;
        });
    }

    public async Task<IActionResult> OnGetAsync(string id) => await LoadAsync(id) ? Page() : NotFound();

    private async Task<IActionResult> ShowAsync(string id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostSaveAsync(string id)
    {
        var values = TrimmedForm();
        var outcome = await PartyForm.SaveAsync(Db, Me, values, Posted);
        if (outcome.SavedId is { } saved) return Redirect($"/parties/{saved}?saved=1");
        if (!await LoadAsync(id)) return NotFound();
        PartyFormState = new PartyFormView(values.GetValueOrDefault("kind") == "organization" ? "organization" : "person", values, outcome.Errors, outcome.Message, !Me.CanWrite);
        return Page();
    }

    private static readonly string[] OwnerTextFields =
    [
        "first_name", "last_name", "birth_date", "personal_number", "document_number", "document_issued_on", "document_country",
        "document_issuer", "document_valid_to", "citizenship", "birth_place", "criterion", "share_percent", "identified_at",
        "verified_at", "verification_source", "sanctions_checked_at", "note",
    ];

    /// <summary>Records a beneficial owner of an organization (order №55 art. 7 p. 3 fields).</summary>
    public async Task<IActionResult> OnPostOwnerAsync(string id)
    {
        var values = TrimmedForm();
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;
        if (!Me.CanWrite)
        {
            OwnerValues = values;
            OwnerMessage = Ka.Common.ReadOnly;
            return await ShowAsync(id);
        }
        if (PermissionPanel.Uuid(values.GetValueOrDefault("party_id")) is not { } partyId)
        {
            OwnerValues = values;
            return await ShowAsync(id);
        }
        var t = Ka.Party.Owners.Criteria;
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (V("first_name") is null) errors["first_name"] = Ka.Party.Errors.FirstName;
        if (V("last_name") is null) errors["last_name"] = Ka.Party.Errors.LastName;
        var criterion = V("criterion") is { } c && t.ContainsKey(c) ? c : "share";
        double? share = V("share_percent") is { } s ? JsNumber.Parse(s) : null;
        if (share is { } sh && !(sh >= 0 && sh <= 100)) errors["share_percent"] = Ka.Party.Owners.ShareError;
        if (criterion == "share" && (share is null || share < 25)) errors["share_percent"] = Ka.Party.Owners.ShareBelowThreshold;
        PartyForm.CheckDates(values, PartyForm.OwnerDateFields, errors);
        if (errors.Count > 0)
        {
            OwnerValues = values;
            OwnerErrors = errors;
            return await ShowAsync(id);
        }

        var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["party_id"] = partyId, ["created_by"] = Me.Id };
        foreach (var field in OwnerTextFields) row[field] = V(field);
        foreach (var field in PartyForm.OwnerDateFields)
            if (row[field] is string d) row[field] = Dates.ParseIsoDate(d);
        row["criterion"] = criterion;
        row["share_percent"] = share is { } value ? decimal.Parse(Fmt.Fixed((decimal)value, 2), CultureInfo.InvariantCulture) : null;
        foreach (var field in new[] { "document_country", "citizenship" })
            row[field] = row[field] is string code ? FirstTwo(JsText.ToUpperCase(code)) : null;
        row["is_direct"] = !Posted("is_indirect");
        row["is_nominee"] = Posted("is_nominee");
        row["is_pep"] = Posted("is_pep");
        await Db.AsUserAsync(Me.Id, async tx =>
        {
            var p = new DynamicParameters();
            p.Add("__id", Guid.NewGuid());
            foreach (var (k, v) in row) p.Add(k, v);
            await tx.ExecuteAsync($"insert into core.party_beneficial_owner (id, {string.Join(", ", row.Keys)}) values (@__id, {string.Join(", ", row.Keys.Select(k => "@" + k))})", p);
            return 0;
        });
        // The legacy owner form was keyed by the number of owners, so after an addition it came back empty, without its message.
        return Redirect($"/parties/{partyId}");
    }

    private static string FirstTwo(string text) => text.Length > 2 ? text[..2] : text;

    /// <summary>Marks an owner verified today, or ends the record (the row is kept for the retention period).</summary>
    public async Task<IActionResult> OnPostOwnerUpdateAsync(string id)
    {
        if (!Me.CanWrite) return await ShowAsync(id);
        if (PermissionPanel.Uuid(Form("id")) is not { } ownerId || PermissionPanel.Uuid(Form("party_id")) is not { } partyId) return await ShowAsync(id);
        var action = Form("action") ?? "";
        var today = Rates.TodayInTbilisi();
        var source = JsText.Trim(Form("verification_source") ?? "");
        await Db.AsUserAsync(Me.Id, async tx =>
        {
            if (action == "verify")
                await tx.ExecuteAsync("""
                    update core.party_beneficial_owner set verified_at = @today, identified_at = coalesce(identified_at, @today),
                           verification_source = coalesce(nullif(@source, N''), verification_source)
                    where id = @ownerId and valid_to is null
                    """, new { today, source, ownerId });
            else if (action == "remove")
                await tx.ExecuteAsync("update core.party_beneficial_owner set valid_to = @today where id = @ownerId and valid_to is null", new { today, ownerId });
            return 0;
        });
        return Redirect($"/parties/{partyId}");
    }

    /// <summary>Records a client risk assessment: the computed level, or another level with a reason.</summary>
    public async Task<IActionResult> OnPostRiskAsync(string id)
    {
        if (!Me.CanWrite)
        {
            RiskErrors = new Dictionary<string, string> { ["level"] = Ka.Common.ReadOnly };
            RiskMessage = Ka.Common.ReadOnly;
            return await ShowAsync(id);
        }
        if (PermissionPanel.Uuid(Form("party_id")) is not { } partyId) return await ShowAsync(id);
        var today = Rates.TodayInTbilisi();
        var computed = await Db.ReadTxAsync(tx => PartyRisk.ComputePartyRiskAsync(tx, partyId, today));
        if (computed is null) return await ShowAsync(id);

        PartyRisk.Override? manual = null;
        if (Form("mode") == "override")
        {
            var level = Form("level") ?? "";
            var reason = JsText.Trim(Form("reason") ?? "");
            string? refusal = null, field = null;
            if (!Ka.Party.Risk.ContainsKey(level)) (refusal, field) = (Ka.Party.RiskCard.ReasonRequired, "level");
            else if (level == computed.Assessment.Level.ToSnakeCode()) (refusal, field) = (Ka.Party.RiskCard.SameAsComputed, "level");
            else if (reason == "") (refusal, field) = (Ka.Party.RiskCard.ReasonRequired, "reason");
            if (refusal is not null)
            {
                RiskErrors = new Dictionary<string, string> { [field!] = refusal };
                RiskMessage = refusal;
                return await ShowAsync(id);
            }
            EnumCode.TryParseSnakeCode<RiskLevel>(level, out var chosen);
            manual = new PartyRisk.Override(chosen, reason);
        }
        await Db.AsUserAsync(Me.Id, async tx =>
        {
            await PartyRisk.RecordPartyRiskAsync(tx, partyId, Me.Id, today, computed.Assessment, manual);
            return 0;
        });
        // The legacy risk form was keyed by the latest assessment, so after a save it came back fresh, without its message.
        return Redirect($"/parties/{partyId}");
    }

    public async Task<IActionResult> OnPostScreenAsync(string id) =>
        await ComplianceActions.ScreenOnePartyAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync(id);

    public async Task<IActionResult> OnPostDecideScreeningAsync(string id) =>
        await ComplianceActions.DecideScreeningAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync(id);

    public async Task<IActionResult> OnPostCreateSuspensionAsync(string id) =>
        await ComplianceActions.CreateSuspensionAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync(id);

    public async Task<IActionResult> OnPostUpdateSuspensionAsync(string id) =>
        await ComplianceActions.UpdateSuspensionAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync(id);

    // Columns derived by the database or not meaningful to a reader of the history.
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { "id", "kind", "created_at", "updated_at", "display_name", "segment" };

    private static string Camel(string column) =>
        string.Concat(column.Split('_').Select((part, i) => i == 0 || part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    private static string Show(string key, JsonNode? value)
    {
        if (value is null) return "—";
        if (value is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.String:
                    var s = v.GetValue<string>();
                    if (s == "") return "—";
                    return key == "risk_level" ? Ka.Party.Risk.GetValueOrDefault(s) ?? s : s;
                case JsonValueKind.True: return Ka.Common.Yes;
                case JsonValueKind.False: return Ka.Common.No;
                case JsonValueKind.Number: return double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            }
        }
        return value.ToJsonString();
    }

    /// <summary>
    /// What an update of the party changed, field by field, in the order PostgreSQL's jsonb kept the keys (shorter keys
    /// first, then by their bytes): the old row's keys, then any new ones.
    /// </summary>
    public static IReadOnlyList<Change> Changes(HistoryRow row)
    {
        var before = row.OldRow is null ? new JsonObject() : JsonNode.Parse(row.OldRow)!.AsObject();
        var after = row.NewRow is null ? new JsonObject() : JsonNode.Parse(row.NewRow)!.AsObject();
        static IEnumerable<string> Ordered(JsonObject o) => o.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal);
        static string Json(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
            : n?.ToJsonString() ?? "null";
        return Ordered(before).Concat(Ordered(after)).Distinct()
            .Where(key => !Hidden.Contains(key) && Json(before[key]) != Json(after[key]))
            .Select(key => new Change(Ka.Party.Fields.GetValueOrDefault(Camel(key)) ?? key, Show(key, before[key]), Show(key, after[key])))
            .ToList();
    }
}
