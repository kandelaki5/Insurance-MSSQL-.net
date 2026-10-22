-- Loss events, claims, reserves, claim payments and their reversal, recoveries, the financial director's
-- reserve/payment requests, complaints, and regress (subrogation) cases
-- (legacy 0011, 0013, 0028, 0030, 0033, 0034, 0035, 0036).

-- ---------------------------------------------------------------- loss events and claims (0011, 0028)

-- One event (accident, theft...) on one policy; a claim is one coverage line of it.
create table core.loss_event (
  id             uniqueidentifier not null constraint loss_event_pkey primary key constraint loss_event_id_df default newid(),
  number         nvarchar(100) not null constraint loss_event_number_key unique,
  policy_id      uniqueidentifier not null constraint loss_event_policy_id_fkey references core.policy (id),
  occurred_on    date not null,
  occurred_place nvarchar(max) null,
  reported_on    date not null,
  description    nvarchar(max) not null,
  request_key    uniqueidentifier null,
  source_ref     nvarchar(400) null,
  source_batch   nvarchar(400) null,
  created_at     datetimeoffset(6) not null constraint loss_event_created_at_df default sysdatetimeoffset(),
  created_by     uniqueidentifier null constraint loss_event_created_by_fkey references core.app_user (id),
  constraint loss_event_dates check (reported_on >= occurred_on)
);
create unique index loss_event_request_key_key on core.loss_event (request_key) where request_key is not null;
create index loss_event_policy_idx on core.loss_event (policy_id);

create table core.claim (
  id                uniqueidentifier not null constraint claim_pkey primary key constraint claim_id_df default newid(),
  number            nvarchar(100) not null constraint claim_number_key unique,
  policy_id         uniqueidentifier not null constraint claim_policy_id_fkey references core.policy (id),
  policy_line_id    uniqueidentifier not null constraint claim_policy_line_id_fkey references core.policy_line (id),
  currency          nchar(3) not null constraint claim_currency_fkey references core.currency (code),
  occurred_on       date not null,
  occurred_place    nvarchar(max) null,
  reported_on       date not null,
  description       nvarchar(max) not null,
  status            nvarchar(20) not null constraint claim_status_df default N'registered'
                    constraint claim_status_check check (status in (N'registered', N'in_review', N'approved', N'paid', N'rejected', N'closed')),
  claimed_amount    decimal(18, 2) null,
  approved_amount   decimal(18, 2) null,
  deductible_amount decimal(18, 2) not null constraint claim_deductible_amount_df default 0,
  is_total_loss     bit not null constraint claim_is_total_loss_df default 0,
  rejection_reason  nvarchar(max) null,
  closed_on         date null,
  created_at        datetimeoffset(6) not null constraint claim_created_at_df default sysdatetimeoffset(),
  created_by        uniqueidentifier null constraint claim_created_by_fkey references core.app_user (id),
  loss_event_id     uniqueidentifier not null constraint claim_loss_event_id_fkey references core.loss_event (id),
  constraint claim_dates check (reported_on >= occurred_on),
  constraint claim_rejection check ((status = N'rejected' and rejection_reason is not null) or (status <> N'rejected' and rejection_reason is null)),
  constraint claim_event_coverage unique (loss_event_id, policy_line_id)
);
create index claim_policy_idx on core.claim (policy_id);
create index claim_occurred_idx on core.claim (occurred_on);
create index claim_status_idx on core.claim (status);
create index claim_loss_event_idx on core.claim (loss_event_id);

-- A claim reserve change above the threshold is asked for and approved by someone else (0035).
create table core.claim_reserve_request (
  id         uniqueidentifier not null constraint claim_reserve_request_pkey primary key constraint claim_reserve_request_id_df default newid(),
  claim_id   uniqueidentifier not null constraint claim_reserve_request_claim_id_fkey references core.claim (id),
  booked_on  date not null,
  base_level decimal(18, 2) not null,
  new_level  decimal(18, 2) not null constraint claim_reserve_request_new_level_check check (new_level >= 0),
  change     decimal(18, 2) not null constraint claim_reserve_request_change_check check (change <> 0),
  reason     nvarchar(500) not null constraint claim_reserve_request_reason_check check (ltrim(rtrim(reason)) <> N'' and len(reason + N'.') - 1 <= 500),
  created_at datetimeoffset(6) not null constraint claim_reserve_request_created_at_df default sysdatetimeoffset(),
  created_by uniqueidentifier not null constraint claim_reserve_request_created_by_df default core.current_actor()
             constraint claim_reserve_request_created_by_fkey references core.app_user (id),
  constraint claim_reserve_request_difference check (change = new_level - base_level)
);
create index claim_reserve_request_claim_idx on core.claim_reserve_request (claim_id);

-- Reserve (RBNS) movements of a claim: the level is the sum of changes.
create table core.claim_reserve (
  id         uniqueidentifier not null constraint claim_reserve_pkey primary key constraint claim_reserve_id_df default newid(),
  claim_id   uniqueidentifier not null constraint claim_reserve_claim_id_fkey references core.claim (id),
  booked_on  date not null,
  change     decimal(18, 2) not null,
  reason     nvarchar(max) not null,
  created_at datetimeoffset(6) not null constraint claim_reserve_created_at_df default sysdatetimeoffset(),
  created_by uniqueidentifier null constraint claim_reserve_created_by_fkey references core.app_user (id),
  request_id uniqueidentifier null constraint claim_reserve_request_id_fkey references core.claim_reserve_request (id)
);
create index claim_reserve_claim_idx on core.claim_reserve (claim_id);
create index claim_reserve_booked_idx on core.claim_reserve (booked_on);
create unique index claim_reserve_request_once on core.claim_reserve (request_id) where request_id is not null;

