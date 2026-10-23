-- Contracts, policies, premium ledger, instalment schedule, receipts and their allocation, premium refund
-- and receipt reversal requests (legacy 0003, 0004, 0005, 0007, 0008, 0009, 0010, 0018, 0020, 0024, 0037, 0038).
--
-- A contract groups the policies issued under it. A change is issued as a new policy with its own number
-- and dates, so each policy fixes its own exchange rate. Money columns named *_gel are in GEL; the others
-- are in the policy's premium currency. Ledgers (premium_transaction, installment, installment_adjustment,
-- payment, payment_allocation, the request tables) are append-only for the application (0008_grants.sql).
--
-- Guards that PostgreSQL ran as BEFORE INSERT row triggers are INSTEAD OF INSERT triggers here: each row
-- is checked before it is written, in the legacy trigger order (alphabetical by trigger name), and only
-- then inserted — the same semantics as PostgreSQL, including for a multi-row insert.

create table core.contract (
  id              uniqueidentifier not null constraint contract_pkey primary key constraint contract_id_df default newid(),
  number          nvarchar(100) not null constraint contract_number_key unique,
  signed_on       date not null,
  policyholder_id uniqueidentifier not null constraint contract_policyholder_id_fkey references core.party (id),
  branch_id       uniqueidentifier null constraint contract_branch_id_fkey references core.branch (id),
  created_at      datetimeoffset(6) not null constraint contract_created_at_df default sysdatetimeoffset(),
  created_by      uniqueidentifier null constraint contract_created_by_fkey references core.app_user (id)
);
create index contract_policyholder_idx on core.contract (policyholder_id);

