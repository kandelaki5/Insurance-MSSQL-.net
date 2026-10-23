using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Compliance;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.Billing;
using static Insurance.Application.Services.FormValues;

namespace Insurance.Application.Services;

/// <summary>
/// The claim actions of the old program (src/app/(app)/claims/actions.ts): registering a loss event and its claims,
/// adding a coverage to an event, reserve requests and their approval, the adjuster's decisions, claim payments (with the
/// deductible, the premium set-off, a total loss, the payee, the financial director's threshold and its requests),
/// recoveries and the reversal of a payment. Each runs in one transaction attributed to the user (Db.AsUserAsync), with
/// the legacy lock order: loss event, claim, policy, client. The pages only read the form and show the outcome.
/// </summary>
public static class ClaimActions
{
    /// <summary>The later cancellation of a totally lost policy could not be brought forward; the payment is rolled back.</summary>
    private sealed class TotalLossCancellationException() : Exception("the later cancellation of a totally lost policy could not be brought forward");

    /// <summary>What one claim payment comes to, worked out before it is written: what a request keeps and its approval checks again.</summary>
    public sealed record ClaimPaymentQuote(DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid, decimal NetPaidGel,
        decimal Rate, string PayeeKind, Guid PayeePartyId);

    /// <summary>A stored request for a claim payment above the threshold (0036): its quote and the choices of the form it came from.</summary>
    private sealed record ClaimPaymentRequestRow(Guid Id, DateOnly PaidOn, decimal Amount, decimal DeductibleWithheld, decimal PremiumOffset, decimal NetPaid,
        decimal NetPaidGel, decimal Rate, string PayeeKind, Guid PayeePartyId, bool OffsetPremium, string TotalLossMode, string? Note)
    {
        public ClaimPaymentQuote Quote => new(PaidOn, Amount, DeductibleWithheld, PremiumOffset, NetPaid, NetPaidGel, Rate, PayeeKind, PayeePartyId);
    }

    /// <summary>The net in GEL is above the company's threshold and no request carries it: thrown, so everything written on the way is rolled back.</summary>
    private sealed class ClaimPaymentAuthorizationNeededException(ClaimPaymentQuote quote) : Exception("the claim payment needs the financial director's authorisation")
    {
        public ClaimPaymentQuote Quote { get; } = quote;
    }

    /// <summary>The payment worked out on approval is not the one asked for: thrown, so nothing of it stays.</summary>
    private sealed class StaleClaimPaymentRequestException() : Exception("the claim payment request is stale");

    /// <summary>What a claim payment is asked with: from the form (payClaim) or from a stored request (approveClaimPaymentRequest).</summary>
    private sealed record ClaimPaymentInput(Guid ClaimId, DateOnly PaidOn, long GrossMinor, string PayeeKind, Guid? PayeePartyId, bool OffsetWanted, TotalLossDeduction Mode, string Note);

    private static readonly string[] TotalLossModes = ["none", "earned_unpaid", "all_unpaid"];

    private static decimal Dec(long minor) => FromTetri(minor);

    private static bool PeriodClosed(Exception e) => DbError.From(e)?.Code == DbError.CheckViolation;

    private static bool PeriodClosedWithoutConstraint(Exception e) => DbError.From(e) is { Code: DbError.CheckViolation, Constraint: null };

    private const string LockRow = "with (updlock, rowlock, holdlock)";

    // ------------------------------------------------------------------ registration

    /// <summary>One coverage asked for in a registration: the policy line, its initial reserve and the amount claimed.</summary>
    private sealed record RequestedCoverage(string LineId, string ReserveField, double Reserve, double Claimed);

    /// <summary>
    /// The coverages a registration asks for: `line:&lt;id&gt;` with `reserve:&lt;id&gt;` (and `claimed:&lt;id&gt;`) for a loss event
    /// over several coverages, or the single policy_line_id / initial_reserve / claimed_amount of the original form.
    /// </summary>
    private static List<RequestedCoverage> RequestedCoverages(PostedForm form, IReadOnlyDictionary<string, string> values)
    {
        var marked = form.Keys.Where(k => k.StartsWith("line:", StringComparison.Ordinal)).Select(k => k[5..]).ToList();
        if (marked.Count > 0)
            return marked.Select(id => new RequestedCoverage(id, $"reserve:{id}", Amount(values.GetValueOrDefault($"reserve:{id}")), Amount(values.GetValueOrDefault($"claimed:{id}")))).ToList();
        if (string.IsNullOrEmpty(values.GetValueOrDefault("policy_line_id"))) return [];
        return [new RequestedCoverage(values["policy_line_id"], "initial_reserve", Amount(values.GetValueOrDefault("initial_reserve")), Amount(values.GetValueOrDefault("claimed_amount")))];
    }

    /// <summary>The number of the k-th claim (from 1) of an event: the first carries the event's own number.</summary>
    public static string PartNumber(string eventNumber, int k) => k == 1 ? eventNumber : $"{eventNumber}-{k}";

