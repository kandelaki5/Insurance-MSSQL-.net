# Claude-ის დავალება — ჯერ ფინანსები, შემდეგ არქიტექტურის მიგრაცია

**ID: INS-DOTNET-MSSQL-004. თარიღი: 24.10.2026.**

მომხმარებლის დავალებაა: ჯერ დაასრულე მიმდინარე ფინანსური სამუშაო, გაასწორე მიმდინარე შემოწმების პრობლემები, ჩააბარე შემოწმებადი შედეგი და მხოლოდ ამის შემდეგ დაიწყე ქვემოთ აღწერილი არქიტექტურის ეტაპობრივი მიგრაცია. ორივე ეტაპი ავტორიზებულია; ფინანსური მიღების პირობების შესრულების შემდეგ მხოლოდ შემდეგი ეტაპის დასაწყებად ხელახალი თანხმობა საჭირო არ არის.

მიზანი: არსებული სადაზღვევო პროგრამის განვითარება **.NET 10 Web API + მიზნობრივი Repository/Query interfaces + ფენებად გამოყოფილი backend + არსებული Angular frontend**, OpenAPI/Swagger-ით და OAuth 2.0/OIDC ინტეგრაციით. ბიზნესწესები, ფინანსური გამოთვლები, ქართული ტექსტები, MSSQL-ის ფინანსური გარანტიები და მომხმარებლის მიერ მოწონებული დიზაინი უნდა შენარჩუნდეს.

## 0. დაწყება და რეალური კონტექსტი

1. წაიკითხე `CODEX-REVIEW-004.md`, არსებული `CLAUDE.md`, წინა დავალებები/გადაწყვეტილებები, `CLAUDE-RESPONSE.md`, `STATUS.md`, `MIGRATION-COVERAGE.md` და ღია PR-ების provenance მასალა. 004 არის ახალი სამუშაო ინსტრუქცია; წინა ისტორიას ნუ წაშლი. ამ ფაილის არქიტექტურული მითითება განზრახ აახლებს ადრინდელ ტექნოლოგიურ მოთხოვნას: frontend Angular-ზე რჩება, backend .NET-ზე.
2. Codex-მა განიხილა `kandelaki5/Insurance-MSSQL-.net`-ის `claude/sleepy-curie-81jkry` branch-ის ზუსტი head **`60595249d95f8218d88ba0466d834909e1114ada`** და მოგვიანო **`48d4b7219bb4b62552c6e4f0cb926fe6dfc5b456`**-ის დამატებითი +11-ხაზიანი browser-test შესწორება. დაწყებისას გადაამოწმე უახლესი head და დაუკავშირე შემდგომი ცვლილებები ამ baseline-ს. შემდგომ commit-ში უკვე გასწორებული საკითხი მეორედ არ გააკეთო; წარმოადგინე მტკიცებულება.
3. **Angular უკვე არსებობს `frontend/`-ში** და ჩვენი დიზაინი/ღია-მუქი თემა უკვე merge-ებულია (`c643519`). მთავარი და პოლისების გვერდები Angular-ია, authenticated read handlers-ით; სხვა ეკრანები დროებით იყენებს Angular shell-ში ჩასმულ Razor ფორმებს. UI თავიდან არ შექმნა და არსებულს ახალი Angular skeleton-ით არ ჩაანაცვლო.
4. მიმდინარე backend: Domain; Application-ში use cases + Dapper/SQL/Db/Tx; Web-ში Razor Pages; Tools CLI. მოქმედი auth: custom server session/cookie + scrypt. თავდაპირველი პრომპტის „SPA არ არის“ უკვე მცდარია. SDK-ის ზუსტი ვერსია წაიკითხე `global.json`-იდან; მხოლოდ არქიტექტურის გამო runtime/frontend major upgrade არ გააკეთო.
5. cloud-ში Windows საქაღალდის წაკითხვა ნუ ივარაუდებ. სამუშაო ადგილი შენი Git checkout-ია. რევიუსა და ამ დავალების ტექსტი თვითკმარია; დამატებითი რეალურად მიუწვდომელი წყარო ზუსტად დაასახელე და დამოუკიდებელი საქმე გააგრძელე.

