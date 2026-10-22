-- Record of imports from the old PostgreSQL database (Insurance.Tools import-postgres). One row per run: where the
-- data came from (host and database, never a password), when, and how many rows of each table were added. The
-- application only reads it.

create table core.data_import (
  id          uniqueidentifier not null constraint data_import_pkey primary key constraint data_import_id_df default newid(),
  source      nvarchar(400) not null,
  started_at  datetimeoffset(6) not null,
  finished_at datetimeoffset(6) not null constraint data_import_finished_at_df default sysdatetimeoffset(),
  -- First run into a fresh database ('initial') or a repeated run that only adds rows the database does not have yet ('repeat').
  mode        nvarchar(10) not null constraint data_import_mode_check check (mode in (N'initial', N'repeat')),
  -- {"table": rows added, ...}
  added       nvarchar(max) not null constraint data_import_added_json check (isjson(added) = 1),
  imported_by nvarchar(128) not null constraint data_import_imported_by_df default suser_sname()
);
GO

grant select on core.data_import to core_app;
GO
