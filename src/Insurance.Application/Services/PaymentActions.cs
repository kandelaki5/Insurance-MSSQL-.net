using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Compliance;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>
/// Premium receipts and what follows them on one policy (legacy src/app/(app)/policies/[id]/payment/actions.ts): a new
/// receipt with FIFO allocation, allocating an advance, moving another policy's overpayment here, reversing a receipt,
/// refunding an overpayment, and the financial director's approvals of a reversal or a refund above the threshold.
/// Every amount is whole tetri as a big integer from the stored decimal, so the largest numeric(18, 2) amounts keep
/// their last tetri, exactly as the legacy bigints did.
/// </summary>
public static partial class PaymentActions
{
    /// <summary>numeric(18, 2)'s largest amount in tetri: the column's capacity, not a business limit.</summary>
    public static readonly BigInteger Numeric18_2Max = BigInteger.Parse("999999999999999999", CultureInfo.InvariantCulture);

    private static readonly BigInteger RateScale = 1_000_000;

    /// <summary>The outcome of a form action: field errors and a message to show the form again with, or where to go.</summary>
    public sealed record FormAnswer(IReadOnlyDictionary<string, string> Errors, string? Message, string? Redirect)
    {
        public static FormAnswer Refused(string message) => new(new Dictionary<string, string>(), message, null);
        public static FormAnswer Invalid(Dictionary<string, string> errors) => new(errors, null, null);
        public static FormAnswer GoTo(string url) => new(new Dictionary<string, string>(), null, url);
    }

    [GeneratedRegex(@"^\+?([0-9]*)(?:[.,]([0-9]*))?(?:[eE]([+-]?)([0-9]+))?\z")]
    private static partial Regex AmountText();

