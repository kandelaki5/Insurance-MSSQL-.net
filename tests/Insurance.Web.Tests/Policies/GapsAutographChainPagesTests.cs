using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// Legacy autograph-permissioned-chain.test.ts on its own days and with its pages, over HTTP on a database of its own (the
/// chain of IT/Policies/GapsAutographChainTests, there relative to today, here on the legacy calendar through the database's
/// clock): Q of 120.00 from 01.10.2026 paid 05.10, October closed by the real close; "today" 03.11.2026. The underwriter issues P
/// from 01.11; the admin's panel actions give a group policy.approve, premium.receipt.create and accounting.period.close, put the
/// viewer in it and remove the receipt from the viewer personally — the permission panel shows where each comes from; the pending
/// list and the card offer the viewer P's approval (each a form naming only P), the viewer approves on the pending page; the
/// receipt is refused, then after inherit received once (the panel shows the group again; the card shows the receipt's day and its
/// allocation's); November by hand; the close not ended on 03.11, and on 03.12 the periods page offers November alone and the
/// viewer closes it there; closing writes nothing financial, October as reported.
/// </summary>
public sealed partial class GapsAutographChainPagesTests
{
    [GeneratedRegex(@"<form\b[^>]*>([\s\S]*?)</form>")] private static partial Regex Forms();
    [GeneratedRegex(@"<input\b([^>]*)>")] private static partial Regex Inputs();

    private static string? Attr(string attrs, string name) => Regex.Match(attrs, $@"\b{name}=""([^""]*)""") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    /// <summary>The forms on a page whose only field (besides the anti-forgery token) is <paramref name="name"/>: the value each names (legacy soleForms).</summary>
    private static List<string?> SoleForms(string html, string name) => Forms().Matches(html)
        .Select(f => Inputs().Matches(f.Groups[1].Value).Select(i => (Name: Attr(i.Groups[1].Value, "name"), Value: Attr(i.Groups[1].Value, "value")))
            .Where(x => x.Name != "__RequestVerificationToken").ToList())
        .Where(fields => fields.Count == 1 && fields[0].Name == name).Select(fields => fields[0].Value).ToList();

    /// <summary>What the panel shows for the chosen user on one permission: [result, source] (legacy panelRow).</summary>
    private static string[] PanelRow(string html, string key)
    {
        var section = Regex.Match(html, @"<section[^>]*\bid=""chosen-user""[^>]*>[\s\S]*?</section>").Value;
        var row = Regex.Match(section, $@"<tr[^>]*\bdata-permission=""{Regex.Escape(key)}""[^>]*>([\s\S]*?)</tr>").Groups[1].Value;
        string Cell(string name) => Html.Text(Regex.Match(row, $@"<td[^>]*data-cell=""{name}""[^>]*>([\s\S]*?)</td>").Groups[1].Value);
        return [Cell("result"), Cell("source")];
    }

