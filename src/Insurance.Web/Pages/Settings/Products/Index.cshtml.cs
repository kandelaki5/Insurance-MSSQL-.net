using Insurance.Application.Services;
using Insurance.Application.Texts.Screens;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Settings.Products;

/// <summary>The texts printed on each product's policy, admin only (legacy settings/products/page.tsx; action saveProductTexts).</summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Product(Guid Id, string Code, string NameKa, bool IsActive);

    private sealed record TextRow(Guid ProductId, string Key, string Value);

    public IReadOnlyList<Product> Products { get; private set; } = [];
    public IReadOnlyDictionary<Guid, Dictionary<string, string>> Texts { get; private set; } = new Dictionary<Guid, Dictionary<string, string>>();
    public string? Saved { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        Saved = Query("saved");
        (Products, Texts) = await Db.ReadTxAsync(async tx => (
            (IReadOnlyList<Product>)(await tx.QueryAsync<Product>("select id as Id, code as Code, name_ka as NameKa, is_active as IsActive from core.product order by code")).ToList(),
            (IReadOnlyDictionary<Guid, Dictionary<string, string>>)(await tx.QueryAsync<TextRow>("select product_id as ProductId, [key] as [Key], value as Value from core.product_text"))
                .GroupBy(t => t.ProductId).ToDictionary(g => g.Key, g => g.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal))));
        return Page();
    }

    /// <summary>Saves the texts printed on one product's policy (order №18/2017 significant-terms header); an empty field is not saved.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        if (PermissionPanel.Uuid(Form("product_id")) is not { } productId) return await OnGetAsync();
        var rows = SettingKeys.ProductTextLabels.Keys.Select(key => (Key: key, Value: JsText.Trim(Form(key) ?? ""))).Where(r => r.Value != "").ToList();
        await Db.AsUserAsync(Me.Id, async tx =>
        {
            foreach (var (key, value) in rows)
                await tx.ExecuteAsync("""
                    merge core.product_text with (holdlock) as t
                    using (select @productId as product_id, @key as [key], @value as value) as n on t.product_id = n.product_id and t.[key] = n.[key]
                    when matched then update set value = n.value
                    when not matched then insert (product_id, [key], value) values (n.product_id, n.[key], n.value);
                    """, new { productId, key, value });
            return 0;
        });
        return Redirect($"/settings/products?saved={Form("product_id")}");
    }
}