-- A claim payment above the threshold is asked for and authorised by the financial director (0036).
create table core.claim_payment_request (
  id                  uniqueidentifier not null constraint claim_payment_request_pkey primary key constraint claim_payment_request_id_df default newid(),
  claim_id            uniqueidentifier not null constraint claim_payment_request_claim_id_fkey references core.claim (id),
  paid_on             date not null,
  amount              decimal(18, 2) not null constraint claim_payment_request_amount_check check (amount > 0),
  deductible_withheld decimal(18, 2) not null constraint claim_payment_request_deductible_withheld_check check (deductible_withheld >= 0),
  premium_offset      decimal(18, 2) not null constraint claim_payment_request_premium_offset_check check (premium_offset >= 0),
  net_paid            decimal(18, 2) not null constraint claim_payment_request_net_paid_check check (net_paid > 0),
  net_paid_gel        decimal(18, 2) not null constraint claim_payment_request_net_paid_gel_check check (net_paid_gel > 0),
  rate                decimal(12, 6) not null constraint claim_payment_request_rate_check check (rate > 0),
  payee_kind          nvarchar(20) not null constraint claim_payment_request_payee_kind_check check (payee_kind in (N'policyholder', N'beneficiary', N'workshop', N'other')),
  payee_party_id      uniqueidentifier not null constraint claim_payment_request_payee_party_id_fkey references core.party (id),
  offset_premium      bit not null,
  total_loss_mode     nvarchar(20) not null constraint claim_payment_request_total_loss_mode_check check (total_loss_mode in (N'none', N'earned_unpaid', N'all_unpaid')),
  note                nvarchar(max) null,
  created_at          datetimeoffset(6) not null constraint claim_payment_request_created_at_df default sysdatetimeoffset(),
  created_by          uniqueidentifier not null constraint claim_payment_request_created_by_df default core.current_actor()
                      constraint claim_payment_request_created_by_fkey references core.app_user (id),
  constraint claim_payment_request_net check (net_paid = amount - deductible_withheld - premium_offset)
);
create index claim_payment_request_claim_idx on core.claim_payment_request (claim_id);

