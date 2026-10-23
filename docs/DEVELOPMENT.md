# Development conventions

How the old TypeScript/PostgreSQL program (Insurance-Software, commit `17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37`) maps onto this C#/SQL Server program. Business rules and texts do not change in the port; only the technology does.

## Layout

| Old | New |
|---|---|
| `src/finance/*.ts` | `src/Insurance.Domain/Finance` — pure rules, `long` minor units, `DateOnly` |
| `src/compliance/*.ts`, pure `src/lib/*.ts` | `src/Insurance.Domain/Compliance`, `src/Insurance.Domain/Common` |
| `src/lib/regulatory/*` | `src/Insurance.Domain/Regulatory` (templates embedded) |
| `src/i18n/*.ts` | `src/Insurance.Application/Texts` — one static class per file, texts byte-for-byte |
| `src/lib/*.ts` that read or write the database | `src/Insurance.Application/Services` — static classes taking a `Tx` |
| `src/app/(app)/**/page.tsx` + `actions.ts` | `src/Insurance.Web/Pages/**` — Razor Pages at the same URL; a server action is a page handler |
| `db/migrations/*.sql` | `database/migrations/*.sql` (T-SQL, final legacy schema; see `database/README.md`) |
| `scripts/*.ts` | `src/Insurance.Tools` (admin CLI) and tests |
| `*.test.ts` | `tests/Insurance.Domain.Tests` (pure), `tests/Insurance.Integration.Tests` (real SQL Server), `tests/Insurance.Web.Tests` (HTTP + browser) |

## Database access

- `Db.AsUserAsync(userId, tx => …)` — one transaction attributed to the user (legacy `asUser`): audit triggers and `created_by` defaults see the actor. Commits on return, rolls back on throw; `XACT_ABORT ON`, so any error ends the transaction, as in PostgreSQL.
- `Db.ReadTxAsync(tx => …)` — reads without a transaction; the same read helpers serve pages and transactions.
- SQL is written in services and pages with Dapper, always parameterised. Table and column names are the legacy ones (snake_case). Map rows to records with `as PascalName` aliases or tuples.
- PostgreSQL → T-SQL: `select … for update` → `with (updlock, rowlock, holdlock)`; `pg_advisory_xact_lock(hashtext('x'), hashtext(id))` → `tx.LockAsync("x", id)`; `returning` → generate the id in C# (`Guid.NewGuid()`) and insert it (tables have triggers, so `OUTPUT` without `INTO` is not allowed); `on conflict do nothing` → `if not exists (…) insert …`; `on conflict do update` → `merge … with (holdlock)`; `distinct on` → `row_number()`; `filter (where …)` → `case when … then … end`; `= any(@ids)` → `in @ids` (at most ~2000 ids per statement, use `Billing.InChunksAsync`); `ilike` → `lower(x) like lower(@q)`; `limit n` → `top (n)` / `offset … fetch`; `now()` → `sysdatetimeoffset()`; `current_date` in Tbilisi → `Rates.TodayInTbilisi()` passed as a parameter.
- Ordering by a uuid: PostgreSQL orders uuids as lower-case text; use `order by lower(convert(nchar(36), id)) collate Latin1_General_BIN2` where the order is visible.
- Number series: `await tx.NextNumberAsync("claim")` (null when the series does not exist).
- Database refusals: `DbError.From(exception)` gives the legacy code — `23505` unique, `23514` check / closed period (`Constraint` null for the period guard), `23503` foreign key, guard codes such as `RI001`, `PA002`, `CQ003`. Map them to the same Georgian messages the old action returned.

## Pages

