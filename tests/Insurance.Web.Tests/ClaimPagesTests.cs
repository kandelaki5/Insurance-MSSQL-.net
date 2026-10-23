using System.Net;
using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests;

/// <summary>
/// The claim pages over HTTP on a real database (legacy claims/page.tsx, new/page.tsx, [id]/page.tsx and claims/actions.ts): every
/// flow posts the real form with its anti-forgery token and checks the redirect and what the database holds afterwards. "Today"
/// is the real day in Tbilisi, as on the old pages; every date is counted from it, inside the open month.
/// </summary>
public sealed partial class ClaimPagesTests(WebApp app) : IClassFixture<WebApp>
{
    private readonly ClaimWorld w = new(app.Db);
    private static DateOnly Today => Rates.TodayInTbilisi();
    private static string D(DateOnly d) => ClaimWorld.D(d);
    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private async Task<(HttpClient Client, CurrentUser User)> AsAsync(string role)
    {
        var (client, id) = await app.SignedInAsync(role);
        var name = await w.ScalarAsync<string>("select full_name from core.app_user where id = @id", new { id });
        return (client, new CurrentUser(id, "", name, role, role));
    }

    /// <summary>A GEL policy that started 40 days ago, with damage (deductible 100), equipment (100) and assistance.</summary>
    private async Task<ClaimWorld.Policy> PolicyAsync(decimal premium = 0m, (DateOnly, decimal)[]? installments = null, decimal damageLimit = 20000m)
    {
        var actor = await w.UserAsync("admin");
        return await w.PolicyAsync(actor.Id, Today.AddDays(-40), [new("damage", damageLimit, 100m, premium), new("equipment", 2000m, 100m), new("assistance", 500m)], installments);
    }

    private async Task<Guid> ApprovedClaimAsync(ClaimWorld.Policy p, string amount = "1000", bool totalLoss = false, string coverage = "damage")
    {
        var u = await w.UserAsync("claims");
        var claim = (await w.RegisterAsync(u, p, Today.AddDays(-10), Today.AddDays(-9), (coverage, amount)))[0];
        await w.ApproveAsync(u, claim, amount, Today, totalLoss);
        return claim;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid claim, string handler, Dictionary<string, string> fields) =>
        await WebApp.PostFormAsync(client, $"/claims/{claim}?handler={handler}", fields, pageUrl: $"/claims/{claim}");

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.ToString() ?? $"(no redirect: {(int)r.StatusCode})";

    [Fact]
    public async Task The_list_finds_a_claim_by_its_event_number_and_the_new_form_is_for_writers_only()
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var ev = await w.ScalarAsync<string>("select v.number from core.loss_event v join core.claim c on c.loss_event_id = v.id where c.id = @claim", new { claim });
        var (viewer, _) = await AsAsync("viewer");
        var list = await (await viewer.GetAsync($"/claims?q={ev.ToLowerInvariant()}&status=approved")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"/claims/{claim}\">{ev} · 5.1</a>", list);
        Assert.Contains("1000.00 GEL", list);
        Assert.DoesNotContain("href=\"/claims/new\"", list);
        Assert.Contains(Html(ClaimTexts.Status["approved"]), list);
        Assert.Contains(Ka.Common.NoData, await (await viewer.GetAsync("/claims?q=nothing-like-this")).Content.ReadAsStringAsync());
        var refused = await viewer.GetAsync("/claims/new");
        Assert.Equal("/claims", Location(refused));
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/claims/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/claims/not-a-claim")).StatusCode);
        // A viewer's claim page has no form that writes.
        var page = await (await viewer.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("handler=", page);
    }

