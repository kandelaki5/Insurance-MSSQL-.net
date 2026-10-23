using System.Globalization;
using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.Billing;

namespace Insurance.Application.Services;

/// <summary>A treaty combination the supervisory rules or the owner's decisions do not allow (H07, H08).</summary>
public sealed class ReinsuranceException(CessionProblem problem) : Exception($"reinsurance: {Codes.Of(problem).Replace('_', '-')}")
{
    public CessionProblem Problem { get; } = problem;
}

/// <summary>
/// An excess-of-loss treaty in the claim's currency is in force for a GEL claim on a foreign-currency coverage (H46): whether
/// it shares such a loss is not decided (old program docs/07 §4.5, status open question 8), so the registration is refused.
/// </summary>
public sealed class CrossCurrencyXlException() : Exception("reinsurance: an excess-of-loss treaty on a claim in another currency than its coverage");

/// <summary>
/// Database side of outward reinsurance: applies treaties to premium ledger rows and claims (legacy src/lib/reinsurance.ts).
/// Rows of one transaction are taken in the order they were written (created_at, then id); in PostgreSQL rows of one
/// transaction shared one created_at, so their order was the ids' (random) order — any order was the legacy behaviour.
/// </summary>
public static class ReinsuranceStore
{
    public sealed record TreatyRow(Guid Id, string Number, string Name, string Kind, string Currency, decimal? CededPercent, decimal? Retention,
        int? Lines, decimal? Priority, decimal? TreatyLimit, decimal CommissionPercent)
    {
        public TreatyKind TreatyKind => Codes.Parse<TreatyKind>(Kind);
    }

    private static long? Minor(decimal? value) => value is null ? null : Tetri(value.Value);

    public static TreatyTerms TermsOf(TreatyRow t) =>
        new(t.TreatyKind, t.CommissionPercent, t.CededPercent, Minor(t.Retention), t.Lines, Minor(t.Priority), Minor(t.TreatyLimit));

    private const string TreatyColumns = """
        t.id as Id, t.number as Number, t.name as Name, t.kind as Kind, t.currency as Currency, t.ceded_percent as CededPercent,
        t.retention as Retention, t.lines as Lines, t.priority as Priority, t.treaty_limit as TreatyLimit, t.commission_percent as CommissionPercent
        """;

    /// <summary>Obligatory treaties of the given kinds covering a class (or its parent class) on a date.</summary>
    private static async Task<List<TreatyRow>> TreatiesForAsync(Tx tx, string classCode, DateOnly on, IReadOnlyList<string> kinds) =>
        (await tx.QueryAsync<TreatyRow>($"""
            select {TreatyColumns}
            from core.ri_treaty t
            where t.status = N'active' and t.kind in @kinds and t.period_from <= @on and t.period_to >= @on
              and exists (select 1 from core.ri_treaty_class c join core.insurance_class ic on ic.code = @classCode
                          where c.treaty_id = t.id and (c.class_code = ic.code or c.class_code = ic.parent_code))
            order by t.kind, t.number
            """, new { classCode, on, kinds })).ToList();

    private sealed record LedgerRowDb(Guid Id, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, Guid? CancelsTransactionId);

    private static void AssertPlan(IReadOnlyList<CessionShare> shares, string riskCurrency)
    {
        if (Domain.Finance.Reinsurance.CheckCessionPlan(shares, riskCurrency) is { } problem) throw new ReinsuranceException(problem);
    }

    /// <summary>Percent of a premium row already ceded, to check and slice the next treaty against.</summary>
    private static async Task<List<CessionShare>> CededPercentOnRowAsync(Tx tx, Guid premiumTransactionId) =>
        (await tx.QueryAsync<(string Kind, decimal SharePercent, string Currency)>("""
            select t.kind, c.share_percent, t.currency from core.premium_cession c join core.ri_treaty t on t.id = c.treaty_id
            where c.premium_transaction_id = @premiumTransactionId order by c.created_at, lower(convert(nchar(36), c.id)) collate Latin1_General_BIN2
            """, new { premiumTransactionId }))
        .Select(r => new CessionShare(Codes.Parse<TreatyKind>(r.Kind), r.SharePercent, r.Currency.Trim())).ToList();

