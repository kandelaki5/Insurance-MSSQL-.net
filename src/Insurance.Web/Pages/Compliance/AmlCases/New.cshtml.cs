using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Compliance.AmlCases;

/// <summary>Registering an unusual transaction (legacy compliance/aml-cases/new/page.tsx, case-form.tsx; action registerCase).</summary>
public sealed class NewModel : AppPageModel
{
    public sealed record ClassOption(string Code, string Name);

    public IReadOnlyList<ClassOption> Classes { get; private set; } = [];

    private async Task LoadAsync() =>
        Classes = (await Db.ReadTxAsync(tx => tx.QueryAsync<ClassOption>(
            "select code as Code, name_ka as Name from core.insurance_class where parent_code is null order by sort_order"))).ToList();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Aml.IsComplianceRole(Me)) return Redirect("/compliance/aml-cases");
        await LoadAsync();
        Values["opened_on"] = Fmt.Iso(Rates.TodayInTbilisi());
        Values["source"] = "monitoring";
        Values["party_id_number"] = Query("party") ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var values = TrimmedForm();
        foreach (var (k, v) in values) Values[k] = v;
        if (!Aml.IsComplianceRole(Me))
        {
            // The legacy action answered without the page's own check; the page itself sends such a user away.
            FormError = Ka.Common.ReadOnly;
            return await ShowAsync();
        }
        string? V(string key) => values.TryGetValue(key, out var v) && v != "" ? v : null;
        var t = AmlTexts.Cases.Errors.ByKey;
        var number = V("party_id_number");
        var partyId = number is null ? null : await Db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>(
            "select top (1) id from core.party where tax_id = @number or foreign_id = @number", new { number }));
        if (partyId is null) Errors["party_id_number"] = t["party"];
        var openedOn = values.GetValueOrDefault("opened_on") ?? "";
        if (!Dates.IsIsoDate(openedOn) || string.CompareOrdinal(openedOn, Fmt.Iso(Rates.TodayInTbilisi())) > 0) Errors["opened_on"] = t["openedOn"];
        if (V("description") is null) Errors["description"] = t["description"];
        var source = V("source") is { } s && AmlTexts.Cases.Sources.ContainsKey(s) ? s : "monitoring";
        if (Errors.Count > 0) return await ShowAsync();

        var id = Guid.NewGuid();
        try
        {
            await Db.AsUserAsync(Me.Id, async tx =>
            {
                var caseNumber = await tx.NextNumberAsync("aml_case");
                await tx.ExecuteAsync("""
                    insert into core.aml_case (id, number, party_id, opened_on, source, description, transaction_ref, class_code, created_by)
                    values (@id, @caseNumber, @partyId, @openedOn, @source, @description, @transactionRef, @classCode, @userId)
                    """, new
                {
                    id, caseNumber, partyId, openedOn = Dates.ParseIsoDate(openedOn), source, description = V("description"),
                    transactionRef = V("transaction_ref"), classCode = V("class_code"), userId = Me.Id,
                });
                return 0;
            });
        }
        catch (Exception ex) when (DbError.From(ex)?.Code == Insurance.Domain.Common.Numbering.SeriesExhaustedCode)
        {
            FormError = Ka.Common.SeriesExhausted;
            return await ShowAsync();
        }
        return Redirect($"/compliance/aml-cases/{id}?registered=1");
    }

    private async Task<IActionResult> ShowAsync()
    {
        if (!Aml.IsComplianceRole(Me)) return Redirect("/compliance/aml-cases");
        await LoadAsync();
        return Page();
    }
}
