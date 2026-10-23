-- Removes the temporary migration reader of migration-reader.sql. Run as the old database's owner, in that database:
--   psql "<the old database>" -f database/postgres/migration-reader-drop.sql
drop owned by insurance_migration_reader;
drop role insurance_migration_reader;
