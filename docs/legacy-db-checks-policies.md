# Legacy real-database checks: policies, payments, client credit, FD requests, permissions

The legacy `npm test` never touched a real database; the `db-check` scripts below were its only real-database evidence, and
several of them (`fd-*-db-check`, `premium-precision-db-check`) were "PREPARED, NOT RUN" — on SQL Server they are real evidence
for the first time. This table maps every numbered case of the scripts of this group (legacy commit `17c1f1f`) to the xUnit
test that now proves it on a real SQL Server (a fresh `ins_it_*` database per test class; the application connecting as a
member of `core_app` only), or says why it does not apply. Scenario numbers and amounts are the legacy scripts' own.

Verdicts: **PASS** — a test runs it on real SQL Server and passes; **N/A** — the case checks something that does not exist in the
new program (with the reason); **NOT TESTED** — it could not be run (with the reason); **OTHER GROUP** — another group's port.
NOT TESTED and OTHER GROUP never count as PASS.

Test classes: in `tests/Insurance.Integration.Tests/Policies` — `LegacyAcceptanceGapsTests` (AG), `LegacyAcceptanceGapsClosedMonthTests`
(AGm), `LegacyApprovalAnswersTests` (Apr), `LegacyClientCreditTests` (CC), `LegacyFdRequestGuardTests` (FdG), `LegacyFdPanelReaderTests`
(FdP), `LegacyFdRaceTests` (FdR), `LegacyIssueConcurrencyTests` (Iss), with the overlap harness `Overlap.cs`; in
`tests/Insurance.Integration.Tests` — `LegacyTotalLossTests` (TL), `LegacyPermissionsDbCheckTests` (Perm), `TestDatabaseIsolationTests`
(Iso), and the earlier `ClaimTotalLossTests`, `PermissionStorageTests`, `ConcurrencyTests`, `Policies/PolicyApprovalTests`; in
`tests/Insurance.Web.Tests/Policies` (HTTP against the real application) — `LegacyAddressGuardTests` (Addr), `LegacyPremiumPrecisionTests` (Prec).

How the legacy harnesses map: "B seen waiting on A" (`pg_blocking_pids`) is `sys.dm_exec_requests.blocking_session_id` read by a
monitor connection; an action's "own backend" is the session that appears in the holder's lock chain after the action is
started (a foreign same-role waiter already waiting is never taken for it); `pg_cancel_backend` inside a transaction is `KILL`
of that proven session (its transaction rolls back); a savepoint per attempt is a transaction per attempt, rolled back; the
legacy app-race child's "held before COMMIT" is a test-only trigger on the table the approval writes last, which holds the
named user's transaction on an application lock the test releases, then lets it commit or throws `HarnessRollback`.

## scripts/acceptance-gaps-regression.ts

| Case | Test | Verdict |
|---|---|---|
| AG-P5 a policy starting in a closed month, approved now: premium on the first open day, cover unchanged, cessions on the same day | AGm.AG_P5_a_policy_starting_in_a_closed_month_approved_now_books_on_the_first_open_day_with_its_cover_unchanged (own database; the legacy run was SKIP without a closed month) | PASS |
| AG-C2 91.25 received and a cancellation on day 182 in either order: premium 181, schedule 181, paid 91.25, owed 89.75 | AG.AG_C2_payment_and_cancellation_in_either_order_keep_181_and_leave_89_75_owed | PASS |
| AG-C4 a claim on the cancelled policy: day before registered, cancellation day and day after refused on occurred_on, 1 claim | AG.AG_C4_a_claim_on_the_cancelled_policy_only_for_a_loss_before_the_cancellation_day | PASS |
| AG-P6 approval during an FMS suspension refused, nothing booked; after the lift approved, one premium row | AG.AG_P6_an_approval_during_an_FMS_suspension_is_refused_and_books_nothing_and_after_the_lift_goes_through | PASS |

## scripts/address-guard-check.ts

| Case | Test | Verdict |
|---|---|---|
| Typed addresses on /policies/pending and the card (odd and inherited codes, ri-*, repeated keys, every answer code, number, forged ack): 200, no answer shown | Addr.An_approval_answer_is_never_taken_from_the_address_bar_and_each_real_press_shows_its_own | PASS |
| The card's other address message (advance=nothing-left) still shows | same | PASS |
| Operator presses approve on the list / on the card (no JavaScript): forbidden, 0 premium rows, no cookie | same | PASS |
| Admin presses approve with a malformed id: invalid | same | PASS |
| Admin approves on the card: done, 1 premium row | same | PASS |
| Admin presses again from the list it had open: already, names the policy, still 1 row | same | PASS |