    private static async Task<string> PageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task On_the_legacy_days_issue_the_admins_group_the_viewer_approves_on_the_pending_page_receives_once_after_inherit_closes_November_on_03_12_on_the_periods_page()
    {
        var site = new PolicyWebApp();
        await site.InitializeAsync();
        try
        {
            var w = site.W;
            var october = D(2026, 10, 1);
            var november = D(2026, 11, 1);
            On(w, D(2026, 11, 3));
            var (adminClient, adminId) = await site.SignedInAsync("admin");
            var admin = await site.UserOfAsync(adminId);
            var underwriter = await w.UserAsync("underwriter");
            var (viewerClient, viewerId) = await site.SignedInAsync("viewer");
            var viewer = await site.UserOfAsync(viewerId);
            var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var holder = await w.ClientAsync(taxId);

            // Q: 120.00 from 01.10, four instalments of 30.00 paid on 05.10; October closed by the real close on 03.11.
            var q = await w.ActivePolicyAsync(admin, holder, october, premium: 120m);
            var qReceipt = await w.ReceiptAsync(holder, D(2026, 10, 5), 120m);
            await using (var c = await w.Database.OpenAppAsync())
                foreach (var i in await w.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = q.Id }))
                    await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@qReceipt, @i, 30, 30, 1, '2026-10-05')", new { qReceipt, i });
            Assert.Equal(new PeriodClose.Answer("done", "2026-10-01"), await PeriodClose.CloseAsync(w.App, admin, "2026-10-01"));
            var octoberReport = await MonthReportAsync(w, october);

            // 1. The underwriter issues P by the real form: pending, 300.00 + 100.00 from 01.11, four instalments of 100.00.
            var (product, _) = await w.CascoAsync();
            var issued = await PolicyActions.IssuePolicyAsync(w.App, w.Rates, underwriter, World.Form(("product_id", product.ToString()), ("policyholder_id_number", taxId),
                ("make", "ტესტი"), ("model", "პილოტი"), ("plate", "TE-500-AG"), ("value", "30000"), ("currency", "GEL"), ("effective_from", "2026-11-01"), ("effective_to", "2027-11-01"),
                ("cov_damage_premium", "300"), ("cov_damage_limit", "30000"), ("cov_damage_deductible", "0"), ("cov_theft_premium", "100"), ("cov_theft_limit", "30000"),
                ("cov_theft_deductible", "0"), ("installments", "4")), code => code is "damage" or "theft");
            var p = await w.ScalarAsync<Guid>("select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder and p.id <> @q", new { holder, q = q.Id });
            Assert.Equal($"/policies/{p}?issued=1", issued.Redirect);
            Assert.Equal([(november, 100m), (D(2027, 2, 1), 100m), (D(2027, 5, 1), 100m), (D(2027, 8, 1), 100m)],
                await w.QueryAsync<(DateOnly, decimal)>("select due_on, amount from core.installment where policy_id = @p order by due_on", new { p }));
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @p", new { p }));

            // 2. The admin's panel actions; the panel page shows where each permission comes from.
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.CreateGroupAsync(tx, "პილოტი"))).Ok);
            var group = await w.ScalarAsync<Guid>("select id from core.user_group where name = N'პილოტი'");
            foreach (var key in new[] { "policy.approve", "premium.receipt.create", PeriodClose.Permission })
                Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetGroupGrantAsync(tx, group.ToString(), key, "true"))).Ok);
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetGroupMemberAsync(tx, group.ToString(), viewer.Id.ToString(), "true"))).Ok);
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetUserDecisionAsync(tx, viewer.Id.ToString(), "premium.receipt.create", "deny"))).Ok);
            var panel = await PageAsync(adminClient, $"/users/permissions?user={viewer.Id}");
            string[] Shown(string allowed, string source) => [allowed, PermissionTexts.Sources[source]];
            Assert.Equal([Shown(PermissionTexts.Panel.Allowed, "group"), Shown(PermissionTexts.Panel.Refused, "revoked"), Shown(PermissionTexts.Panel.Allowed, "group"),
                Shown(PermissionTexts.Panel.Refused, "not-in-role"), Shown(PermissionTexts.Panel.Refused, "not-in-role")],
                new[] { "policy.approve", "premium.receipt.create", PeriodClose.Permission, "claim.payment.record", "claim.recovery.receive" }.Select(k => PanelRow(panel, k)));

            // 3. The pending list and the card offer the viewer P's approval; the viewer approves on the pending page: 300.00 + 100.00
            // booked 01.11 by the viewer; pressed again: already, nothing more.
            Assert.Equal([p.ToString()], SoleForms(await PageAsync(viewerClient, "/policies/pending"), "id"));
            Assert.Equal([p.ToString()], SoleForms(await PageAsync(viewerClient, $"/policies/{p}"), "id"));
            var approved = await WebApp.PostFormAsync(viewerClient, "/policies/pending?handler=Approve", new() { ["id"] = p.ToString() }, "/policies/pending");
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            Task<List<(decimal, string, DateOnly, DateOnly, DateOnly, Guid)>> Booked() => w.QueryAsync<(decimal, string, DateOnly, DateOnly, DateOnly, Guid)>("""
                select t.amount, t.kind, t.booked_on, t.cover_from, t.cover_to, t.created_by from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                where l.policy_id = @p order by t.amount desc
                """, new { p });
            var booked = await Booked();
            Assert.Equal([(300m, "issue", november, november, D(2027, 11, 1), viewer.Id), (100m, "issue", november, november, D(2027, 11, 1), viewer.Id)], booked);
            Assert.Equal([("active", viewer.Id, underwriter.Id)], await w.QueryAsync<(string, Guid, Guid)>("select status, approved_by, created_by from core.policy where id = @p", new { p }));
            Assert.Empty(SoleForms(await PageAsync(viewerClient, "/policies/pending"), "id"));
            Assert.Equal("already", (await PolicyActions.ApprovePolicyAsync(w.App, viewer, p.ToString())).Outcome);
            Assert.Equal(booked, await Booked());

            // 4. The receipt (received 02.11): refused while the viewer's own removal stands; inherit by the admin's panel action — the
            // panel shows the group again; the same form, once, allocated on the day it is made (03.11).
            var ledger = await LedgerAsync(w);
            Assert.Equal(BillingTexts.Payment.NotAllowed, (await w.PayAsync(viewer, p, "100", D(2026, 11, 2), reference: "R-1102")).Message);
            Assert.Equal(ledger, await LedgerAsync(w));
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetUserDecisionAsync(tx, viewer.Id.ToString(), "premium.receipt.create", "inherit"))).Ok);
            Assert.Equal(Shown(PermissionTexts.Panel.Allowed, "group"), PanelRow(await PageAsync(adminClient, $"/users/permissions?user={viewer.Id}"), "premium.receipt.create"));
            Assert.Equal($"/policies/{p}?paid=0", (await w.PayAsync(viewer, p, "100", D(2026, 11, 2), reference: "R-1102")).Redirect);
            Assert.Equal([(D(2026, 11, 2), 100m, "bank", "receipt", holder, viewer.Id)], await w.QueryAsync<(DateOnly, decimal, string, string, Guid, Guid)>(
                "select received_on, amount_gel, method, kind, payer_party_id, created_by from core.payment where reference = N'R-1102'"));
            Assert.Equal([(november, 100m, 100m, 1m, D(2026, 11, 3))], await w.QueryAsync<(DateOnly, decimal, decimal, decimal, DateOnly)>("""
                select i.due_on, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.payment r on r.id = a.payment_id
                join core.installment i on i.id = a.installment_id where r.reference = N'R-1102'
                """));
            var card = Html.Text(await PageAsync(viewerClient, $"/policies/{p}"));
            Assert.Contains("02.11.2026", card);
            Assert.Contains("03.11.2026", card);

            // 5. November by hand (tetri): earned P 300 × 30 / 365 = 24.66 and 100 × 30 / 365 = 8.22, Q 120 × 30 / 365 = 9.86 — 42.74;
            // unearned at 30.11 P 367.12, Q 120 − 120 × 61 / 365 = 99.95 — 467.07; owed P's 300.00 left, none overdue.
            var month = await MonthReportAsync(w, november);
            var c51 = Assert.Single(month.Classes);
            Assert.Equal((1, new BigInteger(40000), new BigInteger(40000), new BigInteger(4274), new BigInteger(46707)),
                (c51.PoliciesIssued, c51.StatisticalWrittenGel, c51.FinancialWrittenGel, c51.EarnedGel, c51.UnearnedGel));
            Assert.Equal((new BigInteger(30000), BigInteger.Zero), (month.Receivables.Single().Outstanding, month.Receivables.Single().Overdue));

            // 6. The close: November not ended on 03.11, nothing written. On 03.12 the periods page offers November alone; the viewer
            // closes it there; again: already.
            var periods = await w.ScalarAsync<int>("select count(*) from core.accounting_period");
            Assert.Equal(new PeriodClose.Answer("not-ended", "2026-11-01"), await PeriodClose.CloseAsync(w.App, viewer, "2026-11-01"));
            Assert.Equal(periods, await w.ScalarAsync<int>("select count(*) from core.accounting_period"));
            On(w, D(2026, 12, 3));
            Assert.Equal(["2026-11-01"], SoleForms(await PageAsync(viewerClient, "/accounting/periods"), "period_start"));
            var closed = await WebApp.PostFormAsync(viewerClient, "/accounting/periods?handler=Close", new() { ["period_start"] = "2026-11-01" }, "/accounting/periods");
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            Assert.Equal([("closed", viewer.Id)], await w.QueryAsync<(string, Guid)>("select status, closed_by from core.accounting_period where period_start = @november", new { november }));
            Assert.Empty(SoleForms(await PageAsync(viewerClient, "/accounting/periods"), "period_start"));
            Assert.Equal(new PeriodClose.Answer("already", "2026-11-01"), await PeriodClose.CloseAsync(w.App, viewer, "2026-11-01"));
            // Closing writes nothing financial: November's report as before; October's as reported.
            var after = await MonthReportAsync(w, november);
            Assert.Equal(month.Totals, after.Totals);
            Assert.Equal(month.Classes, after.Classes);
            Assert.Equal(month.Receivables, after.Receivables);
            var octoberAfter = await MonthReportAsync(w, october);
            Assert.Equal(octoberReport.Classes, octoberAfter.Classes);
            Assert.Equal(octoberReport.Totals, octoberAfter.Totals);
            Assert.Equal(octoberReport.Receivables, octoberAfter.Receivables);
        }
        finally
        {
            await site.DisposeAsync();
        }
    }
}
