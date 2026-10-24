using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The pilot's shared path with the flexible permissions, on one database (legacy autograph-permissioned-chain.test.ts;
/// owner 15.10.2026: role defaults, group defaults, personal allow / deny / inherit; docs/05 ვ4: no one approves what they
/// created): the underwriter issues P (PolicyActions.IssuePolicyAsync); the admin's panel actions give a group
/// policy.approve, premium.receipt.create and accounting.period.close, put the viewer in it and remove the receipt from
/// the viewer personally; the viewer approves P, is refused the receipt, receives once after inherit, and closes the
/// month once it has ended. The legacy days (P from 01.11.2026, "today" 03.11 then 03.12; the older policy Q of 120.00
/// from 01.10 paid 05.10, October closed) are rewritten relative to today: P from the first of today's month, Q from the
/// first of the month before (closed), the close asked with today and then with a day of the next month (PeriodClose
/// takes the day). Money by hand; on a 30-day month after a 31-day one the legacy literals are checked as such.
/// </summary>
public sealed class GapsAutographChainTests
{
    [Fact]
    public async Task Issue_the_admins_group_the_viewer_approves_is_refused_the_receipt_receives_once_after_inherit_closes_the_month_once_it_ended() =>
        await OwnDatabaseAsync(async w =>
        {
            var admin = await w.UserAsync();
            var underwriter = await w.UserAsync("underwriter");
            var viewer = await w.UserAsync("viewer");
            var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var holder = await w.ClientAsync(taxId);
            var start = MonthStart;
            var previous = start.AddMonths(-1);
            // Q: 120.00 from the first of last month, paid on its fifth day (the fixture writes it), last month closed.
            var q = await w.ActivePolicyAsync(admin, holder, previous, premium: 120m);
            var qInst = await w.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = q.Id });
            var qReceipt = await w.ReceiptAsync(holder, previous.AddDays(4), 120m);
            await using (var c = await w.Database.OpenAppAsync())
                foreach (var i in qInst)
                    await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@qReceipt, @i, 30, 30, 1, @on)",
                        new { qReceipt, i, on = previous.AddDays(4) });
            await w.CloseMonthAsync(previous, admin.Id);
            var previousReport = await MonthReportAsync(w, previous);

            // 1. The underwriter issues P by the real form: pending, 300.00 + 100.00, four instalments of 100.00.
            var (product, _) = await w.CascoAsync();
            var issued = await PolicyActions.IssuePolicyAsync(w.App, w.Rates, underwriter, World.Form(("product_id", product.ToString()), ("policyholder_id_number", taxId),
                ("make", "ტესტი"), ("model", "პილოტი"), ("plate", "TE-500-AG"), ("value", "30000"), ("currency", "GEL"), ("effective_from", Iso(start)), ("effective_to", Iso(start.AddYears(1))),
                ("cov_damage_premium", "300"), ("cov_damage_limit", "30000"), ("cov_damage_deductible", "0"), ("cov_theft_premium", "100"), ("cov_theft_limit", "30000"),
                ("cov_theft_deductible", "0"), ("installments", "4")), code => code is "damage" or "theft");
            var p = await w.ScalarAsync<Guid>("select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder and p.id <> @q", new { holder, q = q.Id });
            Assert.Equal($"/policies/{p}?issued=1", issued.Redirect);
            Assert.Equal([("pending_approval", start, start.AddYears(1), underwriter.Id)], await w.QueryAsync<(string, DateOnly, DateOnly, Guid)>(
                "select status, effective_from, effective_to, created_by from core.policy where id = @p", new { p }));
            Assert.Equal([(start, 100m), (start.AddMonths(3), 100m), (start.AddMonths(6), 100m), (start.AddMonths(9), 100m)],
                await w.QueryAsync<(DateOnly, decimal)>("select due_on, amount from core.installment where policy_id = @p order by due_on", new { p }));

            // 2. The admin, by the panel's actions: a group with the three operations, the viewer in it, the viewer's own removal of the receipt.
            var created = await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.CreateGroupAsync(tx, "პილოტი"));
            Assert.True(created.Ok);
            var group = await w.ScalarAsync<Guid>("select id from core.user_group where name = N'პილოტი'");
            foreach (var key in new[] { "policy.approve", "premium.receipt.create", PeriodClose.Permission })
                Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetGroupGrantAsync(tx, group.ToString(), key, "true"))).Ok);
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetGroupMemberAsync(tx, group.ToString(), viewer.Id.ToString(), "true"))).Ok);
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetUserDecisionAsync(tx, viewer.Id.ToString(), "premium.receipt.create", "deny"))).Ok);
            var shown = (await PermissionStore.LoadUserPermissionsAsync(w.App, viewer)).ToDictionary(r => r.Permission);
            Assert.Equal([(true, PermissionSource.Group), (false, PermissionSource.Revoked), (true, PermissionSource.Group), (false, PermissionSource.NotInRole), (false, PermissionSource.NotInRole)],
                new[] { "policy.approve", "premium.receipt.create", PeriodClose.Permission, "claim.payment.record", "claim.recovery.receive" }.Select(k => (shown[k].Allowed, shown[k].Source)));

            // 3. The viewer approves P (created by the underwriter): 300.00 + 100.00 booked on its start by the viewer; again: already.
            Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, viewer, p.ToString())).Outcome);
            var booked = await w.QueryAsync<(decimal, DateOnly, DateOnly, DateOnly, Guid)>("""
                select t.amount, t.booked_on, t.cover_from, t.cover_to, t.created_by from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                where l.policy_id = @p order by t.amount desc
                """, new { p });
            Assert.Equal([(300m, start, start, start.AddYears(1), viewer.Id), (100m, start, start, start.AddYears(1), viewer.Id)], booked);
            Assert.Equal([("active", viewer.Id, underwriter.Id)], await w.QueryAsync<(string, Guid, Guid)>("select status, approved_by, created_by from core.policy where id = @p", new { p }));
            Assert.Equal("already", (await PolicyActions.ApprovePolicyAsync(w.App, viewer, p.ToString())).Outcome);
            Assert.Equal(booked, await w.QueryAsync<(decimal, DateOnly, DateOnly, DateOnly, Guid)>("""
                select t.amount, t.booked_on, t.cover_from, t.cover_to, t.created_by from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                where l.policy_id = @p order by t.amount desc
                """, new { p }));

            // 4. The receipt: refused while the viewer's own removal stands; the admin puts it back to inherit; the same form, once.
            var ledger = await LedgerAsync(w);
            Assert.Equal(BillingTexts.Payment.NotAllowed, (await w.PayAsync(viewer, p, "100", World.Today, reference: "R-1102")).Message);
            Assert.Equal(ledger, await LedgerAsync(w));
            Assert.True((await PermissionPanel.ChangeAsync(w.App, admin.Id, tx => PermissionPanel.SetUserDecisionAsync(tx, viewer.Id.ToString(), "premium.receipt.create", "inherit"))).Ok);
            var receipt = (await PermissionStore.LoadUserPermissionsAsync(w.App, viewer)).Single(r => r.Permission == "premium.receipt.create");
            Assert.Equal((true, PermissionSource.Group), (receipt.Allowed, receipt.Source));
            Assert.Equal($"/policies/{p}?paid=0", (await w.PayAsync(viewer, p, "100", World.Today, reference: "R-1102")).Redirect);
            Assert.Equal([(World.Today, 100m, "bank", "receipt", holder, "R-1102", viewer.Id)], await w.QueryAsync<(DateOnly, decimal, string, string, Guid, string, Guid)>(
                "select received_on, amount_gel, method, kind, payer_party_id, reference, created_by from core.payment where reference = N'R-1102'"));
            Assert.Equal([(start, 100m, 100m, 1m, World.Today)], await w.QueryAsync<(DateOnly, decimal, decimal, decimal, DateOnly)>("""
                select i.due_on, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.payment r on r.id = a.payment_id
                join core.installment i on i.id = a.installment_id where r.reference = N'R-1102'
                """));

            // 5. The month by hand (tetri): P in force from its start — one policy, 400.00 written both ways; earned P and Q for the
            // month's days, Q cumulatively from last month; owed: P's 300.00 left, none overdue (Q paid).
            var days = MonthEnd(start).DayNumber - start.DayNumber + 1;
            var qDays = MonthEnd(start).DayNumber - previous.DayNumber + 1;
            var qBefore = start.DayNumber - previous.DayNumber;
            BigInteger E(long minor, int d) => ReportMath.DivRound(new BigInteger(minor) * d, 365);
            var earned = E(30000, days) + E(10000, days) + (E(12000, qDays) - E(12000, qBefore));
            var unearned = (40000 - E(30000, days) - E(10000, days)) + (12000 - E(12000, qDays));
            if ((qBefore, days) == (31, 30)) Assert.Equal((new BigInteger(4274), new BigInteger(46707)), (earned, unearned));
            var month = await MonthReportAsync(w, start);
            var c51 = Assert.Single(month.Classes);
            Assert.Equal((1, new BigInteger(40000), new BigInteger(40000), earned, unearned), (c51.PoliciesIssued, c51.StatisticalWrittenGel, c51.FinancialWrittenGel, c51.EarnedGel, c51.UnearnedGel));
            Assert.Equal((new BigInteger(30000), BigInteger.Zero), (month.Receivables.Single().Outstanding, month.Receivables.Single().Overdue));

            // 6. The month close: not ended today, nothing written; on the third of next month it closes (last month closed); again: already.
            var periods = await w.ScalarAsync<int>("select count(*) from core.accounting_period");
            Assert.Equal(new PeriodClose.Answer("not-ended", Iso(start)), await PeriodClose.CloseAsync(w.App, viewer, Iso(start), World.Today));
            Assert.Equal(periods, await w.ScalarAsync<int>("select count(*) from core.accounting_period"));
            var later = start.AddMonths(1).AddDays(2);
            Assert.Equal(new PeriodClose.Answer("done", Iso(start)), await PeriodClose.CloseAsync(w.App, viewer, Iso(start), later));
            Assert.Equal([("closed", viewer.Id)], await w.QueryAsync<(string, Guid)>("select status, closed_by from core.accounting_period where period_start = @start", new { start }));
            Assert.Equal(new PeriodClose.Answer("already", Iso(start)), await PeriodClose.CloseAsync(w.App, viewer, Iso(start), later));
            // Closing writes nothing financial: the month's report as before; last month's as reported.
            var after = await MonthReportAsync(w, start);
            Assert.Equal(month.Totals, after.Totals);
            Assert.Equal(month.Classes, after.Classes);
            Assert.Equal(month.Receivables, after.Receivables);
            var previousAfter = await MonthReportAsync(w, previous);
            Assert.Equal(previousReport.Classes, previousAfter.Classes);
            Assert.Equal(previousReport.Totals, previousAfter.Totals);
            Assert.Equal(previousReport.Receivables, previousAfter.Receivables);
        });
}
