-- Foundation: schema, shared procedures, reference data, users and sessions, audit log, settings,
-- numbering, accounting periods, permissions, products, parties and intermediaries.
--
-- Ported from the PostgreSQL schema of Insurance-Software (commit 17c1f1f, migrations 0001–0038) in its
-- final state; each object notes the legacy migration it comes from. Conventions of this port:
--   * every table lives in schema core and keeps the legacy table and column names;
--   * uuid → uniqueidentifier (default NEWID()), timestamptz → datetimeoffset(6), numeric → decimal,
--     boolean → bit, jsonb and text[] → nvarchar(max) holding JSON (ISJSON checked);
--   * the database collation is case-sensitive (as PostgreSQL text comparison is);
--   * a legacy error code (SQLSTATE or custom, e.g. 23514, RI001) is raised by THROW as "[CODE] message",
--     error number 51000, so the application maps it exactly as the old code mapped the SQLSTATE;
--   * pg_advisory_xact_lock → sp_getapplock owned by the transaction (core.xact_lock);
--   * the actor of a change: SESSION_CONTEXT(N'core.user_id'), set by the application per transaction
--     (legacy: set_config('core.user_id', …, true)).
-- Batches are separated by GO lines; the runner applies one file in one transaction.

-- Schema core is created by the migration runner together with its journal (core.schema_migration).
if schema_id(N'core') is null exec (N'create schema core');
GO

-- ---------------------------------------------------------------- shared procedures

