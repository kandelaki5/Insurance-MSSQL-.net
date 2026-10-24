using Insurance.Application.Texts;
using Microsoft.Playwright;

namespace Insurance.Web.Tests;

/// <summary>
/// The shared dd/mm/yyyy date field typed, pasted, edited and picked in a real browser (headless Chromium) on the real pages (legacy
/// date-input-typing.test.ts over src/app/date-input.tsx and src/lib/date-text.ts; here Infrastructure/DateInputTagHelper.cs and
/// wwwroot/js/date-input.js). The old test drove the component's handlers with a stand-in element; here the keys, the caret, the
/// browser's validity message and the form are the browser's own. The fields: the new client's birth date (/parties/new: min
/// 01.01.1900, max 31.12.2100, not required) and the rates page's date (/reference/rates: max today, required).
/// </summary>
public sealed class DateInputTypingTests(BrowserApp app) : IClassFixture<BrowserApp>
{
    private sealed record State(string Shown, string Iso, string Message);

    private static async Task<State> StateAsync(IPage page, string id)
    {
        var s = await page.Locator($"#{id}").EvaluateAsync<string[]>(
            "e => { const box = e.closest('.date-input'); return [e.value, box.querySelector('input[type=hidden]').value, e.validationMessage]; }");
        return new State(s[0], s[1], s[2]);
    }

    private static Task<int[]> CaretAsync(ILocator field) => field.EvaluateAsync<int[]>("e => [e.selectionStart, e.selectionEnd]");

    private static Task SetCaretAsync(ILocator field, int from, int to) =>
        field.EvaluateAsync("(e, r) => e.setSelectionRange(r[0], r[1])", new[] { from, to });

    /// <summary>The ISO values the field passed on (its date-change events), recorded from now on.</summary>
    private static Task RecordChangesAsync(IPage page) =>
        page.EvaluateAsync("() => { window.__changes = []; document.addEventListener('date-change', e => window.__changes.push(e.detail)); }");

    private static Task<string[]> ChangesAsync(IPage page) => page.EvaluateAsync<string[]>("() => window.__changes");

    private async Task<(IPage Page, ILocator Field)> BirthDateAsync(string? value = null)
    {
        var page = await app.SignedInPageAsync("sales");
        await page.GotoAsync(app.BaseUrl + "/parties/new");
        var field = page.Locator("#birth_date");
        if (value is not null)
        {
            await field.FillAsync("");
            await field.PressSequentiallyAsync(value);
        }
        return (page, field);
    }

    [Fact]
    public async Task _05012026_typed_digit_by_digit_reads_05_01_2026_and_the_form_gets_2026_01_05()
    {
        var (page, field) = await BirthDateAsync();
        await RecordChangesAsync(page);
        await field.ClickAsync();
        var steps = new List<string>();
        foreach (var digit in "05012026")
        {
            await page.Keyboard.TypeAsync(digit.ToString());
            steps.Add(await field.InputValueAsync());
        }
        Assert.Equal(["0", "05", "05/0", "05/01", "05/01/2", "05/01/20", "05/01/202", "05/01/2026"], steps);
        Assert.Equal(new State("05/01/2026", "2026-01-05", ""), await StateAsync(page, "birth_date"));
        Assert.Equal(new[] { 10, 10 }, await CaretAsync(field));
        // While incomplete the form had nothing and the field said the date is not valid (legacy changes '', …, then the date).
        Assert.Equal(["", "", "", "", "", "", "", "2026-01-05"], await ChangesAsync(page));
        Assert.True(await field.EvaluateAsync<bool>("e => e.checkValidity()"));
    }

