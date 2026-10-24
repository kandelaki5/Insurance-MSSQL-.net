using System.Globalization;
using System.Text.Json;
using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>Validating and writing a vehicle policy from the form or an import row (legacy src/lib/policy-issue.ts).</summary>
public static class PolicyIssue
{
    public sealed record ProductCoverage(Guid Id, string Code, string ClassCode, bool IsMandatory);

    public sealed record Vehicle(string Make, string Model, int? Year, string? Plate, string? Vin, string? VehicleType, string? Steering, string? Usage, string Value);

    public sealed record Line(Guid CoverageId, string ClassCode, string? Limit, string? Deductible, long PremiumMinor);

    public sealed record VehiclePolicyInput(Guid PolicyholderId, string Segment, (Guid Id, string Code) Product, Vehicle Vehicle, DateOnly From, DateOnly To,
        Currency Currency, IReadOnlyList<Line> Lines, int InstallmentCount, int? GraceDays, Guid? IntermediaryId, double CommissionPercent);

    public sealed record Policyholder(Guid Id, string Segment, string? RiskLevel, bool SanctionsBlocked = false);

    private static readonly string[] Currencies = ["GEL", "USD", "EUR"];

    /// <summary>JavaScript's Number(value.replace(',', '.')) for a filled field, NaN for an empty or missing one.</summary>
    private static double Amount(string? value) => string.IsNullOrEmpty(value) ? double.NaN : JsNumber.Parse(ReplaceFirst(value, ',', '.'));

