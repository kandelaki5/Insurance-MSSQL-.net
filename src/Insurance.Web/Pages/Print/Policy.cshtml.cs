using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Print;

/// <summary>The printable policy (legacy src/app/print/policies/[id]/page.tsx): any signed-in user may open it.</summary>
public sealed partial class PolicyModel : AppPageModel
{
    public sealed record PolicyRow(Guid Id, string Number, string ContractNumber, DateOnly IssuedOn, DateOnly EffectiveFrom, DateOnly EffectiveTo, string Status,
        string PremiumCurrency, decimal PremiumRate, Guid? ProductId, string? Product, string? ChangeNote, string? ReplacesNumber,
        string HolderName, string HolderKind, string? HolderId, string? HolderAddress, string? HolderPhone)
    {
        public string Currency => PremiumCurrency.Trim();
    }

    public sealed record LineRow(string? Coverage, string ClassCode, decimal? SumInsured, decimal? Deductible, decimal Premium);

    public sealed record PartyRow(string Role, string Name, string? IdNumber);

    public PolicyRow Policy { get; private set; } = null!;
    public IReadOnlyList<LineRow> Lines { get; private set; } = [];
    public IReadOnlyList<Billing.InstallmentRow> Schedule { get; private set; } = [];
    public IReadOnlyList<PartyRow> Parties { get; private set; } = [];
    public IReadOnlyList<string> Intermediaries { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Settings { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Texts { get; private set; } = new Dictionary<string, string>();
    public long Total { get; private set; }

    /// <summary>The first insured object's attributes as JavaScript would print each (a string as is, a number as String(n)); a null or missing one is absent.</summary>
    public IReadOnlyDictionary<string, JsonElement> Vehicle { get; private set; } = new Dictionary<string, JsonElement>();

    public string Setting(string key) => Settings.GetValueOrDefault(key) ?? "";
    public string Text(string key) => Texts.GetValueOrDefault(key) ?? "";

    /// <summary>A vehicle attribute as React rendered it: null or missing → null, a number as String(n), a string as is.</summary>
    public string? Attr(string key) => Vehicle.TryGetValue(key, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => Fmt.Plain((decimal)v.GetDouble()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    } : null;

    /// <summary>Whether an attribute is truthy as JavaScript reads it (a non-empty string, a number other than 0 and NaN, true).</summary>
    public bool Truthy(string key) => Vehicle.TryGetValue(key, out var v) && v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() != "",
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.False => false,
        _ => true,
    };

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]{1,2})?\z")]
    private static partial Regex AmountText();

    /// <summary>
    /// A stored amount shown exactly in whole tetri; a vehicle value that is a JSON number (or other text) as
    /// Number(x).toLocaleString('en-US', 2 decimals).
    /// </summary>
    public string Money(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && AmountText().IsMatch(value.GetString()!)) return PolicyCard.Stored(decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture));
        var number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : Insurance.Domain.Common.JsNumber.Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        return double.IsFinite(number) ? number.ToString("#,##0.00", CultureInfo.InvariantCulture) : "NaN";
    }

    public static string Money(decimal value) => PolicyCard.Stored(value);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Uuid.TryParse(RouteData.Values["id"]?.ToString(), out var id)) return NotFound();
        var policy = await Db.ReadAsync(c => c.QuerySingleOrDefaultAsync<PolicyRow>("""
            select p.id as Id, p.number as Number, c.number as ContractNumber, p.issued_on as IssuedOn, p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   p.status as Status, p.premium_currency as PremiumCurrency, p.premium_rate as PremiumRate, p.product_id as ProductId, pr.name_ka as Product,
                   p.change_note as ChangeNote, rp.number as ReplacesNumber, h.display_name as HolderName, h.kind as HolderKind,
                   coalesce(h.tax_id, h.foreign_id) as HolderId, coalesce(h.legal_address, h.actual_address) as HolderAddress, h.phone as HolderPhone
            from core.policy p
            join core.contract c on c.id = p.contract_id
            join core.party h on h.id = c.policyholder_id
            left join core.product pr on pr.id = p.product_id
            left join core.policy rp on rp.id = p.replaces_policy_id
            where p.id = @id
            """, new { id }));
        if (policy is null) return NotFound();
        Policy = policy;
        await Db.ReadTxAsync(async tx =>
        {
            var attributes = (await tx.QueryAsync<string?>("select attributes from core.insured_object where policy_id = @id", new { id })).FirstOrDefault();
            if (attributes is not null && JsonDocument.Parse(attributes).RootElement is { ValueKind: JsonValueKind.Object } root)
                Vehicle = root.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => p.Value.Clone());
            Lines = (await tx.QueryAsync<LineRow>("""
                select pc.name_ka as Coverage, l.class_code as ClassCode, l.sum_insured as SumInsured, l.deductible as Deductible, l.premium as Premium
                from core.policy_line l left join core.product_coverage pc on pc.id = l.product_coverage_id
                where l.policy_id = @id order by pc.sort_order
                """, new { id })).ToList();
            Schedule = (await Billing.LoadScheduleAsync(tx, id)).Where(s => Billing.Tetri(s.Amount) > 0).ToList();
            Parties = (await tx.QueryAsync<PartyRow>("""
                select pp.role as Role, p.display_name as Name, coalesce(p.tax_id, p.foreign_id) as IdNumber
                from core.policy_party pp join core.party p on p.id = pp.party_id where pp.policy_id = @id
                """, new { id })).ToList();
            Intermediaries = (await tx.QueryAsync<string>("""
                select p.display_name from core.policy_intermediary pi
                join core.intermediary i on i.id = pi.intermediary_id join core.party p on p.id = i.party_id where pi.policy_id = @id
                """, new { id })).ToList();
            Settings = await Insurance.Application.Services.Settings.LoadSettingsAsync(tx);
            Texts = policy.ProductId is { } productId ? await Insurance.Application.Services.Settings.LoadProductTextsAsync(tx, productId) : new Dictionary<string, string>();
            return 0;
        });
        Total = Lines.Sum(l => Billing.Tetri(l.Premium));
        return Page();
    }
}
