-- A temporary login that reads the old PostgreSQL database for the data migration (Insurance.Tools import-postgres and
-- reconcile), and nothing more (Codex R06). The old core_reader is not enough: it cannot read core.app_user, whose users
-- and password hashes the import carries over. Run as the old database's owner, in that database:
--
--   psql "<the old database>" -f database/postgres/migration-reader.sql
--   psql "<the old database>" -c '\password insurance_migration_reader'
--
-- and give the import that login (INSURANCE_PG_CONNECTION). It can read every table of schema core but the sessions and
-- the migration journal (neither is carried over), it can change nothing, and every transaction it starts is read-only.
-- Remove it when the migration is done: database/postgres/migration-reader-drop.sql.

create role insurance_migration_reader login nosuperuser nocreatedb nocreaterole noinherit;
alter role insurance_migration_reader set default_transaction_read_only = on;
do $$ begin execute format('grant connect on database %I to insurance_migration_reader', current_database()); end $$;
grant usage on schema core to insurance_migration_reader;
grant select on all tables in schema core to insurance_migration_reader;
revoke select on core.session, core.schema_migration from insurance_migration_reader;
