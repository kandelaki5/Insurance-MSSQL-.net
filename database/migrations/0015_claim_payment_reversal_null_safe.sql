-- A claim payment's reversal compared with its payment NULL-safely, and the H46 snapshot and set-off checks made to refuse an
-- UNKNOWN (Codex R006-01, INS-DOTNET-MSSQL-006, 24.10.2026). 0012 and 0013 are applied and stay as they are; this replaces what
-- they got wrong. (Number 0015: 0014 is the OpenID Connect provider's table on the architecture branch, ADR 0003.)
--
-- 1. claim_payment_guard [CP002] compared the payee, the set-off's rate and its currency as "not ((a is null and b is null) or
--    a = b)": UNKNOWN, so not refused, when the payment had a value and the reversal NULL (or the other way round). A reversal of a
--    set-off at 2.70 USD could drop both, which claim_payment_offset_rate's all-null branch let through, and CP009 checks payments
--    only. The trigger below is 0012's with that one comparison made with EXCEPT.
-- 2. A CHECK refuses only FALSE, not UNKNOWN. claim_limit_snapshot let a partial snapshot in (a rate without its day, a day
--    without its rate, a GEL limit without a rate: the second branch was UNKNOWN), and the set-off checks let a currency without its
--    rate in. Each is re-made as CASE WHEN … THEN 1 ELSE 0 END = 1, the same rule with UNKNOWN as a refusal. Rows written before
--    (an all-null snapshot, a set-off without rate and currency) still satisfy it; existing rows are checked when it is added.

alter table core.claim drop constraint claim_limit_snapshot;
GO
alter table core.claim with check add constraint claim_limit_snapshot check (case when
  (limit_rate is null and limit_rate_on is null and limit_gel is null and deductible_gel is null)
  or (currency = N'GEL' and limit_rate > 0 and limit_rate_on = occurred_on
      and (limit_gel is null or limit_gel >= 0) and (deductible_gel is null or deductible_gel >= 0))
  then 1 else 0 end = 1);
GO

alter table core.claim_payment drop constraint claim_payment_offset_rate;
GO
alter table core.claim_payment with check add constraint claim_payment_offset_rate check (case when
  ((offset_rate is null and offset_currency is null) or (premium_offset <> 0 and offset_rate > 0 and offset_currency is not null))
  and (offset_currency is null or offset_currency <> N'GEL' or offset_rate = 1)
  then 1 else 0 end = 1);
GO

alter table core.claim_payment_request drop constraint claim_payment_request_offset_rate;
GO
alter table core.claim_payment_request with check add constraint claim_payment_request_offset_rate check (case when
  ((offset_rate is null and offset_currency is null) or (premium_offset <> 0 and offset_rate > 0 and offset_currency is not null))
  and (offset_currency is null or offset_currency <> N'GEL' or offset_rate = 1)
  then 1 else 0 end = 1);
GO

