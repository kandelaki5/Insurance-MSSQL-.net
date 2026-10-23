// Interface texts. Other languages are added as sibling files with the same shape.
// Ported from src/i18n/ka.ts (ka); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class Ka
{
    public const string AppName = "სადაზღვევო სისტემა";

    public static class Nav
    {
        public const string Home = "მთავარი";
        public const string Policies = "პოლისები";
        public const string PendingPolicies = "დასამტკიცებელი პოლისები";
        public const string Parties = "კლიენტები";
        public const string Intermediaries = "აგენტები და ბროკერები";
        public const string Users = "მომხმარებლები";
        public const string Settings = "კომპანიის რეკვიზიტები";
        public const string Reference = "ცნობარები";
        public const string Admin = "ადმინისტრირება";
        public const string Classes = "დაზღვევის სახეობები";
        public const string Accounting = "ბუღალტერია";
        public const string Periods = "საანგარიშო პერიოდები";
        public const string PremiumReport = "თვის პრემიის ანგარიში";
        public const string Rates = "ვალუტის კურსები";
        public const string Logout = "გასვლა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("home", Home),
            ("policies", Policies),
            ("pendingPolicies", PendingPolicies),
            ("parties", Parties),
            ("intermediaries", Intermediaries),
            ("users", Users),
            ("settings", Settings),
            ("reference", Reference),
            ("admin", Admin),
            ("classes", Classes),
            ("accounting", Accounting),
            ("periods", Periods),
            ("premiumReport", PremiumReport),
            ("rates", Rates),
            ("logout", Logout));
    }

    public static class Login
    {
        public const string Title = "სისტემაში შესვლა";
        public const string Username = "მომხმარებელი";
        public const string Password = "პაროლი";
        public const string Submit = "შესვლა";
        public const string Invalid = "მომხმარებელი ან პაროლი არასწორია";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("username", Username),
            ("password", Password),
            ("submit", Submit),
            ("invalid", Invalid));
    }

    public static class Common
    {
        public const string Save = "შენახვა";
        public const string Search = "ძებნა";
        public const string Cancel = "გაუქმება";
        public const string Yes = "დიახ";
        public const string No = "არა";
        public const string NoData = "ჩანაწერები არ არის";
        public const string ReadOnly = "თქვენს როლს ცვლილების უფლება არ აქვს";
        public const string SeriesExhausted = "ნომრების სერია ამოიწურა — დოკუმენტი არ შეიქმნა. მიმართეთ ადმინისტრატორს.";
        public const string DateFormat = "დდ/თთ/წწწწ";
        public const string DateInvalid = "თარიღი შეიყვანეთ ფორმატით დდ/თთ/წწწწ (მაგ. 14/10/2026)";

        public static string DateTooEarly(string date) =>
            string.Concat("თარიღი არ შეიძლება იყოს ", date, "-მდე");

        public static string DateTooLate(string date) =>
            string.Concat("თარიღი არ შეიძლება იყოს ", date, "-ის შემდეგ");

        public const string OpenCalendar = "კალენდარი";
    }

    public static class Party
    {
        public const string Title = "კლიენტები";
        public const string NewPerson = "ახალი ფიზიკური პირი";
        public const string NewOrganization = "ახალი იურიდიული პირი";
        public const string SearchPlaceholder = "სახელი, პირადი ნომერი ან საიდენტიფიკაციო კოდი";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kind = global::Insurance.Application.Texts.TextMap.Of(
            ("person", "ფიზიკური პირი"),
            ("organization", "იურიდიული პირი"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Segment = global::Insurance.Application.Texts.TextMap.Of(
            ("retail", "საცალო"),
            ("corporate", "კორპორატიული"),
            ("state", "სახელმწიფო"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Risk = global::Insurance.Application.Texts.TextMap.Of(
            ("low", "დაბალი"),
            ("medium", "საშუალო"),
            ("high", "მაღალი"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Gender = global::Insurance.Application.Texts.TextMap.Of(
            ("male", "მამრობითი"),
            ("female", "მდედრობითი"));

        public static class Sections
        {
            public const string Identity = "საიდენტიფიკაციო მონაცემები";
            public const string Contact = "მისამართი და კონტაქტი";
            public const string Compliance = "იდენტიფიკაცია და რისკი";
            public const string History = "ცვლილებების ისტორია";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("identity", Identity),
                ("contact", Contact),
                ("compliance", Compliance),
                ("history", History));
        }

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Fields = global::Insurance.Application.Texts.TextMap.Of(
            ("name", "სახელწოდება"),
            ("displayName", "სახელი / სახელწოდება"),
            ("idNumber", "საიდენტიფიკაციო ნომერი"),
            ("kind", "ტიპი"),
            ("segment", "სეგმენტი"),
            ("isResident", "რეზიდენტი"),
            ("taxIdPerson", "პირადი ნომერი (11 ციფრი)"),
            ("taxIdOrganization", "საიდენტიფიკაციო კოდი (9 ციფრი)"),
            ("foreignId", "პასპორტის / უცხოური რეგისტრაციის ნომერი"),
            ("countryCode", "ქვეყანა (კოდი)"),
            ("firstName", "სახელი"),
            ("lastName", "გვარი"),
            ("birthDate", "დაბადების თარიღი"),
            ("gender", "სქესი"),
            ("legalForm", "სამართლებრივი ფორმა"),
            ("stateSharePercent", "სახელმწიფოს წილი, %"),
            ("isPublicEntity", "სსიპ ან სახელმწიფო დაწესებულება"),
            ("registrationPlace", "რეგისტრაციის ადგილი"),
            ("legalAddress", "იურიდიული მისამართი"),
            ("actualAddress", "ფაქტობრივი მისამართი"),
            ("phone", "ტელეფონი"),
            ("phoneCode", "ქვეყნის კოდი"),
            ("email", "ელფოსტა"),
            ("riskLevel", "რისკის დონე"),
            ("identifiedAt", "იდენტიფიკაციის თარიღი"),
            ("verifiedAt", "ვერიფიკაციის თარიღი"),
            ("isPep", "პოლიტიკურად აქტიური პირი"),
            ("sanctionsCheckedAt", "სანქციების სიასთან შემოწმების თარიღი"),
            ("isGroupMember", "ჯგუფის წევრი კომპანია"),
            ("riskAssessedOn", "რისკის შეფასების თარიღი"),
            ("riskReviewOn", "რისკის გადახედვის ვადა"),
            ("selfDeclarationOn", "მფლობელობის სტრუქტურის თვითდეკლარაციის თარიღი"));

        public static class Kyc
        {
            public const string Title = "იდენტიფიკაციის მოთხოვნები (KYC)";
            public const string Note = "არასიცოცხლის დაზღვევაში მოთხოვნების სიღრმე კლიენტის კალენდარული წლის ჯამურ პრემიაზეა დამოკიდებული (ფმს №2, მუხ. 4–7; ბრძანება №55, მუხ. 9): ≤ 1 000 ₾ — შემცირებული ნაკრები; ≤ 3 000 ₾ (ფიზიკური) / ≤ 5 000 ₾ (იურიდიული) — იდენტიფიკაცია სავალდებულოა, ვერიფიკაცია არა; ზემოთ — ვერიფიკაცია სავალდებულოა, ბენეფიციარი მესაკუთრისაც, ურთიერთობის დაწყებიდან 30 დღეში. სიცოცხლის დაზღვევაზე შეღავათი არ მოქმედებს.";
            public const string Tier = "მოთხოვნების დონე";

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Tiers = global::Insurance.Application.Texts.TextMap.Of(
                ("reduced", "შემცირებული ნაკრები"),
                ("identification", "იდენტიფიკაცია"),
                ("verification", "იდენტიფიკაცია და ვერიფიკაცია"));

            public const string AnnualPremium = "წლიური პრემია (არასიცოცხლე), ₾";
            public const string NextThreshold = "შემდეგი ზღვარი, ₾";
            public const string RelationshipFrom = "ურთიერთობის დაწყება";
            public const string OwnerDue = "ბენეფიციარის ვერიფიკაციის ვადა";
            public const string Complete = "მოთხოვნები შესრულებულია";

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Issues = global::Insurance.Application.Texts.TextMap.Of(
                ("not_identified", "კლიენტი არ არის იდენტიფიცირებული"),
                ("client_verification_required", "კლიენტის ვერიფიკაცია სავალდებულოა"),
                ("no_beneficial_owner", "ბენეფიციარი მესაკუთრე არ არის დადგენილი"),
                ("no_self_declaration", "მფლობელობის სტრუქტურის თვითდეკლარაცია არ არის"),
                ("owner_not_identified", "ბენეფიციარი მესაკუთრე არ არის იდენტიფიცირებული"),
                ("owner_verification_required", "ბენეფიციარი მესაკუთრის ვერიფიკაცია სავალდებულოა"),
                ("owner_verification_overdue", "ბენეფიციარი მესაკუთრის ვერიფიკაციის 30-დღიანი ვადა გასულია"));
        }

        public static class Owners
        {
            public const string Title = "ბენეფიციარი მესაკუთრეები";
            public const string Note = "ფიზიკური პირი, რომელიც პირდაპირ ან არაპირდაპირ ფლობს 25%-ს ან მეტს, სხვაგვარად აკონტროლებს, ან — როცა ასეთი არ არსებობს — ხელმძღვანელი პირია (ბრძანება №55, მუხ. 2). ამოღებული ჩანაწერი ინახება ურთიერთობის შეწყვეტიდან 5 წელი.";
            public const string Add = "ბენეფიციარი მესაკუთრის დამატება";
            public const string Save = "დამატება";
            public const string Saved = "ბენეფიციარი მესაკუთრე დაემატა";
            public const string None = "ბენეფიციარი მესაკუთრე არ არის ჩაწერილი";
            public const string PersonalNumber = "პირადი ნომერი";
            public const string Citizenship = "მოქალაქეობა (ქვეყნის კოდი)";
            public const string BirthPlace = "დაბადების ადგილი";
            public const string DocumentNumber = "დოკუმენტის ნომერი";
            public const string DocumentIssuedOn = "დოკუმენტის გაცემის თარიღი";
            public const string DocumentCountry = "გამცემი ქვეყანა (კოდი)";
            public const string DocumentIssuer = "გამცემი ორგანო";
            public const string DocumentValidTo = "დოკუმენტის მოქმედების ვადა";
            public const string Criterion = "დადგენის კრიტერიუმი";

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Criteria = global::Insurance.Application.Texts.TextMap.Of(
                ("share", "წილი 25% ან მეტი"),
                ("control", "კონტროლი სხვა გზით"),
                ("executive", "ხელმძღვანელი / წარმომადგენელი"));

            public const string SharePercent = "წილი, %";
            public const string IsIndirect = "არაპირდაპირი მფლობელობა";
            public const string IsNominee = "ნომინალური მფლობელი";
            public const string VerificationSource = "ვერიფიკაციის წყარო";
            public const string NoteField = "შენიშვნა";
            public const string Verify = "ვერიფიკაცია დღეს";
            public const string Remove = "ამოღება";
            public const string ShareError = "წილი უნდა იყოს 0-დან 100-მდე";
            public const string ShareBelowThreshold = "წილის კრიტერიუმისთვის მიუთითეთ 25% ან მეტი; ნაკლებზე აირჩიეთ სხვა კრიტერიუმი";
            public const string Verified = "ვერიფიცირებული";
            public const string Identified = "იდენტიფიცირებული";
            public const string DocumentExpired = "დოკუმენტის ვადა გასულია";
        }

        public static class RiskCard
        {
            public const string Title = "კლიენტის რისკის შეფასება";
            public const string Note = "რისკის დონე სავალდებულოა ყველა კლიენტზე პოლისის გაფორმებამდე (AML კანონი, მუხ. 8; ბრძანება №02, მუხ. 3 „კ“). სისტემა დონეს ფაქტორებით ითვლის; პასუხისმგებელ პირს შეუძლია სხვა დონე მიანიჭოს დასაბუთებით. ყოველი შეფასება ისტორიაში რჩება.";
            public const string Current = "მიმდინარე დონე";
            public const string NotAssessed = "არ არის შეფასებული";
            public const string Computed = "გამოთვლილი დონე";
            public const string Score = "ქულა";
            public const string Factors = "ფაქტორები";
            public const string NoFactors = "მომატებული რისკის ფაქტორი არ არის";
            public const string AnnualPremium = "წლიური პრემია (ლარი)";
            public const string PreviousYearPremium = "წინა წლის პრემია (ლარი)";
            public const string Accept = "გამოთვლილი დონის დადასტურება";
            public const string OverrideLevel = "სხვა დონის მინიჭება";
            public const string OverrideReason = "დასაბუთება";
            public const string Override = "დონის მინიჭება დასაბუთებით";
            public const string ReviewDue = "გადახედვის ვადა";
            public const string Overdue = "გადახედვის ვადა გასულია";
            public const string DueSoon = "გადახედვა ახლოვდება";
            public const string History = "შეფასებების ისტორია";
            public const string Manual = "ხელით";
            public const string ByRules = "წესებით";
            public const string Saved = "რისკის შეფასება შენახულია";
            public const string ReasonRequired = "სხვა დონის მინიჭებას სჭირდება დასაბუთება";
            public const string SameAsComputed = "არჩეული დონე გამოთვლილს ემთხვევა — გამოიყენეთ დადასტურება";

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> FactorNames = global::Insurance.Application.Texts.TextMap.Of(
                ("pep", "პოლიტიკურად აქტიური პირი — მომეტებული რისკი კანონით (მუხ. 21)"),
                ("high_risk_country", "მაღალი რისკის იურისდიქცია (მუხ. 19)"),
                ("non_resident", "არარეზიდენტი"),
                ("not_verified", "ვერიფიკაცია არ არის დასრულებული"),
                ("sanctions_not_checked", "სანქციების სიასთან შემოწმება არ არის"),
                ("sanctions_check_stale", "სანქციების შემოწმება მოძველებულია"),
                ("premium_high", "წლიური პრემია პირველ ზღვარზე მეტია"),
                ("premium_very_high", "წლიური პრემია მეორე ზღვარზე მეტია"),
                ("premium_jump", "წლიური პრემია წინა წელთან შედარებით სამჯერ ან მეტად გაიზარდა"),
                ("state_entity", "სახელმწიფო ერთეული — შემცირებული რისკი"));
        }

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Filters = global::Insurance.Application.Texts.TextMap.Of(
            ("all", "ყველა"),
            ("unrated", "შეუფასებელი რისკი"),
            ("reviewDue", "გადახედვის ვადა გასული / ახლოვდება"));

        public static class Errors
        {
            public const string FirstName = "სახელი სავალდებულოა";
            public const string LastName = "გვარი სავალდებულოა";
            public const string Name = "სახელწოდება სავალდებულოა";
            public const string TaxIdPerson = "პირადი ნომერი უნდა შედგებოდეს 11 ციფრისგან";
            public const string TaxIdOrganization = "საიდენტიფიკაციო კოდი უნდა შედგებოდეს 9 ციფრისგან";
            public const string ForeignId = "არარეზიდენტისთვის მიუთითეთ პასპორტის ან რეგისტრაციის ნომერი";
            public const string Duplicate = "ამ ნომრით კლიენტი უკვე არსებობს";
            public const string StateShare = "სახელმწიფოს წილი უნდა იყოს 0-დან 100-მდე";
            public const string Date = "თარიღი არასწორია — წელი უნდა იყოს ოთხნიშნა (1900–2100)";
            public const string Phone = "ნომერი არასწორია: საქართველოს (+995) ნომერს 9 ციფრი აქვს; სხვა ქვეყნისთვის შეცვალეთ კოდი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("firstName", FirstName),
                ("lastName", LastName),
                ("name", Name),
                ("taxIdPerson", TaxIdPerson),
                ("taxIdOrganization", TaxIdOrganization),
                ("foreignId", ForeignId),
                ("duplicate", Duplicate),
                ("stateShare", StateShare),
                ("date", Date),
                ("phone", Phone));
        }

        public const string Saved = "შენახულია";
    }

    public static class Intermediary
    {
        public const string Title = "აგენტები და ბროკერები";
        public const string New = "ახალი შუამავალი";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kind = global::Insurance.Application.Texts.TextMap.Of(
            ("agent", "აგენტი"),
            ("broker", "ბროკერი"),
            ("bank", "ბანკი"),
            ("employee", "თანამშრომელი"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Status = global::Insurance.Application.Texts.TextMap.Of(
            ("active", "ძალაშია"),
            ("terminated", "შეწყვეტილია"));

        public static class Fields
        {
            public const string Party = "პირი";
            public const string PartyIdNumber = "პირადი ნომერი ან საიდენტიფიკაციო კოდი";
            public const string Kind = "ტიპი";
            public const string ContractNo = "ხელშეკრულების ნომერი";
            public const string ContractDate = "ხელშეკრულების თარიღი";
            public const string ValidTo = "მოქმედების ვადა";
            public const string Status = "სტატუსი";
            public const string CommissionPercent = "საკომისიო, %";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("party", Party),
                ("partyIdNumber", PartyIdNumber),
                ("kind", Kind),
                ("contractNo", ContractNo),
                ("contractDate", ContractDate),
                ("validTo", ValidTo),
                ("status", Status),
                ("commissionPercent", CommissionPercent));
        }

        public static class Errors
        {
            public const string PartyNotFound = "ამ ნომრით პირი კლიენტების რეესტრში ვერ მოიძებნა. ჯერ დაამატეთ იგი კლიენტებში.";
            public const string Kind = "აირჩიეთ ტიპი";
            public const string Commission = "საკომისიო უნდა იყოს 0-დან 100-მდე";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("partyNotFound", PartyNotFound),
                ("kind", Kind),
                ("commission", Commission));
        }

        public const string PartyHint = "შუამავალი ჯერ კლიენტების რეესტრში უნდა იყოს დამატებული.";
    }

    public static class User
    {
        public const string Title = "მომხმარებლები";
        public const string New = "ახალი მომხმარებელი";

        public static class Fields
        {
            public const string Username = "მომხმარებლის სახელი";
            public const string FullName = "სახელი და გვარი";
            public const string Role = "როლი";
            public const string Branch = "ფილიალი";
            public const string Password = "პაროლი";
            public const string NewPassword = "ახალი პაროლი";
            public const string Status = "სტატუსი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("username", Username),
                ("fullName", FullName),
                ("role", Role),
                ("branch", Branch),
                ("password", Password),
                ("newPassword", NewPassword),
                ("status", Status));
        }

        public const string Active = "აქტიური";
        public const string Inactive = "გათიშული";
        public const string Deactivate = "გათიშვა";
        public const string Activate = "გააქტიურება";
        public const string ResetPassword = "პაროლის შეცვლა";
        public const string Created = "მომხმარებელი დაემატა";

        public static class Errors
        {
            public const string Username = "მომხმარებლის სახელი: მხოლოდ ლათინური პატარა ასოები, ციფრები, წერტილი და ტირე (მინიმუმ 3 სიმბოლო)";
            public const string FullName = "სახელი და გვარი სავალდებულოა";
            public const string Password = "პაროლი უნდა შედგებოდეს მინიმუმ 10 სიმბოლოსგან";
            public const string Duplicate = "ასეთი მომხმარებელი უკვე არსებობს";
            public const string Self = "საკუთარი ანგარიშის გათიშვა შეუძლებელია";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("username", Username),
                ("fullName", FullName),
                ("password", Password),
                ("duplicate", Duplicate),
                ("self", Self));
        }
    }

    public static class Settings
    {
        public const string Note = "რეკვიზიტები იბეჭდება პოლისზე და სხვა დოკუმენტებზე, მათ შორის „ხელშეკრულების მნიშვნელოვანი პირობების“ თავსართში (ბრძანება №18). ცარიელი ველი არ ინახება (გარდა იურისდიქციების სიისა). რისკის პარამეტრები კლიენტის რისკის ავტომატურ შეფასებაზე მოქმედებს.";
        public const string Company = "კომპანია და ზედამხედველი";
        public const string NotSaved = "პარამეტრები არ შენახულა";
        public const string Saved = "პარამეტრები შენახულია";
        public const string Aml = "კლიენტის რისკის შეფასების პარამეტრები (AML)";
        public const string Accounting = "ბუღალტერია";
        public const string Policy = "პოლისები";
        public const string InvalidDays = "უნდა იყოს მთელი რიცხვი 1-დან 31-მდე";
        public const string InvalidChoice = "დაუშვებელი მნიშვნელობა";
        public const string InvalidAmount = "უნდა იყოს თანხა ლარში, მაგ. 10000.00";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<string, string>> Choices = global::Insurance.Application.Texts.TextMap.Of(
            ("policy.reduction_order", global::Insurance.Application.Texts.TextMap.Of(
                ("last_first", "ბოლო შენატანები მცირდება (ბოლოდან წინ)"),
                ("equal_unpaid", "თანაბრად ნაწილდება გადაუხდელ შენატანებზე"))),
            // H46 (owner 06.10.2026, old program docs/07 §4.5); wording proposed by Claude, not approved by the owner.
            ("claim.currency", global::Insurance.Application.Texts.TextMap.Of(
                ("gel", "ლარში (ლიმიტი და ფრანშიზა — შემთხვევის დღის კურსით)"),
                ("policy", "პოლისის ვალუტაში"))));

        public static class Numbering
        {
            public const string Title = "დოკუმენტების ნუმერაცია";
            public const string Note = "პრეფიქსი და ციფრების რაოდენობა ახალ ნომრებზე მოქმედებს; უკვე გაცემული ნომრები არ იცვლება. რიგითი (მრიცხველი) აქ არ იცვლება. ციფრები რომ ამოიწურება, ახალი დოკუმენტი არ შეიქმნება.";
            public const string Series = "სერია";
            public const string Prefix = "პრეფიქსი";
            public const string Digits = "ციფრები";
            public const string Next = "შემდეგი ნომერი";
            public const string Save = "ნუმერაციის შენახვა";
            public const string Saved = "ნუმერაცია შენახულია";

            public static string Policy(string product) =>
                string.Concat("პოლისი — ", product);

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Labels = global::Insurance.Application.Texts.TextMap.Of(
                ("contract", "ხელშეკრულება"),
                ("claim", "ზარალი"),
                ("complaint", "პრეტენზია"),
                ("aml_case", "AML საქმე"),
                ("treaty", "გადაზღვევის ხელშეკრულება"));

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Problems = global::Insurance.Application.Texts.TextMap.Of(
                ("prefix", "პრეფიქსი: მხოლოდ ლათინური დიდი ასოები, ციფრები და ტირე, მაქსიმუმ 10 სიმბოლო"),
                ("digits", "ციფრების რაოდენობა უნდა იყოს 1-დან 12-მდე"),
                ("fit", "შემდეგი ნომერი ამ ციფრების რაოდენობაში არ ეტევა"),
                ("taken", "ამ ფორმატით სერია მომავალში უკვე გაცემულ ნომერს მისცემდა — ასეთი ნომრით დოკუმენტი არსებობს"),
                ("overlap", "ამ ფორმატით ორი სერია ერთსა და იმავე ნომერს მისცემდა"),
                ("kind", "ეს პრეფიქსი ან ნომრის ფორმა უკვე აქვს სხვა სახის დოკუმენტებს"));

            public const string KindHint = "სხვადასხვა სახის დოკუმენტებს განსხვავებული პრეფიქსი სჭირდება";
        }

        public static class ProductTexts
        {
            public const string Nav = "პროდუქტის ტექსტები";
            public const string Title = "პოლისზე დასაბეჭდი ტექსტები პროდუქტების მიხედვით";
            public const string Note = "ეს ტექსტები იბეჭდება პოლისის „ხელშეკრულების მნიშვნელოვანი პირობების“ თავსართში (ბრძანება №18, მუხ. 3–5): გამონაკლისების სრული სია, ზარალის წესი და ვადები, გასვლა/შეწყვეტა, ინფორმაციის ვალდებულება. ცარიელი ველი არ ინახება. ტექსტის ცვლილება მომავალ ბეჭდვებზე მოქმედებს; პირობების ვერსია ტექსტშივე მიუთითეთ.";
            public const string Saved = "ტექსტები შენახულია";
            public const string Inactive = "არააქტიური";
            public const string PlaceholderMarker = "შესავსებია";

            public static string Incomplete(int n) =>
                string.Concat("შესავსებია: ", n.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " ველი");
        }
    }

    public static class PremiumReport
    {
        public const string Title = "თვის პრემიის ანგარიში";
        public const string Note = "ლარში, დაზღვევის სახეობების მიხედვით, პრემიის ჟურნალიდან. ყოველი ჩანაწერი თავისი ფიქსირებული კურსითაა; სტატისტიკური პრემია პერიოდში ძალაში შესული პოლისების პრემიაა გაუქმებების გარეშე, ფინანსური კი ყველა ჩანაწერია, რომელიც ამ თვეში აღირიცხა, გაუქმებების ჩათვლით. დავალიანება თვის ბოლო დღის კურსითაა.";
        public const string Month = "თვე";
        public const string Show = "ჩვენება";
        public const string ClassCode = "კოდი";
        public const string ClassName = "სახეობა";
        public const string PoliciesIssued = "ძალაში შესული პოლისები";
        public const string StatisticalWritten = "სტატისტიკური მოზიდული";
        public const string FinancialWritten = "ფინანსური მოზიდული";
        public const string Earned = "გამომუშავებული";
        public const string Unearned = "UPR თვის ბოლოს";
        public const string Total = "სულ";
        public const string ClaimsTitle = "ზარალები";
        public const string ClaimsNote = "ანაზღაურებული: თვეში გაცემული ზარალები ბრუტო (ფრანშიზისა და ჩათვლის ჩათვლით), თითო გადახდის კურსით. RBNS: განცხადებული, დაურეგულირებელი ზარალების რეზერვი თვის ბოლოს, თვის ბოლო დღის კურსით. IBNR: წლის დასაწყისიდან ფინანსური მოზიდული ნეტო პრემიის (ბრუტო მინუს გადაცემული) 5% (ჯანმრთელობა 2%). რეგრესი და გადარჩენილი ქონება: თვეში მიღებული თანხები. გადამზღვევლის წილი ანაზღაურებულში და RBNS-ში — ზარალზე მიბმული ხელშეკრულებებით, გადახდისთანავე, გადამზღვევლის ანგარიშსწორების მიუხედავად.";
        public const string PaidClaims = "ანაზღაურებული";
        public const string Rbns = "RBNS თვის ბოლოს";
        public const string Ibnr = "IBNR";
        public const string Recovered = "რეგრესი და ნარჩენები";
        public const string ReceivableTitle = "პრემიის დავალიანება თვის ბოლოს";
        public const string Currency = "ვალუტა";
        public const string Outstanding = "დავალიანება";
        public const string Overdue = "მათ შორის ვადაგადაცილებული";
        public const string Rate = "კურსი";
        public const string OutstandingGel = "ლარში";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("note", Note),
            ("month", Month),
            ("show", Show),
            ("classCode", ClassCode),
            ("className", ClassName),
            ("policiesIssued", PoliciesIssued),
            ("statisticalWritten", StatisticalWritten),
            ("financialWritten", FinancialWritten),
            ("earned", Earned),
            ("unearned", Unearned),
            ("total", Total),
            ("claimsTitle", ClaimsTitle),
            ("claimsNote", ClaimsNote),
            ("paidClaims", PaidClaims),
            ("rbns", Rbns),
            ("ibnr", Ibnr),
            ("recovered", Recovered),
            ("receivableTitle", ReceivableTitle),
            ("currency", Currency),
            ("outstanding", Outstanding),
            ("overdue", Overdue),
            ("rate", Rate),
            ("outstandingGel", OutstandingGel));
    }

    public static class Periods
    {
        public const string Title = "საანგარიშო პერიოდები";
        public const string Note = "დახურული თვე აღარ იცვლება: ყველა შემდგომი შესწორება მიმდინარე ღია თვეში აისახება, რადგან ზედამხედველობის ფორმები წინა თვეზე ხელახლა არ იგზავნება.";

        public static string Deadline(int days) =>
            string.Concat("დახურვის ვადა: თვის დასრულებიდან ", days.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " კალენდარული დღე (კომპანიის პარამეტრი).");

        public const string Month = "თვე";
        public const string Status = "სტატუსი";
        public const string ClosedAt = "დაიხურა";
        public const string ClosedBy = "დახურა";
        public const string Open = "ღია";
        public const string Closed = "დახურული";
        public const string Close = "თვის დახურვა";
        public const string CloseConfirm = "დახურვის შემდეგ ამ თვეში ფინანსური ჩანაწერი აღარ გაკეთდება. დარწმუნებული ხართ?";
        public const string DueSoon = "დასახურია";
        public const string OverdueClose = "დახურვის ვადა გადაცილებულია";
        public const string NotEnded = "თვე ჯერ არ დასრულებულა";
        public const string PreviousOpen = "ჯერ წინა თვე დაიხურეთ";

        /// <summary>
        /// The answer to every press of "close month" (Codex H25).
        /// </summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> CloseOutcomes = global::Insurance.Application.Texts.TextMap.Of(
            ("done", "თვე დაიხურა"),
            ("already", "ეს თვე უკვე დახურულია — არაფერი შეცვლილა"),
            ("forbidden", "თვის დახურვის უფლება არ გაქვთ — არაფერი შეცვლილა"),
            ("invalid", "თვე არასწორად არის მითითებული — არაფერი შეცვლილა"),
            ("not-ended", "თვე ჯერ არ დასრულებულა — არაფერი შეცვლილა"),
            ("earlier-open", "ჯერ წინა ღია თვე უნდა დაიხუროს — არაფერი შეცვლილა"));
    }

    public static class Audit
    {
        public const string At = "დრო";
        public const string User = "მომხმარებელი";
        public const string Action = "მოქმედება";
        public const string Changes = "ცვლილება";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Actions = global::Insurance.Application.Texts.TextMap.Of(
            ("insert", "შექმნა"),
            ("update", "ცვლილება"),
            ("delete", "წაშლა"));

        public const string System = "სისტემა";
    }

    public static class Classes
    {
        public const string Title = "დაზღვევის სახეობები";
        public const string Note = "ზედამხედველობის სამსახურის ანგარიშგების კლასიფიკატორი. მომხდარი, მაგრამ განუცხადებელი ზარალების რეზერვის განაკვეთი: რეზერვების წესი, მუხლი 7.";
        public const string Code = "კოდი";
        public const string Name = "დასახელება";
        public const string Category = "კატეგორია";
        public const string Ibnr = "IBNR, %";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Categories = global::Insurance.Application.Texts.TextMap.Of(
            ("life", "სიცოცხლე"),
            ("non_life", "არასიცოცხლე"));
    }

    public static class Rates
    {
        public const string Title = "ვალუტის კურსები";
        public const string Note = "საქართველოს ეროვნული ბანკის ოფიციალური კურსი: ლარი ერთ ერთეულზე.";
        public const string Date = "თარიღი";
        public const string Load = "ეროვნული ბანკიდან ჩამოტვირთვა";
        public const string Loaded = "კურსები ჩაიტვირთა";
        public const string Failed = "ეროვნული ბანკიდან კურსის მიღება ვერ მოხერხდა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("note", Note),
            ("date", Date),
            ("load", Load),
            ("loaded", Loaded),
            ("failed", Failed));
    }

    public static class Home
    {
        public const string Welcome = "მოგესალმებით";
        public const string Parties = "კლიენტი რეესტრში";
        public const string Classes = "დაზღვევის სახეობა";
        public const string LastRates = "ბოლო კურსები";
        public const string Reminders = "შეხსენებები";
        public const string UnratedParties = "კლიენტი რისკის დონის გარეშე";
        public const string RiskReviewsDue = "კლიენტი რისკის გადასახედად (ვადა გასული ან 30 დღეში)";
        public const string KycIssues = "კლიენტი დაუსრულებელი იდენტიფიკაციით / ვერიფიკაციით";
        public const string OpenComplaints = "დაუსრულებელი პრეტენზია, მათ შორის ვადაგადაცილებული";
        public const string PendingPolicies = "დასამტკიცებელი პოლისი";
        public const string NothingDue = "შესახსენებელი არაფერია";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("welcome", Welcome),
            ("parties", Parties),
            ("classes", Classes),
            ("lastRates", LastRates),
            ("reminders", Reminders),
            ("unratedParties", UnratedParties),
            ("riskReviewsDue", RiskReviewsDue),
            ("kycIssues", KycIssues),
            ("openComplaints", OpenComplaints),
            ("pendingPolicies", PendingPolicies),
            ("nothingDue", NothingDue));
    }
}
