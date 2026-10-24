-- A claim's occurrence-day snapshot compared NULL-safely (Codex R005-03, 24.10.2026). The guard of 0012 compared each field with
-- "not ((i.x is null and d.x is null) or i.x = d.x)", which is UNKNOWN — so not caught — when one side is NULL and the other is not:
-- the application role could wipe a GEL claim's snapshot with its currency kept (all four fields to NULL satisfy the snapshot's
-- check through its all-null branch), null one field of it, or give a snapshot to a claim that had none. 0012 is applied and stays as
-- it is; this replaces the trigger. EXCEPT compares rows treating two NULLs as equal and a NULL and a value as different.

-- [CL001] a claim's currency and its occurrence day's snapshot are fixed when it is registered.
alter trigger core.claim_currency_fixed on core.claim
after update
as
begin
  set nocount on;
  if not (update(currency) or update(limit_gel) or update(deductible_gel) or update(limit_rate) or update(limit_rate_on) or update(occurred_on)) return;
  if exists (select 1 from inserted i join deleted d on d.id = i.id
             where exists (select i.currency, i.limit_gel, i.deductible_gel, i.limit_rate, i.limit_rate_on
                           except
                           select d.currency, d.limit_gel, d.deductible_gel, d.limit_rate, d.limit_rate_on)
                or (d.limit_rate is not null and i.occurred_on <> d.occurred_on))
    throw 51000, N'[CL001] a claim keeps its currency and its occurrence day''s rate', 1;
end;
GO