    private static async Task InsertCessionAsync(Tx tx, LedgerRowDb row, TreatyRow treaty, decimal sharePercent, Currency currency, Guid userId, Guid? originalCessionId = null)
    {
        PremiumTransaction cession, cessionGel;
        if (originalCessionId is { } originalId)
        {
            // A cancellation reverses the unearned part of the cession actually booked, so the pair never returns more than was ceded.
            var booked = await tx.QuerySingleOrDefaultAsync<(decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)?>(
                "select amount, amount_gel, booked_on, cover_from, cover_to from core.premium_cession where id = @originalId", new { originalId });
            if (booked is not { } b) return;
            var orig = new PremiumTransaction(new Money(Tetri(b.Amount), currency), b.BookedOn, b.CoverFrom, b.CoverTo);
            var cancel = EarnedPremium.CancellationOf(orig, row.CoverFrom);
            var cancelGel = EarnedPremium.CancellationOf(orig with { Amount = new Money(Tetri(b.AmountGel), Currency.GEL) }, row.CoverFrom);
            if (cancel is null || cancelGel is null) return;
            cession = cancel with { BookedOn = row.BookedOn };
            cessionGel = cancelGel with { BookedOn = row.BookedOn };
        }
        else
        {
            // Sliced from the running total of what this row already cedes (H08).
            var already = (await CededPercentOnRowAsync(tx, row.Id)).Sum(c => c.SharePercent);
            var basis = new PremiumTransaction(new Money(Tetri(row.Amount), currency), row.BookedOn, row.CoverFrom, row.CoverTo);
            cession = basis with { Amount = new Money(Domain.Finance.Reinsurance.CededSlice(Tetri(row.Amount), already, sharePercent), currency) };
            cessionGel = basis with { Amount = new Money(Domain.Finance.Reinsurance.CededSlice(Tetri(row.AmountGel), already, sharePercent), Currency.GEL) };
        }
        var commissionPercent = treaty.CommissionPercent;
        await tx.ExecuteAsync("""
            if not exists (select 1 from core.premium_cession where premium_transaction_id = @rowId and treaty_id = @treatyId)
              insert into core.premium_cession (premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel,
                                                booked_on, cover_from, cover_to, cancels_cession_id, created_by)
              values (@rowId, @treatyId, @share, @amount, @amountGel, @commission, @commissionGel, @bookedOn, @coverFrom, @coverTo, @cancels, @userId)
            """, new
        {
            rowId = row.Id, treatyId = treaty.Id, share = Math.Round(sharePercent, 4, MidpointRounding.AwayFromZero),
            amount = FromTetri(cession.Amount.Minor), amountGel = FromTetri(cessionGel.Amount.Minor),
            commission = FromTetri(Domain.Finance.Reinsurance.CededAmount(cession.Amount.Minor, commissionPercent)),
            commissionGel = FromTetri(Domain.Finance.Reinsurance.CededAmount(cessionGel.Amount.Minor, commissionPercent)),
            bookedOn = row.BookedOn, coverFrom = cession.CoverFrom, coverTo = cession.CoverTo, cancels = originalCessionId, userId,
        });
    }

