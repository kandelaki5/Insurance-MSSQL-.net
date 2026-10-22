-- AML/KYC: client risk assessments, beneficial owners, sanctions lists and screening, AML cases and FMS
-- suspensions (legacy 0014, 0016, 0017, 0019, 0021, 0022).

-- Client risk assessment history (FMS order №2/2020); a manual level needs a reason.
create table core.party_risk_assessment (
  id             uniqueidentifier not null constraint party_risk_assessment_pkey primary key constraint party_risk_assessment_id_df default newid(),
  party_id       uniqueidentifier not null constraint party_risk_assessment_party_id_fkey references core.party (id),
  assessed_on    date not null,
  computed_level nvarchar(10) not null constraint party_risk_assessment_computed_level_check check (computed_level in (N'low', N'medium', N'high')),
  level          nvarchar(10) not null constraint party_risk_assessment_level_check check (level in (N'low', N'medium', N'high')),
  score          int not null,
  factors        nvarchar(max) not null constraint party_risk_assessment_factors_df default N'[]'
                 constraint party_risk_assessment_factors_json check (isjson(factors) = 1),
  is_override    bit not null constraint party_risk_assessment_is_override_df default 0,
  reason         nvarchar(max) null,
  review_due_on  date not null,
  assessed_by    uniqueidentifier null constraint party_risk_assessment_assessed_by_fkey references core.app_user (id),
  created_at     datetimeoffset(6) not null constraint party_risk_assessment_created_at_df default sysdatetimeoffset(),
  constraint party_risk_override_reason check (is_override = 0 or reason is not null)
);
create index party_risk_assessment_party_idx on core.party_risk_assessment (party_id, created_at desc);

-- Beneficial owners of an organization client (FMS order №2/2020); a row ends with valid_to.
create table core.party_beneficial_owner (
  id                  uniqueidentifier not null constraint party_beneficial_owner_pkey primary key constraint party_beneficial_owner_id_df default newid(),
  party_id            uniqueidentifier not null constraint party_beneficial_owner_party_id_fkey references core.party (id),
  first_name          nvarchar(200) not null,
  last_name           nvarchar(200) not null,
  birth_date          date null,
  personal_number     nvarchar(50) null,
  document_number     nvarchar(100) null,
  document_issued_on  date null,
  document_country    nchar(2) null,
  document_issuer     nvarchar(400) null,
  document_valid_to   date null,
  citizenship         nchar(2) null,
  birth_place         nvarchar(400) null,
  criterion           nvarchar(20) not null constraint party_beneficial_owner_criterion_check check (criterion in (N'share', N'control', N'executive')),
  share_percent       decimal(5, 2) null constraint party_beneficial_owner_share_percent_check check (share_percent between 0 and 100),
  is_direct           bit not null constraint party_beneficial_owner_is_direct_df default 1,
  is_nominee          bit not null constraint party_beneficial_owner_is_nominee_df default 0,
  is_pep              bit not null constraint party_beneficial_owner_is_pep_df default 0,
  identified_at       date null,
  verified_at         date null,
  verification_source nvarchar(max) null,
  sanctions_checked_at date null,
  note                nvarchar(max) null,
  valid_to            date null,
  created_at          datetimeoffset(6) not null constraint party_beneficial_owner_created_at_df default sysdatetimeoffset(),
  created_by          uniqueidentifier null constraint party_beneficial_owner_created_by_fkey references core.app_user (id)
);
create index party_beneficial_owner_party_idx on core.party_beneficial_owner (party_id) where valid_to is null;

-- Consolidated sanctions lists (UN, ...); one row per listed subject. Array columns hold JSON arrays of strings.
create table core.sanction_list_entry (
  source        nvarchar(100) not null,
  data_id       nvarchar(200) not null,
  kind          nvarchar(20) not null constraint sanction_list_entry_kind_check check (kind in (N'individual', N'entity')),
  name          nvarchar(max) not null,
  aliases       nvarchar(max) not null constraint sanction_list_entry_aliases_df default N'[]'
                constraint sanction_list_entry_aliases_json check (isjson(aliases) = 1 and left(ltrim(aliases), 1) = N'['),
  list_type     nvarchar(200) null,
  reference     nvarchar(200) null,
  listed_on     date null,
  nationalities nvarchar(max) not null constraint sanction_list_entry_nationalities_df default N'[]'
                constraint sanction_list_entry_nationalities_json check (isjson(nationalities) = 1 and left(ltrim(nationalities), 1) = N'['),
  birth_years   nvarchar(max) not null constraint sanction_list_entry_birth_years_df default N'[]'
                constraint sanction_list_entry_birth_years_json check (isjson(birth_years) = 1 and left(ltrim(birth_years), 1) = N'['),
  documents     nvarchar(max) not null constraint sanction_list_entry_documents_df default N'[]'
                constraint sanction_list_entry_documents_json check (isjson(documents) = 1 and left(ltrim(documents), 1) = N'['),
  constraint sanction_list_entry_pkey primary key (source, data_id)
);

