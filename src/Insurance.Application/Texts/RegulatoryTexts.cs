// Interface texts of the supervisory report forms filled with the program's data (INS-REGULATORY-THREE-REPORT-GROUPS-EXISTING-DATA-20261019).
// Ported from src/i18n/regulatory.ts (regulatoryTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class RegulatoryTexts
{
    public const string Nav = "საზედამხედველო ფორმები";
    public const string Title = "საზედამხედველო ფორმები";
    public const string Note = "ზედამხედველობის მოქმედი ფორმები, შევსებული პროგრამაში არსებული მონაცემებით. ივსება მხოლოდ ის უჯრა, რომლის წყაროც პროგრამაშია; მონაცემი, რომელიც პროგრამაში არ აღირიცხება, ცარიელი რჩება (ნული მხოლოდ მაშინ იწერება, როცა პროგრამამ ნული დაითვალა). ფორმულები, ფორმატი და დანარჩენი ფურცლები შაბლონისაა; Excel-ი გახსნისას ყველაფერს თავიდან დაითვლის.";
    public const string Month = "საანგარიშო თვე";
    public const string Show = "ჩვენება";
    public const string Download = "Excel-ის ჩამოტვირთვა";

    public static class Groups
    {
        public const string Reserves = "სადაზღვევო რეზერვების განთავსება (ფორმები 11–14)";
        public const string Form1 = "ფორმა 1 (სადაზღვევო ნაწილი)";
        public const string Statistical = "სტატისტიკური ფორმები";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("reserves", Reserves),
            ("form1", Form1),
            ("statistical", Statistical));
    }

    public const string Form6 = "ფორმა 6. გადამზღვევლები და ბროკერები";
    public const string Quarter = "კვარტალი";

    public static string QuarterDate(string q, string d) =>
        string.Concat("კვარტალი ", q, ", საანგარიშო თარიღი: ", d);

    public const string Form6Coverage = "კვარტლის ბოლო დღეს მოქმედი გადაზღვევის ხელშეკრულებები (ხელშეკრულების პერიოდით, ორივე დღის ჩათვლით), თითო სტრიქონი სახეობა × ხელშეკრულება: სახეობები — ხელშეკრულებაზე მითითებული, ფაკულტატურზე კი ამ დღემდე მასზე განთავსებული პოლისის სტრიქონების სახეობები. სვეტები I–IX ივსება ხელშეკრულებიდან და მისი მხარეებიდან ისე, როგორც შენახულია; რაც არ არის შეყვანილი (რეიტინგი, სააგენტო, ბროკერი), ცარიელი რჩება. ქვეყანა მხარის ბარათიდანაა (თუ არ შეიყვანეს, ბარათზე GE წერია). ხელშეკრულების დახურვის თარიღი და სტატუსის ისტორია პროგრამაში არ ინახება, ამიტომ დახურული ხელშეკრულებაც პერიოდით ითვლება. შაბლონში ექვსი სახეობისთვის ხუთ-ხუთი სტრიქონია; რაც არ ეტევა, ფაილი არ გაიცემა.";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Form6Columns = global::Insurance.Application.Texts.TextMap.Of(
        ("A", "I. სახეობა"),
        ("B", "II. გადამზღვეველი"),
        ("C", "III. ხელშეკრულების ტიპი"),
        ("D", "IV. პერიოდი"),
        ("E", "V. რეიტინგი"),
        ("F", "VI. სარეიტინგო სააგენტო"),
        ("G", "VII. ქვეყანა"),
        ("H", "VIII. ბროკერი"),
        ("I", "IX. ბროკერის ქვეყანა"));

    public const string Form6None = "საანგარიშო თარიღზე მოქმედი გადაზღვევის ხელშეკრულება არ არის — ფორმა ცარიელი გაიცემა (მხოლოდ კომპანია და თარიღი).";

    public static string Form6Overflow(int rows, global::System.Collections.Generic.IReadOnlyList<string> classes) =>
        string.Concat("შაბლონის ბადეში ექვსი სახეობისთვის ხუთ-ხუთი სტრიქონია: ", rows.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " სტრიქონი ბადის ქვემოთ, დამატებით სტრიქონებშია ჩაწერილი", classes.Count > 0 ? string.Concat(" (სახეობები: ", string.Join(", ", classes), ")") : "", ". ყველა სტრიქონი ფაილშიც და გვერდზეც არის.");

    public const string OtherStatistical = "დანარჩენი სტატისტიკური ფორმები — მზადდება, შემდეგ ეტაპზე.";
    public const string PrecisionRefused = "Excel-ის ფაილი არ გაიცა: Excel რიცხვს 15–16 ციფრამდე ინახავს და ამ თანხებს თეთრამდე ზუსტად ვერ შეინახავს (უჯრა: ზუსტი თანხა → რასაც Excel შეინახავდა). ზუსტი ციფრები გვერდზეა.";

    public static string PrecisionMore(int n) =>
        string.Concat("… და კიდევ ", n.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " უჯრა");

    public const string PrecisionPage = "Excel-ის ფაილს ვერ ჩამოტვირთავთ: Excel ზოგიერთ თანხას თეთრამდე ზუსტად ვერ შეინახავს. ზუსტი ციფრები ქვემოთაა.";
    public const string Form1Coverage = "ფინანსური ანგარიშგების დანართი N1, მისი სადაზღვევო ნაწილი, პროგრამაში არსებული მონაცემებით. N1-7 — არასიცოცხლის რეზერვები და გადამზღვევლის წილი (თვის პრემიის ანგარიშიდან); N1-4 — სადაზღვევო მოთხოვნები პირდაპირი / ბროკერებისგან / აგენტებისგან და მათი გაუფასურება (გაუფასურების ანგარიშიდან); Info — კომპანიის რეკვიზიტები პარამეტრებიდან და თარიღი. ბალანსის (N1) სადაზღვევო სტრიქონებს შაბლონის ფორმულები ქვეფორმებიდან კრებს. „გასული წლის შესაბამისი პერიოდი“, სიცოცხლე, გადაზღვევის მოთხოვნები, გადავადებული საკომისიო და დანარჩენი ფორმები ცარიელია — ეს მონაცემები პროგრამაში არ აღირიცხება. ბალანსის სრული შევსება ეს არ არის. მოგება-ზარალის სადაზღვევო ნაწილი (N2, N2-1, N4) — ქვემოთ.";
    public const string N17 = "N1-7. სადაზღვევო რეზერვები და გადამზღვევლის წილი (არასიცოცხლე, საანგარიშო პერიოდი)";
    public const string N14 = "N1-4. სადაზღვევო მოთხოვნები (საანგარიშო პერიოდი)";
    public const string Balance = "N1. ბალანსის სადაზღვევო სტრიქონები (შაბლონის ფორმულებით)";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> N17Lines = global::Insurance.Application.Texts.TextMap.Of(
        ("E10", "00010 გამოუმუშავებელი პრემიის რეზერვი"),
        ("E11", "00020 RBNS"),
        ("E12", "00030 IBNR"),
        ("E13", "00040 სულ რეზერვები, ბრუტო"),
        ("E16", "00050 გადამზღვევლის წილი UPR-ში"),
        ("E17", "00060 გადამზღვევლის წილი RBNS-ში"),
        ("E18", "00070 სულ გადამზღვევლის წილი"),
        ("E24", "00110 სულ რეზერვები, ნეტო"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> N14Lines = global::Insurance.Application.Texts.TextMap.Of(
        ("E10", "00010 სადაზღვევო მოთხოვნები"),
        ("E11", "00020 გაუფასურება"),
        ("E13", "00040 მოთხოვნები ბროკერებისგან"),
        ("E14", "00050 გაუფასურება"),
        ("E16", "00070 მოთხოვნები აგენტებისგან"),
        ("E17", "00080 გაუფასურება"),
        ("E19", "00100 სულ სადაზღვევო მოთხოვნები, წმინდა"),
        ("E26", "00140 სხვა მოთხოვნები გადაზღვევიდან (გადამზღვევლის საკომისიო)"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> BalanceLines = global::Insurance.Application.Texts.TextMap.Of(
        ("00060", "სადაზღვევო მოთხოვნები, წმინდა"),
        ("00070", "გადაზღვევის მოთხოვნები, წმინდა"),
        ("00120", "გადამზღვევლის წილი სადაზღვევო რეზერვებში"),
        ("00130", "გადავადებული საკომისიო ხარჯი"),
        ("00200", "სადაზღვევო რეზერვები, ბრუტო"),
        ("00210", "სხვა სადაზღვევო ვალდებულებები"),
        ("00270", "გადავადებული საკომისიო შემოსავალი"));

    public const string NotKept = "პროგრამაში არ აღირიცხება (Excel-ში შაბლონის ფორმულა ცარიელი უჯრებიდან 0-ს აჩვენებს)";
    public const string ReceivableControl = "შედარება: N1-4-ის მოთხოვნები ბრუტო (პირდაპირი + ბროკერი + აგენტი) და თვის ანგარიშის დავალიანება ლარში";
    public const string Form11 = "ფორმა 11. სადაზღვევო რეზერვები";
    public const string Form12 = "ფორმა 12. ინვესტირებას დაქვემდებარებული რეზერვები (შაბლონის ფორმულებით)";

    public static string Date(string d) =>
        string.Concat("საანგარიშო თარიღი: ", d);

    public static class Columns
    {
        public const string Code = "კოდი";
        public const string Name = "სახეობა";
        public const string C = "UPR ბრუტო";
        public const string D = "UPR ნეტო";
        public const string E = "RBNS ბრუტო";
        public const string F = "RBNS ნეტო";
        public const string G = "IBNR";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("code", Code),
            ("name", Name),
            ("C", C),
            ("D", D),
            ("E", E),
            ("F", F),
            ("G", G));
    }

    public const string Total = "სულ";
    public const string Empty = "—";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Form12Lines = global::Insurance.Application.Texts.TextMap.Of(
        ("D8", "გამოუმუშავებელი პრემიის რეზერვი"),
        ("D9", "გადამზღვევლის წილი გამოუმუშავებელ პრემიაში"),
        ("D7", "გამოუმუშავებელი პრემიის რეზერვის ინვესტირებას დაქვემდებარებული ნაწილი (75%)"),
        ("D16", "RBNS"),
        ("D17", "გადამზღვევლის წილი RBNS-ში"),
        ("D15", "RBNS-ის ინვესტირებას დაქვემდებარებული ნაწილი"),
        ("D24", "IBNR"),
        ("D23", "IBNR-ის ინვესტირებას დაქვემდებარებული ნაწილი"),
        ("D35", "ინვესტირებას დაქვემდებარებული რეზერვები, სულ"));

    public const string Control = "შემოწმება: ფორმის „სულ\" = თვის პრემიის ანგარიშის ჯამი (UPR, გადამზღვევლის წილი UPR-ში, RBNS, მისი წილი, IBNR)";
    public const string ControlOk = "ემთხვევა";
    public const string Unplaced = "ფორმაში ვერ განთავსდა";
    public const string UnplacedNote = "ეს თანხები გატარებულია სახეობაზე, რომლის სტრიქონიც ფორმაში ქვესახეობებადაა დაყოფილი (მაგ. 06 = 06.1 + 06.2 + 06.3), და არა რომელიმე ქვესახეობაზე. რომელ ქვესახეობას ეკუთვნის, პროგრამა თავად არ წყვეტს, ამიტომ ფორმაში არ შევიდა და „სულ\" თვის ანგარიშს სწორედ ამ თანხით ჩამორჩება.";
    public const string ControlBad = "არ ემთხვევა";
    public const string Coverage = "პროგრამაში არსებული მონაცემებით: ფორმა 11 — ყველა არასიცოცხლის სახეობა (UPR, RBNS ბრუტო და ნეტო, IBNR); სახეობა 5-ზე გატარებული თანხები 05.1-ში შედის; სიცოცხლისა და საპენსიო სტრიქონები და სვეტები ცარიელია. ფორმა 12 — რეზერვები ფორმა 11-დან მოდის; სადაზღვევო და გადაზღვევის მოთხოვნები და ვალდებულებები პროგრამაში ჯერ არ აღირიცხება, ამიტომ მათი უჯრები ცარიელია. ფორმები 13–14 — აქტივები პროგრამაში არ არის; შაბლონის ფორმულები უცვლელია.";

    // Form 1's profit and loss: IS (N2), IS-COM (N2-1), P&C(NL) (N4), N2-6 00160, N1-8 (old program PR #366, head d251848).
    public static string IncomeTitle(string from, string to) =>
        string.Concat("მოგება-ზარალი (არასიცოცხლე), ", from, " – ", to);

    public const string IncomeCoverage = "წლის დასაწყისიდან საანგარიშო თარიღამდე (მფლობელი, 20.10.2026). P&C(NL) (N4): მოზიდული პრემია (Corrected), გადაზღვევის პრემია, ანაზღაურებული ზარალები და გადამზღვევლის წილი მათში — სახეობით და სეგმენტით (კორპორატიული / საცალო / სახელმწიფო), რაოდენობით; IS-ის 00010, 00020, 00060, 00070 შაბლონის ფორმულებით ამ ფურცლიდან მოდის. ცვლილება UPR-სა და ზარალების რეზერვში (RBNS + IBNR) ჩაწერილია როგორც „ბოლო − საწყისი“ (საწყისი = წინა წლის 31 დეკემბერი), რომ შაბლონის ფორმულამ გამომუშავებული პრემია და დამდგარი ზარალი სწორად დათვალოს. 00100 — რეგრესი და გადარჩენილი ქონება მიღებისას; 00500 — დებიტორების გაუფასურების ცვლილება; IS-COM — გადაზღვევის, ბროკერისა და აგენტის (თანამშრომლის ჩათვლით) საკომისიო და მისი გადავადებული ნაწილის ცვლილება. მფლობელის პასუხებით (20.10.2026): პოლისის ცვლილება ახალი პოლისია; ზარალის შემთხვევა = ზარალი × სახეობა (ერთ სახეობაზე ერთხელ, რამდენი გადახდაც არ უნდა ჰქონდეს; ორ სახეობაზე — 2 შემთხვევა); ერთსახეობიანი XL ხელშეკრულების პრემია მის სახეობაზეა; ბანკის საკომისიო — N2-6-ის 00160-ში (პერიოდში დარიცხული, არ გადავადდება); გადავადებული საკომისიო — N1-8-ში, საიდანაც ბალანსის 00130 / 00270 შაბლონის ფორმულით ივსება. სეგმენტი — გაცემისას პოლისზე დაფიქსირებული (მფლობელი, 21.10.2026: „ეს მონაცემი არ იცვლება. არის ის, როგორც გაიცა“). ბონუსები, ცედენტის საკომისიო, შემომავალი გადაზღვევა, ცვეთა და „გასული წლის“ სვეტი ცარიელია — პროგრამაში არ აღირიცხება.";

    public const string IsTitle = "N2. სრული შემოსავლის ანგარიშგება (სადაზღვევო საქმიანობა, არასიცოცხლე)";

    public const string OtherExpensesTitle = "N2-6. სხვა ხარჯები (მხოლოდ ბანკის საკომისიო)";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> OtherExpensesLines = global::Insurance.Application.Texts.TextMap.Of(
        ("E31", "00160 ბანკის მომსახურების საკომისიოები (ბანკის საკომისიო, პერიოდში დარიცხული)"));

    public const string DeferredTitle = "N1-8. გადავადებული საკომისიო (საანგარიშო თარიღზე; ბალანსის 00130 და 00270 აქედან)";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> DeferredLines = global::Insurance.Application.Texts.TextMap.Of(
        ("E11", "00020 ბროკერის კუთვნილი საკომისიო"),
        ("E12", "00030 აგენტის (და თანამშრომლის) კუთვნილი საკომისიო"),
        ("E13", "00040 სულ გადავადებული საკომისიო ხარჯი"),
        ("E16", "00050 გადაზღვევიდან კუთვნილი საკომისიო"),
        ("E17", "00060 სულ გადავადებული საკომისიო შემოსავალი"));

    public static string ClaimCountCheck(int cases, int losses) =>
        string.Concat("ანაზღაურებული ზარალების რაოდენობა (P&C(NL), III ნაწილი): ", cases.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " შემთხვევა — ზარალი × სახეობა (მფლობელი, 20.10.2026: „2 სახეობაში 1 ზარალი, 2 შემთხვევაა“); ", "ეს ", losses.ToString(global::System.Globalization.CultureInfo.InvariantCulture), " ზარალია.");

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> TreatyReasons = global::Insurance.Application.Texts.TextMap.Of(
        ("no-class", "სახეობა არ აქვს მითითებული"),
        ("several-classes", "რამდენიმე სახეობა — თანხა არ იყოფა (თითო სახეობას თავისი XL უნდა ჰქონდეს)"),
        ("not-xl", "არც XL, არც კვოტური"),
        ("negative-key", "გადაცემული პრემიის გამომუშავებული ნაწილი უარყოფითია — გაყოფა გადასაწყვეტია"),
        ("no-row", "სახეობას ფორმაში სტრიქონი არ აქვს"));

    public const string ComTitle = "N2-1. საკომისიო შემოსავალი (ხარჯი), არასიცოცხლე";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> IsLines = global::Insurance.Application.Texts.TextMap.Of(
        ("E9", "00010 მოზიდული პრემია, ბრუტო"),
        ("E10", "00020 გადაზღვევის პრემია"),
        ("E11", "00030 ცვლილება UPR-ში, ბრუტო"),
        ("E12", "00040 ცვლილება UPR-ში, გადამზღვევლის წილი"),
        ("E13", "00050 გამომუშავებული პრემია (ნეტო)"),
        ("E14", "00060 ანაზღაურებული ზარალები"),
        ("E15", "00070 გადამზღვევლის წილი ანაზღაურებულ ზარალებში"),
        ("E16", "00080 ცვლილება ზარალების რეზერვში, ბრუტო"),
        ("E17", "00090 ცვლილება ზარალების რეზერვში, გადამზღვევლის წილი"),
        ("E18", "00100 შემოსავალი რეგრესიდან და გადარჩენილი ქონებიდან"),
        ("E19", "00110 დამდგარი ზარალები, ნეტო"),
        ("E20", "00120 დარიცხული ბონუსები"),
        ("E21", "00130 საკომისიო შემოსავალი (ხარჯი), წმინდა"),
        ("E22", "00140 სადაზღვევო მოგება (ზარალი), წმინდა"),
        ("E67", "00500 ცვეთის, ამორტიზაციის და გაუფასურების ხარჯი (მხოლოდ გაუფასურების ცვლილება)"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ComLines = global::Insurance.Application.Texts.TextMap.Of(
        ("E11", "00020 გადაზღვევიდან კუთვნილი საკომისიო (გადავადებას დაქვემდებარებული)"),
        ("E12", "00030 ცვლილება გადავადებულ შემოსავალში"),
        ("E13", "00040 გადამზღვევლისგან მიღებული საკომისიო, წმინდა"),
        ("E19", "00100 ბროკერის საკომისიო (გადავადებას დაქვემდებარებული)"),
        ("E20", "00110 ცვლილება გადავადებულ ხარჯში"),
        ("E21", "00120 ბროკერის საკომისიო, წმინდა"),
        ("E23", "00140 აგენტის საკომისიო (გადავადებას დაქვემდებარებული)"),
        ("E24", "00150 ცვლილება გადავადებულ ხარჯში"),
        ("E25", "00160 აგენტის საკომისიო, წმინდა"),
        ("E26", "00170 საკომისიო შემოსავალი (ხარჯი), წმინდა"));

    public const string IncomeUnplacedNote = "ეს თანხები პროგრამაშია, მაგრამ ფორმის არცერთ სტრიქონში არ შევიდა: სახეობა, რომლის სტრიქონიც ფორმაში ქვესახეობებადაა დაყოფილი (სახეობა 5-ის გარდა, რომელიც 05.1-ში შედის); ხელშეკრულების პრემია, რომელსაც ერთი სახეობა არ აქვს ან XL არ არის (მიზეზი ფრჩხილებშია). პროგრამა თავად არ წყვეტს, სად შევიდეს. სვეტები: რაოდენობა, თანხა.";

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> IncomeParts = global::Insurance.Application.Texts.TextMap.Of(
        ("written", "მოზიდული პრემია"),
        ("ceded", "გადაზღვევის პრემია"),
        ("paid", "ანაზღაურებული ზარალები"),
        ("reinsurerPaid", "გადამზღვევლის წილი ზარალებში"),
        ("treaty", "ხელშეკრულების პრემია"));
}
