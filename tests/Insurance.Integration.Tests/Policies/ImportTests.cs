using System.Text;
using System.Text.Json;
using Insurance.Application.Excel;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Import = Insurance.Application.Services.PolicyImport;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/integration/import-plan-confirmation.test.ts: the corporate import's preview and confirmation on real
/// files (UTF-8 CSV with and without a byte-order mark, an .xlsx workbook) and a real database — the plan checked again on
/// confirmation (PR220-F1), Georgian text as written (PR220-F2), the closed month (PR220-F3), each policy at its own start
/// rate, and the refusals.
/// </summary>
public sealed class ImportTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private const string Header = "make,model,plate,value,installments,premium_damage,limit_damage,deductible_damage,premium_theft,limit_theft,deductible_theft";
    private const string Row1 = "ტესტი,იმპორტი,IM-011-PT,30000,11,900,30000,0,300,30000,0";
    private const string Row2 = "ტესტი,მეორე,IM-004-PT,20000,4,300,20000,0,100,20000,0";

    private static byte[] Csv(bool bom, params string[] rows)
    {
        var text = Encoding.UTF8.GetBytes(Header + "\n" + string.Join("\n", rows) + "\n");
        return bom ? [0xEF, 0xBB, 0xBF, .. text] : text;
    }

    private static byte[] Xlsx()
    {
        var headers = Header.Split(',');
        IEnumerable<KeyValuePair<string, object?>> Rec(string row) => headers.Zip(row.Split(','), (h, v) => new KeyValuePair<string, object?>(h,
            double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : v));
        var sheet = XlsxWriter.JsonToSheet([Rec(Row1), Rec(Row2)], headers);
        var book = new XlsxBook();
        book.AppendSheet(sheet, "policies");
        return book.Write();
    }

    private async Task<(CurrentUser User, string TaxId, Guid Holder)> ClientAsync()
    {
        var user = await w.UserAsync("underwriter");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (user, taxId, await w.ClientAsync(taxId));
    }

    private static Dictionary<string, string> FormFor(string taxId, string from = "2026-10-01", string to = "2027-10-01", string currency = "GEL") =>
        World.Form(("policyholder_id_number", taxId), ("effective_from", from), ("effective_to", to), ("currency", currency), ("installments", "1"));

    private Task<List<(string Number, string Status, DateOnly From, DateOnly To, decimal Rate)>> PoliciesOf(Guid holder) =>
        w.QueryAsync<(string, string, DateOnly, DateOnly, decimal)>("""
            select p.number, p.status, p.effective_from, p.effective_to, p.premium_rate from core.policy p join core.contract c on c.id = p.contract_id
            where c.policyholder_id = @holder order by p.number
            """, new { holder });

    public static IEnumerable<object[]> Files() => [["csv"], ["csv-bom"], ["xlsx"]];

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Two_policies_of_one_file_go_under_one_contract_with_Georgian_text_as_written(string kind)
    {
        var (user, taxId, holder) = await ClientAsync();
        var file = kind == "xlsx" ? Xlsx() : Csv(kind == "csv-bom", Row1, Row2);
        var preview = await Import.PreviewAsync(w.App, user, FormFor(taxId), file);
        Assert.Null(preview.Message);
        Assert.Equal([("ტესტი იმპორტი IM-011-PT", 0, "1200.00 GEL", "2026-10-01 – 2027-10-01", 2), ("ტესტი მეორე IM-004-PT", 0, "400.00 GEL", "2026-10-01 – 2027-10-01", 3)],
            preview.Rows!.Select(r => (r.Description, r.Errors.Count, r.Premium, r.Term, r.Row)));
        Assert.Equal(PolicyTexts.Import.PreviewSummary(2, 2), PolicyTexts.Import.PreviewSummary(preview.Rows!.Count, preview.Rows!.Count(r => r.Errors.Count == 0)));
        var payload = JsonDocument.Parse(preview.Payload!).RootElement;
        Assert.Equal(["ტესტი|იმპორტი|IM-011-PT|11", "ტესტი|მეორე|IM-004-PT|4"], payload.EnumerateArray().Select(p =>
            $"{p.GetProperty("vehicle").GetProperty("make").GetString()}|{p.GetProperty("vehicle").GetProperty("model").GetString()}|{p.GetProperty("vehicle").GetProperty("plate").GetString()}|{p.GetProperty("installmentCount").GetInt32()}"));
        Assert.Empty(await PoliciesOf(holder));

        var confirmed = await Import.ConfirmAsync(w.App, w.Rates, user, preview.Payload, preview);
        var contract = await w.ScalarAsync<string>("select number from core.contract where policyholder_id = @holder", new { holder });
        Assert.Equal($"/policies?q={Uri.EscapeDataString(contract)}", confirmed.Redirect);
        var policies = await PoliciesOf(holder);
        Assert.Equal(2, policies.Count);
        Assert.All(policies, p => Assert.Equal(("pending_approval", new DateOnly(2026, 10, 1), new DateOnly(2027, 10, 1), 1m), (p.Status, p.From, p.To, p.Rate)));
        var lines = await w.QueryAsync<(string Plate, string Coverage, decimal Premium)>("""
            select json_value(o.attributes, '$.plate'), pc.code, l.premium from core.policy_line l join core.insured_object o on o.id = l.insured_object_id
            join core.product_coverage pc on pc.id = l.product_coverage_id join core.policy p on p.id = l.policy_id join core.contract c on c.id = p.contract_id
            where c.policyholder_id = @holder order by 1, 2
            """, new { holder });
        Assert.Equal([("IM-004-PT", "damage", 300m), ("IM-004-PT", "theft", 100m), ("IM-011-PT", "damage", 900m), ("IM-011-PT", "theft", 300m)], lines);
        var objects = await w.QueryAsync<(string Description, string Make, string Model)>("""
            select o.description, json_value(o.attributes, '$.make'), json_value(o.attributes, '$.model') from core.insured_object o
            join core.policy p on p.id = o.policy_id join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder order by 1
            """, new { holder });
        Assert.Equal([("ტესტი იმპორტი IM-011-PT", "ტესტი", "იმპორტი"), ("ტესტი მეორე IM-004-PT", "ტესტი", "მეორე")], objects);
        var installments = await w.QueryAsync<(string Plate, DateOnly DueOn, decimal Amount)>("""
            select json_value(o.attributes, '$.plate'), i.due_on, i.amount from core.installment i join core.insured_object o on o.policy_id = i.policy_id
            join core.policy p on p.id = i.policy_id join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder order by 1, 2
            """, new { holder });
        // 11 payments: 200.00 on 01.10, then ten of 100.00; 4: quarterly 100.00.
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2027, 1, 1), new DateOnly(2027, 4, 1), new DateOnly(2027, 7, 1)], installments.Where(i => i.Plate == "IM-004-PT").Select(i => i.DueOn));
        Assert.All(installments.Where(i => i.Plate == "IM-004-PT"), i => Assert.Equal(100m, i.Amount));
        var eleven = installments.Where(i => i.Plate == "IM-011-PT").ToList();
        Assert.Equal(11, eleven.Count);
        Assert.Equal((new DateOnly(2026, 10, 1), 200m), (eleven[0].DueOn, eleven[0].Amount));
        Assert.All(eleven.Skip(1), (i, n) => Assert.Equal((new DateOnly(2026, 11, 1).AddMonths(n), 100m), (i.DueOn, i.Amount)));
        Assert.Equal(1600m, installments.Sum(i => i.Amount));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder", new { holder }));
    }

    [Fact]
    public async Task The_confirmation_checks_the_plan_again_and_writes_nothing_of_a_changed_payload()
    {
        var (user, taxId, holder) = await ClientAsync();
        var preview = await Import.PreviewAsync(w.App, user, FormFor(taxId), Csv(false, Row1, Row2));
        string Edit(Action<List<Dictionary<string, object?>>> change)
        {
            var inputs = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(preview.Payload!)!;
            change(inputs);
            return JsonSerializer.Serialize(inputs);
        }
        var counter = await w.ScalarAsync<string>("select concat(next_value, '') from core.number_sequence where code = N'contract'");
        Assert.Equal(PolicyTexts.Errors.Installments, (await Import.ConfirmAsync(w.App, w.Rates, user, Edit(i => i[1]["installmentCount"] = 2), preview)).Message);
        Assert.Equal(PolicyTexts.Errors.Installments, (await Import.ConfirmAsync(w.App, w.Rates, user, Edit(i => i[0]["installmentCount"] = "11"), preview)).Message);
        Assert.Equal(PolicyTexts.Errors.InstallmentsEleven, (await Import.ConfirmAsync(w.App, w.Rates, user, Edit(i => i[0]["to"] = "2027-09-01"), preview)).Message);
        Assert.Equal(PolicyTexts.Import.Errors.Unreadable, (await Import.ConfirmAsync(w.App, w.Rates, user, "{not json", preview)).Message);
        Assert.Equal(PolicyTexts.Import.Errors.Empty, (await Import.ConfirmAsync(w.App, w.Rates, user, "[]", preview)).Message);
        Assert.Equal(PolicyTexts.Import.Errors.Empty, (await Import.ConfirmAsync(w.App, w.Rates, user, "{}", preview)).Message);
        Assert.Equal(Ka.Common.ReadOnly, (await Import.ConfirmAsync(w.App, w.Rates, await w.UserAsync("viewer"), preview.Payload, preview)).Message);
        Assert.Empty(await PoliciesOf(holder));
        Assert.Equal(counter, await w.ScalarAsync<string>("select concat(next_value, '') from core.number_sequence where code = N'contract'"));
        // The refused confirmation keeps the preview to show.
        var refused = await Import.ConfirmAsync(w.App, w.Rates, user, "[]", preview);
        Assert.Equal(preview.Rows!.Count, refused.Rows!.Count);
    }

    [Fact]
    public async Task The_previews_refusals()
    {
        var (user, taxId, holder) = await ClientAsync();
        Assert.Equal(Ka.Common.ReadOnly, (await Import.PreviewAsync(w.App, await w.UserAsync("viewer"), FormFor(taxId), Csv(false, Row1))).Message);
        Assert.Equal(PolicyTexts.Errors.Policyholder, (await Import.PreviewAsync(w.App, user, FormFor("999999999"), Csv(false, Row1))).Message);
        Assert.Equal(PolicyTexts.Import.Errors.NoFile, (await Import.PreviewAsync(w.App, user, FormFor(taxId), null)).Message);
        Assert.Equal(PolicyTexts.Import.Errors.NoFile, (await Import.PreviewAsync(w.App, user, FormFor(taxId), [])).Message);
        Assert.Equal(PolicyTexts.Import.Errors.Empty, (await Import.PreviewAsync(w.App, user, FormFor(taxId), Encoding.UTF8.GetBytes(Header + "\n"))).Message);
        Assert.Equal(PolicyTexts.Import.Errors.Unreadable, (await Import.PreviewAsync(w.App, user, FormFor(taxId), [0x50, 0x4b, 0x03, 0x04, 0x00, 0x01])).Message);
        var many = Csv(false, Enumerable.Range(0, Import.MaxRows + 1).Select(n => $"Toyota,Corolla,TT-{n:000}-TT,30000,1,500,,,,,").ToArray());
        Assert.Equal($"{PolicyTexts.Import.Errors.TooMany} ({Import.MaxRows})", (await Import.PreviewAsync(w.App, user, FormFor(taxId), many)).Message);
        // Row errors: a duplicate car, a missing premium; no payload for the batch.
        var rows = await Import.PreviewAsync(w.App, user, FormFor(taxId), Csv(false, Row1, Row1, ",,,,,,,,,,", "Opel,Astra,OP-001-OP,10000,2,,,,,,"));
        Assert.Null(rows.Payload);
        Assert.Equal([PolicyTexts.Import.Errors.Duplicate], rows.Rows![1].Errors);
        Assert.Equal(3, rows.Rows.Count);
        Assert.Equal(4, rows.Rows[2].Row);
        Assert.Contains(PolicyTexts.Errors.Premium, rows.Rows[2].Errors);
        Assert.Contains(PolicyTexts.Errors.Installments, rows.Rows[2].Errors);
        Assert.Equal("", rows.Rows[2].Premium);
        // A suspended client: refused before the file is read.
        await w.SuspendAsync(holder, user.Id);
        Assert.Equal(AmlTexts.Suspension.Blocked, (await Import.PreviewAsync(w.App, user, FormFor(taxId), Csv(false, Row1))).Message);
        Assert.Empty(await PoliciesOf(holder));
    }

    [Fact]
    public async Task Each_usd_policy_keeps_its_own_start_rate_and_a_missing_one_writes_nothing()
    {
        var (user, taxId, holder) = await ClientAsync();
        await w.RateAsync("USD", new DateOnly(2026, 10, 1), 2.7m);
        await w.RateAsync("USD", new DateOnly(2026, 10, 15), 2.8m);
        var second = Row2.Replace(",4,", ",1,");
        var csv = Encoding.UTF8.GetBytes(Header + ",effective_from,effective_to\n" + Row1 + ",,\n" + second + ",2026-10-15,2027-10-15\n");
        var preview = await Import.PreviewAsync(w.App, user, FormFor(taxId, currency: "USD"), csv);
        Assert.NotNull(preview.Payload);
        Assert.NotNull((await Import.ConfirmAsync(w.App, w.Rates, user, preview.Payload, preview)).Redirect);
        var gel = await w.QueryAsync<(decimal Rate, decimal Premium, decimal PremiumGel)>("""
            select p.premium_rate, l.premium, l.premium_gel from core.policy_line l join core.policy p on p.id = l.policy_id join core.contract c on c.id = p.contract_id
            where c.policyholder_id = @holder order by p.effective_from, l.premium desc
            """, new { holder });
        Assert.Equal([(2.7m, 900m, 2430m), (2.7m, 300m, 810m), (2.8m, 300m, 840m), (2.8m, 100m, 280m)], gel);

        // A start day with no stored rate (and no network): the date's message, nothing written.
        var (user2, taxId2, holder2) = await ClientAsync();
        var noRate = Encoding.UTF8.GetBytes(Header + ",effective_from,effective_to\n" + Row1 + ",,\n" + second + ",2026-10-16,2027-10-16\n");
        var p2 = await Import.PreviewAsync(w.App, user2, FormFor(taxId2, currency: "USD"), noRate);
        Assert.Equal($"{PolicyTexts.Errors.Rate}: 2026-10-16", (await Import.ConfirmAsync(w.App, w.Rates, user2, p2.Payload, p2)).Message);
        Assert.Empty(await PoliciesOf(holder2));
    }

    [Fact]
    public async Task The_template_has_the_headers_and_one_example_row()
    {
        var bytes = await Import.TemplateAsync(w.App);
        Assert.Equal((byte)'P', bytes[0]);
        var rows = Insurance.Application.Excel.PolicyImport.ParseImport(bytes, [new("damage", true), new("theft", false)], new Dictionary<string, string>());
        var row = Assert.Single(rows);
        Assert.Equal(("Toyota", "Corolla", "AA-001-AA", "35000", "700", "35000", ""), (row.Values["make"], row.Values["model"], row.Values["plate"], row.Values["value"],
            row.Values["premium_damage"], row.Values["limit_damage"], row.Values["premium_theft"]));
    }
}