create table core.sanction_list_version (
  source       nvarchar(100) not null constraint sanction_list_version_pkey primary key,
  generated_at datetimeoffset(6) null,
  loaded_at    datetimeoffset(6) not null constraint sanction_list_version_loaded_at_df default sysdatetimeoffset(),
  entries      int not null
);

-- One screening of a client or a beneficial owner against the lists, with its decision.
create table core.sanctions_screening (
  id                uniqueidentifier not null constraint sanctions_screening_pkey primary key constraint sanctions_screening_id_df default newid(),
  subject_kind      nvarchar(10) not null constraint sanctions_screening_subject_kind_check check (subject_kind in (N'party', N'owner')),
  subject_id        uniqueidentifier not null,
  party_id          uniqueidentifier not null constraint sanctions_screening_party_id_fkey references core.party (id),
  screened_at       datetimeoffset(6) not null constraint sanctions_screening_screened_at_df default sysdatetimeoffset(),
  list_generated_at datetimeoffset(6) null,
  matches           nvarchar(max) not null constraint sanctions_screening_matches_df default N'[]'
                    constraint sanctions_screening_matches_json check (isjson(matches) = 1),
  outcome           nvarchar(20) not null constraint sanctions_screening_outcome_check check (outcome in (N'clear', N'possible', N'confirmed', N'false_positive')),
  decided_by        uniqueidentifier null constraint sanctions_screening_decided_by_fkey references core.app_user (id),
  decided_at        datetimeoffset(6) null,
  note              nvarchar(max) null,
  created_by        uniqueidentifier null constraint sanctions_screening_created_by_fkey references core.app_user (id)
);
create index sanctions_screening_party_idx on core.sanctions_screening (party_id, screened_at desc);
create index sanctions_screening_open_idx on core.sanctions_screening (outcome) where outcome in (N'possible', N'confirmed');

-- An AML case (suspicious activity), its examination and the suspicious transaction report to the FMS.
create table core.aml_case (
  id              uniqueidentifier not null constraint aml_case_pkey primary key constraint aml_case_id_df default newid(),
  number          nvarchar(100) not null constraint aml_case_number_key unique,
  party_id        uniqueidentifier not null constraint aml_case_party_id_fkey references core.party (id),
  opened_on       date not null,
  source          nvarchar(20) not null constraint aml_case_source_check check (source in (N'monitoring', N'employee', N'fms_request', N'other')),
  description     nvarchar(max) not null,
  transaction_ref nvarchar(400) null,
  class_code      nvarchar(20) null constraint aml_case_class_code_fkey references core.insurance_class (code),
  status          nvarchar(20) not null constraint aml_case_status_df default N'open'
                  constraint aml_case_status_check check (status in (N'open', N'examined', N'reported', N'closed')),
  examined_on     date null,
  examination     nvarchar(max) null,
  suspicion_at    datetimeoffset(6) null,
  str_sent_at     datetimeoffset(6) null,
  str_type        nvarchar(10) null constraint aml_case_str_type_check check (str_type in (N'ml', N'tf', N'other')),
  str_other_basis nvarchar(max) null,
  fms_reference   nvarchar(200) null,
  closed_on       date null,
  note            nvarchar(max) null,
  created_at      datetimeoffset(6) not null constraint aml_case_created_at_df default sysdatetimeoffset(),
  created_by      uniqueidentifier null constraint aml_case_created_by_fkey references core.app_user (id),
  constraint aml_case_other_basis check (str_type is null or str_type <> N'other' or str_other_basis is not null),
  constraint aml_case_reported check (status <> N'reported' or (str_sent_at is not null and str_type is not null and suspicion_at is not null))
);
create index aml_case_party_idx on core.aml_case (party_id);
create index aml_case_status_idx on core.aml_case (status);