    [Fact]
    public async Task A_whole_date_pasted_with_slashes_dots_dashes_or_one_digit_day_and_month_29_February_only_in_a_leap_year()
    {
        var (page, field) = await BirthDateAsync();
        var results = new List<State>();
        foreach (var text in new[] { "14/10/2026", "14.10.2026", "14-10-2026", "5/1/2026", "14102026", "29/02/2028", "29/02/2026" })
        {
            // A paste over the whole text: everything selected, then the text inserted at once (one input event).
            await field.FocusAsync();
            await page.Keyboard.PressAsync("ControlOrMeta+A");
            await page.Keyboard.InsertTextAsync(text);
            results.Add(await StateAsync(page, "birth_date"));
        }
        Assert.Equal(
            [
                new State("14/10/2026", "2026-10-14", ""), new State("14/10/2026", "2026-10-14", ""), new State("14/10/2026", "2026-10-14", ""),
                new State("5/1/2026", "2026-01-05", ""), new State("14/10/2026", "2026-10-14", ""), new State("29/02/2028", "2028-02-29", ""),
                new State("29/02/2026", "", Ka.Common.DateInvalid),
            ], results);
    }

    [Fact]
    public async Task Backspace_from_the_end_one_key_at_a_time_down_to_empty_the_slashes_go_with_their_digits_empty_sends_nothing_and_says_nothing()
    {
        var (page, field) = await BirthDateAsync("14102026");
        await RecordChangesAsync(page);
        await field.FocusAsync();
        await page.Keyboard.PressAsync("End");
        var steps = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            await page.Keyboard.PressAsync("Backspace");
            steps.Add(await field.InputValueAsync());
        }
        Assert.Equal(["14/10/202", "14/10/20", "14/10/2", "14/10/", "14/10", "14/1", "14/", "14", "1", ""], steps);
        Assert.Equal(new State("", "", ""), await StateAsync(page, "birth_date"));
        Assert.Equal("", (await ChangesAsync(page))[^1]);
        Assert.True(await field.EvaluateAsync<bool>("e => e.checkValidity()"));
    }

    [Fact]
    public async Task Changed_in_the_middle_the_month_selected_and_typed_over_and_Delete_before_the_month_the_caret_stays_where_the_person_is()
    {
        var (page, field) = await BirthDateAsync("14102026");
        // 10 selected (positions 3–5) and replaced by "1": 14/1/2026, the caret after the 1.
        await SetCaretAsync(field, 3, 5);
        await page.Keyboard.TypeAsync("1");
        var first = (await StateAsync(page, "birth_date"), await CaretAsync(field));
        await page.Keyboard.TypeAsync("1");
        var second = (await StateAsync(page, "birth_date"), await CaretAsync(field));
        Assert.Equal((new State("14/1/2026", "2026-01-14", ""), "4,4"), (first.Item1, string.Join(",", first.Item2)));
        Assert.Equal((new State("14/11/2026", "2026-11-14", ""), "5,5"), (second.Item1, string.Join(",", second.Item2)));
        // Delete with the caret before the month's first 1: 14/1/2026, January, the caret still at 3.
        await SetCaretAsync(field, 3, 3);
        await page.Keyboard.PressAsync("Delete");
        Assert.Equal(new State("14/1/2026", "2026-01-14", ""), await StateAsync(page, "birth_date"));
        Assert.Equal(new[] { 3, 3 }, await CaretAsync(field));
    }

    [Fact]
    public async Task Backspace_after_the_first_slash_and_Delete_before_it_the_text_stays_14_10_2026_and_the_caret_stays_at_2()
    {
        // Codex PR224-CURSOR-SEPARATOR: the slash taken out is put back by the grouping; the caret goes where the person is, not to the end.
        var (page, field) = await BirthDateAsync("14102026");
        await RecordChangesAsync(page);
        await SetCaretAsync(field, 3, 3);
        await page.Keyboard.PressAsync("Backspace");
        var backspace = (await StateAsync(page, "birth_date"), await CaretAsync(field));
        await SetCaretAsync(field, 2, 2);
        await page.Keyboard.PressAsync("Delete");
        var delete = (await StateAsync(page, "birth_date"), await CaretAsync(field));
        Assert.Equal((new State("14/10/2026", "2026-10-14", ""), "2,2"), (backspace.Item1, string.Join(",", backspace.Item2)));
        Assert.Equal((new State("14/10/2026", "2026-10-14", ""), "2,2"), (delete.Item1, string.Join(",", delete.Item2)));
        Assert.Equal(["2026-10-14", "2026-10-14"], await ChangesAsync(page));
    }

    [Fact]
    public async Task The_calendar_gives_an_ISO_date_shown_as_dd_mm_yyyy_the_same_ISO_sent_and_passed_on()
    {
        var (page, field) = await BirthDateAsync();
        await RecordChangesAsync(page);
        var picker = page.Locator("#birth_date ~ input[type=date]");
        Assert.Equal(("1900-01-01", "2100-12-31", "-1"), (await picker.GetAttributeAsync("min"), await picker.GetAttributeAsync("max"), await picker.GetAttributeAsync("tabindex")));
        // The button opens the browser's own picker (showPicker), recorded here: a headless browser draws no calendar.
        await page.EvaluateAsync("() => { window.__shown = 0; HTMLInputElement.prototype.showPicker = function () { if (this.type === 'date') window.__shown++; }; }");
        await page.Locator("#birth_date ~ button.date-input-button").ClickAsync();
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__shown"));
        // The picker's choice: the browser sets the date field's ISO value and fires change.
        await picker.FillAsync("2026-10-14", new LocatorFillOptions { Force = true });
        Assert.Equal(new State("14/10/2026", "2026-10-14", ""), await StateAsync(page, "birth_date"));
        Assert.Equal("2026-10-14", (await ChangesAsync(page))[^1]);
        // Typed afterwards, the picker follows the text.
        await field.FillAsync("");
        await field.PressSequentiallyAsync("05012026");
        Assert.Equal("2026-01-05", await picker.InputValueAsync());
    }

    [Fact]
    public async Task Min_and_max_a_day_before_says_too_early_after_says_too_late_each_naming_the_bound_as_dd_mm_yyyy()
    {
        var (page, field) = await BirthDateAsync();
        var results = new List<State>();
        foreach (var typed in new[] { "31121899", "01012101", "01011900", "31122100" })
        {
            await field.FillAsync("");
            await field.PressSequentiallyAsync(typed);
            results.Add(await StateAsync(page, "birth_date"));
        }
        // As the old field: the date is still passed on and the field holds the message, which stops the submit in the browser.
        Assert.Equal(
            [
                new State("31/12/1899", "1899-12-31", Ka.Common.DateTooEarly("01/01/1900")), new State("01/01/2101", "2101-01-01", Ka.Common.DateTooLate("31/12/2100")),
                new State("01/01/1900", "1900-01-01", ""), new State("31/12/2100", "2100-12-31", ""),
            ], results);
        // The rates page's max is today: tomorrow is too late, and the form is not sent.
        var rates = await app.SignedInPageAsync("accountant");
        await rates.GotoAsync(app.BaseUrl + "/reference/rates");
        var today = Insurance.Application.Services.Rates.TodayInTbilisi();
        var date = rates.Locator("#date");
        await date.FillAsync("");
        await date.PressSequentiallyAsync(today.AddDays(1).ToString("ddMMyyyy", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Ka.Common.DateTooLate(today.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)), (await StateAsync(rates, "date")).Message);
        Assert.False(await rates.Locator("form.toolbar").EvaluateAsync<bool>("f => f.checkValidity()"));
    }

    [Fact]
    public async Task A_date_the_server_sends_back_replaces_what_is_typed()
    {
        // A refused save (a client without names): 5/1/1985 as typed comes back from the server as 05/01/1985.
        var (page, field) = await BirthDateAsync("5/1/1985");
        Assert.Equal(new State("5/1/1985", "1985-01-05", ""), await StateAsync(page, "birth_date"));
        await page.RunAndWaitForResponseAsync(() => page.Locator("main form button.btn").Last.ClickAsync(), r => r.Request.Method == "POST");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.EndsWith("/parties/new?handler=Save", page.Url);
        Assert.NotEqual(0, await page.Locator(".field-error").CountAsync());
        Assert.Equal(new State("05/01/1985", "1985-01-05", ""), await StateAsync(page, "birth_date"));
        Assert.Equal("1985-01-05", await page.Locator("#birth_date ~ input[type=date]").InputValueAsync());

        // Something half typed, then the page opened again: the server's value, with no message left over.
        var rates = await app.SignedInPageAsync("accountant");
        await rates.GotoAsync(app.BaseUrl + "/reference/rates");
        var today = Insurance.Application.Services.Rates.TodayInTbilisi();
        var server = new State(today.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "");
        Assert.Equal(server, await StateAsync(rates, "date"));
        await rates.Locator("#date").FillAsync("");
        await rates.Locator("#date").PressSequentiallyAsync("1");
        Assert.Equal(new State("1", "", Ka.Common.DateInvalid), await StateAsync(rates, "date"));
        await rates.ReloadAsync();
        Assert.Equal(server, await StateAsync(rates, "date"));
    }

    [Fact]
    public async Task The_name_required_and_disabled_reach_the_fields_as_given()
    {
        // The rates page: the name on the hidden field only, required on the visible one.
        var rates = await app.SignedInPageAsync("accountant");
        await rates.GotoAsync(app.BaseUrl + "/reference/rates");
        var box = rates.Locator("#date").Locator("xpath=..");
        var fields = await box.EvaluateAsync<string[][]>(
            "b => [...b.querySelectorAll('input, button')].map(e => [e.tagName.toLowerCase() + ':' + (e.type || ''), e.getAttribute('name') ?? '', String(e.required === true), String(e.disabled)])");
        Assert.Equal(
            [["input:text", "", "true", "false"], ["input:hidden", "date", "false", "false"], ["input:date", "", "false", "false"], ["button:button", "", "false", "false"]],
            fields);
        // The posted form carries the ISO date under the name.
        Assert.Equal("2026-01-05", await rates.EvaluateAsync<string>("() => { const f = document.querySelector('form.toolbar'); document.querySelector('#date').value = '05/01/2026'; document.querySelector('#date').dispatchEvent(new Event('input')); return new FormData(f).get('date'); }"));

        // A viewer's client card: the birth date shown, every part of the field disabled, the name still on the hidden field — and a
        // disabled field is not sent with its form.
        var sales = await app.SignedInPageAsync("sales");
        await sales.GotoAsync(app.BaseUrl + "/parties/new");
        await sales.FillAsync("#last_name", "თარიღაძე");
        await sales.FillAsync("#first_name", "ნინო");
        await sales.FillAsync("#tax_id", "0" + Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await sales.Locator("#birth_date").PressSequentiallyAsync("12041985");
        await sales.RunAndWaitForResponseAsync(() => sales.Locator("main form button.btn").Last.ClickAsync(), r => r.Request.Method == "POST");
        await sales.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Matches("/parties/[0-9a-f-]{36}", sales.Url);
        var card = new Uri(sales.Url).AbsolutePath;
        var viewer = await app.SignedInPageAsync("viewer");
        await viewer.GotoAsync(app.BaseUrl + card);
        var viewerBox = viewer.Locator("#birth_date").Locator("xpath=..");
        var viewerFields = await viewerBox.EvaluateAsync<string[][]>(
            "b => [...b.querySelectorAll('input, button')].map(e => [e.tagName.toLowerCase() + ':' + (e.type || ''), e.getAttribute('name') ?? '', String(e.disabled), e.value])");
        Assert.Equal(
            [["input:text", "", "true", "12/04/1985"], ["input:hidden", "birth_date", "true", "1985-04-12"], ["input:date", "", "true", "1985-04-12"], ["button:button", "", "true", ""]],
            viewerFields);
        Assert.Null(await viewer.Locator("#birth_date").EvaluateAsync<string?>("e => e.form ? new FormData(e.form).get('birth_date') : null"));
    }
}