- Every page inherits `AppPageModel`; the user is `Me` (`CurrentUser`), `Me.CanWrite` is the legacy `canWrite`, `Me.IsAdmin` the admin check. Permissions: `PermissionStore.HasPermissionAsync(tx, Me, "claims.payment.authorize")` inside the writing transaction, before anything is written.
- A form posts to its page (`OnPostXxxAsync`). On refusal: fill `Errors["field"]`, call `KeepPostedValues()`, return `Page()` — the form shows the values as typed. On success: redirect, as the old action did (`/policies/{id}?saved=1` …). Read posted fields with `Form("name")` (a repeated field is null, as legacy `singleParams`).
- Query parameters: `Query("name")`; unknown values are dropped (legacy `known()`).
- Dates in forms: `<date-input name="…" value="yyyy-mm-dd" min max required />` (dd/mm/yyyy typing, ISO posted). Dates in tables: `Fmt.Date(d)` (dd.mm.yyyy). Numbers as JavaScript showed them: `Fmt.Fixed(x, 2)` (toFixed), `Fmt.Plain(x)` (String(Number(x))), amounts with thousands separators: `Billing.GroupedTetri`.
- Texts: only from `Insurance.Application.Texts`. Do not write Georgian strings in pages.
- Small JavaScript only where the browser needs it (live totals, date input); the server checks everything again.
- CSS: `wwwroot/css/site.css` is the legacy `globals.css`; use the same class names as the old JSX.

## Tests and evidence

Keep the four kinds of evidence apart: source review, unit tests (`Insurance.Domain.Tests`), real database (`Insurance.Integration.Tests`, a fresh `ins_it_*` database per test class), browser/HTTP (`Insurance.Web.Tests`). A test that proves a fix must fail without it (negative control). A check that could not run is reported as SKIP or NOT TESTED with the reason; SKIP never counts as PASS.

Local test server: copy `.env.test.example` to `.env.test` (git-ignored) and point it at a local SQL Server.

### Failure injection (DbOptions.Interceptor)

The legacy offline tests proved two things on their in-memory stores that a real database cannot show by itself: an action's binding permission check refuses when the right is taken away between the quick check and the writing transaction, and a read that fails once comes out as that same error (never "no right", "nothing stored" or zeros), writes nothing, and the retry succeeds. The test seam for both is `DbOptions.Interceptor` (`src/Insurance.Application/Data/CommandInterceptor.cs`):

- An `ICommandInterceptor` is called before every command sent on a connection that a `Db` with it opened — `Tx`, `Db.ReadAsync`, Dapper on `tx.Connection`, the transaction's own `set xact_abort on` — with the SQL text, the transaction's isolation level (null outside a transaction; `Snapshot` for `Db.SnapshotAsync`, i.e. the quick permission check and the pages' permission reads; `ReadCommitted` for `Db.AsUserAsync`) and the parameters. It may throw (the command is never sent; the exception leaves the execute call unchanged, a transaction rolls back) or run an action first (e.g. revoke a grant on another connection) and let the command proceed.
- It is reached through SqlClient's own diagnostic event (`Microsoft.Data.SqlClient.WriteCommandBefore`), only for connections registered by a `Db` with an interceptor; the subscription is made the first time one is registered. Production never sets it (`Program.cs` builds `DbOptions` from the connection string only; `PageFaultTests.The_application_as_configured_has_no_interceptor`), so it costs one null check per connection.
- Tests use `tests/Insurance.Integration.Tests/FailureInjection/Faults.cs`: `FailOnce(match, error)`, `BeforeOnce(match, action)`, a log of every command, and matchers by a stable fragment of a statement's SQL (`Faults.Overrides`, `Memberships`, `Grants`; `Quick(...)` / `Binding(...)` by isolation level) rather than by call count. Web tests use `tests/Insurance.Web.Tests/FailureInjection/FaultSite.cs`, the application with `Faults` in its `DbOptions`; in Development an error that leaves a page is answered 500 with its message.
- The legacy case each test ports is listed in `docs/legacy-tests-gaps-a.md`.

### Browser tests

`tests/Insurance.Web.Tests/BrowserApp.cs` serves the real application with Kestrel on a free local port, on a fresh test database, and drives a headless Chromium with Playwright (`SignedInPageAsync(role)` signs in through the login form). The browser is the one installed for Playwright (`pwsh bin/…/playwright.ps1 install chromium`, done by CI); `PLAYWRIGHT_CHROMIUM_EXECUTABLE` points at another build (this container's `/opt/pw-browsers`).
