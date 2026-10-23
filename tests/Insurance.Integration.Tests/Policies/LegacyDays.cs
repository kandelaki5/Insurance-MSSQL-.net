using System.Globalization;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The legacy fd-net-transfer-chain.test.ts worlds on their own days (03.11.2026 the request, 04.11.2026 the approval), the database's
/// clock standing on each action's day: shared by the integration tests (GapsReceiptChainTests) and the card's web tests
/// (GapsLaterDayCardTests).
/// </summary>
public static class LegacyDays
{
    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    /// <summary>The legacy chain world on its days (fd-net-transfer-chain "a new receipt …"): a policy from 01.10.2026 owing I1 (01.11) and I2 (01.12), threshold 10 000.00; USD 2.70 on 03.11, 2.80 on 04.11.</summary>
    public static async Task<(CurrentUser Admin, CurrentUser Accountant, World.Policy P, List<Guid> Inst)> ChainAsync(World w, string currency, string i1, string i2)
    {
        await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
        await w.RateAsync("USD", D(2026, 11, 3), 2.7m);
        await w.RateAsync("USD", D(2026, 11, 4), 2.8m);
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var from = D(2026, 10, 1);
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), from, currency, currency == "GEL" ? 1m : 2.7m, D(i1) + D(i2),
            [(D(2026, 11, 1).DayNumber - from.DayNumber, D(i1)), (D(2026, 12, 1).DayNumber - from.DayNumber, D(i2))]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var inst = await w.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id });
        On(w, D(2026, 11, 3));
        return (admin, accountant, p, inst);
    }

    public static Task<List<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>> LinesOfAsync(World w, Guid payment) =>
        w.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly)>("select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @payment order by amount_gel desc", new { payment });

    /// <summary>
    /// The legacy cross world on its days: A (GEL) owing 10 000.00 due 15.10.2026 and B (USD, at 2.7) owing 100.00 due 01.11.2026, one client,
    /// both from 01.10.2026; threshold 10 000.00; USD 2.70 on 20.10, 2.80 on 04.11. 10 270.00 GEL received and recorded for A on 20.10;
    /// October then closed by the real close (accountant, 03.11); on 03.11 B takes the 270.00 left at the receipt day's 2.7 and the reversal
    /// is asked from A. The approval is the caller's (the legacy 04.11).
    /// </summary>
    public static async Task<(CurrentUser Admin, World.Policy A, World.Policy B, Guid IA, Guid IB, Guid R, Guid Request)> CrossUsdAsync(World w)
    {
        await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
        await w.RateAsync("USD", D(2026, 10, 20), 2.7m);
        await w.RateAsync("USD", D(2026, 11, 4), 2.8m);
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var from = D(2026, 10, 1);
        var a = await w.PendingPolicyAsync(holder, from, "GEL", 1m, 10000m, [(14, 10000m)]);
        var b = await w.PendingPolicyAsync(holder, from, "USD", 2.7m, 100m, [(31, 100m)]);
        foreach (var p in new[] { a, b }) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var ia = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = a.Id });
        var ib = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = b.Id });
        On(w, D(2026, 10, 20));
        Assert.Equal($"/policies/{a.Id}?paid=27000", (await w.PayAsync(accountant, a.Id, "10270.00", D(2026, 10, 20), reference: "R-CROSS")).Redirect);
        var r = await w.ScalarAsync<Guid>("select id from core.payment where reference = N'R-CROSS'");
        On(w, D(2026, 11, 3));
        Assert.Equal(new PeriodClose.Answer("done", "2026-10-01"), await PeriodClose.CloseAsync(w.App, accountant, "2026-10-01"));
        Assert.Equal($"/policies/{b.Id}?advance=allocated&gel=27000&left=0", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, accountant, b.Id.ToString(), r.ToString()));
        Assert.Equal([(ia, 10000m, 10000m, 1m, D(2026, 10, 20)), (ib, 100m, 270m, 2.7m, D(2026, 11, 3))], await LinesOfAsync(w, r));
        Assert.Equal($"/policies/{a.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, accountant, r.ToString(), a.Id.ToString(), "შეცდომით ჩაირიცხა"));
        var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @r", new { r });
        Assert.Equal([(1, ia, 10000m, 10000m, 1m), (2, ib, 100m, 270m, 2.7m)], await w.QueryAsync<(int, Guid, decimal, decimal, decimal)>(
            "select position, installment_id, amount, amount_gel, rate from core.receipt_reversal_request_line where request_id = @request order by position", new { request }));
        return (admin, a, b, ia, ib, r, request);
    }
}
