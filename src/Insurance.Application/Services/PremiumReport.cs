using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.ReportMath;

namespace Insurance.Application.Services;

/// <summary>
/// Premium figures of one calendar month in GEL, per insurance class, from the ledger (legacy src/lib/premium-report.ts;
/// docs/04; financial reporting order №02/2013; statistical order №03/2013). Every ledger row is valued at its own fixed
/// rate; a cancellation earns through the original it reverses, so the month's figures never move when rates change later.
/// Every figure is whole tetri, exactly (the old bigint; the *_DECIMAL_PRECISION_20261019 fixes).
/// <para>Other slices (the financial director's panel, the regulatory forms) call <see cref="BuildAsync(Tx, Valuation, string)"/>
/// and show figures with <see cref="ReportMoney"/> and the month rule <see cref="ReportMonth"/>.</para>
/// </summary>
public static partial class PremiumReport
{
    public sealed record ClassRow(
        string ClassCode, string ClassName,
        /* Policies that came into force in the month (statistical count). */ int PoliciesIssued,
        /* Premium of policies that came into force in the month, before any cancellation. */ BigInteger StatisticalWrittenGel,
        /* Every premium movement booked in the month, cancellations included. */ BigInteger FinancialWrittenGel,
        BigInteger EarnedGel, BigInteger UnearnedGel,
        /* Premium ceded to reinsurers in the month. */ BigInteger CededWrittenGel,
        /* Reinsurer's share in UPR at month end, at each policy's fixed rate (order №01 art. 5). */ BigInteger CededUnearnedGel,
        /* Reinsurance commission on the month's cessions. */ BigInteger RiCommissionGel);

    public sealed record Totals(int PoliciesIssued, BigInteger StatisticalWrittenGel, BigInteger FinancialWrittenGel, BigInteger EarnedGel,
        BigInteger UnearnedGel, BigInteger CededWrittenGel, BigInteger CededUnearnedGel, BigInteger RiCommissionGel);

    public sealed record ClaimsRow(
        string ClassCode, string ClassName,
        /* Gross indemnity paid in the month, at each payment's own rate. */ BigInteger PaidGel,
        /* Reported-but-not-settled reserve at month end, at the month-end rate. */ BigInteger RbnsGel,
        /* Statutory IBNR: 5% (health 2%) of the year-to-date net written premium (order №01 art. 7). */ BigInteger IbnrGel,
        /* Subrogation and salvage received in the month. */ BigInteger RecoveredGel,
        /* Reinsurer's share in the month's paid claims (recognised when paid, order №02 art. 4.6). */ BigInteger ReinsurerPaidGel,
        /* Reinsurer's share in RBNS at month end, at the month-end rate. */ BigInteger ReinsurerRbnsGel);

    public sealed record ClaimsTotals(BigInteger PaidGel, BigInteger RbnsGel, BigInteger IbnrGel, BigInteger RecoveredGel, BigInteger ReinsurerPaidGel, BigInteger ReinsurerRbnsGel);

    /// <summary>The receivable at month end of one currency: in whole minor units of it, and in tetri at the month-end rate.</summary>
    public sealed record ReceivableRow(string Currency, BigInteger Outstanding, BigInteger Overdue, BigInteger OutstandingGel, decimal Rate, DateOnly RateDate);

