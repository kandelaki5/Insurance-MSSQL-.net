using Insurance.Integration.Tests.Policies;
using Microsoft.Playwright;

namespace Insurance.Web.Tests;

/// <summary>
/// The main work flows in a real browser (headless Chromium) against the real application and a fresh SQL Server test
/// database: forms are filled and pressed as a person would, and each step is checked on the screen and in the rows the
/// database holds. Fictional data only.
/// </summary>
public sealed class BrowserFlowTests(BrowserApp app) : IClassFixture<BrowserApp>
{
    private World W => new(app.Web.Db);
    private static DateOnly Today => Insurance.Application.Services.Rates.TodayInTbilisi();
    private static string Typed(DateOnly d) => d.ToString("ddMMyyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task TypeDateAsync(IPage page, string id, DateOnly date)
    {
        var field = page.Locator($"#{id}");
        await field.FillAsync("");
        await field.PressSequentiallyAsync(Typed(date));
    }

    /// <summary>Presses the button and waits for the page the form leads to.</summary>
    private static async Task SubmitAsync(IPage page, ILocator button)
    {
        // A form the browser would not send (a required field left empty, a date that does not exist) fails here with
        // the fields' names, not as a time-out.
        var invalid = await button.EvaluateAsync<string>("b => [...b.form.elements].filter(e => !e.checkValidity()).map(e => (e.name || e.id) + ': ' + e.validationMessage).join('; ')");
        Assert.True(invalid == "", invalid);
        await page.RunAndWaitForResponseAsync(() => button.ClickAsync(), r => r.Request.Method == "POST");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>The page is at an address of the pattern; otherwise the failure shows what the page said (a refusal).</summary>
    private static async Task AtAsync(IPage page, string pattern)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(page.Url, pattern))
            Assert.Fail($"{page.Url}: {await page.InnerTextAsync("body")}");
    }

    /// <summary>
    /// The workspace's own screens (the home page and the policies list, Codex's Angular shell) read their rows from the page's JSON
    /// handler after the page has loaded. The list counts as drawn when the row of the policy looked for is there and the loading note is
    /// gone — the note alone is also absent before Angular has started (Codex R004-02).
    /// </summary>
    private static async Task LoadedAsync(IPage page, string number)
    {
        await page.Locator("main tr", new() { HasText = number }).First.WaitForAsync();
        await page.WaitForFunctionAsync("() => !document.querySelector('main [role=status]')?.textContent?.includes('იტვირთება')");
    }

