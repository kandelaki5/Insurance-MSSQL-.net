-- Database roles and rights (legacy db/README.md, 0002, 0003, 0008, 0011, 0031 and later revokes).
--
-- core_app:    the application. It reads and writes core tables but cannot change the schema; ledgers are
--              append-only for it (no update/delete); the migration journal is read-only. The login and user
--              that the application connects with are created per installation (they carry a password, so
--              they are not part of the migrations) and made members of this role: see database/README.md.
-- core_reader: read-only access for reviewers, every table except users and sessions.
-- The migration runner re-asserts the ledger and journal limits after every run.

create role core_app;
create role core_reader;
GO

-- Read, add, change and remove.
grant select, insert, update, delete on core.aml_case to core_app;
grant select, insert, update, delete on core.app_user to core_app;
grant select, insert, update, delete on core.branch to core_app;
grant select, insert, update, delete on core.claim to core_app;
grant select, insert, update, delete on core.claim_cession to core_app;
grant select, insert, update, delete on core.complaint to core_app;
grant select, insert, update, delete on core.contract to core_app;
grant select, insert, update, delete on core.currency to core_app;
grant select, insert, update, delete on core.exchange_rate to core_app;
grant select, insert, update, delete on core.fms_suspension to core_app;
grant select, insert, update, delete on core.insurance_class to core_app;
grant select, insert, update, delete on core.insured_object to core_app;
grant select, insert, update, delete on core.intermediary to core_app;
grant select, insert, update, delete on core.login_attempt to core_app;
grant select, insert, update, delete on core.loss_event to core_app;
grant select, insert, update, delete on core.number_sequence to core_app;
grant select, insert, update, delete on core.party to core_app;
grant select, insert, update, delete on core.party_beneficial_owner to core_app;
grant select, insert, update, delete on core.policy to core_app;
grant select, insert, update, delete on core.policy_intermediary to core_app;
grant select, insert, update, delete on core.policy_line to core_app;
grant select, insert, update, delete on core.policy_party to core_app;
grant select, insert, update, delete on core.product to core_app;
grant select, insert, update, delete on core.product_coverage to core_app;
grant select, insert, update, delete on core.product_text to core_app;
grant select, insert, update, delete on core.ri_treaty to core_app;
grant select, insert, update, delete on core.ri_treaty_class to core_app;
grant select, insert, update, delete on core.role to core_app;
grant select, insert, update, delete on core.sanction_list_entry to core_app;
grant select, insert, update, delete on core.sanction_list_version to core_app;
grant select, insert, update, delete on core.sanctions_screening to core_app;
grant select, insert, update, delete on core.session to core_app;
grant select, insert, update, delete on core.setting to core_app;
grant select, insert, update, delete on core.user_group to core_app;
grant select, insert, update, delete on core.user_group_member to core_app;
grant select, insert, update, delete on core.user_group_permission to core_app;
grant select, insert, update, delete on core.user_permission to core_app;

-- A month moves from open to closed, never back and never removed (0008, 0025); a regress case and its
-- parties change status and responses but are never removed (0030).
grant select, insert, update on core.accounting_period to core_app;
grant select, insert, update on core.subrogation_case to core_app;
grant select, insert, update on core.subrogation_party to core_app;

-- Ledgers and requests: append-only.
grant select, insert on core.audit_log to core_app;
grant select, insert on core.claim_payment to core_app;
grant select, insert on core.claim_payment_request to core_app;
grant select, insert on core.claim_recovery to core_app;
grant select, insert on core.claim_reserve to core_app;
grant select, insert on core.claim_reserve_request to core_app;
grant select, insert on core.installment to core_app;
grant select, insert on core.installment_adjustment to core_app;
grant select, insert on core.party_risk_assessment to core_app;
grant select, insert on core.payment to core_app;
grant select, insert on core.payment_allocation to core_app;
grant select, insert on core.premium_cession to core_app;
grant select, insert on core.premium_refund_request to core_app;
grant select, insert on core.premium_refund_request_line to core_app;
grant select, insert on core.premium_transaction to core_app;
grant select, insert on core.receipt_reversal_request to core_app;
grant select, insert on core.receipt_reversal_request_line to core_app;
grant select, insert on core.subrogation_movement to core_app;

-- Read only.
grant select on core.audit_config to core_app;
grant select on core.schedule_reconciliation to core_app;
grant select on core.fms_suspension_untargeted to core_app;
grant select on core.case_recovery_without_movement to core_app;

-- Procedures and functions the application calls.
grant execute on core.next_number to core_app;
grant execute on core.xact_lock to core_app;
grant execute on core.lock_subrogation_claim to core_app;
grant execute on core.assert_period_open to core_app;
grant execute on core.record_case_money to core_app;
grant execute on core.current_actor to core_app;
grant select on core.subrogation_totals to core_app;
grant select on core.subrogation_room to core_app;
grant select on core.resolve_suspension_target to core_app;
GO

-- Reviewers: every table and view of core, except users and sessions.
declare @sql nvarchar(max) = (
  select string_agg(convert(nvarchar(max), N'grant select on core.' + quotename(o.name) + N' to core_reader;'), char(10))
  from sys.objects o
  where o.schema_id = schema_id(N'core') and o.type in (N'U', N'V') and o.name not in (N'app_user', N'session'));
exec sys.sp_executesql @sql;
GO
