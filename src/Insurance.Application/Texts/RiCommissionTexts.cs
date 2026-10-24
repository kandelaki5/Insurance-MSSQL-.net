// Interface texts of the reinsurer's commission receivable and its settlements (docs/05 3ე; form N1-4 00140).
// Ported from Insurance-Software PR #362 (head 540e81a) src/i18n/ri-commission.ts (riCommissionTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class RiCommissionTexts
{
    public const string Nav = "გადამზღვევლის საკომისიო";
    public const string Title = "მისაღები საკომისიო გადამზღვევლისგან";
    public const string Note = "100%-ით გადაზღვეულ პოლისებზე გადამზღვევლის საკომისიო (ხელშეკრულების პროცენტი პრემიიდან) პრემიასთან ერთად ირიცხება და მოთხოვნად რჩება, სანამ გადამზღვევლთან არ ჩავითვლით ან არ ჩაგვირიცხავს. ჯამი ლარში, საანგარიშო თარიღის კურსით, ფორმა N1-4-ის 00140-ში („სხვა მოთხოვნები გადაზღვევიდან“) იწერება.";
    public const string Date = "საანგარიშო თარიღი";
    public const string Show = "ჩვენება";
    public const string None = "ამ თარიღისთვის 100%-ით გადაზღვეულ ხელშეკრულებებზე საკომისიო არ არის";

    public static class Columns
    {
        public const string Treaty = "ხელშეკრულება";
        public const string Reinsurer = "გადამზღვეველი";
        public const string Currency = "ვალუტა";
        public const string Accrued = "დარიცხული";
        public const string Settled = "ანგარიშსწორება";
        public const string Balance = "მისაღები";
        public const string Date = "თარიღი";
        public const string Kind = "სახე";
        public const string Amount = "თანხა";
        public const string Reference = "საფუძველი";
        public const string Note = "შენიშვნა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("treaty", Treaty),
            ("reinsurer", Reinsurer),
            ("currency", Currency),
            ("accrued", Accrued),
            ("settled", Settled),
            ("balance", Balance),
            ("date", Date),
            ("kind", Kind),
            ("amount", Amount),
            ("reference", Reference),
            ("note", Note));
    }

    public const string Total = "სულ ლარში (N1-4 00140)";
    public const string RatesUsed = "კურსი საანგარიშო თარიღისთვის";
    public const string OverSettled = "ამ ხელშეკრულებებზე ანგარიშსწორება დარიცხულს აღემატება (მაგ. პოლისის გაუქმების შემდეგ). 00140-ში ისინი არ ჯამდება; რა ვუყოთ ზედმეტს — მფლობელის გადასაწყვეტია.";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kinds = global::Insurance.Application.Texts.TextMap.Of(
        ("offset", "ჩათვლა"),
        ("receipt", "ჩარიცხვა"));

    public const string SettlementsTitle = "ანგარიშსწორებები";

    public static class Record
    {
        public const string Title = "ანგარიშსწორების ჩაწერა";
        public const string Submit = "ჩაწერა";
        public const string Reverse = "შებრუნება";
        public const string ReversedMark = "შებრუნებულია";
        public const string ReversalMark = "შებრუნება";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("submit", Submit),
            ("reverse", Reverse),
            ("reversedMark", ReversedMark),
            ("reversalMark", ReversalMark));
    }

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Outcomes = global::Insurance.Application.Texts.TextMap.Of(
        ("ok", "ჩაიწერა"),
        ("reversed", "შებრუნდა"),
        ("invalid", "შეავსეთ ხელშეკრულება, თარიღი (არა მომავალი), სახე, ვალუტა და დადებითი თანხა"),
        ("exceeds", "თანხა ამ თარიღისთვის დარჩენილ მისაღებ საკომისიოს აღემატება"),
        ("period-closed", "ეს თვე დახურულია — აირჩიეთ ღია თვის თარიღი"),
        ("already", "ეს ანგარიშსწორება უკვე შებრუნებულია"),
        ("state", "ანგარიშსწორება ვერ მოიძებნა ან თვითონ შებრუნებაა"),
        ("not-allowed", "ანგარიშსწორების ჩაწერის უფლება არ გაქვთ"));
}
