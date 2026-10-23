using System.Net;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using Microsoft.Data.SqlClient;

namespace Insurance.Web.Tests;

/// <summary>
/// Company settings, the number series and the product texts over HTTP (legacy saveSettings, saveNumberSeries,
/// saveProductTexts). The number series cases are ported from scripts/number-settings-check.ts (Codex H48/H49 and the
/// owner's rule of 08.10.2026 that different kinds of document keep different prefixes).
/// </summary>
public sealed class SettingsTests(WebApp app) : IClassFixture<WebApp>
{
    private static Dictionary<string, string> AllSettings(IDictionary<string, string> stored, Dictionary<string, string> change)
    {
        var form = new Dictionary<string, string>();
        foreach (var key in Insurance.Application.Texts.Screens.SettingKeys.Labels.Keys) form[key] = stored.TryGetValue(key, out var v) ? v : "";
        form["company_phone_code"] = "";
        form["company_phone"] = "";
        foreach (var (k, v) in change) form[k] = v;
        return form;
    }

    private async Task<Dictionary<string, string>> StoredAsync()
    {
        await using var c = await app.Db.OpenAdminAsync();
        return (await c.QueryAsync<(string Key, string Value)>("select [key], value from core.setting")).ToDictionary(r => r.Key, r => r.Value);
    }