    [Fact]
    public async Task Registering_a_loss_event_over_two_coverages_redirects_to_its_first_claim_with_its_reinsurance_share()
    {
        var reinsurer = await w.PartyAsync();
        var treaty = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent)
            values (@treaty, @number, N'კვოტა 25 %', @reinsurer, N'quota_share', @from, @to, N'GEL', 25);
            insert core.ri_treaty_class (treaty_id, class_code) values (@treaty, N'5.1');
            """, new { treaty, number = "QS-" + ClaimWorld.Tag(), reinsurer, from = Today.AddDays(-41), to = Today.AddDays(-39) });
        var p = await PolicyAsync(premium: 400m);
        var (client, user) = await AsAsync("claims");
        var form = await (await client.GetAsync($"/claims/new?policy={p.Number.ToLowerInvariant()}")).Content.ReadAsStringAsync();
        var key = RequestKey().Match(form).Groups[1].Value;
        Assert.True(Guid.TryParse(key, out _));
        var fields = new Dictionary<string, string>
        {
            ["policy_number"] = p.Number, ["request_key"] = key, ["occurred_on"] = D(Today.AddDays(-3)), ["reported_on"] = D(Today), ["occurred_place"] = "თბილისი",
            ["description"] = "ავარია", [$"line:{p.LineOf("damage")}"] = "on", [$"reserve:{p.LineOf("damage")}"] = "3000", [$"claimed:{p.LineOf("damage")}"] = "3500",
            [$"line:{p.LineOf("assistance")}"] = "on", [$"reserve:{p.LineOf("assistance")}"] = "150",
        };
        var response = await WebApp.PostFormAsync(client, $"/claims/new?policy={p.Number}", fields);
        var to = Location(response);
        Assert.Matches("^/claims/[0-9a-f-]{36}\\?registered=1$", to);
        var first = Guid.Parse(to[8..44]);
        var claims = await w.RowsAsync<(Guid Id, string Class, decimal? Claimed, Guid? By)>("select id, (select class_code from core.policy_line where id = policy_line_id), claimed_amount, created_by from core.claim where policy_id = @p order by created_at, number", new { p = p.Id });
        // The claims of one registration may share a time (the clock of the server is coarse), so number breaks the tie: N, N-2.
        Assert.Equal([(first, "5.1", (decimal?)3500m, (Guid?)user.Id), (claims[1].Id, "14", null, user.Id)], claims);
        Assert.Equal([(treaty, 25m)], await w.RowsAsync<(Guid, decimal)>("select treaty_id, share_percent from core.claim_cession where claim_id = @first", new { first }));
        var page = await (await client.GetAsync(to)).Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.Registered), page);
        Assert.Contains(Html(ClaimTexts.Sections.EventCount(2)), page);
        Assert.Contains("750.00 GEL", page);
        // The same form sent again opens the same claim; nothing is registered twice.
        Assert.Equal(to, Location(await WebApp.PostFormAsync(client, $"/claims/new?policy={p.Number}", fields)));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.loss_event where policy_id = @p", new { p = p.Id }));
    }

    [Fact]
    public async Task A_cancelled_policy_refuses_a_loss_on_or_after_its_cancel_day_with_the_form_shown_again()
    {
        var p = await PolicyAsync(premium: 365m);
        var actor = await w.UserAsync("admin");
        await w.App.AsUserAsync(actor.Id, tx => PolicyCancel.CancelPolicyInTxAsync(tx, p.Id, Today.AddDays(-5), "გაუქმება", actor.Id));
        var (client, _) = await AsAsync("claims");
        var response = await WebApp.PostFormAsync(client, $"/claims/new?policy={p.Number}", new()
        {
            ["policy_number"] = p.Number, ["occurred_on"] = D(Today.AddDays(-2)), ["reported_on"] = D(Today), ["description"] = "ავარია ✓",
            [$"line:{p.LineOf("damage")}"] = "on", [$"reserve:{p.LineOf("damage")}"] = "1000",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.Errors.OccurredOn), html);
        Assert.Contains("value=\"ავარია ✓\"", html);
        Assert.Contains($"id=\"line:{p.LineOf("damage")}\" name=\"line:{p.LineOf("damage")}\" type=\"checkbox\" checked=\"checked\"", html);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim where policy_id = @p", new { p = p.Id }));
        // Before the cancel day it is accepted, reported late.
        Assert.EndsWith("?registered=1", Location(await WebApp.PostFormAsync(client, $"/claims/new?policy={p.Number}", new()
        {
            ["policy_number"] = p.Number, ["occurred_on"] = D(Today.AddDays(-6)), ["reported_on"] = D(Today), ["description"] = "ავარია",
            [$"line:{p.LineOf("damage")}"] = "on", [$"reserve:{p.LineOf("damage")}"] = "1000",
        })));
    }

    [Fact]
    public async Task A_coverage_added_to_the_event_joins_it_and_a_refusal_shows_on_its_own_form()
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var (client, _) = await AsAsync("claims");
        var refused = await PostAsync(client, claim, "AddCoverage", new() { ["claim_id"] = claim.ToString(), ["policy_line_id"] = p.LineOf("equipment").ToString(),
            ["reported_on"] = D(Today), ["initial_reserve"] = "5000" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var html = await refused.Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.Errors.Reserve), html);
        Assert.Contains("value=\"5000\"", html);
        var added = Location(await PostAsync(client, claim, "AddCoverage", new() { ["claim_id"] = claim.ToString(), ["policy_line_id"] = p.LineOf("equipment").ToString(),
            ["reported_on"] = D(Today), ["initial_reserve"] = "500" }));
        Assert.EndsWith("?registered=1", added);
        Assert.Equal(2, (await w.ClaimsOfEventAsync(claim)).Count);
    }

    [Fact]
    public async Task A_reserve_request_is_approved_by_another_user_and_refused_for_the_one_who_asked()
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var (asker, askerUser) = await AsAsync("admin");
        Assert.EndsWith("?reserve=requested", Location(await PostAsync(asker, claim, "ChangeReserve", new()
        {
            ["claim_id"] = claim.ToString(), ["booked_on"] = D(Today), ["new_level"] = "1200.50", ["reason"] = "ხარჯთაღრიცხვა გაიზარდა",
        })));
        var request = await w.ScalarAsync<Guid>("select id from core.claim_reserve_request where claim_id = @claim", new { claim });
        Assert.Equal(1000m, await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claim", new { claim }));
        var page = await (await asker.GetAsync($"/claims/{claim}?reserve=requested")).Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.ReserveRequests.Own), page);
        Assert.Contains(Html(ClaimTexts.ReserveOutcomes["requested"]), page);
        var fields = new Dictionary<string, string> { ["claim_id"] = claim.ToString(), ["request_id"] = request.ToString() };
        Assert.EndsWith("?reserve=own", Location(await PostAsync(asker, claim, "ApproveReserveRequest", fields)));
        var (accountant, _) = await AsAsync("accountant");
        Assert.EndsWith("?reserve=approve-not-allowed", Location(await PostAsync(accountant, claim, "ApproveReserveRequest", fields)));
        var (approver, approverUser) = await AsAsync("admin");
        Assert.Contains("handler=ApproveReserveRequest", await (await approver.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync());
        Assert.EndsWith("?reserve=approved", Location(await PostAsync(approver, claim, "ApproveReserveRequest", fields)));
        Assert.Equal([(Today, 200.50m, (Guid?)approverUser.Id, (Guid?)request)], await w.RowsAsync<(DateOnly, decimal, Guid?, Guid?)>(
            "select booked_on, change, created_by, request_id from core.claim_reserve where claim_id = @claim and request_id is not null", new { claim }));
        Assert.Contains(Html(ClaimTexts.ReserveRequests.Approved(approverUser.FullName)), await (await asker.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync());
        _ = askerUser;
    }

    [Fact]
    public async Task The_adjusters_decision_approves_with_the_reserve_following_and_keeps_the_query_of_the_page()
    {
        var p = await PolicyAsync();
        var u = await w.UserAsync("claims");
        var claim = (await w.RegisterAsync(u, p, Today.AddDays(-10), Today.AddDays(-9), ("damage", "800")))[0];
        var (client, _) = await AsAsync("claims");
        var review = await WebApp.PostFormAsync(client, $"/claims/{claim}?registered=1&handler=Decide", new() { ["claim_id"] = claim.ToString(), ["decision"] = "review" }, $"/claims/{claim}?registered=1");
        Assert.Equal($"/claims/{claim}?registered=1", Location(review));
        Assert.Equal($"/claims/{claim}", Location(await PostAsync(client, claim, "Decide", new() { ["claim_id"] = claim.ToString(), ["decision"] = "approve", ["approved_amount"] = "900" })));
        Assert.Equal(("approved", 900m, 100m), (await w.RowsAsync<(string, decimal, decimal)>("select status, approved_amount, deductible_amount from core.claim where id = @claim", new { claim })).Single());
        Assert.Equal(900m, await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claim", new { claim }));
        var (viewer, _) = await AsAsync("viewer");
        await PostAsync(viewer, claim, "Decide", new() { ["claim_id"] = claim.ToString(), ["decision"] = "close" });
        Assert.Equal("approved", await w.ScalarAsync<string>("select status from core.claim where id = @claim", new { claim }));
    }

    [Fact]
    public async Task Partial_payments_withhold_the_deductible_once_and_a_refused_payment_shows_the_form_as_typed()
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var (client, user) = await AsAsync("accountant");
        var page = await (await client.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"amount\" type=\"number\" step=\"0.01\" min=\"0\" value=\"1000.00\"", page);
        Dictionary<string, string> Pay(string amount) => new() { ["claim_id"] = claim.ToString(), ["amount"] = amount, ["paid_on"] = D(Today), ["payee_kind"] = "policyholder", ["note"] = "ნაწილი" };
        Assert.Equal($"/claims/{claim}?paid=1", Location(await PostAsync(client, claim, "PayClaim", Pay("250.00"))));
        Assert.Equal($"/claims/{claim}?paid=1", Location(await PostAsync(client, claim, "PayClaim", Pay("250.00"))));
        Assert.Equal([(250m, 100m, 150m, (Guid?)user.Id), (250m, 0m, 250m, user.Id)], await w.RowsAsync<(decimal, decimal, decimal, Guid?)>(
            "select amount, deductible_withheld, net_paid, created_by from core.claim_payment where claim_id = @claim order by created_at", new { claim }));
        var refused = await PostAsync(client, claim, "PayClaim", Pay("600"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var html = await refused.Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.Errors.Exceeds), html);
        Assert.Contains("value=\"600\"", html);
        var shown = await (await client.GetAsync($"/claims/{claim}?paid=1")).Content.ReadAsStringAsync();
        Assert.Contains(Html($"{ClaimTexts.Actions.Pay}: {Ka.Party.Saved}"), shown);
        Assert.Contains("<td class=\"num\">250.00</td>", shown);
        Assert.Equal(500m, await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claim", new { claim }));
    }

    [Fact]
    public async Task The_premium_set_off_takes_the_unpaid_instalment_through_an_internal_receipt()
    {
        var p = await PolicyAsync(premium: 90m, installments: [(Today.AddDays(-40), 90m)]);
        var claim = await ApprovedClaimAsync(p);
        var (client, _) = await AsAsync("accountant");
        Assert.EndsWith("?paid=1", Location(await PostAsync(client, claim, "PayClaim", new()
        {
            ["claim_id"] = claim.ToString(), ["amount"] = "250", ["paid_on"] = D(Today), ["payee_kind"] = "policyholder", ["offset_premium"] = "on",
        })));
        Assert.Equal([(250m, 100m, 90m, 60m)], await w.RowsAsync<(decimal, decimal, decimal, decimal)>(
            "select amount, deductible_withheld, premium_offset, net_paid from core.claim_payment where claim_id = @claim", new { claim }));
        Assert.Equal(("claim_offset", 90m, ClaimTexts.Fields.PremiumOffset), (await w.RowsAsync<(string, decimal, string)>(
            "select method, amount_gel, reference from core.payment where payer_party_id = @party", new { party = p.PartyId })).Single());
    }

    [Theory]
    [InlineData("earned_unpaid")]
    [InlineData("all_unpaid")]
    [InlineData("none")]
    public async Task A_total_loss_is_settled_by_the_chosen_rule(string mode)
    {
        // 365 days, 365.00 premium in one instalment, nothing paid: the loss 10 days ago leaves 30.00 earned (40 − 10 days).
        var actor = await w.UserAsync("admin");
        var p = await w.PolicyAsync(actor.Id, Today.AddDays(-40), [new("damage", 20000m, 0m, 365m)], [(Today.AddDays(-40), 365m)], days: 365);
        var claim = await ApprovedClaimAsync(p, totalLoss: true);
        var (client, _) = await AsAsync("accountant");
        var page = await (await client.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"total_loss_mode\"", page);
        Assert.DoesNotContain("name=\"offset_premium\"", page);
        Assert.EndsWith("?paid=1", Location(await PostAsync(client, claim, "PayClaim", new()
        {
            ["claim_id"] = claim.ToString(), ["amount"] = "1000", ["paid_on"] = D(Today), ["payee_kind"] = "policyholder", ["total_loss_mode"] = mode,
        })));
        var (offset, net) = (await w.RowsAsync<(decimal, decimal)>("select premium_offset, net_paid from core.claim_payment where claim_id = @claim", new { claim })).Single();
        var (status, cancelledOn) = (await w.RowsAsync<(string, DateOnly?)>("select status, cancelled_on from core.policy where id = @id", new { id = p.Id })).Single();
        var expected = mode switch
        {
            "earned_unpaid" => (30m, 970m, "cancelled", (DateOnly?)Today.AddDays(-10)),
            "all_unpaid" => (365m, 635m, "active", null),
            _ => (0m, 1000m, "cancelled", Today.AddDays(-10)),
        };
        Assert.Equal(expected, (offset, net, status, cancelledOn));
    }

    [Fact]
    public async Task A_payment_above_the_threshold_becomes_a_request_that_the_financial_director_authorises_on_the_claim_page()
    {
        var p = await PolicyAsync(damageLimit: 50000m);
        var claim = await ApprovedClaimAsync(p, "20000");
        var (accountant, _) = await AsAsync("accountant");
        Assert.Equal($"/claims/{claim}?payment=requested", Location(await PostAsync(accountant, claim, "PayClaim", new()
        {
            ["claim_id"] = claim.ToString(), ["amount"] = "15000", ["paid_on"] = D(Today), ["payee_kind"] = "policyholder", ["note"] = "დიდი",
        })));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @claim", new { claim }));
        var request = await w.ScalarAsync<Guid>("select id from core.claim_payment_request where claim_id = @claim", new { claim });
        var pending = await (await accountant.GetAsync($"/claims/{claim}?payment=requested")).Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.PaymentRequests.Pending), pending);
        Assert.Contains(Html(ClaimTexts.PaymentOutcomes["requested"]), pending);
        var fields = new Dictionary<string, string> { ["claim_id"] = claim.ToString(), ["request_id"] = request.ToString() };
        Assert.Equal($"/claims/{claim}?payment=not-allowed", Location(await PostAsync(accountant, claim, "ApproveClaimPaymentRequest", fields)));
        var (director, directorUser) = await AsAsync("admin");
        Assert.Contains("handler=ApproveClaimPaymentRequest", await (await director.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync());
        Assert.Equal($"/claims/{claim}?payment=approved", Location(await PostAsync(director, claim, "ApproveClaimPaymentRequest", fields)));
        Assert.Equal([(15000m, 14900m, (Guid?)request, (Guid?)directorUser.Id)], await w.RowsAsync<(decimal, decimal, Guid?, Guid?)>(
            "select amount, net_paid_gel, request_id, created_by from core.claim_payment where claim_id = @claim", new { claim }));
        Assert.Equal($"/claims/{claim}?payment=already", Location(await PostAsync(director, claim, "ApproveClaimPaymentRequest", fields)));
        Assert.Contains(Html(ClaimTexts.PaymentRequests.Approved(directorUser.FullName)), await (await accountant.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("correction")]
    [InlineData("returned")]
    public async Task A_payment_is_reversed_by_its_mirror_as_a_correction_or_as_money_returned(string reason)
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var (client, user) = await AsAsync("accountant");
        await PostAsync(client, claim, "PayClaim", new() { ["claim_id"] = claim.ToString(), ["amount"] = "250", ["paid_on"] = D(Today), ["payee_kind"] = "policyholder" });
        var payment = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @claim", new { claim });
        Assert.Contains("handler=ReverseClaimPayment", await (await client.GetAsync($"/claims/{claim}")).Content.ReadAsStringAsync());
        var fields = new Dictionary<string, string>
        {
            ["claim_id"] = claim.ToString(), ["claim_payment_id"] = payment.ToString(), ["reason"] = reason, ["reversed_on"] = D(Today), ["note"] = "შეცდომით",
            ["returned_on"] = reason == "returned" ? D(Today) : "", ["bank_reference"] = reason == "returned" ? "TBC-17" : "",
        };
        Assert.Equal($"/claims/{claim}?reversal=ok", Location(await PostAsync(client, claim, "ReverseClaimPayment", fields)));
        Assert.Equal([(-250m, -100m, -150m, "reversal", (Guid?)payment, reason, reason == "returned" ? "TBC-17" : null, (Guid?)user.Id)],
            await w.RowsAsync<(decimal, decimal, decimal, string, Guid?, string, string?, Guid?)>("""
                select amount, deductible_withheld, net_paid, kind, reverses_claim_payment_id, reversal_reason, bank_reference, created_by
                from core.claim_payment where claim_id = @claim and kind = N'reversal'
                """, new { claim }));
        var page = await (await client.GetAsync($"/claims/{claim}?reversal=ok")).Content.ReadAsStringAsync();
        Assert.Contains(Html(ClaimTexts.ReversalOutcomes["ok"]), page);
        Assert.Contains(Html(ClaimTexts.Fields.ReversedBy(Today.ToString("dd.MM.yyyy"))), page);
        Assert.Contains(Html(ClaimTexts.Fields.ReversalReasons[reason]), page);
        Assert.DoesNotContain("handler=ReverseClaimPayment", page);
        Assert.Equal($"/claims/{claim}?reversal=already", Location(await PostAsync(client, claim, "ReverseClaimPayment", fields)));
        var (viewer, _) = await AsAsync("viewer");
        Assert.Equal($"/claims/{claim}?reversal=not-allowed", Location(await PostAsync(viewer, claim, "ReverseClaimPayment", fields)));
    }

    [Fact]
    public async Task Salvage_and_regress_are_recorded_and_a_viewer_is_refused()
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var (client, user) = await AsAsync("accountant");
        Dictionary<string, string> Recovery(string kind, string amount) => new() { ["claim_id"] = claim.ToString(), ["kind"] = kind, ["received_on"] = D(Today), ["amount_gel"] = amount, ["note"] = kind };
        Assert.Equal($"/claims/{claim}", Location(await PostAsync(client, claim, "AddRecovery", Recovery("salvage", "50.00"))));
        Assert.Equal($"/claims/{claim}", Location(await PostAsync(client, claim, "AddRecovery", Recovery("subrogation", "25"))));
        Assert.Equal($"/claims/{claim}", Location(await PostAsync(client, claim, "AddRecovery", Recovery("salvage", "0.004"))));
        Assert.Equal([("salvage", 50m, (Guid?)user.Id), ("subrogation", 25m, user.Id)], await w.RowsAsync<(string, decimal, Guid?)>(
            "select kind, amount_gel, created_by from core.claim_recovery where claim_id = @claim order by created_at", new { claim }));
        var (viewer, _) = await AsAsync("viewer");
        var refused = await PostAsync(viewer, claim, "AddRecovery", Recovery("salvage", "10"));
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", Location(refused));
        Assert.Contains(Html(ClaimTexts.RecoveryNotAllowed), await (await viewer.GetAsync(Location(refused))).Content.ReadAsStringAsync());
        Assert.Equal(2, await w.ScalarAsync<int>("select count(*) from core.claim_recovery where claim_id = @claim", new { claim }));
    }

    [Fact]
    public async Task A_viewer_posting_the_payment_reserve_or_registration_forms_directly_is_refused_and_nothing_is_written()
    {
        var p = await PolicyAsync();
        var claim = await ApprovedClaimAsync(p);
        var (viewer, _) = await AsAsync("viewer");
        var response = await PostAsync(viewer, claim, "PayClaim", new() { ["claim_id"] = claim.ToString(), ["amount"] = "250", ["paid_on"] = D(Today), ["payee_kind"] = "policyholder" });
        // The refusal is the payment form's own message (legacy pay-form.tsx kept it on the form it was sent from); the page comes
        // back without a payment form for a viewer, and the refusal is still given above where the form would be
        // (docs/legacy-tests-gaps-a.md, D2: a form sent after its permission was taken away).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shown = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("handler=PayClaim", shown);
        Assert.Contains(Html(ClaimTexts.PaymentNotAllowed), shown);
        Assert.EndsWith("?reserve=not-allowed", Location(await PostAsync(viewer, claim, "ChangeReserve", new()
        {
            ["claim_id"] = claim.ToString(), ["booked_on"] = D(Today), ["new_level"] = "1", ["reason"] = "x",
        })));
        var registration = await WebApp.PostFormAsync(viewer, $"/claims/new?policy={p.Number}", new()
        {
            ["policy_number"] = p.Number, ["occurred_on"] = D(Today.AddDays(-2)), ["reported_on"] = D(Today), ["description"] = "x",
            [$"line:{p.LineOf("damage")}"] = "on", [$"reserve:{p.LineOf("damage")}"] = "10",
        }, pageUrl: "/claims");
        Assert.Equal("/claims", Location(registration));
        Assert.Equal((0, 0, 1), (await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @claim", new { claim }),
            await w.ScalarAsync<int>("select count(*) from core.claim_reserve_request where claim_id = @claim", new { claim }),
            await w.ScalarAsync<int>("select count(*) from core.claim where policy_id = @p", new { p = p.Id })));
    }

    [GeneratedRegex("name=\"request_key\" value=\"([^\"]+)\"")]
    private static partial Regex RequestKey();
}
