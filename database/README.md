# Database (Microsoft SQL Server)

All application tables live in schema `core`, with the table and column names of the old PostgreSQL schema. `migrations/` holds the schema, applied in file-name order by `Insurance.Tools migrate` (the files are also embedded in `Insurance.Application`, so the web host and the tests use the same ones). Each file runs in one transaction together with its record in `core.schema_migration`; batches are separated by `GO` lines.

The migrations are the **final state** of the legacy schema (commit `17c1f1f`, migrations 0001–0038), not a replay of its history; each object names the legacy migration it comes from. Files 0001–0008 hold that state; 0009–0014 add what came after the baseline, each a new file (an applied file is never edited): 0009 the regress case pass (Codex R02), 0010 the sanctions false-positive reuse (owner, 23.10.2026), 0011 the reinsurer's commission settlement (legacy PR #362, its 0039), 0012 a GEL claim on a USD/EUR policy (H46), 0013 that claim's snapshot guarded NULL-safely (Codex R005-03), 0014 the embedded OpenID Connect provider's used authorization codes, each exchanged once (ADR 0003, `docs/architecture/adr/0003-authentication.md`).

## Requirements of the database

- Collation `Latin1_General_100_CS_AS_SC`: text compares case-sensitively, as in PostgreSQL (`CAS001` and `cas001` are different numbers).
- `READ_COMMITTED_SNAPSHOT ON`: read committed with row versioning, which is PostgreSQL's read committed (a statement sees what was committed when it started, readers never wait).

`Insurance.Tools create-database <name>` creates a database with both; the migration runner refuses a database without them.

## Roles

- `core_app` — the application. Reads and writes `core` tables, cannot change the schema. Ledgers and requests are append-only (no update or delete): `audit_log`, `premium_transaction`, `installment`, `installment_adjustment`, `payment`, `payment_allocation`, `premium_cession`, `claim_payment`, `claim_payment_request`, `claim_recovery`, `claim_reserve`, `claim_reserve_request`, `party_risk_assessment`, `premium_refund_request(_line)`, `receipt_reversal_request(_line)`, `ri_commission_settlement`, `subrogation_movement`. Accounting periods, regress cases and their parties are never deleted. The migration journal is read-only. The runner re-asserts all of this after every run and checks the effective rights as a member of the role.
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

1. **No deferred constraints.** PostgreSQL checked at commit that a regress-case recovery had its movement (`claim_recovery_case_movement`, SG034). SQL Server cannot defer a check, so the recovery and its movement are written together by `core.record_case_money`. That procedure first writes a pass for the recovery to `core.case_money_pass`, and the recovery guard accepts money of a case only by using up a pass for that row written in the same session (migration 0009). `core_app` has no right to that table at all (explicitly denied, and the migration runner checks it). The procedure, the guard and the table share an owner, so ownership chaining lets those two modules reach the table where a direct statement of the application cannot. Setting a session key by hand, which 0003's guard trusted, no longer lets the application write a recovery alone (Codex R02). The view `core.case_recovery_without_movement` is still checked by the reconciliation and the tests and must always be empty.
2. **`now()`** is the start of the transaction in PostgreSQL and the time of the statement here: rows written in one transaction may get slightly different `created_at`. Where the legacy code ordered by `created_at, id`, rows of one transaction came out in their ids' (random) order there and in the order they were written here.
3. **`THROW` reads `%` as a format character**, so every refusal goes through `core.fail`, which doubles it.
4. **Text length**: PostgreSQL `text` is unbounded, and so is every text a person types here (names, addresses, document numbers, notes, references: `nvarchar(max)`). Only keys, codes and indexed columns have a length (numbers 100, codes 20–50, permission keys and settings 200, user names 100 as in the legacy check, login attempts' user 400 and source 200 — the login keeps 100 and 200 characters of them, as the legacy login kept 100 of the user name). The client-name index (legacy `lower(display_name)`) is on the name's first 400 characters (`display_name_sort`, an index key holds at most 1700 bytes); lists order by it and then by the whole name.
5. **Upper/lower case of Georgian capitals (Mtavruli)**: `lower()`/`upper()` follow the SQL Server collation, which predates Mtavruli; document numbers are ASCII, so lookups by number are not affected.

## Test databases

Integration and browser tests create their own database on a local server (`ins_it_<time>_<random>`), migrate it, create an application login for it, and drop both afterwards. They never touch another database. Settings: `.env.test` (see `.env.test.example`, git-ignored).