    /// <summary>
    /// The form's amount as whole tetri, exactly: digits, one '.' or ',', an exponent of any written length, a leading
    /// '+' — without floating point, rounded half up past two decimals. Anything else (empty, a sign, NaN, Infinity,
    /// hex, two separators) is null. Past the column's capacity the answer is just above it; below half a tetri it is 0.
    /// </summary>
    public static BigInteger? AmountInTetri(string text)
    {
        var m = AmountText().Match(text);
        if (!m.Success || m.Groups[1].Value + m.Groups[2].Value == "") return null;
        var fraction = m.Groups[2].Value;
        var all = m.Groups[1].Value + fraction;
        var digits = all.TrimStart('0').TrimEnd('0');
        if (digits == "") return BigInteger.Zero;
        var trailing = all.TrimEnd('0').Length;
        var written = m.Groups[4].Success ? m.Groups[4].Value.TrimStart('0') : "";
        if (written == "") written = "0";
        // An exponent longer than 20 digits is past anything the digits could make up for.
        if (written.Length > 20) return m.Groups[3].Value == "-" ? BigInteger.Zero : Numeric18_2Max + 1;
        var exponent = BigInteger.Parse((m.Groups[3].Value == "-" ? "-" : "") + written, CultureInfo.InvariantCulture);
        var shift = exponent - fraction.Length + 2 + (all.Length - trailing);
        var whole = digits.Length + shift;
        if (whole > 18) return Numeric18_2Max + 1;
        if (whole < 0) return BigInteger.Zero;
        if (shift >= 0) return BigInteger.Parse(digits, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)shift);
        var kept = (int)whole;
        var head = digits[..kept];
        return BigInteger.Parse(head == "" ? "0" : head, CultureInfo.InvariantCulture) + (digits[kept] >= '5' ? 1 : 0);
    }

    /// <summary>A decimal(…, 2) amount as whole tetri, exactly.</summary>
    public static BigInteger Exact(decimal amount)
    {
        var scaled = amount * 100m;
        if (scaled != decimal.Truncate(scaled)) throw new ArgumentException($"not an amount in tetri: {amount.ToString(CultureInfo.InvariantCulture)}");
        return new BigInteger(scaled);
    }

    /// <summary>Whole tetri as a decimal with two places, for writing.</summary>
    public static decimal Decimal(BigInteger tetri) => (decimal)tetri / 100m + 0.00m;

    /// <summary>Positive n / d rounded half up (finance divRound for positive values).</summary>
    private static BigInteger DivRound(BigInteger n, BigInteger d) => (n * 2 + d) / (d * 2);

    /// <summary>The rate as the legacy code scaled it (Math.round(rate × 10^6)); GEL needs none.</summary>
    private static BigInteger Scaled(string currency, decimal rate)
    {
        if (currency == "GEL") return RateScale;
        if (!(rate > 0)) throw new InvalidOperationException($"No {currency} rate");
        return new BigInteger(Math.Round(rate * 1_000_000m, MidpointRounding.AwayFromZero));
    }

    /// <summary>The rate as written with an allocation (legacy rate.toFixed(6)).</summary>
    private static decimal Rate6(decimal rate) => Math.Round(rate, 6, MidpointRounding.AwayFromZero);

    /// <summary>An instalment of the policy a receipt, a transfer or an advance pays, in whole tetri of its currency.</summary>
    public sealed record ExactBalance(Guid Id, DateOnly DueOn, BigInteger Outstanding);

    public sealed record ExactLine(Guid InstallmentId, BigInteger Minor, BigInteger Gel);

    public sealed record ExactAllocation(IReadOnlyList<ExactLine> Lines, BigInteger UnallocatedGel);

    /// <summary>
    /// allocatePayment (finance Receivable) exactly, for a new receipt, a transfer and an advance: GEL into the policy's
    /// currency at the day's rate (to the millionth, half up), the oldest unpaid instalment first, each line back into
    /// GEL the same way, the last line taking the rounding difference when the money is all used.
    /// </summary>
    public static ExactAllocation AllocateExactly(BigInteger gel, string currency, IReadOnlyList<ExactBalance> balances, decimal rate)
    {
        if (!(gel > 0)) throw new ArgumentException("Payment must be positive");
        var scaled = Scaled(currency, rate);
        var left = DivRound(gel * RateScale, scaled);
        var open = balances.Where(b => b.Outstanding > 0).OrderBy(b => b.DueOn).ToList();
        var parts = new List<(Guid InstallmentId, BigInteger Minor)>();
        foreach (var b in open)
        {
            if (left <= 0) break;
            var minor = left < b.Outstanding ? left : b.Outstanding;
            left -= minor;
            parts.Add((b.Id, minor));
        }
        var gels = parts.Select(p => DivRound(p.Minor * scaled, RateScale)).ToList();
        if (left <= 0 && parts.Count > 0)
        {
            var before = BigInteger.Zero;
            for (var i = 0; i < gels.Count - 1; i++) before += gels[i];
            gels[^1] = gel - before;
        }
        var allocated = gels.Aggregate(BigInteger.Zero, (s, g) => s + g);
        return new ExactAllocation(parts.Select((p, i) => new ExactLine(p.InstallmentId, p.Minor, gels[i])).ToList(), gel - allocated);
    }

    private sealed record PolicyRow(Guid Id, string Number, string Status, string PremiumCurrency, Guid PolicyholderId)
    {
        public string Currency => PremiumCurrency.Trim();
    }

    private const string PolicySelect = """
        select p.id as Id, p.number as Number, p.status as Status, p.premium_currency as PremiumCurrency, c.policyholder_id as PolicyholderId
        from core.policy p join core.contract c on c.id = p.contract_id
        """;

    private static Task LockPolicyAsync(Tx tx, Guid policyId) =>
        tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @policyId", new { policyId });

    /// <summary>What is paid on each instalment of the policy, in its currency.</summary>
    private static async Task<Dictionary<Guid, BigInteger>> SettledAsync(Tx tx, Guid policyId) =>
        (await tx.QueryAsync<(Guid InstallmentId, decimal Settled)>("""
            select a.installment_id, sum(a.amount) from core.payment_allocation a
            join core.installment i on i.id = a.installment_id where i.policy_id = @policyId group by a.installment_id
            """, new { policyId })).ToDictionary(r => r.InstallmentId, r => Exact(r.Settled));

    /// <summary>What is owed on each instalment on the day (a reduction booked for a later day does not count yet, H23).</summary>
    private static async Task<List<ExactBalance>> BalancesAsync(Tx tx, Guid policyId, DateOnly asOf)
    {
        var settled = await SettledAsync(tx, policyId);
        return (await Billing.LoadScheduleAsync(tx, policyId, asOf))
            .Select(r => new ExactBalance(r.Id, r.DueOn, Exact(r.Amount) - settled.GetValueOrDefault(r.Id)))
            .ToList();
    }

    private static Task InsertAllocationAsync(Tx tx, Guid paymentId, Guid installmentId, decimal amount, decimal amountGel, decimal rate, DateOnly bookedOn) =>
        tx.ExecuteAsync("""
            insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on)
            values (@paymentId, @installmentId, @amount, @amountGel, @rate, @bookedOn)
            """, new { paymentId, installmentId, amount, amountGel, rate, bookedOn });

    private static OperationTargets Targets(IEnumerable<Guid> policyIds, params string?[] numbers) =>
        new(PolicyIds: policyIds.Select(id => (string?)Uuid.Text(id)).ToList(), Numbers: numbers);

    private static bool IsCode(Exception e, params string[] codes) => DbError.From(e) is { } d && codes.Contains(d.Code);

    // ---------------------------------------------------------------- a new receipt

    /// <summary>
    /// A new premium receipt. Who may record one is the permission premium.receipt.create, read for the user of the
    /// session — a quick check first, the binding one inside the transaction before anything is written. The policy is
    /// locked so two clerks cannot settle the same instalment twice; the suspension check under the client's lock is
    /// the binding one. The allocation is booked on the Tbilisi day it is made, the receipt keeps its own date.
    /// </summary>
    public static async Task<FormAnswer> RecordPaymentAsync(Db db, Rates rates, CurrentUser user, IReadOnlyDictionary<string, string> values)
    {
        string? V(string key) => values.TryGetValue(key, out var v) ? v : null;
        if (!await PermissionStore.HasPermissionAsync(db, user, "premium.receipt.create")) return FormAnswer.Refused(BillingTexts.Payment.NotAllowed);
        if (!Uuid.TryParse(V("policy_id") ?? "", out var policyId)) return FormAnswer.Refused(BillingTexts.Payment.NotPayable);
        var policy = await db.ReadTxAsync(tx => tx.QuerySingleOrDefaultAsync<PolicyRow>($"{PolicySelect} where p.id = @policyId", new { policyId }));
        if (policy is null || policy.Status == "draft") return FormAnswer.Refused(BillingTexts.Payment.NotPayable);
        var targets = Targets([policy.Id], policy.Number, V("reference"));
        if (await db.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, policy.PolicyholderId, targets)) is not null)
            return FormAnswer.Refused(AmlTexts.Suspension.Blocked);

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var gelMinor = AmountInTetri(V("amount_gel") ?? "");
        if (gelMinor is not { } gm || gm <= 0 || gm > Numeric18_2Max) errors["amount_gel"] = BillingTexts.Payment.Errors.Amount;
        var receivedText = V("received_on") ?? "";
        var today = db.Today();
        if (!Dates.IsIsoDate(receivedText) || Dates.ParseIsoDate(receivedText) > today) errors["received_on"] = BillingTexts.Payment.Errors.Date;
        var method = V("method") ?? "";
        if (!BillingTexts.Payment.Methods.ContainsKey(method)) errors["method"] = BillingTexts.Payment.Errors.Method;

        decimal? rate = null;
        if (!errors.ContainsKey("received_on"))
        {
            rate = await rates.RateOnAsync(policy.Currency, Dates.ParseIsoDate(receivedText));
            if (rate is null) errors["received_on"] = BillingTexts.Payment.Errors.Rate;
        }
        if (errors.Count > 0 || rate is not { } paymentRate) return FormAnswer.Invalid(errors);
        var receivedOn = Dates.ParseIsoDate(receivedText);
        var gel = gelMinor!.Value;
        var reference = string.IsNullOrEmpty(V("reference")) ? null : V("reference");

        object outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                if (!await PermissionStore.HasPermissionAsync(tx, user, "premium.receipt.create")) return (object)"not-allowed";
                await LockPolicyAsync(tx, policy.Id);
                if (await Aml.SuspensionInTransactionAsync(tx, policy.PolicyholderId, targets) is not null) return "suspended";
                var bookedOn = tx.Today();
                var balances = await BalancesAsync(tx, policy.Id, bookedOn);
                if (!balances.Any(b => b.Outstanding > 0)) return "nothing-owed";

                var result = AllocateExactly(gel, policy.Currency, balances, paymentRate);
                var paymentId = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.payment (id, received_on, amount_gel, method, payer_party_id, reference, created_by)
                    values (@paymentId, @receivedOn, @amountGel, @method, @payer, @reference, @userId)
                    """, new { paymentId, receivedOn, amountGel = Decimal(gel), method, payer = policy.PolicyholderId, reference, userId = user.Id });
                foreach (var line in result.Lines)
                    await InsertAllocationAsync(tx, paymentId, line.InstallmentId, Decimal(line.Minor), Decimal(line.Gel), Rate6(paymentRate), bookedOn);
                return result.UnallocatedGel;
            });
        }
        catch (Exception e) when (IsCode(e, DbError.CheckViolation))
        {
            // The database refuses movements dated in a closed accounting month.
            return FormAnswer.Invalid(new Dictionary<string, string> { ["received_on"] = BillingTexts.Payment.Errors.PeriodClosed });
        }
        return outcome switch
        {
            "not-allowed" => FormAnswer.Refused(BillingTexts.Payment.NotAllowed),
            "nothing-owed" => FormAnswer.Refused(BillingTexts.Payment.Errors.NothingOwed),
            "suspended" => FormAnswer.Refused(AmlTexts.Suspension.Blocked),
            BigInteger left => FormAnswer.GoTo($"/policies/{Uuid.Text(policy.Id)}?paid={left.ToString(CultureInfo.InvariantCulture)}"),
            _ => throw new InvalidOperationException("unexpected receipt outcome"),
        };
    }

    // ---------------------------------------------------------------- an advance, an overpayment

    /// <summary>
    /// Allocates what is left of an earlier receipt to this policy (owner, 05.10.2026; H21): no new receipt, the receipt
    /// keeps its date and its day's rate, the allocation is booked today. Only the policyholder's own receipt; what the
    /// policy does not need stays unallocated. Returns where to go: the card with the outcome.
    /// </summary>
    public static async Task<string> AllocateReceiptAsync(Db db, Rates rates, CurrentUser user, string? policyText, string? paymentText)
    {
        if (!Uuid.TryParse(policyText, out var policyId)) return "/policies";
        string Back(string outcome, string extra = "") => $"/policies/{policyText}?advance={outcome}{extra}";
        if (!user.CanWrite) return Back("read-only");
        if (!Uuid.TryParse(paymentText, out var paymentId)) return Back("not-found");
        var bookedOn = db.Today();

        object outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                // Same lock order as a new receipt (policy first), then the receipt itself.
                var policy = await tx.QuerySingleOrDefaultAsync<PolicyRow>("""
                    select p.id as Id, p.number as Number, p.status as Status, p.premium_currency as PremiumCurrency, c.policyholder_id as PolicyholderId
                    from core.policy p with (updlock, rowlock, holdlock) join core.contract c on c.id = p.contract_id where p.id = @policyId
                    """, new { policyId });
                if (policy is null || policy.Status == "draft") return (object)"not-found";
                await ReceiptReversal.LockReceiptAsync(tx, paymentId);
                // Read after the lock, so what another transaction has just allocated is counted.
                var receipt = (await Billing.LoadAdvancesAsync(tx, policy.PolicyholderId, paymentId)).FirstOrDefault();
                if (receipt is null)
                {
                    var own = await tx.ExecuteScalarAsync<int?>("select 1 from core.payment where id = @paymentId and payer_party_id = @payer",
                        new { paymentId, payer = policy.PolicyholderId });
                    return own is not null ? "nothing-left" : "not-found";
                }
                if (await Aml.SuspensionInTransactionAsync(tx, policy.PolicyholderId, Targets([policy.Id], policy.Number, receipt.Reference)) is not null) return "suspended";
                var balances = await BalancesAsync(tx, policy.Id, bookedOn);
                if (!balances.Any(b => b.Outstanding > 0)) return "nothing-owed";

                // The receipt settles at its own day's rate, as it would have when it was received.
                if (await rates.RateOnAsync(policy.Currency, receipt.ReceivedOn) is not { } rate) return "no-rate";
                var availableGel = Exact(receipt.AvailableGel);
                var result = AllocateExactly(availableGel, policy.Currency, balances, rate);
                foreach (var line in result.Lines)
                    await InsertAllocationAsync(tx, receipt.Id, line.InstallmentId, Decimal(line.Minor), Decimal(line.Gel), Rate6(rate), bookedOn);
                return (availableGel - result.UnallocatedGel, result.UnallocatedGel);
            });
        }
        catch (Exception e) when (IsCode(e, DbError.CheckViolation))
        {
            // Today's month is closed only if the accountant closed it early; nothing is written.
            outcome = "period-closed";
        }
        return outcome is ValueTuple<BigInteger, BigInteger> done
            ? Back("allocated", $"&gel={done.Item1.ToString(CultureInfo.InvariantCulture)}&left={done.Item2.ToString(CultureInfo.InvariantCulture)}")
            : Back((string)outcome);
    }

    /// <summary>
    /// Moves what a receipt holds beyond other policies' instalments (an overpayment left by an approved reduction) onto
    /// this policy (owner, 07.10.2026). In one transaction the receipt gives the excess back — negative allocation lines
    /// on the overpaid instalments, latest holding first, at the lines' own rate — and settles this policy's oldest unpaid
    /// instalments at its own day's rate. Locks (H58): first the policies in id order, then the receipt's lock, then the
    /// amounts are read again and only what the locked policies hold is moved.
    /// </summary>
    public static async Task<string> TransferOverpaymentAsync(Db db, Rates rates, CurrentUser user, string? policyText, string? paymentText)
    {
        if (!Uuid.TryParse(policyText, out var policyId)) return "/policies";
        string Back(string outcome, string extra = "") => $"/policies/{policyText}?advance={outcome}{extra}";
        if (!user.CanWrite) return Back("read-only");
        if (!Uuid.TryParse(paymentText, out var paymentId)) return Back("not-found");
        var bookedOn = db.Today();

        object outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                var policy = await tx.QuerySingleOrDefaultAsync<PolicyRow>($"{PolicySelect} where p.id = @policyId", new { policyId });
                if (policy is null || policy.Status == "draft") return (object)"not-found";
                var candidates = (await Billing.LoadReleasableAsync(tx, policy.PolicyholderId, bookedOn, policy.Id)).Where(r => r.PaymentId == paymentId).ToList();
                if (candidates.Count == 0)
                {
                    var own = await tx.ExecuteScalarAsync<int?>("select 1 from core.payment where id = @paymentId and payer_party_id = @payer",
                        new { paymentId, payer = policy.PolicyholderId });
                    return own is not null ? "nothing-left" : "not-found";
                }
                var sources = candidates.Select(r => r.PolicyId).Distinct().ToList();
                foreach (var id in new[] { policy.Id }.Concat(sources).OrderBy(Uuid.Text, StringComparer.Ordinal)) await LockPolicyAsync(tx, id);
                await ReceiptReversal.LockReceiptAsync(tx, paymentId);
                var status = await tx.QuerySingleOrDefaultAsync<string>("select status from core.policy where id = @policyId", new { policyId = policy.Id });
                if (status is null || status == "draft") return "not-found";
                var locked = (await Billing.LoadReleasableAsync(tx, policy.PolicyholderId, bookedOn, policy.Id))
                    .Where(r => r.PaymentId == paymentId && sources.Contains(r.PolicyId)).ToList();
                if (locked.Count == 0) return "nothing-left";
                if (await Aml.SuspensionInTransactionAsync(tx, policy.PolicyholderId, Targets(new[] { policy.Id }.Concat(sources), policy.Number, locked[0].Reference)) is not null)
                    return "suspended";
                var balances = await BalancesAsync(tx, policy.Id, bookedOn);
                if (!balances.Any(b => b.Outstanding > 0)) return "nothing-owed";
                if (await rates.RateOnAsync(policy.Currency, locked[0].ReceivedOn) is not { } rate) return "no-rate";

                // 1. The receipt gives the excess back, line by line.
                var releasedGel = BigInteger.Zero;
                foreach (var r in locked)
                {
                    var g = Exact(r.ExcessGel);
                    releasedGel += g;
                    await InsertAllocationAsync(tx, r.PaymentId, r.InstallmentId, Decimal(-Exact(r.Excess)), Decimal(-g), r.Rate, bookedOn);
                }
                // 2. It settles this policy, oldest unpaid instalment first, at its own day's rate.
                var result = AllocateExactly(releasedGel, policy.Currency, balances, rate);
                foreach (var line in result.Lines)
                    await InsertAllocationAsync(tx, paymentId, line.InstallmentId, Decimal(line.Minor), Decimal(line.Gel), Rate6(rate), bookedOn);
                return (releasedGel - result.UnallocatedGel, result.UnallocatedGel, releasedGel);
            });
        }
        catch (Exception e) when (IsCode(e, DbError.CheckViolation))
        {
            outcome = "period-closed";
        }
        return outcome is ValueTuple<BigInteger, BigInteger, BigInteger> done
            ? Back("transferred", $"&gel={done.Item1.ToString(CultureInfo.InvariantCulture)}&left={done.Item2.ToString(CultureInfo.InvariantCulture)}&released={done.Item3.ToString(CultureInfo.InvariantCulture)}")
            : Back((string)outcome);
    }

    // ---------------------------------------------------------------- reversal of a receipt

    private sealed record ReversalPlan(ReceiptReversal.ReceiptToReverse Receipt, IReadOnlyList<ReceiptReversal.ReceiptLine> Lines);

    private sealed record ReversalRequest(Guid Id, Guid ReceiptId, DateOnly ReversedOn, decimal AmountGel, string Method, Guid? PayerPartyId, string Reason);

    /// <summary>The reversal is above the company's threshold and no request carries it: thrown, so nothing of it is written.</summary>
    private sealed class ReversalAuthorizationNeeded(ReversalPlan plan) : Exception("the receipt's reversal needs the financial director's authorisation")
    {
        public ReversalPlan Plan { get; } = plan;
    }

    /// <summary>The receipt or its allocations are not what the request planned: thrown, so nothing of it stays.</summary>
    private sealed class StaleReversalRequest : Exception;

    private static bool SameLines(IReadOnlyList<ReceiptReversal.ReceiptLine> a, IReadOnlyList<ReceiptReversal.ReceiptLine> b) =>
        a.Count == b.Count && a.Select((l, i) => l.InstallmentId == b[i].InstallmentId && l.Amount == b[i].Amount && l.AmountGel == b[i].AmountGel && l.Rate == b[i].Rate).All(x => x);

    /// <summary>
    /// Reverses a mistaken receipt in the caller's transaction: a mirror payment with the opposite sign and opposite
    /// allocations, dated <paramref name="day"/>. Above the company's threshold (the GEL stored in the receipt) it needs
    /// the financial director: without a request it is thrown as ReversalAuthorizationNeeded before anything is written;
    /// with an approved request it is written only if the receipt and every one of its allocations are still exactly the
    /// ones planned (StaleReversalRequest if not).
    /// </summary>
    private static async Task<string> SettleReversalAsync(Tx tx, Guid userId, Guid paymentId, string reason, DateOnly day,
        (ReversalRequest Request, IReadOnlyList<ReceiptReversal.ReceiptLine> Lines)? request)
    {
        await ReceiptReversal.LockReceiptAsync(tx, paymentId);
        if (request is { } r0 && await tx.ExecuteScalarAsync<int>("select count(*) from core.payment where reversal_request_id = @id", new { id = r0.Request.Id }) > 0)
            return "already";
        // A claim set-off is not reversed here (docs/07 §4.3a): only reversing that claim payment gives the debt back.
        var original = await tx.QuerySingleOrDefaultAsync<ReceiptReversal.ReceiptToReverse>("""
            select id as Id, amount_gel as AmountGel, method as Method, payer_party_id as PayerPartyId from core.payment p
            where p.id = @paymentId and p.kind = N'receipt' and p.method <> N'claim_offset'
              and not exists (select 1 from core.payment r where r.reverses_payment_id = p.id)
            """, new { paymentId });
        if (original is null) return "state";
        var lines = await ReceiptReversal.ReceiptLinesAsync(tx, original.Id);
        if (request is { } r)
        {
            var q = r.Request;
            if (original.Id != q.ReceiptId || original.AmountGel != q.AmountGel || original.Method != q.Method || original.PayerPartyId != q.PayerPartyId || !SameLines(lines, r.Lines))
                throw new StaleReversalRequest();
        }
        else if (Exact(original.AmountGel) > await Settings.AuthorisationThresholdAsync(tx))
        {
            throw new ReversalAuthorizationNeeded(new ReversalPlan(original, lines));
        }
        await ReceiptReversal.MirrorReceiptAsync(tx, original, day, reason, userId, request?.Request.Id);
        return "ok";
    }

    /// <summary>
    /// Reverses a mistaken receipt on today (canWrite). Above the threshold nothing of it is written and a request for
    /// the financial director is kept instead, with the receipt, the reason and every allocation as stored. Returns
    /// where to go (?reversal=requested), or null when the legacy action returned without a redirect.
    /// </summary>
    public static async Task<string?> ReversePaymentAsync(Db db, CurrentUser user, string? paymentText, string? policyText, string? reasonText)
    {
        if (!user.CanWrite) return null;
        var reason = Insurance.Domain.Common.JsText.Trim(reasonText ?? "");
        if (!Uuid.TryParse(paymentText, out var paymentId) || !Uuid.TryParse(policyText, out var policyId) || reason == "") return null;
        var today = db.Today();

        ReversalPlan? plan = null;
        try
        {
            await db.AsUserAsync(user.Id, tx => SettleReversalAsync(tx, user.Id, paymentId, reason, today, null));
        }
        catch (ReversalAuthorizationNeeded e)
        {
            plan = e.Plan;
        }
        if (plan is null) return null;
        // Nothing of the reversal was written; the request and its plan are kept in a transaction of their own.
        await db.AsUserAsync(user.Id, async tx =>
        {
            var requestId = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert into core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by)
                values (@requestId, @receiptId, @policyId, @today, @amountGel, @method, @payer, @reason, @userId)
                """, new { requestId, receiptId = plan.Receipt.Id, policyId, today, amountGel = plan.Receipt.AmountGel, method = plan.Receipt.Method, payer = plan.Receipt.PayerPartyId, reason, userId = user.Id });
            for (var i = 0; i < plan.Lines.Count; i++)
            {
                var line = plan.Lines[i];
                await tx.ExecuteAsync("""
                    insert into core.receipt_reversal_request_line (request_id, position, installment_id, amount, amount_gel, rate)
                    values (@requestId, @position, @installmentId, @amount, @amountGel, @rate)
                    """, new { requestId, position = i + 1, installmentId = line.InstallmentId, amount = line.Amount, amountGel = line.AmountGel, rate = line.Rate });
            }
            return 0;
        });
        return $"/policies/{policyText}?reversal=requested";
    }

    /// <summary>
    /// Approves a receipt reversal request (owner, 17.10.2026, docs/05 ვ8): the permission payment.large.authorize, a
    /// quick check first and the binding one first in the transaction. Under the receipt's lock the receipt and every
    /// allocation are read again; the mirror is written, on the request's day with its reason, linked to the request,
    /// only if they are exactly the ones planned. Once at most. Returns where to go, or null (legacy: no redirect).
    /// </summary>
    public static async Task<string?> ApproveReceiptReversalRequestAsync(Db db, CurrentUser user, string? policyText, string? requestText)
    {
        if (!Uuid.TryParse(policyText, out var policyId)) return null;
        string Back(string outcome) => $"/policies/{policyText}?reversal={outcome}";
        if (!await PermissionStore.HasPermissionAsync(db, user, "payment.large.authorize")) return Back("not-allowed");
        if (!Uuid.TryParse(requestText, out var requestId)) return Back("state");

        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                if (!await PermissionStore.HasPermissionAsync(tx, user, "payment.large.authorize")) return "not-allowed";
                var request = await tx.QuerySingleOrDefaultAsync<ReversalRequest>("""
                    select id as Id, receipt_id as ReceiptId, reversed_on as ReversedOn, amount_gel as AmountGel, method as Method,
                           payer_party_id as PayerPartyId, reason as Reason
                    from core.receipt_reversal_request where id = @requestId and policy_id = @policyId
                    """, new { requestId, policyId });
                if (request is null) return "state";
                var lines = (await tx.QueryAsync<ReceiptReversal.ReceiptLine>("""
                    select installment_id as InstallmentId, amount as Amount, amount_gel as AmountGel, rate as Rate
                    from core.receipt_reversal_request_line where request_id = @requestId order by position
                    """, new { requestId })).ToList();
                var result = await SettleReversalAsync(tx, user.Id, request.ReceiptId, request.Reason, request.ReversedOn, (request, lines));
                return result == "ok" ? "approved" : result;
            });
        }
        catch (Exception e) when (e is StaleReversalRequest || IsCode(e, "RV002", "RV005"))
        {
            outcome = "stale";
        }
        catch (Exception e) when (IsCode(e, DbError.UniqueViolation))
        {
            outcome = "already";
        }
        catch (Exception e) when (DbError.From(e) is { Code: DbError.CheckViolation, Constraint: null })
        {
            // A closed month raises a check violation without a constraint (core.assert_period_open).
            outcome = "period-closed";
        }
        return Back(outcome);
    }

    // ---------------------------------------------------------------- refund of an overpayment

    public sealed record RefundLine(Guid InstallmentId, decimal Amount, decimal AmountGel);

    /// <summary>A premium refund worked out before it is written (0037): what a request keeps and its approval checks again.</summary>
    private sealed record RefundPlan(DateOnly RefundedOn, string Currency, decimal Rate, decimal AmountGel, Guid PayerPartyId, IReadOnlyList<RefundLine> Lines);

    private sealed record RefundRequest(Guid Id, DateOnly RefundedOn, decimal AmountGel, string Currency, decimal Rate, string Method, string? Reference, Guid PayerPartyId);

    private sealed class RefundAuthorizationNeeded(RefundPlan plan) : Exception("the premium refund needs the financial director's authorisation")
    {
        public RefundPlan Plan { get; } = plan;
    }

    private sealed class StaleRefundRequest : Exception;

    private static bool SamePlan(RefundPlan a, RefundPlan b) =>
        a.RefundedOn == b.RefundedOn && a.Currency == b.Currency && a.Rate == b.Rate && a.AmountGel == b.AmountGel && a.PayerPartyId == b.PayerPartyId
        && a.Lines.Count == b.Lines.Count
        && a.Lines.Select((l, i) => l.InstallmentId == b.Lines[i].InstallmentId && l.Amount == b.Lines[i].Amount && l.AmountGel == b.Lines[i].AmountGel).All(x => x);

    /// <summary>
    /// Pays back premium received beyond what the schedule requires on <paramref name="day"/>, in the caller's
    /// transaction: in GEL at that day's official rate; negative allocations take the excess off the instalments that
    /// hold it, newest first. Above the threshold it needs the financial director (RefundAuthorizationNeeded before
    /// anything is written); with an approved request it is written only if the plan is exactly the one asked for.
    /// </summary>
    private static async Task<string> SettleRefundAsync(Tx tx, Rates rates, Guid userId, Guid policyId, string method, string? reference, DateOnly day, RefundRequest? request, IReadOnlyList<RefundLine>? requestLines)
    {
        var policy = await tx.QuerySingleOrDefaultAsync<PolicyRow>("""
            select p.id as Id, p.number as Number, p.status as Status, p.premium_currency as PremiumCurrency, c.policyholder_id as PolicyholderId
            from core.policy p with (updlock, rowlock, holdlock) join core.contract c on c.id = p.contract_id where p.id = @policyId
            """, new { policyId });
        if (policy is null) return "state";
        // Under the policy's lock: a request already refunded is not refunded again.
        if (request is not null && await tx.ExecuteScalarAsync<int>("select count(*) from core.payment where refund_request_id = @id", new { id = request.Id }) > 0)
            return "already";
        if (await Aml.SuspensionInTransactionAsync(tx, policy.PolicyholderId, Targets([policyId], policy.Number, reference)) is not null) return "suspended";
        var settled = await SettledAsync(tx, policyId);
        // Only what is overpaid on the day: a reduction booked for a later day is refunded once it is in force (H23).
        var excess = (await Billing.LoadScheduleAsync(tx, policyId, day)).AsEnumerable().Reverse()
            .Select(r => (r.Id, Minor: settled.GetValueOrDefault(r.Id) - Exact(r.Amount)))
            .Where(r => r.Minor > 0)
            .ToList();
        var totalMinor = excess.Aggregate(BigInteger.Zero, (s, r) => s + r.Minor);
        if (totalMinor <= 0) return "nothing";

        var rate = await rates.RateOnAsync(policy.Currency, day) ?? throw new InvalidOperationException($"No {policy.Currency} rate for {Dates.ToIso(day)}");
        var scaled = Scaled(policy.Currency, rate);
        BigInteger InGel(BigInteger minor) => DivRound(minor * scaled, RateScale);
        var gelMinor = InGel(totalMinor);
        // Each line is the step between the GEL of the running sums of the foreign amounts, so every running sum is the
        // exact GEL of what is refunded so far: none exceeds the refund (PA002), no line turns sign.
        BigInteger minorSoFar = 0, gelSoFar = 0;
        var lines = excess.Select(r =>
        {
            minorSoFar += r.Minor;
            var lineGel = InGel(minorSoFar) - gelSoFar;
            gelSoFar += lineGel;
            return new RefundLine(r.Id, Decimal(r.Minor), Decimal(lineGel));
        }).ToList();
        var plan = new RefundPlan(day, policy.Currency, Rate6(rate), Decimal(gelMinor), policy.PolicyholderId, lines);

        if (request is not null)
        {
            var asked = new RefundPlan(request.RefundedOn, request.Currency.Trim(), request.Rate, request.AmountGel, request.PayerPartyId, requestLines!);
            if (!SamePlan(asked, plan)) throw new StaleRefundRequest();
        }
        else
        {
            if (!(gelMinor > 0)) throw new InvalidOperationException($"A payment amount must be a positive whole number of minor units: {gelMinor}");
            if (gelMinor > await Settings.AuthorisationThresholdAsync(tx)) throw new RefundAuthorizationNeeded(plan);
        }
        var refundId = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by, refund_request_id)
            values (@refundId, @day, @amountGel, @method, N'refund', @payer, @reference, @userId, @requestId)
            """, new { refundId, day, amountGel = -Decimal(gelMinor), method, payer = policy.PolicyholderId, reference, userId, requestId = request?.Id });
        foreach (var line in lines)
            await InsertAllocationAsync(tx, refundId, line.InstallmentId, -line.Amount, -line.AmountGel, Rate6(rate), day);
        return "ok";
    }

    /// <summary>
    /// Pays back premium received beyond what the schedule now requires (canWrite). Above the threshold nothing of it is
    /// written and a request for the financial director is kept instead. Returns where to go (?refund=requested,
    /// ?blocked=fms), or null when the legacy action returned without a redirect.
    /// </summary>
    public static async Task<string?> RefundOverpaymentAsync(Db db, Rates rates, CurrentUser user, string? policyText, string? methodText, string? referenceText)
    {
        if (!user.CanWrite) return null;
        var method = methodText ?? "bank";
        var reference = Insurance.Domain.Common.JsText.Trim(referenceText ?? "") is { Length: > 0 } r ? r : null;
        if (!Uuid.TryParse(policyText, out var policyId) || !BillingTexts.Payment.Methods.ContainsKey(method)) return null;
        var today = db.Today();

        string? outcome = null;
        RefundPlan? plan = null;
        try
        {
            outcome = await db.AsUserAsync(user.Id, tx => SettleRefundAsync(tx, rates, user.Id, policyId, method, reference, today, null, null));
        }
        catch (RefundAuthorizationNeeded e)
        {
            plan = e.Plan;
        }
        if (plan is not null)
        {
            await db.AsUserAsync(user.Id, async tx =>
            {
                var requestId = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_by)
                    values (@requestId, @policyId, @day, @amountGel, @currency, @rate, @method, @reference, @payer, @userId)
                    """, new { requestId, policyId, day = plan.RefundedOn, amountGel = plan.AmountGel, currency = plan.Currency, rate = plan.Rate, method, reference, payer = plan.PayerPartyId, userId = user.Id });
                for (var i = 0; i < plan.Lines.Count; i++)
                    await tx.ExecuteAsync("""
                        insert into core.premium_refund_request_line (request_id, position, installment_id, amount, amount_gel)
                        values (@requestId, @position, @installmentId, @amount, @amountGel)
                        """, new { requestId, position = i + 1, installmentId = plan.Lines[i].InstallmentId, amount = plan.Lines[i].Amount, amountGel = plan.Lines[i].AmountGel });
                return 0;
            });
            return $"/policies/{policyText}?refund=requested";
        }
        return outcome == "suspended" ? $"/policies/{policyText}?blocked=fms" : null;
    }

    /// <summary>
    /// Approves a premium refund request (owner, 17.10.2026, docs/05 ვ8): the permission payment.large.authorize, quick
    /// check first and the binding one first in the transaction. The refund is worked out again on the request's day with
    /// its method and reference and written only if the GEL and every line are exactly the same. Once at most.
    /// </summary>
    public static async Task<string?> ApprovePremiumRefundRequestAsync(Db db, Rates rates, CurrentUser user, string? policyText, string? requestText)
    {
        if (!Uuid.TryParse(policyText, out var policyId)) return null;
        string Back(string outcome) => $"/policies/{policyText}?refund={outcome}";
        if (!await PermissionStore.HasPermissionAsync(db, user, "payment.large.authorize")) return Back("not-allowed");
        if (!Uuid.TryParse(requestText, out var requestId)) return Back("state");

        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                if (!await PermissionStore.HasPermissionAsync(tx, user, "payment.large.authorize")) return "not-allowed";
                var request = await tx.QuerySingleOrDefaultAsync<RefundRequest>("""
                    select id as Id, refunded_on as RefundedOn, amount_gel as AmountGel, currency as Currency, rate as Rate, method as Method,
                           reference as Reference, payer_party_id as PayerPartyId
                    from core.premium_refund_request where id = @requestId and policy_id = @policyId
                    """, new { requestId, policyId });
                if (request is null) return "state";
                var lines = (await tx.QueryAsync<RefundLine>("""
                    select installment_id as InstallmentId, amount as Amount, amount_gel as AmountGel
                    from core.premium_refund_request_line where request_id = @requestId order by position
                    """, new { requestId })).ToList();
                var result = await SettleRefundAsync(tx, rates, user.Id, policyId, request.Method, request.Reference, request.RefundedOn, request, lines);
                return result == "ok" ? "approved" : result == "nothing" ? "stale" : result;
            });
        }
        catch (Exception e) when (e is StaleRefundRequest || IsCode(e, "RF002", "RF005", "PA002"))
        {
            outcome = "stale";
        }
        catch (Exception e) when (IsCode(e, DbError.UniqueViolation))
        {
            outcome = "already";
        }
        catch (Exception e) when (DbError.From(e) is { Code: DbError.CheckViolation, Constraint: null })
        {
            outcome = "period-closed";
        }
        return Back(outcome);
    }
}