    public sealed record Report(
        string Month, DateOnly From, DateOnly To,
        IReadOnlyList<ClassRow> Classes, Totals Totals,
        IReadOnlyList<ReceivableRow> Receivables,
        IReadOnlyList<ClaimsRow> Claims, ClaimsTotals ClaimsTotals,
        /* Premium of treaties not tied to policies (excess of loss, deposit premium) spread over the month. */ BigInteger TreatyPremiumGel);

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])\z")]
    private static partial Regex MonthText();

    /// <summary>The month a report is asked for (YYYY-MM); anything else — the month of <paramref name="today"/> (the caller's Db.Today(), Tbilisi; legacy reportMonth).</summary>
    public static string ReportMonth(string? requested, DateOnly today) =>
        requested is not null && MonthText().IsMatch(requested) ? requested : today.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>A report figure (tetri) as the report shows it: 1234567 → 12,345.67, exactly (legacy reportMoney).</summary>
    public static string ReportMoney(BigInteger value) => Grouped(value);

    /// <summary>The report with the official rates (the day's, else the last stored one), fetched from the National Bank when missing.</summary>
    public static Task<Report> BuildAsync(Tx db, Rates rates, string month) => BuildAsync(db, rates.LatestRateOnOrBeforeAsync, month);

    private sealed record LedgerDb(Guid Id, Guid? CancelsId, string ClassCode, string ClassName, int SortOrder, decimal AmountGel,
        DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, string Kind, Guid PolicyId);

    private sealed record CessionDb(Guid Id, Guid? CancelsId, string ClassCode, decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);

    private sealed class ClassBucket
    {
        public required string Name;
        public required int Sort;
        public List<Guid> Txs { get; } = [];
        public BigInteger StatWritten;
        public BigInteger FinWritten;
        public HashSet<Guid> Issued { get; } = [];
    }

    private sealed class CededBucket
    {
        public List<Guid> Txs { get; } = [];
        public BigInteger Written;
        public BigInteger Commission;
    }

    /// <summary>
    /// Links each row to the row it cancels, once every row is read, whatever order they came in (review H14; linkLedger's rule):
    /// a cancellation whose original is not among the rows is an error, never a plain negative premium.
    /// </summary>
    internal static Dictionary<Guid, ExactPremiumTx> LinkExact(IReadOnlyList<(Guid Id, Guid? CancelsId, ExactPremiumTx Tx)> rows, string what)
    {
        var byId = new Dictionary<Guid, (Guid? CancelsId, ExactPremiumTx Tx)>();
        foreach (var r in rows) byId[r.Id] = (r.CancelsId, r.Tx);
        foreach (var r in rows)
            if (r.CancelsId is { } c && !byId.ContainsKey(c))
                throw new InvalidOperationException($"{what} {Lower(r.Id)} cancels {Lower(c)}, which is not among the rows read");
        var linked = new Dictionary<Guid, ExactPremiumTx>();
        var linking = new HashSet<Guid>();
        ExactPremiumTx Link(Guid id)
        {
            if (linked.TryGetValue(id, out var done)) return done;
            var (cancels, tx) = byId[id];
            if (cancels is null) return linked[id] = tx;
            if (!linking.Add(id)) throw new InvalidOperationException($"{what} {Lower(id)} cancels itself through {Lower(cancels.Value)}");
            var result = tx with { Cancels = Link(cancels.Value) };
            linking.Remove(id);
            return linked[id] = result;
        }
        foreach (var r in rows) Link(r.Id);
        return linked;
    }

    internal static string Lower(Guid id) => id.ToString("D");

    /// <summary>earnedInPeriod's rule exactly: the earned at the period's end less the earned the day before it starts.</summary>
    internal static BigInteger EarnedIn(IEnumerable<ExactPremiumTx> txs, DateOnly from, DateOnly to) =>
        txs.Aggregate(BigInteger.Zero, (sum, tx) => sum + EarnedPremium.EarnedAsOfExact(tx, to) - EarnedPremium.EarnedAsOfExact(tx, from.AddDays(-1)));

    /// <summary>premiumPosition's unearned exactly: of every row booked by the day, its amount less what it has earned.</summary>
    internal static BigInteger Unearned(IEnumerable<ExactPremiumTx> txs, DateOnly asOf) =>
        txs.Aggregate(BigInteger.Zero, (sum, tx) => asOf < tx.BookedOn ? sum : sum + tx.Minor - EarnedPremium.EarnedAsOfExact(tx, asOf));

    /// <summary>
    /// finance/reinsurance.ts treatyPremiumInPeriod exactly: the treaty premium earned over its period, both dates inclusive, and the
    /// part of a reporting period the cumulative earned at its end less that the day before it starts (nothing before the treaty starts).
    /// </summary>
    internal static BigInteger TreatyPremiumInPeriodExact(BigInteger premium, DateOnly treatyFrom, DateOnly treatyTo, DateOnly from, DateOnly to)
    {
        var tx = new ExactPremiumTx(premium, treatyFrom, treatyFrom, treatyTo.AddDays(1));
        var before = from <= treatyFrom ? BigInteger.Zero : EarnedPremium.EarnedAsOfExact(tx, from.AddDays(-1));
        return EarnedPremium.EarnedAsOfExact(tx, to) - before;
    }

    /// <summary>
    /// A policy's receivable at a day, exactly (receivableAsOf's rule): the credits received by then settle the instalments oldest due
    /// date first, signed as stored; what is left of an instalment due before the day is overdue; in GEL at the rate to the millionth,
    /// half away from zero. A foreign currency without a rate is an error.
    /// </summary>
    internal static (BigInteger Outstanding, BigInteger Overdue, BigInteger OutstandingGel) ExactReceivable(
        IReadOnlyList<Billing.InstallmentRow> schedule, IEnumerable<Billing.AllocationRow> credits, DateOnly asOf, string currency, decimal rate)
    {
        var available = credits.Where(c => c.ReceivedOn <= asOf).Aggregate(BigInteger.Zero, (sum, c) => sum + Tetri(c.Amount));
        BigInteger outstanding = 0, overdue = 0;
        foreach (var row in schedule.OrderBy(r => r.DueOn))
        {
            var amount = Tetri(row.Amount);
            var settled = available < amount ? available : amount;
            available -= settled;
            outstanding += amount - settled;
            if (row.DueOn < asOf) overdue += amount - settled;
        }
        if (currency != "GEL" && !(rate > 0)) throw new InvalidOperationException($"No {currency} rate for {asOf:yyyy-MM-dd}");
        var outstandingGel = currency == "GEL" ? outstanding : DivRound(outstanding * Micro(rate), RateScale);
        return (outstanding, overdue, outstandingGel);
    }

    /// <summary>The month's report (legacy buildPremiumReport), every currency valued with <paramref name="valuation"/>.</summary>
    public static async Task<Report> BuildAsync(Tx db, Valuation valuation, string month)
    {
        var from = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = Dates.AddMonths(from, 1).AddDays(-1);

        // The month-end valuation of a currency, asked once for this report and shared by every row that needs it
        // (PR299_MONTH_REPORT_REPEATED_FX_LOOKUP_20261018). Kept for this call only.
        var valuations = new Dictionary<string, Task<(decimal Rate, DateOnly Date)>>(StringComparer.Ordinal);
        Task<(decimal Rate, DateOnly Date)> ValuationAt(string currency, DateOnly date)
        {
            var key = $"{currency} {date:yyyy-MM-dd}";
            if (!valuations.TryGetValue(key, out var v)) valuations[key] = v = valuation(currency, date);
            return v;
        }

        var rows = (await db.QueryAsync<LedgerDb>("""
            select t.id as Id, t.cancels_transaction_id as CancelsId, l.class_code as ClassCode, c.name_ka as ClassName, c.sort_order as SortOrder,
                   t.amount_gel as AmountGel, t.booked_on as BookedOn, t.cover_from as CoverFrom, t.cover_to as CoverTo, t.kind as Kind, l.policy_id as PolicyId
            from core.premium_transaction t
            join core.policy_line l on l.id = t.policy_line_id
            join core.insurance_class c on c.code = l.class_code
            where t.booked_on <= @to
            order by t.created_at, t.id
            """, new { to })).ToList();

        var byClass = new Dictionary<string, ClassBucket>(StringComparer.Ordinal);
        var classOrder = new List<string>();
        var ledgerRows = new List<(Guid, Guid?, ExactPremiumTx)>();
        foreach (var row in rows)
        {
            var written = Tetri(row.AmountGel);
            ledgerRows.Add((row.Id, row.CancelsId, new ExactPremiumTx(written, row.BookedOn, row.CoverFrom, row.CoverTo)));
            if (!byClass.TryGetValue(row.ClassCode, out var bucket))
            {
                byClass[row.ClassCode] = bucket = new ClassBucket { Name = row.ClassName, Sort = row.SortOrder };
                classOrder.Add(row.ClassCode);
            }
            bucket.Txs.Add(row.Id);
            if (row.BookedOn >= from && row.BookedOn <= to) bucket.FinWritten += written;
            if (row.Kind == "issue" && row.CoverFrom >= from && row.CoverFrom <= to)
            {
                bucket.StatWritten += written;
                bucket.Issued.Add(row.PolicyId);
            }
        }
        var ledger = LinkExact(ledgerRows, "Ledger row");

        // Reinsurer's share: every cession mirrors its ledger row and earns the same way; a correction linked to the cession it cancels.
        var cessionRows = (await db.QueryAsync<CessionDb>("""
            select c.id as Id, c.cancels_cession_id as CancelsId, l.class_code as ClassCode, c.amount_gel as AmountGel, c.commission_gel as CommissionGel,
                   c.booked_on as BookedOn, c.cover_from as CoverFrom, c.cover_to as CoverTo
            from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id
            where c.booked_on <= @to
            order by c.created_at, c.id
            """, new { to })).ToList();
        var cededByClass = new Dictionary<string, CededBucket>(StringComparer.Ordinal);
        var cessionLedgerRows = new List<(Guid, Guid?, ExactPremiumTx)>();
        foreach (var row in cessionRows)
        {
            var minor = Tetri(row.AmountGel);
            cessionLedgerRows.Add((row.Id, row.CancelsId, new ExactPremiumTx(minor, row.BookedOn, row.CoverFrom, row.CoverTo)));
            if (!cededByClass.TryGetValue(row.ClassCode, out var bucket)) cededByClass[row.ClassCode] = bucket = new CededBucket();
            bucket.Txs.Add(row.Id);
            if (row.BookedOn >= from && row.BookedOn <= to)
            {
                bucket.Written += minor;
                bucket.Commission += Tetri(row.CommissionGel);
            }
        }
        var cessionLedger = LinkExact(cessionLedgerRows, "Cession");

        var classes = classOrder
            .Select(code => (Code: code, Bucket: byClass[code]))
            .OrderBy(c => c.Bucket.Sort) // stable, as Array.prototype.sort
            .Select(c =>
            {
                var b = c.Bucket;
                var txs = b.Txs.Select(id => ledger[id]).ToList();
                cededByClass.TryGetValue(c.Code, out var ceded);
                return new ClassRow(c.Code, b.Name, b.Issued.Count, b.StatWritten, b.FinWritten,
                    EarnedIn(txs, from, to), Unearned(txs, to),
                    ceded?.Written ?? 0, ceded is null ? 0 : Unearned(ceded.Txs.Select(id => cessionLedger[id]), to), ceded?.Commission ?? 0);
            })
            .ToList();
        var totals = classes.Aggregate(new Totals(0, 0, 0, 0, 0, 0, 0, 0), (s, c) => new Totals(
            s.PoliciesIssued + c.PoliciesIssued, s.StatisticalWrittenGel + c.StatisticalWrittenGel, s.FinancialWrittenGel + c.FinancialWrittenGel,
            s.EarnedGel + c.EarnedGel, s.UnearnedGel + c.UnearnedGel, s.CededWrittenGel + c.CededWrittenGel, s.CededUnearnedGel + c.CededUnearnedGel,
            s.RiCommissionGel + c.RiCommissionGel));

        // Treaty premium not tied to policies (excess of loss, deposit): the month's pro rata share, at the month-end rate; each
        // treaty's share to its last tetri, valued half up as Math.round(share × rate) did, then summed.
        var treatyRows = await db.QueryAsync<(decimal Premium, string Currency, DateOnly PeriodFrom, DateOnly PeriodTo)>("""
            select treaty_premium, currency, period_from, period_to
            from core.ri_treaty where treaty_premium is not null and period_from <= @to and period_to >= @from
            order by created_at, id
            """, new { from, to });
        BigInteger treatyPremiumGel = 0;
        foreach (var tr in treatyRows)
        {
            var share = TreatyPremiumInPeriodExact(Tetri(tr.Premium), tr.PeriodFrom, tr.PeriodTo, from, to);
            var v = await ValuationAt(tr.Currency.Trim(), to);
            treatyPremiumGel += AtRateHalfUp(share, v.Rate);
        }

        // Receivable at month end: unpaid instalments per policy, revalued at the month-end rate. A policy is owed from the day its
        // premium was booked (docs/04 rule 8), never by its status today (review H03).
        var policies = (await db.QueryAsync<(Guid Id, string Currency)>("""
            select p.id, p.premium_currency from core.policy p
            where exists (select 1 from core.installment i where i.policy_id = p.id)
              and exists (select 1 from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                          where l.policy_id = p.id and t.kind = N'issue' and t.booked_on <= @to)
            order by p.created_at, p.id
            """, new { to })).Select(p => (p.Id, Currency: p.Currency.Trim())).ToList();
        var ids = policies.Select(p => p.Id).ToList();
        // The schedules as they stood at month end (H03) and the allocations booked by then, read for all the policies at once (PR253).
        var schedules = await Billing.LoadSchedulesAsync(db, ids, to);
        var allocationRows = ids.Count == 0 ? [] : await Billing.InChunksAsync(ids, chunk => db.QueryAsync<(Guid PolicyId, decimal Amount, DateOnly ReceivedOn, string Method)>("""
            select i.policy_id, a.amount, a.booked_on, p.method
            from core.payment_allocation a join core.payment p on p.id = a.payment_id
            join core.installment i on i.id = a.installment_id
            where i.policy_id in @chunk and a.booked_on <= @to
            """, new { chunk, to }));
        var allocationsOf = allocationRows.GroupBy(a => a.PolicyId).ToDictionary(g => g.Key, g => g.Select(a => new Billing.AllocationRow(a.Amount, a.ReceivedOn, a.Method)).ToList());
        var receivables = new List<ReceivableRow>();
        foreach (var policy in policies)
        {
            var schedule = schedules.TryGetValue(policy.Id, out var s) ? s : [];
            var allocations = allocationsOf.TryGetValue(policy.Id, out var a) ? a : [];
            var v = await ValuationAt(policy.Currency, to);
            var position = ExactReceivable(schedule, allocations, to, policy.Currency, v.Rate);
            var at = receivables.FindIndex(r => r.Currency == policy.Currency);
            if (at < 0) receivables.Add(new ReceivableRow(policy.Currency, position.Outstanding, position.Overdue, position.OutstandingGel, v.Rate, v.Date));
            else
            {
                var r = receivables[at];
                receivables[at] = r with { Outstanding = r.Outstanding + position.Outstanding, Overdue = r.Overdue + position.Overdue, OutstandingGel = r.OutstandingGel + position.OutstandingGel };
            }
        }

        // Claims per class: payments of the month, RBNS at month end, statutory IBNR, recoveries.
        var yearStart = new DateOnly(from.Year, 1, 1);
        var paidRows = await db.QueryAsync<(string ClassCode, decimal PaidGel)>("""
            select l.class_code, coalesce(sum(round(cp.amount * cp.rate, 2)), 0)
            from core.claim_payment cp join core.claim c on c.id = cp.claim_id join core.policy_line l on l.id = c.policy_line_id
            where cp.paid_on >= @from and cp.paid_on <= @to group by l.class_code
            """, new { from, to });
        var reserveRows = await db.QueryAsync<(string ClassCode, string Currency, decimal Level)>("""
            select l.class_code, c.currency, coalesce(sum(r.change), 0)
            from core.claim_reserve r join core.claim c on c.id = r.claim_id join core.policy_line l on l.id = c.policy_line_id
            where r.booked_on <= @to group by l.class_code, c.currency
            """, new { to });
        var recoveryRows = await db.QueryAsync<(string ClassCode, decimal RecoveredGel)>("""
            select l.class_code, coalesce(sum(cr.amount_gel), 0)
            from core.claim_recovery cr join core.claim c on c.id = cr.claim_id join core.policy_line l on l.id = c.policy_line_id
            where cr.received_on >= @from and cr.received_on <= @to group by l.class_code
            """, new { from, to });
        var ytdRows = await db.QueryAsync<(string ClassCode, decimal WrittenGel)>("""
            select l.class_code, coalesce(sum(t.amount_gel), 0)
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where t.booked_on >= @yearStart and t.booked_on <= @to group by l.class_code
            """, new { yearStart, to });
        var classNames = (await db.QueryAsync<(string Code, string NameKa, int SortOrder)>("select code, name_ka, sort_order from core.insurance_class"))
            .ToDictionary(c => c.Code, StringComparer.Ordinal);
        var cededYtd = (await db.QueryAsync<(string ClassCode, decimal CededGel)>("""
            select l.class_code, coalesce(sum(c.amount_gel), 0)
            from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id join core.policy_line l on l.id = t.policy_line_id
            where c.booked_on >= @yearStart and c.booked_on <= @to group by l.class_code
            """, new { yearStart, to })).ToDictionary(r => r.ClassCode, r => Tetri(r.CededGel), StringComparer.Ordinal);

        var claimsByClass = new Dictionary<string, ClaimsRowBuilder>(StringComparer.Ordinal);
        var claimsOrder = new List<string>();
        ClaimsRowBuilder ClaimsRowOf(string code)
        {
            if (!claimsByClass.TryGetValue(code, out var row))
            {
                claimsByClass[code] = row = new ClaimsRowBuilder(code, classNames.TryGetValue(code, out var c) ? c.NameKa : code);
                claimsOrder.Add(code);
            }
            return row;
        }
        foreach (var r in paidRows) ClaimsRowOf(r.ClassCode).Paid += Tetri(r.PaidGel);
        foreach (var r in reserveRows)
        {
            var currency = r.Currency.Trim();
            var v = await ValuationAt(currency, to);
            var level = Tetri(r.Level);
            ClaimsRowOf(r.ClassCode).Rbns += currency == "GEL" ? level : AtRateHalfUp(level, v.Rate);
        }
        foreach (var r in recoveryRows) ClaimsRowOf(r.ClassCode).Recovered += Tetri(r.RecoveredGel);
        foreach (var r in ytdRows)
        {
            // Net written premium: gross less what was ceded (order №01 art. 7). Health is class 4.
            var line = r.ClassCode == "4" ? IbnrLine.Health : IbnrLine.Other;
            var ibnr = Reserves.StatutoryIbnrExact(Tetri(r.WrittenGel), cededYtd.TryGetValue(r.ClassCode, out var c) ? c : 0, line);
            if (ibnr > 0) ClaimsRowOf(r.ClassCode).Ibnr += ibnr;
        }

        // Reinsurer's share per reinsured claim: in the month's payments and in the reserve at month end.
        var reinsuredClaims = await db.QueryAsync<(Guid Id, string ClassCode, string Currency, decimal PaidMonthGel, decimal PaidMonth)>("""
            select c.id, l.class_code, c.currency,
                   (select coalesce(sum(round(p.amount * p.rate, 2)), 0) from core.claim_payment p where p.claim_id = c.id and p.paid_on >= @from and p.paid_on <= @to),
                   (select coalesce(sum(p.amount), 0) from core.claim_payment p where p.claim_id = c.id and p.paid_on >= @from and p.paid_on <= @to)
            from core.claim c join core.policy_line l on l.id = c.policy_line_id
            where exists (select 1 from core.claim_cession cc where cc.claim_id = c.id)
            order by c.created_at, c.id
            """, new { from, to });
        foreach (var claim in reinsuredClaims)
        {
            // Every share to its last tetri; each rounded as Math.round did (half up), then summed.
            var atEnd = await ReinsuranceStore.ClaimSharesExactAsync(db, claim.Id, to);
            var atStart = await ReinsuranceStore.ClaimSharesExactAsync(db, claim.Id, from.AddDays(-1));
            var v = await ValuationAt(claim.Currency.Trim(), to);
            var row = ClaimsRowOf(claim.ClassCode);
            foreach (var share in atEnd)
            {
                row.ReinsurerRbns += AtRateHalfUp(share.InReserve, v.Rate);
                var paidShare = share.InPaid - (atStart.FirstOrDefault(s => s.TreatyId == share.TreatyId)?.InPaid ?? BigInteger.Zero);
                if (paidShare.IsZero) continue;
                // Proportional share follows the payments' own rates; excess of loss is valued at the month-end rate.
                var paidMonth = Tetri(claim.PaidMonth);
                row.ReinsurerPaid += share.Kind == TreatyKind.ExcessOfLoss || paidMonth.IsZero
                    ? AtRateHalfUp(paidShare, v.Rate)
                    : DivHalfUp(Tetri(claim.PaidMonthGel) * paidShare, paidMonth);
            }
        }

        var claims = claimsOrder
            .Select(code => claimsByClass[code])
            .OrderBy(c => classNames.TryGetValue(c.Code, out var n) ? n.SortOrder : 0)
            .Select(c => c.Build())
            .ToList();
        var claimsTotals = claims.Aggregate(new ClaimsTotals(0, 0, 0, 0, 0, 0), (s, c) => new ClaimsTotals(
            s.PaidGel + c.PaidGel, s.RbnsGel + c.RbnsGel, s.IbnrGel + c.IbnrGel, s.RecoveredGel + c.RecoveredGel,
            s.ReinsurerPaidGel + c.ReinsurerPaidGel, s.ReinsurerRbnsGel + c.ReinsurerRbnsGel));

        return new Report(month, from, to, classes, totals, receivables, claims, claimsTotals, treatyPremiumGel);
    }

    private sealed class ClaimsRowBuilder(string code, string name)
    {
        public string Code { get; } = code;
        public BigInteger Paid, Rbns, Ibnr, Recovered, ReinsurerPaid, ReinsurerRbns;
        public ClaimsRow Build() => new(Code, name, Paid, Rbns, Ibnr, Recovered, ReinsurerPaid, ReinsurerRbns);
    }
}