-- Indemnity paid: gross, deductible withheld, unpaid premium set off, net to the payee (claim
-- currency and GEL at the payment day's rate). A reversal is a mirror row (0033, 0034).
create table core.claim_payment (
  id                        uniqueidentifier not null constraint claim_payment_pkey primary key constraint claim_payment_id_df default newid(),
  claim_id                  uniqueidentifier not null constraint claim_payment_claim_id_fkey references core.claim (id),
  paid_on                   date not null,
  amount                    decimal(18, 2) not null,
  deductible_withheld       decimal(18, 2) not null constraint claim_payment_deductible_withheld_df default 0,
  premium_offset            decimal(18, 2) not null constraint claim_payment_premium_offset_df default 0,
  net_paid                  decimal(18, 2) not null,
  net_paid_gel              decimal(18, 2) not null,
  rate                      decimal(12, 6) not null constraint claim_payment_rate_check check (rate > 0),
  payee_kind                nvarchar(20) not null constraint claim_payment_payee_kind_check check (payee_kind in (N'policyholder', N'beneficiary', N'workshop', N'other')),
  payee_party_id            uniqueidentifier null constraint claim_payment_payee_party_id_fkey references core.party (id),
  offset_payment_id         uniqueidentifier null constraint claim_payment_offset_payment_id_fkey references core.payment (id),
  note                      nvarchar(max) null,
  created_at                datetimeoffset(6) not null constraint claim_payment_created_at_df default sysdatetimeoffset(),
  created_by                uniqueidentifier null constraint claim_payment_created_by_fkey references core.app_user (id),
  kind                      nvarchar(20) not null constraint claim_payment_kind_df default N'payment'
                            constraint claim_payment_kind_check check (kind in (N'payment', N'reversal')),
  reverses_claim_payment_id uniqueidentifier null constraint claim_payment_reverses_claim_payment_id_fkey references core.claim_payment (id),
  reversal_reason           nvarchar(20) null constraint claim_payment_reversal_reason_check check (reversal_reason in (N'correction', N'returned')),
  returned_on               date null,
  bank_reference            nvarchar(100) null
                            constraint claim_payment_bank_reference_check check (bank_reference is null or (ltrim(rtrim(bank_reference)) <> N'' and len(bank_reference + N'.') - 1 <= 100)),
  request_id                uniqueidentifier null constraint claim_payment_request_id_fkey references core.claim_payment_request (id),
  constraint claim_payment_kind_shape check (
    (kind = N'payment' and amount > 0 and deductible_withheld >= 0 and premium_offset >= 0 and net_paid >= 0
      and reverses_claim_payment_id is null and reversal_reason is null and returned_on is null and bank_reference is null)
    or (kind = N'reversal' and amount < 0 and deductible_withheld <= 0 and premium_offset <= 0 and net_paid <= 0
      and reverses_claim_payment_id is not null and reversal_reason is not null
      and ((reversal_reason = N'returned' and returned_on is not null and bank_reference is not null)
        or (reversal_reason <> N'returned' and returned_on is null and bank_reference is null))))
);
create index claim_payment_claim_idx on core.claim_payment (claim_id);
create index claim_payment_paid_idx on core.claim_payment (paid_on);
create unique index claim_payment_request_once on core.claim_payment (request_id) where request_id is not null;
create unique index claim_payment_reversal_once on core.claim_payment (reverses_claim_payment_id) where reverses_claim_payment_id is not null;

-- ---------------------------------------------------------------- regress (0030)

-- Regress (subrogation) on one paid CASCO claim (docs/08): the company claims what it paid from the liable
-- person or their liability insurer. One case per claim of class 5.1 that is not theft; the amounts are an
-- append-only ledger of the case; money received stays in claim_recovery, linked to its case.
create table core.subrogation_case (
  id           uniqueidentifier not null constraint subrogation_case_pkey primary key constraint subrogation_case_id_df default newid(),
  number       nvarchar(100) not null constraint subrogation_case_number_key unique,
  claim_id     uniqueidentifier not null constraint subrogation_case_claim_id_key unique constraint subrogation_case_claim_id_fkey references core.claim (id),
  status       nvarchar(30) not null constraint subrogation_case_status_df default N'opened'
               constraint subrogation_case_status_check check (status in (N'opened', N'evidence_confirmed', N'demand_sent', N'accepted', N'disputed', N'rejected', N'partially_received', N'closed')),
  opened_on    date not null,
  -- The document that shows who was at fault: police report, court decision or admission.
  basis_kind   nvarchar(30) null constraint subrogation_case_basis_kind_check check (basis_kind in (N'police_report', N'court_decision', N'admission')),
  basis_number nvarchar(400) null,
  basis_date   date null,
  note         nvarchar(max) null,
  -- The one-off key of the form: a second submit finds this case instead of opening another.
  request_key  uniqueidentifier not null constraint subrogation_case_request_key_key unique,
  created_at   datetimeoffset(6) not null constraint subrogation_case_created_at_df default sysdatetimeoffset(),
  created_by   uniqueidentifier null constraint subrogation_case_created_by_df default core.current_actor()
               constraint subrogation_case_created_by_fkey references core.app_user (id),
  constraint subrogation_case_basis check (
    (basis_kind is null and basis_number is null and basis_date is null)
    or (basis_kind is not null and basis_number is not null and basis_date is not null))
);

-- The liable person and their liability insurer. Both reduce the case's one balance; the insurer's
-- limit is information (owner, O3).
create table core.subrogation_party (
  id             uniqueidentifier not null constraint subrogation_party_pkey primary key constraint subrogation_party_id_df default newid(),
  case_id        uniqueidentifier not null constraint subrogation_party_case_id_fkey references core.subrogation_case (id),
  party_id       uniqueidentifier not null constraint subrogation_party_party_id_fkey references core.party (id),
  role           nvarchar(30) not null constraint subrogation_party_role_check check (role in (N'liable_person', N'liability_insurer')),
  policy_number  nvarchar(200) null,
  coverage       nvarchar(max) null,
  limit_gel      decimal(18, 2) null constraint subrogation_party_limit_gel_check check (limit_gel > 0),
  response       nvarchar(20) null constraint subrogation_party_response_check check (response in (N'accepted', N'disputed', N'rejected')),
  response_on    date null,
  created_at     datetimeoffset(6) not null constraint subrogation_party_created_at_df default sysdatetimeoffset(),
  created_by     uniqueidentifier null constraint subrogation_party_created_by_df default core.current_actor()
                 constraint subrogation_party_created_by_fkey references core.app_user (id),
  constraint subrogation_party_case_id_party_id_role_key unique (case_id, party_id, role),
  constraint subrogation_party_insurer_details check (role = N'liability_insurer' or (policy_number is null and coverage is null and limit_gel is null)),
  constraint subrogation_party_response check ((response is null and response_on is null) or (response is not null and response_on is not null))
);
create index subrogation_party_case_idx on core.subrogation_party (case_id);

-- Money recovered on a claim (subrogation or salvage), GEL. A receipt is undone by a mirror row.
create table core.claim_recovery (
  id                         uniqueidentifier not null constraint claim_recovery_pkey primary key constraint claim_recovery_id_df default newid(),
  claim_id                   uniqueidentifier not null constraint claim_recovery_claim_id_fkey references core.claim (id),
  kind                       nvarchar(20) not null constraint claim_recovery_kind_check check (kind in (N'subrogation', N'salvage')),
  received_on                date not null,
  amount_gel                 decimal(18, 2) not null,
  note                       nvarchar(max) null,
  created_at                 datetimeoffset(6) not null constraint claim_recovery_created_at_df default sysdatetimeoffset(),
  created_by                 uniqueidentifier null constraint claim_recovery_created_by_fkey references core.app_user (id),
  subrogation_case_id        uniqueidentifier null constraint claim_recovery_subrogation_case_id_fkey references core.subrogation_case (id),
  row_kind                   nvarchar(20) not null constraint claim_recovery_row_kind_df default N'receipt'
                             constraint claim_recovery_row_kind_check check (row_kind in (N'receipt', N'reversal')),
  reverses_claim_recovery_id uniqueidentifier null constraint claim_recovery_reverses_claim_recovery_id_fkey references core.claim_recovery (id),
  bank_reference             nvarchar(100) null,
  constraint claim_recovery_amount_sign check ((row_kind = N'receipt' and amount_gel > 0) or (row_kind = N'reversal' and amount_gel < 0)),
  constraint claim_recovery_case_kind check (subrogation_case_id is null or kind = N'subrogation'),
  constraint claim_recovery_reversal_target check ((row_kind = N'reversal' and reverses_claim_recovery_id is not null) or (row_kind <> N'reversal' and reverses_claim_recovery_id is null))
);
create index claim_recovery_claim_idx on core.claim_recovery (claim_id);
create index claim_recovery_case_idx on core.claim_recovery (subrogation_case_id);
create unique index claim_recovery_reversal_once on core.claim_recovery (reverses_claim_recovery_id) where reverses_claim_recovery_id is not null;
create unique index claim_recovery_case_reference on core.claim_recovery (subrogation_case_id, bank_reference)
  where subrogation_case_id is not null and bank_reference is not null;

-- The ledger of a case: claimed, confirmed, received, cancelled by a court, and the reversal of one of them.
create table core.subrogation_movement (
  id                   uniqueidentifier not null constraint subrogation_movement_pkey primary key constraint subrogation_movement_id_df default newid(),
  case_id              uniqueidentifier not null constraint subrogation_movement_case_id_fkey references core.subrogation_case (id),
  kind                 nvarchar(20) not null constraint subrogation_movement_kind_check check (kind in (N'claimed', N'confirmed', N'received', N'cancelled', N'reversal')),
  booked_on            date not null,
  amount_gel           decimal(18, 2) not null,
  reverses_movement_id uniqueidentifier null constraint subrogation_movement_reverses_movement_id_fkey references core.subrogation_movement (id),
  claim_recovery_id    uniqueidentifier null constraint subrogation_movement_claim_recovery_id_fkey references core.claim_recovery (id),
  payer_id             uniqueidentifier null constraint subrogation_movement_payer_id_fkey references core.subrogation_party (id),
  document_kind        nvarchar(30) not null constraint subrogation_movement_document_kind_check
                       check (document_kind in (N'demand_letter', N'debtor_acceptance', N'court_decision', N'bank_statement', N'correction')),
  document_number      nvarchar(400) null,
  document_date        date null,
  note                 nvarchar(max) null,
  request_key          uniqueidentifier not null constraint subrogation_movement_request_key_key unique,
  created_at           datetimeoffset(6) not null constraint subrogation_movement_created_at_df default sysdatetimeoffset(),
  created_by           uniqueidentifier null constraint subrogation_movement_created_by_df default core.current_actor()
                       constraint subrogation_movement_created_by_fkey references core.app_user (id),
  constraint subrogation_movement_court check (document_kind <> N'court_decision' or (document_number is not null and document_date is not null)),
  constraint subrogation_movement_document check (
    (kind = N'claimed' and document_kind = N'demand_letter')
    or (kind = N'confirmed' and document_kind in (N'debtor_acceptance', N'court_decision'))
    or (kind = N'received' and document_kind = N'bank_statement')
    or (kind = N'reversal' and document_kind = N'correction' and note is not null)
    or (kind = N'cancelled' and document_kind = N'court_decision')),
  constraint subrogation_movement_money check (
    (kind = N'received' and claim_recovery_id is not null and payer_id is not null)
    or (kind in (N'claimed', N'confirmed', N'cancelled') and claim_recovery_id is null and payer_id is null)
    or (kind = N'reversal' and payer_id is null)),
  constraint subrogation_movement_sign check ((kind = N'reversal' and amount_gel < 0) or (kind <> N'reversal' and amount_gel > 0)),
  constraint subrogation_movement_target check ((kind = N'reversal' and reverses_movement_id is not null) or (kind <> N'reversal' and reverses_movement_id is null))
);
create index subrogation_movement_case_idx on core.subrogation_movement (case_id);
create index subrogation_movement_booked_idx on core.subrogation_movement (booked_on);
create unique index subrogation_movement_reversal_once on core.subrogation_movement (reverses_movement_id) where reverses_movement_id is not null;
create unique index subrogation_movement_recovery_once on core.subrogation_movement (claim_recovery_id) where claim_recovery_id is not null;

-- ---------------------------------------------------------------- complaints (0013)

create table core.complaint (
  id               uniqueidentifier not null constraint complaint_pkey primary key constraint complaint_id_df default newid(),
  number           nvarchar(100) not null constraint complaint_number_key unique,
  received_on      date not null,
  channel          nvarchar(20) not null constraint complaint_channel_check check (channel in (N'written', N'electronic', N'oral')),
  party_id         uniqueidentifier null constraint complaint_party_id_fkey references core.party (id),
  customer_name    nvarchar(400) not null,
  customer_contact nvarchar(max) null,
  policy_id        uniqueidentifier null constraint complaint_policy_id_fkey references core.policy (id),
  claim_id         uniqueidentifier null constraint complaint_claim_id_fkey references core.claim (id),
  category         smallint not null constraint complaint_category_check check (category between 1 and 4),
  description      nvarchar(max) not null,
  status           nvarchar(20) not null constraint complaint_status_df default N'open'
                   constraint complaint_status_check check (status in (N'open', N'upheld', N'partly_upheld', N'rejected', N'upheld_other')),
  acknowledged_at  datetimeoffset(6) null,
  responded_on     date null,
  response_text    nvarchar(max) null,
  measures         nvarchar(max) null,
  outcome          nvarchar(max) null,
  note             nvarchar(max) null,
  created_at       datetimeoffset(6) not null constraint complaint_created_at_df default sysdatetimeoffset(),
  created_by       uniqueidentifier null constraint complaint_created_by_fkey references core.app_user (id),
  constraint complaint_response check (status = N'open' or (responded_on is not null and response_text is not null))
);
create index complaint_policy_idx on core.complaint (policy_id);
create index complaint_received_idx on core.complaint (received_on);
create index complaint_status_idx on core.complaint (status);
GO

-- ---------------------------------------------------------------- regress functions (0030)

-- One lock per claim for every regress check, so two operations on a claim cannot cross.
create procedure core.lock_subrogation_claim @claim uniqueidentifier
as
begin
  set nocount on;
  declare @resource nvarchar(255) = concat(N'core.subrogation_claim:', lower(convert(nchar(36), @claim)));
  exec core.xact_lock @resource;
end;
GO

-- A case's totals by kind on a day (or ever, when @as_of is null); a reversal counts under the kind it reverses.
create function core.subrogation_totals(@case uniqueidentifier, @as_of date)
returns table
as
return
  select coalesce(sum(case when coalesce(o.kind, m.kind) = N'claimed' then m.amount_gel end), 0) as claimed,
         coalesce(sum(case when coalesce(o.kind, m.kind) = N'confirmed' then m.amount_gel end), 0) as confirmed,
         coalesce(sum(case when coalesce(o.kind, m.kind) = N'received' then m.amount_gel end), 0) as received,
         coalesce(sum(case when coalesce(o.kind, m.kind) = N'cancelled' then m.amount_gel end), 0) as cancelled
  from core.subrogation_movement m
  left join core.subrogation_movement o on o.id = m.reverses_movement_id
  where m.case_id = @case and (@as_of is null or m.booked_on <= @as_of);
GO

-- What was reimbursed on a claim by a day, in the GEL actually paid (net in GEL plus the premium set off
-- in GEL), less the claim's regress money outside one case (0030, H81).
create function core.subrogation_room(@claim uniqueidentifier, @case uniqueidentifier, @day date)
returns table
as
return
  select coalesce((select sum(cp.net_paid_gel + coalesce(p.amount_gel, 0))
                   from core.claim_payment cp left join core.payment p on p.id = cp.offset_payment_id
                   where cp.claim_id = @claim and cp.paid_on <= @day), 0)
       - coalesce((select sum(r.amount_gel) from core.claim_recovery r
                   where r.claim_id = @claim and r.kind = N'subrogation'
                     and (r.subrogation_case_id is null or r.subrogation_case_id <> @case) and r.received_on <= @day), 0) as room;
GO

-- ---------------------------------------------------------------- claim guards

-- claim_event_policy (0028): a claim is on the policy of its loss event.
create trigger core.claim_event_policy on core.claim
after insert, update
as
begin
  set nocount on;
  if not (update(loss_event_id) or update(policy_id)) return;
  declare @bad uniqueidentifier = (select top (1) i.id from inserted i left join core.loss_event e on e.id = i.loss_event_id
                                   where e.policy_id is null or e.policy_id <> i.policy_id);
  if @bad is not null
  begin
    declare @m nvarchar(300) = concat(N'[LE001] claim ', lower(convert(nchar(36), @bad)), N' is not on the policy of its loss event');
    exec core.fail @m;
  end
end;
GO

-- claim_reserve_period (0011), claim_reserve_request_guard (0035): a reserve movement that carries out a
-- request takes its day and difference over exactly, is approved by someone else, and the reserve has not
-- moved since the request.
create trigger core.claim_reserve_guard on core.claim_reserve
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @claim uniqueidentifier, @booked_on date, @change decimal(18, 2), @created_by uniqueidentifier,
          @request uniqueidentifier, @level decimal(38, 2), @m nvarchar(400);
  declare @r_claim uniqueidentifier, @r_change decimal(18, 2), @r_booked date, @r_by uniqueidentifier, @r_base decimal(18, 2);
  declare rows cursor local fast_forward for select id, claim_id, booked_on, change, created_by, request_id from inserted;
  open rows;
  fetch next from rows into @id, @claim, @booked_on, @change, @created_by, @request;
  while @@fetch_status = 0
  begin
    exec core.assert_period_open @booked_on;
    if @request is not null
    begin
      select @r_claim = null, @r_change = null, @r_booked = null, @r_by = null, @r_base = null;
      select @r_claim = claim_id, @r_change = change, @r_booked = booked_on, @r_by = created_by, @r_base = base_level
      from core.claim_reserve_request where id = @request;
      if @r_claim is null or @r_claim <> @claim
        throw 51000, N'[RR001] a reserve movement carries out a request of its own claim', 1;
      if @change <> @r_change or @booked_on <> @r_booked
        throw 51000, N'[RR002] a reserve movement takes the request''s day and difference over exactly', 1;
      if @created_by is null or @created_by = @r_by
        throw 51000, N'[RR003] a reserve change is approved by someone other than who asked for it', 1;
      set @level = coalesce((select sum(change) from core.claim_reserve where claim_id = @claim), 0);
      if @level <> @r_base
      begin
        set @m = concat(N'[RR004] the reserve moved since the request (now ', @level, N', asked from ', @r_base, N'): a new request is needed');
        exec core.fail @m;
      end
    end
    insert core.claim_reserve (id, claim_id, booked_on, change, reason, created_at, created_by, request_id)
    select id, claim_id, booked_on, change, reason, created_at, created_by, request_id from inserted where id = @id;
    fetch next from rows into @id, @claim, @booked_on, @change, @created_by, @request;
  end
  close rows;
  deallocate rows;
end;
GO

-- claim_payment_request_period (0036).
create trigger core.claim_payment_request_guard on core.claim_payment_request
instead of insert
as
begin
  set nocount on;
  declare @days core.date_list;
  insert @days select distinct paid_on from inserted;
  exec core.assert_periods_open @days;
  insert core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
                                     payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_at, created_by)
  select id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate,
         payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_at, created_by from inserted;
