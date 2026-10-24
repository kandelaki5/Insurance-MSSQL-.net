# INS-DOTNET-MSSQL-004 / B0 — architecture inventory of the current C#/.NET 10 rewrite

Source: `/home/user/Insurance-MSSQL-.net` (read-only survey, 23.10.2026). Nothing in the repo was changed, built or run.
Paths below are relative to the repo root. `file:line` points at the handler / statement.

Legend used in the tables

| Column | Meaning |
|---|---|
| Tx | `AsUser` = `Db.AsUserAsync(userId, …)` (one READ COMMITTED tx, `set xact_abort on`, `sp_set_session_context N'core.user_id'`); `InTx` = `Db.InTransactionAsync` (tx **without** actor); `Snap` = `Db.SnapshotAsync` (SNAPSHOT tx, read-only); `ReadTx` = `Db.ReadTxAsync` (a `Tx` **without** transaction — each statement sees its own RCSI snapshot); `Read` = `Db.ReadAsync` (raw `SqlConnection`, no tx). "n×" = several separate connections/units in one handler. |
| Locks | `U(t)` = `select … with (updlock, rowlock, holdlock)` on table t; `AL(name)` = `tx.LockAsync` → `core.xact_lock` → `sp_getapplock` (transaction owner, timeout −1), `:S` shared. `DB` = taken by triggers/procs in the schema (see §3.4). |
| Perm | `P:"key"` = `PermissionStore.HasPermissionAsync` / `Permissions.EvaluatePermission` with that key; `pre+tx` = checked before the tx **and** again inside the writing tx. `CanWrite` = `RoleCode != "viewer"`; `IsAdmin` = `RoleCode == "admin"`; `CanSeeAml` = role admin/operations_head/accountant; `IsComplianceRole` = `CanSeeAml && CanWrite`; `Role(a/acc)` = `Me.RoleCode is "admin" or "accountant"` in the page model. Every page except `[AllowAnonymousPage]` (Login, Logout, Error) first passes `RequireUserFilter` (signed-in user). |
| SQL-in-Web | ✔ = the handler (or a Web-layer helper it calls) executes SQL itself instead of an Application service. |
| W / R | tables written / read (schema `core`), transitive through the called service. |

---

## 1. Razor routes, handlers and downloads by module

### 1.1 Home

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /` `OnGetAsync` | `src/Insurance.Web/Pages/Index.cshtml.cs:44` | `PartyKyc.PartiesWithKycIssuesAsync` (PartyKyc.cs:75) | ReadTx | — | signed-in; AML reminder count only if `Aml.CanSeeAml(Me)` (:67) | ✔ :50, :58, :68 | R party, insurance_class, complaint, policy, exchange_rate, fms_suspension, party_beneficial_owner, contract, policy_line, premium_transaction | Web: AuditHomeTests, ClockWiringTests, BrowserFlowTests |
| `GET /?handler=Data` `OnGetDataAsync` (JSON for Angular) | `Pages/Index.cshtml.cs:13` | calls `OnGetAsync` + own SQL | ReadTx + Read (2 connections) | — | signed-in only | ✔ :20 | R policy, policy_line, premium_transaction, premium_cession (+ above) | Web: Policies/WorkspaceDataTests, BrowserFlowTests |
| Layout nav (every `_Layout` page) | `Pages/Shared/_Layout.cshtml:14` (`@inject Db`, line 5) | `PermissionStore.ReadAsync` | Snap | — | `P:"audit.log.view"` (:60), `P:"finance.panel.view"` (:64); role blocks: compliance links `admin/operations_head/accountant` (:43), accounting links `admin/accountant` (:50), admin links `admin` (:68) | (service call from a view) | R user_permission, user_group_member, user_group_permission | Web: AuditHomeTests, GapsFdPanelRightsTests |

### 1.2 Auth

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /login` | `Pages/Login.cshtml.cs:14` | `Auth.UserForTokenAsync` via `RequestUser.GetAsync` | Read | — | anonymous | — | R session, app_user, role | Web: AuthTests, HostingTests |
| `POST /login` | `Pages/Login.cshtml.cs:17` | `Auth.LoginAsync` (Auth.cs:65), `Auth.CreateSessionAsync` (Auth.cs:36) | **Read (no tx)** — 4 statements incl. insert+delete on one connection; session insert separate | — | anonymous; throttle 5/user, 30/source per 15 min | — | W login_attempt, session; R app_user | Web: AuthTests, HostingTests |
| `GET /logout` | `Pages/Logout.cshtml.cs:11` | — (redirect `/`) | — | — | anonymous | — | — | Web: AuthTests |
| `POST /logout` | `Pages/Logout.cshtml.cs:13` | `Auth.DestroySessionAsync` (Auth.cs:46) | Read (no tx) | — | anonymous | — | W session | Web: AuthTests, HostingTests |
| `GET /Error` | `Pages/Error.cshtml.cs:15` | — | — | — | anonymous, `[IgnoreAntiforgeryToken]` | — | — | — |

### 1.3 Parties (clients)

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /parties` | `Pages/Parties/Index.cshtml.cs:19` | — | ReadTx | — | signed-in | ✔ :28 | R party | Web: PartyTests |
| `GET /parties/new` | `Pages/Parties/New.cshtml.cs:13` | — | — | — | (form read-only if `!CanWrite`) | — | — | Web: PartyTests, DateInputTypingTests |
| `POST /parties/new?handler=Save` | `Pages/Parties/New.cshtml.cs:16` | **Web helper** `PartyForm.SaveAsync` (`Pages/Parties/PartyForm.cs:47`) | AsUser | — | CanWrite | ✔ PartyForm.cs:100–115 (dynamic insert/update) | W party | Web: PartyTests, BrowserFlowTests, DateInputTypingTests |
| `GET /parties/{id}` | `Pages/Parties/Card.cshtml.cs:138` (`LoadAsync` :82) | `PartyRisk.ComputePartyRiskAsync`, `PartyKyc.LoadKycStatusAsync`, `SanctionsScreening.ScreeningsAsync`, `Aml.ActiveSuspensionAsync`, `Aml.SuspensionsOfAsync` | ReadTx | — | screenings only if role admin/operations_head/accountant (:115); suspension list only if `CanSeeAml` (:119) | ✔ :93 (`tx.Connection` + Dapper), :120, :127 | R party, party_risk_assessment, app_user, audit_log, sanctions_screening, fms_suspension, aml_case, party_beneficial_owner, contract, policy, policy_line, premium_transaction, setting | Web: PartyCardTests, PartyTests |
| `POST ?handler=Save` | `Card.cshtml.cs:142` | Web helper `PartyForm.SaveAsync` | AsUser | — | CanWrite | ✔ | W party | Web: PartyTests |
| `POST ?handler=Owner` | `Card.cshtml.cs:160` | — | AsUser | — | CanWrite | ✔ :202–207 (dynamic insert) | W party_beneficial_owner | Web: PartyCardTests |
| `POST ?handler=OwnerUpdate` | `Card.cshtml.cs:217` | — | AsUser | — | CanWrite | ✔ :224–233 | W party_beneficial_owner | Web: PartyCardTests |
| `POST ?handler=Risk` | `Card.cshtml.cs:240` | `PartyRisk.ComputePartyRiskAsync` (ReadTx, **outside** the write tx) then `PartyRisk.RecordPartyRiskAsync` (PartyRisk.cs:80) | ReadTx + AsUser (tx opened in Web) | — | CanWrite | ✔ tx boundary :250, :271 | W party_risk_assessment, party; R setting, party, policy… | Web: PartyCardTests; Int: ComplianceServicesTests |
| `POST ?handler=Screen` | `Card.cshtml.cs:280` | Web helper `ComplianceActions.ScreenOnePartyAsync` (`Pages/Compliance/ComplianceActions.cs:114`) → `SanctionsScreening.ScreenPartiesAsync` (SanctionsScreening.cs:211) | AsUser (opened in Web, :118) | AL(`core.sanctions_subject:<id>`) (SanctionsScreening.cs:197/227) | CanWrite | ✔ tx boundary | W sanctions_screening, party, party_beneficial_owner; R sanction_list_*, sanctions_subject | Web: SanctionsTests; Int: SanctionsReuseTests |
| `POST ?handler=DecideScreening` | `Card.cshtml.cs:283` | `ComplianceActions.DecideScreeningAsync` (:133) → `SanctionsScreening.DecideAsync` (:287) | AsUser (opened in Web, :138) | AL(`core.sanctions_subject:<id>`) (:292) | CanWrite; note required | ✔ tx boundary | W sanctions_screening, party | Web: SanctionsTests; Int: SanctionsReuseTests |
| `POST ?handler=CreateSuspension` | `Card.cshtml.cs:286` | `ComplianceActions.CreateSuspensionAsync` (:31) + `Aml.SuspensionTargetAsync`, `Aml.LockForSuspensionChangeAsync` | AsUser (Web, :43) | U(policy)/U(claim) of target, AL(`core.party:<id>`) exclusive (Aml.cs:94–98) | IsComplianceRole | ✔ :52 insert | W fms_suspension; R resolve_suspension_target() | Web: AmlTests; Int: AmlSuspensionAcceptanceTests |
| `POST ?handler=UpdateSuspension` | `Card.cshtml.cs:289` | `ComplianceActions.UpdateSuspensionAsync` (:70) | AsUser (Web, :78) | attach/confirm: Aml.LockForSuspensionChangeAsync; lift: none | IsComplianceRole | ✔ :80, :89, :100, :106 | W fms_suspension | Web: AmlTests; Int: AmlSuspensionAcceptanceTests |

### 1.4 Intermediaries

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /intermediaries` | `Pages/Intermediaries/Index.cshtml.cs:12` | — | ReadTx | — | signed-in | ✔ :13 | R intermediary, party | Web: IntermediaryComplaintTests |
| `GET /intermediaries/new` | `New.cshtml.cs:11` | — | — | — | — | — | — | Web: IntermediaryComplaintTests |
| `POST /intermediaries/new?handler=Save` | `New.cshtml.cs:13` | **Web helper** `IntermediaryForm.SaveAsync` (`Pages/Intermediaries/IntermediaryForm.cs:21`) | ReadTx (party lookup) + AsUser | — | CanWrite | ✔ IntermediaryForm.cs:36, :57, :65 | W intermediary; R party | Web: IntermediaryComplaintTests |
| `GET /intermediaries/{id}` | `Card.cshtml.cs:42` | — | ReadTx | — | signed-in | ✔ :20 | R intermediary, party | Web: IntermediaryComplaintTests |
| `POST ?handler=Save` | `Card.cshtml.cs:49` | Web helper `IntermediaryForm.SaveAsync` | AsUser | — | CanWrite | ✔ | W intermediary | Web: IntermediaryComplaintTests |

