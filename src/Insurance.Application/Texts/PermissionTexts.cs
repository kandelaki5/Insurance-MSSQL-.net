// Interface texts of the permissions catalogue and of where a user's permission comes from (for the future admin panel).
// Ported from src/i18n/permissions.ts (permissionTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class PermissionTexts
{
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Permissions = global::Insurance.Application.Texts.TextMap.Of(
        ("premium.receipt.create", "პრემიის მიღება"),
        ("claim.recovery.receive", "რეგრესის / გადარჩენილი ქონების თანხის მიღება"),
        ("claim.payment.record", "ზარალის გადახდის შეყვანა"),
        ("claim.payment.reverse", "ზარალის გადახდის შებრუნება"),
        ("claim.reserve.approve", "რეზერვის ცვლილების დამტკიცება"),
        ("audit.log.view", "ცვლილებების ჟურნალის ნახვა"),
        ("payment.large.authorize", "ზღვარზე მეტი გადახდის ავტორიზაცია (ფინანსური დირექტორი)"),
        ("finance.panel.view", "ფინანსური დირექტორის პანელის ნახვა"),
        ("policy.approve", "პოლისის დამტკიცება"),
        ("accounting.period.close", "თვის დახურვა"),
        ("reinsurance.commission.settle", "გადამზღვევლის საკომისიოს ანგარიშსწორება"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Sections = global::Insurance.Application.Texts.TextMap.Of(
        ("premiums", "პრემიები"),
        ("claims", "ზარალები"),
        ("policies", "პოლისები"),
        ("periods", "საანგარიშო პერიოდები"),
        ("audit", "აუდიტი"),
        ("finance", "ფინანსები"),
        ("reinsurance", "გადაზღვევა"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Sources = global::Insurance.Application.Texts.TextMap.Of(
        ("role", "როლიდან"),
        ("group", "ჯგუფიდან"),
        ("not-in-role", "როლში არ შედის"),
        ("granted", "ინდივიდუალურად მინიჭებული"),
        ("revoked", "ინდივიდუალურად მოხსნილი"),
        ("invalid", "უცნობი / არავალიდური"));

    /// <summary>
    /// The answers of the admin's changes to permissions, groups and memberships.
    /// </summary>
    public static class Changes
    {
        public const string Saved = "შენახულია";
        public const string Unchanged = "ცვლილება არ იყო — უკვე ასეა";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Refusals = global::Insurance.Application.Texts.TextMap.Of(
            ("invalid-id", "მომხმარებელი ან ჯგუფი არასწორადაა მითითებული"),
            ("unknown-permission", "ასეთი უფლება სისტემაში არ არსებობს"),
            ("invalid-decision", "არჩევანი არასწორია"),
            ("missing-user", "ასეთი მომხმარებელი არ არსებობს"),
            ("missing-group", "ასეთი ჯგუფი არ არსებობს"),
            ("invalid-name", "ჯგუფის სახელი ცარიელია ან 100 სიმბოლოზე გრძელია"),
            ("duplicate-name", "ამ სახელით ჯგუფი უკვე არსებობს"));
    }

    /// <summary>
    /// The admin's permissions panel (/users/permissions).
    /// </summary>
    public static class Panel
    {
        public const string Title = "უფლებები";
        public const string Link = "უფლებები და ჯგუფები";
        public const string NotYet = "„პრემიის მიღება“ მართავს ახალი გადახდის რეგისტრაციას პოლისზე, „რეგრესის / გადარჩენილი ქონების თანხის მიღება“ — ზარალზე მიღებული თანხის რეგისტრაციას, „ზარალის გადახდის შეყვანა“ — ზარალის გადახდის რეგისტრაციას, „პოლისის დამტკიცება“ — დასამტკიცებელი პოლისის ძალაში შეყვანას, „თვის დახურვა“ — უძველესი დასრულებული ღია თვის დახურვას: ვისაც უფლება არ აქვს, ვერ შეასრულებს. საკუთარ გაფორმებულ პოლისს არაადმინისტრატორი ვერ დაამტკიცებს, უფლების მიუხედავად. სხვა ოპერაციები (წინა გადახდის ნაშთის გამოყენება, ზედმეტობის გადატანა, ცვლილება, გაუქმება, ზარალის რეგისტრაცია, ზარალის დამტკიცება, რეზერვი) ჯერ ისევ როლის ძველი წესით მოწმდება.";
        public const string Users = "მომხმარებელი";
        public const string Search = "სახელი ან მომხმარებლის სახელი";
        public const string Find = "ძებნა";
        public const string NoUsers = "მომხმარებელი ვერ მოიძებნა";
        public const string Choose = "არჩევა";
        public const string Username = "მომხმარებლის სახელი";
        public const string FullName = "სახელი, გვარი";
        public const string Role = "როლი";
        public const string Inactive = "გამორთული";
        public const string MissingUser = "ასეთი მომხმარებელი არ არსებობს";
        public const string Permission = "უფლება";
        public const string RoleDefault = "როლის საწყისი";
        public const string FromGroups = "ჯგუფებიდან";
        public const string Own = "ინდივიდუალური არჩევანი";
        public const string Result = "საბოლოოდ";
        public const string Source = "წყარო";
        public const string Yes = "კი";
        public const string No = "არა";
        public const string Allowed = "დაშვებულია";
        public const string Refused = "არ არის დაშვებული";
        public const string None = "—";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Choices = global::Insurance.Application.Texts.TextMap.Of(
            ("inherit", "მემკვიდრეობით"),
            ("allow", "მინიჭებული"),
            ("deny", "მოხსნილი"));

        public const string Save = "შენახვა";
        public const string Saving = "ინახება…";
        public const string Memberships = "ჯგუფები, რომლებშიც არის";
        public const string Member = "წევრია";
        public const string NotMember = "წევრი არ არის";
        public const string AddTo = "დამატება";
        public const string RemoveFrom = "ამოღება";
        public const string Groups = "მომხმარებელთა ჯგუფები";
        public const string NoGroups = "ჯგუფი ვერ მოიძებნა";
        public const string GroupSearch = "ჯგუფის სახელი";
        public const string NewGroup = "ახალი ჯგუფის სახელი";
        public const string Create = "შექმნა";
        public const string MissingGroup = "ასეთი ჯგუფი არ არსებობს";
        public const string GroupName = "ჯგუფის სახელი";
        public const string Rename = "სახელის შეცვლა";
        public const string GroupPermissions = "ჯგუფის საწყისი უფლებები";
        public const string Ticked = "მონიშნულია";
        public const string NotTicked = "მონიშნული არ არის";
        public const string Tick = "მონიშვნა";
        public const string Untick = "მოხსნა";
        public const string Members = "წევრები";
        public const string NoMembers = "წევრი ჯერ არ არის";
    }
}
