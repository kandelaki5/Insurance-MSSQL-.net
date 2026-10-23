-- The time a row is stamped with is the time its transaction began, as PostgreSQL's now() was (legacy defaults and
-- updates used now(), the same instant for every statement of one transaction). SQL Server's sysdatetimeoffset() is
-- the time of each statement, so rows written together got different times and the legacy reads that order by
-- created_at and then by a second key (receipt lines: created_at, amount_gel, id) saw another order. The application
-- puts its transaction's start in the session context (Db.InTransactionAsync, key core.tx_at); without it — a
-- statement outside the application's transactions — the statement's own time is used, as before.

create function core.tx_now() returns datetimeoffset(6)
as
begin
  return coalesce(convert(datetimeoffset(6), session_context(N'core.tx_at')), sysdatetimeoffset());
end
GO

-- Every default of a core table that stamped the statement's time now stamps the transaction's.
declare @sql nvarchar(max) = N'';
select @sql += N'alter table core.' + quotename(t.name) + N' drop constraint ' + quotename(d.name) + N'; '
            + N'alter table core.' + quotename(t.name) + N' add constraint ' + quotename(d.name)
            + N' default core.tx_now() for ' + quotename(c.name) + N'; '
from sys.default_constraints d
join sys.tables t on t.object_id = d.parent_object_id
join sys.columns c on c.object_id = d.parent_object_id and c.column_id = d.parent_column_id
where t.schema_id = schema_id(N'core') and t.name <> N'schema_migration'
  and replace(replace(d.definition, N'(', N''), N')', N'') = N'sysdatetimeoffset';
exec sys.sp_executesql @sql;
GO

grant execute on core.tx_now to core_app;
GO
