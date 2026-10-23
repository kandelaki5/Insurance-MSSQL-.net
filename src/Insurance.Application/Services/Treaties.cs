using System.Globalization;
using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>Entering and closing reinsurance treaties (legacy src/app/(app)/reinsurance/treaties/actions.ts createTreaty, closeTreaty).</summary>
public static class Treaties
{
    public sealed record CreateResult(IReadOnlyDictionary<string, string> Errors, string? Message, Guid? Id);

    private static readonly string[] CurrencyCodes = ["GEL", "USD", "EUR"];

    /// <summary>The old number(): empty is NaN (null here), a comma is a decimal point, otherwise JavaScript's Number().</summary>
    private static decimal? Number(string? value) => string.IsNullOrEmpty(value) ? null : JsNumber.ParseFiniteDecimal(value.Replace(',', '.'));

    private static bool PercentOk(decimal? value) => value is >= 0 and <= 100;

    private static decimal Places(decimal value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Validates and creates a treaty with its classes; the values are the posted fields, trimmed; <paramref name="classes"/> the ticked
    /// class codes. Obligatory treaties that would overlap on the same class and dates are checked here, so the conflict is found when the
    /// treaty is entered rather than when a policy is approved (H07, H08).
    /// </summary>
    public static async Task<CreateResult> CreateAsync(Db db, CurrentUser user, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> classes)
    {
        if (!user.CanWrite) return new CreateResult(new Dictionary<string, string>(), Ka.Common.ReadOnly, null);
        string? V(string key) => values.TryGetValue(key, out var v) ? v : null;
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(V("name"))) errors["name"] = ReinsuranceTexts.Errors.Name;
        var kind = V("kind") is { } k && ReinsuranceTexts.Kinds.ContainsKey(k) ? k : "quota_share";
        var reinsurerCode = V("reinsurer_id_number");
        var brokerCode = V("broker_id_number");
        var reinsurer = string.IsNullOrEmpty(reinsurerCode) ? null : await db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>(
            "select top (1) id from core.party where kind = N'organization' and (tax_id = @code or foreign_id = @code)", new { code = reinsurerCode }));
        if (reinsurer is null) errors["reinsurer_id_number"] = ReinsuranceTexts.Errors.Reinsurer;
        var broker = string.IsNullOrEmpty(brokerCode) ? null : await db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>(
            "select top (1) id from core.party where tax_id = @code or foreign_id = @code", new { code = brokerCode }));
        if (!string.IsNullOrEmpty(brokerCode) && broker is null) errors["broker_id_number"] = ReinsuranceTexts.Errors.Broker;
        var fromText = V("period_from") ?? "";
        var toText = V("period_to") ?? "";
        var periodOk = Dates.IsIsoDate(fromText) && Dates.IsIsoDate(toText) && string.CompareOrdinal(toText, fromText) >= 0
            && !fromText.StartsWith("0000", StringComparison.Ordinal);
        if (!periodOk) errors["period_to"] = ReinsuranceTexts.Errors.Period;
        var currency = CurrencyCodes.Contains(V("currency") ?? "") ? V("currency")! : "GEL";

        var cededPercent = Number(V("ceded_percent"));
        var retention = Number(V("retention"));
        var linesValue = JsNumber.Parse(V("lines") ?? "x");
        var priority = Number(V("priority"));
        var limit = Number(V("treaty_limit"));
        var treatyPremium = Number(V("treaty_premium"));
        if (kind is "quota_share" or "facultative" && !PercentOk(cededPercent)) errors["ceded_percent"] = ReinsuranceTexts.Errors.CededPercent;
        var linesOk = double.IsFinite(linesValue) && Math.Floor(linesValue) == linesValue && linesValue > 0;
        if (kind == "surplus" && !(retention > 0 && linesOk)) errors["retention"] = ReinsuranceTexts.Errors.Surplus;
        if (kind == "excess_of_loss" && !(priority >= 0)) errors["priority"] = ReinsuranceTexts.Errors.Priority;
        var commissionPercent = string.IsNullOrEmpty(V("commission_percent")) ? 0m : Number(V("commission_percent"));
        var brokerPercent = string.IsNullOrEmpty(V("broker_percent")) ? 0m : Number(V("broker_percent"));
        if (!PercentOk(commissionPercent)) errors["commission_percent"] = ReinsuranceTexts.Errors.Percent;
        if (!PercentOk(brokerPercent)) errors["broker_percent"] = ReinsuranceTexts.Errors.Percent;
        if (kind != "facultative" && classes.Count == 0) errors["classes"] = ReinsuranceTexts.Errors.Classes;
        if (errors.Count > 0) return new CreateResult(errors, null, null);

        var from = Dates.ParseIsoDate(fromText);
        var to = Dates.ParseIsoDate(toText);
        if (kind != "facultative")
        {
            var overlapping = (await db.ReadTxAsync(tx => tx.QueryAsync<(string Kind, decimal? CededPercent)>("""
                select t.kind, t.ceded_percent from core.ri_treaty t
                where t.status = N'active' and t.kind <> N'facultative' and t.period_from <= @to and t.period_to >= @from
                  and exists (
                    select 1 from core.ri_treaty_class tc join core.insurance_class a on a.code = tc.class_code
                    join core.insurance_class b on b.code in @classes
                    where tc.treaty_id = t.id and coalesce(a.parent_code, a.code) = coalesce(b.parent_code, b.code))
                """, new { from, to, classes }))).ToList();
            var isXol = kind == "excess_of_loss";
            if (overlapping.Any(o => (o.Kind == "excess_of_loss") != isXol)) return new CreateResult(new Dictionary<string, string> { ["classes"] = ReinsuranceTexts.Errors.XolWithProportional }, null, null);
            if (isXol && overlapping.Count > 0) return new CreateResult(new Dictionary<string, string> { ["classes"] = ReinsuranceTexts.Errors.XolLayers }, null, null);
            var quotaTotal = overlapping.Where(o => o.Kind == "quota_share").Sum(o => o.CededPercent ?? 0m);
            if (kind == "quota_share" && quotaTotal + cededPercent!.Value > 100) return new CreateResult(new Dictionary<string, string> { ["ceded_percent"] = ReinsuranceTexts.Errors.OverCeded }, null, null);
        }

        try
        {
            var id = await db.AsUserAsync(user.Id, async tx =>
            {
                var number = await tx.NextNumberAsync("treaty") ?? throw new InvalidOperationException("number series treaty does not exist");
                var created = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.ri_treaty (id, number, name, reinsurer_party_id, broker_party_id, kind, period_from, period_to, currency,
                                                ceded_percent, retention, lines, priority, treaty_limit, treaty_premium, commission_percent, broker_percent,
                                                reinsurer_rating, rating_agency, note, created_by)
                    values (@created, @number, @name, @reinsurer, @broker, @kind, @from, @to, @currency, @cededPercent, @retention, @lines, @priority, @limit,
                            @treatyPremium, @commissionPercent, @brokerPercent, @rating, @agency, @note, @userId)
                    """, new
                {
                    created, number, name = V("name"), reinsurer, broker, kind, from, to, currency,
                    cededPercent = kind is "quota_share" or "facultative" ? Places(cededPercent!.Value, 4) : (decimal?)null,
                    retention = kind == "surplus" ? Places(retention!.Value, 2) : (decimal?)null,
                    lines = kind == "surplus" ? (int?)checked((int)linesValue) : null,
                    priority = kind == "excess_of_loss" ? Places(priority!.Value, 2) : (decimal?)null,
                    limit = kind == "excess_of_loss" && limit > 0 ? Places(limit.Value, 2) : (decimal?)null,
                    treatyPremium = treatyPremium > 0 ? Places(treatyPremium.Value, 2) : (decimal?)null,
                    commissionPercent = Places(commissionPercent!.Value, 4),
                    brokerPercent = Places(brokerPercent!.Value, 4),
                    rating = string.IsNullOrEmpty(V("reinsurer_rating")) ? null : V("reinsurer_rating"),
                    agency = string.IsNullOrEmpty(V("rating_agency")) ? null : V("rating_agency"),
                    note = string.IsNullOrEmpty(V("note")) ? null : V("note"),
                    userId = user.Id,
                });
                foreach (var code in classes)
                    await tx.ExecuteAsync("insert into core.ri_treaty_class (treaty_id, class_code) values (@created, @code)", new { created, code });
                return created;
            });
            return new CreateResult(new Dictionary<string, string>(), null, id);
        }
        catch (Exception e) when (DbError.From(e)?.Code == "NU001")
        {
            return new CreateResult(new Dictionary<string, string>(), Ka.Common.SeriesExhausted, null);
        }
    }

    /// <summary>Closes a treaty: no new cessions; existing ones stay as booked. Nothing happens for a viewer or a closed treaty.</summary>
    public static async Task CloseAsync(Db db, CurrentUser user, Guid id)
    {
        if (!user.CanWrite) return;
        await db.AsUserAsync(user.Id, tx => tx.ExecuteAsync("update core.ri_treaty set status = N'closed' where id = @id and status = N'active'", new { id }));
    }

}
