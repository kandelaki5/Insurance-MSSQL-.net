using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using ExcelImport = Insurance.Application.Excel.PolicyImport;

namespace Insurance.Application.Services;

/// <summary>
/// Corporate policy import (legacy src/app/(app)/policies/import/actions.ts previewImport and confirmImport, and the
/// template route): a file of vehicles for one client is checked without writing anything (dry run, review #22), and
/// the validated policies are then written under one new contract in a single transaction. Reading the workbook and
/// writing the template is src/lib/policy-import.ts (Insurance.Application.Excel.PolicyImport).
/// </summary>
public static class PolicyImport
{
    public const int MaxRows = 500;

    public sealed record PreviewRow(int Row, string Description, string Term, string Premium, IReadOnlyList<string> Errors);

    /// <summary>What a preview or a confirmation gives back (legacy ImportState).</summary>
    public sealed record ImportState(IReadOnlyDictionary<string, string> Values, string? Message = null, string? Policyholder = null,
        IReadOnlyList<PreviewRow>? Rows = null, string? Payload = null, string? Redirect = null);

    // ---------------------------------------------------------------- the payload kept for the confirmation (legacy VehiclePolicyInput JSON)

    public sealed record ProductDto(string Id, string Code);

    public sealed record VehicleDto(string Make, string Model, int? Year, string? Plate, string? Vin,
        [property: JsonPropertyName("vehicle_type")] string? VehicleType, string? Steering, string? Usage, string Value);

    public sealed record LineDto(string CoverageId, string ClassCode, string? Limit, string? Deductible, long PremiumMinor);

    public sealed record InputDto(string PolicyholderId, string Segment, ProductDto Product, VehicleDto Vehicle, JsonElement From, JsonElement To, string Currency,
        IReadOnlyList<LineDto> Lines, JsonElement InstallmentCount, int? GraceDays, string? IntermediaryId, double CommissionPercent);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static InputDto ToDto(PolicyIssue.VehiclePolicyInput i) => new(
        Uuid.Text(i.PolicyholderId), i.Segment, new ProductDto(Uuid.Text(i.Product.Id), i.Product.Code),
        new VehicleDto(i.Vehicle.Make, i.Vehicle.Model, i.Vehicle.Year, i.Vehicle.Plate, i.Vehicle.Vin, i.Vehicle.VehicleType, i.Vehicle.Steering, i.Vehicle.Usage, i.Vehicle.Value),
        JsonSerializer.SerializeToElement(Dates.ToIso(i.From)), JsonSerializer.SerializeToElement(Dates.ToIso(i.To)), i.Currency.ToString(),
        i.Lines.Select(l => new LineDto(Uuid.Text(l.CoverageId), l.ClassCode, l.Limit, l.Deductible, l.PremiumMinor)).ToList(),
        JsonSerializer.SerializeToElement(i.InstallmentCount), i.GraceDays, i.IntermediaryId is { } m ? Uuid.Text(m) : null, i.CommissionPercent);

    private static PolicyIssue.VehiclePolicyInput FromDto(InputDto d, int plan, DateOnly from, DateOnly to) => new(
        Guid.Parse(d.PolicyholderId), d.Segment, (Guid.Parse(d.Product.Id), d.Product.Code),
        new PolicyIssue.Vehicle(d.Vehicle.Make, d.Vehicle.Model, d.Vehicle.Year, d.Vehicle.Plate, d.Vehicle.Vin, d.Vehicle.VehicleType, d.Vehicle.Steering, d.Vehicle.Usage, d.Vehicle.Value),
        from, to, Enum.Parse<Currency>(d.Currency, ignoreCase: false),
        d.Lines.Select(l => new PolicyIssue.Line(Guid.Parse(l.CoverageId), l.ClassCode, l.Limit, l.Deductible, l.PremiumMinor)).ToList(),
        plan, d.GraceDays, d.IntermediaryId is null ? null : Guid.Parse(d.IntermediaryId), d.CommissionPercent);

    // ---------------------------------------------------------------- preview

    private sealed record Context((Guid Id, string Code)? Product, IReadOnlyList<PolicyIssue.ProductCoverage> Coverages,
        (PolicyIssue.Policyholder Holder, string DisplayName)? Policyholder);

    private static async Task<Context> LoadContextAsync(Tx db, IReadOnlyDictionary<string, string> values)
    {
        var product = await db.QuerySingleOrDefaultAsync<(Guid Id, string Code)?>("select id, code from core.product where code = N'CASCO' and is_active = 1");
        var coverages = product is { } p ? await PolicyActions.CoveragesAsync(db, p.Id) : [];
        var holder = await PolicyActions.PolicyholderAsync(db, values.GetValueOrDefault("policyholder_id_number") ?? "");
        return new Context(product, coverages, holder);
    }

