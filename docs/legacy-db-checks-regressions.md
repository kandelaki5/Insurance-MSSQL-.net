# Legacy real-database checks: the regression scripts without a case table

Eight `db-check` scripts of the old program (legacy commit `17c1f1f`) had been ported, but their cases were not counted one by
one in any `docs/legacy-db-checks-*.md` file. This table maps every case each script records (its `record(…)` / `skip(…)` lines, in
the script's own order and with its own labels) to the xUnit test that proves it on a real SQL Server (a fresh `ins_it_*` database
per test class, the application connecting as a member of `core_app` only), each test read against the case; cases that no test
covered were ported now (marked **new**).

Verdicts: **PASS** — a test runs it on real SQL Server and passes; **N/A** — the case checks something that does not exist in the new
program (with the reason); **NOT TESTED** — it could not be run (with the reason). N/A and NOT TESTED never count as PASS.

Test classes: in `tests/Insurance.Integration.Tests` — `ClaimPaymentTests` (CP), `ClaimRegistrationTests` (CR), `ClaimConcurrencyTests`
(CC), `LegacyClaimPayeeRegressionTests` (Payee, new), `LegacyClaimOffsetReversalTests` (Off, new), `FailureInjection/LegacyLossEventFailureTests`
(LEF, new), `FailureInjection/ClaimFaultTests` (CF); in `tests/Insurance.Integration.Tests/Policies` — `AdvanceTransferTests` (Adv),
`PaymentConcurrencyTests` (PC), `EndorsementTests` (End), `EndorsementClosedMonthTests` (EndC), `PaymentRegressionTests` (PR); in
`tests/Insurance.Web.Tests` (HTTP against the real application) — `SettingsTests` (Set), `Policies/GapsPaymentPageTests` (Pay).

The legacy scripts ran on the shared `insurance_test` / `insurance_close_test` with tagged rows and the real day; the tests use their
own databases and fixed fictional days (a year of their own where a month is closed), the amounts and rules of the scripts unchanged.

## scripts/advance-allocation-regression.ts (owner 05.10.2026, Codex H21)

| Case | Test | Verdict |
|---|---|---|
| A: before — receipt unallocated (available 365.00), policy owes 365.00 | Adv.A_a_whole_receipt_settles_every_instalment_oldest_first_booked_today_at_the_receipt_rate | PASS |
| A: allocateReceipt 365 → advance=allocated gel=36500 left=0; available 0.00; owed 0.00; no new receipt | same | PASS |
| A: allocation lines — oldest first, booked today (Tbilisi), receipt-day rate, author recorded | same | PASS |
| A: the receipt itself is unchanged | same | PASS |
| A: history — as of the receipt month's end the policy still owed 365.00 | same | PASS |
| A: the same request again — nothing-left, still 4 lines | same | PASS |
| B: 91.25 → 365: allocated gel=9125 left=0; owed 273.75 | Adv.B_C_D_E_F_part_remainder_another_client_viewer_reversed | PASS |
| C: 500 received, owes 365: allocated 36500 left 13500; owed 0.00; available 135.00 | same | PASS |
| C: the remaining 135 to the client's second policy: left 0; owed 230.00; instalments 91.25 / 43.75 / 0 / 0 | same | PASS |
| C: a receipt to a policy that owes nothing: nothing-owed, nothing written | same | PASS |
| D: a receipt paid by another client: not-found; available 50.00; owed 365.00 | same | PASS |
| E: a viewer: read-only, nothing written | same (the transfer too) | PASS |
| F: a reversed receipt: nothing-left; owed 365.00 | same | PASS |
| G: one receipt to two policies at the same moment: one allocated, the other nothing-left; 365 in all | Adv.G_one_receipt_to_two_policies_or_twice_at_the_same_moment_is_allocated_once | PASS |
| G: the same request twice at the same moment: one allocated, one nothing-left; owed 273.75 | same | PASS |
| R: allocation and reversal of one receipt at once — allocate first / reverse first | PC.An_allocation_and_a_reversal_of_one_receipt_at_once (theory: both orders, forced by a held receipt lock) | PASS |
| X: USD policy, GEL receipt at the receipt-day rate, booked today; owed 273.75 USD | Adv.X_a_usd_policy_takes_a_gel_receipt_at_the_receipt_days_rate (the legacy SKIP when no NBG rate is stored does not arise: the test stores its rates) | PASS |
| history: a receipt in a closed month, allocated in the open month — old month unchanged, current balances reduced (legacy SKIP: NOT TESTED, a month could not be closed) | Pay.Scenario_3_on_the_legacy_days_September_and_October_closed_today_03_11_… — the receipt of 07.09, September and October closed by the real close, allocated on 03.11, booked 03.11; October's report as before | PASS |
| H: database — more than the receipt: PA002 | Adv.H_the_database_refuses_more_than_the_receipt_an_allocation_from_a_reversed_one_and_one_booked_in_a_closed_month | PASS |
| H: database — allocation from a reversed receipt: PA001 | same | PASS |
| H: database — allocation booked in a closed month: 23514, the month not reopened | same (**new** assertion) | PASS |
| H: nothing from the refused inserts: available 50.00, owed 365.00 | same | PASS |

