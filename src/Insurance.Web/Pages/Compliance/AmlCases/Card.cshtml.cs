using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Insurance.Web.Pages.Compliance;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Compliance.AmlCases;

/// <summary>One AML case (legacy compliance/aml-cases/[id]/page.tsx, decision-form.tsx; actions decideCase, createSuspension, updateSuspension).</summary>
public sealed class CardModel : AppPageModel
{
    public sealed record AmlCase(
        Guid Id, string Number, Guid PartyId, string? PartyName, string? PartyIdNumber, DateOnly OpenedOn, string Source, string Description,
        string? TransactionRef, string? ClassCode, string Status, DateOnly? ExaminedOn, string? Examination, string? SuspicionAt, string? StrSentAt,
        string? SuspicionLocal, string? StrSentLocal, string? StrType, string? StrOtherBasis, string? FmsReference, DateOnly? ClosedOn, string? Note,
        bool Late, string? CreatedByName);

    public AmlCase Case { get; private set; } = null!;
    public IReadOnlyList<Aml.SuspensionRow> Suspensions { get; private set; } = [];
    public bool Registered { get; private set; }
    public bool Writable => Aml.IsComplianceRole(Me);

    /// <summary>The decision form's values: the case as stored, or what was posted after a decision (legacy useActionState).</summary>
    public IReadOnlyDictionary<string, string> Decision { get; private set; } = new Dictionary<string, string>();

    public SuspensionSectionView Section => new(AmlTexts.Suspension.Title, Case.PartyId, Case.Id, Suspensions, Writable, $"/compliance/aml-cases/{Case.Id}", false, Query("suspension") == "target");

    private static string Local(string column) => $"format(switchoffset({column}, '+04:00'), 'yyyy-MM-ddTHH:mm')";