    [Fact]
    public async Task Settings_are_checked_as_a_whole_saved_as_the_admin_and_an_unchanged_value_writes_no_audit_row()
    {
        var (client, adminId) = await app.SignedInAsync("admin");
        var stored = await StoredAsync();
        foreach (var (key, value) in new[]
                 {
                     ("company_phone", "12"), ("accounting.close_within_days", "0"), ("accounting.close_within_days", "2.5"),
                     ("payment.fd_threshold_gel", "10,000"), ("payment.fd_threshold_gel", "1.234"), ("policy.reduction_order", "random"),
                 })
        {
            var response = await WebApp.PostFormAsync(client, "/settings?handler=SaveSettings", AllSettings(stored, new() { [key] = value, ["company_name"] = "არ შეინახება" }), "/settings");
            Assert.Equal($"/settings?invalid={Uri.EscapeDataString(key)}", Html.Location(response));
            Assert.Equal(stored["company_name"], (await StoredAsync())["company_name"]);
        }
        var invalid = await Html.GetAsync(client, "/settings?invalid=payment.fd_threshold_gel");
        Assert.Contains($"{Insurance.Application.Texts.Screens.SettingKeys.Labels["payment.fd_threshold_gel"]}: {Ka.Settings.InvalidAmount}. {Ka.Settings.NotSaved}", invalid);

        var saved = await WebApp.PostFormAsync(client, "/settings?handler=SaveSettings", AllSettings(stored, new()
        {
            ["company_name"] = " სატესტო მზღვეველი ", ["company_phone"] = "322 00 11 22", ["payment.fd_threshold_gel"] = "12000.5",
            ["accounting.close_within_days"] = "7", ["aml.high_risk_countries"] = "", ["policy.reduction_order"] = "equal_unpaid", ["regulator_name"] = "",
        }), "/settings");
        Assert.Equal("/settings?saved=1", Html.Location(saved));
        var after = await StoredAsync();
        Assert.Equal(("სატესტო მზღვეველი", "+995 322001122", "12000.5", "7", "", "equal_unpaid"),
            (after["company_name"], after["company_phone"], after["payment.fd_threshold_gel"], after["accounting.close_within_days"], after["aml.high_risk_countries"], after["policy.reduction_order"]));
        // A blank is not saved (except the jurisdictions list): the regulator keeps its name.
        Assert.Equal(stored["regulator_name"], after["regulator_name"]);
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(adminId, await c.ExecuteScalarAsync<Guid>("select top (1) user_id from core.audit_log where table_name = N'setting' and row_id = N'company_name' order by id desc"));
        var rows = await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name = N'setting'");
        Assert.Equal("/settings?saved=1", Html.Location(await WebApp.PostFormAsync(client, "/settings?handler=SaveSettings", AllSettings(after, []), "/settings")));
        Assert.Equal(rows, await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name = N'setting'"));
        var page = await Html.GetAsync(client, "/settings?saved=1");
        Assert.Contains(Ka.Settings.Saved, page);
        Assert.Contains("value=\"322001122\"", page);
    }

    private sealed record State(string Prefix, int Padding, long Next);

    private async Task<State> StateAsync(string code)
    {
        await using var c = await app.Db.OpenAdminAsync();
        return await c.QuerySingleAsync<State>("select prefix as Prefix, padding as Padding, next_value as Next from core.number_sequence where code = @code", new { code });
    }

    private async Task<string> SaveAsync(HttpClient client, Dictionary<string, string> fields)
    {
        var response = await WebApp.PostFormAsync(client, "/settings?handler=SaveNumberSeries", fields, "/settings");
        return Html.Location(response).Replace("/settings?", "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Number_series_refuse_a_format_that_cannot_fit_reaches_a_taken_number_overlaps_or_meets_another_kind()
    {
        var (client, adminId) = await app.SignedInAsync("admin");
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        string Code(string x) => $"policy:REG{tag}{x}";
        await using var admin = await app.Db.OpenAdminAsync();

        // race (H48): a number is taken, its transaction still open, while the administrator saves fewer digits.
        var race = Code("R");
        await admin.ExecuteAsync("insert core.number_sequence (code, prefix, next_value, padding) values (@race, @p, 99, 3)", new { race, p = $"R{tag}" });
        await using (var other = new SqlConnection(app.Db.AppConnection))
        {
            await other.OpenAsync();
            await using var transaction = (SqlTransaction)await other.BeginTransactionAsync();
            Assert.Equal($"R{tag}099", await new Tx(other, transaction).NextNumberAsync(race));
            var saving = SaveAsync(client, new() { [$"digits:{race}"] = "2" });
            await Task.Delay(1500);
            Assert.False(saving.IsCompleted);
            // Negative control: a read without the save's lock does not wait and still sees 99, which 2 digits hold — the
            // save would pass its check on it and leave a series that has run out.
            Assert.Equal(99, await admin.ExecuteScalarAsync<long>("select next_value from core.number_sequence where code = @race", new { race }));
            await transaction.CommitAsync();
            var answer = await saving;
            Assert.Contains("problem=fit", answer);
        }
        Assert.Equal(new State($"R{tag}", 3, 100), await StateAsync(race));

        // history (H49): an earlier format gave PFX11; a format whose future numbers include it is refused.
        var hist = Code("H");
        var pfx = $"H{tag}";
        await admin.ExecuteAsync("insert core.number_sequence (code, prefix, next_value, padding) values (@hist, @x, 2, 2)", new { hist, x = $"X{tag}" });
        var party = Guid.NewGuid();
        var contract = Guid.NewGuid();
        await admin.ExecuteAsync("""
            insert core.party (id, kind, name, tax_id) values (@party, N'organization', N'შპს REG', @taxId);
            insert core.contract (id, number, signed_on, policyholder_id) values (@contract, @contractNo, '2026-04-01', @party);
            insert core.policy (contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment)
            values (@contract, @number, '2026-04-01', '2026-04-01', '2027-04-01', N'pending_approval', N'GEL', 1, N'corporate');
            """, new { party, taxId = Fixtures.RandomTaxId()[..9], contract, contractNo = $"C-REG{tag}", number = $"{pfx}11" });
        Assert.Contains("problem=taken", await SaveAsync(client, new() { [$"prefix:{hist}"] = pfx, [$"digits:{hist}"] = "2" }));
        Assert.Equal($"X{tag}", (await StateAsync(hist)).Prefix);

        // overlap (H49): two policy series set to the same prefix and digits in one form.
        var a = Code("A");
        var b = Code("B");
        await admin.ExecuteAsync("insert core.number_sequence (code, prefix, next_value, padding) values (@a, @pa, 1, 6), (@b, @pb, 1, 6)", new { a, b, pa = $"A{tag}", pb = $"B{tag}" });
        Assert.Contains("problem=overlap", await SaveAsync(client, new() { [$"prefix:{a}"] = $"M{tag}", [$"prefix:{b}"] = $"M{tag}" }));
        Assert.Equal(($"A{tag}", $"B{tag}"), ((await StateAsync(a)).Prefix, (await StateAsync(b)).Prefix));

        // kinds (owner, 08.10.2026): the complaint series may not take a policy series' prefix, nor a format writing the same numbers.
        var complaint = await StateAsync("complaint");
        async Task<int> KindRows() => await admin.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name = N'number_sequence' and row_id = N'complaint'");
        foreach (var fields in new Dictionary<string, string>[]
                 {
                     new() { ["prefix:complaint"] = $"A{tag}", ["digits:complaint"] = "6" },
                     new() { ["prefix:complaint"] = $"A{tag}", ["digits:complaint"] = "8" },
                     new() { ["prefix:complaint"] = $"A{tag}0", ["digits:complaint"] = "5" },
                     new() { ["prefix:complaint"] = $"Z{tag}", [$"prefix:{a}"] = $"Z{tag}" },
                 })
        {
            var rows = await KindRows();
            var answer = await SaveAsync(client, fields);
            Assert.Contains("problem=kind", answer);
            Assert.Contains($"other={Uri.EscapeDataString(a)}", answer);
            Assert.Equal(complaint, await StateAsync("complaint"));
            Assert.Equal($"A{tag}", (await StateAsync(a)).Prefix);
            Assert.Equal(rows, await KindRows());
        }
        var kindPage = Html.Text(await Html.GetAsync(client, $"/settings?series=complaint&problem=kind&other={Uri.EscapeDataString(a)}"));
        Assert.Contains($"{Ka.Settings.Numbering.Labels["complaint"]}: {Ka.Settings.Numbering.Problems["kind"]}: {Ka.Settings.Numbering.Policy($"REG{tag}A")} (A{tag}, {Ka.Settings.Numbering.Digits}: 6). {Ka.Settings.Numbering.KindHint}. {Ka.Settings.NotSaved}", kindPage);
        Assert.Equal("series=saved", await SaveAsync(client, new() { ["prefix:complaint"] = $"Q{tag}" }));
        Assert.Equal($"Q{tag}", (await StateAsync("complaint")).Prefix);
        Assert.Equal("series=saved", await SaveAsync(client, new() { ["prefix:complaint"] = complaint.Prefix }));
        Assert.Equal(complaint, await StateAsync("complaint"));

        // A prefix that is not one, digits out of range, a counter that no longer fits.
        Assert.Contains("problem=prefix", await SaveAsync(client, new() { [$"prefix:{a}"] = "A B" }));
        Assert.Contains("problem=digits", await SaveAsync(client, new() { [$"digits:{a}"] = "13" }));
        Assert.Contains("problem=digits", await SaveAsync(client, new() { [$"digits:{a}"] = "abc" }));

        // ordinary: a plain prefix change typed in small letters is saved and audited with the admin as author.
        var ok = Code("O");
        await admin.ExecuteAsync("insert core.number_sequence (code, prefix, next_value, padding) values (@ok, @p, 15, 6)", new { ok, p = $"O{tag}" });
        Assert.Equal("series=saved", await SaveAsync(client, new() { [$"prefix:{ok}"] = $"n{tag}".ToLowerInvariant() }));
        Assert.Equal(new State($"N{tag}", 6, 15), await StateAsync(ok));
        var audit = (await admin.QueryAsync<(string? OldRow, string? NewRow, Guid? User)>(
            "select old_row, new_row, user_id from core.audit_log where table_name = N'number_sequence' and row_id = @ok and action = N'update'", new { ok })).ToList();
        Assert.Single(audit);
        Assert.Contains($"\"prefix\":\"O{tag}\"", audit[0].OldRow);
        Assert.Contains($"\"prefix\":\"N{tag}\"", audit[0].NewRow);
        Assert.Equal(adminId, audit[0].User);
        var page = await Html.GetAsync(client, "/settings?series=saved");
        Assert.Contains(Ka.Settings.Numbering.Saved, page);
        Assert.Contains($"N{tag}000015", page);
    }

    [Fact]
    public async Task Product_texts_are_saved_per_product_and_an_empty_field_is_not_saved()
    {
        var (client, _) = await app.SignedInAsync("admin");
        await using var c = await app.Db.OpenAdminAsync();
        var product = await c.ExecuteScalarAsync<Guid>("select id from core.product where code = N'CASCO'");
        var before = await c.ExecuteScalarAsync<string?>("select value from core.product_text where product_id = @product and [key] = N'withdrawal'", new { product });
        var page = await Html.GetAsync(client, "/settings/products");
        Assert.Contains(Ka.Settings.ProductTexts.Title, page);
        Assert.Equal($"/settings/products?saved={product}", Html.Location(await WebApp.PostFormAsync(client, "/settings/products",
            new() { ["product_id"] = product.ToString(), ["terms_version"] = " კასკოს პირობები v3 ", ["exclusions"] = "ომი; ბირთვული რისკი", ["withdrawal"] = "" }, "/settings/products")));
        Assert.Equal(("კასკოს პირობები v3", "ომი; ბირთვული რისკი"), await c.QuerySingleAsync<(string, string)>("""
            select (select value from core.product_text where product_id = @product and [key] = N'terms_version'),
                   (select value from core.product_text where product_id = @product and [key] = N'exclusions')
            """, new { product }));
        Assert.Equal(before, await c.ExecuteScalarAsync<string?>("select value from core.product_text where product_id = @product and [key] = N'withdrawal'", new { product }));
        Assert.Contains(Ka.Settings.ProductTexts.Saved, await Html.GetAsync(client, $"/settings/products?saved={product}"));
        Assert.Equal(HttpStatusCode.OK, (await WebApp.PostFormAsync(client, "/settings/products", new() { ["product_id"] = "x" }, "/settings/products")).StatusCode);
    }
}
