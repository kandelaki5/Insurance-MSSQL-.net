using System.Globalization;
using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>
/// The premium schedule, receipts and their allocation as stored (legacy src/lib/billing.ts). Amounts come from the
/// database as decimal(18, 2) and are handled in whole tetri (long), exactly as the legacy code did with bigints.
/// </summary>
public static class Billing
{
    public sealed record InstallmentRow(Guid Id, DateOnly DueOn, decimal Amount);

    public sealed record AllocationRow(decimal Amount, DateOnly ReceivedOn, string Method);

    /// <summary>A receipt of the client with money not yet allocated to any instalment (an advance).</summary>
    public sealed record AdvanceRow(Guid Id, DateOnly ReceivedOn, decimal AmountGel, decimal AvailableGel, string Method, string? Reference);

    /// <summary>A decimal(…, 2) amount as whole tetri.</summary>
    public static long Tetri(decimal amount)
    {
        var scaled = amount * 100m;
        if (scaled != decimal.Truncate(scaled)) throw new ArgumentException($"not an amount in tetri: {amount.ToString(CultureInfo.InvariantCulture)}");
        return (long)scaled;
    }

    /// <summary>Whole tetri as a decimal with two places (as PostgreSQL prints numeric(…, 2)).</summary>
    public static decimal FromTetri(long tetri) => decimal.Divide(tetri, 100m) + 0.00m;

    /// <summary>Whole tetri as numeric(…, 2) text, as PostgreSQL prints it ('-0.05', '0.01').</summary>
    public static string TetriText(long tetri)
    {
        var sign = tetri < 0 ? "-" : "";
        var whole = BigInteger.Abs(tetri);
        return $"{sign}{whole / 100}.{(whole % 100).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}";
    }

