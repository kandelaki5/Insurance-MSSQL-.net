namespace Insurance.Domain.Regulatory;

/// <summary>
/// The interface texts the regulatory export needs, copied word for word from the old src/i18n/regulatory.ts (only these; the rest of
/// that file belongs to the texts' own port).
/// </summary>
public static class RegulatoryTexts
{
    public const string PrecisionRefused =
        "Excel-ის ფაილი არ გაიცა: Excel რიცხვს 15–16 ციფრამდე ინახავს და ამ თანხებს თეთრამდე ზუსტად ვერ შეინახავს (უჯრა: ზუსტი თანხა → რასაც Excel შეინახავდა). " +
        "ზუსტი ციფრები გვერდზეა.";
    public static string PrecisionMore(int n) => $"… და კიდევ {n} უჯრა";
}
