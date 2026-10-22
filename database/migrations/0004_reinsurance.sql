-- Outward reinsurance: treaties, the reinsurer's share of every premium ledger row, and the share applied
-- to each claim (legacy 0015, 0020, 0021, 0023). Rules: reserves order №01/2013 art. 5–7, financial
-- reporting order №02/2013 annex 2 art. 2.11, 4.2, 4.6, internal records order №11/2016 annex 4, order
-- №33/2019 (reinsurer rating).

create table core.ri_treaty (
  id                 uniqueidentifier not null constraint ri_treaty_pkey primary key constraint ri_treaty_id_df default newid(),
  number             nvarchar(100) not null constraint ri_treaty_number_key unique,
  name               nvarchar(400) not null,
  reinsurer_party_id uniqueidentifier not null constraint ri_treaty_reinsurer_party_id_fkey references core.party (id),
  broker_party_id    uniqueidentifier null constraint ri_treaty_broker_party_id_fkey references core.party (id),
  kind               nvarchar(20) not null constraint ri_treaty_kind_check check (kind in (N'quota_share', N'surplus', N'excess_of_loss', N'facultative')),
  period_from        date not null,
  period_to          date not null,
  currency           nchar(3) not null constraint ri_treaty_currency_fkey references core.currency (code),
  -- Quota share / facultative.
  ceded_percent      decimal(7, 4) null constraint ri_treaty_ceded_percent_check check (ceded_percent between 0 and 100),
  -- Surplus: retention per risk and number of lines.
  retention          decimal(18, 2) null constraint ri_treaty_retention_check check (retention >= 0),
  lines              int null constraint ri_treaty_lines_check check (lines >= 0),
  -- Excess of loss: priority per claim and limit above it; premium of the treaty itself.
  priority           decimal(18, 2) null constraint ri_treaty_priority_check check (priority >= 0),
  treaty_limit       decimal(18, 2) null constraint ri_treaty_treaty_limit_check check (treaty_limit >= 0),
  treaty_premium     decimal(18, 2) null constraint ri_treaty_treaty_premium_check check (treaty_premium >= 0),
  commission_percent decimal(7, 4) not null constraint ri_treaty_commission_percent_df default 0
                     constraint ri_treaty_commission_percent_check check (commission_percent between 0 and 100),
  broker_percent     decimal(7, 4) not null constraint ri_treaty_broker_percent_df default 0
                     constraint ri_treaty_broker_percent_check check (broker_percent between 0 and 100),
  reinsurer_rating   nvarchar(50) null,
  rating_agency      nvarchar(100) null,
  status             nvarchar(10) not null constraint ri_treaty_status_df default N'active'
                     constraint ri_treaty_status_check check (status in (N'active', N'closed')),
  note               nvarchar(max) null,
  created_at         datetimeoffset(6) not null constraint ri_treaty_created_at_df default sysdatetimeoffset(),
  created_by         uniqueidentifier null constraint ri_treaty_created_by_fkey references core.app_user (id),
  constraint ri_treaty_period check (period_to >= period_from),
  constraint ri_treaty_terms check (
    (kind in (N'quota_share', N'facultative') and ceded_percent is not null)
    or (kind = N'surplus' and retention is not null and lines is not null)
    or (kind = N'excess_of_loss' and priority is not null))
);
create index ri_treaty_period_idx on core.ri_treaty (period_from, period_to);

create table core.ri_treaty_class (
  treaty_id  uniqueidentifier not null constraint ri_treaty_class_treaty_id_fkey references core.ri_treaty (id),
  class_code nvarchar(20) not null constraint ri_treaty_class_class_code_fkey references core.insurance_class (code),
  constraint ri_treaty_class_pkey primary key (treaty_id, class_code)
);

-- The reinsurer's share of one premium ledger row: the same period and fixed rate as the row; the
-- cancellation of a cession reverses the original's (docs/04 წესი 16).
create table core.premium_cession (
  id                     uniqueidentifier not null constraint premium_cession_pkey primary key constraint premium_cession_id_df default newid(),
  premium_transaction_id uniqueidentifier not null constraint premium_cession_premium_transaction_id_fkey references core.premium_transaction (id),
  treaty_id              uniqueidentifier not null constraint premium_cession_treaty_id_fkey references core.ri_treaty (id),
  share_percent          decimal(7, 4) not null constraint premium_cession_share_percent_check check (share_percent between 0 and 100),
  amount                 decimal(18, 2) not null,
  amount_gel             decimal(18, 2) not null,
  commission             decimal(18, 2) not null constraint premium_cession_commission_df default 0,
  commission_gel         decimal(18, 2) not null constraint premium_cession_commission_gel_df default 0,
  booked_on              date not null,
  cover_from             date not null,
  cover_to               date not null,
  cancels_cession_id     uniqueidentifier null constraint premium_cession_cancels_cession_id_fkey references core.premium_cession (id),
  created_at             datetimeoffset(6) not null constraint premium_cession_created_at_df default sysdatetimeoffset(),
  created_by             uniqueidentifier null constraint premium_cession_created_by_fkey references core.app_user (id),
  constraint premium_cession_premium_transaction_id_treaty_id_key unique (premium_transaction_id, treaty_id)
);
create index premium_cession_treaty_idx on core.premium_cession (treaty_id);
create index premium_cession_booked_idx on core.premium_cession (booked_on);