-- claim_payment_period (0011), claim_payment_request_guard (0036), claim_payment_reversal_guard (0033, 0034).
-- A payment above the threshold carries out an approved request exactly; a reversal is the exact mirror of
-- one payment, dated on or after it, gives back the payment's own premium set-off, and may not leave a
-- regress case above what was paid on any day from its date on.
-- H46 (0012): a request's set-off rate and currency are taken over too, and a reversal keeps them; a GEL claim is paid
-- at rate 1 [CP008]; a set-off is in the policy's currency, and on a GEL claim of a foreign-currency policy it carries
-- its rate [CP009].
alter trigger core.claim_payment_guard on core.claim_payment
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @claim uniqueidentifier, @paid_on date, @amount decimal(18, 2), @deductible decimal(18, 2),
          @offset decimal(18, 2), @net decimal(18, 2), @net_gel decimal(18, 2), @rate decimal(12, 6), @payee_kind nvarchar(20),
          @payee uniqueidentifier, @offset_payment uniqueidentifier, @kind nvarchar(20), @reverses uniqueidentifier,
          @returned_on date, @request uniqueidentifier, @threshold decimal(15, 2), @m nvarchar(400),
          @offset_rate decimal(12, 6), @offset_currency nchar(3), @claim_currency nchar(3), @policy_currency nchar(3), @limit_rate decimal(12, 6);
  declare @o_id uniqueidentifier, @o_claim uniqueidentifier, @o_kind nvarchar(20), @o_paid_on date, @o_amount decimal(18, 2),
          @o_deductible decimal(18, 2), @o_offset decimal(18, 2), @o_net decimal(18, 2), @o_net_gel decimal(18, 2), @o_rate decimal(12, 6),
          @o_payee_kind nvarchar(20), @o_payee uniqueidentifier, @o_offset_payment uniqueidentifier,
          @o_offset_rate decimal(12, 6), @o_offset_currency nchar(3);
  declare @b_id uniqueidentifier, @b_kind nvarchar(20), @b_reverses uniqueidentifier, @b_received date, @b_gel decimal(18, 2);
  declare @case uniqueidentifier, @day date, @added decimal(38, 2), @room decimal(38, 2), @claimed decimal(38, 2), @confirmed decimal(38, 2);
  declare rows cursor local fast_forward for
    select id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
           offset_payment_id, kind, reverses_claim_payment_id, returned_on, request_id, offset_rate, offset_currency from inserted;
  open rows;
  fetch next from rows into @id, @claim, @paid_on, @amount, @deductible, @offset, @net, @net_gel, @rate, @payee_kind, @payee,
                            @offset_payment, @kind, @reverses, @returned_on, @request, @offset_rate, @offset_currency;
  while @@fetch_status = 0
  begin
    exec core.assert_period_open @paid_on;

    if @kind = N'payment'
    begin
      if @request is not null
      begin
        if not exists (select 1 from core.claim_payment_request r where r.id = @request and r.claim_id = @claim)
          throw 51000, N'[CQ001] a claim payment carries out a request of its own claim', 1;
        if not exists (select 1 from core.claim_payment_request r
                       where r.id = @request and @paid_on = r.paid_on and @amount = r.amount and @deductible = r.deductible_withheld
                         and @offset = r.premium_offset and @net = r.net_paid and @net_gel = r.net_paid_gel and @rate = r.rate
                         and @payee_kind = r.payee_kind and @payee = r.payee_party_id
                         and ((@offset_rate is null and r.offset_rate is null) or @offset_rate = r.offset_rate)
                         and ((@offset_currency is null and r.offset_currency is null) or @offset_currency = r.offset_currency))
          throw 51000, N'[CQ002] a claim payment takes its request''s quote over exactly', 1;
      end
      else
      begin
        exec core.fd_threshold_gel N'CQ004', @threshold output;
        if @net_gel > @threshold
        begin
          set @m = concat(N'[CQ003] a claim payment of ', @net_gel, N' GEL net is above the threshold of ', @threshold, N': it needs an approved request');
          exec core.fail @m;
        end
      end

      -- H46. A reversal keeps the payment's rates (CP002), so these are checked on the payment.
      select @claim_currency = null, @policy_currency = null, @limit_rate = null;
      select @claim_currency = c.currency, @policy_currency = p.premium_currency, @limit_rate = c.limit_rate
      from core.claim c join core.policy p on p.id = c.policy_id where c.id = @claim;
      if @claim_currency = N'GEL' and @rate <> 1
        throw 51000, N'[CP008] a claim in GEL is paid at rate 1', 1;
      if (@offset_currency is not null and @offset_currency <> @policy_currency)
         or (@limit_rate is not null and @offset <> 0 and (@offset_rate is null or @offset_currency is null))
        throw 51000, N'[CP009] a premium set-off is in the policy''s currency, at the rate it was converted with', 1;
    end

    if @kind = N'reversal'
    begin
      select @o_id = null, @o_claim = null, @o_kind = null, @o_offset_payment = null, @o_offset_rate = null, @o_offset_currency = null;
      select @o_id = id, @o_claim = claim_id, @o_kind = kind, @o_paid_on = paid_on, @o_amount = amount, @o_deductible = deductible_withheld,
             @o_offset = premium_offset, @o_net = net_paid, @o_net_gel = net_paid_gel, @o_rate = rate, @o_payee_kind = payee_kind,
             @o_payee = payee_party_id, @o_offset_payment = offset_payment_id, @o_offset_rate = offset_rate, @o_offset_currency = offset_currency
      from core.claim_payment where id = @reverses;
      if @o_id is null or @o_kind <> N'payment'
        throw 51000, N'[CP001] only a payment is reversed, and a reversal is not', 1;
      -- NULL-safe (0015): EXCEPT treats two NULLs as equal and a NULL and a value as different, so a reversal that drops (or
      -- adds) the payee, the set-off's rate or its currency is refused; "not (a = b or both null)" was UNKNOWN there.
      if exists (select @o_claim, @o_payee_kind, @o_payee, @o_rate, @o_offset_rate, @o_offset_currency
                 except
                 select @claim, @payee_kind, @payee, @rate, @offset_rate, @offset_currency)
        throw 51000, N'[CP002] a reversal keeps the claim, the payee and the rate of the payment it reverses', 1;
      if @amount <> -@o_amount or @deductible <> -@o_deductible or @offset <> -@o_offset or @net <> -@o_net or @net_gel <> -@o_net_gel
        throw 51000, N'[CP003] a reversal is the exact opposite of the payment it reverses', 1;
      if @paid_on < @o_paid_on
        throw 51000, N'[CP004] a reversal is not dated before the payment it reverses', 1;
      if @returned_on < @o_paid_on
        throw 51000, N'[CP007] money given back cannot come back before the payment it reverses', 1;
      select @b_id = null, @b_kind = null, @b_reverses = null, @b_received = null, @b_gel = null;
      if @o_offset = 0
      begin
        if @offset_payment is not null
          throw 51000, N'[CP005] a payment without a premium set-off gives no set-off back', 1;
      end
      else
      begin
        select @b_id = id, @b_kind = kind, @b_reverses = reverses_payment_id, @b_received = received_on, @b_gel = amount_gel
        from core.payment where id = @offset_payment;
        if @o_offset_payment is null or @b_id is null or @b_kind <> N'reversal'
           or @b_reverses <> @o_offset_payment or @b_received <> @paid_on
          throw 51000, N'[CP005] the set-off is given back by the reversal of the payment''s own set-off receipt, on the same day', 1;
      end

      -- Regress on the claim (0030): what each case claimed or confirmed may not exceed what was paid less the
      -- claim's other regress money, on any day from the reversal's date on; the reversal (not in the table
      -- yet) is added by hand. The lock is the one the regress guard takes.
      exec core.lock_subrogation_claim @claim;
      set @added = @net_gel + coalesce(@b_gel, 0);
      declare cases cursor local fast_forward for select id from core.subrogation_case where claim_id = @claim;
      open cases;
      fetch next from cases into @case;
      while @@fetch_status = 0
      begin
        declare days cursor local fast_forward for
          select d from (
            select @paid_on as d
            union select booked_on from core.subrogation_movement where case_id = @case and booked_on > @paid_on
            union select paid_on from core.claim_payment where claim_id = @claim and paid_on > @paid_on
            union select received_on from core.claim_recovery
                  where claim_id = @claim and kind = N'subrogation' and (subrogation_case_id is null or subrogation_case_id <> @case)
                    and received_on > @paid_on
          ) x order by d;
        open days;
        fetch next from days into @day;
        while @@fetch_status = 0
        begin
          select @claimed = claimed, @confirmed = confirmed from core.subrogation_totals(@case, @day);
          set @room = (select room from core.subrogation_room(@claim, @case, @day)) + @added;
          if @claimed > @room or @confirmed > @room
          begin
            set @m = concat(N'[CP006] the reversal would leave regress case ', lower(convert(nchar(36), @case)), N' above what was paid on ', format(@day, 'yyyy-MM-dd'));
            exec core.fail @m;
          end
          fetch next from days into @day;
        end
        close days;
        deallocate days;
        fetch next from cases into @case;
      end
      close cases;
      deallocate cases;
    end

    insert core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind,
                               payee_party_id, offset_payment_id, note, created_at, created_by, kind, reverses_claim_payment_id,
                               reversal_reason, returned_on, bank_reference, request_id, offset_rate, offset_currency)
    select id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind,
           payee_party_id, offset_payment_id, note, created_at, created_by, kind, reverses_claim_payment_id,
           reversal_reason, returned_on, bank_reference, request_id, offset_rate, offset_currency
    from inserted where id = @id;
    fetch next from rows into @id, @claim, @paid_on, @amount, @deductible, @offset, @net, @net_gel, @rate, @payee_kind, @payee,
                              @offset_payment, @kind, @reverses, @returned_on, @request, @offset_rate, @offset_currency;
  end
  close rows;
  deallocate rows;
end;
GO
