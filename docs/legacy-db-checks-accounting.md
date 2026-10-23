# Legacy real-database checks: month close, monthly report, financial director's panel

The legacy `npm test` never touched a real database; the scripts below were its only real-database evidence, and two of them
(`month-report-precision-db-check`, `fd-month-report-db-check`) were "PREPARED, NOT RUN" — on SQL Server they are real evidence for the
first time. This table maps every numbered case of the accounting scripts (legacy commit `17c1f1f`) to the xUnit test that now proves it
on a real SQL Server (a fresh `ins_it_*` database per test or test class; the application connecting as a member of `core_app` only), or
says why it does not apply. Scenario numbers, amounts and days are the legacy scripts' own, except where an adaptation is stated.

Verdicts: **PASS** — a test runs it on real SQL Server and passes; **N/A** — the case checks something that does not exist in the new
program (with the reason); **NOT TESTED** — it could not be run (with the reason). NOT TESTED never counts as PASS.

Test classes: in `tests/Insurance.Integration.Tests/Accounting` — `LegacyMonthCloseTests` (MC), `LegacyMonthReportPrecisionTests` (MRP, over
the cases of `MonthReportCases.cs` / `MonthReportCases.Steps.cs` with the independent oracle `MonthReportOracle.cs`), `LegacyFdMonthReportTests`
(FdM); in `tests/Insurance.Integration.Tests/Reports` — `TreatySeriesExhaustedTests` (TS) and the earlier `PeriodCloseConcurrencyTests`,
`PremiumReportPrecisionTests`, `PremiumReportCessionTests`, `PremiumReportClaimsTests`, `ReportParityTests`; in `tests/Insurance.Web.Tests/Reports`
(HTTP against the real application) — `LegacyPeriodsHttpTests` (PH), `LegacyMonthReportPagesTests` (MRW, the same cases read through the
report page and the panel) and the earlier `PeriodsPageTests`.

Closing a month is irreversible: every test that closes one has a database of its own (MC and PH create one per test). No closed month is
reopened and no trigger is bypassed to build a fixture — a closed month is made only by the real close (`PeriodClose.CloseAsync`).

## scripts/close-period-regression.ts (Codex H25, H28, H30)