## scripts/claim-payee-regression.ts (stage 4 item 7, PR #45; Codex H62/H63/H64)

All fourteen in one chain on one claim, as the script ran them; each refusal compares the claim's payments, reserve, the policy's
allocations and the claim's status before and after (the script's `ledgers()`).

| Case | Test | Verdict |
|---|---|---|
| K1 workshop, no ID number: refused on payee_id_number, no payment row | Payee.K1_to_K14_… (**new**; also CP.A_third_payee_comes_from_the_register_…) | PASS |
| K2 workshop, an ID number not in the register: refused, no payment row | same | PASS |
| K3 workshop, the service centre's ID: paid 300.00 naming the service centre | same | PASS |
| K4 policyholder (unchanged path): paid 200.00 naming the client | same | PASS |
| K5 workshop under sanctions: refused (payee blocked), every ledger unchanged | same | PASS |
| K6 workshop with an FMS suspension in force: refused, unchanged | same | PASS |
| K7 workshop whose suspension has run out: paid 100.00 to it | same | PASS |
| K8 "other" payee found by a foreign registration number: paid to that party | same | PASS |
| K9 "other" payee who is the policyholder by ID: paid to the client | same | PASS |
| K10 policyholder, the payment dated in the closed month: refused on paid_on, nothing written | same | PASS |
| K11 the policyholder under an FMS suspension: the AML message, unchanged | same | PASS |
| K12 the policyholder under sanctions and the payee: refused, unchanged | same | PASS |
| K13 the policyholder under sanctions, a clean workshop: refused, unchanged | same | PASS |
| K14 both under sanctions: refused, unchanged | same | PASS |

## scripts/endorsement-regression.ts (acceptance row AG-C3; docs/04 r10, 8a, r14)

