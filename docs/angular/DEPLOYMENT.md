# დემოზე განთავსება — 2026-10-23

მისამართი: https://agulisoft-001-site1.ktempurl.com/login

ახალი ღია დიზაინი .NET-თან და არსებულ ცალკე სატესტო MSSQL ბაზასთან ერთად განთავსებულია. კოდი: `bacf01dd8335ad7ce0170ddee64c7c3320a05d7c`, ტოტი `codex/angular-workspace`.

მომხმარებელმა პირდაპირ დაადასტურა რამდენიმე წუთით დროებითი გათიშვა. ატვირთვა შესრულდა SmarterASP.NET File Manager-ით `/site1`-ში. `DECOMPRESS` სამუშაო დასრულდა `Success` სტატუსით; `app_offline.htm` გადაერქვა `angular-maintenance-disabled-20261023.htm`, საიტი კვლავ მუშაობს.

ზუსტად 7 განახლებული ფაილი: Insurance.Web.dll/PDB, Angular main/styles/license და საერთო/შესვლის CSS. `web.config`, appsettings, ბიზნესბირთვის DLL-ები და ბაზის სქემა/მონაცემები არ შეცვლილა. განთავსების manifest: `deployment-manifest.json`.

წინა `Insurance.Web.dll` სერვერზე დაარქივებულია `insurance-before-angular-20261023.zip`-ში (COMPRESS — Success). დასაბრუნებლად ძველი DLL გამოიყენეთ; `web.config` შეინარჩუნეთ. ლოკალურად წინა publish-იდან დაბრუნების პაკეტიც მომზადდა.

განთავსების შემდეგ ავტორიზებული HTTP შემოწმება: 29 ძირითადი გვერდი, JSON API-ები, ძიება, session enforcement, CSRF rejection და assets — PASS. საწყისი მონაცემები: 14 პოლისი / 12 მოქმედი / 51,900 GEL პრემია; CAS000013 — 6,000 GEL პრემია, 0 ნაშთი, 2,400 GEL გადაცემული პრემია. 4 საჯარო Angular/CSS ფაილის SHA-256 ზუსტად დაემთხვა შემოწმებულ publish-ს.

`verification-live.json` და `live-asset-checks.json` შეიცავს მტკიცებულებებს. სრული live browser acceptance არ ჩატარებულა: Angular და native .NET ხედები ბრაუზერში შემოწმდა ადგილობრივად იმავე სატესტო ბაზით, live შემოწმება HTTP-ით. Domain suite-ის ცნობილი 553 PASS / 1 baseline FAIL საკითხი აღწერილია `TEST-RESULTS.md`-ში.

კლოდს ატვირთვის განმეორება აღარ სჭირდება. შემდგომი ცვლილებები გააკეთეთ ამ ტოტის ზემოდან; ახალი რევიუ/გაერთიანება ცალკე ნაბიჯია.
