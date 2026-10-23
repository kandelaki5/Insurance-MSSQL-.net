# ADR 0004 — API contracts: /api/v1, exact money, dates, ProblemDetails

Status: proposed (INS-DOTNET-MSSQL-004 B4), 24.10.2026. ქართულად: თანხა JSON-ში ზუსტად — თეთრები სტრიქონად; შეცდომა — სტანდარტული ფორმით, ქართული ტექსტით.

- Endpoints `/api/v1/{module}/…`, named request/response DTOs; no database row is returned as is.
- **Money:** `{ "minor": "81045", "currency": "GEL", "scale": 2 }` — `minor` a string (a long or BigInteger never becomes a JS number). Rates and percents as decimal strings with their stored precision ("2.701500"). Angular parses and formats them exactly; no financial formula runs in the browser.
- **Dates:** DateOnly as `YYYY-MM-DD`; instants as UTC ISO 8601; business days by the Tbilisi contract (`docs/RUNTIME.md`). Each date says what it is: `effectiveFrom`, `bookedOn`, `paidOn`, `asOf`, `period`.
- **Lists:** server-side paging (`page`, `pageSize`, `total`), filter, search and sort; totals are of the whole filtered set, not of the page.
- **Errors:** `application/problem+json` with a stable `code` (the program's outcome codes: `period-closed`, `exceeds`, `not-allowed`, …), the Georgian text, and field errors for forms; 400 invalid, 401 not signed in, 403 not allowed, 404 missing, 409 state/concurrency (already, stale), 422 a business refusal. A refusal is never a 500.
- **Writes:** keep the existing duplicate-click keys, locks, outcomes and atomicity; the transport adds no money behaviour.
- **OpenAPI:** every endpoint documented — summary, schemas with money/date examples, the permission it needs, its error codes; an optional generated Angular client only if it keeps the money contract exact.