end;
GO

-- claim_payment_period (0011), claim_payment_request_guard (0036), claim_payment_reversal_guard (0033, 0034).
-- A payment above the threshold carries out an approved request exactly; a reversal is the exact mirror of
-- one payment, dated on or after it, gives back the payment's own premium set-off, and may not leave a
-- regress case above what was paid on any day from its date on.
create trigger core.claim_payment_guard on core.claim_payment
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @claim uniqueidentifier, @paid_on date, @amount decimal(18, 2), @deductible decimal(18, 2),
          @offset decimal(18, 2), @net decimal(18, 2), @net_gel decimal(18, 2), @rate decimal(12, 6), @payee_kind nvarchar(20),
          @payee uniqueidentifier, @offset_payment uniqueidentifier, @kind nvarchar(20), @reverses uniqueidentifier,
          @returned_on date, @request uniqueidentifier, @threshold decimal(15, 2), @m nvarchar(400);
  declare @o_id uniqueidentifier, @o_claim uniqueidentifier, @o_kind nvarchar(20), @o_paid_on date, @o_amount decimal(18, 2),
          @o_deductible decimal(18, 2), @o_offset decimal(18, 2), @o_net decimal(18, 2), @o_net_gel decimal(18, 2), @o_rate decimal(12, 6),
          @o_payee_kind nvarchar(20), @o_payee uniqueidentifier, @o_offset_payment uniqueidentifier;
  declare @b_id uniqueidentifier, @b_kind nvarchar(20), @b_reverses uniqueidentifier, @b_received date, @b_gel decimal(18, 2);
  declare @case uniqueidentifier, @day date, @added decimal(38, 2), @room decimal(38, 2), @claimed decimal(38, 2), @confirmed decimal(38, 2);
  declare rows cursor local fast_forward for
    select id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
           offset_payment_id, kind, reverses_claim_payment_id, returned_on, request_id from inserted;
  open rows;
  fetch next from rows into @id, @claim, @paid_on, @amount, @deductible, @offset, @net, @net_gel, @rate, @payee_kind, @payee,
                            @offset_payment, @kind, @reverses, @returned_on, @request;
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
                         and @payee_kind = r.payee_kind and @payee = r.payee_party_id)
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
    end

    if @kind = N'reversal'
    begin
      select @o_id = null, @o_claim = null, @o_kind = null, @o_offset_payment = null;
      select @o_id = id, @o_claim = claim_id, @o_kind = kind, @o_paid_on = paid_on, @o_amount = amount, @o_deductible = deductible_withheld,
             @o_offset = premium_offset, @o_net = net_paid, @o_net_gel = net_paid_gel, @o_rate = rate, @o_payee_kind = payee_kind,
             @o_payee = payee_party_id, @o_offset_payment = offset_payment_id
      from core.claim_payment where id = @reverses;
      if @o_id is null or @o_kind <> N'payment'
        throw 51000, N'[CP001] only a payment is reversed, and a reversal is not', 1;
      if @o_claim <> @claim or @o_payee_kind <> @payee_kind or not ((@o_payee is null and @payee is null) or @o_payee = @payee) or @o_rate <> @rate
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
                               reversal_reason, returned_on, bank_reference, request_id)
    select id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind,
           payee_party_id, offset_payment_id, note, created_at, created_by, kind, reverses_claim_payment_id,
           reversal_reason, returned_on, bank_reference, request_id
    from inserted where id = @id;
    fetch next from rows into @id, @claim, @paid_on, @amount, @deductible, @offset, @net, @net_gel, @rate, @payee_kind, @payee,
                              @offset_payment, @kind, @reverses, @returned_on, @request;
  end
  close rows;
  deallocate rows;
