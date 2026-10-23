# არქიტექტურის მიგრაცია — INS-DOTNET-MSSQL-004, B ეტაპი

ბაზა: ფინანსური baseline `42b0d749b737aaf983a88e2fc2da2843b24df21b` (ბრანჩი `claude/sleepy-curie-81jkry`, PR #2). ეს სამუშაო ცალკე ბრანჩზეა: `claude/architecture-api`. მიზანი: .NET 10 Web API + მიზნობრივი repository/query ინტერფეისები + ფენებად დაყოფილი backend + **არსებული** Angular frontend (Codex-ის დიზაინი და თემები უცვლელია), OpenAPI და OIDC. ბიზნესწესები, ციფრები, ქართული ტექსტები და MSSQL-ის გარანტიები არ იცვლება.

- `route-map.md` — B0 ინვენტარი: ყოველი Razor route/handler/download → use case → SQL/ტრანზაქცია/lock/უფლება → ტესტები; Angular-ის მარშრუტები; ჯვარედინი ნაწილები; SQL Web-ში (პირველი გამოსატანი კანდიდატები).
- `adr/0001-layers.md` — ფენები და დამოკიდებულებები.
- `adr/0002-persistence.md` — Dapper, აგრეგატის repository, მიზნობრივი query, ერთი use case = ერთი ტრანზაქცია.
- `adr/0003-authentication.md` — OIDC შესვლა same-origin BFF-ით, OpenIddict, API-ს OAuth 2.0.
- `adr/0004-api-contracts.md` — `/api/v1`, ზუსტი ფული, თარიღები, ProblemDetails.
- `adr/0005-hosting.md` — იგივე ჰოსტინგი, საიდუმლოებები Git-ის გარეთ, უკან დაბრუნების გზა.

## ნაბიჯების რიგი (თითოეული ცალკე commit, აწყობადი, ტესტებით; უკან დაბრუნება — commit-ის revert)

1. **B1 ჩონჩხი:** `Insurance.Infrastructure` და `Insurance.Api` პროექტები, DI, `/api/v1/health`, OpenAPI დოკუმენტი, ProblemDetails; არქიტექტურის ტესტი (აკრძალული reference ჩავარდება). ბიზნესქცევა არ იცვლება.
2. **B2 პირველი query:** `IPolicyListQuery` / `IPolicyCardQuery` Application-ში, Dapper-ის იმპლემენტაცია Infrastructure-ში; Razor-ის პოლისების სია/ბარათი და ახალი API ერთსა და იმავეს იყენებს (ციფრები იგივე — ტესტებით).
3. **B3 შესვლა:** OpenIddict (Development/test-ში ნამდვილი login), BFF login/callback/logout, სესიის გაუქმება, Swagger Authorize (PKCE). არსებული scrypt-ჰეშები მუშაობს, დაზიანებული — უარყოფილია.
4. **B4/B5 პირველი ვერტიკალი:** `/api/v1/policies` (სერვერული გვერდები, ძიება) და `/api/v1/policies/{id}`; Angular-ის პოლისების სია/დეტალი ახალ API-ზე, იმავე დიზაინით, ორივე თემით; ბრაუზერის ტესტი browser → BFF → API → query → MSSQL.
5. შემდეგ მოდულები მცირე ნაწილებად (`route-map.md` §4-ის რიგით): Parties/reference → Policies writes → Payments → Claims → Reinsurance → Finance/Regulatory → Compliance → Users/permissions/audit/settings. Razor route იშლება მხოლოდ მაშინ, როცა მისი API + Angular ეკრანი (download/print და უარყოფითი გზები) მიღებულია.

რა **არ** კეთდება: microservices, message broker, CQRS framework, generic `IRepository<T>`, EF Core აპლიკაციის მონაცემებზე, ძველი UI-ის წაშლა ჩანაცვლების მიღებამდე, საერთო ბაზების ან დემოს შეცვლა.
