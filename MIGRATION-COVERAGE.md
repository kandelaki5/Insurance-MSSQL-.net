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

ბოლო სრული გაშვება: `dotnet test` — 1784 გავიდა (unit 554, SQL Server 913, HTTP/browser 317), 0 ჩავარდა, 0 გამოტოვდა.

## მოკლედ

| რა | რამდენი | სტატუსი |
|---|---:|---|
| ეკრანები და მისამართები (ეკრანები, Excel-ის ჩამოტვირთვები, ბეჭდვა, შესვლა) | 54 ძველი + `/logout` | ყველა DONE, ტესტით |
| სერვერული მოქმედებები და ჩამოტვირთვის handler-ები | 64 | ყველა DONE, ტესტით |
| მოდულები (`src/finance`, `src/compliance`, `src/lib`, `src/i18n`, კომპონენტები) | 111 | 80 DONE; 31 React-ის კომპონენტია, მისი markup Razor-ის გვერდშია (N/A, გვერდი შემოწმებულია) |
| ბაზის მიგრაციები | 37 | ყველა ასახულია 8 ახალ ფაილში (პ. 5) |
| ძველი ტესტ-ფაილები | 161 (1 598 შემთხვევა) | 135 PORTED, 26 N/A (ძველი სატესტო ინფრასტრუქტურა), 0 ნაწილობრივ, 0 აკლია |
| ძველი რეალური ბაზის შემოწმების სკრიპტები | 45 | 43 ასახულია `docs/legacy-db-checks-*.md`-ში: PASS 364 (120 + 86 + 58 + 100; და 13 რიგი, რომელიც სხვა ნაწილის ტესტებით დამტკიცდა), N/A 43, NOT TESTED 1; 2 სკრიპტი N/A (PostgreSQL-ის ძველი სქემის განახლება) |
| სხვა სკრიპტები (ადმინისტრირება, სატესტო გარემო, მტკიცებულებები, დამხმარეები) | 162 | იხ. პ. 6 |

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

Known differences (documented, not gaps): `loadRates` answers "failed" for an impossible date (2026-02-30) without asking the bank (legacy sent it to the bank); SQL Server has no read-only transaction — the report pages only read (source review), and could read through a login holding only `core_reader` (owner's choice).
