-- The reinsurer's commission settled: offset against what is owed to the reinsurer, or received from it (legacy 0039,
-- Insurance-Software PR #362; owner, 20.10.2026, docs/05 3ე). On a 100% reinsured policy the reinsurer's commission (the
-- treaty's percent of the premium) is accrued with the premium — the cession's commission, booked on the day the premium is —
-- and stays a receivable from the reinsurer (form N1-4 00140) „სანამ გადამზღვევლისთან ამ თანხას ან არ ჩავითვლით, ან არ
-- ჩაგვირიცხავს“. The accountant records the settlement per treaty, as a total, in a currency; N1-4 values what is left at the
-- reporting date's rate.
--
-- - ri_commission_settlement: the treaty, the day, offset or receipt, the currency and the amount (positive), a reference and
--   a note. Never edited or deleted; a mistake is corrected by its reversal — a new row that names it (reverses_id), on a day
--   not before it, the same treaty, kind and currency and the amount negated (RC001); one reversal per settlement at most.
-- - Its day is in an open month (core.assert_periods_open), as every other booking.
-- - The database does not itself stop a settlement above what is left to settle (legacy too): the action does, under the
--   treaty's lock.
-- core_app may read and add, nothing else: the runner revokes update and delete (Migrator.Ledgers) and audits every row.

create table core.ri_commission_settlement (
  id          uniqueidentifier not null constraint ri_commission_settlement_pkey primary key
                               constraint ri_commission_settlement_id_df default newid(),
  treaty_id   uniqueidentifier not null constraint ri_commission_settlement_treaty_id_fkey references core.ri_treaty (id),
  settled_on  date not null,
  kind        nvarchar(10) not null constraint ri_commission_settlement_kind_check check (kind in (N'offset', N'receipt')),
  currency    nchar(3) not null constraint ri_commission_settlement_currency_fkey references core.currency (code),
  amount      decimal(18, 2) not null constraint ri_commission_settlement_amount_check check (amount <> 0),
  reverses_id uniqueidentifier null constraint ri_commission_settlement_reverses_id_fkey references core.ri_commission_settlement (id),
  reference   nvarchar(max) null,
  note        nvarchar(max) null,
  created_at  datetimeoffset(6) not null constraint ri_commission_settlement_created_at_df default sysdatetimeoffset(),
  created_by  uniqueidentifier not null constraint ri_commission_settlement_created_by_fkey references core.app_user (id)
                               constraint ri_commission_settlement_created_by_df default core.current_actor(),
  -- Legacy check ((reverses_id is null) = (amount > 0)): a settlement is positive, its reversal negative.
  constraint ri_commission_settlement_check check ((reverses_id is null and amount > 0) or (reverses_id is not null and amount < 0))
);

create index ri_commission_settlement_treaty_idx on core.ri_commission_settlement (treaty_id, settled_on);
create unique index ri_commission_settlement_reversed_once on core.ri_commission_settlement (reverses_id) where reverses_id is not null;
GO

-- The legacy BEFORE INSERT trigger (core.guard_ri_commission_settlement): every row's day in an open month; a reversal mirrors
-- the settlement it names exactly — that one exists and is no reversal itself, the same treaty, kind and currency, the amount
-- negated, a day not before it (RC001). Inside an INSTEAD OF trigger the columns' defaults do not apply to `inserted`, so an
-- omitted id, created_at or created_by gets its default here.
create trigger core.ri_commission_settlement_guard on core.ri_commission_settlement
instead of insert
as
begin
  set nocount on;
  declare @days core.date_list;
  insert @days (d) select distinct settled_on from inserted where settled_on is not null;
  exec core.assert_periods_open @days;
  if exists (
       select 1
       from inserted r
       left join core.ri_commission_settlement o on o.id = r.reverses_id
       where r.reverses_id is not null
         and (o.id is null or o.reverses_id is not null or r.treaty_id <> o.treaty_id or r.kind <> o.kind or r.currency <> o.currency
              or r.amount <> -o.amount or r.settled_on < o.settled_on))
    exec core.fail N'[RC001] a settlement''s reversal mirrors it exactly';
  insert core.ri_commission_settlement (id, treaty_id, settled_on, kind, currency, amount, reverses_id, reference, note, created_at, created_by)
  select coalesce(id, newid()), treaty_id, settled_on, kind, currency, amount, reverses_id, reference, note,
         coalesce(created_at, sysdatetimeoffset()), coalesce(created_by, core.current_actor())
  from inserted;
end;
GO

insert into core.audit_config (table_name, key_column, update_columns) values (N'ri_commission_settlement', N'id', null);
GO

exec core.refresh_audit_triggers;
GO

-- The ledger's rights only: read and add (the runner re-asserts that update and delete stay revoked).
grant select, insert on core.ri_commission_settlement to core_app;
GO
