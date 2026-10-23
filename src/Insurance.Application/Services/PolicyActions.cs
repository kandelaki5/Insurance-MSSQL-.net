using System.Globalization;
using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Compliance;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.PaymentActions;

namespace Insurance.Application.Services;

/// <summary>
/// Issuing, approving, cancelling and changing a policy (legacy src/app/(app)/policies/actions.ts,
/// policies/[id]/cancel/actions.ts, policies/[id]/endorse/actions.ts, policies/[id]/reinsurance-actions.ts).
/// </summary>
public static class PolicyActions
{
    /// <summary>core.next_number refused: the number series has run out (NU001); nothing was written.</summary>
    public static bool SeriesExhausted(Exception e) => DbError.From(e)?.Code == Insurance.Domain.Common.Numbering.SeriesExhaustedCode;

    private sealed record HolderRow(Guid Id, string Segment, string? RiskLevel, bool SanctionsBlocked, string DisplayName);

    private static string? V(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var v) ? v : null;

    /// <summary>The client with this personal number or registration code (or foreign id), as the forms look it up.</summary>
    public static async Task<(PolicyIssue.Policyholder Holder, string DisplayName)?> PolicyholderAsync(Tx db, string idNumber)
    {
        var row = await db.QueryFirstOrDefaultAsync<HolderRow>("""
            select id as Id, segment as Segment, risk_level as RiskLevel, sanctions_blocked as SanctionsBlocked, display_name as DisplayName
            from core.party where tax_id = @idNumber or foreign_id = @idNumber
            """, new { idNumber });
        return row is null ? null : (new PolicyIssue.Policyholder(row.Id, row.Segment, row.RiskLevel, row.SanctionsBlocked), row.DisplayName);
    }

    public static async Task<IReadOnlyList<PolicyIssue.ProductCoverage>> CoveragesAsync(Tx db, Guid productId) =>
        (await db.QueryAsync<PolicyIssue.ProductCoverage>("""
            select id as Id, code as Code, class_code as ClassCode, is_mandatory as IsMandatory from core.product_coverage
            where product_id = @productId order by sort_order
            """, new { productId })).ToList();

    // ---------------------------------------------------------------- issue

    private sealed record IssueContext((Guid Id, string Code) Product, IReadOnlyList<PolicyIssue.ProductCoverage> Coverages, PolicyIssue.Policyholder? Holder);

