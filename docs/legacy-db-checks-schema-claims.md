# Legacy real-database checks: schema, rights, numbering, claims and regress

The legacy `npm test` never touched a real database; the `db-check` scripts below were its only real-database evidence (several
were "PREPARED, NOT RUN"). This table maps every numbered case of the scripts of this group (legacy commit `17c1f1f`) to the
xUnit test that now proves it on a real SQL Server (`tests/Insurance.Integration.Tests`, a fresh `ins_it_*` database per test
class, the application connecting as a member of `core_app` only), or says why it does not apply.

Verdicts: **PASS** — a test runs it on real SQL Server and passes; **N/A** — the case checks something that does not exist in
the new program (a PostgreSQL upgrade path from a schema older than the final one, a PostgreSQL-only feature, or the legacy test
runner's own infrastructure); **NOT TESTED** — it would need a fixture this group may not build (a trigger bypassed) or a rule the
owner has not decided; **OTHER GROUP** — the case drives an action of another area (policies, receipts, AML, complaints), whose
port is not this group's; it is neither PASS nor N/A. NOT TESTED and OTHER GROUP never count as PASS.

Test classes (all in `tests/Insurance.Integration.Tests`): `LegacyDbCheckNumberingTests` (Num), `LegacyDbCheckSchemaTests` (Sch),
`LegacyDbCheckClaimTests` (Clm), `LegacyDbCheckRegressTests` (Reg), and the earlier `ConcurrencyTests`, `SchemaTests`,
`ClaimReserveTests`, `ClaimRegistrationTests`, `ClaimRecoveryTests`, `ClaimTotalLossTests`, `SubrogationCaseTests`,
`SubrogationReportStoreTests`.

## scripts/number-concurrency-regression.ts

| Case | Test | Verdict |
|---|---|---|
| SQL: 20 requests at once over 8 connections, one series: 1–20, next 21 | Num.Twenty_requests_at_once_over_eight_connections_get_1_to_20_and_three_more_rounds_reach_80_without_a_gap | PASS |
| control: read-then-write numbering without a lock gives duplicates | Num.Negative_control_read_then_write_numbering_without_a_lock_gives_duplicates_under_the_same_load | PASS |
| SQL: three more rounds, 80 distinct, no gap | Num.Twenty_requests_at_once_over_eight_connections_… | PASS |
| SQL: two series at once, 10 each | Num.Two_series_at_the_same_time_each_get_their_own_1_to_10 | PASS |
| SQL: a rolled-back number is given again once; 5 at once 1–5 | Num.A_number_taken_in_a_rolled_back_transaction_is_given_again_once_and_five_requests_at_once_get_1_to_5 | PASS |
| SQL: 2-digit series after 99: NU001, counter 100 | Num.A_two_digit_series_after_99_numbers_refuses_the_100th_with_NU001_and_never_gives_an_earlier_number | PASS |
| NOTE: a series that does not exist gives null | same test | PASS |
| action: issuePolicy 20 at once, own policy and contract numbers | policies group (issuePolicy) | OTHER GROUP |
| action: issuePolicy refuses a sanctions-blocked client (AG-P6) | policies group | OTHER GROUP |

## scripts/number-limit-check.ts

| Case | Test | Verdict |
|---|---|---|
| SQL: 2-digit series, 100th and 101st refused, counter 100 | Num.The_100th_and_101st_request_of_a_two_digit_series_are_both_refused_and_the_counter_stays_at_100 | PASS |
| SQL: 6-digit at 999999 → NS999999, then NU001, counter 1000000 | Num.A_six_digit_series_at_999999_gives_NS999999_then_refuses_and_the_counter_stays_at_1000000 | PASS |
| SQL: 20 at once 9 before the limit: 91–99 once each, 11 × NU001, counter 100 | Num.Twenty_requests_at_once_nine_numbers_before_the_limit_give_91_to_99_once_each_and_refuse_the_other_eleven | PASS |
| SQL: a series that does not exist → null | Num.A_two_digit_series_after_99_numbers_… | PASS |
| (new) the refusal leaves the counter also when called outside a transaction — **defect fixed**, see below | Num.An_exhausted_series_called_outside_a_transaction_also_leaves_the_counter_where_it_was | PASS |
| action: issuePolicy with the contract series at 999999 | policies group | OTHER GROUP |
| action: confirmImport with the policy series run out; first gets the last number, second refused | policies group (import) | OTHER GROUP |
| action: endorsePolicy with the policy series run out | policies group | OTHER GROUP |
| action: registerClaim (one coverage) with the claim series run out | Num.RegisterClaim_with_the_claim_series_run_out_on_one_or_several_coverages_writes_nothing_and_moves_no_counter (+ ClaimRegistrationTests.A_number_series_that_ran_out_refuses_with_the_message_and_writes_nothing) | PASS |
| action: registerClaim (several coverages in one event) with the claim series run out | Num.RegisterClaim_with_the_claim_series_run_out_… | PASS |
| action: registerComplaint / registerCase (AML) / createTreaty with their series run out | complaints, AML and reinsurance-treaty groups (actions not in this group) | OTHER GROUP |

## scripts/verify-0025.ts

| Case | Test | Verdict |
|---|---|---|
| migration 0025 applied; the three triggers; app connection is core_app | Sch.V0025_the_three_triggers_of_settings_and_periods_are_there_and_enabled (the journal itself: SchemaTests.Clean_build_applies_every_migration_and_records_it) | PASS |
| closed history: reopen, delete, closing time, author, date → 23514 even for the owner; no-op passes; app delete → 42501 | Sch.V0025_a_closed_month_cannot_be_reopened_removed_or_have_its_time_author_or_date_changed_even_by_the_database_owner | PASS |
| a setting change audited with author, old and new value; unchanged save writes nothing | Sch.V0025_a_setting_change_is_audited_with_its_author_old_and_new_value_and_an_unchanged_save_writes_nothing | PASS |
| rights: accounting_period I+U no D; audit_log I only; setting I+U+D | Sch.V0025_the_application_may_add_and_close_months_not_remove_them_only_add_audit_rows_and_change_settings | PASS |
| nothing left behind | the closed-month test counts closed months before and after (probes rolled back) | PASS |

## scripts/verify-0026-0028.ts

| Case | Test | Verdict |
|---|---|---|
| migrations 0026–0028 recorded | the new schema is built at the final state; SchemaTests.Clean_build_applies_every_migration_and_records_it | N/A |
| 0026: V99 then NU001, counter 100; every real series fits | Num.Every_real_series_fits_its_digits_V99_then_NU001_with_the_counter_at_100 | PASS |
| 0027: counter move not audited; prefix change audited V → W | Num.Moving_a_series_counter_is_not_audited_and_changing_its_prefix_is_with_the_old_and_new_value | PASS |
| 0028: the existing claims became one event each (backfill) | upgrade path from a pre-0028 schema; here `claim.loss_event_id` is not null from the start (asserted in Sch.V0028_the_unique_coverage_rule…) | N/A |
| 0028: UNIQUE (loss_event_id, policy_line_id); claim_event_policy trigger enabled | Sch.V0028_the_unique_coverage_rule_and_the_policy_guard_of_loss_events_are_in_place | PASS |
| 0028: a claim moved to another policy's event → LE001 | Sch.V0028_a_claim_moved_to_another_policys_event_is_LE001_and_onto_a_coverage_its_event_has_is_23505 (insert: ClaimRegistrationTests.A_claim_on_another_policys_event_…) | PASS |
| 0028: one coverage twice in an event → 23505 | same | PASS |
| core_app may select/insert loss events, no update/delete on any ledger | Sch.V0028_core_app_may_read_and_add_loss_events_and_still_may_not_change_any_ledger | PASS |

## scripts/db-acceptance.ts

| Case | Test | Verdict |
|---|---|---|
| H04 negative control / H04 with the policy lock (two receipts on one policy) | receipts group (recordPayment); the database guard of one receipt: ConcurrencyTests.Two_allocations_of_one_receipt_never_exceed_it | OTHER GROUP |
| H06: close first, a receipt in the month waits and is refused 23514 | ConcurrencyTests.Movement_waits_for_a_month_close_in_flight_and_is_then_refused | PASS |
| H06: receipt in flight, the close waits; a later receipt refused | ConcurrencyTests.Month_close_waits_for_a_movement_in_flight_and_then_closes; SchemaTests.Movement_in_a_closed_month_is_refused_and_the_month_cannot_reopen; negative control ConcurrencyTests.Negative_control_without_the_close_lock_a_movement_lands_in_a_closed_month | PASS |
| H08: two 60 % placements, database guard only (RI001) | ConcurrencyTests.Two_placements_on_one_premium_row_never_exceed_it | PASS |
| H08: two placements through the application (policy lock, plan check) | Clm.Acceptance_H08_two_facultative_placements_through_the_application_the_second_waits_for_the_policy_and_its_plan_check_refuses | PASS |
| H11 negative control / H11 (suspension vs a receipt in flight) | receipts / AML group | OTHER GROUP |
| oral instruction 24 h without the letter; late letter; officer resumes | AML group | OTHER GROUP |
| H13 negative control / H13 (suspension aimed at a policy vs a receipt) | receipts / AML group (the lock order fix below applies to it too) | OTHER GROUP |
| claim target, negative control (client locked before the claim) | Clm.Acceptance_negative_control_a_suspension_that_locks_the_client_before_the_claim_overtakes_the_payment_in_flight — in SQL Server this order does not deadlock (40P01): the suspension overtakes the payment in flight | PASS |
| claim target: the suspension waits for the payment in flight, which pays; the next is refused — **defect fixed** | Clm.Acceptance_a_suspension_aimed_at_the_claim_waits_for_the_payment_in_flight_which_pays_and_the_next_payment_is_refused | PASS |
| attach of an old unmapped suspension (negative control and fix) | the fixture needs `legacy_unmapped = 1`, which only the 0022 transition wrote (legacy disabled the trigger to build it; here `fms_suspension_insert` always writes 0); building it would bypass a trigger | NOT TESTED |
| claim payments, negative control: without the claim lock 1 200 paid on 1 000 | Clm.Acceptance_negative_control_two_payments_of_one_claim_without_the_claim_lock_pay_1200_on_a_1000_approval | PASS |
| claim payments: two payments of 600 at once, the second waits and is refused | Clm.Acceptance_two_payments_of_600_of_one_claim_at_once_wait_for_each_other_and_the_second_is_refused_600_paid | PASS |
| XL: two excess-of-loss treaties on one claim at once → RI004 | Clm.Acceptance_two_writers_of_one_claims_shares_the_second_waits_and_is_refused_and_only_the_excess_of_loss_stays(true, "RI004") | PASS |
| XL: excess of loss and a proportional share at once → RI002 | same (false, "RI002") | PASS |

## scripts/claim-reserve-db-check.ts (0035)

| Case | Test | Verdict |
|---|---|---|
| the migrations it needs are applied | final-state schema | N/A |
| core_app: requests S/I yes, U/D no; reserve movements no U/D | Clm.R0035_rights_triggers_and_the_requests_own_checks_with_its_asker_from_the_session | PASS |
| the triggers: request audit, request guard, month guard | same (the month and request guards are one INSTEAD OF trigger, `claim_reserve_guard`) | PASS |
| a request stored exactly and audited once by its asker | ClaimReserveTests.A_request_records_base_level_and_difference_and_moves_nothing_until_another_user_approves_it | PASS |
| a request alone changes nothing | same | PASS |
| who asked is filled from the session | Clm.R0035_rights_triggers_… | PASS |
| difference not level − base / no change / negative level / no reason → their checks | Clm.R0035_rights_triggers_… | PASS |
| core_app edits / deletes a request → refused | ClaimReserveTests.The_database_refuses_a_movement_that_does_not_carry_out_its_request_exactly | PASS |
| another user approves: one movement, exactly, by the approver | ClaimReserveTests.A_request_records_…; ClaimReserveTests.The_database_refuses_… | PASS |
| a second approval → RR004 (stale before the index) | Clm.R0035_rights_triggers_…; ClaimReserveTests.The_database_refuses_… | PASS |
| a second movement with the reserve back at base → unique index | ClaimReserveTests.The_database_refuses_… | PASS |
| the asker approves own request → RR003; the admin too | ClaimReserveTests.The_database_refuses_…; Clm.R0035_rights_triggers_…; ClaimReserveTests.No_one_approves_their_own_request_the_admin_included_another_person_with_the_permission_does | PASS |
| another amount → RR002; another claim → RR001; reserve moved → RR004 | ClaimReserveTests.The_database_refuses_… | PASS |
| a request dated in a closed month, approved → 23514 (month guard first) | Clm.R0035_rights_triggers_…; ClaimReserveTests.A_request_dated_in_a_month_closed_before_the_approval_is_period_closed_twice | PASS |
| a movement without a request is not touched by the guard | ClaimReserveTests.The_database_refuses_… | PASS |
| claim.reserve.approve in the permission store: admin yes, viewer no, group gives, personal removal takes | ClaimReserveTests.Who_may_approve_by_role; ClaimReserveTests.A_group_gives_it_a_personal_removal_beats_the_group_and_payment_rights_do_not_give_it (the audit of permission-store writes: permissions group) | PASS |
| rollback: nothing of the check stays | Clm.R0035_rights_triggers_… (tagged rows counted after) | PASS |

## scripts/claim-reversal-db-check.ts (0033, 0034)

| Case | Test | Verdict |
|---|---|---|
| rows written before 0033 became payments | upgrade path from a pre-0033 schema | N/A |
| core_app: insert yes, update/delete no | Clm.R0033_the_application_may_add_claim_payments_but_never_change_or_remove_them | PASS |
| the exact mirror dated today accepted | Clm.R0033_the_exact_mirror_dated_today_is_accepted_and_every_wrong_reversal_is_refused_by_its_own_guard | PASS |
| a second reversal → 23505 claim_payment_reversal_once | same | PASS |
| a reversal of the reversal → CP001; another rate → CP002; not the exact opposite → CP003; before the payment → CP004 | same | PASS |
| in a closed month → 23514, month guard first | same | PASS |
| "returned" without bank reference → 23514 claim_payment_kind_shape; with day and reference → ok | same | PASS |
| "returned" before the payment → CP007 (0034) | same | PASS |
| a payment with a negative amount → 23514 claim_payment_kind_shape | same | PASS |
| set-off not given back → CP005; given back by its receipt's reversal the same day → ok; another day → CP005 | Clm.R0033_a_set_off_goes_back_only_by_its_own_receipts_reversal_on_the_same_day | PASS |
| control: the case claiming only what stays → ok | Clm.R0033_a_reversal_that_would_leave_regress_above_what_was_paid_is_CP006_and_the_case_claiming_what_stays_is_accepted | PASS |
| a reversal leaving regress above what was paid → CP006 | same (action level: SubrogationCaseTests.A_claim_payment_reversal_that_would_leave_regress_above_what_was_paid_is_refused_CP006) | PASS |
| nothing of the check stays | asserted in the R0033 tests | PASS |

## scripts/claim-payee-race.ts

| Case | Test | Verdict |
|---|---|---|
| R1 FMS on a third payee, writer first | Clm.R1_to_R7_a_writer_first_holds_the_payout_on_its_participant_which_is_then_refused_with_nothing_written("R1", …) (also ClaimConcurrencyTests.An_FMS_suspension_being_registered_on_the_third_payee_holds_the_payout_which_is_then_refused) | PASS |
| R2 sanctions on a third payee — **defect fixed** | same ("R2") | PASS |
| R4 FMS on the policyholder, clean third payee | same ("R4") | PASS |
| R5 sanctions on the policyholder, clean third payee — **defect fixed** | same ("R5") | PASS |
| R6 FMS on the policyholder who is the payee | same ("R6") | PASS |
| R7 sanctions on the policyholder named as "other" payee — **defect fixed** | same ("R7") | PASS |
| R3 payout first, then the FMS writer on the third payee | Clm.R3_R8_a_payout_first_makes_the_writer_on_the_third_payee_wait_pays_once_and_the_next_payout_is_refused("R3", true) | PASS |
| R8 payout first, then the sanctions writer — **defect fixed** | same ("R8", false) | PASS |

## scripts/claim-offset-evidence.ts (kind `evidence`, not a db-check)

| Case | Test | Verdict |
|---|---|---|
| ordinary claim, set-off, control: 273.75 / 726.25 | ClaimTotalLossTests.An_ordinary_claim_with_the_set_off_takes_only_what_is_owed_up_to_the_net | PASS |
| ordinary claim, set-off, a later cancellation already booked | RULE OPEN in legacy docs/05-kitxvebi.md (owner's Q1/Q2: today's 273.75 or the final 182.75); no rule chosen | NOT TESTED |
| total loss (a), control | ClaimTotalLossTests.Control_rule_a_on_an_active_policy_cancels_from_the_loss_without_a_correction | PASS |
| total loss (a), later cancellation booked: 61.75 | ClaimTotalLossTests.Rule_a_on_a_policy_cancelled_from_a_later_day_brings_the_cancellation_forward_to_the_loss_and_sets_off_61_75 | PASS |
| total loss (b), later cancellation booked | RULE OPEN in legacy docs/05-kitxvebi.md | NOT TESTED |
| total loss (c), later cancellation booked: no offset, 61.75 still owed | ClaimTotalLossTests.Rule_c_on_a_policy_cancelled_from_a_later_day_cancels_from_the_loss_and_sets_off_nothing | PASS |

## scripts/regress-db/check.ts (0030, D1–D20)

| Case | Test | Verdict |
|---|---|---|
| guard: marked target, own sessions, timeouts, nobody else connected | legacy runner infrastructure; each test class builds its own `ins_it_*` database | N/A |
| D1 (legacy phase and after 0030): old recovery rows unchanged by 0030 | upgrade path from a pre-0030 schema (old and new rows side by side: SubrogationCaseTests.G8_G11_old_receipts_and_salvage_stay_as_they_were_and_count_once) | N/A |
| D2 a case by plain SQL on class 14, 3.2, theft, no coverage refused; USD damage opens | Reg.D2_a_case_by_plain_SQL_on_class_14_class_3_2_theft_or_a_line_without_coverage_is_refused_and_a_USD_damage_claim_opens | PASS |
| D3 G23–G25 and every movement audited with its author | Reg.D3_D4_every_movement_is_audited_with_its_author_and_claim_B_of_the_same_event_is_untouched; SubrogationCaseTests.G23_G24_G25_… | PASS |
| D4 claim B of the same event untouched | Reg.D3_D4_… | PASS |
| D5 same request key / same bank reference → 23505, no row | Reg.D5_D6_the_same_request_key_or_bank_reference_is_23505_and_claim_Bs_money_on_claim_As_case_is_SG031_with_no_row | PASS |
| D6 money of claim B on claim A's case → SG031 | same | PASS |
| D7 the cap: 800.01 / back-dated → SG020, 800 ok; USD 2160 (R1) | Reg.D7_the_cap_by_plain_SQL_is_SG020_above_what_was_paid_or_before_the_payment_and_a_USD_claim_counts_the_GEL_paid | PASS |
| D8 a movement and a receipt in a closed month → 23514 | Reg.D8_a_movement_and_a_receipt_dated_in_a_closed_month_are_23514_with_no_row | PASS |
| D9 SG011, SG010, SG012, SG013; money reversed twice → 23505 | Reg.D9_D10_D11_reversals_that_may_not_be_a_cancellation_without_the_courts_number_and_closing_with_money_owed_are_refused | PASS |
| D10 cancellation without the court's number / on a correction → 23514 | same | PASS |
| D11 closing a case with 800 owed → SG006 | same | PASS |
| D12 a receipt is the money row and its movement, or nothing (SG034) | SubrogationCaseTests.A_receipt_and_its_movement_are_written_together_and_a_failure_after_them_keeps_neither; SubrogationCaseTests.R02_the_application_role_cannot_write_case_money_past_the_procedure_even_with_the_old_session_key; SubrogationCaseTests.R02_a_pass_of_another_session_does_not_count_and_concurrent_forgery_leaves_no_orphan (SG034 is immediate here and cannot be set by core_app, see database/README.md difference 1) | PASS |
| D13 two receipts of 400 at once against 700: B waits, SG021, balance 300 | Reg.D13_two_receipts_of_400_at_once_against_700_owed_the_second_waits_on_the_claim_lock_and_is_SG021_balance_300 | PASS |
| D14 the same receipt reversed twice at once: B waits, 23505/SG011 | Reg.D14_the_same_receipt_reversed_twice_at_once_the_second_waits_and_is_refused_and_the_balance_is_back | PASS |
| D15 a second case on a closed case's claim; two cases at once | Reg.D15_one_case_per_claim_a_second_by_plain_SQL_is_23505_and_two_at_once_the_second_waits_and_is_23505; SubrogationCaseTests.H81_one_case_per_claim_… | PASS |
| D16 old subrogation counts in the cap, salvage not; case-less subrogation → SG032 | Reg.D16_old_subrogation_takes_its_share_of_the_cap_salvage_does_not_and_new_caseless_subrogation_is_SG032; SubrogationCaseTests.H81_old_regress_money_…; ClaimRecoveryTests.Once_a_claim_has_a_regress_case_its_regress_money_goes_through_the_case_salvage_does_not | PASS |
| D17 confirmation of 950 while the old 50 is given back: B waits, both ok; cap by day | Reg.D17_a_confirmation_of_950_while_the_old_50_is_given_back_at_once_the_second_waits_both_go_and_the_cap_follows_the_day | PASS |
| D18 a later reversal/confirmation does not cover an earlier over-receipt | Reg.D18_a_later_reversal_or_confirmation_does_not_cover_an_earlier_over_receipt_in_the_service_or_by_plain_SQL; SubrogationCaseTests.H82_… | PASS |
| D19 600 on 10.07 and 600 on 05.07 at once: B waits, SG021 | Reg.D19_600_on_10_07_and_600_on_05_07_at_once_against_1000_confirmed_the_second_waits_and_is_SG021 | PASS |
| D20 status under the lock (H83), with the audit closed → partially_received | SubrogationCaseTests.H83_the_case_is_read_again_under_the_claim_lock_so_a_receipt_that_closed_it_meanwhile_is_seen (audit assertion added) | PASS |
| rights: core_app changes/deletes movement, case, money → 42501 | Reg.Rights_core_app_cannot_change_or_delete_the_ledger_the_case_or_the_money | PASS |
| every day of every case: never more received/cancelled than confirmed | `AssertEveryDayAsync` at the end of Reg.D7, D9_D10_D11, D13, D14, D18, D19 | PASS |

## scripts/regress-db/accept.ts + acceptance.ts (R1–R3)

| Case | Test | Verdict |
|---|---|---|
| guards: target, role, marker, read-only snapshot, migrations, fictional data, fixtures found | legacy runner infrastructure (the fixtures are built by the test itself) | N/A |
| R1 claim A's card day by day = hand-worked figures = SQL oracle | Reg.R1_R2_the_cards_day_by_day_equal_the_hand_worked_figures_and_the_SQL_oracle | PASS |
| R1 history on 31.07 with balances after each line | same | PASS |
| R1 parties; limit unknown on a date without a business day (H88) | same (parties); SubrogationReportStoreTests.H88_a_status_change_is_certain_only_on_the_days_before_it_began_and_a_broken_history_is_unknown | PASS |
| R1 split on the date and today (H89) | SubrogationReportStoreTests.H89_a_rejection_stays_in_force_against_a_later_dated_acceptance_and_a_later_confirmation_is_not_counted | PASS |
| R2 D20 days, history, status now | Reg.R1_R2_… | PASS |
| R2 written order differs from the order shown (H87) | Reg.R1_R2_… (a case written 20.07 then 05.07, shown 05.07 then 20.07) | PASS |
| R3 report on 31.07 and 24.07, outside all / claims of cases / none = oracle; case-less money apart | Reg.R3_the_report_and_the_periods_equal_the_SQL_oracle_and_caseless_money_stays_apart; SubrogationReportStoreTests.QR4_… | PASS |
| R3 only with a balance (Q-R1) | Reg.R3_…; SubrogationReportStoreTests.QR1_… | PASS |
| R3 periods July, 01–15.07, 16–31.07 = oracle (Q-R5) | Reg.R3_…; SubrogationReportStoreTests.QR5_… | PASS |

## scripts/regress-db/isolate.ts + isolation.ts (R4, R5)

| Case | Test | Verdict |
|---|---|---|
| R4.2 an insert inside the read-only snapshot → 25006 | SQL Server has no read-only transaction (a snapshot transaction may write); the reader's statements are SELECT-only by source review: SubrogationReportStoreTests.Every_statement_is_a_single_SELECT_with_parameters_only (a different kind of evidence) — see open question 2 | N/A |
| R4.4 core.next_number inside a read-only transaction → 25006 | same | N/A |
| R4.6 a fresh snapshot shows the same fingerprint | follows R4.2/R4.4 | N/A |
| R5 A/B: a committed claim of 100 is not seen in the open snapshot (card, report, July) | Reg.R5_a_claim_of_100_committed_while_a_snapshot_is_open_is_not_seen_in_it_and_is_seen_in_a_new_one_with_nothing_else_changed; SubrogationReportStoreTests.All_reads_of_one_report_see_one_committed_state | PASS |
| R5 C: a new snapshot sees +100 in card, report, July and the oracle; every other case unchanged | Reg.R5_… | PASS |
| R5 one movement more, no new money row, no case audit row; the audit since A is that insert by its author | Reg.R5_… | PASS |
| R5.8 R1–R3 again after the write | Reg.R5_… (every other case = oracle before) + Reg.R3_… | PASS |
| R5 lost-commit reconciliation, one call without retry | legacy runner infrastructure | N/A |

## scripts/regress-db/catalog-audit.ts + catalog.ts (K1–K9)

| Case | Test | Verdict |
|---|---|---|
| K0 server version, search path | PostgreSQL information only | N/A |
| K1 role attributes: no superuser/bypass/createdb/createrole | Sch.K1_K2_the_roles_are_plain_database_roles_members_of_nothing_and_the_application_login_holds_only_core_app | PASS |
| K1 core_reader read only by default (default_transaction_read_only) | no SQL Server equivalent; core_reader holds SELECT only (Sch.K4_K5_…) | N/A |
| K2 memberships, transitively | Sch.K1_K2_… | PASS |
| K3 schema core: owner; USAGE/CREATE | Sch.K3_the_schema_belongs_to_the_owner_and_neither_role_may_alter_it_or_create_objects | PASS |
| K4 every privilege of core_app and core_reader on each regress table | Sch.K4_K5_every_privilege_of_core_app_and_core_reader_on_each_regress_table_is_exactly_the_plan_and_public_holds_nothing | PASS |
| K5 owners; PUBLIC holds nothing | same | PASS |
| K6 the identity sequence of audit_log | an identity column, no sequence object to grant | N/A |
| K7 functions: no security definer, EXECUTE for core_app | Sch.K7_the_regress_procedures_and_functions_run_as_their_caller_and_core_app_may_execute_them (`search_path=""` has no equivalent; legacy core_reader could EXECUTE through PUBLIC, here it cannot — stricter) | PASS |
| K8 default privileges | SQL Server has none; the migration runner re-asserts every right after each run (Migrator.ReassertAsync; SchemaTests.Application_role_cannot_change_ledgers_journal_or_schema) | N/A |
| K9 the regress tables have exactly the planned triggers, enabled | Sch.K9_the_regress_tables_have_exactly_the_planned_triggers_all_enabled_per_row_guards_and_audits | PASS |

## scripts/regress-db/negative-run.ts + negative.ts (N1–N6)

| Case | Test | Verdict |
|---|---|---|
| N1 delete subrogation_case; N2a/N2b update/delete claim_recovery; N3 update audit_log; N6 update schema_migration → 42501 | Sch.N1_to_N6_core_app_is_refused_on_cases_money_audit_and_journal_and_may_add_audit_rows_with_nothing_moved | PASS |
| N5 insert into audit_log … where false → ok, 0 rows | same | PASS |
| fingerprint before = after (rows, audit ids, identity, number series) | same | PASS |
| the triggers of the four tables checked before and after | Sch.K9_… | PASS |

## scripts/regress-db helpers, scripts/recovery/*

`guard.ts`, `runner.ts`, `session` helpers, `fake-regress-db.ts`, `reset.ts` are the legacy runner's infrastructure; `scripts/recovery/*`
(capture, compare, guard) are helpers of the backup/restore rehearsal, not db-checks and not in this group: **N/A**.

## Counts

Table rows (a row may group several cases of one kind, as listed in it); the helper scripts of the last section are N/A besides.

| Verdict | Rows |
|---|---|
| PASS | 120 |
| N/A | 15 |
| NOT TESTED | 3 |
| OTHER GROUP | 10 |

## OTHER GROUP rows after the merge of all groups

The ten OTHER GROUP rows above are proved by the other areas' tests (all on real SQL Server):

| Row | Test | Verdict |
|---|---|---|
| issuePolicy 20 at once, own policy and contract numbers | Policies.LegacyIssueConcurrencyTests.Twenty_issues_at_once_get_twenty_policy_and_contract_numbers_each_policy_on_its_own_contract | PASS |
| issuePolicy refuses a sanctions-blocked client (AG-P6) | Policies.LegacyIssueConcurrencyTests.A_sanctions_blocked_client_is_refused_by_its_own_rows_while_another_clients_policy_is_issued_at_the_same_moment | PASS |
| issuePolicy with the contract series run out | Policies.IssueSeriesExhaustedTests.An_exhausted_contract_series_writes_nothing | PASS |
| confirmImport with the policy series run out; first gets the last number, second refused | Policies.ImportEndorseSeriesExhaustedTests.ConfirmImport_with_the_policy_series_run_out_writes_no_contract_and_moves_no_counter, …ConfirmImport_of_two_policies_with_one_number_left_rolls_the_first_policy_and_the_contract_back | PASS |
| endorsePolicy with the policy series run out | Policies.ImportEndorseSeriesExhaustedTests.EndorsePolicy_with_the_policy_series_run_out_issues_no_change_and_moves_no_counter (negative control: the same change is issued once numbers are left) | PASS |
| registerComplaint / registerCase with their series run out | Web: IntermediaryComplaintTests.A_run_out_complaint_series_says_so_and_writes_nothing, AmlTests.A_run_out_case_series_says_so_and_writes_nothing | PASS |
| createTreaty with its series run out | Reports.TreatySeriesExhaustedTests.CreateTreaty_with_the_treaty_series_run_out_writes_nothing_and_moves_no_counter (negative control: the same form creates the treaty once numbers are left; docs/legacy-db-checks-accounting.md) | PASS |
| H04 negative control / H04 (two receipts on one policy) | Policies.PaymentConcurrencyTests.Two_receipts_on_one_policy_settle_it_once, …Negative_control_without_the_policy_lock_two_receipts_settle_one_instalment_twice | PASS |
| H11 negative control / H11 | AmlSuspensionAcceptanceTests.H11_a_suspension_waits_for_a_receipt_in_flight_and_the_next_receipt_is_refused, …H11_negative_control_with_the_check_outside_the_transaction_a_receipt_commits_after_the_suspension | PASS |
| oral instruction 24 h without the letter; late letter; officer resumes | AmlSuspensionAcceptanceTests.An_oral_instruction_without_the_letter_stays_in_force_after_24_hours_until_the_officer_lifts_it | PASS |
| H13 negative control / H13 | AmlSuspensionAcceptanceTests.H13_a_suspension_of_one_policy_against_a_receipt_on_it_does_not_deadlock_and_no_receipt_slips_past_it (the legacy order: the receipt commits first); the PostgreSQL deadlock of the old lock order has no SQL Server counterpart (see the claim-target row above) | PASS |

## Defects found and fixed (each test fails on the code before the fix)

1. **core.next_number moved an exhausted series' counter when called outside a transaction** (`database/migrations/0001_foundation.sql`).
   SQL Server commits each statement of a procedure on its own in autocommit mode, so the counter went to 101 and then the procedure
   refused; legacy `select core.next_number(…)` rolled the move back with the refusal. The update now moves the counter only when the
   number fits. Negative control: Num.An_exhausted_series_called_outside_a_transaction_also_leaves_the_counter_where_it_was failed
   (counter 101) before the fix.
2. **A claim payout paid a sanctioned person while the sanctions decision was being written** (`Aml.SuspensionInTransactionAsync`).
   Legacy share-locked the party row ("for share"); the port replaced it with an application lock only, and under row versioning the
   payout read `sanctions_blocked` as it was before the uncommitted decision (R2, R5, R7: paid; R8: the decision did not wait for the
   payout in flight). The party row is now also held shared, as in legacy. Negative control: Clm.R1_to_R7 ("R2", "R5", "R7") and
   Clm.R3_R8 ("R8") failed before the fix.
3. **A suspension aimed at a claim did not wait for a payment in flight on that claim** (`Aml.LockForSuspensionChangeAsync`). Legacy
   "for key share" waits for the payment's "for update"; a shared lock in SQL Server does not wait for an update lock, so the
   suspension overtook the payment and the payment in flight was refused. The target row is now read with an update lock. Negative
   control: Clm.Acceptance_a_suspension_aimed_at_the_claim_waits_… failed before the fix.

## Open questions

1. (legacy docs/05-kitxvebi.md, still open) A premium set-off on an ordinary claim, and total-loss rule 15 (b), when the policy already
   has a cancellation from a later day: today's unpaid premium (273.75) or what the client will finally owe (182.75)? The program keeps
   the legacy behaviour; no rule was chosen here.
2. The legacy report reader ran in a read-only transaction, so even a coding mistake could not write (R4, 25006). SQL Server has no
   read-only transaction; the port relies on the reader's statements being SELECT-only (source review). Should the report pages
   read through a separate login that holds only `core_reader`, to keep a database-enforced guarantee?
