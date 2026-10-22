// Interface texts of the complaints register (order №18/2017, art. 6–7).
// Ported from src/i18n/complaints.ts (complaintTexts); every text is byte-for-byte the old one.
namespace Insurance.Application.Texts;

public static class ComplaintTexts
{
    public const string Nav = "პრეტენზიები";
    public const string Title = "პრეტენზიების რეესტრი";
    public const string New = "პრეტენზიის რეგისტრაცია";
    public const string Register = "რეგისტრაცია";
    public const string Registered = "პრეტენზია დარეგისტრირდა";
    public const string Saved = "შენახულია";
    public const string Note = "წერილობითი და ელექტრონული პრეტენზიები აღირიცხება სრულად; პასუხი არაუგვიანეს 30 კალენდარული დღისა მიღებიდან. ერთი პრეტენზია ორ კატეგორიაზე — ორი ჩანაწერი. ყოველთვიური ანგარიში სამსახურს 10 რიცხვამდე იგზავნება.";
    public const int ResponseDays = 30;

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Channels = global::Insurance.Application.Texts.TextMap.Of(
        ("written", "წერილობითი"),
        ("electronic", "ელექტრონული"),
        ("oral", "ზეპირი"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Categories = global::Insurance.Application.Texts.TextMap.Of(
        ("1", "ანაზღაურების გადაწყვეტილების გადახედვა"),
        ("2", "მომსახურების ხარვეზი"),
        ("3", "ინფორმაციის / დოკუმენტის მიწოდება"),
        ("4", "სხვა სახელშეკრულებო მოთხოვნა"));

    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> Statuses = global::Insurance.Application.Texts.TextMap.Of(
        ("open", "დაუსრულებელი"),
        ("upheld", "საფუძვლიანი — დაკმაყოფილდა"),
        ("partly_upheld", "ნაწილობრივ საფუძვლიანი"),
        ("rejected", "უსაფუძვლო — უარი"),
        ("upheld_other", "საფუძვლიანი, საჭიროა დამატებითი ღონისძიებები"));

    public static class Fields
    {
        public const string Number = "ნომერი";
        public const string ReceivedOn = "მიღების თარიღი";
        public const string Channel = "არხი";
        public const string CustomerName = "მომხმარებელი";
        public const string CustomerContact = "კონტაქტი";
        public const string PartyIdNumber = "პირადი ნომერი / საიდენტიფიკაციო კოდი (თუ კლიენტია)";
        public const string PolicyNumber = "პოლისის ნომერი";
        public const string ClaimNumber = "ზარალის ნომერი";
        public const string Category = "კატეგორია";
        public const string Description = "პრეტენზიის შინაარსი";
        public const string Status = "სტატუსი";
        public const string Deadline = "პასუხის ვადა";
        public const string RespondedOn = "პასუხის მიწოდების თარიღი";
        public const string ResponseText = "პასუხის შინაარსი";
        public const string Measures = "გატარებული ღონისძიებები";
        public const string Outcome = "საბოლოო შედეგი";
        public const string Note = "შენიშვნა";
        public const string Acknowledged = "მიღება დადასტურებულია";
        public const string AcknowledgedAt = "მიღების დადასტურება";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("number", Number),
            ("receivedOn", ReceivedOn),
            ("channel", Channel),
            ("customerName", CustomerName),
            ("customerContact", CustomerContact),
            ("partyIdNumber", PartyIdNumber),
            ("policyNumber", PolicyNumber),
            ("claimNumber", ClaimNumber),
            ("category", Category),
            ("description", Description),
            ("status", Status),
            ("deadline", Deadline),
            ("respondedOn", RespondedOn),
            ("responseText", ResponseText),
            ("measures", Measures),
            ("outcome", Outcome),
            ("note", Note),
            ("acknowledged", Acknowledged),
            ("acknowledgedAt", AcknowledgedAt));
    }

    public static class Actions
    {
        public const string Acknowledge = "მიღების დადასტურება";
        public const string Respond = "პასუხის შენახვა";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("acknowledge", Acknowledge),
            ("respond", Respond));
    }

    public const string Overdue = "ვადა გადაცილებულია";
    public const string DueSoon = "ვადა ახლოვდება";

    public static class Errors
    {
        public const string ReceivedOn = "მიუთითეთ მიღების თარიღი (არა მომავალში)";
        public const string Customer = "მიუთითეთ მომხმარებელი";
        public const string Description = "აღწერეთ პრეტენზია";
        public const string Category = "აირჩიეთ კატეგორია";
        public const string Policy = "ამ ნომრით პოლისი ვერ მოიძებნა";
        public const string Claim = "ამ ნომრით ზარალი ვერ მოიძებნა";
        public const string Response = "დასრულებულ პრეტენზიას სჭირდება პასუხის თარიღი და შინაარსი";

        /// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>
        public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey = global::Insurance.Application.Texts.TextMap.Of(
            ("receivedOn", ReceivedOn),
            ("customer", Customer),
            ("description", Description),
            ("category", Category),
            ("policy", Policy),
            ("claim", Claim),
            ("response", Response));
    }
}