end;
GO

-- claim_recovery_case_guard (0030), claim_recovery_period (0011), and the legacy deferred constraint
-- trigger claim_recovery_case_movement (SG034): a regress recovery goes to a case of its own claim; once a
-- claim has a case its regress money goes through the case; a reversal mirrors one receipt of the same
-- claim and case on or after its day; and money of a case comes with its movement.
--
-- SQL Server has no deferred constraints, so the last rule is kept by core.record_case_money, which writes
-- the recovery and its movement in one call: a recovery with a case is accepted only while that procedure
-- names it in SESSION_CONTEXT(N'core.case_recovery'). See database/README.md.
create trigger core.claim_recovery_guard on core.claim_recovery
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @claim uniqueidentifier, @kind nvarchar(20), @received_on date, @amount_gel decimal(18, 2),
          @case uniqueidentifier, @row_kind nvarchar(20), @reverses uniqueidentifier, @case_claim uniqueidentifier, @m nvarchar(400);
  declare @o_id uniqueidentifier, @o_row_kind nvarchar(20), @o_claim uniqueidentifier, @o_kind nvarchar(20), @o_case uniqueidentifier,
          @o_gel decimal(18, 2), @o_received date;
  declare rows cursor local fast_forward for
    select id, claim_id, kind, received_on, amount_gel, subrogation_case_id, row_kind, reverses_claim_recovery_id from inserted;
  open rows;
  fetch next from rows into @id, @claim, @kind, @received_on, @amount_gel, @case, @row_kind, @reverses;
  while @@fetch_status = 0
  begin
    if @kind = N'subrogation'
      exec core.lock_subrogation_claim @claim;
    if @case is not null
    begin
      set @case_claim = (select claim_id from core.subrogation_case where id = @case);
      if @case_claim is null or @case_claim <> @claim
      begin
        set @m = concat(N'[SG031] recovery on claim ', lower(convert(nchar(36), @claim)), N' cannot go to regress case ',
                        lower(convert(nchar(36), @case)), N' of another claim');
        exec core.fail @m;
      end
    end
    else if @kind = N'subrogation' and @row_kind = N'receipt' and exists (select 1 from core.subrogation_case where claim_id = @claim)
    begin
      set @m = concat(N'[SG032] claim ', lower(convert(nchar(36), @claim)), N' has a regress case: its regress money goes through the case');
      exec core.fail @m;
    end
    if @row_kind = N'reversal'
    begin
      select @o_id = null;
      select @o_id = id, @o_row_kind = row_kind, @o_claim = claim_id, @o_kind = kind, @o_case = subrogation_case_id,
             @o_gel = amount_gel, @o_received = received_on
      from core.claim_recovery where id = @reverses;
      if @o_id is null or @o_row_kind <> N'receipt' or @o_claim <> @claim or @o_kind <> @kind
         or not ((@o_case is null and @case is null) or @o_case = @case)
         or @amount_gel <> -@o_gel or @received_on < @o_received
        throw 51000, N'[SG033] recovery reversal must mirror one receipt of the same claim and case, on or after its day', 1;
    end
    exec core.assert_period_open @received_on;
    if @case is not null
       and (try_convert(uniqueidentifier, try_convert(nvarchar(36), session_context(N'core.case_recovery'))) is null
            or try_convert(uniqueidentifier, try_convert(nvarchar(36), session_context(N'core.case_recovery'))) <> @id)
    begin
      set @m = concat(N'[SG034] recovery ', lower(convert(nchar(36), @id)), N' of regress case ', lower(convert(nchar(36), @case)), N' has no movement');
      exec core.fail @m;
    end
    insert core.claim_recovery (id, claim_id, kind, received_on, amount_gel, note, created_at, created_by, subrogation_case_id, row_kind,
                                reverses_claim_recovery_id, bank_reference)
    select id, claim_id, kind, received_on, amount_gel, note, created_at, created_by, subrogation_case_id, row_kind,
           reverses_claim_recovery_id, bank_reference
    from inserted where id = @id;
    fetch next from rows into @id, @claim, @kind, @received_on, @amount_gel, @case, @row_kind, @reverses;
  end
  close rows;
  deallocate rows;
