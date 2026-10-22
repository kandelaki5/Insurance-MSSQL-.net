// Interface texts of the reinsurance module.
// Ported from src/i18n/reinsurance.ts (reinsuranceTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class ReinsuranceTexts
{
    public const string Nav = "გადაზღვევა";
    public const string NavGroup = "გადაზღვევა";
    public const string Title = "გადაზღვევის ხელშეკრულებები";
    public const string New = "ახალი ხელშეკრულება";
    public const string Create = "შენახვა";
    public const string Saved = "ხელშეკრულება შენახულია";
    public const string Closed = "ხელშეკრულება დახურულია";
    public const string Note = "კვოტური და ექსცედენტური ხელშეკრულება პოლისის დამტკიცებისას ავტომატურად ვრცელდება მითითებული სახეობების პოლისებზე, რომელთა ძალაში შესვლა ხელშეკრულების პერიოდშია; გადამზღვევლის წილი პრემიაში და UPR-ში ითვლება პოლისის ფიქსირებული კურსით (ბრძანება №01, მუხ. 5). ფაკულტატური განთავსება ცალკეულ პოლისზე მიეთითება პოლისის ბარათიდან. ზარალის ექსცედენტი (XL) პრემიაზე არ ვრცელდება — მისი პრემია ხელშეკრულების პერიოდზე პროპორციულად ნაწილდება (№02, დან. 2, მუხ. 4.2), ზარალში კი პრიორიტეტის ზემოთ ლიმიტამდე წილს იღებს.";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kinds = global::Insurance.Application.Texts.TextMap.Of(
        ("quota_share", "კვოტური"),
        ("surplus", "ექსცედენტური"),
        ("excess_of_loss", "ზარალის ექსცედენტი (XL)"),
        ("facultative", "ფაკულტატური"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Statuses = global::Insurance.Application.Texts.TextMap.Of(
        ("active", "მოქმედი"),
        ("closed", "დახურული"));

    public static class Fields
    {
        public const string Number = "ნომერი";
        public const string Name = "დასახელება";
        public const string Reinsurer = "გადამზღვეველი";
        public const string ReinsurerIdNumber = "გადამზღვევლის საიდენტიფიკაციო კოდი (კლიენტების რეესტრიდან)";
        public const string Broker = "ბროკერი";
        public const string BrokerIdNumber = "ბროკერის საიდენტიფიკაციო კოდი (არასავალდებულო)";
        public const string Kind = "ტიპი";
        public const string Period = "პერიოდი";
        public const string PeriodFrom = "დასაწყისი";
        public const string PeriodTo = "დასასრული (ჩათვლით)";
        public const string Currency = "ვალუტა";
        public const string CededPercent = "გადაცემული წილი, %";
        public const string Retention = "შეკავება ერთ რისკზე";
        public const string Lines = "ხაზების რაოდენობა";
        public const string Priority = "პრიორიტეტი ერთ ზარალზე";
        public const string TreatyLimit = "ლიმიტი პრიორიტეტის ზემოთ";
        public const string TreatyPremium = "ხელშეკრულების პრემია (XL / დეპოზიტური)";
        public const string CommissionPercent = "გადაზღვევის საკომისიო, %";
        public const string BrokerPercent = "ბროკერის საკომისიო, %";
        public const string ReinsurerRating = "გადამზღვევლის რეიტინგი";
        public const string RatingAgency = "სარეიტინგო სააგენტო";
        public const string Classes = "სახეობები";
        public const string Status = "სტატუსი";
        public const string Note = "შენიშვნა";
        public const string Terms = "პირობები";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("number", Number),
            ("name", Name),
            ("reinsurer", Reinsurer),
            ("reinsurerIdNumber", ReinsurerIdNumber),
            ("broker", Broker),
            ("brokerIdNumber", BrokerIdNumber),
            ("kind", Kind),
            ("period", Period),
            ("periodFrom", PeriodFrom),
            ("periodTo", PeriodTo),
            ("currency", Currency),
            ("cededPercent", CededPercent),
            ("retention", Retention),
            ("lines", Lines),
            ("priority", Priority),
            ("treatyLimit", TreatyLimit),
            ("treatyPremium", TreatyPremium),
            ("commissionPercent", CommissionPercent),
            ("brokerPercent", BrokerPercent),
            ("reinsurerRating", ReinsurerRating),
            ("ratingAgency", RatingAgency),
            ("classes", Classes),
            ("status", Status),
            ("note", Note),
            ("terms", Terms));
    }

    public static class Actions
    {
        public const string Close = "ხელშეკრულების დახურვა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("close", Close));
    }

    public static class Errors
    {
        public const string Name = "მიუთითეთ დასახელება";
        public const string Reinsurer = "ამ კოდით გადამზღვეველი რეესტრში ვერ მოიძებნა — ჯერ დაამატეთ იგი კლიენტებში (იურიდიული პირი)";
        public const string Broker = "ამ კოდით ბროკერი რეესტრში ვერ მოიძებნა";
        public const string Period = "მიუთითეთ სწორი პერიოდი";
        public const string CededPercent = "წილი უნდა იყოს 0-დან 100-მდე";
        public const string Surplus = "მიუთითეთ შეკავება და ხაზების რაოდენობა";
        public const string Priority = "მიუთითეთ პრიორიტეტი";
        public const string Classes = "აირჩიეთ ერთი სახეობა მაინც";
        public const string Percent = "პროცენტი უნდა იყოს 0-დან 100-მდე";
        public const string OverCeded = "ამ სახეობაზე და პერიოდში მოქმედ ხელშეკრულებებთან ერთად გადაცემული წილი 100%-ს აჭარბებს";
        public const string XolWithProportional = "XL ხელშეკრულება ამ სახეობაზე და პერიოდში პროპორციულ ხელშეკრულებასთან ერთად ვერ იმოქმედებს: ჯერ დასადასტურებელია, XL ბრუტო ზარალზე მუშაობს თუ საკუთარ წილზე (კითხვა მფლობელთან)";
        public const string XolLayers = "ამ სახეობაზე და პერიოდში უკვე მოქმედებს XL ხელშეკრულება: რამდენიმე XL-ის ფენების წესი (რომელი რომლის ზემოთ) ჯერ დასადასტურებელია (კითხვა მფლობელთან)";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("name", Name),
            ("reinsurer", Reinsurer),
            ("broker", Broker),
            ("period", Period),
            ("cededPercent", CededPercent),
            ("surplus", Surplus),
            ("priority", Priority),
            ("classes", Classes),
            ("percent", Percent),
            ("overCeded", OverCeded),
            ("xolWithProportional", XolWithProportional),
            ("xolLayers", XolLayers));
    }

    /// <summary>
    /// Refusals of a whole operation, keyed by the check that failed (review H07, H08).
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Problems = global::Insurance.Application.Texts.TextMap.Of(
        ("over-ceded", "გადაზღვევის ხელშეკრულებების ჯამური წილი ერთ რისკზე 100%-ს აჭარბებს — ოპერაცია არ შესრულდა. შეასწორეთ ხელშეკრულებები."),
        ("xol-with-proportional", "ერთ რისკზე XL და პროპორციული გადაზღვევა ერთად — მათი თანმიმდევრობის წესი ჯერ დასადასტურებელია, ოპერაცია არ შესრულდა."),
        ("currency-mismatch", "XL ხელშეკრულების ვალუტა ზარალის ვალუტას არ ემთხვევა — კურსის წესი ჯერ დასადასტურებელია, ოპერაცია არ შესრულდა."),
        ("xol-layers", "ერთ ზარალზე რამდენიმე XL ხელშეკრულება — ფენების წესი ჯერ დასადასტურებელია, ოპერაცია არ შესრულდა."));

    public static class Cessions
    {
        public const string Title = "გადაზღვევა";
        public const string None = "გადაზღვევა არ არის";
        public const string Treaty = "ხელშეკრულება";
        public const string Share = "წილი, %";
        public const string CededPremium = "გადაცემული პრემია";
        public const string CededPremiumGel = "გადაცემული პრემია, ₾";
        public const string Commission = "საკომისიო";
        public const string Facultative = "ფაკულტატური განთავსება";
        public const string FacultativeTreaty = "ფაკულტატური ხელშეკრულება";
        public const string Place = "განთავსება";
        public const string Placed = "ფაკულტატური განთავსება ჩაიწერა";
        public const string NoFacultative = "ამ პოლისის ვალუტაში მოქმედი ფაკულტატური ხელშეკრულება არ არის";
        public const string AlreadyPlaced = "ეს ხელშეკრულება ამ პოლისზე უკვე გამოყენებულია";
        public const string PeriodClosed = "გადაცემის თარიღი დახურულ თვეშია";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("none", None),
            ("treaty", Treaty),
            ("share", Share),
            ("cededPremium", CededPremium),
            ("cededPremiumGel", CededPremiumGel),
            ("commission", Commission),
            ("facultative", Facultative),
            ("facultativeTreaty", FacultativeTreaty),
            ("place", Place),
            ("placed", Placed),
            ("noFacultative", NoFacultative),
            ("alreadyPlaced", AlreadyPlaced),
            ("periodClosed", PeriodClosed));
    }

    public static class ClaimShare
    {
        public const string Title = "გადამზღვევლის წილი";
        public const string InReserve = "წილი რეზერვში";
        public const string InPaid = "წილი ანაზღაურებულში";
        public const string None = "ზარალი გადაზღვეული არ არის";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("inReserve", InReserve),
            ("inPaid", InPaid),
            ("none", None));
    }

    public static class Bordereau
    {
        public const string Title = "ბორდერო";
        public const string Button = "ბორდერო";
        public const string Note = "ხელშეკრულების ანგარიში პერიოდზე გადამზღვევლისთვის: გადაცემული პრემია და საკომისიო (პერიოდში აღრიცხული გადაცემები), გადამზღვევლის წილი პერიოდში ანაზღაურებულ ზარალებში და რეზერვში პერიოდის ბოლოს, ბალანსი. ველები — შიგა აღრიცხვის წესის დანართი №4 (№11/2016). Excel-ში ჩამოტვირთვა გადამზღვევლისთვის/ბროკერისთვის გასაგზავნად.";
        public const string From = "პერიოდის დასაწყისი";
        public const string To = "პერიოდის დასასრული";
        public const string Show = "ჩვენება";
        public const string Download = "Excel-ის ჩამოტვირთვა";
        public const string PremiumsTitle = "გადაცემული პრემია";
        public const string ClaimsTitle = "ზარალები";
        public const string BalanceTitle = "ბალანსი";
        public const string NoRows = "ამ პერიოდში ჩანაწერები არ არის";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Premium = global::Insurance.Application.Texts.TextMap.Of(
            ("policyNumber", "პოლისი"),
            ("policyholder", "დამზღვევი"),
            ("object", "დაზღვევის საგანი"),
            ("classCode", "სახეობა"),
            ("coverFrom", "დაფარვის დასაწყისი"),
            ("coverTo", "დაფარვის დასასრული"),
            ("sumInsured", "სადაზღვევო თანხა"),
            ("currency", "ვალუტა"),
            ("sharePercent", "წილი, %"),
            ("cededPremium", "გადაცემული პრემია"),
            ("cededPremiumGel", "გადაცემული პრემია, ₾"),
            ("commissionGel", "საკომისიო, ₾"),
            ("bookedOn", "აღრიცხვის თარიღი"),
            ("kind", "ტიპი"),
            ("issue", "გადაცემა"),
            ("cancellation", "გაუქმება"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Claims = global::Insurance.Application.Texts.TextMap.Of(
            ("claimNumber", "ზარალი"),
            ("policyNumber", "პოლისი"),
            ("occurredOn", "შემთხვევის თარიღი"),
            ("reportedOn", "განცხადების თარიღი"),
            ("status", "სტატუსი"),
            ("currency", "ვალუტა"),
            ("paidShare", "წილი ანაზღაურებულში (პერიოდი)"),
            ("paidShareGel", "წილი ანაზღაურებულში, ₾"),
            ("reserveShare", "წილი რეზერვში (პერიოდის ბოლოს)"),
            ("reserveShareGel", "წილი რეზერვში, ₾"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Balance = global::Insurance.Application.Texts.TextMap.Of(
            ("treaty", "ხელშეკრულება"),
            ("reinsurer", "გადამზღვეველი"),
            ("period", "პერიოდი"),
            ("cededGel", "გადაცემული პრემია, ₾"),
            ("commissionGel", "გადაზღვევის საკომისიო, ₾"),
            ("brokerageGel", "ბროკერის საკომისიო, ₾"),
            ("claimsPaidGel", "გადამზღვევლის წილი ანაზღაურებულ ზარალებში, ₾"),
            ("reserveGel", "გადამზღვევლის წილი რეზერვში პერიოდის ბოლოს, ₾ (საინფორმაციო)"),
            ("balanceGel", "გადამზღვევლისთვის გადასახდელი ბალანსი, ₾ (პრემია − საკომისიოები − ზარალების წილი)"));
    }

    public static class Report
    {
        public const string Ceded = "გადაზღვევის პრემია";
        public const string CededUnearned = "გადამზღვევლის წილი UPR-ში";
        public const string RiCommission = "გადაზღვევის საკომისიო";
        public const string NetWritten = "ნეტო მოზიდული";
        public const string ReinsurerPaid = "გადამზღვევლის წილი ანაზღაურებულში";
        public const string ReinsurerRbns = "გადამზღვევლის წილი RBNS-ში";
        public const string TreatyPremium = "XL / დეპოზიტური პრემია თვეში";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("ceded", Ceded),
            ("cededUnearned", CededUnearned),
            ("riCommission", RiCommission),
            ("netWritten", NetWritten),
            ("reinsurerPaid", ReinsurerPaid),
            ("reinsurerRbns", ReinsurerRbns),
            ("treatyPremium", TreatyPremium));
    }
}
