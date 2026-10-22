// Interface texts of the policy screens.
// Ported from src/i18n/policy.ts (policyTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class PolicyTexts
{
    public const string Title = "პოლისები";
    public const string New = "ახალი პოლისი";
    public const string Issue = "პოლისის გაფორმება";
    public const string Issued = "პოლისი გაფორმდა";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Status = global::Insurance.Application.Texts.TextMap.Of(
        ("draft", "მონახაზი"),
        ("pending_approval", "დასამტკიცებელი"),
        ("active", "მოქმედი"),
        ("cancelled", "გაუქმებული"));

    public static class Sections
    {
        public const string Parties = "მხარეები და პროდუქტი";
        public const string Vehicle = "ავტომობილი";
        public const string Term = "ვადა და ვალუტა";
        public const string Coverages = "დაფარვები";
        public const string Sales = "გაყიდვა და გადახდა";
        public const string Finance = "ფინანსური მდგომარეობა";
        public const string Schedule = "გადახდის გრაფიკი";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("parties", Parties),
            ("vehicle", Vehicle),
            ("term", Term),
            ("coverages", Coverages),
            ("sales", Sales),
            ("finance", Finance),
            ("schedule", Schedule));
    }

    public static class Fields
    {
        public const string Number = "პოლისის ნომერი";
        public const string ContractNumber = "ხელშეკრულების ნომერი";
        public const string Policyholder = "დამზღვევი";
        public const string PolicyholderIdNumber = "დამზღვევის პირადი ნომერი ან საიდენტიფიკაციო კოდი";
        public const string Product = "პროდუქტი";
        public const string Object = "დაზღვევის საგანი";
        public const string Plate = "სახელმწიფო ნომერი";
        public const string Vin = "VIN";
        public const string Make = "მარკა";
        public const string Model = "მოდელი";
        public const string Year = "გამოშვების წელი";
        public const string VehicleType = "ტიპი";
        public const string Steering = "საჭე";
        public const string Usage = "გამოყენება";
        public const string Value = "ავტომობილის ღირებულება";
        public const string EffectiveFrom = "ძალაში შესვლა";
        public const string EffectiveTo = "დასრულება";
        public const string Term = "ვადა";
        public const string Currency = "ვალუტა";
        public const string Rate = "კურსი ძალაში შესვლის დღეს";
        public const string Coverage = "დაფარვა";
        public const string InsuranceClass = "სახეობა";
        public const string SumInsured = "სადაზღვევო თანხა / ლიმიტი";
        public const string Deductible = "ფრანშიზა";
        public const string Premium = "პრემია";
        public const string PremiumGel = "პრემია ლარში";
        public const string TotalPremium = "სულ პრემია";
        public const string Intermediary = "აგენტი / ბროკერი";
        public const string CommissionPercent = "საკომისიო, %";
        public const string Installments = "შენატანების რაოდენობა";
        public const string GraceDays = "გაუქმება გადაუხდელობისას (დღე)";
        public const string Status = "სტატუსი";
        public const string IssuedOn = "გაცემის თარიღი";
        public const string DueOn = "გადახდის ვადა";
        public const string Amount = "თანხა";
        public const string Outstanding = "გადასახდელი";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("number", Number),
            ("contractNumber", ContractNumber),
            ("policyholder", Policyholder),
            ("policyholderIdNumber", PolicyholderIdNumber),
            ("product", Product),
            ("object", Object),
            ("plate", Plate),
            ("vin", Vin),
            ("make", Make),
            ("model", Model),
            ("year", Year),
            ("vehicleType", VehicleType),
            ("steering", Steering),
            ("usage", Usage),
            ("value", Value),
            ("effectiveFrom", EffectiveFrom),
            ("effectiveTo", EffectiveTo),
            ("term", Term),
            ("currency", Currency),
            ("rate", Rate),
            ("coverage", Coverage),
            ("insuranceClass", InsuranceClass),
            ("sumInsured", SumInsured),
            ("deductible", Deductible),
            ("premium", Premium),
            ("premiumGel", PremiumGel),
            ("totalPremium", TotalPremium),
            ("intermediary", Intermediary),
            ("commissionPercent", CommissionPercent),
            ("installments", Installments),
            ("graceDays", GraceDays),
            ("status", Status),
            ("issuedOn", IssuedOn),
            ("dueOn", DueOn),
            ("amount", Amount),
            ("outstanding", Outstanding));
    }

    /// <summary>
    /// The standard payment plans (owner, 13.10.2026).
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> PaymentPlans = global::Insurance.Application.Texts.TextMap.Of(
        ("1", "1 — 12 თვე ერთად"),
        ("4", "4 — კვარტალურად"),
        ("11", "11 — პირველი და ბოლო თვე წინასწარ, დანარჩენი თვეობით"),
        ("12", "12 — ყოველთვიურად"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> VehicleTypes = global::Insurance.Application.Texts.TextMap.Of(
        ("car", "მსუბუქი"),
        ("suv", "ჯიპი"),
        ("truck", "სატვირთო"),
        ("bus", "ავტობუსი"),
        ("moto", "მოტოციკლი"),
        ("special", "სპეცტექნიკა"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> SteeringSides = global::Insurance.Application.Texts.TextMap.Of(
        ("left", "მარცხენა"),
        ("right", "მარჯვენა"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Usages = global::Insurance.Application.Texts.TextMap.Of(
        ("personal", "პირადი"),
        ("commercial", "კომერციული"),
        ("taxi", "ტაქსი"),
        ("rental", "გაქირავება"));

    public const string NoIntermediary = "პირდაპირი გაყიდვა";

    public static class Cancel
    {
        public const string Title = "პოლისის გაუქმება";
        public const string Button = "პოლისის გაუქმება";
        public const string Note = "დაფარვა წყდება გაუქმების დღის დასაწყისში: გაუქმების დღე გამომუშავებულად არ ითვლება. გამოუმუშავებელი პრემია სრულად ბრუნდება, საფასურის გარეშე; გადახდის გრაფიკი შენარჩუნებულ პრემიამდე მცირდება.";
        public const string PreviewTitle = "წინასწარი გათვლა არჩეული თარიღით";
        public const string CancelOn = "გაუქმების თარიღი";
        public const string DateHint = "პოლისის მოქმედების ფარგლებში; წარსული თარიღით გაუქმება დასაშვებია, თუ თვე ბუღალტრულად ღიაა.";
        public const string Reason = "გაუქმების მიზეზი";
        public const string Confirm = "გაუქმების დადასტურება";
        public const string Refund = "დასაბრუნებელი (გამოუმუშავებელი) პრემია";
        public const string Retained = "შენარჩუნებული პრემია";
        public const string PaidSoFar = "აქამდე გადახდილი";
        public const string ReturnToClient = "კლიენტს დასაბრუნებელი";
        public const string StillOwed = "კლიენტს დარჩენილი ვალი";
        public const string CancelledOn = "გაუქმების თარიღი";
        public const string CancelledReason = "მიზეზი";
        public const string Done = "პოლისი გაუქმებულია";
        public const string Overpaid = "კლიენტს დასაბრუნებელი თანხა";
        public const string ScheduleReason = "გრაფიკის შემცირება გაუქმებით";

        public static class Errors
        {
            public const string Date = "მიუთითეთ გაუქმების თარიღი";
            public const string Reason = "მიუთითეთ გაუქმების მიზეზი";
            public const string NotActive = "მხოლოდ მოქმედი პოლისის გაუქმებაა შესაძლებელი";
            public const string OutsideTerm = "გაუქმების თარიღი პოლისის მოქმედების ფარგლებს გარეთაა";
            public const string PeriodClosed = "ეს თვე ბუღალტრულად დახურულია; გაუქმება ღია თვის თარიღით დაარეგისტრირეთ";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("date", Date),
                ("reason", Reason),
                ("notActive", NotActive),
                ("outsideTerm", OutsideTerm),
                ("periodClosed", PeriodClosed));
        }
    }

    public static class Endorse
    {
        public const string Title = "პოლისის ცვლილება";
        public const string Button = "ცვლილება";
        public const string Note = "ცვლილება ახალი პოლისის სახით ფორმდება იმავე ხელშეკრულების ქვეშ: იწყება ცვლილების თარიღიდან, მთავრდება ძირითად პოლისთან ერთად და ატარებს მხოლოდ პრემიის სხვაობას დარჩენილი ვადისთვის. კურსი ცვლილების თარიღისაა. ძალაში შედის დამტკიცების შემდეგ.";
        public const string ChangeOn = "ცვლილების თარიღი";
        public const string DateHint = "ძირითადი პოლისის მოქმედების ფარგლებში.";
        public const string Reason = "ცვლილების შინაარსი";
        public const string CoveragesTitle = "რა იცვლება";
        public const string CoveragesNote = "მონიშნეთ დაფარვები, რომლებსაც ცვლილება ეხება. პრემიის სხვაობა არის დამატებით გადასახდელი (+) ან დასაბრუნებელი (−) თანხა დარჩენილი ვადისთვის; ლიმიტი და ფრანშიზა ახალი მნიშვნელობებია.";
        public const string Current = "ამჟამად";
        public const string NotIncluded = "არ არის დაფარული";
        public const string LimitShort = "ლიმიტი";
        public const string DeductibleShort = "ფრანშიზა";
        public const string PremiumShort = "პრემია";
        public const string NewLimit = "ახალი ლიმიტი";
        public const string NewDeductible = "ახალი ფრანშიზა";
        public const string PremiumDelta = "პრემიის სხვაობა";
        public const string TotalDelta = "სულ სხვაობა";
        public const string Submit = "ცვლილების გაფორმება";
        public const string Replaces = "ცვლილება პოლისისა";
        public const string Changes = "ცვლილებები ამ ხელშეკრულებაზე";
        public const string ChangeNote = "ცვლილების შინაარსი";
        public const string ScheduleReason = "პრემიის შემცირება დამტკიცებული ცვლილებით";
        public const string ReductionExceeds = "ცვლილების შემცირება აღემატება ძირითადი პოლისის გრაფიკს — დამტკიცება შეუძლებელია; გააფორმეთ მცირე შემცირება ან დაბრუნება.";

        public static class Errors
        {
            public const string NotActive = "ცვლილება მხოლოდ მოქმედ პოლისზეა შესაძლებელი";
            public const string Date = "ცვლილების თარიღი პოლისის მოქმედების ფარგლებში უნდა იყოს";
            public const string Reason = "აღწერეთ ცვლილების შინაარსი";
            public const string Coverages = "მონიშნეთ მინიმუმ ერთი დაფარვა";
            public const string Rate = "ამ თარიღის ოფიციალური კურსი ვერ მოიძებნა";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("notActive", NotActive),
                ("date", Date),
                ("reason", Reason),
                ("coverages", Coverages),
                ("rate", Rate));
        }
    }

    public static class Import
    {
        public const string Title = "პოლისების ატვირთვა სიით";
        public const string Button = "ატვირთვა სიით";
        public const string Note = "კორპორატიული კლიენტისთვის: ერთი ფაილი, ერთი დამზღვევი, ერთი გენერალური ხელშეკრულება და მის ქვეშ ცალკე პოლისი თითო ავტომობილზე. ჯერ შემოწმება, შემდეგ ჩაწერა; არც ერთი პოლისი არ იქმნება, სანამ ყველა სტრიქონი სწორი არ არის. ჩაწერილი პოლისები დამტკიცებას ელოდება.";
        public const string File = "ფაილი (.xlsx ან .csv)";
        public const string DefaultHint = "თუ ფაილში ცარიელია";
        public const string Check = "ფაილის შემოწმება";
        public const string Template = "შაბლონის ჩამოტვირთვა";
        public const string PreviewTitle = "შემოწმების შედეგი";

        public static string PreviewSummary(int total, int valid) =>
            string.Concat("სტრიქონი სულ: ", total.ToString(global::System.Globalization.CultureInfo.InvariantCulture), ", სწორი: ", valid.ToString(global::System.Globalization.CultureInfo.InvariantCulture), ", შეცდომით: ", (total - valid).ToString(global::System.Globalization.CultureInfo.InvariantCulture));

        public const string RowNo = "სტრიქონი";
        public const string Problems = "შენიშვნები";
        public const string Ok = "სწორია";

        public static string Confirm(int n) =>
            string.Concat(n.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " პოლისის ჩაწერა");

        public const string ConfirmHint = "იქმნება ერთი ხელშეკრულება და ყველა პოლისი ერთ ოპერაციად.";

        public static class Errors
        {
            public const string NoProduct = "პროდუქტი ვერ მოიძებნა";
            public const string NoFile = "აირჩიეთ ფაილი";
            public const string Unreadable = "ფაილის წაკითხვა ვერ მოხერხდა";
            public const string Empty = "ფაილში სტრიქონები არ არის";
            public const string TooMany = "ერთ ფაილში ძალიან ბევრი სტრიქონია";
            public const string Duplicate = "იგივე ავტომობილი ფაილში უკვე არის";
            public const string PeriodClosed = "დახურულ თვეში პოლისი ვერ დაიწყება — ეს სტრიქონი არ შემოვა; მიუთითეთ ღია პერიოდის თარიღი";

            /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
            public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
                ("noProduct", NoProduct),
                ("noFile", NoFile),
                ("unreadable", Unreadable),
                ("empty", Empty),
                ("tooMany", TooMany),
                ("duplicate", Duplicate),
                ("periodClosed", PeriodClosed));
        }
    }

    public static class Pending
    {
        public const string Title = "დასამტკიცებელი პოლისები";
        public const string Nav = "დასამტკიცებელი";
        public const string Note = "გაფორმებული პოლისები, რომლებიც ჯერ ძალაში არ შესულა. დამტკიცებისას პრემია აღირიცხება ძალაში შესვლის თარიღით (ან პირველ ღია თვეში).";
        public const string None = "დასამტკიცებელი პოლისი არ არის";
        public const string NoRight = "პოლისის დამტკიცების უფლება არ გაქვთ. ის როლით აქვს საოპერაციო დეპარტამენტის უფროსს და ადმინისტრატორს; სხვას ადმინისტრატორი ანიჭებს.";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("title", Title),
            ("nav", Nav),
            ("note", Note),
            ("none", None),
            ("noRight", NoRight));
    }

    public static class Print
    {
        public const string Button = "ბეჭდვა";
        public const string PrintText = "ბეჭდვა / PDF";
        public const string Back = "უკან";
        public const string PolicyTitle = "სადაზღვევო პოლისი";
        public const string CompanyId = "საიდენტიფიკაციო კოდი";
        public const string Parties = "მხარეები";

        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Roles = global::Insurance.Application.Texts.TextMap.Of(
            ("insured", "დაზღვეული"),
            ("beneficiary", "მოსარგებლე"),
            ("driver", "მძღოლი"),
            ("representative", "წარმომადგენელი"));

        public const string Period = "სადაზღვევო პერიოდი";
        public const string PaymentNote = "პრემია იხდება ლარში, გადახდის დღის ეროვნული ბანკის ოფიციალური კურსით.";
        public const string SignificantTerms = "ხელშეკრულების მნიშვნელოვანი პირობები";
        public const string IncompleteNotice = "ეს თავსართი შეიცავს არასრულ ინფორმაციას.";
        public const string PeriodLabel = "სადაზღვევო პერიოდი და მოქმედების ვადა";
        public const string SumInsuredLabel = "სადაზღვევო თანხა";
        public const string SumInsuredText = "მითითებულია დაფარვების ცხრილში თითოეული დაფარვისთვის; ანაზღაურება არ აღემატება შესაბამის სადაზღვევო თანხას ან ლიმიტს.";
        public const string ClaimsLabel = "ზარალის პროცედურა";
        public const string DisclosureLabel = "ინფორმაციის მიწოდების ვალდებულება";
        public const string WithdrawalLabel = "ხელშეკრულებიდან გასვლა";
        public const string TerminationLabel = "შეწყვეტა";
        public const string ComplaintsLabel = "პრეტენზიის წარდგენა";

        public static string ComplaintsResponse(string days) =>
            string.Concat("პასუხის მაქსიმალური ვადა: ", days, " კალენდარული დღე.");

        public const string ExclusionsLabel = "გამონაკლისები";
        public const string RegulatorLabel = "საზედამხედველო ორგანო";
    }

    public const string Approve = "პოლისის დამტკიცება";

    /// <summary>
    /// What the employee is told after pressing approve (Codex PR #19 task, item 3).
    /// </summary>
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ApproveOutcomes = global::Insurance.Application.Texts.TextMap.Of(
        ("done", "პოლისი დამტკიცდა და ძალაშია"),
        ("already", "პოლისი უკვე დამტკიცებულია ან აღარ ელოდება დამტკიცებას — არაფერი შეცვლილა"),
        ("forbidden", "პოლისის დამტკიცების უფლება არ გაქვთ — არაფერი შეცვლილა"),
        ("own", "საკუთარი გაფორმებული პოლისის დამტკიცება არ შეიძლება; დაამტკიცებს სხვა თანამშრომელი — არაფერი შეცვლილა"),
        ("invalid", "პოლისი ვერ მოიძებნა — არაფერი შეცვლილა"));

    public const string AwaitingApproval = "პოლისი ელოდება საოპერაციო დეპარტამენტის უფროსის დამტკიცებას. პრემია აღირიცხება დამტკიცებისას.";
    public const string ApprovedBy = "დაამტკიცა";
    public const string GraceHint = "ცარიელი ნიშნავს, რომ ავტომატურად არ უქმდება.";
    public const string PolicyholderHint = "დამზღვევი ჯერ კლიენტების რეესტრში უნდა იყოს დამატებული.";
    public const string Overdue = "ვადაგადაცილებული";
    public const string Days = "დღე";

    public static class Finance
    {
        public const string AsOf = "მდგომარეობა თარიღზე";
        public const string Written = "მოზიდული პრემია";
        public const string Earned = "გამომუშავებული პრემია";
        public const string Unearned = "გამოუმუშავებელი პრემია (UPR)";
        public const string Receivable = "პრემიის დავალიანება";
        public const string Overdue = "მათ შორის ვადაგადაცილებული";
        public const string Commission = "საკომისიო (დარიცხული)";
        public const string DeferredCommission = "გადავადებული საკომისიო";
        public const string InCurrency = "პოლისის ვალუტაში";
        public const string InGel = "ლარში";
        public const string CancellableFrom = "გადაუხდელობით გაუქმება შესაძლებელია";
        public const string ValuationRate = "დავალიანების კურსი";
        public const string StaleRate = "დღევანდელი კურსი ჯერ არ არის";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("asOf", AsOf),
            ("written", Written),
            ("earned", Earned),
            ("unearned", Unearned),
            ("receivable", Receivable),
            ("overdue", Overdue),
            ("commission", Commission),
            ("deferredCommission", DeferredCommission),
            ("inCurrency", InCurrency),
            ("inGel", InGel),
            ("cancellableFrom", CancellableFrom),
            ("valuationRate", ValuationRate),
            ("staleRate", StaleRate));
    }

    public static class Errors
    {
        public const string Policyholder = "ამ ნომრით კლიენტი ვერ მოიძებნა. ჯერ დაამატეთ იგი კლიენტებში.";
        public const string PolicyholderRisk = "კლიენტს რისკის დონე არ აქვს მინიჭებული — ჯერ შეაფასეთ კლიენტის ბარათზე (AML კანონი, მუხ. 8).";
        public const string PolicyholderSanctioned = "კლიენტს მომსახურება შეჩერებული აქვს სანქციების სკრინინგის შედეგად — პოლისის გაფორმება დაუშვებელია (AML კანონი, მუხ. 10.7).";
        public const string Make = "მარკა სავალდებულოა";
        public const string Model = "მოდელი სავალდებულოა";
        public const string VehicleId = "მიუთითეთ სახელმწიფო ნომერი ან VIN";
        public const string Vin = "VIN უნდა შედგებოდეს 17 სიმბოლოსგან";
        public const string Year = "გამოშვების წელი არასწორია";
        public const string Value = "მიუთითეთ ავტომობილის ღირებულება";
        public const string Dates = "დასრულების თარიღი უნდა იყოს ძალაში შესვლის თარიღის შემდეგ";
        public const string Rate = "ამ თარიღის ოფიციალური კურსი ვერ მოიძებნა";
        public const string Coverages = "აირჩიეთ მინიმუმ ერთი დაფარვა და მიუთითეთ პრემია";
        public const string Premium = "მიუთითეთ პრემია";
        public const string Installments = "შენატანების რაოდენობა: 1, 4, 11 ან 12. სხვა გრაფიკი (მაგ. 2 გადახდა) ინდივიდუალურია";
        public const string InstallmentsEleven = "11 გადახდა მხოლოდ ზუსტად 12-თვიან პოლისზეა შესაძლებელი";
        public const string Mandatory = "ეს დაფარვა სავალდებულოა";
        public const string Commission = "საკომისიო უნდა იყოს 0-დან 100-მდე";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("policyholder", Policyholder),
            ("policyholderRisk", PolicyholderRisk),
            ("policyholderSanctioned", PolicyholderSanctioned),
            ("make", Make),
            ("model", Model),
            ("vehicleId", VehicleId),
            ("vin", Vin),
            ("year", Year),
            ("value", Value),
            ("dates", Dates),
            ("rate", Rate),
            ("coverages", Coverages),
            ("premium", Premium),
            ("installments", Installments),
            ("installmentsEleven", InstallmentsEleven),
            ("mandatory", Mandatory),
            ("commission", Commission));
    }
}