The script's text snippets were older wordings; the test uses the answer texts of the legacy i18n at `17c1f1f` (the ones the new program shows).

## scripts/answer-parallel-check.ts

| Case | Test | Verdict |
|---|---|---|
| Parallel presses of the close-month form each show their own answer; periods unchanged | — | OTHER GROUP (only the close-month form: accounting slice, after its merge) |

## scripts/approval-answers-regression.ts

| Case | Test | Verdict |
|---|---|---|
| Malformed ids "abc", "", "1' or '1'='1": invalid, no database error | Apr.A_malformed_or_unknown_id_is_answered_invalid | PASS |
| Unknown id: invalid | same | PASS |
| Operator presses approve: forbidden, still pending, 0 rows | Apr.Operator_forbidden_head_done_again_already (also PolicyApprovalTests.The_roles_starting_set) | PASS |
| Head approves: done + number, active, 1 row | same | PASS |
| Head presses again: already + number, 1 row | same (also PolicyApprovalTests.Every_press_gets_an_answer_and_the_money_is_written_once) | PASS |
| Two presses at once (two tabs): done and already, 1 row | Apr.Two_presses_at_the_same_moment_each_get_their_own_answer_and_approve_once | PASS |

## scripts/client-credit-regression.ts

| Case | Test | Verdict |
|---|---|---|
| C1 A paid 365, changed −100 and approved: schedule 265, settled 365, overpaid 100 | CC.C1_C2_C3_C4_an_overpayment_listed_and_transferred_once_to_the_same_clients_policy_only | PASS |
| C2 B (same client, pending) lists 100 from A; transferred: −8.75 (3rd), −91.25 (4th); B settled 91.25 + 8.75, owes 265; receipt still 365, +4 lines | same | PASS |
| C3 again: nothing-left; a viewer: read-only; nothing written | same | PASS |
| C4 another client's policy: not-found, nothing written, it still owes 365 | same | PASS |
| C5 (H57) 0.02 USD / 0.05 GEL released in two parts: 0.03 + 0.02 GEL, source 0 / 0, targets 0.05, receipt 0.05 | CC.C5_a_usd_holding_released_in_two_parts_keeps_its_gel_exactly (the test stores its own fictional USD 2.70; the legacy run was SKIP without a stored rate) | PASS |
| C6 (H58) transfer and advance allocation of one receipt at once: both finish, A2 35, B 100, A 265, R 400 | CC.C6_a_transfer_and_an_advance_allocation_of_one_receipt_at_the_same_moment_both_finish_and_spend_each_amount_once | PASS |
| C7 A held; allocation queued, then transfer; foreign same-role waiter: each seen on its own session, foreign not counted, no deadlock, nothing-left + transferred, B 100, R 365, foreign finishes | CC.C7_policy_A_held_the_advance_allocation_and_the_transfer_both_finish_without_a_deadlock_and_a_foreign_waiter_is_not_counted | PASS |
| C7 "the old lock order deadlocked" (negative control) | CC.C7_negative_control_the_old_lock_order_target_receipt_then_source_deadlocks_with_the_allocation | PASS |
| C8 A held; two transfers to B1 and B2: one transferred 100, one nothing-left; A 265; R 365 | CC.C8_policy_A_held_two_transfers_of_the_same_excess_one_transfers_100_and_the_other_finds_nothing_left | PASS |
| C9 / C9b A held; refund and transfer in both orders: each amount one way; A net 265 | CC.C9_policy_A_held_a_transfer_and_a_refund_of_the_same_excess_each_amount_goes_one_way(false/true) | PASS |
| C10 / C10b receipt's lock held; reversal and transfer in both orders: receipt + reversal net 0 / 0; B net 0 when transferred first | CC.C10_the_receipts_lock_held_a_transfer_and_a_reversal_the_receipt_and_its_reversal_net_to_zero(false/true) | PASS |
| C11 target held; the transfer killed (its own proven session) after its negative source lines: state as before, ids included; retry transfers 100; repeat nothing-left; foreign waiter left alone | CC.C11_the_transfer_killed_after_its_negative_source_lines_leaves_nothing_behind_the_retry_transfers_once_and_a_foreign_waiter_is_left_alone | PASS |
| C12 two receipts on one USD instalment released in parts to a USD and a GEL policy: listed 0.02/0.05 and 0.01/0.03, then 0.01/0.02; V 0.07, W 0.03; each receipt 0.05 | CC.C12_C14_two_receipts_on_one_usd_instalment_released_in_parts_to_a_usd_and_a_gel_policy_then_one_reversed | PASS |
| C13 receipt 400 (35 advance), 100 transferred: reversed −400, mirror −365, every instalment 0/0, own lines unchanged; again nothing | CC.C13_a_partly_allocated_receipt_with_a_transfer_reversed_then_reversed_again | PASS |
| C14 R1 of C12 reversed: −0.05, mirror lines at their own rates (4 at 2.70, 1 at 1), all 0/0, R2 and S's schedule untouched | CC.C12_C14_… | PASS |
| C15 allocation begun before the transfer, written after it; reversed: −365, all 0/0 | CC.C15_an_advance_allocation_begun_before_a_transfer_and_written_after_it_then_the_receipt_reversed | PASS |
| C15 "by created_at the allocation's lines come before the transfer's" | — | N/A: SQL Server stamps `created_at` per statement, not at the transaction's start (database/README.md, difference 2), so this PostgreSQL mis-order cannot arise; the reversal over the same history is checked above, the ordering search itself by the `Receivable.OrderMirrorLines` unit tests |
| C16 (H60) +100 A, −80 A, +80 B, −60 B, +20 C (40 left): reversed −100, 5 lines −60, all 0/0; again nothing | CC.C16_H60_plus_100_A_minus_80_A_plus_80_B_minus_60_B_plus_20_C_reversed_then_again | PASS |
| C17 (H61) 19 lines, many of one amount, 40 left: reversed −100, 19 lines −60, 10 instalments 0/0, schedules unchanged; again nothing | CC.C17_H61_nineteen_lines_many_of_one_amount_reversed_then_again | PASS |
| CREDIT_OLD_SELECTOR control run (the earlier waiter selection counting a foreign backend) | — | N/A: a control of the legacy harness's own pid selection; here the action's session is identified by difference (new in the chain), and C7/C11 assert the foreign session is not taken |

