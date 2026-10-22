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
    public const string Form1Coverage = "ფინანსური ანგარიშგების დანართი N1, მისი სადაზღვევო ნაწილი, პროგრამაში არსებული მონაცემებით. N1-7 — არასიცოცხლის რეზერვები და გადამზღვევლის წილი (თვის პრემიის ანგარიშიდან); N1-4 — სადაზღვევო მოთხოვნები პირდაპირი / ბროკერებისგან / აგენტებისგან და მათი გაუფასურება (გაუფასურების ანგარიშიდან); Info — კომპანიის რეკვიზიტები პარამეტრებიდან და თარიღი. ბალანსის (N1) სადაზღვევო სტრიქონებს შაბლონის ფორმულები ქვეფორმებიდან კრებს. „გასული წლის შესაბამისი პერიოდი“, სიცოცხლე, გადაზღვევის მოთხოვნები, გადავადებული საკომისიო და დანარჩენი ფორმები ცარიელია — ეს მონაცემები პროგრამაში არ აღირიცხება. ბალანსის სრული შევსება ეს არ არის.";
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
        ("E19", "00100 სულ სადაზღვევო მოთხოვნები, წმინდა"));

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
}
