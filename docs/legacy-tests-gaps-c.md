# Legacy test gaps, group C: the National Bank answer, the rates form, the date field, form 6, facultative XL, impairment debtors

This table maps every case of the legacy tests of this group (Insurance-Software commit `17c1f1f`, `scripts/integration/*.test.ts`)
and the server action `loadRates` to the test of the new program that proves it, or says why it does not apply. Kinds of evidence are
kept apart: **DB** — `tests/Insurance.Integration.Tests` on a real SQL Server (a fresh `ins_it_*` database per test); **HTTP** — the
real application over HTTP on a fresh test database (`tests/Insurance.Web.Tests`); **Browser** — the real application served by
Kestrel and driven by headless Chromium (Playwright); **Source** — a scan of the page sources. **N/A** — the case checks something
that does not exist in the new program (reason given); **NOT TESTED** — could not be run (reason given). N/A and NOT TESTED never
count as PASS. No test calls the real National Bank: the bank is `AnsweringNbg` (an in-process `IHttpClientFactory` whose handler
records every request and answers it from the test) or `RefusingNbg`.

New test files (all passing):

| File | Kind |
|---|---|
| `tests/Insurance.Integration.Tests/Worlds/AnsweringNbg.cs` | the fake bank (the bank's JSON format, with the fields the bank also sends) |
| `tests/Insurance.Integration.Tests/Reports/NbgRateCacheTests.cs` | DB |
| `tests/Insurance.Web.Tests/LoadRatesActionTests.cs` | HTTP |
| `tests/Insurance.Web.Tests/DateInputTypingTests.cs` | Browser |
| `tests/Insurance.Web.Tests/TreatyFormDateTests.cs` | HTTP + Source |
| `tests/Insurance.Web.Tests/Reports/Form6FixesTests.cs` | HTTP (page and xlsx download) |
| `tests/Insurance.Integration.Tests/Reports/FacultativeXlTests.cs` | DB |
| `tests/Insurance.Integration.Tests/Reports/ImpairmentDebtorsOrderTests.cs` | DB |

## nbg-rate-cache.test.ts

| Legacy case | New test | Verdict |
|---|---|---|
| the first read of USD 31.07: 3.1415, after one request with the endpoint, the date, USD and EUR, no cache | `NbgRateCacheTests.The_first_read_of_USD_31_07_is_3_1415_after_one_request_with_the_endpoint_the_date_USD_and_EUR` (one GET to the exact URL) | PASS (DB). The fetch option `cache: 'no-store'` is N/A: `HttpClient` keeps no HTTP cache |
| the cache write: one upsert of exactly USD 3.141500 and EUR 3.500000 for 31.07 — no GBP, no zero rate or quantity | `The_cache_write_is_exactly_USD_3_141500_and_EUR_3_500000_for_31_07_no_GBP_no_zero_rate_or_quantity` (the rows of `core.exchange_rate`) | PASS (DB). The legacy statement texts (SELECT / INSERT … ON CONFLICT) are N/A: T-SQL `merge` |
| read again: USD 3.1415 and EUR 3.5 from the cache alone — no further request, no further write | `Read_again_USD_and_EUR_come_from_the_cache_alone_no_further_request_no_further_write` (bank set to throw; fingerprint of the tables unchanged) | PASS (DB) |
| the July report from the cache: 31.00 USD × 3.1415 = 97.39 GEL; nothing else in it; no request, no write | `The_July_report_from_the_cache_31_00_USD_at_3_1415_is_97_39_GEL_nothing_else_no_request_no_write`; also `The_July_report_on_an_empty_cache_loads_the_rate_once_itself_and_values_at_it` | PASS (DB) |
| the earlier cache rows and the treaty are unchanged | `The_earlier_cache_rows_and_the_treaty_are_unchanged` | PASS (DB) |
| the dedicated stub and model refuse what they do not know | — | N/A: self-checks of the legacy fake database; the new tests use the real SQL Server |
| the model's upsert on a stored currency and date replaces its rate | `Loading_a_stored_currency_and_date_again_replaces_its_rate_and_adds_the_missing_one` (through the bank answer and through `StoreAsync`) | PASS (DB) |
| no socket was opened | every test asserts the exact list of requests the in-process handler received | N/A as stated (sockets); the equivalent — no request leaves the process — holds by construction of `AnsweringNbg` |
| (added) only decoys, an empty day, `[]`, `[{}]` | `An_answer_with_only_decoys_or_no_currencies_stores_nothing_and_the_read_stays_empty` | PASS (DB) |
| (added) error status, broken JSON, no connection, time-out | `A_failing_bank_an_error_status_a_broken_answer_or_no_connection_gives_false_and_stores_nothing` | PASS (DB) |

## reference/rates/actions.ts — `loadRates` → `Pages/Reference/Rates` `OnPostAsync`

| Legacy behaviour | New test (`LoadRatesActionTests`) | Verdict |
|---|---|---|
| a writer's valid date loaded and stored, `{ ok: rates.loaded }`, the page revalidated | `A_writers_date_is_loaded_stored_answered_loaded_and_shown_in_the_table` (admin, accountant: the request, the rows, the table 3.1415 / 3.5000, the date kept; the same date again replaces the rate) | PASS (HTTP) |
| `loadNbgRates` false: a day without rates → `{ error: rates.failed }` | `A_day_without_rates_is_answered_failed_and_stores_nothing` | PASS (HTTP) |
| `loadNbgRates` false: the bank failing → `{ error: rates.failed }` | `A_failing_bank_is_answered_failed_and_stores_nothing` (503, not JSON, no connection, time-out) | PASS (HTTP) |
| a date not matching `^\d{4}-\d{2}-\d{2}$` → `{ error: rates.failed }`, the bank not asked | `A_date_that_is_not_yyyy_mm_dd_is_answered_failed_without_asking_the_bank` | PASS (HTTP) |
| `!canWrite` → `{ error: common.readOnly }`; the page renders no form for a viewer | `A_viewer_sees_no_form_and_a_post_of_it_loads_nothing` (no form; a hand-made post asks nothing of the bank and stores nothing); also `BrowserFoundationTests.A_viewer_sees_no_rate_loading_form` | PASS (HTTP). The readOnly text itself is not observable in either program (the old form component that would show it is not rendered for a viewer; the new page shows messages only inside the writer's block) |
| a well-formed but impossible date (e.g. `2026-02-30`) | — | N/A, a known difference: the old action passed it to the bank and PostgreSQL would have refused the insert; the new action answers `rates.failed` without a request |

## date-input-typing.test.ts (Browser, `DateInputTypingTests`, on `/parties/new` birth date and `/reference/rates`)

| Legacy case | New test | Verdict |
|---|---|---|
| 14102026 typed digit by digit | `BrowserFoundationTests.The_date_field_is_typed_as_day_month_year_and_posts_the_iso_date` (existing) | PASS (Browser) |
| 05012026 the same way: 05/01/2026, 2026-01-05 | `_05012026_typed_digit_by_digit_reads_05_01_2026_and_the_form_gets_2026_01_05` (each step, the caret, the values passed on) | PASS (Browser) |
| a whole date pasted, with slashes, dots, dashes or one-digit day and month; 29 February only in a leap year | `A_whole_date_pasted_with_slashes_dots_dashes_or_one_digit_day_and_month_29_February_only_in_a_leap_year` (select all + one insert) | PASS (Browser) |
| Backspace from the end down to empty; empty sends nothing and says nothing | `Backspace_from_the_end_one_key_at_a_time_down_to_empty_…` | PASS (Browser) |
| changed in the middle: month selected and typed over; Delete before the month; the caret stays | `Changed_in_the_middle_the_month_selected_and_typed_over_and_Delete_before_the_month_…` | PASS (Browser) |
| Backspace after the first slash and Delete before it: the text stays, the caret at 2 | `Backspace_after_the_first_slash_and_Delete_before_it_the_text_stays_14_10_2026_and_the_caret_stays_at_2` | PASS (Browser; the real caret, which the old test could only model) |
| the calendar gives an ISO date | `The_calendar_gives_an_ISO_date_shown_as_dd_mm_yyyy_the_same_ISO_sent_and_passed_on` (the button calls `showPicker`; the picker's value set by Playwright) | PASS (Browser). The drawn calendar itself: NOT TESTED (headless Chromium draws none) |
| min and max: too early / too late naming the bound | `Min_and_max_a_day_before_says_too_early_after_says_too_late_each_naming_the_bound_as_dd_mm_yyyy` (1900/2100 bounds; the rates page's max = today stops the form) | PASS (Browser) |
| a date the server sends back replaces what is typed; name, required and disabled reach the fields | `A_date_the_server_sends_back_replaces_what_is_typed` (a refused save turns typed 5/1/1985 into 05/01/1985; a reload drops a half-typed value and its message); `The_name_required_and_disabled_reach_the_fields_as_given` (rates: name on the hidden field, required on the text field; a viewer's client card: all four parts disabled, not posted) | PASS (Browser) |

## treaty-form-date.test.ts

| Legacy case | New test (`TreatyFormDateTests`) | Verdict |
|---|---|---|
| period_from and period_to show 14/10/2026 and 14/10/2027 and send the ISO dates under their own names | `Period_from_and_period_to_show_dd_mm_yyyy_and_send_the_ISO_date_under_their_own_names` (a refused treaty rendered back; the fresh form's year) | PASS (HTTP) |
| every date field of the forms: date-only ones in the shared field, the three date-and-time ones apart | `Every_rendered_form_has_its_date_only_fields_in_the_shared_field_and_only_the_three_date_and_time_fields_apart` (24 pages rendered over fictional rows: every native date field is a nameless picker, every shared field's parts agree, each page's date names listed; datetime-local only `suspicion_at`, `str_sent_at`, `instructed_at`); `Every_date_field_of_the_page_sources_…` (all `.cshtml`: no literal date field, every helper asked for `"date"` routes it to `<date-input>`, the three datetime-local fields, the shared fields per page) | PASS (HTTP + Source). Difference: the old treaty form used a generic helper; the new page writes its two periods with `<date-input>` directly, and its field helper is never asked for a date (checked) |

## form6-fixes.test.ts

| Legacy case | New test | Verdict |
|---|---|---|
| 2 classes × 2 reinsurers at one sort_order: 2 groups in code order | `Form6FixesTests.Two_classes_by_two_reinsurers_at_one_sort_order_two_groups_in_code_order_each_with_its_own_two` (file and page) | PASS (HTTP) |
| 5 × 2 tied: 5 groups, 200 | `Five_by_two_tied_five_groups_the_file_given_no_missing_cell` | PASS (HTTP) |
| 6 × 5 tied fills the template exactly; the page offers it | `Six_by_five_tied_fills_the_template_exactly_and_the_page_offers_it` | PASS (HTTP) |
| 7 classes tied: the seventh below the grid | `Seven_classes_tied_the_seventh_in_the_row_added_below_the_grid_the_file_given_and_offered` | PASS (HTTP) |
| 9 facultative treaties on 05.1 with 9 periods: 9 rows, 5 + 4 below, page and file | `Nine_facultative_treaties_on_5_1_with_nine_periods_…` (real placements on a 5.1 line) | PASS (HTTP) |
| quarterEnd: the forms' years exactly; 0000–0099 and outside 1900–2100 refused | `Form6Tests.Quarters` (existing) | PASS (unit) |
| the GET: 0099-Q4 → 400, nothing read; 1900-Q1 → its own file | `RegulatoryPagesTests.Another_role_is_sent_home_or_refused_…` (existing) | PASS (HTTP); "nothing read" is N/A (call instrumentation of the legacy stub) |
| the page: a refused quarter falls back to the month's own; never 1999 | `The_page_given_quarter_0099_Q4_falls_back_to_the_months_own_and_never_shows_1999` (new); `RegulatoryPagesTests.Form_6_on_the_page_…` (2026-Q5, existing) | PASS (HTTP) |

Negative control: with the class code removed from form 6's row order (`Form6.Form6Rows`), the 2 × 2 test fails on the page (rows
interleaved 11 A, 12 A, 11 B, 12 B); the file stays grouped because `Form6Groups` groups by class as well.

## facultative-xl.test.ts

| Legacy case | New test | Verdict |
|---|---|---|
| an active XL on the parent class 5 in force over the policy: xol-with-proportional, no INSERT tried | `FacultativeXlTests.An_active_XL_on_the_parent_class_5_in_force_over_the_policy_with_all_five_decoys_refuses_and_writes_nothing`; `ReinsuranceChainTests` theory row `parent` | PASS (DB) |
| without it, all five XL that do not cover this policy together: placed, one INSERT of 109.50 | `Without_it_all_five_XL_that_do_not_cover_this_policy_together_the_placement_is_made_once_109_50` (every column of the one cession row; nothing else changed) | PASS (DB) |
| each alone — closed / other class / ended 30.06.2026 / from 01.07.2027 / other policy (3.2) | `ReinsuranceChainTests.A_facultative_placement_and_the_excess_of_loss_treaties_around_the_policy` (existing theory) | PASS (DB); its "other class" and "another policy" rows both use class 13; the new together-test uses the legacy 4 and 3.2 |
| no socket was opened | — | N/A (no network in the placement) |

## impairment-debtors.test.ts

| Legacy case | New test | Verdict |
|---|---|---|
| the full report on 31.12.2026: four rows, the mixed policy under client once | `ImpairmentReportTests.Four_policies_four_debtors_each_row_on_its_own_and_each_policy_once_in_the_totals` (existing) | PASS (DB) |
| the totals add up (world and variant) | existing test (world); `ImpairmentDebtorsOrderTests` theory (both) | PASS (DB) |
| the report's three reads: text, parameter, raw rows | — | N/A: statement instrumentation of the legacy stub (as in the coverage table) |
| the variant: the mixed policy under broker once | `ImpairmentReportTests.The_variant_puts_the_mixed_policy_under_broker_once_with_no_reserve` (existing) | PASS (DB) |
| no socket was opened and nothing was fetched | — | N/A |
| eight orders in all | `ImpairmentDebtorsOrderTests.Eight_orders_in_all_the_six_of_three_kinds_and_the_two_of_two` | PASS (unit) |
| each of the eight orders: the same totals, the mixed policy once under the first of client → broker → agent | `ImpairmentDebtorsOrderTests.In_each_order_the_same_totals_the_mixed_policy_once_under_the_first_of_client_broker_agent` (8 rows) | PASS (DB + the report's own `TotalsOf`/`KindOf`). SQL Server returns the kinds sorted (`string_agg … within group (order by …)`), so the order is varied on the real report's mixed row, not in the read — the old test also reordered the read's answer artificially. `ImpairmentReport.TotalsOf` was made public for this |
| the advance of 150.00: unallocated / allocated late, December and January (3 cases) | `ImpairmentReportTests.An_advance_changes_no_report_until_allocated_and_its_late_allocation_changes_January_never_December` (existing) | PASS (DB) |
| the broker pays its whole schedule on 30.06.2026 | `ImpairmentReportTests.The_broker_paying_its_whole_schedule_lowers_no_other_policys_debt` (existing) | PASS (DB) |
| a reduction of −184.00 as a change policy; booked late on 15.10.2026 | `ImpairmentReportTests.A_reduction_issued_as_a_change_policy_lowers_the_policy_it_changes_and_has_no_row_of_its_own` (existing, both) | PASS (DB) |

Negative control: with `KindOf` taking the last kind of the list, five of the eight order rows fail (the three orders ending in
the first-ranking kind pass, as expected).

## Defects

None found: every new test passed on the code as it was. The one production change is visibility only
(`ImpairmentReport.TotalsOf` private → public).
