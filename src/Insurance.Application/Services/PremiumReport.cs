using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.Dates;

namespace Insurance.Application.Services;

/// <summary>
/// Premium figures of one calendar month in GEL (legacy src/lib/premium-report.ts, buildPremiumReport, reportMonth,
/// reportMoney), as the financial director's panel shows them: per insurance class from the ledger, every row at its own
/// fixed rate, a cancellation earning through the original it reverses; the receivable at month end at the month-end
/// rate; claims paid, RBNS, statutory IBNR, recoveries and the reinsurers' shares. Every figure is whole tetri as a big
/// integer from the stored amounts, exactly.
/// </summary>
public static partial class PremiumReport
{
    public sealed record ClassRow(string ClassCode, string ClassName, int PoliciesIssued, BigInteger StatisticalWrittenGel, BigInteger FinancialWrittenGel,
        BigInteger EarnedGel, BigInteger UnearnedGel, BigInteger CededWrittenGel, BigInteger CededUnearnedGel, BigInteger RiCommissionGel);

    public sealed record Totals(int PoliciesIssued, BigInteger StatisticalWrittenGel, BigInteger FinancialWrittenGel, BigInteger EarnedGel, BigInteger UnearnedGel,
        BigInteger CededWrittenGel, BigInteger CededUnearnedGel, BigInteger RiCommissionGel);

    public sealed record ClaimsRow(string ClassCode, string ClassName, BigInteger PaidGel, BigInteger RbnsGel, BigInteger IbnrGel, BigInteger RecoveredGel,
        BigInteger ReinsurerPaidGel, BigInteger ReinsurerRbnsGel);

    public sealed record ClaimsTotals(BigInteger PaidGel, BigInteger RbnsGel, BigInteger IbnrGel, BigInteger RecoveredGel, BigInteger ReinsurerPaidGel, BigInteger ReinsurerRbnsGel);

    /// <summary>The receivable at month end of one currency, in its minor units and in tetri at the month-end rate.</summary>
    public sealed record ReceivableRow(string Currency, BigInteger Outstanding, BigInteger Overdue, BigInteger OutstandingGel, decimal Rate, DateOnly RateDate);

    public sealed record Report(string Month, DateOnly From, DateOnly To, IReadOnlyList<ClassRow> Classes, Totals Totals, IReadOnlyList<ReceivableRow> Receivables,
        IReadOnlyList<ClaimsRow> Claims, ClaimsTotals ClaimsTotals, BigInteger TreatyPremiumGel);

    [GeneratedRegex(@"^[0-9]{4}-(0[1-9]|1[0-2])\z")]
    private static partial Regex MonthText();

