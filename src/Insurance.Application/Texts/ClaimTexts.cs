// Interface texts of the claims screens.
// Ported from src/i18n/claims.ts (claimTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class ClaimTexts
{
    public const string Nav = "ზარალები";
    public const string Title = "ზარალები";
    public const string New = "ზარალის რეგისტრაცია";
    public const string Register = "რეგისტრაცია";
    public const string AddCoverage = "დაფარვის დამატება ამ შემთხვევაზე";
    public const string Registered = "ზარალი დარეგისტრირდა";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Status = global::Insurance.Application.Texts.TextMap.Of(
        ("registered", "განცხადებული"),
        ("in_review", "განხილვაში"),
        ("approved", "დამტკიცებული"),
        ("paid", "ანაზღაურებული"),
        ("rejected", "უარყოფილი"),
        ("closed", "დახურული"));

    public static class Sections
    {
        public const string Facts = "ზარალის მონაცემები";
        public const string Reserve = "რეზერვი (RBNS)";
        public const string Decision = "გადაწყვეტილება";
        public const string Payments = "ანაზღაურებები";
        public const string Recoveries = "რეგრესი და გადარჩენილი ქონება";
        public const string Event = "შემთხვევა — ზარალები სახეობებით";

        public static string EventCount(int classes) =>
            string.Concat("ზარალების რაოდენობა (სახეობებით): ", classes.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
    }

    public static class Fields
    {
        public const string Number = "ზარალის ნომერი";
        public const string PolicyNumber = "პოლისის ნომერი";
        public const string Policy = "პოლისი";
        public const string Policyholder = "დამზღვევი";
        public const string Object = "დაზღვევის საგანი";
        public const string Coverage = "დაფარვა";
        public const string OccurredOn = "შემთხვევის თარიღი";
        public const string OccurredPlace = "შემთხვევის ადგილი";
        public const string ReportedOn = "განცხადების თარიღი";
        public const string Description = "გარემოებები";
        public const string ClaimedAmount = "განცხადებული თანხა";
        public const string InitialReserve = "პირველადი რეზერვი";
        public const string ReserveHint = "რეზერვების წესის მიხედვით განცხადებისთანავე იქმნება: განცხადებული თანხა, ან გონივრული მაქსიმუმი, არაუმეტეს სადაზღვევო თანხისა.";
        public const string SumInsured = "სადაზღვევო თანხა / ლიმიტი";
        public const string Deductible = "ფრანშიზა";
        public const string Status = "სტატუსი";
        public const string ClassCode = "სახეობა";
        public const string CurrentReserve = "მიმდინარე რეზერვი";
        public const string PaidTotal = "სულ ანაზღაურებული";
        public const string ApprovedAmount = "ანაზღაურების თანხა";
        public const string IsTotalLoss = "სრული განადგურება";
        public const string RejectionReason = "უარის მიზეზი";
        public const string BookedOn = "თარიღი";
        public const string Change = "ცვლილება";
        public const string NewLevel = "ახალი რეზერვი";
        public const string Reason = "მიზეზი";
        public const string Amount = "თანხა";
        public const string DeductibleWithheld = "დაკავებული ფრანშიზა";
        public const string PremiumOffset = "ურთიერთჩათვლა პრემიასთან";
        public const string NetPaid = "გასაცემი";
        public const string Payee = "მიმღები";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> PayeeKinds = global::Insurance.Application.Texts.TextMap.Of(
            ("policyholder", "დამზღვევი"),
            ("beneficiary", "მოსარგებლე"),
            ("workshop", "სერვისი"),
            ("other", "სხვა"));

        public const string PayeeIdNumber = "მიმღების პირადი ნომერი / საიდენტიფიკაციო კოდი";
        public const string TotalLossMode = "სრული განადგურების წესი";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> TotalLossModes = global::Insurance.Application.Texts.TextMap.Of(
            ("earned_unpaid", "პოლისი უქმდება; ზარალს აკლდება გამომუშავებული გადაუხდელი პრემია"),
            ("all_unpaid", "ზარალს აკლდება მთელი გადაუხდელი პრემია; პრემია სრულად რჩება კომპანიას"),
            ("none", "პოლისი უქმდება გამოკლების გარეშე"));

        public const string Note = "შენიშვნა";
        public const string RecoveryKind = "სახე";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> RecoveryKinds = global::Insurance.Application.Texts.TextMap.Of(
            ("subrogation", "რეგრესი"),
            ("salvage", "გადარჩენილი ქონება"));

        public const string ReceivedOn = "მიღების თარიღი";
        public const string AmountGel = "თანხა, ₾";
        public const string PaymentKind = "სახე";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> PaymentKinds = global::Insurance.Application.Texts.TextMap.Of(
            ("payment", "ანაზღაურება"),
            ("reversal", "შებრუნება"));

        public const string ReversalReason = "შებრუნების მიზეზი";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ReversalReasons = global::Insurance.Application.Texts.TextMap.Of(
            ("correction", "შესწორება — თანხა არ გასულა ან შეცდომით ჩაიწერა"),
            ("returned", "მიმღებმა თანხა დააბრუნა"));

        public const string ReversedOn = "შებრუნების თარიღი";
        public const string ReturnedOn = "დაბრუნების თარიღი";
        public const string BankReference = "ბანკის reference";

        public static string Reverses(string on) =>
            string.Concat("აბრუნებს ", on, "-ის ანაზღაურებას");

        public static string ReversedBy(string on) =>
            string.Concat("შებრუნდა ", on, "-ში");
    }

    public static class Actions
    {
        public const string StartReview = "განხილვის დაწყება";
        public const string Approve = "დამტკიცება";
        public const string Reject = "უარყოფა";
        public const string Close = "ზარალის დახურვა";
        public const string Reopen = "ხელახლა გახსნა";
        public const string AddReserve = "რეზერვის ცვლილების მოთხოვნა";
        public const string ApproveReserve = "დამტკიცება";
        public const string Pay = "ანაზღაურების რეგისტრაცია";
        public const string AddRecovery = "მიღების რეგისტრაცია";
        public const string Reverse = "ანაზღაურების შებრუნება";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("startReview", StartReview),
            ("approve", Approve),
            ("reject", Reject),
            ("close", Close),
            ("reopen", Reopen),
            ("addReserve", AddReserve),
            ("approveReserve", ApproveReserve),
            ("pay", Pay),
            ("addRecovery", AddRecovery),
            ("reverse", Reverse));
    }

    public static class Notes
    {
        public const string Reserve = "ყოველი ცვლილება თარიღით და მიზეზით ინახება; შეცვლა ან წაშლა არ ხდება. ანაზღაურება რეზერვს ავტომატურად ამცირებს.";
        public const string ReserveRequest = "რეზერვის ცვლილება ჯერ მოთხოვნაა: რეზერვი იცვლება მხოლოდ მაშინ, როცა მას დაამტკიცებს სხვა თანამშრომელი, ვისაც ამის უფლება აქვს. საკუთარ მოთხოვნას ვერავინ დაამტკიცებს.";
        public const string Payment = "თანხა პოლისის ვალუტაშია; გასაცემი = თანხა − ფრანშიზა − პრემიასთან ჩათვლა. ლარში გადადის ანაზღაურების დღის კურსით.";
        public const string Offset = "ჩათვლისას პოლისის დავალიანება იფარება ზარალის თანხიდან, ყველაზე ძველი შენატანიდან.";
        public const string Reversal = "შებრუნება ახალი სტრიქონია: თავდაპირველი ანაზღაურება არ იცვლება. ყველა თანხა ზუსტად საპირისპიროა, იგივე კურსით; რეზერვი აღდგება, პრემიასთან ჩათვლა უკან ბრუნდება (პოლისის დავალიანება აღდგება). ბანკში არაფერი იგზავნება — მხოლოდ ფაქტი აღირიცხება.";
        public const string Payee = "დამზღვევის გარდა სხვა მიმღები (მოსარგებლე, სერვისი, სხვა) კლიენტების რეესტრიდან აირჩევა მისი ნომრით — ჯერ რეესტრში უნდა იყოს დამატებული.";

        // H46 (old program docs/07 §4.5, owner 06.10.2026): a GEL claim on a foreign-currency policy. Wording proposed by
        // Claude, not approved by the owner (tests/parity/cases/common-texts-after-baseline.json).
        public const string PaymentGel = "თანხა ლარშია; გასაცემი = თანხა − ფრანშიზა − პრემიასთან ჩათვლა. პრემიის დავალიანება პოლისის ვალუტაშია: ჩათვლა მასში ანაზღაურების დღის კურსით იანგარიშება, ცენტამდე ქვემოთ დამრგვალებით.";
        public const string GelOnForeign = "ზარალი აღირიცხება ლარში. სადაზღვევო თანხა და ფრანშიზა პოლისის ვალუტაშია და ლარში გადაიყვანება შემთხვევის დღის ოფიციალური კურსით.";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("reserve", Reserve),
            ("reserveRequest", ReserveRequest),
            ("payment", Payment),
            ("offset", Offset),
            ("reversal", Reversal),
            ("payee", Payee));
    }

    /// <summary>
    /// The answer when the user may not record a claim's payment (claim.payment.record).
    /// </summary>
    public const string PaymentNotAllowed = "ზარალის გადახდის შეყვანის უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები).";

    /// <summary>
    /// The answer when the user may not reverse a claim's payment (claim.payment.reverse).
    /// </summary>
    public const string ReversalNotAllowed = "ზარალის გადახდის შებრუნების უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები).";

    /// <summary>
    /// What a reversal ended in (reverseClaimPayment), by its outcome.
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ReversalOutcomes = global::Insurance.Application.Texts.TextMap.Of(
        ("ok", "ანაზღაურება შებრუნდა"),
        ("invalid", "შებრუნება არ გატარდა: შეავსეთ მიზეზი და თარიღი (დაბრუნებისას — დაბრუნების თარიღი და ბანკის reference)"),
        ("before", "შებრუნების თარიღი ანაზღაურების თარიღზე ადრე ვერ იქნება"),
        ("returned-before", "დაბრუნების თარიღი ანაზღაურების თარიღზე ადრე ვერ იქნება"),
        ("already", "ეს ანაზღაურება უკვე შებრუნებულია"),
        ("state", "შებრუნება შესაძლებელია მხოლოდ დამტკიცებულ ან ანაზღაურებულ ზარალზე; დახურული ზარალი ჯერ ხელახლა გახსენით"),
        ("period-closed", "ეს თვე ბუღალტრულად დახურულია; მიუთითეთ ღია თვის თარიღი"),
        ("snapshot", "ამ ანაზღაურებას ჩათვლის ქვითრის კავშირი არ აქვს — შებრუნება ავტომატურად ვერ გატარდება, საჭიროა ხელით განხილვა"),
        ("offset-reversed", "ამ ანაზღაურების ჩათვლის ქვითარი უკვე ცალკე შებრუნებულია — შებრუნება ავტომატურად ვერ გატარდება, საჭიროა ხელით განხილვა"),
        ("regress", "შებრუნების შემდეგ რეგრესით მოთხოვნილი თანხა ანაზღაურებულს გადააჭარბებდა — ჯერ რეგრესის საქმე შეასწორეთ"),
        ("suspended", "კლიენტზე მოქმედებს შეჩერება — შებრუნება არ გატარდა; მიმართეთ AML-ის პასუხისმგებელ პირს"),
        ("payee", "ამ მიმღებზე ოპერაცია ვერ გატარდება — მიმართეთ AML-ის პასუხისმგებელ პირს"),
        ("not-allowed", "ზარალის გადახდის შებრუნების უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები)."));

    /// <summary>
    /// The reserve's requests on the claim page (changeReserve, approveReserveRequest).
    /// </summary>
    public static class ReserveRequests
    {
        public const string Title = "რეზერვის ცვლილების მოთხოვნები";
        public const string RequestedBy = "მოითხოვა";
        public const string From = "იყო";
        public const string To = "მოთხოვნილი";
        public const string Pending = "ელოდება დამტკიცებას";
        public const string Stale = "მოძველდა — რეზერვი მოთხოვნის შემდეგ შეიცვალა; საჭიროა ახალი მოთხოვნა";

        public static string Approved(string by) =>
            string.Concat("დაამტკიცა: ", by);

        public const string Own = "თქვენი მოთხოვნაა — სხვამ უნდა დაამტკიცოს";
    }

    /// <summary>
    /// What a reserve request or its approval ended in, by its outcome.
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ReserveOutcomes = global::Insurance.Application.Texts.TextMap.Of(
        ("requested", "მოთხოვნა გაიგზავნა — რეზერვი შეიცვლება დამტკიცების შემდეგ"),
        ("unchanged", "მოთხოვნილი რეზერვი ახლანდელს ემთხვევა — მოთხოვნა არ შეიქმნა"),
        ("invalid", "მიუთითეთ თარიღი, ახალი რეზერვი (არაუარყოფითი, მაქსიმუმ 2 ათწილადი) და მიზეზი"),
        ("not-allowed", "ამ ზარალზე ცვლილების შეტანის უფლება არ გაქვთ"),
        ("approved", "რეზერვის ცვლილება დამტკიცდა და აისახა"),
        ("approve-not-allowed", "რეზერვის ცვლილების დამტკიცების უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები)."),
        ("own", "საკუთარ მოთხოვნას ვერ დაამტკიცებთ — ის სხვამ უნდა დაამტკიცოს"),
        ("already", "ეს მოთხოვნა უკვე დამტკიცებულია"),
        ("stale", "რეზერვი მოთხოვნის შემდეგ შეიცვალა — მოთხოვნა მოძველდა; საჭიროა ახალი მოთხოვნა"),
        ("state", "ეს მოქმედება ამ ზარალის სტატუსში ან ამ მოთხოვნაზე დაუშვებელია"),
        ("period-closed", "მოთხოვნის თარიღის თვე ბუღალტრულად დახურულია — რეზერვი სხვა თარიღით არ გატარდება; საჭიროა ახალი მოთხოვნა ღია თვის თარიღით"));

    /// <summary>
    /// Claim payments above the company's threshold, waiting for the financial director (payClaim, approveClaimPaymentRequest).
    /// </summary>
    public static class PaymentRequests
    {
        public const string Title = "ფინანსური დირექტორის ავტორიზაციას დაქვემდებარებული გადახდები";
        public const string Note = "ნეტო თანხა ლარში ზღვარზე მეტია, ამიტომ გადახდა ჯერ არ შესრულებულა: არც გადახდა, არც რეზერვის ცვლილება, არც პრემიის გადაფარვა არ ჩაწერილა. ავტორიზაციისას თანხა ხელახლა დაითვლება და ჩაიწერება მხოლოდ იგივე თანხით.";
        public const string RequestedBy = "მოითხოვა";
        public const string Gross = "ბრუტო";
        public const string NetGel = "ნეტო, ლარი";
        public const string Pending = "ელოდება ავტორიზაციას";

        public static string Approved(string by) =>
            string.Concat("ავტორიზაცია: ", by);

        public const string Approve = "ავტორიზაცია და გადახდა";
    }

    /// <summary>
    /// What a claim payment request or its approval ended in, by its outcome.
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> PaymentOutcomes = global::Insurance.Application.Texts.TextMap.Of(
        ("requested", "გადახდა ჯერ არ შესრულებულა: ნეტო თანხა ზღვარზე მეტია და ფინანსური დირექტორის ავტორიზაციას საჭიროებს. მოთხოვნა შენახულია."),
        ("approved", "ავტორიზაცია გაიცა — გადახდა ჩაიწერა"),
        ("already", "ეს მოთხოვნა უკვე შესრულებულია"),
        ("stale", "მოთხოვნის შემდეგ თანხა ან პირობები შეიცვალა (ფრანშიზა, პრემიის დავალიანება, კურსი, მიმღები) — არაფერი ჩაწერილა; საჭიროა ახალი მოთხოვნა"),
        ("not-allowed", "ზღვარზე მეტი გადახდის ავტორიზაციის უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები)."),
        ("state", "ეს მოქმედება ამ ზარალის სტატუსში ან ამ მოთხოვნაზე დაუშვებელია"),
        ("exceeds", "გადახდა დამტკიცებულ თანხას გადააჭარბებდა — არაფერი ჩაწერილა"),
        ("rate", "გადახდის დღის კურსი არ მოიძებნა — არაფერი ჩაწერილა"),
        ("suspended", "კლიენტზე მოქმედებს შეჩერება — გადახდა არ შესრულდა; მიმართეთ AML-ის პასუხისმგებელ პირს"),
        ("payee", "ამ მიმღებზე ანაზღაურება ვერ გაიცემა — მიმართეთ AML-ის პასუხისმგებელ პირს"),
        ("deductible", "ამ სახეობის დაფარვებს შემთხვევაში განსხვავებული ფრანშიზა აქვს — გადახდა შეჩერებულია, სანამ წესი დაზუსტდება"),
        ("period-closed", "გადახდის თარიღის თვე ბუღალტრულად დახურულია — საჭიროა ახალი მოთხოვნა ღია თვის თარიღით"),
        ("total-loss", "პოლისის გვიანდელი გაუქმება სრული დაღუპვის დღეზე ვერ გადმოვიდა — გადახდა არ შესრულდა"));

    /// <summary>
    /// The answer when the user may not record a recovery (claim.recovery.receive).
    /// </summary>
    public const string RecoveryNotAllowed = "რეგრესის / გადარჩენილი ქონების თანხის მიღების უფლება არ გაქვთ. უფლებას ადმინისტრატორი ანიჭებს (მომხმარებლები → უფლებები და ჯგუფები).";

    public static class Errors
    {
        public const string Policy = "ამ ნომრით მოქმედი პოლისი ვერ მოიძებნა";
        public const string Coverage = "აირჩიეთ დაფარვა";
        public const string CoverageTwice = "ეს დაფარვა ამ შემთხვევაში უკვე არის";
        public const string Payee = "ამ ნომრით პირი კლიენტების რეესტრში არ არის — ჯერ დაამატეთ რეესტრში";

        // General on purpose: the operator is not told whether it is a sanction or an FMS suspension.
        public const string PayeeBlocked = "ამ მიმღებზე ანაზღაურება ვერ გაიცემა — მიმართეთ AML-ის პასუხისმგებელ პირს";

        public const string DeductibleDiffers = "ამ სახეობის დაფარვებს შემთხვევაში განსხვავებული ფრანშიზა აქვს — გადახდა შეჩერებულია, სანამ წესი დაზუსტდება";
        public const string OccurredOn = "შემთხვევის თარიღი პოლისის მოქმედების ფარგლებში უნდა იყოს";
        public const string ReportedOn = "განცხადების თარიღი შემთხვევის თარიღზე ადრე ვერ იქნება";
        public const string Description = "აღწერეთ გარემოებები";
        public const string Reserve = "მიუთითეთ რეზერვი, არაუმეტეს სადაზღვევო თანხისა";
        public const string Amount = "მიუთითეთ დადებითი თანხა";
        public const string Approved = "მიუთითეთ ანაზღაურების თანხა";
        public const string Reason = "მიუთითეთ მიზეზი";
        public const string Date = "თარიღი არასწორია";
        public const string PeriodClosed = "ეს თვე ბუღალტრულად დახურულია; მიუთითეთ ღია თვის თარიღი";
        public const string State = "ეს მოქმედება ამ სტატუსში დაუშვებელია";
        public const string TotalLossCancellation = "პოლისი უკვე გაუქმებულია უფრო გვიანი თარიღიდან და გაუქმება ზარალის დღეზე ვერ გადმოვიდა; ანაზღაურება არ გატარდა";
        public const string Exceeds = "ანაზღაურება დამტკიცებულ თანხას აჭარბებს";
        public const string Rate = "ამ თარიღის კურსი ვერ მოიძებნა";

        // H46 (old program docs/07 §4.5, owner 06.10.2026): refusals where no owner rule decides the case yet. Wording proposed
        // by Claude, not approved by the owner (tests/parity/cases/common-texts-after-baseline.json).
        public const string CurrencyMixed = "ამ შემთხვევის ზარალები სხვა ვალუტაშია: ერთ შემთხვევაში ლარისა და სხვა ვალუტის ზარალის აღრიცხვის წესი ჯერ დასადასტურებელია — არაფერი ჩაწერილა";
        public const string TotalLossCurrency = "ზარალი ლარშია, პოლისი კი სხვა ვალუტაში: სრული განადგურებისას პრემიის დაკავების წესი ასეთ ზარალზე ჯერ დასადასტურებელია — ანაზღაურება არ გატარდა";
        public const string XlCurrency = "ზარალი ლარშია, დაფარვა კი სხვა ვალუტაში: ზარალის ექსცედენტის (XL) ხელშეკრულების გამოყენების წესი ასეთ ზარალზე ჯერ დასადასტურებელია — ზარალი არ დარეგისტრირდა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("policy", Policy),
            ("coverage", Coverage),
            ("coverageTwice", CoverageTwice),
            ("payee", Payee),
            ("payeeBlocked", PayeeBlocked),
            ("deductibleDiffers", DeductibleDiffers),
            ("occurredOn", OccurredOn),
            ("reportedOn", ReportedOn),
            ("description", Description),
            ("reserve", Reserve),
            ("amount", Amount),
            ("approved", Approved),
            ("reason", Reason),
            ("date", Date),
            ("periodClosed", PeriodClosed),
            ("state", State),
            ("totalLossCancellation", TotalLossCancellation),
            ("exceeds", Exceeds),
            ("rate", Rate));
    }
}
