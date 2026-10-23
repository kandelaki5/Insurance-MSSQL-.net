# შესაძლებლობების გადატანის რუკა (MIGRATION-COVERAGE) — INS-DOTNET-MSSQL-001

ძველი პროგრამა: Insurance-Software, commit `17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37`. ახალი: ეს რეპოზიტორია, ბრანჩი `claude/sleepy-curie-81jkry`.

ამ ფაილში ძველი პროგრამის ყოველი ნაწილი ერთხელ მაინც არის ჩამოთვლილი: სად არის ის ახალ კოდში, რა ამოწმებს მას და რა სტატუსი აქვს. ცხრილის უჯრები ინგლისურადაა, რადგან ფაილების, ტესტებისა და ფუნქციების სახელებს შეიცავს. სტატუსები ასე იკითხება:
- **DONE / PORTED / PASS / MAPPED** — გადატანილია და ტესტით შემოწმებულია.
- **N/A** — ახალ პროგრამაში ასეთი რამ აღარ არსებობს. მაგალითად, ძველი სატესტო ინფრასტრუქტურა ან PostgreSQL-ის განახლების გზა. მიზეზი ყველგან მითითებულია.
- **NOT TESTED** — შემოწმება ვერ გაეშვა, მიზეზი მითითებულია. NOT TESTED და SKIP **არასოდეს** ითვლება გავლილად.

შემოწმების ოთხი სახე ცალ-ცალკეა:
- **unit** — `tests/Insurance.Domain.Tests`, ბაზის გარეშე, მათ შორის ძველ TypeScript კოდთან შედარების შემთხვევები;
- **SQL Server** — `tests/Insurance.Integration.Tests`, ყოველ ტესტ-კლასს თავისი ახალი `ins_it_*` ბაზა აქვს;
- **HTTP** — `tests/Insurance.Web.Tests`, ნამდვილი ვებპროგრამა ნამდვილ ბაზაზე;
- **browser** — Playwright და Chromium.

ბოლო სრული გაშვება (`602ffbc2`, INS-DOTNET-MSSQL-003): `dotnet test` — 1926 გავიდა (unit 595, SQL Server 986, HTTP/browser 345), 0 ჩავარდა, 0 გამოტოვდა. (1784 — საწყისი გადაწერის ბოლო გაშვება; ქვემოთ §1–§7 იმ მდგომარეობას აღწერს, §9 — ღია PR-ებს.)

## მოკლედ