create table core.policy (
  id                 uniqueidentifier not null constraint policy_pkey primary key constraint policy_id_df default newid(),
  contract_id        uniqueidentifier not null constraint policy_contract_id_fkey references core.contract (id),
  number             nvarchar(100) not null constraint policy_number_key unique,
  issued_on          date not null,
  issue_place        nvarchar(max) null,
  -- Set when this policy was issued as a change to an earlier one.
  replaces_policy_id uniqueidentifier null constraint policy_replaces_policy_id_fkey references core.policy (id),
  effective_from     date not null,
  -- Cover ends at the start of this day: the term in days is effective_to - effective_from.
  effective_to       date not null,
  status             nvarchar(20) not null constraint policy_status_df default N'draft'
                     constraint policy_status_check check (status in (N'draft', N'pending_approval', N'active', N'cancelled')),
  cancelled_on       date null,
  cancel_reason      nvarchar(max) null,
  premium_currency   nchar(3) not null constraint policy_premium_currency_fkey references core.currency (code),
  -- Official rate on effective_from (1 for GEL); fixes premium and UPR in GEL (order №01/2013, art. 5).
  premium_rate       decimal(12, 6) not null constraint policy_premium_rate_check check (premium_rate > 0),
  created_at         datetimeoffset(6) not null constraint policy_created_at_df default sysdatetimeoffset(),
  created_by         uniqueidentifier null constraint policy_created_by_fkey references core.app_user (id),
  -- Grace period for automatic cancellation for non-payment; null means no automatic cancellation (0004).
  cancel_grace_days  int null constraint policy_cancel_grace_days_check check (cancel_grace_days >= 0),
  product_id         uniqueidentifier null constraint policy_product_id_fkey references core.product (id),
  -- The reporting segment frozen at issue (0007, review #11).
  segment            nvarchar(10) not null constraint policy_segment_check check (segment in (N'retail', N'corporate', N'state')),
  approved_at        datetimeoffset(6) null,
  approved_by        uniqueidentifier null constraint policy_approved_by_fkey references core.app_user (id),
  change_note        nvarchar(max) null,
  constraint policy_term check (effective_to > effective_from),
  constraint policy_cancel_within_term check (cancelled_on is null or (cancelled_on >= effective_from and cancelled_on <= effective_to)),
  constraint policy_cancel_status check ((status = N'cancelled' and cancelled_on is not null) or (status <> N'cancelled' and cancelled_on is null)),
  constraint policy_active_is_approved check (status <> N'active' or approved_at is not null)
);
create index policy_contract_idx on core.policy (contract_id);
create index policy_term_idx on core.policy (effective_from, effective_to);

create table core.insured_object (
  id          uniqueidentifier not null constraint insured_object_pkey primary key constraint insured_object_id_df default newid(),
  policy_id   uniqueidentifier not null constraint insured_object_policy_id_fkey references core.policy (id),
  object_type nvarchar(50) not null,
  description nvarchar(max) not null,
  -- Fields specific to the object type (vehicle, property, person...), defined by the product (JSON object).
  attributes  nvarchar(max) not null constraint insured_object_attributes_df default N'{}'
              constraint insured_object_attributes_json check (isjson(attributes) = 1)
);
create index insured_object_policy_idx on core.insured_object (policy_id);

-- Premium, sum insured and deductible are kept per insurance class, as reporting requires for combined
-- policies (order №11/2016, annex 2).
create table core.policy_line (
  id                   uniqueidentifier not null constraint policy_line_pkey primary key constraint policy_line_id_df default newid(),
  policy_id            uniqueidentifier not null constraint policy_line_policy_id_fkey references core.policy (id),
  insured_object_id    uniqueidentifier null constraint policy_line_insured_object_id_fkey references core.insured_object (id),
  class_code           nvarchar(20) not null constraint policy_line_class_code_fkey references core.insurance_class (code),
  sum_insured          decimal(18, 2) null,
  sum_insured_currency nchar(3) null constraint policy_line_sum_insured_currency_fkey references core.currency (code),
  deductible           decimal(18, 2) null,
  deductible_percent   decimal(5, 2) null constraint policy_line_deductible_percent_check check (deductible_percent between 0 and 100),
  -- Negative on a change that reduces cover.
  premium              decimal(18, 2) not null,
  premium_gel          decimal(18, 2) not null,
  is_fully_reinsured   bit not null constraint policy_line_is_fully_reinsured_df default 0,
  product_coverage_id  uniqueidentifier null constraint policy_line_product_coverage_id_fkey references core.product_coverage (id)
);
create index policy_line_policy_idx on core.policy_line (policy_id);
create index policy_line_class_idx on core.policy_line (class_code);

create table core.policy_party (
  policy_id uniqueidentifier not null constraint policy_party_policy_id_fkey references core.policy (id),
  party_id  uniqueidentifier not null constraint policy_party_party_id_fkey references core.party (id),
  role      nvarchar(20) not null constraint policy_party_role_check check (role in (N'insured', N'beneficiary', N'driver', N'representative')),
  constraint policy_party_pkey primary key (policy_id, party_id, role)
);
create index policy_party_party_idx on core.policy_party (party_id);

create table core.policy_intermediary (
  policy_id          uniqueidentifier not null constraint policy_intermediary_policy_id_fkey references core.policy (id),
  intermediary_id    uniqueidentifier not null constraint policy_intermediary_intermediary_id_fkey references core.intermediary (id),
  commission_percent decimal(5, 2) not null constraint policy_intermediary_commission_percent_check check (commission_percent between 0 and 100),
  constraint policy_intermediary_pkey primary key (policy_id, intermediary_id)
);

-- Ledger of written premium: the source of every premium figure. Rows are never edited; a cancellation
-- is a negative row covering the cancel date to the policy end, pointing at the row it reverses (0007 #1/#2).
create table core.premium_transaction (
  id                     uniqueidentifier not null constraint premium_transaction_pkey primary key constraint premium_transaction_id_df default newid(),
  policy_line_id         uniqueidentifier not null constraint premium_transaction_policy_line_id_fkey references core.policy_line (id),
  kind                   nvarchar(20) not null constraint premium_transaction_kind_check check (kind in (N'issue', N'cancellation', N'correction')),
  amount                 decimal(18, 2) not null,
  amount_gel             decimal(18, 2) not null,
  booked_on              date not null,
  cover_from             date not null,
  cover_to               date not null,
  created_at             datetimeoffset(6) not null constraint premium_transaction_created_at_df default sysdatetimeoffset(),
  created_by             uniqueidentifier null constraint premium_transaction_created_by_fkey references core.app_user (id),
  cancels_transaction_id uniqueidentifier null constraint premium_transaction_cancels_transaction_id_fkey references core.premium_transaction (id),
  constraint premium_transaction_cover check (cover_to > cover_from)
);
create index premium_transaction_line_idx on core.premium_transaction (policy_line_id);
create index premium_transaction_booked_idx on core.premium_transaction (booked_on);

create table core.installment (
  id          uniqueidentifier not null constraint installment_pkey primary key constraint installment_id_df default newid(),
  policy_id   uniqueidentifier not null constraint installment_policy_id_fkey references core.policy (id),
  due_on      date not null,
  amount      decimal(18, 2) not null,
  -- Who owes it, as the receivables form separates them (order №02/2013, form N1-4).
  debtor_kind nvarchar(10) not null constraint installment_debtor_kind_df default N'client'
              constraint installment_debtor_kind_check check (debtor_kind in (N'client', N'broker', N'agent'))
);
create index installment_policy_idx on core.installment (policy_id, due_on);

-- The schedule has history (0018, review H03): an instalment's original amount never changes; a
-- reduction by cancellation or an approved change is a dated adjustment.
create table core.installment_adjustment (
  id             uniqueidentifier not null constraint installment_adjustment_pkey primary key constraint installment_adjustment_id_df default newid(),
  installment_id uniqueidentifier not null constraint installment_adjustment_installment_id_fkey references core.installment (id),
  booked_on      date not null,
  change         decimal(18, 2) not null,
  reason         nvarchar(max) not null,
  created_at     datetimeoffset(6) not null constraint installment_adjustment_created_at_df default sysdatetimeoffset(),
  created_by     uniqueidentifier null constraint installment_adjustment_created_by_fkey references core.app_user (id)
);
create index installment_adjustment_installment_idx on core.installment_adjustment (installment_id, booked_on);

-- Money received (always GEL), its reversal and premium refunds (0003, 0004, 0010).
create table core.payment (
  id                  uniqueidentifier not null constraint payment_pkey primary key constraint payment_id_df default newid(),
  received_on         date not null,
  amount_gel          decimal(18, 2) not null,
  method              nvarchar(30) not null constraint payment_method_check
                      check (method in (N'bank', N'cash', N'online', N'claim_offset', N'commission_offset')),
  payer_party_id      uniqueidentifier null constraint payment_payer_party_id_fkey references core.party (id),
  reference           nvarchar(max) null,
  created_at          datetimeoffset(6) not null constraint payment_created_at_df default sysdatetimeoffset(),
  created_by          uniqueidentifier null constraint payment_created_by_fkey references core.app_user (id),
  kind                nvarchar(20) not null constraint payment_kind_df default N'receipt'
                      constraint payment_kind_check check (kind in (N'receipt', N'reversal', N'refund')),
  reverses_payment_id uniqueidentifier null constraint payment_reverses_payment_id_fkey references core.payment (id),
  refund_request_id   uniqueidentifier null,
  reversal_request_id uniqueidentifier null,
  constraint payment_amount_sign check ((kind = N'receipt' and amount_gel > 0) or (kind in (N'reversal', N'refund') and amount_gel < 0)),
  constraint payment_reversal_target check ((kind = N'reversal' and reverses_payment_id is not null) or (kind <> N'reversal' and reverses_payment_id is null))
);
create index payment_received_idx on core.payment (received_on);
create unique index payment_reversal_once on core.payment (reverses_payment_id) where reverses_payment_id is not null;
create unique index payment_refund_request_once on core.payment (refund_request_id) where refund_request_id is not null;
create unique index payment_reversal_request_once on core.payment (reversal_request_id) where reversal_request_id is not null;

-- Settles instalments at the official rate of the payment day. Reversals are negative rows.
create table core.payment_allocation (
  id             uniqueidentifier not null constraint payment_allocation_pkey primary key constraint payment_allocation_id_df default newid(),
  payment_id     uniqueidentifier not null constraint payment_allocation_payment_id_fkey references core.payment (id),
  installment_id uniqueidentifier not null constraint payment_allocation_installment_id_fkey references core.installment (id),
  amount         decimal(18, 2) not null,
  amount_gel     decimal(18, 2) not null,
  rate           decimal(12, 6) not null constraint payment_allocation_rate_check check (rate > 0),
  created_at     datetimeoffset(6) not null constraint payment_allocation_created_at_df default sysdatetimeoffset(),
  -- Legacy default current_date (the database server's day, UTC on the legacy host).
  booked_on      date not null constraint payment_allocation_booked_on_df default convert(date, sysutcdatetime()),
  created_by     uniqueidentifier null constraint payment_allocation_created_by_df default core.current_actor()
                 constraint payment_allocation_created_by_fkey references core.app_user (id)
);
create index payment_allocation_payment_idx on core.payment_allocation (payment_id);
create index payment_allocation_installment_idx on core.payment_allocation (installment_id);

-- A premium refund above the financial director's threshold needs an approved request (0037); the
-- refund then takes the request and its allocation plan over exactly.
create table core.premium_refund_request (
  id             uniqueidentifier not null constraint premium_refund_request_pkey primary key constraint premium_refund_request_id_df default newid(),
  policy_id      uniqueidentifier not null constraint premium_refund_request_policy_id_fkey references core.policy (id),
  refunded_on    date not null,
  amount_gel     decimal(18, 2) not null constraint premium_refund_request_amount_gel_check check (amount_gel > 0),
  currency       nchar(3) not null constraint premium_refund_request_currency_fkey references core.currency (code),
  rate           decimal(12, 6) not null constraint premium_refund_request_rate_check check (rate > 0),
  method         nvarchar(30) not null constraint premium_refund_request_method_check check (method in (N'bank', N'cash', N'online')),
  reference      nvarchar(max) null,
  payer_party_id uniqueidentifier not null constraint premium_refund_request_payer_party_id_fkey references core.party (id),
  created_at     datetimeoffset(6) not null constraint premium_refund_request_created_at_df default sysdatetimeoffset(),
  created_by     uniqueidentifier not null constraint premium_refund_request_created_by_df default core.current_actor()
                 constraint premium_refund_request_created_by_fkey references core.app_user (id)
);
create index premium_refund_request_policy_idx on core.premium_refund_request (policy_id);

create table core.premium_refund_request_line (
  id             uniqueidentifier not null constraint premium_refund_request_line_pkey primary key constraint premium_refund_request_line_id_df default newid(),
  request_id     uniqueidentifier not null constraint premium_refund_request_line_request_id_fkey references core.premium_refund_request (id),
  position       int not null constraint premium_refund_request_line_position_check check (position > 0),
  installment_id uniqueidentifier not null constraint premium_refund_request_line_installment_id_fkey references core.installment (id),
  amount         decimal(18, 2) not null constraint premium_refund_request_line_amount_check check (amount > 0),
  amount_gel     decimal(18, 2) not null constraint premium_refund_request_line_amount_gel_check check (amount_gel >= 0),
  constraint premium_refund_request_line_request_id_position_key unique (request_id, position)
);

-- A receipt's reversal above the threshold needs an approved request (0038), taken over exactly.
create table core.receipt_reversal_request (
  id             uniqueidentifier not null constraint receipt_reversal_request_pkey primary key constraint receipt_reversal_request_id_df default newid(),
  receipt_id     uniqueidentifier not null constraint receipt_reversal_request_receipt_id_fkey references core.payment (id),
  policy_id      uniqueidentifier not null constraint receipt_reversal_request_policy_id_fkey references core.policy (id),
  reversed_on    date not null,
  amount_gel     decimal(18, 2) not null constraint receipt_reversal_request_amount_gel_check check (amount_gel > 0),
  method         nvarchar(30) not null constraint receipt_reversal_request_method_check check (method in (N'bank', N'cash', N'online')),
  payer_party_id uniqueidentifier null constraint receipt_reversal_request_payer_party_id_fkey references core.party (id),
  reason         nvarchar(max) not null constraint receipt_reversal_request_reason_check check (ltrim(rtrim(reason)) <> N''),
  created_at     datetimeoffset(6) not null constraint receipt_reversal_request_created_at_df default sysdatetimeoffset(),
  created_by     uniqueidentifier not null constraint receipt_reversal_request_created_by_df default core.current_actor()
                 constraint receipt_reversal_request_created_by_fkey references core.app_user (id)
);
create index receipt_reversal_request_policy_idx on core.receipt_reversal_request (policy_id);
create index receipt_reversal_request_receipt_idx on core.receipt_reversal_request (receipt_id);

create table core.receipt_reversal_request_line (
  id             uniqueidentifier not null constraint receipt_reversal_request_line_pkey primary key constraint receipt_reversal_request_line_id_df default newid(),
  request_id     uniqueidentifier not null constraint receipt_reversal_request_line_request_id_fkey references core.receipt_reversal_request (id),
  position       int not null constraint receipt_reversal_request_line_position_check check (position > 0),
  installment_id uniqueidentifier not null constraint receipt_reversal_request_line_installment_id_fkey references core.installment (id),
  amount         decimal(18, 2) not null,
  amount_gel     decimal(18, 2) not null,
  rate           decimal(12, 6) not null constraint receipt_reversal_request_line_rate_check check (rate > 0),
  constraint receipt_reversal_request_line_request_id_position_key unique (request_id, position)
);

alter table core.payment add constraint payment_refund_request_id_fkey foreign key (refund_request_id) references core.premium_refund_request (id);
alter table core.payment add constraint payment_reversal_request_id_fkey foreign key (reversal_request_id) references core.receipt_reversal_request (id);
GO

-- ---------------------------------------------------------------- guards

-- premium_transaction_period (0008).
create trigger core.premium_transaction_guard on core.premium_transaction
instead of insert
as
begin
  set nocount on;
  declare @days core.date_list;
  insert @days select distinct booked_on from inserted;
  exec core.assert_periods_open @days;
  insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_at, created_by, cancels_transaction_id)
  select id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_at, created_by, cancels_transaction_id from inserted;
end;
GO

-- installment_adjustment_period (0018).
create trigger core.installment_adjustment_guard on core.installment_adjustment
instead of insert
as
begin
  set nocount on;
  declare @days core.date_list;
  insert @days select distinct booked_on from inserted;
  exec core.assert_periods_open @days;
  insert core.installment_adjustment (id, installment_id, booked_on, change, reason, created_at, created_by)
  select id, installment_id, booked_on, change, reason, created_at, created_by from inserted;
end;
GO

-- payment_period (0008), payment_refund_request_guard (0037), payment_reversal_request_guard (0038):
-- a refund or a receipt's reversal (other than a claim set-off's) above the financial director's
-- threshold needs an approved request, which it then takes over exactly.
create trigger core.payment_guard on core.payment
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @received_on date, @amount_gel decimal(18, 2), @method nvarchar(30), @payer uniqueidentifier,
          @reference nvarchar(max), @kind nvarchar(20), @reverses uniqueidentifier, @refund_request uniqueidentifier,
          @reversal_request uniqueidentifier, @threshold decimal(15, 2), @m nvarchar(400);
  declare rows cursor local fast_forward for
    select id, received_on, amount_gel, method, payer_party_id, reference, kind, reverses_payment_id, refund_request_id, reversal_request_id from inserted;
  open rows;
  fetch next from rows into @id, @received_on, @amount_gel, @method, @payer, @reference, @kind, @reverses, @refund_request, @reversal_request;
  while @@fetch_status = 0
  begin
    exec core.assert_period_open @received_on;

    if @kind = N'refund'
    begin
      if @refund_request is not null
      begin
        if not exists (select 1 from core.premium_refund_request r
                       where r.id = @refund_request and @amount_gel = -r.amount_gel and @received_on = r.refunded_on and @method = r.method
                         and ((@reference is null and r.reference is null) or @reference = r.reference)
                         and ((@payer is null and r.payer_party_id is null) or @payer = r.payer_party_id))
          throw 51000, N'[RF002] a premium refund takes its request over exactly', 1;
      end
      else
      begin
        exec core.fd_threshold_gel N'RF004', @threshold output;
        if -@amount_gel > @threshold
        begin
          set @m = concat(N'[RF003] a premium refund of ', -@amount_gel, N' GEL is above the threshold of ', @threshold, N': it needs an approved request');
          exec core.fail @m;
        end
      end
    end

    if @kind = N'reversal' and @method <> N'claim_offset'
    begin
      if @reversal_request is not null
      begin
        if not exists (select 1 from core.receipt_reversal_request r
                       where r.id = @reversal_request and @reverses = r.receipt_id and @amount_gel = -r.amount_gel
                         and @received_on = r.reversed_on and @method = r.method
                         and ((@payer is null and r.payer_party_id is null) or @payer = r.payer_party_id)
                         and @reference = r.reason)
          throw 51000, N'[RV002] a receipt''s reversal takes its request over exactly', 1;
      end
      else
      begin
        exec core.fd_threshold_gel N'RV004', @threshold output;
        if -@amount_gel > @threshold
        begin
          set @m = concat(N'[RV003] a receipt''s reversal of ', -@amount_gel, N' GEL is above the threshold of ', @threshold, N': it needs an approved request');
          exec core.fail @m;
        end
      end
    end

    insert core.payment (id, received_on, amount_gel, method, payer_party_id, reference, created_at, created_by, kind, reverses_payment_id, refund_request_id, reversal_request_id)
    select id, received_on, amount_gel, method, payer_party_id, reference, created_at, created_by, kind, reverses_payment_id, refund_request_id, reversal_request_id
    from inserted where id = @id;
    fetch next from rows into @id, @received_on, @amount_gel, @method, @payer, @reference, @kind, @reverses, @refund_request, @reversal_request;
  end
  close rows;
  deallocate rows;
end;
GO

-- payment_allocation_period (0008), _refund_plan (0037), _reversal_plan (0038), _total (0024):
-- allocations of one receipt never exceed it, a reversed receipt takes no new allocation, and an
-- allocation of a requested refund or reversal is a line of its plan. The per-receipt lock serialises
-- allocations of one receipt (legacy pg_advisory_xact_lock on core.payment_allocation).
create trigger core.payment_allocation_guard on core.payment_allocation
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @payment uniqueidentifier, @installment uniqueidentifier, @amount decimal(18, 2), @amount_gel decimal(18, 2),
          @rate decimal(12, 6), @booked_on date, @request uniqueidentifier, @receipt_kind nvarchar(20), @receipt_gel decimal(18, 2),
          @allocated decimal(38, 2), @resource nvarchar(255), @m nvarchar(400);
  declare rows cursor local fast_forward for
    select id, payment_id, installment_id, amount, amount_gel, rate, booked_on from inserted;
  open rows;
  fetch next from rows into @id, @payment, @installment, @amount, @amount_gel, @rate, @booked_on;
  while @@fetch_status = 0
  begin
    exec core.assert_period_open @booked_on;

    set @request = (select refund_request_id from core.payment where id = @payment);
    if @request is not null
       and not exists (select 1 from core.premium_refund_request_line l join core.premium_refund_request q on q.id = l.request_id
                       where l.request_id = @request and l.installment_id = @installment
                         and @amount = -l.amount and @amount_gel = -l.amount_gel and @rate = q.rate)
      throw 51000, N'[RF005] an allocation of a requested refund is a line of its plan', 1;

    set @request = (select reversal_request_id from core.payment where id = @payment);
    if @request is not null
       and not exists (select 1 from core.receipt_reversal_request_line l
                       where l.request_id = @request and l.installment_id = @installment
                         and @amount = -l.amount and @amount_gel = -l.amount_gel and @rate = l.rate)
      throw 51000, N'[RV005] an allocation of a requested reversal is a line of its plan', 1;

    set @resource = concat(N'core.payment_allocation:', lower(convert(nchar(36), @payment)));
    exec core.xact_lock @resource;
    select @receipt_kind = kind, @receipt_gel = amount_gel from core.payment where id = @payment;
    if @receipt_kind = N'receipt' and exists (select 1 from core.payment r where r.reverses_payment_id = @payment)
    begin
      set @m = concat(N'[PA001] payment ', lower(convert(nchar(36), @payment)), N' is reversed and takes no allocation');
      exec core.fail @m;
    end
    set @allocated = coalesce((select sum(amount_gel) from core.payment_allocation where payment_id = @payment), 0) + @amount_gel;
    if abs(@allocated) > abs(@receipt_gel) or sign(@allocated) * sign(@receipt_gel) < 0
    begin
      set @m = concat(N'[PA002] allocations of payment ', lower(convert(nchar(36), @payment)), N' would exceed it (', @allocated, N' of ', @receipt_gel, N')');
      exec core.fail @m;
    end

    insert core.payment_allocation (id, payment_id, installment_id, amount, amount_gel, rate, created_at, booked_on, created_by)
    select id, payment_id, installment_id, amount, amount_gel, rate, created_at, booked_on, created_by from inserted where id = @id;
    fetch next from rows into @id, @payment, @installment, @amount, @amount_gel, @rate, @booked_on;
  end
  close rows;
  deallocate rows;
end;
GO

-- premium_refund_request_period (0037).
create trigger core.premium_refund_request_guard on core.premium_refund_request
instead of insert
as
begin
  set nocount on;
  declare @days core.date_list;
  insert @days select distinct refunded_on from inserted;
  exec core.assert_periods_open @days;
  insert core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_at, created_by)
  select id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_at, created_by from inserted;
