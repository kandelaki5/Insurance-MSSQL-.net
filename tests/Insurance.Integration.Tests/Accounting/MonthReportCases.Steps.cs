using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Accounting.MonthReportOracle;

namespace Insurance.Integration.Tests.Accounting;

public sealed partial class MonthReportCase
{
    // ---------------------------------------------------------------- R1. The largest, in one class, unpaid.
    private async Task R1()
    {
        BigInteger L = T("90071992547408.01"), X = T("9999999999999999.99");
        await ClientAsync("R1");
        await IssuedAsync("R1", "l", "GEL", "90071992547408.01", "90071992547408.01", [(First0, "90071992547408.00"), (First1, "0.01")]);
        await IssuedAsync("R1", "x", "GEL", "9999999999999999.99", "9999999999999999.99", [(Day($"{M0}-15"), "9999999999999999.99")]);
        await IssueRowAsync("R1", "l", "90071992547408.01", "90071992547408.01");
        await IssueRowAsync("R1", "x", "9999999999999999.99", "9999999999999999.99");
        // The first month: both written to the tetri, their sum beyond one row's largest; owed both, overdue but l's 0.01 due on the next month's first.
        await ReportAsync($"R1 {M0}", M0, new(IssuesIn([L, X], 0), Owe("GEL", new(L + X, L - 1 + X, L + X))), keep: M0);
        Identity($"R1 {M0}: the first month of cover", M0);
        await ReportAsync($"R1 {M1}", M1, new(IssuesIn([L, X], 1), Owe("GEL", new(L + X, L + X, L + X))), keep: M1);
        Identity($"R1 {M0} and {M1} together", M0, M1);
        if (October)
        {
            Pinned("R1 m0 earned", "856965018545122.32", Kept(M0).Earned);
            Pinned("R1 m0 UPR", "9233106974002285.68", Kept(M0).Upr);
            Pinned("R1 m1 earned", "829320985688828.06", Kept(M1).Earned);
            Pinned("R1 m1 UPR", "8403785988313457.62", Kept(M1).Upr);
            Pinned("R1 written", "10090071992547408.00", Kept(M0).Fin);
            Pinned("R1 m0 overdue", "10090071992547407.99", L - 1 + X);
        }
        await CountsAsync("R1", new() { ["party"] = 1, ["contract"] = 1, ["policy"] = 2, ["policy_line"] = 2, ["premium_transaction"] = 2, ["installment"] = 3 });
    }

    // ---------------------------------------------------------------- R2. The .37: one issue of 90071992547408.01, its first month's UPR.
    private async Task R2()
    {
        var L = T("90071992547408.01");
        await ClientAsync("R2");
        await IssuedAsync("R2", "s", "GEL", "90071992547408.01", "90071992547408.01", [(First0, "90071992547408.01")]);
        await IssueRowAsync("R2", "s", "90071992547408.01", "90071992547408.01");
        await ReportAsync($"R2 {M0}", M0, new(IssuesIn([L], 0), Owe("GEL", new(L, L, L))), keep: M0);
        Identity($"R2 {M0}: written = earned + UPR", M0);
        if (October)
        {
            Pinned("R2 m0 earned", "7649950051971.64", Kept(M0).Earned);
            Pinned("R2 m0 UPR", "82422042495436.37", Kept(M0).Upr);
        }
        await CountsAsync("R2", new() { ["party"] = 1, ["contract"] = 1, ["policy"] = 1, ["policy_line"] = 1, ["premium_transaction"] = 1, ["installment"] = 1 });
    }

    // ---------------------------------------------------------------- R3. A 1.00 issue cancelled from the next month's 15th, the cancellation read before its original; undone.
    private async Task R3()
    {
        BigInteger P = 100;
        var cancelFrom = Day($"{M1}-15");
        var kept = cancelFrom.DayNumber - First0.DayNumber;
        var refund = P - EarnedTo(P, kept, Total);
        BigInteger Mirror(int upTo) => -(EarnedTo(P, upTo, Total) - EarnedTo(P, kept, Total));
        var plain = IssuesIn([P], 1);
        await ClientAsync("R3");
        await IssuedAsync("R3", "c", "GEL", "1.00", "1.00", [(First0, "1.00")]);
        await IssueRowAsync("R3", "c", "1.00", "1.00");
        await SnapshotAsync("R3", M0, "R3:m0");
        await ReportAsync($"R3 {M1} before the cancellation", M1, new(plain, Nothing));
        var line = Id("R3:c:line");
        // The cancellation's row is written with a created_at before its original's (09:00 against 10:00 of the first day).
        await Exec("""
            insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
            values (@id, @line, N'cancellation', @amount, @amount, @on, @on, @to, @issue, @at, @admin)
            """, new { id = Id("R3:c:cancel"), line, amount = Dec(-refund), on = cancelFrom, to = CoverTo, issue = Id("R3:c:issue"), at = At(First0, 9), admin = W.Admin });
        var ledger = await RowsAsync("""
            select concat(kind, ' ', convert(nvarchar(40), amount), ' ', convert(char(10), booked_on, 23), ' ', convert(char(10), cover_from, 23), ' ', isnull(lower(convert(nchar(36), cancels_transaction_id)), N'-'))
            from core.premium_transaction where policy_line_id = @line order by kind desc
            """, new { line });
        Same("R3: the ledger — the issue as stored, the cancellation linked to it",
            $"issue 1.00 {Iso(First0)} {Iso(First0)} -|cancellation {Amount(-refund)} {Iso(cancelFrom)} {Iso(cancelFrom)} {Id("R3:c:issue")}", string.Join("|", ledger));
        // The cancellation earns as its original from the cancel day (not as a plain negative premium over its own days); no UPR left.
        await ReportAsync($"R3 {M1} cancelled", M1, new(new Fig(0, 0, -refund, plain.Earned + Mirror(B), 0), Nothing), keep: "cancelled");
        await SnapshotAsync($"R3 {M0} after the cancellation", M0, "R3:m0", compare: true);
        await Exec("""
            insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
            values (@id, @line, N'correction', @amount, @amount, @on, @from, @to, @cancel, @at, @admin)
            """, new { id = Id("R3:c:undo"), line, amount = Dec(refund), on = Day($"{M1}-20"), from = cancelFrom, to = CoverTo, cancel = Id("R3:c:cancel"), at = At(Day($"{M1}-20"), 10), admin = W.Admin });
        await ReportAsync($"R3 {M1} undone: as if never cancelled", M1, new(plain, Nothing), keep: "undone");
        await SnapshotAsync($"R3 {M0} after the undo", M0, "R3:m0", compare: true);
        await IssueRowAsync("R3", "c", "1.00", "1.00", onlyIssue: true);
        if (October)
        {
            Pinned("R3 cancelled written", "-0.88", Kept("cancelled").Fin);
            Pinned("R3 cancelled earned", "0.04", Kept("cancelled").Earned);
            Pinned("R3 cancelled UPR", "0.00", Kept("cancelled").Upr);
            Pinned("R3 undone written", "0.00", Kept("undone").Fin);
            Pinned("R3 undone earned", "0.09", Kept("undone").Earned);
            Pinned("R3 undone UPR", "0.83", Kept("undone").Upr);
        }
        await CountsAsync("R3", new() { ["party"] = 1, ["contract"] = 1, ["policy"] = 1, ["policy_line"] = 1, ["premium_transaction"] = 3, ["installment"] = 1 });
    }