    /// <summary>
    /// Applies the obligatory proportional treaties to a freshly booked premium row (policy approval or endorsement):
    /// quota share for any currency; surplus only when the line's sum insured is in the treaty's currency, since the
    /// retention is expressed in it.
    /// </summary>
    public static async Task CedeIssuedPremiumAsync(Tx tx, Guid premiumTransactionId, Guid userId)
    {
        var row = await tx.QuerySingleOrDefaultAsync<(Guid Id, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo,
            Guid? CancelsTransactionId, string ClassCode, decimal? SumInsured, string? SumInsuredCurrency, string Currency)?>("""
            select t.id, t.amount, t.amount_gel, t.booked_on, t.cover_from, t.cover_to, t.cancels_transaction_id,
                   l.class_code, l.sum_insured, l.sum_insured_currency, p.premium_currency
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id
            where t.id = @premiumTransactionId
            """, new { premiumTransactionId });
        if (row is not { } r) return;
        var ledger = new LedgerRowDb(r.Id, r.Amount, r.AmountGel, r.BookedOn, r.CoverFrom, r.CoverTo, r.CancelsTransactionId);
        var currency = ParseCurrency(r.Currency);
        var treaties = await TreatiesForAsync(tx, r.ClassCode, r.CoverFrom, ["quota_share", "surplus"]);
        var plan = treaties
            .Where(t => !(t.Kind == "surplus" && r.SumInsuredCurrency?.Trim() != t.Currency.Trim()))
            .Select(t => (Treaty: t, Share: Domain.Finance.Reinsurance.CededPercentOf(TermsOf(t), Minor(r.SumInsured))))
            .Where(p => p.Share > 0)
            .ToList();
        // The whole plan is checked before the first cession is written; a refusal rolls back the approval that booked the premium (H08).
        AssertPlan([.. await CededPercentOnRowAsync(tx, r.Id), .. plan.Select(p => new CessionShare(p.Treaty.TreatyKind, p.Share, p.Treaty.Currency.Trim()))], r.Currency.Trim());
        foreach (var (treaty, share) in plan) await InsertCessionAsync(tx, ledger, treaty, share, currency, userId);
    }

    /// <summary>Mirrors every cession of the original row on its cancellation row.</summary>
    public static async Task CedeCancellationAsync(Tx tx, Guid cancellationTransactionId, Guid userId)
    {
        var row = await tx.QuerySingleOrDefaultAsync<(Guid Id, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, Guid? CancelsTransactionId, string Currency)?>("""
            select t.id, t.amount, t.amount_gel, t.booked_on, t.cover_from, t.cover_to, t.cancels_transaction_id, p.premium_currency
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id
            where t.id = @cancellationTransactionId
            """, new { cancellationTransactionId });
        if (row is not { CancelsTransactionId: { } originalId } r) return;
        var original = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.premium_transaction where id = @originalId", new { originalId });
        if (original is null) return;
        var cessions = (await tx.QueryAsync<(Guid CessionId, decimal SharePercent, Guid TreatyId)>("""
            select c.id, c.share_percent, c.treaty_id
            from core.premium_cession c
            where c.premium_transaction_id = @originalId order by c.created_at, lower(convert(nchar(36), c.id)) collate Latin1_General_BIN2
            """, new { originalId })).ToList();
        var ledger = new LedgerRowDb(r.Id, r.Amount, r.AmountGel, r.BookedOn, r.CoverFrom, r.CoverTo, r.CancelsTransactionId);
        foreach (var c in cessions)
        {
            var treaty = await TreatyAsync(tx, c.TreatyId);
            await InsertCessionAsync(tx, ledger, treaty!, c.SharePercent, ParseCurrency(r.Currency), userId, c.CessionId);
        }
    }

    private static Task<TreatyRow?> TreatyAsync(Tx tx, Guid id) =>
        tx.QuerySingleOrDefaultAsync<TreatyRow>($"select {TreatyColumns} from core.ri_treaty t where t.id = @id", new { id });

    public enum FacultativeOutcome { Placed, NotFound, AlreadyPlaced, NothingToCede, OverCeded, XolWithProportional }

