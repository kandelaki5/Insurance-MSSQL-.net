using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Finance;

/// <summary>One instalment of a payment plan: its due date and amount in minor units.</summary>
public sealed record PlanInstallment(DateOnly DueOn, long Minor);

/// <summary>Standard payment plans (plan.ts). A plan is its number of payments.</summary>
public static class Plan
{
    /// <summary>
    /// The standard payment plans (owner, 13.10.2026; docs/05 a16): 1 — the year at once, 4 — quarterly, 12 — monthly, 11 —
    /// the first and the last month in advance, then one month each. Two payments, or any other plan, is an individual
    /// schedule the operator enters.
    /// </summary>
    public static readonly IReadOnlyList<int> PaymentPlans = [1, 4, 11, 12];

    public static bool IsPaymentPlan(int value) => PaymentPlans.Contains(value);

    /// <summary>The 11-payment plan needs a term of exactly twelve calendar months: its first payment is two of them.</summary>
    public static bool FitsPlan(int plan, DateOnly from, DateOnly to) => plan != 11 || AddMonths(from, 12) == to;

    /// <summary>
    /// The instalments of a standard plan, in minor units, by due date.
    /// - 1, 4, 12: equal instalments on calendar months (01.01, 01.04, 01.07, 01.10 for four on a yearly policy); a term that
    ///   does not divide into whole months falls back to equal day spans. The last takes the rounding remainder.
    /// - 11 (a twelve-month term): a twelfth of the premium, rounded down, is one month; the first instalment, on the start,
    ///   is two months (the first and the last), then ten monthly instalments from the second month; the last takes the
    ///   rounding remainder. 1 200.00 from 01.01: 200.00 on 01.01, then 100.00 on 01.02 … 01.11 (owner, 13.10.2026).
    /// Other plan numbers are laid out by the first rule, as the old code did (0 gives no instalments).
    /// </summary>
    public static List<PlanInstallment> PlanInstallments(int plan, DateOnly from, DateOnly to, long totalMinor)
    {
        if (plan == 11)
        {
            if (!FitsPlan(plan, from, to)) throw new ArgumentOutOfRangeException(null, "the 11-payment plan needs a term of twelve calendar months");
            var month = JsMath.FloorDiv(totalMinor, 12);
            var parts = Enumerable.Range(0, 11).Select(k => new PlanInstallment(AddMonths(from, k), k == 0 ? 2 * month : month)).ToList();
            parts[10] = parts[10] with { Minor = totalMinor - parts.Take(10).Sum(p => p.Minor) };
            return parts;
        }
        if (plan < 0) throw new ArgumentOutOfRangeException(null, "Invalid array length");
        if (plan == 0) return [];
        var termDays = DaysBetween(from, to);
        var termMonths = JsMath.Round(termDays / 30.4375m);
        var monthly = termMonths >= plan && termMonths % plan == 0;
        var part = JsMath.FloorDiv(totalMinor, plan);
        return Enumerable.Range(0, plan).Select(k => new PlanInstallment(
            monthly ? AddMonths(from, (int)(k * termMonths / plan)) : AddDays(from, (int)JsMath.FloorDiv((long)k * termDays, plan)),
            k == plan - 1 ? totalMinor - part * (plan - 1) : part)).ToList();
    }
}