### 1.5 Reference

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /reference/classes` | `Pages/Reference/Classes.cshtml.cs:12` | — | Read | — | signed-in | ✔ :13 | R insurance_class | Web: AuthTests, BrowserFoundationTests, HostingTests |
| `GET /reference/rates` | `Pages/Reference/Rates.cshtml.cs:18` | — | Read | — | signed-in | ✔ :34 | R exchange_rate | Web: LoadRatesActionTests |
| `POST /reference/rates` | `Rates.cshtml.cs:20` | `Rates.LoadNbgRatesAsync` (Rates.cs:17) → HTTP NBG → `Rates.StoreAsync` (:42) | InTx (no actor) | `merge … with (holdlock)` | CanWrite | ✔ (reload :34) | W exchange_rate | Web: LoadRatesActionTests; Int: NbgRateCacheTests |

### 1.6 Policies

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /policies` | `Pages/Policies/Index.cshtml.cs:56` | — | Read | — | signed-in | ✔ :64 | R policy, contract, party, product, insured_object, policy_line, premium_transaction | Web: PolicyFlowTests, CorrectionWebTests |
| `GET /policies?handler=Data` (JSON for Angular) | `Index.cshtml.cs:15` | `OnGetAsync` + `Billing.LoadSchedulesAsync`, `PolicyCard.ExactOutstanding` | Read + ReadTx | — | signed-in only | ✔ :23, :28 | R + installment, installment_adjustment, payment_allocation, payment, premium_cession | Web: WorkspaceDataTests, BrowserFlowTests |
| `GET /policies/pending` | `Pending.cshtml.cs:39` (`LoadAsync` :19) | `PermissionStore.ReadAsync` | Read + Snap | — | button: `P:"policy.approve"` (:36) | ✔ :21 | R policy, contract, party, insured_object, policy_line, app_user | Web: PolicyFlowTests, GapsAutographChainPagesTests |
| `POST /policies/pending?handler=Approve` | `Pending.cshtml.cs:41` | `PolicyActions.ApprovePolicyAsync` (PolicyActions.cs:121) | AsUser | U(policy) (:136, :172, :181), AL(`core.accounting_period`:S) (:151), `Aml.SuspensionInTransactionAsync` = AL(`core.party:<id>`:S) + party holdlock; DB | `P:"policy.approve"` pre+tx (:125, :132); own policy refused unless admin (:139) | — | W policy, premium_transaction, premium_cession, installment_adjustment; R accounting_period, contract, installment, payment_allocation, ri_treaty(_class)… | Int: PolicyApprovalTests, LegacyApprovalAnswersTests, OpenMonthCorrectionTests, PolicyFaultTests; Web: PolicyFlowTests |
| `GET /policies/new` | `New.cshtml.cs:47` | — | Read ×3 | — | CanWrite (else redirect) | ✔ :28, :33, :37 | R product, product_coverage, intermediary, party | Web: PolicyFlowTests, GapsPaymentPlanFormsTests |
| `POST /policies/new` | `New.cshtml.cs:59` | `PolicyActions.IssuePolicyAsync` (:47) → `PolicyIssue.CreateVehiclePolicyAsync` | ReadTx ×3 (context, first open day, blocking suspension — outside tx) + AsUser | `core.next_number` row lock; DB | CanWrite | — | W contract, policy, policy_line, insured_object, installment, policy_intermediary | Int: CancelIssueTests, LegacyIssueConcurrencyTests, EndorsementTests; Web: PolicyFlowTests |
| `GET /policies/import` | `Import.cshtml.cs:23` | — | — | — | CanWrite | — | — | Web: PolicyFlowTests |
| `POST ?handler=Preview` | `Import.cshtml.cs:35` | `PolicyImport.PreviewAsync` (PolicyImport.cs:80) | ReadTx ×n | — | CanWrite | — | R party, product, product_coverage (no write) | Web: PolicyFlowTests, GapsPaymentPlanFormsTests |
| `POST ?handler=Confirm` | `Import.cshtml.cs:53` | `PolicyImport.ConfirmAsync` (:137) | ReadTx + **one AsUser for the whole batch** (:195) | next_number; DB | CanWrite | — | W contract, policy, policy_line, insured_object, installment, policy_intermediary | Web: PolicyFlowTests, PaymentFlowTests; Int: GapsImportPlanTests, ImportTests |
| **Download** `GET /policies/import/template` (.xlsx) | `ImportTemplate.cshtml.cs:10` | `PolicyImport.TemplateAsync` (:219) | ReadTx | — | signed-in | — | R product, product_coverage | Web: BrowserFlowTests, PaymentFlowTests |
| `GET /policies/{id}` | `Details.cshtml.cs:216` (`LoadAsync` :84) | `Billing.LoadScheduleAsync/LoadAdvancesAsync/LoadReleasableAsync`, `PermissionStore.ReadAsync`, `Rates.LatestRateOnOrBeforeAsync`, `PolicyCard.*` | Read + ReadTx + Snap (+ possible InTx in Rates) | — | buttons: `P:"premium.receipt.create"` (:164), `P:"policy.approve"` (:165), `P:"payment.large.authorize"` (:166); advances/releasable only if CanWrite | ✔ 14 queries :88–:194 | R policy, contract, party, product, insured_object, policy_line, product_coverage, premium_transaction, payment, payment_allocation, installment(_adjustment), policy_intermediary, intermediary, premium_refund_request, receipt_reversal_request, app_user, premium_cession, ri_treaty; **may W exchange_rate** | Web: GapsCardDisplayTests, GapsCardLegacyDayTests, GapsLaterDayCardTests, LegacyPremiumPrecisionTests, PageFaultTests |
| `GET /policies/{id}?handler=Data` (JSON for Angular) | `Details.cshtml.cs:222` | same `LoadAsync` | as above | — | signed-in only | ✔ (same) | as above | Web: WorkspaceDataTests |
| `POST ?handler=Approve` | `Details.cshtml.cs:242` | `PolicyActions.ApprovePolicyAsync` | AsUser | as Pending | `P:"policy.approve"` pre+tx | — | as Pending | Web: PolicyFlowTests, GapsAutographChainPagesTests |
| `POST ?handler=ReversePayment` | `Details.cshtml.cs:250` | `PaymentActions.ReversePaymentAsync` (PaymentActions.cs:427) → `SettleReversalAsync` (:394) or request | AsUser | AL(`core.payment_allocation:<payment>`) (ReceiptReversal.cs:20, PaymentActions.cs:397); DB | CanWrite; above `Settings.AuthorisationThresholdAsync` → request for FD | — | W payment, payment_allocation, receipt_reversal_request(_line); R setting | Int: FdRequestTests, GapsFdRefundReversalTests, PaymentConcurrencyTests; Web: GapsFdPanelRightsTests |
| `POST ?handler=RefundOverpayment` | `Details.cshtml.cs:253` | `PaymentActions.RefundOverpaymentAsync` (:602) → `SettleRefundAsync` (:541) | AsUser | U(policy) (:545); suspension AL(core.party:S); **Rates.RateOnAsync inside tx** (:561) | CanWrite; threshold → request | — | W payment, payment_allocation, premium_refund_request(_line) | Int: OpenPrRefundTests, FdRequestTests |
| `POST ?handler=ApproveReversalRequest` | `Details.cshtml.cs:256` | `PaymentActions.ApproveReceiptReversalRequestAsync` (:471) | AsUser | as reversal | `P:"payment.large.authorize"` pre+tx (:475, :483) | — | W payment, payment_allocation; R receipt_reversal_request(_line) | Int: FdRequestTests, LegacyFdRaceTests, FdApprovalFaultTests |
| `POST ?handler=ApproveRefundRequest` | `Details.cshtml.cs:259` | `PaymentActions.ApprovePremiumRefundRequestAsync` (:646) | AsUser | as refund | `P:"payment.large.authorize"` pre+tx (:650, :658) | — | W payment, payment_allocation | Int: FdRequestTests, GapsFdRefundReversalTests |
| `POST ?handler=PlaceFacultative` | `Details.cshtml.cs:262` | `PolicyActions.PlaceFacultativeAsync` (:421) → `ReinsuranceStore.PlaceFacultativeAsync` (:168) | AsUser | U(policy) (ReinsuranceStore.cs:172); DB AL(`core.premium_cession:<tx>`) | CanWrite | — | W premium_cession | Int: FacultativeConcurrencyTests, FacultativeXlTests |
| `GET /policies/{id}/cancel` | `Cancel.cshtml.cs:68` (`LoadAsync` :25) | domain `CancelPreview` | Read ×3 | — | CanWrite (else redirect) | ✔ :28, :33, :39 | R policy, premium_transaction, policy_line, payment_allocation, installment | Web: PolicyFlowTests, ClosedMonthAndPrintTests |
| `POST /policies/{id}/cancel` | `Cancel.cshtml.cs:74` | `PolicyActions.CancelPolicyAsync` (:259) → `PolicyCancel.CancelPolicyInTxAsync` (PolicyCancel.cs:30) | AsUser | U(policy) (PolicyCancel.cs:33); DB | CanWrite | ✔ (reload) | W policy, premium_transaction, premium_cession, installment_adjustment | Int: CancelIssueTests, GapsPolicyCancelAsUserTests; Web: PolicyFlowTests |
| `GET /policies/{id}/endorse` | `Endorse.cshtml.cs:49` | — | Read ×2 | — | CanWrite | ✔ :27, :32 | R policy, product_coverage, policy_line | Web: PolicyFlowTests |
| `POST /policies/{id}/endorse` | `Endorse.cshtml.cs:55` | `PolicyActions.EndorsePolicyAsync` (:308) | ReadTx ×3 + AsUser | U(policy) via `ChangeNumber.NumberingAsync` (ChangeNumber.cs:37, :47) | CanWrite | ✔ (reload) | W policy (pending_approval), policy_line, insured_object, policy_party, policy_intermediary, installment | Int: EndorsementTests; Web: PolicyFlowTests |
| `GET /policies/{id}/payment` | `Payment.cshtml.cs:69` (`LoadAsync` :30) | `PermissionStore.ReadAsync`, `Billing.*`, `Rates.RateOnAsync` | Snap + Read + ReadTx | — | `P:"premium.receipt.create"` or CanWrite, else redirect (:33–:34) | ✔ :35, :47 | R policy, contract, installment(_adjustment), payment_allocation, payment; may W exchange_rate | Web: GapsPaymentPageTests, PaymentFlowTests |
| `POST /policies/{id}/payment` | `Payment.cshtml.cs:75` | `PaymentActions.RecordPaymentAsync` (:175) | ReadTx ×2 (pre) + AsUser | U(policy) (:212 → :137); `Aml.SuspensionInTransactionAsync`; DB | `P:"premium.receipt.create"` pre+tx (:178, :211) | ✔ (reload) | W payment, payment_allocation | Int: ReceiptTests, PolicyFaultTests; Web: PaymentFlowTests |
| `POST ?handler=Allocate` | `Payment.cshtml.cs:87` | `PaymentActions.AllocateReceiptAsync` (:251) | AsUser | U(policy) (:267), AL(core.payment_allocation:<id>) (:270); Rates inside tx (:284) | CanWrite | — | W payment_allocation | Int: OpenPrAllocateReceiptTests, PaymentConcurrencyTests; Web: GapsPaymentPageTests |
| `POST ?handler=Transfer` | `Payment.cshtml.cs:90` | `PaymentActions.TransferOverpaymentAsync` (:309) | AsUser | U(policy) ordered (:332), AL(core.payment_allocation) (:333); Rates inside tx (:343) | CanWrite | — | W payment_allocation | Int: OpenPrTransferTests, AdvanceTransferTests; Web: GapsPaymentPageTests |