| რა | რამდენი | სტატუსი |
|---|---:|---|
| ეკრანები და მისამართები (ეკრანები, Excel-ის ჩამოტვირთვები, ბეჭდვა, შესვლა) | 54 ძველი + `/logout` | ყველა DONE, ტესტით |
| სერვერული მოქმედებები და ჩამოტვირთვის handler-ები | 64 | ყველა DONE, ტესტით |
| მოდულები (`src/finance`, `src/compliance`, `src/lib`, `src/i18n`, კომპონენტები) | 111 | 80 DONE; 31 React-ის კომპონენტია, მისი markup Razor-ის გვერდშია (N/A, გვერდი შემოწმებულია) |
| ბაზის მიგრაციები | 37 + ღია PR-ების | საწყისი 37 ასახულია 8 ახალ ფაილში; შემდეგ დაემატა 0009–0012 (Codex R02, სანქციები, #362, H46) (პ. 5) |
| ძველი ტესტ-ფაილები | 161 (1 598 შემთხვევა) | 135 PORTED, 26 N/A (ძველი სატესტო ინფრასტრუქტურა), 0 ნაწილობრივ, 0 აკლია |
| ძველი რეალური ბაზის შემოწმების სკრიპტები | 45 | 43 ასახულია `docs/legacy-db-checks-*.md`-ში: PASS 364 (120 + 86 + 58 + 100; და 13 რიგი, რომელიც სხვა ნაწილის ტესტებით დამტკიცდა), N/A 43, NOT TESTED 1; 2 სკრიპტი N/A (PostgreSQL-ის ძველი სქემის განახლება) |
| სხვა სკრიპტები (ადმინისტრირება, სატესტო გარემო, მტკიცებულებები, დამხმარეები) | 162 | იხ. პ. 6 |
| ძველი პროგრამის ღია PR-ები (Codex INS-DOTNET-MSSQL-003) | 156 | PORTED 133, N/A 22, ნაწილობრივ ღია 1 (#363) — პ. 9 |

დეტალური ჩანაწერები შემთხვევების მიხედვით:
- `docs/legacy-db-checks-schema-claims.md`, `docs/legacy-db-checks-policies.md`, `docs/legacy-db-checks-accounting.md`, `docs/legacy-db-checks-regressions.md` — ძველი რეალური ბაზის სკრიპტები;
- `docs/legacy-tests-gaps-a.md`, `docs/legacy-tests-gaps-b.md`, `docs/legacy-tests-gaps-c.md` — ძველი ტესტების ის შემთხვევები, რომლებიც ბოლოს დაიხურა.

## 1. ეკრანები და მისამართები (Routes)

Test classes = v1 list ∪ a fresh grep of `tests/Insurance.Web.Tests` for the URL (exact path, `{id}` as an interpolation or a GUID). New classes since v1 in **bold**.

| Legacy route file | Route | New Razor page | Tests over HTTP / browser | Status |
|---|---|---|---|---|
| `src/app/(app)/page.tsx` | `/` | `src/Insurance.Web/Pages/Index.cshtml` | AuditHomeTests, BrowserFlowTests, AmlTests, **ClockWiringTests** | DONE |
| `src/app/(app)/accounting/impairment/page.tsx` | `/accounting/impairment` | `src/Insurance.Web/Pages/Accounting/Impairment.cshtml` | ImpairmentPagesTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/accounting/impairment/xlsx/route.ts` | `/accounting/impairment/xlsx` | `src/Insurance.Web/Pages/Accounting/ImpairmentXlsx.cshtml` | ImpairmentPagesTests, **ReportPageFaultTests** | DONE |
| `src/app/(app)/accounting/periods/page.tsx` | `/accounting/periods` | `src/Insurance.Web/Pages/Accounting/Periods.cshtml` | PeriodsPageTests, AuthTests, **LegacyPeriodsHttpTests** | DONE |
| `src/app/(app)/accounting/policy-premium/page.tsx` | `/accounting/policy-premium` | `src/Insurance.Web/Pages/Accounting/PolicyPremium.cshtml` | PolicyPremiumPagesTests, BrowserFlowTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/accounting/policy-premium/xlsx/route.ts` | `/accounting/policy-premium/xlsx` | `src/Insurance.Web/Pages/Accounting/PolicyPremiumXlsx.cshtml` | PolicyPremiumPagesTests, BrowserFlowTests | DONE |
| `src/app/(app)/accounting/premium-report/page.tsx` | `/accounting/premium-report` | `src/Insurance.Web/Pages/Accounting/PremiumReport.cshtml` | PremiumReportPageTests, PaymentFlowTests, BrowserFlowTests, **GapsReportPagesTests**, **LegacyMonthReportPagesTests**, **ReportPageFaultTests** | DONE |
| `src/app/(app)/accounting/regulatory/page.tsx` | `/accounting/regulatory` | `src/Insurance.Web/Pages/Accounting/Regulatory/Index.cshtml` | RegulatoryPagesTests, BrowserFlowTests, **Form6FixesTests** | DONE |
| `src/app/(app)/accounting/regulatory/form1/xlsx/route.ts` | `/accounting/regulatory/form1/xlsx` | `src/Insurance.Web/Pages/Accounting/Regulatory/form1/Xlsx.cshtml (Form1XlsxModel in Downloads.cshtml.cs)` | RegulatoryPagesTests | DONE |
| `src/app/(app)/accounting/regulatory/form6/xlsx/route.ts` | `/accounting/regulatory/form6/xlsx` | `src/Insurance.Web/Pages/Accounting/Regulatory/form6/Xlsx.cshtml (Form6XlsxModel)` | RegulatoryPagesTests, **Form6FixesTests** | DONE |
| `src/app/(app)/accounting/regulatory/reserves/xlsx/route.ts` | `/accounting/regulatory/reserves/xlsx` | `src/Insurance.Web/Pages/Accounting/Regulatory/reserves/Xlsx.cshtml (ReservesXlsxModel)` | RegulatoryPagesTests | DONE |
| `src/app/(app)/audit/page.tsx` | `/audit` | `src/Insurance.Web/Pages/Audit/Index.cshtml` | AuditHomeTests, **AuditFaultTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/claims/page.tsx` | `/claims` | `src/Insurance.Web/Pages/Claims/Index.cshtml` | ClaimPagesTests, BrowserFlowTests | DONE |
| `src/app/(app)/claims/new/page.tsx` | `/claims/new` | `src/Insurance.Web/Pages/Claims/New.cshtml` | ClaimPagesTests, BrowserFlowTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/claims/[id]/page.tsx` | `/claims/{id:guid}` | `src/Insurance.Web/Pages/Claims/Claim.cshtml` | ClaimPagesTests, BrowserFlowTests, **ClaimPageFaultTests**, **FdPanelFaultTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/complaints/page.tsx` | `/complaints` | `src/Insurance.Web/Pages/Complaints/Index.cshtml` | IntermediaryComplaintTests | DONE |
| `src/app/(app)/complaints/new/page.tsx` | `/complaints/new` | `src/Insurance.Web/Pages/Complaints/New.cshtml` | IntermediaryComplaintTests, **ClockWiringTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/complaints/[id]/page.tsx` | `/complaints/{id}` | `src/Insurance.Web/Pages/Complaints/Card.cshtml` | IntermediaryComplaintTests | DONE |
| `src/app/(app)/compliance/aml-cases/page.tsx` | `/compliance/aml-cases` | `src/Insurance.Web/Pages/Compliance/AmlCases/Index.cshtml` | AmlTests, AuditHomeTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/compliance/aml-cases/new/page.tsx` | `/compliance/aml-cases/new` | `src/Insurance.Web/Pages/Compliance/AmlCases/New.cshtml` | AmlTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/compliance/aml-cases/[id]/page.tsx` | `/compliance/aml-cases/{id}` | `src/Insurance.Web/Pages/Compliance/AmlCases/Card.cshtml` | AmlTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/compliance/aml-report/page.tsx` | `/compliance/aml-report` | `src/Insurance.Web/Pages/Compliance/AmlReport/Index.cshtml` | AmlTests, BrowserFlowTests | DONE |
| `src/app/(app)/compliance/aml-report/xlsx/route.ts` | `/compliance/aml-report/xlsx` | `src/Insurance.Web/Pages/Compliance/AmlReport/Xlsx.cshtml` | AmlTests, BrowserFlowTests | DONE |
| `src/app/(app)/compliance/sanctions/page.tsx` | `/compliance/sanctions` | `src/Insurance.Web/Pages/Compliance/Sanctions/Index.cshtml` | SanctionsTests | DONE |
| `src/app/(app)/finance/panel/page.tsx` | `/finance/panel` | `src/Insurance.Web/Pages/Finance/Panel.cshtml` | PaymentFlowTests, **FdPanelFaultTests**, **GapsFdPanelRightsTests**, **LegacyMonthReportPagesTests** | DONE |
| `src/app/(app)/intermediaries/page.tsx` | `/intermediaries` | `src/Insurance.Web/Pages/Intermediaries/Index.cshtml` | IntermediaryComplaintTests | DONE |
| `src/app/(app)/intermediaries/new/page.tsx` | `/intermediaries/new` | `src/Insurance.Web/Pages/Intermediaries/New.cshtml` | IntermediaryComplaintTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/intermediaries/[id]/page.tsx` | `/intermediaries/{id}` | `src/Insurance.Web/Pages/Intermediaries/Card.cshtml` | IntermediaryComplaintTests | DONE |
| `src/app/(app)/parties/page.tsx` | `/parties` | `src/Insurance.Web/Pages/Parties/Index.cshtml` | PartyTests, UserTests | DONE |
| `src/app/(app)/parties/new/page.tsx` | `/parties/new` | `src/Insurance.Web/Pages/Parties/New.cshtml` | PartyTests, BrowserFlowTests, **DateInputTypingTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/parties/[id]/page.tsx` | `/parties/{id}` | `src/Insurance.Web/Pages/Parties/Card.cshtml` | PartyCardTests, PartyTests, SanctionsTests, AmlTests, **ClockWiringTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/payments/overdue/page.tsx` | `/payments/overdue` | `src/Insurance.Web/Pages/Payments/Overdue.cshtml` | PaymentFlowTests | DONE |
| `src/app/(app)/policies/page.tsx` | `/policies` | `src/Insurance.Web/Pages/Policies/Index.cshtml` | PolicyFlowTests, BrowserFlowTests, **FdPanelFaultTests**, PaymentFlowTests | DONE |
| `src/app/(app)/policies/new/page.tsx` | `/policies/new` | `src/Insurance.Web/Pages/Policies/New.cshtml` | PolicyFlowTests, BrowserFlowTests, **GapsPaymentPlanFormsTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/policies/pending/page.tsx` | `/policies/pending` | `src/Insurance.Web/Pages/Policies/Pending.cshtml` | PolicyFlowTests, ClosedMonthAndPrintTests, LegacyAddressGuardTests, BrowserFlowTests, ClosedMonthWebTests | DONE |
| `src/app/(app)/policies/import/page.tsx` | `/policies/import` | `src/Insurance.Web/Pages/Policies/Import.cshtml` | PaymentFlowTests, PolicyFlowTests, BrowserFlowTests, **GapsPaymentPlanFormsTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/policies/import/template/route.ts` | `/policies/import/template` | `src/Insurance.Web/Pages/Policies/ImportTemplate.cshtml` | PaymentFlowTests, BrowserFlowTests | DONE |
| `src/app/(app)/policies/[id]/page.tsx` | `/policies/{id:guid}` | `src/Insurance.Web/Pages/Policies/Details.cshtml` | PolicyFlowTests, PaymentFlowTests, LegacyPremiumPrecisionTests, BrowserFlowTests, **GapsCardDisplayTests**, **GapsCardStoredMoneyTests**, **GapsFdPanelRightsTests**, **GapsLaterDayCardTests**, **GapsPaymentPageTests**, **GapsPrintPolicyTests** +3 more | DONE |
| `src/app/(app)/policies/[id]/cancel/page.tsx` | `/policies/{id:guid}/cancel` | `src/Insurance.Web/Pages/Policies/Cancel.cshtml` | PolicyFlowTests, ClosedMonthAndPrintTests, ClosedMonthWebTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/policies/[id]/endorse/page.tsx` | `/policies/{id:guid}/endorse` | `src/Insurance.Web/Pages/Policies/Endorse.cshtml` | PolicyFlowTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/policies/[id]/payment/page.tsx` | `/policies/{id:guid}/payment` | `src/Insurance.Web/Pages/Policies/Payment.cshtml` | PaymentFlowTests, LegacyPremiumPrecisionTests, ClosedMonthAndPrintTests, BrowserFlowTests, ClosedMonthWebTests, **GapsCardDisplayTests**, **GapsLaterDayCardTests**, **GapsPaymentPageForeignRowTests**, **GapsPaymentPageTests**, **PageFaultTests** +2 more | DONE |
| `src/app/(app)/reference/classes/page.tsx` | `/reference/classes` | `src/Insurance.Web/Pages/Reference/Classes.cshtml` | AuthTests, BrowserFoundationTests | DONE |
| `src/app/(app)/reference/rates/page.tsx` | `/reference/rates` | `src/Insurance.Web/Pages/Reference/Rates.cshtml` | BrowserFoundationTests, **DateInputTypingTests**, **LoadRatesActionTests**, **TreatyFormDateTests** | DONE |
| `src/app/(app)/reinsurance/treaties/page.tsx` | `/reinsurance/treaties` | `src/Insurance.Web/Pages/Reinsurance/Treaties/Index.cshtml` | TreatyPagesTests | DONE |
| `src/app/(app)/reinsurance/treaties/new/page.tsx` | `/reinsurance/treaties/new` | `src/Insurance.Web/Pages/Reinsurance/Treaties/New.cshtml` | TreatyPagesTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/reinsurance/treaties/[id]/page.tsx` | `/reinsurance/treaties/{id}` | `src/Insurance.Web/Pages/Reinsurance/Treaties/Detail.cshtml` | TreatyPagesTests, BordereauPagesTests | DONE |
| `src/app/(app)/reinsurance/treaties/[id]/bordereau/page.tsx` | `/reinsurance/treaties/{id}/bordereau` | `src/Insurance.Web/Pages/Reinsurance/Treaties/Bordereau.cshtml` | BordereauPagesTests, **TreatyFormDateTests** | DONE |
| `src/app/(app)/reinsurance/treaties/[id]/bordereau/xlsx/route.ts` | `/reinsurance/treaties/{id}/bordereau/xlsx` | `src/Insurance.Web/Pages/Reinsurance/Treaties/BordereauXlsx.cshtml` | BordereauPagesTests | DONE |
| `src/app/(app)/settings/page.tsx` | `/settings` | `src/Insurance.Web/Pages/Settings/Index.cshtml` | SettingsTests, UserTests, **LegacyPeriodsHttpTests** | DONE |
| `src/app/(app)/settings/products/page.tsx` | `/settings/products` | `src/Insurance.Web/Pages/Settings/Products/Index.cshtml` | SettingsTests, UserTests | DONE |
| `src/app/(app)/users/page.tsx` | `/users` | `src/Insurance.Web/Pages/Users/Index.cshtml` | UserTests, AuthTests, BrowserFlowTests, PermissionPanelTests | DONE |
| `src/app/(app)/users/permissions/page.tsx` | `/users/permissions` | `src/Insurance.Web/Pages/Users/Permissions/Index.cshtml` | PermissionPanelTests, UserTests, **ClaimPageFaultTests**, **GapsPaymentPageTests**, **PageFaultTests** | DONE |
| `src/app/login/page.tsx` | `/login` | `src/Insurance.Web/Pages/Login.cshtml` | AuthTests, BrowserFoundationTests, BordereauPagesTests, ImpairmentPagesTests, **LoadRatesActionTests**, PeriodsPageTests, PermissionPanelTests, PolicyPremiumPagesTests, PremiumReportPageTests, RegulatoryPagesTests +2 more | DONE |
| `src/app/print/policies/[id]/page.tsx` | `/print/policies/{id:guid}` | `src/Insurance.Web/Pages/Print/Policy.cshtml` | PrintPolicyTests, ClosedMonthAndPrintTests, PolicyFlowTests, BrowserFlowTests, **GapsPaymentPageTests**, **GapsPrintPolicyTests** | DONE |
| `(legacy logout is an action, not a route)` | `/logout` | `src/Insurance.Web/Pages/Logout.cshtml (new)` | AuthTests | DONE |

**Totals (table 2):** 55 routes (54 legacy + the new /logout) — DONE 55, MISSING 0 (v1: DONE 55). `/reference/rates` is no longer GET-only: LoadRatesActionTests posts the form.

## 2. სერვერული მოქმედებები (Server actions)

Tests = v1 list + new classes since v1 (bold) found by grepping all tests for the service method, the `?handler=` name or the posted URL; tests that only use an action to build a fixture are included, GET-only matches are not.

| Legacy file | Function | New handler → service | Tests that call it | Status |
|---|---|---|---|---|
| `accounting/periods/actions.ts` | `closePeriod` | Periods OnPostCloseAsync → PeriodClose | PeriodsPageTests, PeriodCloseConcurrencyTests, **LegacyFdMonthReportTests**, **LegacyMonthCloseTests**, **LegacyPeriodsHttpTests**, **PeriodCloseFaultTests**, **ClockTests**, **GapsAutographChainTests** +3 more | DONE |
| `claims/actions.ts` | `registerClaim` | Claims/New OnPostAsync → ClaimActions.RegisterClaimAsync | ClaimRegistrationTests, ClaimPagesTests, LegacyDbCheckNumberingTests | DONE |
| `claims/actions.ts` | `addEventCoverage` | Claim OnPostAddCoverageAsync → ClaimActions | ClaimRegistrationTests, ClaimPagesTests | DONE |
| `claims/actions.ts` | `changeReserve` | Claim OnPostChangeReserveAsync → ClaimActions | ClaimReserveTests, ClaimPagesTests, **ClaimFaultTests** | DONE |
| `claims/actions.ts` | `approveReserveRequest` | Claim OnPostApproveReserveRequestAsync | ClaimReserveTests, ClaimPagesTests, **ClaimFaultTests** | DONE |
| `claims/actions.ts` | `decideClaim` | Claim OnPostDecideAsync | ClaimReserveTests, ClaimPagesTests, **ClaimFaultTests** | DONE |
| `claims/actions.ts` | `payClaim` | Claim OnPostPayClaimAsync → ClaimActions.PayClaimAsync | ClaimPaymentTests, ClaimTotalLossTests, ClaimConcurrencyTests, ClaimPagesTests | DONE |
| `claims/actions.ts` | `approveClaimPaymentRequest` | Claim OnPostApproveClaimPaymentRequestAsync | ClaimPaymentTests, ClaimConcurrencyTests, LegacyFdRaceTests, ClaimPagesTests, **FdPanelFaultTests** | DONE |
| `claims/actions.ts` | `addRecovery` | Claim OnPostAddRecoveryAsync | ClaimRecoveryTests, PremiumReportPageTests, ClaimPagesTests, **ClaimFaultTests** | DONE |
| `claims/actions.ts` | `reverseClaimPayment` | Claim OnPostReverseClaimPaymentAsync | ClaimReversalTests, ClaimConcurrencyTests, ClaimPagesTests, **ClaimFaultTests**, **GapsFdPanelChainTests** | DONE |
| `complaints/actions.ts` | `registerComplaint` | Complaints/New OnPostAsync | IntermediaryComplaintTests, **TreatyFormDateTests** | DONE |
| `complaints/actions.ts` | `acknowledgeComplaint` | Complaints/Card OnPostAcknowledgeAsync | IntermediaryComplaintTests | DONE |
| `complaints/actions.ts` | `respondComplaint` | Complaints/Card OnPostRespondAsync | IntermediaryComplaintTests | DONE |
| `compliance/aml-cases/actions.ts` | `registerCase` | AmlCases/New OnPostAsync | AmlTests, **TreatyFormDateTests** | DONE |
| `compliance/aml-cases/actions.ts` | `decideCase` | AmlCases/Card OnPostDecideAsync | AmlTests | DONE |
| `compliance/aml-cases/actions.ts` | `createSuspension` | AmlCases/Card, Parties/Card OnPostCreateSuspensionAsync → ComplianceActions | AmlTests, AmlSuspensionAcceptanceTests | DONE |
| `compliance/aml-cases/actions.ts` | `updateSuspension` | AmlCases Index/Card, Parties/Card OnPostUpdateSuspensionAsync | AmlTests | DONE |
| `compliance/sanctions/actions.ts` | `refreshUnList` | Sanctions OnPostRefreshAsync | SanctionsTests | DONE |
| `compliance/sanctions/actions.ts` | `screenEveryone` | Sanctions OnPostScreenAllAsync | SanctionsTests | DONE |
| `compliance/sanctions/actions.ts` | `screenOneParty` | Parties/Card OnPostScreenAsync → ComplianceActions.ScreenOnePartyAsync | SanctionsTests | DONE |
| `compliance/sanctions/actions.ts` | `decideScreening` | Sanctions / Parties/Card OnPostDecideScreeningAsync | SanctionsTests | DONE |
| `intermediaries/actions.ts` | `saveIntermediary` | Intermediaries New/Card OnPostSaveAsync | IntermediaryComplaintTests, **TreatyFormDateTests** | DONE |
| `parties/actions.ts` | `saveParty` | Parties New/Card OnPostSaveAsync | PartyTests, **DateInputTypingTests** | DONE |
| `parties/actions.ts` | `addBeneficialOwner` | Parties/Card OnPostOwnerAsync | PartyCardTests | DONE |
| `parties/actions.ts` | `updateBeneficialOwner` | Parties/Card OnPostOwnerUpdateAsync | PartyCardTests | DONE |
| `parties/actions.ts` | `assessPartyRisk` | Parties/Card OnPostRiskAsync → PartyRisk | PartyCardTests, ComplianceServicesTests | DONE |
| `policies/[id]/cancel/actions.ts` | `cancelPolicy` | Policies/Cancel OnPostAsync → PolicyCancel | CancelIssueTests, PolicyFlowTests, PolicyPremiumCancelWriterTests, LegacyTotalLossTests, **GapsPaymentPageTests**, **GapsPolicyCancelAsUserTests**, **GapsReportPagesTests** | DONE |
| `policies/[id]/endorse/actions.ts` | `endorsePolicy` | Policies/Endorse OnPostAsync → PolicyActions.EndorsePolicyAsync | EndorsementTests, ImportEndorseSeriesExhaustedTests, PolicyFlowTests, **GapsPaymentPageTests** | DONE |
| `policies/[id]/payment/actions.ts` | `recordPayment` | Payment OnPostAsync → PaymentActions.RecordPaymentAsync | ReceiptTests, PaymentFlowTests, PaymentRegressionTests, PaymentConcurrencyTests, LegacyPremiumPrecisionTests, **LegacyMonthCloseTests**, **PolicyFaultTests**, **GapsImportPlanTests** | DONE |
| `policies/[id]/payment/actions.ts` | `allocateReceipt` | Payment OnPostAllocateAsync | AdvanceTransferTests, PaymentFlowTests, LegacyClientCreditTests, **LegacyMonthCloseTests**, **GapsCardDisplayTests**, **GapsFdPanelRightsTests**, **GapsImportPlanTests**, **GapsMonthReportReversalTests**, **GapsPaymentPageTests** +1 more | DONE |
| `policies/[id]/payment/actions.ts` | `transferOverpayment` | Payment OnPostTransferAsync | AdvanceTransferTests, PaymentFlowTests, LegacyClientCreditTests, **GapsCardDisplayTests**, **GapsPaymentPageTests** | DONE |
| `policies/[id]/payment/actions.ts` | `reversePayment` | Details OnPostReversePaymentAsync → ReceiptReversal | FdRequestTests, PaymentFlowTests, LegacyClientCreditTests, **GapsElevenTests**, **GapsFdPanelChainTests**, **GapsFdPanelRightsTests**, **GapsFdRefundReversalTests**, **GapsImportPlanTests**, **GapsLaterDayCardTests** +2 more | DONE |
| `policies/[id]/payment/actions.ts` | `approveReceiptReversalRequest` | Details OnPostApproveReversalRequestAsync | FdRequestTests, LegacyFdRaceTests, LegacyPremiumPrecisionTests, PaymentFlowTests, **GapsFdPanelChainTests**, **GapsFdPanelRightsTests**, **GapsFdRefundReversalTests**, **GapsLaterDayCardTests**, **GapsMonthReportReversalTests**, **GapsReceiptChainTests** | DONE |
| `policies/[id]/payment/actions.ts` | `refundOverpayment` | Details OnPostRefundOverpaymentAsync | FdRequestTests, PaymentRegressionTests, PaymentFlowTests, **GapsFdPanelChainTests**, **GapsFdRefundReversalTests**, **GapsReceiptChainTests** | DONE |
| `policies/[id]/payment/actions.ts` | `approvePremiumRefundRequest` | Details OnPostApproveRefundRequestAsync | FdRequestTests, LegacyFdRaceTests, PaymentFlowTests, **GapsFdPanelChainTests**, **GapsFdRefundReversalTests**, **GapsReceiptChainTests** | DONE |
| `policies/[id]/reinsurance-actions.ts` | `placeFacultativeOnPolicy` | Details OnPostPlaceFacultativeAsync → ReinsuranceStore | ReinsuranceChainTests, FacultativeConcurrencyTests, PolicyFlowTests | DONE |
| `policies/actions.ts` | `issuePolicy` | Policies/New OnPostAsync → PolicyActions.IssuePolicyAsync (PolicyIssue) | CancelIssueTests, LegacyIssueConcurrencyTests, PolicyFlowTests, **GapsAutographChainTests**, **GapsElevenTests** | DONE |
| `policies/actions.ts` | `approvePolicy` | Pending / Details OnPostApproveAsync → PolicyActions.ApprovePolicyAsync | PolicyApprovalTests, LegacyApprovalAnswersTests, LegacyAddressGuardTests, PolicyFlowTests, **LegacyMonthCloseTests**, **PolicyFaultTests**, **GapsAutographChainTests**, **GapsCardDisplayTests**, **GapsElevenTests**, **GapsFdPanelChainTests** +8 more | DONE |
| `policies/import/actions.ts` | `previewImport` | Import OnPostPreviewAsync → PolicyImport | ImportTests, PaymentFlowTests, **GapsPaymentPlanFormsTests** | DONE |
| `policies/import/actions.ts` | `confirmImport` | Import OnPostConfirmAsync | ImportTests, ImportEndorseSeriesExhaustedTests, PaymentFlowTests | DONE |
| `reference/rates/actions.ts` | `loadRates` | Reference/Rates OnPostAsync → Rates.LoadNbgRatesAsync | BrowserFoundationTests (viewer: no form), **LoadRatesActionTests** (the POST), **NbgRateCacheTests** (Rates.LoadNbgRatesAsync with the fake bank) | DONE ▲ |
| `reinsurance/treaties/actions.ts` | `createTreaty` | Treaties/New OnPostAsync → Treaties | TreatyPagesTests, **TreatyFormDateTests** | DONE |
| `reinsurance/treaties/actions.ts` | `closeTreaty` | Treaties/Detail OnPostCloseAsync | TreatyPagesTests | DONE |
| `settings/actions.ts` | `saveSettings` | Settings OnPostSaveSettingsAsync | SettingsTests, **LegacyPeriodsHttpTests** | DONE |
| `settings/actions.ts` | `saveNumberSeries` | Settings OnPostSaveNumberSeriesAsync | SettingsTests | DONE |
| `settings/products/actions.ts` | `saveProductTexts` | Settings/Products OnPostAsync | SettingsTests | DONE |
| `users/actions.ts` | `createUser` | Users OnPostCreateAsync | UserTests | DONE |
| `users/actions.ts` | `setUserActive` | Users OnPostSetActiveAsync | UserTests | DONE |
| `users/actions.ts` | `resetPassword` | Users OnPostResetPasswordAsync | UserTests | DONE |
| `users/permissions/actions.ts` | `createGroupAction` | Users/Permissions OnPostCreateGroupAsync → PermissionStore | PermissionPanelTests, PermissionStorageTests, **PermissionFaultTests**, **PolicyFaultTests**, **GapsAutographChainTests** | DONE |
| `users/permissions/actions.ts` | `renameGroupAction` | OnPostRenameGroupAsync | PermissionPanelTests, PermissionStorageTests, **PermissionFaultTests** | DONE |
| `users/permissions/actions.ts` | `setGroupMemberAction` | OnPostSetGroupMemberAsync | PermissionPanelTests, PermissionStorageTests, **PageFaultTests**, **PermissionFaultTests**, **PolicyFaultTests**, **GapsAutographChainTests** | DONE |
| `users/permissions/actions.ts` | `setGroupGrantAction` | OnPostSetGroupGrantAsync | PermissionPanelTests, PermissionStorageTests, **PageFaultTests**, **PolicyFaultTests**, **GapsAutographChainTests** | DONE |
| `users/permissions/actions.ts` | `setUserDecisionAction` | OnPostSetUserDecisionAsync | PermissionPanelTests, PermissionStorageTests, **ClaimPageFaultTests**, **PageFaultTests**, **PermissionFaultTests**, **PolicyFaultTests**, **GapsAutographChainTests**, **GapsPaymentPageTests** | DONE |
| `login/actions.ts` | `login` | Login OnPostAsync → Auth | AuthTests, BrowserFoundationTests, **LoadRatesActionTests** | DONE |
| `login/actions.ts` | `logout` | Logout OnPostAsync | AuthTests, BrowserFoundationTests | DONE |
| `accounting/impairment/xlsx/route.ts` | `GET` | ImpairmentXlsx OnGetAsync → ReportWorkbooks | ImpairmentPagesTests, **ReportPageFaultTests** | DONE |
| `accounting/policy-premium/xlsx/route.ts` | `GET` | PolicyPremiumXlsx OnGetAsync | PolicyPremiumPagesTests | DONE |
| `accounting/regulatory/form1/xlsx/route.ts` | `GET` | Form1XlsxModel.OnGetAsync | RegulatoryPagesTests | DONE |
| `accounting/regulatory/form6/xlsx/route.ts` | `GET` | Form6XlsxModel.OnGetAsync | RegulatoryPagesTests, **Form6FixesTests** | DONE |
| `accounting/regulatory/reserves/xlsx/route.ts` | `GET` | ReservesXlsxModel.OnGetAsync | RegulatoryPagesTests | DONE |
| `compliance/aml-report/xlsx/route.ts` | `GET` | AmlReport/Xlsx OnGetAsync → AmlReport | AmlTests, ComplianceServicesTests | DONE |
| `policies/import/template/route.ts` | `GET` | ImportTemplate OnGetAsync | ImportTests, PaymentFlowTests | DONE |
| `reinsurance/treaties/[id]/bordereau/xlsx/route.ts` | `GET` | BordereauXlsx OnGetAsync → Bordereau | BordereauPagesTests | DONE |

**Totals (table 3):** 64 functions — DONE 64, MISSING 0 (v1: DONE 63, MISSING 1 — `loadRates`, now covered).

## 3. მოდულები (Legacy modules)

N/A = a React-only component whose markup is in the named Razor file (its behaviour is tested through that page). New test classes since v1 in **bold**.

| Legacy module | New C# / Razor file(s) | Tests | Status |
|---|---|---|---|
| `src/compliance/client-risk.ts` | `src/Insurance.Domain/Compliance/ClientRisk.cs` | ClientRiskTests, ComplianceParityTests | DONE |
| `src/compliance/kyc.ts` | `src/Insurance.Domain/Compliance/Kyc.cs` | KycTests, ComplianceParityTests | DONE |
| `src/compliance/sanctions.ts` | `src/Insurance.Domain/Compliance/Sanctions.cs` | SanctionsTests (Domain), ComplianceParityTests | DONE |
| `src/compliance/suspension.ts` | `src/Insurance.Domain/Compliance/Suspension.cs` | SuspensionTests, ComplianceParityTests | DONE |
| `src/finance/cancel-preview.ts` | `src/Insurance.Domain/Finance/CancelPreview.cs` | CancelPreviewTests, ParityCoreTests | DONE |
| `src/finance/commission.ts` | `src/Insurance.Domain/Finance/Commission.cs` | FinanceTests, ParityCoreTests, **GapsCardPositionTests** | DONE |
| `src/finance/dates.ts` | `src/Insurance.Domain/Finance/Dates.cs` | DatesTests, ParityCoreTests | DONE |
| `src/finance/deductible.ts` | `src/Insurance.Domain/Finance/Deductible.cs` | DeductibleTests, ParityCoreTests | DONE |
| `src/finance/earned-premium.ts` | `src/Insurance.Domain/Finance/EarnedPremium.cs` | FinanceTests, ParityCoreTests | DONE |
| `src/finance/impairment.ts` | `src/Insurance.Domain/Finance/Impairment.cs` | ImpairmentTests, ImpairmentExactTests, ParityReceivableTests | DONE |
| `src/finance/money.ts` | `src/Insurance.Domain/Finance/Money.cs, JsMath.cs` | ParityCoreTests (finance-money.json), FinanceTests | DONE |
| `src/finance/payment-authorization.ts` | `src/Insurance.Domain/Finance/PaymentAuthorization.cs` | PaymentAuthorizationTests, ParityReceivableTests | DONE |
| `src/finance/plan.ts` | `src/Insurance.Domain/Finance/Plan.cs` | PlanTests, ParityCoreTests | DONE |
| `src/finance/receivable.ts` | `src/Insurance.Domain/Finance/Receivable.cs` | FinanceTests, ParityReceivableTests, **GapsCardPositionTests**, **GapsCardDisplayTests** | DONE |
| `src/finance/reinsurance.ts` | `src/Insurance.Domain/Finance/Reinsurance.cs` | ReinsuranceTests, ParityReinsuranceSubrogationTests | DONE |
| `src/finance/reserves.ts` | `src/Insurance.Domain/Finance/Reserves.cs` | FinanceTests, ParityCoreTests | DONE |
| `src/finance/subrogation-report.ts` | `src/Insurance.Domain/Finance/SubrogationReport.cs` | SubrogationReportTests, ParityReinsuranceSubrogationTests | DONE |
| `src/finance/subrogation.ts` | `src/Insurance.Domain/Finance/Subrogation.cs` | SubrogationTests (Finance), ParityReinsuranceSubrogationTests | DONE |
| `src/finance/total-loss.ts` | `src/Insurance.Domain/Finance/TotalLoss.cs` | FinanceTests, ParityReceivableTests, ClaimTotalLossTests | DONE |
| `src/lib/aml-report.ts` | `src/Insurance.Application/Services/AmlReport.cs` | ComplianceServicesTests, AmlTests | DONE |
| `src/lib/aml.ts` | `src/Insurance.Application/Services/Aml.cs` | AmlTests, AmlSuspensionAcceptanceTests, PaymentConcurrencyTests, LegacyDbCheckClaimTests, **ClockWiringTests** | DONE |
| `src/lib/audit-log.ts` | `src/Insurance.Application/Services/AuditLog.cs` | AuditHomeTests (via /audit), SchemaTests, **AuditFaultTests** | DONE |
| `src/lib/auth.ts` | `src/Insurance.Application/Services/Auth.cs, src/Insurance.Web/Infrastructure/RequestUser.cs, RequireUserFilter.cs` | AuthTests, DataMigrationTests | DONE |
| `src/lib/billing.ts` | `src/Insurance.Application/Services/Billing.cs` | LegacyClientCreditTests, LegacyPermissionsDbCheckTests, ClaimPaymentTests, LegacyPremiumPrecisionTests, **GapsReleasableExactTests**, **GapsCardPositionTests**, **ReportFaultTests** | DONE |
| `src/lib/bordereau.ts` | `src/Insurance.Application/Services/Bordereau.cs` | BordereauPagesTests, ReportParityTests | DONE |
| `src/lib/client-risk.ts` | `src/Insurance.Application/Services/PartyRisk.cs` | ComplianceServicesTests, PartyCardTests | DONE |
| `src/lib/close-deadline.ts` | `src/Insurance.Domain/Common/CloseDeadline.cs` | CloseDeadlineTests, CommonParityTests | DONE |
| `src/lib/date-text.ts` | `src/Insurance.Domain/Common/DateText.cs` | DateTextTests, CommonParityTests | DONE |
| `src/lib/db.ts` | `src/Insurance.Application/Data/Db.cs, Tx.cs, DbError.cs, DbOptions.cs, DapperSetup.cs` | SchemaTests, TestDatabaseIsolationTests (and every integration test), **CommandInterceptorTests**, **ClockTests**, **ClockWiringTests** | DONE |
| `src/lib/fd-panel.ts` | `src/Insurance.Application/Services/FdPanel.cs` | FdPanelTests, LegacyFdPanelReaderTests, PaymentFlowTests, **FdPanelFaultTests**, **GapsFdPanelChainTests**, **GapsFdPanelRightsTests**, **LegacyFdMonthReportTests** | DONE |
| `src/lib/impairment-report.ts` | `src/Insurance.Application/Services/ImpairmentReport.cs` | ImpairmentReportTests, ImpairmentCurrencyTests, ReportParityTests, **ImpairmentDebtorsOrderTests**, **ReportPageFaultTests** | DONE |
| `src/lib/known.ts` | `src/Insurance.Domain/Common/QueryParams.cs` | QueryParamsTests, CommonParityTests | DONE |
| `src/lib/kyc.ts` | `src/Insurance.Application/Services/PartyKyc.cs` | ComplianceServicesTests | DONE |
| `src/lib/month.ts` | `src/Insurance.Domain/Common/Month.cs` | MonthTests, CommonParityTests | DONE |
| `src/lib/numbering.ts` | `src/Insurance.Domain/Common/Numbering.cs` | NumberingTests, LegacyDbCheckNumberingTests, SettingsTests, **TreatySeriesExhaustedTests** | DONE |
| `src/lib/password.ts` | `src/Insurance.Domain/Common/Password.cs, Scrypt.cs` | PasswordTests, DataMigrationTests, UserTests | DONE |
| `src/lib/payment-threshold.ts` | `src/Insurance.Application/Services/Settings.cs` | SettingsTests, ClaimPaymentTests, FdRequestTests, **GapsFdClaimSettingTests**, **GapsFdRefundReversalTests** | DONE |
| `src/lib/permission-panel.ts` | `src/Insurance.Application/Services/PermissionPanel.cs` | PermissionPanelTests, PermissionStorageTests, **PermissionFaultTests**, **PageFaultTests** | DONE |
| `src/lib/permission-store.ts` | `src/Insurance.Application/Services/PermissionStore.cs` | PermissionStorageTests, LegacyPermissionsDbCheckTests, **PermissionFaultTests**, **ClaimFaultTests**, **PolicyFaultTests**, **PeriodCloseFaultTests** | DONE |
| `src/lib/permissions.ts` | `src/Insurance.Domain/Common/Permissions.cs` | PermissionsTests, CommonParityTests | DONE |
| `src/lib/phone.ts` | `src/Insurance.Domain/Common/Phone.cs` | PhoneTests, CommonParityTests, PartyTests | DONE |
| `src/lib/policy-cancel.ts` | `src/Insurance.Application/Services/PolicyCancel.cs` | CancelIssueTests, PolicyPremiumCancelWriterTests, ClaimTotalLossTests, LegacyTotalLossTests, **GapsPolicyCancelAsUserTests** | DONE |
| `src/lib/policy-import.ts` | `src/Insurance.Application/Excel/PolicyImport.cs (+ XlsxReader, XlsReader, DsvReader, SheetToJson, SheetJsParts, JsDate), Services/PolicyImport.cs` | ImportTests, ExcelImportParityTests, ExcelParityTests, **GapsImportPlanTests**, **GapsPaymentPlanFormsTests** | DONE |
| `src/lib/policy-issue.ts` | `src/Insurance.Application/Services/PolicyIssue.cs` | CancelIssueTests, LegacyIssueConcurrencyTests (via PolicyActions.IssuePolicyAsync), **GapsElevenTests**, **GapsAutographChainTests** | DONE |
| `src/lib/policy-premium-report.ts` | `src/Insurance.Application/Services/PolicyPremiumReport.cs` | PolicyPremiumReportTests, PolicyPremiumPagesTests, ReportParityTests | DONE |
| `src/lib/premium-report.ts` | `src/Insurance.Application/Services/PremiumReport.cs, ReportMath.cs` | PremiumReport*Tests, MonthReportFxLookupTests, MonthlyReportThousandTests, ReportParityTests, **LegacyMonthReportPrecisionTests**, **LegacyFdMonthReportTests**, **ReportFaultTests**, **GapsReportPagesTests** | DONE |
| `src/lib/rates.ts` | `src/Insurance.Application/Services/Rates.cs` | MonthReportFxLookupTests, ReinsuranceChainTests, **NbgRateCacheTests**, **LoadRatesActionTests** | DONE ▲ |
| `src/lib/receipt-reversal.ts` | `src/Insurance.Application/Services/ReceiptReversal.cs` | FdRequestTests, LegacyClientCreditTests, ClaimReversalTests, **GapsFdRefundReversalTests**, **GapsReceiptChainTests**, **GapsMonthReportReversalTests** | DONE |
| `src/lib/regulatory/export.ts` | `src/Insurance.Domain/Regulatory/Export.cs` | ExcelCentsTests, ExcelPrecisionTests, RegulatoryPagesTests | DONE |
| `src/lib/regulatory/financial-annex.ts` | `src/Insurance.Domain/Regulatory/FinancialAnnex.cs` | FinancialAnnexTests, RegulatoryParityTests | DONE |
| `src/lib/regulatory/form6-source.ts` | `src/Insurance.Application/Services/Form6Source.cs` | RegulatoryPagesTests, **Form6FixesTests** | DONE |
| `src/lib/regulatory/form6.ts` | `src/Insurance.Domain/Regulatory/Form6.cs` | Form6Tests, RegulatoryParityTests, **Form6FixesTests** | DONE |
| `src/lib/regulatory/formula.ts` | `src/Insurance.Domain/Regulatory/Formula.cs, JsSemantics.cs` | RegulatoryParityTests | DONE |
| `src/lib/regulatory/reserves-placement.ts` | `src/Insurance.Domain/Regulatory/ReservesPlacement.cs` | ReservesPlacementTests, RegulatoryParityTests | DONE |
| `src/lib/regulatory/templates.ts` | `src/Insurance.Domain/Regulatory/Templates.cs (+ template files)` | TemplatesTests | DONE |
| `src/lib/regulatory/workbook.ts` | `src/Insurance.Domain/Regulatory/Workbook.cs` | RegulatoryParityTests, Form6Tests | DONE |
| `src/lib/regulatory/zip.ts` | `src/Insurance.Domain/Regulatory/Zip.cs` | RegulatoryParityTests (workbook round trips) | DONE |
| `src/lib/reinsurance.ts` | `src/Insurance.Application/Services/ReinsuranceStore.cs, Treaties.cs` | ReinsuranceChainTests, ClaimRegistrationTests, TreatyPagesTests, FacultativeConcurrencyTests, **FacultativeXlTests**, **TreatySeriesExhaustedTests** | DONE |
| `src/lib/sanctions.ts` | `src/Insurance.Application/Services/SanctionsScreening.cs` | SanctionsTests (Web) | DONE |
| `src/lib/settings.ts` | `src/Insurance.Application/Services/Settings.cs` | SettingsTests, **GapsFdClaimSettingTests** | DONE |
| `src/lib/subrogation-report-store.ts` | `src/Insurance.Application/Services/SubrogationReportStore.cs` | SubrogationReportStoreTests, LegacyDbCheckRegressTests | DONE |
| `src/lib/subrogation-store.ts` | `src/Insurance.Application/Services/SubrogationStore.cs` | SubrogationCaseTests, LegacyDbCheckRegressTests | DONE |
| `src/lib/subrogation.ts` | `src/Insurance.Application/Services/SubrogationCases.cs, src/Insurance.Domain/Common/Subrogation.cs` | SubrogationCaseTests, Common/SubrogationTests, ClaimRecoveryTests | DONE |
| `src/lib/xlsx-layout.ts` | `src/Insurance.Application/Excel/XlsxLayout.cs, XlsxWriter.cs` | XlsxLayoutTests, ExcelParityTests, ReportWorkbookLayoutTests | DONE |
| `src/i18n/aml.ts` | `src/Insurance.Application/Texts/AmlTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/audit.ts` | `src/Insurance.Application/Texts/AuditTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/billing.ts` | `src/Insurance.Application/Texts/BillingTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/claims.ts` | `src/Insurance.Application/Texts/ClaimTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/complaints.ts` | `src/Insurance.Application/Texts/ComplaintTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/fd-panel.ts` | `src/Insurance.Application/Texts/FdPanelTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/impairment.ts` | `src/Insurance.Application/Texts/ImpairmentTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/ka.ts` | `src/Insurance.Application/Texts/Ka.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/permissions.ts` | `src/Insurance.Application/Texts/PermissionTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/policy-premium.ts` | `src/Insurance.Application/Texts/PolicyPremiumTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/policy.ts` | `src/Insurance.Application/Texts/PolicyTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/regulatory.ts` | `src/Insurance.Application/Texts/RegulatoryTexts (+ Domain/Regulatory/RegulatoryTexts.cs).cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/reinsurance.ts` | `src/Insurance.Application/Texts/ReinsuranceTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/i18n/sanctions.ts` | `src/Insurance.Application/Texts/SanctionsTexts.cs (TextMap.cs)` | TextsParityTests (every text, number and function of src/i18n) | DONE |
| `src/app/(app)/accounting/periods/close-answer.tsx` | `src/Insurance.Web/Pages/Accounting/Periods.cshtml` | PeriodsPageTests, **LegacyPeriodsHttpTests** | N/A |
| `src/app/(app)/claims/[id]/add-coverage-form.tsx` | `src/Insurance.Web/Pages/Claims/Claim.cshtml` | ClaimPagesTests | N/A |
| `src/app/(app)/claims/[id]/pay-form.tsx` | `src/Insurance.Web/Pages/Claims/Claim.cshtml` | ClaimPagesTests, **ClaimPageFaultTests** | N/A |
| `src/app/(app)/claims/new/claim-form.tsx` | `src/Insurance.Web/Pages/Claims/New.cshtml` | ClaimPagesTests | N/A |
| `src/app/(app)/complaints/[id]/respond-form.tsx` | `src/Insurance.Web/Pages/Complaints/Card.cshtml` | IntermediaryComplaintTests | N/A |
| `src/app/(app)/complaints/new/complaint-form.tsx` | `src/Insurance.Web/Pages/Complaints/New.cshtml` | IntermediaryComplaintTests | N/A |
| `src/app/(app)/compliance/aml-cases/[id]/decision-form.tsx` | `src/Insurance.Web/Pages/Compliance/AmlCases/Card.cshtml` | AmlTests | N/A |
| `src/app/(app)/compliance/aml-cases/new/case-form.tsx` | `src/Insurance.Web/Pages/Compliance/AmlCases/New.cshtml` | AmlTests | N/A |
| `src/app/(app)/compliance/aml-cases/suspension-section.tsx` | `src/Insurance.Web/Pages/Compliance/_SuspensionSection.cshtml (+ ComplianceActions.cs)` | AmlTests | N/A |
| `src/app/(app)/compliance/sanctions/screening-table.tsx` | `src/Insurance.Web/Pages/Compliance/_ScreeningTable.cshtml` | SanctionsTests | N/A |
| `src/app/(app)/intermediaries/intermediary-form.tsx` | `src/Insurance.Web/Pages/Intermediaries/_IntermediaryForm.cshtml, IntermediaryForm.cs` | IntermediaryComplaintTests | N/A |
| `src/app/(app)/layout.tsx` | `src/Insurance.Web/Pages/Shared/_Layout.cshtml` | BrowserFoundationTests (menu by role), AuthTests, **FdPanelFaultTests (menu)** | N/A |
| `src/app/(app)/parties/[id]/owner-form.tsx` | `src/Insurance.Web/Pages/Parties/Card.cshtml` | PartyCardTests | N/A |
| `src/app/(app)/parties/[id]/risk-form.tsx` | `src/Insurance.Web/Pages/Parties/Card.cshtml` | PartyCardTests | N/A |
| `src/app/(app)/parties/party-form.tsx` | `src/Insurance.Web/Pages/Parties/_PartyForm.cshtml, PartyForm.cs` | PartyTests | N/A |
| `src/app/(app)/policies/[id]/cancel/cancel-form.tsx` | `src/Insurance.Web/Pages/Policies/Cancel.cshtml` | PolicyFlowTests | N/A |
| `src/app/(app)/policies/[id]/endorse/endorse-form.tsx` | `src/Insurance.Web/Pages/Policies/Endorse.cshtml` | PolicyFlowTests | N/A |
| `src/app/(app)/policies/[id]/payment/payment-form.tsx` | `src/Insurance.Web/Pages/Policies/Payment.cshtml` | PaymentFlowTests, **GapsPaymentPageTests** | N/A |
| `src/app/(app)/policies/[id]/policy-actions.tsx` | `src/Insurance.Web/Pages/Policies/Details.cshtml` | PolicyFlowTests, PaymentFlowTests, **GapsCardDisplayTests**, **GapsPaymentPageTests** | N/A |
| `src/app/(app)/policies/approve-answer.tsx` | `src/Insurance.Web/Pages/Shared/_ApproveAnswer.cshtml` | LegacyAddressGuardTests, PolicyFlowTests | N/A |
| `src/app/(app)/policies/import/import-form.tsx` | `src/Insurance.Web/Pages/Policies/Import.cshtml` | PaymentFlowTests, **GapsPaymentPlanFormsTests** (plan options) | N/A |
| `src/app/(app)/policies/policy-form.tsx` | `src/Insurance.Web/Pages/Policies/New.cshtml` | PolicyFlowTests, BrowserFlowTests, **GapsPaymentPlanFormsTests** (plan options) | N/A |
| `src/app/(app)/reference/rates/load-form.tsx` | `src/Insurance.Web/Pages/Reference/Rates.cshtml` | BrowserFoundationTests (viewer: no form), **LoadRatesActionTests** (the post), **DateInputTypingTests** (its date field) | N/A |
| `src/app/(app)/reinsurance/treaties/new/treaty-form.tsx` | `src/Insurance.Web/Pages/Reinsurance/Treaties/New.cshtml` | TreatyPagesTests, **TreatyFormDateTests** | N/A |
| `src/app/(app)/settings/keys.ts` | `src/Insurance.Application/Texts/Screens/SettingKeys.cs (C#, not N/A)` | SettingsTests | DONE |
| `src/app/(app)/settings/products/keys.ts` | `src/Insurance.Application/Texts/Screens/SettingKeys.cs (C#, not N/A)` | SettingsTests | DONE |
| `src/app/(app)/users/permissions/permission-forms.tsx` | `src/Insurance.Web/Pages/Users/Permissions/Index.cshtml` | PermissionPanelTests, **PageFaultTests** | N/A |
| `src/app/(app)/users/user-forms.tsx` | `src/Insurance.Web/Pages/Users/Index.cshtml` | UserTests | N/A |
| `src/app/date-input.tsx` | `src/Insurance.Web/Infrastructure/DateInputTagHelper.cs + wwwroot/js/date-input.js` | BrowserFoundationTests, DateTextTests, **DateInputTypingTests**, **TreatyFormDateTests** | N/A |
| `src/app/layout.tsx` | `src/Insurance.Web/Pages/Shared/_Layout.cshtml, _Bare.cshtml` | AuthTests, BrowserFoundationTests | N/A |
| `src/app/login/login-form.tsx` | `src/Insurance.Web/Pages/Login.cshtml` | AuthTests, BrowserFoundationTests | N/A |
| `src/app/print/layout.tsx` | `src/Insurance.Web/Pages/Shared/_Print.cshtml` | PrintPolicyTests, **GapsPrintPolicyTests** | N/A |
| `src/app/print/print-button.tsx` | `src/Insurance.Web/wwwroot/js/print-button.js (via _Print.cshtml)` | PrintPolicyTests (data-print), BrowserFlowTests | N/A |

**Totals (table 4):** 111 modules — DONE 80, N/A 31, MISSING 0 (v1: DONE 80, N/A 31; `rates.ts` no longer has an untested bank-answer path).
## 4. ძველი ტესტ-ფაილები (Legacy test files)

Cases = `test(`/`it(` calls in the file (v1 count). Rows changed since v1 are marked ▲.

| Legacy file | Cases | New test class(es) | Status | Evidence | Notes |
|---|---:|---|---|---|---|
| `scripts/claude-test-users-plan.test.ts` | 5 | TestUsersToolTests | PORTED | unit / SQL Server |  |
| `scripts/integration/audit-log-viewer.test.ts` | 14 | AuditHomeTests, AuditFaultTests | PORTED ▲ | HTTP | gaps-a: removals, grants, read failure, cursor bounds, hash never shown, log unchanged |
| `scripts/integration/autograph-permissioned-chain.test.ts` | 1 | GapsAutographChainTests; steps: PolicyApprovalTests, ReceiptTests, PeriodsPageTests, PolicyFlowTests, PermissionPanelTests, GapsAutographChainPagesTests | PORTED ▲ | SQL Server / HTTP | chain on one database (gaps-b) and, on the legacy days with the test clock, the four pages rendered inside the chain (pending list, card, periods, permission panel) — b4a3c32 |
| `scripts/integration/cession-cancel-report.test.ts` | 7 | ReinsuranceChainTests | PORTED | SQL Server | "no socket" N/A |
| `scripts/integration/cession-cancellation.test.ts` | 5 | ReinsuranceChainTests | PORTED | SQL Server | GEL and USD, as a Theory |
| `scripts/integration/claim-cession-invalid-plan.test.ts` | 7 | ClaimRegistrationTests | PORTED | SQL Server |  |
| `scripts/integration/claim-cession-writer.test.ts` | 3 | ClaimRegistrationTests | PORTED | SQL Server |  |
| `scripts/integration/claim-payment-permission.test.ts` | 32 | ClaimPaymentTests, ClaimConcurrencyTests, ClaimPagesTests, ClaimFaultTests, ClaimPageFaultTests, ReportFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a: every race / read-failure / fault case; defect D2 fixed |
| `scripts/integration/claim-payment-reversal.test.ts` | 28 | ClaimReversalTests, ClaimConcurrencyTests, ClaimPagesTests, ClaimFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a |
| `scripts/integration/claim-recovery-monthly-report.test.ts` | 1 | ClaimRecoveryTests, PremiumReportPageTests | PORTED | SQL Server / HTTP |  |
| `scripts/integration/claim-recovery-report-page.test.ts` | 2 | PremiumReportPageTests, ReportPageFaultTests | PORTED ▲ | HTTP | gaps-a: recovery read fails once |
| `scripts/integration/claim-recovery-viewer-refusal.test.ts` | 5 | ClaimRecoveryTests | PORTED | SQL Server |  |
| `scripts/integration/claim-reserve-approval.test.ts` | 17 | ClaimReserveTests, ClaimPagesTests, ClaimFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a |
| `scripts/integration/claim-xl-writer.test.ts` | 4 | ClaimRegistrationTests | PORTED | SQL Server |  |
| `scripts/integration/date-input-typing.test.ts` | 9 | BrowserFoundationTests, DateInputTypingTests, DateTextTests | PORTED ▲ | unit / browser | gaps-c, all 9 cases PASS; sub-point NOT TESTED: the drawn calendar (headless Chromium draws none) |
| `scripts/integration/db-check-cleanup.test.ts` | 13 | — | N/A | — | db-check runner cleanup helper |
| `scripts/integration/db-check-guard.test.ts` | 14 | — | N/A | — | db-check guard helper |
| `scripts/integration/eleven-approve-receipt.test.ts` | 5 | GapsElevenTests, PlanTests, ImportTests | PORTED ▲ | unit / SQL Server | gaps-b (legacy days on the test clock); stand-in cases N/A |
| `scripts/integration/eleven-writer.test.ts` | 3 | GapsElevenTests, PlanTests, ImportTests | PORTED ▲ | unit / SQL Server | gaps-b: real PolicyActions.IssuePolicyAsync; stand-in N/A |
| `scripts/integration/facultative-cancel-report.test.ts` | 7 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/facultative-claim-chain.test.ts` | 6 | ClaimRegistrationTests, ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/facultative-preflight.test.ts` | 3 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/facultative-repeat.test.ts` | 4 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/facultative-selected.test.ts` | 3 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/facultative-xl.test.ts` | 4 | ReinsuranceChainTests, FacultativeXlTests | PORTED ▲ | SQL Server | gaps-c: all five XL together |
| `scripts/integration/fd-app-concurrency-db-check.test.ts` | 33 | (LegacyFdRaceTests) | N/A ▲ | SQL Server | runner over a fake DB and child processes; script mapped in docs/legacy-db-checks-policies.md |
| `scripts/integration/fd-concurrency-db-check.test.ts` | 22 | (LegacyFdRaceTests) | N/A ▲ | SQL Server | race-engine runner over a double; script mapped in docs/legacy-db-checks-policies.md |
| `scripts/integration/fd-db-check.test.ts` | 20 | (LegacyFdRequestGuardTests) | N/A ▲ | SQL Server | fd-requests-db-check runner guards; script mapped in docs/legacy-db-checks-policies.md |
| `scripts/integration/fd-month-report-db-check.test.ts` | 31 | (LegacyFdMonthReportTests) | N/A ▲ | SQL Server | runner/oracle over a fake store; the script itself now mapped in docs/legacy-db-checks-accounting.md |
| `scripts/integration/fd-net-transfer-chain.test.ts` | 99 | GapsFdClaimTests, GapsFdClaimSettingTests, GapsFdRefundReversalTests, GapsFdPanelChainTests, GapsReceiptChainTests, GapsLaterDayCardTests, GapsPaymentPageTests, ClaimPaymentTests, FdRequestTests, FdRequestClosedMonthTests, LegacyFdRaceTests, LegacyFdRequestGuardTests, LegacyPremiumPrecisionTests, FdApprovalFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-b covers the business cases; the read failures (refund day rate, group grants in the quick and binding checks, the other permission reads) are FdApprovalFaultTests (b4a3c32). N/A: forged refund/reversal forms (no such fields) |
| `scripts/integration/fd-panel-db-check.test.ts` | 21 | (LegacyFdPanelReaderTests) | N/A ▲ | SQL Server | runner guards; script mapped in docs/legacy-db-checks-policies.md |
| `scripts/integration/fd-panel.test.ts` | 25 | FdPanelTests, LegacyFdPanelReaderTests, PaymentFlowTests, FdPanelFaultTests, ReportFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a (open question Q1: which exception types become "could not be read") |
| `scripts/integration/financial-picture.test.ts` | 3 | — | N/A | — | db-check helper (financial-picture.ts); LegacyDbCheckClaimTests has its own |
| `scripts/integration/form1-broker-page.test.ts` | 3 | RegulatoryPagesTests | PORTED | HTTP |  |
| `scripts/integration/form1-broker-unimpaired.test.ts` | 2 | ImpairmentReportTests | PORTED | SQL Server |  |
| `scripts/integration/form6-fixes.test.ts` | 8 | RegulatoryPagesTests, Form6Tests, Form6FixesTests | PORTED ▲ | unit / HTTP | gaps-c; "nothing read" N/A |
| `scripts/integration/history-fingerprint.test.ts` | 18 | — | N/A | — | history fingerprint / compare script |
| `scripts/integration/impairment-debtors.test.ts` | 13 | ImpairmentReportTests, ImpairmentDebtorsOrderTests | PORTED ▲ | unit / SQL Server | gaps-c: eight orders; read instrumentation N/A |
| `scripts/integration/impairment-eur-cent-reversal.test.ts` | 2 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-eur-cent-settlement.test.ts` | 2 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-eur-cent.test.ts` | 2 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-exact-n14.test.ts` | 4 | ImpairmentReportTests | PORTED | SQL Server | "old JS-number path" N/A |
| `scripts/integration/impairment-fx-form1.test.ts` | 3 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-half-tetri.test.ts` | 2 | ImpairmentReportTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-history.test.ts` | 7 | ImpairmentCurrencyTests | PORTED | SQL Server | model/stub cases N/A |
| `scripts/integration/impairment-reversal.test.ts` | 5 | ImpairmentReportTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-usd-dated.test.ts` | 3 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-usd-eur-get-failure.test.ts` | 2 | ImpairmentCurrencyTests, ImpairmentPagesTests | PORTED | SQL Server / HTTP |  |
| `scripts/integration/impairment-usd-eur-get.test.ts` | 3 | ImpairmentCurrencyTests, ImpairmentPagesTests | PORTED | SQL Server / HTTP | cells via the service workbook; the GET route itself in ImpairmentPagesTests |
| `scripts/integration/impairment-usd-eur.test.ts` | 3 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-usd-full-payment.test.ts` | 2 | ImpairmentCurrencyTests | PORTED | SQL Server | workbook from the service, not the GET |
| `scripts/integration/impairment-usd-full-reversal.test.ts` | 3 | ImpairmentCurrencyTests | PORTED | SQL Server | workbook from the service, not the GET |
| `scripts/integration/impairment-usd-reduction-surplus.test.ts` | 2 | ImpairmentCurrencyTests, ImpairmentPagesTests | PORTED | SQL Server / HTTP |  |
| `scripts/integration/impairment-usd-xlsx.test.ts` | 2 | ImpairmentReportTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-usd.test.ts` | 3 | ImpairmentCurrencyTests | PORTED | SQL Server |  |
| `scripts/integration/impairment-xlsx-get.test.ts` | 7 | ImpairmentPagesTests, ReportPageFaultTests | PORTED ▲ | HTTP | gaps-a |
| `scripts/integration/import-plan-confirmation.test.ts` | 22 | ImportTests, ImportClosedMonthTests, PaymentFlowTests, GapsImportPlanTests | PORTED ▲ | SQL Server / HTTP | gaps-b; stand-in N/A |
| `scripts/integration/late-commission-cancel.test.ts` | 8 | PremiumReportPageTests, PolicyPremiumPagesTests | PORTED | HTTP |  |
| `scripts/integration/late-commission-page.test.ts` | 1 | PremiumReportPageTests | PORTED | HTTP |  |
| `scripts/integration/late-premium-cession.test.ts` | 3 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/line-claim-isolation.test.ts` | 6 | LineClaimIsolationTests | PORTED | SQL Server |  |
| `scripts/integration/month-report-full-main.test.ts` | 4 | (LegacyMonthReportPrecisionTests) | N/A ▲ | SQL Server | month-report precision check main() runner |
| `scripts/integration/month-report-fx-lookup.test.ts` | 5 | MonthReportFxLookupTests | PORTED | SQL Server |  |
| `scripts/integration/month-report-precision-db-check.test.ts` | 26 | (LegacyMonthReportPrecisionTests, LegacyMonthReportPagesTests) | N/A ▲ | SQL Server / HTTP | runner/child-process/BigInt channel; the script now mapped in docs/legacy-db-checks-accounting.md |
| `scripts/integration/monthly-report-1000.test.ts` | 2 | MonthlyReportThousandTests | PORTED | SQL Server |  |
| `scripts/integration/nbg-rate-cache.test.ts` | 8 | NbgRateCacheTests | PORTED ▲ | SQL Server | gaps-c: fake bank AnsweringNbg; stub/socket cases N/A |
| `scripts/integration/negative-commission-report.test.ts` | 7 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/negative-endorsement-claim.test.ts` | 6 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/negative-endorsement.test.ts` | 7 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/outside-repo.test.ts` | 1 | — | N/A | — | outside-repo path helper |
| `scripts/integration/payment-plan-forms-render.test.ts` | 3 | GapsPaymentPlanFormsTests | PORTED ▲ | HTTP | gaps-b; React-render case N/A |
| `scripts/integration/payment-plan-validation.test.ts` | 3 | PlanTests, CancelIssueTests | PORTED | unit / SQL Server |  |
| `scripts/integration/payment-shared-page.test.ts` | 71 | AdvanceTransferTests, ReceiptTests, ReceiptArithmeticTests, PaymentFlowTests, LegacyClientCreditTests, LegacyPremiumPrecisionTests, PaymentRegressionTests, GapsPaymentPageTests, GapsCardDisplayTests, GapsCardStoredMoneyTests, GapsCardPositionTests, GapsPaymentPageForeignRowTests, GapsCardLegacyDayTests, GapsPaymentPageBankOnceTests | PORTED ▲ | SQL Server / HTTP | gaps-b covers the rest; on the rendered card at 03.11.2026 (test clock): receivable 1–5, the 7 premium-position cases, commission incl. "0 % shows no rows" and the intermediaries' label; the bank asked exactly once for an empty foreign policy (b4a3c32) |
| `scripts/integration/period-close-permission.test.ts` | 11 | PeriodsPageTests, PeriodCloseConcurrencyTests, PeriodCloseFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a; new-year case also on the page via the test clock |
| `scripts/integration/permission-actions.test.ts` | 6 | PermissionPanelTests, PermissionStorageTests, PermissionFaultTests, PageFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a |
| `scripts/integration/permission-panel.test.ts` | 13 | PermissionPanelTests, PermissionFaultTests, PageFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a: 14 reads |
| `scripts/integration/permission-storage.test.ts` | 16 | PermissionStorageTests, LegacyPermissionsDbCheckTests, PermissionFaultTests | PORTED ▲ | SQL Server | gaps-a; defect D1 fixed |
| `scripts/integration/policy-approve-permission.test.ts` | 10 | PolicyApprovalTests, LegacyApprovalAnswersTests, PolicyFaultTests | PORTED ▲ | SQL Server | gaps-a |
| `scripts/integration/policy-cancel-asuser.test.ts` | 6 | PolicyPremiumCancelWriterTests, PolicyPremiumPagesTests, GapsPolicyCancelAsUserTests, GapsReportPagesTests, GapsReportPagesTests (cession read failing) | PORTED ▲ | SQL Server / HTTP | gaps-b covers the rest; the monthly page when the cession read fails once: GapsReportPagesTests.The_monthly_page_when_the_cession_read_fails_once_… (b4a3c32) |
| `scripts/integration/policy-premium-cancel-writer.test.ts` | 2 | PolicyPremiumCancelWriterTests | PORTED | SQL Server |  |
| `scripts/integration/policy-premium-combined.test.ts` | 1 | PolicyPremiumCancelWriterTests | PORTED | SQL Server |  |
| `scripts/integration/policy-premium-page.test.ts` | 3 | PolicyPremiumPagesTests | PORTED | HTTP |  |
| `scripts/integration/policy-premium-report.test.ts` | 12 | PolicyPremiumReportTests, PolicyPremiumPagesTests | PORTED | SQL Server / HTTP |  |
| `scripts/integration/policy-premium-xlsx.test.ts` | 6 | PolicyPremiumPagesTests | PORTED | HTTP |  |
| `scripts/integration/precision-cases-real-actions.test.ts` | 5 | LegacyPremiumPrecisionTests | PORTED | HTTP | adapter-fault text N/A |
| `scripts/integration/precision-fd-panel-chain.test.ts` | 6 | LegacyPremiumPrecisionTests, LegacyFdPanelReaderTests, PaymentFlowTests, GapsReceiptChainTests, GapsFdPanelRightsTests, GapsLaterDayCardTests | PORTED ▲ | SQL Server / HTTP | gaps-b |
| `scripts/integration/precision-month-report-cession.test.ts` | 4 | PremiumReportCessionTests | PORTED | SQL Server | report level; page/panel only through PremiumReport.ReportMoney |
| `scripts/integration/precision-month-report-ibnr.test.ts` | 4 | PremiumReportClaimsTests | PORTED | SQL Server | report level |
| `scripts/integration/precision-month-report-paid.test.ts` | 5 | PremiumReportClaimsTests | PORTED | SQL Server | report level |
| `scripts/integration/precision-month-report-rbns.test.ts` | 5 | PremiumReportClaimsTests | PORTED | SQL Server | report level |
| `scripts/integration/precision-month-report-ri-shares.test.ts` | 6 | PremiumReportClaimsTests | PORTED | SQL Server | report level |
| `scripts/integration/precision-month-report-treaty.test.ts` | 4 | PremiumReportCessionTests | PORTED | SQL Server | report level |
| `scripts/integration/precision-month-report.test.ts` | 7 | PremiumReportPrecisionTests, GapsMonthReportReversalTests | PORTED ▲ | SQL Server | gaps-b |
| `scripts/integration/premium-precision-db-check.test.ts` | 45 | (LegacyPremiumPrecisionTests) | N/A ▲ | HTTP | runner/child-process tests; script mapped in docs/legacy-db-checks-policies.md |
| `scripts/integration/premium-report-page-joint.test.ts` | 2 | PremiumReportPageTests, GapsReportPagesTests | PORTED ▲ | HTTP | gaps-b: rows by the real import/approval/late receipt |
| `scripts/integration/premium-report-page.test.ts` | 3 | PremiumReportPageTests | PORTED | HTTP | own rows (355.07/3 598.03 instead of 358.36/3 674.31); React-loading case N/A |
| `scripts/integration/print-policy-money.test.ts` | 9 | PrintPolicyTests, GapsPrintPolicyTests | PORTED ▲ | HTTP | gaps-b; defect (JSON-number vehicle value) fixed |
| `scripts/integration/receipt-permission.test.ts` | 12 | ReceiptTests, PaymentFlowTests, PolicyFlowTests, PolicyFaultTests, PageFaultTests, GapsPaymentPageTests | PORTED ▲ | SQL Server / HTTP | gaps-a + gaps-b |
| `scripts/integration/recovery-permission.test.ts` | 9 | ClaimRecoveryTests, ClaimPagesTests, ClaimFaultTests, ClaimPageFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a |
| `scripts/integration/regulatory-page.test.ts` | 12 | RegulatoryPagesTests | PORTED | HTTP |  |
| `scripts/integration/reinsurance-history.test.ts` | 23 | ReinsuranceHistoryTests | PORTED | SQL Server | fake-model cases N/A |
| `scripts/integration/releasable-exact.test.ts` | 8 | LegacyClientCreditTests, AdvanceTransferTests, GapsReleasableExactTests | PORTED ▲ | SQL Server | gaps-b; "1234.5 as text" N/A |
| `scripts/integration/report-1000-page-session.test.ts` | 2 | PremiumReportPageTests | PORTED | HTTP |  |
| `scripts/integration/report-bulk-reads.test.ts` | 4 | MonthlyReportThousandTests, LegacyPermissionsDbCheckTests, ReportFaultTests, ReportPageFaultTests | PORTED ▲ | SQL Server / HTTP | gaps-a; statements-per-policy N/A (shown for the report instead) |
| `scripts/integration/report-xlsx-layout.test.ts` | 5 | ReportWorkbookLayoutTests | PORTED | SQL Server |  |
| `scripts/integration/ri-commission-report.test.ts` | 5 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/surplus-chain.test.ts` | 10 | ReinsuranceChainTests | PORTED | SQL Server | fake-column case N/A |
| `scripts/integration/treaty-form-date.test.ts` | 2 | TreatyPagesTests, BrowserFoundationTests, TreatyFormDateTests | PORTED ▲ | HTTP / browser | gaps-c (HTTP + source scan of all .cshtml) |
| `scripts/integration/two-quota-cent.test.ts` | 5 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/usd-facultative-report.test.ts` | 6 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/usd-quota-cession.test.ts` | 4 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/usd-ri-commission-page.test.ts` | 1 | PremiumReportPageTests | PORTED | HTTP |  |
| `scripts/integration/usd-ri-commission-report.test.ts` | 6 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/usd-xl-prior-rate.test.ts` | 7 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/usd-xl-valuation.test.ts` | 6 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/integration/xl-treaty-premium.test.ts` | 7 | ReinsuranceChainTests | PORTED | SQL Server |  |
| `scripts/m1-runtime/core.test.ts` | 41 | — | N/A | — | m1-runtime tool |
| `scripts/m1-runtime/spawn-grandchild.test.ts` | 6 | — | N/A | — | m1-runtime child processes |
| `scripts/migrate-core.test.ts` | 13 | (SchemaTests cover the new migrator) | N/A | SQL Server | migrate-core (PostgreSQL migration runner) |
| `scripts/recovery/capture.test.ts` | 9 | — | N/A | — | backup/restore capture |
| `scripts/recovery/compare.test.ts` | 8 | — | N/A | — | backup/restore compare |
| `scripts/recovery/guard.test.ts` | 3 | — | N/A | — | backup/restore guard |
| `scripts/regress-db/acceptance.test.ts` | 24 | (R1–R3: LegacyDbCheckRegressTests, SubrogationReportStoreTests) | N/A | SQL Server | regress-db runner over fakes; mapped in docs/legacy-db-checks-schema-claims.md |
| `scripts/regress-db/catalog.test.ts` | 46 | (K1–K9: LegacyDbCheckSchemaTests) | N/A | SQL Server | regress-db catalog runner over a model; mapped in docs |
| `scripts/regress-db/guard.test.ts` | 28 | — | N/A | — | regress-db guard |
| `scripts/regress-db/isolation.test.ts` | 38 | (R5: LegacyDbCheckRegressTests) | N/A | SQL Server | regress-db isolation runner; mapped in docs |
| `scripts/regress-db/negative.test.ts` | 14 | (N1–N6: LegacyDbCheckSchemaTests) | N/A | SQL Server | regress-db negative runner; mapped in docs |
| `scripts/regress-db/runner.test.ts` | 8 | — | N/A | — | regress-db runner |
| `scripts/regress-db/session.test.ts` | 8 | — | N/A | — | regress-db session wrapper |
| `scripts/subrogation-migration.test.ts` | 10 | LegacyDbCheckRegressTests, LegacyDbCheckSchemaTests, SubrogationCaseTests | PORTED | SQL Server | the 0030 rules checked on the real database instead of in the SQL text; "$$ quoting" and "number to itself" N/A |
| `src/compliance/client-risk.test.ts` | 10 | ClientRiskTests, ComplianceParityTests | PORTED | unit | 10/10 |
| `src/compliance/kyc.test.ts` | 5 | KycTests, ComplianceParityTests | PORTED | unit | 5/5 |
| `src/compliance/sanctions.test.ts` | 6 | SanctionsTests (Domain), ComplianceParityTests | PORTED | unit / HTTP | 6/6 |
| `src/compliance/suspension.test.ts` | 8 | SuspensionTests, ComplianceParityTests | PORTED | unit | 8/8 |
| `src/finance/cancel-preview.test.ts` | 3 | CancelPreviewTests, ParityCoreTests | PORTED | unit | 3/3 |
| `src/finance/cancellation-forward.test.ts` | 4 | CancellationForwardTests | PORTED | unit | 4/4 |
| `src/finance/dates.test.ts` | 2 | DatesTests | PORTED | unit | 2/2 |
| `src/finance/deductible.test.ts` | 8 | DeductibleTests, ParityCoreTests | PORTED | unit | 8/8 |
| `src/finance/finance.test.ts` | 34 | FinanceTests, Parity*Tests | PORTED | unit | 34/34 |
| `src/finance/impairment-exact.test.ts` | 2 | ImpairmentExactTests | PORTED | unit |  |
| `src/finance/impairment.test.ts` | 10 | ImpairmentTests, ParityReceivableTests | PORTED | unit | 10/10 |
| `src/finance/payment-authorization.test.ts` | 7 | PaymentAuthorizationTests, ParityReceivableTests | PORTED | unit | 7/7 |
| `src/finance/plan.test.ts` | 3 | PlanTests, ParityCoreTests | PORTED | unit | 3/3 |
| `src/finance/reinsurance.test.ts` | 10 | ReinsuranceTests, ParityReinsuranceSubrogationTests | PORTED | unit | 10/10 |
| `src/finance/subrogation-report.test.ts` | 35 | SubrogationReportTests, ParityReinsuranceSubrogationTests | PORTED | unit | 35 → 36 |
| `src/finance/subrogation.test.ts` | 3 | SubrogationTests (Domain/Finance), ParityReinsuranceSubrogationTests | PORTED | unit | rule loop, negative controls, old-head regressions |
| `src/lib/close-deadline.test.ts` | 4 | CloseDeadlineTests | PORTED | unit | 4/4 |
| `src/lib/date-text.test.ts` | 4 | DateTextTests | PORTED | unit | 4/4 |
| `src/lib/known.test.ts` | 3 | QueryParamsTests | PORTED | unit | 3/3 |
| `src/lib/month.test.ts` | 2 | MonthTests | PORTED | unit | 2/2 |
| `src/lib/numbering.test.ts` | 5 | NumberingTests | PORTED | unit | 5/5 |
| `src/lib/permissions.test.ts` | 24 | PermissionsTests | PORTED | unit | 24/24 |
| `src/lib/phone.test.ts` | 5 | PhoneTests | PORTED | unit | 5/5 |
| `src/lib/regulatory/excel-cents.test.ts` | 4 | ExcelCentsTests, RegulatoryPagesTests (422) | PORTED | unit / HTTP |  |
| `src/lib/regulatory/excel-precision.test.ts` | 5 | ExcelPrecisionTests, RegulatoryPagesTests | PORTED | unit / HTTP |  |
| `src/lib/regulatory/financial-annex.test.ts` | 5 | FinancialAnnexTests | PORTED | unit | 5/5 |
| `src/lib/regulatory/form6.test.ts` | 16 | Form6Tests | PORTED | unit | 16/16 |
| `src/lib/regulatory/reserves-placement.test.ts` | 8 | ReservesPlacementTests | PORTED | unit | 8/8 |
| `src/lib/subrogation-report-store.test.ts` | 14 | SubrogationReportStoreTests | PORTED | SQL Server | merged into 9 tests; N/A: the out-of-id-order mock history and the read-only repeatable-read snapshot (no SQL Server equivalent; see docs open question 2) |
| `src/lib/subrogation.test.ts` | 19 | SubrogationCaseTests, Common/SubrogationTests | PORTED | unit / SQL Server | 19 → 20 |
| `src/lib/xlsx-layout.test.ts` | 9 | XlsxLayoutTests | PORTED | unit | 9/9 |

**Totals (table 1):** 161 files, 1598 legacy cases — PORTED 135, PARTIAL 0, N/A 26, MISSING 0 (v1: PORTED 101, PARTIAL 32, N/A 26, MISSING 2).

## 5. ბაზის სქემა (`db/migrations` → `database/migrations`)

ძველი 37 მიგრაციის საბოლოო მდგომარეობა (pg_dump `17c1f1f`-ზე: 59 ცხრილი, 34 ფუნქცია, 63 ტრიგერი) ახალ 8 ფაილშია ჩაწერილი. ცალკე „განახლების“ მიგრაციები (0022, 0031 და ა.შ.) ახალ ბაზაში საჭირო აღარ არის: ახალი ბაზა პირდაპირ საბოლოო მდგომარეობით იქმნება, ხოლო ძველი მონაცემები `import-postgres`-ით გადადის.

| ძველი მიგრაცია | რა შემოაქვს | ახალი ადგილი | შემოწმება | სტატუსი |
|---|---|---|---|---|
| 0001_foundation | ცნობარები, მომხმარებლები, სესიები, აუდიტი, კლიენტები | 0001_foundation, 0006_audit | SchemaTests (სუფთა აგება, ქართული ტექსტი, case-sensitive გასაღებები, აუდიტი) | DONE |
| 0002_audit_log_append_only | აუდიტის ჟურნალი მხოლოდ ემატება | 0007_grants | `Application_role_cannot_change_ledgers_journal_or_schema` | DONE |
| 0003_policies | ხელშეკრულება, პოლისი, პრემიის ჟურნალი, გრაფიკი, გადახდები | 0002_policies_billing | SchemaTests, ConcurrencyTests, PolicyApprovalTests, ReceiptTests, PaymentConcurrencyTests | DONE |
| 0004_no_suspension_and_offsets | შეჩერება არ არსებობს; ურთიერთჩათვლა | 0002_policies_billing | CancelIssueTests, LegacyAcceptanceGapsTests | DONE |
| 0005_products | პროდუქტები, დაფარვები, ნუმერაცია | 0001_foundation | `Number_series_refuses_when_it_runs_out` | DONE |
| 0006_schema_migration_log | მიგრაციების ჟურნალი | `Migrator` + `core.schema_migration` | `Clean_build_applies_every_migration_and_records_it`, `Repeated_migration_changes_nothing` | DONE |
| 0007_review_fixes | რევიუს შესწორებები (შეზღუდვები) | 0001–0003 | SchemaTests | DONE |
| 0008_approval_and_periods | პოლისის დამტკიცება, დახურული თვეები | 0001 (accounting_period, assert_period_open), 0002 | `Movement_in_a_closed_month_is_refused_and_the_month_cannot_reopen`, ConcurrencyTests | DONE |
| 0009_policy_change_note | ცვლილების აღწერა | 0002 | EndorsementTests | DONE |
| 0010_payment_reversal_and_refund | შებრუნება სარკისებრი ჩანაწერით, ზედმეტის დაბრუნება | 0002 | ReceiptTests, AdvanceTransferTests, PaymentRegressionTests | DONE |
| 0011_claims | ზარალი, რეზერვი, ანაზღაურება, ამოღება | 0003_claims_subrogation | ClaimRegistrationTests, ClaimReserveTests, ClaimPaymentTests, ClaimRecoveryTests, LegacyDbCheckClaimTests | DONE |
| 0012_settings_and_product_texts | კომპანიის რეკვიზიტები, პროდუქტის ტექსტები | 0001 | SettingsTests, PrintPolicyTests (ClosedMonthAndPrintTests) | DONE |
| 0013_complaints | პრეტენზიების რეესტრი | 0003 | IntermediaryComplaintTests | DONE |
| 0014_client_risk | კლიენტის რისკის შეფასება | 0005_compliance | PartyCardTests, ComplianceServicesTests | DONE |
| 0015_reinsurance | გადაზღვევის ხელშეკრულებები და გადაცემები | 0004_reinsurance | `Two_placements_on_one_premium_row_never_exceed_it`, ReinsuranceChainTests, FacultativeConcurrencyTests, TreatyPagesTests | DONE |
| 0016_beneficial_owners | ბენეფიციარი მესაკუთრეები | 0005 | PartyCardTests | DONE |
| 0017_sanctions | გაეროს სანქციების სია და შემოწმება | 0005 | SanctionsTests | DONE |
| 0018_schedule_history | გრაფიკის ისტორია (კორექტირებები) | 0002 (installment_adjustment, schedule_reconciliation) | EndorsementTests, CancellationForwardTests, შეჯერება (DataMigrationTests) | DONE |
| 0019_aml_cases | AML შემთხვევები, STR, ფმს-ის შეჩერება | 0005 | AmlTests, AmlSuspensionAcceptanceTests | DONE |
| 0020_reinsurance_limits_and_schedule_control | გადაზღვევის ლიმიტები, გრაფიკის კონტროლი | 0002, 0004 | ReinsuranceChainTests, PolicyApprovalTests | DONE |
| 0021_xol_layers_and_suspension_target | XoL ფენები, შეჩერების ობიექტი | 0004, 0005 (`resolve_suspension_target`) | ReinsuranceChainTests, AmlSuspensionAcceptanceTests | DONE |
| 0022_suspension_target_transition | ძველი შეჩერებების გადაყვანა | 0005 (`fms_suspension_untargeted`) | იმპორტი + შეჯერება | DONE |
| 0023_serialize_cession_guards | გადაცემის დაცვის სერიალიზაცია | 0004 (`core.xact_lock`) | `Two_placements_on_one_premium_row_never_exceed_it` (+ საკონტროლო) | DONE |
| 0024_receipt_allocation_guard | შემოსვლის განაწილების დაცვა | 0002 (PA001/PA002) | `Two_allocations_of_one_receipt_never_exceed_it` | DONE |
| 0025_settings_and_period_audit | პარამეტრებისა და თვის დახურვის აუდიტი | 0006_audit | `Audit_records_who_changed_what_and_skips_password_and_no_op` | DONE |
| 0026_number_series_limit | ნომერი არასოდეს მოიჭრება | 0001 (`core.next_number`) | `Number_series_refuses_when_it_runs_out` | DONE |
| 0027_number_series_audit | ნუმერაცია — კომპანიის პარამეტრი | 0001, 0006 | SettingsTests, LegacyDbCheckNumberingTests | DONE |
| 0028_loss_event | ზარალის მოვლენა სახეობებად | 0003 | ClaimRegistrationTests, LegacyDbCheckClaimTests | DONE |
| 0030_subrogation | რეგრესი | 0003 (`record_case_money`, `subrogation_totals`, `subrogation_room`) | SubrogationCaseTests, SubrogationReportStoreTests, LegacyDbCheckRegressTests | DONE |
| 0031_schema_migration_read_only | მიგრაციების ჟურნალი მხოლოდ წასაკითხად | 0007_grants, `Migrator` | `Application_role_reads_the_journal`, `Application_role_cannot_change_ledgers_journal_or_schema` | DONE |
| 0032_permissions | უფლებები და ჯგუფები | 0001 | PermissionStorageTests, PermissionPanelTests, LegacyPermissionsDbCheckTests | DONE |
| 0033_claim_payment_reversal | ანაზღაურების შებრუნება | 0003 | ClaimReversalTests | DONE |
| 0034_claim_reversal_returned_date | დაბრუნების თარიღის შეზღუდვა | 0003 | ClaimReversalTests, LegacyDbCheckClaimTests | DONE |
| 0035_claim_reserve_request | რეზერვის ცვლილება დამტკიცებით | 0003 | ClaimReserveTests, LegacyDbCheckClaimTests | DONE |
| 0036_claim_payment_request | ანაზღაურება ზღვარს ზემოთ — ფინ. დირექტორი | 0003 | ClaimPaymentTests, LegacyFdRaceTests, LegacyFdRequestGuardTests | DONE |
| 0037_premium_refund_request | დაბრუნება ზღვარს ზემოთ — ფინ. დირექტორი | 0002 | FdRequestTests, LegacyFdRaceTests, LegacyFdRequestGuardTests | DONE |
| 0038_receipt_reversal_request | შებრუნება ზღვარს ზემოთ — ფინ. დირექტორი | 0002 | FdRequestTests, LegacyFdRaceTests, LegacyFdRequestGuardTests | DONE |
| — | მონაცემების გადატანის ჟურნალი (ახალი) | 0008_data_import | DataMigrationTests | DONE |
| — (Codex R02) | SG034-ის „საშვი“, რომელსაც core_app ვერ ყალბდება | 0009_case_money_pass | SubrogationCaseTests.R02_*, SchemaTests | DONE |
| — (მფლობელი, 23.10.2026) | სანქციების ცრუ დამთხვევის გადაწყვეტილების ხელახლა გამოყენება | 0010_sanctions_reuse | SanctionsReuseTests, ScreeningFingerprintTests | DONE |
| ძველი PR #362 (0039) | გადამზღვევლის საკომისიოს ჩათვლის ჟურნალი | 0011_ri_commission_settlement | RiCommissionSettlementTests, RiCommissionPageTests | DONE |
| H46 (ძველი docs/07 §4.5) | ლარის ზარალი სავალუტო პოლისზე: კურსის ფიქსაცია, ჩათვლის კურსი | 0012_claim_gel_currency | ClaimGelOnForeignPolicyTests, ClaimGelCardTests, CrossCurrencySetOffTests | DONE |
| — (Codex R005-03) | ზარალის snapshot-ის NULL-უსაფრთხო დაცვა (0012-ის ტრიგერის ჩანაცვლება) | 0013_claim_snapshot_null_safe | ClaimGelOnForeignPolicyTests.The_snapshot_is_neither_wiped_nor_given_… (core_app) | DONE |

## 6. ძველი სკრიპტები (scripts/)

Source: the inventory of the legacy scripts (207 files: db-check 45, evidence 5, ops 4, dev-uat 13, helper 140). Doc abbreviations: **SC** = `docs/legacy-db-checks-schema-claims.md`, **PO** = `docs/legacy-db-checks-policies.md`, **AC** = `docs/legacy-db-checks-accounting.md`. Counts are the docs' table rows (one row may group several cases), recounted here per section; they agree with each doc's own totals (SC PASS 120 · N/A 15 · NOT TESTED 3 · OTHER GROUP 10; PO PASS 86 · N/A 12 · OTHER GROUP 3; AC PASS 58 · N/A 9). Every OTHER GROUP row is proved PASS by another group's test (listed in the "OTHER GROUP rows after …" sections of SC and PO); "→ PASS" marks them.

### 6a. db-check scripts (45)

| Script | Doc | Verdict rows | Status |
|---|---|---|---|
| `acceptance-gaps-regression.ts` | PO | PASS 4 | MAPPED |
| `address-guard-check.ts` | PO | PASS 6 | MAPPED |
| `address-guard-periods-check.ts` | AC | PASS 7 · N/A 1 | MAPPED |
| `answer-parallel-check.ts` | PO + AC | PO: OTHER GROUP 1 → PASS; AC: PASS 5 | MAPPED |
| `approval-answers-regression.ts` | PO | PASS 6 | MAPPED |
| `claim-payee-race.ts` | SC | PASS 8 | MAPPED |
| `claim-reserve-db-check.ts` | SC | PASS 16 · N/A 1 | MAPPED |
| `claim-reversal-db-check.ts` | SC | PASS 12 · N/A 1 | MAPPED |
| `client-credit-regression.ts` | PO | PASS 18 · N/A 2 | MAPPED |
| `close-period-regression.ts` | AC | PASS 8 · N/A 1 | MAPPED |
| `db-acceptance.ts` | SC | PASS 10 · OTHER GROUP 4 → PASS · NOT TESTED 1 | MAPPED, 1 NOT TESTED |
| `fd-app-concurrency-db-check.ts` | PO | PASS 5 · N/A 1 | MAPPED (PREPARED, NOT RUN in legacy) |
| `fd-concurrency-db-check.ts` | PO | PASS 6 · N/A 1 | MAPPED (PREPARED, NOT RUN in legacy) |
| `fd-month-report-db-check.ts` | AC | PASS 10 · N/A 3 | MAPPED (PREPARED, NOT RUN in legacy) |
| `fd-panel-db-check.ts` | PO | PASS 3 · N/A 1 | MAPPED (PREPARED, NOT RUN in legacy) |
| `fd-requests-db-check.ts` | PO | PASS 4 · N/A 1 | MAPPED (PREPARED, NOT RUN in legacy) |
| `month-report-precision-db-check.ts` | AC | PASS 12 · N/A 2 | MAPPED (PREPARED, NOT RUN in legacy) |
| `number-concurrency-regression.ts` | SC + PO | SC: PASS 7 · OTHER GROUP 2 → PASS; PO: PASS 2 · OTHER GROUP 1 → PASS | MAPPED |
| `number-limit-check.ts` | SC + AC | SC: PASS 7 · OTHER GROUP 4 → PASS; AC: PASS 1 | MAPPED |
| `p6-close-test.ts` | AC | PASS 14 · N/A 2 | MAPPED |
| `parallel-users-probe.ts` | PO (Dev/UAT) | PASS 1 | MAPPED |
| `permissions-db-check.ts` | PO + AC | PO: PASS 14 · N/A 1 · OTHER GROUP 1 → PASS; AC: PASS 1 | MAPPED |
| `pool-end-check.mjs` | PO (Dev/UAT) | N/A 1 (postgres.js pool behaviour) | MAPPED |
| `premium-precision-db-check.ts` | PO | PASS 7 · N/A 2 | MAPPED (PREPARED, NOT RUN in legacy) |
| `reg-deactivate-check.ts` | PO (Dev/UAT) | N/A 1 (no deactivation tool: tests make no users in a shared DB) | MAPPED |
| `run-cleanup-check.ts` | PO (Dev/UAT) | PASS 1 (failpoint/hang matrix N/A) | MAPPED |
| `snapshot-consistency-check.ts` | PO (Dev/UAT) | N/A 1 (db-snapshot not ported; BACKUP DATABASE is consistent) | MAPPED |
| `total-loss-regression.ts` | PO | PASS 9 | MAPPED |
| `verify-0025.ts` | SC | PASS 5 | MAPPED |
| `verify-0026-0028.ts` | SC | PASS 6 · N/A 2 | MAPPED |
| `regress-db/check.ts` | SC | PASS 21 · N/A 2 | MAPPED |
| `regress-db/accept.ts` (+acceptance.ts) | SC | PASS 9 · N/A 1 | MAPPED |
| `regress-db/isolate.ts` (+isolation.ts) | SC | PASS 4 · N/A 4 | MAPPED |
| `regress-db/catalog-audit.ts` (+catalog.ts) | SC | PASS 7 · N/A 4 | MAPPED |
| `regress-db/negative-run.ts` (+negative.ts) | SC | PASS 4 | MAPPED |
| `advance-allocation-regression.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `claim-offset-reversal-regression.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `claim-payee-regression.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `endorsement-regression.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `loss-event-check.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `number-settings-check.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `payment-regression.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `schedule-asof-regression.ts` | RG | see docs/legacy-db-checks-regressions.md | MAPPED |
| `db-upgrade-0018-test.ts` | — | — | N/A: PostgreSQL upgrade path from a pre-0018 schema (SC's N/A rule for upgrade paths); the new schema is built whole, old data comes by `import-postgres` |
| `db-upgrade-test.ts` | — | — | N/A: PostgreSQL upgrade path from a pre-0021 schema (free-text suspensions); as above |

Doc rows over the 45: PASS 260 · N/A 36 · NOT TESTED 1 (db-acceptance) · OTHER GROUP 13, all → PASS (the second NOT TESTED pair of SC belongs to `claim-offset-evidence.ts`, kind evidence, below).

### 6b. ops (4), dev-uat (13), evidence (5)

| Script | Kind | New equivalent | Status |
|---|---|---|---|
| `migrate.ts` | ops | `Insurance.Tools migrate [--until]` (Migrator; also re-checks collation, rights, audit triggers) — SchemaTests | DONE |
| `seed.ts` | ops | `seed-admin <user> <name>` (password from INSURANCE_ADMIN_PASSWORD) | DONE, no automated test (DatabaseCommands.SeedAdminAsync is never called by a test) |
| `db-snapshot.ts` | ops | README "სარეზერვო ასლი და აღდგენა": BACKUP DATABASE … WITH CHECKSUM, RESTORE VERIFYONLY (PO: not ported) | N/A (replaced by SQL Server backup; no per-table fingerprint) |
| `m1-runtime-check.ts` | ops | `create-database` + `migrate` + `create-app-login` | N/A (PostgreSQL M1 runtime rehearsal on insurance_close_test) |
| `claude-test-users.ts` | dev-uat | `test-users .env.uat` — TestUsersToolTests (refuses a non-test DB) | DONE |
| `reg-users-inventory.ts` | dev-uat | `users-inventory [prefix]` — TestUsersToolTests | DONE |
| `reg-users-deactivate.ts` | dev-uat | — | N/A (PO: tests make no run users in a shared DB) |
| `claude-login-check.ts` | dev-uat | — (TestUsersToolTests verifies each stored hash against the written password; sign-in: AuthTests) | N/A (no command) |
| `test-db-reset.ts` | dev-uat | TestDatabase fixture: a fresh `ins_it_*` DB per test class (DatabaseAdmin.CreateDatabaseAsync + migrate + CreateAppLoginAsync), dropped after | N/A (replaced) |
| `close-test-db-reset.ts` | dev-uat | same TestDatabase fixture | N/A (replaced) |
| `regress-db/reset.ts` | dev-uat | same; `migrate --until` for an older schema | N/A (replaced) |
| `dev-uat.ts` | dev-uat | README: `dotnet run --project src/Insurance.Web` with `ConnectionStrings__Insurance` | N/A; note: no guard refusing a non-test database name (legacy dev-uat.ts had one) |
| `uat-setup.ts` | dev-uat | `seed-admin` + `test-users` | N/A |
| `uat-state.ts` | dev-uat | — (no read-only policy dump command) | N/A |
| `uat-accountant-claims.ts` | dev-uat | fixture N/A; its check (viewer's direct decide/changeReserve/payClaim refused, nothing written): ClaimPagesTests.A_viewer_posting_the_payment_reserve_or_registration_forms_directly_… | N/A (check covered) |
| `uat-advance-fixtures.ts` | dev-uat | — (tests build their own worlds) | N/A (browser-UAT fixture) |
| `uat-form-choice-fixtures.ts` | dev-uat | — | N/A (browser-UAT fixture) |
| `claim-offset-evidence.ts` | evidence | SC: ClaimTotalLossTests | MAPPED: PASS 4 · NOT TESTED 2 (rule open) |
| `subrogation-mutants.ts` | evidence | SubrogationTests (Domain) `Mutants` theory: each weakened rule must fail | DONE (unit) |
| `integration-evidence.ts` | evidence | — as a script; its actions are tested: PartyTests, PartyCardTests (saveParty, addBeneficialOwner), SettingsTests (saveSettings), ImpairmentPagesTests (report/workbook) | N/A (actions covered) |
| `history-fingerprint.ts` | evidence | `reconcile` (old PostgreSQL vs new SQL Server, DataMigrationTests; skipped without INSURANCE_TEST_PG_WORLD) | DONE: `fingerprint` command (per-table row count + SHA-256), BackupRestoreTests; was: no before/after fingerprint command for an update of a SQL Server database |
| `recovery-observer.ts` | evidence | README restore under another name + `migrate` as a check | DONE: BackupRestoreTests (BACKUP WITH CHECKSUM, RESTORE VERIFYONLY, RESTORE under a new name, fingerprints equal; negative control) |

### 6c. helpers (140), one row per folder

| Folder | Files | New equivalent | Status |
|---|---:|---|---|
| `scripts/` | 4 | claude-test-users-plan.ts → `TestUsers.cs` (TestUsersToolTests); migrate-core.ts → `Migrator` (SchemaTests); http-form.ts → test helpers `Html`, `WebApp.PostFormAsync`, `ReportSite.PostFormAsync`; history-compare.ts → none (with history-fingerprint) | DONE 3, N/A 1 |
| `scripts/integration/` | 77 | offline harness (hooks, registers, fake stores, worlds, race/child launchers) → real SQL Server worlds (`World`, `ClaimWorld`, `ReportWorld`, `GapsSupport`, `LegacyDays`, `Overlap`), the failure-injection seam (`Faults`, `FaultSite`), the test clock (`TestClock`) | N/A (replaced) |
| `scripts/integration/stubs/` | 46 | Next/React/session/DB stand-ins → the real application over HTTP/Playwright; bank stand-ins → `AnsweringNbg`, `RefusingNbg` | N/A (replaced) |
| `scripts/m1-runtime/` | 3 | — (M1 PostgreSQL runtime) | N/A |
| `scripts/recovery/` | 3 | README backup/restore | N/A |
| `scripts/regress-db/` | 7 | runner, guard, session, fake DB; their cases are mapped in SC | N/A |

**Totals (table 5):** 207 files.
- db-check 45: MAPPED in docs 43 (35 in the first three docs: PASS 260 · N/A 36 · NOT TESTED 1 · OTHER GROUP 13 → PASS; 8 in docs/legacy-db-checks-regressions.md: PASS 100 · N/A 7); N/A 2.
- evidence 5: MAPPED 1 (PASS 4 · NOT TESTED 2), DONE 3, N/A 1.
- ops 4: DONE 2 (seed-admin untested), N/A 2.
- dev-uat 13: DONE 2, N/A 11.
- helper 140 (6 folder rows): DONE 3, N/A 137.

## 7. შეჯამება (Summary)

| Table | Total | Status counts (v1 in brackets) |
|---|---:|---|
| 1. Legacy test files | 161 files (1 598 cases) | PORTED 135 [101], PARTIAL 0 [32], N/A 26 [26], MISSING 0 [2] |
| 2. Routes | 55 (54 legacy + /logout) | DONE 55 [55], MISSING 0 |
| 3. Server actions + route handlers | 64 | DONE 64 [63], MISSING 0 [1] |
| 4. Legacy modules | 111 | DONE 80 [80], N/A 31 [31], MISSING 0 |
| 5. Legacy scripts (non-test) | 207 | db-check 45: MAPPED 43, N/A 2 · evidence 5: MAPPED 1, DONE 3, N/A 1 · ops 4: DONE 2, N/A 2 · dev-uat 13: DONE 2, N/A 11 · helper 140: DONE 3, N/A 137 |

## 8. NOT TESTED (მიზეზით; არ ითვლება გავლილად)

1. `claim-offset-evidence.ts` (2 cases): a premium set-off on an ordinary claim, and total-loss rule 15 (b), when the policy already has a cancellation from a later day — today's unpaid premium (273.75) or what the client will finally owe (182.75)? RULE OPEN in legacy `docs/05-kitxvebi.md`; the program keeps the legacy behaviour; owner's decision needed.
2. `db-acceptance.ts` (1 case): attaching an old unmapped suspension — the fixture needs `legacy_unmapped = 1`, written only by the legacy 0022 transition; building it would bypass a trigger (forbidden).
3. `date-input-typing.test.ts` (sub-point): the drawn calendar itself (headless Chromium draws none); the ISO value it gives is tested.
4. `dev-uat.ts`: N/A — the legacy local UAT launcher refused non-test database names; the new program has no launcher (the web app takes its connection from configuration); `test-users` keeps the refusal.
5. **An old-number suspension (`legacy_unmapped = 1`) as a fixture** (23.10.2026, found while porting open PRs #150, #158, #177, #178): `ComplianceFixtures.LegacyUnmappedSuspensionAsync` and `OpenPrWorld.LegacyUnmappedSuspensionAsync` write such a row with the insert trigger `fms_suspension_insert` set aside for that one row on the test's own throwaway database — as the old `scripts/db-acceptance.ts:161` did on its test database. The working agreement says a fixture that bypasses a trigger is reported as SKIP; the tests that use it (AmlTests, AmlSuspensionAcceptanceTests, OpenPrAllocateReceiptTests.PR158, OpenPrTransferTests.PR177/PR178, OpenPrRefundTests.PR150) pass, but they are listed here so they are not read as plain PASS, and item 2 above (NOT TESTED for the same reason) is inconsistent with them. Codex / the owner to decide which reading holds.
6. **H46 — what no owner rule decides** (old program docs/07 §4.5 decides the claim in GEL, the limit and deductible at the occurrence day's rate, the set-off at the payment day's rate; the rest is listed here exactly, each with what the rewrite does — none is counted as a finished rule):
   - (H46-U1) rounding of `limit_gel` / `deductible_gel`: to the tetri, half away from zero (`Money.ToGel`), the design's — OPEN;
   - (H46-U2) a GEL policy's claim has no snapshot (the columns stay NULL) — OPEN;
   - (H46-U3) a total loss across currencies: refused, nothing written (`ClaimGelOnForeignPolicyTests.A_total_loss_across_currencies_…`) — OPEN;
   - (H46-U4) one event or class deductible holding claims in two currencies (an old USD event taking a new GEL coverage): refused — OPEN;
   - (H46-U5) an XL treaty in another currency than the claim (RI003, the old status's open question 8): refused, registration included — OPEN;
   - (H46-U6) GEL claims as a company setting (`claim.currency`, `gel` when none is saved) rather than for every company — OPEN;
   - (H46-U7) what makes a claim "new": the moment it is registered under the setting — OPEN;
   - (H46-U8) the set-off in the policy's currency is not stored on the claim payment (it is the sum of its receipt's allocations) — OPEN;
   - (H46-U9) `offset_rate` / `offset_currency` NULL without a set-off, set on every set-off (a same-currency one included, at its rate) — the design's, OPEN;
   - the set-off itself is rounded down to the cent and split into GEL shares by the running total rounded down (Codex R005-02) — the design's, reviewed by Codex.
7. **The demo / hosting**: nothing was deployed and no test ran on the hosting database (Codex INS-DOTNET-MSSQL-003 forbids it; the owner's request to deploy the merged version waits on the hosting access and on approval of migrations 0009–0012 there).

Known differences (documented, not gaps): `loadRates` answers "failed" for an impossible date (2026-02-30) without asking the bank (legacy sent it to the bank); SQL Server has no read-only transaction — the report pages only read (source review), and could read through a login holding only `core_reader` (owner's choice).

## 9. ძველი პროგრამის ღია PR-ები (Codex INS-DOTNET-MSSQL-003)

Codex-ის სიაში 156 ღია PR-ია: 69 საწყისი ვერსიის (`17c1f1f`) წინაპარია, 87 — არა. ქვემოთ თითოეულია: მოთხოვნა, საწყისი თუ არა, ზედ რაზე დგას, ახალი კოდი, ახალი ტესტი და სტატუსი. „წინაპარი“ ავტომატურად „გადატანილს“ არ ნიშნავს და „არაწინაპარი“ — „დაკარგულს“: თითოეული PR-ის საკუთარი diff-ი (წინა PR-ის head-თან) ცალკეა შემოწმებული. ცხრილის გადამოწმების სახე: კოდისა და ტესტების დათვალიერება (source review) + ქვემოთ დასახელებული ტესტები, რომლებიც სრულ გაშვებაში გადის.

**სტატუსები (23.10.2026):** PORTED 133 (59 წინაპარი + 74 არაწინაპარი), N/A 22, ნაწილობრივ ღია 1 (#363 — ერთ წიგნზე კომბინირებული შემოწმების ნაწილი). წინა რუკის 21 ხარვეზი (ქცევა იყო, სცენარის ტესტი — არა) დაიხურა ტესტებით ნამდვილ SQL Server-ზე, საკონტროლო მუტაციებით.

**გადახრები ძველისგან, რომლებიც განზრახ დარჩა:**
- **#361** — ძველი კოდი JavaScript-ის რიცხვით ითვლიდა და ზოგ თანხას „არაზუსტად“ თვლიდა; ახალი ყველა თანხას ზუსტად (decimal/long თეთრი) ითვლის და გვერდზე აჩვენებს, Excel-ის ფაილს კი მხოლოდ 15 ციფრზე მეტ თანხაზე არ გასცემს (`ImpairmentPrecisionGuardTests`). „არაზუსტი“ რჩება მხოლოდ long-ის გადავსებისას.
- **#350** — R10 ორივე გამშვებში გადის (`MonthReportCases.R10`), ძველის შემოწმების პაკეტის (`_v2`) მთვლელები N/A-ა.
- **#366** — ძველ ტესტში საწყისი ნაშთები და გადამზღვევლის წილები ხელით ჩაწერილი იყო (stand-in); აქ ისინი ნამდვილი ანგარიშებიდან მოდის, ამიტომ IS-ის შესავალი ციფრები ძველი ტესტისგან განსხვავდება და ხელით ხელახლაა დათვლილი (`IncomeStatementReaderTests`-ის კომენტარები). P&C(NL), IS-COM, N2-6 და N1-8 ძველის ციფრებია ზუსტად. ძველის 7 საკონტროლო ვარიანტი C#-ში ტესტებადაა.
- **#209** — #218/#225-ის წესით შესწორებული გრაფიკი 4 × 80-ია (ძველ #209-ზე 100/100/100/20), ამიტომ გადახდამდე ვადაგადაცილებულია 80.00 (ძველში 100.00); გადახდის შემდეგ ციფრები ძველისაა.
- **#200** — საწყისი ვერსიიდან (0035) რეზერვის ცვლილება მოთხოვნა + სხვის მიერ დამტკიცებაა; ტესტი ამ წესს მისდევს.

### How to read it

- **ancestor?** = the PR head is an ancestor of the baseline (from the CSV, re-checked). Y rows were copied with the baseline; their scenario is mapped to the .NET test that MIGRATION-COVERAGE §4 names.
- **builds on / superseded**: most non-ancestor PRs are one stacked chain off old main 5cea59d: #137 → #138 → #140 → … → #206 → #207 → #209 → #210 → #211 → #212 → #214 → #215 → (#216 → #219) and (#218 → #225). Every PR in the chain contains all earlier ones, so each row lists **only that PR's own diff** (its head against the previous PR's head). #363 merges #358/#361/#362/#366/#260; #354 stacks on #350. Those are not counted as separate work.
- The chain was written against old main 5cea59d; the baseline later changed some rules underneath it (reserve change became a request + approval (0035); refunds and reversals above a threshold became financial-director requests (0036–0038); #207/#218 changed what a −80.00 change does in the open month). Rows say "under the baseline rule" where the legacy figures were superseded.
- Test paths: **I/** = tests/Insurance.Integration.Tests/, **W/** = tests/Insurance.Web.Tests/, **D/** = tests/Insurance.Domain.Tests/. **PA** = src/Insurance.Application/Services/PaymentActions.cs.
- **Status**: PORTED = a named .NET test exercises the scenario (figures may differ — then marked "equivalent"); BEHAVIOUR PRESENT – SCENARIO TEST NOT PORTED = the .NET code has the branch/rule but no .NET test exercises this scenario (these are the gaps); N/A = docs, Node/PG process tooling, demo deployment; OPEN / IN PROGRESS = named.
- Evidence kind for this map: source review + grep of the .NET tests only. No test was run for this map.

### Map (descending PR number)

| PR | title (short) | ancestor? | kind | concrete requirement / scenario | builds on / superseded | .NET code | .NET test (file + test) | status |
|---|---|---|---|---|---|---|---|---|
| #370 | docs: Zaza full-product/technical review notes | N | docs | Review notes file ZAZA.md (no behaviour) | on main e41199e | — | — | N/A (docs only) |
| #369 | docs: Zaza onboarding and architecture guide | N | docs | Onboarding/review guide docs/ZAZA-START-HERE.md (no behaviour) | on main e41199e | — | — | N/A (docs only) |
| #366 | Fill form 1's profit and loss (IS, IS-COM, P&C(NL)) | N | feature | Form 1 income statement from the year's own figures; owner answers: quota deposit split by class, bank commission charged as it accrues, a loss on two classes = two cases, segment as fixed at issue | base a8e3fc9; merged into #363 | src/Insurance.Domain/Regulatory/IncomeStatement.cs, src/Insurance.Application/Services/IncomeStatementReader.cs, Pages/Accounting/Regulatory | D/Regulatory/IncomeStatementTests (15); I/Reports/IncomeStatementReaderTests (10, the 7 old negative controls as tests); W/Reports/IncomeStatementPageTests (3, incl. the late-read failure) | PORTED |
| #364 | Record the demo database run of migration 0039 | N | docs | Evidence of a PostgreSQL demo run of 0039 (pg_dump/restore logs, rights JSON) | needs #362 (0039) | — | — | N/A (historical PG run / demo deployment evidence) |
| #363 | Combined reporting head #361+#362+#358 (check only) | N | combined-check | Merge of #358+#361+#362+#366+#260 plus its own one-book test (regulatory-combined.test.ts): every real reader over one book; prior-year deposit key; prior-year ceded claims carried over; bank commission across the year; late commission read fails once; form 1 route parameters; USD ceded claims via claimSharesExact | stacked on #358; merges #361/#362/#366/#260 — not separate work | constituents: #361 ported; #362/#366 in progress | partly: W/Reports/IncomeStatementPageTests reads every real reader (#362 receivable, #366 reader, month report, impairment) over one fictional book through the page and the Form 1 file, with the late commission read failing once | OPEN (partly): the combined test's prior-year deposit key, prior-year ceded claims carried over and the bank's commission across the year are not asserted as one book |
| #362 | Keep the reinsurer's commission a receivable until settled | N | feature | RI commission stays a receivable until settled (0039 ledger, settlement page/actions, backdated settlement does not uncover a later one, 100%-ceded rows counted once) | base 7c02274; merged into #363 | src/Insurance.Domain/Finance/RiCommission.cs, src/Insurance.Application/Services/RiCommissionLedger.cs, database/migrations/0011, Pages/Accounting/RiCommission | D/Finance/RiCommissionTests; I/Reports/RiCommissionSettlementTests (11); W/Reports/RiCommissionPageTests (4) | PORTED |
| #361 | Warn instead of showing impairment figures numbers cannot hold | N | fix | Impairment page/XLSX compare every figure with the exact report; beyond safe precision show a warning, never wrong figures | base 7c02274; merged into #363 | src/Insurance.Application/Services/ImpairmentReport.cs (BuildCheckedAsync) | W/Reports/ImpairmentPrecisionGuardTests: An_ordinary_date_shows_its_figures_and_gives_the_file, Fifteen_digits_exactly_are_given_as_a_file, A_sum_beyond_a_long_is_reported_inexact_never_an_error_page | PORTED (deviation: exact decimal figures are shown, a file is refused only beyond 15 digits; see §4) |
| #358 | Fill statistical form 6 by quarter | Y | feature | Form 6 (reinsurers and brokers) by quarter, groups, rows below the grid | in baseline (head = baseline 17c1f1f) | src/Insurance.Domain/Regulatory/Form6.cs | D/Regulatory/Form6Tests (Quarters, RowsInTheirGroups, MoreThanSixClasses …); W/Reports/Form6FixesTests; W/Reports/RegulatoryPagesTests.Form_6_on_the_page_and_in_its_file_… | PORTED (baseline port) |
| #354 | Month report checker: copy run, stored rates, _v2 package | N | tooling | PG month-report checker: record the copy run (NOT TESTED), use the copy's stored rates, prepare a _v2 run package | stacked on #350; also carries main merges #360/#365/#367/#368 | checker scripts mapped in docs/legacy-db-checks-accounting.md | (I/Accounting/LegacyMonthReportPrecisionTests runs the cases) | N/A (PG checker/run-package process + docs) |
| #350 | Check a treaty premium spread over two months in the checker | N | test-only | R10: three EUR XL treaties from m0-16 to m1-15, each treaty's premium earned over two months, exact to the tetri | base dd1045e | src/Insurance.Application/Services/PremiumReport.cs (treaty premium by days) | I/Accounting/MonthReportCases.Steps.cs R10, run by I/Accounting/LegacyMonthReportPrecisionTests.Each_case_adds_exactly_its_own_figures_to_both_months | PORTED |
| #260 | Wait for the M1 test grandchild to end on its own | N | fix | Node child-process test timing (Windows grandchild exit) of scripts/m1-runtime | landed on main as #368 after the baseline | — | — | N/A (Node process tooling test) |
| #230 | Owner's answer: earned premium stays rounded cumulatively | N | docs | Owner decision recorded in docs/05: earned premium rounded cumulatively (position difference) | base 985d6bf | baseline behaviour (src/Insurance.Domain/Finance earned position) | D/Finance/FinanceTests 'earned in a period is the difference of two positions' | N/A (docs only; rule already in baseline and tested) |
| #225 | Number a change on approval: /1, /2 or a correction record | N | feature | Change gets its number on approval (ROOT/1, /2; correction record ROOT/შესწორება-n; request number /მოთხოვნა-n); correction record left out of the policies list; series run out still enters the request. Also bundles ~28 card/payment-page test commits (L225:3854–7160) | stacked on #218 (whole #207–#218 chain) | src/Insurance.Application/Services/ChangeNumber.cs, PolicyActions.cs | D/Finance/OpenMonthCorrectionTests.ChangeNumbers; I/Policies/EndorsementTests.Two_corrections_lay_the_schedule_out_by_its_first_shares_and_number_on_from_the_original; I/Policies/ImportEndorseSeriesExhaustedTests.EndorsePolicy_with_the_policy_series_run_out_is_entered_as_a_request_and_moves_no_counter; W/Policies/CorrectionWebTests.After_a_correction_the_list_shows_the_original_once_at_320_and_the_card_400_beside_320 | PORTED (change numbering ported; bundled card tests only partly (see gaps)) |
| #219 | Test the new policy paid in full through to October's report | N | test-only | September closed; 400.00 policy issued+approved 01.10; 400.00 received and allocated 07.10 → four allocations of 100.00; October owes nothing | stacked on #216 | baseline behaviour (PaymentActions.RecordPaymentAsync, PremiumReport) | I/Reports/OpenPr219PaidInFullTests.PR219_September_closed_the_400_policy_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #218 | Lay the schedule out again when an open-month change corrects a policy | N | feature | An open-month correction rebuilds the original's schedule by its first shares (PR218-F1 to nothing, F2 two corrections) | stacked on #215; needs #207 | src/Insurance.Application/Services/Billing.cs (RebuildScheduleAsync), src/Insurance.Domain/Finance/Receivable.cs (RebuildInstallments) | D/Finance/OpenMonthCorrectionTests.FirstShares; I/Policies/OpenMonthCorrectionTests L225_3105_plus_100_…, L225_3174_corrected_to_nothing_then_up_again_…; I/Policies/EndorsementTests.E2_minus_100_corrects_the_original_only_when_approved_its_schedule_laid_out_again | PORTED |
| #216 | Test a new policy on the first open day through to October's report | N | test-only | September closed; policy from 01.10 issued and approved 01.10 → October one policy, 400.00 both ways, earned 33.97, UPR 366.03 | stacked on #215 | baseline behaviour + #212 rule (PolicyActions) | I/Policies/GapsAutographChainTests.Issue_the_admins_group_the_viewer_approves_… (P from the 1st of the month, last month closed, month report: one policy 400.00); I/Policies/LegacyAcceptanceGapsTests.AG_P5_… (approved on the first open day) | PORTED (equivalent world, own dates) |
| #215 | Render the card's coverage table in a test and check each cell | N | feature | Card coverage table as its own component; each cell (initial vs current premium) rendered and checked | stacked on #214 | Web policy card page (coverage table) | W/Policies/CorrectionWebTests.After_a_correction_the_list_shows_the_original_once_at_320_and_the_card_400_beside_320; W/BrowserFlowTests.A_change_in_the_originals_open_month_corrects_it_numbered_on_approval_and_the_card_shows_both_premiums | PORTED |
| #214 | Test the card's current premium per coverage and day | N | test-only | After a real issue (damage 300 + theft 100): pending → nothing current; approved → nothing on 30.09, each coverage its own on 01.10 | stacked on #212 | Web policy card page (current premium per line as of the day) | W/Policies/GapsCardDisplayTests (current-premium cell, legacy PR #211 comment); W/Policies/CorrectionWebTests.After_a_correction_… | PORTED (per brief; the 30.09/01.10 day cut-off itself is not asserted) |
| #212 | Refuse a new policy that would start in a closed period | N | feature | Issue form refuses a start in a closed month; approval after the month closed is refused ('შეუძლებელია პოლისის დამტკიცება, თვე დახურულია'), nothing written | stacked on #211 | src/Insurance.Application/Services/PolicyActions.cs | I/Policies/PolicyApprovalTests (ApprovalClosedMonthTests).A_policy_whose_month_closed_before_approval_is_refused_and_nothing_is_written; I/Policies/EndorsementTests (EndorsementClosedMonthTests).A_date_in_a_closed_month_is_refused_at_entry_and_at_approval_and_nothing_is_written; I/Policies/LegacyAcceptanceGapsTests.AG_P5_…; W/Policies/ClosedMonthAndPrintTests | PORTED |
| #211 | Show a coverage's initial and current premium apart on the card | N | feature | Card shows each coverage's initial premium and current premium side by side | stacked on #210 | Web policy card page + PolicyTexts.CoverageColumns | W/Policies/CorrectionWebTests.After_a_correction_…; W/Policies/GapsCardDisplayTests (current premium cell) | PORTED |
| #210 | Test the first open day after a closed period, and the owner's open October | N | test-only | Change dated 31.10 with October closed refused; dated 01.11 allowed (Nov written −80, earned 24.99, UPR 228.16); approval after November closed refused; owner's open October: approved 02.11 while October open → change on its own date in October | stacked on #209 | PolicyActions (closed-period checks) | I/Policies/EndorsementTests (EndorsementClosedMonthTests).A_date_in_a_closed_month_is_refused_at_entry_and_at_approval_and_nothing_is_written; I/Policies/OpenMonthCorrectionTests (OpenMonthCorrectionClosedOriginTests).L225_2419_… | PORTED (boundaries ported; the November figures 24.99/228.16 not asserted) |
| #209 | Test a receipt after the open-month correction in the monthly report | N | test-only | −80.00 change approved in the open October corrects the original; then 100.00 received 07.10 settles the corrected first instalment; October report | stacked on #207 | baseline + #207 rule | I/Reports/OpenPr209CorrectionThenReceiptTests.PR209_a_minus_80_change_… (under #218/#225: schedule 4 × 80, overdue 80 before the receipt) | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #207 | Correct the original policy when a change is approved in its open month | N | feature | A change approved while the original's month is open corrects the original (no separate policy); closed month → policy of its own | stacked on #206 | src/Insurance.Application/Services/PolicyActions.cs | I/Policies/EndorsementTests.E1_plus_100_in_the_originals_open_month_corrects_the_original_from_its_start, E2_…; I/Policies/OpenMonthCorrectionTests.L225_1775_2201_minus_80_corrects_the_original_to_4_x_80_and_October_counts_one_policy_at_320 | PORTED |
| #206 | Test an earlier receipt allocated across the year's end | N | test-only | Receipt of 31.12.2026 allocated 31.01.2027 by allocateReceipt: December unchanged (owed 400, 100 overdue); January owes 300 with 100 still overdue | stacked on #204 | baseline (allocation booked today; report cut by booked_on) | I/Reports/OpenPr206YearEndAllocationTests.PR206_a_receipt_of_31_12_allocated_on_31_01_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #204 | Test the real −80.00 change and approval read by the monthly report | N | test-only | −80.00 change on the original paid 100.00, read by the report: October owed 220, written 320 (figures revised by #207: earned 27.18, UPR 292.82) | stacked on #203; figures superseded by #207 | PolicyActions + PremiumReport | I/Policies/OpenMonthCorrectionTests.L225_1775_2201_minus_80_corrects_the_original_to_4_x_80_and_October_counts_one_policy_at_320 | PORTED (as revised by #207) |
| #203 | Test a receipt across the month's end in the monthly report | N | test-only | 100.00 received 31.10, allocated 01.11 by recordPayment: October unchanged (owed 400, 100 overdue); November owes 300 | stacked on #202 | baseline | I/Policies/GapsElevenTests.Eleven_a_later_receipt_received_31_10_entered_07_11_is_booked_in_November_October_as_reported_November_owes_900_earned_98_63_UPR_999_45 | PORTED (equivalent (11-instalment world)) |
| #202 | Test the real issue and approval read by the monthly report | N | test-only | Pending → October empty; approved → one policy in 5.1, written 400, earned 33.97, UPR 366.03; September empty | stacked on #201 | PolicyActions + PremiumReport | I/Policies/GapsElevenTests.Eleven_full_month_report_one_policy_written_1200_earned_101_92_UPR_1098_08_owed_1000_nothing_overdue_IBNR_60_the_month_before_empty; I/Policies/GapsAutographChainTests.Issue_… | PORTED (equivalent figures) |
| #201 | Test the real claim payment and the monthly report on one store | N | test-only | payClaim 100.00 to the service centre 08.10: October paid 100, reserve 1400; November paid 0, reserve 1400 | stacked on #200 | ClaimActions.PayClaimAsync + PremiumReport | I/Reports/OpenPr201PaymentReportTests.PR201_paying_100_of_the_damage_claim_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #200 | Test the real reserve change and the monthly report on one store | N | test-only | changeReserve theft 500→700 booked 10.11: November reserve 700, October 500; same form again adds nothing | stacked on #199; baseline replaced direct changeReserve by request+approval (0035) | ClaimActions.ChangeReserveAsync / approval + PremiumReport | I/Reports/OpenPr200ReserveReportTests.PR200_the_theft_reserve_moved_from_500_to_700_… (under the baseline rule: request + approval, 0035) | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #199 | Test a reserve row booked in November leaves October as it was | N | test-only | Theft reserve +200 booked 10.11 after October was read: October unchanged (500), November 700 | stacked on #198 | PremiumReport (RBNS by booked_on) | I/Reports/PremiumReportClaimsTests.RBNS_of_each_class_is_the_reserve_to_its_last_tetri_and_an_unapproved_request_is_no_reserve ('a November change leaves October as it was') | PORTED |
| #198 | Test the monthly report reading the paid and the closed claim alike | N | test-only | A paid, then closed claim gives the same report as the paid one: September empty; October paid 1000, reserve 500, IBNR 20; November paid 0 | stacked on #197 | PremiumReport | I/Reports/OpenPr198ClosedClaimReportTests.PR198_the_damage_claim_paid_1000_then_closed_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #197 | Test decideClaim closing a fully paid claim with its history kept | N | test-only | Close a fully paid claim: only status and closed_on change; two payments, six reserve rows, zero balance stay; second close writes nothing | stacked on #196 | src/Insurance.Application/Services/ClaimActions.cs (DecideClaimAsync) | I/ClaimPaymentTests.A_claim_closed_or_rejected_after_its_payment_keeps_the_deductible_it_withheld; I/ClaimReserveTests.Decisions_review_approve_reject_close_reopen_move_the_status_and_the_reserve | PORTED (equivalent; repeat close not asserted) |
| #196 | Test payClaim reaching the fully paid boundary exactly | N | test-only | 100.00 then exactly 900.00: claim paid, reserve 0; 0.01 more refused, nothing written | stacked on #195 | src/Insurance.Application/Services/ClaimActions.cs (PayClaimAsync) | I/ClaimPaymentTests.The_final_payment_failing_after_its_money_is_written_leaves_nothing_and_the_retry_pays_750_once (status paid at exactly the approved amount); Form_checks_a_future_day_no_amount_more_than_approved_and_a_claim_not_approved (1000.01 refused) | PORTED |
| #195 | Test payClaim paying a service centre and refusing an excess | N | test-only | 100.00 to a service centre, reserve −100; then 2000.00 more than approved refused | stacked on #194 | src/Insurance.Application/Services/ClaimActions.cs | I/ClaimPaymentTests.A_third_payee_comes_from_the_register_and_is_refused_when_sanctioned_suspended_or_unknown; Form_checks_a_future_day_no_amount_more_than_approved_and_a_claim_not_approved | PORTED |
| #194 | Test decideClaim approving a claim and its reserve following, once | N | test-only | Approve for 1000: reserve follows with one row −200; same approval again writes nothing | stacked on #193 | src/Insurance.Application/Services/ClaimActions.cs (DecideClaimAsync) | I/ClaimReserveTests.Decisions_review_approve_reject_close_reopen_move_the_status_and_the_reserve | PORTED (repeat approval not asserted) |
| #193 | Test changeReserve refusing a closed claim | N | test-only | Reserve change on a closed claim: nothing written, reserve and history unchanged | stacked on #192 | src/Insurance.Application/Services/ClaimActions.cs (ChangeReserveAsync) | I/ClaimReserveTests.A_closed_claim_another_claims_request_or_a_made_up_id_is_refused | PORTED |
| #192 | Test changeReserve booking only the difference, once | N | test-only | 1000→1200 books one row +200; same form again adds nothing | stacked on #191; superseded by the baseline reserve-request flow (0035) | src/Insurance.Application/Services/ClaimActions.cs (reserve request + approval) | I/ClaimReserveTests.A_request_records_base_level_and_difference_and_moves_nothing_until_another_user_approves_it; The_same_level_a_negative_or_malformed_level_no_reason_or_a_future_day_asks_nothing | PORTED (under the baseline's request rule) |
| #191 | Test registerClaim refusing another policy's coverage | N | test-only | Policy A's form with only another policy's coverage marked: coverage error, nothing written for either policy | stacked on #190 | src/Insurance.Application/Services/ClaimActions.cs (RegisterClaimAsync) | I/ClaimRegistrationTests.Adding_a_coverage_checks_the_day_the_reserve_and_the_line (foreign line refused); I/LegacyDbCheckSchemaTests.V0028_a_claim_moved_to_another_policys_event_is_LE001_and_onto_a_coverage_its_event_has_is_23505 | PORTED (equivalent (not literally another policy's real line on the register form)) |
| #190 | Test registerClaim refusing a reserve one tetri over its limit | N | test-only | Second coverage reserve 20 000.01 > 20 000.00: field error, nothing written | stacked on #189 | src/Insurance.Application/Services/ClaimActions.cs | I/ClaimRegistrationTests.One_coverage_over_its_limit_writes_nothing_and_takes_no_number; Adding_a_coverage_checks_the_day_the_reserve_and_the_line (2000.01, one tetri over) | PORTED |
| #189 | Test registerClaim on two coverages and its same-key resubmit | N | test-only | One event, claims N and N-2, reserves 1000/500; same request key again opens the same claim, nothing written | stacked on #188 | src/Insurance.Application/Services/ClaimActions.cs | I/ClaimRegistrationTests.One_registration_over_three_coverages_is_one_event_with_claims_N_N2_N3_and_two_classes; The_same_form_twice_in_a_row_or_at_the_same_moment_registers_one_event | PORTED |
| #188 | Test issuePolicy issuing the demo policy before its approval | N | test-only | issuePolicy: one contract, pending policy, lines 300+100, 4×100, no premium row; approval books 300/100; second approval adds nothing | stacked on #187 | src/Insurance.Application/Services/PolicyActions.cs | I/Policies/GapsAutographChainTests.Issue_the_admins_group_…; W/Policies/PolicyFlowTests.Issue_then_approve_from_the_pending_list_writes_the_ledger_schedule_and_cession; I/Policies/PolicyApprovalTests.Every_press_gets_an_answer_and_the_money_is_written_once | PORTED |
| #187 | Test recordPayment before the change is issued and approved | N | test-only | Receipt 100 on the first instalment; then the change approved: receipt and allocation untouched | stacked on #186 | src/Insurance.Application/Services/PaymentActions.cs (RecordPaymentAsync) + PolicyActions | I/Policies/EndorsementTests.E7_a_paid_or_partly_paid_original_changed_minus_100_keeps_what_was_paid | PORTED |
| #186 | Test endorsePolicy issuing the −80.00 change before its approval | N | test-only | endorsePolicy: one pending change policy, line −80; the original's schedule unchanged until approval | stacked on #185 | PolicyActions (EndorsePolicyAsync) | I/Policies/EndorsementTests.E2_minus_100_corrects_the_original_only_when_approved_its_schedule_laid_out_again; W/Policies/PolicyFlowTests.Endorse_through_the_form_issues_the_change_as_a_new_pending_policy | PORTED |
| #185 | Test approvePolicy lowering a paid policy's schedule by 80.00 | N | test-only | Approved −80 on 400 paid 100: last instalment 20, dated history (4×100 before 07.10), second approval 'already' | stacked on #184; open-month case superseded by #207 (4×80), closed-month case kept | PolicyActions (ApprovePolicyAsync) | I/Policies/OpenMonthCorrectionTests.L225_2419_the_originals_month_closed_the_change_is_its_own_policy_and_October_counts_minus_80; I/Policies/EndorsementTests (EndorsementOwnPolicyTests).A_change_of_a_closed_months_original_is_its_own_policy_…; I/Policies/PaymentRegressionTests.H23_an_approved_reduction_from_a_future_day_counts_from_that_day_in_card_payment_and_report | PORTED |
| #184 | Add the three accepted advance controls to the guard-controls runner | N | tooling | Node mutation-control runner (guard-controls) over the legacy offline tests | stacked chain #182→#184 | — | — | N/A (Node test tooling) |
| #183 | Confine the guard-controls runner and judge both of its runs | N | tooling | Node mutation-control runner (guard-controls) over the legacy offline tests | stacked chain #182→#184 | — | — | N/A (Node test tooling) |
| #182 | Add a runner for the eight client-credit financial controls | N | tooling | Node mutation-control runner (guard-controls) over the legacy offline tests | stacked chain #182→#184 | — | — | N/A (Node test tooling) |
| #181 | Move the fourteen advance tests' store into one module | N | tooling | Refactor of the in-memory test store of #152–#178 (no behaviour) | stacked on #178 | — | — | N/A (test-harness refactor; scenarios mapped on #152–#178) |
| #180 | Point the seven client-credit tests at the shared store | N | tooling | Refactor of the in-memory test store of #152–#178 (no behaviour) | stacked on #178 | — | — | N/A (test-harness refactor; scenarios mapped on #152–#178) |
| #179 | Move the four suspension tests' store into one module | N | tooling | Refactor of the in-memory test store of #152–#178 (no behaviour) | stacked on #178 | — | — | N/A (test-harness refactor; scenarios mapped on #152–#178) |
| #178 | Test transferOverpayment past an old suspension carrying another number | N | test-only | Legacy suspension with another number does not block: A gives back 30.00 USD/81.00, B gets 20.00/54.00 | stacked on #177 | src/Insurance.Application/Services/PaymentActions.cs (TransferOverpaymentAsync, suspension check) | I/Policies/OpenPrTransferTests.PR178_an_old_unmapped_suspension_with_another_number_… (old-number fixture, see #158) | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #177 | Test transferOverpayment against an old suspension carrying B's number | N | test-only | Legacy suspension matching the target's number blocks the transfer: ?advance=suspended, nothing moves | stacked on #176 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/OpenPrTransferTests.PR177_an_old_unmapped_suspension_carrying_Bs_number_… (old-number fixture, see #158) | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #176 | Test transferOverpayment with a suspension over the target policy only | N | test-only | Written suspension over target B only: ?advance=suspended | stacked on #175 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/OpenPrTransferTests.PR176_a_suspension_over_the_target_policy_B_only_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #175 | Test transferOverpayment with a suspension over the source policy only | N | test-only | Written suspension over source A only: ?advance=suspended | stacked on #174 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/OpenPrTransferTests.PR175_a_suspension_over_the_source_policy_A_only_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #174 | Test transferOverpayment onto a policy already paid in full | N | test-only | Target paid in full by its own receipt: ?advance=nothing-owed; A keeps its excess, no rate asked | stacked on #173 | src/Insurance.Application/Services/PaymentActions.cs ('nothing-owed' branch) | I/Policies/OpenPrTransferTests.PR174_onto_B_already_paid_in_full_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #173 | Test transferOverpayment refusing a viewer before any statement | N | test-only | Viewer: ?advance=read-only, nothing reaches the money | stacked on #172 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.B_C_D_E_F_part_remainder_another_client_viewer_reversed (E); I/Policies/LegacyClientCreditTests.C1_C2_C3_C4_… (C3) | PORTED |
| #172 | Test transferOverpayment when its first write is refused | N | test-only | Release on A refused: 23514 → ?advance=period-closed; another error → the same error out; nothing written | stacked on #171 | src/Insurance.Application/Services/PaymentActions.cs (catch CheckViolation → period-closed) | I/Policies/OpenPrTransferTests.PR172_the_release_on_A_failing_… ; OpenPrPeriodClosedTests.PR172_…_23514_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #171 | Test transferOverpayment with no rate for the receipt's day | N | test-only | USD target, no rate on the receipt day: ?advance=no-rate, nothing written | stacked on #170 | src/Insurance.Application/Services/PaymentActions.cs ('no-rate' branch) | I/Policies/OpenPrNoRateTests.PR171_transferOverpayment_onto_a_USD_target_with_no_rate_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #170 | Test transferOverpayment refusing another client's receipt | N | test-only | Another client's receipt: ?advance=not-found before any lock | stacked on #169 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/LegacyClientCreditTests.C1_C2_C3_C4_an_overpayment_listed_and_transferred_once_to_the_same_clients_policy_only (C4); I/Policies/AdvanceTransferTests.Nothing_owed_on_the_target_writes_nothing | PORTED |
| #169 | Test transferOverpayment twice for the same receipt and target | N | test-only | Second transfer finds nothing to release: ?advance=nothing-left | stacked on #168 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.A_transfer_moves_the_largest_overpayments_exactly ('Again: nothing left'); I/Policies/LegacyClientCreditTests.C1_C2_C3_C4_… (C3) | PORTED |
| #168 | Test transferOverpayment from one policy of a client to another | N | test-only | USD A→B: release 30.00 USD/81.00 GEL, settle 20.00/54.00, 27.00 GEL stays on the receipt | stacked on #167 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.A_usd_target_takes_the_released_gel_at_the_receipt_days_rate_the_last_line_taking_the_rounding (20.00/54.00 at 2.7); Two_sources_an_earlier_release_netted_off_and_the_rest_staying_on_the_receipt; I/Policies/LegacyClientCreditTests.C5_a_usd_holding_released_in_two_parts_keeps_its_gel_exactly | PORTED (equivalent) |
| #167 | Test allocateReceipt twice with a receipt larger than the debt | N | test-only | 675 GEL on 200 USD owed: second call ?advance=nothing-owed, 135.00 GEL kept | stacked on #166 | src/Insurance.Application/Services/PaymentActions.cs (AllocateReceiptAsync) | I/Policies/AdvanceTransferTests.B_C_D_E_F_part_remainder_another_client_viewer_reversed (C: 500 on 365 → 135 left, then nothing-owed) | PORTED (equivalent, GEL) |
| #166 | Test allocateReceipt twice in a row with the same receipt | N | test-only | First allocates 7 tetri, second ?advance=nothing-left | stacked on #165 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.A_a_whole_receipt_settles_every_instalment_oldest_first_booked_today_at_the_receipt_rate (second call nothing-left); G_one_receipt_to_two_policies_or_twice_at_the_same_moment_is_allocated_once | PORTED |
| #165 | Test allocateReceipt when its first allocation is refused | N | test-only | First INSERT refused: 23514 → ?advance=period-closed (page refreshed); another error → same error, nothing written | stacked on #163 | src/Insurance.Application/Services/PaymentActions.cs (catch CheckViolation → period-closed) | I/Policies/OpenPrAllocateReceiptTests.PR165_the_first_allocation_failing_… ; OpenPrPeriodClosedTests.PR165_…_23514_answers_period_closed_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #163 | Test allocateReceipt splitting a cents advance over two lines | N | test-only | 0.07 GEL at 2.7 on a USD debt in cents: 0.02/0.05 and 0.01/0.02, last line takes the rest | stacked on #161 | src/Insurance.Application/Services/PaymentActions.cs (AllocateExactly) | I/Policies/OpenPrAllocateReceiptTests.PR163_a_cents_advance_of_0_07_GEL_on_two_instalments_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #161 | Test allocateReceipt refusing a viewer before any statement | N | test-only | Viewer: ?advance=read-only, nothing reaches the money | stacked on #160 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.B_C_D_E_F_part_remainder_another_client_viewer_reversed (E); I/Policies/GapsReceiptChainTests (viewer read-only) | PORTED |
| #160 | Test allocateReceipt with a USD reduction booked for a later day | N | test-only | Reduction booked 01.02.2027; on 31.01 the schedule as of today (100/100) is settled at the receipt's rate | stacked on #159 | src/Insurance.Application/Services/PaymentActions.cs (BalancesAsync as of today) | I/Policies/PaymentRegressionTests.H23_an_earlier_receipt_allocated_after_the_cancellation_covers_todays_debt (real allocateReceipt, future-dated change) | PORTED (equivalent, GEL) |
| #159 | Test allocateReceipt refusing a receipt already used in full | N | test-only | Receipt A used in full while B is free: ?advance=nothing-left; B not used instead | stacked on #158 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.A_a_whole_receipt_settles_every_instalment_oldest_first_booked_today_at_the_receipt_rate (nothing-left after full use) | PORTED ('B not substituted' not asserted) |
| #158 | Test allocateReceipt against an old suspension carrying a bank reference | N | test-only | Legacy suspension PAY-77: receipt ' pay-77 ' blocked (?advance=suspended), PAY-78 allocated | stacked on #157 | src/Insurance.Application/Services/PaymentActions.cs (Targets incl. receipt reference) | I/Policies/OpenPrAllocateReceiptTests.PR158_an_old_suspension_carrying_only_PAY_77_… (old-number fixture as ComplianceFixtures, insert trigger set aside for that row, as legacy db-acceptance.ts:161) | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #157 | Test allocateReceipt against a suspension over one policy only | N | test-only | Suspension over A: refused on A, allocated on B (100/270 and 50/135 at 2.7) | stacked on #156 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/OpenPrAllocateReceiptTests.PR157_a_suspension_over_policy_A_only_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #156 | Test allocateReceipt blocked by an active suspension of its client | N | test-only | Client-wide suspension: ?advance=suspended, receipt kept whole | stacked on #155 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/OpenPrAllocateReceiptTests.PR156_an_active_suspension_of_all_the_clients_operations_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #155 | Test allocateReceipt stopping when the receipt day has no USD rate | N | test-only | USD policy, no rate on 20.12.2026: ?advance=no-rate, nothing written | stacked on #154 | src/Insurance.Application/Services/PaymentActions.cs ('no-rate' branch) | I/Policies/OpenPrAllocateReceiptTests OpenPrNoRateTests.PR155_allocateReceipt_with_no_USD_rate_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #154 | Test allocateReceipt refusing another client's receipt | N | test-only | Client B's receipt on client A's policy: ?advance=not-found | stacked on #153 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.B_C_D_E_F_part_remainder_another_client_viewer_reversed (D) | PORTED |
| #153 | Test allocateReceipt keeping an excess USD advance on its receipt | N | test-only | 675 GEL on 200 USD owed: two allocations 100 USD/270 GEL, 135 GEL left as advance | stacked on #152 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.B_C_D_E_F_part_remainder_another_client_viewer_reversed (C: remainder 135 stays) | PORTED (equivalent, GEL) |
| #152 | Test allocateReceipt on a USD advance at the receipt's own rate | N | test-only | 405 GEL advance of 20.12.2026 on a USD policy: oldest first, 100/270 and 50/135 at 2.7, booked today | stacked on #151 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/AdvanceTransferTests.X_a_usd_policy_takes_a_gel_receipt_at_the_receipt_days_rate; W/Policies/GapsLaterDayCardTests.USD_cross_B_takes_270_at_20_10s_2_7_the_reversal_approved_04_11_A_owes_10000_and_Bs_card_values_USD_100_at_04_11s_2_8_280_not_270 | PORTED |
| #151 | Test refundOverpayment refunding only what is really overpaid | N | test-only | A: 200 paid on 181 owed → 19.00 refunded; B: 150 on 181 → nothing, no rate asked | stacked on #150 | src/Insurance.Application/Services/PaymentActions.cs (RefundOverpaymentAsync) | I/Policies/FdRequestTests.A_refund_request_whose_plan_changed_is_stale_and_writes_nothing (19.00 paid back); I/Policies/GapsFdRefundReversalTests.Nothing_overpaid_no_refund_no_request | PORTED (GEL) |
| #150 | Test refundOverpayment against an old suspension with a bank reference | N | test-only | Legacy suspension PAY-77 blocks the refund (?blocked=fms); PAY-78 refunds −588.80 | stacked on #149 | src/Insurance.Application/Services/PaymentActions.cs (refund Targets incl. reference) | I/Policies/OpenPrRefundTests.PR150_an_old_suspension_carrying_only_PAY_77_… (old-number fixture, see #158) | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #149 | Test refundOverpayment against a suspension over one policy only | N | test-only | Suspension over A blocks refund on A; over B lets A refund −588.80 | stacked on #148 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/OpenPrRefundTests.PR149_a_suspension_over_A_blocks_the_refund_on_A_… | PORTED (23.10.2026; real action on SQL Server, negative control) |
| #148 | Test refundOverpayment blocked by an active suspension of its client | N | test-only | Client-wide suspension: ?blocked=fms before any financial read | stacked on #147 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/FdRequestTests.A_suspension_stops_a_refund_and_its_approval | PORTED |
| #147 | Test refundOverpayment keeping GEL cents across two USD instalments | N | test-only | 0.01 USD over on two instalments at 3.6: −0.07 GEL as −0.04 and −0.03 | stacked on #146 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/GapsFdRefundReversalTests.A_refund_of_USD_2_50_at_2_002_is_minus_5_01_and_over_two_instalments_minus_10_01_newest_line_5_01_the_last_5_00; One_two_and_three_lines_GEL_0_07_EUR_0_01_twice_at_3_1234_USD_three_times_0_01_at_2_5 | PORTED (equivalent figures) |
| #146 | Test refundOverpayment stopping when today's USD rate is unavailable | N | test-only | Rate null → 'No USD rate for 2027-01-31'; provider throws → that very error; nothing written | stacked on #145 | src/Insurance.Application/Services/PaymentActions.cs | I/FailureInjection/FdApprovalFaultTests.The_refund_days_rate_read_fails_the_same_error_nothing_written_the_stored_rows_as_they_were_the_retry_refunds_minus_5_01_once | PORTED (throw half; null-rate message not asserted) |
| #145 | Test refundOverpayment on a USD policy at today's rate, once | N | test-only | 184 USD overpaid refunded once as −588.80 GEL at today's 3.2; second call nothing | stacked on #144 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/GapsFdRefundReversalTests.USD_3703_70_paid_back_at_the_refund_days_rate_above_10000_or_not_the_receipt_as_it_was; FdApprovalFaultTests (retry once) | PORTED |
| #144 | Test refundOverpayment refunding a reduction in force today once | N | test-only | 365 paid, −184 booked today: 184 refunded once, second call nothing | stacked on #143 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/PaymentRegressionTests.B_full_payment_cancellation_and_one_refund | PORTED |
| #143 | Test refundOverpayment against a reduction booked for a later day | N | test-only | −184 booked for 01.07.2027: nothing overpaid on 31.01.2027, nothing written | stacked on #142 | src/Insurance.Application/Services/PaymentActions.cs | I/Policies/PaymentRegressionTests.H23_a_refund_before_the_cancellation_day_pays_back_nothing | PORTED |
| #142 | Test reversePayment rejecting a receipt with no valid history | N | test-only | 100 allocated as +100 A and +50 B: reversal throws, no mirror, no refresh | stacked on #141 | src/Insurance.Domain/Finance/Receivable.cs (OrderMirrorLines), ReceiptReversal.cs | D/Finance/FinanceTests.MirrorLinesAreOrdered ('impossible' history) | PORTED (unit level; action-level throw not tested) |
| #141 | Test reversePayment on a receipt with positive and negative lines | N | test-only | Mirrors −100 A, +80 A, −80 B, +60 B, −20 C in a guard-allowed order | stacked on #140 | ReceiptReversal.cs + Receivable.OrderMirrorLines | I/Policies/LegacyClientCreditTests.C16_H60_plus_100_A_minus_80_A_plus_80_B_minus_60_B_plus_20_C_reversed_then_again | PORTED (same figures) |
| #140 | Test that reversePayment refuses a claim set-off | N | test-only | A claim set-off receipt is not reversed here: nothing written | stacked on #138 | src/Insurance.Application/Services/PaymentActions.cs (ReversePaymentAsync) | I/LegacyClaimOffsetReversalTests.O0_a_set_off_of_100_O1_its_receipt_reversed_directly_writes_nothing_O2_an_ordinary_receipt_of_50_still_reverses; I/Policies/FdRequestTests.A_claim_set_off_is_not_reversed_here | PORTED |
| #138 | Test reversePayment on the euro cent receipt | N | test-only | Receipt 0.03 GEL (0.01 EUR at 3.0) reversed on 31.01.2027: one reversal + one mirror at the receipt's rate; second reversal writes nothing | stacked on #137 (base 5cea59d) | src/Insurance.Application/Services/PaymentActions.cs + ReceiptReversal.cs | I/Policies/LegacyClientCreditTests.C13_a_partly_allocated_receipt_with_a_transfer_reversed_then_reversed_again; C16_…; EUR-cent reversal only hand-written in I/Reports/ImpairmentCurrencyTests.A_euro_cent_owed_on_each_of_two_policies_is_0_04_GEL_on_each_row_and_0_08_in_N1_4_settled_and_reversed | PORTED (equivalent; EUR-cent figures not through the real action) |
| #137 | Test the reversal of the receipt that settled the euro cent | Y | test-only | EUR cent receipt reversed in full: owed again, 0.04 at the report's rate | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.A_euro_cent_owed_on_each_of_two_policies_is_0_04_GEL_on_each_row_and_0_08_in_N1_4_settled_and_reversed | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #136 | Test the euro cent on one EUR policy paid in full at another rate | Y | test-only | 0.01 EUR settled at its own 3.0: row 0.04 → 0, N1-4 drops by 0.04 | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.A_euro_cent_owed_on_each_of_two_policies_…_settled_and_reversed | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #135 | Test one euro cent unpaid on two EUR policies | Y | test-only | 0.01 EUR on each of two policies: 0.04 GEL per row, 0.08 in N1-4, workbook | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.A_euro_cent_owed_on_each_of_two_policies_… | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #134 | Test the USD + EUR download when the EUR rate fails | Y | test-only | Second (EUR) rate fails after USD: error out, nothing written, next report right | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.When_the_second_rate_fails_the_error_comes_out_nothing_is_written_and_the_next_reports_are_right | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #133 | Test the USD + EUR impairment report through the download route | Y | test-only | USD+EUR workbook through the real GET route | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs + Web impairment xlsx handler | W/Reports/ImpairmentPagesTests.The_page_and_the_file_of_01_07_2026_the_brokers_surplus_on_its_row_and_N1_4_untouched_by_it; I/Reports/ImpairmentCurrencyTests.USD_and_EUR_in_one_report_each_at_its_own_rate_N1_4_adds_GEL | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #132 | Test USD and EUR policies together in the impairment report | Y | test-only | USD and EUR each at its own rate; N1-4 adds GEL | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.USD_and_EUR_in_one_report_each_at_its_own_rate_N1_4_adds_GEL | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #131 | Test a reduction that overpays a USD policy | Y | test-only | Surplus on its row, taken from no total | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.A_reduction_larger_than_what_is_unpaid_shows_its_surplus_on_its_row_and_takes_nothing_from_any_total | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #130 | Test the full reversal of the USD receipt | Y | test-only | Full reversal of the last receipt brings 480 USD back | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.Paid_in_full_it_owes_nothing_and_the_full_reversal_of_the_last_receipt_brings_its_480_USD_back | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #129 | Test the USD policy paid in full | Y | test-only | Paid in full owes nothing (report and workbook) | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.Paid_in_full_it_owes_nothing_and_the_full_reversal_… | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #128 | Test the impairment XLSX download when the user, a read or the rate fails | Y | test-only | GET route: user/read/rate failures give no file, nothing written | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs + Web handler | W/FailureInjection/PageFaultTests.Impairment_the_signed_in_user_cannot_be_read_…, Impairment_the_third_read_paid_premium_fails_…; W/Reports/ImpairmentPagesTests.Without_a_rate_for_the_date_the_request_fails_and_gives_no_file_… | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #127 | Test the impairment XLSX download route (GET) | Y | test-only | Who may download, date checks, fallback to last month's end | in baseline (stacked chain #69→#137) | Web impairment xlsx handler | W/Reports/ImpairmentPagesTests.Only_the_admin_and_the_accountant_a_wrong_or_future_date_is_refused_and_the_page_falls_back_to_last_months_end | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #126 | Compare each full report before its XLSX export (USD) | Y | test-only | Test hygiene: full report compared before export | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReportWorkbooks.cs | I/Reports/ImpairmentReportTests.The_workbook_has_the_templates_36_columns_one_row_per_policy_and_the_N1_4_totals | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #125 | Test the USD impairment reports exported to XLSX | Y | test-only | USD reports exported and read back | in baseline (stacked chain #69→#137) | ReportWorkbooks.cs | I/Reports/ImpairmentReportTests.The_workbook_has_the_templates_36_columns_one_row_per_policy_and_the_N1_4_totals | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #124 | Test a new USD payment at its own rate valued by two reports | Y | test-only | A January payment at its own rate counts in January only, valued at each report's rate | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.A_payment_dated_in_January_at_its_own_rate_counts_in_January_only_and_is_valued_at_each_reports_rate | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #123 | Test a USD policy worked in USD valued at the reporting date's rate | Y | test-only | USD broker worked in USD, valued at the reporting date's rate | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.The_USD_broker_is_worked_in_USD_and_valued_at_the_reporting_dates_rate_its_fixed_GEL_history_untouched | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #122 | Test a reduction booked late: out of the earlier report, aged by cover days | Y | test-only | Reduction booked 15.10 not in the 14.10 report; aged by cover days | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.A_reduction_issued_as_a_change_policy_lowers_the_policy_it_changes_and_has_no_row_of_its_own (second half) | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #121 | Test a negative change policy without a schedule attributed to the broker policy | Y | test-only | Change policy lowers the policy it changes, no row of its own | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.A_reduction_issued_as_a_change_policy_lowers_the_policy_it_changes_and_has_no_row_of_its_own | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #120 | Test a broker paying its whole schedule mid-term | Y | test-only | Prepaid part lowers no other debt | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.The_broker_paying_its_whole_schedule_lowers_no_other_policys_debt | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #119 | Check both January reads leave the store unchanged (advance test) | Y | test-only | Test hygiene: report reads write nothing | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.An_advance_changes_no_report_until_allocated_and_its_late_allocation_changes_January_never_December | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #118 | Test an advance allocated late: December unchanged, January falls | Y | test-only | Late allocation changes January, never December | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.An_advance_changes_no_report_until_allocated_and_its_late_allocation_changes_January_never_December | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #117 | Test the debtor order leaves the N1-4 totals unchanged | Y | test-only | Any debtor order gives the same totals, mixed policy once | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentDebtorsOrderTests.In_each_order_the_same_totals_the_mixed_policy_once_under_the_first_of_client_broker_agent; Eight_orders_in_all_… | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #116 | Test the impairment report over four debtors | Y | test-only | Four debtors, each row on its own, mixed policy counted once | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.Four_policies_four_debtors_each_row_on_its_own_and_each_policy_once_in_the_totals | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #115 | Test the impairment report after a full receipt reversal | Y | test-only | Reversal leaves December; January sees nothing paid | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentReportTests.A_receipt_reversed_in_full_leaves_December_and_January_sees_nothing_paid | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #114 | Test the impairment report on 31.12.2026 and after a later append | Y | test-only | December unchanged by January rows | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ImpairmentReport.cs | I/Reports/ImpairmentCurrencyTests.The_December_report_is_unchanged_by_rows_appended_in_January_which_the_January_report_reads | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #113 | Test a rate loaded from one NBG answer, cached and reported | Y | test-only | One NBG answer cached and used by the report | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/Rates.cs | I/Reports/NbgRateCacheTests.The_first_read_of_USD_31_07_is_3_1415_after_one_request_…; The_July_report_from_the_cache_… | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #112 | Test the stored earlier rate used when the month-end USD rate is missing | Y | test-only | Latest earlier stored rate used; none is an error | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceChainTests.With_no_rate_for_the_month_end_the_latest_earlier_stored_rate_is_used_and_none_is_an_error | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #111 | Test a USD treaty premium valued at the stored month-end rate | Y | test-only | USD XL premium at the month-end rate | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceChainTests.A_USD_excess_of_loss_premium_is_valued_at_the_stored_month_end_rate | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #110 | Test fixed XL treaty premium spread by days into the report | Y | test-only | XL premium spread by days; closing later changes no past month | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceChainTests.The_fixed_premium_of_excess_of_loss_treaties_is_spread_by_days_and_closing_them_later_changes_no_past_month | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #109 | Test a reinsurance commission falls in its booking month, with its cancellation | Y | test-only | Late-booked commission in its booking month with its sign | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | W/Reports/PremiumReportPageTests.A_late_booked_commission_falls_in_the_month_it_is_booked_with_its_sign_and_earlier_months_stay | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #108 | Test a booked reduction's negative commission through the report | Y | test-only | Negative commission of a reduction in the monthly report | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs, src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_booked_reduction_is_ceded_with_its_sign_and_read_in_the_monthly_report | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #107 | Test two lines and two claims of one class keep reinsurance apart | Y | test-only | Each claim takes only its own line's treaty, reserve, payments | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/LineClaimIsolationTests.Each_claim_takes_only_its_own_lines_treaty_and_its_own_reserve_and_payments | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #106 | Test a claim on a reduction takes the positive 30% of its cession | Y | test-only | Claim on a reduction: positive share stored on the negative cession | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_claim_on_a_reduction_takes_the_positive_share_stored_on_its_negative_cession | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #105 | Test a booked negative endorsement ceded with its sign | Y | test-only | Negative endorsement ceded with its sign, read monthly | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_booked_reduction_is_ceded_with_its_sign_and_read_in_the_monthly_report | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #104 | Test a USD quota and a hand-placed USD facultative share | Y | test-only | USD quota + USD facultative to the July report | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_USD_quota_and_a_hand_placed_USD_facultative_share_each_column_on_its_own | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #103 | Test a quota and a hand-placed facultative share cancelled together | Y | test-only | Quota + facultative cancelled together | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_quota_and_a_hand_placed_facultative_share_cancelled_together | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #102 | Test a hand-placed facultative share through to the claim reader | Y | test-only | Facultative share reaches the claim | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_hand_placed_facultative_share_reaches_the_claim_with_the_lines_quota; I/ClaimRegistrationTests.A_facultative_share_placed_by_hand_reaches_the_claim_… | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #101 | Test a facultative placement uses only the chosen active facultative treaty | Y | test-only | Only the chosen active facultative treaty | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_facultative_placement_uses_the_chosen_treaty_and_only_an_active_facultative_one | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #100 | Test an XL covering the policy stops a facultative placement | Y | test-only | XL in force over the policy refuses the placement | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_facultative_placement_and_the_excess_of_loss_treaties_around_the_policy; I/Reports/FacultativeXlTests | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #99 | Test a facultative placement is checked on every row before any write | Y | test-only | Every premium row checked before writing | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_facultative_placement_is_checked_on_every_premium_row_before_anything_is_written | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #98 | Test a facultative placement by hand and its repeat | Y | test-only | Placement by hand; the same again | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_facultative_treaty_placed_by_hand_and_the_same_placement_again | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #97 | Test a cession and its cancellation through July and August | Y | test-only | Cession and cancellation from the real writers to two reports | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_cession_and_its_cancellation_from_the_real_writers_to_the_July_and_August_reports | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #96 | Test a USD cession's 20% commission writer→report | Y | test-only | USD quota 20% commission per currency | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_USD_quota_with_a_20_percent_commission_cedes_each_currency_from_its_own_amount | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #95 | Test a USD quota cession slices each booked amount | Y | test-only | USD cession sliced from each booked amount | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_USD_quota_cession_is_sliced_from_the_amount_booked_in_each_currency | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #94 | Test two 50% quotas cede a one-tetri premium once | Y | test-only | One tetri ceded once | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.Two_50_percent_quotas_on_one_tetri_cede_it_once | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #93 | Test a 20% RI commission from premium writer to report | Y | test-only | 20% commission to the July report | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_20_percent_commission_from_the_premium_writer_to_the_July_report | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #92 | Test surplus capacity, retention and currency limits | Y | test-only | Surplus limits on the real writer (theory cases) | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.One_GEL_surplus_from_the_premium_writer_to_the_claim_writer_and_the_reader (Theory) | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #91 | Test one GEL surplus from premium writer to claim reader | Y | test-only | GEL surplus writer→claim→reader | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.One_GEL_surplus_from_the_premium_writer_to_the_claim_writer_and_the_reader | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #90 | Test a disallowed claim cession plan is refused before any write | Y | test-only | Disallowed treaty combination refuses registration | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs, ClaimActions.cs | I/ClaimRegistrationTests.A_treaty_combination_the_rules_do_not_allow_refuses_the_registration_with_nothing_written | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #89 | Test one GEL XL claim share from writer to reader | Y | test-only | GEL XL on the loss day written once above priority | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/ClaimRegistrationTests.One_GEL_excess_of_loss_in_force_on_the_loss_day_is_written_once_and_shares_above_its_priority | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #88 | Test a claim's share is written from its line's cession | Y | test-only | Share from the line's booked cession, not today's treaties | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/ClaimRegistrationTests.A_proportional_share_is_taken_from_the_lines_booked_cession_not_from_the_treaties_in_force_now | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #87 | Test a USD cancellation reverses both stored cession amounts | Y | test-only | USD cancellation reverses both amounts | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_cancellation_reverses_the_cession_actually_booked_on_its_own_treaty (Theory, USD) | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #86 | Test a cancellation reverses the cession actually booked | Y | test-only | Cancellation reverses the booked cession | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_cancellation_reverses_the_cession_actually_booked_on_its_own_treaty (Theory, GEL) | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #85 | Test a late-booked premium is ceded on its booking date (AG-P5) | Y | test-only | Late premium ceded on its booking date, original cover | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/ReinsuranceStore.cs, src/Insurance.Domain/Finance/Reinsurance.cs | I/Reports/ReinsuranceChainTests.A_late_booked_premium_row_is_ceded_with_its_booking_date_and_its_original_cover_once | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #84 | Test the report's claims rows and totals over two classes | Y | test-only | Two classes, each row its own claim | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceHistoryTests.Two_classes_each_row_its_own_claim_the_totals_add_them_and_a_later_month_of_one_moves_only_it | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #83 | Test IBNR stays out of the reinsurer's share | Y | test-only | IBNR out of reinsurers' shares | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceHistoryTests.IBNR_on_the_years_net_written_premium_stays_out_of_the_reinsurers_shares | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #81 | Test the report's month boundaries for the reinsurer share | Y | test-only | First/last day belong to their month | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceHistoryTests.The_first_and_the_last_day_belong_to_their_month_and_only_to_it | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #80 | Test a month's reinsurer share never moves afterwards (AG-R8) | Y | test-only | Quota share of each month as it was after later events (also carries #79's M1 tool commit) | in baseline (stacked chain #69→#137) | src/Insurance.Application/Services/PremiumReport.cs | I/Reports/ReinsuranceHistoryTests.Quota_30_percent_each_month_as_it_was_after_later_payments_a_closed_treaty_and_a_new_one | PORTED (baseline port (MIGRATION-COVERAGE §4)) |
| #79 | Add the M1 runtime check for insurance_close_test | Y | tooling | Offline M1 runtime check tool (scripts/m1-runtime) | in baseline | — | — | N/A (Node/PG process tooling (MIGRATION-COVERAGE §4: m1-runtime N/A)) |
| #78 | Request the M1 runtime check on insurance_close_test | Y | docs | Request/plan document | in baseline | — | — | N/A (docs only) |
| #77 | Make the migration journal read-only for core_app (M1) | Y | feature | core_app may read but not change the migration journal (0031) | in baseline | database/migrations (grants) | I/SchemaTests.Application_role_reads_the_journal; I/LegacyDbCheckSchemaTests.N1_to_N6_core_app_is_refused_on_cases_money_audit_and_journal_and_may_add_audit_rows_with_nothing_moved | PORTED (baseline port) |
| #76 | Request the offline M1 patch (docs only) | Y | docs | Request document | in baseline | — | — | N/A (docs only) |
| #75 | Plan the audit and migration-log hardening of core_app | Y | docs | Plan document | in baseline | — | — | N/A (docs only) |
| #74 | Record the one zero-row negative run of core_app | Y | docs | Run record (7 PASS) | in baseline | — | — | N/A (docs only (PG run record)) |
| #73 | Request the one zero-row negative run of core_app | Y | docs | Request document | in baseline | — | — | N/A (docs only) |
| #72 | Add the zero-row negative attempts of core_app | Y | tooling | regress-db negative runner (PG) | in baseline | — | (rules: I/LegacyDbCheckSchemaTests.N1_to_N6_…) | N/A (PG runner tooling; rules re-checked on SQL Server) |
| #71 | Record the read-only catalog re-run: 353 PASS | Y | docs | Run record | in baseline | — | — | N/A (docs only) |
| #70 | Compare each trigger's function by catalog identity and OID | Y | tooling | regress-db catalog runner (PG OIDs) | in baseline | — | (rules: I/LegacyDbCheckSchemaTests.K9_the_regress_tables_have_exactly_the_planned_triggers_…) | N/A (PG catalog tooling) |
| #69 | Record the read-only catalog run: 335 PASS, 9 FAIL | Y | docs | Run record | in baseline | — | — | N/A (docs only) |

### Summary

| status | all | ancestor | non-ancestor |
|---|---:|---:|---:|
| PORTED | 133 | 59 | 74 |
| BEHAVIOUR PRESENT – SCENARIO TEST NOT PORTED | 0 | 0 | 0 |
| N/A | 22 | 10 | 12 |
| OPEN (partly) | 1 | 0 | 1 |
| **total** | 156 | 69 | 87 |

Kinds: test-only 122, docs 11, feature 10, tooling 10, fix 2, combined-check 1.

### The 21 former gaps (ported 23.10.2026)

#149, #150, #155–#158, #163, #165, #171, #172, #174–#178 (the real allocateReceipt, transferOverpayment and refundOverpayment on SQL Server, 18 tests in I/Policies/OpenPr*Tests) and #198, #200, #201, #206, #209, #219 (the real claim and premium actions read by the real month report, 6 tests in I/Reports/OpenPrReportChainTests). Negative controls run by hand on the code: the suspension check of the receipt, the transfer and the refund switched off (#156–#158, #175–#177, #149–#150 fail), the no-rate branches (#155, #171 fail), the nothing-owed branch (#174 fails), the report's allocations cut by the receipt's day (#206 fails). #146's missing-rate message is asserted (OpenPrNoRateTests.PR146_…). The old-number suspensions of #150, #158, #177, #178 are written as the existing ComplianceFixtures and the old db-acceptance.ts:161 write them: the insert trigger is set aside for that one row on the test's own database — a fixture the working agreement may count as SKIP; recorded, not hidden.

### Smaller gaps inside rows marked PORTED (sub-scenarios not asserted)

- **#225** bundles ~28 card / payment-page test commits (legacy demo test L225:3854–7160): card read failures (allocations, advances, premium ledger, other policies' receipts — fails once then again), the card's notice of an overpayment on the client's other policy by reporting day, a request crossing midnight asking the clock once, genuine-money end-to-end transfer + reversal + closed October. Only parts have .NET equivalents (LegacyClientCreditTests C1/C12–C14 for transfer + reversal, FdApprovalFaultTests for the USD refund rate failure); the card read-failure, other-policy-notice and midnight-clock cases were not found.
- **#214** the per-day cut-off of the card's current premium (nothing on 30.09, each coverage on 01.10) is not asserted.
- **#210** the November figures of the first-open-day change (written −80.00, earned 24.99, UPR 228.16) are not asserted.
- **#146** the null-rate message ("No USD rate for 2027-01-31") of refundOverpayment is not asserted (the thrown-error half is).
- **#142** "no valid history" is tested at unit level (MirrorLinesAreOrdered), not through ReversePaymentAsync.
- **#194/#197** the repeated approval / repeated close writing nothing is not asserted.
- **#138, #153, #160, #167** ported in GEL or with other figures; the USD/EUR-cent variants of the real action are not.

### Open (named)

- **#362** and **#366** are PORTED (above). **H46** (no open PR of its own) is implemented: migration 0012, I/ClaimGelOnForeignPolicyTests (9), W/ClaimGelCardTests (1).
- **#363** combined reporting head — OPEN (partly): every real reader over one book is now read through the page and the Form 1 file (W/Reports/IncomeStatementPageTests), with the late commission read failing once; its prior-year deposit key, prior-year ceded claims carried over and the bank's commission across the year are not asserted as one book.

### Notes

- main moved 6 commits past the baseline (#367 CI concurrency, #368 = #260's M1 grandchild test fix, Zaza docs); none is a business rule.
- The 69 ancestor PRs (#69–#137 except #82, plus #358): 57 test-only scenario PRs, each mapped to a named .NET test; 2 features (#358 form 6, #77 journal read-only), ported; 10 docs/tooling rows N/A. The 87 non-ancestor PRs: 74 PORTED (65 test-only, 8 features, 1 fix), 12 N/A, 1 open in part (#363) — so "87 non-ancestor" is not "87 lost".