end;
GO

-- receipt_reversal_request_period (0038).
create trigger core.receipt_reversal_request_guard on core.receipt_reversal_request
instead of insert
as
begin
  set nocount on;
  declare @days core.date_list;
  insert @days select distinct reversed_on from inserted;
  exec core.assert_periods_open @days;
  insert core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_at, created_by)
  select id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_at, created_by from inserted;
end;
GO

-- Schedule against approved premium per policy (0020): a non-zero difference shows a schedule that does
-- not match the premium ledger. A change that reduces cover and has no schedule of its own reduces the
-- original policy's schedule, so its ledger amount counts there.
create view core.schedule_reconciliation
as
with schedule as (
  select i.policy_id, sum(i.amount) + coalesce(sum(j.change), 0) as scheduled
  from core.installment i
  left join (select installment_id, sum(change) as change from core.installment_adjustment group by installment_id) j
    on j.installment_id = i.id
  group by i.policy_id
), ledger as (
  select l.policy_id, sum(t.amount) as amount
  from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
  group by l.policy_id
), reductions as (
  select c.replaces_policy_id as policy_id, sum(lg.amount) as amount
  from core.policy c join ledger lg on lg.policy_id = c.id
  where c.replaces_policy_id is not null and lg.amount < 0
    and not exists (select 1 from core.installment i where i.policy_id = c.id)
  group by c.replaces_policy_id
)
select p.id as policy_id, p.number, p.status, s.scheduled,
       coalesce(lg.amount, 0) + coalesce(r.amount, 0) as approved_premium,
       s.scheduled - (coalesce(lg.amount, 0) + coalesce(r.amount, 0)) as difference
from schedule s
join core.policy p on p.id = s.policy_id
left join ledger lg on lg.policy_id = p.id
left join reductions r on r.policy_id = p.id
where p.status in (N'active', N'cancelled');
GO
