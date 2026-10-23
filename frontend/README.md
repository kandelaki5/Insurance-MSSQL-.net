# აგული — Angular ინტერფეისი .NET/MSSQL პროგრამისთვის

მთავარი გვერდი და პოლისების სია დაკავშირებულია ავტორიზებულ .NET JSON handler-ებთან. სხვა მოდულების არსებული Razor ფორმები მუშაობს საერთო Angular ნავიგაციასა და იმავე ღია დიზაინში. ეს ეტაპობრივი ინტეგრაციაა; დანარჩენი ფორმები სრულად Angular-ზე ჯერ არ გადაწერილა.

## აწყობა

Node.js 24.21+ და .NET SDK 10 საჭიროა.

```
npm ci
npm run build
```

ბრძანებები შეასრულე `frontend/`-ში. ფაილები შეიქმნება `../src/Insurance.Web/wwwroot/ui/`-ში. შემდეგ ძირიდან:

```
dotnet run --project src/Insurance.Web
```

`ConnectionStrings__Insurance` მიუთითე გარემოში. შედი პროგრამის არსებული ანგარიშით. ერთადერთი ადგილობრივი პორტისთვის გამოიყენე `dotnet run --project src/Insurance.Web --urls http://127.0.0.1:5200`. დამოუკიდებელი `ng serve` რეალური სერვერის cookie-სა და Razor გვერდებს ვერ ჩაანაცვლებს; ინტეგრირებული დეველოპმენტი .NET მისამართზე შეამოწმე.

მთავარი მონაცემები: `/?handler=Data`; პოლისები: `/policies?handler=Data`; ბარათი: `/policies/{guid}?handler=Data`.

ტესტები, არქიტექტურა და შეზღუდვები: `CLAUDE-ANGULAR-HANDOFF.md` და `docs/angular/`.