-- An FMS instruction to suspend a client's operations: all of them, or one transaction (a policy or a
-- claim of the client). Rows saved before targets existed and matching nothing keep legacy_unmapped (0022).
create table core.fms_suspension (
  id                   uniqueidentifier not null constraint fms_suspension_pkey primary key constraint fms_suspension_id_df default newid(),
  party_id             uniqueidentifier not null constraint fms_suspension_party_id_fkey references core.party (id),
  case_id              uniqueidentifier null constraint fms_suspension_case_id_fkey references core.aml_case (id),
  kind                 nvarchar(10) not null constraint fms_suspension_kind_check check (kind in (N'oral', N'written')),
  scope                nvarchar(20) not null constraint fms_suspension_scope_df default N'all'
                       constraint fms_suspension_scope_check check (scope in (N'all', N'transaction')),
  transaction_ref      nvarchar(400) null,
  instructed_at        datetimeoffset(6) not null,
  written_confirmed_at datetimeoffset(6) null,
  ends_at              datetimeoffset(6) not null,
  lifted_at            datetimeoffset(6) null,
  lift_reason          nvarchar(max) null,
  note                 nvarchar(max) null,
  created_at           datetimeoffset(6) not null constraint fms_suspension_created_at_df default sysdatetimeoffset(),
  created_by           uniqueidentifier null constraint fms_suspension_created_by_fkey references core.app_user (id),
  target_policy_id     uniqueidentifier null constraint fms_suspension_target_policy_id_fkey references core.policy (id),
  target_claim_id      uniqueidentifier null constraint fms_suspension_target_claim_id_fkey references core.claim (id),
  legacy_unmapped      bit not null constraint fms_suspension_legacy_unmapped_df default 0,
  constraint fms_suspension_target check (scope = N'all' or target_policy_id is not null or target_claim_id is not null or legacy_unmapped = 1)
);
create index fms_suspension_party_idx on core.fms_suspension (party_id);
GO

-- The single policy or claim of a client that carries a number (case and surrounding spaces ignored);
-- nothing when the number is empty, unknown, another client's, or ambiguous (0022).
create function core.resolve_suspension_target(@party uniqueidentifier, @ref nvarchar(400))
returns table
as
return
  with wanted as (select upper(ltrim(rtrim(coalesce(@ref, N'')))) as w),
  matches as (
    select p.id as policy_id, convert(uniqueidentifier, null) as claim_id, p.number
    from core.policy p join core.contract c on c.id = p.contract_id cross join wanted
    where wanted.w <> N'' and upper(p.number) = wanted.w and c.policyholder_id = @party
    union all
    select null, cl.id, cl.number
    from core.claim cl join core.policy p on p.id = cl.policy_id join core.contract c on c.id = p.contract_id cross join wanted
    where wanted.w <> N'' and upper(cl.number) = wanted.w and c.policyholder_id = @party
  )
  select policy_id, claim_id, number from matches where (select count(*) from matches) = 1;
GO

create view core.fms_suspension_untargeted
as
select id, party_id, instructed_at, transaction_ref, lifted_at
from core.fms_suspension
where legacy_unmapped = 1;
GO