    private static string ReplaceFirst(string s, char from, char to)
    {
        var i = s.IndexOf(from, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), to.ToString(), s.AsSpan(i + 1));
    }

    /// <summary>JavaScript's x.toFixed(2).</summary>
    private static string Fixed2(double x) => x.ToString("F2", CultureInfo.InvariantCulture);

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Validates the fields of one vehicle policy (from the form or an import row) and shapes them for
    /// <see cref="CreateVehiclePolicyAsync"/>. <paramref name="included"/> says whether a coverage was selected.
    /// </summary>
    public static (Dictionary<string, string> Errors, VehiclePolicyInput? Input) ValidateVehiclePolicy(
        IReadOnlyDictionary<string, string> values, Func<string, bool> included, IReadOnlyList<ProductCoverage> coverages,
        Policyholder? policyholder, (Guid Id, string Code) product, DateOnly today)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (policyholder is null) errors["policyholder_id_number"] = PolicyTexts.Errors.Policyholder;
        // No business with a sanctioned client (AML law art. 10 p. 7); a risk level is mandatory before a policy is
        // issued (art. 8; order №02 art. 3 "კ").
        else if (policyholder.SanctionsBlocked) errors["policyholder_id_number"] = PolicyTexts.Errors.PolicyholderSanctioned;
        else if (policyholder.RiskLevel is null) errors["policyholder_id_number"] = PolicyTexts.Errors.PolicyholderRisk;
        if (string.IsNullOrEmpty(Get(values, "make"))) errors["make"] = PolicyTexts.Errors.Make;
        if (string.IsNullOrEmpty(Get(values, "model"))) errors["model"] = PolicyTexts.Errors.Model;
        if (string.IsNullOrEmpty(Get(values, "plate")) && string.IsNullOrEmpty(Get(values, "vin"))) errors["plate"] = PolicyTexts.Errors.VehicleId;
        if (!string.IsNullOrEmpty(Get(values, "vin")) && Get(values, "vin")!.Length != 17) errors["vin"] = PolicyTexts.Errors.Vin;
        var year = JsNumber.Parse(Get(values, "year") ?? "");
        if (!string.IsNullOrEmpty(Get(values, "year")) && !(year >= 1950 && year <= today.Year + 1)) errors["year"] = PolicyTexts.Errors.Year;
        var vehicleValue = Amount(Get(values, "value"));
        if (!(vehicleValue > 0)) errors["value"] = PolicyTexts.Errors.Value;

        var fromText = Get(values, "effective_from") ?? "";
        var toText = Get(values, "effective_to") ?? "";
        var datesOk = Dates.IsIsoDate(fromText) && Dates.IsIsoDate(toText) && Dates.DaysBetween(Dates.ParseIsoDate(fromText), Dates.ParseIsoDate(toText)) > 0;
        if (!datesOk) errors["effective_to"] = PolicyTexts.Errors.Dates;
        var currency = Enum.Parse<Currency>(Currencies.Contains(Get(values, "currency") ?? "") ? Get(values, "currency")! : "GEL");

        var lines = coverages
            .Where(c => c.IsMandatory || included(c.Code))
            .Select(c =>
            {
                var premium = Amount(Get(values, $"cov_{c.Code}_premium"));
                // Validated after rounding to tetri, so 0.001 cannot become a zero-premium line.
                var premiumMinor = double.IsFinite(premium) ? (long)Math.Floor(premium * 100 + 0.5) : 0;
                if (premiumMinor <= 0) errors[$"cov_{c.Code}_premium"] = PolicyTexts.Errors.Premium;
                var limit = Amount(Get(values, $"cov_{c.Code}_limit"));
                var deductible = Amount(Get(values, $"cov_{c.Code}_deductible"));
                return new Line(c.Id, c.ClassCode, limit > 0 ? Fixed2(limit) : null, deductible >= 0 ? Fixed2(deductible) : null, premiumMinor);
            })
            .ToList();
        if (lines.Count == 0) errors["coverages"] = PolicyTexts.Errors.Coverages;

        var commissionPercent = string.IsNullOrEmpty(Get(values, "commission_percent")) ? 0 : JsNumber.Parse(Get(values, "commission_percent")!);
        if (!string.IsNullOrEmpty(Get(values, "intermediary_id")) && !(commissionPercent >= 0 && commissionPercent <= 100)) errors["commission_percent"] = PolicyTexts.Errors.Commission;
        // A standard plan (owner, 13.10.2026); anything else — two payments too — is refused, never quietly made one payment.
        var planNumber = string.IsNullOrEmpty(Get(values, "installments")) ? 1 : JsNumber.Parse(Get(values, "installments")!);
        var isPlan = planNumber == Math.Floor(planNumber) && double.IsFinite(planNumber) && Plan.IsPaymentPlan((int)planNumber);
        if (!isPlan) errors["installments"] = PolicyTexts.Errors.Installments;
        else if (Dates.IsIsoDate(fromText) && Dates.IsIsoDate(toText) && !Plan.FitsPlan((int)planNumber, Dates.ParseIsoDate(fromText), Dates.ParseIsoDate(toText)))
            errors["installments"] = PolicyTexts.Errors.InstallmentsEleven;
        var installmentCount = isPlan ? (int)planNumber : 1;
        int? graceDays = null;
        if (!string.IsNullOrEmpty(Get(values, "cancel_grace_days")))
        {
            var g = JsNumber.Parse(Get(values, "cancel_grace_days")!);
            // The legacy code wrote NaN into the integer column (a database error); a non-number stays an error.
            if (!double.IsFinite(g)) throw new FormatException($"cancel_grace_days is not a number: {Get(values, "cancel_grace_days")}");
            graceDays = (int)Math.Max(0, Math.Truncate(g));
        }

        if (errors.Count > 0 || policyholder is null) return (errors, null);
        return (errors, new VehiclePolicyInput(
            policyholder.Id, policyholder.Segment, product,
            new Vehicle(Get(values, "make")!, Get(values, "model")!, string.IsNullOrEmpty(Get(values, "year")) ? null : (int)year,
                NullIfEmpty(Get(values, "plate")), NullIfEmpty(Get(values, "vin")), NullIfEmpty(Get(values, "vehicle_type")),
                NullIfEmpty(Get(values, "steering")), NullIfEmpty(Get(values, "usage")), Fixed2(vehicleValue)),
            Dates.ParseIsoDate(fromText), Dates.ParseIsoDate(toText), currency, lines, installmentCount, graceDays,
            Guid.TryParse(Get(values, "intermediary_id"), out var intermediary) ? intermediary : null, commissionPercent));
    }

    private static string? NullIfEmpty(string? v) => string.IsNullOrEmpty(v) ? null : v;

    /// <summary>
    /// Writes one policy awaiting approval under an existing contract: the policy, its vehicle, its lines (premium fixed
    /// in GEL at the effective-date rate, order №01/2013 art. 5) and the instalment schedule. The premium ledger is
    /// written later, on approval. Returns the policy id; null when the product has no number series.
    /// </summary>
    public static async Task<Guid> CreateVehiclePolicyAsync(Tx tx, VehiclePolicyInput input, Guid contractId, decimal rate, Guid userId, DateOnly today)
    {
        var number = await tx.NextNumberAsync("policy:" + input.Product.Code)
            ?? throw new InvalidOperationException($"number series policy:{input.Product.Code} does not exist");
        var policyId = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency,
                                     premium_rate, product_id, cancel_grace_days, segment, created_by)
            values (@policyId, @contractId, @number, @today, @from, @to, N'pending_approval', @currency,
                    @rate, @productId, @graceDays, @segment, @userId)
            """, new
        {
            policyId, contractId, number, today, from = input.From, to = input.To, currency = input.Currency.ToString(),
            rate = Math.Round(rate, 6, MidpointRounding.AwayFromZero), productId = input.Product.Id, graceDays = input.GraceDays, segment = input.Segment, userId,
        });

        var v = input.Vehicle;
        var description = string.Join(' ', new[] { v.Make, v.Model, string.IsNullOrEmpty(v.Plate) ? v.Vin : v.Plate }.Where(s => !string.IsNullOrEmpty(s)));
        var attributes = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["make"] = v.Make, ["model"] = v.Model, ["year"] = v.Year, ["plate"] = v.Plate, ["vin"] = v.Vin, ["vehicle_type"] = v.VehicleType,
            ["steering"] = v.Steering, ["usage"] = v.Usage, ["value"] = v.Value, ["value_currency"] = input.Currency.ToString(),
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var objectId = Guid.NewGuid();
        await tx.ExecuteAsync("insert into core.insured_object (id, policy_id, object_type, description, attributes) values (@objectId, @policyId, N'vehicle', @description, @attributes)",
            new { objectId, policyId, description, attributes });

        foreach (var line in input.Lines)
        {
            var gelMinor = Money.ToGel(new Money(line.PremiumMinor, input.Currency), input.From, (_, _) => rate);
            await tx.ExecuteAsync("""
                insert into core.policy_line (policy_id, insured_object_id, product_coverage_id, class_code, sum_insured,
                                              sum_insured_currency, deductible, premium, premium_gel)
                values (@policyId, @objectId, @coverageId, @classCode, @limit, @limitCurrency, @deductible, @premium, @premiumGel)
                """, new
            {
                policyId, objectId, coverageId = line.CoverageId, classCode = line.ClassCode,
                limit = line.Limit is null ? (decimal?)null : decimal.Parse(line.Limit, CultureInfo.InvariantCulture),
                limitCurrency = line.Limit is null ? null : input.Currency.ToString(),
                deductible = line.Deductible is null ? (decimal?)null : decimal.Parse(line.Deductible, CultureInfo.InvariantCulture),
                premium = Billing.FromTetri(line.PremiumMinor), premiumGel = Billing.FromTetri(gelMinor),
            });
        }

        // The plan's instalments (finance PlanInstallments): 1, 4, 12 equal on calendar months, 11 the first and the last
        // month in advance; the last instalment takes the rounding remainder.
        var totalMinor = input.Lines.Sum(l => l.PremiumMinor);
        foreach (var i in Plan.PlanInstallments(input.InstallmentCount, input.From, input.To, totalMinor))
            await tx.ExecuteAsync("insert into core.installment (policy_id, due_on, amount) values (@policyId, @dueOn, @amount)",
                new { policyId, dueOn = i.DueOn, amount = Billing.FromTetri(i.Minor) });

        if (input.IntermediaryId is { } intermediaryId)
            await tx.ExecuteAsync("insert into core.policy_intermediary (policy_id, intermediary_id, commission_percent) values (@policyId, @intermediaryId, @percent)",
                new { policyId, intermediaryId, percent = decimal.Parse(Fixed2(input.CommissionPercent), CultureInfo.InvariantCulture) });
        return policyId;
    }
}