    /// <summary>The month a report is asked for (YYYY-MM); anything else — the current month in Tbilisi.</summary>
    public static string ReportMonth(string? requested) =>
        requested is not null && MonthText().IsMatch(requested) ? requested : Rates.TodayInTbilisi().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>A report figure (tetri) as the report shows it: 1234567 → 12,345.67, exactly.</summary>
    public static string Money(BigInteger tetri)
    {
        var abs = BigInteger.Abs(tetri);
        var whole = (abs / 100).ToString(CultureInfo.InvariantCulture);
        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(whole[i]);
        }
        return $"{(tetri < 0 ? "-" : "")}{grouped}.{(abs % 100).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}";
    }

    private static readonly BigInteger RateScale = 1_000_000;

    private static BigInteger Exact(decimal amount) => PaymentActions.Exact(amount);

    private static BigInteger Scaled(decimal rate) => new(Math.Round(rate * 1_000_000m, MidpointRounding.AwayFromZero));

    /// <summary>n / d rounded half away from zero, on big integers.</summary>
    private static BigInteger DivRound(BigInteger n, BigInteger d)
    {
        var negative = n < 0 != d < 0;
        var q = (BigInteger.Abs(n) * 2 + BigInteger.Abs(d)) / (BigInteger.Abs(d) * 2);
        return negative ? -q : q;
    }

    /// <summary>n / d to the nearest whole, a half towards +∞ (Math.round(n / d)), exactly.</summary>
    private static BigInteger DivHalfUp(BigInteger n, BigInteger d)
    {
        if (d < 0) { n = -n; d = -d; }
        BigInteger t = 2 * n + d, u = 2 * d;
        var q = t / u;
        return t % u != 0 && t < 0 ? q - 1 : q;
    }

    /// <summary>Whole tetri of a currency in GEL at a stored rate, rounded half towards +∞ (Math.round(tetri × rate)).</summary>
    private static BigInteger AtRateHalfUp(BigInteger tetri, decimal rate)
    {
        var n = 2 * tetri * Scaled(rate) + RateScale;
        var d = 2 * RateScale;
        var q = n / d;
        return n % d != 0 && n < 0 ? q - 1 : q;
    }

    private sealed class ExactTx(BigInteger minor, DateOnly bookedOn, DateOnly coverFrom, DateOnly coverTo)
    {
        public BigInteger Minor { get; } = minor;
        public DateOnly BookedOn { get; } = bookedOn;
        public DateOnly CoverFrom { get; } = coverFrom;
        public DateOnly CoverTo { get; } = coverTo;
        public ExactTx? Cancels { get; set; }
    }

    /// <summary>earnedAsOf exactly: nothing before the row is booked; a cancellation mirrors its original's earning from the cancel date on.</summary>
    private static BigInteger EarnedAsOf(ExactTx tx, DateOnly asOf)
    {
        if (DayNumber(asOf) < DayNumber(tx.BookedOn)) return 0;
        if (tx.Cancels is not null) return -(EarnedAsOf(tx.Cancels, asOf) - EarnedAsOf(tx.Cancels, AddDays(tx.CoverFrom, -1)));
        var total = DaysBetween(tx.CoverFrom, tx.CoverTo);
        if (total <= 0) throw new InvalidOperationException($"Cover period ends before it starts: {ToIso(tx.CoverFrom)}..{ToIso(tx.CoverTo)}");
        var elapsed = Math.Min(Math.Max(DaysBetween(tx.CoverFrom, asOf) + 1, 0), total);
        return DivRound(tx.Minor * elapsed, total);
    }

    private static BigInteger EarnedIn(IEnumerable<ExactTx> txs, DateOnly from, DateOnly to) =>
        txs.Aggregate(BigInteger.Zero, (sum, tx) => sum + EarnedAsOf(tx, to) - EarnedAsOf(tx, AddDays(from, -1)));

    private static BigInteger Unearned(IEnumerable<ExactTx> txs, DateOnly asOf) =>
        txs.Aggregate(BigInteger.Zero, (sum, tx) => DayNumber(asOf) < DayNumber(tx.BookedOn) ? sum : sum + tx.Minor - EarnedAsOf(tx, asOf));

    /// <summary>treatyPremiumInPeriod exactly: the treaty premium earned over its period, both dates inclusive.</summary>
    private static BigInteger TreatyPremiumInPeriod(BigInteger premium, DateOnly treatyFrom, DateOnly treatyTo, DateOnly from, DateOnly to)
    {
        var tx = new ExactTx(premium, treatyFrom, treatyFrom, AddDays(treatyTo, 1));
        var before = DayNumber(from) <= DayNumber(treatyFrom) ? BigInteger.Zero : EarnedAsOf(tx, AddDays(from, -1));
        return EarnedAsOf(tx, to) - before;
    }

    private sealed record LedgerRead(Guid Id, Guid? CancelsId, string ClassCode, string ClassName, int SortOrder, decimal AmountGel, DateOnly BookedOn,
        DateOnly CoverFrom, DateOnly CoverTo, string Kind, Guid PolicyId);

    private sealed record CessionRead(Guid Id, Guid? CancelsId, string ClassCode, decimal AmountGel, decimal CommissionGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);

    private sealed class ClassBucket(string name, int sort)
    {
        public string Name { get; } = name;
        public int Sort { get; } = sort;
        public List<ExactTx> Txs { get; } = [];
        public BigInteger StatWritten { get; set; }
        public BigInteger FinWritten { get; set; }
        public HashSet<Guid> Issued { get; } = [];
    }

    private sealed class CededBucket
    {
        public List<ExactTx> Txs { get; } = [];
        public BigInteger Written { get; set; }
        public BigInteger Commission { get; set; }
    }

    private sealed class ClaimsBucket(string code, string name)
    {
        public string Code { get; } = code;
        public string Name { get; } = name;
        public BigInteger Paid, Rbns, Ibnr, Recovered, ReinsurerPaid, ReinsurerRbns;
    }

    /// <summary>The month's report (legacy buildPremiumReport), read through <paramref name="db"/>; valuations through <paramref name="rates"/>.</summary>
    public static async Task<Report> BuildAsync(Tx db, Rates rates, string month)
    {
        var from = ParseIsoDate(month + "-01");
        var to = AddDays(AddMonths(from, 1), -1);

        // The month-end valuation of a currency, asked once for this report and shared by every row that needs it (PR299).
        var valuations = new Dictionary<string, (decimal Rate, DateOnly Date)>(StringComparer.Ordinal);
        async Task<(decimal Rate, DateOnly Date)> ValuationAt(string currency, DateOnly date)
        {
            var key = $"{currency} {ToIso(date)}";
            if (!valuations.TryGetValue(key, out var v)) valuations[key] = v = await rates.LatestRateOnOrBeforeAsync(currency, date);
            return v;
        }

        var rows = (await db.QueryAsync<LedgerRead>("""
            select t.id as Id, t.cancels_transaction_id as CancelsId, l.class_code as ClassCode, c.name_ka as ClassName, c.sort_order as SortOrder,
                   t.amount_gel as AmountGel, t.booked_on as BookedOn, t.cover_from as CoverFrom, t.cover_to as CoverTo, t.kind as Kind, l.policy_id as PolicyId
            from core.premium_transaction t
            join core.policy_line l on l.id = t.policy_line_id
            join core.insurance_class c on c.code = l.class_code
            where t.booked_on <= @to
            order by t.created_at
            """, new { to })).ToList();

        // Cancellations are linked once every row is read, whatever order they came in (review H14).
        var ledger = new Dictionary<Guid, (Guid? CancelsId, ExactTx Tx)>();
        var byClass = new Dictionary<string, ClassBucket>(StringComparer.Ordinal);
        var classOrder = new List<string>();
        foreach (var row in rows)
        {
            var written = Exact(row.AmountGel);
            var tx = new ExactTx(written, row.BookedOn, row.CoverFrom, row.CoverTo);
            ledger[row.Id] = (row.CancelsId, tx);
            if (!byClass.TryGetValue(row.ClassCode, out var bucket))
            {
                byClass[row.ClassCode] = bucket = new ClassBucket(row.ClassName, row.SortOrder);
                classOrder.Add(row.ClassCode);
            }
            bucket.Txs.Add(tx);
            if (row.BookedOn >= from && row.BookedOn <= to) bucket.FinWritten += written;
            if (row.Kind == "issue" && row.CoverFrom >= from && row.CoverFrom <= to)
            {
                bucket.StatWritten += written;
                bucket.Issued.Add(row.PolicyId);
            }
        }
        foreach (var (id, row) in ledger)
        {
            if (row.CancelsId is not { } cancels) continue;
            if (!ledger.TryGetValue(cancels, out var original)) throw new InvalidOperationException($"Ledger row {id} cancels {cancels}, which is not among the rows read");
            row.Tx.Cancels = original.Tx;
        }

        // Reinsurer's share: every cession mirrors its ledger row and earns the same way.
        var cessionRows = (await db.QueryAsync<CessionRead>("""
            select c.id as Id, c.cancels_cession_id as CancelsId, l.class_code as ClassCode, c.amount_gel as AmountGel, c.commission_gel as CommissionGel,
                   c.booked_on as BookedOn, c.cover_from as CoverFrom, c.cover_to as CoverTo
            from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id
            where c.booked_on <= @to
            order by c.created_at
            """, new { to })).ToList();
        var cessionLedger = new Dictionary<Guid, (Guid? CancelsId, ExactTx Tx)>();
        var cededByClass = new Dictionary<string, CededBucket>(StringComparer.Ordinal);
        foreach (var row in cessionRows)
        {
            var tx = new ExactTx(Exact(row.AmountGel), row.BookedOn, row.CoverFrom, row.CoverTo);
            cessionLedger[row.Id] = (row.CancelsId, tx);
            if (!cededByClass.TryGetValue(row.ClassCode, out var bucket)) cededByClass[row.ClassCode] = bucket = new CededBucket();
            bucket.Txs.Add(tx);
            if (row.BookedOn >= from && row.BookedOn <= to)
            {
                bucket.Written += tx.Minor;
                bucket.Commission += Exact(row.CommissionGel);
            }
        }
        foreach (var (id, row) in cessionLedger)
        {
            if (row.CancelsId is not { } cancels) continue;
            if (!cessionLedger.TryGetValue(cancels, out var original)) throw new InvalidOperationException($"Cession {id} cancels {cancels}, which is not among the rows read");
            row.Tx.Cancels = original.Tx;
        }

        var classes = classOrder.Select(code => (Code: code, Bucket: byClass[code]))
            .OrderBy(c => c.Bucket.Sort) // stable, as Array.prototype.sort
            .Select(c =>
            {
                var b = c.Bucket;
                cededByClass.TryGetValue(c.Code, out var ceded);
                return new ClassRow(c.Code, b.Name, b.Issued.Count, b.StatWritten, b.FinWritten, EarnedIn(b.Txs, from, to), Unearned(b.Txs, to),
                    ceded?.Written ?? 0, ceded is null ? 0 : Unearned(ceded.Txs, to), ceded?.Commission ?? 0);
            })
            .ToList();
        var totals = new Totals(classes.Sum(c => c.PoliciesIssued),
            Sum(classes, c => c.StatisticalWrittenGel), Sum(classes, c => c.FinancialWrittenGel), Sum(classes, c => c.EarnedGel), Sum(classes, c => c.UnearnedGel),
            Sum(classes, c => c.CededWrittenGel), Sum(classes, c => c.CededUnearnedGel), Sum(classes, c => c.RiCommissionGel));

        // Treaty premium not tied to policies (excess of loss, deposit): the month's pro rata share, at the month-end rate.
        var treatyRows = (await db.QueryAsync<(decimal Premium, string Currency, DateOnly PeriodFrom, DateOnly PeriodTo)>("""
            select treaty_premium, currency, period_from, period_to
            from core.ri_treaty where treaty_premium is not null and period_from <= @to and period_to >= @from
            """, new { from, to })).ToList();
        BigInteger treatyPremiumGel = 0;
        foreach (var tr in treatyRows)
        {
            var share = TreatyPremiumInPeriod(Exact(tr.Premium), tr.PeriodFrom, tr.PeriodTo, from, to);
            var valuation = await ValuationAt(tr.Currency.Trim(), to);
            treatyPremiumGel += AtRateHalfUp(share, valuation.Rate);
        }

        // Receivable at month end: a policy is owed from the day its premium was booked, never by its status today (H03).
        var policies = (await db.QueryAsync<(Guid Id, string Currency)>("""
            select p.id, p.premium_currency from core.policy p
            where exists (select 1 from core.installment i where i.policy_id = p.id)
              and exists (select 1 from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                          where l.policy_id = p.id and t.kind = N'issue' and t.booked_on <= @to)
            order by p.created_at, p.id
            """, new { to })).ToList();
        var ids = policies.Select(p => p.Id).ToList();
        var schedules = await Billing.LoadSchedulesAsync(db, ids, to);
        var allocationRows = ids.Count == 0 ? [] : await Billing.InChunksAsync(ids, chunk => db.QueryAsync<(Guid PolicyId, decimal Amount, DateOnly ReceivedOn)>("""
            select i.policy_id, a.amount, a.booked_on
            from core.payment_allocation a join core.payment p on p.id = a.payment_id
            join core.installment i on i.id = a.installment_id
            where i.policy_id in @chunk and a.booked_on <= @to
            """, new { chunk, to }));
        var allocationsOf = allocationRows.GroupBy(a => a.PolicyId).ToDictionary(g => g.Key, g => g.ToList());
        var receivables = new List<ReceivableRow>();
        foreach (var policy in policies)
        {
            var currency = policy.Currency.Trim();
            var valuation = await ValuationAt(currency, to);
            var available = allocationsOf.GetValueOrDefault(policy.Id, []).Where(c => c.ReceivedOn <= to).Aggregate(BigInteger.Zero, (s, c) => s + Exact(c.Amount));
            BigInteger outstanding = 0, overdue = 0;
            foreach (var row in schedules.GetValueOrDefault(policy.Id, []).OrderBy(r => r.DueOn))
            {
                var amount = Exact(row.Amount);
                var settled = available < amount ? available : amount;
                available -= settled;
                outstanding += amount - settled;
                if (row.DueOn < to) overdue += amount - settled;
            }
            if (currency != "GEL" && !(valuation.Rate > 0)) throw new InvalidOperationException($"No {currency} rate for {ToIso(to)}");
            var outstandingGel = currency == "GEL" ? outstanding : DivRound(outstanding * Scaled(valuation.Rate), RateScale);
            var at = receivables.FindIndex(r => r.Currency == currency);
            if (at < 0) receivables.Add(new ReceivableRow(currency, outstanding, overdue, outstandingGel, valuation.Rate, valuation.Date));
            else receivables[at] = receivables[at] with { Outstanding = receivables[at].Outstanding + outstanding, Overdue = receivables[at].Overdue + overdue, OutstandingGel = receivables[at].OutstandingGel + outstandingGel };
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
            """, new { yearStart, to })).ToDictionary(r => r.ClassCode, r => Exact(r.CededGel), StringComparer.Ordinal);

        var claimsByClass = new Dictionary<string, ClaimsBucket>(StringComparer.Ordinal);
        var claimsOrder = new List<string>();
        ClaimsBucket ClaimsOf(string code)
        {
            if (!claimsByClass.TryGetValue(code, out var row))
            {
                claimsByClass[code] = row = new ClaimsBucket(code, classNames.TryGetValue(code, out var c) ? c.NameKa : code);
                claimsOrder.Add(code);
            }
            return row;
        }
        foreach (var r in paidRows) ClaimsOf(r.ClassCode).Paid += Exact(r.PaidGel);
        foreach (var r in reserveRows)
        {
            var currency = r.Currency.Trim();
            var valuation = await ValuationAt(currency, to);
            var level = Exact(r.Level);
            ClaimsOf(r.ClassCode).Rbns += currency == "GEL" ? level : AtRateHalfUp(level, valuation.Rate);
        }
        foreach (var r in recoveryRows) ClaimsOf(r.ClassCode).Recovered += Exact(r.RecoveredGel);
        foreach (var r in ytdRows)
        {
            // Net written premium: gross less what was ceded (order №01 art. 7). Health is class 4.
            var line = r.ClassCode == "4" ? IbnrLine.Health : IbnrLine.Other;
            var ibnr = Reserves.StatutoryIbnrExact(Exact(r.WrittenGel), cededYtd.GetValueOrDefault(r.ClassCode), line);
            if (ibnr > 0) ClaimsOf(r.ClassCode).Ibnr += ibnr;
        }

        // Reinsurer's share per reinsured claim: in the month's payments and in the reserve at month end.
        var reinsured = (await db.QueryAsync<(Guid Id, string ClassCode, string Currency, decimal PaidMonthGel, decimal PaidMonth)>("""
            select c.id, l.class_code, c.currency,
                   (select coalesce(sum(round(p.amount * p.rate, 2)), 0) from core.claim_payment p where p.claim_id = c.id and p.paid_on >= @from and p.paid_on <= @to),
                   (select coalesce(sum(p.amount), 0) from core.claim_payment p where p.claim_id = c.id and p.paid_on >= @from and p.paid_on <= @to)
            from core.claim c join core.policy_line l on l.id = c.policy_line_id
            where exists (select 1 from core.claim_cession cc where cc.claim_id = c.id)
            """, new { from, to })).ToList();
        foreach (var claim in reinsured)
        {
            var atEnd = await ReinsuranceStore.ClaimSharesExactAsync(db, claim.Id, to);
            var atStart = await ReinsuranceStore.ClaimSharesExactAsync(db, claim.Id, AddDays(from, -1));
            var valuation = await ValuationAt(claim.Currency.Trim(), to);
            var row = ClaimsOf(claim.ClassCode);
            foreach (var share in atEnd)
            {
                row.ReinsurerRbns += AtRateHalfUp(share.InReserve, valuation.Rate);
                var paidShare = share.InPaid - (atStart.FirstOrDefault(s => s.TreatyId == share.TreatyId)?.InPaid ?? 0);
                if (paidShare == 0) continue;
                // Proportional share follows the payments' own rates; excess of loss is valued at the month-end rate.
                var paidMonth = Exact(claim.PaidMonth);
                row.ReinsurerPaid += share.Kind == TreatyKind.ExcessOfLoss || paidMonth == 0
                    ? AtRateHalfUp(paidShare, valuation.Rate)
                    : DivHalfUp(Exact(claim.PaidMonthGel) * paidShare, paidMonth);
            }
        }

        var claims = claimsOrder.Select(code => claimsByClass[code])
            .OrderBy(c => classNames.TryGetValue(c.Code, out var n) ? n.SortOrder : 0)
            .Select(c => new ClaimsRow(c.Code, c.Name, c.Paid, c.Rbns, c.Ibnr, c.Recovered, c.ReinsurerPaid, c.ReinsurerRbns))
            .ToList();
        var claimsTotals = new ClaimsTotals(Sum(claims, c => c.PaidGel), Sum(claims, c => c.RbnsGel), Sum(claims, c => c.IbnrGel), Sum(claims, c => c.RecoveredGel),
            Sum(claims, c => c.ReinsurerPaidGel), Sum(claims, c => c.ReinsurerRbnsGel));

        return new Report(month, from, to, classes, totals, receivables, claims, claimsTotals, treatyPremiumGel);
    }

    private static BigInteger Sum<T>(IEnumerable<T> rows, Func<T, BigInteger> f) => rows.Aggregate(BigInteger.Zero, (s, r) => s + f(r));
}