## A. პირველი ეტაპი — ფინანსური სამუშაოს ბოლომდე მიყვანა

**ამ ეტაპის მიღებამდე არ დაიწყო მასობრივი ფაილების გადაადგილება, ახალი auth-ის შემოყვანა, ძველი UI-ის წაშლა ან repo-wide rename.** არსებული ფინანსური ცვლილებები დასრულდეს ცალკე გასაგები commit-ებით.

### A1. მიმდინარე რევიუს შესწორებები

- **R004-01:** RI commission 810.45 GEL იწერება N1-4 `E26`-სა და ბალანსის `BS!E15 / 00070`-ში, მაგრამ `FinancialAnnex.BalanceLines`-ის `Filled=false`-ის გამო ვებბალანსში თანხა დამალულია. შეასწორე preview/coverage სემანტიკა: ამ ეტაპზე აღრიცხული საკომისიოს თანხა გამოჩნდეს; სხვა დაუმუშავებელი გადაზღვევის მოთხოვნები სრულად აღრიცხულად არ გამოაცხადო. შეამოწმე web preview ↔ XLSX ერთგვაროვნება საწყისი ნაშთის, ნაწილობრივი settlement-ისა და reversal-ის შემდეგ.
- **R004-02:** CI run `40014359740`-ში correction-ის BrowserFlow ტესტი ჩატვირთვის მდგომარეობას კითხულობდა. **შენს `48d4b72` commit-ში მოლოდინი და განზრახ დაგვიანებული data-response კონტროლი უკვე დამატებულია**; იგივე გასწორება თავიდან არ გააკეთო. დაადასტურე მისი სრული საბოლოო CI (`40016973966` ან შემდგომი საბოლოო SHA). საჭიროების შემთხვევაში Playwright მოლოდინი კონკრეტული policy row-ის გამოჩენას მიაბი, რადგან loading-ის ნიშნულის არყოფნა Angular bootstrap-მდეც შესაძლებელია. შეინარჩუნე `320.00 GEL` და correction-ის ცალკე პოლისად არჩათვლის შემოწმებები. ფიქსირებული readiness sleep / assertion-ის ამოღება / SKIP დაუშვებელია; response-ის განზრახ დაგვიანება regression-ის საკონტროლოდ დასაშვებია.
- **R004-04:** განაახლე `CLAUDE-RESPONSE.md`-ში 003-ის ფაქტობრივი პასუხი და ახალი 004 ჩანაწერი; `STATUS.md`, `MIGRATION-COVERAGE.md`, findings inventory და PR-ის აღწერა. ისტორიული შედეგები ისტორიულად მონიშნე. ახლა #362-ს N/A ეწერება, თუმცა მოდული უკვე არსებობს; ასეთი შეუსაბამობები გაასწორე.
- წინა R01–R06-ზე განხორციელებული შესწორებები შეინარჩუნე. უკვე გასწორებული დაზიანებული ჰეშის უარყოფა, SG034-ის app-role დაცვა, HTTPS/proxy ქცევა, Windows parity და CI-ის PG migration ტესტები რეგრესიისგან დაიცავი.

### A2. #362 — გადამზღვევლის საკომისიოს ანგარიშსწორება