    private static string NewTaxId() => "0" + Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Client_policy_approval_payment_and_the_financial_figures()
    {
        // 1. A sales manager registers a client.
        var sales = await app.SignedInPageAsync("sales");
        await sales.GotoAsync(app.BaseUrl + "/parties/new");
        var taxId = NewTaxId();
        await sales.FillAsync("#last_name", "ბრაუზერიძე");
        await sales.FillAsync("#first_name", "ნინო");
        await sales.FillAsync("#tax_id", taxId);
        await TypeDateAsync(sales, "birth_date", new DateOnly(1985, 4, 12));
        await sales.FillAsync("#phone", "599123456");
        await SubmitAsync(sales, sales.Locator("main form button.btn").Last);
        await AtAsync(sales, "/parties/[0-9a-f-]{36}");
        Assert.Contains("ბრაუზერიძე ნინო", await sales.InnerTextAsync("main"));
        Assert.Equal(1, await W.ScalarAsync<int>("select count(*) from core.party where tax_id = @taxId", new { taxId }));

        // The client's risk is assessed on the card (AML law art. 8: no policy for a client without a risk level).
        await SubmitAsync(sales, sales.Locator("button[name=mode][value=accept]"));
        Assert.Equal(1, await W.ScalarAsync<int>("select count(*) from core.party_risk_assessment r join core.party p on p.id = r.party_id where p.tax_id = @taxId", new { taxId }));

        // 2. The same manager issues a CASCO policy for the client: 600 GEL in four quarterly instalments.
        await sales.GotoAsync(app.BaseUrl + "/policies/new");
        await sales.FillAsync("#policyholder_id_number", taxId);
        await sales.FillAsync("#make", "Toyota");
        await sales.FillAsync("#model", "Prius");
        await sales.FillAsync("#year", "2020");
        await sales.FillAsync("#plate", "BR-001-WS");
        await sales.FillAsync("#value", "30000");
        await sales.FillAsync("input[name=cov_damage_limit]", "30000");
        await sales.FillAsync("input[name=cov_damage_premium]", "600");
        await sales.SelectOptionAsync("#installments", "4");
        await SubmitAsync(sales, sales.Locator("main form button.btn").Last);
        await AtAsync(sales, "/policies/[0-9a-f-]{36}\\?issued=1$");
        var policyId = Guid.Parse(new Uri(sales.Url).AbsolutePath[10..46]);
        var number = await W.ScalarAsync<string>("select number from core.policy where id = @policyId", new { policyId });
        Assert.Equal("pending_approval", await W.ScalarAsync<string>("select status from core.policy where id = @policyId", new { policyId }));

        // 3. The head of operations approves it from the pending list; the answer is shown on the list.
        var head = await app.SignedInPageAsync("operations_head");
        await head.GotoAsync(app.BaseUrl + "/policies/pending");
        var row = head.Locator("tr", new() { HasText = number });
        await SubmitAsync(head, row.Locator("button.btn"));
        Assert.Contains(number, await head.InnerTextAsync("main"));
        Assert.Equal("active", await W.ScalarAsync<string>("select status from core.policy where id = @policyId", new { policyId }));
        Assert.Equal("150.00/150.00/150.00/150.00", await W.ScheduleTextAsync(policyId));

        // 4. The accountant records the first instalment and a half of the second.
        var accountant = await app.SignedInPageAsync("accountant");
        await accountant.GotoAsync(app.BaseUrl + $"/policies/{policyId}/payment");
        await accountant.FillAsync("#amount_gel", "300");
        await accountant.FillAsync("#reference", "BANK-BR-1");
        await SubmitAsync(accountant, accountant.Locator("main form:not(.inline-form) button.btn").Last);
        Assert.Equal(300m, await W.ScalarAsync<decimal>("""
            select sum(a.amount_gel) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @policyId
            """, new { policyId }));
        var card = await accountant.InnerTextAsync("main");
        Assert.Contains("300.00", card);

        // 5. The financial figures: the month's premium report shows the premium written this month, the policy's 600.00
        // included (the other tests of this class write into the same database, so the total is read from the ledger).
        var written = await W.ScalarAsync<decimal>(
            "select sum(amount_gel) from core.premium_transaction where booked_on >= @first and booked_on < dateadd(month, 1, @first)",
            new { first = new DateOnly(Today.Year, Today.Month, 1) });
        Assert.True(written >= 600m);
        await accountant.GotoAsync(app.BaseUrl + "/accounting/premium-report");
        var report = await accountant.InnerTextAsync("main");
        Assert.Contains(written.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture), report);
    }

    [Fact]
    public async Task Claim_registration_reserve_decision_and_the_accountants_payment()
    {
        // An active CASCO policy (the policy flow itself is the test above).
        var holder = await W.ClientAsync();
        var policy = await W.ActivePolicyAsync(await W.UserAsync("operations_head"), holder, Today.AddDays(-30));

        // A claims specialist finds the policy by number and registers the claim on the damage cover.
        var claims = await app.SignedInPageAsync("claims");
        await claims.GotoAsync(app.BaseUrl + "/claims/new");
        await claims.FillAsync("#policy", policy.Number);
        await claims.RunAndWaitForResponseAsync(() => claims.ClickAsync("main form.toolbar button"), r => r.Url.Contains("policy=", StringComparison.Ordinal));
        await TypeDateAsync(claims, "occurred_on", Today.AddDays(-2));
        await TypeDateAsync(claims, "reported_on", Today.AddDays(-1));
        await claims.FillAsync("#description", "შეჯახება გზაჯვარედინზე");
        var line = claims.Locator("main form[method=post] tbody tr").First;
        await line.Locator("input[type=checkbox]").CheckAsync();
        await line.Locator("input[name^='claimed:']").FillAsync("1200");
        await line.Locator("input[name^='reserve:']").FillAsync("1000");
        await SubmitAsync(claims, claims.Locator("main form[method=post] button.btn").Last);
        await AtAsync(claims, "/claims/[0-9a-f-]{36}");
        var claimId = Guid.Parse(new Uri(claims.Url).AbsolutePath[8..44]);
        Assert.Equal(1000m, await W.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claimId", new { claimId }));

        // The decision: approved for 800.00; the payment of 800.00 to the policyholder.
        var approve = claims.Locator("form", new() { Has = claims.Locator("input[name=decision][value=approve]") });
        await approve.Locator("input[name=approved_amount]").FillAsync("800");
        await SubmitAsync(claims, approve.Locator("button.btn"));
        Assert.Equal("approved", await W.ScalarAsync<string>("select status from core.claim where id = @claimId", new { claimId }));
        // The reserve follows the decision: it now holds exactly what is still to be paid (legacy decideClaim).
        Assert.Equal(800m, await W.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claimId", new { claimId }));
        // The claims specialist has no payment form; the accountant records the payment (docs/05 ვ2).
        Assert.Equal(0, await claims.Locator("#amount").CountAsync());
        var accountant = await app.SignedInPageAsync("accountant");
        await accountant.GotoAsync(app.BaseUrl + $"/claims/{claimId}");
        await accountant.FillAsync("#amount", "800");
        await SubmitAsync(accountant, accountant.Locator("form", new() { Has = accountant.Locator("#amount") }).Locator("button.btn").Last);
        Assert.Equal(800m, await W.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @claimId", new { claimId }));
        var card = await accountant.InnerTextAsync("main");
        Assert.Contains("800.00", card);
        // The payment released the reserve: nothing is left to pay.
        Assert.Equal(0m, await W.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claimId", new { claimId }));

        // The payment was a mistake: the accountant reverses it on the card. A mirror row is written (the payment itself is
        // never changed) and the reserve comes back; the reversed payment offers no second reversal.
        await accountant.Locator("details summary", new() { HasText = Insurance.Application.Texts.ClaimTexts.Actions.Reverse }).ClickAsync();
        var reverse = accountant.Locator("form[action*='ReverseClaimPayment']");
        await SubmitAsync(accountant, reverse.Locator("button.btn"));
        Assert.Equal(["800.00|payment", "-800.00|reversal"], (await W.QueryAsync<(decimal Amount, string Kind)>(
            "select amount, kind from core.claim_payment where claim_id = @claimId order by created_at", new { claimId })).Select(r => $"{r.Amount:0.00}|{r.Kind}"));
        Assert.Equal(800m, await W.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claimId", new { claimId }));
        Assert.Equal(0, await accountant.Locator("form[action*='ReverseClaimPayment']").CountAsync());
    }

    [Fact]
    public async Task A_change_in_the_originals_open_month_corrects_it_numbered_on_approval_and_the_card_shows_both_premiums()
    {
        // The old program's PRs #207/#218/#225/#211 through the screens: a 400.00 policy of this month, a −80.00 change entered on
        // the change form, approved on the pending list; the original is corrected to 320.00, 4 × 80.00, and keeps its number.
        var holder = await W.ClientAsync();
        var first = new DateOnly(Today.Year, Today.Month, 1);
        var o = await W.PendingPolicyAsync(holder, first, premium: 400m, schedule: [(0, 100m), (31, 100m), (61, 100m), (92, 100m)]);
        Assert.Equal("done", (await Insurance.Application.Services.PolicyActions.ApprovePolicyAsync(W.App, await W.UserAsync("operations_head"), o.Id.ToString())).Outcome);

        var sales = await app.SignedInPageAsync("sales");
        await sales.GotoAsync(app.BaseUrl + $"/policies/{o.Id}/endorse");
        await TypeDateAsync(sales, "change_on", Today);
        await sales.FillAsync("#reason", "შემცირება ბრაუზერიდან");
        await sales.CheckAsync("input[name=cov_damage_on]");
        await sales.FillAsync("input[name=cov_damage_delta]", "-80");
        await SubmitAsync(sales, sales.Locator("main form[method=post] button.btn").Last);
        await AtAsync(sales, "/policies/[0-9a-f-]{36}\\?issued=1$");
        Assert.Contains($"{o.Number}/მოთხოვნა-1", await sales.InnerTextAsync("main"));

        var head = await app.SignedInPageAsync("operations_head");
        await head.GotoAsync(app.BaseUrl + "/policies/pending");
        await SubmitAsync(head, head.Locator("tr", new() { HasText = $"{o.Number}/მოთხოვნა-1" }).Locator("button.btn"));
        Assert.Contains($"{o.Number}/შესწორება-1", await head.InnerTextAsync("main"));
        Assert.Equal("80.00/80.00/80.00/80.00", await W.ScheduleTextAsync(o.Id));

        await head.GotoAsync(app.BaseUrl + $"/policies/{o.Id}");
        var coverage = head.Locator("table", new() { HasText = Insurance.Application.Texts.PolicyTexts.CoverageColumns.CurrentPremium }).Locator("tbody tr").First;
        var cells = await coverage.Locator("td").AllInnerTextsAsync();
        Assert.Equal(["400.00", "400.00", "320.00"], cells.TakeLast(3));
        // The list's rows come late on purpose (as on a slow server, CI run 40014359740): the test reads them once drawn.
        await head.RouteAsync("**/api/v1/policies?*", async route => { await Task.Delay(1500); await route.ContinueAsync(); });
        await head.GotoAsync(app.BaseUrl + $"/policies?q={Uri.EscapeDataString(o.Number)}");
        await LoadedAsync(head, o.Number);
        var listed = await head.InnerTextAsync("main");
        Assert.Contains("320.00 GEL", listed);
        Assert.DoesNotContain($"{o.Number}/შესწორება-1", listed);
    }

    [Fact]
    public async Task A_viewer_sees_every_page_but_no_form_that_writes_and_cannot_post_one()
    {
        var holder = await W.ClientAsync();
        var policy = await W.ActivePolicyAsync(await W.UserAsync("operations_head"), holder, Today.AddDays(-10));
        var viewer = await app.SignedInPageAsync("viewer");

        await viewer.GotoAsync(app.BaseUrl + "/policies");
        await LoadedAsync(viewer, policy.Number);
        Assert.Contains(policy.Number, await viewer.InnerTextAsync("main"));
        Assert.Equal(0, await viewer.Locator("main a[href='/policies/new']").CountAsync());
        await viewer.GotoAsync(app.BaseUrl + $"/policies/{policy.Id}");
        Assert.Contains(policy.Number, await viewer.InnerTextAsync("main"));
        Assert.Equal(0, await viewer.Locator("main form[method=post]").CountAsync());

        // Typed addresses: the new-policy and payment forms send the viewer away; administration is not shown.
        await viewer.GotoAsync(app.BaseUrl + "/policies/new");
        Assert.DoesNotContain("/policies/new", viewer.Url, StringComparison.Ordinal);
        await viewer.GotoAsync(app.BaseUrl + $"/policies/{policy.Id}/payment");
        Assert.Equal(0, await viewer.Locator("#amount_gel").CountAsync());
        var users = await viewer.GotoAsync(app.BaseUrl + "/users");
        Assert.True(users!.Status is 403 or 404 || !viewer.Url.EndsWith("/users", StringComparison.Ordinal), $"{users.Status} {viewer.Url}");

        // A form posted by hand (the viewer's own session and anti-forgery token) writes nothing.
        var before = await W.ScalarAsync<int>("select count(*) from core.payment");
        await viewer.GotoAsync(app.BaseUrl + "/policies");
        await viewer.EvaluateAsync<int>("""
            async ([url, id]) => {
              const token = document.querySelector('input[name=__RequestVerificationToken]')?.value
                ?? (await (await fetch('/policies')).text()).match(/name="__RequestVerificationToken" type="hidden" value="([^"]+)"/)?.[1] ?? '';
              const body = new URLSearchParams({ __RequestVerificationToken: token, policy_id: id, amount_gel: '100', method: 'bank', received_on: new Date().toISOString().slice(0, 10), reference: 'X' });
              const r = await fetch(url, { method: 'POST', body, redirect: 'manual' });
              return r.status;
            }
            """, new object[] { $"/policies/{policy.Id}/payment", policy.Id.ToString() });
        Assert.Equal(before, await W.ScalarAsync<int>("select count(*) from core.payment"));
    }

    [Fact]
    public async Task Excel_downloads_open_as_workbooks_and_the_policy_prints()
    {
        var holder = await W.ClientAsync();
        var policy = await W.ActivePolicyAsync(await W.UserAsync("operations_head"), holder, Today.AddDays(-5));

        // The import template, downloaded by pressing its link.
        var sales = await app.SignedInPageAsync("sales");
        await sales.GotoAsync(app.BaseUrl + "/policies/import");
        var download = await sales.RunAndWaitForDownloadAsync(() => sales.ClickAsync("main a[href$='/policies/import/template']"));
        Assert.EndsWith(".xlsx", download.SuggestedFilename, StringComparison.Ordinal);
        await AssertWorkbookAsync(download);

        // The AML report workbook (a compliance role).
        var accountant = await app.SignedInPageAsync("accountant");
        await accountant.GotoAsync(app.BaseUrl + "/compliance/aml-report");
        var aml = await accountant.RunAndWaitForDownloadAsync(() => accountant.ClickAsync("main a[href*='/compliance/aml-report/xlsx']"));
        await AssertWorkbookAsync(aml);

        // The accountant's report workbooks: premium by policy, and the regulator's form 1 and reserves placement.
        await accountant.GotoAsync(app.BaseUrl + "/accounting/policy-premium");
        await AssertWorkbookAsync(await accountant.RunAndWaitForDownloadAsync(() => accountant.ClickAsync("main a[href^='/accounting/policy-premium/xlsx']")));
        await accountant.GotoAsync(app.BaseUrl + "/accounting/regulatory");
        foreach (var form in new[] { "form1", "reserves" })
            await AssertWorkbookAsync(await accountant.RunAndWaitForDownloadAsync(() => accountant.ClickAsync($"main a[href^='/accounting/regulatory/{form}/xlsx']")));

        // The printed policy: its number, the policyholder, and the print button.
        await sales.GotoAsync(app.BaseUrl + $"/print/policies/{policy.Id}");
        var printed = await sales.InnerTextAsync("body");
        Assert.Contains(policy.Number, printed);
        Assert.Equal(1, await sales.Locator("button[data-print], button.print, .no-print button").CountAsync());
        var pdf = await sales.PdfAsync(new PagePdfOptions { Format = "A4" });
        Assert.True(pdf.Length > 1000 && pdf[0] == (byte)'%' && pdf[1] == (byte)'P', "the print page renders to a PDF");
    }

    private static async Task AssertWorkbookAsync(IDownload download)
    {
        var path = await download.PathAsync();
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length > 100 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', download.SuggestedFilename);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "xl/workbook.xml");
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal));
    }
}