### 1.7 Payments / Billing

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /payments/overdue` | `Pages/Payments/Overdue.cshtml.cs:17` | `Rates.RateOnAsync`, `Billing.ToInstallments/ToCredits`, domain `Receivable.ReceivableAsOf` | ReadTx (+ Rates connections) | — | signed-in | ✔ :24, :34, :41 | R policy, contract, party, installment, installment_adjustment, payment_allocation, payment; may W exchange_rate | Web: PaymentFlowTests |

### 1.8 Print

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| **Print** `GET /print/policies/{id}` (HTML, `_Print` layout) | `Pages/Print/Policy.cshtml.cs:111` | `Billing.LoadScheduleAsync`, `Settings.LoadSettingsAsync`, `Settings.LoadProductTextsAsync` | Read + ReadTx | — | signed-in (any user) | ✔ :114, :130, :133, :139, :143 | R policy, contract, party, product, insured_object, policy_line, product_coverage, policy_party, policy_intermediary, intermediary, installment(_adjustment), setting, product_text | Web: GapsPrintPolicyTests, ClosedMonthAndPrintTests, PolicyFlowTests |

### 1.9 Claims

All claim handlers are thin; logic is in `ClaimActions` / `ClaimReads`. `LockRow` = `with (updlock, rowlock, holdlock)` (ClaimActions.cs:55). Lock order: loss_event → claim → policy → client.

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /claims` | `Pages/Claims/Index.cshtml.cs:15` | `ClaimReads.ListAsync` (ClaimReads.cs:15) | ReadTx | — | signed-in | — | R claim, loss_event, policy, contract, party, insured_object, policy_line, claim_reserve, claim_payment | Web: ClaimPagesTests |
| `GET /claims/new` | `New.cshtml.cs:27` | `ClaimReads.PolicyByNumberAsync`, `ClaimReads.LinesAsync`, `ClaimActions.NewClaimCurrencyAsync` | ReadTx ×3 | — | CanWrite | — | R policy, contract, party, insured_object, policy_line, product_coverage, setting | Web: ClaimPagesTests |
| `POST /claims/new` | `New.cshtml.cs:34` | `ClaimActions.RegisterClaimAsync` (:127) (+ `ReinsuranceStore.AttachClaimCessionsAsync`) | ReadTx ×3 + AsUser | next_number; idempotent by request_key; DB | CanWrite | — | W loss_event, claim, claim_reserve, claim_cession | Int: ClaimRegistrationTests, LegacyDbCheckNumberingTests; Web: ClaimPagesTests |
| `GET /claims/{id:guid}` | `Claim.cshtml.cs:41` (`ShowAsync` :43) | `ClaimReads.PageAsync` (:95), `PermissionStore.ReadAsync` | ReadTx + Snap | — | buttons: `P:"claim.recovery.receive"`, `P:"claim.payment.record"`, `P:"claim.payment.reverse"`, `P:"claim.reserve.approve"`, `P:"payment.large.authorize"` (:53–:57) | — | R 17 claim/policy tables | Web: ClaimPagesTests, ClaimGelCardTests, ClaimPageFaultTests |
| `POST ?handler=AddCoverage` | `Claim.cshtml.cs:87` | `ClaimActions.AddEventCoverageAsync` (:250) | AsUser | U(loss_event) (:271); Rates inside tx (:289) | CanWrite | — | W claim, claim_reserve, claim_cession | Int: ClaimRegistrationTests, ClaimPaymentTests |
| `POST ?handler=PayClaim` | `Claim.cshtml.cs:93` | `ClaimActions.PayClaimAsync` (:739) | ReadTx (payee lookup) + AsUser (pay :778 or request :805) | U(loss_event, claim, policy) (:540–:554, :604, :639); suspension AL(core.party:S) for holder+payee (:556, :560); Rates inside tx (:568, :606); DB | `P:"claim.payment.record"` pre+tx (:742, :781, :807) | — | W claim, claim_payment, claim_payment_request, claim_reserve, payment, payment_allocation, policy, premium_cession | Int: ClaimPaymentTests, ClaimTotalLossTests, LegacyClaimPayeeRegressionTests |
| `POST ?handler=ChangeReserve` | `Claim.cshtml.cs:99` | `ClaimActions.ChangeReserveAsync` (:359) | AsUser | U(claim) (:373) | CanWrite | — | W claim_reserve_request | Int: ClaimReserveTests, ClaimFaultTests |
| `POST ?handler=ApproveReserveRequest` | `Claim.cshtml.cs:102` | `ClaimActions.ApproveReserveRequestAsync` (:393) | AsUser | U(claim) (:411); DB | `P:"claim.reserve.approve"` pre+tx (:399, :410) | — | W claim_reserve | Int: ClaimReserveTests |
| `POST ?handler=Decide` | `Claim.cshtml.cs:105` | `ClaimActions.DecideClaimAsync` (:450) | AsUser | U(claim) (:463) | CanWrite | — | W claim, claim_reserve | Int: ClaimReserveTests, ClaimPaymentTests |
| `POST ?handler=ApproveClaimPaymentRequest` | `Claim.cshtml.cs:112` | `ClaimActions.ApproveClaimPaymentRequestAsync` (:853) | AsUser | as PayClaim | `P:"payment.large.authorize"` pre+tx (:859, :870) | — | as PayClaim | Int: ClaimConcurrencyTests, GapsFdClaimTests, LegacyFdRaceTests; Web: FdPanelFaultTests |
| `POST ?handler=AddRecovery` | `Claim.cshtml.cs:115` | `ClaimActions.AddRecoveryAsync` (:909) | AsUser | DB (period, case money guard) | `P:"claim.recovery.receive"` pre+tx (:913, :926) | — | W claim_recovery | Int: ClaimRecoveryTests |
| `POST ?handler=ReverseClaimPayment` | `Claim.cshtml.cs:118` | `ClaimActions.ReverseClaimPaymentAsync` (:951) | AsUser | U(loss_event, claim, policy) (:983–:991); suspension (:993, :1009); AL(core.payment_allocation) (:1019) | `P:"claim.payment.reverse"` pre+tx (:956, :978) | — | W claim_payment, claim_reserve, payment, payment_allocation | Int: ClaimReversalTests, ClaimConcurrencyTests |

