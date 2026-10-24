# ADR 0005 — Hosting and deployment stay on the current site; each step can be rolled back

Status: proposed (INS-DOTNET-MSSQL-004), 24.10.2026. ქართულად: იგივე ჰოსტინგი; ახალი ფასიანი სერვისი არ გვჭირდება; ყოველ ნაბიჯს უკან დაბრუნების გზა აქვს.

- One ASP.NET Core site on SmarterASP.NET (IIS, in-process): Razor host + Api + the embedded IdP + the Angular build in `wwwroot/ui`. No new paid service is required by this architecture.
- Configuration and secrets (connection strings, IdP signing/encryption certificates, client secrets) only from the host's configuration/environment or user-secrets in development — never in Git. Data Protection keys persisted (database or `App_Data`).
- A deploy = build (Angular `npm ci` + production build, `dotnet publish`), back up the site's DLLs and the database, apply new numbered migrations with the admin login, copy the files. Code rollback = the previous DLLs; schema rollback is not a rewritten history — a new migration if ever needed.
- Health: `/health` (database reachable, migrations at the expected number) for the host's monitor; structured logs without personal data or secrets.
- Shared databases (the demo, the owner's) are changed only with the owner's separate approval.
