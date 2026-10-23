-- An authorization code of the embedded OpenID Connect provider (ADR 0003, INS-DOTNET-MSSQL-004 B3) is exchanged once
-- (RFC 6749 §4.1.2). The provider keeps no token store (OpenIddict's degraded mode): its codes are self-contained and live two
-- minutes, so the program writes down the SHA-256 of each code it exchanged and refuses the same code a second time. Nothing is
-- updated or deleted by the application; a code's row is worthless once its two minutes are over.
create table core.oidc_code_use (
  code_hash char(64) not null constraint oidc_code_use_pkey primary key,
  used_at   datetimeoffset(6) not null constraint oidc_code_use_used_at_df default sysdatetimeoffset()
);

grant select, insert on core.oidc_code_use to core_app;