C11 note: on SQL Server a `select … with (xlock)` does not stop a read-committed reader of an unchanged row (the foreign-key check), so
the holder changes B's instalments to themselves (`amount = amount`, audited like any change) and is rolled back — nothing of it stays.

## scripts/fd-concurrency-db-check.ts (race-engine.ts) — PREPARED, NOT RUN in the old program

| Case | Test | Verdict |
|---|---|---|
| Claim payment (0036), A commits: B seen waiting on A, meets claim_payment_request_once; one row, A's; B's nowhere; change log = the setup's request insert by A, older rows unchanged | FdR.The_row_that_carries_the_link_written_twice_at_once_is_written_once("claim", true) | PASS |
| Claim payment, A rolls back: B carried out | same ("claim", false) | PASS |
| Premium refund (0037), A commits / rolls back: payment_refund_request_once; allocation exactly the plan; loser's nowhere | same ("refund", true/false) | PASS |
| Receipt reversal, one request twice, A commits / rolls back: payment_reversal_once or payment_reversal_request_once | same ("reversal-one", true/false) | PASS |
| Receipt reversal, two requests on one receipt, A commits / rolls back: payment_reversal_once | same ("reversal-two", true/false) | PASS |
| The check's rows: 8 carried out; untouched rows' fingerprint | each race reads back its own rows; every race has its own fresh rows in the class's own database | PASS |
| Refused on any database but its throwaway copy; journal / index prerequisites (NOT TESTED without) | — | N/A: the test database is created by the real migrations for the class and dropped after it (no shared copy, no missing migration) |

## scripts/fd-app-concurrency-db-check.ts (app-race.ts) — PREPARED, NOT RUN in the old program

| Case | Test | Verdict |
|---|---|---|
| approveClaimPaymentRequest twice, A commits: A approved, B already (waits on A); one claim payment by A, reserve −10 000.01 by A, claim paid; change log = the claim's update by A | FdR.Two_approvals_of_one_claim_payment_request_pay_it_once(true) | PASS |
| … A rolled back at its end: A thrown HarnessRollback, B approved | same (false) | PASS |
| approvePremiumRefundRequest twice, A commits / rolled back: approved + already / thrown + approved; one refund −10 000.01 with its line by the winner; no other row | FdR.Two_approvals_of_one_premium_refund_request_refund_once(true/false) | PASS |
| approveReceiptReversalRequest, one request twice, A commits / rolled back: approved + already / thrown + approved; one mirror and its line by the winner | FdR.Two_approvals_of_a_receipts_reversal_mirror_it_once(false, true/false) | PASS |
| … two requests on one receipt: approved + state / thrown + approved | same (true, true/false) | PASS |
| Each child's pool closed, exit 0 | — | N/A: no child processes; each action runs in-process on its own pooled connection |

