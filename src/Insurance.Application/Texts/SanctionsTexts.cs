// Interface texts of sanctions screening (AML law art. 10, 41; FMS order №1 art. 4).
// Ported from src/i18n/sanctions.ts (sanctionsTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class SanctionsTexts
{
    public const string NavGroup = "შესაბამისობა";
    public const string Nav = "სანქციების სკრინინგი";
    public const string Title = "სანქციების სკრინინგი";
    public const string Note = "კლიენტი და მისი ბენეფიციარი მესაკუთრეები მოწმდება გაეროს უშიშროების საბჭოს კონსოლიდირებულ სიაში ურთიერთობის დამყარებამდე და სიის ყოველი განახლებისას (AML კანონი, მუხ. 10.7, 41; ფმს №1, მუხ. 4; №55, მუხ. 1.5). დამთხვევა დაუყოვნებლივ უნდა შეისწავლოთ: დადასტურებული დამთხვევისას კლიენტს მომსახურება უჩერდება და ფმს-ს ეცნობება. ეს ინფორმაცია კონფიდენციალურია — კლიენტს არ ეცნობება (მუხ. 28).";

    public static class List
    {
        public const string Title = "გაეროს სია";
        public const string GeneratedAt = "სიის ვერსია (გაეროს თარიღი)";
        public const string LoadedAt = "ჩამოტვირთვის თარიღი";
        public const string Entries = "ჩანაწერები";
        public const string NotLoaded = "სია ჯერ არ არის ჩამოტვირთული";
        public const string Load = "სიის განახლება გაეროდან";
        public const string Loaded = "სია განახლდა";
        public const string LoadFailed = "სიის ჩამოტვირთვა ვერ მოხერხდა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("generatedAt", GeneratedAt),
            ("loadedAt", LoadedAt),
            ("entries", Entries),
            ("notLoaded", NotLoaded),
            ("load", Load),
            ("loaded", Loaded),
            ("loadFailed", LoadFailed));
    }

    public static class Screen
    {
        public const string All = "ყველა კლიენტის სკრინინგი";
        public const string One = "სკრინინგი ახლა";
        public const string Done = "სკრინინგი შესრულდა";
        public const string Screened = "შემოწმდა";
        public const string Hits = "დამთხვევა შესასწავლი";
        public const string NoList = "ჯერ ჩამოტვირთეთ სია";
        // New in the rewrite (owner, 23.10.2026: a dismissed false positive is reused on an unchanged re-screening); the
        // wording is a proposal awaiting the owner.
        public const string Reused = "დაიხურა წინა გადაწყვეტილებით";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("all", All),
            ("one", One),
            ("done", Done),
            ("screened", Screened),
            ("hits", Hits),
            ("noList", NoList));
    }

    public static class Open
    {
        public const string Title = "შესასწავლი დამთხვევები";
        public const string None = "შესასწავლი დამთხვევა არ არის";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("none", None));
    }

    public const string History = "სკრინინგის ისტორია";
    public const string Last = "ბოლო სკრინინგი";
    public const string Never = "სკრინინგი არ ჩატარებულა";
    public const string Blocked = "სანქცირებული — მომსახურება შეჩერებულია";

    public static class Fields
    {
        public const string Subject = "ვინ შემოწმდა";
        public const string Party = "კლიენტი";
        public const string ScreenedAt = "დრო";
        public const string Matches = "დამთხვევები";
        public const string Outcome = "შედეგი";
        public const string Note = "დასაბუთება";
        public const string DecidedBy = "გადაწყვიტა";
        public const string Score = "მსგავსება";
        public const string ListedAs = "სიაში";
        public const string Reasons = "საფუძველი";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("subject", Subject),
            ("party", Party),
            ("screenedAt", ScreenedAt),
            ("matches", Matches),
            ("outcome", Outcome),
            ("note", Note),
            ("decidedBy", DecidedBy),
            ("score", Score),
            ("listedAs", ListedAs),
            ("reasons", Reasons));
    }

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> SubjectKinds = global::Insurance.Application.Texts.TextMap.Of(
        ("party", "კლიენტი"),
        ("owner", "ბენეფიციარი მესაკუთრე"));

    /// <summary>A reused decision in the decision column (new in the rewrite; wording proposed, awaiting the owner).</summary>
    public const string ReusedDecision = "გამოყენებულია წინა გადაწყვეტილება";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Outcomes = global::Insurance.Application.Texts.TextMap.Of(
        ("clear", "სუფთა"),
        ("possible", "შესასწავლი"),
        ("confirmed", "დადასტურებული დამთხვევა"),
        ("false_positive", "ცრუ დამთხვევა"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Reasons = global::Insurance.Application.Texts.TextMap.Of(
        ("name", "სახელი"),
        ("birth_year", "დაბადების წელი"),
        ("document", "დოკუმენტის ნომერი"));

    public const string Strong = "ძლიერი";

    public static class Actions
    {
        public const string Dismiss = "ცრუ დამთხვევა";
        public const string Confirm = "დადასტურება (მომსახურების შეჩერება)";
        public const string Lift = "შეჩერების მოხსნა";
        public const string NoteRequired = "გადაწყვეტილებას სჭირდება დასაბუთება";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("dismiss", Dismiss),
            ("confirm", Confirm),
            ("lift", Lift),
            ("noteRequired", NoteRequired));
    }

    public const string PolicyBlocked = "კლიენტი სანქცირებულია — პოლისის გაფორმება აკრძალულია (AML კანონი, მუხ. 10.7)";
}
