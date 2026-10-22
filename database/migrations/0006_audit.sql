-- Audit of every change to the audited tables: who, when, what it was, what it became (legacy core.audit(),
-- 0001, 0007, 0025, 0027). PostgreSQL's one trigger function used to_jsonb(row), so a column added later
-- was covered automatically. Here each table gets its own trigger, generated from core.audit_config and the
-- table's current columns; the migration runner regenerates all of them after every run
-- (core.refresh_audit_triggers), so a column added by a later migration is covered too.
--
-- Same rules as the legacy function: password hashes are never logged; an update that changes nothing
-- writes nothing; the row id is the key column's text (uuid in lower case, as PostgreSQL prints it);
-- the actor is SESSION_CONTEXT(N'core.user_id') of the transaction.

create table core.audit_config (
  table_name     sysname not null constraint audit_config_pkey primary key,
  key_column     sysname not null constraint audit_config_key_column_df default N'id',
  -- When set, an update is logged only if one of these columns changed (legacy number_sequence_audit_update, 0027).
  update_columns nvarchar(400) null
);

insert into core.audit_config (table_name, key_column, update_columns) values
  (N'accounting_period', N'period_start', null),
  (N'aml_case', N'id', null),
  (N'app_user', N'id', null),
  (N'claim', N'id', null),
  (N'claim_payment_request', N'id', null),
  (N'claim_reserve_request', N'id', null),
  (N'complaint', N'id', null),
  (N'contract', N'id', null),
  (N'fms_suspension', N'id', null),
  (N'installment', N'id', null),
  (N'insured_object', N'id', null),
  (N'intermediary', N'id', null),
  (N'loss_event', N'id', null),
  (N'number_sequence', N'code', N'prefix,padding'),
  (N'party', N'id', null),
  (N'party_beneficial_owner', N'id', null),
  (N'policy', N'id', null),
  (N'policy_line', N'id', null),
  (N'premium_refund_request', N'id', null),
  (N'premium_refund_request_line', N'id', null),
  (N'product', N'id', null),
  (N'product_coverage', N'id', null),
  (N'receipt_reversal_request', N'id', null),
  (N'receipt_reversal_request_line', N'id', null),
  (N'ri_treaty', N'id', null),
  (N'sanctions_screening', N'id', null),
  (N'setting', N'key', null),
  (N'subrogation_case', N'id', null),
  (N'subrogation_movement', N'id', null),
  (N'subrogation_party', N'id', null),
  (N'user_group', N'id', null),
  (N'user_group_member', N'id', null),
  (N'user_group_permission', N'id', null),
  (N'user_permission', N'id', null);
GO

-- Columns of this port that the legacy rows did not have (search helpers) and the password hash stay out of the log.
create function core.audit_skips_column(@column sysname) returns bit
as
begin
  return case when @column in (N'password_hash', N'display_name_lower', N'name_lower') then 1 else 0 end;
end;
GO

-- (Re)creates the audit trigger of one table from its current columns.
create procedure core.create_audit_trigger @table sysname
as
begin
  set nocount on;
  declare @key sysname, @update_columns nvarchar(400), @object int = object_id(N'core.' + quotename(@table));
  select @key = key_column, @update_columns = update_columns from core.audit_config where table_name = @table;
  if @key is null or @object is null
  begin
    declare @m nvarchar(300) = concat(N'[42P01] audit: unknown table ', @table);
    exec core.fail @m;
  end

  -- The JSON of one row of alias r: every column, uuids in lower case.
  declare @cols nvarchar(max) = (
    select string_agg(convert(nvarchar(max),
             case when t.name = N'uniqueidentifier'
                  then N'lower(convert(nchar(36), r.' + quotename(c.name) + N')) as ' + quotename(c.name)
                  else N'r.' + quotename(c.name) + N' as ' + quotename(c.name) end), N', ') within group (order by c.column_id)
    from sys.columns c join sys.types t on t.user_type_id = c.user_type_id
    where c.object_id = @object and core.audit_skips_column(c.name) = 0);
  declare @key_type sysname = (select t.name from sys.columns c join sys.types t on t.user_type_id = c.user_type_id
                               where c.object_id = @object and c.name = @key);
  declare @row_id nvarchar(400) = case @key_type
    when N'uniqueidentifier' then N'lower(convert(nchar(36), r.' + quotename(@key) + N'))'
    when N'date' then N'convert(nvarchar(10), r.' + quotename(@key) + N', 23)'
    else N'convert(nvarchar(200), r.' + quotename(@key) + N')' end;
  declare @json_new nvarchar(max) = N'(select ' + replace(@cols, N'r.', N'i.') + N' for json path, without_array_wrapper, include_null_values)';
  declare @json_old nvarchar(max) = N'(select ' + replace(@cols, N'r.', N'd.') + N' for json path, without_array_wrapper, include_null_values)';
  declare @changed nvarchar(max) = N'1 = 1';
  if @update_columns is not null
    set @changed = (select string_agg(convert(nvarchar(max),
                      N'not ((i.' + quotename(trim(value)) + N' is null and d.' + quotename(trim(value)) + N' is null) or i.' + quotename(trim(value))
                      + N' = d.' + quotename(trim(value)) + N')'), N' or ')
                    from string_split(@update_columns, N','));

  declare @sql nvarchar(max) = N'
create or alter trigger core.' + quotename(@table + N'_audit') + N' on core.' + quotename(@table) + N'
after insert, update, delete
as
begin
  set nocount on;
  declare @actor uniqueidentifier = core.current_actor();
  if exists (select 1 from inserted) and not exists (select 1 from deleted)
    insert core.audit_log (user_id, table_name, row_id, action, old_row, new_row)
    select @actor, N' + quotename(@table, '''') + N', ' + replace(@row_id, N'r.', N'i.') + N', N''insert'', null, ' + @json_new + N'
    from inserted i;
  else if exists (select 1 from deleted) and not exists (select 1 from inserted)
    insert core.audit_log (user_id, table_name, row_id, action, old_row, new_row)
    select @actor, N' + quotename(@table, '''') + N', ' + replace(@row_id, N'r.', N'd.') + N', N''delete'', ' + @json_old + N', null
    from deleted d;
  else
    insert core.audit_log (user_id, table_name, row_id, action, old_row, new_row)
    select @actor, N' + quotename(@table, '''') + N', x.row_id, N''update'', x.old_row, x.new_row
    from (
      select ' + replace(@row_id, N'r.', N'i.') + N' as row_id, ' + @json_old + N' as old_row, ' + @json_new + N' as new_row
      from inserted i join deleted d on d.' + quotename(@key) + N' = i.' + quotename(@key) + N'
      where ' + @changed + N'
    ) x
    where x.old_row <> x.new_row collate Latin1_General_BIN2;
end;';
  exec sys.sp_executesql @sql;
end;
GO

-- Regenerates every audit trigger; the migration runner calls it after each run.
create procedure core.refresh_audit_triggers
as
begin
  set nocount on;
  declare @table sysname;
  declare tables cursor local fast_forward for select table_name from core.audit_config order by table_name;
  open tables;
  fetch next from tables into @table;
  while @@fetch_status = 0
  begin
    exec core.create_audit_trigger @table;
    fetch next from tables into @table;
  end
  close tables;
  deallocate tables;
end;
GO

exec core.refresh_audit_triggers;
GO