### 1.10 Complaints

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /complaints` | `Pages/Complaints/Index.cshtml.cs:16` | — | ReadTx | — | signed-in | ✔ :21 | R complaint, policy | Web: IntermediaryComplaintTests |
| `GET /complaints/new` | `New.cshtml.cs:14` | — | — | — | CanWrite | — | — | Web: IntermediaryComplaintTests |
| `POST /complaints/new` | `New.cshtml.cs:24` | — | ReadTx ×3 + AsUser | next_number `complaint` | CanWrite | ✔ :44, :52, :57, :69–:72 | W complaint; R party, policy, claim | Web: IntermediaryComplaintTests, ClockWiringTests |
| `GET /complaints/{id}` | `Card.cshtml.cs:54` | — | ReadTx | — | signed-in | ✔ :29 | R complaint, policy, claim | Web: IntermediaryComplaintTests |
| `POST ?handler=Acknowledge` | `Card.cshtml.cs:57` | — | AsUser | — | CanWrite | ✔ :61 | W complaint | Web: IntermediaryComplaintTests |
| `POST ?handler=Respond` | `Card.cshtml.cs:68` | — | AsUser | — | CanWrite | ✔ :88 | W complaint | Web: IntermediaryComplaintTests |

### 1.11 Reinsurance

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /reinsurance/treaties` | `Pages/Reinsurance/Treaties/Index.cshtml.cs:13` | — | ReadTx | — | signed-in | ✔ :14 | R ri_treaty, party, ri_treaty_class, premium_cession | Web: TreatyPagesTests |
| `GET /reinsurance/treaties/new` | `New.cshtml.cs:14` | — | ReadTx | — | CanWrite | ✔ :48 | R insurance_class | Web: TreatyPagesTests, TreatyFormDateTests |
| `POST /reinsurance/treaties/new` | `New.cshtml.cs:26` | `Treaties.CreateAsync` (Treaties.cs:28) | ReadTx ×3 + AsUser | next_number `treaty` | CanWrite | ✔ (reload :48) | W ri_treaty, ri_treaty_class; R party, insurance_class | Web: TreatyPagesTests; Int: TreatySeriesExhaustedTests |
| `GET /reinsurance/treaties/{id}` | `Detail.cshtml.cs:20` | — | ReadTx ×2 | — | signed-in | ✔ :23, :36 | R ri_treaty, party, ri_treaty_class, insurance_class, premium_cession, premium_transaction, policy_line, policy | Web: TreatyPagesTests |
| `POST ?handler=Close` | `Detail.cshtml.cs:51` | `Treaties.CloseAsync` (:128) | AsUser | — | CanWrite | — | W ri_treaty | Web: TreatyPagesTests |
| `GET /reinsurance/treaties/{id}/bordereau` | `Bordereau.cshtml.cs:23` | `Bordereau.BuildAsync` (Bordereau.cs:33) | ReadTx (+ Rates) | — | signed-in | — | R ri_treaty, premium_cession, premium_transaction, policy(_line), contract, party, insured_object, claim(_cession/_payment/_reserve); may W exchange_rate | Web: BordereauPagesTests; Int: ReportParityTests |
| **Download** `GET …/bordereau/xlsx` | `BordereauXlsx.cshtml.cs:11` | `Bordereau.BuildAsync` + `ReportWorkbooks.Bordereau` | ReadTx | — | signed-in | — | as above | Web: BordereauPagesTests |

### 1.12 Compliance / AML / Sanctions

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /compliance/aml-cases` | `Pages/Compliance/AmlCases/Index.cshtml.cs:37` | `Aml.SuspensionsOfAsync` | ReadTx | — | CanSeeAml (else `/`) | ✔ :25 | R aml_case, party, fms_suspension | Web: AmlTests |
| `POST ?handler=UpdateSuspension` | `Index.cshtml.cs:51` | `ComplianceActions.UpdateSuspensionAsync` | AsUser | see 1.3 | IsComplianceRole | ✔ | W fms_suspension | Web: AmlTests |
| `GET /compliance/aml-cases/new` | `New.cshtml.cs:21` | — | ReadTx | — | IsComplianceRole | ✔ :18 | R insurance_class | Web: AmlTests |
| `POST /compliance/aml-cases/new` | `New.cshtml.cs:31` | — | ReadTx + AsUser | next_number `aml_case` | IsComplianceRole | ✔ :44, :56–:59 | W aml_case; R party | Web: AmlTests |
| `GET /compliance/aml-cases/{id}` | `Card.cshtml.cs:62` | `Aml.SuspensionsOfAsync` | ReadTx ×2 | — | CanSeeAml | ✔ :35 | R aml_case, party, app_user, fms_suspension | Web: AmlTests |
| `POST ?handler=Decide` | `Card.cshtml.cs:77` | — | AsUser | — | IsComplianceRole | ✔ :103–:117 | W aml_case | Web: AmlTests |
| `POST ?handler=CreateSuspension` / `UpdateSuspension` | `Card.cshtml.cs:127` / `:130` | `ComplianceActions.*` | AsUser | see 1.3 | IsComplianceRole | ✔ | W fms_suspension | Web: AmlTests |
| `GET /compliance/aml-report` | `AmlReport/Index.cshtml.cs:13` | `AmlReport.BuildAsync` (AmlReport.cs:131) | ReadTx | — | CanSeeAml | — | R aml_case, fms_suspension, party, party_risk_assessment, policy, policy_line, contract, claim_payment, payment, insurance_class | Web: AmlTests; Int: ComplianceServicesTests |
| **Download** `GET /compliance/aml-report/xlsx` | `AmlReport/Xlsx.cshtml.cs:10` | `AmlReport.BuildAsync` + `AmlReport.Workbook` | ReadTx | — | CanSeeAml (else 403) | — | as above | Web: AmlTests, BrowserFlowTests |
| `GET /compliance/sanctions` | `Sanctions/Index.cshtml.cs:34` | `SanctionsScreening.ListVersionAsync`, `ScreeningsAsync` | ReadTx | — | signed-in (any user, owner decision 18.10) | — | R sanction_list_version, sanctions_screening, party, party_beneficial_owner, app_user | Web: SanctionsTests |
| `POST ?handler=Refresh` | `Sanctions/Index.cshtml.cs:42` | `SanctionsScreening.LoadUnListAsync` (:54) → HTTP UN list | InTx (no actor) | `merge sanction_list_version with (holdlock)` | CanWrite | — | W sanction_list_version, sanction_list_entry | Web: SanctionsTests; Int: SanctionsReuseTests |
| `POST ?handler=ScreenAll` | `Sanctions/Index.cshtml.cs:49` | `SanctionsScreening.ScreenPartiesAsync` (all parties) | AsUser (opened in Web, :52) — **one tx for every party** | AL per subject | CanWrite | ✔ tx boundary | W sanctions_screening, party, party_beneficial_owner | Web: SanctionsTests; Int: SanctionsReuseTests |
| `POST ?handler=DecideScreening` | `Sanctions/Index.cshtml.cs:56` | `ComplianceActions.DecideScreeningAsync` | AsUser | AL subject | CanWrite | ✔ tx boundary | W sanctions_screening, party | Web: SanctionsTests |

### 1.13 Accounting / Reports / Regulatory

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /accounting/periods` | `Pages/Accounting/Periods.cshtml.cs:28` (`LoadAsync` :37) | `Settings.LoadSettingsAsync`, `PermissionStore.ReadAsync` | ReadTx + Snap | — | **no page check** (nav shows it to admin/accountant only); button `P:"accounting.period.close"` (:59) | ✔ :45, :46 | R setting, policy, accounting_period, app_user | Web: PeriodsPageTests, LegacyPeriodsHttpTests |
| `POST ?handler=Close` | `Periods.cshtml.cs:30` | `PeriodClose.CloseAsync` (PeriodClose.cs:21) | AsUser | AL(`core.accounting_period`) exclusive (:39); `accounting_period with (updlock, holdlock)` (:50) | `P:"accounting.period.close"` pre+tx (:26, :36) | — | W accounting_period; R policy | Int: LegacyMonthCloseTests, PeriodCloseConcurrencyTests, PeriodCloseFaultTests; Web: PeriodsPageTests |
| `GET /accounting/premium-report` | `PremiumReport.cshtml.cs:12` | `PremiumReport.BuildAsync` (PremiumReport.cs:65) | ReadTx (+ Rates) | — | signed-in (any user) | — | R premium_transaction, premium_cession, policy(_line), installment, payment(_allocation), claim*, ri_treaty, insurance_class; may W exchange_rate | Web: PremiumReportPageTests, LegacyMonthReportPagesTests; Int: ReportParityTests, MonthReportCases |
| `GET /accounting/policy-premium` | `PolicyPremium.cshtml.cs:12` | `PolicyPremiumReport.BuildAsync` (:53) | ReadTx | — | Role(a/acc) (else `/`) | — | R policy, policy_line, premium_transaction, premium_cession, contract, party, insurance_class | Web: PolicyPremiumPagesTests |
| **Download** `GET /accounting/policy-premium/xlsx` | `PolicyPremiumXlsx.cshtml.cs:11` | + `ReportWorkbooks.PolicyPremium` | ReadTx | — | Role(a/acc) (else 403) | — | as above | Web: PolicyPremiumPagesTests |
| `GET /accounting/impairment` | `Impairment.cshtml.cs:25` | `ImpairmentReport.BuildCheckedAsync` (:134) | ReadTx (+ Rates) | — | Role(a/acc) | — | R policy, contract, party, installment, payment_allocation, policy_line, premium_transaction, policy_party, policy_intermediary, intermediary | Web: ImpairmentPagesTests, ImpairmentPrecisionGuardTests |
| **Download** `GET /accounting/impairment/xlsx` | `ImpairmentXlsx.cshtml.cs:12` | + `ReportWorkbooks.Impairment` (422 if not exportable) | ReadTx | — | Role(a/acc) (else 403) | — | as above | Web: ImpairmentPagesTests, PageFaultTests |
| `GET /accounting/regulatory` | `Regulatory/Index.cshtml.cs:61` | `RegulatoryForms.MonthAsync` (RegulatoryForms.cs:39: PremiumReport + Settings + ImpairmentReport.BuildTotalsExactAsync + RiCommissionLedger.ReceivableGelAsync + IncomeStatementReader.ReadAsync), `RegulatoryForms.Form6RowsAsync`, `Form1`, `Reserves` | ReadTx (one Tx, no transaction) | — | Role(a/acc) | — | R union of reports + ri_commission_settlement | Web: RegulatoryPagesTests, RegulatoryCombinedTests, IncomeStatementPageTests, Form6FixesTests |
| **Download** `GET /accounting/regulatory/form1/xlsx` | `Regulatory/Downloads.cshtml.cs:13` (`Form1XlsxModel`) | `RegulatoryForms.MonthAsync` + `Form1` → `Downloads.Form` (422 on precision) | ReadTx | — | Role(a/acc) (403) | — | as above | Web: RegulatoryPagesTests, RegulatoryCombinedTests |
| **Download** `GET /accounting/regulatory/form6/xlsx` | `Downloads.cshtml.cs:27` (`Form6XlsxModel`) | `RegulatoryForms.Form6WorkbookAsync` (:68) | ReadTx | — | Role(a/acc) (403) | — | R premium_transaction, premium_cession, policy_line, ri_treaty(_class), party, insurance_class, setting | Web: Form6FixesTests, RegulatoryPagesTests |
| **Download** `GET /accounting/regulatory/reserves/xlsx` | `Downloads.cshtml.cs:42` (`ReservesXlsxModel`) | `PremiumReport.BuildAsync` + `Settings.LoadSettingsAsync` + `RegulatoryForms.Reserves` | ReadTx | — | Role(a/acc) (403) | — | as premium report | Web: RegulatoryPagesTests |
| `GET /accounting/ri-commission` | `RiCommission.cshtml.cs:32` | `RiCommissionLedger.BuildReportAsync` (:73), `PermissionStore.ReadAsync` | ReadTx + Snap | — | Role(a/acc); button `P:"reinsurance.commission.settle"` (:39) | — | R ri_treaty, premium_cession, premium_transaction, policy(_line), party, ri_commission_settlement | Web: RiCommissionPageTests; Int: RiCommissionSettlementTests |
| `POST ?handler=Record` | `RiCommission.cshtml.cs:45` | `RiCommissionLedger.RecordAsync` (:163) | AsUser | `ri_treaty with (updlock, holdlock, rowlock)` (:154) | `P:"reinsurance.commission.settle"` pre+tx (:166, :186) — **no role check** on POST | — | W ri_commission_settlement | Int: RiCommissionSettlementTests; Web: RiCommissionPageTests |
| `POST ?handler=Reverse` | `RiCommission.cshtml.cs:49` | `RiCommissionLedger.ReverseAsync` (:208) | AsUser | as Record | same key pre+tx (:210, :219) | — | W ri_commission_settlement | same |

