# ADR 0003 — OpenID Connect sign-in through a same-origin BFF; OAuth 2.0 for the API

Status: proposed (INS-DOTNET-MSSQL-004 B3), 24.10.2026. ქართულად: შესვლა OIDC-ით; ბრაუზერი ტოკენს არ ინახავს — სერვერი ინახავს, ბრაუზერს მხოლოდ დაცული cookie აქვს.

## Context
Today: the program's own sign-in (scrypt hashes in `core.app_user`, a server session row, a Secure/HttpOnly cookie, antiforgery, throttling, disable/logout/expiry checked on every request). Hosting: one IIS site on SmarterASP.NET (one domain, HTTPS, no extra paid service assumed). The browser runs the Angular shell on the same origin.

## Options
1. **BFF, same origin** — the server is the OIDC client (confidential, secret on the server), keeps the tokens server-side, gives the browser only its existing kind of cookie; the Angular app calls `/api/v1` with the cookie and an antiforgery header.
2. **SPA with Authorization Code + PKCE** — the browser holds access/refresh tokens; needs CORS only if API and SPA are on different origins; token storage and refresh rotation in the browser.

## Decision
- **BFF, same origin**, for the browser. Code flow with PKCE between the BFF and the identity provider; state, nonce and correlation checked by the ASP.NET Core OIDC handler; Implicit and Resource Owner Password grants not used. No token reaches the browser.
- **Identity provider: OpenIddict (Apache-2.0), embedded in the same site**, over the program's own users: the existing scrypt hashes keep working (a damaged hash is still refused), nobody's password is reset, the disabled flag, throttling and audit stay. Its stores are Dapper implementations in Infrastructure (no EF Core), its signing/encryption keys come from configuration or certificate files outside Git; a development certificate only in Development/test. An external provider (Entra ID, Auth0, Keycloak) can replace it later behind the same OIDC handler — one issuer + subject is bound to one `app_user` (a new table `core.app_user_login`, never merged by e-mail).
- **API access:** the same API accepts the BFF's cookie (browser) or an OAuth 2.0 bearer access token (Swagger's Authorize, tools), validated for issuer, audience `insurance-api`, signature, algorithm and lifetime.
- **Swagger UI** in Development/UAT only: a public client `insurance-swagger` with Authorization Code + PKCE and its own callback; no secret in Swagger; disabled in Production unless protected.
- **Authorization:** roles, groups and overrides stay in the database (`PermissionStore`); a write checks the current permission inside its transaction. Claims are only identity (subject); they are never the source of permissions. Angular guards are convenience only.
- Cookies: Secure, HttpOnly, SameSite=Lax (the OIDC callback is a top-level GET), antiforgery on every cookie-authenticated write; CORS off in production (same origin), exact origins only in dev.

## Consequences
- The first slice (B5) delivers: OpenIddict in Development with a real login page, the BFF's login/logout/callback, revocation (disabling a user or signing out ends the BFF session at once — the session row is still checked per request), and Swagger Authorize with PKCE.
- SmarterASP.NET: one site keeps the IdP, the BFF and the API; Data Protection keys must be persisted (the database or `App_Data`) so cookies survive an app-pool recycle — to be configured before the first deploy of this stage.