    // ---------------------------------------------------------------- R4. One tetri, and an instalment due on the month's last day.
    private async Task R4()
    {
        await ClientAsync("R4");
        await IssuedAsync("R4", "t", "GEL", "100.01", "100.01", [(First0, "100.00"), (First1, "0.01")]);
        await IssuedAsync("R4", "v", "GEL", "5.00", "5.00", [(End0, "5.00")]);
        var d0 = D0Today;
        await ReceiveAsync("R4", "t", d0, "100.00", 0);
        var party = Id("R4:party");
        Same("R4: the receipt", Payment(d0, "100.00", "receipt", party, W.Accountant), string.Join("|", await PaymentsOfAsync(party)));
        var receipt = await PaymentIdAsync(party, "receipt");
        Same("R4: its line", Line(Id("R4:t:i1"), "100.00", "100.00", "1.000000", Today, W.Accountant), string.Join("|", await LinesOfAsync(receipt)));
        // The first month: t's 0.01 (due on the next month's first) and v's 5.00 due on its last day — owed, neither overdue yet.
        await ReportAsync($"R4 {M0}", M0, new(IssuesIn([10001, 500], 0), Owe("GEL", new(501, 0, 501))));
        await ReportAsync($"R4 {M1}", M1, new(IssuesIn([10001, 500], 1), Owe("GEL", new(501, 501, 501))));
        await CountsAsync("R4", new() { ["party"] = 1, ["contract"] = 1, ["policy"] = 2, ["policy_line"] = 2, ["premium_transaction"] = 2, ["installment"] = 3, ["payment"] = 1, ["payment_allocation"] = 1 });
    }

    // ---------------------------------------------------------------- R5. USD: the stored rate for the written premium and the receipt; each month end's own for the receivable.
    private async Task R5()
    {
        var d0 = D0Today;
        await W.RateAsync("USD", Iso(d0), 2.7m);
        await W.RateAsync("USD", Iso(End0), 2.8m);
        await W.RateAsync("USD", Iso(End1), 2.9m);
        var owed = T("100.00") - T("10.00");
        await ClientAsync("R5");
        await IssuedAsync("R5", "u", "USD", "100.00", "270.00", [(First0, "100.00")]);
        await IssueRowAsync("R5", "u", "100.00", "270.00");
        await ReceiveAsync("R5", "u", d0, "27.00", 0);
        var party = Id("R5:party");
        Same("R5: the receipt", Payment(d0, "27.00", "receipt", party, W.Accountant), string.Join("|", await PaymentsOfAsync(party)));
        var receipt = await PaymentIdAsync(party, "receipt");
        Same("R5: its line — 10.00 USD at the receipt day's 2.7", Line(Id("R5:u:i1"), "10.00", "27.00", "2.700000", Today, W.Accountant), string.Join("|", await LinesOfAsync(receipt)));
        await ReportAsync($"R5 {M0}", M0, new(IssuesIn([27000], 0), Owe("USD", new(owed, owed, AtRateAway(owed, "2.8"), $"2.8000 ({Dmy(End0)})"))));
        await ReportAsync($"R5 {M1}", M1, new(IssuesIn([27000], 1), Owe("USD", new(owed, owed, AtRateAway(owed, "2.9"), $"2.9000 ({Dmy(End1)})"))));
        if (October)
        {
            Pinned("R5 m0 in GEL", "252.00", AtRateAway(owed, "2.8"));
            Pinned("R5 m1 in GEL", "261.00", AtRateAway(owed, "2.9"));
        }
        await CountsAsync("R5", new() { ["party"] = 1, ["contract"] = 1, ["policy"] = 1, ["policy_line"] = 1, ["premium_transaction"] = 1, ["installment"] = 1, ["payment"] = 1, ["payment_allocation"] = 1 });
    }