### 1.14 Finance panel

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /finance/panel` | `Pages/Finance/Panel.cshtml.cs:35` | `PermissionStore.ReadAsync`; `FdPanel.ReadPendingQueueAsync` per queue (FdPanel.cs:86); `PremiumReport.BuildAsync` | Snap + ReadTx ×4 (one per queue + report) | — | `P:"finance.panel.view"` (:38) — read before anything else | — | R premium_refund_request, receipt_reversal_request, claim_payment_request + premium report tables | Web: GapsFdPanelRightsTests, FdPanelFaultTests; Int: FdPanelTests, LegacyFdPanelReaderTests |

### 1.15 Audit

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /audit` | `Pages/Audit/Index.cshtml.cs:39` | `PermissionStore.HasPermissionAsync(Db, Me, "audit.log.view")`, `AuditLog.ReadPageAsync` (AuditLog.cs:41) | Snap + ReadTx ×2 | — | `P:"audit.log.view"` (:41) | ✔ :44 (user list) | R audit_log, app_user | Web: AuditHomeTests, AuditFaultTests |

### 1.16 Users / Permissions

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /users` | `Pages/Users/Index.cshtml.cs:41` | — | ReadTx | — | IsAdmin | ✔ :31, :38, :39 | R app_user, role, branch | Web: UserTests |
| `POST ?handler=Create` | `Users/Index.cshtml.cs:51` | `Domain.Common.Password.HashPassword` | **InTx (no actor, deliberate)** | — | IsAdmin | ✔ :68 | W app_user | Web: UserTests |
| `POST ?handler=SetActive` | `Users/Index.cshtml.cs:86` | — | InTx | — | IsAdmin; cannot deactivate self | ✔ :93–:96 | W app_user, session (delete) | Web: UserTests |
| `POST ?handler=ResetPassword` | `Users/Index.cshtml.cs:108` | `Auth.CreateSessionAsync` if self | InTx (+ Read) | — | IsAdmin | ✔ :118–:122 | W app_user, session | Web: UserTests |
| `GET /users/permissions` | `Users/Permissions/Index.cshtml.cs:67` | `PermissionPanel.ReadAsync` (PermissionPanel.cs:62) | Snap | — | IsAdmin | — | R app_user, role, user_group, user_group_member, user_group_permission, user_permission | Web: PermissionPanelTests; Int: PermissionFaultTests |
| `POST ?handler=CreateGroup` | `Permissions/Index.cshtml.cs:84` | `PermissionPanel.ChangeAsync` → `PermissionStore.CreateGroupAsync` | AsUser (admin) | — (unique index fallback) | IsAdmin (page only; not re-checked in tx) | — | W user_group | Web: PermissionPanelTests |
| `POST ?handler=RenameGroup` | `:86` | → `PermissionStore.RenameGroupAsync` | AsUser | `user_group with (updlock, rowlock)` | IsAdmin | — | W user_group | same |
| `POST ?handler=SetGroupMember` | `:88` | → `PermissionStore.SetGroupMemberAsync` | AsUser | U(user_group_member) | IsAdmin | — | W user_group_member | same |
| `POST ?handler=SetGroupGrant` | `:90` | → `PermissionStore.SetGroupGrantAsync` | AsUser | U(user_group_permission) | IsAdmin | — | W user_group_permission | same; Int: PermissionStorageTests |
| `POST ?handler=SetUserDecision` | `:92` | → `PermissionStore.SetUserDecisionAsync` | AsUser | U(user_permission) | IsAdmin | — | W user_permission | same |

### 1.17 Settings / Products

| Route / handler | Page model | Application call | Tx | Locks | Perm | SQL-in-Web | W / R | Tests |
|---|---|---|---|---|---|---|---|---|
| `GET /settings` | `Pages/Settings/Index.cshtml.cs:21` | `Settings.LoadSettingsAsync` | ReadTx | — | IsAdmin | ✔ :26 | R setting, number_sequence, product | Web: SettingsTests |
| `POST ?handler=SaveSettings` | `Settings/Index.cshtml.cs:41` | domain validators (`Phone`, `CloseDeadline`, `Settings.ParseThreshold`) | AsUser | `merge core.setting with (holdlock)` (:71) | IsAdmin | ✔ :67–:75 | W setting | Web: SettingsTests, LegacyPeriodsHttpTests |
| `POST ?handler=SaveNumberSeries` | `Settings/Index.cshtml.cs:99` | domain `Numbering.*` | AsUser | `number_sequence with (updlock, rowlock)` in code order (:107) | IsAdmin | ✔ :105, :131 (dynamic `core.{table}`), :144 | W number_sequence; R policy/claim/complaint/… numbers | Web: SettingsTests |
| `GET /settings/products` | `Settings/Products/Index.cshtml.cs:20` | — | ReadTx | — | IsAdmin | ✔ :25, :26 | R product, product_text | Web: SettingsTests |
| `POST /settings/products` | `Settings/Products/Index.cshtml.cs:32` | — | AsUser | `merge core.product_text with (holdlock)` (:41) | IsAdmin | ✔ :37–:45 | W product_text | Web: SettingsTests |

### 1.18 Application functionality without any route

| Service | Note | Tests |
|---|---|---|
| `SubrogationCases` (Services/SubrogationCases.cs: OpenCase, AddParty, RecordEvidence, RecordResponse, AddMovement, Receive, Reverse) via `ISubrogationStore` / `SqlSubrogationStore` (SubrogationStore.cs:35, :63); `SubrogationReportStore` (Snap) | No Razor page, no Angular screen. Only place with a repository interface and a transaction delegate (`SubrogationTransact`, SubrogationCases.cs:18, `InDatabase` :29). Lock: `core.lock_subrogation_claim` (SubrogationStore.cs:69). | Int: SubrogationCaseTests, SubrogationReportStoreTests, ClaimRecoveryTests, ClaimConcurrencyTests, LegacyDbCheckRegressTests |

---

## 2. Angular side (`frontend/`, Angular 22, standalone components)

Build: `frontend/angular.json` outputPath `../src/Insurance.Web/wwwroot/ui` (artifacts `main.js`, `styles.css` checked into `wwwroot/ui`). Loaded by `Pages/Shared/_Layout.cshtml:82` (`<script src="~/ui/main.js" type="module">`) on every `_Layout` page; not on `_Bare` (Login, Error) or `_Print` (print) pages. Bootstrap: `frontend/src/main.ts`; providers `provideRouter`, `provideHttpClient` (no interceptors) — `app.config.ts`.

### 2.1 Routes and components

| Angular route | Component (file) | Server data handler(s) called | Notes |
|---|---|---|---|
| `''` (only `/`) | `Dashboard` (`frontend/src/app/dashboard.ts`) | via `DemoStore` (`demo-store.ts`): `GET /?handler=Data` (:63) and `GET /policies?handler=Data&q=` (:77) — both fired in the store constructor | Links: `routerLink="/policies"` (+`?policy=<id>`), plain `href` to `/policies/new`, `/reference/rates`, reminder hrefs from server, `/login` on error. Razor `Index.cshtml` is still rendered into hidden `#server-content` (duplicate reads). |
| `'policies'` (only `/policies`) | `Policies` (`policies.ts`) + `PolicyDetail` slide-over (`policy-detail.ts`) | `GET /policies?handler=Data&q=` (store `load`, re-search `policies.ts:101`); `GET /policies/{id}?handler=Data` (`demo-store.ts:108`, used by `policy-detail.ts:323` and `policies.ts:303` for `?policy=`) | Client-side filter/paging; **CSV export is client-only** (`policies.ts:343–369`, Blob from loaded rows, no endpoint). `?new` and "new" button → `location.assign('/policies/new')` (native form). Detail panel links to `/policies/{id}` and `/print/policies/{id}` (native). |
| `'**'` (every other path) | `NativePage` (`native-page.ts`) | none | Moves the server-rendered `#server-content` (the Razor page body) into the shell (`native-page.ts:20–24`); breadcrumb from `#server-navigation`. **All 55 other Razor routes are native pages inside the shell**, including every form/POST. |
| (shell) | `App` (`app.ts`) | none (reads DOM) | Nav built from hidden `#server-navigation` (`_Layout.cshtml:29–75`; `app.ts:101–107`); user name/role from `<app-root data-full-name data-role-name data-can-write>` (`_Layout.cshtml:77`); logout submits hidden `#server-logout` form with antiforgery token (`_Layout.cshtml:76`, `app.ts:157–159`). |
| — | `Icon` (`icon.ts`), `ThemeService` (`theme.ts`, `localStorage` theme), `new-policy.ts` (empty: "Policy issuance intentionally uses the original .NET form") | — | — |

