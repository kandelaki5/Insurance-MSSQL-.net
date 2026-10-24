-- SG034 without a key the application can set (Codex R02).
--
-- 0003 let core.record_case_money name the recovery it writes in SESSION_CONTEXT(N'core.case_recovery'), and
-- the recovery guard accepted money of a regress case only while that key named the row. sp_set_session_context
-- is open to every session, so the application role could set the key itself and leave a recovery without its
-- movement. Now the procedure writes a one-row pass to core.case_money_pass, a table core_app has no right to
-- at all, and the guard uses it up; the procedure, the guard and the table belong to the schema's owner, so
-- ownership chaining lets the two modules reach the table that a direct statement of core_app cannot. A pass is
-- bound to the session that wrote it and lives only inside the procedure's transaction: another session never
-- sees it (row versioning shows only committed rows, and none is ever committed), and a refused movement rolls
-- the pass, the recovery and everything else back together.

create table core.case_money_pass (
  recovery_id uniqueidentifier not null constraint case_money_pass_pkey primary key,
  session_id smallint not null constraint case_money_pass_session_default default (@@spid)
);
GO

-- Explicitly none, whatever a later grant on the schema gives: only the two modules above reach this table.
deny select, insert, update, delete on core.case_money_pass to core_app;
GO

alter trigger core.claim_recovery_guard on core.claim_recovery
instead of insert
as
begin
  set nocount on;
  declare @id uniqueidentifier, @claim uniqueidentifier, @kind nvarchar(20), @received_on date, @amount_gel decimal(18, 2),
          @case uniqueidentifier, @row_kind nvarchar(20), @reverses uniqueidentifier, @case_claim uniqueidentifier, @m nvarchar(400);
  declare @o_id uniqueidentifier, @o_row_kind nvarchar(20), @o_claim uniqueidentifier, @o_kind nvarchar(20), @o_case uniqueidentifier,
          @o_gel decimal(18, 2), @o_received date;
  declare rows cursor local fast_forward for
    select id, claim_id, kind, received_on, amount_gel, subrogation_case_id, row_kind, reverses_claim_recovery_id from inserted;
  open rows;
  fetch next from rows into @id, @claim, @kind, @received_on, @amount_gel, @case, @row_kind, @reverses;
  while @@fetch_status = 0
  begin
    if @kind = N'subrogation'
      exec core.lock_subrogation_claim @claim;
    if @case is not null
    begin
      set @case_claim = (select claim_id from core.subrogation_case where id = @case);
      if @case_claim is null or @case_claim <> @claim
      begin
        set @m = concat(N'[SG031] recovery on claim ', lower(convert(nchar(36), @claim)), N' cannot go to regress case ',
                        lower(convert(nchar(36), @case)), N' of another claim');
        exec core.fail @m;
      end
    end
    else if @kind = N'subrogation' and @row_kind = N'receipt' and exists (select 1 from core.subrogation_case where claim_id = @claim)
    begin
      set @m = concat(N'[SG032] claim ', lower(convert(nchar(36), @claim)), N' has a regress case: its regress money goes through the case');
      exec core.fail @m;
    end
    if @row_kind = N'reversal'
    begin
      select @o_id = null;
      select @o_id = id, @o_row_kind = row_kind, @o_claim = claim_id, @o_kind = kind, @o_case = subrogation_case_id,
             @o_gel = amount_gel, @o_received = received_on
      from core.claim_recovery where id = @reverses;
      if @o_id is null or @o_row_kind <> N'receipt' or @o_claim <> @claim or @o_kind <> @kind
         or not ((@o_case is null and @case is null) or @o_case = @case)
         or @amount_gel <> -@o_gel or @received_on < @o_received
        throw 51000, N'[SG033] recovery reversal must mirror one receipt of the same claim and case, on or after its day', 1;
    end
    exec core.assert_period_open @received_on;
    if @case is not null
    begin
      -- The pass core.record_case_money wrote for this row in this session's transaction, used up here.
      delete core.case_money_pass where recovery_id = @id and session_id = @@spid;
      if @@rowcount = 0
      begin
        set @m = concat(N'[SG034] recovery ', lower(convert(nchar(36), @id)), N' of regress case ', lower(convert(nchar(36), @case)), N' has no movement');
        exec core.fail @m;
      end
    end
    insert core.claim_recovery (id, claim_id, kind, received_on, amount_gel, note, created_at, created_by, subrogation_case_id, row_kind,
                                reverses_claim_recovery_id, bank_reference)
    select id, claim_id, kind, received_on, amount_gel, note, created_at, created_by, subrogation_case_id, row_kind,
           reverses_claim_recovery_id, bank_reference
    from inserted where id = @id;
    fetch next from rows into @id, @claim, @kind, @received_on, @amount_gel, @case, @row_kind, @reverses;
  end
  close rows;
  deallocate rows;
end;
GO

alter procedure core.record_case_money
  @recovery_id uniqueidentifier, @claim_id uniqueidentifier, @case_id uniqueidentifier, @row_kind nvarchar(20),
  @reverses_recovery_id uniqueidentifier, @received_on date, @amount_gel decimal(18, 2), @bank_reference nvarchar(100),
  @recovery_note nvarchar(max), @created_by uniqueidentifier,
  @movement_id uniqueidentifier, @movement_kind nvarchar(20), @reverses_movement_id uniqueidentifier, @payer_id uniqueidentifier,
  @document_kind nvarchar(30), @document_number nvarchar(max), @document_date date, @movement_note nvarchar(max), @request_key uniqueidentifier
as
begin
  set nocount on;
  set xact_abort on;
  begin transaction;
  insert core.case_money_pass (recovery_id) values (@recovery_id);
  insert core.claim_recovery (id, claim_id, kind, subrogation_case_id, row_kind, reverses_claim_recovery_id, received_on, amount_gel, bank_reference, note, created_by)
  values (@recovery_id, @claim_id, N'subrogation', @case_id, @row_kind, @reverses_recovery_id, @received_on, @amount_gel, @bank_reference, @recovery_note, @created_by);
  insert core.subrogation_movement (id, case_id, kind, booked_on, amount_gel, reverses_movement_id, claim_recovery_id, payer_id,
                                    document_kind, document_number, document_date, note, request_key)
  values (@movement_id, @case_id, @movement_kind, @received_on, @amount_gel, @reverses_movement_id, @recovery_id, @payer_id,
          @document_kind, @document_number, @document_date, @movement_note, @request_key);
  commit transaction;
end;
GO
