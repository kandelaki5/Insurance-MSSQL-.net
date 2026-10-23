using System.Globalization;
using Insurance.Application.Services;
using Insurance.Application.Texts.Screens;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Settings;

/// <summary>
/// Company and regulator details, the AML thresholds, the closing deadline, the payment threshold and the document number
/// series, admin only (legacy settings/page.tsx; actions saveSettings, saveNumberSeries).
/// </summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record Series(string Code, string Prefix, int Padding, long NextValue, string? Product);

    public IReadOnlyDictionary<string, string> Stored { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyList<Series> SeriesRows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        (Stored, SeriesRows) = await Db.ReadTxAsync(async tx => (
            await Application.Services.Settings.LoadSettingsAsync(tx),
            (IReadOnlyList<Series>)(await tx.QueryAsync<Series>("""
                select s.code as Code, s.prefix as Prefix, s.padding as Padding, s.next_value as NextValue, p.name_ka as Product
                from core.number_sequence s left join core.product p on s.code = N'policy:' + p.code
                where s.code in @codes or s.code like N'policy:%'
                order by case when s.code like N'policy:%' then 0 else 1 end, s.code
                """, new { codes = SettingKeys.SeriesTables.Keys.ToArray() })).ToList()));
        return Page();
    }

    public string Label(Series s) =>
        s.Code.StartsWith("policy:", StringComparison.Ordinal)
            ? Application.Texts.Ka.Settings.Numbering.Policy(s.Product ?? s.Code[7..])
            : Application.Texts.Ka.Settings.Numbering.Labels.GetValueOrDefault(s.Code) ?? s.Code;

    /// <summary>Saves the company and regulator details used on printed documents, the AML thresholds and the closing deadline.</summary>
    public async Task<IActionResult> OnPostSaveSettingsAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        var rows = SettingKeys.Labels.Keys.Select(key => (Key: key, Value: JsText.Trim(Form(key) ?? ""))).ToList();
        string Invalid(string key) => $"/settings?invalid={Uri.EscapeDataString(key)}";
        // Phones come as a country code (+995 unless another is entered) and a number, stored in one format.
        for (var i = 0; i < rows.Count; i++)
        {
            if (!SettingKeys.PhoneKeys.Contains(rows[i].Key) || rows[i].Value == "") continue;
            var phone = Phone.NormalizePhone(Form($"{rows[i].Key}_code") ?? "", rows[i].Value);
            if (phone is null) return Redirect(Invalid(rows[i].Key));
            rows[i] = (rows[i].Key, phone);
        }
        // A number of days must be a whole number from 1 to 31; nothing is saved otherwise.
        foreach (var row in rows.Where(r => SettingKeys.DayKeys.Contains(r.Key) && r.Value != ""))
            if (CloseDeadline.ParseCloseWithinDays(row.Value) is null) return Redirect(Invalid(row.Key));
        // An amount must be one (the financial director's threshold); nothing is saved otherwise.
        foreach (var row in rows.Where(r => SettingKeys.AmountKeys.Contains(r.Key) && r.Value != ""))
            if (Application.Services.Settings.ParseThreshold(row.Value) is null) return Redirect(Invalid(row.Key));
        // A choice must be one of its list; nothing is saved otherwise.
        foreach (var row in rows.Where(r => SettingKeys.ChoiceKeys.ContainsKey(r.Key) && r.Value != ""))
            if (!SettingKeys.ChoiceKeys[row.Key].Contains(row.Value)) return Redirect(Invalid(row.Key));
        var kept = rows.Where(r => r.Value != "" || SettingKeys.BlankAllowed.Contains(r.Key)).ToList();
        // Written as the admin, so the audit records who changed which setting, from what to what; a setting saved
        // unchanged writes no audit row.
        if (kept.Count > 0)
            await Db.AsUserAsync(Me.Id, async tx =>
            {
                foreach (var (key, value) in kept)
                    await tx.ExecuteAsync("""
                        merge core.setting with (holdlock) as s
                        using (select @key as [key], @value as value) as n on s.[key] = n.[key]
                        when matched then update set value = n.value
                        when not matched then insert ([key], value) values (n.[key], n.value);
                        """, new { key, value });
                return 0;
            });
        // A fresh page: says it was saved, drops an earlier refusal from the address and shows the stored values.
        return Redirect("/settings?saved=1");
    }

    private sealed record Proposed(string Code, string Table, string Prefix, double Digits, long Next, bool Changed)
    {
        public bool WholeDigits => double.IsFinite(Digits) && Digits == Math.Floor(Digits) && Math.Abs(Digits) < 1e9;
        public int IntDigits => (int)Digits;
    }

    /// <summary>The table holding a series' documents (policy series share core.policy).</summary>
    private static string SeriesTable(string code) => code.StartsWith("policy:", StringComparison.Ordinal) ? "policy" : SettingKeys.SeriesTables[code];

    /// <summary>
    /// Document number series (owner, 06.10.2026): the administrator may change a series' prefix and digits; the counter
    /// itself is not changed here. All in one transaction (Codex H48): the series rows are locked in a fixed order — so no
    /// number can be taken in between — then the current counters are read, every requested change is checked, and either
    /// all are saved or none. Refused when the prefix or digits are invalid, when the next number would not fit, when any
    /// number the series would still give already exists, when two series of the same documents could give the same number
    /// (Codex H49), or when another kind of document would be numbered alike (owner, 08.10.2026).
    /// </summary>
    public async Task<IActionResult> OnPostSaveNumberSeriesAsync()
    {
        if (!Me.IsAdmin) return Redirect("/");
        // As the admin, so the audit records who changed which series, from what to what.
        var refusal = await Db.AsUserAsync<(string Code, string Problem, string? Other)?>(Me.Id, async tx =>
        {
            var series = (await tx.QueryAsync<Series>("""
                select code as Code, prefix as Prefix, padding as Padding, next_value as NextValue, null as Product
                from core.number_sequence with (updlock, rowlock)
                where code in @codes or code like N'policy:%'
                order by code
                """, new { codes = SettingKeys.SeriesTables.Keys.ToArray() })).ToList();
            var proposed = series.Select(s =>
            {
                var prefix = JsText.ToUpperCase(JsText.Trim(Form($"prefix:{s.Code}") ?? s.Prefix));
                var digits = JsNumber.Parse(JsText.Trim(Form($"digits:{s.Code}") ?? s.Padding.ToString(CultureInfo.InvariantCulture)));
                return new Proposed(s.Code, SeriesTable(s.Code), prefix, digits, s.NextValue, prefix != s.Prefix || digits != s.Padding);
            }).ToList();
            static bool Clash(Proposed a, Proposed b) =>
                a.Prefix == b.Prefix || (a.WholeDigits && b.WholeDigits && Numbering.KindsClash((a.Prefix, a.IntDigits), (b.Prefix, b.IntDigits)));
            foreach (var p in proposed.Where(x => x.Changed))
            {
                var problem = !Numbering.SeriesPrefix().IsMatch(p.Prefix) ? SeriesProblem.Prefix
                    : !p.WholeDigits ? SeriesProblem.Digits
                    : Numbering.CheckSeries(p.Prefix, p.IntDigits, p.Next);
                if (problem is { } pr) return (p.Code, pr.ToSnakeCode(), null);
                // Another kind of document numbered alike: checked against the series as they would be after this save, so
                // two changes in one form cannot meet either.
                var alike = proposed.FirstOrDefault(o => o.Table != p.Table && Clash(p, o));
                if (alike is not null) return (p.Code, "kind", alike.Code);
                // Any document of the same kind already holding a number this series would still give.
                var pattern = p.Prefix + string.Concat(Enumerable.Repeat("[0-9]", p.IntDigits));
                var held = await tx.ExecuteScalarAsync<int>($"""
                    select count(*) from core.{p.Table}
                    where case when datalength(number) = @bytes and number collate Latin1_General_BIN2 like @pattern
                               then case when convert(decimal(38, 0), substring(number, @start, 50)) >= @next then 1 else 0 end
                               else 0 end = 1
                    """, new { bytes = (p.Prefix.Length + p.IntDigits) * 2, pattern, start = p.Prefix.Length + 1, next = (decimal)p.Next });
                if (held > 0) return (p.Code, "taken", null);
                // Another series of the same documents that could give the same number.
                if (proposed.Any(o => o.Code != p.Code && o.Table == p.Table && o.WholeDigits
                        && Numbering.FormatsOverlap(new SeriesFormat(p.Prefix, p.IntDigits, p.Next), new SeriesFormat(o.Prefix, o.IntDigits, o.Next))))
                    return (p.Code, "overlap", null);
            }
            foreach (var p in proposed.Where(x => x.Changed))
                await tx.ExecuteAsync("update core.number_sequence set prefix = @prefix, padding = @padding where code = @code", new { prefix = p.Prefix, padding = p.IntDigits, code = p.Code });
            return null;
        });
        if (refusal is { } r)
        {
            var other = r.Other is not null ? $"&other={Uri.EscapeDataString(r.Other)}" : "";
            return Redirect($"/settings?series={Uri.EscapeDataString(r.Code)}&problem={r.Problem}{other}");
        }
        return Redirect("/settings?series=saved");
    }
}
