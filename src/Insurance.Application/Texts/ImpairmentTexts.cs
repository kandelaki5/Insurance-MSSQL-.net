// Interface texts of the impairment reserve on insurance receivables (order №02 annex 2 art. 2.50).
// Ported from src/i18n/impairment.ts (impairmentTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class ImpairmentTexts
{
    public const string Nav = "დებიტორების გაუფასურება";
    public const string Title = "სადაზღვევო დებიტორების გაუფასურების რეზერვი";
    public const string Note = "ბაზა — თითოეულ პოლისზე გამომუშავებული, მაგრამ გადაუხდელი პრემია საანგარიშო თარიღისთვის. გამომუშავებული პრემია იყოფა იმის მიხედვით, რამდენი ხნის წინ გამომუშავდა; გადახდა ჯერ ყველაზე ძველ გამომუშავებულ ნაწილს ფარავს, ამიტომ გადაუხდელად ბოლოს გამომუშავებული რჩება. რეზერვი: 1 წელზე მეტი — 100%, 9–12 თვე — 50%, 6–9 — 30%, 3–6 — 10%, 0–3 — 0% (№02 ბრძანება, დანართი 2, მუხ. 2.50). ლარში — საანგარიშო თარიღის კურსით. ჯამები დაყოფილია ფორმა N1-4-ის მიხედვით: პირდაპირი / ბროკერებისგან / აგენტებისგან.";
    public const string Date = "საანგარიშო თარიღი";
    public const string Show = "ჩვენება";
    public const string Download = "Excel-ის ჩამოტვირთვა";
    public const string None = "ამ თარიღისთვის მოთხოვნა არ არის";
    public const string TotalsTitle = "ჯამი ფორმა N1-4-ის მიხედვით";
    public const string RowsTitle = "პოლისების მიხედვით";
    public const string RatesUsed = "კურსი საანგარიშო თარიღისთვის";

    public static class Bands
    {
        public const string Over12 = "1 წელზე მეტი";
        public const string M9to12 = "9–12 თვე";
        public const string M6to9 = "6–9 თვე";
        public const string M3to6 = "3–6 თვე";
        public const string M0to3 = "0–3 თვე";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("over12", Over12),
            ("m9to12", M9to12),
            ("m6to9", M6to9),
            ("m3to6", M3to6),
            ("m0to3", M0to3));
    }

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Kinds = global::Insurance.Application.Texts.TextMap.Of(
        ("client", "სადაზღვევო მოთხოვნები (პირდაპირი)"),
        ("broker", "მოთხოვნები ბროკერებისგან"),
        ("agent", "მოთხოვნები აგენტებისგან"),
        ("all", "სულ"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Columns = global::Insurance.Application.Texts.TextMap.Of(
        ("number", "#"),
        ("policyNumber", "პოლისის ნომერი"),
        ("coverFrom", "ძალაში შესვლა"),
        ("coverTo", "დასრულება / გაუქმება"),
        ("debtorKind", "მოთხოვნის სახე (N1-4)"),
        ("intermediary", "ბროკერი / აგენტი"),
        ("policyholder", "დამზღვევი"),
        ("classes", "დაზღვევის სახეობა"),
        ("segment", "კორპორატიული / საცალო / სახელმწიფო"),
        ("insured", "დაზღვეული"),
        ("beneficiary", "მოსარგებლე"),
        ("premium", "პრემია (ხელშეკრულების ვალუტით)"),
        ("currency", "ვალუტა"),
        ("earned", "გამომუშავებული"),
        ("earnedTotal", "სულ გამომუშავებული"),
        ("paid", "გადახდილი პრემია"),
        ("unpaidPremium", "გადაუხდელი პრემია სულ (N1-4)"),
        ("rate", "კურსი"),
        ("unpaidPremiumGel", "გადაუხდელი პრემია, ₾"),
        ("unpaidEarned", "გადაუხდელი გამომუშავებული"),
        ("unpaid", "გადაუხდელი"),
        ("unpaidEarnedGel", "გადაუხდელი გამომუშავებული, ₾"),
        ("reserve", "რეზერვი"),
        ("reserveGel", "რეზერვი, ₾"),
        ("policies", "პოლისები"));
}