/// <summary>An import row starting in a closed month (PR220-F3): an error in the preview, refused again on confirmation. Its own database: September 2026 is closed.</summary>
public sealed class ImportClosedMonthTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task A_row_starting_in_a_closed_month_is_an_error_and_a_payload_changed_to_it_is_refused()
    {
        var user = await w.UserAsync("underwriter");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        await w.CloseMonthAsync(new DateOnly(2026, 9, 1), user.Id);
        const string header = "make,model,plate,value,installments,premium_damage,effective_from,effective_to";
        var csv = Encoding.UTF8.GetBytes($"{header}\nA,B,AB-001-AB,1000,1,100,2026-09-01,2027-09-01\nC,D,CD-001-CD,1000,1,100,2026-10-01,2027-10-01\n");
        var form = World.Form(("policyholder_id_number", taxId), ("currency", "GEL"), ("installments", "1"));
        var preview = await Import.PreviewAsync(w.App, user, form, csv);
        Assert.Equal([PolicyTexts.Import.Errors.PeriodClosed], preview.Rows![0].Errors);
        Assert.Empty(preview.Rows[1].Errors);
        Assert.Null(preview.Payload);

        // The first open day itself is allowed; a payload changed to start in the closed month is refused.
        var valid = await Import.PreviewAsync(w.App, user, form, Encoding.UTF8.GetBytes($"{header}\nC,D,CD-001-CD,1000,1,100,2026-10-01,2027-10-01\n"));
        Assert.NotNull(valid.Payload);
        var changed = valid.Payload!.Replace("\"from\":\"2026-10-01\"", "\"from\":\"2026-09-01\"", StringComparison.Ordinal);
        Assert.NotEqual(valid.Payload, changed);
        Assert.Equal(PolicyTexts.Import.Errors.PeriodClosed, (await Import.ConfirmAsync(w.App, w.Rates, user, changed, valid)).Message);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @holder", new { holder }));
        Assert.NotNull((await Import.ConfirmAsync(w.App, w.Rates, user, valid.Payload, valid)).Redirect);
    }
}
