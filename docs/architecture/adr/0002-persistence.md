# ADR 0002 — Dapper repositories per aggregate, targeted queries, one transaction per use case

Status: proposed (INS-DOTNET-MSSQL-004 B2), 24.10.2026. ქართულად: Dapper რჩება; ერთი მოქმედება = ერთი ტრანზაქცია, როგორც ახლა.

## Decision
- Dapper with parameterized SQL stays; no EF Core for application data.
- **Unit of work = the existing `Tx`.** A use case opens one `IUnitOfWork` (one connection, one transaction, the actor's session context for the audit triggers — `core.current_actor()`), and every repository/query it uses takes that unit of work. The binding permission check, the locks (`updlock/holdlock`, `tx.LockAsync`), the ledger writes and the audit happen inside it, as they do now. No repository opens its own connection or commits.
- Repositories follow aggregates and use cases (policy with its lines/schedule; claim with its reserve/payments; receipt with its allocations; treaty with its cessions; …), not tables. Reports and screens read through targeted `I*Query` interfaces returning the read models they show.
- The actor is the server-side `app_user.id` of the session (ADR 0003), never a value from a request body. A pooled connection never carries another user's session context: the context is set at the start of the transaction and the connection is reset on return (as `Db` does today).
- The database's guarantees stay where they are: `core_app` least rights, append-only ledgers, closed-period triggers, SG034's pass, the unique and check constraints, race/deadlock handling and the "answer of the database" mapping (`DbError`).
- Migrations: applied files are never edited; a schema change is a new numbered file; the admin login migrates, the app login only runs.

## Consequences
The first extraction (policies list/detail, ADR 0004/B5) moves the list's and card's SQL out of the page models into `IPolicyListQuery`/`IPolicyCardQuery` implemented in Infrastructure, shared by the Razor pages and the API so both show the same figures.