end;
GO

-- ---------------------------------------------------------------- regress guards (0030)

-- subrogation_case_guard, insert: a case is opened on a claim of class 5.1 with a known coverage that is not theft.
create trigger core.subrogation_case_insert on core.subrogation_case
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @claim uniqueidentifier, @status nvarchar(30), @class nvarchar(20), @coverage nvarchar(50), @m nvarchar(300);
  declare rows cursor local fast_forward for select id, claim_id, status from inserted;
  open rows;
  fetch next from rows into @id, @claim, @status;
  while @@fetch_status = 0
  begin
    exec core.lock_subrogation_claim @claim;
    select @class = null, @coverage = null;
    select @class = l.class_code, @coverage = pc.code
    from core.claim c join core.policy_line l on l.id = c.policy_line_id
    left join core.product_coverage pc on pc.id = l.product_coverage_id
    where c.id = @claim;
    if @class is null or @class <> N'5.1'
    begin
      set @m = concat(N'[SG001] claim ', lower(convert(nchar(36), @claim)), N' is not of class 5.1: no regress');
      exec core.fail @m;
    end
    if @coverage is null
    begin
      set @m = concat(N'[SG002] claim ', lower(convert(nchar(36), @claim)), N' has no known coverage: no regress');
      exec core.fail @m;
    end
    if @coverage = N'theft'
    begin
      set @m = concat(N'[SG003] claim ', lower(convert(nchar(36), @claim)), N' is a theft, not an accident: no regress');
      exec core.fail @m;
    end
    if @status <> N'opened'
      throw 51000, N'[SG005] a regress case starts opened', 1;
    insert core.subrogation_case (id, number, claim_id, status, opened_on, basis_kind, basis_number, basis_date, note, request_key, created_at, created_by)
    select id, number, claim_id, status, opened_on, basis_kind, basis_number, basis_date, note, request_key, created_at, created_by
    from inserted where id = @id;
    fetch next from rows into @id, @claim, @status;
  end
  close rows;
  deallocate rows;
