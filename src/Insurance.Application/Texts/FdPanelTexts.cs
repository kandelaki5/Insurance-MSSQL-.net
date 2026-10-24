// Interface texts of the financial director's panel (src/app/(app)/finance/panel).
// Ported from src/i18n/fd-panel.ts (fdPanelTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class FdPanelTexts
{
    public const string Nav = "ფინანსური დირექტორის პანელი";
    public const string Title = "ფინანსური დირექტორის პანელი";
    public const string Note = "ზღვარზე მეტი თანხის მოთხოვნები, რომლებიც ჯერ არ შესრულებულა — ძველი ზემოთაა. პანელი მხოლოდ აჩვენებს: ავტორიზაცია ხდება ზარალის ან პოლისის ბარათზე, სადაც უფლება, თანხა და თვე ხელახლა მოწმდება. ამიტომ სიაში მყოფი მოთხოვნა შეიძლება ბარათზე მოძველებული აღმოჩნდეს, ან ერთსა და იმავე გადახდაზე ორი მოთხოვნა იყოს.";
    public const string NotAllowed = "ფინანსური დირექტორის პანელის ნახვის უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები).";

    public static class Queues
    {
        public static class Claim
        {
            public const string Title = "ზარალის ანაზღაურება";
            public const string Kind = "ზარალის ანაზღაურება";
            public const string Card = "ზარალი";
            public const string Amount = "ნეტო გადასარიცხი, ლარი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("title", Title),
                ("kind", Kind),
                ("card", Card),
                ("amount", Amount));
        }

        public static class Refund
        {
            public const string Title = "პრემიის დაბრუნება";
            public const string Kind = "პრემიის დაბრუნება";
            public const string Card = "პოლისი";
            public const string Amount = "დასაბრუნებელი, ლარი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("title", Title),
                ("kind", Kind),
                ("card", Card),
                ("amount", Amount));
        }

        public static class Reversal
        {
            public const string Title = "პრემიის მიღების შებრუნება";
            public const string Kind = "მიღების შებრუნება";
            public const string Card = "პოლისი";
            public const string Amount = "ჩარიცხვის ლარი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("title", Title),
                ("kind", Kind),
                ("card", Card),
                ("amount", Amount));
        }
    }

    public static class Columns
    {
        public const string RequestedOn = "მოთხოვნის დღე";
        public const string Kind = "ტიპი";
        public const string Request = "მოთხოვნა";
        public const string Receipt = "ჩარიცხვა";
        public const string Requester = "მოითხოვა";
        public const string Status = "მდგომარეობა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("requestedOn", RequestedOn),
            ("kind", Kind),
            ("request", Request),
            ("receipt", Receipt),
            ("requester", Requester),
            ("status", Status));
    }

    public const string Pending = "შესრულება ჯერ არ ფიქსირდება";
    public const string Empty = "შეუსრულებელი მოთხოვნა არ არის.";
    public const string NotReady = "ეს სია ჯერ ვერ იკითხება: ბაზაში მისი ცხრილი არ არის (მიგრაცია არ არის გაშვებული). ეს არ ნიშნავს, რომ მოთხოვნა არ არის.";
    public const string Failed = "სიის წაკითხვა ვერ მოხერხდა. ეს არ ნიშნავს, რომ მოთხოვნა არ არის — სცადეთ თავიდან.";
    public const string Next = "შემდეგი გვერდი →";
    public const string First = "← პირველი გვერდი";

    /// <summary>
    /// The chosen month's figures, from the monthly premium report.
    /// </summary>
    public static class Summary
    {
        public const string Title = "თვის მაჩვენებლები";
        public const string Note = "თვის პრემიის ანგარიშის ჯამები, იმავე წესით: ლარში, თითო ჩანაწერი თავისი კურსით, დავალიანება თვის ბოლო დღის კურსით. ზემოთ მოცემული შეუსრულებელი მოთხოვნები აქ არ შედის. ანაზღაურებული ზარალი ბრუტოა (ფრანშიზისა და ჩათვლის ჩათვლით) — ეს არ არის ბანკიდან გასული თანხა.";
        public const string Premium = "პრემია";
        public const string PaidGross = "ანაზღაურებული (ბრუტო)";
        public const string Failed = "თვის ანგარიში ვერ აიგო. ეს არ ნიშნავს, რომ ამ თვეში მაჩვენებლები ნულია — სცადეთ თავიდან.";
        public const string Detailed = "დეტალური თვის ანგარიში";
        public const string OverdueToday = "მიმდინარე ვადაგადაცილება (დღევანდელი მდგომარეობით)";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("note", Note),
            ("premium", Premium),
            ("paidGross", PaidGross),
            ("failed", Failed),
            ("detailed", Detailed),
            ("overdueToday", OverdueToday));
    }
}
