--
-- PostgreSQL database dump
--

\restrict v1J7fbfqlklC4vfdFMOhGwccmiQfubpWVngHIbfObfL4KrCAX5IZ8ObeeOBHO5x

-- Dumped from database version 17.11 (Debian 17.11-1.pgdg13+2)
-- Dumped by pg_dump version 17.11 (Debian 17.11-1.pgdg13+2)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: core; Type: SCHEMA; Schema: -; Owner: -
--

CREATE SCHEMA core;


--
-- Name: assert_period_open(date); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.assert_period_open(booked date) RETURNS void
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin
  perform pg_advisory_xact_lock_shared(hashtext('core.accounting_period'));
  if exists (
    select 1 from core.accounting_period
    where period_start = date_trunc('month', booked)::date and status = 'closed'
  ) then
    raise exception 'period % is closed; book the movement in the current open period', to_char(booked, 'YYYY-MM')
      using errcode = 'check_violation';
  end if;
end;
$$;


--
-- Name: audit(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.audit() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  actor uuid := nullif(current_setting('core.user_id', true), '')::uuid;
  key_column text := coalesce(tg_argv[0], 'id');
  old_json jsonb := case when tg_op = 'INSERT' then null else to_jsonb(old) - 'password_hash' end;
  new_json jsonb := case when tg_op = 'DELETE' then null else to_jsonb(new) - 'password_hash' end;
begin
  if tg_op = 'INSERT' then
    insert into core.audit_log (user_id, table_name, row_id, action, new_row)
    values (actor, tg_table_name, new_json ->> key_column, 'insert', new_json);
    return new;
  elsif tg_op = 'UPDATE' then
    if old_json is distinct from new_json then
      insert into core.audit_log (user_id, table_name, row_id, action, old_row, new_row)
      values (actor, tg_table_name, new_json ->> key_column, 'update', old_json, new_json);
    end if;
    return new;
  else
    insert into core.audit_log (user_id, table_name, row_id, action, old_row)
    values (actor, tg_table_name, old_json ->> key_column, 'delete', old_json);
    return old;
  end if;
end;
$$;


--
-- Name: check_claim_recovery_movement(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.check_claim_recovery_movement() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin
  if new.subrogation_case_id is not null
     and not exists (select 1 from core.subrogation_movement where claim_recovery_id = new.id) then
    raise exception 'recovery % of regress case % has no movement', new.id, new.subrogation_case_id using errcode = 'SG034';
  end if;
  return null;
end;
$$;


--
-- Name: guard_accounting_period(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_accounting_period() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin
  if tg_op = 'DELETE' then
    if old.status = 'closed' then
      raise exception 'period % is closed and cannot be removed', to_char(old.period_start, 'YYYY-MM')
        using errcode = 'check_violation';
    end if;
    return old;
  end if;
  if new.period_start is distinct from old.period_start then
    raise exception 'the month of an accounting period cannot change (% to %)', to_char(old.period_start, 'YYYY-MM'), to_char(new.period_start, 'YYYY-MM')
      using errcode = 'check_violation';
  end if;
  -- The whole row is compared, so a column added later is covered too.
  if old.status = 'closed' and to_jsonb(new) is distinct from to_jsonb(old) then
    raise exception 'period % is closed; its status, closing time and author cannot change', to_char(old.period_start, 'YYYY-MM')
      using errcode = 'check_violation';
  end if;
  return new;
end;
$$;


--
-- Name: guard_claim_cession(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_cession() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  new_kind text;
  new_currency char(3);
  claim_currency char(3);
  proportional numeric;
  xol_count integer;
begin
  perform pg_advisory_xact_lock(hashtext('core.claim_cession'), hashtext(new.claim_id::text));
  select kind, currency into new_kind, new_currency from core.ri_treaty where id = new.treaty_id;
  select currency into claim_currency from core.claim where id = new.claim_id;
  if new_kind = 'excess_of_loss' and new_currency <> claim_currency then
    raise exception 'excess-of-loss treaty in % cannot share a loss in %', new_currency, claim_currency using errcode = 'RI003';
  end if;
  select coalesce(sum(c.share_percent) filter (where t.kind <> 'excess_of_loss'), 0),
         count(*) filter (where t.kind = 'excess_of_loss')
    into proportional, xol_count
  from core.claim_cession c join core.ri_treaty t on t.id = c.treaty_id where c.claim_id = new.claim_id;
  if new_kind <> 'excess_of_loss' then proportional := proportional + new.share_percent; else xol_count := xol_count + 1; end if;
  if proportional > 100 then
    raise exception 'proportional shares of claim % would exceed 100 %%', new.claim_id using errcode = 'RI001';
  end if;
  if xol_count > 0 and proportional > 0 then
    raise exception 'claim % would combine excess of loss with a proportional share', new.claim_id using errcode = 'RI002';
  end if;
  if xol_count > 1 then
    raise exception 'claim % would take more than one excess-of-loss treaty', new.claim_id using errcode = 'RI004';
  end if;
  return new;
end;
$$;


--
-- Name: guard_claim_event(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_event() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin
  if (select policy_id from core.loss_event where id = new.loss_event_id) is distinct from new.policy_id then
    raise exception 'claim % is not on the policy of its loss event', new.id using errcode = 'LE001';
  end if;
  return new;
end;
$$;


--
-- Name: guard_claim_payment(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_payment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.paid_on); return new; end;
$$;


--
-- Name: guard_claim_payment_request(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_payment_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $_$
declare
  r core.claim_payment_request;
  threshold numeric;
  stored text;
begin
  if new.kind <> 'payment' then
    return new;
  end if;
  if new.request_id is not null then
    select * into r from core.claim_payment_request where id = new.request_id;
    if r.id is null or r.claim_id <> new.claim_id then
      raise exception 'a claim payment carries out a request of its own claim' using errcode = 'CQ001';
    end if;
    if new.paid_on <> r.paid_on or new.amount <> r.amount or new.deductible_withheld <> r.deductible_withheld
       or new.premium_offset <> r.premium_offset or new.net_paid <> r.net_paid or new.net_paid_gel <> r.net_paid_gel
       or new.rate <> r.rate or new.payee_kind <> r.payee_kind or new.payee_party_id is distinct from r.payee_party_id then
      raise exception 'a claim payment takes its request''s quote over exactly' using errcode = 'CQ002';
    end if;
    return new;
  end if;
  select value into stored from core.setting where key = 'payment.fd_threshold_gel';
  if stored is null or stored !~ '^\d{1,13}(\.\d{1,2})?$' then
    raise exception 'the payment authorisation threshold is not set as an amount' using errcode = 'CQ004';
  end if;
  threshold := stored::numeric;
  if new.net_paid_gel > threshold then
    raise exception 'a claim payment of % GEL net is above the threshold of %: it needs an approved request', new.net_paid_gel, threshold
      using errcode = 'CQ003';
  end if;
  return new;
end;
$_$;


--
-- Name: guard_claim_payment_reversal(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_payment_reversal() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  o core.claim_payment;
  offset_back core.payment;
  case_ref uuid;
  check_day date;
  t record;
  room numeric;
  added numeric;
begin
  if new.kind <> 'reversal' then
    return new;
  end if;
  select * into o from core.claim_payment where id = new.reverses_claim_payment_id;
  if o.id is null or o.kind <> 'payment' then
    raise exception 'only a payment is reversed, and a reversal is not' using errcode = 'CP001';
  end if;
  if o.claim_id <> new.claim_id or o.payee_kind <> new.payee_kind or o.payee_party_id is distinct from new.payee_party_id
     or o.rate <> new.rate then
    raise exception 'a reversal keeps the claim, the payee and the rate of the payment it reverses' using errcode = 'CP002';
  end if;
  if new.amount <> -o.amount or new.deductible_withheld <> -o.deductible_withheld or new.premium_offset <> -o.premium_offset
     or new.net_paid <> -o.net_paid or new.net_paid_gel <> -o.net_paid_gel then
    raise exception 'a reversal is the exact opposite of the payment it reverses' using errcode = 'CP003';
  end if;
  if new.paid_on < o.paid_on then
    raise exception 'a reversal is not dated before the payment it reverses' using errcode = 'CP004';
  end if;
  if new.returned_on < o.paid_on then
    raise exception 'money given back cannot come back before the payment it reverses' using errcode = 'CP007';
  end if;
  if o.premium_offset = 0 then
    if new.offset_payment_id is not null then
      raise exception 'a payment without a premium set-off gives no set-off back' using errcode = 'CP005';
    end if;
  else
    select * into offset_back from core.payment where id = new.offset_payment_id;
    if o.offset_payment_id is null or offset_back.id is null or offset_back.kind <> 'reversal'
       or offset_back.reverses_payment_id <> o.offset_payment_id or offset_back.received_on <> new.paid_on then
      raise exception 'the set-off is given back by the reversal of the payment''s own set-off receipt, on the same day'
        using errcode = 'CP005';
    end if;
  end if;

  -- Regress on the claim (0030): what each case claimed or confirmed may not exceed what was paid less the claim's other
  -- regress money, on any day. The reversal lowers what was paid from its date on; every day a total changes from then
  -- on is checked, with the reversal added by hand (the row is not in the table yet). The lock is the one the regress
  -- guard takes, so a movement and a reversal do not pass each other.
  perform core.lock_subrogation_claim(new.claim_id);
  added := new.net_paid_gel + coalesce(offset_back.amount_gel, 0);
  for case_ref in select id from core.subrogation_case where claim_id = new.claim_id loop
    for check_day in
      select d from (
        select new.paid_on as d
        union select booked_on from core.subrogation_movement where case_id = case_ref and booked_on > new.paid_on
        union select paid_on from core.claim_payment where claim_id = new.claim_id and paid_on > new.paid_on
        union select received_on from core.claim_recovery
              where claim_id = new.claim_id and kind = 'subrogation' and subrogation_case_id is distinct from case_ref
                and received_on > new.paid_on
      ) days order by d
    loop
      select * into t from core.subrogation_totals(case_ref, check_day);
      select coalesce(sum(cp.net_paid_gel + coalesce(p.amount_gel, 0)), 0) into room
      from core.claim_payment cp left join core.payment p on p.id = cp.offset_payment_id
      where cp.claim_id = new.claim_id and cp.paid_on <= check_day;
      room := room + added - (select coalesce(sum(r.amount_gel), 0) from core.claim_recovery r
                              where r.claim_id = new.claim_id and r.kind = 'subrogation'
                                and r.subrogation_case_id is distinct from case_ref and r.received_on <= check_day);
      if t.claimed > room or t.confirmed > room then
        raise exception 'the reversal would leave regress case % above what was paid on %', case_ref, check_day
          using errcode = 'CP006';
      end if;
    end loop;
  end loop;
  return new;
end;
$$;


--
-- Name: guard_claim_recovery(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_recovery() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.received_on); return new; end;
$$;


--
-- Name: guard_claim_recovery_case(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_recovery_case() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  case_claim uuid;
  original core.claim_recovery;
begin
  if new.kind = 'subrogation' then
    perform core.lock_subrogation_claim(new.claim_id);
  end if;
  if new.subrogation_case_id is not null then
    select claim_id into case_claim from core.subrogation_case where id = new.subrogation_case_id;
    if case_claim is distinct from new.claim_id then
      raise exception 'recovery on claim % cannot go to regress case % of another claim', new.claim_id, new.subrogation_case_id
        using errcode = 'SG031';
    end if;
  elsif new.kind = 'subrogation' and new.row_kind = 'receipt'
        and exists (select 1 from core.subrogation_case where claim_id = new.claim_id) then
    raise exception 'claim % has a regress case: its regress money goes through the case', new.claim_id using errcode = 'SG032';
  end if;
  if new.row_kind = 'reversal' then
    select * into original from core.claim_recovery where id = new.reverses_claim_recovery_id;
    if original.id is null or original.row_kind <> 'receipt' or original.claim_id <> new.claim_id or original.kind <> new.kind
       or original.subrogation_case_id is distinct from new.subrogation_case_id
       or new.amount_gel <> -original.amount_gel or new.received_on < original.received_on then
      raise exception 'recovery reversal must mirror one receipt of the same claim and case, on or after its day'
        using errcode = 'SG033';
    end if;
  end if;
  return new;
end;
$$;


--
-- Name: guard_claim_reserve(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_reserve() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.booked_on); return new; end;
$$;


--
-- Name: guard_claim_reserve_request(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_claim_reserve_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  r core.claim_reserve_request;
  level numeric;
begin
  if new.request_id is null then
    return new;
  end if;
  select * into r from core.claim_reserve_request where id = new.request_id;
  if r.id is null or r.claim_id <> new.claim_id then
    raise exception 'a reserve movement carries out a request of its own claim' using errcode = 'RR001';
  end if;
  if new.change <> r.change or new.booked_on <> r.booked_on then
    raise exception 'a reserve movement takes the request''s day and difference over exactly' using errcode = 'RR002';
  end if;
  if new.created_by is null or new.created_by = r.created_by then
    raise exception 'a reserve change is approved by someone other than who asked for it' using errcode = 'RR003';
  end if;
  select coalesce(sum(change), 0) into level from core.claim_reserve where claim_id = new.claim_id;
  if level <> r.base_level then
    raise exception 'the reserve moved since the request (now %, asked from %): a new request is needed', level, r.base_level
      using errcode = 'RR004';
  end if;
  return new;
end;
$$;


--
-- Name: guard_fms_suspension_target(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_fms_suspension_target() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  r record;
begin
  -- Only the transition above marks a row as unmapped; afterwards the mark can only be
  -- cleared, by attaching a target.
  if tg_op = 'INSERT' then
    new.legacy_unmapped := false;
  else
    new.legacy_unmapped := old.legacy_unmapped and new.target_policy_id is null and new.target_claim_id is null;
  end if;
  if new.scope = 'transaction' and new.target_policy_id is null and new.target_claim_id is null and not new.legacy_unmapped then
    select * into r from core.resolve_suspension_target(new.party_id, new.transaction_ref);
    new.target_policy_id := r.policy_id;
    new.target_claim_id := r.claim_id;
    if r.number is not null then new.transaction_ref := r.number; end if;
  end if;
  if new.target_policy_id is not null and not exists (
    select 1 from core.policy p join core.contract c on c.id = p.contract_id
    where p.id = new.target_policy_id and c.policyholder_id = new.party_id
  ) then
    raise exception 'suspension target policy does not belong to the client' using errcode = 'AML01';
  end if;
  if new.target_claim_id is not null and not exists (
    select 1 from core.claim cl join core.policy p on p.id = cl.policy_id join core.contract c on c.id = p.contract_id
    where cl.id = new.target_claim_id and c.policyholder_id = new.party_id
  ) then
    raise exception 'suspension target claim does not belong to the client' using errcode = 'AML01';
  end if;
  return new;
end;
$$;


--
-- Name: guard_installment_adjustment(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_installment_adjustment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.booked_on); return new; end;
$$;


--
-- Name: guard_payment(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_payment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.received_on); return new; end;
$$;


--
-- Name: guard_payment_allocation(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_payment_allocation() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.booked_on); return new; end;
$$;


--
-- Name: guard_payment_allocation_total(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_payment_allocation_total() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  receipt_kind text;
  receipt_gel numeric;
  allocated_gel numeric;
begin
  perform pg_advisory_xact_lock(hashtext('core.payment_allocation'), hashtext(new.payment_id::text));
  select kind, amount_gel into receipt_kind, receipt_gel from core.payment where id = new.payment_id;
  if receipt_kind = 'receipt' and exists (select 1 from core.payment r where r.reverses_payment_id = new.payment_id) then
    raise exception 'payment % is reversed and takes no allocation', new.payment_id using errcode = 'PA001';
  end if;
  select coalesce(sum(amount_gel), 0) + new.amount_gel into allocated_gel
  from core.payment_allocation where payment_id = new.payment_id;
  if abs(allocated_gel) > abs(receipt_gel) or sign(allocated_gel) * sign(receipt_gel) < 0 then
    raise exception 'allocations of payment % would exceed it (% of %)', new.payment_id, allocated_gel, receipt_gel
      using errcode = 'PA002';
  end if;
  return new;
end;
$$;


--
-- Name: guard_payment_refund_request(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_payment_refund_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $_$
declare
  r core.premium_refund_request;
  threshold numeric;
  stored text;
begin
  if new.kind <> 'refund' then
    return new;
  end if;
  if new.refund_request_id is not null then
    select * into r from core.premium_refund_request where id = new.refund_request_id;
    if r.id is null or new.amount_gel <> -r.amount_gel or new.received_on <> r.refunded_on or new.method <> r.method
       or new.reference is distinct from r.reference or new.payer_party_id is distinct from r.payer_party_id then
      raise exception 'a premium refund takes its request over exactly' using errcode = 'RF002';
    end if;
    return new;
  end if;
  select value into stored from core.setting where key = 'payment.fd_threshold_gel';
  if stored is null or stored !~ '^\d{1,13}(\.\d{1,2})?$' then
    raise exception 'the payment authorisation threshold is not set as an amount' using errcode = 'RF004';
  end if;
  threshold := stored::numeric;
  if -new.amount_gel > threshold then
    raise exception 'a premium refund of % GEL is above the threshold of %: it needs an approved request', -new.amount_gel, threshold
      using errcode = 'RF003';
  end if;
  return new;
end;
$_$;


--
-- Name: guard_payment_reversal_request(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_payment_reversal_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $_$
declare
  r core.receipt_reversal_request;
  threshold numeric;
  stored text;
begin
  if new.kind <> 'reversal' or new.method = 'claim_offset' then
    return new;
  end if;
  if new.reversal_request_id is not null then
    select * into r from core.receipt_reversal_request where id = new.reversal_request_id;
    if r.id is null or new.reverses_payment_id is distinct from r.receipt_id or new.amount_gel <> -r.amount_gel
       or new.received_on <> r.reversed_on or new.method <> r.method or new.payer_party_id is distinct from r.payer_party_id
       or new.reference is distinct from r.reason then
      raise exception 'a receipt''s reversal takes its request over exactly' using errcode = 'RV002';
    end if;
    return new;
  end if;
  select value into stored from core.setting where key = 'payment.fd_threshold_gel';
  if stored is null or stored !~ '^\d{1,13}(\.\d{1,2})?$' then
    raise exception 'the payment authorisation threshold is not set as an amount' using errcode = 'RV004';
  end if;
  threshold := stored::numeric;
  if -new.amount_gel > threshold then
    raise exception 'a receipt''s reversal of % GEL is above the threshold of %: it needs an approved request', -new.amount_gel, threshold
      using errcode = 'RV003';
  end if;
  return new;
end;
$_$;


--
-- Name: guard_premium_cession(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_premium_cession() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.booked_on); return new; end;
$$;


--
-- Name: guard_premium_cession_total(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_premium_cession_total() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  row_amount numeric;
  row_amount_gel numeric;
  total_share numeric;
  total_amount numeric;
  total_amount_gel numeric;
begin
  perform pg_advisory_xact_lock(hashtext('core.premium_cession'), hashtext(new.premium_transaction_id::text));
  select amount, amount_gel into row_amount, row_amount_gel from core.premium_transaction where id = new.premium_transaction_id;
  select coalesce(sum(share_percent), 0) + new.share_percent,
         coalesce(sum(abs(amount)), 0) + abs(new.amount),
         coalesce(sum(abs(amount_gel)), 0) + abs(new.amount_gel)
    into total_share, total_amount, total_amount_gel
  from core.premium_cession where premium_transaction_id = new.premium_transaction_id;
  if total_share > 100 or total_amount > abs(row_amount) or total_amount_gel > abs(row_amount_gel) then
    raise exception 'cessions of premium row % would exceed the row (% %%)', new.premium_transaction_id, total_share
      using errcode = 'RI001';
  end if;
  return new;
end;
$$;


--
-- Name: guard_premium_refund_request(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_premium_refund_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.refunded_on); return new; end;
$$;


--
-- Name: guard_premium_transaction(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_premium_transaction() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.booked_on); return new; end;
$$;


--
-- Name: guard_receipt_reversal_request(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_receipt_reversal_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin perform core.assert_period_open(new.reversed_on); return new; end;
$$;


--
-- Name: guard_refund_allocation_plan(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_refund_allocation_plan() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  request uuid;
begin
  select refund_request_id into request from core.payment where id = new.payment_id;
  if request is null then
    return new;
  end if;
  if not exists (
    select 1 from core.premium_refund_request_line l join core.premium_refund_request q on q.id = l.request_id
    where l.request_id = request and l.installment_id = new.installment_id
      and new.amount = -l.amount and new.amount_gel = -l.amount_gel and new.rate = q.rate
  ) then
    raise exception 'an allocation of a requested refund is a line of its plan' using errcode = 'RF005';
  end if;
  return new;
end;
$$;


--
-- Name: guard_reversal_allocation_plan(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_reversal_allocation_plan() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  request uuid;
begin
  select reversal_request_id into request from core.payment where id = new.payment_id;
  if request is null then
    return new;
  end if;
  if not exists (
    select 1 from core.receipt_reversal_request_line l
    where l.request_id = request and l.installment_id = new.installment_id
      and new.amount = -l.amount and new.amount_gel = -l.amount_gel and new.rate = l.rate
  ) then
    raise exception 'an allocation of a requested reversal is a line of its plan' using errcode = 'RV005';
  end if;
  return new;
end;
$$;


--
-- Name: guard_subrogation_case(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_subrogation_case() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  line_class text;
  coverage_code text;
  t record;
begin
  if tg_op = 'INSERT' then
    perform core.lock_subrogation_claim(new.claim_id);
    select l.class_code, pc.code into line_class, coverage_code
    from core.claim c join core.policy_line l on l.id = c.policy_line_id
    left join core.product_coverage pc on pc.id = l.product_coverage_id
    where c.id = new.claim_id;
    if line_class is distinct from '5.1' then
      raise exception 'claim % is not of class 5.1: no regress', new.claim_id using errcode = 'SG001';
    end if;
    if coverage_code is null then
      raise exception 'claim % has no known coverage: no regress', new.claim_id using errcode = 'SG002';
    end if;
    if coverage_code = 'theft' then
      raise exception 'claim % is a theft, not an accident: no regress', new.claim_id using errcode = 'SG003';
    end if;
    if new.status <> 'opened' then
      raise exception 'a regress case starts opened' using errcode = 'SG005';
    end if;
  else
    if new.claim_id <> old.claim_id or new.number <> old.number or new.request_key <> old.request_key
       or new.opened_on <> old.opened_on or new.created_by is distinct from old.created_by or new.created_at <> old.created_at then
      raise exception 'regress case % keeps its claim, number and origin', old.id using errcode = 'SG005';
    end if;
    if new.status = 'closed' and old.status <> 'closed' then
      perform core.lock_subrogation_claim(new.claim_id);
      select * into t from core.subrogation_totals(new.id, null);
      if not (t.confirmed > 0 and t.confirmed - t.received - t.cancelled = 0) then
        raise exception 'regress case % still has % owed, or nothing confirmed', new.id, t.confirmed - t.received - t.cancelled
          using errcode = 'SG006';
      end if;
    end if;
  end if;
  return new;
end;
$$;


--
-- Name: guard_subrogation_movement(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_subrogation_movement() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  case_claim uuid;
  target core.subrogation_movement;
  money_row core.claim_recovery;
  payer_case uuid;
  effective text := new.kind;
  t record;
  check_day date;
  room numeric;
  claimed numeric;
  confirmed numeric;
  received numeric;
  cancelled numeric;
begin
  perform core.assert_period_open(new.booked_on);
  select claim_id into case_claim from core.subrogation_case where id = new.case_id;
  perform core.lock_subrogation_claim(case_claim);

  if new.kind = 'reversal' then
    select * into target from core.subrogation_movement where id = new.reverses_movement_id;
    if target.id is null or target.case_id <> new.case_id or target.kind = 'reversal' then
      raise exception 'a reversal undoes one movement of its own case' using errcode = 'SG010';
    end if;
    if exists (select 1 from core.subrogation_movement where reverses_movement_id = target.id) then
      raise exception 'movement % is reversed already', target.id using errcode = 'SG011';
    end if;
    if new.amount_gel <> -target.amount_gel then
      raise exception 'a reversal is exactly minus its original' using errcode = 'SG012';
    end if;
    if new.booked_on < target.booked_on then
      raise exception 'a reversal is dated on or after its original' using errcode = 'SG013';
    end if;
    effective := target.kind;
    if target.kind = 'received' then
      select * into money_row from core.claim_recovery where id = new.claim_recovery_id;
      if money_row.id is null or money_row.reverses_claim_recovery_id is distinct from target.claim_recovery_id
         or money_row.received_on <> new.booked_on then
        raise exception 'the reversal of a receipt goes with the reversal of its money' using errcode = 'SG014';
      end if;
    elsif new.claim_recovery_id is not null then
      raise exception 'only a receipt has money to reverse' using errcode = 'SG014';
    end if;
  elsif new.kind = 'received' then
    select * into money_row from core.claim_recovery where id = new.claim_recovery_id;
    if money_row.id is null or money_row.subrogation_case_id is distinct from new.case_id or money_row.row_kind <> 'receipt'
       or money_row.amount_gel <> new.amount_gel or money_row.received_on <> new.booked_on then
      raise exception 'a receipt goes with its own money row of the case' using errcode = 'SG014';
    end if;
    select case_id into payer_case from core.subrogation_party where id = new.payer_id;
    if payer_case is distinct from new.case_id then
      raise exception 'the payer is not a party of the case' using errcode = 'SG015';
    end if;
  end if;

  -- Every day from the new movement's on where something of the claim changes. The new row is not
  -- in the table yet (before insert), so it is added to each day's totals by hand.
  for check_day in
    select d from (
      select new.booked_on as d
      union select booked_on from core.subrogation_movement where case_id = new.case_id and booked_on > new.booked_on
      union select paid_on from core.claim_payment where claim_id = case_claim and paid_on > new.booked_on
      union select received_on from core.claim_recovery
            where claim_id = case_claim and kind = 'subrogation' and subrogation_case_id is distinct from new.case_id
              and received_on > new.booked_on
    ) days order by d
  loop
    select * into t from core.subrogation_totals(new.case_id, check_day);
    claimed := t.claimed + case when effective = 'claimed' then new.amount_gel else 0 end;
    confirmed := t.confirmed + case when effective = 'confirmed' then new.amount_gel else 0 end;
    received := t.received + case when effective = 'received' then new.amount_gel else 0 end;
    cancelled := t.cancelled + case when effective = 'cancelled' then new.amount_gel else 0 end;
    if confirmed - received - cancelled < 0 then
      raise exception 'regress case % would owe less than nothing on %', new.case_id, check_day using errcode = 'SG021';
    end if;
    if new.kind in ('claimed', 'confirmed') then
      select coalesce(sum(cp.net_paid_gel + coalesce(p.amount_gel, 0)), 0) into room
      from core.claim_payment cp left join core.payment p on p.id = cp.offset_payment_id
      where cp.claim_id = case_claim and cp.paid_on <= check_day;
      room := room - (select coalesce(sum(r.amount_gel), 0) from core.claim_recovery r
                      where r.claim_id = case_claim and r.kind = 'subrogation'
                        and r.subrogation_case_id is distinct from new.case_id and r.received_on <= check_day);
      if (new.kind = 'claimed' and claimed > room) or (new.kind = 'confirmed' and confirmed > room) then
        raise exception 'regress on claim % may not exceed what was paid less its other regress money on % (%)', case_claim, check_day, room
          using errcode = 'SG020';
      end if;
    end if;
  end loop;
  return new;
end;
$$;


--
-- Name: guard_subrogation_party(); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.guard_subrogation_party() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
begin
  if new.case_id <> old.case_id or new.party_id <> old.party_id or new.role <> old.role then
    raise exception 'regress party % keeps its case, person and role', old.id using errcode = 'SG007';
  end if;
  return new;
end;
$$;


--
-- Name: lock_subrogation_claim(uuid); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.lock_subrogation_claim(claim_ref uuid) RETURNS void
    LANGUAGE sql
    SET search_path TO ''
    AS $$
  select pg_advisory_xact_lock(hashtext('core.subrogation_claim'), hashtext(claim_ref::text))
$$;


--
-- Name: next_number(text); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.next_number(seq text) RETURNS text
    LANGUAGE plpgsql
    SET search_path TO ''
    AS $$
declare
  taken bigint;
  series_prefix text;
  series_padding int;
begin
  update core.number_sequence
  set next_value = next_value + 1
  where code = seq
  returning next_value - 1, prefix, padding into taken, series_prefix, series_padding;
  if taken is null then
    return null;
  end if;
  if length(taken::text) > series_padding then
    raise exception 'number series % has run out: % does not fit in % digits', seq, taken, series_padding
      using errcode = 'NU001';
  end if;
  return series_prefix || lpad(taken::text, series_padding, '0');
end;
$$;


--
-- Name: resolve_suspension_target(uuid, text); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.resolve_suspension_target(p_party uuid, p_ref text, OUT policy_id uuid, OUT claim_id uuid, OUT number text) RETURNS record
    LANGUAGE plpgsql STABLE
    SET search_path TO ''
    AS $$
declare
  wanted text := upper(btrim(coalesce(p_ref, '')));
  matches integer;
begin
  if wanted = '' then return; end if;
  select count(*) into matches from (
    select p.id from core.policy p join core.contract c on c.id = p.contract_id
    where upper(p.number) = wanted and c.policyholder_id = p_party
    union all
    select cl.id from core.claim cl join core.policy p on p.id = cl.policy_id join core.contract c on c.id = p.contract_id
    where upper(cl.number) = wanted and c.policyholder_id = p_party
  ) m;
  if matches <> 1 then return; end if;
  select p.id, p.number into policy_id, number from core.policy p join core.contract c on c.id = p.contract_id
  where upper(p.number) = wanted and c.policyholder_id = p_party;
  if policy_id is null then
    select cl.id, cl.number into claim_id, number from core.claim cl join core.policy p on p.id = cl.policy_id join core.contract c on c.id = p.contract_id
    where upper(cl.number) = wanted and c.policyholder_id = p_party;
  end if;
end;
$$;


--
-- Name: subrogation_totals(uuid, date); Type: FUNCTION; Schema: core; Owner: -
--

CREATE FUNCTION core.subrogation_totals(case_ref uuid, as_of date, OUT claimed numeric, OUT confirmed numeric, OUT received numeric, OUT cancelled numeric) RETURNS record
    LANGUAGE sql STABLE
    SET search_path TO ''
    AS $$
  select coalesce(sum(m.amount_gel) filter (where coalesce(o.kind, m.kind) = 'claimed'), 0),
         coalesce(sum(m.amount_gel) filter (where coalesce(o.kind, m.kind) = 'confirmed'), 0),
         coalesce(sum(m.amount_gel) filter (where coalesce(o.kind, m.kind) = 'received'), 0),
         coalesce(sum(m.amount_gel) filter (where coalesce(o.kind, m.kind) = 'cancelled'), 0)
  from core.subrogation_movement m left join core.subrogation_movement o on o.id = m.reverses_movement_id
  where m.case_id = case_ref and (as_of is null or m.booked_on <= as_of)
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: accounting_period; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.accounting_period (
    period_start date NOT NULL,
    status text DEFAULT 'open'::text NOT NULL,
    closed_at timestamp with time zone,
    closed_by uuid,
    CONSTRAINT accounting_period_closed_fields CHECK (((status = 'closed'::text) = (closed_at IS NOT NULL))),
    CONSTRAINT accounting_period_period_start_check CHECK ((period_start = (date_trunc('month'::text, (period_start)::timestamp with time zone))::date)),
    CONSTRAINT accounting_period_status_check CHECK ((status = ANY (ARRAY['open'::text, 'closed'::text])))
);


--
-- Name: aml_case; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.aml_case (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    party_id uuid NOT NULL,
    opened_on date NOT NULL,
    source text NOT NULL,
    description text NOT NULL,
    transaction_ref text,
    class_code text,
    status text DEFAULT 'open'::text NOT NULL,
    examined_on date,
    examination text,
    suspicion_at timestamp with time zone,
    str_sent_at timestamp with time zone,
    str_type text,
    str_other_basis text,
    fms_reference text,
    closed_on date,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT aml_case_other_basis CHECK (((str_type IS DISTINCT FROM 'other'::text) OR (str_other_basis IS NOT NULL))),
    CONSTRAINT aml_case_reported CHECK (((status <> 'reported'::text) OR ((str_sent_at IS NOT NULL) AND (str_type IS NOT NULL) AND (suspicion_at IS NOT NULL)))),
    CONSTRAINT aml_case_source_check CHECK ((source = ANY (ARRAY['monitoring'::text, 'employee'::text, 'fms_request'::text, 'other'::text]))),
    CONSTRAINT aml_case_status_check CHECK ((status = ANY (ARRAY['open'::text, 'examined'::text, 'reported'::text, 'closed'::text]))),
    CONSTRAINT aml_case_str_type_check CHECK ((str_type = ANY (ARRAY['ml'::text, 'tf'::text, 'other'::text])))
);


--
-- Name: app_user; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.app_user (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    username text NOT NULL,
    full_name text NOT NULL,
    password_hash text NOT NULL,
    role_code text NOT NULL,
    branch_id uuid,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT app_user_username_check CHECK ((username = lower(username)))
);


--
-- Name: audit_log; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.audit_log (
    id bigint NOT NULL,
    at timestamp with time zone DEFAULT now() NOT NULL,
    user_id uuid,
    table_name text NOT NULL,
    row_id text NOT NULL,
    action text NOT NULL,
    old_row jsonb,
    new_row jsonb,
    CONSTRAINT audit_log_action_check CHECK ((action = ANY (ARRAY['insert'::text, 'update'::text, 'delete'::text])))
);


--
-- Name: audit_log_id_seq; Type: SEQUENCE; Schema: core; Owner: -
--

ALTER TABLE core.audit_log ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME core.audit_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: branch; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.branch (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code text NOT NULL,
    name_ka text NOT NULL
);


--
-- Name: claim; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    policy_id uuid NOT NULL,
    policy_line_id uuid NOT NULL,
    currency character(3) NOT NULL,
    occurred_on date NOT NULL,
    occurred_place text,
    reported_on date NOT NULL,
    description text NOT NULL,
    status text DEFAULT 'registered'::text NOT NULL,
    claimed_amount numeric(18,2),
    approved_amount numeric(18,2),
    deductible_amount numeric(18,2) DEFAULT 0 NOT NULL,
    is_total_loss boolean DEFAULT false NOT NULL,
    rejection_reason text,
    closed_on date,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    loss_event_id uuid NOT NULL,
    CONSTRAINT claim_dates CHECK ((reported_on >= occurred_on)),
    CONSTRAINT claim_rejection CHECK (((status = 'rejected'::text) = (rejection_reason IS NOT NULL))),
    CONSTRAINT claim_status_check CHECK ((status = ANY (ARRAY['registered'::text, 'in_review'::text, 'approved'::text, 'paid'::text, 'rejected'::text, 'closed'::text])))
);


--
-- Name: claim_cession; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim_cession (
    claim_id uuid NOT NULL,
    treaty_id uuid NOT NULL,
    share_percent numeric(7,4) DEFAULT 0 NOT NULL,
    priority numeric(18,2),
    treaty_limit numeric(18,2),
    CONSTRAINT claim_cession_share_percent_check CHECK (((share_percent >= (0)::numeric) AND (share_percent <= (100)::numeric)))
);


--
-- Name: claim_payment; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim_payment (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    claim_id uuid NOT NULL,
    paid_on date NOT NULL,
    amount numeric(18,2) NOT NULL,
    deductible_withheld numeric(18,2) DEFAULT 0 NOT NULL,
    premium_offset numeric(18,2) DEFAULT 0 NOT NULL,
    net_paid numeric(18,2) NOT NULL,
    net_paid_gel numeric(18,2) NOT NULL,
    rate numeric(12,6) NOT NULL,
    payee_kind text NOT NULL,
    payee_party_id uuid,
    offset_payment_id uuid,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    kind text DEFAULT 'payment'::text NOT NULL,
    reverses_claim_payment_id uuid,
    reversal_reason text,
    returned_on date,
    bank_reference text,
    request_id uuid,
    CONSTRAINT claim_payment_bank_reference_check CHECK (((bank_reference IS NULL) OR ((btrim(bank_reference) <> ''::text) AND (char_length(bank_reference) <= 100)))),
    CONSTRAINT claim_payment_kind_check CHECK ((kind = ANY (ARRAY['payment'::text, 'reversal'::text]))),
    CONSTRAINT claim_payment_kind_shape CHECK ((((kind = 'payment'::text) AND (amount > (0)::numeric) AND (deductible_withheld >= (0)::numeric) AND (premium_offset >= (0)::numeric) AND (net_paid >= (0)::numeric) AND (reverses_claim_payment_id IS NULL) AND (reversal_reason IS NULL) AND (returned_on IS NULL) AND (bank_reference IS NULL)) OR ((kind = 'reversal'::text) AND (amount < (0)::numeric) AND (deductible_withheld <= (0)::numeric) AND (premium_offset <= (0)::numeric) AND (net_paid <= (0)::numeric) AND (reverses_claim_payment_id IS NOT NULL) AND (reversal_reason IS NOT NULL) AND ((reversal_reason = 'returned'::text) = (returned_on IS NOT NULL)) AND ((reversal_reason = 'returned'::text) = (bank_reference IS NOT NULL))))),
    CONSTRAINT claim_payment_payee_kind_check CHECK ((payee_kind = ANY (ARRAY['policyholder'::text, 'beneficiary'::text, 'workshop'::text, 'other'::text]))),
    CONSTRAINT claim_payment_rate_check CHECK ((rate > (0)::numeric)),
    CONSTRAINT claim_payment_reversal_reason_check CHECK ((reversal_reason = ANY (ARRAY['correction'::text, 'returned'::text])))
);


--
-- Name: claim_payment_request; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim_payment_request (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    claim_id uuid NOT NULL,
    paid_on date NOT NULL,
    amount numeric(18,2) NOT NULL,
    deductible_withheld numeric(18,2) NOT NULL,
    premium_offset numeric(18,2) NOT NULL,
    net_paid numeric(18,2) NOT NULL,
    net_paid_gel numeric(18,2) NOT NULL,
    rate numeric(12,6) NOT NULL,
    payee_kind text NOT NULL,
    payee_party_id uuid NOT NULL,
    offset_premium boolean NOT NULL,
    total_loss_mode text NOT NULL,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid NOT NULL,
    CONSTRAINT claim_payment_request_amount_check CHECK ((amount > (0)::numeric)),
    CONSTRAINT claim_payment_request_deductible_withheld_check CHECK ((deductible_withheld >= (0)::numeric)),
    CONSTRAINT claim_payment_request_net CHECK ((net_paid = ((amount - deductible_withheld) - premium_offset))),
    CONSTRAINT claim_payment_request_net_paid_check CHECK ((net_paid > (0)::numeric)),
    CONSTRAINT claim_payment_request_net_paid_gel_check CHECK ((net_paid_gel > (0)::numeric)),
    CONSTRAINT claim_payment_request_payee_kind_check CHECK ((payee_kind = ANY (ARRAY['policyholder'::text, 'beneficiary'::text, 'workshop'::text, 'other'::text]))),
    CONSTRAINT claim_payment_request_premium_offset_check CHECK ((premium_offset >= (0)::numeric)),
    CONSTRAINT claim_payment_request_rate_check CHECK ((rate > (0)::numeric)),
    CONSTRAINT claim_payment_request_total_loss_mode_check CHECK ((total_loss_mode = ANY (ARRAY['none'::text, 'earned_unpaid'::text, 'all_unpaid'::text])))
);


--
-- Name: claim_recovery; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim_recovery (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    claim_id uuid NOT NULL,
    kind text NOT NULL,
    received_on date NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    subrogation_case_id uuid,
    row_kind text DEFAULT 'receipt'::text NOT NULL,
    reverses_claim_recovery_id uuid,
    bank_reference text,
    CONSTRAINT claim_recovery_amount_sign CHECK ((((row_kind = 'receipt'::text) AND (amount_gel > (0)::numeric)) OR ((row_kind = 'reversal'::text) AND (amount_gel < (0)::numeric)))),
    CONSTRAINT claim_recovery_case_kind CHECK (((subrogation_case_id IS NULL) OR (kind = 'subrogation'::text))),
    CONSTRAINT claim_recovery_kind_check CHECK ((kind = ANY (ARRAY['subrogation'::text, 'salvage'::text]))),
    CONSTRAINT claim_recovery_reversal_target CHECK (((row_kind = 'reversal'::text) = (reverses_claim_recovery_id IS NOT NULL))),
    CONSTRAINT claim_recovery_row_kind_check CHECK ((row_kind = ANY (ARRAY['receipt'::text, 'reversal'::text])))
);


--
-- Name: claim_reserve; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim_reserve (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    claim_id uuid NOT NULL,
    booked_on date NOT NULL,
    change numeric(18,2) NOT NULL,
    reason text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    request_id uuid
);


--
-- Name: claim_reserve_request; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.claim_reserve_request (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    claim_id uuid NOT NULL,
    booked_on date NOT NULL,
    base_level numeric(18,2) NOT NULL,
    new_level numeric(18,2) NOT NULL,
    change numeric(18,2) NOT NULL,
    reason text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid NOT NULL,
    CONSTRAINT claim_reserve_request_change_check CHECK ((change <> (0)::numeric)),
    CONSTRAINT claim_reserve_request_difference CHECK ((change = (new_level - base_level))),
    CONSTRAINT claim_reserve_request_new_level_check CHECK ((new_level >= (0)::numeric)),
    CONSTRAINT claim_reserve_request_reason_check CHECK (((btrim(reason) <> ''::text) AND (char_length(reason) <= 500)))
);


--
-- Name: complaint; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.complaint (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    received_on date NOT NULL,
    channel text NOT NULL,
    party_id uuid,
    customer_name text NOT NULL,
    customer_contact text,
    policy_id uuid,
    claim_id uuid,
    category smallint NOT NULL,
    description text NOT NULL,
    status text DEFAULT 'open'::text NOT NULL,
    acknowledged_at timestamp with time zone,
    responded_on date,
    response_text text,
    measures text,
    outcome text,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT complaint_category_check CHECK (((category >= 1) AND (category <= 4))),
    CONSTRAINT complaint_channel_check CHECK ((channel = ANY (ARRAY['written'::text, 'electronic'::text, 'oral'::text]))),
    CONSTRAINT complaint_response CHECK (((status = 'open'::text) OR ((responded_on IS NOT NULL) AND (response_text IS NOT NULL)))),
    CONSTRAINT complaint_status_check CHECK ((status = ANY (ARRAY['open'::text, 'upheld'::text, 'partly_upheld'::text, 'rejected'::text, 'upheld_other'::text])))
);


--
-- Name: contract; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.contract (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    signed_on date NOT NULL,
    policyholder_id uuid NOT NULL,
    branch_id uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid
);


--
-- Name: currency; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.currency (
    code character(3) NOT NULL,
    name_ka text NOT NULL,
    minor_units smallint DEFAULT 2 NOT NULL
);


--
-- Name: exchange_rate; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.exchange_rate (
    currency character(3) NOT NULL,
    rate_date date NOT NULL,
    rate numeric(12,6) NOT NULL,
    CONSTRAINT exchange_rate_rate_check CHECK ((rate > (0)::numeric))
);


--
-- Name: fms_suspension; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.fms_suspension (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    party_id uuid NOT NULL,
    case_id uuid,
    kind text NOT NULL,
    scope text DEFAULT 'all'::text NOT NULL,
    transaction_ref text,
    instructed_at timestamp with time zone NOT NULL,
    written_confirmed_at timestamp with time zone,
    ends_at timestamp with time zone NOT NULL,
    lifted_at timestamp with time zone,
    lift_reason text,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    target_policy_id uuid,
    target_claim_id uuid,
    legacy_unmapped boolean DEFAULT false NOT NULL,
    CONSTRAINT fms_suspension_kind_check CHECK ((kind = ANY (ARRAY['oral'::text, 'written'::text]))),
    CONSTRAINT fms_suspension_scope_check CHECK ((scope = ANY (ARRAY['all'::text, 'transaction'::text]))),
    CONSTRAINT fms_suspension_target CHECK (((scope = 'all'::text) OR (target_policy_id IS NOT NULL) OR (target_claim_id IS NOT NULL) OR legacy_unmapped))
);


--
-- Name: fms_suspension_untargeted; Type: VIEW; Schema: core; Owner: -
--

CREATE VIEW core.fms_suspension_untargeted AS
 SELECT id,
    party_id,
    instructed_at,
    transaction_ref,
    lifted_at
   FROM core.fms_suspension s
  WHERE legacy_unmapped;


--
-- Name: installment; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.installment (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    policy_id uuid NOT NULL,
    due_on date NOT NULL,
    amount numeric(18,2) NOT NULL,
    debtor_kind text DEFAULT 'client'::text NOT NULL,
    CONSTRAINT installment_debtor_kind_check CHECK ((debtor_kind = ANY (ARRAY['client'::text, 'broker'::text, 'agent'::text])))
);


--
-- Name: installment_adjustment; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.installment_adjustment (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    installment_id uuid NOT NULL,
    booked_on date NOT NULL,
    change numeric(18,2) NOT NULL,
    reason text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid
);


--
-- Name: insurance_class; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.insurance_class (
    code text NOT NULL,
    parent_code text,
    name_ka text NOT NULL,
    category text NOT NULL,
    ibnr_percent numeric(5,2) NOT NULL,
    sort_order integer NOT NULL,
    CONSTRAINT insurance_class_category_check CHECK ((category = ANY (ARRAY['life'::text, 'non_life'::text])))
);


--
-- Name: insured_object; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.insured_object (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    policy_id uuid NOT NULL,
    object_type text NOT NULL,
    description text NOT NULL,
    attributes jsonb DEFAULT '{}'::jsonb NOT NULL
);


--
-- Name: intermediary; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.intermediary (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    party_id uuid NOT NULL,
    kind text NOT NULL,
    contract_no text,
    contract_date date,
    valid_to date,
    status text DEFAULT 'active'::text NOT NULL,
    commission_percent numeric(5,2),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT intermediary_commission_percent_check CHECK (((commission_percent >= (0)::numeric) AND (commission_percent <= (100)::numeric))),
    CONSTRAINT intermediary_kind_check CHECK ((kind = ANY (ARRAY['agent'::text, 'broker'::text, 'bank'::text, 'employee'::text]))),
    CONSTRAINT intermediary_status_check CHECK ((status = ANY (ARRAY['active'::text, 'terminated'::text])))
);


--
-- Name: login_attempt; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.login_attempt (
    id bigint NOT NULL,
    username text NOT NULL,
    source text,
    attempted_at timestamp with time zone DEFAULT now() NOT NULL,
    succeeded boolean NOT NULL
);


--
-- Name: login_attempt_id_seq; Type: SEQUENCE; Schema: core; Owner: -
--

ALTER TABLE core.login_attempt ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME core.login_attempt_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: loss_event; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.loss_event (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    policy_id uuid NOT NULL,
    occurred_on date NOT NULL,
    occurred_place text,
    reported_on date NOT NULL,
    description text NOT NULL,
    request_key uuid,
    source_ref text,
    source_batch text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT loss_event_dates CHECK ((reported_on >= occurred_on))
);


--
-- Name: number_sequence; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.number_sequence (
    code text NOT NULL,
    prefix text NOT NULL,
    next_value bigint DEFAULT 1 NOT NULL,
    padding integer DEFAULT 6 NOT NULL
);


--
-- Name: party; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.party (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    kind text NOT NULL,
    is_resident boolean DEFAULT true NOT NULL,
    tax_id text,
    foreign_id text,
    country_code character(2) DEFAULT 'GE'::bpchar NOT NULL,
    first_name text,
    last_name text,
    birth_date date,
    gender text,
    name text,
    legal_form text,
    state_share_percent numeric(5,2),
    is_public_entity boolean DEFAULT false NOT NULL,
    registration_place text,
    legal_address text,
    actual_address text,
    phone text,
    email text,
    risk_level text,
    identified_at date,
    verified_at date,
    is_pep boolean DEFAULT false NOT NULL,
    sanctions_checked_at date,
    is_group_member boolean DEFAULT false NOT NULL,
    display_name text GENERATED ALWAYS AS (
CASE
    WHEN (kind = 'person'::text) THEN TRIM(BOTH FROM ((COALESCE(last_name, ''::text) || ' '::text) || COALESCE(first_name, ''::text)))
    ELSE name
END) STORED,
    segment text GENERATED ALWAYS AS (
CASE
    WHEN (kind = 'person'::text) THEN 'retail'::text
    WHEN (is_public_entity OR (COALESCE(state_share_percent, (0)::numeric) >= (50)::numeric)) THEN 'state'::text
    ELSE 'corporate'::text
END) STORED,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    risk_assessed_on date,
    risk_review_on date,
    self_declaration_on date,
    sanctions_blocked boolean DEFAULT false NOT NULL,
    CONSTRAINT party_gender_check CHECK ((gender = ANY (ARRAY['male'::text, 'female'::text]))),
    CONSTRAINT party_identifier CHECK (((tax_id IS NOT NULL) OR (foreign_id IS NOT NULL))),
    CONSTRAINT party_kind_check CHECK ((kind = ANY (ARRAY['person'::text, 'organization'::text]))),
    CONSTRAINT party_organization_fields CHECK (((kind <> 'organization'::text) OR (name IS NOT NULL))),
    CONSTRAINT party_person_fields CHECK (((kind <> 'person'::text) OR ((first_name IS NOT NULL) AND (last_name IS NOT NULL)))),
    CONSTRAINT party_risk_level_check CHECK ((risk_level = ANY (ARRAY['low'::text, 'medium'::text, 'high'::text]))),
    CONSTRAINT party_state_share_percent_check CHECK (((state_share_percent >= (0)::numeric) AND (state_share_percent <= (100)::numeric))),
    CONSTRAINT party_tax_id_format CHECK (((tax_id IS NULL) OR ((kind = 'person'::text) AND (tax_id ~ '^[0-9]{11}$'::text)) OR ((kind = 'organization'::text) AND (tax_id ~ '^[0-9]{9}$'::text))))
);


--
-- Name: party_beneficial_owner; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.party_beneficial_owner (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    party_id uuid NOT NULL,
    first_name text NOT NULL,
    last_name text NOT NULL,
    birth_date date,
    personal_number text,
    document_number text,
    document_issued_on date,
    document_country character(2),
    document_issuer text,
    document_valid_to date,
    citizenship character(2),
    birth_place text,
    criterion text NOT NULL,
    share_percent numeric(5,2),
    is_direct boolean DEFAULT true NOT NULL,
    is_nominee boolean DEFAULT false NOT NULL,
    is_pep boolean DEFAULT false NOT NULL,
    identified_at date,
    verified_at date,
    verification_source text,
    sanctions_checked_at date,
    note text,
    valid_to date,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT party_beneficial_owner_criterion_check CHECK ((criterion = ANY (ARRAY['share'::text, 'control'::text, 'executive'::text]))),
    CONSTRAINT party_beneficial_owner_share_percent_check CHECK (((share_percent >= (0)::numeric) AND (share_percent <= (100)::numeric)))
);


--
-- Name: party_risk_assessment; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.party_risk_assessment (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    party_id uuid NOT NULL,
    assessed_on date NOT NULL,
    computed_level text NOT NULL,
    level text NOT NULL,
    score integer NOT NULL,
    factors jsonb DEFAULT '[]'::jsonb NOT NULL,
    is_override boolean DEFAULT false NOT NULL,
    reason text,
    review_due_on date NOT NULL,
    assessed_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT party_risk_assessment_computed_level_check CHECK ((computed_level = ANY (ARRAY['low'::text, 'medium'::text, 'high'::text]))),
    CONSTRAINT party_risk_assessment_level_check CHECK ((level = ANY (ARRAY['low'::text, 'medium'::text, 'high'::text]))),
    CONSTRAINT party_risk_override_reason CHECK (((NOT is_override) OR (reason IS NOT NULL)))
);


--
-- Name: payment; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.payment (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    received_on date NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    method text NOT NULL,
    payer_party_id uuid,
    reference text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    kind text DEFAULT 'receipt'::text NOT NULL,
    reverses_payment_id uuid,
    refund_request_id uuid,
    reversal_request_id uuid,
    CONSTRAINT payment_amount_sign CHECK ((((kind = 'receipt'::text) AND (amount_gel > (0)::numeric)) OR ((kind = ANY (ARRAY['reversal'::text, 'refund'::text])) AND (amount_gel < (0)::numeric)))),
    CONSTRAINT payment_kind_check CHECK ((kind = ANY (ARRAY['receipt'::text, 'reversal'::text, 'refund'::text]))),
    CONSTRAINT payment_method_check CHECK ((method = ANY (ARRAY['bank'::text, 'cash'::text, 'online'::text, 'claim_offset'::text, 'commission_offset'::text]))),
    CONSTRAINT payment_reversal_target CHECK (((kind = 'reversal'::text) = (reverses_payment_id IS NOT NULL)))
);


--
-- Name: payment_allocation; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.payment_allocation (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    payment_id uuid NOT NULL,
    installment_id uuid NOT NULL,
    amount numeric(18,2) NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    rate numeric(12,6) NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    booked_on date DEFAULT CURRENT_DATE NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid,
    CONSTRAINT payment_allocation_rate_check CHECK ((rate > (0)::numeric))
);


--
-- Name: policy; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.policy (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    contract_id uuid NOT NULL,
    number text NOT NULL,
    issued_on date NOT NULL,
    issue_place text,
    replaces_policy_id uuid,
    effective_from date NOT NULL,
    effective_to date NOT NULL,
    status text DEFAULT 'draft'::text NOT NULL,
    cancelled_on date,
    cancel_reason text,
    premium_currency character(3) NOT NULL,
    premium_rate numeric(12,6) NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    cancel_grace_days integer,
    product_id uuid,
    segment text NOT NULL,
    approved_at timestamp with time zone,
    approved_by uuid,
    change_note text,
    CONSTRAINT policy_active_is_approved CHECK (((status <> 'active'::text) OR (approved_at IS NOT NULL))),
    CONSTRAINT policy_cancel_grace_days_check CHECK ((cancel_grace_days >= 0)),
    CONSTRAINT policy_cancel_status CHECK (((status = 'cancelled'::text) = (cancelled_on IS NOT NULL))),
    CONSTRAINT policy_cancel_within_term CHECK (((cancelled_on IS NULL) OR ((cancelled_on >= effective_from) AND (cancelled_on <= effective_to)))),
    CONSTRAINT policy_premium_rate_check CHECK ((premium_rate > (0)::numeric)),
    CONSTRAINT policy_segment_check CHECK ((segment = ANY (ARRAY['retail'::text, 'corporate'::text, 'state'::text]))),
    CONSTRAINT policy_status_check CHECK ((status = ANY (ARRAY['draft'::text, 'pending_approval'::text, 'active'::text, 'cancelled'::text]))),
    CONSTRAINT policy_term CHECK ((effective_to > effective_from))
);


--
-- Name: policy_intermediary; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.policy_intermediary (
    policy_id uuid NOT NULL,
    intermediary_id uuid NOT NULL,
    commission_percent numeric(5,2) NOT NULL,
    CONSTRAINT policy_intermediary_commission_percent_check CHECK (((commission_percent >= (0)::numeric) AND (commission_percent <= (100)::numeric)))
);


--
-- Name: policy_line; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.policy_line (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    policy_id uuid NOT NULL,
    insured_object_id uuid,
    class_code text NOT NULL,
    sum_insured numeric(18,2),
    sum_insured_currency character(3),
    deductible numeric(18,2),
    deductible_percent numeric(5,2),
    premium numeric(18,2) NOT NULL,
    premium_gel numeric(18,2) NOT NULL,
    is_fully_reinsured boolean DEFAULT false NOT NULL,
    product_coverage_id uuid,
    CONSTRAINT policy_line_deductible_percent_check CHECK (((deductible_percent >= (0)::numeric) AND (deductible_percent <= (100)::numeric)))
);


--
-- Name: policy_party; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.policy_party (
    policy_id uuid NOT NULL,
    party_id uuid NOT NULL,
    role text NOT NULL,
    CONSTRAINT policy_party_role_check CHECK ((role = ANY (ARRAY['insured'::text, 'beneficiary'::text, 'driver'::text, 'representative'::text])))
);


--
-- Name: premium_cession; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.premium_cession (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    premium_transaction_id uuid NOT NULL,
    treaty_id uuid NOT NULL,
    share_percent numeric(7,4) NOT NULL,
    amount numeric(18,2) NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    commission numeric(18,2) DEFAULT 0 NOT NULL,
    commission_gel numeric(18,2) DEFAULT 0 NOT NULL,
    booked_on date NOT NULL,
    cover_from date NOT NULL,
    cover_to date NOT NULL,
    cancels_cession_id uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT premium_cession_share_percent_check CHECK (((share_percent >= (0)::numeric) AND (share_percent <= (100)::numeric)))
);


--
-- Name: premium_refund_request; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.premium_refund_request (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    policy_id uuid NOT NULL,
    refunded_on date NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    currency text NOT NULL,
    rate numeric(12,6) NOT NULL,
    method text NOT NULL,
    reference text,
    payer_party_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid NOT NULL,
    CONSTRAINT premium_refund_request_amount_gel_check CHECK ((amount_gel > (0)::numeric)),
    CONSTRAINT premium_refund_request_method_check CHECK ((method = ANY (ARRAY['bank'::text, 'cash'::text, 'online'::text]))),
    CONSTRAINT premium_refund_request_rate_check CHECK ((rate > (0)::numeric))
);


--
-- Name: premium_refund_request_line; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.premium_refund_request_line (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    request_id uuid NOT NULL,
    "position" integer NOT NULL,
    installment_id uuid NOT NULL,
    amount numeric(18,2) NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    CONSTRAINT premium_refund_request_line_amount_check CHECK ((amount > (0)::numeric)),
    CONSTRAINT premium_refund_request_line_amount_gel_check CHECK ((amount_gel >= (0)::numeric)),
    CONSTRAINT premium_refund_request_line_position_check CHECK (("position" > 0))
);


--
-- Name: premium_transaction; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.premium_transaction (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    policy_line_id uuid NOT NULL,
    kind text NOT NULL,
    amount numeric(18,2) NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    booked_on date NOT NULL,
    cover_from date NOT NULL,
    cover_to date NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    cancels_transaction_id uuid,
    CONSTRAINT premium_transaction_cover CHECK ((cover_to > cover_from)),
    CONSTRAINT premium_transaction_kind_check CHECK ((kind = ANY (ARRAY['issue'::text, 'cancellation'::text, 'correction'::text])))
);


--
-- Name: product; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.product (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code text NOT NULL,
    name_ka text NOT NULL,
    object_type text NOT NULL,
    is_active boolean DEFAULT true NOT NULL
);


--
-- Name: product_coverage; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.product_coverage (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    product_id uuid NOT NULL,
    code text NOT NULL,
    name_ka text NOT NULL,
    class_code text NOT NULL,
    is_mandatory boolean DEFAULT false NOT NULL,
    sort_order integer NOT NULL
);


--
-- Name: product_text; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.product_text (
    product_id uuid NOT NULL,
    key text NOT NULL,
    value text NOT NULL
);


--
-- Name: receipt_reversal_request; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.receipt_reversal_request (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    receipt_id uuid NOT NULL,
    policy_id uuid NOT NULL,
    reversed_on date NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    method text NOT NULL,
    payer_party_id uuid,
    reason text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid NOT NULL,
    CONSTRAINT receipt_reversal_request_amount_gel_check CHECK ((amount_gel > (0)::numeric)),
    CONSTRAINT receipt_reversal_request_method_check CHECK ((method = ANY (ARRAY['bank'::text, 'cash'::text, 'online'::text]))),
    CONSTRAINT receipt_reversal_request_reason_check CHECK ((btrim(reason) <> ''::text))
);


--
-- Name: receipt_reversal_request_line; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.receipt_reversal_request_line (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    request_id uuid NOT NULL,
    "position" integer NOT NULL,
    installment_id uuid NOT NULL,
    amount numeric(18,2) NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    rate numeric(12,6) NOT NULL,
    CONSTRAINT receipt_reversal_request_line_position_check CHECK (("position" > 0)),
    CONSTRAINT receipt_reversal_request_line_rate_check CHECK ((rate > (0)::numeric))
);


--
-- Name: ri_treaty; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.ri_treaty (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    name text NOT NULL,
    reinsurer_party_id uuid NOT NULL,
    broker_party_id uuid,
    kind text NOT NULL,
    period_from date NOT NULL,
    period_to date NOT NULL,
    currency character(3) NOT NULL,
    ceded_percent numeric(7,4),
    retention numeric(18,2),
    lines integer,
    priority numeric(18,2),
    treaty_limit numeric(18,2),
    treaty_premium numeric(18,2),
    commission_percent numeric(7,4) DEFAULT 0 NOT NULL,
    broker_percent numeric(7,4) DEFAULT 0 NOT NULL,
    reinsurer_rating text,
    rating_agency text,
    status text DEFAULT 'active'::text NOT NULL,
    note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ri_treaty_broker_percent_check CHECK (((broker_percent >= (0)::numeric) AND (broker_percent <= (100)::numeric))),
    CONSTRAINT ri_treaty_ceded_percent_check CHECK (((ceded_percent >= (0)::numeric) AND (ceded_percent <= (100)::numeric))),
    CONSTRAINT ri_treaty_commission_percent_check CHECK (((commission_percent >= (0)::numeric) AND (commission_percent <= (100)::numeric))),
    CONSTRAINT ri_treaty_kind_check CHECK ((kind = ANY (ARRAY['quota_share'::text, 'surplus'::text, 'excess_of_loss'::text, 'facultative'::text]))),
    CONSTRAINT ri_treaty_lines_check CHECK ((lines >= 0)),
    CONSTRAINT ri_treaty_period CHECK ((period_to >= period_from)),
    CONSTRAINT ri_treaty_priority_check CHECK ((priority >= (0)::numeric)),
    CONSTRAINT ri_treaty_retention_check CHECK ((retention >= (0)::numeric)),
    CONSTRAINT ri_treaty_status_check CHECK ((status = ANY (ARRAY['active'::text, 'closed'::text]))),
    CONSTRAINT ri_treaty_terms CHECK ((((kind = ANY (ARRAY['quota_share'::text, 'facultative'::text])) AND (ceded_percent IS NOT NULL)) OR ((kind = 'surplus'::text) AND (retention IS NOT NULL) AND (lines IS NOT NULL)) OR ((kind = 'excess_of_loss'::text) AND (priority IS NOT NULL)))),
    CONSTRAINT ri_treaty_treaty_limit_check CHECK ((treaty_limit >= (0)::numeric)),
    CONSTRAINT ri_treaty_treaty_premium_check CHECK ((treaty_premium >= (0)::numeric))
);


--
-- Name: ri_treaty_class; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.ri_treaty_class (
    treaty_id uuid NOT NULL,
    class_code text NOT NULL
);


--
-- Name: role; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.role (
    code text NOT NULL,
    name_ka text NOT NULL
);


--
-- Name: sanction_list_entry; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.sanction_list_entry (
    source text NOT NULL,
    data_id text NOT NULL,
    kind text NOT NULL,
    name text NOT NULL,
    aliases text[] DEFAULT '{}'::text[] NOT NULL,
    list_type text,
    reference text,
    listed_on date,
    nationalities text[] DEFAULT '{}'::text[] NOT NULL,
    birth_years text[] DEFAULT '{}'::text[] NOT NULL,
    documents text[] DEFAULT '{}'::text[] NOT NULL,
    CONSTRAINT sanction_list_entry_kind_check CHECK ((kind = ANY (ARRAY['individual'::text, 'entity'::text])))
);


--
-- Name: sanction_list_version; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.sanction_list_version (
    source text NOT NULL,
    generated_at timestamp with time zone,
    loaded_at timestamp with time zone DEFAULT now() NOT NULL,
    entries integer NOT NULL
);


--
-- Name: sanctions_screening; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.sanctions_screening (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    subject_kind text NOT NULL,
    subject_id uuid NOT NULL,
    party_id uuid NOT NULL,
    screened_at timestamp with time zone DEFAULT now() NOT NULL,
    list_generated_at timestamp with time zone,
    matches jsonb DEFAULT '[]'::jsonb NOT NULL,
    outcome text NOT NULL,
    decided_by uuid,
    decided_at timestamp with time zone,
    note text,
    created_by uuid,
    CONSTRAINT sanctions_screening_outcome_check CHECK ((outcome = ANY (ARRAY['clear'::text, 'possible'::text, 'confirmed'::text, 'false_positive'::text]))),
    CONSTRAINT sanctions_screening_subject_kind_check CHECK ((subject_kind = ANY (ARRAY['party'::text, 'owner'::text])))
);


--
-- Name: schedule_reconciliation; Type: VIEW; Schema: core; Owner: -
--

CREATE VIEW core.schedule_reconciliation AS
 WITH schedule AS (
         SELECT i.policy_id,
            (sum(i.amount) + COALESCE(sum(j.change), (0)::numeric)) AS scheduled
           FROM (core.installment i
             LEFT JOIN ( SELECT installment_adjustment.installment_id,
                    sum(installment_adjustment.change) AS change
                   FROM core.installment_adjustment
                  GROUP BY installment_adjustment.installment_id) j ON ((j.installment_id = i.id)))
          GROUP BY i.policy_id
        ), ledger AS (
         SELECT l.policy_id,
            sum(t.amount) AS amount
           FROM (core.premium_transaction t
             JOIN core.policy_line l ON ((l.id = t.policy_line_id)))
          GROUP BY l.policy_id
        ), reductions AS (
         SELECT c.replaces_policy_id AS policy_id,
            sum(lg_1.amount) AS amount
           FROM (core.policy c
             JOIN ledger lg_1 ON ((lg_1.policy_id = c.id)))
          WHERE ((c.replaces_policy_id IS NOT NULL) AND (lg_1.amount < (0)::numeric) AND (NOT (EXISTS ( SELECT 1
                   FROM core.installment i
                  WHERE (i.policy_id = c.id)))))
          GROUP BY c.replaces_policy_id
        )
 SELECT p.id AS policy_id,
    p.number,
    p.status,
    s.scheduled,
    (COALESCE(lg.amount, (0)::numeric) + COALESCE(r.amount, (0)::numeric)) AS approved_premium,
    (s.scheduled - (COALESCE(lg.amount, (0)::numeric) + COALESCE(r.amount, (0)::numeric))) AS difference
   FROM (((schedule s
     JOIN core.policy p ON ((p.id = s.policy_id)))
     LEFT JOIN ledger lg ON ((lg.policy_id = p.id)))
     LEFT JOIN reductions r ON ((r.policy_id = p.id)))
  WHERE (p.status = ANY (ARRAY['active'::text, 'cancelled'::text]));


--
-- Name: schema_migration; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.schema_migration (
    name text NOT NULL,
    applied_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: session; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.session (
    token_hash text NOT NULL,
    user_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL
);


--
-- Name: setting; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.setting (
    key text NOT NULL,
    value text NOT NULL
);


--
-- Name: subrogation_case; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.subrogation_case (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    number text NOT NULL,
    claim_id uuid NOT NULL,
    status text DEFAULT 'opened'::text NOT NULL,
    opened_on date NOT NULL,
    basis_kind text,
    basis_number text,
    basis_date date,
    note text,
    request_key uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid,
    CONSTRAINT subrogation_case_basis CHECK ((((basis_kind IS NULL) AND (basis_number IS NULL) AND (basis_date IS NULL)) OR ((basis_kind IS NOT NULL) AND (basis_number IS NOT NULL) AND (basis_date IS NOT NULL)))),
    CONSTRAINT subrogation_case_basis_kind_check CHECK ((basis_kind = ANY (ARRAY['police_report'::text, 'court_decision'::text, 'admission'::text]))),
    CONSTRAINT subrogation_case_status_check CHECK ((status = ANY (ARRAY['opened'::text, 'evidence_confirmed'::text, 'demand_sent'::text, 'accepted'::text, 'disputed'::text, 'rejected'::text, 'partially_received'::text, 'closed'::text])))
);


--
-- Name: subrogation_movement; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.subrogation_movement (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    case_id uuid NOT NULL,
    kind text NOT NULL,
    booked_on date NOT NULL,
    amount_gel numeric(18,2) NOT NULL,
    reverses_movement_id uuid,
    claim_recovery_id uuid,
    payer_id uuid,
    document_kind text NOT NULL,
    document_number text,
    document_date date,
    note text,
    request_key uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid,
    CONSTRAINT subrogation_movement_court CHECK (((document_kind <> 'court_decision'::text) OR ((document_number IS NOT NULL) AND (document_date IS NOT NULL)))),
    CONSTRAINT subrogation_movement_document CHECK ((((kind = 'claimed'::text) AND (document_kind = 'demand_letter'::text)) OR ((kind = 'confirmed'::text) AND (document_kind = ANY (ARRAY['debtor_acceptance'::text, 'court_decision'::text]))) OR ((kind = 'received'::text) AND (document_kind = 'bank_statement'::text)) OR ((kind = 'reversal'::text) AND (document_kind = 'correction'::text) AND (note IS NOT NULL)) OR ((kind = 'cancelled'::text) AND (document_kind = 'court_decision'::text)))),
    CONSTRAINT subrogation_movement_document_kind_check CHECK ((document_kind = ANY (ARRAY['demand_letter'::text, 'debtor_acceptance'::text, 'court_decision'::text, 'bank_statement'::text, 'correction'::text]))),
    CONSTRAINT subrogation_movement_kind_check CHECK ((kind = ANY (ARRAY['claimed'::text, 'confirmed'::text, 'received'::text, 'cancelled'::text, 'reversal'::text]))),
    CONSTRAINT subrogation_movement_money CHECK ((((kind = 'received'::text) AND (claim_recovery_id IS NOT NULL) AND (payer_id IS NOT NULL)) OR ((kind = ANY (ARRAY['claimed'::text, 'confirmed'::text, 'cancelled'::text])) AND (claim_recovery_id IS NULL) AND (payer_id IS NULL)) OR ((kind = 'reversal'::text) AND (payer_id IS NULL)))),
    CONSTRAINT subrogation_movement_sign CHECK ((((kind = 'reversal'::text) AND (amount_gel < (0)::numeric)) OR ((kind <> 'reversal'::text) AND (amount_gel > (0)::numeric)))),
    CONSTRAINT subrogation_movement_target CHECK (((kind = 'reversal'::text) = (reverses_movement_id IS NOT NULL)))
);


--
-- Name: subrogation_party; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.subrogation_party (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    case_id uuid NOT NULL,
    party_id uuid NOT NULL,
    role text NOT NULL,
    policy_number text,
    coverage text,
    limit_gel numeric(18,2),
    response text,
    response_on date,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid DEFAULT (NULLIF(current_setting('core.user_id'::text, true), ''::text))::uuid,
    CONSTRAINT subrogation_party_insurer_details CHECK (((role = 'liability_insurer'::text) OR ((policy_number IS NULL) AND (coverage IS NULL) AND (limit_gel IS NULL)))),
    CONSTRAINT subrogation_party_limit_gel_check CHECK ((limit_gel > (0)::numeric)),
    CONSTRAINT subrogation_party_response CHECK (((response IS NULL) = (response_on IS NULL))),
    CONSTRAINT subrogation_party_response_check CHECK ((response = ANY (ARRAY['accepted'::text, 'disputed'::text, 'rejected'::text]))),
    CONSTRAINT subrogation_party_role_check CHECK ((role = ANY (ARRAY['liable_person'::text, 'liability_insurer'::text])))
);


--
-- Name: user_group; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.user_group (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT user_group_name_check CHECK (((btrim(name) <> ''::text) AND (name = btrim(name)) AND (char_length(name) <= 100)))
);


--
-- Name: user_group_member; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.user_group_member (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    group_id uuid NOT NULL,
    user_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: user_group_permission; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.user_group_permission (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    group_id uuid NOT NULL,
    permission text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT user_group_permission_permission_check CHECK ((permission ~ '^[a-z_]+(\.[a-z_]+)+$'::text))
);


--
-- Name: user_permission; Type: TABLE; Schema: core; Owner: -
--

CREATE TABLE core.user_permission (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    permission text NOT NULL,
    decision text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT user_permission_decision_check CHECK ((decision = ANY (ARRAY['allow'::text, 'deny'::text]))),
    CONSTRAINT user_permission_permission_check CHECK ((permission ~ '^[a-z_]+(\.[a-z_]+)+$'::text))
);


--
-- Data for Name: accounting_period; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.accounting_period (period_start, status, closed_at, closed_by) FROM stdin;
2026-01-01	closed	2026-10-22 23:42:10.957907+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
2026-02-01	closed	2026-10-22 23:42:10.973412+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
2026-03-01	closed	2026-10-22 23:42:10.985647+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
\.


--
-- Data for Name: aml_case; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.aml_case (id, number, party_id, opened_on, source, description, transaction_ref, class_code, status, examined_on, examination, suspicion_at, str_sent_at, str_type, str_other_basis, fms_reference, closed_on, note, created_at, created_by) FROM stdin;
d8648d25-64a5-4bd9-938b-bbbd395a1faa	AML000001	86b38924-7dc9-4d1d-864a-292366679667	2026-08-10	monitoring	უჩვეულოდ დიდი ნაღდი შენატანი	\N	5.1	open	\N	\N	\N	\N	\N	\N	\N	\N	\N	2026-10-22 23:42:10.308687+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
\.


--
-- Data for Name: app_user; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.app_user (id, username, full_name, password_hash, role_code, branch_id, is_active, created_at) FROM stdin;
deb1b458-9189-473e-b86e-047bb138fa0a	world.admin	ადმინი ტესტაძე	scrypt$ee231e7fa9ff4e058f30be5d6d78943a$85c19dd6cdb9c8e0364102b398755681635c3f89c7f16e66158e91564d8f39786d27fec5adb4ce26b6ee048bcd5e604c6276bf50c1a152d1958cd841154bf442	admin	a715a88c-9080-470a-8826-c682a446d758	t	2026-10-22 23:42:09.029467+00
f811b716-2a18-4fe9-b792-fb90f6665f39	world.head	უფროსი ტესტაძე	scrypt$e3056a9acaf1c9b50e2b485f02463c7a$36400028cffd016ecaa88095b0e438bf908ef548edb511318e7d86cc2739153b95f3a04a6c9a255d7bdac1cc2c8b91f02bc7fbd587787708c94c47ff2e696813	operations_head	a715a88c-9080-470a-8826-c682a446d758	t	2026-10-22 23:42:09.101719+00
e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	world.accountant	ბუღალტერი ტესტაძე	scrypt$32feb41d8b2fad892d5f7dd7480c5391$79c7784da529605c5031d06a5d1b0a78261d02d7ef4e0c3dc64d912ed9319175b28d7fa7739d0806bfaa0c34dcff31ce362f136f94b19e171c5c60c27d342c2d	accountant	a715a88c-9080-470a-8826-c682a446d758	t	2026-10-22 23:42:09.181185+00
31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	world.sales	გაყიდვები ტესტაძე	scrypt$303a63540829b67a6d8eba2e6464e1a2$c46a2d08cbd21c3da04802c250b6b0e10d5b0002bfd8e532c72703a53c9db6bbbde35ff18d27c7732eb610ddb05eaa8fd0f4964ff7eefe2af922cf50c022d3a8	sales	a715a88c-9080-470a-8826-c682a446d758	t	2026-10-22 23:42:09.244202+00
d85d4e8c-e844-40f2-9e9f-ae180d288ab2	world.claims	ზარალები ტესტაძე	scrypt$f2bfc9ff5c6fac56299204e735895c08$f9f4731e6fadbfefae917b650bdf22f45bea894cd455e3fe8b7e82eab13b24e31fbb0de65f2297e8c47af52f21a75fa8de0e5b9744542069f9f2c66f9e28dada	claims	a715a88c-9080-470a-8826-c682a446d758	t	2026-10-22 23:42:09.311615+00
741d7861-3c7d-417c-8c9c-4e7d6def58f6	world.viewer	მნახველი ტესტაძე	scrypt$ff2dfd00fa40ef3a4f37ba9ec3e34350$5f4930410d264137325ad8b6a02a9d75d9788818c5f3de48ed33665eb8508d4ec074afe92be545ae9dd9805960f7996f5d7e5ee45e5ce3e4a319dda456622f07	viewer	a715a88c-9080-470a-8826-c682a446d758	t	2026-10-22 23:42:09.366673+00
\.


--
-- Data for Name: audit_log; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.audit_log (id, at, user_id, table_name, row_id, action, old_row, new_row) FROM stdin;
1	2026-10-22 23:42:08.802775+00	\N	number_sequence	subrogation	insert	\N	{"code": "subrogation", "prefix": "RG", "padding": 6, "next_value": 1}
2	2026-10-22 23:42:08.869557+00	\N	setting	payment.fd_threshold_gel	insert	\N	{"key": "payment.fd_threshold_gel", "value": "10000.00"}
3	2026-10-22 23:42:09.029467+00	\N	app_user	deb1b458-9189-473e-b86e-047bb138fa0a	insert	\N	{"id": "deb1b458-9189-473e-b86e-047bb138fa0a", "username": "world.admin", "branch_id": "a715a88c-9080-470a-8826-c682a446d758", "full_name": "ადმინი ტესტაძე", "is_active": true, "role_code": "admin", "created_at": "2026-10-22T23:42:09.029467+00:00"}
4	2026-10-22 23:42:09.101719+00	\N	app_user	f811b716-2a18-4fe9-b792-fb90f6665f39	insert	\N	{"id": "f811b716-2a18-4fe9-b792-fb90f6665f39", "username": "world.head", "branch_id": "a715a88c-9080-470a-8826-c682a446d758", "full_name": "უფროსი ტესტაძე", "is_active": true, "role_code": "operations_head", "created_at": "2026-10-22T23:42:09.101719+00:00"}
5	2026-10-22 23:42:09.181185+00	\N	app_user	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	insert	\N	{"id": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "username": "world.accountant", "branch_id": "a715a88c-9080-470a-8826-c682a446d758", "full_name": "ბუღალტერი ტესტაძე", "is_active": true, "role_code": "accountant", "created_at": "2026-10-22T23:42:09.181185+00:00"}
6	2026-10-22 23:42:09.244202+00	\N	app_user	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insert	\N	{"id": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "username": "world.sales", "branch_id": "a715a88c-9080-470a-8826-c682a446d758", "full_name": "გაყიდვები ტესტაძე", "is_active": true, "role_code": "sales", "created_at": "2026-10-22T23:42:09.244202+00:00"}
7	2026-10-22 23:42:09.311615+00	\N	app_user	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	insert	\N	{"id": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "username": "world.claims", "branch_id": "a715a88c-9080-470a-8826-c682a446d758", "full_name": "ზარალები ტესტაძე", "is_active": true, "role_code": "claims", "created_at": "2026-10-22T23:42:09.311615+00:00"}
8	2026-10-22 23:42:09.366673+00	\N	app_user	741d7861-3c7d-417c-8c9c-4e7d6def58f6	insert	\N	{"id": "741d7861-3c7d-417c-8c9c-4e7d6def58f6", "username": "world.viewer", "branch_id": "a715a88c-9080-470a-8826-c682a446d758", "full_name": "მნახველი ტესტაძე", "is_active": true, "role_code": "viewer", "created_at": "2026-10-22T23:42:09.366673+00:00"}
9	2026-10-22 23:42:09.66571+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	f8194c08-4581-429c-863c-56a54b6e90e0	insert	\N	{"id": "f8194c08-4581-429c-863c-56a54b6e90e0", "kind": "person", "name": null, "email": "01001010101@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010101", "segment": "retail", "last_name": "ტესტიშვილი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.66571+00:00", "first_name": "გიორგი", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.66571+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ტესტიშვილი გიორგი", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
10	2026-10-22 23:42:09.676366+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	insert	\N	{"id": "1af24bfa-0837-4206-8cf9-9cf4b929c7a9", "kind": "person", "name": null, "email": "01001010102@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010102", "segment": "retail", "last_name": "მაგალითაძე", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.676366+00:00", "first_name": "ნინო", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.676366+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "მაგალითაძე ნინო", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
11	2026-10-22 23:42:09.681879+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	86b38924-7dc9-4d1d-864a-292366679667	insert	\N	{"id": "86b38924-7dc9-4d1d-864a-292366679667", "kind": "person", "name": null, "email": "01001010103@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010103", "segment": "retail", "last_name": "ნიმუშელი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.681879+00:00", "first_name": "ლევან", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.681879+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ნიმუშელი ლევან", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
12	2026-10-22 23:42:09.685774+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	insert	\N	{"id": "5196e2f1-a7e6-4f00-8d5d-32f94d46b89a", "kind": "person", "name": null, "email": "01001010104@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010104", "segment": "retail", "last_name": "სანიმუშო", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.685774+00:00", "first_name": "მარიამ", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.685774+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "სანიმუშო მარიამ", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
13	2026-10-22 23:42:09.690033+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	592c2a48-9d6f-4850-a21d-e9d8752f03e2	insert	\N	{"id": "592c2a48-9d6f-4850-a21d-e9d8752f03e2", "kind": "organization", "name": "შპს ტესტ-ტრანსი", "email": null, "phone": null, "gender": null, "is_pep": false, "tax_id": "404040404", "segment": "corporate", "last_name": null, "birth_date": null, "created_at": "2026-10-22T23:42:09.690033+00:00", "first_name": null, "foreign_id": null, "legal_form": "შპს", "risk_level": null, "updated_at": "2026-10-22T23:42:09.690033+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "შპს ტესტ-ტრანსი", "identified_at": null, "legal_address": "ბათუმი", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": 0.00, "sanctions_checked_at": null}
14	2026-10-22 23:42:09.693829+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	c6920e3c-f54b-4d9b-bb58-470390c3e1d4	insert	\N	{"id": "c6920e3c-f54b-4d9b-bb58-470390c3e1d4", "kind": "organization", "name": "სატესტო გადამზღვეველი", "email": null, "phone": null, "gender": null, "is_pep": false, "tax_id": "505050505", "segment": "corporate", "last_name": null, "birth_date": null, "created_at": "2026-10-22T23:42:09.693829+00:00", "first_name": null, "foreign_id": null, "legal_form": "სს", "risk_level": null, "updated_at": "2026-10-22T23:42:09.693829+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "სატესტო გადამზღვეველი", "identified_at": null, "legal_address": null, "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
15	2026-10-22 23:42:09.728825+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	party	f8194c08-4581-429c-863c-56a54b6e90e0	update	{"id": "f8194c08-4581-429c-863c-56a54b6e90e0", "kind": "person", "name": null, "email": "01001010101@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010101", "segment": "retail", "last_name": "ტესტიშვილი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.66571+00:00", "first_name": "გიორგი", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.66571+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ტესტიშვილი გიორგი", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}	{"id": "f8194c08-4581-429c-863c-56a54b6e90e0", "kind": "person", "name": null, "email": "01001010101@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010101", "segment": "retail", "last_name": "ტესტიშვილი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.66571+00:00", "first_name": "გიორგი", "foreign_id": null, "legal_form": null, "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.728825+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ტესტიშვილი გიორგი", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
16	2026-10-22 23:42:09.739396+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	party	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	update	{"id": "1af24bfa-0837-4206-8cf9-9cf4b929c7a9", "kind": "person", "name": null, "email": "01001010102@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010102", "segment": "retail", "last_name": "მაგალითაძე", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.676366+00:00", "first_name": "ნინო", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.676366+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "მაგალითაძე ნინო", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}	{"id": "1af24bfa-0837-4206-8cf9-9cf4b929c7a9", "kind": "person", "name": null, "email": "01001010102@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010102", "segment": "retail", "last_name": "მაგალითაძე", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.676366+00:00", "first_name": "ნინო", "foreign_id": null, "legal_form": null, "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.739396+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "მაგალითაძე ნინო", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
17	2026-10-22 23:42:09.746025+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	party	86b38924-7dc9-4d1d-864a-292366679667	update	{"id": "86b38924-7dc9-4d1d-864a-292366679667", "kind": "person", "name": null, "email": "01001010103@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010103", "segment": "retail", "last_name": "ნიმუშელი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.681879+00:00", "first_name": "ლევან", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.681879+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ნიმუშელი ლევან", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}	{"id": "86b38924-7dc9-4d1d-864a-292366679667", "kind": "person", "name": null, "email": "01001010103@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010103", "segment": "retail", "last_name": "ნიმუშელი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.681879+00:00", "first_name": "ლევან", "foreign_id": null, "legal_form": null, "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.746025+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ნიმუშელი ლევან", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
28	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	3f2f58f8-ba53-4fca-a9d8-2516141aed31	insert	\N	{"id": "3f2f58f8-ba53-4fca-a9d8-2516141aed31", "amount": 375.00, "due_on": "2026-07-10", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "debtor_kind": "client"}
29	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	4d596a71-9ee4-454c-8270-15122556ee86	insert	\N	{"id": "4d596a71-9ee4-454c-8270-15122556ee86", "amount": 375.00, "due_on": "2026-10-10", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "debtor_kind": "client"}
41	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	aaa17376-534f-4c7e-b5b7-50dd61fae311	insert	\N	{"id": "aaa17376-534f-4c7e-b5b7-50dd61fae311", "premium": 120.00, "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "class_code": "5.1", "deductible": null, "premium_gel": 352.49, "sum_insured": 30000.00, "insured_object_id": "92840a88-05c8-46c7-8bd4-d4b4e7f09498", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "52062d68-9063-498e-8510-af676b807b25", "sum_insured_currency": "EUR"}
18	2026-10-22 23:42:09.757436+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	party	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	update	{"id": "5196e2f1-a7e6-4f00-8d5d-32f94d46b89a", "kind": "person", "name": null, "email": "01001010104@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010104", "segment": "retail", "last_name": "სანიმუშო", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.685774+00:00", "first_name": "მარიამ", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:09.685774+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "სანიმუშო მარიამ", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}	{"id": "5196e2f1-a7e6-4f00-8d5d-32f94d46b89a", "kind": "person", "name": null, "email": "01001010104@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010104", "segment": "retail", "last_name": "სანიმუშო", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.685774+00:00", "first_name": "მარიამ", "foreign_id": null, "legal_form": null, "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.757436+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "სანიმუშო მარიამ", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
19	2026-10-22 23:42:09.767333+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	party	592c2a48-9d6f-4850-a21d-e9d8752f03e2	update	{"id": "592c2a48-9d6f-4850-a21d-e9d8752f03e2", "kind": "organization", "name": "შპს ტესტ-ტრანსი", "email": null, "phone": null, "gender": null, "is_pep": false, "tax_id": "404040404", "segment": "corporate", "last_name": null, "birth_date": null, "created_at": "2026-10-22T23:42:09.690033+00:00", "first_name": null, "foreign_id": null, "legal_form": "შპს", "risk_level": null, "updated_at": "2026-10-22T23:42:09.690033+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "შპს ტესტ-ტრანსი", "identified_at": null, "legal_address": "ბათუმი", "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": 0.00, "sanctions_checked_at": null}	{"id": "592c2a48-9d6f-4850-a21d-e9d8752f03e2", "kind": "organization", "name": "შპს ტესტ-ტრანსი", "email": null, "phone": null, "gender": null, "is_pep": false, "tax_id": "404040404", "segment": "corporate", "last_name": null, "birth_date": null, "created_at": "2026-10-22T23:42:09.690033+00:00", "first_name": null, "foreign_id": null, "legal_form": "შპს", "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.767333+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "შპს ტესტ-ტრანსი", "identified_at": null, "legal_address": "ბათუმი", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": 0.00, "sanctions_checked_at": null}
20	2026-10-22 23:42:09.777585+00	deb1b458-9189-473e-b86e-047bb138fa0a	ri_treaty	1478e4f6-9d46-4c8c-918e-27df990481f6	insert	\N	{"id": "1478e4f6-9d46-4c8c-918e-27df990481f6", "kind": "quota_share", "name": "კასკოს კვოტა 2026", "note": null, "lines": null, "number": "RI000001", "status": "active", "currency": "GEL", "priority": null, "period_to": "2026-12-31", "retention": null, "created_at": "2026-10-22T23:42:09.777585+00:00", "created_by": "deb1b458-9189-473e-b86e-047bb138fa0a", "period_from": "2026-01-01", "treaty_limit": null, "ceded_percent": 30.0000, "rating_agency": null, "broker_percent": 0.0000, "treaty_premium": null, "broker_party_id": null, "reinsurer_rating": null, "commission_percent": 20.0000, "reinsurer_party_id": "c6920e3c-f54b-4d9b-bb58-470390c3e1d4"}
21	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	fd887ebc-e174-4e02-84dc-9e471eba30f0	insert	\N	{"id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "number": "C000001", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.796238+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "f8194c08-4581-429c-863c-56a54b6e90e0"}
22	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	5409b7cc-17bf-4804-b8d1-2b9cf5149520	insert	\N	{"id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "number": "CAS000001", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.796238+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-01-10", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-01-10", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
23	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	c4429f21-927d-419c-9e12-b280c86693cb	insert	\N	{"id": "c4429f21-927d-419c-9e12-b280c86693cb", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "AA-101-AA", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "Toyota Prius AA-101-AA", "object_type": "vehicle"}
24	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	74cc6832-5506-42c5-ab4a-9e7e318e583c	insert	\N	{"id": "74cc6832-5506-42c5-ab4a-9e7e318e583c", "premium": 1200.00, "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "class_code": "5.1", "deductible": 300.00, "premium_gel": 1200.00, "sum_insured": 30000.00, "insured_object_id": "c4429f21-927d-419c-9e12-b280c86693cb", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "GEL"}
25	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	1f18943d-b145-40cf-825b-49331f2fe7f3	insert	\N	{"id": "1f18943d-b145-40cf-825b-49331f2fe7f3", "premium": 300.00, "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "class_code": "5.1", "deductible": null, "premium_gel": 300.00, "sum_insured": 30000.00, "insured_object_id": "c4429f21-927d-419c-9e12-b280c86693cb", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "52062d68-9063-498e-8510-af676b807b25", "sum_insured_currency": "GEL"}
26	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	f2a01c1c-bc0e-41c0-85f7-4e0eedba0526	insert	\N	{"id": "f2a01c1c-bc0e-41c0-85f7-4e0eedba0526", "amount": 375.00, "due_on": "2026-01-10", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "debtor_kind": "client"}
27	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	9a9c5950-df88-413d-a82e-926b933efc13	insert	\N	{"id": "9a9c5950-df88-413d-a82e-926b933efc13", "amount": 375.00, "due_on": "2026-04-10", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "debtor_kind": "client"}
30	2026-10-22 23:42:09.819308+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	5409b7cc-17bf-4804-b8d1-2b9cf5149520	update	{"id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "number": "CAS000001", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.796238+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-01-10", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-01-10", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "number": "CAS000001", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.796238+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.819308+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-01-10", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-01-10", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
31	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f	insert	\N	{"id": "bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f", "number": "C000002", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.85792+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "1af24bfa-0837-4206-8cf9-9cf4b929c7a9"}
32	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	6e5fa754-2657-49fa-8420-670c99250948	insert	\N	{"id": "6e5fa754-2657-49fa-8420-670c99250948", "number": "CAS000002", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.85792+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f", "issue_place": null, "cancelled_on": null, "effective_to": "2027-02-01", "premium_rate": 2.683201, "cancel_reason": null, "effective_from": "2026-02-01", "premium_currency": "USD", "cancel_grace_days": null, "replaces_policy_id": null}
33	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	c4291172-62fa-4811-8697-7404a3bb8cbd	insert	\N	{"id": "c4291172-62fa-4811-8697-7404a3bb8cbd", "policy_id": "6e5fa754-2657-49fa-8420-670c99250948", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "BB-202-BB", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "USD"}, "description": "Toyota Prius BB-202-BB", "object_type": "vehicle"}
34	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	ea24133b-7977-4038-a142-01098e563276	insert	\N	{"id": "ea24133b-7977-4038-a142-01098e563276", "premium": 850.50, "policy_id": "6e5fa754-2657-49fa-8420-670c99250948", "class_code": "5.1", "deductible": 300.00, "premium_gel": 2282.06, "sum_insured": 30000.00, "insured_object_id": "c4291172-62fa-4811-8697-7404a3bb8cbd", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "USD"}
35	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	03d2e7f6-9f95-459e-a0de-5fc80f05f269	insert	\N	{"id": "03d2e7f6-9f95-459e-a0de-5fc80f05f269", "amount": 850.50, "due_on": "2026-02-01", "policy_id": "6e5fa754-2657-49fa-8420-670c99250948", "debtor_kind": "client"}
36	2026-10-22 23:42:09.867584+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	6e5fa754-2657-49fa-8420-670c99250948	update	{"id": "6e5fa754-2657-49fa-8420-670c99250948", "number": "CAS000002", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.85792+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f", "issue_place": null, "cancelled_on": null, "effective_to": "2027-02-01", "premium_rate": 2.683201, "cancel_reason": null, "effective_from": "2026-02-01", "premium_currency": "USD", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "6e5fa754-2657-49fa-8420-670c99250948", "number": "CAS000002", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.85792+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.867584+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f", "issue_place": null, "cancelled_on": null, "effective_to": "2027-02-01", "premium_rate": 2.683201, "cancel_reason": null, "effective_from": "2026-02-01", "premium_currency": "USD", "cancel_grace_days": null, "replaces_policy_id": null}
37	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	eff8795e-b21d-407e-b01f-ec037e47215b	insert	\N	{"id": "eff8795e-b21d-407e-b01f-ec037e47215b", "number": "C000003", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.885596+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "86b38924-7dc9-4d1d-864a-292366679667"}
38	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	299a5381-4bac-49df-82f5-8dda6c323c55	insert	\N	{"id": "299a5381-4bac-49df-82f5-8dda6c323c55", "number": "CAS000003", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.885596+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "eff8795e-b21d-407e-b01f-ec037e47215b", "issue_place": null, "cancelled_on": null, "effective_to": "2027-03-15", "premium_rate": 2.937415, "cancel_reason": null, "effective_from": "2026-03-15", "premium_currency": "EUR", "cancel_grace_days": null, "replaces_policy_id": null}
39	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	92840a88-05c8-46c7-8bd4-d4b4e7f09498	insert	\N	{"id": "92840a88-05c8-46c7-8bd4-d4b4e7f09498", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "CC-303-CC", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "EUR"}, "description": "Toyota Prius CC-303-CC", "object_type": "vehicle"}
40	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	dbcd711c-90d0-451b-a757-9e09e8a5e18b	insert	\N	{"id": "dbcd711c-90d0-451b-a757-9e09e8a5e18b", "premium": 1440.00, "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "class_code": "5.1", "deductible": 300.00, "premium_gel": 4229.88, "sum_insured": 30000.00, "insured_object_id": "92840a88-05c8-46c7-8bd4-d4b4e7f09498", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "EUR"}
42	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	2a754539-0fb8-48d9-aa42-bce8ce20c19c	insert	\N	{"id": "2a754539-0fb8-48d9-aa42-bce8ce20c19c", "amount": 130.00, "due_on": "2026-03-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
43	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	90caddb3-71ce-4433-8314-b580324ed31d	insert	\N	{"id": "90caddb3-71ce-4433-8314-b580324ed31d", "amount": 130.00, "due_on": "2026-04-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
44	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	e11573e2-2478-4796-9aae-298fade34eeb	insert	\N	{"id": "e11573e2-2478-4796-9aae-298fade34eeb", "amount": 130.00, "due_on": "2026-05-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
45	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	0fe91db3-5b4d-4eea-ae7a-adabac725b42	insert	\N	{"id": "0fe91db3-5b4d-4eea-ae7a-adabac725b42", "amount": 130.00, "due_on": "2026-06-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
46	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	0dc27e9a-eff4-4596-9749-4e8e5e179bea	insert	\N	{"id": "0dc27e9a-eff4-4596-9749-4e8e5e179bea", "amount": 130.00, "due_on": "2026-07-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
47	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	ee5cdc4d-b9bf-45ae-8431-de748811982e	insert	\N	{"id": "ee5cdc4d-b9bf-45ae-8431-de748811982e", "amount": 130.00, "due_on": "2026-08-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
48	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	9d18493c-e123-43aa-94da-c59844875212	insert	\N	{"id": "9d18493c-e123-43aa-94da-c59844875212", "amount": 130.00, "due_on": "2026-09-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
49	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	cf3a3125-2218-44de-a572-efe04de96d65	insert	\N	{"id": "cf3a3125-2218-44de-a572-efe04de96d65", "amount": 130.00, "due_on": "2026-10-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
50	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	2bf5e918-d356-45bd-9184-5bf90c25f9be	insert	\N	{"id": "2bf5e918-d356-45bd-9184-5bf90c25f9be", "amount": 130.00, "due_on": "2026-11-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
51	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	58fec97b-8b6f-42c5-b0a7-71bef7e05a91	insert	\N	{"id": "58fec97b-8b6f-42c5-b0a7-71bef7e05a91", "amount": 130.00, "due_on": "2026-12-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
52	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	b5cac111-4bc1-4b34-b5d5-d851c44ac589	insert	\N	{"id": "b5cac111-4bc1-4b34-b5d5-d851c44ac589", "amount": 130.00, "due_on": "2027-01-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
53	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	19f0298e-311e-47b9-9320-a476379ceb63	insert	\N	{"id": "19f0298e-311e-47b9-9320-a476379ceb63", "amount": 130.00, "due_on": "2027-02-15", "policy_id": "299a5381-4bac-49df-82f5-8dda6c323c55", "debtor_kind": "client"}
54	2026-10-22 23:42:09.902444+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	299a5381-4bac-49df-82f5-8dda6c323c55	update	{"id": "299a5381-4bac-49df-82f5-8dda6c323c55", "number": "CAS000003", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.885596+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "eff8795e-b21d-407e-b01f-ec037e47215b", "issue_place": null, "cancelled_on": null, "effective_to": "2027-03-15", "premium_rate": 2.937415, "cancel_reason": null, "effective_from": "2026-03-15", "premium_currency": "EUR", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "299a5381-4bac-49df-82f5-8dda6c323c55", "number": "CAS000003", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.885596+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.902444+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "eff8795e-b21d-407e-b01f-ec037e47215b", "issue_place": null, "cancelled_on": null, "effective_to": "2027-03-15", "premium_rate": 2.937415, "cancel_reason": null, "effective_from": "2026-03-15", "premium_currency": "EUR", "cancel_grace_days": null, "replaces_policy_id": null}
55	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	139d9a61-e9f8-4853-b157-3f6a63bd13e0	insert	\N	{"id": "139d9a61-e9f8-4853-b157-3f6a63bd13e0", "number": "C000004", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.928526+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "5196e2f1-a7e6-4f00-8d5d-32f94d46b89a"}
56	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	d26af7b9-b4a2-4ebd-99ce-056b92affe5a	insert	\N	{"id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "number": "CAS000004", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.928526+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "139d9a61-e9f8-4853-b157-3f6a63bd13e0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-04-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-04-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
57	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	9200d829-cc92-4777-a39a-660638424200	insert	\N	{"id": "9200d829-cc92-4777-a39a-660638424200", "policy_id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "DD-404-DD", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "Toyota Prius DD-404-DD", "object_type": "vehicle"}
58	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	26399d85-e443-4c29-8ff4-d45b997b3dd5	insert	\N	{"id": "26399d85-e443-4c29-8ff4-d45b997b3dd5", "premium": 999.99, "policy_id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "class_code": "5.1", "deductible": 300.00, "premium_gel": 999.99, "sum_insured": 30000.00, "insured_object_id": "9200d829-cc92-4777-a39a-660638424200", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "GEL"}
59	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	e7db70dd-8d60-4f72-83a0-8250df6d834c	insert	\N	{"id": "e7db70dd-8d60-4f72-83a0-8250df6d834c", "amount": 999.99, "due_on": "2026-04-01", "policy_id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "debtor_kind": "client"}
121	2026-10-22 23:42:10.817017+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_party	d8d5b123-1e9d-4c06-bce2-776f5e7048e3	insert	\N	{"id": "d8d5b123-1e9d-4c06-bce2-776f5e7048e3", "role": "liable_person", "case_id": "5a6762b2-1968-4599-8d4e-94ec83281741", "coverage": null, "party_id": "592c2a48-9d6f-4850-a21d-e9d8752f03e2", "response": null, "limit_gel": null, "created_at": "2026-10-22T23:42:10.817017+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "response_on": null, "policy_number": null}
60	2026-10-22 23:42:09.938371+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	d26af7b9-b4a2-4ebd-99ce-056b92affe5a	update	{"id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "number": "CAS000004", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.928526+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "139d9a61-e9f8-4853-b157-3f6a63bd13e0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-04-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-04-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "number": "CAS000004", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.928526+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.938371+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "139d9a61-e9f8-4853-b157-3f6a63bd13e0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-04-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-04-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
61	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	562aa8ea-8b53-4c26-add1-abac31ea7133	insert	\N	{"id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "number": "C000005", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.955682+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "592c2a48-9d6f-4850-a21d-e9d8752f03e2"}
62	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	a72126a1-0ec2-41d5-94d1-21b3c431814b	insert	\N	{"id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "number": "CAS000005", "status": "pending_approval", "segment": "corporate", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.955682+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "issue_place": null, "cancelled_on": null, "effective_to": "2027-05-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-05-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
63	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	cc8df06f-ba09-45e5-99f3-07af8ce859af	insert	\N	{"id": "cc8df06f-ba09-45e5-99f3-07af8ce859af", "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "EE-505-EE", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "Toyota Prius EE-505-EE", "object_type": "vehicle"}
64	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	0ebf5941-4daa-4007-adbd-b844a614038f	insert	\N	{"id": "0ebf5941-4daa-4007-adbd-b844a614038f", "premium": 2400.00, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "class_code": "5.1", "deductible": 300.00, "premium_gel": 2400.00, "sum_insured": 30000.00, "insured_object_id": "cc8df06f-ba09-45e5-99f3-07af8ce859af", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "GEL"}
65	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	ddc24815-9791-4455-8e5a-c6139151cba8	insert	\N	{"id": "ddc24815-9791-4455-8e5a-c6139151cba8", "premium": 600.00, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "class_code": "5.1", "deductible": null, "premium_gel": 600.00, "sum_insured": 30000.00, "insured_object_id": "cc8df06f-ba09-45e5-99f3-07af8ce859af", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "52062d68-9063-498e-8510-af676b807b25", "sum_insured_currency": "GEL"}
66	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	1c1f1f22-c12d-4e47-b660-80bc33114975	insert	\N	{"id": "1c1f1f22-c12d-4e47-b660-80bc33114975", "amount": 750.00, "due_on": "2026-05-01", "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "debtor_kind": "client"}
67	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	3266ed4f-d151-4c23-bb5d-1222326787c1	insert	\N	{"id": "3266ed4f-d151-4c23-bb5d-1222326787c1", "amount": 750.00, "due_on": "2026-08-01", "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "debtor_kind": "client"}
68	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	718b0622-ba8f-4d15-90a8-aeddaa1968d5	insert	\N	{"id": "718b0622-ba8f-4d15-90a8-aeddaa1968d5", "amount": 750.00, "due_on": "2026-11-01", "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "debtor_kind": "client"}
69	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	9a75a79f-6f32-4db8-805d-0871b82ee8b7	insert	\N	{"id": "9a75a79f-6f32-4db8-805d-0871b82ee8b7", "amount": 750.00, "due_on": "2027-02-01", "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "debtor_kind": "client"}
70	2026-10-22 23:42:09.97252+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	a72126a1-0ec2-41d5-94d1-21b3c431814b	update	{"id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "number": "CAS000005", "status": "pending_approval", "segment": "corporate", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.955682+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "issue_place": null, "cancelled_on": null, "effective_to": "2027-05-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-05-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "number": "CAS000005", "status": "active", "segment": "corporate", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.955682+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.97252+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "issue_place": null, "cancelled_on": null, "effective_to": "2027-05-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-05-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
97	2026-10-22 23:42:10.51281+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	01de7961-6f90-4d17-bb91-38d569055701	insert	\N	{"id": "01de7961-6f90-4d17-bb91-38d569055701", "number": "CAS000008", "status": "pending_approval", "segment": "corporate", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.51281+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": "ქურდობის დაფარვის მოხსნა", "contract_id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "issue_place": null, "cancelled_on": null, "effective_to": "2027-05-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-09-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b"}
71	2026-10-22 23:42:10.122388+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	policy	d26af7b9-b4a2-4ebd-99ce-056b92affe5a	update	{"id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "number": "CAS000004", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.928526+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.938371+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "139d9a61-e9f8-4853-b157-3f6a63bd13e0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-04-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-04-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "d26af7b9-b4a2-4ebd-99ce-056b92affe5a", "number": "CAS000004", "status": "cancelled", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:09.928526+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:09.938371+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "139d9a61-e9f8-4853-b157-3f6a63bd13e0", "issue_place": null, "cancelled_on": "2026-07-01", "effective_to": "2027-04-01", "premium_rate": 1.000000, "cancel_reason": "კლიენტის განცხადება", "effective_from": "2026-04-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
72	2026-10-22 23:42:10.159834+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	b0539124-f3d7-4c97-903a-49c820bc2c1d	insert	\N	{"id": "b0539124-f3d7-4c97-903a-49c820bc2c1d", "number": "CAS000006", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.159834+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": "ქურდობის ლიმიტის გაზრდა", "contract_id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-01-10", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520"}
73	2026-10-22 23:42:10.159834+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	a1791b42-b7ce-4b7b-bad6-7c3b7a6355e6	insert	\N	{"id": "a1791b42-b7ce-4b7b-bad6-7c3b7a6355e6", "policy_id": "b0539124-f3d7-4c97-903a-49c820bc2c1d", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "AA-101-AA", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "Toyota Prius AA-101-AA", "object_type": "vehicle"}
74	2026-10-22 23:42:10.159834+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	38fe816a-372d-4780-afd1-471f04c308eb	insert	\N	{"id": "38fe816a-372d-4780-afd1-471f04c308eb", "premium": 150.00, "policy_id": "b0539124-f3d7-4c97-903a-49c820bc2c1d", "class_code": "5.1", "deductible": null, "premium_gel": 150.00, "sum_insured": 35000.00, "insured_object_id": "a1791b42-b7ce-4b7b-bad6-7c3b7a6355e6", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "52062d68-9063-498e-8510-af676b807b25", "sum_insured_currency": "GEL"}
75	2026-10-22 23:42:10.159834+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	d31d7c94-b238-4a6d-b0fe-41374fce65e2	insert	\N	{"id": "d31d7c94-b238-4a6d-b0fe-41374fce65e2", "amount": 150.00, "due_on": "2026-06-01", "policy_id": "b0539124-f3d7-4c97-903a-49c820bc2c1d", "debtor_kind": "client"}
76	2026-10-22 23:42:10.175842+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	b0539124-f3d7-4c97-903a-49c820bc2c1d	update	{"id": "b0539124-f3d7-4c97-903a-49c820bc2c1d", "number": "CAS000006", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.159834+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": "ქურდობის ლიმიტის გაზრდა", "contract_id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-01-10", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520"}	{"id": "b0539124-f3d7-4c97-903a-49c820bc2c1d", "number": "CAS000006", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.159834+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:10.175842+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": "ქურდობის ლიმიტის გაზრდა", "contract_id": "fd887ebc-e174-4e02-84dc-9e471eba30f0", "issue_place": null, "cancelled_on": null, "effective_to": "2027-01-10", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520"}
77	2026-10-22 23:42:10.19982+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	loss_event	139519c6-e471-4267-85ac-f841d74e7c93	insert	\N	{"id": "139519c6-e471-4267-85ac-f841d74e7c93", "number": "CLM000001", "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "created_at": "2026-10-22T23:42:10.19982+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "source_ref": null, "description": "შეჯახება გზაჯვარედინზე", "occurred_on": "2026-06-10", "reported_on": "2026-06-11", "request_key": "6d94b983-767c-12f4-9a59-12d5f212eaa7", "source_batch": null, "occurred_place": "თბილისი"}
78	2026-10-22 23:42:10.19982+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	claim	df2fe01b-405b-44c2-95df-a0eb6e9953d5	insert	\N	{"id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "number": "CLM000001", "status": "registered", "currency": "GEL", "closed_on": null, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "created_at": "2026-10-22T23:42:10.19982+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "შეჯახება გზაჯვარედინზე", "occurred_on": "2026-06-10", "reported_on": "2026-06-11", "is_total_loss": false, "loss_event_id": "139519c6-e471-4267-85ac-f841d74e7c93", "claimed_amount": 6000.00, "occurred_place": "თბილისი", "policy_line_id": "0ebf5941-4daa-4007-adbd-b844a614038f", "approved_amount": null, "rejection_reason": null, "deductible_amount": 0.00}
79	2026-10-22 23:42:10.218306+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	claim_reserve_request	9220a800-060e-4b64-911b-689cb3c10d36	insert	\N	{"id": "9220a800-060e-4b64-911b-689cb3c10d36", "change": -500.00, "reason": "შეფასების შემდეგ", "claim_id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "booked_on": "2026-06-20", "new_level": 4500.00, "base_level": 5000.00, "created_at": "2026-10-22T23:42:10.218306+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2"}
98	2026-10-22 23:42:10.51281+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	5e3f4ed7-3a49-4a1f-b224-612df5f6adc7	insert	\N	{"id": "5e3f4ed7-3a49-4a1f-b224-612df5f6adc7", "policy_id": "01de7961-6f90-4d17-bb91-38d569055701", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "EE-505-EE", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "Toyota Prius EE-505-EE", "object_type": "vehicle"}
80	2026-10-22 23:42:10.225406+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	claim	df2fe01b-405b-44c2-95df-a0eb6e9953d5	update	{"id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "number": "CLM000001", "status": "registered", "currency": "GEL", "closed_on": null, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "created_at": "2026-10-22T23:42:10.19982+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "შეჯახება გზაჯვარედინზე", "occurred_on": "2026-06-10", "reported_on": "2026-06-11", "is_total_loss": false, "loss_event_id": "139519c6-e471-4267-85ac-f841d74e7c93", "claimed_amount": 6000.00, "occurred_place": "თბილისი", "policy_line_id": "0ebf5941-4daa-4007-adbd-b844a614038f", "approved_amount": null, "rejection_reason": null, "deductible_amount": 0.00}	{"id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "number": "CLM000001", "status": "approved", "currency": "GEL", "closed_on": null, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "created_at": "2026-10-22T23:42:10.19982+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "შეჯახება გზაჯვარედინზე", "occurred_on": "2026-06-10", "reported_on": "2026-06-11", "is_total_loss": false, "loss_event_id": "139519c6-e471-4267-85ac-f841d74e7c93", "claimed_amount": 6000.00, "occurred_place": "თბილისი", "policy_line_id": "0ebf5941-4daa-4007-adbd-b844a614038f", "approved_amount": 4500.00, "rejection_reason": null, "deductible_amount": 300.00}
81	2026-10-22 23:42:10.272002+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	claim	df2fe01b-405b-44c2-95df-a0eb6e9953d5	update	{"id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "number": "CLM000001", "status": "approved", "currency": "GEL", "closed_on": null, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "created_at": "2026-10-22T23:42:10.19982+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "შეჯახება გზაჯვარედინზე", "occurred_on": "2026-06-10", "reported_on": "2026-06-11", "is_total_loss": false, "loss_event_id": "139519c6-e471-4267-85ac-f841d74e7c93", "claimed_amount": 6000.00, "occurred_place": "თბილისი", "policy_line_id": "0ebf5941-4daa-4007-adbd-b844a614038f", "approved_amount": 4500.00, "rejection_reason": null, "deductible_amount": 300.00}	{"id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "number": "CLM000001", "status": "paid", "currency": "GEL", "closed_on": null, "policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b", "created_at": "2026-10-22T23:42:10.19982+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "შეჯახება გზაჯვარედინზე", "occurred_on": "2026-06-10", "reported_on": "2026-06-11", "is_total_loss": false, "loss_event_id": "139519c6-e471-4267-85ac-f841d74e7c93", "claimed_amount": 6000.00, "occurred_place": "თბილისი", "policy_line_id": "0ebf5941-4daa-4007-adbd-b844a614038f", "approved_amount": 4500.00, "rejection_reason": null, "deductible_amount": 300.00}
82	2026-10-22 23:42:10.300943+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	complaint	a67ef776-52a7-4c8c-bf6e-3552d70c83f6	insert	\N	{"id": "a67ef776-52a7-4c8c-bf6e-3552d70c83f6", "note": null, "number": "PR000001", "status": "open", "channel": "written", "outcome": null, "category": 2, "claim_id": null, "measures": null, "party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.300943+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "description": "გადახდის დაგვიანება", "received_on": "2026-07-02", "responded_on": null, "customer_name": "გიორგი ტესტიშვილი", "response_text": null, "acknowledged_at": null, "customer_contact": "+995 599 12 34 56"}
83	2026-10-22 23:42:10.308687+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	aml_case	d8648d25-64a5-4bd9-938b-bbbd395a1faa	insert	\N	{"id": "d8648d25-64a5-4bd9-938b-bbbd395a1faa", "note": null, "number": "AML000001", "source": "monitoring", "status": "open", "party_id": "86b38924-7dc9-4d1d-864a-292366679667", "str_type": null, "closed_on": null, "opened_on": "2026-08-10", "class_code": "5.1", "created_at": "2026-10-22T23:42:10.308687+00:00", "created_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "description": "უჩვეულოდ დიდი ნაღდი შენატანი", "examination": null, "examined_on": null, "str_sent_at": null, "suspicion_at": null, "fms_reference": null, "str_other_basis": null, "transaction_ref": null}
84	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	004067a8-8b4c-43b3-968d-2dc6be2db74a	insert	\N	{"id": "004067a8-8b4c-43b3-968d-2dc6be2db74a", "number": "C000006", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.321311+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "1af24bfa-0837-4206-8cf9-9cf4b929c7a9"}
85	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	insert	\N	{"id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "number": "CAS000007", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.321311+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "004067a8-8b4c-43b3-968d-2dc6be2db74a", "issue_place": null, "cancelled_on": null, "effective_to": "2027-06-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
86	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	7c657499-bf16-4f1b-b969-1a4cff877d56	insert	\N	{"id": "7c657499-bf16-4f1b-b969-1a4cff877d56", "policy_id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "attributes": {"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "FF-606-FF", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "Toyota Prius FF-606-FF", "object_type": "vehicle"}
87	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	d4c16ccf-e41e-4cb2-b082-a8267e59477a	insert	\N	{"id": "d4c16ccf-e41e-4cb2-b082-a8267e59477a", "premium": 24000.00, "policy_id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "class_code": "5.1", "deductible": 300.00, "premium_gel": 24000.00, "sum_insured": 30000.00, "insured_object_id": "7c657499-bf16-4f1b-b969-1a4cff877d56", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "GEL"}
88	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	df455f7e-39f3-4f28-a1b0-c565e00d464c	insert	\N	{"id": "df455f7e-39f3-4f28-a1b0-c565e00d464c", "amount": 24000.00, "due_on": "2026-06-01", "policy_id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "debtor_kind": "client"}
99	2026-10-22 23:42:10.51281+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	a89358a9-a44a-4661-a14e-6677ad33d674	insert	\N	{"id": "a89358a9-a44a-4661-a14e-6677ad33d674", "premium": -200.00, "policy_id": "01de7961-6f90-4d17-bb91-38d569055701", "class_code": "5.1", "deductible": null, "premium_gel": -200.00, "sum_insured": null, "insured_object_id": "5e3f4ed7-3a49-4a1f-b224-612df5f6adc7", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "52062d68-9063-498e-8510-af676b807b25", "sum_insured_currency": null}
89	2026-10-22 23:42:10.331771+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	update	{"id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "number": "CAS000007", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.321311+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "004067a8-8b4c-43b3-968d-2dc6be2db74a", "issue_place": null, "cancelled_on": null, "effective_to": "2027-06-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "number": "CAS000007", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.321311+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:10.331771+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "004067a8-8b4c-43b3-968d-2dc6be2db74a", "issue_place": null, "cancelled_on": null, "effective_to": "2027-06-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
90	2026-10-22 23:42:10.360019+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	policy	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	update	{"id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "number": "CAS000007", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.321311+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:10.331771+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "004067a8-8b4c-43b3-968d-2dc6be2db74a", "issue_place": null, "cancelled_on": null, "effective_to": "2027-06-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "number": "CAS000007", "status": "cancelled", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.321311+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:10.331771+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "004067a8-8b4c-43b3-968d-2dc6be2db74a", "issue_place": null, "cancelled_on": "2026-06-20", "effective_to": "2027-06-01", "premium_rate": 1.000000, "cancel_reason": "გადაფორმება", "effective_from": "2026-06-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
91	2026-10-22 23:42:10.379889+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	premium_refund_request	347c8f2b-d8c9-4655-9788-ca92e25d238f	insert	\N	{"id": "347c8f2b-d8c9-4655-9788-ca92e25d238f", "rate": 1.000000, "method": "bank", "currency": "GEL", "policy_id": "72dacb3b-0f40-43c5-8774-1a5f9e0f7173", "reference": "დიდი დაბრუნება GE00TB0000000000000002", "amount_gel": 22750.68, "created_at": "2026-10-22T23:42:10.379889+00:00", "created_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "refunded_on": "2026-10-23", "payer_party_id": "1af24bfa-0837-4206-8cf9-9cf4b929c7a9"}
92	2026-10-22 23:42:10.379889+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	premium_refund_request_line	18f7fcdd-15bb-42a8-bae1-51f4352654f0	insert	\N	{"id": "18f7fcdd-15bb-42a8-bae1-51f4352654f0", "amount": 22750.68, "position": 1, "amount_gel": 22750.68, "request_id": "347c8f2b-d8c9-4655-9788-ca92e25d238f", "installment_id": "df455f7e-39f3-4f28-a1b0-c565e00d464c"}
93	2026-10-22 23:42:10.438222+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	loss_event	5fa64b35-ef1d-43b6-b3f7-78c03b31e882	insert	\N	{"id": "5fa64b35-ef1d-43b6-b3f7-78c03b31e882", "number": "CLM000002", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.438222+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "source_ref": null, "description": "სრული განადგურება", "occurred_on": "2026-08-05", "reported_on": "2026-08-06", "request_key": "7b1f9a3e-0c2d-4e5f-8a6b-9c0d1e2f3a4b", "source_batch": null, "occurred_place": "რუსთავი"}
94	2026-10-22 23:42:10.438222+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	claim	007668d6-63b2-42e2-a885-780f8b9f861d	insert	\N	{"id": "007668d6-63b2-42e2-a885-780f8b9f861d", "number": "CLM000002", "status": "registered", "currency": "GEL", "closed_on": null, "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.438222+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "სრული განადგურება", "occurred_on": "2026-08-05", "reported_on": "2026-08-06", "is_total_loss": false, "loss_event_id": "5fa64b35-ef1d-43b6-b3f7-78c03b31e882", "claimed_amount": 15000.00, "occurred_place": "რუსთავი", "policy_line_id": "74cc6832-5506-42c5-ab4a-9e7e318e583c", "approved_amount": null, "rejection_reason": null, "deductible_amount": 0.00}
95	2026-10-22 23:42:10.447463+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	claim	007668d6-63b2-42e2-a885-780f8b9f861d	update	{"id": "007668d6-63b2-42e2-a885-780f8b9f861d", "number": "CLM000002", "status": "registered", "currency": "GEL", "closed_on": null, "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.438222+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "სრული განადგურება", "occurred_on": "2026-08-05", "reported_on": "2026-08-06", "is_total_loss": false, "loss_event_id": "5fa64b35-ef1d-43b6-b3f7-78c03b31e882", "claimed_amount": 15000.00, "occurred_place": "რუსთავი", "policy_line_id": "74cc6832-5506-42c5-ab4a-9e7e318e583c", "approved_amount": null, "rejection_reason": null, "deductible_amount": 0.00}	{"id": "007668d6-63b2-42e2-a885-780f8b9f861d", "number": "CLM000002", "status": "approved", "currency": "GEL", "closed_on": null, "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.438222+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "description": "სრული განადგურება", "occurred_on": "2026-08-05", "reported_on": "2026-08-06", "is_total_loss": false, "loss_event_id": "5fa64b35-ef1d-43b6-b3f7-78c03b31e882", "claimed_amount": 15000.00, "occurred_place": "რუსთავი", "policy_line_id": "74cc6832-5506-42c5-ab4a-9e7e318e583c", "approved_amount": 15000.00, "rejection_reason": null, "deductible_amount": 300.00}
96	2026-10-22 23:42:10.463924+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	claim_payment_request	42b87c04-0d7b-48ce-b38a-107c53609492	insert	\N	{"id": "42b87c04-0d7b-48ce-b38a-107c53609492", "note": "ავტორიზაციით", "rate": 1.000000, "amount": 12000.00, "paid_on": "2026-08-20", "claim_id": "007668d6-63b2-42e2-a885-780f8b9f861d", "net_paid": 11700.00, "created_at": "2026-10-22T23:42:10.463924+00:00", "created_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "payee_kind": "policyholder", "net_paid_gel": 11700.00, "offset_premium": false, "payee_party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "premium_offset": 0.00, "total_loss_mode": "none", "deductible_withheld": 300.00}
100	2026-10-22 23:42:10.524061+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	01de7961-6f90-4d17-bb91-38d569055701	update	{"id": "01de7961-6f90-4d17-bb91-38d569055701", "number": "CAS000008", "status": "pending_approval", "segment": "corporate", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.51281+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": "ქურდობის დაფარვის მოხსნა", "contract_id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "issue_place": null, "cancelled_on": null, "effective_to": "2027-05-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-09-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b"}	{"id": "01de7961-6f90-4d17-bb91-38d569055701", "number": "CAS000008", "status": "active", "segment": "corporate", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.51281+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:10.524061+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": "ქურდობის დაფარვის მოხსნა", "contract_id": "562aa8ea-8b53-4c26-add1-abac31ea7133", "issue_place": null, "cancelled_on": null, "effective_to": "2027-05-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-09-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": "a72126a1-0ec2-41d5-94d1-21b3c431814b"}
101	2026-10-22 23:42:10.552847+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party_beneficial_owner	6e5e9488-0029-4390-bf65-f3900f1da84c	insert	\N	{"id": "6e5e9488-0029-4390-bf65-f3900f1da84c", "note": null, "is_pep": false, "party_id": "592c2a48-9d6f-4850-a21d-e9d8752f03e2", "valid_to": null, "criterion": "share", "is_direct": true, "last_name": "მფლობელიძე", "birth_date": "1970-02-02", "created_at": "2026-10-22T23:42:10.552847+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "first_name": "დავით", "is_nominee": false, "birth_place": null, "citizenship": "GE", "verified_at": null, "identified_at": null, "share_percent": 60.00, "document_issuer": null, "document_number": null, "personal_number": "01001010199", "document_country": null, "document_valid_to": null, "document_issued_on": null, "verification_source": null, "sanctions_checked_at": null}
102	2026-10-22 23:42:10.560701+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	fms_suspension	bac62356-6618-4847-b82a-cd5a58394de8	insert	\N	{"id": "bac62356-6618-4847-b82a-cd5a58394de8", "kind": "oral", "note": "ფმს-ის ზეპირი მითითება", "scope": "all", "case_id": null, "ends_at": "2026-09-15T06:30:00+00:00", "party_id": "86b38924-7dc9-4d1d-864a-292366679667", "lifted_at": null, "created_at": "2026-10-22T23:42:10.560701+00:00", "created_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "lift_reason": null, "instructed_at": "2026-09-10T06:30:00+00:00", "legacy_unmapped": false, "target_claim_id": null, "transaction_ref": null, "target_policy_id": null, "written_confirmed_at": null}
103	2026-10-22 23:42:10.574649+00	deb1b458-9189-473e-b86e-047bb138fa0a	user_group	25f6fd68-481e-4558-b56f-3d34d9c47701	insert	\N	{"id": "25f6fd68-481e-4558-b56f-3d34d9c47701", "name": "ფინანსური დირექტორები", "created_at": "2026-10-22T23:42:10.574649+00:00"}
104	2026-10-22 23:42:10.58472+00	deb1b458-9189-473e-b86e-047bb138fa0a	user_group_permission	a574e35c-5ea6-4a53-859a-24fd673dd60a	insert	\N	{"id": "a574e35c-5ea6-4a53-859a-24fd673dd60a", "group_id": "25f6fd68-481e-4558-b56f-3d34d9c47701", "created_at": "2026-10-22T23:42:10.58472+00:00", "permission": "finance.panel.view"}
105	2026-10-22 23:42:10.593126+00	deb1b458-9189-473e-b86e-047bb138fa0a	user_group_member	0ff93b3a-8bd5-4e97-990f-5b6536e44de8	insert	\N	{"id": "0ff93b3a-8bd5-4e97-990f-5b6536e44de8", "user_id": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "group_id": "25f6fd68-481e-4558-b56f-3d34d9c47701", "created_at": "2026-10-22T23:42:10.593126+00:00"}
106	2026-10-22 23:42:10.600302+00	deb1b458-9189-473e-b86e-047bb138fa0a	user_permission	fe26d7eb-29e4-4a2c-ae0a-47f649ef85d5	insert	\N	{"id": "fe26d7eb-29e4-4a2c-ae0a-47f649ef85d5", "user_id": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "decision": "allow", "created_at": "2026-10-22T23:42:10.600302+00:00", "permission": "audit.log.view"}
107	2026-10-22 23:42:10.607396+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	complaint	a67ef776-52a7-4c8c-bf6e-3552d70c83f6	update	{"id": "a67ef776-52a7-4c8c-bf6e-3552d70c83f6", "note": null, "number": "PR000001", "status": "open", "channel": "written", "outcome": null, "category": 2, "claim_id": null, "measures": null, "party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.300943+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "description": "გადახდის დაგვიანება", "received_on": "2026-07-02", "responded_on": null, "customer_name": "გიორგი ტესტიშვილი", "response_text": null, "acknowledged_at": null, "customer_contact": "+995 599 12 34 56"}	{"id": "a67ef776-52a7-4c8c-bf6e-3552d70c83f6", "note": null, "number": "PR000001", "status": "open", "channel": "written", "outcome": null, "category": 2, "claim_id": null, "measures": null, "party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.300943+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "description": "გადახდის დაგვიანება", "received_on": "2026-07-02", "responded_on": null, "customer_name": "გიორგი ტესტიშვილი", "response_text": null, "acknowledged_at": "2026-10-22T23:42:10.607396+00:00", "customer_contact": "+995 599 12 34 56"}
108	2026-10-22 23:42:10.617955+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	complaint	a67ef776-52a7-4c8c-bf6e-3552d70c83f6	update	{"id": "a67ef776-52a7-4c8c-bf6e-3552d70c83f6", "note": null, "number": "PR000001", "status": "open", "channel": "written", "outcome": null, "category": 2, "claim_id": null, "measures": null, "party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.300943+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "description": "გადახდის დაგვიანება", "received_on": "2026-07-02", "responded_on": null, "customer_name": "გიორგი ტესტიშვილი", "response_text": null, "acknowledged_at": "2026-10-22T23:42:10.607396+00:00", "customer_contact": "+995 599 12 34 56"}	{"id": "a67ef776-52a7-4c8c-bf6e-3552d70c83f6", "note": null, "number": "PR000001", "status": "partly_upheld", "channel": "written", "outcome": "ნაწილობრივ", "category": 2, "claim_id": null, "measures": "პროცესის შემოწმება", "party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "policy_id": "5409b7cc-17bf-4804-b8d1-2b9cf5149520", "created_at": "2026-10-22T23:42:10.300943+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "description": "გადახდის დაგვიანება", "received_on": "2026-07-02", "responded_on": "2026-07-20", "customer_name": "გიორგი ტესტიშვილი", "response_text": "გადახდა აღდგა", "acknowledged_at": "2026-10-22T23:42:10.607396+00:00", "customer_contact": "+995 599 12 34 56"}
109	2026-10-22 23:42:10.621518+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	party	db6b5659-c47b-4440-b655-806dc9a4fe37	insert	\N	{"id": "db6b5659-c47b-4440-b655-806dc9a4fe37", "kind": "person", "name": null, "email": null, "phone": null, "gender": null, "is_pep": false, "tax_id": "01001010110", "segment": "retail", "last_name": "შუამავლიშვილი", "birth_date": null, "created_at": "2026-10-22T23:42:10.621518+00:00", "first_name": "აგენტი", "foreign_id": null, "legal_form": null, "risk_level": null, "updated_at": "2026-10-22T23:42:10.621518+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "შუამავლიშვილი აგენტი", "identified_at": null, "legal_address": null, "actual_address": null, "risk_review_on": null, "is_group_member": false, "is_public_entity": false, "risk_assessed_on": null, "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}
110	2026-10-22 23:42:10.62902+00	deb1b458-9189-473e-b86e-047bb138fa0a	intermediary	3ffa29d9-8f39-4905-b6a5-30e5bb1217dc	insert	\N	{"id": "3ffa29d9-8f39-4905-b6a5-30e5bb1217dc", "kind": "agent", "status": "active", "party_id": "db6b5659-c47b-4440-b655-806dc9a4fe37", "valid_to": null, "created_at": "2026-10-22T23:42:10.62902+00:00", "contract_no": "AG-001", "contract_date": "2026-01-01", "commission_percent": 10.00}
111	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	contract	e710c511-3698-46a6-96d9-cb18c5dee966	insert	\N	{"id": "e710c511-3698-46a6-96d9-cb18c5dee966", "number": "C000007", "branch_id": null, "signed_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.637809+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "policyholder_id": "f8194c08-4581-429c-863c-56a54b6e90e0"}
112	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy	032821d0-09ce-408c-aaab-93cd4c80438f	insert	\N	{"id": "032821d0-09ce-408c-aaab-93cd4c80438f", "number": "CAS000009", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.637809+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "e710c511-3698-46a6-96d9-cb18c5dee966", "issue_place": null, "cancelled_on": null, "effective_to": "2027-09-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-09-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
113	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	insured_object	54402341-f947-4da6-89c8-5c69b6258066	insert	\N	{"id": "54402341-f947-4da6-89c8-5c69b6258066", "policy_id": "032821d0-09ce-408c-aaab-93cd4c80438f", "attributes": {"vin": null, "make": "BMW", "year": 2022, "model": "X5", "plate": "GG-707-GG", "usage": null, "value": "90000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}, "description": "BMW X5 GG-707-GG", "object_type": "vehicle"}
114	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	policy_line	7df4bf19-1eb3-4605-834d-c6073b53c1d5	insert	\N	{"id": "7df4bf19-1eb3-4605-834d-c6073b53c1d5", "premium": 15000.00, "policy_id": "032821d0-09ce-408c-aaab-93cd4c80438f", "class_code": "5.1", "deductible": 500.00, "premium_gel": 15000.00, "sum_insured": 90000.00, "insured_object_id": "54402341-f947-4da6-89c8-5c69b6258066", "deductible_percent": null, "is_fully_reinsured": false, "product_coverage_id": "fcb6d9fc-4f59-4a4c-80d3-0148494a32ef", "sum_insured_currency": "GEL"}
115	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	installment	355b51a8-1310-468e-9629-2f5d2674c7cc	insert	\N	{"id": "355b51a8-1310-468e-9629-2f5d2674c7cc", "amount": 15000.00, "due_on": "2026-09-01", "policy_id": "032821d0-09ce-408c-aaab-93cd4c80438f", "debtor_kind": "client"}
116	2026-10-22 23:42:10.648943+00	f811b716-2a18-4fe9-b792-fb90f6665f39	policy	032821d0-09ce-408c-aaab-93cd4c80438f	update	{"id": "032821d0-09ce-408c-aaab-93cd4c80438f", "number": "CAS000009", "status": "pending_approval", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.637809+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": null, "approved_by": null, "change_note": null, "contract_id": "e710c511-3698-46a6-96d9-cb18c5dee966", "issue_place": null, "cancelled_on": null, "effective_to": "2027-09-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-09-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}	{"id": "032821d0-09ce-408c-aaab-93cd4c80438f", "number": "CAS000009", "status": "active", "segment": "retail", "issued_on": "2026-10-23", "created_at": "2026-10-22T23:42:10.637809+00:00", "created_by": "31df0fa3-64b7-4d56-9b3c-50fa111d6cd6", "product_id": "76c2c324-f357-4885-b597-54496f62db7c", "approved_at": "2026-10-22T23:42:10.648943+00:00", "approved_by": "f811b716-2a18-4fe9-b792-fb90f6665f39", "change_note": null, "contract_id": "e710c511-3698-46a6-96d9-cb18c5dee966", "issue_place": null, "cancelled_on": null, "effective_to": "2027-09-01", "premium_rate": 1.000000, "cancel_reason": null, "effective_from": "2026-09-01", "premium_currency": "GEL", "cancel_grace_days": null, "replaces_policy_id": null}
117	2026-10-22 23:42:10.68549+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt_reversal_request	1b3570bb-c7fe-43c6-87c1-b8df86c256fe	insert	\N	{"id": "1b3570bb-c7fe-43c6-87c1-b8df86c256fe", "method": "bank", "reason": "ბანკმა დააბრუნა", "policy_id": "032821d0-09ce-408c-aaab-93cd4c80438f", "amount_gel": 15000.00, "created_at": "2026-10-22T23:42:10.68549+00:00", "created_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "receipt_id": "0d6abf52-7ff6-41dc-b040-5128a51538e4", "reversed_on": "2026-10-23", "payer_party_id": "f8194c08-4581-429c-863c-56a54b6e90e0"}
118	2026-10-22 23:42:10.68549+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt_reversal_request_line	2bcbcba2-7e51-443e-84ff-4fe975ce934d	insert	\N	{"id": "2bcbcba2-7e51-443e-84ff-4fe975ce934d", "rate": 1.000000, "amount": 15000.00, "position": 1, "amount_gel": 15000.00, "request_id": "1b3570bb-c7fe-43c6-87c1-b8df86c256fe", "installment_id": "355b51a8-1310-468e-9629-2f5d2674c7cc"}
119	2026-10-22 23:42:10.706429+00	deb1b458-9189-473e-b86e-047bb138fa0a	ri_treaty	ebefd2b8-d62a-4fed-8962-5574378ac169	insert	\N	{"id": "ebefd2b8-d62a-4fed-8962-5574378ac169", "kind": "facultative", "name": "ფაკულტატური EUR", "note": null, "lines": null, "number": "RI000002", "status": "active", "currency": "EUR", "priority": null, "period_to": "2027-12-31", "retention": null, "created_at": "2026-10-22T23:42:10.706429+00:00", "created_by": "deb1b458-9189-473e-b86e-047bb138fa0a", "period_from": "2026-01-01", "treaty_limit": null, "ceded_percent": 50.0000, "rating_agency": null, "broker_percent": 0.0000, "treaty_premium": null, "broker_party_id": null, "reinsurer_rating": null, "commission_percent": 15.0000, "reinsurer_party_id": "c6920e3c-f54b-4d9b-bb58-470390c3e1d4"}
120	2026-10-22 23:42:10.80471+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_case	5a6762b2-1968-4599-8d4e-94ec83281741	insert	\N	{"id": "5a6762b2-1968-4599-8d4e-94ec83281741", "note": "მესამე პირის ბრალი", "number": "RG000001", "status": "opened", "claim_id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "opened_on": "2026-08-10", "basis_date": null, "basis_kind": null, "created_at": "2026-10-22T23:42:10.80471+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "97f8cc2e-5445-78a1-e87c-fa7df2462e6f", "basis_number": null}
122	2026-10-22 23:42:10.822705+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_movement	25829dee-8e54-41b0-a414-84712cfe8692	insert	\N	{"id": "25829dee-8e54-41b0-a414-84712cfe8692", "kind": "claimed", "note": null, "case_id": "5a6762b2-1968-4599-8d4e-94ec83281741", "payer_id": null, "booked_on": "2026-08-12", "amount_gel": 3000.00, "created_at": "2026-10-22T23:42:10.822705+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "33351dec-7174-f94c-3528-c7fe64ab7349", "document_date": "2026-08-12", "document_kind": "demand_letter", "document_number": "DL-1", "claim_recovery_id": null, "reverses_movement_id": null}
123	2026-10-22 23:42:10.835998+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_movement	c5d73807-4889-40a9-964d-f9f38e3ef7f2	insert	\N	{"id": "c5d73807-4889-40a9-964d-f9f38e3ef7f2", "kind": "confirmed", "note": null, "case_id": "5a6762b2-1968-4599-8d4e-94ec83281741", "payer_id": null, "booked_on": "2026-08-20", "amount_gel": 2000.00, "created_at": "2026-10-22T23:42:10.835998+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "a74f3698-2efb-2a42-0206-cdb021c0ff9a", "document_date": "2026-08-20", "document_kind": "debtor_acceptance", "document_number": "DA-1", "claim_recovery_id": null, "reverses_movement_id": null}
124	2026-10-22 23:42:10.835998+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_case	5a6762b2-1968-4599-8d4e-94ec83281741	update	{"id": "5a6762b2-1968-4599-8d4e-94ec83281741", "note": "მესამე პირის ბრალი", "number": "RG000001", "status": "opened", "claim_id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "opened_on": "2026-08-10", "basis_date": null, "basis_kind": null, "created_at": "2026-10-22T23:42:10.80471+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "97f8cc2e-5445-78a1-e87c-fa7df2462e6f", "basis_number": null}	{"id": "5a6762b2-1968-4599-8d4e-94ec83281741", "note": "მესამე პირის ბრალი", "number": "RG000001", "status": "accepted", "claim_id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "opened_on": "2026-08-10", "basis_date": null, "basis_kind": null, "created_at": "2026-10-22T23:42:10.80471+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "97f8cc2e-5445-78a1-e87c-fa7df2462e6f", "basis_number": null}
125	2026-10-22 23:42:10.844639+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_movement	aa02377b-6fd4-45ad-b706-3e1ea66d627e	insert	\N	{"id": "aa02377b-6fd4-45ad-b706-3e1ea66d627e", "kind": "received", "note": "პირველი თანხა", "case_id": "5a6762b2-1968-4599-8d4e-94ec83281741", "payer_id": "d8d5b123-1e9d-4c06-bce2-776f5e7048e3", "booked_on": "2026-09-15", "amount_gel": 500.00, "created_at": "2026-10-22T23:42:10.844639+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "a7a7efb7-0a99-94f8-2038-5c1db9261e2d", "document_date": "2026-09-15", "document_kind": "bank_statement", "document_number": "BANK-REG-1", "claim_recovery_id": "bc875600-20bf-4d2c-a5a7-d1334aba396a", "reverses_movement_id": null}
126	2026-10-22 23:42:10.844639+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	subrogation_case	5a6762b2-1968-4599-8d4e-94ec83281741	update	{"id": "5a6762b2-1968-4599-8d4e-94ec83281741", "note": "მესამე პირის ბრალი", "number": "RG000001", "status": "accepted", "claim_id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "opened_on": "2026-08-10", "basis_date": null, "basis_kind": null, "created_at": "2026-10-22T23:42:10.80471+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "97f8cc2e-5445-78a1-e87c-fa7df2462e6f", "basis_number": null}	{"id": "5a6762b2-1968-4599-8d4e-94ec83281741", "note": "მესამე პირის ბრალი", "number": "RG000001", "status": "partially_received", "claim_id": "df2fe01b-405b-44c2-95df-a0eb6e9953d5", "opened_on": "2026-08-10", "basis_date": null, "basis_kind": null, "created_at": "2026-10-22T23:42:10.80471+00:00", "created_by": "d85d4e8c-e844-40f2-9e9f-ae180d288ab2", "request_key": "97f8cc2e-5445-78a1-e87c-fa7df2462e6f", "basis_number": null}
127	2026-10-22 23:42:10.868296+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	sanctions_screening	513cb186-9211-4228-bbf2-041769dc3d80	insert	\N	{"id": "513cb186-9211-4228-bbf2-041769dc3d80", "note": null, "matches": [], "outcome": "clear", "party_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "created_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "decided_at": null, "decided_by": null, "subject_id": "f8194c08-4581-429c-863c-56a54b6e90e0", "screened_at": "2026-10-22T23:42:10.868296+00:00", "subject_kind": "party", "list_generated_at": "2026-09-01T00:00:00+00:00"}
128	2026-10-22 23:42:10.868296+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	party	f8194c08-4581-429c-863c-56a54b6e90e0	update	{"id": "f8194c08-4581-429c-863c-56a54b6e90e0", "kind": "person", "name": null, "email": "01001010101@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010101", "segment": "retail", "last_name": "ტესტიშვილი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.66571+00:00", "first_name": "გიორგი", "foreign_id": null, "legal_form": null, "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.728825+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ტესტიშვილი გიორგი", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": null}	{"id": "f8194c08-4581-429c-863c-56a54b6e90e0", "kind": "person", "name": null, "email": "01001010101@example.ge", "phone": "+995 599123456", "gender": "male", "is_pep": false, "tax_id": "01001010101", "segment": "retail", "last_name": "ტესტიშვილი", "birth_date": "1985-04-12", "created_at": "2026-10-22T23:42:09.66571+00:00", "first_name": "გიორგი", "foreign_id": null, "legal_form": null, "risk_level": "medium", "updated_at": "2026-10-22T23:42:09.728825+00:00", "is_resident": true, "verified_at": null, "country_code": "GE", "display_name": "ტესტიშვილი გიორგი", "identified_at": null, "legal_address": "თბილისი, ტესტის ქ. 1", "actual_address": null, "risk_review_on": "2028-10-23", "is_group_member": false, "is_public_entity": false, "risk_assessed_on": "2026-10-23", "sanctions_blocked": false, "registration_place": null, "self_declaration_on": null, "state_share_percent": null, "sanctions_checked_at": "2026-10-22"}
129	2026-10-22 23:42:10.957907+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	accounting_period	2026-01-01	insert	\N	{"status": "closed", "closed_at": "2026-10-22T23:42:10.957907+00:00", "closed_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "period_start": "2026-01-01"}
130	2026-10-22 23:42:10.973412+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	accounting_period	2026-02-01	insert	\N	{"status": "closed", "closed_at": "2026-10-22T23:42:10.973412+00:00", "closed_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "period_start": "2026-02-01"}
131	2026-10-22 23:42:10.985647+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	accounting_period	2026-03-01	insert	\N	{"status": "closed", "closed_at": "2026-10-22T23:42:10.985647+00:00", "closed_by": "e7b65221-8d6a-4e2e-98c2-65d12e3bfa75", "period_start": "2026-03-01"}
\.


--
-- Data for Name: branch; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.branch (id, code, name_ka) FROM stdin;
a715a88c-9080-470a-8826-c682a446d758	HQ	სათავო ოფისი
\.


--
-- Data for Name: claim; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim (id, number, policy_id, policy_line_id, currency, occurred_on, occurred_place, reported_on, description, status, claimed_amount, approved_amount, deductible_amount, is_total_loss, rejection_reason, closed_on, created_at, created_by, loss_event_id) FROM stdin;
df2fe01b-405b-44c2-95df-a0eb6e9953d5	CLM000001	a72126a1-0ec2-41d5-94d1-21b3c431814b	0ebf5941-4daa-4007-adbd-b844a614038f	GEL	2026-06-10	თბილისი	2026-06-11	შეჯახება გზაჯვარედინზე	paid	6000.00	4500.00	300.00	f	\N	\N	2026-10-22 23:42:10.19982+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	139519c6-e471-4267-85ac-f841d74e7c93
007668d6-63b2-42e2-a885-780f8b9f861d	CLM000002	5409b7cc-17bf-4804-b8d1-2b9cf5149520	74cc6832-5506-42c5-ab4a-9e7e318e583c	GEL	2026-08-05	რუსთავი	2026-08-06	სრული განადგურება	approved	15000.00	15000.00	300.00	f	\N	\N	2026-10-22 23:42:10.438222+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	5fa64b35-ef1d-43b6-b3f7-78c03b31e882
\.


--
-- Data for Name: claim_cession; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim_cession (claim_id, treaty_id, share_percent, priority, treaty_limit) FROM stdin;
df2fe01b-405b-44c2-95df-a0eb6e9953d5	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	\N	\N
007668d6-63b2-42e2-a885-780f8b9f861d	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	\N	\N
\.


--
-- Data for Name: claim_payment; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim_payment (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_payment_id, note, created_at, created_by, kind, reverses_claim_payment_id, reversal_reason, returned_on, bank_reference, request_id) FROM stdin;
3ace7f40-135b-4d02-b6cd-abe82f529d2a	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-07-01	2000.00	300.00	1700.00	0.00	0.00	1.000000	policyholder	592c2a48-9d6f-4850-a21d-e9d8752f03e2	6dcf6819-b717-454b-9dd1-883d09be6605	პირველი ნაწილი	2026-10-22 23:42:10.240371+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	payment	\N	\N	\N	\N	\N
41b5751a-10a1-4993-a637-0e6effe415de	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-07-15	2500.00	0.00	0.00	2500.00	2500.00	1.000000	policyholder	592c2a48-9d6f-4850-a21d-e9d8752f03e2	\N	მეორე ნაწილი	2026-10-22 23:42:10.272002+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	payment	\N	\N	\N	\N	\N
ad669ccf-6854-4ce1-9fee-7a450f9036ac	007668d6-63b2-42e2-a885-780f8b9f861d	2026-08-20	12000.00	300.00	0.00	11700.00	11700.00	1.000000	policyholder	f8194c08-4581-429c-863c-56a54b6e90e0	\N	ავტორიზაციით	2026-10-22 23:42:10.473583+00	deb1b458-9189-473e-b86e-047bb138fa0a	payment	\N	\N	\N	\N	42b87c04-0d7b-48ce-b38a-107c53609492
01765775-dd64-4faa-9bbf-321fd18fb3a4	007668d6-63b2-42e2-a885-780f8b9f861d	2026-08-21	-12000.00	-300.00	0.00	-11700.00	-11700.00	1.000000	policyholder	f8194c08-4581-429c-863c-56a54b6e90e0	\N	არასწორი თანხა	2026-10-22 23:42:10.49267+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	reversal	ad669ccf-6854-4ce1-9fee-7a450f9036ac	correction	\N	\N	\N
\.


--
-- Data for Name: claim_payment_request; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim_payment_request (id, claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id, offset_premium, total_loss_mode, note, created_at, created_by) FROM stdin;
42b87c04-0d7b-48ce-b38a-107c53609492	007668d6-63b2-42e2-a885-780f8b9f861d	2026-08-20	12000.00	300.00	0.00	11700.00	11700.00	1.000000	policyholder	f8194c08-4581-429c-863c-56a54b6e90e0	f	none	ავტორიზაციით	2026-10-22 23:42:10.463924+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
\.


--
-- Data for Name: claim_recovery; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim_recovery (id, claim_id, kind, received_on, amount_gel, note, created_at, created_by, subrogation_case_id, row_kind, reverses_claim_recovery_id, bank_reference) FROM stdin;
a8fb2317-eb78-4b40-82db-0359ef176ba2	df2fe01b-405b-44c2-95df-a0eb6e9953d5	salvage	2026-08-01	350.00	ნაშთის რეალიზაცია	2026-10-22 23:42:10.29108+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	\N	receipt	\N	\N
bc875600-20bf-4d2c-a5a7-d1334aba396a	df2fe01b-405b-44c2-95df-a0eb6e9953d5	subrogation	2026-09-15	500.00	პირველი თანხა	2026-10-22 23:42:10.844639+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	5a6762b2-1968-4599-8d4e-94ec83281741	receipt	\N	BANK-REG-1
\.


--
-- Data for Name: claim_reserve; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim_reserve (id, claim_id, booked_on, change, reason, created_at, created_by, request_id) FROM stdin;
06e51175-f816-4a9e-81ce-6eba79610745	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-06-11	5000.00	პირველადი რეზერვი	2026-10-22 23:42:10.19982+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	\N
ecfa7098-1a72-49a0-8a56-d90c34400b2a	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-10-23	-500.00	დამტკიცება	2026-10-22 23:42:10.225406+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	\N
160622c9-4248-4413-8eb2-0ef50b95ba1c	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-07-01	-2000.00	ანაზღაურების რეგისტრაცია	2026-10-22 23:42:10.240371+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	\N
e0427bf8-b00f-4ba6-b923-badda4812ca1	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-07-15	-2500.00	ანაზღაურების რეგისტრაცია	2026-10-22 23:42:10.272002+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	\N
f809372d-4c88-40c2-8c70-0ac83e16f212	007668d6-63b2-42e2-a885-780f8b9f861d	2026-08-06	15000.00	პირველადი რეზერვი	2026-10-22 23:42:10.438222+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	\N
cc261569-4292-426a-af80-89266b33c2c8	007668d6-63b2-42e2-a885-780f8b9f861d	2026-08-20	-12000.00	ანაზღაურების რეგისტრაცია	2026-10-22 23:42:10.473583+00	deb1b458-9189-473e-b86e-047bb138fa0a	\N
2c1d869f-caba-47b7-ab76-d52ad531dde7	007668d6-63b2-42e2-a885-780f8b9f861d	2026-08-21	12000.00	ანაზღაურების შებრუნება	2026-10-22 23:42:10.49267+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	\N
\.


--
-- Data for Name: claim_reserve_request; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.claim_reserve_request (id, claim_id, booked_on, base_level, new_level, change, reason, created_at, created_by) FROM stdin;
9220a800-060e-4b64-911b-689cb3c10d36	df2fe01b-405b-44c2-95df-a0eb6e9953d5	2026-06-20	5000.00	4500.00	-500.00	შეფასების შემდეგ	2026-10-22 23:42:10.218306+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
\.


--
-- Data for Name: complaint; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.complaint (id, number, received_on, channel, party_id, customer_name, customer_contact, policy_id, claim_id, category, description, status, acknowledged_at, responded_on, response_text, measures, outcome, note, created_at, created_by) FROM stdin;
a67ef776-52a7-4c8c-bf6e-3552d70c83f6	PR000001	2026-07-02	written	f8194c08-4581-429c-863c-56a54b6e90e0	გიორგი ტესტიშვილი	+995 599 12 34 56	5409b7cc-17bf-4804-b8d1-2b9cf5149520	\N	2	გადახდის დაგვიანება	partly_upheld	2026-10-22 23:42:10.607396+00	2026-07-20	გადახდა აღდგა	პროცესის შემოწმება	ნაწილობრივ	\N	2026-10-22 23:42:10.300943+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
\.


--
-- Data for Name: contract; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.contract (id, number, signed_on, policyholder_id, branch_id, created_at, created_by) FROM stdin;
fd887ebc-e174-4e02-84dc-9e471eba30f0	C000001	2026-10-23	f8194c08-4581-429c-863c-56a54b6e90e0	\N	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f	C000002	2026-10-23	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	\N	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
eff8795e-b21d-407e-b01f-ec037e47215b	C000003	2026-10-23	86b38924-7dc9-4d1d-864a-292366679667	\N	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
139d9a61-e9f8-4853-b157-3f6a63bd13e0	C000004	2026-10-23	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	\N	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
562aa8ea-8b53-4c26-add1-abac31ea7133	C000005	2026-10-23	592c2a48-9d6f-4850-a21d-e9d8752f03e2	\N	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
004067a8-8b4c-43b3-968d-2dc6be2db74a	C000006	2026-10-23	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	\N	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
e710c511-3698-46a6-96d9-cb18c5dee966	C000007	2026-10-23	f8194c08-4581-429c-863c-56a54b6e90e0	\N	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
\.


--
-- Data for Name: currency; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.currency (code, name_ka, minor_units) FROM stdin;
GEL	ლარი	2
USD	აშშ დოლარი	2
EUR	ევრო	2
\.


--
-- Data for Name: exchange_rate; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.exchange_rate (currency, rate_date, rate) FROM stdin;
USD	2025-01-01	2.680101
EUR	2025-01-01	2.930101
USD	2025-01-02	2.680202
EUR	2025-01-02	2.930202
USD	2025-01-03	2.680303
EUR	2025-01-03	2.930303
USD	2025-01-04	2.680404
EUR	2025-01-04	2.930404
USD	2025-01-05	2.680505
EUR	2025-01-05	2.930505
USD	2025-01-06	2.680606
EUR	2025-01-06	2.930606
USD	2025-01-07	2.680707
EUR	2025-01-07	2.930707
USD	2025-01-08	2.680808
EUR	2025-01-08	2.930808
USD	2025-01-09	2.680909
EUR	2025-01-09	2.930909
USD	2025-01-10	2.681010
EUR	2025-01-10	2.931010
USD	2025-01-11	2.681111
EUR	2025-01-11	2.931111
USD	2025-01-12	2.681212
EUR	2025-01-12	2.931212
USD	2025-01-13	2.681313
EUR	2025-01-13	2.931313
USD	2025-01-14	2.681414
EUR	2025-01-14	2.931414
USD	2025-01-15	2.681515
EUR	2025-01-15	2.931515
USD	2025-01-16	2.681616
EUR	2025-01-16	2.931616
USD	2025-01-17	2.681717
EUR	2025-01-17	2.931717
USD	2025-01-18	2.681818
EUR	2025-01-18	2.931818
USD	2025-01-19	2.681919
EUR	2025-01-19	2.931919
USD	2025-01-20	2.682020
EUR	2025-01-20	2.932020
USD	2025-01-21	2.682121
EUR	2025-01-21	2.932121
USD	2025-01-22	2.682222
EUR	2025-01-22	2.932222
USD	2025-01-23	2.682323
EUR	2025-01-23	2.932323
USD	2025-01-24	2.682424
EUR	2025-01-24	2.932424
USD	2025-01-25	2.682525
EUR	2025-01-25	2.932525
USD	2025-01-26	2.682626
EUR	2025-01-26	2.932626
USD	2025-01-27	2.682727
EUR	2025-01-27	2.932727
USD	2025-01-28	2.682828
EUR	2025-01-28	2.932828
USD	2025-01-29	2.682929
EUR	2025-01-29	2.932929
USD	2025-01-30	2.683030
EUR	2025-01-30	2.933030
USD	2025-01-31	2.683131
EUR	2025-01-31	2.933131
USD	2025-02-01	2.683201
EUR	2025-02-01	2.933201
USD	2025-02-02	2.683302
EUR	2025-02-02	2.933302
USD	2025-02-03	2.683403
EUR	2025-02-03	2.933403
USD	2025-02-04	2.683504
EUR	2025-02-04	2.933504
USD	2025-02-05	2.683605
EUR	2025-02-05	2.933605
USD	2025-02-06	2.683706
EUR	2025-02-06	2.933706
USD	2025-02-07	2.683807
EUR	2025-02-07	2.933807
USD	2025-02-08	2.683908
EUR	2025-02-08	2.933908
USD	2025-02-09	2.684009
EUR	2025-02-09	2.934009
USD	2025-02-10	2.684110
EUR	2025-02-10	2.934110
USD	2025-02-11	2.684211
EUR	2025-02-11	2.934211
USD	2025-02-12	2.684312
EUR	2025-02-12	2.934312
USD	2025-02-13	2.684413
EUR	2025-02-13	2.934413
USD	2025-02-14	2.684514
EUR	2025-02-14	2.934514
USD	2025-02-15	2.684615
EUR	2025-02-15	2.934615
USD	2025-02-16	2.684716
EUR	2025-02-16	2.934716
USD	2025-02-17	2.684817
EUR	2025-02-17	2.934817
USD	2025-02-18	2.684918
EUR	2025-02-18	2.934918
USD	2025-02-19	2.685019
EUR	2025-02-19	2.935019
USD	2025-02-20	2.685120
EUR	2025-02-20	2.935120
USD	2025-02-21	2.685221
EUR	2025-02-21	2.935221
USD	2025-02-22	2.685322
EUR	2025-02-22	2.935322
USD	2025-02-23	2.685423
EUR	2025-02-23	2.935423
USD	2025-02-24	2.685524
EUR	2025-02-24	2.935524
USD	2025-02-25	2.685625
EUR	2025-02-25	2.935625
USD	2025-02-26	2.685726
EUR	2025-02-26	2.935726
USD	2025-02-27	2.685827
EUR	2025-02-27	2.935827
USD	2025-02-28	2.685928
EUR	2025-02-28	2.935928
USD	2025-03-01	2.686001
EUR	2025-03-01	2.936001
USD	2025-03-02	2.686102
EUR	2025-03-02	2.936102
USD	2025-03-03	2.686203
EUR	2025-03-03	2.936203
USD	2025-03-04	2.686304
EUR	2025-03-04	2.936304
USD	2025-03-05	2.686405
EUR	2025-03-05	2.936405
USD	2025-03-06	2.686506
EUR	2025-03-06	2.936506
USD	2025-03-07	2.686607
EUR	2025-03-07	2.936607
USD	2025-03-08	2.686708
EUR	2025-03-08	2.936708
USD	2025-03-09	2.686809
EUR	2025-03-09	2.936809
USD	2025-03-10	2.686910
EUR	2025-03-10	2.936910
USD	2025-03-11	2.687011
EUR	2025-03-11	2.937011
USD	2025-03-12	2.687112
EUR	2025-03-12	2.937112
USD	2025-03-13	2.687213
EUR	2025-03-13	2.937213
USD	2025-03-14	2.687314
EUR	2025-03-14	2.937314
USD	2025-03-15	2.687415
EUR	2025-03-15	2.937415
USD	2025-03-16	2.687516
EUR	2025-03-16	2.937516
USD	2025-03-17	2.687617
EUR	2025-03-17	2.937617
USD	2025-03-18	2.687718
EUR	2025-03-18	2.937718
USD	2025-03-19	2.687819
EUR	2025-03-19	2.937819
USD	2025-03-20	2.687920
EUR	2025-03-20	2.937920
USD	2025-03-21	2.688021
EUR	2025-03-21	2.938021
USD	2025-03-22	2.688122
EUR	2025-03-22	2.938122
USD	2025-03-23	2.688223
EUR	2025-03-23	2.938223
USD	2025-03-24	2.688324
EUR	2025-03-24	2.938324
USD	2025-03-25	2.688425
EUR	2025-03-25	2.938425
USD	2025-03-26	2.688526
EUR	2025-03-26	2.938526
USD	2025-03-27	2.688627
EUR	2025-03-27	2.938627
USD	2025-03-28	2.688728
EUR	2025-03-28	2.938728
USD	2025-03-29	2.688829
EUR	2025-03-29	2.938829
USD	2025-03-30	2.688930
EUR	2025-03-30	2.938930
USD	2025-03-31	2.689031
EUR	2025-03-31	2.939031
USD	2025-04-01	2.689101
EUR	2025-04-01	2.939101
USD	2025-04-02	2.689202
EUR	2025-04-02	2.939202
USD	2025-04-03	2.689303
EUR	2025-04-03	2.939303
USD	2025-04-04	2.689404
EUR	2025-04-04	2.939404
USD	2025-04-05	2.689505
EUR	2025-04-05	2.939505
USD	2025-04-06	2.689606
EUR	2025-04-06	2.939606
USD	2025-04-07	2.680007
EUR	2025-04-07	2.930007
USD	2025-04-08	2.680108
EUR	2025-04-08	2.930108
USD	2025-04-09	2.680209
EUR	2025-04-09	2.930209
USD	2025-04-10	2.680310
EUR	2025-04-10	2.930310
USD	2025-04-11	2.680411
EUR	2025-04-11	2.930411
USD	2025-04-12	2.680512
EUR	2025-04-12	2.930512
USD	2025-04-13	2.680613
EUR	2025-04-13	2.930613
USD	2025-04-14	2.680714
EUR	2025-04-14	2.930714
USD	2025-04-15	2.680815
EUR	2025-04-15	2.930815
USD	2025-04-16	2.680916
EUR	2025-04-16	2.930916
USD	2025-04-17	2.681017
EUR	2025-04-17	2.931017
USD	2025-04-18	2.681118
EUR	2025-04-18	2.931118
USD	2025-04-19	2.681219
EUR	2025-04-19	2.931219
USD	2025-04-20	2.681320
EUR	2025-04-20	2.931320
USD	2025-04-21	2.681421
EUR	2025-04-21	2.931421
USD	2025-04-22	2.681522
EUR	2025-04-22	2.931522
USD	2025-04-23	2.681623
EUR	2025-04-23	2.931623
USD	2025-04-24	2.681724
EUR	2025-04-24	2.931724
USD	2025-04-25	2.681825
EUR	2025-04-25	2.931825
USD	2025-04-26	2.681926
EUR	2025-04-26	2.931926
USD	2025-04-27	2.682027
EUR	2025-04-27	2.932027
USD	2025-04-28	2.682128
EUR	2025-04-28	2.932128
USD	2025-04-29	2.682229
EUR	2025-04-29	2.932229
USD	2025-04-30	2.682330
EUR	2025-04-30	2.932330
USD	2025-05-01	2.682401
EUR	2025-05-01	2.932401
USD	2025-05-02	2.682502
EUR	2025-05-02	2.932502
USD	2025-05-03	2.682603
EUR	2025-05-03	2.932603
USD	2025-05-04	2.682704
EUR	2025-05-04	2.932704
USD	2025-05-05	2.682805
EUR	2025-05-05	2.932805
USD	2025-05-06	2.682906
EUR	2025-05-06	2.932906
USD	2025-05-07	2.683007
EUR	2025-05-07	2.933007
USD	2025-05-08	2.683108
EUR	2025-05-08	2.933108
USD	2025-05-09	2.683209
EUR	2025-05-09	2.933209
USD	2025-05-10	2.683310
EUR	2025-05-10	2.933310
USD	2025-05-11	2.683411
EUR	2025-05-11	2.933411
USD	2025-05-12	2.683512
EUR	2025-05-12	2.933512
USD	2025-05-13	2.683613
EUR	2025-05-13	2.933613
USD	2025-05-14	2.683714
EUR	2025-05-14	2.933714
USD	2025-05-15	2.683815
EUR	2025-05-15	2.933815
USD	2025-05-16	2.683916
EUR	2025-05-16	2.933916
USD	2025-05-17	2.684017
EUR	2025-05-17	2.934017
USD	2025-05-18	2.684118
EUR	2025-05-18	2.934118
USD	2025-05-19	2.684219
EUR	2025-05-19	2.934219
USD	2025-05-20	2.684320
EUR	2025-05-20	2.934320
USD	2025-05-21	2.684421
EUR	2025-05-21	2.934421
USD	2025-05-22	2.684522
EUR	2025-05-22	2.934522
USD	2025-05-23	2.684623
EUR	2025-05-23	2.934623
USD	2025-05-24	2.684724
EUR	2025-05-24	2.934724
USD	2025-05-25	2.684825
EUR	2025-05-25	2.934825
USD	2025-05-26	2.684926
EUR	2025-05-26	2.934926
USD	2025-05-27	2.685027
EUR	2025-05-27	2.935027
USD	2025-05-28	2.685128
EUR	2025-05-28	2.935128
USD	2025-05-29	2.685229
EUR	2025-05-29	2.935229
USD	2025-05-30	2.685330
EUR	2025-05-30	2.935330
USD	2025-05-31	2.685431
EUR	2025-05-31	2.935431
USD	2025-06-01	2.685501
EUR	2025-06-01	2.935501
USD	2025-06-02	2.685602
EUR	2025-06-02	2.935602
USD	2025-06-03	2.685703
EUR	2025-06-03	2.935703
USD	2025-06-04	2.685804
EUR	2025-06-04	2.935804
USD	2025-06-05	2.685905
EUR	2025-06-05	2.935905
USD	2025-06-06	2.686006
EUR	2025-06-06	2.936006
USD	2025-06-07	2.686107
EUR	2025-06-07	2.936107
USD	2025-06-08	2.686208
EUR	2025-06-08	2.936208
USD	2025-06-09	2.686309
EUR	2025-06-09	2.936309
USD	2025-06-10	2.686410
EUR	2025-06-10	2.936410
USD	2025-06-11	2.686511
EUR	2025-06-11	2.936511
USD	2025-06-12	2.686612
EUR	2025-06-12	2.936612
USD	2025-06-13	2.686713
EUR	2025-06-13	2.936713
USD	2025-06-14	2.686814
EUR	2025-06-14	2.936814
USD	2025-06-15	2.686915
EUR	2025-06-15	2.936915
USD	2025-06-16	2.687016
EUR	2025-06-16	2.937016
USD	2025-06-17	2.687117
EUR	2025-06-17	2.937117
USD	2025-06-18	2.687218
EUR	2025-06-18	2.937218
USD	2025-06-19	2.687319
EUR	2025-06-19	2.937319
USD	2025-06-20	2.687420
EUR	2025-06-20	2.937420
USD	2025-06-21	2.687521
EUR	2025-06-21	2.937521
USD	2025-06-22	2.687622
EUR	2025-06-22	2.937622
USD	2025-06-23	2.687723
EUR	2025-06-23	2.937723
USD	2025-06-24	2.687824
EUR	2025-06-24	2.937824
USD	2025-06-25	2.687925
EUR	2025-06-25	2.937925
USD	2025-06-26	2.688026
EUR	2025-06-26	2.938026
USD	2025-06-27	2.688127
EUR	2025-06-27	2.938127
USD	2025-06-28	2.688228
EUR	2025-06-28	2.938228
USD	2025-06-29	2.688329
EUR	2025-06-29	2.938329
USD	2025-06-30	2.688430
EUR	2025-06-30	2.938430
USD	2025-07-01	2.688501
EUR	2025-07-01	2.938501
USD	2025-07-02	2.688602
EUR	2025-07-02	2.938602
USD	2025-07-03	2.688703
EUR	2025-07-03	2.938703
USD	2025-07-04	2.688804
EUR	2025-07-04	2.938804
USD	2025-07-05	2.688905
EUR	2025-07-05	2.938905
USD	2025-07-06	2.689006
EUR	2025-07-06	2.939006
USD	2025-07-07	2.689107
EUR	2025-07-07	2.939107
USD	2025-07-08	2.689208
EUR	2025-07-08	2.939208
USD	2025-07-09	2.689309
EUR	2025-07-09	2.939309
USD	2025-07-10	2.689410
EUR	2025-07-10	2.939410
USD	2025-07-11	2.689511
EUR	2025-07-11	2.939511
USD	2025-07-12	2.689612
EUR	2025-07-12	2.939612
USD	2025-07-13	2.680013
EUR	2025-07-13	2.930013
USD	2025-07-14	2.680114
EUR	2025-07-14	2.930114
USD	2025-07-15	2.680215
EUR	2025-07-15	2.930215
USD	2025-07-16	2.680316
EUR	2025-07-16	2.930316
USD	2025-07-17	2.680417
EUR	2025-07-17	2.930417
USD	2025-07-18	2.680518
EUR	2025-07-18	2.930518
USD	2025-07-19	2.680619
EUR	2025-07-19	2.930619
USD	2025-07-20	2.680720
EUR	2025-07-20	2.930720
USD	2025-07-21	2.680821
EUR	2025-07-21	2.930821
USD	2025-07-22	2.680922
EUR	2025-07-22	2.930922
USD	2025-07-23	2.681023
EUR	2025-07-23	2.931023
USD	2025-07-24	2.681124
EUR	2025-07-24	2.931124
USD	2025-07-25	2.681225
EUR	2025-07-25	2.931225
USD	2025-07-26	2.681326
EUR	2025-07-26	2.931326
USD	2025-07-27	2.681427
EUR	2025-07-27	2.931427
USD	2025-07-28	2.681528
EUR	2025-07-28	2.931528
USD	2025-07-29	2.681629
EUR	2025-07-29	2.931629
USD	2025-07-30	2.681730
EUR	2025-07-30	2.931730
USD	2025-07-31	2.681831
EUR	2025-07-31	2.931831
USD	2025-08-01	2.681901
EUR	2025-08-01	2.931901
USD	2025-08-02	2.682002
EUR	2025-08-02	2.932002
USD	2025-08-03	2.682103
EUR	2025-08-03	2.932103
USD	2025-08-04	2.682204
EUR	2025-08-04	2.932204
USD	2025-08-05	2.682305
EUR	2025-08-05	2.932305
USD	2025-08-06	2.682406
EUR	2025-08-06	2.932406
USD	2025-08-07	2.682507
EUR	2025-08-07	2.932507
USD	2025-08-08	2.682608
EUR	2025-08-08	2.932608
USD	2025-08-09	2.682709
EUR	2025-08-09	2.932709
USD	2025-08-10	2.682810
EUR	2025-08-10	2.932810
USD	2025-08-11	2.682911
EUR	2025-08-11	2.932911
USD	2025-08-12	2.683012
EUR	2025-08-12	2.933012
USD	2025-08-13	2.683113
EUR	2025-08-13	2.933113
USD	2025-08-14	2.683214
EUR	2025-08-14	2.933214
USD	2025-08-15	2.683315
EUR	2025-08-15	2.933315
USD	2025-08-16	2.683416
EUR	2025-08-16	2.933416
USD	2025-08-17	2.683517
EUR	2025-08-17	2.933517
USD	2025-08-18	2.683618
EUR	2025-08-18	2.933618
USD	2025-08-19	2.683719
EUR	2025-08-19	2.933719
USD	2025-08-20	2.683820
EUR	2025-08-20	2.933820
USD	2025-08-21	2.683921
EUR	2025-08-21	2.933921
USD	2025-08-22	2.684022
EUR	2025-08-22	2.934022
USD	2025-08-23	2.684123
EUR	2025-08-23	2.934123
USD	2025-08-24	2.684224
EUR	2025-08-24	2.934224
USD	2025-08-25	2.684325
EUR	2025-08-25	2.934325
USD	2025-08-26	2.684426
EUR	2025-08-26	2.934426
USD	2025-08-27	2.684527
EUR	2025-08-27	2.934527
USD	2025-08-28	2.684628
EUR	2025-08-28	2.934628
USD	2025-08-29	2.684729
EUR	2025-08-29	2.934729
USD	2025-08-30	2.684830
EUR	2025-08-30	2.934830
USD	2025-08-31	2.684931
EUR	2025-08-31	2.934931
USD	2025-09-01	2.685001
EUR	2025-09-01	2.935001
USD	2025-09-02	2.685102
EUR	2025-09-02	2.935102
USD	2025-09-03	2.685203
EUR	2025-09-03	2.935203
USD	2025-09-04	2.685304
EUR	2025-09-04	2.935304
USD	2025-09-05	2.685405
EUR	2025-09-05	2.935405
USD	2025-09-06	2.685506
EUR	2025-09-06	2.935506
USD	2025-09-07	2.685607
EUR	2025-09-07	2.935607
USD	2025-09-08	2.685708
EUR	2025-09-08	2.935708
USD	2025-09-09	2.685809
EUR	2025-09-09	2.935809
USD	2025-09-10	2.685910
EUR	2025-09-10	2.935910
USD	2025-09-11	2.686011
EUR	2025-09-11	2.936011
USD	2025-09-12	2.686112
EUR	2025-09-12	2.936112
USD	2025-09-13	2.686213
EUR	2025-09-13	2.936213
USD	2025-09-14	2.686314
EUR	2025-09-14	2.936314
USD	2025-09-15	2.686415
EUR	2025-09-15	2.936415
USD	2025-09-16	2.686516
EUR	2025-09-16	2.936516
USD	2025-09-17	2.686617
EUR	2025-09-17	2.936617
USD	2025-09-18	2.686718
EUR	2025-09-18	2.936718
USD	2025-09-19	2.686819
EUR	2025-09-19	2.936819
USD	2025-09-20	2.686920
EUR	2025-09-20	2.936920
USD	2025-09-21	2.687021
EUR	2025-09-21	2.937021
USD	2025-09-22	2.687122
EUR	2025-09-22	2.937122
USD	2025-09-23	2.687223
EUR	2025-09-23	2.937223
USD	2025-09-24	2.687324
EUR	2025-09-24	2.937324
USD	2025-09-25	2.687425
EUR	2025-09-25	2.937425
USD	2025-09-26	2.687526
EUR	2025-09-26	2.937526
USD	2025-09-27	2.687627
EUR	2025-09-27	2.937627
USD	2025-09-28	2.687728
EUR	2025-09-28	2.937728
USD	2025-09-29	2.687829
EUR	2025-09-29	2.937829
USD	2025-09-30	2.687930
EUR	2025-09-30	2.937930
USD	2025-10-01	2.688001
EUR	2025-10-01	2.938001
USD	2025-10-02	2.688102
EUR	2025-10-02	2.938102
USD	2025-10-03	2.688203
EUR	2025-10-03	2.938203
USD	2025-10-04	2.688304
EUR	2025-10-04	2.938304
USD	2025-10-05	2.688405
EUR	2025-10-05	2.938405
USD	2025-10-06	2.688506
EUR	2025-10-06	2.938506
USD	2025-10-07	2.688607
EUR	2025-10-07	2.938607
USD	2025-10-08	2.688708
EUR	2025-10-08	2.938708
USD	2025-10-09	2.688809
EUR	2025-10-09	2.938809
USD	2025-10-10	2.688910
EUR	2025-10-10	2.938910
USD	2025-10-11	2.689011
EUR	2025-10-11	2.939011
USD	2025-10-12	2.689112
EUR	2025-10-12	2.939112
USD	2025-10-13	2.689213
EUR	2025-10-13	2.939213
USD	2025-10-14	2.689314
EUR	2025-10-14	2.939314
USD	2025-10-15	2.689415
EUR	2025-10-15	2.939415
USD	2025-10-16	2.689516
EUR	2025-10-16	2.939516
USD	2025-10-17	2.689617
EUR	2025-10-17	2.939617
USD	2025-10-18	2.680018
EUR	2025-10-18	2.930018
USD	2025-10-19	2.680119
EUR	2025-10-19	2.930119
USD	2025-10-20	2.680220
EUR	2025-10-20	2.930220
USD	2025-10-21	2.680321
EUR	2025-10-21	2.930321
USD	2025-10-22	2.680422
EUR	2025-10-22	2.930422
USD	2025-10-23	2.680523
EUR	2025-10-23	2.930523
USD	2025-10-24	2.680624
EUR	2025-10-24	2.930624
USD	2025-10-25	2.680725
EUR	2025-10-25	2.930725
USD	2025-10-26	2.680826
EUR	2025-10-26	2.930826
USD	2025-10-27	2.680927
EUR	2025-10-27	2.930927
USD	2025-10-28	2.681028
EUR	2025-10-28	2.931028
USD	2025-10-29	2.681129
EUR	2025-10-29	2.931129
USD	2025-10-30	2.681230
EUR	2025-10-30	2.931230
USD	2025-10-31	2.681331
EUR	2025-10-31	2.931331
USD	2025-11-01	2.681401
EUR	2025-11-01	2.931401
USD	2025-11-02	2.681502
EUR	2025-11-02	2.931502
USD	2025-11-03	2.681603
EUR	2025-11-03	2.931603
USD	2025-11-04	2.681704
EUR	2025-11-04	2.931704
USD	2025-11-05	2.681805
EUR	2025-11-05	2.931805
USD	2025-11-06	2.681906
EUR	2025-11-06	2.931906
USD	2025-11-07	2.682007
EUR	2025-11-07	2.932007
USD	2025-11-08	2.682108
EUR	2025-11-08	2.932108
USD	2025-11-09	2.682209
EUR	2025-11-09	2.932209
USD	2025-11-10	2.682310
EUR	2025-11-10	2.932310
USD	2025-11-11	2.682411
EUR	2025-11-11	2.932411
USD	2025-11-12	2.682512
EUR	2025-11-12	2.932512
USD	2025-11-13	2.682613
EUR	2025-11-13	2.932613
USD	2025-11-14	2.682714
EUR	2025-11-14	2.932714
USD	2025-11-15	2.682815
EUR	2025-11-15	2.932815
USD	2025-11-16	2.682916
EUR	2025-11-16	2.932916
USD	2025-11-17	2.683017
EUR	2025-11-17	2.933017
USD	2025-11-18	2.683118
EUR	2025-11-18	2.933118
USD	2025-11-19	2.683219
EUR	2025-11-19	2.933219
USD	2025-11-20	2.683320
EUR	2025-11-20	2.933320
USD	2025-11-21	2.683421
EUR	2025-11-21	2.933421
USD	2025-11-22	2.683522
EUR	2025-11-22	2.933522
USD	2025-11-23	2.683623
EUR	2025-11-23	2.933623
USD	2025-11-24	2.683724
EUR	2025-11-24	2.933724
USD	2025-11-25	2.683825
EUR	2025-11-25	2.933825
USD	2025-11-26	2.683926
EUR	2025-11-26	2.933926
USD	2025-11-27	2.684027
EUR	2025-11-27	2.934027
USD	2025-11-28	2.684128
EUR	2025-11-28	2.934128
USD	2025-11-29	2.684229
EUR	2025-11-29	2.934229
USD	2025-11-30	2.684330
EUR	2025-11-30	2.934330
USD	2025-12-01	2.684401
EUR	2025-12-01	2.934401
USD	2025-12-02	2.684502
EUR	2025-12-02	2.934502
USD	2025-12-03	2.684603
EUR	2025-12-03	2.934603
USD	2025-12-04	2.684704
EUR	2025-12-04	2.934704
USD	2025-12-05	2.684805
EUR	2025-12-05	2.934805
USD	2025-12-06	2.684906
EUR	2025-12-06	2.934906
USD	2025-12-07	2.685007
EUR	2025-12-07	2.935007
USD	2025-12-08	2.685108
EUR	2025-12-08	2.935108
USD	2025-12-09	2.685209
EUR	2025-12-09	2.935209
USD	2025-12-10	2.685310
EUR	2025-12-10	2.935310
USD	2025-12-11	2.685411
EUR	2025-12-11	2.935411
USD	2025-12-12	2.685512
EUR	2025-12-12	2.935512
USD	2025-12-13	2.685613
EUR	2025-12-13	2.935613
USD	2025-12-14	2.685714
EUR	2025-12-14	2.935714
USD	2025-12-15	2.685815
EUR	2025-12-15	2.935815
USD	2025-12-16	2.685916
EUR	2025-12-16	2.935916
USD	2025-12-17	2.686017
EUR	2025-12-17	2.936017
USD	2025-12-18	2.686118
EUR	2025-12-18	2.936118
USD	2025-12-19	2.686219
EUR	2025-12-19	2.936219
USD	2025-12-20	2.686320
EUR	2025-12-20	2.936320
USD	2025-12-21	2.686421
EUR	2025-12-21	2.936421
USD	2025-12-22	2.686522
EUR	2025-12-22	2.936522
USD	2025-12-23	2.686623
EUR	2025-12-23	2.936623
USD	2025-12-24	2.686724
EUR	2025-12-24	2.936724
USD	2025-12-25	2.686825
EUR	2025-12-25	2.936825
USD	2025-12-26	2.686926
EUR	2025-12-26	2.936926
USD	2025-12-27	2.687027
EUR	2025-12-27	2.937027
USD	2025-12-28	2.687128
EUR	2025-12-28	2.937128
USD	2025-12-29	2.687229
EUR	2025-12-29	2.937229
USD	2025-12-30	2.687330
EUR	2025-12-30	2.937330
USD	2025-12-31	2.687431
EUR	2025-12-31	2.937431
USD	2026-01-01	2.680101
EUR	2026-01-01	2.930101
USD	2026-01-02	2.680202
EUR	2026-01-02	2.930202
USD	2026-01-03	2.680303
EUR	2026-01-03	2.930303
USD	2026-01-04	2.680404
EUR	2026-01-04	2.930404
USD	2026-01-05	2.680505
EUR	2026-01-05	2.930505
USD	2026-01-06	2.680606
EUR	2026-01-06	2.930606
USD	2026-01-07	2.680707
EUR	2026-01-07	2.930707
USD	2026-01-08	2.680808
EUR	2026-01-08	2.930808
USD	2026-01-09	2.680909
EUR	2026-01-09	2.930909
USD	2026-01-10	2.681010
EUR	2026-01-10	2.931010
USD	2026-01-11	2.681111
EUR	2026-01-11	2.931111
USD	2026-01-12	2.681212
EUR	2026-01-12	2.931212
USD	2026-01-13	2.681313
EUR	2026-01-13	2.931313
USD	2026-01-14	2.681414
EUR	2026-01-14	2.931414
USD	2026-01-15	2.681515
EUR	2026-01-15	2.931515
USD	2026-01-16	2.681616
EUR	2026-01-16	2.931616
USD	2026-01-17	2.681717
EUR	2026-01-17	2.931717
USD	2026-01-18	2.681818
EUR	2026-01-18	2.931818
USD	2026-01-19	2.681919
EUR	2026-01-19	2.931919
USD	2026-01-20	2.682020
EUR	2026-01-20	2.932020
USD	2026-01-21	2.682121
EUR	2026-01-21	2.932121
USD	2026-01-22	2.682222
EUR	2026-01-22	2.932222
USD	2026-01-23	2.682323
EUR	2026-01-23	2.932323
USD	2026-01-24	2.682424
EUR	2026-01-24	2.932424
USD	2026-01-25	2.682525
EUR	2026-01-25	2.932525
USD	2026-01-26	2.682626
EUR	2026-01-26	2.932626
USD	2026-01-27	2.682727
EUR	2026-01-27	2.932727
USD	2026-01-28	2.682828
EUR	2026-01-28	2.932828
USD	2026-01-29	2.682929
EUR	2026-01-29	2.932929
USD	2026-01-30	2.683030
EUR	2026-01-30	2.933030
USD	2026-01-31	2.683131
EUR	2026-01-31	2.933131
USD	2026-02-01	2.683201
EUR	2026-02-01	2.933201
USD	2026-02-02	2.683302
EUR	2026-02-02	2.933302
USD	2026-02-03	2.683403
EUR	2026-02-03	2.933403
USD	2026-02-04	2.683504
EUR	2026-02-04	2.933504
USD	2026-02-05	2.683605
EUR	2026-02-05	2.933605
USD	2026-02-06	2.683706
EUR	2026-02-06	2.933706
USD	2026-02-07	2.683807
EUR	2026-02-07	2.933807
USD	2026-02-08	2.683908
EUR	2026-02-08	2.933908
USD	2026-02-09	2.684009
EUR	2026-02-09	2.934009
USD	2026-02-10	2.684110
EUR	2026-02-10	2.934110
USD	2026-02-11	2.684211
EUR	2026-02-11	2.934211
USD	2026-02-12	2.684312
EUR	2026-02-12	2.934312
USD	2026-02-13	2.684413
EUR	2026-02-13	2.934413
USD	2026-02-14	2.684514
EUR	2026-02-14	2.934514
USD	2026-02-15	2.684615
EUR	2026-02-15	2.934615
USD	2026-02-16	2.684716
EUR	2026-02-16	2.934716
USD	2026-02-17	2.684817
EUR	2026-02-17	2.934817
USD	2026-02-18	2.684918
EUR	2026-02-18	2.934918
USD	2026-02-19	2.685019
EUR	2026-02-19	2.935019
USD	2026-02-20	2.685120
EUR	2026-02-20	2.935120
USD	2026-02-21	2.685221
EUR	2026-02-21	2.935221
USD	2026-02-22	2.685322
EUR	2026-02-22	2.935322
USD	2026-02-23	2.685423
EUR	2026-02-23	2.935423
USD	2026-02-24	2.685524
EUR	2026-02-24	2.935524
USD	2026-02-25	2.685625
EUR	2026-02-25	2.935625
USD	2026-02-26	2.685726
EUR	2026-02-26	2.935726
USD	2026-02-27	2.685827
EUR	2026-02-27	2.935827
USD	2026-02-28	2.685928
EUR	2026-02-28	2.935928
USD	2026-03-01	2.686001
EUR	2026-03-01	2.936001
USD	2026-03-02	2.686102
EUR	2026-03-02	2.936102
USD	2026-03-03	2.686203
EUR	2026-03-03	2.936203
USD	2026-03-04	2.686304
EUR	2026-03-04	2.936304
USD	2026-03-05	2.686405
EUR	2026-03-05	2.936405
USD	2026-03-06	2.686506
EUR	2026-03-06	2.936506
USD	2026-03-07	2.686607
EUR	2026-03-07	2.936607
USD	2026-03-08	2.686708
EUR	2026-03-08	2.936708
USD	2026-03-09	2.686809
EUR	2026-03-09	2.936809
USD	2026-03-10	2.686910
EUR	2026-03-10	2.936910
USD	2026-03-11	2.687011
EUR	2026-03-11	2.937011
USD	2026-03-12	2.687112
EUR	2026-03-12	2.937112
USD	2026-03-13	2.687213
EUR	2026-03-13	2.937213
USD	2026-03-14	2.687314
EUR	2026-03-14	2.937314
USD	2026-03-15	2.687415
EUR	2026-03-15	2.937415
USD	2026-03-16	2.687516
EUR	2026-03-16	2.937516
USD	2026-03-17	2.687617
EUR	2026-03-17	2.937617
USD	2026-03-18	2.687718
EUR	2026-03-18	2.937718
USD	2026-03-19	2.687819
EUR	2026-03-19	2.937819
USD	2026-03-20	2.687920
EUR	2026-03-20	2.937920
USD	2026-03-21	2.688021
EUR	2026-03-21	2.938021
USD	2026-03-22	2.688122
EUR	2026-03-22	2.938122
USD	2026-03-23	2.688223
EUR	2026-03-23	2.938223
USD	2026-03-24	2.688324
EUR	2026-03-24	2.938324
USD	2026-03-25	2.688425
EUR	2026-03-25	2.938425
USD	2026-03-26	2.688526
EUR	2026-03-26	2.938526
USD	2026-03-27	2.688627
EUR	2026-03-27	2.938627
USD	2026-03-28	2.688728
EUR	2026-03-28	2.938728
USD	2026-03-29	2.688829
EUR	2026-03-29	2.938829
USD	2026-03-30	2.688930
EUR	2026-03-30	2.938930
USD	2026-03-31	2.689031
EUR	2026-03-31	2.939031
USD	2026-04-01	2.689101
EUR	2026-04-01	2.939101
USD	2026-04-02	2.689202
EUR	2026-04-02	2.939202
USD	2026-04-03	2.689303
EUR	2026-04-03	2.939303
USD	2026-04-04	2.689404
EUR	2026-04-04	2.939404
USD	2026-04-05	2.689505
EUR	2026-04-05	2.939505
USD	2026-04-06	2.689606
EUR	2026-04-06	2.939606
USD	2026-04-07	2.680007
EUR	2026-04-07	2.930007
USD	2026-04-08	2.680108
EUR	2026-04-08	2.930108
USD	2026-04-09	2.680209
EUR	2026-04-09	2.930209
USD	2026-04-10	2.680310
EUR	2026-04-10	2.930310
USD	2026-04-11	2.680411
EUR	2026-04-11	2.930411
USD	2026-04-12	2.680512
EUR	2026-04-12	2.930512
USD	2026-04-13	2.680613
EUR	2026-04-13	2.930613
USD	2026-04-14	2.680714
EUR	2026-04-14	2.930714
USD	2026-04-15	2.680815
EUR	2026-04-15	2.930815
USD	2026-04-16	2.680916
EUR	2026-04-16	2.930916
USD	2026-04-17	2.681017
EUR	2026-04-17	2.931017
USD	2026-04-18	2.681118
EUR	2026-04-18	2.931118
USD	2026-04-19	2.681219
EUR	2026-04-19	2.931219
USD	2026-04-20	2.681320
EUR	2026-04-20	2.931320
USD	2026-04-21	2.681421
EUR	2026-04-21	2.931421
USD	2026-04-22	2.681522
EUR	2026-04-22	2.931522
USD	2026-04-23	2.681623
EUR	2026-04-23	2.931623
USD	2026-04-24	2.681724
EUR	2026-04-24	2.931724
USD	2026-04-25	2.681825
EUR	2026-04-25	2.931825
USD	2026-04-26	2.681926
EUR	2026-04-26	2.931926
USD	2026-04-27	2.682027
EUR	2026-04-27	2.932027
USD	2026-04-28	2.682128
EUR	2026-04-28	2.932128
USD	2026-04-29	2.682229
EUR	2026-04-29	2.932229
USD	2026-04-30	2.682330
EUR	2026-04-30	2.932330
USD	2026-05-01	2.682401
EUR	2026-05-01	2.932401
USD	2026-05-02	2.682502
EUR	2026-05-02	2.932502
USD	2026-05-03	2.682603
EUR	2026-05-03	2.932603
USD	2026-05-04	2.682704
EUR	2026-05-04	2.932704
USD	2026-05-05	2.682805
EUR	2026-05-05	2.932805
USD	2026-05-06	2.682906
EUR	2026-05-06	2.932906
USD	2026-05-07	2.683007
EUR	2026-05-07	2.933007
USD	2026-05-08	2.683108
EUR	2026-05-08	2.933108
USD	2026-05-09	2.683209
EUR	2026-05-09	2.933209
USD	2026-05-10	2.683310
EUR	2026-05-10	2.933310
USD	2026-05-11	2.683411
EUR	2026-05-11	2.933411
USD	2026-05-12	2.683512
EUR	2026-05-12	2.933512
USD	2026-05-13	2.683613
EUR	2026-05-13	2.933613
USD	2026-05-14	2.683714
EUR	2026-05-14	2.933714
USD	2026-05-15	2.683815
EUR	2026-05-15	2.933815
USD	2026-05-16	2.683916
EUR	2026-05-16	2.933916
USD	2026-05-17	2.684017
EUR	2026-05-17	2.934017
USD	2026-05-18	2.684118
EUR	2026-05-18	2.934118
USD	2026-05-19	2.684219
EUR	2026-05-19	2.934219
USD	2026-05-20	2.684320
EUR	2026-05-20	2.934320
USD	2026-05-21	2.684421
EUR	2026-05-21	2.934421
USD	2026-05-22	2.684522
EUR	2026-05-22	2.934522
USD	2026-05-23	2.684623
EUR	2026-05-23	2.934623
USD	2026-05-24	2.684724
EUR	2026-05-24	2.934724
USD	2026-05-25	2.684825
EUR	2026-05-25	2.934825
USD	2026-05-26	2.684926
EUR	2026-05-26	2.934926
USD	2026-05-27	2.685027
EUR	2026-05-27	2.935027
USD	2026-05-28	2.685128
EUR	2026-05-28	2.935128
USD	2026-05-29	2.685229
EUR	2026-05-29	2.935229
USD	2026-05-30	2.685330
EUR	2026-05-30	2.935330
USD	2026-05-31	2.685431
EUR	2026-05-31	2.935431
USD	2026-06-01	2.685501
EUR	2026-06-01	2.935501
USD	2026-06-02	2.685602
EUR	2026-06-02	2.935602
USD	2026-06-03	2.685703
EUR	2026-06-03	2.935703
USD	2026-06-04	2.685804
EUR	2026-06-04	2.935804
USD	2026-06-05	2.685905
EUR	2026-06-05	2.935905
USD	2026-06-06	2.686006
EUR	2026-06-06	2.936006
USD	2026-06-07	2.686107
EUR	2026-06-07	2.936107
USD	2026-06-08	2.686208
EUR	2026-06-08	2.936208
USD	2026-06-09	2.686309
EUR	2026-06-09	2.936309
USD	2026-06-10	2.686410
EUR	2026-06-10	2.936410
USD	2026-06-11	2.686511
EUR	2026-06-11	2.936511
USD	2026-06-12	2.686612
EUR	2026-06-12	2.936612
USD	2026-06-13	2.686713
EUR	2026-06-13	2.936713
USD	2026-06-14	2.686814
EUR	2026-06-14	2.936814
USD	2026-06-15	2.686915
EUR	2026-06-15	2.936915
USD	2026-06-16	2.687016
EUR	2026-06-16	2.937016
USD	2026-06-17	2.687117
EUR	2026-06-17	2.937117
USD	2026-06-18	2.687218
EUR	2026-06-18	2.937218
USD	2026-06-19	2.687319
EUR	2026-06-19	2.937319
USD	2026-06-20	2.687420
EUR	2026-06-20	2.937420
USD	2026-06-21	2.687521
EUR	2026-06-21	2.937521
USD	2026-06-22	2.687622
EUR	2026-06-22	2.937622
USD	2026-06-23	2.687723
EUR	2026-06-23	2.937723
USD	2026-06-24	2.687824
EUR	2026-06-24	2.937824
USD	2026-06-25	2.687925
EUR	2026-06-25	2.937925
USD	2026-06-26	2.688026
EUR	2026-06-26	2.938026
USD	2026-06-27	2.688127
EUR	2026-06-27	2.938127
USD	2026-06-28	2.688228
EUR	2026-06-28	2.938228
USD	2026-06-29	2.688329
EUR	2026-06-29	2.938329
USD	2026-06-30	2.688430
EUR	2026-06-30	2.938430
USD	2026-07-01	2.688501
EUR	2026-07-01	2.938501
USD	2026-07-02	2.688602
EUR	2026-07-02	2.938602
USD	2026-07-03	2.688703
EUR	2026-07-03	2.938703
USD	2026-07-04	2.688804
EUR	2026-07-04	2.938804
USD	2026-07-05	2.688905
EUR	2026-07-05	2.938905
USD	2026-07-06	2.689006
EUR	2026-07-06	2.939006
USD	2026-07-07	2.689107
EUR	2026-07-07	2.939107
USD	2026-07-08	2.689208
EUR	2026-07-08	2.939208
USD	2026-07-09	2.689309
EUR	2026-07-09	2.939309
USD	2026-07-10	2.689410
EUR	2026-07-10	2.939410
USD	2026-07-11	2.689511
EUR	2026-07-11	2.939511
USD	2026-07-12	2.689612
EUR	2026-07-12	2.939612
USD	2026-07-13	2.680013
EUR	2026-07-13	2.930013
USD	2026-07-14	2.680114
EUR	2026-07-14	2.930114
USD	2026-07-15	2.680215
EUR	2026-07-15	2.930215
USD	2026-07-16	2.680316
EUR	2026-07-16	2.930316
USD	2026-07-17	2.680417
EUR	2026-07-17	2.930417
USD	2026-07-18	2.680518
EUR	2026-07-18	2.930518
USD	2026-07-19	2.680619
EUR	2026-07-19	2.930619
USD	2026-07-20	2.680720
EUR	2026-07-20	2.930720
USD	2026-07-21	2.680821
EUR	2026-07-21	2.930821
USD	2026-07-22	2.680922
EUR	2026-07-22	2.930922
USD	2026-07-23	2.681023
EUR	2026-07-23	2.931023
USD	2026-07-24	2.681124
EUR	2026-07-24	2.931124
USD	2026-07-25	2.681225
EUR	2026-07-25	2.931225
USD	2026-07-26	2.681326
EUR	2026-07-26	2.931326
USD	2026-07-27	2.681427
EUR	2026-07-27	2.931427
USD	2026-07-28	2.681528
EUR	2026-07-28	2.931528
USD	2026-07-29	2.681629
EUR	2026-07-29	2.931629
USD	2026-07-30	2.681730
EUR	2026-07-30	2.931730
USD	2026-07-31	2.681831
EUR	2026-07-31	2.931831
USD	2026-08-01	2.681901
EUR	2026-08-01	2.931901
USD	2026-08-02	2.682002
EUR	2026-08-02	2.932002
USD	2026-08-03	2.682103
EUR	2026-08-03	2.932103
USD	2026-08-04	2.682204
EUR	2026-08-04	2.932204
USD	2026-08-05	2.682305
EUR	2026-08-05	2.932305
USD	2026-08-06	2.682406
EUR	2026-08-06	2.932406
USD	2026-08-07	2.682507
EUR	2026-08-07	2.932507
USD	2026-08-08	2.682608
EUR	2026-08-08	2.932608
USD	2026-08-09	2.682709
EUR	2026-08-09	2.932709
USD	2026-08-10	2.682810
EUR	2026-08-10	2.932810
USD	2026-08-11	2.682911
EUR	2026-08-11	2.932911
USD	2026-08-12	2.683012
EUR	2026-08-12	2.933012
USD	2026-08-13	2.683113
EUR	2026-08-13	2.933113
USD	2026-08-14	2.683214
EUR	2026-08-14	2.933214
USD	2026-08-15	2.683315
EUR	2026-08-15	2.933315
USD	2026-08-16	2.683416
EUR	2026-08-16	2.933416
USD	2026-08-17	2.683517
EUR	2026-08-17	2.933517
USD	2026-08-18	2.683618
EUR	2026-08-18	2.933618
USD	2026-08-19	2.683719
EUR	2026-08-19	2.933719
USD	2026-08-20	2.683820
EUR	2026-08-20	2.933820
USD	2026-08-21	2.683921
EUR	2026-08-21	2.933921
USD	2026-08-22	2.684022
EUR	2026-08-22	2.934022
USD	2026-08-23	2.684123
EUR	2026-08-23	2.934123
USD	2026-08-24	2.684224
EUR	2026-08-24	2.934224
USD	2026-08-25	2.684325
EUR	2026-08-25	2.934325
USD	2026-08-26	2.684426
EUR	2026-08-26	2.934426
USD	2026-08-27	2.684527
EUR	2026-08-27	2.934527
USD	2026-08-28	2.684628
EUR	2026-08-28	2.934628
USD	2026-08-29	2.684729
EUR	2026-08-29	2.934729
USD	2026-08-30	2.684830
EUR	2026-08-30	2.934830
USD	2026-08-31	2.684931
EUR	2026-08-31	2.934931
USD	2026-09-01	2.685001
EUR	2026-09-01	2.935001
USD	2026-09-02	2.685102
EUR	2026-09-02	2.935102
USD	2026-09-03	2.685203
EUR	2026-09-03	2.935203
USD	2026-09-04	2.685304
EUR	2026-09-04	2.935304
USD	2026-09-05	2.685405
EUR	2026-09-05	2.935405
USD	2026-09-06	2.685506
EUR	2026-09-06	2.935506
USD	2026-09-07	2.685607
EUR	2026-09-07	2.935607
USD	2026-09-08	2.685708
EUR	2026-09-08	2.935708
USD	2026-09-09	2.685809
EUR	2026-09-09	2.935809
USD	2026-09-10	2.685910
EUR	2026-09-10	2.935910
USD	2026-09-11	2.686011
EUR	2026-09-11	2.936011
USD	2026-09-12	2.686112
EUR	2026-09-12	2.936112
USD	2026-09-13	2.686213
EUR	2026-09-13	2.936213
USD	2026-09-14	2.686314
EUR	2026-09-14	2.936314
USD	2026-09-15	2.686415
EUR	2026-09-15	2.936415
USD	2026-09-16	2.686516
EUR	2026-09-16	2.936516
USD	2026-09-17	2.686617
EUR	2026-09-17	2.936617
USD	2026-09-18	2.686718
EUR	2026-09-18	2.936718
USD	2026-09-19	2.686819
EUR	2026-09-19	2.936819
USD	2026-09-20	2.686920
EUR	2026-09-20	2.936920
USD	2026-09-21	2.687021
EUR	2026-09-21	2.937021
USD	2026-09-22	2.687122
EUR	2026-09-22	2.937122
USD	2026-09-23	2.687223
EUR	2026-09-23	2.937223
USD	2026-09-24	2.687324
EUR	2026-09-24	2.937324
USD	2026-09-25	2.687425
EUR	2026-09-25	2.937425
USD	2026-09-26	2.687526
EUR	2026-09-26	2.937526
USD	2026-09-27	2.687627
EUR	2026-09-27	2.937627
USD	2026-09-28	2.687728
EUR	2026-09-28	2.937728
USD	2026-09-29	2.687829
EUR	2026-09-29	2.937829
USD	2026-09-30	2.687930
EUR	2026-09-30	2.937930
USD	2026-10-01	2.688001
EUR	2026-10-01	2.938001
USD	2026-10-02	2.688102
EUR	2026-10-02	2.938102
USD	2026-10-03	2.688203
EUR	2026-10-03	2.938203
USD	2026-10-04	2.688304
EUR	2026-10-04	2.938304
USD	2026-10-05	2.688405
EUR	2026-10-05	2.938405
USD	2026-10-06	2.688506
EUR	2026-10-06	2.938506
USD	2026-10-07	2.688607
EUR	2026-10-07	2.938607
USD	2026-10-08	2.688708
EUR	2026-10-08	2.938708
USD	2026-10-09	2.688809
EUR	2026-10-09	2.938809
USD	2026-10-10	2.688910
EUR	2026-10-10	2.938910
USD	2026-10-11	2.689011
EUR	2026-10-11	2.939011
USD	2026-10-12	2.689112
EUR	2026-10-12	2.939112
USD	2026-10-13	2.689213
EUR	2026-10-13	2.939213
USD	2026-10-14	2.689314
EUR	2026-10-14	2.939314
USD	2026-10-15	2.689415
EUR	2026-10-15	2.939415
USD	2026-10-16	2.689516
EUR	2026-10-16	2.939516
USD	2026-10-17	2.689617
EUR	2026-10-17	2.939617
USD	2026-10-18	2.680018
EUR	2026-10-18	2.930018
USD	2026-10-19	2.680119
EUR	2026-10-19	2.930119
USD	2026-10-20	2.680220
EUR	2026-10-20	2.930220
USD	2026-10-21	2.680321
EUR	2026-10-21	2.930321
USD	2026-10-22	2.680422
EUR	2026-10-22	2.930422
USD	2026-10-23	2.680523
EUR	2026-10-23	2.930523
USD	2026-10-24	2.680624
EUR	2026-10-24	2.930624
USD	2026-10-25	2.680725
EUR	2026-10-25	2.930725
USD	2026-10-26	2.680826
EUR	2026-10-26	2.930826
USD	2026-10-27	2.680927
EUR	2026-10-27	2.930927
USD	2026-10-28	2.681028
EUR	2026-10-28	2.931028
USD	2026-10-29	2.681129
EUR	2026-10-29	2.931129
USD	2026-10-30	2.681230
EUR	2026-10-30	2.931230
USD	2026-10-31	2.681331
EUR	2026-10-31	2.931331
USD	2026-11-01	2.681401
EUR	2026-11-01	2.931401
USD	2026-11-02	2.681502
EUR	2026-11-02	2.931502
USD	2026-11-03	2.681603
EUR	2026-11-03	2.931603
USD	2026-11-04	2.681704
EUR	2026-11-04	2.931704
USD	2026-11-05	2.681805
EUR	2026-11-05	2.931805
USD	2026-11-06	2.681906
EUR	2026-11-06	2.931906
USD	2026-11-07	2.682007
EUR	2026-11-07	2.932007
USD	2026-11-08	2.682108
EUR	2026-11-08	2.932108
USD	2026-11-09	2.682209
EUR	2026-11-09	2.932209
USD	2026-11-10	2.682310
EUR	2026-11-10	2.932310
USD	2026-11-11	2.682411
EUR	2026-11-11	2.932411
USD	2026-11-12	2.682512
EUR	2026-11-12	2.932512
USD	2026-11-13	2.682613
EUR	2026-11-13	2.932613
USD	2026-11-14	2.682714
EUR	2026-11-14	2.932714
USD	2026-11-15	2.682815
EUR	2026-11-15	2.932815
USD	2026-11-16	2.682916
EUR	2026-11-16	2.932916
USD	2026-11-17	2.683017
EUR	2026-11-17	2.933017
USD	2026-11-18	2.683118
EUR	2026-11-18	2.933118
USD	2026-11-19	2.683219
EUR	2026-11-19	2.933219
USD	2026-11-20	2.683320
EUR	2026-11-20	2.933320
USD	2026-11-21	2.683421
EUR	2026-11-21	2.933421
USD	2026-11-22	2.683522
EUR	2026-11-22	2.933522
USD	2026-11-23	2.683623
EUR	2026-11-23	2.933623
USD	2026-11-24	2.683724
EUR	2026-11-24	2.933724
USD	2026-11-25	2.683825
EUR	2026-11-25	2.933825
USD	2026-11-26	2.683926
EUR	2026-11-26	2.933926
USD	2026-11-27	2.684027
EUR	2026-11-27	2.934027
USD	2026-11-28	2.684128
EUR	2026-11-28	2.934128
USD	2026-11-29	2.684229
EUR	2026-11-29	2.934229
USD	2026-11-30	2.684330
EUR	2026-11-30	2.934330
USD	2026-12-01	2.684401
EUR	2026-12-01	2.934401
USD	2026-12-02	2.684502
EUR	2026-12-02	2.934502
USD	2026-12-03	2.684603
EUR	2026-12-03	2.934603
USD	2026-12-04	2.684704
EUR	2026-12-04	2.934704
USD	2026-12-05	2.684805
EUR	2026-12-05	2.934805
USD	2026-12-06	2.684906
EUR	2026-12-06	2.934906
USD	2026-12-07	2.685007
EUR	2026-12-07	2.935007
USD	2026-12-08	2.685108
EUR	2026-12-08	2.935108
USD	2026-12-09	2.685209
EUR	2026-12-09	2.935209
USD	2026-12-10	2.685310
EUR	2026-12-10	2.935310
USD	2026-12-11	2.685411
EUR	2026-12-11	2.935411
USD	2026-12-12	2.685512
EUR	2026-12-12	2.935512
USD	2026-12-13	2.685613
EUR	2026-12-13	2.935613
USD	2026-12-14	2.685714
EUR	2026-12-14	2.935714
USD	2026-12-15	2.685815
EUR	2026-12-15	2.935815
USD	2026-12-16	2.685916
EUR	2026-12-16	2.935916
USD	2026-12-17	2.686017
EUR	2026-12-17	2.936017
USD	2026-12-18	2.686118
EUR	2026-12-18	2.936118
USD	2026-12-19	2.686219
EUR	2026-12-19	2.936219
USD	2026-12-20	2.686320
EUR	2026-12-20	2.936320
USD	2026-12-21	2.686421
EUR	2026-12-21	2.936421
USD	2026-12-22	2.686522
EUR	2026-12-22	2.936522
USD	2026-12-23	2.686623
EUR	2026-12-23	2.936623
USD	2026-12-24	2.686724
EUR	2026-12-24	2.936724
USD	2026-12-25	2.686825
EUR	2026-12-25	2.936825
USD	2026-12-26	2.686926
EUR	2026-12-26	2.936926
USD	2026-12-27	2.687027
EUR	2026-12-27	2.937027
USD	2026-12-28	2.687128
EUR	2026-12-28	2.937128
USD	2026-12-29	2.687229
EUR	2026-12-29	2.937229
USD	2026-12-30	2.687330
EUR	2026-12-30	2.937330
USD	2026-12-31	2.687431
EUR	2026-12-31	2.937431
\.


--
-- Data for Name: fms_suspension; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.fms_suspension (id, party_id, case_id, kind, scope, transaction_ref, instructed_at, written_confirmed_at, ends_at, lifted_at, lift_reason, note, created_at, created_by, target_policy_id, target_claim_id, legacy_unmapped) FROM stdin;
bac62356-6618-4847-b82a-cd5a58394de8	86b38924-7dc9-4d1d-864a-292366679667	\N	oral	all	\N	2026-09-10 06:30:00+00	\N	2026-09-15 06:30:00+00	\N	\N	ფმს-ის ზეპირი მითითება	2026-10-22 23:42:10.560701+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	\N	\N	f
\.


--
-- Data for Name: installment; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.installment (id, policy_id, due_on, amount, debtor_kind) FROM stdin;
f2a01c1c-bc0e-41c0-85f7-4e0eedba0526	5409b7cc-17bf-4804-b8d1-2b9cf5149520	2026-01-10	375.00	client
9a9c5950-df88-413d-a82e-926b933efc13	5409b7cc-17bf-4804-b8d1-2b9cf5149520	2026-04-10	375.00	client
3f2f58f8-ba53-4fca-a9d8-2516141aed31	5409b7cc-17bf-4804-b8d1-2b9cf5149520	2026-07-10	375.00	client
4d596a71-9ee4-454c-8270-15122556ee86	5409b7cc-17bf-4804-b8d1-2b9cf5149520	2026-10-10	375.00	client
03d2e7f6-9f95-459e-a0de-5fc80f05f269	6e5fa754-2657-49fa-8420-670c99250948	2026-02-01	850.50	client
2a754539-0fb8-48d9-aa42-bce8ce20c19c	299a5381-4bac-49df-82f5-8dda6c323c55	2026-03-15	130.00	client
90caddb3-71ce-4433-8314-b580324ed31d	299a5381-4bac-49df-82f5-8dda6c323c55	2026-04-15	130.00	client
e11573e2-2478-4796-9aae-298fade34eeb	299a5381-4bac-49df-82f5-8dda6c323c55	2026-05-15	130.00	client
0fe91db3-5b4d-4eea-ae7a-adabac725b42	299a5381-4bac-49df-82f5-8dda6c323c55	2026-06-15	130.00	client
0dc27e9a-eff4-4596-9749-4e8e5e179bea	299a5381-4bac-49df-82f5-8dda6c323c55	2026-07-15	130.00	client
ee5cdc4d-b9bf-45ae-8431-de748811982e	299a5381-4bac-49df-82f5-8dda6c323c55	2026-08-15	130.00	client
9d18493c-e123-43aa-94da-c59844875212	299a5381-4bac-49df-82f5-8dda6c323c55	2026-09-15	130.00	client
cf3a3125-2218-44de-a572-efe04de96d65	299a5381-4bac-49df-82f5-8dda6c323c55	2026-10-15	130.00	client
2bf5e918-d356-45bd-9184-5bf90c25f9be	299a5381-4bac-49df-82f5-8dda6c323c55	2026-11-15	130.00	client
58fec97b-8b6f-42c5-b0a7-71bef7e05a91	299a5381-4bac-49df-82f5-8dda6c323c55	2026-12-15	130.00	client
b5cac111-4bc1-4b34-b5d5-d851c44ac589	299a5381-4bac-49df-82f5-8dda6c323c55	2027-01-15	130.00	client
19f0298e-311e-47b9-9320-a476379ceb63	299a5381-4bac-49df-82f5-8dda6c323c55	2027-02-15	130.00	client
e7db70dd-8d60-4f72-83a0-8250df6d834c	d26af7b9-b4a2-4ebd-99ce-056b92affe5a	2026-04-01	999.99	client
1c1f1f22-c12d-4e47-b660-80bc33114975	a72126a1-0ec2-41d5-94d1-21b3c431814b	2026-05-01	750.00	client
3266ed4f-d151-4c23-bb5d-1222326787c1	a72126a1-0ec2-41d5-94d1-21b3c431814b	2026-08-01	750.00	client
718b0622-ba8f-4d15-90a8-aeddaa1968d5	a72126a1-0ec2-41d5-94d1-21b3c431814b	2026-11-01	750.00	client
9a75a79f-6f32-4db8-805d-0871b82ee8b7	a72126a1-0ec2-41d5-94d1-21b3c431814b	2027-02-01	750.00	client
d31d7c94-b238-4a6d-b0fe-41374fce65e2	b0539124-f3d7-4c97-903a-49c820bc2c1d	2026-06-01	150.00	client
df455f7e-39f3-4f28-a1b0-c565e00d464c	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	2026-06-01	24000.00	client
355b51a8-1310-468e-9629-2f5d2674c7cc	032821d0-09ce-408c-aaab-93cd4c80438f	2026-09-01	15000.00	client
\.


--
-- Data for Name: installment_adjustment; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.installment_adjustment (id, installment_id, booked_on, change, reason, created_at, created_by) FROM stdin;
25656b24-fd07-4883-a436-d9d301903741	e7db70dd-8d60-4f72-83a0-8250df6d834c	2026-07-01	-750.68	გრაფიკის შემცირება გაუქმებით 2026-07-01	2026-10-22 23:42:10.122388+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
9432e295-1f82-4905-9674-6bf576e02544	df455f7e-39f3-4f28-a1b0-c565e00d464c	2026-06-20	-22750.68	გრაფიკის შემცირება გაუქმებით 2026-06-20	2026-10-22 23:42:10.360019+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
5b699b8e-5868-4477-9cc5-ad6127961d27	9a75a79f-6f32-4db8-805d-0871b82ee8b7	2026-09-01	-200.00	პრემიის შემცირება დამტკიცებული ცვლილებით CAS000008	2026-10-22 23:42:10.524061+00	f811b716-2a18-4fe9-b792-fb90f6665f39
\.


--
-- Data for Name: insurance_class; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.insurance_class (code, parent_code, name_ka, category, ibnr_percent, sort_order) FROM stdin;
1	\N	სიცოცხლის დაზღვევა	life	5.00	100
1.1	1	სიცოცხლის ვადიანი დაზღვევა	life	5.00	101
1.2	1	სიცოცხლის უვადო დაზღვევა	life	5.00	102
1.3	1	სიცოცხლის დაზღვევის მაგროვებადი და დაბრუნებადი სახეობები	life	5.00	103
1.4	1	სიცოცხლის დაზღვევის სხვა ფორმები	life	5.00	104
2	\N	სამოგზაურო დაზღვევა	non_life	5.00	200
3	\N	უბედური შემთხვევის დაზღვევა	non_life	5.00	300
3.1	3	დაზღვევა უბედური შემთხვევებისაგან	non_life	5.00	301
3.2	3	მგზავრების, მძღოლის ან ეკიპაჟის დაზღვევა უბედური შემთხვევებისაგან	non_life	5.00	302
4	\N	სამედიცინო (ჯანმრთელობის) დაზღვევა	non_life	2.00	400
5	\N	სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა (გარდა სარკინიგზო ტრანსპორტისა)	non_life	5.00	500
5.1	5	სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)	non_life	5.00	501
5.2	5	სხვა სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა	non_life	5.00	502
6	\N	სახმელეთო ტრანსპორტის გამოყენებასთან დაკავშირებული სამოქალაქო პასუხისმგებლობის დაზღვევა	non_life	5.00	600
6.1	6	ავტომოტოტრანსპორტის მფლობელთა სამოქალაქო პასუხისმგებლობის სავალდებულო დაზღვევა	non_life	5.00	601
6.2	6	სახმელეთო ტრანსპორტის გამოყენებასთან დაკავშირებული სამოქალაქო პასუხისმგებლობის დაზღვევა	non_life	5.00	602
6.3	6	სახმელეთო გადამზიდველის სამოქალაქო პასუხისმგებლობის დაზღვევა	non_life	5.00	603
7	\N	სარკინიგზო სატრანსპორტო საშუალებათა დაზღვევა	non_life	5.00	700
8	\N	საჰაერო სატრანსპორტო საშუალებათა დაზღვევა (კორპუსის დაზღვევა)	non_life	5.00	800
9	\N	საჰაერო სატრანსპორტო საშუალებათა გამოყენებასთან დაკავშირებული პასუხისმგებლობის დაზღვევა	non_life	5.00	900
9.1	9	საჰაერო გადამზიდველის პასუხისმგებლობის დაზღვევა	non_life	5.00	901
9.2	9	სხვა პასუხისმგებლობის დაზღვევა, რომელიც დაკავშირებულია საჰაერო ტრანსპორტის გამოყენებასთან	non_life	5.00	902
10	\N	მცურავი სატრანსპორტო საშუალებების დაზღვევა (კორპუსის დაზღვევა)	non_life	5.00	1000
11	\N	მცურავ სატრანსპორტო საშუალებათა გამოყენებასთან დაკავშირებული პასუხისმგებლობის დაზღვევა	non_life	5.00	1100
11.1	11	საზღვაო გადამზიდველის პასუხისმგებლობის დაზღვევა	non_life	5.00	1101
11.2	11	სხვა პასუხისმგებლობის დაზღვევა, რომელიც დაკავშირებულია მცურავი ტრანსპორტის გამოყენებასთან	non_life	5.00	1102
12	\N	ტვირთების დაზღვევა	non_life	5.00	1200
13	\N	ქონების დაზღვევა (გარდა პპ. (5), (7), (8), (10) და (12)-ში ჩამოთვლილი ქონებისა)	non_life	5.00	1300
14	\N	დაზღვევა საფინანსო დანაკარგებისაგან	non_life	5.00	1400
15	\N	ვალდებულებათა შესრულების დაზღვევა	non_life	5.00	1500
15.1	15	საბაჟო ვალდებულებების შესრულების გარანტიები/ფინანსური რისკის დაზღვევის პოლისები	non_life	5.00	1501
15.2	15	სახელმწიფო შესყიდვებთან დაკავშირებული გარანტიები	non_life	5.00	1502
15.3	15	სხვა სახის გარანტიები	non_life	5.00	1503
16	\N	საკრედიტო ვალდებულებათა დაზღვევა	non_life	5.00	1600
17	\N	სამოქალაქო პასუხისმგებლობის დაზღვევა (გარდა პპ. (6), (9), (11) სახეობებში ჩამოთვლილი პასუხისმგებლობისა)	non_life	5.00	1700
17.1	17	პროფესიული პასუხისმგებლობის დაზღვევა	non_life	5.00	1701
17.2	17	დამქირავებლის პასუხისმგებლობის დაზღვევა	non_life	5.00	1702
17.3	17	სხვა სახის პასუხისმგებლობის დაზღვევა	non_life	5.00	1703
18	\N	იურიდიული ხარჯების დაზღვევა	non_life	5.00	1800
\.


--
-- Data for Name: insured_object; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.insured_object (id, policy_id, object_type, description, attributes) FROM stdin;
c4429f21-927d-419c-9e12-b280c86693cb	5409b7cc-17bf-4804-b8d1-2b9cf5149520	vehicle	Toyota Prius AA-101-AA	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "AA-101-AA", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
c4291172-62fa-4811-8697-7404a3bb8cbd	6e5fa754-2657-49fa-8420-670c99250948	vehicle	Toyota Prius BB-202-BB	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "BB-202-BB", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "USD"}
92840a88-05c8-46c7-8bd4-d4b4e7f09498	299a5381-4bac-49df-82f5-8dda6c323c55	vehicle	Toyota Prius CC-303-CC	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "CC-303-CC", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "EUR"}
9200d829-cc92-4777-a39a-660638424200	d26af7b9-b4a2-4ebd-99ce-056b92affe5a	vehicle	Toyota Prius DD-404-DD	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "DD-404-DD", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
cc8df06f-ba09-45e5-99f3-07af8ce859af	a72126a1-0ec2-41d5-94d1-21b3c431814b	vehicle	Toyota Prius EE-505-EE	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "EE-505-EE", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
a1791b42-b7ce-4b7b-bad6-7c3b7a6355e6	b0539124-f3d7-4c97-903a-49c820bc2c1d	vehicle	Toyota Prius AA-101-AA	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "AA-101-AA", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
7c657499-bf16-4f1b-b969-1a4cff877d56	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	vehicle	Toyota Prius FF-606-FF	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "FF-606-FF", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
5e3f4ed7-3a49-4a1f-b224-612df5f6adc7	01de7961-6f90-4d17-bb91-38d569055701	vehicle	Toyota Prius EE-505-EE	{"vin": null, "make": "Toyota", "year": 2019, "model": "Prius", "plate": "EE-505-EE", "usage": null, "value": "30000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
54402341-f947-4da6-89c8-5c69b6258066	032821d0-09ce-408c-aaab-93cd4c80438f	vehicle	BMW X5 GG-707-GG	{"vin": null, "make": "BMW", "year": 2022, "model": "X5", "plate": "GG-707-GG", "usage": null, "value": "90000.00", "steering": null, "vehicle_type": null, "value_currency": "GEL"}
\.


--
-- Data for Name: intermediary; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.intermediary (id, party_id, kind, contract_no, contract_date, valid_to, status, commission_percent, created_at) FROM stdin;
3ffa29d9-8f39-4905-b6a5-30e5bb1217dc	db6b5659-c47b-4440-b655-806dc9a4fe37	agent	AG-001	2026-01-01	\N	active	10.00	2026-10-22 23:42:10.62902+00
\.


--
-- Data for Name: login_attempt; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.login_attempt (id, username, source, attempted_at, succeeded) FROM stdin;
1	world.sales	\N	2026-10-22 23:42:10.7861+00	f
2	nobody	\N	2026-10-22 23:42:10.790665+00	f
\.


--
-- Data for Name: loss_event; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.loss_event (id, number, policy_id, occurred_on, occurred_place, reported_on, description, request_key, source_ref, source_batch, created_at, created_by) FROM stdin;
139519c6-e471-4267-85ac-f841d74e7c93	CLM000001	a72126a1-0ec2-41d5-94d1-21b3c431814b	2026-06-10	თბილისი	2026-06-11	შეჯახება გზაჯვარედინზე	6d94b983-767c-12f4-9a59-12d5f212eaa7	\N	\N	2026-10-22 23:42:10.19982+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
5fa64b35-ef1d-43b6-b3f7-78c03b31e882	CLM000002	5409b7cc-17bf-4804-b8d1-2b9cf5149520	2026-08-05	რუსთავი	2026-08-06	სრული განადგურება	7b1f9a3e-0c2d-4e5f-8a6b-9c0d1e2f3a4b	\N	\N	2026-10-22 23:42:10.438222+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
\.


--
-- Data for Name: number_sequence; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.number_sequence (code, prefix, next_value, padding) FROM stdin;
complaint	PR	2	6
aml_case	AML	2	6
claim	CLM	3	6
contract	C	8	6
policy:CASCO	CAS	10	6
treaty	RI	3	6
subrogation	RG	2	6
\.


--
-- Data for Name: party; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.party (id, kind, is_resident, tax_id, foreign_id, country_code, first_name, last_name, birth_date, gender, name, legal_form, state_share_percent, is_public_entity, registration_place, legal_address, actual_address, phone, email, risk_level, identified_at, verified_at, is_pep, sanctions_checked_at, is_group_member, created_at, updated_at, risk_assessed_on, risk_review_on, self_declaration_on, sanctions_blocked) FROM stdin;
c6920e3c-f54b-4d9b-bb58-470390c3e1d4	organization	t	505050505	\N	GE	\N	\N	\N	\N	სატესტო გადამზღვეველი	სს	\N	f	\N	\N	\N	\N	\N	\N	\N	\N	f	\N	f	2026-10-22 23:42:09.693829+00	2026-10-22 23:42:09.693829+00	\N	\N	\N	f
1af24bfa-0837-4206-8cf9-9cf4b929c7a9	person	t	01001010102	\N	GE	ნინო	მაგალითაძე	1985-04-12	male	\N	\N	\N	f	\N	თბილისი, ტესტის ქ. 1	\N	+995 599123456	01001010102@example.ge	medium	\N	\N	f	\N	f	2026-10-22 23:42:09.676366+00	2026-10-22 23:42:09.739396+00	2026-10-23	2028-10-23	\N	f
86b38924-7dc9-4d1d-864a-292366679667	person	t	01001010103	\N	GE	ლევან	ნიმუშელი	1985-04-12	male	\N	\N	\N	f	\N	თბილისი, ტესტის ქ. 1	\N	+995 599123456	01001010103@example.ge	medium	\N	\N	f	\N	f	2026-10-22 23:42:09.681879+00	2026-10-22 23:42:09.746025+00	2026-10-23	2028-10-23	\N	f
5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	person	t	01001010104	\N	GE	მარიამ	სანიმუშო	1985-04-12	male	\N	\N	\N	f	\N	თბილისი, ტესტის ქ. 1	\N	+995 599123456	01001010104@example.ge	medium	\N	\N	f	\N	f	2026-10-22 23:42:09.685774+00	2026-10-22 23:42:09.757436+00	2026-10-23	2028-10-23	\N	f
592c2a48-9d6f-4850-a21d-e9d8752f03e2	organization	t	404040404	\N	GE	\N	\N	\N	\N	შპს ტესტ-ტრანსი	შპს	0.00	f	\N	ბათუმი	\N	\N	\N	medium	\N	\N	f	\N	f	2026-10-22 23:42:09.690033+00	2026-10-22 23:42:09.767333+00	2026-10-23	2028-10-23	\N	f
db6b5659-c47b-4440-b655-806dc9a4fe37	person	t	01001010110	\N	GE	აგენტი	შუამავლიშვილი	\N	\N	\N	\N	\N	f	\N	\N	\N	\N	\N	\N	\N	\N	f	\N	f	2026-10-22 23:42:10.621518+00	2026-10-22 23:42:10.621518+00	\N	\N	\N	f
f8194c08-4581-429c-863c-56a54b6e90e0	person	t	01001010101	\N	GE	გიორგი	ტესტიშვილი	1985-04-12	male	\N	\N	\N	f	\N	თბილისი, ტესტის ქ. 1	\N	+995 599123456	01001010101@example.ge	medium	\N	\N	f	2026-10-22	f	2026-10-22 23:42:09.66571+00	2026-10-22 23:42:09.728825+00	2026-10-23	2028-10-23	\N	f
\.


--
-- Data for Name: party_beneficial_owner; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.party_beneficial_owner (id, party_id, first_name, last_name, birth_date, personal_number, document_number, document_issued_on, document_country, document_issuer, document_valid_to, citizenship, birth_place, criterion, share_percent, is_direct, is_nominee, is_pep, identified_at, verified_at, verification_source, sanctions_checked_at, note, valid_to, created_at, created_by) FROM stdin;
6e5e9488-0029-4390-bf65-f3900f1da84c	592c2a48-9d6f-4850-a21d-e9d8752f03e2	დავით	მფლობელიძე	1970-02-02	01001010199	\N	\N	\N	\N	\N	GE	\N	share	60.00	t	f	f	\N	\N	\N	\N	\N	\N	2026-10-22 23:42:10.552847+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6
\.


--
-- Data for Name: party_risk_assessment; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.party_risk_assessment (id, party_id, assessed_on, computed_level, level, score, factors, is_override, reason, review_due_on, assessed_by, created_at) FROM stdin;
ed950f59-d252-48e3-adbe-48bb16292da4	f8194c08-4581-429c-863c-56a54b6e90e0	2026-10-23	medium	medium	3	[{"code": "not_verified", "points": 1, "decisive": false}, {"code": "sanctions_not_checked", "points": 2, "decisive": false}]	f	\N	2028-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:09.728825+00
3a59f08e-3ddf-4f39-a7ca-cce72cd354a1	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	2026-10-23	medium	medium	3	[{"code": "not_verified", "points": 1, "decisive": false}, {"code": "sanctions_not_checked", "points": 2, "decisive": false}]	f	\N	2028-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:09.739396+00
2f3d83c3-bd8d-4e08-b00c-18efd29e40ff	86b38924-7dc9-4d1d-864a-292366679667	2026-10-23	medium	medium	3	[{"code": "not_verified", "points": 1, "decisive": false}, {"code": "sanctions_not_checked", "points": 2, "decisive": false}]	f	\N	2028-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:09.746025+00
0567802a-de11-445e-a26e-e515ad2f3694	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	2026-10-23	medium	medium	3	[{"code": "not_verified", "points": 1, "decisive": false}, {"code": "sanctions_not_checked", "points": 2, "decisive": false}]	f	\N	2028-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:09.757436+00
8ba86335-aeb8-40da-a0a9-578a7850cc70	592c2a48-9d6f-4850-a21d-e9d8752f03e2	2026-10-23	medium	medium	3	[{"code": "not_verified", "points": 1, "decisive": false}, {"code": "sanctions_not_checked", "points": 2, "decisive": false}]	f	\N	2028-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:09.767333+00
\.


--
-- Data for Name: payment; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.payment (id, received_on, amount_gel, method, payer_party_id, reference, created_at, created_by, kind, reverses_payment_id, refund_request_id, reversal_request_id) FROM stdin;
802cd18d-6b0e-417c-976f-725657ef88a3	2026-01-12	375.00	bank	f8194c08-4581-429c-863c-56a54b6e90e0	გადახდა	2026-10-22 23:42:09.99293+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
12991d45-1360-459c-93ed-b9ba421eb857	2026-04-02	400.00	bank	f8194c08-4581-429c-863c-56a54b6e90e0	გადახდა	2026-10-22 23:42:10.013725+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
10123dc3-e356-4918-9468-b0c3ec2e07f0	2026-02-03	2300.00	bank	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	გადახდა	2026-10-22 23:42:10.028482+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
8c33f88c-b63e-4617-ae69-9c4d110defa8	2026-03-20	500.00	cash	86b38924-7dc9-4d1d-864a-292366679667	გადახდა	2026-10-22 23:42:10.046238+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
eaad3ef6-7f2d-41c5-98b0-760a9223fc11	2026-04-05	1100.00	bank	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	გადახდა	2026-10-22 23:42:10.066551+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
f37c8aa1-1bcc-4e06-863e-3c6fafb35604	2026-05-03	750.00	online	592c2a48-9d6f-4850-a21d-e9d8752f03e2	გადახდა	2026-10-22 23:42:10.081634+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
4623279a-7278-4b80-9a9a-cc2a658b5959	2026-10-23	-1100.00	bank	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	შეცდომით გატარდა	2026-10-22 23:42:10.093953+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	reversal	eaad3ef6-7f2d-41c5-98b0-760a9223fc11	\N	\N
1439293a-5c8b-4bcb-a6ec-efc62d1be1a6	2026-04-06	1100.00	bank	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	გადახდა	2026-10-22 23:42:10.10994+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
77f48f1e-617e-42b9-b0df-d912817a3546	2026-10-23	-750.68	bank	5196e2f1-a7e6-4f00-8d5d-32f94d46b89a	დაბრუნება GE00TB0000000000000001	2026-10-22 23:42:10.143141+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	refund	\N	\N	\N
6dcf6819-b717-454b-9dd1-883d09be6605	2026-07-01	1700.00	claim_offset	592c2a48-9d6f-4850-a21d-e9d8752f03e2	პირველი ნაწილი	2026-10-22 23:42:10.240371+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
4ed7fc74-8def-40a1-b587-2d335dfd5424	2026-06-02	24000.00	bank	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	გადახდა	2026-10-22 23:42:10.350093+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
f2d66480-4bbe-40e7-88f7-27cc1911a698	2026-10-23	-22750.68	bank	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	დიდი დაბრუნება GE00TB0000000000000002	2026-10-22 23:42:10.389271+00	deb1b458-9189-473e-b86e-047bb138fa0a	refund	\N	347c8f2b-d8c9-4655-9788-ca92e25d238f	\N
0d6abf52-7ff6-41dc-b040-5128a51538e4	2026-09-02	15000.00	bank	f8194c08-4581-429c-863c-56a54b6e90e0	გადახდა	2026-10-22 23:42:10.667565+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	receipt	\N	\N	\N
2136bb7e-d51e-45ee-b009-62b8bfda06ee	2026-10-23	-15000.00	bank	f8194c08-4581-429c-863c-56a54b6e90e0	ბანკმა დააბრუნა	2026-10-22 23:42:10.694949+00	deb1b458-9189-473e-b86e-047bb138fa0a	reversal	0d6abf52-7ff6-41dc-b040-5128a51538e4	\N	1b3570bb-c7fe-43c6-87c1-b8df86c256fe
\.


--
-- Data for Name: payment_allocation; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.payment_allocation (id, payment_id, installment_id, amount, amount_gel, rate, created_at, booked_on, created_by) FROM stdin;
ab251777-3c56-4ccf-8e60-d8fd562f099b	802cd18d-6b0e-417c-976f-725657ef88a3	f2a01c1c-bc0e-41c0-85f7-4e0eedba0526	375.00	375.00	1.000000	2026-10-22 23:42:09.99293+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
14715977-e75b-4537-ad21-ad3b9c04faf8	12991d45-1360-459c-93ed-b9ba421eb857	9a9c5950-df88-413d-a82e-926b933efc13	375.00	375.00	1.000000	2026-10-22 23:42:10.013725+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
b883ce92-5475-45c8-9232-cb1b05b41c8a	12991d45-1360-459c-93ed-b9ba421eb857	3f2f58f8-ba53-4fca-a9d8-2516141aed31	25.00	25.00	1.000000	2026-10-22 23:42:10.013725+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
b598ef1d-8d1a-4b40-89da-61fc725aebd1	10123dc3-e356-4918-9468-b0c3ec2e07f0	03d2e7f6-9f95-459e-a0de-5fc80f05f269	850.50	2282.23	2.683403	2026-10-22 23:42:10.028482+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
20fce0d3-a7a7-4dd2-aeb3-72e9b96f3f87	8c33f88c-b63e-4617-ae69-9c4d110defa8	2a754539-0fb8-48d9-aa42-bce8ce20c19c	130.00	381.93	2.937920	2026-10-22 23:42:10.046238+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
cb1b9bf1-dc49-4a0e-aa7d-6f5e599f762d	8c33f88c-b63e-4617-ae69-9c4d110defa8	90caddb3-71ce-4433-8314-b580324ed31d	40.19	118.07	2.937920	2026-10-22 23:42:10.046238+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
0b1f07db-68cd-4f1e-a2ea-333dd3a7c5f3	eaad3ef6-7f2d-41c5-98b0-760a9223fc11	e7db70dd-8d60-4f72-83a0-8250df6d834c	999.99	999.99	1.000000	2026-10-22 23:42:10.066551+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
f5d3c3a1-0ca4-45fc-9671-4baf36a4841a	f37c8aa1-1bcc-4e06-863e-3c6fafb35604	1c1f1f22-c12d-4e47-b660-80bc33114975	750.00	750.00	1.000000	2026-10-22 23:42:10.081634+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
b0bba953-771c-487f-abbb-492b179b42cf	4623279a-7278-4b80-9a9a-cc2a658b5959	e7db70dd-8d60-4f72-83a0-8250df6d834c	-999.99	-999.99	1.000000	2026-10-22 23:42:10.093953+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
d3a6f54a-1d35-4e63-885a-ae3a6032a79c	1439293a-5c8b-4bcb-a6ec-efc62d1be1a6	e7db70dd-8d60-4f72-83a0-8250df6d834c	999.99	999.99	1.000000	2026-10-22 23:42:10.10994+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
d72f6c0e-10d7-42ca-8fe5-a181af3d2609	77f48f1e-617e-42b9-b0df-d912817a3546	e7db70dd-8d60-4f72-83a0-8250df6d834c	-750.68	-750.68	1.000000	2026-10-22 23:42:10.143141+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
40213114-bdd6-4a91-8752-a07263a34b74	6dcf6819-b717-454b-9dd1-883d09be6605	1c1f1f22-c12d-4e47-b660-80bc33114975	750.00	750.00	1.000000	2026-10-22 23:42:10.240371+00	2026-07-01	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
f95f32b2-880b-48ed-b34e-036d3f1217fb	6dcf6819-b717-454b-9dd1-883d09be6605	3266ed4f-d151-4c23-bb5d-1222326787c1	750.00	750.00	1.000000	2026-10-22 23:42:10.240371+00	2026-07-01	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
0817ba63-ae30-4f1f-bad7-8cde6243d045	6dcf6819-b717-454b-9dd1-883d09be6605	718b0622-ba8f-4d15-90a8-aeddaa1968d5	200.00	200.00	1.000000	2026-10-22 23:42:10.240371+00	2026-07-01	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
21f2366e-80aa-48fa-9d24-0ba0eb399af8	4ed7fc74-8def-40a1-b587-2d335dfd5424	df455f7e-39f3-4f28-a1b0-c565e00d464c	24000.00	24000.00	1.000000	2026-10-22 23:42:10.350093+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
7161dcf7-451f-46f7-a914-1237189a7378	f2d66480-4bbe-40e7-88f7-27cc1911a698	df455f7e-39f3-4f28-a1b0-c565e00d464c	-22750.68	-22750.68	1.000000	2026-10-22 23:42:10.389271+00	2026-10-23	deb1b458-9189-473e-b86e-047bb138fa0a
aa2aa49a-8438-44d2-afbc-58a1bfa425d9	0d6abf52-7ff6-41dc-b040-5128a51538e4	355b51a8-1310-468e-9629-2f5d2674c7cc	15000.00	15000.00	1.000000	2026-10-22 23:42:10.667565+00	2026-10-23	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
6845b46a-b5c0-4d72-8666-e7cf35c5b235	2136bb7e-d51e-45ee-b009-62b8bfda06ee	355b51a8-1310-468e-9629-2f5d2674c7cc	-15000.00	-15000.00	1.000000	2026-10-22 23:42:10.694949+00	2026-10-23	deb1b458-9189-473e-b86e-047bb138fa0a
\.


--
-- Data for Name: policy; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.policy (id, contract_id, number, issued_on, issue_place, replaces_policy_id, effective_from, effective_to, status, cancelled_on, cancel_reason, premium_currency, premium_rate, created_at, created_by, cancel_grace_days, product_id, segment, approved_at, approved_by, change_note) FROM stdin;
5409b7cc-17bf-4804-b8d1-2b9cf5149520	fd887ebc-e174-4e02-84dc-9e471eba30f0	CAS000001	2026-10-23	\N	\N	2026-01-10	2027-01-10	active	\N	\N	GEL	1.000000	2026-10-22 23:42:09.796238+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:09.819308+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
6e5fa754-2657-49fa-8420-670c99250948	bf67a0bb-85c4-4e65-8c0f-2cfac5bdba2f	CAS000002	2026-10-23	\N	\N	2026-02-01	2027-02-01	active	\N	\N	USD	2.683201	2026-10-22 23:42:09.85792+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:09.867584+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
299a5381-4bac-49df-82f5-8dda6c323c55	eff8795e-b21d-407e-b01f-ec037e47215b	CAS000003	2026-10-23	\N	\N	2026-03-15	2027-03-15	active	\N	\N	EUR	2.937415	2026-10-22 23:42:09.885596+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:09.902444+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
a72126a1-0ec2-41d5-94d1-21b3c431814b	562aa8ea-8b53-4c26-add1-abac31ea7133	CAS000005	2026-10-23	\N	\N	2026-05-01	2027-05-01	active	\N	\N	GEL	1.000000	2026-10-22 23:42:09.955682+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	corporate	2026-10-22 23:42:09.97252+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
d26af7b9-b4a2-4ebd-99ce-056b92affe5a	139d9a61-e9f8-4853-b157-3f6a63bd13e0	CAS000004	2026-10-23	\N	\N	2026-04-01	2027-04-01	cancelled	2026-07-01	კლიენტის განცხადება	GEL	1.000000	2026-10-22 23:42:09.928526+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:09.938371+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
b0539124-f3d7-4c97-903a-49c820bc2c1d	fd887ebc-e174-4e02-84dc-9e471eba30f0	CAS000006	2026-10-23	\N	5409b7cc-17bf-4804-b8d1-2b9cf5149520	2026-06-01	2027-01-10	active	\N	\N	GEL	1.000000	2026-10-22 23:42:10.159834+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:10.175842+00	f811b716-2a18-4fe9-b792-fb90f6665f39	ქურდობის ლიმიტის გაზრდა
72dacb3b-0f40-43c5-8774-1a5f9e0f7173	004067a8-8b4c-43b3-968d-2dc6be2db74a	CAS000007	2026-10-23	\N	\N	2026-06-01	2027-06-01	cancelled	2026-06-20	გადაფორმება	GEL	1.000000	2026-10-22 23:42:10.321311+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:10.331771+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
01de7961-6f90-4d17-bb91-38d569055701	562aa8ea-8b53-4c26-add1-abac31ea7133	CAS000008	2026-10-23	\N	a72126a1-0ec2-41d5-94d1-21b3c431814b	2026-09-01	2027-05-01	active	\N	\N	GEL	1.000000	2026-10-22 23:42:10.51281+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	corporate	2026-10-22 23:42:10.524061+00	f811b716-2a18-4fe9-b792-fb90f6665f39	ქურდობის დაფარვის მოხსნა
032821d0-09ce-408c-aaab-93cd4c80438f	e710c511-3698-46a6-96d9-cb18c5dee966	CAS000009	2026-10-23	\N	\N	2026-09-01	2027-09-01	active	\N	\N	GEL	1.000000	2026-10-22 23:42:10.637809+00	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	\N	76c2c324-f357-4885-b597-54496f62db7c	retail	2026-10-22 23:42:10.648943+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
\.


--
-- Data for Name: policy_intermediary; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.policy_intermediary (policy_id, intermediary_id, commission_percent) FROM stdin;
032821d0-09ce-408c-aaab-93cd4c80438f	3ffa29d9-8f39-4905-b6a5-30e5bb1217dc	10.00
\.


--
-- Data for Name: policy_line; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.policy_line (id, policy_id, insured_object_id, class_code, sum_insured, sum_insured_currency, deductible, deductible_percent, premium, premium_gel, is_fully_reinsured, product_coverage_id) FROM stdin;
74cc6832-5506-42c5-ab4a-9e7e318e583c	5409b7cc-17bf-4804-b8d1-2b9cf5149520	c4429f21-927d-419c-9e12-b280c86693cb	5.1	30000.00	GEL	300.00	\N	1200.00	1200.00	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
1f18943d-b145-40cf-825b-49331f2fe7f3	5409b7cc-17bf-4804-b8d1-2b9cf5149520	c4429f21-927d-419c-9e12-b280c86693cb	5.1	30000.00	GEL	\N	\N	300.00	300.00	f	52062d68-9063-498e-8510-af676b807b25
ea24133b-7977-4038-a142-01098e563276	6e5fa754-2657-49fa-8420-670c99250948	c4291172-62fa-4811-8697-7404a3bb8cbd	5.1	30000.00	USD	300.00	\N	850.50	2282.06	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
dbcd711c-90d0-451b-a757-9e09e8a5e18b	299a5381-4bac-49df-82f5-8dda6c323c55	92840a88-05c8-46c7-8bd4-d4b4e7f09498	5.1	30000.00	EUR	300.00	\N	1440.00	4229.88	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
aaa17376-534f-4c7e-b5b7-50dd61fae311	299a5381-4bac-49df-82f5-8dda6c323c55	92840a88-05c8-46c7-8bd4-d4b4e7f09498	5.1	30000.00	EUR	\N	\N	120.00	352.49	f	52062d68-9063-498e-8510-af676b807b25
26399d85-e443-4c29-8ff4-d45b997b3dd5	d26af7b9-b4a2-4ebd-99ce-056b92affe5a	9200d829-cc92-4777-a39a-660638424200	5.1	30000.00	GEL	300.00	\N	999.99	999.99	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
0ebf5941-4daa-4007-adbd-b844a614038f	a72126a1-0ec2-41d5-94d1-21b3c431814b	cc8df06f-ba09-45e5-99f3-07af8ce859af	5.1	30000.00	GEL	300.00	\N	2400.00	2400.00	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
ddc24815-9791-4455-8e5a-c6139151cba8	a72126a1-0ec2-41d5-94d1-21b3c431814b	cc8df06f-ba09-45e5-99f3-07af8ce859af	5.1	30000.00	GEL	\N	\N	600.00	600.00	f	52062d68-9063-498e-8510-af676b807b25
38fe816a-372d-4780-afd1-471f04c308eb	b0539124-f3d7-4c97-903a-49c820bc2c1d	a1791b42-b7ce-4b7b-bad6-7c3b7a6355e6	5.1	35000.00	GEL	\N	\N	150.00	150.00	f	52062d68-9063-498e-8510-af676b807b25
d4c16ccf-e41e-4cb2-b082-a8267e59477a	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	7c657499-bf16-4f1b-b969-1a4cff877d56	5.1	30000.00	GEL	300.00	\N	24000.00	24000.00	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
a89358a9-a44a-4661-a14e-6677ad33d674	01de7961-6f90-4d17-bb91-38d569055701	5e3f4ed7-3a49-4a1f-b224-612df5f6adc7	5.1	\N	\N	\N	\N	-200.00	-200.00	f	52062d68-9063-498e-8510-af676b807b25
7df4bf19-1eb3-4605-834d-c6073b53c1d5	032821d0-09ce-408c-aaab-93cd4c80438f	54402341-f947-4da6-89c8-5c69b6258066	5.1	90000.00	GEL	500.00	\N	15000.00	15000.00	f	fcb6d9fc-4f59-4a4c-80d3-0148494a32ef
\.


--
-- Data for Name: policy_party; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.policy_party (policy_id, party_id, role) FROM stdin;
a72126a1-0ec2-41d5-94d1-21b3c431814b	f8194c08-4581-429c-863c-56a54b6e90e0	driver
01de7961-6f90-4d17-bb91-38d569055701	f8194c08-4581-429c-863c-56a54b6e90e0	driver
\.


--
-- Data for Name: premium_cession; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.premium_cession (id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel, booked_on, cover_from, cover_to, cancels_cession_id, created_at, created_by) FROM stdin;
262549ae-55d2-46f7-b140-9bad4015df2b	de5c655e-d5f0-4387-a219-97e4b1c14137	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	360.00	360.00	72.00	72.00	2026-01-10	2026-01-10	2027-01-10	\N	2026-10-22 23:42:09.819308+00	f811b716-2a18-4fe9-b792-fb90f6665f39
9773998a-0992-4c16-b057-438ee7f4dc58	d3794f9b-af5b-4c77-83c8-99dbb6d74d4b	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	90.00	90.00	18.00	18.00	2026-01-10	2026-01-10	2027-01-10	\N	2026-10-22 23:42:09.819308+00	f811b716-2a18-4fe9-b792-fb90f6665f39
c8a915c4-f445-494b-b893-443f257d9699	711224f6-22b7-4d52-b17c-1f247cb5444b	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	255.15	684.62	51.03	136.92	2026-02-01	2026-02-01	2027-02-01	\N	2026-10-22 23:42:09.867584+00	f811b716-2a18-4fe9-b792-fb90f6665f39
e30e6d3e-880e-4c04-bdee-383c54d36c8c	5023bbf9-10ea-4717-9778-1bbd8998ba7e	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	432.00	1268.96	86.40	253.79	2026-03-15	2026-03-15	2027-03-15	\N	2026-10-22 23:42:09.902444+00	f811b716-2a18-4fe9-b792-fb90f6665f39
683c751e-c148-442b-84f2-4f79369aad4f	cf64f662-8401-4236-85fd-188963377fab	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	36.00	105.75	7.20	21.15	2026-03-15	2026-03-15	2027-03-15	\N	2026-10-22 23:42:09.902444+00	f811b716-2a18-4fe9-b792-fb90f6665f39
ca36d8de-adc7-4b2c-ba62-bc98701581f3	97d13744-a0c2-4870-8d2a-0b8845208c67	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	300.00	300.00	60.00	60.00	2026-04-01	2026-04-01	2027-04-01	\N	2026-10-22 23:42:09.938371+00	f811b716-2a18-4fe9-b792-fb90f6665f39
f8a3a275-9824-4228-b9a0-cc71a8daec60	42f44f6e-42db-42f8-863b-7387e61d84a5	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	720.00	720.00	144.00	144.00	2026-05-01	2026-05-01	2027-05-01	\N	2026-10-22 23:42:09.97252+00	f811b716-2a18-4fe9-b792-fb90f6665f39
057cb58c-6a52-4bd3-ba8d-4a4dea7f65db	4355aa4c-ea62-4ab4-aac7-b577e892cf93	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	180.00	180.00	36.00	36.00	2026-05-01	2026-05-01	2027-05-01	\N	2026-10-22 23:42:09.97252+00	f811b716-2a18-4fe9-b792-fb90f6665f39
c80154a2-404a-4855-bad8-5915dc36bfaa	0a5fbb2a-740b-4a94-beb5-3dd0f8abc68a	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	-225.21	-225.21	-45.04	-45.04	2026-07-01	2026-07-01	2027-04-01	ca36d8de-adc7-4b2c-ba62-bc98701581f3	2026-10-22 23:42:10.122388+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
389efb69-73dc-432f-8beb-0cf8c00016c6	d5cca496-ebff-4fd5-a3b4-e74a568bc04e	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	45.00	45.00	9.00	9.00	2026-06-01	2026-06-01	2027-01-10	\N	2026-10-22 23:42:10.175842+00	f811b716-2a18-4fe9-b792-fb90f6665f39
f4cb53fc-b14a-41ea-bfc5-f4b97b18ceed	35cd52bc-8a8c-4837-a968-5cdfe796076d	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	7200.00	7200.00	1440.00	1440.00	2026-06-01	2026-06-01	2027-06-01	\N	2026-10-22 23:42:10.331771+00	f811b716-2a18-4fe9-b792-fb90f6665f39
c480b8b9-15cf-4ad2-ae17-439dd543bfba	60377e71-4292-4ce0-8028-1d78254823f0	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	-6825.21	-6825.21	-1365.04	-1365.04	2026-06-20	2026-06-20	2027-06-01	f4cb53fc-b14a-41ea-bfc5-f4b97b18ceed	2026-10-22 23:42:10.360019+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
9e977f3b-5204-49b8-afcf-1524da66efcf	40e15945-4ae7-46b3-a037-c341a150c75c	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	-60.00	-60.00	-12.00	-12.00	2026-09-01	2026-09-01	2027-05-01	\N	2026-10-22 23:42:10.524061+00	f811b716-2a18-4fe9-b792-fb90f6665f39
d8e41c23-3ce3-4a36-b7db-63eb21dae254	975d74d4-007a-48eb-afd7-03abb8f4f221	1478e4f6-9d46-4c8c-918e-27df990481f6	30.0000	4500.00	4500.00	900.00	900.00	2026-09-01	2026-09-01	2027-09-01	\N	2026-10-22 23:42:10.648943+00	f811b716-2a18-4fe9-b792-fb90f6665f39
fb815e8d-dd84-4719-b2ec-a2f96fe3715c	5023bbf9-10ea-4717-9778-1bbd8998ba7e	ebefd2b8-d62a-4fed-8962-5574378ac169	50.0000	720.00	2114.94	108.00	317.24	2026-03-15	2026-03-15	2027-03-15	\N	2026-10-22 23:42:10.715372+00	deb1b458-9189-473e-b86e-047bb138fa0a
04c2d747-02dc-4e19-b9e2-b10f45c8c742	cf64f662-8401-4236-85fd-188963377fab	ebefd2b8-d62a-4fed-8962-5574378ac169	50.0000	60.00	176.24	9.00	26.44	2026-03-15	2026-03-15	2027-03-15	\N	2026-10-22 23:42:10.715372+00	deb1b458-9189-473e-b86e-047bb138fa0a
\.


--
-- Data for Name: premium_refund_request; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, reference, payer_party_id, created_at, created_by) FROM stdin;
347c8f2b-d8c9-4655-9788-ca92e25d238f	72dacb3b-0f40-43c5-8774-1a5f9e0f7173	2026-10-23	22750.68	GEL	1.000000	bank	დიდი დაბრუნება GE00TB0000000000000002	1af24bfa-0837-4206-8cf9-9cf4b929c7a9	2026-10-22 23:42:10.379889+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
\.


--
-- Data for Name: premium_refund_request_line; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.premium_refund_request_line (id, request_id, "position", installment_id, amount, amount_gel) FROM stdin;
18f7fcdd-15bb-42a8-bae1-51f4352654f0	347c8f2b-d8c9-4655-9788-ca92e25d238f	1	df455f7e-39f3-4f28-a1b0-c565e00d464c	22750.68	22750.68
\.


--
-- Data for Name: premium_transaction; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_at, created_by, cancels_transaction_id) FROM stdin;
de5c655e-d5f0-4387-a219-97e4b1c14137	74cc6832-5506-42c5-ab4a-9e7e318e583c	issue	1200.00	1200.00	2026-01-10	2026-01-10	2027-01-10	2026-10-22 23:42:09.819308+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
d3794f9b-af5b-4c77-83c8-99dbb6d74d4b	1f18943d-b145-40cf-825b-49331f2fe7f3	issue	300.00	300.00	2026-01-10	2026-01-10	2027-01-10	2026-10-22 23:42:09.819308+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
711224f6-22b7-4d52-b17c-1f247cb5444b	ea24133b-7977-4038-a142-01098e563276	issue	850.50	2282.06	2026-02-01	2026-02-01	2027-02-01	2026-10-22 23:42:09.867584+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
5023bbf9-10ea-4717-9778-1bbd8998ba7e	dbcd711c-90d0-451b-a757-9e09e8a5e18b	issue	1440.00	4229.88	2026-03-15	2026-03-15	2027-03-15	2026-10-22 23:42:09.902444+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
cf64f662-8401-4236-85fd-188963377fab	aaa17376-534f-4c7e-b5b7-50dd61fae311	issue	120.00	352.49	2026-03-15	2026-03-15	2027-03-15	2026-10-22 23:42:09.902444+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
97d13744-a0c2-4870-8d2a-0b8845208c67	26399d85-e443-4c29-8ff4-d45b997b3dd5	issue	999.99	999.99	2026-04-01	2026-04-01	2027-04-01	2026-10-22 23:42:09.938371+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
42f44f6e-42db-42f8-863b-7387e61d84a5	0ebf5941-4daa-4007-adbd-b844a614038f	issue	2400.00	2400.00	2026-05-01	2026-05-01	2027-05-01	2026-10-22 23:42:09.97252+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
4355aa4c-ea62-4ab4-aac7-b577e892cf93	ddc24815-9791-4455-8e5a-c6139151cba8	issue	600.00	600.00	2026-05-01	2026-05-01	2027-05-01	2026-10-22 23:42:09.97252+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
0a5fbb2a-740b-4a94-beb5-3dd0f8abc68a	26399d85-e443-4c29-8ff4-d45b997b3dd5	cancellation	-750.68	-750.68	2026-07-01	2026-07-01	2027-04-01	2026-10-22 23:42:10.122388+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	97d13744-a0c2-4870-8d2a-0b8845208c67
d5cca496-ebff-4fd5-a3b4-e74a568bc04e	38fe816a-372d-4780-afd1-471f04c308eb	issue	150.00	150.00	2026-06-01	2026-06-01	2027-01-10	2026-10-22 23:42:10.175842+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
35cd52bc-8a8c-4837-a968-5cdfe796076d	d4c16ccf-e41e-4cb2-b082-a8267e59477a	issue	24000.00	24000.00	2026-06-01	2026-06-01	2027-06-01	2026-10-22 23:42:10.331771+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
60377e71-4292-4ce0-8028-1d78254823f0	d4c16ccf-e41e-4cb2-b082-a8267e59477a	cancellation	-22750.68	-22750.68	2026-06-20	2026-06-20	2027-06-01	2026-10-22 23:42:10.360019+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	35cd52bc-8a8c-4837-a968-5cdfe796076d
40e15945-4ae7-46b3-a037-c341a150c75c	a89358a9-a44a-4661-a14e-6677ad33d674	issue	-200.00	-200.00	2026-09-01	2026-09-01	2027-05-01	2026-10-22 23:42:10.524061+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
975d74d4-007a-48eb-afd7-03abb8f4f221	7df4bf19-1eb3-4605-834d-c6073b53c1d5	issue	15000.00	15000.00	2026-09-01	2026-09-01	2027-09-01	2026-10-22 23:42:10.648943+00	f811b716-2a18-4fe9-b792-fb90f6665f39	\N
\.


--
-- Data for Name: product; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.product (id, code, name_ka, object_type, is_active) FROM stdin;
76c2c324-f357-4885-b597-54496f62db7c	CASCO	ავტოდაზღვევა (კასკო)	vehicle	t
\.


--
-- Data for Name: product_coverage; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.product_coverage (id, product_id, code, name_ka, class_code, is_mandatory, sort_order) FROM stdin;
fcb6d9fc-4f59-4a4c-80d3-0148494a32ef	76c2c324-f357-4885-b597-54496f62db7c	damage	დაზიანება (ავარია, ხანძარი, სტიქია, მესამე პირის ქმედება)	5.1	t	1
52062d68-9063-498e-8510-af676b807b25	76c2c324-f357-4885-b597-54496f62db7c	theft	ქურდობა / გატაცება	5.1	f	2
03ce3f6b-c9de-49f7-a05f-228ee8195b13	76c2c324-f357-4885-b597-54496f62db7c	mtpl	მესამე პირის წინაშე პასუხისმგებლობა	6.2	f	3
06b63db2-bc27-4445-bdcf-d242ed905008	76c2c324-f357-4885-b597-54496f62db7c	accident	მძღოლის და მგზავრების უბედური შემთხვევა	3.2	f	4
73e773f2-a9d6-4a49-99b1-9d0057830165	76c2c324-f357-4885-b597-54496f62db7c	equipment	დამატებითი მოწყობილობა	5.1	f	5
a4781195-f770-4c89-86c3-18f4c049766a	76c2c324-f357-4885-b597-54496f62db7c	assistance	ევაკუატორი / ტექდახმარება	14	f	6
791362f8-8497-4b8e-880d-734d4359f897	76c2c324-f357-4885-b597-54496f62db7c	replacement	შემცვლელი ავტომობილი	14	f	7
\.


--
-- Data for Name: product_text; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.product_text (product_id, key, value) FROM stdin;
76c2c324-f357-4885-b597-54496f62db7c	documents	დაზღვევის ხელშეკრულება შედგება: ამ პოლისისგან, განაცხადისგან, დაზღვევის პირობებისგან და საინფორმაციო ფურცლისგან.
76c2c324-f357-4885-b597-54496f62db7c	terms_version	ავტოდაზღვევის (კასკო) პირობები, ვერსია 1.0 (შესავსებია)
76c2c324-f357-4885-b597-54496f62db7c	start_condition	დაზღვევა ძალაში შედის მითითებული თარიღის 00:00 საათიდან, პირველი შენატანის გადახდის პირობით, და მთავრდება დასრულების თარიღის წინა დღის 24:00 საათზე.
76c2c324-f357-4885-b597-54496f62db7c	claims_procedure	ზარალის შემთხვევაში დამზღვევი ვალდებულია დაუყოვნებლივ, არაუგვიანეს 3 სამუშაო დღისა, აცნობოს მზღვეველს; პროცედურა აღწერილია დაზღვევის პირობების შესაბამის მუხლებში (შესავსებია).
76c2c324-f357-4885-b597-54496f62db7c	disclosure_duty	დამზღვევი ვალდებულია მზღვეველს მიაწოდოს სრული და ზუსტი ინფორმაცია დაზღვევის საგნისა და რისკის შესახებ და აცნობოს მათი ცვლილება; ამ ვალდებულების დარღვევა შეიძლება გახდეს ანაზღაურებაზე უარის საფუძველი.
76c2c324-f357-4885-b597-54496f62db7c	withdrawal	ხელშეკრულებიდან გასვლის პირობები, ვადა და წესი განსაზღვრულია დაზღვევის პირობებით (შესავსებია).
76c2c324-f357-4885-b597-54496f62db7c	termination	ხელშეკრულება შეიძლება ვადამდე შეწყდეს დამზღვევის განცხადებით; დაბრუნებას ექვემდებარება გამოუმუშავებელი პრემია შეწყვეტის დღიდან, საფასურის გარეშე.
76c2c324-f357-4885-b597-54496f62db7c	exclusions	არ ანაზღაურდება ზარალი, რომელიც გამოწვეულია: განზრახი ქმედებით; უხეში გაუფრთხილებლობით (დაზღვევის პირობების შესაბამისი მუხლის მიხედვით); ავტომობილის მართვით ალკოჰოლური, ნარკოტიკული ან ფსიქოტროპული ზემოქმედების ქვეშ; მართვის უფლების გარეშე; საომარი მოქმედებებით, ტერორიზმით, რადიაციით. სრული სია: დაზღვევის პირობები, გამონაკლისების მუხლი (შესავსებია).
\.


--
-- Data for Name: receipt_reversal_request; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_at, created_by) FROM stdin;
1b3570bb-c7fe-43c6-87c1-b8df86c256fe	0d6abf52-7ff6-41dc-b040-5128a51538e4	032821d0-09ce-408c-aaab-93cd4c80438f	2026-10-23	15000.00	bank	f8194c08-4581-429c-863c-56a54b6e90e0	ბანკმა დააბრუნა	2026-10-22 23:42:10.68549+00	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
\.


--
-- Data for Name: receipt_reversal_request_line; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.receipt_reversal_request_line (id, request_id, "position", installment_id, amount, amount_gel, rate) FROM stdin;
2bcbcba2-7e51-443e-84ff-4fe975ce934d	1b3570bb-c7fe-43c6-87c1-b8df86c256fe	1	355b51a8-1310-468e-9629-2f5d2674c7cc	15000.00	15000.00	1.000000
\.


--
-- Data for Name: ri_treaty; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.ri_treaty (id, number, name, reinsurer_party_id, broker_party_id, kind, period_from, period_to, currency, ceded_percent, retention, lines, priority, treaty_limit, treaty_premium, commission_percent, broker_percent, reinsurer_rating, rating_agency, status, note, created_at, created_by) FROM stdin;
1478e4f6-9d46-4c8c-918e-27df990481f6	RI000001	კასკოს კვოტა 2026	c6920e3c-f54b-4d9b-bb58-470390c3e1d4	\N	quota_share	2026-01-01	2026-12-31	GEL	30.0000	\N	\N	\N	\N	\N	20.0000	0.0000	\N	\N	active	\N	2026-10-22 23:42:09.777585+00	deb1b458-9189-473e-b86e-047bb138fa0a
ebefd2b8-d62a-4fed-8962-5574378ac169	RI000002	ფაკულტატური EUR	c6920e3c-f54b-4d9b-bb58-470390c3e1d4	\N	facultative	2026-01-01	2027-12-31	EUR	50.0000	\N	\N	\N	\N	\N	15.0000	0.0000	\N	\N	active	\N	2026-10-22 23:42:10.706429+00	deb1b458-9189-473e-b86e-047bb138fa0a
\.


--
-- Data for Name: ri_treaty_class; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.ri_treaty_class (treaty_id, class_code) FROM stdin;
1478e4f6-9d46-4c8c-918e-27df990481f6	5.1
\.


--
-- Data for Name: role; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.role (code, name_ka) FROM stdin;
admin	ადმინისტრატორი
underwriter	ანდერრაიტერი
sales	გაყიდვების მენეჯერი
claims	ზარალების სპეციალისტი
accountant	ბუღალტერი
viewer	მხოლოდ ნახვა
operations_head	საოპერაციო დეპარტამენტის უფროსი
\.


--
-- Data for Name: sanction_list_entry; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.sanction_list_entry (source, data_id, kind, name, aliases, list_type, reference, listed_on, nationalities, birth_years, documents) FROM stdin;
un	6908001	individual	IVAN TESTOV	{"Ivan Testov","ИВАН ТЕСТОВ","იოანე ტესტოვი"}	UN List	QDi.999	2020-01-15	{Testland}	{1970}	{P1234567}
un	6908002	entity	TEST TRADING LLC	{"Test Trading \\"Ltd\\""}	UN List	QDe.999	\N	{}	{}	{}
\.


--
-- Data for Name: sanction_list_version; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.sanction_list_version (source, generated_at, loaded_at, entries) FROM stdin;
un	2026-09-01 00:00:00+00	2026-10-22 23:42:10.566825+00	2
\.


--
-- Data for Name: sanctions_screening; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.sanctions_screening (id, subject_kind, subject_id, party_id, screened_at, list_generated_at, matches, outcome, decided_by, decided_at, note, created_by) FROM stdin;
513cb186-9211-4228-bbf2-041769dc3d80	party	f8194c08-4581-429c-863c-56a54b6e90e0	f8194c08-4581-429c-863c-56a54b6e90e0	2026-10-22 23:42:10.868296+00	2026-09-01 00:00:00+00	[]	clear	\N	\N	\N	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75
\.


--
-- Data for Name: schema_migration; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.schema_migration (name, applied_at) FROM stdin;
0001_foundation.sql	2026-10-22 23:42:08.549548+00
0002_audit_log_append_only.sql	2026-10-22 23:42:08.590054+00
0003_policies.sql	2026-10-22 23:42:08.594596+00
0004_no_suspension_and_offsets.sql	2026-10-22 23:42:08.631469+00
0005_products.sql	2026-10-22 23:42:08.636471+00
0006_schema_migration_log.sql	2026-10-22 23:42:08.648706+00
0007_review_fixes.sql	2026-10-22 23:42:08.651663+00
0008_approval_and_periods.sql	2026-10-22 23:42:08.658683+00
0009_policy_change_note.sql	2026-10-22 23:42:08.665508+00
0010_payment_reversal_and_refund.sql	2026-10-22 23:42:08.668819+00
0011_claims.sql	2026-10-22 23:42:08.674145+00
0012_settings_and_product_texts.sql	2026-10-22 23:42:08.689233+00
0013_complaints.sql	2026-10-22 23:42:08.695216+00
0014_client_risk.sql	2026-10-22 23:42:08.703991+00
0015_reinsurance.sql	2026-10-22 23:42:08.712053+00
0016_beneficial_owners.sql	2026-10-22 23:42:08.725767+00
0017_sanctions.sql	2026-10-22 23:42:08.731725+00
0018_schedule_history.sql	2026-10-22 23:42:08.740416+00
0019_aml_cases.sql	2026-10-22 23:42:08.747564+00
0020_reinsurance_limits_and_schedule_control.sql	2026-10-22 23:42:08.757403+00
0021_xol_layers_and_suspension_target.sql	2026-10-22 23:42:08.763273+00
0022_suspension_target_transition.sql	2026-10-22 23:42:08.768328+00
0023_serialize_cession_guards.sql	2026-10-22 23:42:08.773509+00
0024_receipt_allocation_guard.sql	2026-10-22 23:42:08.779331+00
0025_settings_and_period_audit.sql	2026-10-22 23:42:08.783608+00
0026_number_series_limit.sql	2026-10-22 23:42:08.786847+00
0027_number_series_audit.sql	2026-10-22 23:42:08.790717+00
0028_loss_event.sql	2026-10-22 23:42:08.794201+00
0030_subrogation.sql	2026-10-22 23:42:08.802775+00
0031_schema_migration_read_only.sql	2026-10-22 23:42:08.827458+00
0032_permissions.sql	2026-10-22 23:42:08.832333+00
0033_claim_payment_reversal.sql	2026-10-22 23:42:08.844888+00
0034_claim_reversal_returned_date.sql	2026-10-22 23:42:08.851542+00
0035_claim_reserve_request.sql	2026-10-22 23:42:08.857807+00
0036_claim_payment_request.sql	2026-10-22 23:42:08.869557+00
0037_premium_refund_request.sql	2026-10-22 23:42:08.88181+00
0038_receipt_reversal_request.sql	2026-10-22 23:42:08.893395+00
\.


--
-- Data for Name: session; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.session (token_hash, user_id, created_at, expires_at) FROM stdin;
01fded2292a1325633e9e9ce5d00f46b4e1b67f0a9bc570f9eb92f079a53efe1	deb1b458-9189-473e-b86e-047bb138fa0a	2026-10-22 23:42:09.036078+00	2026-10-23 23:42:09.036078+00
d463fd1082b7dcde0c7b922a695f91ff151a47a9f48f320679299ce15f01d693	f811b716-2a18-4fe9-b792-fb90f6665f39	2026-10-22 23:42:09.10393+00	2026-10-23 23:42:09.10393+00
7bba07f87d45a1ac468a22dd2dae522273781fdc37cecadd096296ca36e7d2f9	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:09.1842+00	2026-10-23 23:42:09.1842+00
7b81726895e27af3fd4456da32700f8250c1309ed1db6c20d9d49683a0ee07c3	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	2026-10-22 23:42:09.246302+00	2026-10-23 23:42:09.246302+00
033bb660fcf74a14b833720c89d556c5141225597478e5bef7d96d2d14f7c1fb	d85d4e8c-e844-40f2-9e9f-ae180d288ab2	2026-10-22 23:42:09.313616+00	2026-10-23 23:42:09.313616+00
75db5ced6ecb0d2906314834b23044955264371d2d6d178fdb3ed9e78cf91344	741d7861-3c7d-417c-8c9c-4e7d6def58f6	2026-10-22 23:42:09.368662+00	2026-10-23 23:42:09.368662+00
\.


--
-- Data for Name: setting; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.setting (key, value) FROM stdin;
company_name	სადაზღვევო კომპანია (შესავსებია)
company_id_number	000000000
company_address	მისამართი (შესავსებია)
company_phone	+995 ...
company_email	info@example.ge
company_licence	ლიცენზია № ... (შესავსებია)
complaints_unit	მომხმარებელთა პრეტენზიების განხილვის სამსახური
complaints_channels	წერილობით ოფისში, ელფოსტით ან ვებგვერდის ფორმით
complaints_response_days	30
regulator_name	სსიპ საქართველოს დაზღვევის სახელმწიფო ზედამხედველობის სამსახური
regulator_address	თბილისი, ... (შესავსებია)
aml.high_risk_countries	
aml.premium_point_one_gel	15000
aml.premium_point_two_gel	50000
aml.sanctions_check_max_age_days	365
aml.medium_score	2
aml.high_score	5
aml.review_months_high	12
aml.review_months_medium	24
aml.review_months_low	48
payment.fd_threshold_gel	10000.00
\.


--
-- Data for Name: subrogation_case; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.subrogation_case (id, number, claim_id, status, opened_on, basis_kind, basis_number, basis_date, note, request_key, created_at, created_by) FROM stdin;
5a6762b2-1968-4599-8d4e-94ec83281741	RG000001	df2fe01b-405b-44c2-95df-a0eb6e9953d5	partially_received	2026-08-10	\N	\N	\N	მესამე პირის ბრალი	97f8cc2e-5445-78a1-e87c-fa7df2462e6f	2026-10-22 23:42:10.80471+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
\.


--
-- Data for Name: subrogation_movement; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.subrogation_movement (id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id, document_kind, document_number, document_date, note, request_key, created_at, created_by) FROM stdin;
25829dee-8e54-41b0-a414-84712cfe8692	5a6762b2-1968-4599-8d4e-94ec83281741	claimed	2026-08-12	3000.00	\N	\N	\N	demand_letter	DL-1	2026-08-12	\N	33351dec-7174-f94c-3528-c7fe64ab7349	2026-10-22 23:42:10.822705+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
c5d73807-4889-40a9-964d-f9f38e3ef7f2	5a6762b2-1968-4599-8d4e-94ec83281741	confirmed	2026-08-20	2000.00	\N	\N	\N	debtor_acceptance	DA-1	2026-08-20	\N	a74f3698-2efb-2a42-0206-cdb021c0ff9a	2026-10-22 23:42:10.835998+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
aa02377b-6fd4-45ad-b706-3e1ea66d627e	5a6762b2-1968-4599-8d4e-94ec83281741	received	2026-09-15	500.00	\N	bc875600-20bf-4d2c-a5a7-d1334aba396a	d8d5b123-1e9d-4c06-bce2-776f5e7048e3	bank_statement	BANK-REG-1	2026-09-15	პირველი თანხა	a7a7efb7-0a99-94f8-2038-5c1db9261e2d	2026-10-22 23:42:10.844639+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
\.


--
-- Data for Name: subrogation_party; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.subrogation_party (id, case_id, party_id, role, policy_number, coverage, limit_gel, response, response_on, created_at, created_by) FROM stdin;
d8d5b123-1e9d-4c06-bce2-776f5e7048e3	5a6762b2-1968-4599-8d4e-94ec83281741	592c2a48-9d6f-4850-a21d-e9d8752f03e2	liable_person	\N	\N	\N	\N	\N	2026-10-22 23:42:10.817017+00	d85d4e8c-e844-40f2-9e9f-ae180d288ab2
\.


--
-- Data for Name: user_group; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.user_group (id, name, created_at) FROM stdin;
25f6fd68-481e-4558-b56f-3d34d9c47701	ფინანსური დირექტორები	2026-10-22 23:42:10.574649+00
\.


--
-- Data for Name: user_group_member; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.user_group_member (id, group_id, user_id, created_at) FROM stdin;
0ff93b3a-8bd5-4e97-990f-5b6536e44de8	25f6fd68-481e-4558-b56f-3d34d9c47701	e7b65221-8d6a-4e2e-98c2-65d12e3bfa75	2026-10-22 23:42:10.593126+00
\.


--
-- Data for Name: user_group_permission; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.user_group_permission (id, group_id, permission, created_at) FROM stdin;
a574e35c-5ea6-4a53-859a-24fd673dd60a	25f6fd68-481e-4558-b56f-3d34d9c47701	finance.panel.view	2026-10-22 23:42:10.58472+00
\.


--
-- Data for Name: user_permission; Type: TABLE DATA; Schema: core; Owner: -
--

COPY core.user_permission (id, user_id, permission, decision, created_at) FROM stdin;
fe26d7eb-29e4-4a2c-ae0a-47f649ef85d5	31df0fa3-64b7-4d56-9b3c-50fa111d6cd6	audit.log.view	allow	2026-10-22 23:42:10.600302+00
\.


--
-- Name: audit_log_id_seq; Type: SEQUENCE SET; Schema: core; Owner: -
--

SELECT pg_catalog.setval('core.audit_log_id_seq', 131, true);


--
-- Name: login_attempt_id_seq; Type: SEQUENCE SET; Schema: core; Owner: -
--

SELECT pg_catalog.setval('core.login_attempt_id_seq', 2, true);


--
-- Name: accounting_period accounting_period_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.accounting_period
    ADD CONSTRAINT accounting_period_pkey PRIMARY KEY (period_start);


--
-- Name: aml_case aml_case_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.aml_case
    ADD CONSTRAINT aml_case_number_key UNIQUE (number);


--
-- Name: aml_case aml_case_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.aml_case
    ADD CONSTRAINT aml_case_pkey PRIMARY KEY (id);


--
-- Name: app_user app_user_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.app_user
    ADD CONSTRAINT app_user_pkey PRIMARY KEY (id);


--
-- Name: app_user app_user_username_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.app_user
    ADD CONSTRAINT app_user_username_key UNIQUE (username);


--
-- Name: audit_log audit_log_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.audit_log
    ADD CONSTRAINT audit_log_pkey PRIMARY KEY (id);


--
-- Name: branch branch_code_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.branch
    ADD CONSTRAINT branch_code_key UNIQUE (code);


--
-- Name: branch branch_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.branch
    ADD CONSTRAINT branch_pkey PRIMARY KEY (id);


--
-- Name: claim_cession claim_cession_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_cession
    ADD CONSTRAINT claim_cession_pkey PRIMARY KEY (claim_id, treaty_id);


--
-- Name: claim claim_event_coverage; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_event_coverage UNIQUE (loss_event_id, policy_line_id);


--
-- Name: claim claim_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_number_key UNIQUE (number);


--
-- Name: claim_payment claim_payment_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_pkey PRIMARY KEY (id);


--
-- Name: claim_payment_request claim_payment_request_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment_request
    ADD CONSTRAINT claim_payment_request_pkey PRIMARY KEY (id);


--
-- Name: claim claim_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_pkey PRIMARY KEY (id);


--
-- Name: claim_recovery claim_recovery_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_recovery
    ADD CONSTRAINT claim_recovery_pkey PRIMARY KEY (id);


--
-- Name: claim_reserve claim_reserve_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve
    ADD CONSTRAINT claim_reserve_pkey PRIMARY KEY (id);


--
-- Name: claim_reserve_request claim_reserve_request_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve_request
    ADD CONSTRAINT claim_reserve_request_pkey PRIMARY KEY (id);


--
-- Name: complaint complaint_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.complaint
    ADD CONSTRAINT complaint_number_key UNIQUE (number);


--
-- Name: complaint complaint_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.complaint
    ADD CONSTRAINT complaint_pkey PRIMARY KEY (id);


--
-- Name: contract contract_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.contract
    ADD CONSTRAINT contract_number_key UNIQUE (number);


--
-- Name: contract contract_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.contract
    ADD CONSTRAINT contract_pkey PRIMARY KEY (id);


--
-- Name: currency currency_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.currency
    ADD CONSTRAINT currency_pkey PRIMARY KEY (code);


--
-- Name: exchange_rate exchange_rate_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.exchange_rate
    ADD CONSTRAINT exchange_rate_pkey PRIMARY KEY (currency, rate_date);


--
-- Name: fms_suspension fms_suspension_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.fms_suspension
    ADD CONSTRAINT fms_suspension_pkey PRIMARY KEY (id);


--
-- Name: installment_adjustment installment_adjustment_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.installment_adjustment
    ADD CONSTRAINT installment_adjustment_pkey PRIMARY KEY (id);


--
-- Name: installment installment_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.installment
    ADD CONSTRAINT installment_pkey PRIMARY KEY (id);


--
-- Name: insurance_class insurance_class_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.insurance_class
    ADD CONSTRAINT insurance_class_pkey PRIMARY KEY (code);


--
-- Name: insured_object insured_object_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.insured_object
    ADD CONSTRAINT insured_object_pkey PRIMARY KEY (id);


--
-- Name: intermediary intermediary_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.intermediary
    ADD CONSTRAINT intermediary_pkey PRIMARY KEY (id);


--
-- Name: login_attempt login_attempt_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.login_attempt
    ADD CONSTRAINT login_attempt_pkey PRIMARY KEY (id);


--
-- Name: loss_event loss_event_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.loss_event
    ADD CONSTRAINT loss_event_number_key UNIQUE (number);


--
-- Name: loss_event loss_event_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.loss_event
    ADD CONSTRAINT loss_event_pkey PRIMARY KEY (id);


--
-- Name: loss_event loss_event_request_key_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.loss_event
    ADD CONSTRAINT loss_event_request_key_key UNIQUE (request_key);


--
-- Name: number_sequence number_sequence_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.number_sequence
    ADD CONSTRAINT number_sequence_pkey PRIMARY KEY (code);


--
-- Name: party_beneficial_owner party_beneficial_owner_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party_beneficial_owner
    ADD CONSTRAINT party_beneficial_owner_pkey PRIMARY KEY (id);


--
-- Name: party party_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party
    ADD CONSTRAINT party_pkey PRIMARY KEY (id);


--
-- Name: party_risk_assessment party_risk_assessment_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party_risk_assessment
    ADD CONSTRAINT party_risk_assessment_pkey PRIMARY KEY (id);


--
-- Name: payment_allocation payment_allocation_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment_allocation
    ADD CONSTRAINT payment_allocation_pkey PRIMARY KEY (id);


--
-- Name: payment payment_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment
    ADD CONSTRAINT payment_pkey PRIMARY KEY (id);


--
-- Name: policy_intermediary policy_intermediary_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_intermediary
    ADD CONSTRAINT policy_intermediary_pkey PRIMARY KEY (policy_id, intermediary_id);


--
-- Name: policy_line policy_line_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_line
    ADD CONSTRAINT policy_line_pkey PRIMARY KEY (id);


--
-- Name: policy policy_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_number_key UNIQUE (number);


--
-- Name: policy_party policy_party_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_party
    ADD CONSTRAINT policy_party_pkey PRIMARY KEY (policy_id, party_id, role);


--
-- Name: policy policy_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_pkey PRIMARY KEY (id);


--
-- Name: premium_cession premium_cession_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_cession
    ADD CONSTRAINT premium_cession_pkey PRIMARY KEY (id);


--
-- Name: premium_cession premium_cession_premium_transaction_id_treaty_id_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_cession
    ADD CONSTRAINT premium_cession_premium_transaction_id_treaty_id_key UNIQUE (premium_transaction_id, treaty_id);


--
-- Name: premium_refund_request_line premium_refund_request_line_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request_line
    ADD CONSTRAINT premium_refund_request_line_pkey PRIMARY KEY (id);


--
-- Name: premium_refund_request_line premium_refund_request_line_request_id_position_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request_line
    ADD CONSTRAINT premium_refund_request_line_request_id_position_key UNIQUE (request_id, "position");


--
-- Name: premium_refund_request premium_refund_request_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request
    ADD CONSTRAINT premium_refund_request_pkey PRIMARY KEY (id);


--
-- Name: premium_transaction premium_transaction_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_transaction
    ADD CONSTRAINT premium_transaction_pkey PRIMARY KEY (id);


--
-- Name: product product_code_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product
    ADD CONSTRAINT product_code_key UNIQUE (code);


--
-- Name: product_coverage product_coverage_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product_coverage
    ADD CONSTRAINT product_coverage_pkey PRIMARY KEY (id);


--
-- Name: product_coverage product_coverage_product_id_code_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product_coverage
    ADD CONSTRAINT product_coverage_product_id_code_key UNIQUE (product_id, code);


--
-- Name: product product_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product
    ADD CONSTRAINT product_pkey PRIMARY KEY (id);


--
-- Name: product_text product_text_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product_text
    ADD CONSTRAINT product_text_pkey PRIMARY KEY (product_id, key);


--
-- Name: receipt_reversal_request_line receipt_reversal_request_line_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request_line
    ADD CONSTRAINT receipt_reversal_request_line_pkey PRIMARY KEY (id);


--
-- Name: receipt_reversal_request_line receipt_reversal_request_line_request_id_position_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request_line
    ADD CONSTRAINT receipt_reversal_request_line_request_id_position_key UNIQUE (request_id, "position");


--
-- Name: receipt_reversal_request receipt_reversal_request_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request
    ADD CONSTRAINT receipt_reversal_request_pkey PRIMARY KEY (id);


--
-- Name: ri_treaty_class ri_treaty_class_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty_class
    ADD CONSTRAINT ri_treaty_class_pkey PRIMARY KEY (treaty_id, class_code);


--
-- Name: ri_treaty ri_treaty_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty
    ADD CONSTRAINT ri_treaty_number_key UNIQUE (number);


--
-- Name: ri_treaty ri_treaty_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty
    ADD CONSTRAINT ri_treaty_pkey PRIMARY KEY (id);


--
-- Name: role role_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.role
    ADD CONSTRAINT role_pkey PRIMARY KEY (code);


--
-- Name: sanction_list_entry sanction_list_entry_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.sanction_list_entry
    ADD CONSTRAINT sanction_list_entry_pkey PRIMARY KEY (source, data_id);


--
-- Name: sanction_list_version sanction_list_version_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.sanction_list_version
    ADD CONSTRAINT sanction_list_version_pkey PRIMARY KEY (source);


--
-- Name: sanctions_screening sanctions_screening_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.sanctions_screening
    ADD CONSTRAINT sanctions_screening_pkey PRIMARY KEY (id);


--
-- Name: schema_migration schema_migration_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.schema_migration
    ADD CONSTRAINT schema_migration_pkey PRIMARY KEY (name);


--
-- Name: session session_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.session
    ADD CONSTRAINT session_pkey PRIMARY KEY (token_hash);


--
-- Name: setting setting_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.setting
    ADD CONSTRAINT setting_pkey PRIMARY KEY (key);


--
-- Name: subrogation_case subrogation_case_claim_id_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_case
    ADD CONSTRAINT subrogation_case_claim_id_key UNIQUE (claim_id);


--
-- Name: subrogation_case subrogation_case_number_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_case
    ADD CONSTRAINT subrogation_case_number_key UNIQUE (number);


--
-- Name: subrogation_case subrogation_case_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_case
    ADD CONSTRAINT subrogation_case_pkey PRIMARY KEY (id);


--
-- Name: subrogation_case subrogation_case_request_key_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_case
    ADD CONSTRAINT subrogation_case_request_key_key UNIQUE (request_key);


--
-- Name: subrogation_movement subrogation_movement_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_pkey PRIMARY KEY (id);


--
-- Name: subrogation_movement subrogation_movement_request_key_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_request_key_key UNIQUE (request_key);


--
-- Name: subrogation_party subrogation_party_case_id_party_id_role_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_party
    ADD CONSTRAINT subrogation_party_case_id_party_id_role_key UNIQUE (case_id, party_id, role);


--
-- Name: subrogation_party subrogation_party_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_party
    ADD CONSTRAINT subrogation_party_pkey PRIMARY KEY (id);


--
-- Name: user_group_member user_group_member_group_id_user_id_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_member
    ADD CONSTRAINT user_group_member_group_id_user_id_key UNIQUE (group_id, user_id);


--
-- Name: user_group_member user_group_member_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_member
    ADD CONSTRAINT user_group_member_pkey PRIMARY KEY (id);


--
-- Name: user_group_permission user_group_permission_group_id_permission_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_permission
    ADD CONSTRAINT user_group_permission_group_id_permission_key UNIQUE (group_id, permission);


--
-- Name: user_group_permission user_group_permission_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_permission
    ADD CONSTRAINT user_group_permission_pkey PRIMARY KEY (id);


--
-- Name: user_group user_group_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group
    ADD CONSTRAINT user_group_pkey PRIMARY KEY (id);


--
-- Name: user_permission user_permission_pkey; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_permission
    ADD CONSTRAINT user_permission_pkey PRIMARY KEY (id);


--
-- Name: user_permission user_permission_user_id_permission_key; Type: CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_permission
    ADD CONSTRAINT user_permission_user_id_permission_key UNIQUE (user_id, permission);


--
-- Name: aml_case_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX aml_case_party_idx ON core.aml_case USING btree (party_id);


--
-- Name: aml_case_status_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX aml_case_status_idx ON core.aml_case USING btree (status);


--
-- Name: audit_log_row_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX audit_log_row_idx ON core.audit_log USING btree (table_name, row_id, at DESC);


--
-- Name: claim_loss_event_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_loss_event_idx ON core.claim USING btree (loss_event_id);


--
-- Name: claim_occurred_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_occurred_idx ON core.claim USING btree (occurred_on);


--
-- Name: claim_payment_claim_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_payment_claim_idx ON core.claim_payment USING btree (claim_id);


--
-- Name: claim_payment_paid_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_payment_paid_idx ON core.claim_payment USING btree (paid_on);


--
-- Name: claim_payment_request_claim_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_payment_request_claim_idx ON core.claim_payment_request USING btree (claim_id);


--
-- Name: claim_payment_request_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX claim_payment_request_once ON core.claim_payment USING btree (request_id) WHERE (request_id IS NOT NULL);


--
-- Name: claim_payment_reversal_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX claim_payment_reversal_once ON core.claim_payment USING btree (reverses_claim_payment_id) WHERE (reverses_claim_payment_id IS NOT NULL);


--
-- Name: claim_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_policy_idx ON core.claim USING btree (policy_id);


--
-- Name: claim_recovery_case_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_recovery_case_idx ON core.claim_recovery USING btree (subrogation_case_id);


--
-- Name: claim_recovery_case_reference; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX claim_recovery_case_reference ON core.claim_recovery USING btree (subrogation_case_id, bank_reference) WHERE ((subrogation_case_id IS NOT NULL) AND (bank_reference IS NOT NULL));


--
-- Name: claim_recovery_claim_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_recovery_claim_idx ON core.claim_recovery USING btree (claim_id);


--
-- Name: claim_recovery_reversal_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX claim_recovery_reversal_once ON core.claim_recovery USING btree (reverses_claim_recovery_id) WHERE (reverses_claim_recovery_id IS NOT NULL);


--
-- Name: claim_reserve_booked_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_reserve_booked_idx ON core.claim_reserve USING btree (booked_on);


--
-- Name: claim_reserve_claim_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_reserve_claim_idx ON core.claim_reserve USING btree (claim_id);


--
-- Name: claim_reserve_request_claim_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_reserve_request_claim_idx ON core.claim_reserve_request USING btree (claim_id);


--
-- Name: claim_reserve_request_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX claim_reserve_request_once ON core.claim_reserve USING btree (request_id) WHERE (request_id IS NOT NULL);


--
-- Name: claim_status_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX claim_status_idx ON core.claim USING btree (status);


--
-- Name: complaint_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX complaint_policy_idx ON core.complaint USING btree (policy_id);


--
-- Name: complaint_received_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX complaint_received_idx ON core.complaint USING btree (received_on);


--
-- Name: complaint_status_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX complaint_status_idx ON core.complaint USING btree (status);


--
-- Name: contract_policyholder_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX contract_policyholder_idx ON core.contract USING btree (policyholder_id);


--
-- Name: fms_suspension_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX fms_suspension_party_idx ON core.fms_suspension USING btree (party_id);


--
-- Name: installment_adjustment_installment_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX installment_adjustment_installment_idx ON core.installment_adjustment USING btree (installment_id, booked_on);


--
-- Name: installment_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX installment_policy_idx ON core.installment USING btree (policy_id, due_on);


--
-- Name: insured_object_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX insured_object_policy_idx ON core.insured_object USING btree (policy_id);


--
-- Name: intermediary_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX intermediary_party_idx ON core.intermediary USING btree (party_id);


--
-- Name: login_attempt_source_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX login_attempt_source_idx ON core.login_attempt USING btree (source, attempted_at DESC);


--
-- Name: login_attempt_username_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX login_attempt_username_idx ON core.login_attempt USING btree (username, attempted_at DESC);


--
-- Name: loss_event_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX loss_event_policy_idx ON core.loss_event USING btree (policy_id);


--
-- Name: party_beneficial_owner_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX party_beneficial_owner_party_idx ON core.party_beneficial_owner USING btree (party_id) WHERE (valid_to IS NULL);


--
-- Name: party_display_name_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX party_display_name_idx ON core.party USING btree (lower(display_name));


--
-- Name: party_risk_assessment_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX party_risk_assessment_party_idx ON core.party_risk_assessment USING btree (party_id, created_at DESC);


--
-- Name: party_risk_review_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX party_risk_review_idx ON core.party USING btree (risk_review_on);


--
-- Name: party_tax_id_key; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX party_tax_id_key ON core.party USING btree (tax_id) WHERE (tax_id IS NOT NULL);


--
-- Name: payment_allocation_installment_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX payment_allocation_installment_idx ON core.payment_allocation USING btree (installment_id);


--
-- Name: payment_allocation_payment_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX payment_allocation_payment_idx ON core.payment_allocation USING btree (payment_id);


--
-- Name: payment_received_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX payment_received_idx ON core.payment USING btree (received_on);


--
-- Name: payment_refund_request_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX payment_refund_request_once ON core.payment USING btree (refund_request_id) WHERE (refund_request_id IS NOT NULL);


--
-- Name: payment_reversal_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX payment_reversal_once ON core.payment USING btree (reverses_payment_id) WHERE (reverses_payment_id IS NOT NULL);


--
-- Name: payment_reversal_request_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX payment_reversal_request_once ON core.payment USING btree (reversal_request_id) WHERE (reversal_request_id IS NOT NULL);


--
-- Name: policy_contract_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX policy_contract_idx ON core.policy USING btree (contract_id);


--
-- Name: policy_line_class_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX policy_line_class_idx ON core.policy_line USING btree (class_code);


--
-- Name: policy_line_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX policy_line_policy_idx ON core.policy_line USING btree (policy_id);


--
-- Name: policy_party_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX policy_party_party_idx ON core.policy_party USING btree (party_id);


--
-- Name: policy_term_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX policy_term_idx ON core.policy USING btree (effective_from, effective_to);


--
-- Name: premium_cession_booked_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX premium_cession_booked_idx ON core.premium_cession USING btree (booked_on);


--
-- Name: premium_cession_treaty_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX premium_cession_treaty_idx ON core.premium_cession USING btree (treaty_id);


--
-- Name: premium_refund_request_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX premium_refund_request_policy_idx ON core.premium_refund_request USING btree (policy_id);


--
-- Name: premium_transaction_booked_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX premium_transaction_booked_idx ON core.premium_transaction USING btree (booked_on);


--
-- Name: premium_transaction_line_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX premium_transaction_line_idx ON core.premium_transaction USING btree (policy_line_id);


--
-- Name: receipt_reversal_request_policy_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX receipt_reversal_request_policy_idx ON core.receipt_reversal_request USING btree (policy_id);


--
-- Name: receipt_reversal_request_receipt_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX receipt_reversal_request_receipt_idx ON core.receipt_reversal_request USING btree (receipt_id);


--
-- Name: ri_treaty_period_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX ri_treaty_period_idx ON core.ri_treaty USING btree (period_from, period_to);


--
-- Name: sanctions_screening_open_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX sanctions_screening_open_idx ON core.sanctions_screening USING btree (outcome) WHERE (outcome = ANY (ARRAY['possible'::text, 'confirmed'::text]));


--
-- Name: sanctions_screening_party_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX sanctions_screening_party_idx ON core.sanctions_screening USING btree (party_id, screened_at DESC);


--
-- Name: session_user_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX session_user_idx ON core.session USING btree (user_id);


--
-- Name: subrogation_movement_booked_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX subrogation_movement_booked_idx ON core.subrogation_movement USING btree (booked_on);


--
-- Name: subrogation_movement_case_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX subrogation_movement_case_idx ON core.subrogation_movement USING btree (case_id);


--
-- Name: subrogation_movement_recovery_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX subrogation_movement_recovery_once ON core.subrogation_movement USING btree (claim_recovery_id) WHERE (claim_recovery_id IS NOT NULL);


--
-- Name: subrogation_movement_reversal_once; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX subrogation_movement_reversal_once ON core.subrogation_movement USING btree (reverses_movement_id) WHERE (reverses_movement_id IS NOT NULL);


--
-- Name: subrogation_party_case_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX subrogation_party_case_idx ON core.subrogation_party USING btree (case_id);


--
-- Name: user_group_member_user_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE INDEX user_group_member_user_idx ON core.user_group_member USING btree (user_id);


--
-- Name: user_group_name_idx; Type: INDEX; Schema: core; Owner: -
--

CREATE UNIQUE INDEX user_group_name_idx ON core.user_group USING btree (lower(name));


--
-- Name: accounting_period accounting_period_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER accounting_period_audit AFTER INSERT OR DELETE OR UPDATE ON core.accounting_period FOR EACH ROW EXECUTE FUNCTION core.audit('period_start');


--
-- Name: accounting_period accounting_period_immutable; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER accounting_period_immutable BEFORE DELETE OR UPDATE ON core.accounting_period FOR EACH ROW EXECUTE FUNCTION core.guard_accounting_period();


--
-- Name: aml_case aml_case_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER aml_case_audit AFTER INSERT OR DELETE OR UPDATE ON core.aml_case FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: app_user app_user_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER app_user_audit AFTER INSERT OR DELETE OR UPDATE ON core.app_user FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: claim claim_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_audit AFTER INSERT OR DELETE OR UPDATE ON core.claim FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: claim_cession claim_cession_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_cession_guard BEFORE INSERT ON core.claim_cession FOR EACH ROW EXECUTE FUNCTION core.guard_claim_cession();


--
-- Name: claim claim_event_policy; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_event_policy BEFORE INSERT OR UPDATE OF loss_event_id, policy_id ON core.claim FOR EACH ROW EXECUTE FUNCTION core.guard_claim_event();


--
-- Name: claim_payment claim_payment_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_payment_period BEFORE INSERT ON core.claim_payment FOR EACH ROW EXECUTE FUNCTION core.guard_claim_payment();


--
-- Name: claim_payment_request claim_payment_request_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_payment_request_audit AFTER INSERT OR DELETE OR UPDATE ON core.claim_payment_request FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: claim_payment claim_payment_request_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_payment_request_guard BEFORE INSERT ON core.claim_payment FOR EACH ROW EXECUTE FUNCTION core.guard_claim_payment_request();


--
-- Name: claim_payment_request claim_payment_request_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_payment_request_period BEFORE INSERT ON core.claim_payment_request FOR EACH ROW EXECUTE FUNCTION core.guard_claim_payment();


--
-- Name: claim_payment claim_payment_reversal_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_payment_reversal_guard BEFORE INSERT ON core.claim_payment FOR EACH ROW EXECUTE FUNCTION core.guard_claim_payment_reversal();


--
-- Name: claim_recovery claim_recovery_case_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_recovery_case_guard BEFORE INSERT ON core.claim_recovery FOR EACH ROW EXECUTE FUNCTION core.guard_claim_recovery_case();


--
-- Name: claim_recovery claim_recovery_case_movement; Type: TRIGGER; Schema: core; Owner: -
--

CREATE CONSTRAINT TRIGGER claim_recovery_case_movement AFTER INSERT ON core.claim_recovery DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION core.check_claim_recovery_movement();


--
-- Name: claim_recovery claim_recovery_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_recovery_period BEFORE INSERT ON core.claim_recovery FOR EACH ROW EXECUTE FUNCTION core.guard_claim_recovery();


--
-- Name: claim_reserve claim_reserve_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_reserve_period BEFORE INSERT ON core.claim_reserve FOR EACH ROW EXECUTE FUNCTION core.guard_claim_reserve();


--
-- Name: claim_reserve_request claim_reserve_request_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_reserve_request_audit AFTER INSERT OR DELETE OR UPDATE ON core.claim_reserve_request FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: claim_reserve claim_reserve_request_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER claim_reserve_request_guard BEFORE INSERT ON core.claim_reserve FOR EACH ROW EXECUTE FUNCTION core.guard_claim_reserve_request();


--
-- Name: complaint complaint_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER complaint_audit AFTER INSERT OR DELETE OR UPDATE ON core.complaint FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: contract contract_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER contract_audit AFTER INSERT OR DELETE OR UPDATE ON core.contract FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: fms_suspension fms_suspension_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER fms_suspension_audit AFTER INSERT OR DELETE OR UPDATE ON core.fms_suspension FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: fms_suspension fms_suspension_target; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER fms_suspension_target BEFORE INSERT OR UPDATE ON core.fms_suspension FOR EACH ROW EXECUTE FUNCTION core.guard_fms_suspension_target();


--
-- Name: installment_adjustment installment_adjustment_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER installment_adjustment_period BEFORE INSERT ON core.installment_adjustment FOR EACH ROW EXECUTE FUNCTION core.guard_installment_adjustment();


--
-- Name: installment installment_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER installment_audit AFTER INSERT OR DELETE OR UPDATE ON core.installment FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: insured_object insured_object_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER insured_object_audit AFTER INSERT OR DELETE OR UPDATE ON core.insured_object FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: intermediary intermediary_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER intermediary_audit AFTER INSERT OR DELETE OR UPDATE ON core.intermediary FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: loss_event loss_event_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER loss_event_audit AFTER INSERT OR DELETE OR UPDATE ON core.loss_event FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: number_sequence number_sequence_audit_insert_delete; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER number_sequence_audit_insert_delete AFTER INSERT OR DELETE ON core.number_sequence FOR EACH ROW EXECUTE FUNCTION core.audit('code');


--
-- Name: number_sequence number_sequence_audit_update; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER number_sequence_audit_update AFTER UPDATE ON core.number_sequence FOR EACH ROW WHEN (((old.prefix IS DISTINCT FROM new.prefix) OR (old.padding IS DISTINCT FROM new.padding))) EXECUTE FUNCTION core.audit('code');


--
-- Name: party party_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER party_audit AFTER INSERT OR DELETE OR UPDATE ON core.party FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: party_beneficial_owner party_beneficial_owner_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER party_beneficial_owner_audit AFTER INSERT OR DELETE OR UPDATE ON core.party_beneficial_owner FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: payment_allocation payment_allocation_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_allocation_period BEFORE INSERT ON core.payment_allocation FOR EACH ROW EXECUTE FUNCTION core.guard_payment_allocation();


--
-- Name: payment_allocation payment_allocation_refund_plan; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_allocation_refund_plan BEFORE INSERT ON core.payment_allocation FOR EACH ROW EXECUTE FUNCTION core.guard_refund_allocation_plan();


--
-- Name: payment_allocation payment_allocation_reversal_plan; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_allocation_reversal_plan BEFORE INSERT ON core.payment_allocation FOR EACH ROW EXECUTE FUNCTION core.guard_reversal_allocation_plan();


--
-- Name: payment_allocation payment_allocation_total; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_allocation_total BEFORE INSERT ON core.payment_allocation FOR EACH ROW EXECUTE FUNCTION core.guard_payment_allocation_total();


--
-- Name: payment payment_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_period BEFORE INSERT ON core.payment FOR EACH ROW EXECUTE FUNCTION core.guard_payment();


--
-- Name: payment payment_refund_request_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_refund_request_guard BEFORE INSERT ON core.payment FOR EACH ROW EXECUTE FUNCTION core.guard_payment_refund_request();


--
-- Name: payment payment_reversal_request_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER payment_reversal_request_guard BEFORE INSERT ON core.payment FOR EACH ROW EXECUTE FUNCTION core.guard_payment_reversal_request();


--
-- Name: policy policy_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER policy_audit AFTER INSERT OR DELETE OR UPDATE ON core.policy FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: policy_line policy_line_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER policy_line_audit AFTER INSERT OR DELETE OR UPDATE ON core.policy_line FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: premium_cession premium_cession_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER premium_cession_period BEFORE INSERT ON core.premium_cession FOR EACH ROW EXECUTE FUNCTION core.guard_premium_cession();


--
-- Name: premium_cession premium_cession_total; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER premium_cession_total BEFORE INSERT ON core.premium_cession FOR EACH ROW EXECUTE FUNCTION core.guard_premium_cession_total();


--
-- Name: premium_refund_request premium_refund_request_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER premium_refund_request_audit AFTER INSERT OR DELETE OR UPDATE ON core.premium_refund_request FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: premium_refund_request_line premium_refund_request_line_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER premium_refund_request_line_audit AFTER INSERT OR DELETE OR UPDATE ON core.premium_refund_request_line FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: premium_refund_request premium_refund_request_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER premium_refund_request_period BEFORE INSERT ON core.premium_refund_request FOR EACH ROW EXECUTE FUNCTION core.guard_premium_refund_request();


--
-- Name: premium_transaction premium_transaction_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER premium_transaction_period BEFORE INSERT ON core.premium_transaction FOR EACH ROW EXECUTE FUNCTION core.guard_premium_transaction();


--
-- Name: product product_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER product_audit AFTER INSERT OR DELETE OR UPDATE ON core.product FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: product_coverage product_coverage_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER product_coverage_audit AFTER INSERT OR DELETE OR UPDATE ON core.product_coverage FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: receipt_reversal_request receipt_reversal_request_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER receipt_reversal_request_audit AFTER INSERT OR DELETE OR UPDATE ON core.receipt_reversal_request FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: receipt_reversal_request_line receipt_reversal_request_line_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER receipt_reversal_request_line_audit AFTER INSERT OR DELETE OR UPDATE ON core.receipt_reversal_request_line FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: receipt_reversal_request receipt_reversal_request_period; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER receipt_reversal_request_period BEFORE INSERT ON core.receipt_reversal_request FOR EACH ROW EXECUTE FUNCTION core.guard_receipt_reversal_request();


--
-- Name: ri_treaty ri_treaty_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER ri_treaty_audit AFTER INSERT OR DELETE OR UPDATE ON core.ri_treaty FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: sanctions_screening sanctions_screening_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER sanctions_screening_audit AFTER INSERT OR DELETE OR UPDATE ON core.sanctions_screening FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: setting setting_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER setting_audit AFTER INSERT OR DELETE OR UPDATE ON core.setting FOR EACH ROW EXECUTE FUNCTION core.audit('key');


--
-- Name: subrogation_case subrogation_case_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER subrogation_case_audit AFTER INSERT OR DELETE OR UPDATE ON core.subrogation_case FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: subrogation_case subrogation_case_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER subrogation_case_guard BEFORE INSERT OR UPDATE ON core.subrogation_case FOR EACH ROW EXECUTE FUNCTION core.guard_subrogation_case();


--
-- Name: subrogation_movement subrogation_movement_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER subrogation_movement_audit AFTER INSERT OR DELETE OR UPDATE ON core.subrogation_movement FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: subrogation_movement subrogation_movement_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER subrogation_movement_guard BEFORE INSERT ON core.subrogation_movement FOR EACH ROW EXECUTE FUNCTION core.guard_subrogation_movement();


--
-- Name: subrogation_party subrogation_party_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER subrogation_party_audit AFTER INSERT OR DELETE OR UPDATE ON core.subrogation_party FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: subrogation_party subrogation_party_guard; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER subrogation_party_guard BEFORE UPDATE ON core.subrogation_party FOR EACH ROW EXECUTE FUNCTION core.guard_subrogation_party();


--
-- Name: user_group user_group_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER user_group_audit AFTER INSERT OR DELETE OR UPDATE ON core.user_group FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: user_group_member user_group_member_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER user_group_member_audit AFTER INSERT OR DELETE OR UPDATE ON core.user_group_member FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: user_group_permission user_group_permission_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER user_group_permission_audit AFTER INSERT OR DELETE OR UPDATE ON core.user_group_permission FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: user_permission user_permission_audit; Type: TRIGGER; Schema: core; Owner: -
--

CREATE TRIGGER user_permission_audit AFTER INSERT OR DELETE OR UPDATE ON core.user_permission FOR EACH ROW EXECUTE FUNCTION core.audit();


--
-- Name: accounting_period accounting_period_closed_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.accounting_period
    ADD CONSTRAINT accounting_period_closed_by_fkey FOREIGN KEY (closed_by) REFERENCES core.app_user(id);


--
-- Name: aml_case aml_case_class_code_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.aml_case
    ADD CONSTRAINT aml_case_class_code_fkey FOREIGN KEY (class_code) REFERENCES core.insurance_class(code);


--
-- Name: aml_case aml_case_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.aml_case
    ADD CONSTRAINT aml_case_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: aml_case aml_case_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.aml_case
    ADD CONSTRAINT aml_case_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: app_user app_user_branch_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.app_user
    ADD CONSTRAINT app_user_branch_id_fkey FOREIGN KEY (branch_id) REFERENCES core.branch(id);


--
-- Name: app_user app_user_role_code_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.app_user
    ADD CONSTRAINT app_user_role_code_fkey FOREIGN KEY (role_code) REFERENCES core.role(code);


--
-- Name: audit_log audit_log_user_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.audit_log
    ADD CONSTRAINT audit_log_user_id_fkey FOREIGN KEY (user_id) REFERENCES core.app_user(id);


--
-- Name: claim_cession claim_cession_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_cession
    ADD CONSTRAINT claim_cession_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: claim_cession claim_cession_treaty_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_cession
    ADD CONSTRAINT claim_cession_treaty_id_fkey FOREIGN KEY (treaty_id) REFERENCES core.ri_treaty(id);


--
-- Name: claim claim_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: claim claim_currency_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_currency_fkey FOREIGN KEY (currency) REFERENCES core.currency(code);


--
-- Name: claim claim_loss_event_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_loss_event_id_fkey FOREIGN KEY (loss_event_id) REFERENCES core.loss_event(id);


--
-- Name: claim_payment claim_payment_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: claim_payment claim_payment_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: claim_payment claim_payment_offset_payment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_offset_payment_id_fkey FOREIGN KEY (offset_payment_id) REFERENCES core.payment(id);


--
-- Name: claim_payment claim_payment_payee_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_payee_party_id_fkey FOREIGN KEY (payee_party_id) REFERENCES core.party(id);


--
-- Name: claim_payment_request claim_payment_request_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment_request
    ADD CONSTRAINT claim_payment_request_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: claim_payment_request claim_payment_request_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment_request
    ADD CONSTRAINT claim_payment_request_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: claim_payment claim_payment_request_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_request_id_fkey FOREIGN KEY (request_id) REFERENCES core.claim_payment_request(id);


--
-- Name: claim_payment_request claim_payment_request_payee_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment_request
    ADD CONSTRAINT claim_payment_request_payee_party_id_fkey FOREIGN KEY (payee_party_id) REFERENCES core.party(id);


--
-- Name: claim_payment claim_payment_reverses_claim_payment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_payment
    ADD CONSTRAINT claim_payment_reverses_claim_payment_id_fkey FOREIGN KEY (reverses_claim_payment_id) REFERENCES core.claim_payment(id);


--
-- Name: claim claim_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: claim claim_policy_line_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim
    ADD CONSTRAINT claim_policy_line_id_fkey FOREIGN KEY (policy_line_id) REFERENCES core.policy_line(id);


--
-- Name: claim_recovery claim_recovery_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_recovery
    ADD CONSTRAINT claim_recovery_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: claim_recovery claim_recovery_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_recovery
    ADD CONSTRAINT claim_recovery_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: claim_recovery claim_recovery_reverses_claim_recovery_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_recovery
    ADD CONSTRAINT claim_recovery_reverses_claim_recovery_id_fkey FOREIGN KEY (reverses_claim_recovery_id) REFERENCES core.claim_recovery(id);


--
-- Name: claim_recovery claim_recovery_subrogation_case_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_recovery
    ADD CONSTRAINT claim_recovery_subrogation_case_id_fkey FOREIGN KEY (subrogation_case_id) REFERENCES core.subrogation_case(id);


--
-- Name: claim_reserve claim_reserve_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve
    ADD CONSTRAINT claim_reserve_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: claim_reserve claim_reserve_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve
    ADD CONSTRAINT claim_reserve_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: claim_reserve_request claim_reserve_request_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve_request
    ADD CONSTRAINT claim_reserve_request_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: claim_reserve_request claim_reserve_request_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve_request
    ADD CONSTRAINT claim_reserve_request_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: claim_reserve claim_reserve_request_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.claim_reserve
    ADD CONSTRAINT claim_reserve_request_id_fkey FOREIGN KEY (request_id) REFERENCES core.claim_reserve_request(id);


--
-- Name: complaint complaint_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.complaint
    ADD CONSTRAINT complaint_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: complaint complaint_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.complaint
    ADD CONSTRAINT complaint_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: complaint complaint_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.complaint
    ADD CONSTRAINT complaint_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: complaint complaint_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.complaint
    ADD CONSTRAINT complaint_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: contract contract_branch_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.contract
    ADD CONSTRAINT contract_branch_id_fkey FOREIGN KEY (branch_id) REFERENCES core.branch(id);


--
-- Name: contract contract_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.contract
    ADD CONSTRAINT contract_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: contract contract_policyholder_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.contract
    ADD CONSTRAINT contract_policyholder_id_fkey FOREIGN KEY (policyholder_id) REFERENCES core.party(id);


--
-- Name: exchange_rate exchange_rate_currency_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.exchange_rate
    ADD CONSTRAINT exchange_rate_currency_fkey FOREIGN KEY (currency) REFERENCES core.currency(code);


--
-- Name: fms_suspension fms_suspension_case_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.fms_suspension
    ADD CONSTRAINT fms_suspension_case_id_fkey FOREIGN KEY (case_id) REFERENCES core.aml_case(id);


--
-- Name: fms_suspension fms_suspension_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.fms_suspension
    ADD CONSTRAINT fms_suspension_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: fms_suspension fms_suspension_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.fms_suspension
    ADD CONSTRAINT fms_suspension_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: fms_suspension fms_suspension_target_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.fms_suspension
    ADD CONSTRAINT fms_suspension_target_claim_id_fkey FOREIGN KEY (target_claim_id) REFERENCES core.claim(id);


--
-- Name: fms_suspension fms_suspension_target_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.fms_suspension
    ADD CONSTRAINT fms_suspension_target_policy_id_fkey FOREIGN KEY (target_policy_id) REFERENCES core.policy(id);


--
-- Name: installment_adjustment installment_adjustment_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.installment_adjustment
    ADD CONSTRAINT installment_adjustment_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: installment_adjustment installment_adjustment_installment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.installment_adjustment
    ADD CONSTRAINT installment_adjustment_installment_id_fkey FOREIGN KEY (installment_id) REFERENCES core.installment(id);


--
-- Name: installment installment_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.installment
    ADD CONSTRAINT installment_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: insurance_class insurance_class_parent_code_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.insurance_class
    ADD CONSTRAINT insurance_class_parent_code_fkey FOREIGN KEY (parent_code) REFERENCES core.insurance_class(code);


--
-- Name: insured_object insured_object_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.insured_object
    ADD CONSTRAINT insured_object_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: intermediary intermediary_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.intermediary
    ADD CONSTRAINT intermediary_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: loss_event loss_event_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.loss_event
    ADD CONSTRAINT loss_event_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: loss_event loss_event_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.loss_event
    ADD CONSTRAINT loss_event_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: party_beneficial_owner party_beneficial_owner_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party_beneficial_owner
    ADD CONSTRAINT party_beneficial_owner_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: party_beneficial_owner party_beneficial_owner_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party_beneficial_owner
    ADD CONSTRAINT party_beneficial_owner_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: party_risk_assessment party_risk_assessment_assessed_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party_risk_assessment
    ADD CONSTRAINT party_risk_assessment_assessed_by_fkey FOREIGN KEY (assessed_by) REFERENCES core.app_user(id);


--
-- Name: party_risk_assessment party_risk_assessment_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.party_risk_assessment
    ADD CONSTRAINT party_risk_assessment_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: payment_allocation payment_allocation_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment_allocation
    ADD CONSTRAINT payment_allocation_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: payment_allocation payment_allocation_installment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment_allocation
    ADD CONSTRAINT payment_allocation_installment_id_fkey FOREIGN KEY (installment_id) REFERENCES core.installment(id);


--
-- Name: payment_allocation payment_allocation_payment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment_allocation
    ADD CONSTRAINT payment_allocation_payment_id_fkey FOREIGN KEY (payment_id) REFERENCES core.payment(id);


--
-- Name: payment payment_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment
    ADD CONSTRAINT payment_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: payment payment_payer_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment
    ADD CONSTRAINT payment_payer_party_id_fkey FOREIGN KEY (payer_party_id) REFERENCES core.party(id);


--
-- Name: payment payment_refund_request_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment
    ADD CONSTRAINT payment_refund_request_id_fkey FOREIGN KEY (refund_request_id) REFERENCES core.premium_refund_request(id);


--
-- Name: payment payment_reversal_request_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment
    ADD CONSTRAINT payment_reversal_request_id_fkey FOREIGN KEY (reversal_request_id) REFERENCES core.receipt_reversal_request(id);


--
-- Name: payment payment_reverses_payment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.payment
    ADD CONSTRAINT payment_reverses_payment_id_fkey FOREIGN KEY (reverses_payment_id) REFERENCES core.payment(id);


--
-- Name: policy policy_approved_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_approved_by_fkey FOREIGN KEY (approved_by) REFERENCES core.app_user(id);


--
-- Name: policy policy_contract_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_contract_id_fkey FOREIGN KEY (contract_id) REFERENCES core.contract(id);


--
-- Name: policy policy_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: policy_intermediary policy_intermediary_intermediary_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_intermediary
    ADD CONSTRAINT policy_intermediary_intermediary_id_fkey FOREIGN KEY (intermediary_id) REFERENCES core.intermediary(id);


--
-- Name: policy_intermediary policy_intermediary_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_intermediary
    ADD CONSTRAINT policy_intermediary_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: policy_line policy_line_class_code_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_line
    ADD CONSTRAINT policy_line_class_code_fkey FOREIGN KEY (class_code) REFERENCES core.insurance_class(code);


--
-- Name: policy_line policy_line_insured_object_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_line
    ADD CONSTRAINT policy_line_insured_object_id_fkey FOREIGN KEY (insured_object_id) REFERENCES core.insured_object(id);


--
-- Name: policy_line policy_line_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_line
    ADD CONSTRAINT policy_line_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: policy_line policy_line_product_coverage_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_line
    ADD CONSTRAINT policy_line_product_coverage_id_fkey FOREIGN KEY (product_coverage_id) REFERENCES core.product_coverage(id);


--
-- Name: policy_line policy_line_sum_insured_currency_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_line
    ADD CONSTRAINT policy_line_sum_insured_currency_fkey FOREIGN KEY (sum_insured_currency) REFERENCES core.currency(code);


--
-- Name: policy_party policy_party_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_party
    ADD CONSTRAINT policy_party_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: policy_party policy_party_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy_party
    ADD CONSTRAINT policy_party_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: policy policy_premium_currency_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_premium_currency_fkey FOREIGN KEY (premium_currency) REFERENCES core.currency(code);


--
-- Name: policy policy_product_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_product_id_fkey FOREIGN KEY (product_id) REFERENCES core.product(id);


--
-- Name: policy policy_replaces_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.policy
    ADD CONSTRAINT policy_replaces_policy_id_fkey FOREIGN KEY (replaces_policy_id) REFERENCES core.policy(id);


--
-- Name: premium_cession premium_cession_cancels_cession_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_cession
    ADD CONSTRAINT premium_cession_cancels_cession_id_fkey FOREIGN KEY (cancels_cession_id) REFERENCES core.premium_cession(id);


--
-- Name: premium_cession premium_cession_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_cession
    ADD CONSTRAINT premium_cession_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: premium_cession premium_cession_premium_transaction_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_cession
    ADD CONSTRAINT premium_cession_premium_transaction_id_fkey FOREIGN KEY (premium_transaction_id) REFERENCES core.premium_transaction(id);


--
-- Name: premium_cession premium_cession_treaty_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_cession
    ADD CONSTRAINT premium_cession_treaty_id_fkey FOREIGN KEY (treaty_id) REFERENCES core.ri_treaty(id);


--
-- Name: premium_refund_request premium_refund_request_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request
    ADD CONSTRAINT premium_refund_request_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: premium_refund_request premium_refund_request_currency_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request
    ADD CONSTRAINT premium_refund_request_currency_fkey FOREIGN KEY (currency) REFERENCES core.currency(code);


--
-- Name: premium_refund_request_line premium_refund_request_line_installment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request_line
    ADD CONSTRAINT premium_refund_request_line_installment_id_fkey FOREIGN KEY (installment_id) REFERENCES core.installment(id);


--
-- Name: premium_refund_request_line premium_refund_request_line_request_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request_line
    ADD CONSTRAINT premium_refund_request_line_request_id_fkey FOREIGN KEY (request_id) REFERENCES core.premium_refund_request(id);


--
-- Name: premium_refund_request premium_refund_request_payer_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request
    ADD CONSTRAINT premium_refund_request_payer_party_id_fkey FOREIGN KEY (payer_party_id) REFERENCES core.party(id);


--
-- Name: premium_refund_request premium_refund_request_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_refund_request
    ADD CONSTRAINT premium_refund_request_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: premium_transaction premium_transaction_cancels_transaction_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_transaction
    ADD CONSTRAINT premium_transaction_cancels_transaction_id_fkey FOREIGN KEY (cancels_transaction_id) REFERENCES core.premium_transaction(id);


--
-- Name: premium_transaction premium_transaction_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_transaction
    ADD CONSTRAINT premium_transaction_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: premium_transaction premium_transaction_policy_line_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.premium_transaction
    ADD CONSTRAINT premium_transaction_policy_line_id_fkey FOREIGN KEY (policy_line_id) REFERENCES core.policy_line(id);


--
-- Name: product_coverage product_coverage_class_code_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product_coverage
    ADD CONSTRAINT product_coverage_class_code_fkey FOREIGN KEY (class_code) REFERENCES core.insurance_class(code);


--
-- Name: product_coverage product_coverage_product_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product_coverage
    ADD CONSTRAINT product_coverage_product_id_fkey FOREIGN KEY (product_id) REFERENCES core.product(id);


--
-- Name: product_text product_text_product_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.product_text
    ADD CONSTRAINT product_text_product_id_fkey FOREIGN KEY (product_id) REFERENCES core.product(id);


--
-- Name: receipt_reversal_request receipt_reversal_request_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request
    ADD CONSTRAINT receipt_reversal_request_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: receipt_reversal_request_line receipt_reversal_request_line_installment_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request_line
    ADD CONSTRAINT receipt_reversal_request_line_installment_id_fkey FOREIGN KEY (installment_id) REFERENCES core.installment(id);


--
-- Name: receipt_reversal_request_line receipt_reversal_request_line_request_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request_line
    ADD CONSTRAINT receipt_reversal_request_line_request_id_fkey FOREIGN KEY (request_id) REFERENCES core.receipt_reversal_request(id);


--
-- Name: receipt_reversal_request receipt_reversal_request_payer_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request
    ADD CONSTRAINT receipt_reversal_request_payer_party_id_fkey FOREIGN KEY (payer_party_id) REFERENCES core.party(id);


--
-- Name: receipt_reversal_request receipt_reversal_request_policy_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request
    ADD CONSTRAINT receipt_reversal_request_policy_id_fkey FOREIGN KEY (policy_id) REFERENCES core.policy(id);


--
-- Name: receipt_reversal_request receipt_reversal_request_receipt_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.receipt_reversal_request
    ADD CONSTRAINT receipt_reversal_request_receipt_id_fkey FOREIGN KEY (receipt_id) REFERENCES core.payment(id);


--
-- Name: ri_treaty ri_treaty_broker_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty
    ADD CONSTRAINT ri_treaty_broker_party_id_fkey FOREIGN KEY (broker_party_id) REFERENCES core.party(id);


--
-- Name: ri_treaty_class ri_treaty_class_class_code_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty_class
    ADD CONSTRAINT ri_treaty_class_class_code_fkey FOREIGN KEY (class_code) REFERENCES core.insurance_class(code);


--
-- Name: ri_treaty_class ri_treaty_class_treaty_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty_class
    ADD CONSTRAINT ri_treaty_class_treaty_id_fkey FOREIGN KEY (treaty_id) REFERENCES core.ri_treaty(id);


--
-- Name: ri_treaty ri_treaty_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty
    ADD CONSTRAINT ri_treaty_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: ri_treaty ri_treaty_currency_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty
    ADD CONSTRAINT ri_treaty_currency_fkey FOREIGN KEY (currency) REFERENCES core.currency(code);


--
-- Name: ri_treaty ri_treaty_reinsurer_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.ri_treaty
    ADD CONSTRAINT ri_treaty_reinsurer_party_id_fkey FOREIGN KEY (reinsurer_party_id) REFERENCES core.party(id);


--
-- Name: sanctions_screening sanctions_screening_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.sanctions_screening
    ADD CONSTRAINT sanctions_screening_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: sanctions_screening sanctions_screening_decided_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.sanctions_screening
    ADD CONSTRAINT sanctions_screening_decided_by_fkey FOREIGN KEY (decided_by) REFERENCES core.app_user(id);


--
-- Name: sanctions_screening sanctions_screening_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.sanctions_screening
    ADD CONSTRAINT sanctions_screening_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: session session_user_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.session
    ADD CONSTRAINT session_user_id_fkey FOREIGN KEY (user_id) REFERENCES core.app_user(id) ON DELETE CASCADE;


--
-- Name: subrogation_case subrogation_case_claim_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_case
    ADD CONSTRAINT subrogation_case_claim_id_fkey FOREIGN KEY (claim_id) REFERENCES core.claim(id);


--
-- Name: subrogation_case subrogation_case_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_case
    ADD CONSTRAINT subrogation_case_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: subrogation_movement subrogation_movement_case_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_case_id_fkey FOREIGN KEY (case_id) REFERENCES core.subrogation_case(id);


--
-- Name: subrogation_movement subrogation_movement_claim_recovery_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_claim_recovery_id_fkey FOREIGN KEY (claim_recovery_id) REFERENCES core.claim_recovery(id);


--
-- Name: subrogation_movement subrogation_movement_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: subrogation_movement subrogation_movement_payer_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_payer_id_fkey FOREIGN KEY (payer_id) REFERENCES core.subrogation_party(id);


--
-- Name: subrogation_movement subrogation_movement_reverses_movement_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_movement
    ADD CONSTRAINT subrogation_movement_reverses_movement_id_fkey FOREIGN KEY (reverses_movement_id) REFERENCES core.subrogation_movement(id);


--
-- Name: subrogation_party subrogation_party_case_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_party
    ADD CONSTRAINT subrogation_party_case_id_fkey FOREIGN KEY (case_id) REFERENCES core.subrogation_case(id);


--
-- Name: subrogation_party subrogation_party_created_by_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_party
    ADD CONSTRAINT subrogation_party_created_by_fkey FOREIGN KEY (created_by) REFERENCES core.app_user(id);


--
-- Name: subrogation_party subrogation_party_party_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.subrogation_party
    ADD CONSTRAINT subrogation_party_party_id_fkey FOREIGN KEY (party_id) REFERENCES core.party(id);


--
-- Name: user_group_member user_group_member_group_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_member
    ADD CONSTRAINT user_group_member_group_id_fkey FOREIGN KEY (group_id) REFERENCES core.user_group(id);


--
-- Name: user_group_member user_group_member_user_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_member
    ADD CONSTRAINT user_group_member_user_id_fkey FOREIGN KEY (user_id) REFERENCES core.app_user(id);


--
-- Name: user_group_permission user_group_permission_group_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_group_permission
    ADD CONSTRAINT user_group_permission_group_id_fkey FOREIGN KEY (group_id) REFERENCES core.user_group(id);


--
-- Name: user_permission user_permission_user_id_fkey; Type: FK CONSTRAINT; Schema: core; Owner: -
--

ALTER TABLE ONLY core.user_permission
    ADD CONSTRAINT user_permission_user_id_fkey FOREIGN KEY (user_id) REFERENCES core.app_user(id);


--
-- PostgreSQL database dump complete
--

\unrestrict v1J7fbfqlklC4vfdFMOhGwccmiQfubpWVngHIbfObfL4KrCAX5IZ8ObeeOBHO5x