-- Raises a refusal "[CODE] message" (error 51000). THROW reads % as a format character, so a literal % of
-- the message (an amount, a number, a user's text) is doubled first.
create procedure core.fail @message nvarchar(2048)
as
begin
  declare @text nvarchar(2048) = replace(@message, N'%', N'%%');
  throw 51000, @text, 1;
end;
GO

-- Transaction-scoped named lock (legacy pg_advisory_xact_lock / pg_advisory_xact_lock_shared).
-- @mode: Exclusive or Shared. Waits as long as needed (lock timeout of the session), like PostgreSQL.
create procedure core.xact_lock @resource nvarchar(255), @mode nvarchar(32) = N'Exclusive'
as
begin
  set nocount on;
  if @@trancount = 0
    throw 51000, N'[55000] core.xact_lock needs a transaction', 1;
  declare @result int;
  exec @result = sp_getapplock @Resource = @resource, @LockMode = @mode, @LockOwner = N'Transaction', @LockTimeout = -1;
  if @result < 0
  begin
    declare @m nvarchar(400) = concat(N'[55P03] lock ', @resource, N' not granted (', @result, N')');
    exec core.fail @m;
  end
end;
GO

-- The actor of the current transaction, or null (legacy: nullif(current_setting('core.user_id', true), '')::uuid).
create function core.current_actor() returns uniqueidentifier
as
begin
  return try_convert(uniqueidentifier, try_convert(nvarchar(36), session_context(N'core.user_id')));
end;
GO

-- An amount as stored in a setting: 1–13 digits and up to 2 decimals (legacy regex '^\d{1,13}(\.\d{1,2})?$').
create function core.is_amount_text(@s nvarchar(max)) returns bit
as
begin
  if @s is null return 0;
  declare @t nvarchar(max) = @s collate Latin1_General_BIN2;
  declare @dot int = charindex(N'.', @t);
  declare @int nvarchar(max) = case when @dot = 0 then @t else left(@t, @dot - 1) end;
  declare @dec nvarchar(max) = case when @dot = 0 then null else substring(@t, @dot + 1, 4000) end;
  if len(@int + N'x') - 1 not between 1 and 13 or @int like N'%[^0-9]%' collate Latin1_General_BIN2 return 0;
  if @dot > 0 and (len(@dec + N'x') - 1 not between 1 and 2 or @dec like N'%[^0-9]%' collate Latin1_General_BIN2) return 0;
  return 1;
end;
GO

-- A permission key: lower-case words joined by dots, at least two (legacy regex '^[a-z_]+(\.[a-z_]+)+$').
create function core.is_permission_key(@s nvarchar(200)) returns bit
with schemabinding
as
begin
  if @s is null return 0;
  if @s collate Latin1_General_BIN2 like N'%[^a-z_.]%' return 0;
  if @s collate Latin1_General_BIN2 not like N'%.%' or @s collate Latin1_General_BIN2 like N'.%'
     or @s collate Latin1_General_BIN2 like N'%.' or @s collate Latin1_General_BIN2 like N'%..%' return 0;
  if datalength(@s) <> datalength(rtrim(@s)) return 0;
  return 1;
end;
GO

-- ---------------------------------------------------------------- reference (legacy 0001)

create table core.currency (
  code        nchar(3) not null constraint currency_pkey primary key,
  name_ka     nvarchar(200) not null,
  minor_units smallint not null constraint currency_minor_units_df default 2
);

insert into core.currency (code, name_ka) values
  (N'GEL', N'ლარი'), (N'USD', N'აშშ დოლარი'), (N'EUR', N'ევრო');

-- Official National Bank of Georgia rate: GEL per 1 unit of currency.
create table core.exchange_rate (
  currency  nchar(3) not null constraint exchange_rate_currency_fkey references core.currency (code),
  rate_date date not null,
  rate      decimal(12, 6) not null constraint exchange_rate_rate_check check (rate > 0),
  constraint exchange_rate_pkey primary key (currency, rate_date)
);

-- Classifier of insurance classes used in the regulator's reporting forms.
create table core.insurance_class (
  code         nvarchar(20) not null constraint insurance_class_pkey primary key,
  parent_code  nvarchar(20) null constraint insurance_class_parent_code_fkey references core.insurance_class (code),
  name_ka      nvarchar(400) not null,
  category     nvarchar(20) not null constraint insurance_class_category_check check (category in (N'life', N'non_life')),
  -- Statutory IBNR rate of net written premium (reserves order №01/2013, art. 7).
  ibnr_percent decimal(5, 2) not null,
  sort_order   int not null
);

insert into core.insurance_class (code, parent_code, name_ka, category, ibnr_percent, sort_order) values
  (N'1',    null,  N'სიცოცხლის დაზღვევა', N'life', 5, 100),
  (N'1.1',  N'1',  N'სიცოცხლის ვადიანი დაზღვევა', N'life', 5, 101),
  (N'1.2',  N'1',  N'სიცოცხლის უვადო დაზღვევა', N'life', 5, 102),
  (N'1.3',  N'1',  N'სიცოცხლის დაზღვევის მაგროვებადი და დაბრუნებადი სახეობები', N'life', 5, 103),
  (N'1.4',  N'1',  N'სიცოცხლის დაზღვევის სხვა ფორმები', N'life', 5, 104),
  (N'2',    null,  N'სამოგზაურო დაზღვევა', N'non_life', 5, 200),
  (N'3',    null,  N'უბედური შემთხვევის დაზღვევა', N'non_life', 5, 300),
  (N'3.1',  N'3',  N'დაზღვევა უბედური შემთხვევებისაგან', N'non_life', 5, 301),
  (N'3.2',  N'3',  N'მგზავრების, მძღოლის ან ეკიპაჟის დაზღვევა უბედური შემთხვევებისაგან', N'non_life', 5, 302),
  (N'4',    null,  N'სამედიცინო (ჯანმრთელობის) დაზღვევა', N'non_life', 2, 400),
  (N'5',    null,  N'სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა (გარდა სარკინიგზო ტრანსპორტისა)', N'non_life', 5, 500),
  (N'5.1',  N'5',  N'სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)', N'non_life', 5, 501),
  (N'5.2',  N'5',  N'სხვა სახმელეთო სატრანსპორტო საშუალებათა დაზღვევა', N'non_life', 5, 502),
  (N'6',    null,  N'სახმელეთო ტრანსპორტის გამოყენებასთან დაკავშირებული სამოქალაქო პასუხისმგებლობის დაზღვევა', N'non_life', 5, 600),
  (N'6.1',  N'6',  N'ავტომოტოტრანსპორტის მფლობელთა სამოქალაქო პასუხისმგებლობის სავალდებულო დაზღვევა', N'non_life', 5, 601),
  (N'6.2',  N'6',  N'სახმელეთო ტრანსპორტის გამოყენებასთან დაკავშირებული სამოქალაქო პასუხისმგებლობის დაზღვევა', N'non_life', 5, 602),
  (N'6.3',  N'6',  N'სახმელეთო გადამზიდველის სამოქალაქო პასუხისმგებლობის დაზღვევა', N'non_life', 5, 603),
  (N'7',    null,  N'სარკინიგზო სატრანსპორტო საშუალებათა დაზღვევა', N'non_life', 5, 700),
  (N'8',    null,  N'საჰაერო სატრანსპორტო საშუალებათა დაზღვევა (კორპუსის დაზღვევა)', N'non_life', 5, 800),
  (N'9',    null,  N'საჰაერო სატრანსპორტო საშუალებათა გამოყენებასთან დაკავშირებული პასუხისმგებლობის დაზღვევა', N'non_life', 5, 900),
  (N'9.1',  N'9',  N'საჰაერო გადამზიდველის პასუხისმგებლობის დაზღვევა', N'non_life', 5, 901),
  (N'9.2',  N'9',  N'სხვა პასუხისმგებლობის დაზღვევა, რომელიც დაკავშირებულია საჰაერო ტრანსპორტის გამოყენებასთან', N'non_life', 5, 902),
  (N'10',   null,  N'მცურავი სატრანსპორტო საშუალებების დაზღვევა (კორპუსის დაზღვევა)', N'non_life', 5, 1000),
  (N'11',   null,  N'მცურავ სატრანსპორტო საშუალებათა გამოყენებასთან დაკავშირებული პასუხისმგებლობის დაზღვევა', N'non_life', 5, 1100),
  (N'11.1', N'11', N'საზღვაო გადამზიდველის პასუხისმგებლობის დაზღვევა', N'non_life', 5, 1101),
  (N'11.2', N'11', N'სხვა პასუხისმგებლობის დაზღვევა, რომელიც დაკავშირებულია მცურავი ტრანსპორტის გამოყენებასთან', N'non_life', 5, 1102),
  (N'12',   null,  N'ტვირთების დაზღვევა', N'non_life', 5, 1200),
  (N'13',   null,  N'ქონების დაზღვევა (გარდა პპ. (5), (7), (8), (10) და (12)-ში ჩამოთვლილი ქონებისა)', N'non_life', 5, 1300),
  (N'14',   null,  N'დაზღვევა საფინანსო დანაკარგებისაგან', N'non_life', 5, 1400),
  (N'15',   null,  N'ვალდებულებათა შესრულების დაზღვევა', N'non_life', 5, 1500),
  (N'15.1', N'15', N'საბაჟო ვალდებულებების შესრულების გარანტიები/ფინანსური რისკის დაზღვევის პოლისები', N'non_life', 5, 1501),
  (N'15.2', N'15', N'სახელმწიფო შესყიდვებთან დაკავშირებული გარანტიები', N'non_life', 5, 1502),
  (N'15.3', N'15', N'სხვა სახის გარანტიები', N'non_life', 5, 1503),
  (N'16',   null,  N'საკრედიტო ვალდებულებათა დაზღვევა', N'non_life', 5, 1600),
  (N'17',   null,  N'სამოქალაქო პასუხისმგებლობის დაზღვევა (გარდა პპ. (6), (9), (11) სახეობებში ჩამოთვლილი პასუხისმგებლობისა)', N'non_life', 5, 1700),
  (N'17.1', N'17', N'პროფესიული პასუხისმგებლობის დაზღვევა', N'non_life', 5, 1701),
  (N'17.2', N'17', N'დამქირავებლის პასუხისმგებლობის დაზღვევა', N'non_life', 5, 1702),
  (N'17.3', N'17', N'სხვა სახის პასუხისმგებლობის დაზღვევა', N'non_life', 5, 1703),
  (N'18',   null,  N'იურიდიული ხარჯების დაზღვევა', N'non_life', 5, 1800);

create table core.branch (
  id      uniqueidentifier not null constraint branch_pkey primary key constraint branch_id_df default newid(),
  code    nvarchar(50) not null constraint branch_code_key unique,
  name_ka nvarchar(400) not null
);

insert into core.branch (code, name_ka) values (N'HQ', N'სათავო ოფისი');

-- ---------------------------------------------------------------- users (legacy 0001, 0007, 0008)

create table core.role (
  code    nvarchar(50) not null constraint role_pkey primary key,
  name_ka nvarchar(200) not null
);

insert into core.role (code, name_ka) values
  (N'admin', N'ადმინისტრატორი'),
  (N'underwriter', N'ანდერრაიტერი'),
  (N'sales', N'გაყიდვების მენეჯერი'),
  (N'claims', N'ზარალების სპეციალისტი'),
  (N'accountant', N'ბუღალტერი'),
  (N'viewer', N'მხოლოდ ნახვა'),
  (N'operations_head', N'საოპერაციო დეპარტამენტის უფროსი');

create table core.app_user (
  id            uniqueidentifier not null constraint app_user_pkey primary key constraint app_user_id_df default newid(),
  username      nvarchar(100) not null constraint app_user_username_key unique
                constraint app_user_username_check check (username = lower(username)),
  full_name     nvarchar(400) not null,
  password_hash nvarchar(400) not null,
  role_code     nvarchar(50) not null constraint app_user_role_code_fkey references core.role (code),
  branch_id     uniqueidentifier null constraint app_user_branch_id_fkey references core.branch (id),
  is_active     bit not null constraint app_user_is_active_df default 1,
  created_at    datetimeoffset(6) not null constraint app_user_created_at_df default sysdatetimeoffset()
);

create table core.session (
  token_hash nvarchar(128) not null constraint session_pkey primary key,
  user_id    uniqueidentifier not null constraint session_user_id_fkey references core.app_user (id) on delete cascade,
  created_at datetimeoffset(6) not null constraint session_created_at_df default sysdatetimeoffset(),
  expires_at datetimeoffset(6) not null
);
create index session_user_idx on core.session (user_id);

-- Login attempts, so password guessing can be slowed down (legacy 0007, review #15).
create table core.login_attempt (
  id           bigint identity(1, 1) not null constraint login_attempt_pkey primary key,
  username     nvarchar(400) not null,
  source       nvarchar(200) null,
  attempted_at datetimeoffset(6) not null constraint login_attempt_attempted_at_df default sysdatetimeoffset(),
  succeeded    bit not null
);
create index login_attempt_username_idx on core.login_attempt (username, attempted_at desc);
create index login_attempt_source_idx on core.login_attempt (source, attempted_at desc);

-- ---------------------------------------------------------------- audit (legacy 0001, 0002, 0007, 0025)

-- Every change to an audited table is recorded: who, when, what it was, what it became. The rows are
-- written by the per-table triggers that core.create_audit_trigger generates (0007_audit.sql).
create table core.audit_log (
  id         bigint identity(1, 1) not null constraint audit_log_pkey primary key,
  at         datetimeoffset(6) not null constraint audit_log_at_df default sysdatetimeoffset(),
  user_id    uniqueidentifier null constraint audit_log_user_id_fkey references core.app_user (id),
  table_name nvarchar(128) not null,
  row_id     nvarchar(200) not null,
  action     nvarchar(10) not null constraint audit_log_action_check check (action in (N'insert', N'update', N'delete')),
  old_row    nvarchar(max) null constraint audit_log_old_row_json check (old_row is null or isjson(old_row) = 1),
  new_row    nvarchar(max) null constraint audit_log_new_row_json check (new_row is null or isjson(new_row) = 1)
);
create index audit_log_row_idx on core.audit_log (table_name, row_id, at desc);

-- ---------------------------------------------------------------- settings (legacy 0012, 0014, 0036)

create table core.setting (
  [key]  nvarchar(200) not null constraint setting_pkey primary key,
  value  nvarchar(max) not null
);

insert into core.setting ([key], value) values
  (N'company_name', N'სადაზღვევო კომპანია (შესავსებია)'),
  (N'company_id_number', N'000000000'),
  (N'company_address', N'მისამართი (შესავსებია)'),
  (N'company_phone', N'+995 ...'),
  (N'company_email', N'info@example.ge'),
  (N'company_licence', N'ლიცენზია № ... (შესავსებია)'),
  (N'complaints_unit', N'მომხმარებელთა პრეტენზიების განხილვის სამსახური'),
  (N'complaints_channels', N'წერილობით ოფისში, ელფოსტით ან ვებგვერდის ფორმით'),
  (N'complaints_response_days', N'30'),
  (N'regulator_name', N'სსიპ საქართველოს დაზღვევის სახელმწიფო ზედამხედველობის სამსახური'),
  (N'regulator_address', N'თბილისი, ... (შესავსებია)'),
  (N'aml.high_risk_countries', N''),
  (N'aml.premium_point_one_gel', N'15000'),
  (N'aml.premium_point_two_gel', N'50000'),
  (N'aml.sanctions_check_max_age_days', N'365'),
  (N'aml.medium_score', N'2'),
  (N'aml.high_score', N'5'),
  (N'aml.review_months_high', N'12'),
  (N'aml.review_months_medium', N'24'),
  (N'aml.review_months_low', N'48'),
  (N'payment.fd_threshold_gel', N'10000.00');
GO

-- The payment authorisation threshold in GEL, or an error when it is not set as an amount
-- (legacy guards CQ004 / RF004 / RV004 read it the same way). @code is the caller's error code.
create procedure core.fd_threshold_gel @code nchar(5), @threshold decimal(15, 2) output
as
begin
  set nocount on;
  declare @stored nvarchar(max) = (select value from core.setting where [key] = N'payment.fd_threshold_gel');
  if core.is_amount_text(@stored) = 0
  begin
    declare @m nvarchar(200) = concat(N'[', @code, N'] the payment authorisation threshold is not set as an amount');
    exec core.fail @m;
  end
  set @threshold = convert(decimal(15, 2), @stored);
end;
GO

-- ---------------------------------------------------------------- numbering (legacy 0005, 0026, 0027)

create table core.number_sequence (
  code       nvarchar(100) not null constraint number_sequence_pkey primary key,
  prefix     nvarchar(50) not null,
  next_value bigint not null constraint number_sequence_next_value_df default 1,
  padding    int not null constraint number_sequence_padding_df default 6
);

insert into core.number_sequence (code, prefix) values
  (N'contract', N'C'), (N'policy:CASCO', N'CAS'), (N'claim', N'CLM'), (N'complaint', N'PR'),
  (N'treaty', N'RI'), (N'aml_case', N'AML'), (N'subrogation', N'RG');
GO

-- Takes the next number of a series, e.g. 'CAS000001'; safe under concurrent use (the row lock of the
-- update). A series that does not exist gives null; one that has run out refuses (NU001) and, with the
-- rest of the transaction, takes no number.
create procedure core.next_number @seq nvarchar(100), @number nvarchar(200) output
as
begin
  set nocount on;
  declare @taken table (taken bigint, prefix nvarchar(50), padding int);
  update core.number_sequence
  set next_value = next_value + 1
  output deleted.next_value, inserted.prefix, inserted.padding into @taken
  where code = @seq;
  declare @value bigint, @prefix nvarchar(50), @padding int;
  select @value = taken, @prefix = prefix, @padding = padding from @taken;
  if @value is null
  begin
    set @number = null;
    return;
  end
  declare @digits nvarchar(20) = convert(nvarchar(20), @value);
  if len(@digits) > @padding
  begin
    declare @m nvarchar(300) = concat(N'[NU001] number series ', @seq, N' has run out: ', @digits, N' does not fit in ', @padding, N' digits');
    exec core.fail @m;
  end
  set @number = @prefix + case when @padding > len(@digits) then replicate(N'0', @padding - len(@digits)) else N'' end + @digits;
end;
GO

-- ---------------------------------------------------------------- accounting periods (legacy 0008, 0025)

-- Closed months are immutable: a closed month is never corrected; anything discovered later is booked
-- in the current open period (owner, 03.10.2026; docs/04 რ14).
create table core.accounting_period (
  period_start date not null constraint accounting_period_pkey primary key
               constraint accounting_period_period_start_check check (day(period_start) = 1),
  status       nvarchar(10) not null constraint accounting_period_status_df default N'open'
               constraint accounting_period_status_check check (status in (N'open', N'closed')),
  closed_at    datetimeoffset(6) null,
  closed_by    uniqueidentifier null constraint accounting_period_closed_by_fkey references core.app_user (id),
  constraint accounting_period_closed_fields check ((status = N'closed' and closed_at is not null) or (status <> N'closed' and closed_at is null))
);
GO

-- Raises when a financial movement is dated inside a closed month. Takes the period lock shared, so a
-- month close (which takes it exclusive) cannot slip in between a movement's check and its commit
-- (docs/04 წესი 19, review H04/H06).
create procedure core.assert_period_open @booked date
as
begin
  set nocount on;
  exec core.xact_lock N'core.accounting_period', N'Shared';
  if exists (select 1 from core.accounting_period
             where period_start = datefromparts(year(@booked), month(@booked), 1) and status = N'closed')
  begin
    declare @m nvarchar(300) = concat(N'[23514] period ', format(@booked, 'yyyy-MM'), N' is closed; book the movement in the current open period');
    exec core.fail @m;
  end
end;
GO

-- The same check for every distinct booking day of a set of rows (multi-row inserts).
create type core.date_list as table (d date not null primary key);
GO

create procedure core.assert_periods_open @days core.date_list readonly
as
begin
  set nocount on;
  exec core.xact_lock N'core.accounting_period', N'Shared';
  declare @closed date = (select top (1) d.d from @days d
                          join core.accounting_period p on p.period_start = datefromparts(year(d.d), month(d.d), 1) and p.status = N'closed'
                          order by d.d);
  if @closed is not null
  begin
    declare @m nvarchar(300) = concat(N'[23514] period ', format(@closed, 'yyyy-MM'), N' is closed; book the movement in the current open period');
    exec core.fail @m;
  end
end;
GO

-- A closed month cannot be changed or removed; a month's date cannot change at all (legacy
-- guard_accounting_period, 0025). The whole row is compared, so a column added later is covered too.
create trigger core.accounting_period_immutable on core.accounting_period
after update, delete
as
begin
  set nocount on;
  if not exists (select 1 from deleted) return;
  declare @m nvarchar(300), @month date;
  if not exists (select 1 from inserted)
  begin
    set @month = (select top (1) period_start from deleted where status = N'closed' order by period_start);
    if @month is not null
    begin
      set @m = concat(N'[23514] period ', format(@month, 'yyyy-MM'), N' is closed and cannot be removed');
      exec core.fail @m;
    end
    return;
  end
  if update(period_start)
     and exists (select 1 from deleted d where not exists (select 1 from inserted i where i.period_start = d.period_start))
  begin
    set @m = N'[23514] the month of an accounting period cannot change';
    exec core.fail @m;
  end
  set @month = (select top (1) d.period_start from deleted d join inserted i on i.period_start = d.period_start
                where d.status = N'closed'
                  and (select d.* for json path, without_array_wrapper, include_null_values)
                      <> (select i.* for json path, without_array_wrapper, include_null_values)
                order by d.period_start);
  if @month is not null
  begin
    set @m = concat(N'[23514] period ', format(@month, 'yyyy-MM'), N' is closed; its status, closing time and author cannot change');
    exec core.fail @m;
  end
end;
GO

-- ---------------------------------------------------------------- permissions (legacy 0032)

-- Groups of users, their members and permissions, and a user's own allow/deny decision on one
-- permission. Which keys exist is the catalogue's business (Insurance.Domain.Common.Permissions);
-- the database keeps only the key's form.
create table core.user_group (
  id         uniqueidentifier not null constraint user_group_pkey primary key constraint user_group_id_df default newid(),
  name       nvarchar(100) not null
             constraint user_group_name_check check (ltrim(rtrim(name)) <> N'' and datalength(name) = datalength(ltrim(rtrim(name))) and len(name) <= 100),
  name_lower as lower(name),
  created_at datetimeoffset(6) not null constraint user_group_created_at_df default sysdatetimeoffset()
);
create unique index user_group_name_idx on core.user_group (name_lower);

create table core.user_group_member (
  id         uniqueidentifier not null constraint user_group_member_pkey primary key constraint user_group_member_id_df default newid(),
  group_id   uniqueidentifier not null constraint user_group_member_group_id_fkey references core.user_group (id),
  user_id    uniqueidentifier not null constraint user_group_member_user_id_fkey references core.app_user (id),
  created_at datetimeoffset(6) not null constraint user_group_member_created_at_df default sysdatetimeoffset(),
  constraint user_group_member_group_id_user_id_key unique (group_id, user_id)
);
create index user_group_member_user_idx on core.user_group_member (user_id);

create table core.user_group_permission (
  id         uniqueidentifier not null constraint user_group_permission_pkey primary key constraint user_group_permission_id_df default newid(),
  group_id   uniqueidentifier not null constraint user_group_permission_group_id_fkey references core.user_group (id),
  permission nvarchar(200) not null constraint user_group_permission_permission_check check (core.is_permission_key(permission) = 1),
  created_at datetimeoffset(6) not null constraint user_group_permission_created_at_df default sysdatetimeoffset(),
  constraint user_group_permission_group_id_permission_key unique (group_id, permission)
);

create table core.user_permission (
  id         uniqueidentifier not null constraint user_permission_pkey primary key constraint user_permission_id_df default newid(),
  user_id    uniqueidentifier not null constraint user_permission_user_id_fkey references core.app_user (id),
  permission nvarchar(200) not null constraint user_permission_permission_check check (core.is_permission_key(permission) = 1),
  decision   nvarchar(10) not null constraint user_permission_decision_check check (decision in (N'allow', N'deny')),
  created_at datetimeoffset(6) not null constraint user_permission_created_at_df default sysdatetimeoffset(),
  constraint user_permission_user_id_permission_key unique (user_id, permission)
);

-- ---------------------------------------------------------------- products (legacy 0005, 0012)

create table core.product (
  id          uniqueidentifier not null constraint product_pkey primary key constraint product_id_df default newid(),
  code        nvarchar(50) not null constraint product_code_key unique,
  name_ka     nvarchar(400) not null,
  object_type nvarchar(50) not null,
  is_active   bit not null constraint product_is_active_df default 1
);

create table core.product_coverage (
  id           uniqueidentifier not null constraint product_coverage_pkey primary key constraint product_coverage_id_df default newid(),
  product_id   uniqueidentifier not null constraint product_coverage_product_id_fkey references core.product (id),
  code         nvarchar(50) not null,
  name_ka      nvarchar(400) not null,
  class_code   nvarchar(20) not null constraint product_coverage_class_code_fkey references core.insurance_class (code),
  is_mandatory bit not null constraint product_coverage_is_mandatory_df default 0,
  sort_order   int not null,
  constraint product_coverage_product_id_code_key unique (product_id, code)
);

-- Product texts for printed documents (terms version, exclusions, procedures...).
create table core.product_text (
  product_id uniqueidentifier not null constraint product_text_product_id_fkey references core.product (id),
  [key]      nvarchar(200) not null,
  value      nvarchar(max) not null,
  constraint product_text_pkey primary key (product_id, [key])
);

declare @casco uniqueidentifier = newid();
insert into core.product (id, code, name_ka, object_type) values (@casco, N'CASCO', N'ავტოდაზღვევა (კასკო)', N'vehicle');
insert into core.product_coverage (product_id, code, name_ka, class_code, is_mandatory, sort_order) values
  (@casco, N'damage',      N'დაზიანება (ავარია, ხანძარი, სტიქია, მესამე პირის ქმედება)', N'5.1', 1, 1),
  (@casco, N'theft',       N'ქურდობა / გატაცება',                                        N'5.1', 0, 2),
  (@casco, N'mtpl',        N'მესამე პირის წინაშე პასუხისმგებლობა',                        N'6.2', 0, 3),
  (@casco, N'accident',    N'მძღოლის და მგზავრების უბედური შემთხვევა',                    N'3.2', 0, 4),
  (@casco, N'equipment',   N'დამატებითი მოწყობილობა',                                    N'5.1', 0, 5),
  (@casco, N'assistance',  N'ევაკუატორი / ტექდახმარება',                                 N'14',  0, 6),
  (@casco, N'replacement', N'შემცვლელი ავტომობილი',                                      N'14',  0, 7);
insert into core.product_text (product_id, [key], value) values
  (@casco, N'documents', N'დაზღვევის ხელშეკრულება შედგება: ამ პოლისისგან, განაცხადისგან, დაზღვევის პირობებისგან და საინფორმაციო ფურცლისგან.'),
  (@casco, N'terms_version', N'ავტოდაზღვევის (კასკო) პირობები, ვერსია 1.0 (შესავსებია)'),
  (@casco, N'start_condition', N'დაზღვევა ძალაში შედის მითითებული თარიღის 00:00 საათიდან, პირველი შენატანის გადახდის პირობით, და მთავრდება დასრულების თარიღის წინა დღის 24:00 საათზე.'),
  (@casco, N'claims_procedure', N'ზარალის შემთხვევაში დამზღვევი ვალდებულია დაუყოვნებლივ, არაუგვიანეს 3 სამუშაო დღისა, აცნობოს მზღვეველს; პროცედურა აღწერილია დაზღვევის პირობების შესაბამის მუხლებში (შესავსებია).'),
  (@casco, N'disclosure_duty', N'დამზღვევი ვალდებულია მზღვეველს მიაწოდოს სრული და ზუსტი ინფორმაცია დაზღვევის საგნისა და რისკის შესახებ და აცნობოს მათი ცვლილება; ამ ვალდებულების დარღვევა შეიძლება გახდეს ანაზღაურებაზე უარის საფუძველი.'),
  (@casco, N'withdrawal', N'ხელშეკრულებიდან გასვლის პირობები, ვადა და წესი განსაზღვრულია დაზღვევის პირობებით (შესავსებია).'),
  (@casco, N'termination', N'ხელშეკრულება შეიძლება ვადამდე შეწყდეს დამზღვევის განცხადებით; დაბრუნებას ექვემდებარება გამოუმუშავებელი პრემია შეწყვეტის დღიდან, საფასურის გარეშე.'),
  (@casco, N'exclusions', N'არ ანაზღაურდება ზარალი, რომელიც გამოწვეულია: განზრახი ქმედებით; უხეში გაუფრთხილებლობით (დაზღვევის პირობების შესაბამისი მუხლის მიხედვით); ავტომობილის მართვით ალკოჰოლური, ნარკოტიკული ან ფსიქოტროპული ზემოქმედების ქვეშ; მართვის უფლების გარეშე; საომარი მოქმედებებით, ტერორიზმით, რადიაციით. სრული სია: დაზღვევის პირობები, გამონაკლისების მუხლი (შესავსებია).');
GO

-- ---------------------------------------------------------------- parties (legacy 0001, 0014, 0017)

-- Any person or organization, entered once and reused in every role (policyholder, insured,
-- beneficiary, agent, reinsurer...). Fields follow the internal record-keeping order №11/2016
-- (annex 2) and the customer identification rule (FMS order №2/2020).
create table core.party (
  id                   uniqueidentifier not null constraint party_pkey primary key constraint party_id_df default newid(),
  kind                 nvarchar(20) not null constraint party_kind_check check (kind in (N'person', N'organization')),
  is_resident          bit not null constraint party_is_resident_df default 1,
  -- Personal number (11 digits) or identification code (9 digits) for residents.
  tax_id               nvarchar(50) null,
  -- Passport or foreign registration number for non-residents.
  foreign_id           nvarchar(100) null,
  country_code         nchar(2) not null constraint party_country_code_df default N'GE',
  first_name           nvarchar(200) null,
  last_name            nvarchar(200) null,
  birth_date           date null,
  gender               nvarchar(10) null constraint party_gender_check check (gender in (N'male', N'female')),
  name                 nvarchar(400) null,
  legal_form           nvarchar(200) null,
  state_share_percent  decimal(5, 2) null constraint party_state_share_percent_check check (state_share_percent between 0 and 100),
  is_public_entity     bit not null constraint party_is_public_entity_df default 0,
  registration_place   nvarchar(max) null,
  legal_address        nvarchar(max) null,
  actual_address       nvarchar(max) null,
  phone                nvarchar(100) null,
  email                nvarchar(320) null,
  risk_level           nvarchar(10) null constraint party_risk_level_check check (risk_level in (N'low', N'medium', N'high')),
  identified_at        date null,
  verified_at          date null,
  is_pep               bit not null constraint party_is_pep_df default 0,
  sanctions_checked_at date null,
  is_group_member      bit not null constraint party_is_group_member_df default 0,
  display_name         as (case when kind = N'person'
                                then ltrim(rtrim(coalesce(last_name, N'') + N' ' + coalesce(first_name, N'')))
                                else name end) persisted,
  -- Reporting segment (financial reporting order №02/2013, annex 2, art. 8).
  segment              as (convert(nvarchar(10), case
                             when kind = N'person' then N'retail'
                             when is_public_entity = 1 or coalesce(state_share_percent, 0) >= 50 then N'state'
                             else N'corporate' end)) persisted,
  created_at           datetimeoffset(6) not null constraint party_created_at_df default sysdatetimeoffset(),
  updated_at           datetimeoffset(6) not null constraint party_updated_at_df default sysdatetimeoffset(),
  risk_assessed_on     date null,
  risk_review_on       date null,
  self_declaration_on  date null,
  sanctions_blocked    bit not null constraint party_sanctions_blocked_df default 0,
  constraint party_person_fields check (kind <> N'person' or (first_name is not null and last_name is not null)),
  constraint party_organization_fields check (kind <> N'organization' or name is not null),
  constraint party_identifier check (tax_id is not null or foreign_id is not null),
  constraint party_tax_id_format check (
    tax_id is null
    or (kind = N'person' and datalength(tax_id) = 22 and tax_id collate Latin1_General_BIN2 not like N'%[^0-9]%')
    or (kind = N'organization' and datalength(tax_id) = 18 and tax_id collate Latin1_General_BIN2 not like N'%[^0-9]%'))
);
create unique index party_tax_id_key on core.party (tax_id) where tax_id is not null;
alter table core.party add display_name_lower as lower(case when kind = N'person'
  then ltrim(rtrim(coalesce(last_name, N'') + N' ' + coalesce(first_name, N''))) else name end) persisted;
create index party_display_name_idx on core.party (display_name_lower);
create index party_risk_review_idx on core.party (risk_review_on);

-- Agents, brokers and other sales channels (order №11/2016, annexes 5 and 6).
create table core.intermediary (
  id                 uniqueidentifier not null constraint intermediary_pkey primary key constraint intermediary_id_df default newid(),
  party_id           uniqueidentifier not null constraint intermediary_party_id_fkey references core.party (id),
  kind               nvarchar(20) not null constraint intermediary_kind_check check (kind in (N'agent', N'broker', N'bank', N'employee')),
  contract_no        nvarchar(200) null,
  contract_date      date null,
  valid_to           date null,
  status             nvarchar(20) not null constraint intermediary_status_df default N'active'
                     constraint intermediary_status_check check (status in (N'active', N'terminated')),
  commission_percent decimal(5, 2) null constraint intermediary_commission_percent_check check (commission_percent between 0 and 100),
  created_at         datetimeoffset(6) not null constraint intermediary_created_at_df default sysdatetimeoffset()
);
create index intermediary_party_idx on core.intermediary (party_id);
