# Codex review 006 — ფინანსური შესწორებები და არქიტექტურის პირველი ნაწილი

**INS-DOTNET-MSSQL-006 · 24.10.2026 (თბილისი)**

წინა რევიუს სამი ძირითადი შესწორება განხორციელებულია: RI balance preview, FX ჩათვლის განაწილება და claim snapshot-ის უცვლელობა. ფინანსური ტოტის CI დამოუკიდებლად გადამოწმდა: **1957 PASS / 0 FAIL / 0 SKIP**. არქიტექტურის პირველ ნაწილში query ports, API, ზუსტი თანხები და Swagger/OIDC დამატებულია; ის ჯერ სრული მიღების ეტაპზე არ არის. ქვემოთ სამი კონკრეტული დარჩენილი ხარვეზი და შემდეგი სამუშაოა.

## შემოწმების ზუსტი საზღვარი

- ფინანსები: [PR #2](https://github.com/kandelaki5/Insurance-MSSQL-.net/pull/2), head **`d6372f1a0bfa61f86bbd6305858269e8d84247f9`**; ავტორის ფინანსური baseline `42b0d749b737aaf983a88e2fc2da2843b24df21b`.
- არქიტექტურა: `claude/architecture-api`, head **`6fef9fe933f1ff4294fceb9e222c3a5ae551b83f`**; კოდის ბოლო ცვლილება `0e09fcf`, შემდეგ დოკუმენტები. არქიტექტურული diff ფინანსურ ტოტთან: 52 ფაილი, +2353/−271 ხაზი.
- ამ ტოტისთვის გაიხსნა **[draft PR #3](https://github.com/kandelaki5/Insurance-MSSQL-.net/pull/3)**, base = `claude/sleepy-curie-81jkry`, რათა ცალკე გამოჩნდეს არქიტექტურის ცვლილებები და CI გაეშვას. ეს merge/deploy არ არის.
- წყარო გაიხსნა ცალკე არქივში `source.zip`/`source/`. live demo, საერთო ბაზა და Claude-ის კოდის ტოტები ამ რევიუთი არ შეცვლილა. ყველა ახალი/ძველი ფინანსური წესის ამომწურავი ხელახალი აუდიტი არ ჩატარებულა.

## წინა რევიუს მიღებული შესწორებები

| პუნქტი | გადამოწმებული შედეგი |
|---|---|
| R005-01 / R004-01 | `BalanceCoverage.Partly`; 00070-ის თანხა ჩანს და შეზღუდვა განმარტებულია. დამოუკიდებელი probe: coverage=Partly, `BS!E15=810.45`. დამატებულია subform/web balance/XLSX კონტროლი settlement/reversal-ზე |
| R005-02 | განაწილება დაგროვილი თანხის ნაბიჯებითაა: 12 × 0.01 USD @2.70 → `[2,3,3,2,3,3,2,3,3,3,2,3]` GEL თეთრი; ყველა ნაწილი ≥0, ყოველი prefix ≤32, ჯამი 32. დამოუკიდებლად გამეორდა. CI-ში გადის ნამდვილი PayClaimAsync და reversal regression-იც |
| R005-03-ის მთავარი snapshot update გზა | ახალი **0013** ცვლის trigger-ს EXCEPT შედარებით; 0012 უცვლელია. core_app ტესტები ამოწმებს snapshot-ის წაშლას, ცალკე NULL ცვლილებებს, NULL→value და კანონიერი description update-ს. CI PASS. ქვემოთ CP002-ის სხვა, ჯერ დარჩენილი NULL პრობლემა ცალკე საკითხია |
| R004-02 | კონკრეტული policy row-ის მოლოდინი დამატებულია. თემის ღილაკის async state და permission rows-ის შემთხვევითი რიგიც ტესტებში სწორადაა გათვალისწინებული |
| #363 | დამატებულია 6 combined book და 26 real-DB/HTTP/workbook კონტროლი. ავტორის სრული mutation ექსპერიმენტები ამ ციკლში ადგილობრივად არ გამეორებულა; ტესტები/მონაცემები კოდით შემოწმდა და CI-ში გავიდა |
| Angular CI | დაემატა lockfile-იდან `npm ci`, production build და committed assets-თან შედარება; ფინანსური CI-ის ეს ნაბიჯებიც PASS |

005-ის ზუსტად აღწერილი ძირითადი შემთხვევები ხელახლა გასასწორებლად აღარ ითვლება. #366/H46 ძირითადი პორტი შენარჩუნებულია. H46 total-loss/mixed-currency/XL და სხვა OPEN ქვეშემთხვევები მომხმარებლის გადაუწყვეტელ არჩევანად რჩება და დასრულებულ მხარდაჭერად არ გამოცხადდეს.

## R006-01 / P2 — reversal-ის offset metadata ჯერ ისევ შეიძლება დაიკარგოს NULL შედარებით

**ადგილი:** `database/migrations/0012_claim_gel_currency.sql:169–172`, `claim_payment_guard` / CP002.

0013 სწორად ასწორებს `core.claim` snapshot update-ს, მაგრამ payment reversal-ის nullable შედარებები კვლავ ძველი ფორმითაა:

```sql
not ((@o_offset_rate is null and @offset_rate is null) or @o_offset_rate = @offset_rate)
```

ორიგინალის `offset_rate=2.70`, `offset_currency='USD'` და reversal-ის ორივე ველზე NULL-ის შემთხვევაში შედარებები UNKNOWN ხდება. თუ claim/payee/rate სხვა მხრივ იგივეა, მთელი IF TRUE არ არის და CP002 არ ისვრის. offset CHECK-ის პირველი branch ორივე NULL-ს უშვებს; CP009 მხოლოდ ჩვეულებრივი payment-ის branch-შია. სწორი ფინანსური სარკისა და კანონიერად დაბრუნებული offset receipt-ის პირობებში metadata-ს დაკარგვის დამბლოკავი პირობა აღარ რჩება.

**მტკიცებულება:** source review + დამოუკიდებელი `SqlTypes` predicate კონტროლი: `CP002=UNKNOWN`, `throwsCP002=false`, CHECK all-null branch=true. სრული SQL reversal ამ სცენარზე ადგილობრივად არ შესრულებულა. არსებობს არასწორ non-null კურსზე უარყოფის ტესტი; ის non-null→NULL-ს ვერ ფარავს.

**საჭირო შესწორება:** ახალი corrective migration-ით NULL-safe exact-mirror შედარება; immutable 0012/0013/0014 არ გადაწერო. core_app-ით რეალური reversal სცენარი: rate/currency ორივე NULL, მხოლოდ ერთი NULL, სხვა non-null მნიშვნელობა და ზუსტი კანონიერი სარკე. ჰეშის/საიდუმლო context-ის დამატება პრობლემას არ აგვარებს. snapshot CHECK-ის ნაწილობრივ NULL rate/date კომბინაციებიც გაამყარე/შეამოწმე — CHECK UNKNOWN-ს ავტომატურად უარყოფად არ თვლის. ისტორიული all-null rows თავსებადი დარჩეს.

## R006-02 / P2 — Bearer გზაზე `api` scope მოთხოვნილი არ არის

**ადგილი:** `src/Insurance.Web/Infrastructure/HttpCurrentUserSource.cs:27–29`; დაკავშირებული `IdentityProvider.cs:115–116` და `ApiServices.cs:47–50`.

OpenAPI API-სთვის `api` scope-ს ასახელებს, მაგრამ runtime მხოლოდ token-ის ვალიდურობას, subject-ს და ცოცხალ session-ს ამოწმებს. issuer authorization principal-ზე API audience-ს უპირობოდ აყენებს, მაშინაც, როცა მოთხოვნილი scopes მხოლოდ `openid/profile`-ია. API gate-საც scope check არ აქვს. ამიტომ audience/signature/session validation scope-ის მოთხოვნას არ ცვლის.

ეს არის კოდით დადასტურებული authorization contract-ის დანაკლისი; ცალკე no-api-scope token-ის რეალური HTTP გაცვლა ჩემს ლოკალურ გარემოში არ შესრულებულა. არსებული OIDC ტესტები ყოველთვის `openid api`-ს ითხოვს. OpenIddict-ის [ოფიციალური API validation მაგალითიც](https://documentation.openiddict.com/guides/getting-started/implementing-token-validation-in-your-apis) scope-ს ცალკე `HasScope`-ით ამოწმებს.

**საჭირო შესწორება:** Bearer-ისთვის მოითხოვე შესაბამისი resource scope; ვალიდური, მაგრამ არასაკმარისი scope-ის პასუხი იყოს 403 / `insufficient_scope`, ხოლო არავალიდური token-ისა 401. cookie გზა, session revocation და ბიზნეს permissions ცალკე შენარჩუნდეს. ტესტში მოიპოვე რეალური code/token `scope=openid`-ით და დარწმუნდი, რომ დაცული API მას არ იღებს; `api` scope-ით მიიღოს. არაკანონიერ Bearer-ს მოქმედ cookie-ზე fallback არ მისცე.

## R006-03 / P2 — ახალი მრავალწაკითხვიანი query-ები რეალურად ტრანზაქციაში არ მუშაობს

**ადგილი:** `src/Insurance.Infrastructure/Policies/PolicyListQuery.cs:48–50` და `PolicyCardQuery.cs:15`.

ახალი query-ს კომენტარი ერთ read transaction-ს ჰპირდება, მაგრამ იძახებს `Db.ReadTxAsync`-ს. ეს helper პირდაპირაა დოკუმენტირებული „without a transaction“ და ქმნის `new Tx(connection, null, ...)`-ს (`Db.cs:42–46`). SQL Server-ის RCSI ცალკე statement snapshot-ებს აძლევს, მთელ ოპერაციას არა.

მაგალითად, list-ის count-ის შემდეგ ახალი policy-ს commit იძლევა ერთი მდგომარეობის `total`-სა და მეორის rows-ს. card-ის header/current premium/schedule/allocations/receivable სხვადასხვა commit-ის წაკითხვით შეიძლება შეიკრიბოს და ერთ JSON-ში ერთმანეთთან შეუსაბამო ფინანსური სურათი მივიღოთ. ერთი connection და Tx სახელის ობიექტი თვითონ ტრანზაქცია არ არის.

**მტკიცებულება:** call chain/source verification; local two-session SQL race ამ ციკლში არ გამეორებულა. ეს არ არის მთელი legacy-ის მასობრივი გადაკეთების მოთხოვნა — პირველ რიგში ახალი API query-ს ერთიანი კონტრაქტი უნდა შესრულდეს.

**საჭირო შესწორება:** top-level list/card read და მისი ერთად აღსაქმელი Razor caller გამოიყენოს ერთიანი read snapshot (`Db.SnapshotAsync` უკვე არსებობს) ან ეკვივალენტური უზრუნველყოფილი საზღვარი; caller-owned Tx overload ტრანზაქციის ნაწილად დარჩეს. დაამატე ორი-სესიის კონტროლი: count/rows-ს შორის insert და card-ის დაკავშირებულ reads-ს შორის payment/correction. პასუხი ერთ მთლიან committed მდგომარეობას უნდა აღწერდეს. helper-ის გლობალური სემანტიკა ბრმად არ შეცვალო; write UoW/locks/audit შეინარჩუნე.

## პირველი არქიტექტურული ნაწილის დარჩენილი მიღება

ეს პუნქტები ნაწილის დასრულების საქმეა და ზემოთ ჩამოთვლილ სამ დეფექტს დამატებით არ ითვლება:

1. **BFF/OIDC client flow ჯერ განხორციელებული არ არის.** Swagger-ისთვის embedded provider და API Bearer მუშაობის კოდი არსებობს, მაგრამ Angular login ისევ უშუალოდ custom session cookie-ია; OIDC client/callback/token server-side flow არ არის. ADR-ის სამიზნე და „as built“ განსხვავდება. აღწერე ეს PARTIAL-ად და დაასრულე არჩეული გადაწყვეტილება. გარე ფასიანი IdP სავალდებულო არ არის — არსებული embedded provider შეიძლება დარჩეს; server/client ნაწილის ზუსტი როლები გაარჩიე. პირველი ნაწილის დასრულება მხოლოდ Swagger Authorize-ს არ ნიშნავს.
2. **Server pagination frontend-ს ჯერ არ გამოუყენებია:** API-ს აქვს offset/limit/total, Angular კი `limit=200` პირველ ნაწილს იღებს, total-ს უგულებელყოფს და 7-სტრიქონიან ადგილობრივ გვერდებად ჭრის. UI ამ 200-იან ზღვარს ხსნის, ამიტომ დამალულ რეგრესიად არ ვაფასებთ; თუმცა B4-ის საბოლოო pagination/filter/sort მიღება ჯერ არ შესრულებულა. server-backed paging და მთლიან შესაბამის dataset-ზე filter/count/summary ცალსახად დასასრულებელია.
3. **ფენები გარდამავალია:** query adapter-ები Infrastructure-შია, მაგრამ `Db/Tx` და ბევრი SQL Application-ში დარჩა; IPolicyCardQuery-ის ერთ overload-ს კონკრეტული Tx გადააქვს. ეს map-ში აშკარადაა აღწერილი და მთელი მიგრაცია დასრულებული არაა. შემდეგი ნაბიჯები მიზნობრივი UoW/query ports-ით გაგრძელდეს; მთელი კოდის rename არ დაიწყოს.
4. **Deploy მიღება ღიაა:** Data Protection გასაღებების persistence, production OIDC certificates/issuer/callback და IIS config/recycle ტესტი ჯერ შესასრულებელია. მიმდინარე draft არ განათავსო საერთო დემოზე მხოლოდ compile/CI წარმატების საფუძველზე.

## შემოწმების მტკიცებულება

| გარემო / ვერსია | შედეგი |
|---|---|
| ფინანსური PR #2 CI, `d6372f1`, [run 40034232172](https://github.com/kandelaki5/Insurance-MSSQL-.net/actions/runs/40034232172) | **SUCCESS**; Linux Domain 597, Integration 988, Web 372 — **1957 PASS / 0 FAIL / 0 SKIP**. Angular clean install/build/asset comparison, PG world და „No test skipped“ PASS |
| იმავე ფინანსური CI-ის merge checkout | `04b2514d54c8e8cbe76d32eda5b493f43f7854d2` = d6372f1 + main 5e77da9; source head-ში არ ირევა |
| ადგილობრივი Windows Domain, architecture `6fef9fe` | **597 PASS / 0 FAIL / 0 SKIP** |
| ადგილობრივი architecture layer tests | **3 PASS / 0 FAIL / 0 SKIP** |
| ადგილობრივი architecture Release solution build | **0 error / 0 warning** |
| ადგილობრივი Angular production build | PASS, ადრე დაყენებული შესაბამისი dependencies-ით; clean install ადგილობრივად ცალკე არ გამეორებულა |
| R005-01/02 probe + CP002 predicate | აღწერილია ზემოთ; შედეგები შენახულია |
| არქიტექტურის [PR #3 CI 40048858538](https://github.com/kandelaki5/Insurance-MSSQL-.net/actions/runs/40048858538) | ანგარიშის მომზადებისას მიმდინარეობს; სრული შედეგი ჯერ არ დასტურდება |

ავტორის architecture 1980 PASS (597+988+395) მისი გარემოს ანგარიშია. ადგილობრივად სრული MSSQL/PG/Web/browser suite არ გაშვებულა, რადგან isolated local SQL fixture არ არის; საერთო ჰოსტინგის ბაზა ამისთვის არ გამოყენებულა. ეს სიზუსტე შენარჩუნდეს შემდეგ პასუხშიც.

მტკიცებულებები: `source.zip`, `source/`, `domain-tests.log`, `results/`, `build.log`, `layer-tests.log`, `layer-results/`, `angular-build.log`, `independent-probes.log`, `probes/`, `github-ci-evidence.json`.

## Claude-ის შემდეგი დავალება

1. პირველ რიგში R006-01–03 და მათი მიზნობრივი regression ტესტები. საჭირო ახალი migration-ის ნომერი ფინანსურ და architecture ტოტებზე შეათანხმე; უკვე არსებული 0014 OIDC migration არ გადააწერო და ერთ ნომერს ორი განსხვავებული migration არ მისცე.
2. PR #3-ზე შეინარჩუნე architecture delta და ფინანსურ parent-თან თავსებადობა; ფინანსური guard-ის საჭირო backport გააკეთე გასაგები commit-ით. main merge/deploy ამ დავალებას არ მოჰყვება.
3. PR #3-ის სრული CI გაამწვანე და ჩააბარე ფინანსური/architecture ტესტები SHA-ების მიხედვით. სავალდებულო suite-ში 0 FAIL / 0 SKIP, frontend build/committed assets თანხვედრა და ახალი უარყოფითი კონტროლები.
4. შემდეგ დაასრულე BFF/OIDC მიღება და policy paging/filter/sort არსებული დიზაინის შენარჩუნებით. სანამ პირველი ნაწილი მიღებული არაა, policy/payment/claim writes-ის ფართო API გადაწერას ნუ დაიწყებ.
5. დასრულებულ read-only policies/auth ნაწილზე დააფიქსირე მიღებული SHA; შემდეგ გააგრძელე დამტკიცებული B6-ის შემდეგი მოდული მცირე PR-ით, საჭირო საერთო UoW პორტებით. უბრალოდ შემდეგი ნაბიჯის დაწყების თანხმობას ნუ დაელოდები — ეს სამუშაო უკვე მოთხოვნილია.
6. `CLAUDE-RESPONSE.md`-ს დაამატე **INS-DOTNET-MSSQL-006**, ზუსტი SHA/CI, თითო finding-ის მიზეზი/ფაილი/ტესტი/მტკიცებულება, PARTIAL/OPEN/NOT TESTED. PR #2-ის აღწერაც განაახლე: ჯერ ისევ 003, 0012 და ძველი 1926-ის შეჯამება წერია, მიუხედავად ახალი 004/005 პასუხისა და 0013-ისა. პასუხი PR #3-ზეც დადე.

არქიტექტურის სრული B0–B6 დავალება: [004 prompt](https://github.com/kandelaki5/Insurance-MSSQL-.net/blob/0eee6367654c4c32631b7c2eac4974cbd1d500fb/CLAUDE-NEXT-TASK-004-FINANCE-THEN-ARCHITECTURE.md). ეს 006 არის უახლესი მიღების შენიშვნა; უკვე დახურული საკითხები თავიდან გასაკეთებელი არ არის.