## scripts/fd-panel-db-check.ts (fd-panel-walk.ts) — PREPARED, NOT RUN in the old program

| Case | Test | Verdict |
|---|---|---|
| Claim payments: 52 requests (40 at one instant), first page 50 with its cursor; the cursor's request carried out; pages after it — each once, in (created_at, id) order; read again — 51 siblings, card, receipt, requester, GEL exactly (90 071 992 547 408.01); its payment's reversal does not bring it back; reads write nothing; all rolled back | FdP.Claim_payments_paged_in_order_carried_out_off_the_list_and_not_brought_back_by_its_reversal | PASS |
| Premium refunds: the same walk | FdP.Premium_refunds_paged_in_order_and_carried_out_off_the_list | PASS |
| Receipt reversals: the same walk, each with its receipt | FdP.Receipt_reversals_paged_in_order_and_carried_out_off_the_list | PASS |
| Prerequisite objects (tables, columns, triggers, indexes, setting) | — | N/A: the real migrations build them; their names differ by design (one guard trigger per table on SQL Server, database/README.md) |

## scripts/fd-requests-db-check.ts — PREPARED, NOT RUN in the old program

Each attempt runs in its own transaction, rolled back; every attempt's result is compared with the legacy expectation, and the
ledgers' fingerprint is the same before and after.

