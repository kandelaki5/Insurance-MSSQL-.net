// Settings and product texts the administrator edits, with their Georgian labels, in display order.
// Ported from src/app/(app)/settings/keys.ts and src/app/(app)/settings/products/keys.ts; every text is byte-for-byte the old one.
namespace Insurance.Application.Texts.Screens;

// Not under src/i18n in the old program (the settings screens kept them), so outside the text classes compared with it.

public static class SettingKeys
{
    /// <summary>Settings editable by the administrator, in display order, with their Georgian labels.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = TextMap.Of(
        ("company_name", "კომპანიის დასახელება"),
        ("company_id_number", "საიდენტიფიკაციო კოდი"),
        ("company_address", "მისამართი"),
        ("company_phone", "ტელეფონი"),
        ("company_email", "ელფოსტა"),
        ("company_licence", "ლიცენზია"),
        ("complaints_unit", "პრეტენზიების განმხილველი ერთეული"),
        ("complaints_channels", "პრეტენზიის წარდგენის ფორმები"),
        ("complaints_response_days", "პრეტენზიაზე პასუხის მაქსიმალური ვადა (დღე)"),
        ("regulator_name", "საზედამხედველო ორგანო"),
        ("regulator_address", "საზედამხედველო ორგანოს მისამართი"),
        ("aml.high_risk_countries", "მაღალი რისკის იურისდიქციები (ქვეყნის კოდები, მძიმით)"),
        ("aml.premium_point_one_gel", "წლიური პრემია, რომლიდანაც 1 ქულა (ლარი)"),
        ("aml.premium_point_two_gel", "წლიური პრემია, რომლიდანაც 2 ქულა (ლარი)"),
        ("aml.sanctions_check_max_age_days", "სანქციების შემოწმების მოქმედების ვადა (დღე)"),
        ("aml.medium_score", "საშუალო რისკის ქულა (-დან)"),
        ("aml.high_score", "მაღალი რისკის ქულა (-დან)"),
        ("aml.review_months_high", "გადახედვის ინტერვალი — მაღალი (თვე)"),
        ("aml.review_months_medium", "გადახედვის ინტერვალი — საშუალო (თვე)"),
        ("aml.review_months_low", "გადახედვის ინტერვალი — დაბალი (თვე)"),
        ("accounting.close_within_days", "თვის დახურვის ვადა თვის დასრულებიდან (კალენდარული დღე, 1–31)"),
        ("payment.fd_threshold_gel", "ერთი გადახდა, რომლის ზემოთაც ფინანსური დირექტორის ავტორიზაციაა საჭირო (ლარი)"),
        ("policy.reduction_order", "ცვლილებით შემცირებული პრემიის განაწილება გრაფიკზე"),
        // H46 (owner 06.10.2026, old program docs/07 §8.4); wording proposed by Claude, not approved by the owner.
        ("claim.currency", "ზარალის ვალუტა უცხო ვალუტის პოლისზე"));

    /// <summary>Settings chosen from a fixed list (the first value is the default when nothing is saved).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ChoiceKeys = TextMap.Of<IReadOnlyList<string>>(
        // Owner, 07.10.2026: either the last instalments shrink, or the reduction is split equally over the unpaid ones.
        ("policy.reduction_order", ["last_first", "equal_unpaid"]),
        // H46: a new claim on a USD/EUR policy is in GEL (owner, 06.10.2026: "ზარალი აღირიცხება ლარში"), or in the policy's
        // currency as the old program recorded it. Read when a claim is registered; a claim keeps its currency.
        ("claim.currency", ["gel", "policy"]));

    /// <summary>Settings holding a whole number of days, checked on save (CloseDeadline).</summary>
    public static readonly IReadOnlySet<string> DayKeys = new HashSet<string>(StringComparer.Ordinal) { "accounting.close_within_days" };

    /// <summary>Settings holding an amount in GEL (up to 13 digits and 2 decimals), checked on save (Settings.ParseThreshold).</summary>
    public static readonly IReadOnlySet<string> AmountKeys = new HashSet<string>(StringComparer.Ordinal) { "payment.fd_threshold_gel" };

    /// <summary>Settings holding a phone number: entered as country code + number (Phone).</summary>
    public static readonly IReadOnlySet<string> PhoneKeys = new HashSet<string>(StringComparer.Ordinal) { "company_phone" };

    /// <summary>Settings that may legitimately be blank (a blank is saved, not ignored).</summary>
    public static readonly IReadOnlySet<string> BlankAllowed = new HashSet<string>(StringComparer.Ordinal) { "aml.high_risk_countries" };

    /// <summary>
    /// Number series the administrator may set (prefix and digits), with the table holding their documents; policy series
    /// ('policy:&lt;product&gt;') are added per product and kept in core.policy.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SeriesTables = TextMap.Of(
        ("contract", "contract"),
        ("claim", "claim"),
        ("complaint", "complaint"),
        ("aml_case", "aml_case"),
        ("treaty", "ri_treaty"));

    /// <summary>Texts of the printed policy, per product, in print order (keys fixed by the print template).</summary>
    public static readonly IReadOnlyDictionary<string, string> ProductTextLabels = TextMap.Of(
        ("terms_version", "დაზღვევის პირობების დასახელება და ვერსია"),
        ("documents", "ხელშეკრულების შემადგენელი დოკუმენტები"),
        ("start_condition", "ძალაში შესვლის და დასრულების პირობა"),
        ("claims_procedure", "ზარალის შეტყობინებისა და რეგულირების წესი და ვადები"),
        ("disclosure_duty", "ინფორმაციის მიწოდების ვალდებულება და მისი დარღვევის შედეგი"),
        ("withdrawal", "ხელშეკრულებიდან გასვლის პირობები"),
        ("termination", "ვადამდე შეწყვეტა და დაბრუნება"),
        ("exclusions", "გამონაკლისების სრული ჩამონათვალი"));
}
