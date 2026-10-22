// Interface texts of the AML supervisory report (order №47/2020).
// Ported from src/i18n/aml.ts (amlTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class AmlTexts
{
    public const string Nav = "AML ანგარიშგება (№47)";
    public const string Title = "ფგ/ტდ რისკის ზედამხედველობის ანგარიშგება (ბრძანება №47)";
    public const string Note = "ნახევარწლიური (31 ივლისამდე) და წლიური (31 იანვრამდე) ანგარიშგების აგრეგატები ფორმებისთვის №2–№6, №8 და №9. პრემია — გაცემული პოლისით, გადახდის მიუხედავად, ზრდა აისახება, შემცირება არა; ლარში პოლისის ძალაში შესვლის კურსით; რისკის დონე — პერიოდის ბოლო დღის მდგომარეობით. ფორმა №9 ივსება „საეჭვო გარიგებების“ რეესტრიდან, №8-ის შეჩერებები — ფმს-ის შეჩერებებიდან. ფორმები №1 (მზღვეველის მონაცემები), №7 (მესამე პირზე დაყრდნობა) და №10 (რეკომენდაციები) ხელით ივსება. Excel სამსახურის ოფიციალურ შაბლონში გადასატანია — შაბლონი (O38-danarti1.xlsx) სისტემაში ჯერ არ არის ატვირთული.";
    public const string Year = "წელი";
    public const string Period = "პერიოდი";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Halves = global::Insurance.Application.Texts.TextMap.Of(
        ("1", "I ნახევარი (იანვარი–ივნისი)"),
        ("2", "II ნახევარი (ივლისი–დეკემბერი)"),
        ("year", "მთელი წელი"));

    public const string Show = "ჩვენება";
    public const string Download = "Excel-ის ჩამოტვირთვა";

    public static class Forms
    {
        public const string N2 = "ფორმა №2 — კლიენტთა ბაზა რისკის დონის მიხედვით";
        public const string N3 = "ფორმა №3 — კლიენტთა რეზიდენტობა";
        public const string N4 = "ფორმა №4 — დაზღვევის სახეობები";
        public const string N5 = "ფორმა №5 — უნაღდო ოპერაციები 3 000 ₾-ზე მეტი ერთჯერადად";
        public const string N6 = "ფორმა №6 — ნაღდი ოპერაციები 3 000 ₾-ზე მეტი ერთჯერადად";
        public const string N8 = "ფორმა №8 — სანქცირებული კლიენტები და შეჩერებული გარიგებები";
        public const string N9 = "ფორმა №9 — ფმს-ში საეჭვო სტატუსით გაგზავნილი ანგარიშგებები";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("n2", N2),
            ("n3", N3),
            ("n4", N4),
            ("n5", N5),
            ("n6", N6),
            ("n8", N8),
            ("n9", N9));
    }

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Residency = global::Insurance.Application.Texts.TextMap.Of(
        ("resident", "რეზიდენტი"),
        ("non_resident", "არარეზიდენტი"));

    public const string N5Note = "კონტრაგენტის მომსახურე ფინანსური ინსტიტუტის ქვეყანა ჯერ არ აღირიცხება — ყველა ოპერაცია საქართველოდ (GE) ითვლება; ზარალებში არ შედის სერვისცენტრებზე გადახდები.";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Columns = global::Insurance.Application.Texts.TextMap.Of(
        ("risk", "რისკის დონე"),
        ("segment", "კლიენტის ტიპი"),
        ("activeAtStart", "აქტიური კლიენტები პერიოდის დასაწყისში"),
        ("issuedClients", "პერიოდში გაფორმებული — კლიენტები"),
        ("issuedPremiumGel", "პერიოდში გაფორმებული — პრემია, ₾"),
        ("activeAtEnd", "აქტიური კლიენტები პერიოდის ბოლოს"),
        ("country", "ქვეყნის კოდი"),
        ("personClients", "ფიზიკური პირები — კლიენტები"),
        ("otherClients", "სხვა კლიენტები"),
        ("personPremiumGel", "ფიზიკური პირები — პრემია, ₾"),
        ("otherPremiumGel", "სხვა — პრემია, ₾"),
        ("personActiveAtEnd", "ფიზიკური პირები — აქტიური ბოლოს"),
        ("otherActiveAtEnd", "სხვა — აქტიური ბოლოს"),
        ("line", "სახეობა"),
        ("operation", "ოპერაცია"),
        ("count", "რაოდენობა"),
        ("amountGel", "ოდენობა, ₾"),
        ("frozenClients", "კლიენტები, რომელთა ქონებას დაედო ყადაღა"),
        ("suspendedClients", "კლიენტები, რომელთა გარიგებები შეჩერდა (ფმს)"),
        ("strType", "სტატუსი"),
        ("residency", "კლიენტის რეზიდენტობა"),
        ("additionalInfo", "დამატებითი ინფორმაცია"),
        ("caseNumber", "შემთხვევა"),
        ("sentAt", "გაგზავნის დრო"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Risk = global::Insurance.Application.Texts.TextMap.Of(
        ("high", "მაღალი"),
        ("medium", "საშუალო"),
        ("low", "დაბალი"),
        ("none", "შეუფასებელი"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Segment = global::Insurance.Application.Texts.TextMap.Of(
        ("person_resident", "ფიზიკური პირი, რეზიდენტი"),
        ("organization_resident", "იურიდიული პირი, რეზიდენტი"),
        ("person_non_resident", "ფიზიკური პირი, არარეზიდენტი"),
        ("organization_non_resident", "იურიდიული პირი, არარეზიდენტი"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Line = global::Insurance.Application.Texts.TextMap.Of(
        ("life", "სიცოცხლის დაზღვევა (საინვესტიციო)"),
        ("other", "დაზღვევის სხვა სახეობები"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Operation = global::Insurance.Application.Texts.TextMap.Of(
        ("premium_received", "პრემიის მიღება"),
        ("premium_refunded", "პრემიის დაბრუნება"),
        ("claims_paid", "სადაზღვევო ანაზღაურების გაცემა"));

    public const string Total = "სულ";
    public const string None = "ჩანაწერები არ არის";

    public static class Cases
    {
        public const string Nav = "საეჭვო გარიგებები (AML)";
        public const string Title = "უჩვეულო და საეჭვო გარიგებების შემთხვევები";
        public const string Note = "უჩვეულო გარიგება დაუყოვნებლივ უნდა შეისწავლოთ და შედეგი დაასაბუთოთ (AML კანონი, მუხ. 20); საფუძვლიანი ეჭვისას ფმს-ს ანგარიშგება ეგზავნება ეჭვის გაჩენის დღესვე (მუხ. 25–26). ეს ჩანაწერები კონფიდენციალურია — კლიენტს არ ეცნობება (მუხ. 28). ფმს-ში გაგზავნილი ანგარიშგებები ავტომატურად ხვდება №47-ის ფორმა №9-ში.";
        public const string New = "შემთხვევის რეგისტრაცია";
        public const string Register = "რეგისტრაცია";
        public const string Registered = "შემთხვევა დარეგისტრირდა";
        public const string Saved = "შენახულია";

        public static class Fields
        {
            public const string Number = "ნომერი";
            public const string Party = "კლიენტი";
            public const string PartyIdNumber = "კლიენტის პირადი ნომერი / საიდენტიფიკაციო კოდი";
            public const string OpenedOn = "გახსნის თარიღი";
            public const string Source = "წყარო";
            public const string Description = "გარიგების აღწერა და რატომ არის უჩვეულო";
            public const string TransactionRef = "გარიგების მითითება (პოლისი, გადახდა, ზარალი)";
            public const string ClassCode = "დაზღვევის სახეობა";
            public const string Status = "სტატუსი";
            public const string ExaminedOn = "შესწავლის თარიღი";
            public const string Examination = "შესწავლის შედეგი და დასაბუთება";
            public const string SuspicionAt = "ეჭვის გაჩენის დრო";
            public const string StrSentAt = "ანგარიშგების გაგზავნის დრო";
            public const string StrType = "ანგარიშგების სტატუსი";
            public const string StrOtherBasis = "საფუძვლის აღწერა („სხვა“-სთვის)";
            public const string FmsReference = "ფმს-ის ნომერი / დადასტურება";
            public const string ClosedOn = "დახურვის თარიღი";
            public const string CreatedBy = "დაარეგისტრირა";
            public const string Note = "შენიშვნა";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("number", Number),
                ("party", Party),
                ("partyIdNumber", PartyIdNumber),
                ("openedOn", OpenedOn),
                ("source", Source),
                ("description", Description),
                ("transactionRef", TransactionRef),
                ("classCode", ClassCode),
                ("status", Status),
                ("examinedOn", ExaminedOn),
                ("examination", Examination),
                ("suspicionAt", SuspicionAt),
                ("strSentAt", StrSentAt),
                ("strType", StrType),
                ("strOtherBasis", StrOtherBasis),
                ("fmsReference", FmsReference),
                ("closedOn", ClosedOn),
                ("createdBy", CreatedBy),
                ("note", Note));
        }

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Sources = global::Insurance.Application.Texts.TextMap.Of(
            ("monitoring", "მონიტორინგის წესი"),
            ("employee", "თანამშრომლის შეტყობინება"),
            ("fms_request", "ფმს-ის მოთხოვნა"),
            ("other", "სხვა"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Statuses = global::Insurance.Application.Texts.TextMap.Of(
            ("open", "შესასწავლი"),
            ("examined", "შესწავლილი — ანგარიშგების გარეშე"),
            ("reported", "ანგარიშგება გაგზავნილია"),
            ("closed", "დახურული"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> StrTypes = global::Insurance.Application.Texts.TextMap.Of(
            ("ml", "ფულის გათეთრება"),
            ("tf", "ტერორიზმის დაფინანსება"),
            ("other", "სხვა"));

        public static class Decisions
        {
            public const string Examine = "შესწავლის შენახვა (ანგარიშგების გარეშე)";
            public const string Report = "ფმს-ში გაგზავნილის ფიქსაცია";
            public const string Close = "დახურვა";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("examine", Examine),
                ("report", Report),
                ("close", Close));
        }

        public const string LateStr = "ანგარიშგება ეჭვის გაჩენის დღესვე არ გაგზავნილა";
        public const string DecisionTitle = "შესწავლა და გადაწყვეტილება";
        public const string Decision = "მოქმედება";
        public const string All = "ყველა";

        public static class Errors
        {
            public const string Party = "ამ ნომრით კლიენტი ვერ მოიძებნა";
            public const string Description = "აღწერეთ გარიგება";
            public const string OpenedOn = "მიუთითეთ თარიღი";
            public const string Examination = "შესწავლის შედეგი სავალდებულოა";
            public const string Str = "ანგარიშგებას სჭირდება ეჭვის და გაგზავნის დრო და სტატუსი";
            public const string OtherBasis = "სტატუსზე „სხვა“ აღწერეთ საფუძველი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("party", Party),
                ("description", Description),
                ("openedOn", OpenedOn),
                ("examination", Examination),
                ("str", Str),
                ("otherBasis", OtherBasis));
        }
    }

    public static class Suspension
    {
        public const string Title = "ფმს-ის მითითებით შეჩერება";
        public const string Note = "ფმს-ის მითითებით გარიგება ან კლიენტის ოპერაციები ჩერდება არაუმეტეს 72 საათით, დასვენების და უქმე დღეების გამოკლებით (AML კანონი, მუხ. 36). ზეპირი მითითება მომდევნო 24 საათში წერილობით უნდა დადასტურდეს; თუ არ დადასტურდა, კომპანია უფლებამოსილია განაახლოს ოპერაციები (პ. 3) — სისტემა კლიენტს თავისით არ ხსნის, გადაწყვეტილებას იღებს კომპლაიანსის ოფიცერი („შეჩერების მოხსნა“ საფუძვლით). 72 საათის გასვლისას შეჩერება ავტომატურად წყდება. შეჩერების დროს კლიენტზე პრემიის მიღება, ანაზღაურების გაცემა და პოლისის გაფორმება იბლოკება.";
        public const string Active = "ოპერაციები შეჩერებულია ფმს-ის მითითებით";
        public const string None = "შეჩერება არ არის";
        public const string Create = "შეჩერების რეგისტრაცია";
        public const string Confirm = "წერილობითი დადასტურება მიღებულია";
        public const string Lift = "შეჩერების მოხსნა";

        public static class Fields
        {
            public const string Kind = "მითითების ფორმა";
            public const string Scope = "მოცულობა";
            public const string TransactionRef = "გარიგება: პოლისის ან ზარალის ნომერი";
            public const string InstructedAt = "მითითების მიღების დრო";
            public const string WrittenConfirmedAt = "წერილობითი დადასტურება";
            public const string EndsAt = "ვადა (72 სამუშაო საათი)";
            public const string LiftedAt = "მოხსნა";
            public const string LiftReason = "მოხსნის საფუძველი";
            public const string ConfirmBy = "წერილობითი დადასტურების ვადა";
            public const string Note = "შენიშვნა";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("kind", Kind),
                ("scope", Scope),
                ("transactionRef", TransactionRef),
                ("instructedAt", InstructedAt),
                ("writtenConfirmedAt", WrittenConfirmedAt),
                ("endsAt", EndsAt),
                ("liftedAt", LiftedAt),
                ("liftReason", LiftReason),
                ("confirmBy", ConfirmBy),
                ("note", Note));
        }

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kinds = global::Insurance.Application.Texts.TextMap.Of(
            ("oral", "ზეპირი"),
            ("written", "წერილობითი"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Scopes = global::Insurance.Application.Texts.TextMap.Of(
            ("all", "კლიენტის ყველა ოპერაცია"),
            ("transaction", "კონკრეტული გარიგება"));

        public const string AwaitingConfirmation = "ელოდება წერილობით დადასტურებას";
        public const string Lapsed = "ვადა ამოიწურა / მოხსნილია";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> States = global::Insurance.Application.Texts.TextMap.Of(
            ("active", "მოქმედებს"),
            ("awaiting", "მოქმედებს — ელოდება წერილობით დადასტურებას"),
            ("unconfirmed", "მოქმედებს — წერილობითი დადასტურება 24 საათში არ მიღებულა"),
            ("lapsed", "აღარ მოქმედებს"));

        public const string UnconfirmedHint = "წერილობითი მითითება ვადაში არ მოსულა. კომპანია უფლებამოსილია განაახლოს ოპერაციები (AML კანონი, მუხ. 36, პ. 3): მოხსენით შეჩერება საფუძვლით, ან დატოვეთ და დაუკავშირდით ფმს-ს. მოგვიანებით მიღებული წერილი დაადასტურეთ ღილაკით.";
        public const string UnconfirmedReminder = "ზეპირი შეჩერება წერილობითი დადასტურების გარეშე — საჭიროა ოფიცრის გადაწყვეტილება";
        public const string InForceTitle = "მოქმედი შეჩერებები";
        public const string History = "შეჩერებების ისტორია";
        public const string Party = "კლიენტი";
        public const string Case = "შემთხვევა";
        public const string LiftReasonPlaceholder = "მაგ.: ფმს-ის გადაწყვეტილება / ვადის გასვლა";
        public const string LegacyUnmapped = "ძველი ჩანაწერი, ჩანაწერზე მიუბმელი — ბლოკავს ამ ნომრის ოპერაციას; მიაბით პოლისს/ზარალს ან მოხსენით";
        public const string Attach = "მიბმა";
        public const string AttachPlaceholder = "პოლისის ან ზარალის ნომერი";
        public const string TargetHint = "„კონკრეტული გარიგებისთვის“ სავალდებულოა ამ კლიენტის პოლისის ან ზარალის ნომერი";
        public const string TargetError = "არ შენახულა: მიუთითეთ ამ კლიენტის პოლისის ან ზარალის არსებული ნომერი. თუ ერთი და იგივე ნომერი კლიენტის პოლისსაც და ზარალსაც აქვს, სისტემა თვითონ არ ირჩევს — მიმართეთ ადმინისტრატორს.";
        public const string Blocked = "კლიენტის ოპერაციები შეჩერებულია ფმს-ის მითითებით — ოპერაცია დაუშვებელია (AML კანონი, მუხ. 36)";
    }
}
