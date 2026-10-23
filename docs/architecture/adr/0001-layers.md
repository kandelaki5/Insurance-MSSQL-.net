# ADR 0001 — One program in layers: Domain, Application, Infrastructure, Api (+ the Razor host while it lasts)

Status: proposed (INS-DOTNET-MSSQL-004 B1), 24.10.2026. ქართულად: პროგრამა რჩება ერთად (არა microservices), ოღონდ ფენებად; SQL და HTTP ბიზნესწესებს არ ერევა.

## Context
Today: `Insurance.Domain` (pure rules), `Insurance.Application` (use cases **and** Dapper/SqlClient/Excel/HTTP in the same project), `Insurance.Web` (Razor Pages, some page models run SQL themselves; three JSON handlers for Angular), `Insurance.Tools` (CLI). The owner's design (Angular, `frontend/`) runs inside the Razor host. The target is a .NET 10 Web API with targeted repository/query interfaces and the existing Angular frontend, without changing a business rule, a figure or a guarantee of the database.

## Decision
- `Insurance.Domain` — unchanged role: pure financial and compliance rules, value objects. No reference to HTTP, SQL or frameworks.
- `Insurance.Application` — use cases, request/response DTOs, and the **ports** they need: one repository per aggregate where a use case writes (`IPolicyRepository`, `IClaimRepository`, …), targeted read models for screens and reports (`IPolicyListQuery`, `IPremiumReportQuery`, …), `IUnitOfWork` (see ADR 0002), `IClock`, `IRates`, `IPermissionGuard`, `IWorkbookReader/Writer` where Excel is needed. It keeps no Dapper/SqlClient/HttpClient in the end state.
- `Insurance.Infrastructure` (new) — Dapper + SqlClient implementations of those ports, `Db`/`Tx`, the migration runner, the National Bank client, Excel I/O, the OIDC server's stores and keys (ADR 0003). Moved out of Application step by step, module by module.
- `Insurance.Api` (new) — minimal-API endpoints under `/api/v1`, DTO mapping and validation, ProblemDetails, OpenAPI, authentication/authorization. No SQL, no financial formula.
- `Insurance.Web` — the current Razor host, kept as the composition root and the place the Angular build is served from (same origin), until each route has an accepted API + Angular replacement (map: `docs/architecture/route-map.md`).
- `Insurance.Tools` — keeps every command; resolves the layers through DI.

References: `Api → Application → Domain`; `Infrastructure → Application, Domain`; `Web → Api, Application, Infrastructure` (composition only); no cycles. An architecture test (reflection over the assemblies' references) fails the build when a forbidden reference appears; SQL left in a not-yet-migrated page is listed in the route map, not hidden.

Not done for the sake of the name: no microservices, message broker, event bus, CQRS framework, mediator library or generic `IRepository<T>`.

## Consequences
- The Api runs in the same process and site as the Razor host (one IIS site on SmarterASP.NET, one origin) — no CORS in production, one cookie, one deployment.
- Moving code is done per module with its tests; a module's API and screen replace its Razor route only when the route map marks them accepted.
- Rollback of a step: revert its commit; no migration is involved in a layering step (schema changes stay new numbered migrations).