ძირითადი პორტი უკვე დამატებულია: `RiCommission`, `RiCommissionLedger`, migration `0011`, `/accounting/ri-commission`, `reinsurance.commission.settle`, N1-4 `00140`. გადაამოწმე ძველი [PR #362](https://github.com/kandelaki5/Insurance-Software/pull/362)-ის ზუსტი წყარო/შენიშვნები და დაასრულე acceptance; თავიდან არ გადაწერო მუშა მოდული.

აუცილებელი კონტროლებია: ერთ ან რამდენიმე treaty-ზე ჯამური ზუსტად 100% cession; ნაწილობრივი/სრული offset/receipt; reversal; თარიღობრივი შეფასება და FX; დახურული თვე; audit/actor; permission revocation ტრანზაქციაში; ერთდროული settlement; backdated settlement, რომელიც მოგვიანო ნაშთს ვერ გადააჭარბებს. მოგვიანო გაუქმებით წარმოშობილი უარყოფითი ნაშთი აჩვენე გამჭვირვალედ; მისი ახალი ბუღალტრული კლასიფიკაცია თვითნებურად არ მოიგონო.

### A3. #366 — Income Statement / Form 1

პორტი დაასრულე [PR #366](https://github.com/kandelaki5/Insurance-Software/pull/366)-ისა და მისი პირდაპირი დამოკიდებულებების ზუსტი diff-ებით: **IS, IS-COM, P&C(NL)**, შესაბამისი საკომისიოები და უკვე დადასტურებული ბალანსის სტრიქონები. ცარიელი workbook ფურცელი განხორციელებული შესაძლებლობა არ არის.

- ჯერ შეადგინე თანხის ყოველი სტრიქონის წყაროსა და პერიოდის map; შემდეგ გადაიტანე დადასტურებული ფორმულები. შეინარჩუნე ნიშნები, გაუქმებები/კორექციები, დარიცხული/გამომუშავებული პრემია, რეზერვების მოძრაობა, გადაზღვევის წილი და თანხების ას-of/პერიოდის საზღვრები წყაროს მიხედვით.
- ძველი PR-ების დაუზუსტებელი საკითხები — segment-ის არჩევანი, უარყოფითი earned key, multi-class XL split, deposit XL, bank deferred N1-8 — გადაამოწმე შესაბამის გადაწყვეტილებებთან. რაც გადაწყვეტილია განახორციელე; რაც არაა, მონიშნე კონკრეტული OPEN წყაროთი და ზუსტად გამიჯნე შედეგში. ცარიელი/უცნობი მონაცემი გამოგონილ ნულად არ აქციო.
- შეამოწმე თვიური/წლიდან მიმდინარე პერიოდის ქცევა იქ, სადაც წყარო ამას ითხოვს; ქვეფორმებისა და ბალანსის შეჯერება; web preview/XLSX/PDF ან ბეჭდვის თანხვედრა; რეალური XLSX-ის ჩაწერა/ხელახლა წაკითხვა და სიზუსტის დაცვა.
- არ შექმნა სრული ახალი General Ledger ან დაუმტკიცებელი IFRS მოდელი ამ პორტის სახელით. გადაიტანე მოთხოვნილი და დადასტურებული ფუნქციონალი.

### A4. H46 — GEL ზარალი FX პოლისზე

განახორციელე `docs/07` §4.5-ის დადასტურებული დიზაინი: ზარალი GEL-ში, პრემიის ჩათვლა პოლისის ვალუტაში გადახდის დღის კურსით, საჭირო `offset_rate` / `offset_currency` და audit მონაცემებით. „საჭიროა თუ არა ეს ფუნქცია?“ თავიდან აღარ იკითხო.

- გაარჩიე claim/payment ვალუტა და premium/offset ვალუტა; არც ერთი თანხა იმავე ნომინალით სხვა ვალუტაში არ ჩათვალო.
- მხოლოდ ახალი დანომრილი migration დაამატე საჭირო სქემისთვის. არსებული ზარალების ისტორია, ადრე დაშვებული same-currency ქცევა, თანხის ლიმიტები, franchise, reserve, partial payment, total-loss/set-off, reinsurance share და reversal თავსებადად დარჩეს.
- რეალური DB/HTTP კონტროლებით დაამტკიცე: USD/EUR პოლისზე GEL ზარალი; სწორი კურსი; კურსის არქონა; ნაწილობრივი გადახდა; premium offset; განმეორებითი დაჭერის/ორი სესიის დაცვა; შეჩერება/სანქციები; დახურული თვე; audit; reversal-ის ფინანსური სარკე. დაუზუსტებელი კონკრეტული rounding/date არჩევანი ცალკე აღწერე და არსებული დადასტურებული წესი გამოიყენე, თუ წყარო ადგენს.

### A5. ღია PR-ების სრული დაფარვის map

გადახედე მოთხოვნილ ღია PR scope-ს, მათ შორის #207/#218/#225/#212/#211/#214/#215, #350, #361, #362, #366 და #369/#370-ის მოთხოვნებთან კავშირს. საწყის `17c1f1f`-ში არყოფნა თავისთავად N/A არ არის. შეადგინე ყოველი ფუნქციისთვის:

`legacy PR + SHA → დადასტურებული მოთხოვნა → ახალი ფაილი/მიგრაცია → test/CI → PORTED / PARTIAL / OPEN / N/A (კონკრეტული მიზეზი)`.

გადაფარული PR-ები ორჯერ არ ჩათვალო. ისტორიული #354/#364 DB-run მტკიცებულება ახალ ბაზაზე გაშვების ნებართვას არ იძლევა. უცნობი franchise/Q1/Q2/სხვა ღია გადაწყვეტილებები ჩუმად არ გადაწყვიტო; ადრე მიღებულ ექვს პასუხს დაეყრდენი.

### A6. ფინანსური ეტაპის მიღება და baseline

1. Release build — 0 error; არსებული warning-as-error კონტროლი შენარჩუნდეს. Angular production build და სუფთა dependency install-ის გამეორებადი ნაბიჯი CI-ში ჩართე.
2. საბოლოო SHA-ზე Domain, რეალური იზოლირებული MSSQL/app-role, ორი სესია, PG import/reconciliation და HTTP/Chromium ტესტები: **0 FAIL / 0 SKIP** სავალდებულო suite-ში; Windows Domain/parity ცალკე. ძველი golden შედეგები მარტო ტესტების გასამწვანებლად არ გადაწერო.
3. ფინანსური დემო ნაკადები: client → policy → approval → payment → report; claim → reserve → decision → payment → reversal; reinsurance premium/claim shares; RI commission settlement; ახალი #366/H46 ნაკადები; Excel/print. მოქმედი shared demo DB destructive fixture-ად არ გამოიყენო.
4. დაუზუსტებელი ბიზნესქვეკითხვა ზუსტად OPEN-ად დარჩეს და არ გამოცხადდეს დასრულებულ ფუნქციად. თუ ის ფინანსური ეტაპის მიღებას უშუალოდ ბლოკავს, მიუთითე კონკრეტული საჭირო გადაწყვეტილება; დამოუკიდებელი ფინანსური სამუშაო გააგრძელე. ასეთ შემთხვევაში ფართო არქიტექტურულ მიგრაციაზე არ გადახვიდე.
5. `CLAUDE-RESPONSE.md`-ში გამოაქვეყნე FINANCE COMPLETE ან PARTIAL კონკრეტული მიზეზით; მიუთითე ზუსტი SHA, CI ბმულები, PASS/FAIL/SKIP და feature coverage. test pass პროცენტი პროექტის დასრულების პროცენტი არ არის.
6. მიღებული ფინანსური SHA დააფიქსირე როგორც შემდგომი არქიტექტურის baseline. მხოლოდ მწვანე CI არ ცვლის გამოტოვებული მოთხოვნის განხორციელებას. შემდეგ დაიწყე B ეტაპი ცალკე `claude/architecture-...` სამუშაო branch-ზე, შენს მიღებულ ფინანსურ head-ზე დაყრდნობით.

## B. მეორე ეტაპი — არქიტექტურა მცირე, შემოწმებადი ნაწილებით

### B0. ინვენტარი და მოკლე ADR-ები

სანამ მასობრივად გადაიტან კოდს, დაწერე:

- არსებული Razor route/action/download/print → Application use case → SQL/transaction/permission → ტესტის რუკა;
- არსებული Angular route/component/data handler → მომავალი API endpoint რუკა; UI-ის რომელი ნაწილები ჯერ native bridge-ს იყენებს;
- არქიტექტურის/Repository-UoW, authentication/IdP/BFF, API money/date/error contracts და hosting/deployment ADR-ები;
- შემდეგი პატარა ცვლილებების რიგი და თითოეულის rollback გზა.

ამ ეტაპზე მიზანია მოდულებად გაყოფილი ერთი პროგრამა. microservices, message broker, event bus, CQRS framework ან generic abstraction მხოლოდ „enterprise“ სახელისთვის არ დაამატო.

### B1. ფენები და დამოკიდებულებები

- `Insurance.Domain` — სუფთა ფინანსური წესები/value objects; HTTP/DB/framework დამოკიდებულების გარეშე.
- `Insurance.Application` — use cases, DTO-ები, კონკრეტული repository/query/UoW interfaces; HTTP/SqlClient/Dapper/Excel reader იმპლემენტაციები არ უნდა შევიდეს საბოლოო სუფთა ფენაში. საჭირო Excel შესაძლებლობებისთვის გამოიყენე მიზნობრივი პორტები; სუფთა ფინანსური ფორმულები არ გადაადგილდეს შემთხვევით.
- `Insurance.Infrastructure` — Dapper/SqlClient, repository/query იმპლემენტაციები, Db/Tx, Migrator, external integration/Excel I/O და auth ინფრასტრუქტურა საჭიროებისამებრ.
- `Insurance.Api` — endpoints/controllers, auth, DTO mapping/validation, OpenAPI, ProblemDetails; SQL და ფინანსური ფორმულები აქ არ იყოს.
- `Insurance.Tools` — CLI ბრძანებები შენარჩუნდეს და ახალი ფენები DI-ით გამოიყენოს.
- `Insurance.Web` — დროებით თავსებადი host/ძველი UI; წაიშალოს მხოლოდ მისი ყველა route/action/download/print-ის ჩანაცვლების მიღების შემდეგ.

მიმართულება: `Api → Application → Domain`; `Infrastructure → Application/Domain`. `Api → Infrastructure` მხოლოდ composition root-ში/DI რეგისტრაციისთვის. ციკლური reference აკრძალულია. ფენების წესები project-reference/architecture ტესტით შემოწმდეს. გარდამავალი SQL მხოლოდ ჯერ არმიგრირებულ კოდში დარჩეს, map-ში აშკარა აღრიცხვით.

### B2. Repository / Query / Unit of Work

- Dapper და parameterized SQL შეინარჩუნე; EF Core-ზე ნუ გადახვალ.
- Repository მოერგოს aggregate-სა და use case-ს; ანგარიშებისთვის გამოიყენე მიზნობრივი `I*Query`. ნუ შექმნი თითო ცხრილზე უნივერსალურ CRUD `IRepository<T>`-ს და ნუ გაამრავლებ უსარგებლო ფენებს.
- **ერთი use case = არსებული საჭირო ატომური ტრანზაქცია.** იმავე connection/transaction-ში შესრულდეს permission-ის სავალდებულო შემოწმება, საჭირო locks, ledger writes და audit actor/session context. ყოველი repository call-ისთვის ცალკე connection/commit არ გახსნა.
- ავთენტიფიცირებული პიროვნება სერვერზე გადააბი შიდა `app_user`-ის სტაბილურ ID-ს; actor არ აიღო request body-დან. `issuer + subject` მტკიცედ დააკავშირე მომხმარებელთან; მხოლოდ ელფოსტის თანხვედრით ანგარიშები ავტომატურად არ გააერთიანო.
- შეინარჩუნე `core_app`-ის მინიმალური უფლებები, append-only ფინანსები, closed-period triggers, SG034 და race/deadlock handling. pooled connection-ში სხვისი session context არ დარჩეს.
- უკვე გამოყენებული migration ფაილები უცვლელია. ახალი სქემა = ახალი დანომრილი migration; ადმინისტრაციული მიგრაცია და ჩვეულებრივი app connection ცალ-ცალკე დარჩეს.

### B3. OAuth 2.0 / OIDC — ჯერ hosting-სთან თავსებადი არჩევანი

Login-ისთვის გამოიყენე **OpenID Connect**; API access-ისთვის OAuth 2.0. ერთი ხელით გაცემული JWT OAuth/OIDC ინტეგრაციის დასრულება არ არის.

საწყის ვარიანტად შეაფასე **BFF + same-origin Angular/API**: Angular კოდი ცალკე frontend პროექტად რჩება, მაგრამ ერთი origin-იდან შეიძლება გაიცეს; access/refresh tokens სერვერზე ინახება, ბრაუზერი იყენებს Secure/HttpOnly session cookie-ს და CSRF დაცვას. „ცალკე frontend პროექტი“ ავტომატურად ცალკე domain-სა და browser-token შენახვას არ მოითხოვს.

- ADR-ში შეადარე BFF და პირდაპირი SPA Code+PKCE, მიმდინარე SmarterASP/IIS-ის შეზღუდვების, დომენის/HTTPS-ის, callback URL-ების, secrets-ისა და server-side persistence-ის გათვალისწინებით. სხვა hosting-ის/ფასიანი სერვისის აუცილებლობა დაასაბუთე; ჩუმად არ შეცვალო განთავსების ადგილი.
- IdP აირჩიე მინიმალური საჭირო სირთულით: გარე provider ან თავსებადი OpenIddict/სხვა. შეამოწმე მხარდაჭერა/ლიცენზია/ხარჯი და ტესტირებადობა. მხოლოდ სიტყვიერი არჩევანი საკმარისი არ არის — dev/test login იმუშაოს.
- Code flow + PKCE; Implicit და Resource Owner Password grant არ გამოიყენო. OIDC state/nonce/correlation, redirect URI და PKCE სწორად შემოწმდეს.
- თუ API JWT-ს იღებს, შეამოწმე issuer, audience, signature, allowed algorithms და lifetime; ბრაუზერში server client secret არასოდეს მოათავსო.
- BFF ვარიანტში token-ები ბრაუზერს არ დაუბრუნო. პირდაპირ SPA ვარიანტში access/refresh token-ის `localStorage/sessionStorage`-ში შენახვა სავალდებულო გამოსავლად არ მიიღო: აღწერე უსაფრთხო სიცოცხლის ციკლი/მეხსიერების გამოყენება, refresh rotation/revocation და login flow. არსებული theme-ის localStorage პარამეტრი მხოლოდ ვიზუალური პრეფერენციაა.
- cookie writes დაიცავი CSRF-ით; CORS CSRF დაცვას არ ცვლის. SameSite/Secure/callback ქცევა არჩეულ flow-ს მოარგე. CORS საჭიროებისას მხოლოდ ზუსტ dev/UAT/prod origins-ზე დაუშვი; credentials-თან wildcard არა.
- არსებული როლები, ჯგუფები, overrides და `PermissionStore` სემანტიკა შეინარჩუნე. claims/scopes არ გახდეს მოძველებული უფლებების მუდმივი წყარო: write-ზე ბაზაში მიმდინარე უფლებების სავალდებულო შემოწმება ტრანზაქციაში დარჩეს. Angular guard მხოლოდ UI-ის მოხერხებულობისთვისაა.
- მოქმედი მომხმარებლებისა და ვალიდური scrypt ჰეშების თავსებადობა შეინარჩუნე provider user store-ით ან დოკუმენტირებული მიგრაციით; დაზიანებული ჰეშები კვლავ უარყოფილია. ყველა მომხმარებლის პაროლის იძულებითი უცნობი reset არ გააკეთო.
- შეინარჩუნე throttling, disabled user/logout/expiry/revocation, audit და proxy trust საზღვარი. DB-დან შეცვლილი ანგარიშის დაბლოკვა მოქმედ სესიაზეც მოთხოვნილი სისწრაფით გავრცელდეს.
- test issuer მხოლოდ test/dev გარემოში; წარმოებაში მის გასაღებებს/ბაიპასს ვერ გამოიყენებ. secrets/connection strings/კლიენტის საიდუმლოებები მხოლოდ env/user-secrets/დაცულ კონფიგურაციაში, არა Git-ში.

### B4. API კონტრაქტები და Swagger/OpenAPI

- consistent `/api/v1/...` endpoints და სახელდებული request/response DTO-ები; DB row-ები პირდაპირ არ დააბრუნო. ფორმის ბიზნესუარყოფა უცნობ 500-ად არ იქცეს; HTTP სტატუსები/ProblemDetails და სტაბილური business error codes შეთანხმებულად გამოიყენე, ქართულ ტექსტებთან ერთად.
- **ფული JSON-ში ზუსტად:** შეთანხმებული ფორმა, მაგალითად `{ "minor": "81045", "currency": "GEL", "scale": 2 }`. minor units არის სტრიქონი; დიდ long/BigInteger-ს JS `number`-ში არ გადააქცევ. კურსები/პროცენტები საჭირო სიზუსტით decimal string-ად გაიცეს. Angular-ში ზუსტი parsing/formatting გამოიყენე; ფინანსური ფორმულები UI-ში არ გადაიტანო.
- DateOnly — `YYYY-MM-DD`; timestamp — UTC ISO 8601; ბიზნესდღის გამოთვლა — არსებული თბილისის კონტრაქტით. period/asOf, effective/booked/paid თარიღების მნიშვნელობები ცალსახად აღწერე.
- ჩამონათვლებში server-side pagination/filter/search/sort; totals მხოლოდ მიმდინარე გვერდის ჯამი არ იყოს. pending/active/cancelled და issued/current premium მკაფიოდ განასხვავე.
- writes-ზე შეინარჩუნე არსებული duplicate-click/idempotency, concurrency და ატომურობა; არ შემოიტანო ახალი თანხობრივი ქცევა transport-ის შეცვლის გამო. Excel/print/download permissions უცვლელი იყოს.
- OpenAPI ყველა ახალ endpoint-ზე: summary, DTO schema, money/date მაგალითები, permission მოთხოვნები, 400/401/403/404/409/422 პასუხები გამოყენების მიხედვით. სურვილისამებრ generated Angular client, თუ კონტრაქტს სიზუსტით ინარჩუნებს.
- Development/UAT-ში Swagger UI + რეალური OAuth2 Authorization Code/PKCE Authorize ცალკე public client-ით, სწორი scopes/callback-ით. BFF-ის გამოყენებამ Swagger-ში secret-ის ჩაწერა არ უნდა მოითხოვოს. Production Swagger გამორთული ან შესაბამისი დაცვით შეზღუდული იყოს.

### B5. პირველი სამუშაო ვერტიკალური ნაწილი

**პირველად: login/session-ის არჩეული flow + მხოლოდ წასაკითხი policies list/detail.** არსებული Angular დიზაინი გამოიყენე; საწყის ეტაპზე payment/claim write არ აირჩიო.

მცირე ნაბიჯები:

1. ADR/route map/API contract და Infrastructure/Api skeleton — ყოველი ცვლილება აწყობადი იყოს.
2. კონკრეტული policies query interface/იმპლემენტაცია, არსებული ფინანსური მონაცემების სემანტიკის დაცვით; SQL controllers-ში არ დარჩეს.
3. არჩეული OIDC/BFF ან PKCE ინტეგრაციის სამუშაო login, logout და revocation/permission კონტროლები; Swagger Authorize.
4. Angular policies list/detail დაუკავშირე ახალ API-ს. ჩვენი layout, light/dark theme, ქართული ტექსტები, responsive ქცევა, loading/empty/error მდგომარეობები და keyboard accessibility შენარჩუნდეს. URL query/search და server paging ერთგვაროვნად მუშაობდეს.
5. დაამტკიცე browser → auth/BFF → API → query → რეალური isolated MSSQL → UI გზა. შეადარე issued/current premium, correction-ის ერთადერთი დათვლა, ვალუტა/ზუსტი ფული, outstanding/asOf, უფლებები და pagination ძველ მიღებულ baseline-ს.

მხოლოდ skeleton პირველი სამუშაო ნაწილის დასრულება არ არის. ძველი Razor გვერდების ამოშლა ან დანარჩენი მოდულების პარალელურად მასობრივი გადაწერა ამ პირველი ნაწილის მიღებამდე არ გააკეთო.

### B6. შემდეგი მოდულები და გადართვა

პირველი ნაწილის შემდეგ მცირე PR-ებით გააგრძელე: Parties/reference → Policies writes → Payments → Claims → Reinsurance → Finance/Accounting/Regulatory → Compliance → Users/permissions/audit/settings; ზუსტი რიგი დამოკიდებულებებით დაასაბუთე.

თითო module-ზე SQL/repository extraction, use case/API, Angular ეკრანი, permissions, test parity და docs ერთად ჩააბარე. frontend არსებული `frontend/` განავითარე standalone/lazy feature components-ით; თავიდან ახალი ცალკე UI არ ააგო. ქართული labels/errors თანმიმდევრულად მართე. საჭირო dev proxy აღწერე.

Razor/native bridge მხოლოდ იმ ფუნქციისთვის მოაშორე, რომლის ეკვივალენტური Angular/API flow, download/print და უარყოფითი გზებიც მიღებულია. მანამდე migration map-ში დარჩენილი routes ზუსტად აღრიცხე. CI-ში ჩართე frontend build და შესაბამისი რეალური browser ნაკადები, ორივე theme-ის smoke კონტროლით.

სრული გადართვისთვის: ერთსა და იმავე ფინანსურ dataset-ზე reconciliation/fingerprint, დახურული თვის და append-only დაცვა, backup/restore-ისა და code/schema rollback-ის რეალისტური გეგმა, config/IdP keys/session persistence, logging/health checks და hosting ნაბიჯები. Code rollback-ისთვის immutable migration-ის ისტორია თვითნებურად უკან არ გადაწერო.

## მიწოდება და კომუნიკაცია

- A და B ეტაპები სხვადასხვა commit/branch/პატარა PR-ებად შეინარჩუნე; სხვის მიმდინარე branch-ს ძალით არ გადააწერო. `main`-ზე force push ან შეუმოწმებელი გაერთიანება არ გააკეთო. საერთო დემოს განახლება ამ დავალებით ავტომატურად მოთხოვნილი არ არის.
- პასუხში პირდაპირ დაადასტურე **INS-DOTNET-MSSQL-004**-ის მიღება და პრიორიტეტი: **FINANCE FIRST → VERIFIED BASELINE → ARCHITECTURE**.
- პირველ ჩაბარებაში აჩვენე A ეტაპის ფაქტობრივი შედეგი და R004-01–04 პასუხები; შემდეგ B0/B1 და პირველი მუშაობადი policies/auth ნაწილი. გადაიყვანე სტატუსი მხოლოდ შესაბამისი მიღების პირობების შესრულებისას.
- განაახლე `CLAUDE-RESPONSE.md`, `STATUS.md`, `MIGRATION-COVERAGE.md`, ADR-ები, DEVELOPMENT/README; მიუთითე SHA, ზუსტი CI ბმულები, PASS/FAIL/SKIP, დარჩენილი Razor routes და OPEN გადაწყვეტილებები. PR #2-ის განხილვაშიც დადე მოკლე შედეგი, რომ Codex-მა და მომხმარებელმა cloud-იდან ნახონ.
- ქმედითი უცნობი მხოლოდ კონკრეტულ ბიზნესწესზე/მიუწვდომელ გარემოზე დასვი. დასრულებული ქვეფუნქცია მთლიან პროექტად ნუ გამოაცხადებ. იმუშავე ეტაპებად, სწრაფად და ხარისხიანად, არსებული მიღებული ფუნქციების რეგრესიის გარეშე.

ტექნიკური ორიენტირები: [Microsoft — OIDC web/BFF](https://learn.microsoft.com/aspnet/core/security/authentication/configure-oidc-web-authentication), [Microsoft — persistence / aggregate repositories / Dapper queries](https://learn.microsoft.com/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/infrastructure-persistence-layer-design). ეს პრინციპებია; არჩევანი ჩვენს რეალურ პროგრამასა და hosting-ზე დაასაბუთე.

