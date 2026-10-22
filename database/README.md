# Database (Microsoft SQL Server)

All application tables live in schema `core`, with the table and column names of the old PostgreSQL schema. `migrations/` holds the schema, applied in file-name order by `Insurance.Tools migrate` (the files are also embedded in `Insurance.Application`, so the web host and the tests use the same ones). Each file runs in one transaction together with its record in `core.schema_migration`; batches are separated by `GO` lines.

The migrations are the **final state** of the legacy schema (commit `17c1f1f`, migrations 0001–0038), not a replay of its history; each object names the legacy migration it comes from.

## Requirements of the database

- Collation `Latin1_General_100_CS_AS_SC`: text compares case-sensitively, as in PostgreSQL (`CAS001` and `cas001` are different numbers).
- `READ_COMMITTED_SNAPSHOT ON`: read committed with row versioning, which is PostgreSQL's read committed (a statement sees what was committed when it started, readers never wait).

`Insurance.Tools create-database <name>` creates a database with both; the migration runner refuses a database without them.

## Roles

- `core_app` — the application. Reads and writes `core` tables, cannot change the schema. Ledgers and requests are append-only (no update or delete): `audit_log`, `premium_transaction`, `installment`, `installment_adjustment`, `payment`, `payment_allocation`, `premium_cession`, `claim_payment`, `claim_payment_request`, `claim_recovery`, `claim_reserve`, `claim_reserve_request`, `party_risk_assessment`, `premium_refund_request(_line)`, `receipt_reversal_request(_line)`, `subrogation_movement`. Accounting periods, regress cases and their parties are never deleted. The migration journal is read-only. The runner re-asserts all of this after every run and checks the effective rights as a member of the role.
- `core_reader` — reviewers: select on every table and view except `app_user` and `session`.

The login the application connects with carries a password, so it is not part of the migrations:

```
INSURANCE_MASTER_CONNECTION="Server=…;Database=master;User ID=<admin>;Password=…;TrustServerCertificate=True"
INSURANCE_ADMIN_CONNECTION="Server=…;Database=<name>;User ID=<admin>;Password=…;TrustServerCertificate=True"
INSURANCE_APP_PASSWORD="<a new strong password>"
dotnet run --project src/Insurance.Tools -- create-app-login insurance_app
```

## How PostgreSQL features are kept

| PostgreSQL | SQL Server |
|---|---|
| `BEFORE INSERT` row triggers (guards) | `INSTEAD OF INSERT` triggers that check each row, in the legacy trigger order, before writing it — the same semantics, also for multi-row inserts |
| `BEFORE UPDATE/DELETE` guards that only refuse | `AFTER` triggers that refuse (the whole statement rolls back) |
| guard modifying the row (`fms_suspension_target`) | `INSTEAD OF INSERT` / `INSTEAD OF UPDATE` |
| `raise exception … using errcode = 'RI001'` | `core.fail '[RI001] …'` → error 51000; the application maps the bracketed code exactly as the SQLSTATE before |
| `pg_advisory_xact_lock(_shared)` | `core.xact_lock` → `sp_getapplock` owned by the transaction (`Exclusive` / `Shared`) |
| `set_config('core.user_id', …, true)` | `sp_set_session_context N'core.user_id'`, set for the transaction and cleared after it |
| `core.audit()` with `to_jsonb(row)` | a generated trigger per table (`core.create_audit_trigger`, from `core.audit_config` and the current columns), regenerated after every migration run |
| `core.next_number(seq)` function | `core.next_number` procedure with an output parameter |
| generated columns | persisted computed columns |
| partial unique indexes | filtered unique indexes |
| `jsonb`, `text[]` | `nvarchar(max)` with an `ISJSON` check (arrays as JSON arrays) |
| `uuid`, `timestamptz`, `numeric`, `boolean` | `uniqueidentifier`, `datetimeoffset(6)`, `decimal`, `bit` |

## Differences that remain (and how they are covered)

1. **No deferred constraints.** PostgreSQL checked at commit that a regress-case recovery had its movement (`claim_recovery_case_movement`, SG034). SQL Server cannot defer a check, so the recovery and its movement are written together by `core.record_case_money`; a recovery with a case is accepted only while that procedure names it in the session context. An application that deliberately set that key itself could write a recovery alone — the view `core.case_recovery_without_movement` must therefore always be empty, and the reconciliation and the tests check it.
2. **`now()`** is the start of the transaction in PostgreSQL and the time of the statement here: rows written in one transaction may get slightly different `created_at`. Where the legacy code ordered by `created_at, id`, rows of one transaction came out in their ids' (random) order there and in the order they were written here.
3. **`THROW` reads `%` as a format character**, so every refusal goes through `core.fail`, which doubles it.
4. **Text length**: PostgreSQL `text` is unbounded; columns that are keys or indexed have a length here (numbers 100, codes 20–50, names 200–400 characters). The application validates lengths before writing.
5. **Upper/lower case of Georgian capitals (Mtavruli)**: `lower()`/`upper()` follow the SQL Server collation, which predates Mtavruli; document numbers are ASCII, so lookups by number are not affected.

## Test databases

Integration and browser tests create their own database on a local server (`ins_it_<time>_<random>`), migrate it, create an application login for it, and drop both afterwards. They never touch another database. Settings: `.env.test` (see `.env.test.example`, git-ignored).
