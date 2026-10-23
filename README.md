# სადაზღვევო სისტემა — C#/.NET და MS SQL Server

ეს არის სადაზღვევო პროგრამის ([Insurance-Software](https://github.com/kandelaki5/Insurance-Software), commit `17c1f1f`) გადაწერა C#/.NET 10-სა და Microsoft SQL Server-ზე. ბიზნესწესები, ფინანსური გამოთვლები და ქართული ტექსტები არ შეცვლილა, შეიცვალა მხოლოდ ტექნოლოგია. სამუშაოს მიმდინარეობა ჩანს `STATUS.md`-ში, ხოლო თითოეული შესაძლებლობის ბედი `MIGRATION-COVERAGE.md`-ში.

## რა სჭირდება

- .NET SDK 10 (`global.json`);
- Microsoft SQL Server 2019 ან უფრო ახალი: Developer/Express ლოკალურად, ან სერვერი;
- ტესტებისთვის, სურვილისამებრ, Docker. ასე მარტივად გაუშვებთ SQL Server-ს: `mcr.microsoft.com/mssql/server:2022-latest`.

## სტრუქტურა

| საქაღალდე | რა არის |
|---|---|
| `src/Insurance.Domain` | ფინანსური და შესაბამისობის წესები, საზედამხედველო ფორმები. ბაზას არ იყენებს |
| `src/Insurance.Application` | ბიზნესოპერაციები, ბაზასთან მუშაობა, ქართული ტექსტები, მიგრაციების გამშვები |
| `src/Insurance.Web` | ვებპროგრამა (ASP.NET Core Razor Pages) |
| `src/Insurance.Tools` | ადმინისტრატორის ბრძანებები: ბაზის შექმნა, მიგრაცია, ადმინისტრატორი, მონაცემების გადატანა |
| `database/migrations` | MSSQL სქემა (T-SQL). განმარტება: `database/README.md` |
| `tests/` | ტესტები: unit, რეალური SQL Server, HTTP და ბრაუზერი |
| `docs/` | `DEVELOPMENT.md` (კოდის წესები), `MIGRATION.md` (მონაცემების გადატანა) |

## პირველი გაშვება

კავშირის სტრიქონები და პაროლები მხოლოდ გარემოს ცვლადებში ან user secrets-ში ინახება, არასოდეს ფაილში, რომელიც git-ში ხვდება. ქვემოთ `<…>` თქვენი მნიშვნელობებია.

```bash
# 1. ადმინისტრატორის კავშირები (SQL Server-ის ადმინი)
export INSURANCE_MASTER_CONNECTION="Server=localhost,1433;Database=master;User ID=<ადმინი>;Password=<…>;TrustServerCertificate=True"
export INSURANCE_ADMIN_CONNECTION="Server=localhost,1433;Database=insurance;User ID=<ადმინი>;Password=<…>;TrustServerCertificate=True"

# 2. ახალი ბაზა სწორი პარამეტრებით, შემდეგ სქემა
dotnet run --project src/Insurance.Tools -- create-database insurance
dotnet run --project src/Insurance.Tools -- migrate

# 3. პროგრამის მომხმარებელი ბაზაში (მხოლოდ core_app როლი)
export INSURANCE_APP_PASSWORD="<ახალი ძლიერი პაროლი>"
dotnet run --project src/Insurance.Tools -- create-app-login insurance_app

# 4. პირველი ადმინისტრატორი
export INSURANCE_ADMIN_PASSWORD="<ადმინისტრატორის პაროლი>"
dotnet run --project src/Insurance.Tools -- seed-admin admin "ადმინისტრატორი"

# 5. ვებპროგრამა (პროგრამის მომხმარებლით და არა ადმინით)
export ConnectionStrings__Insurance="Server=localhost,1433;Database=insurance;User ID=insurance_app;Password=<INSURANCE_APP_PASSWORD>;TrustServerCertificate=True"
dotnet run --project src/Insurance.Web
```

მიგრაციის ფაილი, რომელიც ერთხელ უკვე გაეშვა რომელიმე ბაზაზე, აღარ იცვლება. ყოველი ახალი ცვლილება შემდეგი ნომრის ახალ ფაილში იწერება (ძველი პროგრამის წესი). `migrate` გაშვებულ ფაილებს ხელახლა არ ამოწმებს.

ბრაუზერში გახსენით ბრძანების ეკრანზე ნაჩვენები მისამართი (მაგ. `http://localhost:5000`) და შედით ადმინისტრატორით. დანარჩენ მომხმარებლებს „მომხმარებლების“ ეკრანიდან შექმნით.

ინტერნეტში გაშვებისას საჭიროა HTTPS. თუ პროგრამა reverse proxy-ს უკანაა, `Insurance:TrustForwardedFor=true` ჩართეთ მხოლოდ მაშინ, როცა proxy `X-Forwarded-For`-ს თავად წერს.

## სატესტო მომხმარებლები

სატესტო ბაზაზე (მისი სახელი `ins_it_`-ით იწყება ან შეიცავს `test`-ს) შეგიძლიათ შექმნათ თითო მომხმარებელი ყოველ როლზე: `claude.admin`, `claude.operator`, `claude.head`, `claude.accountant`, `claude.viewer`.

```bash
export INSURANCE_ADMIN_CONNECTION="Server=localhost,1433;Database=insurance_test;User ID=<ადმინი>;Password=<…>;TrustServerCertificate=True"
dotnet run --project src/Insurance.Tools -- test-users .env.uat
```

- პაროლები იქმნება შემთხვევით. ისინი მხოლოდ `.env.uat` ფაილში იწერება, ეკრანზე არ იბეჭდება და git-ში არ ხვდება.
- ბაზაში მხოლოდ მათი ჰეში ინახება.
- განმეორებით გაშვება მხოლოდ დაკლებულ მომხმარებლებს ქმნის. თუ ფაილში შენახული პაროლი ბაზაში არსებულს არ ემთხვევა, ბრძანება არაფერს ცვლის და ამას ეკრანზე დაწერს.
- ნამდვილ ბაზაზე ბრძანება უარს ამბობს.
- ეს მომხმარებლები მხოლოდ სატესტო ბაზაზე და ადგილობრივ სერვერზე გამოიყენეთ.

`users-inventory reg-` მხოლოდ კითხულობს: აჩვენებს მომხმარებლებს, რომელთა სახელი ამ პრეფიქსით იწყება, მათ სესიებს და მათზე მიმთითებელ ჩანაწერებს.

## ტესტები

```bash
cp .env.test.example .env.test    # ჩაწერეთ ლოკალური სატესტო SQL Server (git-ში არ ხვდება)
dotnet build
dotnet test
```

- ტესტები თავისთვის ქმნის ცალკე ბაზებს (`ins_it_<დრო>_<შემთხვევითი>`) და მერე შლის. სხვა ბაზას არ ეხება.
- ბრაუზერის ტესტებს Playwright-ის Chromium სჭირდება: `pwsh tests/Insurance.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`.
- მონაცემების გადატანის ტესტებს ძველი პროგრამის ბაზა სჭირდება (`INSURANCE_TEST_PG_WORLD`, იხ. `docs/MIGRATION.md`). მის გარეშე ეს ტესტები გამოტოვებულად (SKIP) ჩანს.

## სარეზერვო ასლი და აღდგენა

```sql
-- სრული ასლი (ადმინისტრატორი, SQL Server-ზე)
BACKUP DATABASE insurance TO DISK = N'/var/opt/mssql/backup/insurance-2026-10-23.bak' WITH CHECKSUM, COMPRESSION, INIT;
RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/insurance-2026-10-23.bak' WITH CHECKSUM;

-- აღდგენა სხვა სახელით (შემოწმებისთვის), მიმდინარე ბაზის შეუცვლელად
RESTORE DATABASE insurance_restore_check FROM DISK = N'/var/opt/mssql/backup/insurance-2026-10-23.bak'
  WITH MOVE N'insurance' TO N'/var/opt/mssql/data/insurance_restore_check.mdf',
       MOVE N'insurance_log' TO N'/var/opt/mssql/data/insurance_restore_check_log.ldf', CHECKSUM;
```

- აღდგენის შემდეგ შეამოწმეთ, რომ ბაზა სწორადაა აწყობილი: `INSURANCE_ADMIN_CONNECTION`-ში მიუთითეთ აღდგენილი ბაზა და გაუშვით `dotnet run --project src/Insurance.Tools -- migrate`. ბრძანება ამოწმებს collation-ს, უფლებებს და აუდიტის ტრიგერებს და ახალს არაფერს ამატებს, თუ ბაზა უკვე ბოლო ვერსიაზეა.
- რეგულარული ასლი: სრული ასლი ყოველდღე, ჟურნალის ასლი ყოველ საათში (`FULL` recovery model). ასლის აღდგენადობა ცალკე ბაზაზე რეგულარულად შეამოწმეთ.

## მნიშვნელოვანი წესები

- **ფინანსური ჟურნალები მხოლოდ ახალი ჩანაწერებით ივსება.** პრემია, გადახდები და განაწილებები არ სწორდება და არ იშლება; შესწორება ახალი ჩანაწერია. ამას ბაზა თავად იცავს: პროგრამის როლს ამის უფლება არ აქვს.
- **დახურულ თვეში ფინანსური ჩანაწერი არ იწერება.** ამას ბაზის ტრიგერი იცავს.
- **ყველა ცვლილება აუდიტში იწერება:** ვინ, როდის, რა იყო და რა გახდა.
- **კომპანიის თავისებურება კონფიგურაციაა და არა კოდი.**