    // ---------------------------------------------------------------- R6. A receipt's advance taken by another policy, its reversal asked, failed once, approved.
    private async Task R6()
    {
        var d0 = Day($"{M0}-20");
        var d1 = Day($"{M1}-03");
        var party = Id("R6:party");
        Guid a = Id("R6:a"), receipt = Id("R6:receipt"), request = Id("R6:request");
        await ClientAsync("R6");
        await IssuedAsync("R6", "a", "GEL", "10000.00", "10000.00", [(Day($"{M0}-15"), "10000.00")]);
        await IssuedAsync("R6", "b", "GEL", "0.01", "0.01", [(First1, "0.01")]);
        var written = IssuesIn([1000000, 1], 1);
        // recordPayment of 10 000.01 for a on d0, as the legacy child wrote it (a's 10 000.00; 0.01 left as an advance).
        await W.App.AsUserAsync(W.Accountant, async tx =>
        {
            await tx.ExecuteAsync("insert core.payment (id, received_on, amount_gel, method, payer_party_id, reference, created_by) values (@receipt, @d0, 10000.01, N'bank', @party, @tag, @acc)",
                new { receipt, d0, party, tag = Tag, acc = W.Accountant });
            await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on, created_by) values (@receipt, @i, 10000.00, 10000.00, 1, @d0, @acc)",
                new { receipt, i = Id("R6:a:i1"), d0, acc = W.Accountant });
            return 0;
        });
        Same("R6: the receipt", Payment(d0, "10000.01", "receipt", party, W.Accountant), string.Join("|", await PaymentsOfAsync(party)));
        await ReportAsync($"R6 {M0}: a settled, b's 0.01 owed, not yet overdue", M0, new(IssuesIn([1000000, 1], 0), Owe("GEL", new(1, 0, 1))));
        await SnapshotAsync("R6", M0, "R6:m0");
        // allocateReceipt from b's own form on d1, and reversePayment from a on d1 (above the 10 000.00 threshold: a request with the receipt's lines).
        await W.App.AsUserAsync(W.Accountant, async tx =>
        {
            await tx.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on, created_by) values (@receipt, @i, 0.01, 0.01, 1, @d1, @acc)",
                new { receipt, i = Id("R6:b:i1"), d1, acc = W.Accountant });
            await tx.ExecuteAsync("""
                insert core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by)
                values (@request, @receipt, @a, @d1, 10000.01, N'bank', @party, @tag, @acc)
                """, new { request, receipt, a, d1, party, tag = Tag, acc = W.Accountant });
            await tx.ExecuteAsync("""
                insert core.receipt_reversal_request_line (request_id, position, installment_id, amount, amount_gel, rate)
                values (@request, 1, @ia, 10000.00, 10000.00, 1), (@request, 2, @ib, 0.01, 0.01, 1)
                """, new { request, ia = Id("R6:a:i1"), ib = Id("R6:b:i1") });
            return 0;
        });
        var lineA = Line(Id("R6:a:i1"), "10000.00", "10000.00", "1.000000", d0, W.Accountant);
        var lineB = Line(Id("R6:b:i1"), "0.01", "0.01", "1.000000", d1, W.Accountant);
        Same("R6: the receipt's lines — a's and b's", $"{lineA}|{lineB}", string.Join("|", await LinesOfAsync(receipt)));
        var nothingOwed = new Want(written, Owe("GEL", new(0, 0, 0)));
        await ReportAsync($"R6 {M1} while the reversal waits: nothing owed", M1, nothingOwed);
        await SnapshotAsync($"R6 {M0} while the reversal waits", M0, "R6:m0", compare: true);

        string Back(string o) => $"/policies/{a}?reversal={o}";
        await using (var c = await W.Database.OpenAdminAsync())
            await c.ExecuteAsync("""
                create trigger core.test_fault_mirror on core.payment_allocation after insert as
                begin
                  set nocount on;
                  if exists (select 1 from inserted i join core.payment p on p.id = i.payment_id
                             where p.kind = N'reversal' and (select count(*) from core.payment_allocation x where x.payment_id = i.payment_id) >= 2)
                    throw 50999, N'ChildFault: the check''s child fails after the mirror''s first line', 1;
                end
                """);
        var failed = await Record.ExceptionAsync(() => PaymentActions.ApproveReceiptReversalRequestAsync(W.App, Admin, a.ToString(), request.ToString()));
        await using (var c = await W.Database.OpenAdminAsync()) await c.ExecuteAsync("drop trigger core.test_fault_mirror");
        Same("R6: the admin, the action failing after the mirror's first line", "ChildFault: the check's child fails after the mirror's first line", failed?.Message ?? "no failure");
        Same("R6: no mirror", "", string.Join("|", (await PaymentsOfAsync(party)).Skip(1)));
        await ReportAsync($"R6 {M1} after the failure: still nothing owed", M1, nothingOwed);
        Same("R6: the admin — approved", Back("approved"), await PaymentActions.ApproveReceiptReversalRequestAsync(W.App, Admin, a.ToString(), request.ToString()) ?? "null");
        var mirror = await PaymentIdAsync(party, "reversal");
        var mirrorRow = Payment(d1, "-10000.01", "reversal", party, W.Admin, receipt, request);
        Same("R6: one mirror on the request's day, by the admin", mirrorRow, string.Join("|", (await PaymentsOfAsync(party)).Skip(1)));
        Same("R6: its lines — both, negated exactly", $"{Line(Id("R6:a:i1"), "-10000.00", "-10000.00", "1.000000", d1, W.Admin)}|{Line(Id("R6:b:i1"), "-0.01", "-0.01", "1.000000", d1, W.Admin)}",
            string.Join("|", await LinesOfAsync(mirror)));
        Same("R6: again — already", Back("already"), await PaymentActions.ApproveReceiptReversalRequestAsync(W.App, Admin, a.ToString(), request.ToString()) ?? "null");
        Same("R6: the receipt as recorded, still one mirror", $"{Payment(d0, "10000.01", "receipt", party, W.Accountant)}|{mirrorRow}", string.Join("|", await PaymentsOfAsync(party)));
        await ReportAsync($"R6 {M1} approved: a's 10 000.00 and b's 0.01 owed again, both overdue", M1, new(written, Owe("GEL", new(1000001, 1000001, 1000001))));
        await SnapshotAsync($"R6 {M0} after the approval", M0, "R6:m0", compare: true);
        await CountsAsync("R6", new() { ["party"] = 1, ["contract"] = 1, ["policy"] = 2, ["policy_line"] = 2, ["premium_transaction"] = 2, ["installment"] = 2, ["payment"] = 2, ["payment_allocation"] = 4, ["receipt_reversal_request"] = 1, ["receipt_reversal_request_line"] = 2 });
    }

    // ---------------------------------------------------------------- shared reinsurance and claim rows (R7, R9)

    private async Task TreatyAsync(string k, string n, string kind, string currency, string? ceded = null, string? priority = null, string? limit = null, string? premium = null,
        string commission = "0.0000", DateOnly? to = null, DateOnly? from = null) =>
        await Exec("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, priority, treaty_limit, treaty_premium,
                                   commission_percent, broker_percent, status, created_at, created_by)
            values (@id, @number, @name, @re, @kind, @from, @to, @currency, @ceded, @priority, @limit, @premium, @commission, 0, N'active', @at, @admin)
            """, new
        {
            id = Id($"{k}:{n}"), number = $"{Tag}-RI-{k}-{n}", name = $"{Tag} {k} {n} (გამოგონილი)", re = Id($"{k}:re"), kind, from = from ?? First0, to = to ?? First0.AddDays(364), currency,
            ceded = ceded is null ? (decimal?)null : D(ceded), priority = priority is null ? (decimal?)null : D(priority), limit = limit is null ? (decimal?)null : D(limit),
            premium = premium is null ? (decimal?)null : D(premium), commission = D(commission), at = At(First0, 8), admin = W.Admin,
        });

    private async Task ClaimAsync(string k, string c, string policyKey, string currency, string reserve)
    {
        await Exec("""
            insert core.loss_event (id, number, policy_id, occurred_on, reported_on, description, created_at, created_by)
            values (@id, @number, @policy, @on, @on, @description, @at, @admin)
            """, new { id = Id($"{k}:{c}:event"), number = $"{Tag}-LE-{k}-{c}", policy = Id($"{k}:{policyKey}"), on = First0, description = $"{Tag} შემთხვევა {c}", at = At(First0, 8, 10), admin = W.Admin });
        await Exec("""
            insert core.claim (id, number, policy_id, policy_line_id, loss_event_id, currency, occurred_on, reported_on, description, status, deductible_amount, created_at, created_by)
            values (@id, @number, @policy, @line, @event, @currency, @on, @on, @description, N'in_review', 0, @at, @admin)
            """, new { id = Id($"{k}:{c}"), number = $"{Tag}-CLM-{k}-{c}", policy = Id($"{k}:{policyKey}"), line = Id($"{k}:{policyKey}:line"), @event = Id($"{k}:{c}:event"), currency, on = First0,
            description = $"{Tag} ზარალი {c}", at = At(First0, 8, 20), admin = W.Admin });
        await Exec("insert core.claim_reserve (id, claim_id, booked_on, change, reason, created_at, created_by) values (@id, @claim, @on, @change, @reason, @at, @admin)",
            new { id = Id($"{k}:{c}:reserve"), claim = Id($"{k}:{c}"), on = Day($"{M0}-05"), change = D(reserve), reason = Tag, at = At(Day($"{M0}-05"), 9), admin = W.Admin });
    }

    private async Task ClaimPaymentAsync(string k, string c, string id, DateOnly on, string amount, string deductible, string net, string netGel, string rate, Guid? reverses = null) =>
        await Exec("""
            insert core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_payment_id,
                                       kind, reverses_claim_payment_id, reversal_reason, created_at, created_by)
            values (@id, @claim, @on, @amount, @deductible, 0, @net, @netGel, @rate, N'policyholder', @party, null, @kind, @reverses, @reason, @at, @admin)
            """, new
        {
            id = Id(id), claim = Id($"{k}:{c}"), on, amount = D(amount), deductible = D(deductible), net = D(net), netGel = D(netGel), rate = D(rate), party = Id($"{k}:party"),
            kind = reverses is null ? "payment" : "reversal", reverses, reason = reverses is null ? null : "correction", at = At(on, 10), admin = W.Admin,
        });

    private async Task ClaimCessionAsync(string k, string c, string t, string percent, string? priority = null, string? limit = null) =>
        await Exec("insert core.claim_cession (claim_id, treaty_id, share_percent, priority, treaty_limit) values (@claim, @treaty, @percent, @priority, @limit)",
            new { claim = Id($"{k}:{c}"), treaty = Id($"{k}:{t}"), percent = D(percent), priority = priority is null ? (decimal?)null : D(priority), limit = limit is null ? (decimal?)null : D(limit) });

    private async Task PremiumCessionAsync(string id, string onRow, string treaty, string share, BigInteger amount, BigInteger commission, DateOnly bookedOn, DateOnly coverFrom, string? cancels, DateTimeOffset at) =>
        await Exec("""
            insert core.premium_cession (id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel, booked_on, cover_from, cover_to,
                                         cancels_cession_id, created_at, created_by)
            values (@id, @row, @treaty, @share, @amount, @amount, @commission, @commission, @bookedOn, @coverFrom, @to, @cancels, @at, @admin)
            """, new
        {
            id = Id(id), row = Id(onRow), treaty = Id(treaty), share = D(share), amount = Dec(amount), commission = Dec(commission), bookedOn, coverFrom, to = CoverTo,
            cancels = cancels is null ? (Guid?)null : Id(cancels), at, admin = W.Admin,
        });

    // ---------------------------------------------------------------- R7. The rest of the month's money, every figure not zero.
    private async Task R7()
    {
        const string k = "R7";
        BigInteger L = T("90071992547408.01"), X = T("9999999999999999.99");
        BigInteger CededUpr(int month) => new[] { (BigInteger)30000, X }.Aggregate(BigInteger.Zero, (s, amt) => s + amt - EarnedTo(amt, month == 0 ? A : B, Total));
        // IBNR: the class's net written of the report month's calendar year to its end (1 000.00 + X written, 300.00 + X ceded, on the first day): 5 %.
        BigInteger Ibnr(string month)
        {
            var inYear = First0.Year == Day(month + "-01").Year && First0 <= MonthEnd(month);
            var net = inYear ? 100000 + X - (30000 + X) : 0;
            return net > 0 ? Away(net * 5, 100) : 0;
        }
        var c2 = Slices(5000, 300_000);
        var c3 = Slices(36500, 500_000, 500_000);
        var c3paid = Slices(1, 500_000, 500_000);
        var m0Money = new Money([30000 + X, CededUpr(0), 6000 + L],
            [L + 27000 + 1 + 60000, X + AtRateUp(5000, "2.8") + 36500 + 90000, Ibnr(M0), L,
             Up(L * L, L) + Up(27000 * Slices(10000, 300_000)[0], 10000) + c3paid.Aggregate(BigInteger.Zero, (s, x) => s + (x == 0 ? 0 : Up(x, 1))) + 50000,
             X + AtRateUp(c2[0], "2.8") + c3[0] + c3[1] + 50000],
            L + AtRateUp(10000, "2.8"));
        var m1Money = new Money([0, CededUpr(1), 0],
            [-27000, X + AtRateUp(5000, "2.9") + 36500 + 90000, Ibnr(M1), 0, Up(-27000 * -3000, -10000), X + AtRateUp(c2[0], "2.9") + c3[0] + c3[1] + 50000], 0);

        await W.RateAsync("USD", Iso(End0), 2.8m);
        await W.RateAsync("USD", Iso(End1), 2.9m);
        await ClientAsync(k);
        await PartyAsync(Id($"{k}:re"), $"{Tag} {k} გადამზღვევი (გამოგონილი)", $"{Tag}-{k}-re");
        await IssuedAsync(k, "p", "GEL", "1000.00", "1000.00", []);
        await IssuedAsync(k, "x", "GEL", "9999999999999999.99", "9999999999999999.99", []);
        await TreatyAsync(k, "T1", "quota_share", "GEL", ceded: "30.0000", commission: "20.0000");
        await TreatyAsync(k, "T2", "quota_share", "GEL", ceded: "100.0000");
        await TreatyAsync(k, "T3", "quota_share", "USD", ceded: "30.0000");
        await TreatyAsync(k, "T4", "quota_share", "GEL", ceded: "50.0000");
        await TreatyAsync(k, "T5", "facultative", "GEL", ceded: "50.0000");
        await TreatyAsync(k, "T6", "excess_of_loss", "GEL", priority: "100.00", limit: "1000.00", premium: "90071992547408.01", to: End0);
        await TreatyAsync(k, "T7", "excess_of_loss", "USD", priority: "100.00", limit: "1000.00", premium: "100.00", to: End0);
        await PremiumCessionAsync($"{k}:ces-p", $"{k}:p:issue", $"{k}:T1", "30.0000", 30000, 6000, First0, First0, null, At(First0, 11));
        await PremiumCessionAsync($"{k}:ces-x", $"{k}:x:issue", $"{k}:T2", "100.0000", X, L, First0, First0, null, At(First0, 11));
        await ClaimAsync(k, "C1", "p", "GEL", "9999999999999999.99");
        await ClaimAsync(k, "C2", "p", "USD", "50.00");
        await ClaimAsync(k, "C3", "p", "GEL", "365.00");
        await ClaimAsync(k, "C4", "p", "GEL", "900.00");
        var paid = Day($"{M0}-10");
        // C1's gross 90071992547408.01 with a deductible leaving 10 000.00 net: within the payment threshold, no request needed.
        await ClaimPaymentAsync(k, "C1", $"{k}:C1:pay", paid, "90071992547408.01", "90071992537408.01", "10000.00", "10000.00", "1.000000");
        await ClaimPaymentAsync(k, "C2", $"{k}:C2:pay", paid, "100.00", "0.00", "100.00", "270.00", "2.700000");
        await ClaimPaymentAsync(k, "C3", $"{k}:C3:pay", paid, "0.01", "0.00", "0.01", "0.01", "1.000000");
        await ClaimPaymentAsync(k, "C4", $"{k}:C4:pay", paid, "600.00", "0.00", "600.00", "600.00", "1.000000");
        await Exec("""
            insert core.claim_recovery (id, claim_id, kind, received_on, amount_gel, row_kind, reverses_claim_recovery_id, subrogation_case_id, created_at, created_by)
            values (@id, @claim, N'salvage', @on, 90071992547408.01, N'receipt', null, null, @at, @admin)
            """, new { id = Id($"{k}:C1:recovery"), claim = Id($"{k}:C1"), on = Day($"{M0}-12"), at = At(Day($"{M0}-12"), 10), admin = W.Admin });
        await ClaimCessionAsync(k, "C1", "T2", "100.0000");
        await ClaimCessionAsync(k, "C2", "T3", "30.0000");
        await ClaimCessionAsync(k, "C3", "T4", "50.0000");
        await ClaimCessionAsync(k, "C3", "T5", "50.0000");
        await ClaimCessionAsync(k, "C4", "T6", "0.0000", "100.00", "1000.00");

        await ReportAsync($"R7 {M0}", M0, new(IssuesIn([100000, X], 0), Nothing, m0Money));
        await SnapshotAsync("R7", M0, "R7:m0");
        // The next month: C2's payment reversed, exactly its opposite at its own rate.
        await ClaimPaymentAsync(k, "C2", $"{k}:C2:reversal", Day($"{M1}-05"), "-100.00", "0.00", "-100.00", "-270.00", "2.700000", reverses: Id($"{k}:C2:pay"));
        await ReportAsync($"R7 {M1}", M1, new(IssuesIn([100000, X], 1), Nothing, m1Money));
        await SnapshotAsync($"R7 {M0} after the reversal", M0, "R7:m0", compare: true);
        await CountsAsync("R7", new() { ["party"] = 2, ["contract"] = 1, ["policy"] = 2, ["policy_line"] = 2, ["premium_transaction"] = 2, ["ri_treaty"] = 7, ["premium_cession"] = 2, ["loss_event"] = 4, ["claim"] = 4, ["claim_reserve"] = 4, ["claim_payment"] = 5, ["claim_recovery"] = 1, ["claim_cession"] = 5 });
    }

    // ---------------------------------------------------------------- R8. Ceded premium's history: four cessions, their cancellations linked to them, undone in the next month.
    private sealed record R8Row(string P, BigInteger G, string Treaty, string Share, BigInteger Refund, BigInteger C0, BigInteger Ck, BigInteger Cu, BigInteger Com0, BigInteger ComK, BigInteger ComU);

    private async Task R8()
    {
        const string k = "R8";
        BigInteger S = 36500, L = T("90071992547408.01"), X = T("9999999999999999.99"), Dd = 10;
        var cancelOn = Day($"{M0}-16");
        var undoOn = Day($"{M1}-10");
        var kept = cancelOn.DayNumber - First0.DayNumber;
        var plan = new (string P, BigInteger G, string T, BigInteger Share, BigInteger Com, string Text)[]
        {
            ("s", S, "T1", 400_000, 250_000, "365.00"), ("l", L, "T2", 1_000_000, 200_000, "90071992547408.01"),
            ("x", X, "T2", 1_000_000, 200_000, "9999999999999999.99"), ("d", Dd, "T2", 1_000_000, 200_000, "0.10"),
        };
        var rows = plan.Select(p =>
        {
            var c0 = Away(p.G * p.Share, 1_000_000);
            var refund = p.G - EarnedTo(p.G, kept, Total);
            var cRefund = c0 - EarnedTo(c0, kept, Total);
            return new R8Row(p.P, p.G, p.T, p.Share == 400_000 ? "40.0000" : "100.0000", refund, c0, -cRefund, cRefund,
                Away(c0 * p.Com, 1_000_000), Away(-cRefund * p.Com, 1_000_000), Away(cRefund * p.Com, 1_000_000));
        }).ToList();
        BigInteger Sum(Func<R8Row, BigInteger> f) => rows.Aggregate(BigInteger.Zero, (s, r) => s + f(r));
        // IBNR: the class's net written of the report month's year to its end, the rows as booked; only the 40 % cession leaves a net.
        BigInteger Ibnr(string month, string stage)
        {
            var booked = rows.SelectMany(r => new List<(DateOnly On, BigInteger X)> { (First0, r.G - r.C0) }
                .Concat(stage != "issue" ? [(cancelOn, -r.Refund - r.Ck)] : [])
                .Concat(stage == "undo" ? [(undoOn, r.Refund - r.Cu)] : []));
            var start = new DateOnly(Day(month + "-01").Year, 1, 1);
            var net = booked.Where(b => b.On >= start && b.On <= MonthEnd(month)).Aggregate(BigInteger.Zero, (s, b) => s + b.X);
            return net > 0 ? Away(net * 5, 100) : 0;
        }
        Money Of(BigInteger[] ceded, string month, string stage) => new(ceded, [0, 0, Ibnr(month, stage), 0, 0, 0], 0);
        var ret = Sum(r => EarnedTo(r.G, kept, Total));

        await ClientAsync(k);
        await PartyAsync(Id($"{k}:re"), $"{Tag} {k} გადამზღვევი (გამოგონილი)", $"{Tag}-{k}-re");
        foreach (var p in plan) await IssuedAsync(k, p.P, "GEL", p.Text, p.Text, []);
        await TreatyAsync(k, "T1", "quota_share", "GEL", ceded: "40.0000", commission: "25.0000");
        await TreatyAsync(k, "T2", "quota_share", "GEL", ceded: "100.0000", commission: "20.0000");
        foreach (var r in rows) await PremiumCessionAsync($"{k}:{r.P}:ces:issue", $"{k}:{r.P}:issue", $"{k}:{r.Treaty}", r.Share, r.C0, r.Com0, First0, First0, null, At(First0, 11));
        async Task CessionRead(R8Row r, string what, BigInteger amount, BigInteger commission, DateOnly on, DateOnly from, string? cancels, string row) =>
            Same($"R8: {r.P}'s {what} cession as stored",
                $"{Id(row)} {Id($"{k}:{r.Treaty}")} {r.Share} {Amount(amount)} {Amount(amount)} {Amount(commission)} {Amount(commission)} {Iso(on)} {Iso(from)} {Iso(CoverTo)} {(cancels is null ? "-" : Id(cancels).ToString())}",
                string.Join("|", await RowsAsync("""
                    select concat(lower(convert(nchar(36), premium_transaction_id)), ' ', lower(convert(nchar(36), treaty_id)), ' ', convert(nvarchar(20), share_percent), ' ',
                                  convert(nvarchar(40), amount), ' ', convert(nvarchar(40), amount_gel), ' ', convert(nvarchar(40), commission), ' ', convert(nvarchar(40), commission_gel), ' ',
                                  convert(char(10), booked_on, 23), ' ', convert(char(10), cover_from, 23), ' ', convert(char(10), cover_to, 23), ' ', isnull(lower(convert(nchar(36), cancels_cession_id)), N'-'))
                    from core.premium_cession where id = @id
                    """, new { id = Id($"{k}:{r.P}:ces:{what}") })));
        foreach (var r in rows) await CessionRead(r, "issue", r.C0, r.Com0, First0, First0, null, $"{k}:{r.P}:issue");
        await ReportAsync($"R8 {M0} ceded, not yet cancelled", M0, new(IssuesIn([S, L, X, Dd], 0), Nothing,
            Of([Sum(r => r.C0), Sum(r => r.C0 - EarnedTo(r.C0, A, Total)), Sum(r => r.Com0)], M0, "issue")));

        // The four cancelled from the 16th, each cession mirrored and linked; the cancellations written before their originals' rows.
        foreach (var r in rows)
        {
            await Exec("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
                values (@id, @line, N'cancellation', @amount, @amount, @on, @on, @to, @issue, @at, @admin)
                """, new { id = Id($"{k}:{r.P}:cancel"), line = Id($"{k}:{r.P}:line"), amount = Dec(-r.Refund), on = cancelOn, to = CoverTo, issue = Id($"{k}:{r.P}:issue"), at = At(First0, 9), admin = W.Admin });
            await PremiumCessionAsync($"{k}:{r.P}:ces:cancel", $"{k}:{r.P}:cancel", $"{k}:{r.Treaty}", r.Share, r.Ck, r.ComK, cancelOn, cancelOn, $"{k}:{r.P}:ces:issue", At(First0, 9, 30));
        }
        foreach (var r in rows) await CessionRead(r, "cancel", r.Ck, r.ComK, cancelOn, cancelOn, $"{k}:{r.P}:ces:issue", $"{k}:{r.P}:cancel");
        // Each cancellation earns as its original from the cancel day; no UPR, ceded or gross, is left.
        await ReportAsync($"R8 {M0} cancelled", M0, new(new Fig(4, S + L + X + Dd, ret, ret, 0), Nothing,
            Of([Sum(r => r.C0 + r.Ck), 0, Sum(r => r.Com0 + r.ComK)], M0, "cancel")), keep: M0);
        await ReportAsync($"R8 {M1} cancelled: nothing", M1, new(Fig.Zero, Nothing, Of([0, 0, 0], M1, "cancel")));
        await SnapshotAsync("R8", M0, "R8:m0");

        foreach (var r in rows)
        {
            await Exec("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
                values (@id, @line, N'correction', @amount, @amount, @on, @from, @to, @cancel, @at, @admin)
                """, new { id = Id($"{k}:{r.P}:undo"), line = Id($"{k}:{r.P}:line"), amount = Dec(r.Refund), on = undoOn, from = cancelOn, to = CoverTo, cancel = Id($"{k}:{r.P}:cancel"), at = At(undoOn, 10), admin = W.Admin });
            await PremiumCessionAsync($"{k}:{r.P}:ces:undo", $"{k}:{r.P}:undo", $"{k}:{r.Treaty}", r.Share, r.Cu, r.ComU, undoOn, cancelOn, $"{k}:{r.P}:ces:cancel", At(undoOn, 11));
        }
        foreach (var r in rows) await CessionRead(r, "undo", r.Cu, r.ComU, undoOn, cancelOn, $"{k}:{r.P}:ces:cancel", $"{k}:{r.P}:undo");
        // The undo is the cancellation's opposite: the next month as if never cancelled, the cancelled part earned back in it; the first as it was.
        await ReportAsync($"R8 {M1} undone", M1, new(new Fig(0, 0, Sum(r => r.Refund), Sum(r => EarnedTo(r.G, B, Total)) - ret, Sum(r => r.G - EarnedTo(r.G, B, Total))), Nothing,
            Of([Sum(r => r.Cu), Sum(r => r.C0 - EarnedTo(r.C0, B, Total)), Sum(r => r.ComU)], M1, "undo")), keep: M1);
        Identity($"R8 {M0} and {M1} together", M0, M1);
        await SnapshotAsync($"R8 {M0} after the undo", M0, "R8:m0", compare: true);
        foreach (var r in rows) await CessionRead(r, "issue", r.C0, r.Com0, First0, First0, null, $"{k}:{r.P}:issue");
        await CountsAsync("R8", new() { ["party"] = 2, ["contract"] = 1, ["policy"] = 4, ["policy_line"] = 4, ["premium_transaction"] = 12, ["ri_treaty"] = 2, ["premium_cession"] = 12 });
    }

    // ---------------------------------------------------------------- R9. EUR: issued at the stored 3.2, received at the day's 3.1, paid at its own 3.05.
    private async Task R9()
    {
        const string k = "R9";
        var d0 = D0Today;
        string payRate = "3.100000", end0Rate = "2.987600", end1Rate = "3.345600";
        BigInteger Bb = T("90071992547408.75"), Bgel = T("288230376151708.00"), E = 36500, Egel = 116800;
        BigInteger R = T("90071992547408.01"), Pay = 10001, T2 = T("90071992547408.01"), T3 = 10005;
        const string PayRate = "3.050000";
        var share30 = (BigInteger t) => Away(t * 300_000, 1_000_000);
        var paidGel = AtRateAway(Pay, PayRate);
        BigInteger Ibnr(string month) => First0.Year == Day(month + "-01").Year && First0 <= MonthEnd(month) ? Away((Egel + Bgel) * 5, 100) : 0;
        Owed OwedAt(int month) => month == 0
            ? new(E - 1000 + Bb, E - 1000, AtRateAway(E - 1000, end0Rate) + AtRateAway(Bb, end0Rate), $"2.9876 ({Dmy(End0)})")
            : new(E - 1000 + Bb, E - 1000 + Bb, AtRateAway(E - 1000, end1Rate) + AtRateAway(Bb, end1Rate), $"3.3456 ({Dmy(End1)})");
        Money MoneyIn(int month) => month == 0
            ? new([0, 0, 0], [paidGel, AtRateUp(R, end0Rate), Ibnr(M0), 0, Up(paidGel * share30(Pay), Pay), AtRateUp(share30(R), end0Rate)], AtRateUp(T2, end0Rate) + AtRateUp(T3, end0Rate))
            : new([0, 0, 0], [-paidGel, AtRateUp(R, end1Rate), Ibnr(M1), 0, Up(-paidGel * -share30(Pay), -Pay), AtRateUp(share30(R), end1Rate)], 0);

        await W.RateAsync("EUR", Iso(d0), 3.1m);
        await W.RateAsync("EUR", Iso(End0), 2.9876m);
        await W.RateAsync("EUR", Iso(End1), 3.3456m);
        await ClientAsync(k);
        await PartyAsync(Id($"{k}:re"), $"{Tag} {k} გადამზღვევი (გამოგონილი)", $"{Tag}-{k}-re");
        await IssuedAsync(k, "e", "EUR", "365.00", "1168.00", [(First0, "365.00")], "3.200000");
        await IssuedAsync(k, "b", "EUR", "90071992547408.75", "288230376151708.00", [(First1, "90071992547408.75")], "3.200000");
        await TreatyAsync(k, "T1", "quota_share", "EUR", ceded: "30.0000");
        await TreatyAsync(k, "T2", "excess_of_loss", "EUR", priority: "100.00", limit: "1000.00", premium: "90071992547408.01", to: End0);
        await TreatyAsync(k, "T3", "excess_of_loss", "EUR", priority: "100.00", limit: "1000.00", premium: "100.05", to: End0);
        await ClaimAsync(k, "C1", "e", "EUR", "90071992547408.01");
        await ClaimPaymentAsync(k, "C1", $"{k}:C1:pay", Day($"{M0}-10"), "100.01", "0.00", "100.01", Amount(paidGel), PayRate);
        await ClaimCessionAsync(k, "C1", "T1", "30.0000");

        await IssueRowAsync(k, "e", "365.00", "1168.00");
        await IssueRowAsync(k, "b", "90071992547408.75", "288230376151708.00");
        await ReceiveAsync(k, "e", d0, "31.00", 0);
        var party = Id($"{k}:party");
        Same("R9: the receipt", Payment(d0, "31.00", "receipt", party, W.Accountant), string.Join("|", await PaymentsOfAsync(party)));
        Same($"R9: its line — 10.00 EUR at the receipt day's {payRate[..3]}", Line(Id($"{k}:e:i1"), "10.00", "31.00", payRate, Today, W.Accountant),
            string.Join("|", await LinesOfAsync(await PaymentIdAsync(party, "receipt"))));
        const string paySql = """
            select concat(convert(nvarchar(40), amount), ' ', convert(nvarchar(40), net_paid_gel), ' ', convert(nvarchar(20), rate), ' ', kind, ' ', isnull(lower(convert(nchar(36), reverses_claim_payment_id)), N'-'))
            from core.claim_payment where claim_id = @claim order by paid_on
            """;
        Same("R9: the claim's payment as stored, at its own rate", $"100.01 {Amount(paidGel)} {PayRate} payment -", string.Join("|", await RowsAsync(paySql, new { claim = Id($"{k}:C1") })));
        await ReportAsync($"R9 {M0}", M0, new(IssuesIn([Egel, Bgel], 0), Owe("EUR", OwedAt(0)), MoneyIn(0)));
        await SnapshotAsync("R9", M0, "R9:m0");
        await ClaimPaymentAsync(k, "C1", $"{k}:C1:reversal", Day($"{M1}-05"), "-100.01", "0.00", "-100.01", Amount(-paidGel), PayRate, reverses: Id($"{k}:C1:pay"));
        Same("R9: the payment and its reversal, each at its own rate",
            $"100.01 {Amount(paidGel)} {PayRate} payment -|-100.01 {Amount(-paidGel)} {PayRate} reversal {Id($"{k}:C1:pay")}", string.Join("|", await RowsAsync(paySql, new { claim = Id($"{k}:C1") })));
        await ReportAsync($"R9 {M1}", M1, new(IssuesIn([Egel, Bgel], 1), Owe("EUR", OwedAt(1)), MoneyIn(1)));
        await SnapshotAsync($"R9 {M0} after the reversal", M0, "R9:m0", compare: true);
        await CountsAsync("R9", new() { ["party"] = 2, ["contract"] = 1, ["policy"] = 2, ["policy_line"] = 2, ["premium_transaction"] = 2, ["installment"] = 2, ["payment"] = 1, ["payment_allocation"] = 1, ["ri_treaty"] = 3, ["loss_event"] = 1, ["claim"] = 1, ["claim_reserve"] = 1, ["claim_payment"] = 2, ["claim_cession"] = 1 });
    }

    /// <summary>
    /// R10 (legacy PR #350): three EUR XL treaties of m0-16 to m1-15, so each treaty's premium falls in two months — earned over
    /// the treaty's days, both ends included, half away from zero; each month's share valued at that month end's own rate,
    /// treaty by treaty, then summed. The earlier cases all ended in m0, so a report that skipped every share after a treaty's
    /// first month passed them. The 16.50 treaty makes "sum first, then value" differ by a tetri in both months.
    /// </summary>
    private async Task R10()
    {
        const string k = "R10";
        string end0Rate = "2.987600", end1Rate = "3.345600";
        var (from, to) = (Day($"{M0}-16"), Day($"{M1}-15"));
        var total = to.DayNumber - from.DayNumber + 1;
        var inFirst = End0.DayNumber - from.DayNumber + 1;
        BigInteger[] premiums = [36500, T("90071992547408.01"), 1650];
        var shares = premiums.Select(p =>
        {
            var first = EarnedTo(p, inFirst, total);
            var second = p - first;
            if (first + second != p || first <= 0 || second <= 0) throw new Xunit.Sdk.XunitException($"R10: {p} does not split into two months");
            return (First: first, Second: second);
        }).ToList();
        BigInteger Treaty(int month) => shares.Aggregate(BigInteger.Zero, (sum, s) => sum + (month == 0 ? AtRateUp(s.First, end0Rate) : AtRateUp(s.Second, end1Rate)));

        await W.RateAsync("EUR", Iso(Day($"{M0}-20")), 3.1m);
        await W.RateAsync("EUR", Iso(End0), 2.9876m);
        await W.RateAsync("EUR", Iso(End1), 3.3456m);
        await PartyAsync(Id($"{k}:re"), $"{Tag} {k} გადამზღვევი (გამოგონილი)", $"{Tag}-{k}-re");
        await TreatyAsync(k, "T1", "excess_of_loss", "EUR", priority: "100.00", limit: "1000.00", premium: "365.00", from: from, to: to);
        await TreatyAsync(k, "T2", "excess_of_loss", "EUR", priority: "100.00", limit: "1000.00", premium: "90071992547408.01", from: from, to: to);
        await TreatyAsync(k, "T3", "excess_of_loss", "EUR", priority: "100.00", limit: "1000.00", premium: "16.50", from: from, to: to);

        await ReportAsync($"R10 {M0}: the treaties' first {inFirst} days of {total}", M0, new(Fig.Zero, Nothing, new([0, 0, 0], [0, 0, 0, 0, 0, 0], Treaty(0))));
        await SnapshotAsync("R10", M0, "R10:m0");
        await ReportAsync($"R10 {M1}: the rest, to the 15th", M1, new(Fig.Zero, Nothing, new([0, 0, 0], [0, 0, 0, 0, 0, 0], Treaty(1))));
        await SnapshotAsync($"R10 {M0} after the next month was read", M0, "R10:m0", compare: true);
        await CountsAsync("R10", new() { ["party"] = 1, ["ri_treaty"] = 3 });
        if (October)
        {
            // By hand (legacy PR #350, re-checked with Python integers): 188.39 + 176.61, 46 488 770 347 049.30 + 43 583 222 200 358.71,
            // 8.52 + 7.98; summing the shares before valuing would give …432.78 and …137.66.
            Pinned("R10 October", "138889850289432.77", Treaty(0));
            Pinned("R10 November", "145812028194137.67", Treaty(1));
        }
    }
}