Summary: 3 Angular routes, 6 components (App, Dashboard, Policies, PolicyDetail, NativePage, Icon), 1 data service (`DemoStore`), 3 server JSON handlers, **0 Angular writes** (every POST is a native Razor form with antiforgery). Navigation between Razor pages is full page reload (plain `href`). Auth for XHR = same-origin session cookie; an expired session yields a 302 → `/login` HTML, which the store detects as "rows is not an array" (`demo-store.ts:81`). No 401/JSON error contract.

---

## 3. Cross-cutting

### 3.1 Session / auth / HTTP pipeline

| Concern | Where | Detail |
|---|---|---|
| Current user type | `src/Insurance.Application/Services/Auth.cs:10–17` | `CurrentUser(Id, Username, FullName, RoleCode, RoleName)`; `Subject`, `CanWrite` (`!= "viewer"`), `IsAdmin` |
| Authentication | `Auth.cs:24–92` (`Auth`, singleton, Program.cs:14) | cookie `session` (:26), 12 h (:27); random 32-byte token, only SHA-256 in `core.session`; scrypt (`Domain/Common/Password.cs`, `Scrypt.cs`); throttling via `core.login_attempt` 5/user, 30/source in 15 min (:28–:31). All statements via `Db.ReadAsync` (no transaction). |
| Per-request user | `src/Insurance.Web/Infrastructure/RequestUser.cs` (scoped, Program.cs:15) | reads cookie once; `SetCookie`/`ClearCookie`: HttpOnly, SameSite=Lax, Secure=`Hosting.SecureCookies`, Path=/ (:32–:33) |
| Gate | `Infrastructure/RequireUserFilter.cs` (global `IAsyncPageFilter`, Program.cs:26) | no user → `302 /login`, unless `[AllowAnonymousPage]` (Login, Logout, Error). Applies to JSON and file handlers too. |
| Page base | `Infrastructure/AppPageModel.cs` | `CurrentUser`/`Db` from `RequestServices` (service locator), form helpers `Form`, `Query`, `TrimmedForm`, `Errors/Values/FormError/Notice` |
| Antiforgery | `Program.cs:29–34` | cookie `csrf`, SameSite=Lax, Secure per `Hosting.AntiforgeryPolicy`; Razor Pages default validation on every POST; `ErrorModel` `[IgnoreAntiforgeryToken]` |
| Forwarded headers / HTTPS | `Infrastructure/Hosting.cs` | `X-Forwarded-For/Proto` only from `Insurance:ForwardedHeaders:KnownProxies/KnownNetworks` + loopback (:16–:28); `RequireHttpsOutsideDevelopment` → 400 text (:35–:45); `UseForwardedHeaders` first (Program.cs:39); HSTS + `/Error` outside Development (:41–:45) |
| Security headers | `Program.cs:46–54` | `Cache-Control: no-store`, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: same-origin` |
| Authorization model | `Domain/Common/Permissions.cs:92–103` (catalogue), `Services/PermissionStore.cs` | 11 keys: `premium.receipt.create`, `claim.recovery.receive`, `claim.payment.record`, `policy.approve`, `accounting.period.close`, `claim.payment.reverse`, `claim.reserve.approve`, `audit.log.view`, `payment.large.authorize`, `finance.panel.view`, `reinsurance.commission.settle`. Inputs read in `SnapshotAsync` (PermissionStore.cs:28–29); `HasPermissionAsync(Db…)` and `HasPermissionAsync(Tx…)` (:43, :49). Coarse checks are role predicates spread over pages/services (`CanWrite`, `IsAdmin`, `Aml.CanSeeAml/IsComplianceRole` Aml.cs:32–35, `RoleCode is "admin" or "accountant"` in 9 accounting page models). No ASP.NET Core `[Authorize]`/policies/claims are used. |
| Other options | `Program.cs:18` | `SanctionsOptions` ← `Insurance:Sanctions`; HttpClients `nbg` (15 s), `sanctions` (120 s) (ServiceRegistration.cs:11–14) |

### 3.2 Db / Tx abstraction (`src/Insurance.Application/Data/`)

| File | Role |
|---|---|
| `Db.cs` (singleton, Program.cs:13) | `OpenAsync` (:27), `ReadAsync` (raw `SqlConnection`, :36), `ReadTxAsync` (`Tx` without transaction, :43), `AsUserAsync` (:50 → private `InTransactionAsync(userId…)` :67: READ COMMITTED, `set xact_abort on` :75, `exec sp_set_session_context N'core.user_id', @id` :77, commit/rollback, clear context in `finally` :96), `InTransactionAsync` without actor (:64), `SnapshotAsync` (:54). `Now()/Today()` from `DbOptions.Clock`. |
| `Tx.cs` | connection+transaction pair; Dapper wrappers (`ExecuteAsync`, `QueryAsync`, …, :22–:35); `LockAsync(name, key, shared)` → `exec core.xact_lock` (:38–:40); `NextNumberAsync` → `core.next_number` (:43–:50); `Connection` is public (pages use it). |
| `DbOptions.cs` | `ConnectionString`, `Interceptor` (test seam), `Clock` |
| `CommandInterceptor.cs` | `ICommandInterceptor` test seam over SqlClient `DiagnosticListener` (fault injection) |
| `DbError.cs` | SQL error → legacy SQLSTATE codes (guards raise 51000 `[CODE] msg`; 2627/2601→23505, 547→23514/23503, 515→23502, 1205→40P01) |
| `DapperSetup.cs`, `Codes.cs`, `TbilisiClock.cs` | DateOnly handlers; enum↔snake_case; Tbilisi day |
| `DatabaseAdmin.cs` | create database (CS_AS_SC collation, RCSI, snapshot), app login in `core_app`, drop test DB |
| `Migrator.cs` | migration runner (§3.4) |

There is **no** repository/query interface layer except `ISubrogationStore`. All other services are `static` classes taking `Db`/`Tx` as parameters and writing Dapper SQL inline; only `Db`, `Auth`, `Rates`, `RequestUser` are DI-registered.

### 3.3 Actor / audit / session context

| Item | Where |
|---|---|
| Set actor | `Db.cs:77` `sp_set_session_context N'core.user_id'`; cleared `Db.cs:96` |
| Read actor | `database/migrations/0001_foundation.sql:52–56` `core.current_actor()` = `session_context(N'core.user_id')` |
| Audit triggers | `0006_audit.sql:105–115` insert `core.audit_log(user_id = core.current_actor(), …)`; regenerated by `exec core.refresh_audit_triggers` from `Migrator.ReassertAsync` |
| created_by defaults | e.g. `0003_claims_subrogation.sql:66, :104, :169`, `0011_ri_commission_settlement.sql:29` `default core.current_actor()` |
| Second context key | `0003_claims_subrogation.sql:848–851` `core.case_recovery` (case money pass, 0009) |
| Writes without actor | `InTransactionAsync`: `Rates.StoreAsync` (Rates.cs:43), `SanctionsScreening.LoadUnListAsync` (:63), Users Create/SetActive/ResetPassword; `Db.ReadAsync` writes: `Auth` session/login_attempt; Tools (raw admin connection) |

### 3.4 Database-side locks and guards

| Lock | Where |
|---|---|
| `core.xact_lock` = `sp_getapplock … @LockOwner='Transaction', @LockTimeout=-1`, refuses outside a tx | `0001_foundation.sql:35–49` |
| `core.assert_period_open` / `assert_periods_open` take `core.accounting_period` **Shared**; month close takes it Exclusive | `0001_foundation.sql:341–365`; called by ledger triggers in 0002 (:297, :370), 0003 (:366, :436, :599, :740), 0004 (:100), 0009 (:66), 0012 (:123) |
| `core.payment_allocation:<payment>` | trigger `0002_policies_billing.sql:386–387` (also `ReceiptReversal.LockReceiptAsync`) |
| `core.premium_cession:<premium_tx>` | trigger `0004_reinsurance.sql:101–102`, :141 |
| `core.subrogation_claim:<claim>` | proc `core.lock_subrogation_claim` `0003_claims_subrogation.sql:294–300` |
| `core.next_number` (row lock on `number_sequence`) | `0001_foundation.sql:295–…` |

### 3.5 Migration runner

`Migrator.MigrateAsync` (`Data/Migrator.cs:64`): embedded `database/migrations/*.sql` (`Insurance.Application.csproj:20`; 0001–0013), split on `GO`, each file in its own tx + row in `core.schema_migration`; `CheckDatabaseAsync` (CS collation, RCSI, snapshot) :107; `ReassertAsync` :121: refresh audit triggers, revoke update/delete on 19 `Ledgers` (:19–:25), revoke delete on `NoDelete` (accounting_period, subrogation_case, subrogation_party), deny all on `Closed` (case_money_pass), read-only `ReadOnly` (schema_migration, audit_config, data_import), `core_reader` grants except app_user/session, effective-rights probe via `core_app_rights_probe`. Called by Tools `migrate` and tests (`Integration.Tests/TestDatabase.cs`, `SchemaTests`).

### 3.6 Tools CLI (`src/Insurance.Tools/Cli.cs:37–66`)

| Command | Calls | Connection / env | Writes |
|---|---|---|---|
| `create-database <name>` | `DatabaseAdmin.CreateDatabaseAsync` | `INSURANCE_MASTER_CONNECTION` | new DB |
| `migrate [--until <prefix>]` | `Migrator.MigrateAsync` | `INSURANCE_ADMIN_CONNECTION` | schema, rights |
| `create-app-login <login>` | `DatabaseAdmin.CreateAppLoginAsync` | master + admin; `INSURANCE_APP_PASSWORD` | login/user in core_app |
| `seed-admin <user> <name>` | `DatabaseCommands.SeedAdminAsync` (raw `SqlConnection`, `merge core.app_user`) | admin; `INSURANCE_ADMIN_PASSWORD` | app_user |
| `test-users [<env>]` | `TestUsers.RunAsync` (raw SQL; refuses non-test DB `IsTestDatabase`) | admin | app_user; writes `.env.uat` |
| `users-inventory [<prefix>]` | `UsersInventory.ReadAsync/Print` | admin | read-only |
| `fingerprint` | `Fingerprint.ReadAsync/Lines` | admin | read-only |
| `import-postgres` | `DataMigrationCommands` → `DataMigration/PostgresImport` (Npgsql) | `INSURANCE_PG_CONNECTION` + admin | all tables, `data_import` |
| `reconcile` | `DataMigration/Reconcile` | PG + admin | read-only |

Tools never use `Db`/`Tx`; they use raw `SqlConnection` + Dapper with the admin connection (no actor).

---

## 4. Counts

| Item | Count |
|---|---|
| Razor routes (`@page`) | **57** (incl. `/Error`, `/login`, `/logout`) |
| Page-model classes | 57 (Downloads.cshtml.cs holds 3) |
| Handlers | **125** = 60 GET (incl. 3 JSON `OnGetData`) + 65 POST |
| Downloads | **8 .xlsx** (`/policies/import/template`, `/accounting/policy-premium/xlsx`, `/accounting/impairment/xlsx`, `/accounting/regulatory/form1/xlsx`, `/form6/xlsx`, `/reserves/xlsx`, `/compliance/aml-report/xlsx`, `/reinsurance/treaties/{id}/bordereau/xlsx`) + **1 print** HTML (`/print/policies/{id}`); **0 PDF**; Angular CSV export is client-side only |
| Write (state-changing) handlers | **64 POST** (all POST except `Import?handler=Preview`); plus GETs that can write `exchange_rate` through `Rates.RateOnAsync` → NBG fetch → `InTransactionAsync` (policy card + Data, payment page, overdue, premium report, impairment (+xlsx), regulatory (+form1/reserves), ri-commission, finance panel, bordereau (+xlsx)) |
| POST handlers that go entirely through an Application service | **38** (incl. the read-only Import Preview): Policies 15 (New, Pending/Approve, Details ×6, Cancel, Endorse, Import ×2, Payment ×3), Claims 9, Accounting 3 (Periods Close, RiCommission Record/Reverse), Reinsurance 2, Reference/Rates 1, Sanctions Refresh 1, Permissions 5, Login 1, Logout 1 |
| POST handlers whose SQL is in the Web project | **22** (table below) |
| POST handlers where Web opens the transaction around an Application call | **5** (Parties/Card Risk, Screen, DecideScreening; Sanctions ScreenAll, DecideScreening) — 22 + 5 + 38 = 65 |
| Read handlers with SQL in the page model | **32** GET handlers (§4.2) |

### 4.1 Write handlers with SQL in Web (first candidates for Application use cases + repositories)

| # | Handler | SQL at |
|---|---|---|
| 1–2 | `Parties/New` & `Parties/Card` `OnPostSave` → `PartyForm.SaveAsync` | `Pages/Parties/PartyForm.cs:100`, `:110`, `:115` |
| 3 | `Parties/Card.OnPostOwnerAsync` | `Pages/Parties/Card.cshtml.cs:202–207` |
| 4 | `Parties/Card.OnPostOwnerUpdateAsync` | `Card.cshtml.cs:224`, `:227`, `:233` |
| 5–9 | `CreateSuspension`/`UpdateSuspension` on Parties/Card (:286, :289), AmlCases/Card (:127, :130), AmlCases/Index (:51) → `ComplianceActions` | `Pages/Compliance/ComplianceActions.cs:43`, `:52`, `:78`, `:80`, `:89`, `:100`, `:106` |
| 10–11 | `Intermediaries/New` & `Card` `OnPostSave` → `IntermediaryForm.SaveAsync` | `Pages/Intermediaries/IntermediaryForm.cs:36`, `:52`, `:57`, `:65` |
| 12 | `Complaints/New.OnPostAsync` | `Pages/Complaints/New.cshtml.cs:44`, `:52`, `:57`, `:69–:72` |
| 13 | `Complaints/Card.OnPostAcknowledgeAsync` | `Pages/Complaints/Card.cshtml.cs:61` |
| 14 | `Complaints/Card.OnPostRespondAsync` | `Card.cshtml.cs:88` |
| 15 | `Compliance/AmlCases/New.OnPostAsync` | `Pages/Compliance/AmlCases/New.cshtml.cs:44`, `:56–:59` |
| 16 | `Compliance/AmlCases/Card.OnPostDecideAsync` | `Pages/Compliance/AmlCases/Card.cshtml.cs:103–:117` |
| 17 | `Users.OnPostCreateAsync` | `Pages/Users/Index.cshtml.cs:68` |
| 18 | `Users.OnPostSetActiveAsync` | `Users/Index.cshtml.cs:93–:96` |
| 19 | `Users.OnPostResetPasswordAsync` | `Users/Index.cshtml.cs:118–:122` |
| 20 | `Settings.OnPostSaveSettingsAsync` | `Pages/Settings/Index.cshtml.cs:67–:75` |
| 21 | `Settings.OnPostSaveNumberSeriesAsync` | `Settings/Index.cshtml.cs:103–:144` (incl. dynamic `core.{p.Table}` :131) |
| 22 | `Settings/Products.OnPostAsync` | `Pages/Settings/Products/Index.cshtml.cs:37–:45` |
| T1–T5 | tx opened in Web around an Application call (5 handlers): `Parties/Card.OnPostRiskAsync` (Card.cshtml.cs:250 ReadTx, :271 AsUser); `Parties/Card.OnPostScreenAsync` → `ComplianceActions.ScreenOnePartyAsync` (ComplianceActions.cs:118); `Parties/Card.OnPostDecideScreeningAsync` and `Sanctions.OnPostDecideScreeningAsync` → `ComplianceActions.DecideScreeningAsync` (:138); `Sanctions.OnPostScreenAllAsync` (Sanctions/Index.cshtml.cs:52) | — |

### 4.2 Read handlers with SQL in the page model (query-interface candidates)

`Index.OnGetAsync` (Index.cshtml.cs:50, :58, :68), `Index.OnGetDataAsync` (:20); `Parties/Index` (:28); `Parties/Card` load (:93, :120, :127); `Intermediaries/Index` (:13), `Card` (:20); `Reference/Classes` (:13), `Rates` (:34); `Policies/Index.OnGetAsync` (:64), `OnGetDataAsync` (:23, :28); `Policies/Pending` (:21); `Policies/New` (:28, :33, :37); `Policies/Cancel` (:28, :33, :39); `Policies/Endorse` (:27, :32); `Policies/Payment` (:35, :47); `Policies/Details` (`:88`, `:106`, `:111`, `:112`, `:118`, `:123`, `:131`, `:146`, `:153`, `:167`, `:173`, `:184`, `:194`) for both `OnGetAsync` and `OnGetDataAsync`; `Payments/Overdue` (:24, :34, :41); `Print/Policy` (:114, :130, :133, :139, :143); `Complaints/Index` (:21), `Card` (:29); `Compliance/AmlCases/Index` (:25), `New` (:18), `Card` (:35); `Reinsurance/Treaties/Index` (:14), `New` (:48), `Detail` (:23, :36); `Accounting/Periods` (:45, :46); `Audit/Index` (:44); `Users/Index` (:31, :38, :39); `Settings/Index` (:26); `Settings/Products` (:25, :26).

---

## 5. Project dependencies

| Project | ProjectReference | Packages |
|---|---|---|
| `src/Insurance.Domain` | — | — (embedded regulator templates + `JsUpper.txt`) |
| `src/Insurance.Application` | Domain | Microsoft.Data.SqlClient 7.1.1, Dapper 2.1.89, M.E.DependencyInjection.Abstractions 10.0.0, M.E.Http 10.0.0, System.Security.Cryptography.Pkcs 10.0.13, ExcelDataReader 3.8.0; embeds migrations |
| `src/Insurance.Web` | Application | none directly (Dapper + SqlClient arrive transitively) |
| `src/Insurance.Tools` | Application | Npgsql 10.0.3 |
| `tests/Insurance.Domain.Tests` | Domain, **Application** | xunit, Test.Sdk |
| `tests/Insurance.Integration.Tests` | Application, Tools | xunit, SkippableFact |
| `tests/Insurance.Web.Tests` | Web (+ compile-links `TestDatabase.cs`, `TestClock.cs`, `Fixtures.cs`, `ClaimWorld.cs`, `Policies/World.cs`, `GapsSupport.cs`, `LegacyDays.cs`, `ComplianceFixtures.cs`, `FailureInjection/Faults.cs`, `Worlds/*.cs`, `Accounting/MonthReport*.cs` from Integration.Tests) | Playwright 1.64.1, Mvc.Testing 10.0.13 |

Web uses Dapper / SqlClient directly:
- `using Dapper;` in 13 page files: `Pages/Index.cshtml.cs:5`, `Parties/Card.cshtml.cs:4`, `Payments/Overdue.cshtml.cs:1`, `Accounting/Periods.cshtml.cs:1`, `Print/Policy.cshtml.cs:4`, `Policies/{New:1, Details:2, Cancel:2, Index:1, Endorse:1, Payment:2, Pending:1}`, `Reference/{Rates:2, Classes:1}`.
- `DynamicParameters`: `Pages/Parties/PartyForm.cs:103`, `Pages/Parties/Card.cshtml.cs:204`.
- `SqlConnection` lambdas via `Db.ReadAsync(c => c.Query…)`: Index:20, Classes:13, Rates:34, Policies/Index:64, Pending:21, New:28/33/37, Cancel:28/33/39, Endorse:27/32, Payment:35, Details:88, Print:114; raw `tx.Connection` + Dapper: `Parties/Card.cshtml.cs:93`.
- View-level DB access: `Pages/Shared/_Layout.cshtml:5` `@inject Db`, `:14` `PermissionStore.ReadAsync`.
- No `using Microsoft.Data.SqlClient` in Web; the type is used implicitly through `Db.ReadAsync`'s `Func<SqlConnection, …>`.

---

## 6. Observations most relevant to the Domain / Application / Infrastructure / Api split

1. **Application = use cases + data access in one assembly.** `Insurance.Application` carries SqlClient/Dapper, `Data/*` (Db, Tx, Migrator, DatabaseAdmin), HTTP gateways (`Rates` → NBG, `SanctionsScreening.FetchAsync` → UN list), Excel readers/writers (`Excel/*`, `ReportWorkbooks`) and all Georgian UI texts (`Texts/*`, ~180 references from services). An Infrastructure project would take `Data/*`, the SQL inside the services, `Rates` HTTP, sanctions download, Excel I/O; Texts belong to the presentation side (or a shared resources project).
2. **Services are static and SQL-inline.** ~40 static service classes take `Db`/`Tx` and hand-write Dapper SQL; only `ISubrogationStore`/`SqlSubrogationStore` + the `SubrogationTransact` delegate already follow a repository + unit-of-work shape — usable as the template. Making services instance-based behind interfaces touches ~150 integration/web test files that call `PolicyActions.X(db, …)` etc. directly.
3. **Unit of work is chosen by the caller, sometimes in Web.** Transactions are opened in services (`AsUserAsync`) except T1–T5 (§4.1) and the 22 Web-SQL writes. A clean Api needs every write to enter one Application use case that owns `AsUserAsync`/actor setup.
4. **Services are web-shaped.** Inputs are raw form dictionaries / `PostedForm` (20 signatures) with field-name-keyed error dictionaries; outputs are redirect URLs (`FormAnswer.GoTo("/policies/{id}?issued=1")`, `Back("not-allowed")`, `RiCommissionLedger.BackTo`; 27 URL literals in ClaimActions, PaymentActions, PolicyActions, PolicyImport, RiCommissionLedger). For a JSON Api these need command DTOs and result/outcome codes; the Razor adapters can keep building URLs.
5. **Authorization is three-layered and scattered.** Permission keys (11, `Permissions.cs`) are checked pre+in-tx inside services (good, keep in Application); coarse role checks (`CanWrite`, `IsAdmin`, `CanSeeAml`, `RoleCode is "admin" or "accountant"`) live in page models — inconsistent spots: `/accounting/periods` GET has no page check; `/accounting/ri-commission` POST checks only the permission key, not the role the GET requires; permission-panel changes re-check `IsAdmin` only in the page, not in the tx. For OIDC: map the IdP identity to `core.app_user` → `CurrentUser`, and move role predicates into Application policies so Api and Razor share them.
6. **Reads are not transactional and fan out.** Most screens use `ReadTxAsync`/`ReadAsync` (no tx, per-statement snapshot) across several connections (e.g. policy card: Read + ReadTx + Snapshot + possible Rates tx). Query interfaces should decide explicitly whether a screen needs `SnapshotAsync`.
7. **Side-effecting reads.** `Rates.RateOnAsync`/`LatestRateOnOrBeforeAsync` fetch NBG and insert `exchange_rate` (own tx, no actor) during GETs and — worse for layering — **inside open write transactions** (PaymentActions.cs:284, :343, :561; ClaimActions.cs:289, :568, :606): an HTTP call and a second connection while row/app locks are held. Separate an `IExchangeRateProvider` (Infrastructure) and decide whether lookups inside a UoW must be cache-only.
8. **DB-enforced invariants are part of the design.** Append-only ledgers (revokes), period-open guard with shared/exclusive app lock, receipt/cession app locks, audit triggers driven by `session_context('core.user_id')`. The Infrastructure UoW must keep `set xact_abort on` + `sp_set_session_context` per transaction (`Db.cs:75–77`) and clear it (`:96`); any EF/other ORM adoption must preserve this.
9. **Auth is cookie + DB session, not ASP.NET auth.** `RequireUserFilter` + `RequestUser` (service-locator in `AppPageModel`); login/session writes run outside a transaction. Angular XHR relies on the same cookie and gets a 302 HTML on expiry — an Api needs 401/403 JSON and, with OIDC, bearer/cookie scheme via ASP.NET authentication handlers.
10. **Angular is a shell, not yet a client.** 2 Angular screens read 3 `OnGetData` handlers that are page-model handlers (not an API), reuse the Razor `OnGetAsync` (so the hidden Razor HTML is also rendered → duplicate queries on `/` and `/policies`), and return anonymous JSON objects with no contract. Every write and 55 of 57 routes remain native Razor inside `NativePage`. Nav/user info is scraped from the Razor layout DOM.
11. **Web-layer business code to move first:** `Pages/Parties/PartyForm.cs`, `Pages/Intermediaries/IntermediaryForm.cs`, `Pages/Compliance/ComplianceActions.cs` (suspension create/update with lock ordering), Settings number-series validation (`Settings/Index.cshtml.cs:99–153`, dynamic table SQL), complaints/AML case/user/settings writes.
12. **Domain is clean of infrastructure except regulatory workbook/zip handling** (`Domain/Regulatory/Workbook.cs`, `Zip.cs`, embedded templates) — acceptable as pure file-model code, but it is I/O-format logic that could move to Infrastructure if the Domain must stay format-agnostic. `Domain.Tests` already references Application (Excel parity), so the test split will need the same care.
13. **Subrogation has no UI** (Application + tests only) — any Api/Angular plan must decide whether it is in scope; it is the most "Api-ready" module.
14. **Tools bypass Db/Tx** (raw admin `SqlConnection`), which is correct for admin tasks but means Infrastructure must keep a separate admin-connection path (migrate, seed, import) outside the app's `core_app` connection.