end;
GO

-- subrogation_case_guard, update: a case keeps its claim, number and origin; it closes only when something
-- was confirmed and nothing is owed.
create trigger core.subrogation_case_update on core.subrogation_case
after update
as
begin
  set nocount on;
  declare @m nvarchar(300), @id uniqueidentifier, @claim uniqueidentifier, @owed decimal(38, 2), @confirmed decimal(38, 2);
  set @id = (select top (1) o.id from deleted o join inserted n on n.id = o.id
             where n.claim_id <> o.claim_id or n.number <> o.number or n.request_key <> o.request_key or n.opened_on <> o.opened_on
                or not ((n.created_by is null and o.created_by is null) or n.created_by = o.created_by) or n.created_at <> o.created_at);
  if @id is not null or exists (select 1 from inserted n where not exists (select 1 from deleted o where o.id = n.id))
  begin
    set @m = concat(N'[SG005] regress case ', lower(convert(nchar(36), coalesce(@id, (select top (1) id from deleted)))), N' keeps its claim, number and origin');
    exec core.fail @m;
  end
  declare closing cursor local fast_forward for
    select n.id, n.claim_id from inserted n join deleted o on o.id = n.id where n.status = N'closed' and o.status <> N'closed';
  open closing;
  fetch next from closing into @id, @claim;
  while @@fetch_status = 0
  begin
    exec core.lock_subrogation_claim @claim;
    select @confirmed = confirmed, @owed = confirmed - received - cancelled from core.subrogation_totals(@id, null);
    if not (@confirmed > 0 and @owed = 0)
    begin
      set @m = concat(N'[SG006] regress case ', lower(convert(nchar(36), @id)), N' still has ', @owed, N' owed, or nothing confirmed');
      exec core.fail @m;
    end
    fetch next from closing into @id, @claim;
  end
  close closing;
  deallocate closing;
end;
GO

-- subrogation_party_guard: a regress party keeps its case, person and role.
create trigger core.subrogation_party_guard on core.subrogation_party
after update
as
begin
  set nocount on;
  declare @id uniqueidentifier = (select top (1) o.id from deleted o left join inserted n on n.id = o.id
                                  where n.id is null or n.case_id <> o.case_id or n.party_id <> o.party_id or n.role <> o.role);
  if @id is not null
  begin
    declare @m nvarchar(300) = concat(N'[SG007] regress party ', lower(convert(nchar(36), @id)), N' keeps its case, person and role');
    exec core.fail @m;
  end
end;
GO