-- fms_suspension_target (0021, 0022), as INSTEAD OF triggers because it completes the row: only the
-- 0022 transition marks a row unmapped, afterwards the mark can only be cleared by attaching a target; a
-- one-transaction suspension written with only a number gets its target from the number; a target is
-- always the client's own.
create trigger core.fms_suspension_insert on core.fms_suspension
instead of insert
as
begin
  set nocount on;
  declare @rows table (
    id uniqueidentifier primary key, party_id uniqueidentifier, case_id uniqueidentifier, kind nvarchar(10), scope nvarchar(20),
    transaction_ref nvarchar(400), instructed_at datetimeoffset(6), written_confirmed_at datetimeoffset(6), ends_at datetimeoffset(6),
    lifted_at datetimeoffset(6), lift_reason nvarchar(max), note nvarchar(max), created_at datetimeoffset(6), created_by uniqueidentifier,
    target_policy_id uniqueidentifier, target_claim_id uniqueidentifier, legacy_unmapped bit);
  insert @rows select id, party_id, case_id, kind, scope, transaction_ref, instructed_at, written_confirmed_at, ends_at, lifted_at,
                      lift_reason, note, created_at, created_by, target_policy_id, target_claim_id, 0 from inserted;
  update r set target_policy_id = t.policy_id, target_claim_id = t.claim_id, transaction_ref = coalesce(t.number, r.transaction_ref)
  from @rows r cross apply core.resolve_suspension_target(r.party_id, r.transaction_ref) t
  where r.scope = N'transaction' and r.target_policy_id is null and r.target_claim_id is null and r.legacy_unmapped = 0;
  if exists (select 1 from @rows r where r.target_policy_id is not null and not exists (
               select 1 from core.policy p join core.contract c on c.id = p.contract_id where p.id = r.target_policy_id and c.policyholder_id = r.party_id))
    throw 51000, N'[AML01] suspension target policy does not belong to the client', 1;
  if exists (select 1 from @rows r where r.target_claim_id is not null and not exists (
               select 1 from core.claim cl join core.policy p on p.id = cl.policy_id join core.contract c on c.id = p.contract_id
               where cl.id = r.target_claim_id and c.policyholder_id = r.party_id))
    throw 51000, N'[AML01] suspension target claim does not belong to the client', 1;
  insert core.fms_suspension (id, party_id, case_id, kind, scope, transaction_ref, instructed_at, written_confirmed_at, ends_at, lifted_at,
                              lift_reason, note, created_at, created_by, target_policy_id, target_claim_id, legacy_unmapped)
  select id, party_id, case_id, kind, scope, transaction_ref, instructed_at, written_confirmed_at, ends_at, lifted_at,
         lift_reason, note, created_at, created_by, target_policy_id, target_claim_id, legacy_unmapped from @rows;
end;
GO

create trigger core.fms_suspension_update on core.fms_suspension
instead of update
as
begin
  set nocount on;
  if update(id)
    throw 51000, N'[23514] a suspension keeps its id', 1;
  declare @rows table (
    id uniqueidentifier primary key, party_id uniqueidentifier, case_id uniqueidentifier, kind nvarchar(10), scope nvarchar(20),
    transaction_ref nvarchar(400), instructed_at datetimeoffset(6), written_confirmed_at datetimeoffset(6), ends_at datetimeoffset(6),
    lifted_at datetimeoffset(6), lift_reason nvarchar(max), note nvarchar(max), created_at datetimeoffset(6), created_by uniqueidentifier,
    target_policy_id uniqueidentifier, target_claim_id uniqueidentifier, legacy_unmapped bit);
  insert @rows
  select i.id, i.party_id, i.case_id, i.kind, i.scope, i.transaction_ref, i.instructed_at, i.written_confirmed_at, i.ends_at, i.lifted_at,
         i.lift_reason, i.note, i.created_at, i.created_by, i.target_policy_id, i.target_claim_id,
         case when d.legacy_unmapped = 1 and i.target_policy_id is null and i.target_claim_id is null then 1 else 0 end
  from inserted i join deleted d on d.id = i.id;
  update r set target_policy_id = t.policy_id, target_claim_id = t.claim_id, transaction_ref = coalesce(t.number, r.transaction_ref)
  from @rows r cross apply core.resolve_suspension_target(r.party_id, r.transaction_ref) t
  where r.scope = N'transaction' and r.target_policy_id is null and r.target_claim_id is null and r.legacy_unmapped = 0;
  if exists (select 1 from @rows r where r.target_policy_id is not null and not exists (
               select 1 from core.policy p join core.contract c on c.id = p.contract_id where p.id = r.target_policy_id and c.policyholder_id = r.party_id))
    throw 51000, N'[AML01] suspension target policy does not belong to the client', 1;
  if exists (select 1 from @rows r where r.target_claim_id is not null and not exists (
               select 1 from core.claim cl join core.policy p on p.id = cl.policy_id join core.contract c on c.id = p.contract_id
               where cl.id = r.target_claim_id and c.policyholder_id = r.party_id))
    throw 51000, N'[AML01] suspension target claim does not belong to the client', 1;
  update s set party_id = r.party_id, case_id = r.case_id, kind = r.kind, scope = r.scope, transaction_ref = r.transaction_ref,
               instructed_at = r.instructed_at, written_confirmed_at = r.written_confirmed_at, ends_at = r.ends_at, lifted_at = r.lifted_at,
               lift_reason = r.lift_reason, note = r.note, created_at = r.created_at, created_by = r.created_by,
               target_policy_id = r.target_policy_id, target_claim_id = r.target_claim_id, legacy_unmapped = r.legacy_unmapped
  from core.fms_suspension s join @rows r on r.id = s.id;
end;
GO
