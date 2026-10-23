# Codex review findings still open at baseline 17c1f1f, checked against the rewrite

Read-only research, 23.10.2026. Old program: `kandelaki5/Insurance-Software` at `17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37` (head of PR #358, `regulatory-form6`). Rewrite: `this repository`, branch `claude/sleepy-curie-81jkry` (head `d419fee`).

## Sources read

- Local Codex copies (03–04.10): `legacy Codex/findings-status.md`, `conflicts.md`, `claude-next-task.md`, `latest-review.md`, `review-state.json`, `security/report.md` + `findings.json`. Their own text says they are historical. The cloud context comment says so too: PR #19 [issuecomment-5986606796](https://github.com/kandelaki5/Insurance-Software/pull/19#issuecomment-5986606796), which `AGENTS.md` names as the current decisions record.
- GitHub, fetched with `gh api` (read only):
  - all 370 PRs;
  - 1,519 issue comments (about 860 are Codex's, from 03.10 to 22.10);
  - 41 PR reviews (18 are from the `chatgpt-codex-connector` bot, all on PRs ≤ #23);
  - 44 inline review comments.
- What was extracted:
  - 82 named finding IDs (`*_2026MMDD`);
  - H01–H112;
  - every Codex table row marked open, PARTIAL, remaining or NOT TESTED.
- Also read:
  - the latest Codex reviews: PR #354, #358 (the baseline review INS-PR358-FORM6-MAIN-CI-17C1F1F-20261020), #359–#368;
  - PR #369 and #370 (ZAZA notes; they have no comments);
  - legacy `docs/00-statusi.md` ("მფლობელს ელოდება", "კითხვები რევიუდან");
  - `docs/05-kitxvebi.md`, `docs/07-zaralis-chashla-saxeobebad.md` §4.3ა/§4.5;
  - `docs/regulaciebi/07-angarishgebis-formebi.md` §6.

## Result in one paragraph

Of the 82 named findings and H01–H112, almost all are closed in Codex's own later records ("fixed_verified_source_offline", "დახურულია", "CLOSED_*").

- **Open at the baseline (17c1f1f):** 28 items.
  - Owner questions that Codex keeps open, with the "მფლობელის გადაწყვეტილება" field empty or "unanswered": CLAIM-Q1, CLAIM-Q2, H07, H08, the different-deductible case 9.1/H50, H44-B1 and H44-B2.
  - H46: an owner-confirmed claim-currency design that was never built.
  - Technical acceptance items left as NOT TESTED or partial: H11 legacy rows, original items 12, 13, 18, 20, 22 and 24, real-DB acceptance.
  - Codex findings on PRs outside the baseline (#354, #361, #362, #364, #366).
- **Rewrite outcome for the 28 items:**
  - COVERED/FIXED: 1 (#12);
  - partly covered: 2 (#17, #19);
  - STILL OPEN: 16 (owner decision with the legacy behaviour kept: 10 — #1–5, 7, 8, 16, 22, 23; legacy gap kept for parity or NOT TESTED: 6 — #6, 11, 14, 15, 18, 21);
  - N/A: 9 (#9, 10, 13, 20, 24–28: legacy-only infrastructure, not in the baseline, or already decided by the owner).
- **Documentation error in CLAUDE-RESPONSE.md §6.3.** It says a claim-payment reversal (storno) does not check the FMS suspension or sanctions. The rewrite does check, exactly as legacy does: it refuses (`ClaimActions.cs:858, 874–877`, test `ClaimReversalTests.cs:232`).

## A. Findings open at the baseline and their state in the rewrite

| # | ID | Source | What it says | State at 17c1f1f | Rewrite |
|---|---|---|---|---|---|
| 1 | CLAIM-Q1 (H24 family) | PR #18 [5986315440](https://github.com/kandelaki5/Insurance-Software/pull/18#issuecomment-5986315440); `AGENTS.md`; docs/05 ა13ა | Ordinary claim set-off when the policy already has a later cancellation: deduct today's unpaid 273.75 or the final 182.75? | Unanswered by the owner; the code gives 182.75, which only counts as existing behaviour, not as a rule | **STILL OPEN — owner decision.** The legacy behaviour is kept; `MIGRATION-COVERAGE.md` §8 lists this as NOT TESTED. Asked in CLAUDE-RESPONSE §6.5 |
| 2 | CLAIM-Q2 | same; docs/05 ა13ბ | Total loss `all_unpaid` with an earlier future cancellation: undo the cancellation (273.75) or keep it (182.75)? | Deferred by the owner "until the contract is seen". Codex: do not re-ask every cycle | **STILL OPEN — owner decision.** Legacy behaviour kept. Asked in §6.5 together with Q1 |
| 3 | H07 | findings-status 06:15/16:15; docs/00 "კითხვები" q.8 | An XL treaty in one currency and a claim in another: which rate date and rounding apply to priority/limit? | Safe refusal: a mixed-currency XL is refused before insert; owner question open (Codex list "XL/FX") | **STILL OPEN — owner decision; the refusal is kept** (`src/Insurance.Domain/Finance/Reinsurance.cs:164` CurrencyMismatch; `tests/Insurance.Domain.Tests/Finance/ReinsuranceTests.cs:103`). **Not asked in §6** |
| 4 | H08 | findings-status; docs/00 q.9; PR #89 review | Several treaties on one risk: is XL applied to the gross or the retained loss? is surplus applied before or after QS? how do XL layers stack? | Proportional total ≤100% enforced. XL together with proportional, and two XLs, are refused; owner question open | **STILL OPEN — owner decision; the refusals are kept** (`Reinsurance.cs:165–169` XolLayers/OverCeded/XolWithProportional; `ReinsuranceTests.cs:81,94`; `tests/Insurance.Web.Tests/Reports/TreatyPagesTests.cs:97,108`). **Not asked in §6** |
| 5 | "ღია 9.1" / H50 | PR #244 [6205631566](https://github.com/kandelaki5/Insurance-Software/pull/244#issuecomment-6205631566), #245; docs/00 "გადადებული" | Two coverages of one class in one loss event with different deductibles (200/50): which deductible applies once? | Payment is stopped with a message; Codex: "სხვადასხვა ფრანშიზის არჩევანი/ღია 9.1 არ გადაწყვეტილა" | **STILL OPEN — owner decision; the stop is kept** (`ClaimActions.cs:705`, `ClaimTexts.DeductibleDiffers`; `tests/Insurance.Integration.Tests/ClaimPaymentTests.cs`). Asked in §6.4 |
| 6 | H46 | PR #33 reviews; Codex open list in PR #241–#245 ("H46/newGELmodel") | A new claim in GEL on a USD policy breaks the same-currency rule of payment and set-off. The owner-confirmed design (`docs/07` §4.5: claim in GEL, set-off in policy currency at the payment-day rate, `offset_rate`/`offset_currency`) is not built | Design only; code keeps claim currency = policy currency | **STILL OPEN — legacy gap kept for parity.** The claim takes the policy's currency (`ClaimActions.cs:103,221`). Not mentioned in the rewrite's docs |
| 7 | H44-B1 | PR #33 review; docs/07 §4.3ა ზ; docs/00 q.3 | How a reversed claim payment above the AML threshold appears in the AML report (variants 1/2/3) | Interim variant 1 (the original stays an operation) | **STILL OPEN — owner decision; variant 1 is kept** (`src/Insurance.Application/Services/AmlReport.cs:170`). **Not asked in §6** |
| 8 | H44-B2 | same | Should a claim-payment reversal be stopped by an FMS suspension or sanctions on the client or payee? | Interim rule: refuse and write 0 rows (`legacy claims/actions.ts:779–837`) | **STILL OPEN — owner decision. The refusal is kept and tested** (`ClaimActions.cs:858,874–877`; `ClaimReversalTests.cs:232`). **But CLAUDE-RESPONSE §6.3 says "ახლა ... არ მოწმდება" (not checked), which is wrong. Fix the wording** |
| 9 | H09 | findings-status 16:15; PR #19 context | Audit-derived history and backfill of old unapproved reductions cannot be replayed | "Accepted legacy limitation", not fixed | **N/A** — no PostgreSQL upgrade path; `import-postgres` copies the rows as they are (`docs/MIGRATION.md`) |
| 10 | H10 | same | A legacy closed month blocks the 0020 backfill trigger on upgrade | "Support deferred" | **N/A** — the rewrite has its own fresh schema; 2 upgrade db-check scripts are N/A in `MIGRATION-COVERAGE.md` |
| 11 | H11 (legacy rows) | findings-status; PR #2/#3; claude-next-task FIN-REVIEW-20261003-H01-H03 | Old FMS suspension rows with `legacy_unmapped` (written only by 0022): attach/confirm behaviour and the DB upgrade proof | Partially accepted; DB acceptance of legacy rows remained | **STILL OPEN — NOT TESTED** (1 case in `MIGRATION-COVERAGE.md` §8; a fixture would bypass a trigger). The schema supports it: `database/migrations/0005_compliance.sql:149–150`. The new-target behaviour is covered: `tests/Insurance.Integration.Tests/AmlSuspensionAcceptanceTests.cs` (H11 tests with a negative control) |
| 12 | H04/H06, RI/AML two-session concurrency | claude-next-task.md; review-state.json `pending_areas` | Prove with two real DB sessions: approve vs write on one policy, period close vs write, RI cap, suspension vs a payment in flight | Codex never ran it; the author's evidence only | **COVERED** on real SQL Server with negative controls (`tests/Insurance.Integration.Tests/ConcurrencyTests.cs`, `ClaimConcurrencyTests.cs`, `AmlSuspensionAcceptanceTests.cs:81,99`; CLAUDE-RESPONSE §3) |
| 13 | H37 | PR #29 | Equal-frequency (2 s) comparison of the old/new parallel-users probe | NOT TESTED | **N/A** — legacy test harness (`TestDatabaseIsolationTests.cs` covers isolation) |
| 14 | Original #12 (AML/CDD) | `2026-10-03-chatgpt.md`; findings-status 05:15 | `risk_level` is required at issue, but there is no full CDD/sanctions process audit | Partial | **STILL OPEN — gap kept for parity** (no legal/AML process acceptance in the rewrite either) |
| 15 | Original #13 | same | Capital and mandatory notifications matrix | Deferred | **STILL OPEN — gap kept** (capital/Solvency forms not built in legacy or rewrite; only reserves placement, N1 and Form 6) |
| 16 | Original #14 + "approvalroles" | same; PR #237–#245 rows "სხვა მოქმედებების granular როლები — დარჩენილი ფარგლები" | Full role matrix and two-person approval. Advance/transfer/reverse/refund/endorse/cancel still use the broad `canWrite` | Partially answered (docs/05 ვ); the rest is owner-dependent | **STILL OPEN — owner decision, legacy guards kept** (permission catalogue ported; `canWrite` paths unchanged) |
| 17 | Original #18 + AG-T6 / 9.5 | findings-status; docs/00 "მფლობელს ელოდება" q.4 | Real backup → restore evidence; storage place, retention, RPO/RTO | No confirmations given | **PARTLY COVERED** — backup → restore → fingerprint is tested (`tests/Insurance.Integration.Tests/BackupRestoreTests.cs`, `fingerprint` tool). The RPO/RTO/storage owner decision is still open |
| 18 | Original #20 | same | Metric dictionary and reconciliation with official forms | Deferred | **STILL OPEN** (same as legacy) |
| 19 | Original #22 | same | Historic-data migration dry run, control totals, rollback | Deferred; docs/00 lists migration questions 9.13–9.21 and "two lines of the same class in the Avtograph file" ⏳ | **PARTLY COVERED** — PG→MSSQL `import-postgres` + `reconcile` (`tests/Insurance.Integration.Tests/DataMigrationTests.cs`, 76/76 on fictional data). A run on the real export is NOT TESTED. Migration of the company's history (Avtograph) is still open |
| 20 | Original #24 | same | CI/branch protection not provable (main unprotected, rulesets 403) | Partial | **N/A / NOT TESTED** — the rewrite's CI has never run (CLAUDE-RESPONSE §5) |
| 21 | Original #10 | same | Full GL, forms, close reconciliation | Deferred (also ZAZA-003) | **STILL OPEN — out of scope in both** |
| 22 | LEGACY-READ-SCOPE-PR295 / PR296 | PR #298; docs/05 ვ8 | `/accounting/periods`, `/accounting/premium-report` and `/compliance/sanctions` can be read by any signed-in user | Owner deferred "until the company meeting" | **STILL OPEN — owner decision; behaviour kept** (`Pages/Accounting/Periods.cshtml.cs`, `PremiumReport.cshtml.cs` GET with no role gate; `Compliance/Sanctions/Index.cshtml.cs` writes only with `CanWrite`) |
| 23 | "underwriter" / "XL" in Codex's open list | PR #241–#245 | Underwriter approval of non-standard terms ("standard conditions" undefined); XL premium distribution to policies (weight, "above $50 000", commission by results) | Owner open (docs/05 ვ9, ვ10); not built | **STILL OPEN — owner decision; not built in either** |
| 24 | PR364_A_RUN_EXECUTION_EVIDENCE_UNVERIFIED_20261020, PR364_STOP_AFTER_FAILURE_NOT_OBSERVED_20261021 | PR #364 [6305859512](https://github.com/kandelaki5/Insurance-Software/pull/364#issuecomment-6305859512) | The historical Stage A DB copy run lacks verified logs; it continued after STOP | Open (owner disposition) | **N/A** — legacy DB-run evidence |
| 25 | PR354_V2_PACKAGE_SAFETY_SCOPE_OVERSTATED_20261022 | PR #354 [6346774057](https://github.com/kandelaki5/Insurance-Software/pull/354#issuecomment-6346774057) | The v2 run package overstates "0 writes" / role restore | Open (raised after the baseline) | **N/A** — legacy run package |
| 26 | PR366 income statement (segment choice open, negative earned key, multi-class XL split, deposit XL, bank deferred N1-8) | PR #366, #358 [6297777706](https://github.com/kandelaki5/Insurance-Software/pull/358#issuecomment-6297777706), #363 | Form 1 IS work and its owner questions | Open PR, **not in 17c1f1f** | ~~**N/A for this baseline** — the rewrite has no income statement. If #366 is merged later it needs porting~~ (state at `d419fee`). **Now PORTED** (`602ffbc`, IncomeStatementTests 15, IncomeStatementReaderTests 10, IncomeStatementPageTests 3); the owner's answers of 20–21.10 implemented, see §D |
| 27 | PR361/PR362 (precision guard, RI commission settlement, demo 0039) | PR #361, #362 | Findings fixed inside those PRs | Open PRs, not in 17c1f1f (baseline migrations end at 0038) | ~~**N/A** — not in the rewrite; port on merge~~ (state at `d419fee`). **Now PORTED**: #361 `afc43e7` (ImpairmentPrecisionGuardTests), #362 `e915643` (migration 0011, RiCommissionTests, RiCommissionSettlementTests, RiCommissionPageTests); the demo 0039 run stays N/A (legacy PostgreSQL run evidence), see §D |
| 28 | C01 | `Codex/conflicts.md` ("პროდუქტის მფლობელის გადაწყვეტილება:" empty) | When to split the business-operations layer | The local file is stale: the owner chose (b) on 05.10 (docs/00 "კითხვები" q.6; PR #19 context "C01 shared helper გადაწყვეტილია") | **N/A** — closed by the owner; the rewrite has an `Application/Services` layer |

Closed at the baseline per Codex (checked, not repeated here): H01–H06, H12–H36, H38–H45, H47–H112. Of the 82 named IDs, every one that predates PR #364 is closed (for example PR224-*, PR225-CORRECTION-LIST, receipt/claim permission activation, the month-report and policy-card decimal-precision series, PR349/351/358/363 findings). The Codex security review (2 medium findings: login throttling, sessions after an admin's own password reset) is closed in legacy, and the rewrite keeps the fix (`Services/Auth.cs` `login_attempt`; `Pages/Users/Index.cshtml.cs:96,122`).

## B. Owner decisions still empty in Codex's records or docs, and whether CLAUDE-RESPONSE §6 asks them

| Decision | Where it is recorded open | In §6? |
|---|---|---|
| CLAIM-Q1 / CLAIM-Q2 (273.75 vs 182.75) | `AGENTS.md`; PR #19 context; docs/05 ა13 | Yes, §6.5 |
| Different deductibles of one class in one event (9.1/H50) | docs/00 "გადადებული"; Codex PR #244 | Yes, §6.4 |
| Claim-payment reversal under suspension/sanctions (B2) | docs/07 §4.3ა ზ | Yes, §6.3, **but it misstates current behaviour (it is refused, not unchecked)** |
| **AML report treatment of a reversed claim payment (B1)** | docs/07 §4.3ა ზ; docs/00 q.3 | **No** |
| **XL in another currency: rate date and rounding (H07)** | docs/00 "კითხვები" q.8; Codex list "XL/FX" | **No** |
| **Several treaties on one risk: XL gross vs net, surplus, layers (H08)** | docs/00 q.9 | **No** |
| **XL premium distribution to policies (weight, "$50 000", commission)** | docs/05 ვ9 | **No** |
| **Underwriter "standard conditions" and approval order** | docs/05 ვ10; Codex list "underwriter" | **No** |
| **Refund to the client: refund-day rate or original-payment rate** (docs/05 ა12; refund-day is Claude's own unconfirmed assumption; rewrite `PaymentActions.cs` SettleRefundAsync uses the refund day) | docs/05 ა12; ZAZA-007 | **No** |
| **Three pages readable by every signed-in user** (deferred until the company meeting) | docs/05 ვ8 | **No** (deferred by the owner, so mention it rather than ask) |
| **Remaining role matrix**: who sees the client risk profile; granular rights for advance/transfer/reverse/refund/endorse/cancel | docs/00 "კითხვები" q.4; Codex PR #237 row | **No** |
| **Claim payee**: must an "other" payee be in the register; must a bank/leasing beneficiary receive the payment (q.2ბ/გ) | docs/00 "მფლობელს ელოდება" q.2 | **No** (no answer found in docs/05) |
| **Identical numbers across document kinds (rule 2)** | docs/00 "მფლობელს ელოდება" q.1 | **No** (refused today; Codex recommends keeping that) |
| **Recovery confirmations AG-T6, RPO/RTO, storage (9.5)** | docs/00 q.4 | **No** |
| **Regulatory questions 2–12** (statistical premium with cancellation, change policy counted as new, 100% reinsured, net sum insured, claim counts, recourse/IBNR in form 2, XL deposit premium split, IBNR within the year, reinsurer settlement, accounting data source, court cases/history) **and the other split classes 1, 3, 6, 9, 11, 15, 17; docs/05 ბ2ა** | `docs/regulaciebi/07-angarishgebis-formebi.md` §6, §7 | **No** |
| №58 order on own server; pilot test environment | docs/05 დ1, ვ8 | No (legal/organisational) |
| C01 | Codex/conflicts.md field empty | Not needed: the owner decided on 05.10 |

§6 also asks three decisions that come from the rewrite itself, not from Codex: §6.1 (verifyPassword accepts any password on a broken hash), §6.2 (approval after the month closed, PR #212) and §6.6 (re-screening an earlier false match).

## C. Note on PR #369/#370 (Zaza, 22.10, after the baseline, not Codex)

- ZAZA-001…009 are new and have no team answer.
- ZAZA-007 repeats items B (Q1/Q2, refund rate, XL, standard conditions).
- ZAZA-008 (money handled as `Number`/`Math.round` in claims/cancel/reinsurance writes) is kept in the rewrite for parity: `ClaimActions.cs:407–409` uses `double` for limit/deductible and `JsRound`.
- ZAZA-009 security points:
  - per-page auth (no middleware): same as legacy;
  - `secure` cookie: `SameAsRequest` / `IsHttps` in `Program.cs:31`, `RequestUser.cs:27`;
  - upload parser: ExcelDataReader for .xls.
  These were not Codex findings at the baseline.

## D. Update after Codex INS-DOTNET-MSSQL-003 (23.10.2026, evening)

The table rows above are the state at `d419fee` and are not rewritten; what changed since, with the commit and its test:

| Item | Then | Now | Commit | Evidence |
|---|---|---|---|---|
| 6 — H46 (GEL claim on a USD/EUR policy) | STILL OPEN, not built | **Built** as the owner confirmed it (docs/07 §4.5): company setting `claim.currency` (gel by default, policy for the old behaviour), migration 0012, set-off at the payment day's rate rounded down to the cent. Cases no rule decides are refused, nothing written (total loss across currencies, two currencies in one event, XL in another currency) | `015028e` | I/ClaimGelOnForeignPolicyTests (9, negative control: 8 fail when a new claim takes the policy's currency), W/ClaimGelCardTests, D/CrossCurrencySetOffTests |
| §6.1 — verifyPassword on a damaged hash (Codex R01) | rewrite decision asked | **Fixed**: only `scrypt$<32 hex>$<128 hex>` is verified | `d08f031` | PasswordTests, CommonParityTests (6 divergences listed), AuthTests |
| §6.2 — approval after the month closed (PR #212) | rewrite decision asked | **Built** as #212 (owner): refused with „შეუძლებელია პოლისის დამტკიცება, თვე დახურულია“ under the period lock | `4a073ea` | ApprovalClosedMonthTests, FdRequestClosedMonthTests |
| §6.6 — re-screening an earlier false match | rewrite decision asked | **Built** (owner, 23.10.2026): a reasoned dismissal is reused on an unchanged subject, list and hits; never lifts a block | `f90d73b` | SanctionsReuseTests (9), ScreeningFingerprintTests |
| ZAZA-009 `secure` cookie | SameAsRequest | Secure outside Development, plain HTTP refused there, forwarded headers only from configured proxies (Codex R03) | `f51a693` | HostingTests (4) |
| Open-PR capabilities #207/#218/#225/#211/#214/#215, #361, #350, #362, #366 | not in the baseline | **Built** (MIGRATION-COVERAGE §9) | `4a073ea`, `afc43e7`, `e915643`, `602ffbc` | named there |

Still open, unchanged: CLAIM-Q1/Q2 (DEFERRED), the different-deductibles-within-one-class sub-case (OPEN), H07, H08, H44-B1, H44-B2, the role matrix and the other owner questions of section B.