-- subrogation_movement_guard: a reversal undoes one movement of its own case once, exactly, on or after it,
-- with the money's own reversal for a receipt; a receipt goes with its money row and a payer of the case; on
-- every day from the movement's on, the case never owes less than nothing and claimed/confirmed never exceed
-- what was paid less the claim's other regress money. The new row is not in the table yet, so it is added
-- to each day's totals by hand.
create trigger core.subrogation_movement_guard on core.subrogation_movement
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @case uniqueidentifier, @kind nvarchar(20), @booked_on date, @amount_gel decimal(18, 2),
          @reverses uniqueidentifier, @recovery uniqueidentifier, @payer uniqueidentifier, @case_claim uniqueidentifier,
          @effective nvarchar(20), @m nvarchar(400), @day date, @room decimal(38, 2),
          @claimed decimal(38, 2), @confirmed decimal(38, 2), @received decimal(38, 2), @cancelled decimal(38, 2);
  declare @t_id uniqueidentifier, @t_case uniqueidentifier, @t_kind nvarchar(20), @t_gel decimal(18, 2), @t_booked date, @t_recovery uniqueidentifier;
  declare @r_id uniqueidentifier, @r_reverses uniqueidentifier, @r_received date, @r_case uniqueidentifier, @r_row_kind nvarchar(20), @r_gel decimal(18, 2);
  declare rows cursor local fast_forward for
    select id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id from inserted;
  open rows;
  fetch next from rows into @id, @case, @kind, @booked_on, @amount_gel, @reverses, @recovery, @payer;
  while @@fetch_status = 0
  begin
    exec core.assert_period_open @booked_on;
    set @case_claim = (select claim_id from core.subrogation_case where id = @case);
    exec core.lock_subrogation_claim @case_claim;
    set @effective = @kind;

    if @kind = N'reversal'
    begin
      select @t_id = null;
      select @t_id = id, @t_case = case_id, @t_kind = kind, @t_gel = amount_gel, @t_booked = booked_on, @t_recovery = claim_recovery_id
      from core.subrogation_movement where id = @reverses;
      if @t_id is null or @t_case <> @case or @t_kind = N'reversal'
        throw 51000, N'[SG010] a reversal undoes one movement of its own case', 1;
      if exists (select 1 from core.subrogation_movement where reverses_movement_id = @t_id)
      begin
        set @m = concat(N'[SG011] movement ', lower(convert(nchar(36), @t_id)), N' is reversed already');
        exec core.fail @m;
      end
      if @amount_gel <> -@t_gel
        throw 51000, N'[SG012] a reversal is exactly minus its original', 1;
      if @booked_on < @t_booked
        throw 51000, N'[SG013] a reversal is dated on or after its original', 1;
      set @effective = @t_kind;
      if @t_kind = N'received'
      begin
        select @r_id = null;
        select @r_id = id, @r_reverses = reverses_claim_recovery_id, @r_received = received_on from core.claim_recovery where id = @recovery;
        if @r_id is null or not ((@r_reverses is null and @t_recovery is null) or @r_reverses = @t_recovery) or @r_received <> @booked_on
          throw 51000, N'[SG014] the reversal of a receipt goes with the reversal of its money', 1;
      end
      else if @recovery is not null
        throw 51000, N'[SG014] only a receipt has money to reverse', 1;
    end
    else if @kind = N'received'
    begin
      select @r_id = null;
      select @r_id = id, @r_case = subrogation_case_id, @r_row_kind = row_kind, @r_gel = amount_gel, @r_received = received_on
      from core.claim_recovery where id = @recovery;
      if @r_id is null or not ((@r_case is null and @case is null) or @r_case = @case) or @r_row_kind <> N'receipt'
         or @r_gel <> @amount_gel or @r_received <> @booked_on
        throw 51000, N'[SG014] a receipt goes with its own money row of the case', 1;
      if not exists (select 1 from core.subrogation_party where id = @payer and case_id = @case)
        throw 51000, N'[SG015] the payer is not a party of the case', 1;
    end

    declare days cursor local fast_forward for
      select d from (
        select @booked_on as d
        union select booked_on from core.subrogation_movement where case_id = @case and booked_on > @booked_on
        union select paid_on from core.claim_payment where claim_id = @case_claim and paid_on > @booked_on
        union select received_on from core.claim_recovery
              where claim_id = @case_claim and kind = N'subrogation' and (subrogation_case_id is null or subrogation_case_id <> @case)
                and received_on > @booked_on
      ) x order by d;
    open days;
    fetch next from days into @day;
    while @@fetch_status = 0
    begin
      select @claimed = claimed + case when @effective = N'claimed' then @amount_gel else 0 end,
             @confirmed = confirmed + case when @effective = N'confirmed' then @amount_gel else 0 end,
             @received = received + case when @effective = N'received' then @amount_gel else 0 end,
             @cancelled = cancelled + case when @effective = N'cancelled' then @amount_gel else 0 end
      from core.subrogation_totals(@case, @day);
      if @confirmed - @received - @cancelled < 0
      begin
        set @m = concat(N'[SG021] regress case ', lower(convert(nchar(36), @case)), N' would owe less than nothing on ', format(@day, 'yyyy-MM-dd'));
        exec core.fail @m;
      end
      if @kind in (N'claimed', N'confirmed')
      begin
        set @room = (select room from core.subrogation_room(@case_claim, @case, @day));
        if (@kind = N'claimed' and @claimed > @room) or (@kind = N'confirmed' and @confirmed > @room)
        begin
          set @m = concat(N'[SG020] regress on claim ', lower(convert(nchar(36), @case_claim)), N' may not exceed what was paid less its other regress money on ',
                          format(@day, 'yyyy-MM-dd'), N' (', @room, N')');
          exec core.fail @m;
        end
      end
      fetch next from days into @day;
    end
    close days;
    deallocate days;

    insert core.subrogation_movement (id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id,
                                      document_kind, document_number, document_date, note, request_key, created_at, created_by)
    select id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id,
           document_kind, document_number, document_date, note, request_key, created_at, created_by
    from inserted where id = @id;
    fetch next from rows into @id, @case, @kind, @booked_on, @amount_gel, @reverses, @recovery, @payer;
  end
  close rows;
  deallocate rows;
end;
GO

-- Money of a regress case with its movement, in one call (replaces the legacy deferred check SG034).
-- The recovery row (receipt or the reversal of one) and its movement are written together or not at all.
create procedure core.record_case_money
  @recovery_id uniqueidentifier, @claim_id uniqueidentifier, @case_id uniqueidentifier, @row_kind nvarchar(20),
  @reverses_recovery_id uniqueidentifier, @received_on date, @amount_gel decimal(18, 2), @bank_reference nvarchar(100),
  @recovery_note nvarchar(max), @created_by uniqueidentifier,
  @movement_id uniqueidentifier, @movement_kind nvarchar(20), @reverses_movement_id uniqueidentifier, @payer_id uniqueidentifier,
  @document_kind nvarchar(30), @document_number nvarchar(400), @document_date date, @movement_note nvarchar(max), @request_key uniqueidentifier
as
begin
  set nocount on;
  set xact_abort on;
  begin transaction;
  declare @key nvarchar(36) = lower(convert(nchar(36), @recovery_id));
  exec sp_set_session_context N'core.case_recovery', @key;
  insert core.claim_recovery (id, claim_id, kind, subrogation_case_id, row_kind, reverses_claim_recovery_id, received_on, amount_gel, bank_reference, note, created_by)
  values (@recovery_id, @claim_id, N'subrogation', @case_id, @row_kind, @reverses_recovery_id, @received_on, @amount_gel, @bank_reference, @recovery_note, @created_by);
  exec sp_set_session_context N'core.case_recovery', null;
  insert core.subrogation_movement (id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id,
                                    document_kind, document_number, document_date, note, request_key)
  values (@movement_id, @case_id, @movement_kind, @received_on, @amount_gel, @reverses_movement_id, @recovery_id, @payer_id,
          @document_kind, @document_number, @document_date, @movement_note, @request_key);
  commit transaction;
end;
GO

-- A regress recovery without its movement: must always be empty (the rule the legacy deferred trigger kept).
create view core.case_recovery_without_movement
as
select r.id, r.claim_id, r.subrogation_case_id, r.received_on, r.amount_gel
from core.claim_recovery r
where r.subrogation_case_id is not null
  and not exists (select 1 from core.subrogation_movement m where m.claim_recovery_id = r.id);
GO