    /// <summary>Places a facultative treaty on every premium row of a policy, originals before cancellations.</summary>
    public static async Task<FacultativeOutcome> PlaceFacultativeAsync(Tx tx, Guid policyId, Guid treatyId, Guid userId)
    {
        // Two placements on one policy run one after the other, so the second one's plan check sees the first
        // (two-session test: without it, 60 % + 60 % were both placed).
        await tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @policyId", new { policyId });
        var treaty = await tx.QuerySingleOrDefaultAsync<TreatyRow>(
            $"select {TreatyColumns} from core.ri_treaty t where t.id = @treatyId and t.kind = N'facultative' and t.status = N'active'", new { treatyId });
        if (treaty is null) return FacultativeOutcome.NotFound;
        var policyCurrency = await tx.QuerySingleOrDefaultAsync<string>("select premium_currency from core.policy where id = @policyId", new { policyId });
        if (policyCurrency is null) return FacultativeOutcome.NotFound;
        var currency = ParseCurrency(policyCurrency);
        var rows = (await tx.QueryAsync<LedgerRowDb>("""
            select t.id as Id, t.amount as Amount, t.amount_gel as AmountGel, t.booked_on as BookedOn, t.cover_from as CoverFrom, t.cover_to as CoverTo,
                   t.cancels_transaction_id as CancelsTransactionId
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id = @policyId order by case when t.cancels_transaction_id is not null then 1 else 0 end, t.created_at
            """, new { policyId })).ToList();
        if (rows.Count == 0) return FacultativeOutcome.NothingToCede;
        var existing = await tx.ExecuteScalarAsync<int>("select count(*) from core.premium_cession c where c.treaty_id = @treatyId and c.premium_transaction_id in @ids",
            new { treatyId, ids = rows.Select(r => r.Id).ToList() });
        if (existing > 0) return FacultativeOutcome.AlreadyPlaced;
        var share = Domain.Finance.Reinsurance.CededPercentOf(TermsOf(treaty), null);

        // The placement is refused as a whole when, on any premium row, it would take the total ceded share above 100 %
        // (H08), or when an excess-of-loss treaty covers the policy's risk.
        var xol = await tx.ExecuteScalarAsync<int>("""
            select count(*) from core.policy p join core.policy_line l on l.policy_id = p.id
            join core.insurance_class ic on ic.code = l.class_code
            join core.ri_treaty_class tc on tc.class_code = ic.code or tc.class_code = ic.parent_code
            join core.ri_treaty t on t.id = tc.treaty_id
            where p.id = @policyId and t.kind = N'excess_of_loss' and t.status = N'active'
              and t.period_from < p.effective_to and t.period_to >= p.effective_from
            """, new { policyId });
        if (xol > 0) return FacultativeOutcome.XolWithProportional;
        foreach (var row in rows.Where(r => r.CancelsTransactionId is null))
        {
            var problem = Domain.Finance.Reinsurance.CheckCessionPlan([.. await CededPercentOnRowAsync(tx, row.Id), new CessionShare(TreatyKind.Facultative, share, treaty.Currency.Trim())], policyCurrency.Trim());
            if (problem is not null) return problem == CessionProblem.OverCeded ? FacultativeOutcome.OverCeded : FacultativeOutcome.XolWithProportional;
        }

        var cessionIds = new Dictionary<Guid, Guid>();
        foreach (var row in rows)
        {
            if (row.CancelsTransactionId is { } cancels)
            {
                if (!cessionIds.TryGetValue(cancels, out var cessionId)) continue;
                await InsertCessionAsync(tx, row, treaty, share, currency, userId, cessionId);
            }
            else
            {
                await InsertCessionAsync(tx, row, treaty, share, currency, userId);
            }
            var created = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from core.premium_cession where premium_transaction_id = @rowId and treaty_id = @treatyId",
                new { rowId = row.Id, treatyId });
            if (created is { } id) cessionIds[row.Id] = id;
        }
        return FacultativeOutcome.Placed;
    }

    /// <summary>
    /// Fixes how a new claim is shared: the proportional percent of each treaty on the policy line (from its cessions),
    /// plus the excess-of-loss treaties in force on the occurrence date.
    /// </summary>
    public static async Task AttachClaimCessionsAsync(Tx tx, Guid claimId, Guid policyLineId, string classCode, DateOnly occurredOn)
    {
        var proportional = (await tx.QueryAsync<(Guid TreatyId, decimal SharePercent, string Kind, string Currency)>("""
            select treaty_id, share_percent, kind, currency from (
              select c.treaty_id, c.share_percent, tr.kind, tr.currency,
                     row_number() over (partition by c.treaty_id order by c.created_at desc) as n
              from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id join core.ri_treaty tr on tr.id = c.treaty_id
              where t.policy_line_id = @policyLineId and c.cancels_cession_id is null
            ) x where n = 1
            order by lower(convert(nchar(36), treaty_id)) collate Latin1_General_BIN2
            """, new { policyLineId })).ToList();
        var xl = await TreatiesForAsync(tx, classCode, occurredOn, ["excess_of_loss"]);
        var currencies = await tx.QuerySingleOrDefaultAsync<(string Claim, string Line)>("""
            select c.currency, coalesce(l.sum_insured_currency, p.premium_currency)
            from core.claim c join core.policy_line l on l.id = c.policy_line_id join core.policy p on p.id = c.policy_id where c.id = @claimId
            """, new { claimId });
        var claimCurrency = currencies.Claim?.Trim() ?? "";
        // Refused as a whole, with the claim registration rolled back, rather than shared wrongly (H07: an
        // excess-of-loss priority in another currency; H08: more than the loss). A GEL claim on a USD coverage meets a USD
        // treaty here and is refused (RI003), as the old program's design keeps it (docs/07 §4.5).
        AssertPlan(
            [.. proportional.Select(p => new CessionShare(Codes.Parse<TreatyKind>(p.Kind), p.SharePercent, p.Currency.Trim())),
             .. xl.Select(t => new CessionShare(t.TreatyKind, 0m, t.Currency.Trim()))],
            claimCurrency);
        // H46: a GEL treaty and a GEL claim whose coverage (sum insured) is in another currency. Whether the treaty shares that
        // loss is open (status question 8); docs/04 rule 16ბ ties only a surplus treaty's retention to the sum insured's
        // currency. Not decided, so the registration is refused rather than an excess of loss attached or skipped by guess.
        if (xl.Count > 0 && claimCurrency == "GEL" && currencies.Line is { } line && line.Trim() != "GEL") throw new CrossCurrencyXlException();
        foreach (var p in proportional)
            await tx.ExecuteAsync("""
                if not exists (select 1 from core.claim_cession where claim_id = @claimId and treaty_id = @treatyId)
                  insert into core.claim_cession (claim_id, treaty_id, share_percent) values (@claimId, @treatyId, @share)
                """, new { claimId, treatyId = p.TreatyId, share = p.SharePercent });
        foreach (var t in xl)
            await tx.ExecuteAsync("""
                if not exists (select 1 from core.claim_cession where claim_id = @claimId and treaty_id = @treatyId)
                  insert into core.claim_cession (claim_id, treaty_id, share_percent, priority, treaty_limit) values (@claimId, @treatyId, 0, @priority, @limit)
                """, new { claimId, treatyId = t.Id, priority = t.Priority, limit = t.TreatyLimit });
    }

    public sealed record ClaimShare(Guid TreatyId, string TreatyNumber, string TreatyName, TreatyKind Kind, decimal SharePercent, long InReserve, long InPaid);

    private sealed record ClaimShareRow(Guid TreatyId, string Number, string Name, string Kind, string Currency, string ClaimCurrency, decimal SharePercent,
        decimal? Priority, decimal? TreatyLimit, decimal Reserve, decimal Paid);

    /// <summary>The claim's treaties with their fixed terms and the claim's reserve and payments to a day, as stored (one statement).</summary>
    private static async Task<List<ClaimShareRow>> ClaimShareRowsAsync(Tx db, Guid claimId, DateOnly? asOf)
    {
        var rows = (await db.QueryAsync<ClaimShareRow>("""
            select cc.treaty_id as TreatyId, t.number as Number, t.name as Name, t.kind as Kind, t.currency as Currency, cl.currency as ClaimCurrency,
                   cc.share_percent as SharePercent, cc.priority as Priority, cc.treaty_limit as TreatyLimit,
                   (select coalesce(sum(r.change), 0) from core.claim_reserve r where r.claim_id = cc.claim_id and (@asOf is null or r.booked_on <= @asOf)) as Reserve,
                   (select coalesce(sum(p.amount), 0) from core.claim_payment p where p.claim_id = cc.claim_id and (@asOf is null or p.paid_on <= @asOf)) as Paid
            from core.claim_cession cc join core.ri_treaty t on t.id = cc.treaty_id join core.claim cl on cl.id = cc.claim_id
            where cc.claim_id = @claimId order by t.kind, t.number
            """, new { claimId, asOf })).ToList();
        // Shares stored before the H07/H08 checks are re-checked rather than reported wrongly.
        if (rows.Count > 0) AssertPlan(rows.Select(r => new CessionShare(Codes.Parse<TreatyKind>(r.Kind), r.SharePercent, r.Currency.Trim())).ToList(), rows[0].ClaimCurrency.Trim());
        return rows;
    }

    /// <summary>Reinsurer's share of a claim's current reserve and cumulative payments, per treaty (minor units of the claim currency).</summary>
    public static async Task<IReadOnlyList<ClaimShare>> ClaimSharesAsync(Tx db, Guid claimId, DateOnly? asOf = null)
    {
        var rows = await ClaimShareRowsAsync(db, claimId, asOf);
        var alreadyPercent = 0m;
        var result = new List<ClaimShare>();
        foreach (var r in rows)
        {
            var kind = Codes.Parse<TreatyKind>(r.Kind);
            var terms = new ClaimTreatyTerms(kind, Minor(r.Priority), Minor(r.TreatyLimit));
            var reserve = Tetri(r.Reserve);
            var paid = Tetri(r.Paid);
            if (kind == TreatyKind.ExcessOfLoss)
            {
                // Excess of loss attaches to the whole loss (paid plus outstanding); the reserve share is what is left after payments.
                var xlPaid = Domain.Finance.Reinsurance.ReinsurerShareOfClaim(terms, r.SharePercent, paid);
                result.Add(new ClaimShare(r.TreatyId, r.Number, r.Name, kind, r.SharePercent,
                    Domain.Finance.Reinsurance.ReinsurerShareOfClaim(terms, r.SharePercent, paid + reserve) - xlPaid, xlPaid));
                continue;
            }
            // Proportional shares are sliced from their running total, so together they never exceed the loss.
            var inReserve = reserve > 0 ? Domain.Finance.Reinsurance.CededSlice(reserve, alreadyPercent, r.SharePercent) : 0;
            var inPaid = paid > 0 ? Domain.Finance.Reinsurance.CededSlice(paid, alreadyPercent, r.SharePercent) : 0;
            alreadyPercent += r.SharePercent;
            result.Add(new ClaimShare(r.TreatyId, r.Number, r.Name, kind, r.SharePercent, inReserve, inPaid));
        }
        return result;
    }

    /// <summary>One treaty's share of a claim in whole tetri of the claim's currency, exactly.</summary>
    public sealed record ClaimShareExact(Guid TreatyId, TreatyKind Kind, BigInteger InReserve, BigInteger InPaid);

    /// <summary>
    /// ClaimShares exactly, on big integers, for the monthly report: the same statement, plan check and rules, from the
    /// stored amounts to their last tetri.
    /// </summary>
    public static async Task<IReadOnlyList<ClaimShareExact>> ClaimSharesExactAsync(Tx db, Guid claimId, DateOnly? asOf = null)
    {
        var rows = await ClaimShareRowsAsync(db, claimId, asOf);
        BigInteger alreadyUnits = 0;
        var result = new List<ClaimShareExact>();
        foreach (var r in rows)
        {
            var kind = Codes.Parse<TreatyKind>(r.Kind);
            var units = Domain.Finance.Reinsurance.PercentUnitsExact(r.SharePercent.ToString(CultureInfo.InvariantCulture));
            BigInteger reserve = Tetri(r.Reserve), paid = Tetri(r.Paid);
            if (kind == TreatyKind.ExcessOfLoss)
            {
                var terms = new ClaimTreatyTermsExact(kind, r.Priority is { } p ? Tetri(p) : null, r.TreatyLimit is { } l ? Tetri(l) : null);
                var xlPaid = Domain.Finance.Reinsurance.ReinsurerShareOfClaimExact(terms, units, paid);
                result.Add(new ClaimShareExact(r.TreatyId, kind, Domain.Finance.Reinsurance.ReinsurerShareOfClaimExact(terms, units, paid + reserve) - xlPaid, xlPaid));
                continue;
            }
            var inReserve = reserve > 0 ? Domain.Finance.Reinsurance.CededSliceExact(reserve, alreadyUnits, units) : BigInteger.Zero;
            var inPaid = paid > 0 ? Domain.Finance.Reinsurance.CededSliceExact(paid, alreadyUnits, units) : BigInteger.Zero;
            alreadyUnits += units;
            result.Add(new ClaimShareExact(r.TreatyId, kind, inReserve, inPaid));
        }
        return result;
    }
}
