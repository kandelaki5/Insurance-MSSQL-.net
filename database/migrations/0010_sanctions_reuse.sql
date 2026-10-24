-- Reusing a justified false-positive decision on an unchanged re-screening (owner, 23.10.2026).
--
-- A screening row now keeps three fingerprints (Insurance.Domain.Compliance.ScreeningFingerprint): the subject's
-- screening data, the set of matches, and the list's whole content. A new screening with hits whose three
-- fingerprints, subject and list date equal those of an earlier false positive that an authorised user dismissed
-- with a reason is recorded as a false positive that points to that decision (reuses_screening_id) instead of
-- waiting for a new review. Who decided, when and why stay on the decision itself and are read from there, never
-- copied. Rows written before this migration have no fingerprints and are never reused.
--
-- decision records which decision closed a row ('dismiss', 'confirm', 'lift'): only a dismissed hit is reused, not
-- a confirmed one whose block was lifted later.

alter table core.sanction_list_version add content_sha256 char(64) null;

alter table core.sanctions_screening add
  subject_sha256      char(64) null,
  matches_sha256      char(64) null,
  list_sha256         char(64) null,
  decision            nvarchar(10) null constraint sanctions_screening_decision_check check (decision in (N'dismiss', N'confirm', N'lift')),
  reuses_screening_id uniqueidentifier null constraint sanctions_screening_reuses_fkey references core.sanctions_screening (id);
GO

alter table core.sanctions_screening add constraint sanctions_screening_reuse_check check (
  reuses_screening_id is null
  or (outcome = N'false_positive' and decided_by is null and decided_at is null and note is null and decision is null
      and subject_sha256 is not null and matches_sha256 is not null and list_sha256 is not null));

create index sanctions_screening_reuse_idx on core.sanctions_screening (subject_id, subject_sha256, list_sha256, matches_sha256)
  where outcome in (N'false_positive', N'confirmed') and reuses_screening_id is null;
GO

-- [SS001] a reuse must point to a dismissed, reasoned decision on the same subject with the same fingerprints and
-- list date, itself not a reuse; [SS002] a reuse is never changed afterwards.
create trigger core.sanctions_screening_reuse_guard on core.sanctions_screening
after insert, update
as
begin
  set nocount on;
  if update(reuses_screening_id) and exists (
       select 1 from inserted i join deleted d on d.id = i.id
       where (d.reuses_screening_id is not null or i.reuses_screening_id is not null)
         and (d.reuses_screening_id is null or i.reuses_screening_id is null or d.reuses_screening_id <> i.reuses_screening_id))
    throw 51000, N'[SS002] a reused decision cannot be changed', 1;
  if exists (select 1 from inserted i join deleted d on d.id = i.id where d.reuses_screening_id is not null
             and (i.outcome <> d.outcome or i.decided_by is not null or i.note is not null or i.decision is not null))
    throw 51000, N'[SS002] a reused decision cannot be changed', 1;
  if exists (
       select 1 from inserted i
       where i.reuses_screening_id is not null
         and not exists (
           select 1 from core.sanctions_screening o
           where o.id = i.reuses_screening_id
             and o.reuses_screening_id is null
             and o.subject_kind = i.subject_kind and o.subject_id = i.subject_id
             and o.subject_sha256 = i.subject_sha256 and o.matches_sha256 = i.matches_sha256 and o.list_sha256 = i.list_sha256
             and ((o.list_generated_at is null and i.list_generated_at is null) or o.list_generated_at = i.list_generated_at)
             and o.outcome = N'false_positive' and o.decision = N'dismiss'
             and o.decided_by is not null and o.decided_at is not null
             and len(ltrim(rtrim(coalesce(o.note, N'')))) > 0))
    throw 51000, N'[SS001] a reused decision must be a dismissed, reasoned false positive of the same subject, data, matches and list', 1;
end;
GO