| Case | Test | Verdict |
|---|---|---|
| Operator presses close of the oldest open month: forbidden + month; periods unchanged | MC.Close_period_regression_every_press_answered_refusals_and_a_repeat_write_nothing_and_twenty_presses_get_their_own (also PeriodsPageTests.The_roles_starting_set_… over HTTP) | PASS |
| Viewer presses close: forbidden; periods unchanged | same | PASS |
| Accountant presses "2026-00-01", "2026-13-01", "2026-02-30", "0000-01-01", "2101-01-01", "abc", "": invalid, no month; periods unchanged | same | PASS |
| Accountant presses the current month: not-ended + month | same | PASS |
| Accountant presses the month after the oldest open one: earlier-open | same | PASS |
| Accountant presses a month closed before: already; its closing time and author as they were | same (the closed month made by the real close in the test's own database) | PASS |
| 20 presses by the accountant at once, alternating the current month and "2026-13-01": 10 × not-ended, 10 × invalid, each to its own press; periods unchanged | same (20 concurrent `PeriodClose.CloseAsync`, each on its own pooled connection) | PASS |
| Accountant closes the oldest open month (done) — legacy SKIP "irreversible on the shared insurance_test" | same (own database): done, closed by the accountant with a closing time | PASS |
| Run users and sessions deleted | — | N/A: no shared database; the test's database and login are dropped |

## scripts/p6-close-test.ts (the real close end to end; legacy database insurance_close_test)

| Case | Test | Verdict |
|---|---|---|
| ENV: a separate database with nothing closed | MC.P6_a_real_month_close_end_to_end (its own database; no period before the run, asserted) | PASS |
| ENV: "25 migrations up to 0025" | — | N/A: the schema is the final legacy state in 0001–0008, applied by the runner (SchemaTests checks the journal) |
| Fixture: a policy from M−3, 365.00 in four 91.25, approved by the head through the real approval; a receipt of 91.25 dated M−3's 5th, unallocated | same (`PolicyActions.ApprovePolicyAsync` answers done) | PASS |
| C9 operator presses close of M−3: forbidden, M−3 stays open | same | PASS |
| C3 accountant closes the current month: not-ended, no row | same | PASS |
| C4 accountant closes M−2 while M−3 is open: earlier-open, M−2 stays open | same | PASS |
| C1 accountant closes M−3: done; closed, by the accountant, with a closing time | same | PASS |
| C2 the same close again: already; closing time and author exactly as in C1 | same | PASS |
| C5 two real transactions close M−2 at once while a third session holds the period lock: both seen waiting on it (`sys.dm_tran_locks` APPLICATION/WAIT = 2, in place of `pg_locks`), one done, one already, one audit row | same (also PeriodCloseConcurrencyTests.Two_closes_of_one_month_at_once_close_it_once) | PASS |
| C6a core_app writes a premium movement dated in closed M−3: 23514 | same | PASS |
| C6b a payment received in closed M−3 through the real RecordPayment: the form answers on the date field (PeriodClosed); no new receipt | same | PASS |
| C7 (P7) the receipt that arrived in closed M−3 allocated now (real AllocateReceipt): every line booked today, 91.25 in all; the receipt keeps its date; no new receipt; M−3 unchanged | same | PASS |
| C8 app delete 42501; reopen 23514; owner delete, new date, new closing time, new author 23514; an update that changes nothing passes; M−3 exactly as closed | same (also SchemaTests.Movement_in_a_closed_month_is_refused_and_the_month_cannot_reopen) | PASS |
| C11a the audit of the closes: two inserts, M−3 and M−2, by the accountant, status closed; refused changes left nothing | same | PASS |
| C11b a setting saved 5, 3, 3, 8 through the real settings form: insert ∅→5, update 5→3, update 3→8, each by the admin; saving 3 again wrote nothing | PH.P6_C11b_a_setting_saved_5_3_3_8_is_audited_insert_then_two_updates_each_by_the_admin (also SettingsTests) | PASS |
| Code version recorded at the start and the end | — | N/A: a legacy run harness; the tests run on the built commit |

## scripts/address-guard-periods-check.ts (Codex H26, H28, H29, H30)

| Case | Test | Verdict |
|---|---|---|
| Typed addresses (`close=__proto__ / constructor / toString / hasOwnProperty / nonsense`, repeated keys, every answer code, with the open, the closed and impossible months, a forged `ack`): 200, no answer shown | PH.A_close_month_answer_is_never_taken_from_the_address_and_months_that_cannot_close_are_answered_and_closed_by_nobody (also PeriodsPageTests.Which_month_and_once_…) | PASS |
| The real form read from the admin's page (its month the oldest open one, M−2) | same | PASS |
| Operator presses close of the oldest open month (no JavaScript): forbidden, no cookie set | same | PASS |
| Admin presses the current month: not-ended, no cookie | same | PASS |
| Admin presses "2026-13-01" and "0000-01-01": invalid, no cookie | same | PASS |
| Admin presses a closed month: already, no cookie — legacy SKIP when insurance_test had none | same (M−3 closed by the real close in the site's own database) | PASS |
| Accounting periods unchanged by the run | same | PASS |
| Run users and sessions deleted | — | N/A: the site's database is dropped |

## scripts/answer-parallel-check.ts (Codex H30, PR #23)

The accountant's browser is played by raw requests carrying its session cookie, the anti-forgery cookie and the form token of its own
page, and the extra `answer-0…answer-7` cookies the legacy browser held; each response's Set-Cookie is applied as it arrives and a redirect
(as the earlier versions answered) would be followed with them.

| Case | Test | Verdict |
|---|---|---|
| 2 presses at once, 0 answer cookies held: each shows its own answer | PH.Presses_of_close_month_at_once_each_show_their_own_answer_whatever_answer_cookies_the_browser_holds | PASS |
| 2 presses at once, 7 answer cookies held | same | PASS |
| 2 presses at once, 8 answer cookies held | same | PASS |
| 20 presses at once, 0 held | same (every response 200 with exactly its own answer and no Set-Cookie) | PASS |
| Accounting periods unchanged by the run | same | PASS |

## scripts/month-report-precision-db-check.ts (cases of scripts/integration/month-report-cases.ts) — PREPARED, NOT RUN in the old program

The claim that the accounting slice's parity tests already covered this check was verified case by case (column "before"): the earlier
tests covered most single figures (PremiumReportPrecisionTests, PremiumReportCessionTests, PremiumReportClaimsTests, ReportParityTests over
generated ordinary worlds), but none read the report page or the panel per case, none ran R7/R8/R9 as one world, and R1's and R5's next
month, R6's approval chain and the row counts were not checked. So every case is ported as written, each in its own database, each step
read through the report (MRP) and through the report, the page as an accountant and the panel as an admin (MRW), every page figure parsed
back from its text exactly. The legacy "before its fixtures" read of a shared copy is empty here (a database of its own), so what a case
adds is the month itself.

Adaptations: the new program has no clock to set and books an allocation today. R4, R5, R9 record their receipt through the real
`PaymentActions.RecordPaymentAsync`, so their first month m0 is the current month and the receipt day d0 is its 20th (or today before the
20th); the line is expected booked today. The other cases keep the planned months (October and November 2026), and their pinned texts
(PINNED_2026_10) are asserted too. R6's receipt, advance allocation and reversal request are written as the rows those actions wrote on
d0 = 20.10 and d1 = 03.11; its approval is the real `ApproveReceiptReversalRequestAsync` (which books the mirror on the request's day).

| Case | Before (earlier tests) | Test | Verdict |
|---|---|---|---|
| R1 90071992547408.01 and 9999999999999999.99 in one class, unpaid: rows as stored; m0 and m1 class, totals, GEL owed and overdue (l's 0.01 due on m1's first not overdue in m0); written = earned + UPR over m0 and m0+m1; pinned texts | m0 only, report only (PremiumReportPrecisionTests.The_largest_issues_…, Two_of_the_largest_…) | MRP.Each_case_adds_exactly_its_own_figures_to_both_months("R1") + MRW.Each_case_on_the_report_page_and_the_panel_exactly("R1") | PASS |
| R2 one issue of 90071992547408.01: m0's UPR …436.37 to the tetri, written = earned + UPR; pinned | report only (Earned_and_UPR_exactly_…) | MRP("R2") + MRW("R2") | PASS |
| R3 a 1.00 issue cancelled from m1-15 (the cancellation's row written before its original's), then undone on m1-20: m1 −0.88 / 0.04 / 0.00, undone 0.00 / 0.09 / 0.83; m0's whole report unchanged after each; the issue as first stored | report figures only (A_linked_cancellation_…) | MRP("R3") + MRW("R3") | PASS |
| R4 one tetri: 100.00 received (real RecordPayment, ?paid=0) against 100.00 + 0.01, and 5.00 due on m0's last day: receipt and line as stored; m0 owed 5.01 none overdue, m1 5.01 overdue | rows, not the action (One_tetri_owed_…) | MRP("R4") + MRW("R4") | PASS |
| R5 USD 100.00 at 2.7: 27.00 GEL received at the day's 2.7 = 10.00 USD (real RecordPayment); owed 90.00 at m0's end 2.8 = 252.00 and m1's end 2.9 = 261.00, the rate shown "2.8000 (dd.mm.yyyy)" | m0 only, rows (USD_receivable_is_revalued_…) | MRP("R5") + MRW("R5") | PASS |
| R6 a receipt of 10 000.01 (a's 10 000.00, 0.01 an advance), b's advance on d1, the reversal requested: m0 a settled / b's 0.01 owed not overdue; m1 nothing owed while waiting; the real approval failing after the mirror's first line (a test-only trigger, dropped after) writes nothing, still nothing owed; approved: one mirror on the request's day by the admin with both lines negated exactly; again: already; m1 owes 10 000.01, overdue; m0's whole report unchanged throughout | rows, no request or approval (A_reversed_receipt_brings_the_debt_back_…) | MRP("R6") + MRW("R6") | PASS |
| R6 b's payment page offering the advance, allocateReceipt from b's own form, reversePayment above the threshold (the actions' own rows) | — | Policies: LegacyPremiumPrecisionTests.Cross_policy_advance_and_the_receipts_reversal_above_the_threshold("B" … "D2") (docs/legacy-db-checks-policies.md) | PASS |
| R7 the rest of the money in one world, every figure not zero: claims paid (90071992547408.01 gross with a deductible, 100.00 USD at its own 2.7, 0.01 between two 50 % shares, 600.00 to an XL), RBNS at the month-end rate, IBNR 5 % of the net, recovery, the reinsurer's paid and RBNS (100 %, 30 % USD, 50 + 50 %, XL), ceded premium with 20 % commission, two XL treaty premiums (GEL and USD); m1 with C2's payment reversed at its own rate; m0's whole report unchanged | each kind alone (PremiumReportClaimsTests, PremiumReportCessionTests), not this world, not on the page | MRP("R7") + MRW("R7") | PASS |
| R8 four issues ceded (365.00 at 40 %, 25 %; the largest two and 0.10 at 100 %, 20 %), cancelled from m0-16 with each cession's cancellation linked to the one it cancels (written before their originals), undone on m1-10: ceded, ceded UPR, commission, IBNR at each stage; every cession read back as stored; identity over m0+m1; m0 unchanged by the undo | one cession (A_partial_correction_read_before_its_cession_…) | MRP("R8") + MRW("R8") | PASS |
| R9 EUR: 365.00 and 90071992547408.75 at the stored 3.2; 31.00 GEL received at the day's 3.1 = 10.00 EUR (real RecordPayment); a claim paid 100.01 at its own 3.05 (305.03), reversed in m1; reserve, the reinsurer's 30 % shares and two EUR XL treaty premiums at each month end's own rate (2.9876, 3.3456); the receivable "2.9876 (dd.mm.yyyy)" | not covered (EUR only in other reports' tests) | MRP("R9") + MRW("R9") | PASS |
| The check's rows per table exactly the cases' own (`counts`) | — | every case ends with it (CountsAsync: every row of the 21 tables in its database is the case's) | PASS |
| The National Bank asked 0 times | — | MRW asserts the site's refusing NBG client received no request; MRP reads stored rates only | PASS |
| Every row of the copy that is not the check's own unchanged (footprint fingerprint) | — | — | N/A: each case has a database of its own holding only its rows (the counts row above); the read-writes-nothing fingerprint is FdM's |
| Run guards: confirm by name, MONTH_REPORT_CHECK_DB, host allowlist, reviewed-source fingerprint, two open months, free class, rates not overwritten, sessions by token hash, child processes, wire encoding of BigInt, exit codes | — | — | N/A: the legacy run's harness; tests create their own database and read the report in-process (BigInteger end to end) |

## scripts/fd-month-report-db-check.ts (scripts/integration/month-report-scenario.ts) — PREPARED, NOT RUN in the old program

`FdPanelTests.The_month_report_figures_by_hand` (earlier) checked a different, smaller world by hand; the scenario's own checks were not
covered, so it is ported as written: M = the current month, N = the next, class K = 2 (no line yet), GEL and USD (stored 2.654321 on M's
1st and 2.712345 on N's), in a database that also holds rows of class 5.1 in GEL and EUR from the month before M, a claim on them, rows of
them in M and N, and that month closed by the real close before the scenario's rows.

| Case | Test | Verdict |
|---|---|---|
| Each of the eight reads writes nothing (every ledger's count and checksum, the change log's last id, the journal, just before and just after) | FdM.Two_open_months_of_one_new_class_the_figures_by_hand_everything_else_as_before_and_reading_writes_nothing | PASS |
| Class K has no figures before the rows | same | PASS |
| M: class K issued 2, written 730.00, earned 2 × days × 1.00, UPR, ceded 0; claims paid 200.00 gross (not the 190.00 net), RBNS 300.00 + 100.00 USD at the month-end rate, IBNR 36.50, recovered 30.00 | same | PASS |
| M: every other class and claims row, the treaty premium and the totals but K as before | same | PASS |
| M: GEL receivable change 265.00 / 82.50 overdue; USD 146.00 / 146.00 at the month-end rate; the USD rate and date the last stored on or before the month's end; other currencies (EUR) as before | same | PASS |
| M read twice: the same report | same | PASS |
| M read again after N's movements: the very same report | same | PASS |
| N: class K issued 1 (H), earned 3 × days, UPR; claims paid 100.00, RBNS 200.00 + 150.00 USD, IBNR 54.75 (18.25 across a year end), recovered 10.00; GEL 547.50 owed and overdue; USD at N's rate; everything else as before | same | PASS |
| N: the pending claim payment request (70.00) is no payment, the pending premium refund request (10.00) no movement of the receivable | same | PASS |
| The last closed month's report the very same before and after both months' rows — legacy NOT TESTED when none is closed | same (the month before M closed by the real close) | PASS |
| Prerequisites (journal, tables), the server is insurance_test as core_app, admin / free class / USD rate fixtures | — | N/A: built by the real migrations in the test's own database; the reads run as the application role |
| One rolled-back transaction, nothing left after it, fingerprint after the rollback, the connection closes | — | N/A: no rollback is needed; the test's database is dropped |
| The National Bank not asked | — | N/A: the report reads the stored rates only (ReportWorld.StoredRates has no National Bank client) |

## scripts/permissions-db-check.ts — the period close's month series (PR250)

| Case | Test | Verdict |
|---|---|---|
| From the raw day (2026-09-15) the series to 2026-11-01 skips October, from the 1st it does not | MC.PR250_a_first_policy_from_mid_month_cannot_step_over_a_month_that_is_still_open: the raw-day series computed in the test holds only M−3; the real close with a first policy from M−3's 15th, M−3 closed, answers earlier-open for M−1 (M−2 open), then closes M−2 and M−1 in order | PASS |

## scripts/number-limit-check.ts — the createTreaty row

| Case | Test | Verdict |
|---|---|---|
| createTreaty (facultative, 10 %, class 5.1) with the "treaty" series run out: the series-exhausted message, no treaty or class row, counters unchanged | TS.CreateTreaty_with_the_treaty_series_run_out_writes_nothing_and_moves_no_counter (negative control: the same form with numbers left creates the treaty with its class) | PASS |

## Totals

PASS 58 · N/A 9 · NOT TESTED 0 (one per table row above; rows marked "same" share a test but are counted as their own case).

No defect was found: every case behaves on SQL Server as the legacy scripts expected, and the oracles of the two "PREPARED, NOT RUN"
checks matched the new program's figures to the tetri on their first run. A negative check of the harness itself: changing one expected
tetri (R7's treaty premium) made MRP fail with the exact figure.

## Open questions

None new. The adaptations above (the receipt month of R4/R5/R9; R6's earlier steps as rows) follow from the new program having no
settable clock, not from a business rule.