| Case | Test | Verdict |
|---|---|---|
| E1 +100 GEL: a new pending policy (same contract, from the change date to the original's end), one line +100, one instalment of 100 on the change date; the original untouched | End.E1_plus_100_is_a_new_pending_policy_and_approved_books_on_the_change_date | PASS |
| E1 approved: active, one issue row +100 booked on the change date, cover change – end; the original still owes 365 | same | PASS |
| E2 −100, then approved: owes 365 until approval, then 265; premium −100 | End.E2_minus_100_reduces_the_original_only_when_approved | PASS |
| E3 −400 on a 365 schedule: approval refused, nothing booked, the change stays pending | End.E3_a_reduction_larger_than_the_schedule_is_not_approved | PASS |
| E5 a policy starting in a closed month changed −100 from a day in it: cover and rate as asked; premium and reduction on the first open day; nothing in the closed month | EndC.E5_E6_a_change_of_a_policy_starting_in_a_closed_month_is_booked_on_the_first_open_day_once | PASS |
| E6 the approval pressed again: already, nothing more written | same | PASS |
| E7 a fully (365) / partly (91.25) paid original changed −100, approved: schedule 265, what was paid kept (owed −100 / 173.75) | End.E7_a_paid_or_partly_paid_original_changed_minus_100_keeps_what_was_paid (theory) | PASS |
| E4 +100 USD valued at the change day's rate, not the original's | End.E4_a_usd_change_is_valued_at_the_change_days_rate | PASS |
| E8a a viewer calls endorsePolicy directly: read-only, nothing written | End.E8_roles_and_identifiers_on_the_actions_themselves | PASS |
| E8b sales makes the change; sales and the viewer call approve directly: forbidden, nothing written | same | PASS |
| E8c malformed and unknown ids, a change of a pending policy: refused, nothing written | same | PASS |
| E8d operations_head approves, presses again: already | same | PASS |
| E9 "equally over the unpaid instalments", first paid, −100: 57.91 / 57.92 / 57.92 | End.E9_E10_equal_over_the_unpaid_instalments_and_an_overpaid_remainder | PASS |
| E10 the same, three paid, −100: the unpaid one goes, 8.75 off a paid one, overpaid | same | PASS |

## scripts/loss-event-check.ts (docs/07 §10; Codex INS-MHL-LOSS-EVENT-FIRST-STAGE, H51–H56)

| Case | Test | Verdict |
|---|---|---|
| backfill: one event per old claim, numbered as the claim, ledgers unchanged (`--before` / `--after` around migration 0028) | — | N/A: the PostgreSQL upgrade path from the schema before 0028; the new schema is built whole with loss events, old data comes by `import-postgres` (DataMigrationTests) |
| A one event over damage + equipment (5.1) + assistance (14): one event, three claims, count by class 2 | CR.One_registration_over_three_coverages_is_one_event_with_claims_N_N2_N3_and_two_classes | PASS |
| B one coverage over its limit: nothing written, no number taken | CR.One_coverage_over_its_limit_writes_nothing_and_takes_no_number | PASS |
| C the same form twice in a row and at the same moment: one event | CR.The_same_form_twice_in_a_row_or_at_the_same_moment_registers_one_event | PASS |
| D a claim on another policy's event (LE001); a coverage twice in one event (database and "add coverage") | CR.A_claim_on_another_policys_event_and_a_coverage_twice_in_one_event_are_refused | PASS |
| E 2000 (damage) + 500 (equipment) + 150 (assistance), deductible 200 on both 5.1: 200 withheld once | CP.One_class_deductible_in_an_event_2000_withholds_200_500_none_150_its_own_50_net_2450 — the 5.1 part exactly (200 once); the test gives assistance (class 14) a deductible of its own (50), the legacy none, so its net total is 2 400.00 there against the legacy 2 450.00 | PASS (5.1 rule); the assistance difference is the fixture's |
| F two payments at the same moment on two coverages of one class: the deductible withheld once | CC.Two_payments_on_one_class_in_one_event_wait_for_the_event_and_withhold_its_deductible_once (+ its negative control without the lock) | PASS |
| G deductibles 200 and 50 in one class: both payments refused, nothing written | CP.Different_deductibles_of_one_class_in_an_event_refuse_both_payments | PASS |
| H a coverage added later joins its class (0 more withheld) | CP.Partial_payments_out_of_order_withhold_200_in_all_and_a_coverage_added_later_joins_its_class | PASS |
| J1 200 / 200, 100 + 100: withheld 200, net 0 | CP.J1_to_J4_small_and_single_payments_withhold_the_class_deductible_at_most_once (**new**, theory) | PASS |
| J2 three 5.1 coverages, 50 + 50 + 50: withheld 150, net 0 | same | PASS |
| J3 / J4 a single 100 / 500, deductible 200: 100 / 0 and 200 / 300 | same | PASS |
| J5 paid in parts out of order (120, 80, equipment 500, 800): withheld 200, net 1300 | CP.Partial_payments_out_of_order_… | PASS |
| K damage 500 paid (200), closed → reopened → rejected; equipment 500: its payment unchanged, 0 more, 200 / 800 in all | CP.A_claim_closed_or_rejected_after_its_payment_keeps_the_deductible_it_withheld (theory: closed; closed → reopened → rejected) | PASS |
| L0 one payment of this run waits, a foreign request waits too (a control that the harness counts only its own waiters) | — | N/A: a check of the legacy harness's backend identification (pg_blocking_pids); the tests hold the lock on a connection of their own and see their own tasks wait |
| L1 / L2 100 + 100 and 150 + 150 at the same moment, the event held: both wait, then 200 once (net 0 / 100) | CC.L1_L2_two_small_payments_at_the_same_moment_wait_for_the_held_event_then_withhold_200_once (**new**, theory) | PASS |
| L3 a payment cancelled while it waits for the event lock writes nothing; sent again it withholds as usual; the foreign request left alone | LEF.L3_a_payment_whose_event_lock_fails_writes_nothing_sent_again_it_withholds_as_usual (**new**: the event-lock statement fails once through the interceptor; the cancelling of a waiting backend and "the foreign request" are the legacy harness's) | PASS |
| M a read-only (viewer) user calls register, add coverage, change reserve, pay and close directly: each refused before writing | CP.A_read_only_user_calling_the_actions_directly_writes_nothing (events, claims' reserves, payments, reserve requests and the claim's status compared; the claim number counter is not compared — the registration is refused before the number is taken, CR.One_coverage_over_its_limit_… shows the counter rule) | PASS |
| N equipment 100 paid while damage 100 was paid before (200 / 200); an error after its payment row: nothing stays (ids included); the retry: 2 payments, 200 withheld, net 0, paid; the repeat writes nothing | LEF.N_equipment_100_paid_while_damage_100_was_paid_before_… (**new**; the reserve insert fails through the interceptor after the payment row was sent; also CF.Small_first_claim_…) | PASS |
| N-cleanup the holder lets go and the request settles, whatever the cancel gives | — | N/A: the legacy harness's own liveness check (a held SHARE lock and pg_cancel_backend); the interceptor holds nothing |
| O an ordinary claim with the premium set-off: an error after the receipt, its allocation and the payment row leaves nothing; the retry: receipt claim_offset 405.00, two allocations 202.50 earliest first, 1000 / 200 / 405 / 395 linked, reserve −1000, claim paid, policy and schedule unchanged; the repeat writes nothing | LEF.O_P_…(totalLoss: false) (**new**) | PASS |
| P a total loss with all unpaid premium set off: the same, the policy not cancelled, to its end | LEF.O_P_…(totalLoss: true) (**new**) | PASS |
| O / P pass rule on reserve histories and against altered copies of the outcome | — | N/A: checks of the legacy script's own pass function; the tests assert exact rows (a wrong row fails the assertion itself) |
| Q1 damage 1000 with the set-off (200 / 200, owes 405): 1000 / 200 / 405 / 395, one receipt 405, two allocations 202.50 | LEF.Q_two_coverages_of_one_class_paid_with_the_set_off_… (**new**) | PASS |
| Q2 equipment 500 with the set-off asked: an error after its payment row (no receipt or allocation on this path); the first untouched; the retry 500 / 0 / 0 / 500 without a receipt, reserve −500, both paid, 1500 / 200 / 405 / 895 in all; the repeat writes nothing | same | PASS |
| Q pass rule against altered copies | — | N/A: the legacy script's own pass function, as above |
| I the original single-coverage form: one claim numbered as its event | CR.The_original_single_coverage_form_still_registers_one_claim_numbered_as_its_event | PASS |

## scripts/number-settings-check.ts (Codex H48/H49, PR #35; owner 08.10.2026)

| Case | Test | Verdict |
|---|---|---|
| race: a number taken (its transaction open) while 2 digits are saved (counter 99 → 100): refused (fit), digits 3, counter 100 | Set.Number_series_refuse_a_format_that_cannot_fit_reaches_a_taken_number_overlaps_or_meets_another_kind (with its negative control: an unlocked read still sees 99) | PASS |
| history: PFX11 exists; the series asked to become PFX + 2 digits: refused (taken), prefix stays | same | PASS |
| overlap: two policy series set to the same prefix and digits in one form: refused, neither changes | same | PASS |
| kinds: the complaint series set to the policies' prefix (same digits; other digits), a format writing the same numbers, both moved to one prefix in one form: refused (kind, other = the policy series), audit +0 | same (all four) | PASS |
| kinds: a prefix no other kind uses, then back: saved twice | same | PASS |
| ordinary: one series gets a new prefix typed in small letters: saved upper-cased, digits 6, counter 15, one audit row by the admin | same | PASS |

## scripts/payment-regression.ts (UAT D1, review H20)

| Case | Test | Verdict |
|---|---|---|
| A: approved through approvePolicy: one issue row 365.00 | PR.A_two_partial_payments_settle_the_first_instalment_oldest_first | PASS |
| A: recordPayment 73.00: saved, owed 292.00, the first instalment 73.00 | same | PASS |
| A: recordPayment 18.25: saved, owed 273.75, the first instalment 91.25 | same | PASS |
| A: allocation dates the Tbilisi day of entry, receipt dates kept | same | PASS |
| A: exactly two receipts (73.00, 18.25), one line each on the first instalment | same | PASS |
| B: recordPayment 365.00: nothing owed | PR.B_full_payment_cancellation_and_one_refund | PASS |
| B: cancelPolicy on day 181: one cancellation −184.00; owed −184.00 | same | PASS |
| B: refundOverpayment, then again: one refund −184.00, the second adds nothing; net 181.00; owed 0 | same | PASS |
| audit: the approvals and the cancellation in the audit trail (≥ 3 policy updates over A and B) | PR.A_… (A's approval, **new** assertion) and PR.B_… (B's approval and cancellation, ≥ 2) | PASS |

## scripts/schedule-asof-regression.ts (Codex H23; docs/04 rule 8)

| Case | Test | Verdict |
|---|---|---|
| H23 scenario skipped when the cancellation day is not in the future | — | N/A: the tests put the cancellation 74 days after today by construction, so the case always runs |
| P1: cancelPolicy from a future day: one adjustment −91.00 booked on that day | PR.H23_a_cancellation_from_a_future_day_counts_from_that_day_only | PASS |
| P1: debt by day: today 273.75, the day before 273.75, the day 182.75 | same | PASS |
| P1: recordPayment 273.75 today: allocated in full (paid=0); allocations 365.00; today 0.00, from the day −91.00 | same | PASS |
| P2: refundOverpayment before the cancellation day: nothing refunded; today 0.00 | PR.H23_a_refund_before_the_cancellation_day_pays_back_nothing | PASS |
| P3: allocateReceipt 273.75 after the cancellation: allocated 27375, left 0; today 0.00 | PR.H23_an_earlier_receipt_allocated_after_the_cancellation_covers_todays_debt | PASS |
| P5: skipped without the CASCO product | — | N/A: the product is part of the migrated schema |
| P5: endorsement −50 from the future day, approved: premium −50.00 and the original's −50.00 booked that day | PR.H23_an_approved_reduction_from_a_future_day_counts_from_that_day_in_card_payment_and_report | PASS |
| P5: the original's debt by day: 273.75 / 273.75 / 223.75 | same | PASS |
| P5: the report's month-end receivable: this month unchanged, the change's month −50.00 | same | PASS |
| P5: recordPayment 273.75 today: paid=0, today 0.00, from the day −50.00 | same | PASS |
| P4: a cancellation from a past day counts today: retained 182.00, debt 90.75 | PR.H23_a_cancellation_from_a_past_day_counts_today | PASS |

## scripts/claim-offset-reversal-regression.ts (docs/07 §4.3a, found 08.10.2026)

| Case | Test | Verdict |
|---|---|---|
| O0 fixture: 365.00 unpaid, claim 1 000.00 approved, 100.00 paid with the set-off — claim payment set-off 100.00 net 0.00, a claim_offset receipt of 100.00 | Off.O0_a_set_off_of_100_O1_its_receipt_reversed_directly_writes_nothing_O2_an_ordinary_receipt_of_50_still_reverses (**new**, through the real payClaim) | PASS |
| O1 the set-off receipt reversed by a direct request: no reversal; payments, allocations, the claim payment, reserve and status unchanged; the outstanding stays | same (also FdRequestTests.A_claim_set_off_is_not_reversed_here, on a directly written set-off receipt) | PASS |
| O2 an ordinary bank receipt of 50 on the same policy reversed: −50.00 with mirror lines summing −50.00 | same | PASS |

## Totals

| Script | PASS | N/A | NOT TESTED |
|---|---:|---:|---:|
| advance-allocation-regression | 22 | 0 | 0 |
| claim-payee-regression | 14 | 0 | 0 |
| endorsement-regression | 14 | 0 | 0 |
| loss-event-check | 22 | 5 | 0 |
| number-settings-check | 6 | 0 | 0 |
| payment-regression | 9 | 0 | 0 |
| schedule-asof-regression | 10 | 2 | 0 |
| claim-offset-reversal-regression | 3 | 0 | 0 |
| **All** | **100** | **7** | **0** |

No defect was found by these ports: every new case behaved on SQL Server as the legacy script expected on its first run.