    /// <summary>
    /// The first open day: the day after the last closed month, null when no month is closed. A policy cannot start
    /// before it (owner, 13.10.2026; docs/05 a16): such an import row is an error in the preview and is not imported.
    /// </summary>
    private static Task<DateOnly?> FirstOpenDayAsync(Tx db) => PolicyCancel.FirstOpenDayAsync(db);

    /// <summary>Parses and validates the upload without writing anything (dry run, review #22).</summary>
    public static async Task<ImportState> PreviewAsync(Db db, CurrentUser user, IReadOnlyDictionary<string, string> values, byte[]? file)
    {
        if (!user.CanWrite) return new ImportState(values, Ka.Common.ReadOnly);
        var context = await db.ReadTxAsync(tx => LoadContextAsync(tx, values));
        if (context.Product is not { } product) return new ImportState(values, PolicyTexts.Import.Errors.NoProduct);
        if (context.Policyholder is not { } policyholder) return new ImportState(values, PolicyTexts.Errors.Policyholder);
        if (await db.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, policyholder.Holder.Id)) is not null) return new ImportState(values, AmlTexts.Suspension.Blocked);
        if (file is not { Length: > 0 }) return new ImportState(values, PolicyTexts.Import.Errors.NoFile);

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["effective_from"] = values.GetValueOrDefault("effective_from") ?? "",
            ["effective_to"] = values.GetValueOrDefault("effective_to") ?? "",
            ["currency"] = values.GetValueOrDefault("currency") ?? "GEL",
            ["installments"] = values.GetValueOrDefault("installments") ?? "1",
        };
        List<Excel.ImportRow> parsed;
        try
        {
            parsed = ExcelImport.ParseImport(file, context.Coverages.Select(c => new Excel.ProductCoverage(c.Code, c.IsMandatory)).ToList(), defaults);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ImportState(values, PolicyTexts.Import.Errors.Unreadable);
        }
        if (parsed.Count == 0) return new ImportState(values, PolicyTexts.Import.Errors.Empty);
        if (parsed.Count > MaxRows) return new ImportState(values, $"{PolicyTexts.Import.Errors.TooMany} ({MaxRows})");

        var rows = new List<PreviewRow>();
        var inputs = new List<PolicyIssue.VehiclePolicyInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var firstOpen = await db.ReadTxAsync(FirstOpenDayAsync);
        var today = db.Today();
        foreach (var r in parsed)
        {
            string V(string key) => r.Values.GetValueOrDefault(key) ?? "";
            bool Included(string code) => V($"cov_{code}_premium") != "";
            var (errors, input) = PolicyIssue.ValidateVehiclePolicy(r.Values, Included, context.Coverages, policyholder.Holder, product, today);
            var messages = errors.Values.ToList();
            if (input is not null && firstOpen is { } open && input.From < open) messages.Add(PolicyTexts.Import.Errors.PeriodClosed);
            var key = Insurance.Domain.Common.JsText.ToUpperCase(V("plate") != "" ? V("plate") : V("vin"));
            if (key != "" && seen.Contains(key)) messages.Add(PolicyTexts.Import.Errors.Duplicate);
            if (key != "") seen.Add(key);
            if (input is not null && messages.Count == 0) inputs.Add(input);
            rows.Add(new PreviewRow(r.Row,
                string.Join(' ', new[] { V("make"), V("model"), V("plate") != "" ? V("plate") : V("vin") }.Where(s => s != "")),
                $"{V("effective_from")} – {V("effective_to")}",
                input is null ? "" : $"{(input.Lines.Sum(l => l.PremiumMinor) / 100.0).ToString("F2", CultureInfo.InvariantCulture)} {input.Currency}",
                messages));
        }
        var allValid = rows.All(r => r.Errors.Count == 0);
        return new ImportState(values, null, policyholder.DisplayName, rows, allValid ? JsonSerializer.Serialize(inputs.Select(ToDto), Json) : null);
    }

    // ---------------------------------------------------------------- confirm

    /// <summary>Writes every validated policy under one new contract, in a single transaction (legacy confirmImport).</summary>
    public static async Task<ImportState> ConfirmAsync(Db db, Rates rates, CurrentUser user, string? payload, ImportState previous)
    {
        if (!user.CanWrite) return previous with { Message = Ka.Common.ReadOnly };
        List<InputDto>? dtos;
        try
        {
            using var document = JsonDocument.Parse(payload ?? "");
            // Not a list of policies is "empty", as the legacy check after JSON.parse said.
            dtos = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.Deserialize<List<InputDto>>(Json) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return previous with { Message = PolicyTexts.Import.Errors.Unreadable };
        }
        if (dtos is null || dtos.Count == 0 || dtos.Count > MaxRows) return previous with { Message = PolicyTexts.Import.Errors.Empty };

        // The payload comes back from the browser, so its payment plan is checked again here, before anything is written
        // (review PR220-F1): only a standard plan (owner, 13.10.2026), and 11 only on a twelve-month term.
        var checkedInputs = new List<(InputDto Dto, int Plan, DateOnly From, DateOnly To)>();
        foreach (var d in dtos)
        {
            if (d is null) return previous with { Message = PolicyTexts.Errors.Installments };
            var plan = d.InstallmentCount.ValueKind == JsonValueKind.Number && d.InstallmentCount.TryGetDouble(out var n) && n == Math.Floor(n) && n is >= 0 and <= 100 ? (int)n : -1;
            if (!Plan.IsPaymentPlan(plan)) return previous with { Message = PolicyTexts.Errors.Installments };
            if (!(Iso(d.From) is { } from && Iso(d.To) is { } to)) return previous with { Message = PolicyTexts.Import.Errors.Unreadable };
            if (!Plan.FitsPlan(plan, from, to)) return previous with { Message = PolicyTexts.Errors.InstallmentsEleven };
            checkedInputs.Add((d, plan, from, to));
        }
        // The start is checked again too, against the months closed by now (review PR220-F3).
        var firstOpen = await db.ReadTxAsync(FirstOpenDayAsync);
        if (firstOpen is { } open && checkedInputs.Any(i => i.From < open)) return previous with { Message = PolicyTexts.Import.Errors.PeriodClosed };

        List<PolicyIssue.VehiclePolicyInput> inputs;
        try
        {
            inputs = checkedInputs.Select(i => FromDto(i.Dto, i.Plan, i.From, i.To)).ToList();
        }
        catch (Exception e) when (e is FormatException or ArgumentException or NullReferenceException)
        {
            return previous with { Message = PolicyTexts.Import.Errors.Unreadable };
        }
        foreach (var holder in inputs.Select(i => i.PolicyholderId).Distinct())
            if (await db.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, holder)) is not null) return previous with { Message = AmlTexts.Suspension.Blocked };

        // Every policy keeps its own rate at its own effective date.
        var fixedRates = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var key = $"{input.Currency}:{Dates.ToIso(input.From)}";
            if (fixedRates.ContainsKey(key)) continue;
            if (await rates.RateOnAsync(input.Currency.ToString(), input.From) is not { } rate) return previous with { Message = $"{PolicyTexts.Errors.Rate}: {Dates.ToIso(input.From)}" };
            fixedRates[key] = rate;
        }

        var today = db.Today();
        string contractNumber;
        try
        {
            contractNumber = await db.AsUserAsync(user.Id, async tx =>
            {
                var number = await tx.NextNumberAsync("contract") ?? throw new InvalidOperationException("number series contract does not exist");
                var contractId = Guid.NewGuid();
                await tx.ExecuteAsync("insert into core.contract (id, number, signed_on, policyholder_id, created_by) values (@contractId, @number, @today, @holder, @userId)",
                    new { contractId, number, today, holder = inputs[0].PolicyholderId, userId = user.Id });
                foreach (var input in inputs)
                    await PolicyIssue.CreateVehiclePolicyAsync(tx, input, contractId, fixedRates[$"{input.Currency}:{Dates.ToIso(input.From)}"], user.Id, today);
                return number;
            });
        }
        catch (Exception e) when (PolicyActions.SeriesExhausted(e))
        {
            return previous with { Message = Ka.Common.SeriesExhausted };
        }
        return previous with { Message = null, Redirect = $"/policies?q={Uri.EscapeDataString(contractNumber)}" };
    }

    private static DateOnly? Iso(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } s && Dates.IsIsoDate(s) ? Dates.ParseIsoDate(s) : null;

    // ---------------------------------------------------------------- template

    /// <summary>The upload template for the motor product (legacy import/template/route.ts).</summary>
    public static async Task<byte[]> TemplateAsync(Db db)
    {
        var coverages = await db.ReadTxAsync(tx => tx.QueryAsync<(string Code, bool IsMandatory)>("""
            select pc.code, pc.is_mandatory
            from core.product_coverage pc join core.product p on p.id = pc.product_id
            where p.code = N'CASCO' order by pc.sort_order
            """));
        return ExcelImport.BuildTemplate(coverages.Select(c => new Excel.ProductCoverage(c.Code, c.IsMandatory)).ToList());
    }
}
