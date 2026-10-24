// Interface texts of the billing screens (premium payments, overdue list).
// Ported from src/i18n/billing.ts (billingTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class BillingTexts
{
    public const string Nav = "ვადაგადაცილებული გადასახდელები";

    public static class Payment
    {
        public const string Record = "გადახდის რეგისტრაცია";
        public const string Title = "პრემიის გადახდის მიღება";
        public const string Method = "გადახდის საშუალება";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Methods = global::Insurance.Application.Texts.TextMap.Of(
            ("bank", "საბანკო გადარიცხვა"),
            ("cash", "ნაღდი ანგარიშსწორება"),
            ("online", "ონლაინ გადახდა"));

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> MethodLabels = global::Insurance.Application.Texts.TextMap.Of(
            ("bank", "საბანკო გადარიცხვა"),
            ("cash", "ნაღდი ანგარიშსწორება"),
            ("online", "ონლაინ გადახდა"),
            ("claim_offset", "ჩათვლა ზარალთან"),
            ("commission_offset", "ჩათვლა საკომისიოსთან"));

        public const string AmountGel = "თანხა, ₾";
        public const string ReceivedOn = "გადახდის თარიღი";
        public const string Reference = "საბუთის ნომერი / კომენტარი";
        public const string Submit = "გადახდის შენახვა";
        public const string OutstandingNow = "ამჟამინდელი დავალიანება";
        public const string RateNote = "თანხა იფარება გადახდის დღის ეროვნული ბანკის კურსით, ყველაზე ძველი შენატანიდან.";
        public const string Saved = "გადახდა შენახულია და მიება შენატანებს";
        public const string Advance = "გადასახდელზე მეტი ჩაირიცხა; ზედმეტი თანხა (₾) რჩება წინასწარ გადახდად";
        public const string History = "გადახდები";
        public const string Kind = "ტიპი";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kinds = global::Insurance.Application.Texts.TextMap.Of(
            ("receipt", "მიღება"),
            ("reversal", "შებრუნება"),
            ("refund", "დაბრუნება"));

        public const string Reverse = "შებრუნება";
        public const string ReverseReason = "შებრუნების მიზეზი";
        public const string Reversed = "შებრუნებულია";
        public const string Refund = "თანხის დაბრუნება კლიენტისთვის";
        public const string RefundNote = "დაბრუნება ლარში, დაბრუნების დღის ოფიციალური კურსით.";

        /// <summary>
        /// Refunds above the company's threshold, waiting for the financial director (refundOverpayment, approvePremiumRefundRequest).
        /// </summary>
        public static class RefundRequests
        {
            public const string Title = "ფინანსური დირექტორის ავტორიზაციას დაქვემდებარებული დაბრუნებები";
            public const string Note = "დასაბრუნებელი თანხა ლარში ზღვარზე მეტია, ამიტომ თანხა ჯერ არ დაბრუნებულა: არც დაბრუნება, არც განაწილება არ ჩაწერილა. ავტორიზაციისას თანხა და ყოველი ხაზი ხელახლა დაითვლება და ჩაიწერება მხოლოდ იგივე გეგმით.";
            public const string RefundedOn = "დაბრუნების დღე";
            public const string AmountGel = "თანხა, ₾";
            public const string RequestedBy = "მოითხოვა";
            public const string Pending = "ელოდება ავტორიზაციას";

            public static string Approved(string by) =>
                string.Concat("ავტორიზაცია: ", by);

            public const string Approve = "ავტორიზაცია და დაბრუნება";
        }

        /// <summary>
        /// Receipt reversals above the company's threshold, waiting for the financial director (reversePayment, approveReceiptReversalRequest).
        /// </summary>
        public static class ReversalRequests
        {
            public const string Title = "ფინანსური დირექტორის ავტორიზაციას დაქვემდებარებული შებრუნებები";
            public const string Note = "ჩარიცხვის შენახული თანხა ლარში ზღვარზე მეტია, ამიტომ შებრუნება ჯერ არ გატარებულა: არც სარკისებური გადახდა, არც განაწილებები არ ჩაწერილა. ავტორიზაციისას ჩარიცხვა და მისი ყველა განაწილება ხელახლა წაიკითხება და შებრუნდება მხოლოდ იგივე გეგმით, შენახული თანხებითა და კურსით.";
            public const string ReversedOn = "შებრუნების დღე";
            public const string AmountGel = "ჩარიცხვა, ₾";
            public const string Reason = "მიზეზი";
            public const string RequestedBy = "მოითხოვა";
            public const string Pending = "ელოდება ავტორიზაციას";

            public static string Approved(string by) =>
                string.Concat("ავტორიზაცია: ", by);

            public const string Approve = "ავტორიზაცია და შებრუნება";
        }

        /// <summary>
        /// What a receipt reversal request or its approval ended in, by its outcome.
        /// </summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ReversalOutcomes = global::Insurance.Application.Texts.TextMap.Of(
            ("requested", "შებრუნება ჯერ არ გატარებულა: ჩარიცხვის თანხა ზღვარზე მეტია და ფინანსური დირექტორის ავტორიზაციას საჭიროებს. მოთხოვნა შენახულია."),
            ("approved", "ავტორიზაცია გაიცა — ჩარიცხვა შებრუნდა"),
            ("already", "ეს მოთხოვნა უკვე შესრულებულია"),
            ("stale", "მოთხოვნის შემდეგ ჩარიცხვა ან მისი განაწილება შეიცვალა — არაფერი ჩაწერილა; საჭიროა ახალი მოთხოვნა"),
            ("not-allowed", "ზღვარზე მეტი გადახდის ავტორიზაციის უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები)."),
            ("state", "ეს ჩარიცხვა უკვე შებრუნებულია ან ეს მოთხოვნა ამ პოლისზე არ არის"),
            ("period-closed", "შებრუნების დღის თვე ბუღალტრულად დახურულია — საჭიროა ახალი მოთხოვნა"));

        /// <summary>
        /// What a refund request or its approval ended in, by its outcome.
        /// </summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> RefundOutcomes = global::Insurance.Application.Texts.TextMap.Of(
            ("requested", "თანხა ჯერ არ დაბრუნებულა: დასაბრუნებელი თანხა ზღვარზე მეტია და ფინანსური დირექტორის ავტორიზაციას საჭიროებს. მოთხოვნა შენახულია."),
            ("approved", "ავტორიზაცია გაიცა — თანხა დაბრუნდა"),
            ("already", "ეს მოთხოვნა უკვე შესრულებულია"),
            ("stale", "მოთხოვნის შემდეგ ზედმეტობა, მისი განაწილება ან კურსი შეიცვალა — არაფერი ჩაწერილა; საჭიროა ახალი მოთხოვნა"),
            ("not-allowed", "ზღვარზე მეტი გადახდის ავტორიზაციის უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები)."),
            ("state", "ეს მოქმედება ამ პოლისზე ან ამ მოთხოვნაზე დაუშვებელია"),
            ("suspended", "კლიენტზე მოქმედებს შეჩერება — თანხა არ დაბრუნდა; მიმართეთ AML-ის პასუხისმგებელ პირს"),
            ("period-closed", "დაბრუნების დღის თვე ბუღალტრულად დახურულია — საჭიროა ახალი მოთხოვნა"));

        public const string AllocatedTo = "დაიფარა";
        public const string AllocatedOn = "განაწილების თარიღი";
        public const string HerePolicy = "ამ პოლისზე, ₾";
        public const string ReverseShared = "ეს თანხა სხვა პოლისზეც არის განაწილებული; შებრუნდება მთლიანი თანხა ყველა პოლისიდან.";

        public static class Advances
        {
            public const string Title = "კლიენტის გაუნაწილებელი თანხები";
            public const string Note = "უკვე მიღებული თანხა, რომელიც ჯერ არცერთ შენატანს არ ფარავს. განაწილებისას ახალი გადახდა არ იქმნება: თანხა ფარავს ამ პოლისის ყველაზე ძველ გადაუხდელ შენატანებს მიღების დღის კურსით, განაწილება აღირიცხება დღევანდელი თარიღით, მიღების თარიღი არ იცვლება. რაც პოლისს არ სჭირდება, გაუნაწილებელი რჩება.";
            public const string Available = "გაუნაწილებელი, ₾";
            public const string Allocate = "ამ პოლისზე განაწილება";
            public const string CardNote = "პოლისის მფლობელს აქვს გაუნაწილებელი თანხა";
            public const string Open = "ნახვა და განაწილება";
            public const string Allocated = "გაუნაწილებელი თანხიდან ამ პოლისზე განაწილდა";
            public const string Left = "გაუნაწილებელი დარჩა";
            public const string OverpaidTitle = "კლიენტის ზედმეტად გადახდილი თანხები სხვა პოლისებზე";
            public const string OverpaidNote = "შემცირების შემდეგ შენატანზე თავის თანხაზე მეტია გადახდილი. გადატანისას ახალი გადახდა არ იქმნება: ქვითარი ზედმეტობას იმ შენატანებს უბრუნებს და ამ პოლისის ყველაზე ძველ გადაუხდელ შენატანებს ფარავს მიღების დღის კურსით; განაწილება აღირიცხება დღევანდელი თარიღით. რაც ამ პოლისს არ სჭირდება, გაუნაწილებელი რჩება.";
            public const string OverpaidAvailable = "ზედმეტად გადახდილი, ₾";
            public const string FromPolicies = "პოლისებზე";
            public const string Transfer = "ამ პოლისზე გადატანა";
            public const string CardOverpaid = "პოლისის მფლობელს სხვა პოლისებზე ზედმეტად გადახდილი თანხა აქვს";
            public const string Transferred = "ზედმეტად გადახდილიდან ამ პოლისზე გადმოვიდა";
            public const string Released = "სხვა პოლისებიდან გათავისუფლდა";

            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Outcomes = global::Insurance.Application.Texts.TextMap.Of(
                ("read-only", "თქვენს როლს თანხის განაწილების უფლება არ აქვს"),
                ("not-found", "ეს თანხა ამ პოლისის მფლობელს არ ეკუთვნის ან ვერ მოიძებნა; არაფერი შეცვლილა"),
                ("nothing-left", "ამ თანხიდან გასანაწილებელი აღარაფერი დარჩა (უკვე განაწილდა ან შებრუნდა); არაფერი შეცვლილა"),
                ("nothing-owed", "პოლისზე გადაუხდელი შენატანი არ არის; თანხა გაუნაწილებელი დარჩა"),
                ("period-closed", "მიმდინარე თვე ბუღალტრულად დახურულია; განაწილება არ აღირიცხა"),
                ("no-rate", "მიღების დღის კურსი ვერ მოიძებნა; განაწილება არ აღირიცხა"));
        }

        public const string NotPayable = "პოლისზე გადახდა არ მიიღება: სტატუსი არ იძლევა ამის საშუალებას";
        public const string NotAllowed = "პრემიის მიღების უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები).";

        public static class Errors
        {
            public const string Amount = "მიუთითეთ დადებითი თანხა";
            public const string Date = "გადახდის თარიღი არასწორია ან მომავალშია";
            public const string Method = "აირჩიეთ გადახდის საშუალება";
            public const string Rate = "ამ თარიღის კურსი ვერ მოიძებნა";
            public const string NothingOwed = "პოლისზე გადაუხდელი შენატანი არ არის";
            public const string PeriodClosed = "ეს თვე ბუღალტრულად დახურულია; გადახდა მიმდინარე ღია თვის თარიღით დაარეგისტრირეთ";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("amount", Amount),
                ("date", Date),
                ("method", Method),
                ("rate", Rate),
                ("nothingOwed", NothingOwed),
                ("periodClosed", PeriodClosed));
        }
    }

    public static class Overdue
    {
        public const string Title = "ვადაგადაცილებული გადასახდელები";
        public const string AsOf = "მდგომარეობა";
        public const string Policy = "პოლისი";
        public const string Policyholder = "პოლისის მფლობელი";
        public const string OldestDue = "უძველესი ვადა";
        public const string DaysLate = "დაგვიანება, დღე";
        public const string OverdueText = "ვადაგადაცილებული";
        public const string Total = "ჯამური დავალიანება";
        public const string InGel = "ლარში";
        public const string TotalGel = "სულ ვადაგადაცილებული (₾, დღევანდელი კურსით)";
        public const string Register = "გადახდა";
        public const string None = "ვადაგადაცილებული შენატანი არ არის";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("asOf", AsOf),
            ("policy", Policy),
            ("policyholder", Policyholder),
            ("oldestDue", OldestDue),
            ("daysLate", DaysLate),
            ("overdue", OverdueText),
            ("total", Total),
            ("inGel", InGel),
            ("totalGel", TotalGel),
            ("register", Register),
            ("none", None));
    }
}