    private async Task<bool> LoadAsync(string id)
    {
        if (PermissionPanel.Uuid(id) is not { } key) return false;
        var c = await Db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<AmlCase>($"""
            select c.id as Id, c.number as Number, c.party_id as PartyId, pa.display_name as PartyName, coalesce(pa.tax_id, pa.foreign_id) as PartyIdNumber,
                   c.opened_on as OpenedOn, c.source as Source, c.description as Description, c.transaction_ref as TransactionRef, c.class_code as ClassCode,
                   c.status as Status, c.examined_on as ExaminedOn, c.examination as Examination,
                   format(switchoffset(c.suspicion_at, '+04:00'), 'dd.MM.yyyy HH:mm') as SuspicionAt,
                   format(switchoffset(c.str_sent_at, '+04:00'), 'dd.MM.yyyy HH:mm') as StrSentAt,
                   {Local("c.suspicion_at")} as SuspicionLocal, {Local("c.str_sent_at")} as StrSentLocal,
                   c.str_type as StrType, c.str_other_basis as StrOtherBasis, c.fms_reference as FmsReference, c.closed_on as ClosedOn, c.note as Note,
                   convert(bit, case when c.str_sent_at is not null
                        and convert(date, switchoffset(c.str_sent_at, '+04:00')) > convert(date, switchoffset(c.suspicion_at, '+04:00')) then 1 else 0 end) as Late,
                   u.full_name as CreatedByName
            from core.aml_case c join core.party pa on pa.id = c.party_id left join core.app_user u on u.id = c.created_by
            where c.id = @key
            """, new { key }));
        if (c is null) return false;
        Case = c;
        Suspensions = await Db.ReadTxAsync(tx => Aml.SuspensionsOfAsync(tx, caseId: key));
        Registered = !string.IsNullOrEmpty(Query("registered"));
        Decision = new Dictionary<string, string>
        {
            ["id"] = c.Id.ToString(), ["status"] = c.Status, ["examined_on"] = c.ExaminedOn is { } e ? Fmt.Iso(e) : "", ["examination"] = c.Examination ?? "",
            ["suspicion_at"] = c.SuspicionLocal ?? "", ["str_sent_at"] = c.StrSentLocal ?? "", ["str_type"] = c.StrType ?? "",
            ["str_other_basis"] = c.StrOtherBasis ?? "", ["fms_reference"] = c.FmsReference ?? "", ["note"] = c.Note ?? "",
        };
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!Aml.CanSeeAml(Me)) return Redirect("/");
        return await LoadAsync(id) ? Page() : NotFound();
    }

    private async Task<IActionResult> ShowAsync(string id, IReadOnlyDictionary<string, string>? posted = null)
    {
        if (!Aml.CanSeeAml(Me)) return Redirect("/");
        if (!await LoadAsync(id)) return NotFound();
        if (posted is not null) Decision = new Dictionary<string, string>(posted) { ["id"] = Case.Id.ToString(), ["status"] = Case.Status };
        return Page();
    }

    /// <summary>Records the examination and its outcome: no report, a report sent to the FMS, or closure.</summary>
    public async Task<IActionResult> OnPostDecideAsync(string id)
    {
        var values = TrimmedForm();
        if (!Aml.IsComplianceRole(Me))
        {
            FormError = Ka.Common.ReadOnly;
            return await ShowAsync(id, values);
        }
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;
        var caseText = values.GetValueOrDefault("id") ?? "";
        var decision = values.GetValueOrDefault("decision") ?? "";
        if (PermissionPanel.Uuid(caseText) is not { } caseId) return await ShowAsync(id, values);
        var t = AmlTexts.Cases.Errors.ByKey;
        var today = Rates.TodayInTbilisi();
        if (decision is "examine" or "report" && V("examination") is null) Errors["examination"] = t["examination"];
        var suspicionAt = Aml.TbilisiInstant(values.GetValueOrDefault("suspicion_at"));
        var strSentAt = Aml.TbilisiInstant(values.GetValueOrDefault("str_sent_at"));
        var strType = V("str_type") is { } st && AmlTexts.Cases.StrTypes.ContainsKey(st) ? st : null;
        if (decision == "report")
        {
            if (suspicionAt is null || strSentAt is null || strType is null) Errors["str_sent_at"] = t["str"];
            if (strType == "other" && V("str_other_basis") is null) Errors["str_other_basis"] = t["otherBasis"];
        }
        if (Errors.Count > 0) return await ShowAsync(id, values);

        var examinedOn = V("examined_on") is { } eo && Dates.IsIsoDate(eo) ? Dates.ParseIsoDate(eo) : today;
        await Db.AsUserAsync(Me.Id, async tx =>
        {
            if (decision == "examine")
                await tx.ExecuteAsync("""
                    update core.aml_case set status = N'examined', examined_on = @examinedOn, examination = @examination, note = @note
                    where id = @caseId and status in (N'open', N'examined')
                    """, new { examinedOn, examination = V("examination"), note = V("note"), caseId });
            else if (decision == "report")
                await tx.ExecuteAsync("""
                    update core.aml_case set status = N'reported', examined_on = @examinedOn, examination = @examination, suspicion_at = @suspicionAt,
                           str_sent_at = @strSentAt, str_type = @strType, str_other_basis = @otherBasis, fms_reference = @fmsReference, note = @note
                    where id = @caseId and status in (N'open', N'examined', N'reported')
                    """, new { examinedOn, examination = V("examination"), suspicionAt, strSentAt, strType, otherBasis = V("str_other_basis"), fmsReference = V("fms_reference"), note = V("note"), caseId });
            else if (decision == "close")
                await tx.ExecuteAsync("""
                    update core.aml_case set status = N'closed', closed_on = @today, note = coalesce(nullif(@note, N''), note)
                    where id = @caseId and status in (N'examined', N'reported')
                    """, new { today, note = values.GetValueOrDefault("note") ?? "", caseId });
            return 0;
        });
        Notice = AmlTexts.Cases.Saved;
        return await ShowAsync(id, values);
    }

    public async Task<IActionResult> OnPostCreateSuspensionAsync(string id) =>
        await ComplianceActions.CreateSuspensionAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync(id);

    public async Task<IActionResult> OnPostUpdateSuspensionAsync(string id) =>
        await ComplianceActions.UpdateSuspensionAsync(Db, Me, Form) is { } to ? Redirect(to) : await ShowAsync(id);
}
