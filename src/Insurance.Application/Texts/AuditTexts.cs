// Interface texts of the shared change log (src/app/(app)/audit).
// Ported from src/i18n/audit.ts (auditTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class AuditTexts
{
    public const string Nav = "ცვლილებების ჟურნალი";
    public const string Title = "ცვლილებების ჟურნალი";
    public const string Note = "ვინ, როდის, რა შეცვალა სისტემაში. ჟურნალი მხოლოდ იკითხება — ჩანაწერი არ იცვლება და არ იშლება. ახალი ჩანაწერები ზემოთაა.";
    public const string NotAllowed = "ცვლილებების ჟურნალის ნახვის უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები).";

    public static class Filters
    {
        public const string From = "თარიღიდან";
        public const string To = "თარიღამდე";
        public const string Table = "ცხრილი";
        public const string User = "მომხმარებელი";
        public const string Any = "ყველა";
        public const string Apply = "ფილტრი";
        public const string Reset = "გასუფთავება";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("from", From),
            ("to", To),
            ("table", Table),
            ("user", User),
            ("any", Any),
            ("apply", Apply),
            ("reset", Reset));
    }

    public static class Columns
    {
        public const string At = "დრო";
        public const string Table = "ცხრილი";
        public const string Row = "ჩანაწერი";
        public const string Action = "მოქმედება";
        public const string User = "მომხმარებელი";
        public const string Changes = "ცვლილება";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("at", At),
            ("table", Table),
            ("row", Row),
            ("action", Action),
            ("user", User),
            ("changes", Changes));
    }

    public const string Next = "შემდეგი გვერდი →";
    public const string First = "← პირველი გვერდი";
    public const string Empty = "ამ ფილტრით ჩანაწერი არ მოიძებნა.";

    /// <summary>
    /// Tables by their Georgian name; any other is shown as it is.
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Tables = global::Insurance.Application.Texts.TextMap.Of(
        ("party", "კლიენტი"),
        ("policy", "პოლისი"),
        ("contract", "ხელშეკრულება"),
        ("claim", "ზარალი"),
        ("loss_event", "შემთხვევა"),
        ("claim_reserve_request", "რეზერვის მოთხოვნა"),
        ("app_user", "მომხმარებელი"),
        ("user_group", "მომხმარებლების ჯგუფი"),
        ("user_group_member", "ჯგუფის წევრი"),
        ("user_group_permission", "ჯგუფის უფლება"),
        ("user_permission", "პირადი უფლება"),
        ("setting", "პარამეტრი"),
        ("product", "პროდუქტი"),
        ("subrogation_case", "რეგრესის საქმე"),
        ("subrogation_party", "რეგრესის მხარე"),
        ("subrogation_movement", "რეგრესის მოძრაობა"),
        ("accounting_period", "საანგარიშო პერიოდი"),
        ("aml_case", "AML საქმე"),
        ("complaint", "საჩივარი"),
        ("fms_suspension", "FMS შეჩერება"),
        ("installment", "შენატანი"),
        ("insured_object", "დაზღვეული ობიექტი"),
        ("intermediary", "შუამავალი"),
        ("party_beneficial_owner", "ბენეფიციარი მესაკუთრე"),
        ("policy_line", "პოლისის დაფარვა"),
        ("product_coverage", "პროდუქტის დაფარვა"),
        ("ri_treaty", "გადაზღვევის ხელშეკრულება"),
        ("sanctions_screening", "სანქციების შემოწმება"));
}
