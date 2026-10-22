// Interface texts of the premium report by policy and insurance class (the owner's NBG list layout, docs/04 r1–r8).
// Ported from src/i18n/policy-premium.ts (policyPremiumTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class PolicyPremiumTexts
{
    public const string Nav = "პრემია პოლისების მიხედვით";
    public const string Title = "პრემია პოლისების მიხედვით";
    public const string Note = "ლარში, თითო პოლისის თითო სახეობაზე, პრემიის ჟურნალიდან (თითო ჩანაწერი თავისი ფიქსირებული კურსით). სიაში მხოლოდ ის სტრიქონებია, რომლებსაც პერიოდში თანხა აქვთ (მოზიდული, გამომუშავებული ან UPR პერიოდის ბოლოს) ან პერიოდში გაუქმდა. სტატისტიკური პრემია — პოლისის პრემია, თუ პოლისი პერიოდში მოქმედებდა (გაუქმებამდე). მოზიდული (Corrected) — პერიოდში აღრიცხული პრემია: პერიოდში დაწყებულ პოლისზე პრემია (გაუქმებისას მხოლოდ შენარჩუნებული ნაწილი), წინა პერიოდის პოლისის გაუქმებისას მინუს დასაბრუნებელი. Correction — წინა პერიოდში დაწყებული და ამ პერიოდში გაუქმებული პოლისის დასაბრუნებელი ნაწილი. გაუქმების დღე გამომუშავებულად არ ითვლება. ცვლილება, რომელიც ცალკე პოლისად გაიცა, ცალკე სტრიქონია (როგორც NBG list-ის „Is Modification“): დაწყება — შეცვლილი პოლისის დაწყება, ცვლილების თარიღი — ცვლილების. გადაზღვევა: პრემია — პერიოდში გატარებული (გაუქმების დაბრუნებით); გამომუშავებული და გამოუმუშავებელი — დღეების პროპორციით, ისევე როგორც პრემია; საკომისიოც დღეების პროპორციით.";
    public const string From = "პერიოდის დასაწყისი";
    public const string To = "პერიოდის დასასრული";
    public const string Show = "ჩვენება";
    public const string Download = "Excel-ის ჩამოტვირთვა";
    public const string None = "ამ პერიოდში პრემიის მოძრაობა არ არის";
    public const string Total = "სულ";
    public const string Yes = "კი";
    public const string No = "არა";

    public static string Rows(int n) =>
        string.Concat("სტრიქონები: ", n.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

    public static class Columns
    {
        public const string ContractNumber = "ხელშეკრულების ნომერი";
        public const string PolicyNumber = "პოლისის ნომერი";
        public const string Policyholder = "დამზღვევი";
        public const string IsModification = "ცვლილება";
        public const string ClassCode = "სახეობის კოდი";
        public const string ClassName = "სახეობა";
        public const string Currency = "ვალუტა";
        public const string Premium = "პრემია (ვალუტით)";
        public const string Statistical = "სტატისტიკური პრემია (ლარი)";
        public const string Correction = "Correction (ლარი)";
        public const string Corrected = "მოზიდული პრემია / Corrected (ლარი)";
        public const string StartDate = "დაწყება";
        public const string ChangeDate = "ცვლილების თარიღი";
        public const string EndDate = "დასასრული";
        public const string CancelDate = "გაუქმების თარიღი";
        public const string Earned = "გამომუშავებული (ლარი)";
        public const string Unearned = "UPR პერიოდის ბოლოს (ლარი)";
        public const string RiPremium = "გადაზღვევის პრემია (ლარი)";
        public const string RiEarned = "გადაზღვევის გამომუშავებული პრემია (ლარი)";
        public const string RiUnearned = "გადაზღვევის გამოუმუშავებელი პრემია (ლარი)";
        public const string CommissionEarned = "საკომისიო გამომუშავებული (ლარი)";
        public const string CommissionUnearned = "საკომისიო გამოუმუშავებელი (ლარი)";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("contractNumber", ContractNumber),
            ("policyNumber", PolicyNumber),
            ("policyholder", Policyholder),
            ("isModification", IsModification),
            ("classCode", ClassCode),
            ("className", ClassName),
            ("currency", Currency),
            ("premium", Premium),
            ("statistical", Statistical),
            ("correction", Correction),
            ("corrected", Corrected),
            ("startDate", StartDate),
            ("changeDate", ChangeDate),
            ("endDate", EndDate),
            ("cancelDate", CancelDate),
            ("earned", Earned),
            ("unearned", Unearned),
            ("riPremium", RiPremium),
            ("riEarned", RiEarned),
            ("riUnearned", RiUnearned),
            ("commissionEarned", CommissionEarned),
            ("commissionUnearned", CommissionUnearned));
    }
}