-- The share of one claim taken by a treaty: proportional (percent) or excess of loss (priority and limit).
create table core.claim_cession (
  claim_id      uniqueidentifier not null constraint claim_cession_claim_id_fkey references core.claim (id),
  treaty_id     uniqueidentifier not null constraint claim_cession_treaty_id_fkey references core.ri_treaty (id),
  share_percent decimal(7, 4) not null constraint claim_cession_share_percent_df default 0
                constraint claim_cession_share_percent_check check (share_percent between 0 and 100),
  priority      decimal(18, 2) null,
  treaty_limit  decimal(18, 2) null,
  constraint claim_cession_pkey primary key (claim_id, treaty_id)
);
GO

-- premium_cession_period (0015), premium_cession_total (0020, 0023): the cessions of one premium row never
-- exceed it (100 %, and its amount in both currencies). The per-row lock makes a second placement on the
-- same row wait for the first transaction and then sum its row too (two-session acceptance test, 0023).
create trigger core.premium_cession_guard on core.premium_cession
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @row uniqueidentifier, @share decimal(7, 4), @amount decimal(18, 2), @amount_gel decimal(18, 2), @booked_on date,
          @row_amount decimal(18, 2), @row_gel decimal(18, 2), @total_share decimal(38, 4), @total_amount decimal(38, 2), @total_gel decimal(38, 2),
          @resource nvarchar(255), @m nvarchar(400);
  declare rows cursor local fast_forward for select id, premium_transaction_id, share_percent, amount, amount_gel, booked_on from inserted;
  open rows;
  fetch next from rows into @id, @row, @share, @amount, @amount_gel, @booked_on;
  while @@fetch_status = 0
  begin
    exec core.assert_period_open @booked_on;
    set @resource = concat(N'core.premium_cession:', lower(convert(nchar(36), @row)));
    exec core.xact_lock @resource;
    select @row_amount = amount, @row_gel = amount_gel from core.premium_transaction where id = @row;
    select @total_share = coalesce(sum(share_percent), 0) + @share,
           @total_amount = coalesce(sum(abs(amount)), 0) + abs(@amount),
           @total_gel = coalesce(sum(abs(amount_gel)), 0) + abs(@amount_gel)
    from core.premium_cession where premium_transaction_id = @row;
    if @total_share > 100 or @total_amount > abs(@row_amount) or @total_gel > abs(@row_gel)
    begin
      set @m = concat(N'[RI001] cessions of premium row ', lower(convert(nchar(36), @row)), N' would exceed the row (', @total_share, N' %)');
      exec core.fail @m;
    end
    insert core.premium_cession (id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel,
                                 booked_on, cover_from, cover_to, cancels_cession_id, created_at, created_by)
    select id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel,
           booked_on, cover_from, cover_to, cancels_cession_id, created_at, created_by
    from inserted where id = @id;
    fetch next from rows into @id, @row, @share, @amount, @amount_gel, @booked_on;
  end
  close rows;
  deallocate rows;
end;
GO

-- claim_cession_guard (0020, 0021, 0023): the proportional shares of a claim never exceed 100 %, excess of
-- loss never combines with a proportional share, a claim takes at most one excess-of-loss treaty, and an
-- excess-of-loss treaty shares only a loss in its own currency.
create trigger core.claim_cession_guard on core.claim_cession
instead of insert
as
begin
  set nocount on;
  declare @claim uniqueidentifier, @treaty uniqueidentifier, @share decimal(7, 4), @kind nvarchar(20), @currency nchar(3), @claim_currency nchar(3),
          @proportional decimal(38, 4), @xol int, @resource nvarchar(255), @m nvarchar(400);
  declare rows cursor local fast_forward for select claim_id, treaty_id, share_percent from inserted;
  open rows;
  fetch next from rows into @claim, @treaty, @share;
  while @@fetch_status = 0
  begin
    set @resource = concat(N'core.claim_cession:', lower(convert(nchar(36), @claim)));
    exec core.xact_lock @resource;
    select @kind = kind, @currency = currency from core.ri_treaty where id = @treaty;
    set @claim_currency = (select currency from core.claim where id = @claim);
    if @kind = N'excess_of_loss' and @currency <> @claim_currency
    begin
      set @m = concat(N'[RI003] excess-of-loss treaty in ', @currency, N' cannot share a loss in ', @claim_currency);
      exec core.fail @m;
    end
    select @proportional = coalesce(sum(case when t.kind <> N'excess_of_loss' then c.share_percent end), 0),
           @xol = count(case when t.kind = N'excess_of_loss' then 1 end)
    from core.claim_cession c join core.ri_treaty t on t.id = c.treaty_id where c.claim_id = @claim;
    if @kind <> N'excess_of_loss' set @proportional = @proportional + @share; else set @xol = @xol + 1;
    if @proportional > 100
    begin
      set @m = concat(N'[RI001] proportional shares of claim ', lower(convert(nchar(36), @claim)), N' would exceed 100 %');
      exec core.fail @m;
    end
    if @xol > 0 and @proportional > 0
    begin
      set @m = concat(N'[RI002] claim ', lower(convert(nchar(36), @claim)), N' would combine excess of loss with a proportional share');
      exec core.fail @m;
    end
    if @xol > 1
    begin
      set @m = concat(N'[RI004] claim ', lower(convert(nchar(36), @claim)), N' would take more than one excess-of-loss treaty');
      exec core.fail @m;
    end
    insert core.claim_cession (claim_id, treaty_id, share_percent, priority, treaty_limit)
    select claim_id, treaty_id, share_percent, priority, treaty_limit from inserted where claim_id = @claim and treaty_id = @treaty;
    fetch next from rows into @claim, @treaty, @share;
  end
  close rows;
  deallocate rows;
end;
GO