    /// <summary>The first claim of the event a registration form (its one-off key) already registered, if any.</summary>
    private static Task<Guid?> RegisteredByAsync(Db db, Guid requestKey) =>
        db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<Guid?>("""
            select top (1) c.id from core.claim c join core.loss_event v on v.id = c.loss_event_id
            where v.request_key = @requestKey order by c.created_at, c.number
            """, new { requestKey }));

    private sealed record PolicyForClaim(Guid Id, DateOnly EffectiveFrom, DateOnly EffectiveTo, string PremiumCurrency, string Status, DateOnly? CancelledOn);

    private sealed record LineForClaim(Guid Id, string ClassCode, decimal? SumInsured);

    /// <summary>
    /// Registers a loss event and a claim on each coverage it touches (docs/07 §10), and books each initial reserve on the
    /// reporting date (order №01 art. 6.2). All in one transaction: if any coverage fails — cover dates, reserve,
    /// reinsurance limits, a closed month — nothing is written and no number is taken. A second submit of the same form
    /// (its one-off key) opens the event the first one registered instead of registering the loss again.
    /// </summary>
    public static async Task<FormOutcome> RegisterClaimAsync(Db db, CurrentUser user, PostedForm form)
    {
        var values = form.Trimmed();
        if (!user.CanWrite) return FormOutcome.Refused(message: Ka.Common.ReadOnly);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var requestKey = UuidOf(values.GetValueOrDefault("request_key"));
        if (requestKey is { } key && await RegisteredByAsync(db, key) is { } done) return FormOutcome.Redirect($"/claims/{done}?registered=1");

        var number = Domain.Common.JsText.ToUpperCase(values.GetValueOrDefault("policy_number") ?? "");
        var policy = await db.ReadTxAsync(tx => tx.QueryFirstOrDefaultAsync<PolicyForClaim>("""
            select id as Id, effective_from as EffectiveFrom, effective_to as EffectiveTo, premium_currency as PremiumCurrency, status as Status,
                   cancelled_on as CancelledOn
            from core.policy where upper(number) = @number
            """, new { number }));
        if (policy is null || policy.Status is not ("active" or "cancelled")) errors["policy_number"] = ClaimTexts.Errors.Policy;

        var requested = RequestedCoverages(form, values);
        if (requested.Count == 0) errors["policy_line_id"] = ClaimTexts.Errors.Coverage;
        if (requested.Select(r => r.LineId).Distinct(StringComparer.Ordinal).Count() != requested.Count) errors["policy_line_id"] = ClaimTexts.Errors.CoverageTwice;
        var lines = policy is not null && requested.Count > 0 && requested.All(r => IsUuid(r.LineId))
            ? (await db.ReadTxAsync(tx => tx.QueryAsync<LineForClaim>(
                "select id as Id, class_code as ClassCode, sum_insured as SumInsured from core.policy_line where id in @ids and policy_id = @policyId",
                new { ids = requested.Select(r => Guid.Parse(r.LineId)).Distinct().ToList(), policyId = policy.Id }))).ToList()
            : [];
        if (policy is not null && requested.Count > 0 && lines.Count != requested.Count) errors["policy_line_id"] = ClaimTexts.Errors.Coverage;

        var occurredOn = values.GetValueOrDefault("occurred_on") ?? "";
        var reportedOn = values.GetValueOrDefault("reported_on") ?? "";
        // Cover actually in force: the term, cut short by a cancellation (the cancel day itself is no longer covered,
        // docs/04 rule რ6). A loss before the cancellation may still be reported later.
        var coverEnd = policy is null ? null : (policy.CancelledOn is { } c && c < policy.EffectiveTo ? c : policy.EffectiveTo).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (DateOf(occurredOn) is null || (policy is not null && coverEnd is not null
            && (string.CompareOrdinal(occurredOn, Iso(policy.EffectiveFrom)) < 0 || string.CompareOrdinal(occurredOn, coverEnd) >= 0)))
            errors["occurred_on"] = ClaimTexts.Errors.OccurredOn;
        if (DateOf(reportedOn) is null || string.CompareOrdinal(reportedOn, occurredOn) < 0) errors["reported_on"] = ClaimTexts.Errors.ReportedOn;
        if (string.IsNullOrEmpty(values.GetValueOrDefault("description"))) errors["description"] = ClaimTexts.Errors.Description;
        var parts = requested.Select(r =>
        {
            // The old code matched the typed id against the stored one as text (lower case).
            var line = lines.FirstOrDefault(l => l.Id.ToString() == r.LineId);
            long? limitMinor = line?.SumInsured is { } s ? Tetri(s) : null;
            var reserveMinor = MinorOf(r.Reserve);
            if (reserveMinor <= 0 || (limitMinor is { } limit && reserveMinor > limit)) errors[r.ReserveField] = ClaimTexts.Errors.Reserve;
            return (Request: r, Line: line, ReserveMinor: reserveMinor);
        }).ToList();
        if (errors.Count > 0 || policy is null || parts.Any(p => p.Line is null)) return FormOutcome.Refused(errors);

        var occurred = DateOf(occurredOn)!.Value;
        var reported = DateOf(reportedOn)!.Value;
        var place = string.IsNullOrEmpty(values.GetValueOrDefault("occurred_place")) ? null : values["occurred_place"];
        var description = values["description"];
        Guid claimId;
        try
        {
            claimId = await db.AsUserAsync(user.Id, async tx =>
            {
                var eventNumber = await tx.NextNumberAsync("claim");
                var eventId = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.loss_event (id, number, policy_id, occurred_on, occurred_place, reported_on, description, request_key, created_by)
                    values (@eventId, @eventNumber, @policyId, @occurred, @place, @reported, @description, @requestKey, @userId)
                    """, new { eventId, eventNumber, policyId = policy.Id, occurred, place, reported, description, requestKey, userId = user.Id });
                Guid? first = null;
                for (var i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    var id = Guid.NewGuid();
                    var claimed = p.Request.Claimed;
                    await tx.ExecuteAsync("""
                        insert into core.claim (id, number, loss_event_id, policy_id, policy_line_id, currency, occurred_on, occurred_place, reported_on, description,
                                                claimed_amount, created_by)
                        values (@id, @number, @eventId, @policyId, @lineId, @currency, @occurred, @place, @reported, @description, @claimed, @userId)
                        """, new
                    {
                        id, number = eventNumber is null ? null : PartNumber(eventNumber, i + 1), eventId, policyId = policy.Id, lineId = p.Line!.Id,
                        currency = policy.PremiumCurrency, occurred, place, reported, description,
                        claimed = double.IsFinite(claimed) && claimed > 0 ? Fixed2(claimed) : (decimal?)null, userId = user.Id,
                    });
                    await tx.ExecuteAsync("""
                        insert into core.claim_reserve (claim_id, booked_on, change, reason, created_by) values (@id, @reported, @change, @reason, @userId)
                        """, new { id, reported, change = Dec(p.ReserveMinor), reason = ClaimTexts.Fields.InitialReserve, userId = user.Id });
                    await ReinsuranceStore.AttachClaimCessionsAsync(tx, id, p.Line.Id, p.Line.ClassCode, occurred);
                    first ??= id;
                }
                return first!.Value;
            });
        }
        catch (Exception e)
        {
            var error = DbError.From(e);
            if (error?.Code == DbError.CheckViolation) return FormOutcome.Refused(new Dictionary<string, string> { ["reported_on"] = ClaimTexts.Errors.PeriodClosed });
            if (error?.Code == Domain.Common.Numbering.SeriesExhaustedCode) return FormOutcome.Refused(errors, Ka.Common.SeriesExhausted);
            if (e is ReinsuranceException ri) return FormOutcome.Refused(errors, ReinsuranceTexts.Problems[Codes.Of(ri.Problem).Replace('_', '-')]);
            // The same form submitted twice at once: the other submit registered the event.
            if (error?.Code == DbError.UniqueViolation && requestKey is { } again && await RegisteredByAsync(db, again) is { } other)
                return FormOutcome.Redirect($"/claims/{other}?registered=1");
            throw;
        }
        return FormOutcome.Redirect($"/claims/{claimId}?registered=1");
    }

    private sealed record EventForCoverage(Guid Id, string Number, Guid PolicyId, DateOnly OccurredOn, string? OccurredPlace, string Description, string Currency);

    /// <summary>
    /// Adds a coverage to a loss event that is already registered (e.g. the tow truck found out later): a new claim on that
    /// coverage, numbered after the event's claims, with its initial reserve. The event is locked so two additions cannot
    /// take the same number.
    /// </summary>
    public static async Task<FormOutcome> AddEventCoverageAsync(Db db, CurrentUser user, PostedForm form)
    {
        var values = form.Trimmed();
        if (!user.CanWrite) return FormOutcome.Refused(message: Ka.Common.ReadOnly);
        var claimText = values.GetValueOrDefault("claim_id") ?? "";
        if (!IsUuid(claimText) || !IsUuid(values.GetValueOrDefault("policy_line_id") ?? ""))
            return FormOutcome.Refused(new Dictionary<string, string> { ["policy_line_id"] = ClaimTexts.Errors.Coverage });
        var claimId = Guid.Parse(claimText);
        var lineId = Guid.Parse(values["policy_line_id"]);
        var reserveMinor = MinorOf(Amount(values.GetValueOrDefault("initial_reserve")));
        var reportedOn = values.GetValueOrDefault("reported_on") ?? "";
        if (DateOf(reportedOn) is not { } reported) return FormOutcome.Refused(new Dictionary<string, string> { ["reported_on"] = ClaimTexts.Errors.ReportedOn });

        string added;
        try
        {
            added = await db.AsUserAsync(user.Id, async tx =>
            {
                var ev = await tx.QuerySingleOrDefaultAsync<EventForCoverage>($"""
                    select v.id as Id, v.number as Number, v.policy_id as PolicyId, v.occurred_on as OccurredOn, v.occurred_place as OccurredPlace,
                           v.description as Description, p.premium_currency as Currency
                    from core.loss_event v {LockRow} join core.policy p on p.id = v.policy_id
                    where v.id = (select loss_event_id from core.claim where id = @claimId)
                    """, new { claimId });
                if (ev is null) return "coverage";
                if (reported < ev.OccurredOn) return "date";
                var line = await tx.QuerySingleOrDefaultAsync<LineForClaim>(
                    "select id as Id, class_code as ClassCode, sum_insured as SumInsured from core.policy_line where id = @lineId and policy_id = @policyId",
                    new { lineId, policyId = ev.PolicyId });
                if (line is null) return "coverage";
                if (reserveMinor <= 0 || (line.SumInsured is { } s && reserveMinor > Tetri(s))) return "reserve";
                var held = await tx.QuerySingleOrDefaultAsync<(int N, bool Taken)>("""
                    select count(*), convert(bit, case when count(case when policy_line_id = @lineId then 1 end) > 0 then 1 else 0 end)
                    from core.claim where loss_event_id = @eventId
                    """, new { lineId = line.Id, eventId = ev.Id });
                if (held.Taken) return "twice";
                var id = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.claim (id, number, loss_event_id, policy_id, policy_line_id, currency, occurred_on, occurred_place, reported_on, description, created_by)
                    values (@id, @number, @eventId, @policyId, @lineId, @currency, @occurred, @place, @reported, @description, @userId)
                    """, new
                {
                    id, number = PartNumber(ev.Number, held.N + 1), eventId = ev.Id, policyId = ev.PolicyId, lineId = line.Id, currency = ev.Currency,
                    occurred = ev.OccurredOn, place = ev.OccurredPlace, reported, description = ev.Description, userId = user.Id,
                });
                await tx.ExecuteAsync("insert into core.claim_reserve (claim_id, booked_on, change, reason, created_by) values (@id, @reported, @change, @reason, @userId)",
                    new { id, reported, change = Dec(reserveMinor), reason = ClaimTexts.Fields.InitialReserve, userId = user.Id });
                await ReinsuranceStore.AttachClaimCessionsAsync(tx, id, line.Id, line.ClassCode, ev.OccurredOn);
                return id.ToString();
            });
        }
        catch (Exception e)
        {
            if (PeriodClosed(e)) return FormOutcome.Refused(new Dictionary<string, string> { ["reported_on"] = ClaimTexts.Errors.PeriodClosed });
            if (e is ReinsuranceException ri) return FormOutcome.Refused(message: ReinsuranceTexts.Problems[Codes.Of(ri.Problem).Replace('_', '-')]);
            throw;
        }
        return added switch
        {
            "coverage" => FormOutcome.Refused(new Dictionary<string, string> { ["policy_line_id"] = ClaimTexts.Errors.Coverage }),
            "twice" => FormOutcome.Refused(new Dictionary<string, string> { ["policy_line_id"] = ClaimTexts.Errors.CoverageTwice }),
            "reserve" => FormOutcome.Refused(new Dictionary<string, string> { ["initial_reserve"] = ClaimTexts.Errors.Reserve }),
            "date" => FormOutcome.Refused(new Dictionary<string, string> { ["reported_on"] = ClaimTexts.Errors.ReportedOn }),
            _ => FormOutcome.Redirect($"/claims/{added}?registered=1"),
        };
    }

    // ------------------------------------------------------------------ reserve

    /// <summary>A reserve level as typed: a non-negative amount with at most two decimals, read exactly (whole tetri).</summary>
    public static long? LevelOf(string text)
    {
        // /^\d{1,16}([.,]\d{1,2})?$/
        var sep = text.IndexOfAny(['.', ',']);
        var whole = sep < 0 ? text : text[..sep];
        var fraction = sep < 0 ? "" : text[(sep + 1)..];
        if (whole.Length is < 1 or > 16 || !whole.All(char.IsAsciiDigit)) return null;
        if (sep >= 0 && (fraction.Length is < 1 or > 2 || !fraction.All(char.IsAsciiDigit))) return null;
        return long.Parse(whole, System.Globalization.CultureInfo.InvariantCulture) * 100 + long.Parse(fraction.PadRight(2, '0'), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<decimal> ReserveLevelAsync(Tx tx, Guid claimId) =>
        tx.QuerySingleOrDefaultAsync<decimal>("select coalesce(sum(change), 0) from core.claim_reserve where claim_id = @claimId", new { claimId });

    /// <summary>
    /// Asks for the reserve to be moved to a new level (owner, 17.10.2026, docs/05 ვ8: the claims head approves a reserve
    /// change). Nothing is booked here: the request keeps the day, the level asked for, the level it was asked from and the
    /// difference, exactly, with the reason and who asked; the reserve moves only when someone else with
    /// claim.reserve.approve approves it. Who may ask is as before (canWrite). Returns where to go: null stays on the page.
    /// </summary>
    public static async Task<string?> ChangeReserveAsync(Db db, CurrentUser user, PostedForm form, DateOnly today)
    {
        var claimText = form.Get("claim_id") ?? "";
        if (!IsUuid(claimText)) return null;
        string Back(string outcome) => $"/claims/{claimText}?reserve={outcome}";
        if (!user.CanWrite) return Back("not-allowed");
        var bookedOn = form.Get("booked_on") ?? "";
        var reason = Domain.Common.JsText.Trim(form.Get("reason") ?? "");
        var target = LevelOf(Domain.Common.JsText.Trim(form.Get("new_level") ?? ""));
        if (DateOf(bookedOn) is not { } booked || booked > today || reason == "" || reason.Length > 500 || target is null) return Back("invalid");
        var claimId = Guid.Parse(claimText);

        var outcome = await db.AsUserAsync(user.Id, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>($"select status from core.claim {LockRow} where id = @claimId", new { claimId });
            if (status is null or "rejected" or "closed") return "state";
            var baseLevel = Tetri(await ReserveLevelAsync(tx, claimId));
            if (target == baseLevel) return "unchanged";
            await tx.ExecuteAsync("""
                insert into core.claim_reserve_request (claim_id, booked_on, base_level, new_level, change, reason, created_by)
                values (@claimId, @booked, @baseLevel, @newLevel, @change, @reason, @userId)
                """, new { claimId, booked, baseLevel = Dec(baseLevel), newLevel = Dec(target.Value), change = Dec(target.Value - baseLevel), reason, userId = user.Id });
            return "requested";
        });
        return Back(outcome);
    }

    /// <summary>
    /// Approves a reserve request (owner, 17.10.2026, docs/05 ვ8): the reserve movement is booked on the request's day with
    /// the request's difference, taken over by the database from the stored request, and linked to it — one movement per
    /// request. Who may approve is claim.reserve.approve, for the user of the session: a quick check first, the binding one
    /// first in the transaction. No one approves their own request (ვ4), the admin included. A closed or rejected claim, a
    /// reserve that moved since the request (stale) or a request's day in a month closed since is refused.
    /// </summary>
    public static async Task<string?> ApproveReserveRequestAsync(Db db, CurrentUser user, PostedForm form)
    {
        var claimText = form.Get("claim_id") ?? "";
        var requestText = form.Get("request_id") ?? "";
        if (!IsUuid(claimText)) return null;
        string Back(string outcome) => $"/claims/{claimText}?reserve={outcome}";
        if (!await PermissionStore.HasPermissionAsync(db, user, "claim.reserve.approve")) return Back("approve-not-allowed");
        if (!IsUuid(requestText)) return Back("state");
        var claimId = Guid.Parse(claimText);
        var requestId = Guid.Parse(requestText);

        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                // The binding permission check: in this transaction, before anything is read for the approval or written.
                if (!await PermissionStore.HasPermissionAsync(tx, user, "claim.reserve.approve")) return "approve-not-allowed";
                var status = await tx.QuerySingleOrDefaultAsync<string>($"select status from core.claim {LockRow} where id = @claimId", new { claimId });
                if (status is null) return "state";
                var request = await tx.QuerySingleOrDefaultAsync<(decimal BaseLevel, Guid CreatedBy, bool Done)?>("""
                    select q.base_level, q.created_by,
                           convert(bit, case when exists (select 1 from core.claim_reserve m where m.request_id = q.id) then 1 else 0 end)
                    from core.claim_reserve_request q where q.id = @requestId and q.claim_id = @claimId
                    """, new { requestId, claimId });
                if (request is not { } r) return "state";
                if (r.Done) return "already";
                if (r.CreatedBy == user.Id) return "own";
                if (status is "rejected" or "closed") return "state";
                if (Tetri(await ReserveLevelAsync(tx, claimId)) != Tetri(r.BaseLevel)) return "stale";
                await tx.ExecuteAsync("""
                    insert into core.claim_reserve (claim_id, booked_on, change, reason, created_by, request_id)
                    select claim_id, booked_on, change, reason, @userId, id from core.claim_reserve_request where id = @requestId
                    """, new { userId = user.Id, requestId });
                return "approved";
            });
        }
        catch (Exception e) when (DbError.From(e) is { } error && (error is { Code: DbError.CheckViolation, Constraint: null } || error.Code is DbError.UniqueViolation or "RR003" or "RR004"))
        {
            // A closed month raises a check violation without a constraint (core.assert_period_open).
            outcome = error.Code switch { DbError.CheckViolation => "period-closed", DbError.UniqueViolation => "already", "RR003" => "own", _ => "stale" };
        }
        return Back(outcome);
    }

    // ------------------------------------------------------------------ decision

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["review"] = ["registered"],
        ["approve"] = ["registered", "in_review"],
        ["reject"] = ["registered", "in_review"],
        ["close"] = ["paid", "rejected", "approved"],
        ["reopen"] = ["closed", "rejected"],
    };

    /// <summary>Status transitions decided by the adjuster: review, approve (with amount), reject, close, reopen. The page stays where it is.</summary>
    public static async Task DecideClaimAsync(Db db, CurrentUser user, PostedForm form, DateOnly today)
    {
        if (!user.CanWrite) return;
        var claimText = form.Get("claim_id") ?? "";
        var decision = form.Get("decision") ?? "";
        if (!IsUuid(claimText)) return;
        var claimId = Guid.Parse(claimText);

        await db.AsUserAsync(user.Id, async tx =>
        {
            var claim = await tx.QuerySingleOrDefaultAsync<(string Status, decimal? Deductible, decimal? SumInsured)?>($"""
                select c.status, l.deductible, l.sum_insured from core.claim c {LockRow} join core.policy_line l on l.id = c.policy_line_id where c.id = @claimId
                """, new { claimId });
            if (claim is not { } c) return 0;
            if (!Allowed.TryGetValue(decision, out var from) || !from.Contains(c.Status)) return 0;
            const string Reserve = "insert into core.claim_reserve (claim_id, booked_on, change, reason, created_by) values (@claimId, @today, @change, @reason, @userId)";

            if (decision == "review")
            {
                await tx.ExecuteAsync("update core.claim set status = N'in_review' where id = @claimId", new { claimId });
            }
            else if (decision == "approve")
            {
                var approved = Amount(form.Get("approved_amount") ?? "");
                if (!(approved > 0)) return 0;
                double? limit = c.SumInsured is { } s ? (double)s : null;
                var approvedAmount = limit is { } l ? Math.Min(approved, l) : approved;
                var deductible = Math.Min(c.Deductible is { } d ? (double)d : 0, approvedAmount);
                var totalLoss = form.Get("is_total_loss") == "on";
                await tx.ExecuteAsync("""
                    update core.claim set status = N'approved', approved_amount = @approved, deductible_amount = @deductible, is_total_loss = @totalLoss,
                      rejection_reason = null
                    where id = @claimId
                    """, new { approved = Fixed2(approvedAmount), deductible = Fixed2(deductible), totalLoss, claimId });
                // The reserve follows the decision: it now holds exactly what is still to be paid.
                var paid = await tx.QuerySingleOrDefaultAsync<decimal>("select coalesce(sum(amount), 0) from core.claim_payment where claim_id = @claimId", new { claimId });
                var level = await ReserveLevelAsync(tx, claimId);
                var change = JsRound(approvedAmount * 100) - Tetri(paid) - Tetri(level);
                if (change != 0) await tx.ExecuteAsync(Reserve, new { claimId, today, change = Dec(change), reason = ClaimTexts.Actions.Approve, userId = user.Id });
            }
            else if (decision == "reject")
            {
                var reason = Domain.Common.JsText.Trim(form.Get("rejection_reason") ?? "");
                if (reason == "") return 0;
                await tx.ExecuteAsync("update core.claim set status = N'rejected', rejection_reason = @reason where id = @claimId", new { reason, claimId });
                var level = Tetri(await ReserveLevelAsync(tx, claimId));
                if (level != 0) await tx.ExecuteAsync(Reserve, new { claimId, today, change = Dec(-level), reason = ClaimTexts.Actions.Reject, userId = user.Id });
            }
            else if (decision == "close")
            {
                var level = Tetri(await ReserveLevelAsync(tx, claimId));
                if (level != 0) await tx.ExecuteAsync(Reserve, new { claimId, today, change = Dec(-level), reason = ClaimTexts.Actions.Close, userId = user.Id });
                await tx.ExecuteAsync("update core.claim set status = N'closed', closed_on = @today where id = @claimId", new { today, claimId });
            }
            else if (decision == "reopen")
            {
                await tx.ExecuteAsync("update core.claim set status = N'in_review', closed_on = null, rejection_reason = null where id = @claimId", new { claimId });
            }
            return 0;
        });
    }

    // ------------------------------------------------------------------ payment

    private sealed record ClaimForPayment(string Status, Guid PolicyId, string Currency, decimal? ApprovedAmount, decimal DeductibleAmount, bool IsTotalLoss, Guid PolicyholderId);

    /// <summary>
    /// One claim payment worked out and written, in the caller's transaction (after its binding permission check): gross in
    /// the claim's currency, less the deductible and any premium set off, at the payment day's rate. A set-off is a premium
    /// receipt (method claim_offset) allocated to the oldest instalments; a total loss applies the chosen settlement rule
    /// (docs/04 rule რ15) and may cancel the policy on the loss date. A net above the company's threshold needs the
    /// financial director: from the form it is thrown as ClaimPaymentAuthorizationNeeded (everything written on the way goes
    /// with the rollback); from an approved request it is written with the request, if it is still exactly what was asked.
    /// </summary>
    private static async Task<string> SettleClaimPaymentAsync(Tx tx, Rates rates, Guid userId, ClaimPaymentInput input, ClaimPaymentRequestRow? request)
    {
        var (claimId, paidOn, grossMinor, payeeKind, payeePartyId, offsetWanted, mode, note) = input;
        // Lock order everywhere: loss event, claim, policy, client. The event comes first so that two payments on coverages
        // of one class in the event cannot both withhold its one deductible.
        var eventId = await tx.QuerySingleOrDefaultAsync<Guid?>("select loss_event_id from core.claim where id = @claimId", new { claimId });
        if (eventId is null) return "state";
        await tx.ExecuteAsync($"select id from core.loss_event {LockRow} where id = @eventId", new { eventId });
        var claim = await tx.QuerySingleOrDefaultAsync<ClaimForPayment>($"""
            select c.status as Status, c.policy_id as PolicyId, c.currency as Currency, c.approved_amount as ApprovedAmount, c.deductible_amount as DeductibleAmount,
                   c.is_total_loss as IsTotalLoss, co.policyholder_id as PolicyholderId
            from core.claim c {LockRow} join core.policy p on p.id = c.policy_id join core.contract co on co.id = p.contract_id
            where c.id = @claimId
            """, new { claimId });
        if (claim is null || claim.Status is not ("approved" or "paid") || claim.ApprovedAmount is not { } approvedAmount) return "state";
        if (request is not null
            && await tx.QuerySingleOrDefaultAsync<bool>("select convert(bit, case when exists (select 1 from core.claim_payment where request_id = @id) then 1 else 0 end)", new { id = request.Id }))
            return "already";
        var refs = await tx.QuerySingleOrDefaultAsync<(string ClaimNumber, string PolicyNumber)>(
            "select c.number, p.number from core.claim c join core.policy p on p.id = c.policy_id where c.id = @claimId", new { claimId });
        // The policy is locked before the client check so that a suspension aimed at this policy cannot interleave.
        await tx.ExecuteAsync($"select id from core.policy {LockRow} where id = @policyId", new { policyId = claim.PolicyId });
        var targets = new OperationTargets([claim.PolicyId.ToString()], [claimId.ToString()], [refs.ClaimNumber, refs.PolicyNumber]);
        if (await Aml.SuspensionInTransactionAsync(tx, claim.PolicyholderId, targets) is not null) return "suspended";
        // The one who receives the money (Codex H62/H63; AML law art. 36 and 41(7)): no funds to a sanctioned person, and an
        // FMS suspension in force over the recipient stops the payment as it does the policyholder's.
        var payeeId = payeeKind == "policyholder" ? claim.PolicyholderId : payeePartyId!.Value;
        if (payeeId != claim.PolicyholderId && await Aml.SuspensionInTransactionAsync(tx, payeeId, targets) is not null) return "payee";
        // Both participants (Codex H64): a sanctioned policyholder may not be paid through a clean third payee either.
        var blocked = await tx.QuerySingleOrDefaultAsync<int>("select count(*) from core.party where id in (@a, @b) and sanctions_blocked = 1",
            new { a = claim.PolicyholderId, b = payeeId });
        if (blocked > 0) return "payee";
        var paidSoFar = await tx.QuerySingleOrDefaultAsync<decimal>("select coalesce(sum(amount), 0) from core.claim_payment where claim_id = @claimId", new { claimId });
        if (Tetri(paidSoFar) + grossMinor > Tetri(approvedAmount)) return "exceeds";
        var currencyCode = claim.Currency.Trim();
        if (await rates.RateOnAsync(currencyCode, paidOn) is not { } rate) return "rate";
        var currency = ParseCurrency(currencyCode);
        RateProvider atRate = (_, _) => rate;

        // The agreed deductible is withheld in full and only once, spread over partial payments as needed (review H02) —
        // once for the class in the loss event, over all its coverages (docs/07 §10, Codex H50). Coverages of the class with
        // different deductibles: which one applies is not decided yet, so nothing is paid. The group is every claim of the
        // class in the event, whatever its status (Codex H52); the target is the coverages' agreed deductible (H51).
        var group = (await tx.QueryAsync<(Guid Id, decimal? LineDeductible)>("""
            select c.id, l.deductible from core.claim c join core.policy_line l on l.id = c.policy_line_id
            where c.loss_event_id = @eventId
              and l.class_code = (select l2.class_code from core.claim c2 join core.policy_line l2 on l2.id = c2.policy_line_id where c2.id = @claimId)
            """, new { eventId, claimId })).ToList();
        if (Deductible.ClassDeductible(group.Select(g => Tetri(g.LineDeductible ?? 0m)).ToList()) is not { } shared) return "deductible";
        var groupWithheld = await tx.QuerySingleOrDefaultAsync<decimal>("select coalesce(sum(deductible_withheld), 0) from core.claim_payment where claim_id in @ids",
            new { ids = group.Select(g => g.Id).ToList() });
        var deductibleMinor = Deductible.DeductibleToWithhold(shared, Tetri(groupWithheld), grossMinor);
        var afterDeductible = grossMinor - deductibleMinor;

        // Premium set-off and total-loss handling need the policy's receivable as of the payment day.
        long offsetMinor = 0;
        Guid? offsetPaymentId = null;
        if (offsetWanted || claim.IsTotalLoss)
        {
            // Lock order everywhere: claim, then policy (review H04).
            await tx.ExecuteAsync($"select id from core.policy {LockRow} where id = @policyId", new { policyId = claim.PolicyId });
            var schedule = await LoadScheduleAsync(tx, claim.PolicyId);
            var allocations = (await tx.QueryAsync<AllocationRow>("""
                select a.amount as Amount, a.booked_on as ReceivedOn, p.method as Method
                from core.payment_allocation a join core.payment p on p.id = a.payment_id
                join core.installment i on i.id = a.installment_id where i.policy_id = @policyId
                """, new { policyId = claim.PolicyId })).ToList();
            var receivable = Receivable.ReceivableAsOf(ToInstallments(schedule, currency), ToCredits(allocations, currency), paidOn, atRate);

            if (claim.IsTotalLoss && mode != TotalLossDeduction.AllUnpaid)
            {
                // Cancel on the loss date first (no earning after the loss), then settle against what is still owed.
                var c = await tx.QuerySingleOrDefaultAsync<(DateOnly OccurredOn, string Status, DateOnly? CancelledOn)?>(
                    "select cl.occurred_on, po.status, po.cancelled_on from core.claim cl join core.policy po on po.id = cl.policy_id where cl.id = @claimId", new { claimId });
                // A policy already cancelled from a later day has that cancellation brought forward to the loss (H24); if it
                // cannot be done, nothing is paid rather than a wrong amount.
                var laterCancellation = c is { Status: "cancelled", CancelledOn: { } was } && c.Value.OccurredOn < was;
                if (c is { } cc && (cc.Status == "active" || laterCancellation))
                {
                    var (outcome, _) = await PolicyCancel.CancelPolicyInTxAsync(tx, claim.PolicyId, cc.OccurredOn, ClaimTexts.Fields.IsTotalLoss, userId, bringForward: true);
                    if (laterCancellation && outcome != PolicyCancel.CancelOutcome.Done) throw new TotalLossCancellationException();
                }
            }
            var after = await LoadScheduleAsync(tx, claim.PolicyId);
            var position = Receivable.ReceivableAsOf(ToInstallments(after, currency), ToCredits(allocations, currency), paidOn, atRate);
            var settlement = claim.IsTotalLoss
                ? TotalLoss.SettleTotalLoss(new TotalLossInput([], position, new Money(afterDeductible, currency), paidOn, mode))
                : null;
            var wanted = settlement is not null ? settlement.Offset?.Credit.Amount.Minor ?? 0 : offsetWanted ? Math.Min(afterDeductible, receivable.Outstanding.Minor) : 0;
            offsetMinor = Math.Max(0, Math.Min(wanted, afterDeductible));

            if (offsetMinor > 0)
            {
                // In GEL by the finance core, exactly, at the rate already read for the payment day.
                var offsetGel = Money.ToGel(new Money(offsetMinor, currency), paidOn, atRate);
                var allocation = Receivable.AllocatePayment(offsetGel, paidOn, currency, position.Balances, atRate);
                var paymentId = Guid.NewGuid();
                await tx.ExecuteAsync("""
                    insert into core.payment (id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by)
                    values (@paymentId, @paidOn, @amountGel, N'claim_offset', N'receipt', @payer, @reference, @userId)
                    """, new { paymentId, paidOn, amountGel = Dec(offsetGel), payer = claim.PolicyholderId, reference = note == "" ? ClaimTexts.Fields.PremiumOffset : note, userId });
                foreach (var line in allocation.Lines)
                    await tx.ExecuteAsync("""
                        insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on)
                        values (@paymentId, @installmentId, @amount, @amountGel, @rate, @paidOn)
                        """, new { paymentId, installmentId = Guid.Parse(line.InstallmentId), amount = Dec(line.Amount.Minor), amountGel = Dec(line.AmountGel), rate = Math.Round(rate, 6), paidOn });
                offsetPaymentId = paymentId;
            }
        }
        var netMinor = afterDeductible - offsetMinor;
        var netGel = Money.ToGel(new Money(netMinor, currency), paidOn, atRate);

        // The financial director's authorisation (owner, 17.10.2026, docs/05 ვ8): the net actually transferred, in GEL as it
        // is recorded, strictly above the company's threshold — one payment, never a sum.
        var quote = new ClaimPaymentQuote(paidOn, Dec(grossMinor), Dec(deductibleMinor), Dec(offsetMinor), Dec(netMinor), Dec(netGel), Math.Round(rate, 6), payeeKind, payeeId);
        const string InsertPayment = """
            insert into core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                            payee_kind, payee_party_id, offset_payment_id, note, created_by, request_id)
            values (@claimId, @paidOn, @amount, @deductible, @offset, @net, @netGel, @rate, @payeeKind, @payeeId, @offsetPaymentId, @note, @userId, @requestId)
            """;
        if (request is not null)
        {
            if (request.Quote != quote) throw new StaleClaimPaymentRequestException();
        }
        else if (netGel > 0 && PaymentAuthorization.Check(new Money(netGel, Currency.GEL), paidOn, (_, _) => 1m, await Settings.AuthorisationThresholdAsync(tx)).RequiresAuthorization)
        {
            throw new ClaimPaymentAuthorizationNeededException(quote);
        }
        await tx.ExecuteAsync(InsertPayment, new
        {
            claimId, paidOn, amount = quote.Amount, deductible = quote.DeductibleWithheld, offset = quote.PremiumOffset, net = quote.NetPaid, netGel = quote.NetPaidGel,
            rate = quote.Rate, payeeKind, payeeId, offsetPaymentId, note = note == "" ? null : note, userId, requestId = request?.Id,
        });
        // Paying takes the amount out of the reserve.
        await tx.ExecuteAsync("insert into core.claim_reserve (claim_id, booked_on, change, reason, created_by) values (@claimId, @paidOn, @change, @reason, @userId)",
            new { claimId, paidOn, change = Dec(-grossMinor), reason = ClaimTexts.Actions.Pay, userId });
        var fullyPaid = Tetri(paidSoFar) + grossMinor >= Tetri(approvedAmount);
        await tx.ExecuteAsync("update core.claim set status = @status where id = @claimId", new { status = fullyPaid ? "paid" : "approved", claimId });
        return "ok";
    }

    private static TotalLossDeduction ModeOf(string? code) => code switch
    {
        "earned_unpaid" => TotalLossDeduction.EarnedUnpaid,
        "all_unpaid" => TotalLossDeduction.AllUnpaid,
        _ => TotalLossDeduction.None,
    };

    /// <summary>
    /// Pays an approved claim. Who may record it is the permission claim.payment.record (docs/05 ვ2), for the user of the
    /// session — never from the form: a quick check before anything is read, the binding one first in the transaction. A
    /// net above the company's threshold (owner, 17.10.2026, docs/05 ვ8) is not paid: nothing of the payment stays, and a
    /// request for the financial director is kept instead, with the quote and the form's choices.
    /// </summary>
    public static async Task<FormOutcome> PayClaimAsync(Db db, Rates rates, CurrentUser user, PostedForm form, DateOnly today)
    {
        var values = form.Trimmed();
        if (!await PermissionStore.HasPermissionAsync(db, user, "claim.payment.record")) return FormOutcome.Refused(message: ClaimTexts.PaymentNotAllowed);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimText = values.GetValueOrDefault("claim_id") ?? "";
        if (!IsUuid(claimText)) return FormOutcome.Refused(errors, ClaimTexts.Errors.State);
        var claimId = Guid.Parse(claimText);

        var paidOnText = values.GetValueOrDefault("paid_on") ?? "";
        var paidOn = DateOf(paidOnText);
        if (paidOn is null || paidOn > today) errors["paid_on"] = ClaimTexts.Errors.Date;
        var grossMinor = MinorOf(Amount(values.GetValueOrDefault("amount")));
        if (grossMinor <= 0) errors["amount"] = ClaimTexts.Errors.Amount;
        var payeeKind = values.GetValueOrDefault("payee_kind") ?? "";
        var knownKind = ClaimTexts.Fields.PayeeKinds.ContainsKey(payeeKind);
        if (!knownKind) errors["payee_kind"] = ClaimTexts.Errors.State;
        // Anyone but the policyholder is taken from the register by their ID number (stage 4, item 7), so the payment records
        // who actually received the money. The policyholder is the policy's own client.
        Guid? payeePartyId = null;
        if (knownKind && payeeKind != "policyholder")
        {
            var idNumber = values.GetValueOrDefault("payee_id_number") ?? "";
            var found = idNumber == ""
                ? []
                : (await db.ReadTxAsync(tx => tx.QueryAsync<Guid>("select top (2) id from core.party where tax_id = @idNumber or foreign_id = @idNumber", new { idNumber }))).ToList();
            if (found.Count == 1) payeePartyId = found[0];
            else errors["payee_id_number"] = ClaimTexts.Errors.Payee;
        }
        var offsetWanted = form.Has("offset_premium");
        var modeCode = values.GetValueOrDefault("total_loss_mode");
        var mode = ModeOf(TotalLossModes.Contains(modeCode) ? modeCode : "none");
        if (errors.Count > 0) return FormOutcome.Refused(errors);

        var input = new ClaimPaymentInput(claimId, paidOn!.Value, grossMinor, payeeKind, payeePartyId, offsetWanted, mode, values.GetValueOrDefault("note") ?? "");
        string result;
        ClaimPaymentQuote? quote = null;
        try
        {
            result = await db.AsUserAsync(user.Id, async tx =>
            {
                // The binding permission check: in this transaction, before anything is read for the payment or written.
                if (!await PermissionStore.HasPermissionAsync(tx, user, "claim.payment.record")) return "not-allowed";
                return await SettleClaimPaymentAsync(tx, rates, user.Id, input, null);
            });
        }
        catch (Exception e) when (PeriodClosed(e))
        {
            return FormOutcome.Refused(new Dictionary<string, string> { ["paid_on"] = ClaimTexts.Errors.PeriodClosed });
        }
        catch (TotalLossCancellationException)
        {
            return FormOutcome.Refused(errors, ClaimTexts.Errors.TotalLossCancellation);
        }
        catch (ClaimPaymentAuthorizationNeededException needed)
        {
            quote = needed.Quote;
            result = "ok";
        }
        if (quote is { } q)
        {
            // Nothing of the payment stayed (rolled back above); the request is kept in a transaction of its own, by the same
            // binding check. Its day is in an open month (0036, as a payment's).
            string asked;
            try
            {
                asked = await db.AsUserAsync(user.Id, async tx =>
                {
                    if (!await PermissionStore.HasPermissionAsync(tx, user, "claim.payment.record")) return "not-allowed";
                    await tx.ExecuteAsync("""
                        insert into core.claim_payment_request (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                                payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_by)
                        values (@claimId, @paidOn, @amount, @deductible, @offset, @net, @netGel, @rate, @payeeKind, @payeeId, @offsetWanted, @mode, @note, @userId)
                        """, new
                    {
                        claimId, paidOn = q.PaidOn, amount = q.Amount, deductible = q.DeductibleWithheld, offset = q.PremiumOffset, net = q.NetPaid, netGel = q.NetPaidGel,
                        rate = q.Rate, payeeKind = q.PayeeKind, payeeId = q.PayeePartyId, offsetWanted, mode = Codes.Of(mode),
                        note = string.IsNullOrEmpty(values.GetValueOrDefault("note")) ? null : values["note"], userId = user.Id,
                    });
                    return "requested";
                });
            }
            catch (Exception e) when (PeriodClosed(e))
            {
                return FormOutcome.Refused(new Dictionary<string, string> { ["paid_on"] = ClaimTexts.Errors.PeriodClosed });
            }
            if (asked == "not-allowed") return FormOutcome.Refused(errors, ClaimTexts.PaymentNotAllowed);
            return FormOutcome.Redirect($"/claims/{claimText}?payment=requested");
        }
        return result switch
        {
            "not-allowed" => FormOutcome.Refused(errors, ClaimTexts.PaymentNotAllowed),
            "state" or "already" => FormOutcome.Refused(errors, ClaimTexts.Errors.State),
            "suspended" => FormOutcome.Refused(errors, AmlTexts.Suspension.Blocked),
            "payee" => FormOutcome.Refused(errors, ClaimTexts.Errors.PayeeBlocked),
            "deductible" => FormOutcome.Refused(errors, ClaimTexts.Errors.DeductibleDiffers),
            "exceeds" => FormOutcome.Refused(new Dictionary<string, string> { ["amount"] = ClaimTexts.Errors.Exceeds }),
            "rate" => FormOutcome.Refused(new Dictionary<string, string> { ["paid_on"] = ClaimTexts.Errors.Rate }),
            _ => FormOutcome.Redirect($"/claims/{claimText}?paid=1"),
        };
    }

    /// <summary>
    /// Approves a claim payment request (owner, 17.10.2026, docs/05 ვ8): the financial director's authorisation, the
    /// permission payment.large.authorize, for the user of the session — a quick check first, the binding one first in the
    /// transaction. Whoever has it may approve a request they made themselves; it does not need claim.payment.record. The
    /// payment is worked out again from the request's choices, with every check of a payment; it is written, with the
    /// request, only if it is exactly the quote that was asked for — otherwise nothing is written (stale). One request is
    /// paid once at most (checked under the claim's lock; the database's unique index too). Returns where to go.
    /// </summary>
    public static async Task<string?> ApproveClaimPaymentRequestAsync(Db db, Rates rates, CurrentUser user, PostedForm form)
    {
        var claimText = form.Get("claim_id") ?? "";
        var requestText = form.Get("request_id") ?? "";
        if (!IsUuid(claimText)) return null;
        string Back(string outcome) => $"/claims/{claimText}?payment={outcome}";
        if (!await PermissionStore.HasPermissionAsync(db, user, "payment.large.authorize")) return Back("not-allowed");
        if (!IsUuid(requestText)) return Back("state");
        var claimId = Guid.Parse(claimText);
        var requestId = Guid.Parse(requestText);

        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                // The binding permission check: in this transaction, before anything is read for the approval or written.
                if (!await PermissionStore.HasPermissionAsync(tx, user, "payment.large.authorize")) return "not-allowed";
                var request = await tx.QuerySingleOrDefaultAsync<ClaimPaymentRequestRow>("""
                    select id as Id, paid_on as PaidOn, amount as Amount, deductible_withheld as DeductibleWithheld, premium_offset as PremiumOffset, net_paid as NetPaid,
                           net_paid_gel as NetPaidGel, rate as Rate, payee_kind as PayeeKind, payee_party_id as PayeePartyId, offset_premium as OffsetPremium,
                           total_loss_mode as TotalLossMode, note as Note
                    from core.claim_payment_request where id = @requestId and claim_id = @claimId
                    """, new { requestId, claimId });
                if (request is null) return "state";
                var result = await SettleClaimPaymentAsync(tx, rates, user.Id, new ClaimPaymentInput(
                    claimId, request.PaidOn, Tetri(request.Amount), request.PayeeKind, request.PayeeKind == "policyholder" ? null : request.PayeePartyId,
                    request.OffsetPremium, ModeOf(request.TotalLossMode), request.Note ?? ""), request);
                return result == "ok" ? "approved" : result;
            });
        }
        catch (StaleClaimPaymentRequestException)
        {
            outcome = "stale";
        }
        catch (TotalLossCancellationException)
        {
            outcome = "total-loss";
        }
        catch (Exception e) when (DbError.From(e)?.Code is "CQ002" or DbError.UniqueViolation or DbError.CheckViolation)
        {
            outcome = DbError.From(e)!.Code switch { "CQ002" => "stale", DbError.UniqueViolation => "already", _ => "period-closed" };
        }
        return Back(outcome);
    }

    // ------------------------------------------------------------------ recovery

    /// <summary>
    /// Records money recovered after a claim; income is recognised on receipt. Who may record it is the permission
    /// claim.recovery.receive (docs/05 ვ2), for the user of the session — never from the form. A quick check answers first;
    /// the binding one is read again inside the transaction before the row is written. A refusal goes back to the claim with
    /// its message; anything else stays on the page (null).
    /// </summary>
    public static async Task<string?> AddRecoveryAsync(Db db, CurrentUser user, PostedForm form)
    {
        var claimText = form.Get("claim_id") ?? "";
        string? Refuse() => IsUuid(claimText) ? $"/claims/{claimText}?recovery=not-allowed" : null;
        if (!await PermissionStore.HasPermissionAsync(db, user, "claim.recovery.receive")) return Refuse();
        var kind = form.Get("kind") ?? "";
        var receivedOn = DateOf(form.Get("received_on") ?? "");
        var gel = Amount(form.Get("amount_gel") ?? "");
        // The amount is stored with two decimals and a receipt must be above zero, so the stored value is what is checked:
        // 0.004 would be stored as 0.00 (review PR220-RECOVERY-ROUNDZERO).
        decimal? stored = double.IsFinite(gel) ? Fixed2(gel) : null;
        var note = Domain.Common.JsText.Trim(form.Get("note") ?? "") is { Length: > 0 } n ? n : null;
        if (!IsUuid(claimText) || !ClaimTexts.Fields.RecoveryKinds.ContainsKey(kind) || receivedOn is null || !(stored > 0)) return null;
        var claimId = Guid.Parse(claimText);
        var outcome = await db.AsUserAsync(user.Id, async tx =>
        {
            // The binding permission check: in this transaction, before the row is written.
            if (!await PermissionStore.HasPermissionAsync(tx, user, "claim.recovery.receive")) return "not-allowed";
            await tx.ExecuteAsync("""
                insert into core.claim_recovery (claim_id, kind, received_on, amount_gel, note, created_by) values (@claimId, @kind, @receivedOn, @stored, @note, @userId)
                """, new { claimId, kind, receivedOn, stored, note, userId = user.Id });
            return "ok";
        });
        return outcome == "not-allowed" ? Refuse() : null;
    }

    // ------------------------------------------------------------------ reversal

    private sealed record ClaimForReversal(string Status, Guid PolicyId, decimal? ApprovedAmount, Guid PolicyholderId, string ClaimNumber, string PolicyNumber);

    private sealed record OriginalPayment(string Kind, DateOnly PaidOn, Guid? PayeePartyId, decimal PremiumOffset, Guid? OffsetPaymentId, bool Reversed);

    /// <summary>
    /// Reverses a recorded claim payment (owner, 17.10.2026, docs/05 ვ8: the accountant, with no approval after it; design
    /// docs/07 §4.3 and §4.3ა). The reversal is a new claim_payment row, every amount the exact opposite of the original's
    /// at the original's rate — copied by the database from the stored row, nothing computed again — and the original is
    /// never changed. In the same transaction: the reserve gets the gross back, a premium set-off is given back by reversing
    /// its receipt (the premium debt returns), and the claim's status follows the signed sum of what was paid. Who may do it
    /// is claim.payment.reverse, for the user of the session. Lock order as in payClaim: loss event, claim, policy, client;
    /// then the set-off receipt. A suspension or a sanction on the client or the payee stops the reversal as it stops a
    /// payment (docs/07 §4.3ა ზ, B2). Returns where to go: null stays on the page.
    /// </summary>
    public static async Task<string?> ReverseClaimPaymentAsync(Db db, CurrentUser user, PostedForm form, DateOnly today)
    {
        var claimText = form.Get("claim_id") ?? "";
        if (!IsUuid(claimText)) return null;
        string Back(string outcome) => $"/claims/{claimText}?reversal={outcome}";
        if (!await PermissionStore.HasPermissionAsync(db, user, "claim.payment.reverse")) return Back("not-allowed");

        var paymentText = form.Get("claim_payment_id") ?? "";
        var reason = form.Get("reason") ?? "";
        var reversedOn = DateOf(form.Get("reversed_on") ?? "");
        var returnedOn = DateOf(Domain.Common.JsText.Trim(form.Get("returned_on") ?? ""));
        var bankReference = Domain.Common.JsText.Trim(form.Get("bank_reference") ?? "");
        var note = Domain.Common.JsText.Trim(form.Get("note") ?? "") is { Length: > 0 } n ? n : null;
        var returned = reason == "returned";
        if (!IsUuid(paymentText) || !ClaimTexts.Fields.ReversalReasons.ContainsKey(reason) || reversedOn is null || reversedOn > today
            || (returned && (returnedOn is null || returnedOn > today || bankReference == "" || bankReference.Length > 100)))
            return Back("invalid");
        var claimId = Guid.Parse(claimText);
        var paymentId = Guid.Parse(paymentText);
        var on = reversedOn.Value;

        string outcome;
        try
        {
            outcome = await db.AsUserAsync(user.Id, async tx =>
            {
                // The binding permission check: in this transaction, before anything is read for the reversal or written.
                if (!await PermissionStore.HasPermissionAsync(tx, user, "claim.payment.reverse")) return "not-allowed";
                var eventId = await tx.QuerySingleOrDefaultAsync<Guid?>("""
                    select c.loss_event_id from core.claim_payment cp join core.claim c on c.id = cp.claim_id where cp.id = @paymentId and cp.claim_id = @claimId
                    """, new { paymentId, claimId });
                if (eventId is null) return "state";
                await tx.ExecuteAsync($"select id from core.loss_event {LockRow} where id = @eventId", new { eventId });
                var claim = await tx.QuerySingleOrDefaultAsync<ClaimForReversal>($"""
                    select c.status as Status, c.policy_id as PolicyId, c.approved_amount as ApprovedAmount, co.policyholder_id as PolicyholderId,
                           c.number as ClaimNumber, p.number as PolicyNumber
                    from core.claim c {LockRow} join core.policy p on p.id = c.policy_id join core.contract co on co.id = p.contract_id
                    where c.id = @claimId
                    """, new { claimId });
                if (claim is null || claim.Status is not ("approved" or "paid") || claim.ApprovedAmount is null) return "state";
                await tx.ExecuteAsync($"select id from core.policy {LockRow} where id = @policyId", new { policyId = claim.PolicyId });
                var targets = new OperationTargets([claim.PolicyId.ToString()], [claimId.ToString()], [claim.ClaimNumber, claim.PolicyNumber]);
                if (await Aml.SuspensionInTransactionAsync(tx, claim.PolicyholderId, targets) is not null) return "suspended";

                var original = await tx.QuerySingleOrDefaultAsync<OriginalPayment>("""
                    select cp.kind as Kind, cp.paid_on as PaidOn, cp.payee_party_id as PayeePartyId, cp.premium_offset as PremiumOffset,
                           cp.offset_payment_id as OffsetPaymentId,
                           convert(bit, case when exists (select 1 from core.claim_payment r where r.reverses_claim_payment_id = cp.id) then 1 else 0 end) as Reversed
                    from core.claim_payment cp where cp.id = @paymentId
                    """, new { paymentId });
                if (original is null || original.Kind != "payment") return "state";
                if (original.Reversed) return "already";
                if (on < original.PaidOn) return "before";
                // Money given back cannot come back before it was paid (0034). The day it came back may be in a closed month:
                // it is only recorded, the reversal is booked on its own day.
                if (returned && returnedOn < original.PaidOn) return "returned-before";
                // The one who received the money is the original's payee, never taken from the form.
                var payeeId = original.PayeePartyId ?? claim.PolicyholderId;
                if (payeeId != claim.PolicyholderId && await Aml.SuspensionInTransactionAsync(tx, payeeId, targets) is not null) return "payee";
                var blocked = await tx.QuerySingleOrDefaultAsync<int>("select count(*) from core.party where id in (@a, @b) and sanctions_blocked = 1",
                    new { a = claim.PolicyholderId, b = payeeId });
                if (blocked > 0) return "payee";

                // The premium set-off goes back with the payment: its receipt is reversed, so the premium debt returns. A receipt
                // already reversed on its own, or a set-off without its receipt (an old row), is left for a person to look at.
                Guid? offsetBack = null;
                if (original.OffsetPaymentId is { } receiptId)
                {
                    await ReceiptReversal.LockReceiptAsync(tx, receiptId);
                    var receipt = await tx.QuerySingleOrDefaultAsync<(Guid Id, decimal AmountGel, string Method, Guid? PayerPartyId, string Kind, bool Reversed)?>("""
                        select p.id, p.amount_gel, p.method, p.payer_party_id, p.kind,
                               convert(bit, case when exists (select 1 from core.payment r where r.reverses_payment_id = p.id) then 1 else 0 end)
                        from core.payment p where p.id = @receiptId
                        """, new { receiptId });
                    if (receipt is not { Kind: "receipt" } r) return "snapshot";
                    if (r.Reversed) return "offset-reversed";
                    offsetBack = await ReceiptReversal.MirrorReceiptAsync(tx, new ReceiptReversal.ReceiptToReverse(r.Id, r.AmountGel, r.Method, r.PayerPartyId),
                        on, note ?? ClaimTexts.Actions.Reverse, user.Id);
                }
                else if (Tetri(original.PremiumOffset) != 0)
                {
                    return "snapshot";
                }

                // Every amount copied from the stored original by the database, with the opposite sign: nothing is rounded again.
                await tx.ExecuteAsync("""
                    insert into core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                                    payee_kind, payee_party_id, offset_payment_id, note, created_by,
                                                    kind, reverses_claim_payment_id, reversal_reason, returned_on, bank_reference)
                    select newid(), claim_id, @on, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate,
                           payee_kind, payee_party_id, @offsetBack, @note, @userId,
                           N'reversal', id, @reason, @returnedOn, @bankReference
                    from core.claim_payment where id = @paymentId
                    """, new
                {
                    on, offsetBack, note, userId = user.Id, reason, returnedOn = returned ? returnedOn : null, bankReference = returned ? bankReference : null, paymentId,
                });
                // The reserve gets the gross back on the reversal's day.
                await tx.ExecuteAsync("""
                    insert into core.claim_reserve (claim_id, booked_on, change, reason, created_by)
                    select claim_id, @on, amount, @why, @userId from core.claim_payment where id = @paymentId
                    """, new { on, why = ClaimTexts.Actions.Reverse, userId = user.Id, paymentId });
                // The status by the signed gross paid (docs/07 §4.3, Codex H44): what was paid, reversals included, against the approved amount.
                await tx.ExecuteAsync("""
                    update c set status = case when (select coalesce(sum(amount), 0) from core.claim_payment where claim_id = c.id) >= c.approved_amount
                                               then N'paid' else N'approved' end
                    from core.claim c where c.id = @claimId
                    """, new { claimId });
                return "ok";
            });
        }
        catch (Exception e) when (DbError.From(e) is { } error && (error is { Code: DbError.CheckViolation, Constraint: null } || error.Code is DbError.UniqueViolation or "CP006" or "CP007"))
        {
            // A closed month raises a check violation without a constraint (core.assert_period_open); a check of the table is a defect.
            outcome = error.Code switch { DbError.CheckViolation => "period-closed", DbError.UniqueViolation => "already", "CP006" => "regress", _ => "returned-before" };
        }
        return Back(outcome);
    }
}