    /// <summary>
    /// A new vehicle policy awaiting approval, under a new contract (legacy issuePolicy): validated as the form and the
    /// import validate it; the client may not be suspended (FMS); the premium is fixed in GEL at the effective-date rate.
    /// </summary>
    public static async Task<FormAnswer> IssuePolicyAsync(Db db, Rates rates, CurrentUser user, IReadOnlyDictionary<string, string> values, Func<string, bool> included)
    {
        if (!user.CanWrite) return FormAnswer.Refused(Ka.Common.ReadOnly);
        var context = await db.ReadTxAsync(async tx =>
        {
            // A product id that is not a uuid finds no product (legacy: the uuid cast failed, the form had a hidden field).
            if (!Uuid.TryParse(V(values, "product_id"), out var productId)) return null;
            var product = await tx.QuerySingleOrDefaultAsync<(Guid Id, string Code)?>("select id, code from core.product where id = @productId and is_active = 1", new { productId });
            if (product is not { } p) return null;
            var coverages = await CoveragesAsync(tx, p.Id);
            var holder = await PolicyholderAsync(tx, V(values, "policyholder_id_number") ?? "");
            return new IssueContext(p, coverages, holder?.Holder);
        });
        if (context is null) return FormAnswer.Refused(PolicyTexts.Errors.Coverages);
        var today = Rates.TodayInTbilisi();
        var (errors, input) = PolicyIssue.ValidateVehiclePolicy(values, included, context.Coverages, context.Holder, context.Product, today);
        if (context.Holder is { } holder && await db.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, holder.Id)) is not null)
            errors["policyholder_id_number"] = AmlTexts.Suspension.Blocked;
        decimal? rate = null;
        if (input is not null)
        {
            rate = await rates.RateOnAsync(input.Currency.ToString(), input.From);
            if (rate is null) errors["currency"] = PolicyTexts.Errors.Rate;
        }
        if (input is null || rate is not { } fixedRate || errors.Count > 0) return new FormAnswer(errors, null, null);

        Guid policyId;
        try
        {
            policyId = await db.AsUserAsync(user.Id, async tx =>
            {
                var number = await tx.NextNumberAsync("contract") ?? throw new InvalidOperationException("number series contract does not exist");
                var contractId = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.contract (id, number, signed_on, policyholder_id, created_by) values (@contractId, @number, @today, @holder, @userId)
                    """, new { contractId, number, today, holder = input.PolicyholderId, userId = user.Id });
                return await PolicyIssue.CreateVehiclePolicyAsync(tx, input, contractId, fixedRate, user.Id, today);
            });
        }
        catch (Exception e) when (SeriesExhausted(e))
        {
            return FormAnswer.Refused(Ka.Common.SeriesExhausted);
        }
        return FormAnswer.GoTo($"/policies/{Uuid.Text(policyId)}?issued=1");
    }

    // ---------------------------------------------------------------- approve

    /// <summary>What one press of "approve" got: the outcome and, when the policy exists, its number.</summary>
    public sealed record ApproveAnswer(string Outcome, string Number);

    private sealed record PendingPolicy(Guid Id, string Number, string Status, DateOnly EffectiveFrom, DateOnly EffectiveTo, Guid? ReplacesPolicyId, string PremiumCurrency, Guid? CreatedBy);

    /// <summary>
    /// Approval puts the policy in force: the premium ledger rows are written now, dated on the effective date or on the
    /// first day of the earliest open month when that date lies in a closed period. Who may approve is policy.approve,
    /// for the user of the session — a quick check, the binding one first in the transaction. No one but the admin
    /// approves a policy they created (docs/05 ვ4, ვ8). A change that reduces the premium lowers the original's schedule
    /// in the same transaction, in the company's chosen order (owner, 07.10.2026); a reduction larger than the whole
    /// schedule is refused. A treaty combination that would cede more than the premium rolls the approval back (H08).
    /// </summary>
    public static async Task<ApproveAnswer> ApprovePolicyAsync(Db db, CurrentUser user, string? idText)
    {
        static ApproveAnswer Answer(string outcome, string? number = null) => new(outcome, number ?? "");
        if (!Uuid.TryParse(idText, out var id)) return Answer("invalid");
        if (!await PermissionStore.HasPermissionAsync(db, user, "policy.approve")) return Answer("forbidden");

        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                if (!await PermissionStore.HasPermissionAsync(tx, user, "policy.approve")) return "forbidden";
                var policy = await tx.QuerySingleOrDefaultAsync<PendingPolicy>("""
                    select id as Id, number as Number, status as Status, effective_from as EffectiveFrom, effective_to as EffectiveTo,
                           replaces_policy_id as ReplacesPolicyId, premium_currency as PremiumCurrency, created_by as CreatedBy
                    from core.policy with (updlock, rowlock, holdlock) where id = @id
                    """, new { id });
                if (policy is null || policy.Status != "pending_approval") return "skipped";
                if (policy.CreatedBy == user.Id && user.RoleCode != "admin") return "own";
                var holder = await tx.QuerySingleOrDefaultAsync<(Guid PolicyholderId, string? ReplacesNumber)?>("""
                    select c.policyholder_id, o.number
                    from core.policy p join core.contract c on c.id = p.contract_id left join core.policy o on o.id = p.replaces_policy_id
                    where p.id = @id
                    """, new { id });
                if (holder is { } h && await Aml.SuspensionInTransactionAsync(tx, h.PolicyholderId, new OperationTargets(
                        PolicyIds: [Uuid.Text(policy.Id), policy.ReplacesPolicyId is { } r ? Uuid.Text(r) : null],
                        Numbers: [policy.Number, h.ReplacesNumber])) is not null)
                    return "suspended";

                var firstOpen = await PolicyCancel.FirstOpenDayAsync(tx);
                var bookedOn = firstOpen is { } last && last > policy.EffectiveFrom ? last : policy.EffectiveFrom;
                var lines = (await tx.QueryAsync<(Guid Id, decimal Premium, decimal PremiumGel)>(
                    "select id, premium, premium_gel from core.policy_line where policy_id = @id", new { id })).ToList();

                var currency = Billing.ParseCurrency(policy.PremiumCurrency);
                var totalMinor = lines.Sum(l => Billing.Tetri(l.Premium));
                if (policy.ReplacesPolicyId is { } originalId && totalMinor < 0)
                {
                    await tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @originalId", new { originalId });
                    var scheduled = Billing.ToInstallments(await Billing.LoadScheduleAsync(tx, originalId), currency).Sum(i => i.Amount.Minor);
                    if (scheduled + totalMinor < 0) return "reduction-exceeds";
                    var chosen = await tx.QuerySingleOrDefaultAsync<string>("select value from core.setting where [key] = N'policy.reduction_order'");
                    var choices = Ka.Settings.Choices["policy.reduction_order"].Keys.ToList();
                    var order = Codes.Parse<ReductionOrder>(chosen is not null && choices.Contains(chosen) ? chosen : choices[0]);
                    await Billing.ReduceScheduleByAsync(tx, originalId, currency, -totalMinor, order, bookedOn, $"{PolicyTexts.Endorse.ScheduleReason} {policy.Number}", user.Id);
                }

                foreach (var line in lines)
                {
                    var booked = Guid.NewGuid();
                    await tx.ExecuteAsync("""
                        insert into core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
                        values (@booked, @lineId, N'issue', @premium, @premiumGel, @bookedOn, @from, @to, @userId)
                        """, new { booked, lineId = line.Id, premium = line.Premium, premiumGel = line.PremiumGel, bookedOn, from = policy.EffectiveFrom, to = policy.EffectiveTo, userId = user.Id });
                    await ReinsuranceStore.CedeIssuedPremiumAsync(tx, booked, user.Id);
                }
                await tx.ExecuteAsync("update core.policy set status = N'active', approved_at = sysdatetimeoffset(), approved_by = @userId where id = @id",
                    new { userId = user.Id, id });
                return "approved";
            });
        }
        catch (ReinsuranceException e)
        {
            outcome = "ri-" + Codes.Of(e.Problem).Replace('_', '-');
        }
        if (outcome == "forbidden") return Answer("forbidden");
        var number = await db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<string>("select number from core.policy where id = @id", new { id }));
        if (number is null) return Answer("invalid");
        return Answer(outcome == "approved" ? "done" : outcome == "skipped" ? "already" : outcome, number);
    }

    /// <summary>The text for an approval answer (legacy approve-answer.tsx answerText), or null for an unknown outcome.</summary>
    public static string? AnswerText(string outcome) =>
        PolicyTexts.ApproveOutcomes.TryGetValue(outcome, out var text) ? text
        : outcome == "reduction-exceeds" ? PolicyTexts.Endorse.ReductionExceeds
        : outcome == "suspended" ? AmlTexts.Suspension.Blocked
        : outcome.StartsWith("ri-", StringComparison.Ordinal) && ReinsuranceTexts.Problems.TryGetValue(outcome[3..], out var ri) ? ri
        : null;

    // ---------------------------------------------------------------- cancel

    /// <summary>Cancels an active policy from a date (legacy cancelPolicy): validation, then PolicyCancel in one transaction.</summary>
    public static async Task<FormAnswer> CancelPolicyAsync(Db db, CurrentUser user, IReadOnlyDictionary<string, string> values)
    {
        if (!user.CanWrite) return FormAnswer.Refused(Ka.Common.ReadOnly);
        if (!Uuid.TryParse(V(values, "policy_id"), out var id)) return FormAnswer.Refused(PolicyTexts.Cancel.Errors.NotActive);
        var cancelText = V(values, "cancel_on") ?? "";
        var reason = V(values, "reason");
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Dates.IsIsoDate(cancelText)) errors["cancel_on"] = PolicyTexts.Cancel.Errors.Date;
        if (string.IsNullOrEmpty(reason)) errors["reason"] = PolicyTexts.Cancel.Errors.Reason;
        if (errors.Count > 0) return FormAnswer.Invalid(errors);

        PolicyCancel.CancelOutcome outcome;
        try
        {
            outcome = (await db.AsUserAsync(user.Id, tx => PolicyCancel.CancelPolicyInTxAsync(tx, id, Dates.ParseIsoDate(cancelText), reason!, user.Id))).Outcome;
        }
        catch (Exception e) when (DbError.From(e)?.Code == DbError.CheckViolation)
        {
            return FormAnswer.Invalid(new Dictionary<string, string> { ["cancel_on"] = PolicyTexts.Cancel.Errors.PeriodClosed });
        }
        return outcome switch
        {
            PolicyCancel.CancelOutcome.NotActive => FormAnswer.Refused(PolicyTexts.Cancel.Errors.NotActive),
            PolicyCancel.CancelOutcome.OutsideTerm => FormAnswer.Invalid(new Dictionary<string, string> { ["cancel_on"] = PolicyTexts.Cancel.Errors.OutsideTerm }),
            _ => FormAnswer.GoTo($"/policies/{Uuid.Text(id)}?cancelled=1"),
        };
    }

    // ---------------------------------------------------------------- endorse

    private sealed record OriginalPolicy(Guid Id, Guid ContractId, string Status, DateOnly EffectiveFrom, DateOnly EffectiveTo, string PremiumCurrency,
        Guid? ProductId, string? ProductCode, string Segment, int? CancelGraceDays);

    /// <summary>JavaScript's Number(value.replace(',', '.')) for a filled field, NaN for an empty or missing one.</summary>
    private static double Amount(string? value)
    {
        if (string.IsNullOrEmpty(value)) return double.NaN;
        var i = value.IndexOf(',', StringComparison.Ordinal);
        return Insurance.Domain.Common.JsNumber.Parse(i < 0 ? value : string.Concat(value.AsSpan(0, i), ".", value.AsSpan(i + 1)));
    }

    /// <summary>
    /// A change to an active policy is issued as a new policy under the same contract (docs/04 rule რ10): it starts on
    /// the change date, ends with the original, carries only the premium difference for the remaining term (per
    /// coverage, possibly negative) and fixes its own exchange rate. It awaits approval like any policy. A positive
    /// difference is due on the change date; a negative one reduces the original's schedule only on approval (H01).
    /// </summary>
    public static async Task<FormAnswer> EndorsePolicyAsync(Db db, Rates rates, CurrentUser user, IReadOnlyDictionary<string, string> values, Func<string, bool> included)
    {
        if (!user.CanWrite) return FormAnswer.Refused(Ka.Common.ReadOnly);
        if (!Uuid.TryParse(V(values, "policy_id"), out var originalId)) return FormAnswer.Refused(PolicyTexts.Endorse.Errors.NotActive);
        var original = await db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<OriginalPolicy>("""
            select p.id as Id, p.contract_id as ContractId, p.status as Status, p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   p.premium_currency as PremiumCurrency, p.product_id as ProductId, pr.code as ProductCode, p.segment as Segment, p.cancel_grace_days as CancelGraceDays
            from core.policy p left join core.product pr on pr.id = p.product_id where p.id = @originalId
            """, new { originalId }));
        if (original is not { Status: "active", ProductId: { } productId }) return FormAnswer.Refused(PolicyTexts.Endorse.Errors.NotActive);
        var coverages = await db.ReadTxAsync(tx => CoveragesAsync(tx, productId));

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var changeText = V(values, "change_on") ?? "";
        DateOnly changeOn = default;
        if (!Dates.IsIsoDate(changeText) || (changeOn = Dates.ParseIsoDate(changeText)) < original.EffectiveFrom || changeOn >= original.EffectiveTo)
            errors["change_on"] = PolicyTexts.Endorse.Errors.Date;
        var reason = V(values, "reason");
        if (string.IsNullOrEmpty(reason)) errors["reason"] = PolicyTexts.Endorse.Errors.Reason;

        var lines = coverages.Where(c => included(c.Code)).Select(c =>
        {
            var delta = Amount(V(values, $"cov_{c.Code}_delta"));
            var deltaMinor = double.IsFinite(delta) ? (long)Math.Floor(delta * 100 + 0.5) : 0;
            var limit = Amount(V(values, $"cov_{c.Code}_limit"));
            var deductible = Amount(V(values, $"cov_{c.Code}_deductible"));
            return (Coverage: c, DeltaMinor: deltaMinor,
                Limit: limit > 0 ? (decimal?)decimal.Parse(limit.ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null,
                Deductible: deductible >= 0 ? (decimal?)decimal.Parse(deductible.ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null);
        }).ToList();
        if (lines.Count == 0) errors["coverages"] = PolicyTexts.Endorse.Errors.Coverages;

        var currencyText = original.PremiumCurrency.Trim();
        decimal? rate = null;
        if (!errors.ContainsKey("change_on"))
        {
            rate = await rates.RateOnAsync(currencyText, changeOn);
            if (rate is null) errors["change_on"] = PolicyTexts.Endorse.Errors.Rate;
        }
        if (errors.Count > 0 || rate is not { } fixedRate) return FormAnswer.Invalid(errors);
        var currency = Billing.ParseCurrency(currencyText);
        var today = Rates.TodayInTbilisi();
        var totalDelta = lines.Sum(l => l.DeltaMinor);

        Guid newId;
        try
        {
            newId = await db.AsUserAsync(user.Id, async tx =>
            {
                await tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = original.Id });
                var number = await tx.NextNumberAsync("policy:" + original.ProductCode)
                    ?? throw new InvalidOperationException($"number series policy:{original.ProductCode} does not exist");
                var policyId = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.policy (id, contract_id, number, issued_on, replaces_policy_id, effective_from, effective_to, status,
                                             premium_currency, premium_rate, product_id, cancel_grace_days, segment, change_note, created_by)
                    values (@policyId, @contractId, @number, @today, @originalId, @changeOn, @to, N'pending_approval',
                            @currency, @rate, @productId, @graceDays, @segment, @reason, @userId)
                    """, new
                {
                    policyId, contractId = original.ContractId, number, today, originalId = original.Id, changeOn, to = original.EffectiveTo,
                    currency = currencyText, rate = Math.Round(fixedRate, 6, MidpointRounding.AwayFromZero), productId, graceDays = original.CancelGraceDays,
                    segment = original.Segment, reason, userId = user.Id,
                });

                // The insured object travels with the change, unchanged.
                var objects = (await tx.QueryAsync<(string ObjectType, string Description, string? Attributes)>(
                    "select object_type, description, attributes from core.insured_object where policy_id = @id order by (select null)", new { id = original.Id })).ToList();
                Guid? objectId = null;
                foreach (var o in objects)
                {
                    var copy = Guid.NewGuid();
                    await tx.ExecuteAsync("insert into core.insured_object (id, policy_id, object_type, description, attributes) values (@copy, @policyId, @type, @description, @attributes)",
                        new { copy, policyId, type = o.ObjectType, description = o.Description, attributes = o.Attributes });
                    objectId ??= copy;
                }

                foreach (var line in lines)
                {
                    var gelMinor = Money.ToGel(new Money(line.DeltaMinor, currency), changeOn, (_, _) => fixedRate);
                    await tx.ExecuteAsync("""
                        insert into core.policy_line (policy_id, insured_object_id, product_coverage_id, class_code, sum_insured,
                                                      sum_insured_currency, deductible, premium, premium_gel)
                        values (@policyId, @objectId, @coverageId, @classCode, @limit, @limitCurrency, @deductible, @premium, @premiumGel)
                        """, new
                    {
                        policyId, objectId, coverageId = line.Coverage.Id, classCode = line.Coverage.ClassCode, limit = line.Limit,
                        limitCurrency = line.Limit is null ? null : currencyText, deductible = line.Deductible,
                        premium = Billing.FromTetri(line.DeltaMinor), premiumGel = Billing.FromTetri(gelMinor),
                    });
                }

                if (totalDelta > 0)
                    await tx.ExecuteAsync("insert into core.installment (policy_id, due_on, amount) values (@policyId, @changeOn, @amount)",
                        new { policyId, changeOn, amount = Billing.FromTetri(totalDelta) });

                await tx.ExecuteAsync("""
                    insert into core.policy_intermediary (policy_id, intermediary_id, commission_percent)
                    select @policyId, intermediary_id, commission_percent from core.policy_intermediary where policy_id = @id
                    """, new { policyId, id = original.Id });
                await tx.ExecuteAsync("""
                    insert into core.policy_party (policy_id, party_id, role)
                    select @policyId, party_id, role from core.policy_party where policy_id = @id
                    """, new { policyId, id = original.Id });
                return policyId;
            });
        }
        catch (Exception e) when (SeriesExhausted(e))
        {
            return FormAnswer.Refused(Ka.Common.SeriesExhausted);
        }
        return FormAnswer.GoTo($"/policies/{Uuid.Text(newId)}?issued=1");
    }

    // ---------------------------------------------------------------- facultative

    /// <summary>Places a facultative treaty on a policy; the outcome is shown on the policy card (null: nothing done, as legacy returned).</summary>
    public static async Task<string?> PlaceFacultativeAsync(Db db, CurrentUser user, string? policyText, string? treatyText)
    {
        if (!user.CanWrite || !Uuid.TryParse(policyText, out var policyId) || !Uuid.TryParse(treatyText, out var treatyId)) return null;
        string outcome;
        try
        {
            var placed = await db.AsUserAsync(user.Id, tx => ReinsuranceStore.PlaceFacultativeAsync(tx, policyId, treatyId, user.Id));
            outcome = Codes.Of(placed).Replace('_', '-');
        }
        catch (Exception e) when (DbError.From(e)?.Code == DbError.CheckViolation)
        {
            outcome = "period-closed";
        }
        return $"/policies/{policyText}?facultative={outcome}";
    }
}