    /// <summary>Whole tetri for reading, as en-US toLocaleString shows two decimals ('-1,234.50'), but exactly.</summary>
    public static string GroupedTetri(long tetri)
    {
        var text = TetriText(tetri);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var whole = text[..dot];
        var sign = whole.StartsWith('-') ? "-" : "";
        var digits = sign == "" ? whole : whole[1..];
        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(digits[i]);
        }
        return sign + grouped + text[dot..];
    }

    /// <summary>
    /// The client's receipts with an unallocated remainder, oldest first. A reversed receipt has nothing left to
    /// allocate, and a claim offset is not money received.
    /// </summary>
    public static async Task<IReadOnlyList<AdvanceRow>> LoadAdvancesAsync(Tx db, Guid partyId, Guid? paymentId = null) =>
        (await db.QueryAsync<AdvanceRow>("""
            select p.id as Id, p.received_on as ReceivedOn, p.amount_gel as AmountGel,
                   p.amount_gel - coalesce(sum(a.amount_gel), 0) as AvailableGel, p.method as Method, p.reference as Reference
            from core.payment p left join core.payment_allocation a on a.payment_id = p.id
            where p.payer_party_id = @partyId and p.kind = N'receipt' and p.method <> N'claim_offset'
              and (@paymentId is null or p.id = @paymentId)
              and not exists (select 1 from core.payment r where r.reverses_payment_id = p.id)
            group by p.id, p.received_on, p.amount_gel, p.method, p.reference, p.created_at
            having p.amount_gel - coalesce(sum(a.amount_gel), 0) > 0
            order by p.received_on, p.created_at
            """, new { partyId, paymentId })).ToList();

    /// <summary>
    /// Positive tetri in a currency times its stored rate (GEL per unit, six places) as GEL tetri, rounded half up —
    /// on the exact rate, with no floating point.
    /// </summary>
    public static long TimesRate(long tetri, decimal rate)
    {
        var micro = rate * 1_000_000m;
        if (micro != decimal.Truncate(micro) || rate < 0) throw new ArgumentException($"not a rate: {rate.ToString(CultureInfo.InvariantCulture)}");
        var scale = new BigInteger(1_000_000);
        var product = new BigInteger(tetri) * new BigInteger(micro);
        return (long)((product * 2 + scale) / (scale * 2));
    }

    /// <summary>Money a client's receipt holds beyond an instalment's amount (after a reduction): what one receipt can give back from one instalment.</summary>
    public sealed record ReleasableRow(
        Guid PaymentId, DateOnly ReceivedOn, string Method, string? Reference, decimal PaymentGel,
        Guid PolicyId, string PolicyNumber, string Currency, Guid InstallmentId, decimal Rate,
        /* In the policy's currency. */ decimal Excess, decimal ExcessGel);

    private sealed record ReleasableInstallment(Guid PolicyId, string PolicyNumber, string Currency, Guid InstallmentId, decimal Amount);

    private sealed record ReleasableLine(Guid InstallmentId, Guid PaymentId, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn,
        bool Eligible, DateOnly ReceivedOn, string Method, string? Reference, decimal PaymentGel);

    /// <summary>
    /// The client's overpayments (owner, 07.10.2026): an instalment that, after an approved reduction, holds more than its
    /// amount as of <paramref name="asOf"/>. The excess is attributed to the receipts that settled it, the latest
    /// allocation first, and only to money receipts (not a claim set-off, not a reversed receipt); what a set-off holds is
    /// left where it is. Each row is what one receipt can release from one instalment, so a transfer can book exactly
    /// those lines. <paramref name="exceptPolicyId"/> leaves one policy out (the one being paid: its own excess is
    /// refunded, not moved).
    /// </summary>
    public static async Task<IReadOnlyList<ReleasableRow>> LoadReleasableAsync(Tx db, Guid partyId, DateOnly asOf, Guid? exceptPolicyId = null)
    {
        var instalments = (await db.QueryAsync<ReleasableInstallment>("""
            select p.id as PolicyId, p.number as PolicyNumber, p.premium_currency as Currency, i.id as InstallmentId,
                   i.amount + coalesce((select sum(j.change) from core.installment_adjustment j where j.installment_id = i.id and j.booked_on <= @asOf), 0) as Amount
            from core.policy p join core.contract c on c.id = p.contract_id join core.installment i on i.policy_id = p.id
            where c.policyholder_id = @partyId and p.status <> N'draft' and (@exceptPolicyId is null or p.id <> @exceptPolicyId)
            order by p.number, i.due_on
            """, new { partyId, asOf, exceptPolicyId })).ToList();
        if (instalments.Count == 0) return [];
        var lines = (await db.QueryAsync<ReleasableLine>("""
            select a.installment_id as InstallmentId, a.payment_id as PaymentId, a.amount as Amount, a.amount_gel as AmountGel, a.rate as Rate,
                   a.booked_on as BookedOn,
                   convert(bit, case when pm.kind = N'receipt' and pm.method <> N'claim_offset'
                                      and not exists (select 1 from core.payment r where r.reverses_payment_id = pm.id) then 1 else 0 end) as Eligible,
                   pm.received_on as ReceivedOn, pm.method as Method, pm.reference as Reference, pm.amount_gel as PaymentGel
            from core.payment_allocation a join core.payment pm on pm.id = a.payment_id
            where a.installment_id in @ids
            order by a.installment_id, a.booked_on desc, a.created_at desc
            """, new { ids = instalments.Select(i => i.InstallmentId).ToList() })).ToList();

        var result = new List<ReleasableRow>();
        foreach (var inst in instalments)
        {
            var own = lines.Where(l => l.InstallmentId == inst.InstallmentId).ToList();
            var excess = own.Sum(l => Tetri(l.Amount)) - Tetri(inst.Amount);
            if (excess <= 0) continue;
            // Net holding of each receipt on this instalment, in both currencies (earlier releases net off), latest
            // first. A release of the whole holding takes its GEL remainder exactly, and a partial one never more than
            // that remainder, so repeated releases add up to what was allocated and nothing is lost to rounding (H57).
            var holders = new List<(ReleasableLine Payment, long Net, long NetGel)>();
            foreach (var l in own)
            {
                var at = holders.FindIndex(h => h.Payment.PaymentId == l.PaymentId);
                if (at >= 0) holders[at] = (holders[at].Payment, holders[at].Net + Tetri(l.Amount), holders[at].NetGel + Tetri(l.AmountGel));
                else holders.Add((l, Tetri(l.Amount), Tetri(l.AmountGel)));
            }
            foreach (var (payment, net, netGel) in holders)
            {
                if (excess <= 0) break;
                if (!payment.Eligible || net <= 0) continue;
                var take = Math.Min(excess, net);
                excess -= take;
                var atRate = TimesRate(take, payment.Rate);
                var gel = take == net ? netGel : Math.Min(atRate, netGel);
                result.Add(new ReleasableRow(payment.PaymentId, payment.ReceivedOn, payment.Method, payment.Reference, payment.PaymentGel,
                    inst.PolicyId, inst.PolicyNumber, inst.Currency, inst.InstallmentId, payment.Rate, FromTetri(take), FromTetri(gel)));
            }
        }
        return result;
    }

    public static Currency ParseCurrency(string code) => Enum.Parse<Currency>(code.Trim(), ignoreCase: false);

    public static IReadOnlyList<Installment> ToInstallments(IEnumerable<InstallmentRow> rows, Currency currency) =>
        rows.Select(r => new Installment(r.Id.ToString(), r.DueOn, new Money(Tetri(r.Amount), currency))).ToList();

    /// <summary>Allocation rows (policy currency) as credits against the premium receivable.</summary>
    public static IReadOnlyList<Credit> ToCredits(IEnumerable<AllocationRow> rows, Currency currency) =>
        rows.Select(r => new Credit(r.ReceivedOn, new Money(Tetri(r.Amount), currency), r.Method == "claim_offset" ? CreditKind.ClaimOffset : CreditKind.Payment)).ToList();

    /// <summary>
    /// The policy's schedule as it stood at the end of <paramref name="asOf"/> (all adjustments when null): the amount
    /// originally scheduled plus every adjustment booked by then. Adjustments are dated, so a reduction booked in April
    /// leaves January's receivable as it was reported (docs/04 rule რ14).
    /// </summary>
    public static async Task<IReadOnlyList<InstallmentRow>> LoadScheduleAsync(Tx db, Guid policyId, DateOnly? asOf = null) =>
        (await db.QueryAsync<InstallmentRow>("""
            select i.id as Id, i.due_on as DueOn,
                   i.amount + coalesce((select sum(j.change) from core.installment_adjustment j
                                        where j.installment_id = i.id and (@asOf is null or j.booked_on <= @asOf)), 0) as Amount
            from core.installment i
            where i.policy_id = @policyId
            order by i.due_on
            """, new { policyId, asOf })).ToList();

    /// <summary>
    /// The schedules of many policies as they stood at the end of <paramref name="asOf"/>, by policy — LoadSchedule's rule
    /// for each, in two reads whatever the number of policies. A policy without instalments has none here.
    /// </summary>
    public static async Task<Dictionary<Guid, List<InstallmentRow>>> LoadSchedulesAsync(Tx db, IReadOnlyCollection<Guid> policyIds, DateOnly asOf)
    {
        var schedules = new Dictionary<Guid, List<InstallmentRow>>();
        if (policyIds.Count == 0) return schedules;
        var installments = await InChunksAsync(policyIds, ids => db.QueryAsync<(Guid PolicyId, Guid Id, DateOnly DueOn, decimal Amount)>("""
            select i.policy_id, i.id, i.due_on, i.amount
            from core.installment i
            where i.policy_id in @ids
            order by i.policy_id, i.due_on
            """, new { ids }));
        var adjusted = await InChunksAsync(policyIds, ids => db.QueryAsync<(Guid InstallmentId, decimal Change)>("""
            select j.installment_id, coalesce(sum(j.change), 0)
            from core.installment_adjustment j join core.installment i on i.id = j.installment_id
            where i.policy_id in @ids and j.booked_on <= @asOf
            group by j.installment_id
            """, new { ids, asOf }));
        var change = adjusted.ToDictionary(a => a.InstallmentId, a => Tetri(a.Change));
        foreach (var row in installments.OrderBy(r => r.PolicyId.ToString(), StringComparer.Ordinal).ThenBy(r => r.DueOn))
        {
            if (!schedules.TryGetValue(row.PolicyId, out var list)) schedules[row.PolicyId] = list = [];
            list.Add(new InstallmentRow(row.Id, row.DueOn, FromTetri(Tetri(row.Amount) + change.GetValueOrDefault(row.Id))));
        }
        return schedules;
    }

    /// <summary>SQL Server takes at most 2100 parameters in one statement; long id lists are read in parts.</summary>
    public static async Task<List<T>> InChunksAsync<T>(IReadOnlyCollection<Guid> ids, Func<List<Guid>, Task<IEnumerable<T>>> read, int size = 2000)
    {
        var all = new List<T>();
        foreach (var chunk in ids.Chunk(size)) all.AddRange(await read(chunk.ToList()));
        return all;
    }

    private const string InsertAdjustment = """
        insert into core.installment_adjustment (installment_id, booked_on, change, reason, created_by)
        values (@installmentId, @bookedOn, @change, @reason, @userId)
        """;

    /// <summary>
    /// Takes an approved reduction off the schedule in the company's chosen order (finance ReduceInstallments; owner,
    /// 07.10.2026), booking dated adjustments. Nothing is ever deleted or rewritten. Refused (ArgumentOutOfRangeException,
    /// legacy RangeError) when the reduction exceeds the whole schedule.
    /// </summary>
    public static async Task ReduceScheduleByAsync(Tx tx, Guid policyId, Currency currency, long reductionMinor, ReductionOrder order, DateOnly bookedOn, string reason, Guid userId)
    {
        var installments = ToInstallments(await LoadScheduleAsync(tx, policyId), currency);
        var settled = (await tx.QueryAsync<(Guid InstallmentId, decimal Settled)>("""
            select a.installment_id, sum(a.amount) from core.payment_allocation a
            join core.installment i on i.id = a.installment_id where i.policy_id = @policyId group by a.installment_id
            """, new { policyId })).ToDictionary(r => r.InstallmentId.ToString(), r => Tetri(r.Settled));
        var target = Receivable.ReduceInstallments(installments, settled, reductionMinor, order);
        foreach (var i in installments)
        {
            var change = target.GetValueOrDefault(i.Id) - i.Amount.Minor;
            if (change == 0) continue;
            await tx.ExecuteAsync(InsertAdjustment, new { installmentId = Guid.Parse(i.Id), bookedOn, change = FromTetri(change), reason, userId });
        }
    }

    /// <summary>
    /// Makes the schedule adjustments booked after <paramref name="bookedOn"/> count from <paramref name="bookedOn"/> (a
    /// later cancellation brought forward to a total loss, H24). Nothing is edited: each is undone by an opposite
    /// adjustment on its own day and booked again on <paramref name="bookedOn"/>, so the full schedule is unchanged and
    /// every earlier day keeps what it showed.
    /// </summary>
    public static async Task AdvanceScheduleAdjustmentsAsync(Tx tx, Guid policyId, DateOnly bookedOn, string reason, Guid userId)
    {
        var later = (await tx.QueryAsync<(Guid InstallmentId, DateOnly BookedOn, decimal Change)>("""
            select j.installment_id, j.booked_on, j.change
            from core.installment_adjustment j join core.installment i on i.id = j.installment_id
            where i.policy_id = @policyId and j.booked_on > @bookedOn
            order by j.booked_on, j.created_at
            """, new { policyId, bookedOn })).ToList();
        foreach (var j in later)
        {
            await tx.ExecuteAsync(InsertAdjustment, new { installmentId = j.InstallmentId, bookedOn = j.BookedOn, change = -j.Change, reason, userId });
            await tx.ExecuteAsync(InsertAdjustment, new { installmentId = j.InstallmentId, bookedOn, change = j.Change, reason, userId });
        }
    }

    /// <summary>
    /// Brings the schedule down to <paramref name="targetMinor"/> (the retained premium after a cancellation, or the
    /// original less an approved reduction) by booking dated adjustments: dates and order are kept, the last instalments
    /// shrink or go to zero. Nothing is ever deleted or rewritten.
    /// </summary>
    public static async Task ReduceScheduleAsync(Tx tx, Guid policyId, Currency currency, long targetMinor, DateOnly bookedOn, string reason, Guid userId)
    {
        var installments = ToInstallments(await LoadScheduleAsync(tx, policyId), currency);
        var kept = Receivable.RescheduleInstallments(installments, targetMinor).ToDictionary(i => i.Id, i => i.Amount.Minor);
        foreach (var i in installments)
        {
            var change = kept.GetValueOrDefault(i.Id) - i.Amount.Minor;
            if (change == 0) continue;
            await tx.ExecuteAsync(InsertAdjustment, new { installmentId = Guid.Parse(i.Id), bookedOn, change = FromTetri(change), reason, userId });
        }
    }
}