| Case | Test | Verdict |
|---|---|---|
| core_app on the five request tables: select/insert yes, update/delete/truncate no | FdG.Rights_of_core_app_on_the_request_tables | PASS |
| 0036: stored and audited once by the session user; net ≠ gross − ded − set-off (claim_payment_request_net); net 0; closed month (23514); update / delete (42501); the payment carrying its request exactly; a second one (claim_payment_request_once); another claim (CQ001); another gross, deductible, set-off, net, GEL, rate, day, payee, payee kind (CQ002 ×9); 10 000.01 without a request (CQ003); exactly 10 000.00 allowed; threshold empty / not a number / three decimals / missing (CQ004 ×4); a reversal of a requested payment allowed | FdG.Migration_0036_a_claim_payment_takes_its_request_over_exactly (closed month: 2020-01 closed in the class's database; the legacy run was SKIP without one) | PASS |
| 0037: request and line audited; line of nothing; position 0; two lines at one position; closed month; update / delete (42501); the refund with its planned line exactly; another GEL, method, reference, no reference, payer, day (RF002 ×6); NULL reference taken over; allocation with another instalment, amount, GEL, rate (RF005 ×4); a second refund (payment_refund_request_once); 10 000.01 without a request (RF003); exactly 10 000.00 allowed; threshold not a number / missing (RF004 ×2); a receipt of 20 000 not a refund; ten USD 0.01 lines at 2.5; nine −0.03 lines refused by PA002; half a tetri (USD 2.50 at 2.002 = 5.01); a refund with a line outside its plan refused as a whole | FdG.Migration_0037_a_premium_refund_takes_its_request_and_plan_over_exactly | PASS |
| 0038: request and line audited; closed month; update / delete (42501); the mirror with its planned line exactly; an advance's request with no line; 12 000 with no request (RV003); another GEL, method, payer, reason, day, receipt (RV002 ×6); allocation with another instalment, amount, GEL, today's rate (RV005 ×4); a second mirror (payment_reversal_once or _request_once); 10 000.01 without a request (RV003); 10 000.00 allowed; threshold not a number / missing (RV004 ×2); a claim set-off's mirror not checked; 90 071 992 547 408.01 on two policies with a −0.01 and a 0-GEL line negated exactly | FdG.Migration_0038_a_receipts_reversal_takes_its_request_and_plan_over_exactly | PASS |
| Prerequisite objects and journal | — | N/A: built by the real migrations (trigger names differ by design) |

## scripts/premium-precision-db-check.ts (precision-cases.ts) — PREPARED, NOT RUN in the old program

| Case | Test | Verdict |
|---|---|---|
| A1 GEL 90 071 992 547 408.01 over two instalments: payment page before / after, the receipt and its two lines exactly, the card's receivable and history | Prec.A1_GEL_90071992547408_01_over_two_instalments | PASS |
| A2 numeric(18, 2)'s largest 9 999 999 999 999 999.99: card before, receipt, line, page after | Prec.A2_GEL_numeric_18_2_largest_9999999999999999_99 | PASS |
| A3 one tetri: 0.01 owed after 100.00; 0.02 settles it and leaves 0.01 an advance | Prec.A3_GEL_one_tetri_owed_after_100_then_0_02_settles_it_and_leaves_0_01_an_advance | PASS |
| B cross-policy advance taken by b from b's own form; reversal above the threshold requested, refused to the accountant, approved by the admin; one mirror, both debts back; again already | Prec.Cross_policy_advance_and_the_receipts_reversal_above_the_threshold("B", …) | PASS |
| C the same with b in USD: advance at the receipt day's 2.70; mirror at the stored rate; b's card at today's 2.80 (100.00 USD = 280.00) | same ("C", …) | PASS |
| D1 stale: the request asked before b took the advance | same ("D1", …) | PASS |
| D2 a failure after the mirror's first line (injected), nothing reversed; the retry reverses once | same ("D2", …) | PASS |
| The three days d0 / d1 / d2 of the child's test clock | — | N/A: the new program has no settable clock; d0 is the 20th of the previous month and d1 = d2 = today, so every allocation and reversal is booked today (the expected rows say so) |
| Reviewed-source fingerprint, throwaway copy, child exit codes, National Bank asked 0 times | — | N/A: harness of the legacy run; the web host's NBG client is replaced by one that refuses every request |

## scripts/number-concurrency-regression.ts — the issuePolicy part

| Case | Test | Verdict |
|---|---|---|
| action: issuePolicy 20 times at once: 20 policies, distinct CAS… and C… numbers, each on its own contract | Iss.Twenty_issues_at_once_get_twenty_policy_and_contract_numbers_each_policy_on_its_own_contract | PASS |
| action: a sanctions_blocked client refused on policyholder_id_number, no contract or policy of its own, while another client's policy is issued at the same moment (counters only observed, H41) | Iss.A_sanctions_blocked_client_is_refused_by_its_own_rows_while_another_clients_policy_is_issued_at_the_same_moment | PASS |
| SQL level (core.next_number) | docs/legacy-db-checks-schema-claims.md | OTHER GROUP |

## scripts/total-loss-regression.ts (H24)

| Case | Test | Verdict |
|---|---|---|
| (a) earned_unpaid, cancellation from FUTURE booked: cancelled from the loss, 61.75 / 938.25, debt 0 today and on FUTURE | TL.A_earned_unpaid_with_a_future_cancellation_already_booked_and_the_premium_by_day (also ClaimTotalLossTests.Rule_a_on_a_policy_cancelled_from_a_later_day_…) | PASS |
| (a) ledger: issue 365, −91 @FUTURE, −212 @loss, +91 correction @FUTURE; sum 153; reconciliation 0 | same | PASS |
| (a) premium by day: today written 153.00 UPR 0.00; the day before the loss unchanged | same | PASS |
| (a) reinsurance mirror: 4 cessions, 30 % of 153.00 | TL.A_with_a_facultative_placement_placed_after_the_later_cancellation_mirrors_one_cession_per_row_adding_up_to_30_percent (also ClaimTotalLossTests) | PASS |
| (c) none with FUTURE booked: cancelled from the loss, 0.00 / 1000.00, debt 61.75 today and on FUTURE, reconciliation 0 | TL.C_none_with_a_future_cancellation_cancels_from_the_loss_and_sets_off_nothing | PASS |
| (a) with PAST already in force: 61.75 / 938.25, debt 0, ledger 153 | TL.A_with_the_later_cancellation_already_in_force | PASS |
| (a) paid 600 + 400: offsets 61.75 / 0.00, nets 538.25 / 400.00, one correction, one row on the loss day, debt 0 | TL.A_paid_in_two_parts_brings_the_cancellation_forward_once_and_sets_off_once | PASS |
| An ordinary cancellation from an earlier day on an already cancelled policy: refused, still from FUTURE, ledger unchanged | TL.An_ordinary_cancellation_from_an_earlier_day_on_an_already_cancelled_policy_is_refused_and_changes_nothing | PASS |
| Control: (a) on an active policy, no correction | TL.Control_A_on_an_active_policy_cancels_from_the_loss_without_a_correction | PASS |

## scripts/permissions-db-check.ts

| Case | Test | Verdict |
|---|---|---|
| Migrations on insurance_test: last 0032 | — | N/A: the new schema is the final legacy state in 0001–0008 (database/README.md); SchemaTests checks the journal |
| The four permission tables: core_app select/insert/update/delete | Perm.The_four_permission_tables_core_app_may_read_and_change_them | PASS |
| Every permission table has its audit trigger | PermissionStorageTests.Every_permission_table_is_audited | PASS |
| Role defaults: viewer no approve / close; accountant closes and receives, does not approve | Perm.The_store_on_the_real_database_role_defaults_a_group_a_removal_inherit_refusals_and_the_audit_by_the_admin | PASS |
| createGroup as core_app; the group's grant gives the viewer the approval (source group) | same | PASS |
| The viewer's own removal beats the group; inherit gives it back | same | PASS |
| The accountant's month close removed, then back to the role | same | PASS |
| The same grant again changes nothing | same | PASS |
| An unknown permission refused by the store (unknown-permission) | same | PASS |
| Malformed key 23514; decision other than allow/deny 23514; a second decision 23505 | same (also PermissionStorageTests.The_schema_itself_refuses_what_the_store_never_writes) | PASS |
| The audit: each change by the admin (group 1, grant 1, member 1, user_permission insert 3, delete 3) | same | PASS |
| Numeric and the exact sum agree: 90071992547408.00 + −90071992547407.99 = 0.01 | Perm.Exact_numeric_addition_and_the_bulk_schedules_and_allocations_equal_the_per_policy_reads | PASS |
| LoadSchedules equals LoadSchedule for every policy at 31.10.2026 and today | same (and a third day) | PASS |
| The bulk allocations equal the per-policy ones for every policy | same | PASS |
| The period close's month series (PR250) | — | OTHER GROUP (accounting slice, after its merge) |
| Cleanup: the group emptied, run users retired, connections closed | same test empties the group through the store; users and connections: the class's database is dropped | PASS |

## Dev/UAT infrastructure checks

| Script / case | Test | Verdict |
|---|---|---|
| parallel-users-probe.ts: another run's users stay active with their sessions | Iso.Dropping_one_runs_database_and_login_leaves_a_parallel_runs_database_rows_and_login_as_they_were | PASS |
| run-cleanup-check.ts: a failing regression leaves none of its users able to sign in, reports the original error, closes its pools, touches only its own users | Iso.The_cleanup_refuses_a_database_that_is_not_a_test_database + Iso.Dropping_one_runs_… | PASS (the property; the failpoint/hang matrix of the four legacy regression scripts is N/A: there are no such scripts and no run users in a shared database — each test class drops its own database and login) |
| reg-deactivate-check.ts: reg-users-deactivate.ts changes exactly the reviewed manifest, refuses a changed plan, leaves a running test's user | — | N/A: there is no deactivation tool in the new program (Insurance.Tools has test-users and users-inventory only), because tests no longer create users in a shared database |
| pool-end-check.mjs: postgres.js pool end() with a timeout returns with a request running | — | N/A: a postgres.js behaviour; ADO.NET connections are disposed per unit of work and the test databases are dropped with `rollback immediate` |
| snapshot-consistency-check.ts: db-snapshot backup files match their fingerprint under concurrent writes | — | N/A: db-snapshot.ts is not ported (no backup command in Insurance.Tools); SQL Server's own BACKUP DATABASE is transactionally consistent |

## Totals

PASS 86 · N/A 12 · NOT TESTED 0 · OTHER GROUP 3 (one per table row above; rows marked "same" share a test but are counted as their own case).

No defect was found: every case behaves on SQL Server as the legacy scripts expected.

## OTHER GROUP rows after the accounting port

| Row | Test | Verdict |
|---|---|---|
| answer-parallel-check.ts: parallel presses of the close-month form each show their own answer; periods unchanged | Web: Reports.LegacyPeriodsHttpTests.Presses_of_close_month_at_once_each_show_their_own_answer_whatever_answer_cookies_the_browser_holds (docs/legacy-db-checks-accounting.md) | PASS |
| permissions-db-check.ts: the period close's month series (PR250) | Accounting.LegacyMonthCloseTests.PR250_a_first_policy_from_mid_month_cannot_step_over_a_month_that_is_still_open (docs/legacy-db-checks-accounting.md) | PASS |
