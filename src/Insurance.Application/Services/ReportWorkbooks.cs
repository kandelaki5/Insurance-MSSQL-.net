using Insurance.Application.Excel;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>
/// The report workbooks written from rows (legacy bordereauWorkbook, policyPremiumWorkbook, impairmentWorkbook): the same sheets, columns,
/// values and types. A money cell is the amount as Number((minor / 100).toFixed(2)) gave it — the exact two-place amount.
/// </summary>
public static class ReportWorkbooks
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static decimal Money(long minor) => ReportMath.Money(minor);
    private static string Ddmmyyyy(DateOnly d) => d.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The bordereau as an Excel workbook: a premium sheet, a claims sheet and the balance.</summary>
    public static byte[] Bordereau(Bordereau.Statement b)
    {
        var p = ReinsuranceTexts.Bordereau.Premium;
        var c = ReinsuranceTexts.Bordereau.Claims;
        var bal = ReinsuranceTexts.Bordereau.Balance;
        var book = new XlsxBook();
        var premium = b.Premiums.Select(r => (IEnumerable<KeyValuePair<string, object?>>)new KeyValuePair<string, object?>[]
        {
            new(p["policyNumber"], r.PolicyNumber),
            new(p["policyholder"], r.Policyholder),
            new(p["object"], r.Object ?? ""),
            new(p["classCode"], r.ClassCode),
            new(p["coverFrom"], Iso(r.CoverFrom)),
            new(p["coverTo"], Iso(r.CoverTo)),
            new(p["sumInsured"], r.SumInsured is { } si ? si : ""),
            new(p["currency"], r.Currency),
            new(p["sharePercent"], r.SharePercent),
            new(p["cededPremium"], Money(r.CededPremium)),
            new(p["cededPremiumGel"], Money(r.CededPremiumGel)),
            new(p["commissionGel"], Money(r.CommissionGel)),
            new(p["bookedOn"], Iso(r.BookedOn)),
            new(p["kind"], r.IsCancellation ? p["cancellation"] : p["issue"]),
        }).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(premium), "Premium");
        var claims = b.Claims.Select(r => (IEnumerable<KeyValuePair<string, object?>>)new KeyValuePair<string, object?>[]
        {
            new(c["claimNumber"], r.ClaimNumber),
            new(c["policyNumber"], r.PolicyNumber),
            new(c["occurredOn"], Iso(r.OccurredOn)),
            new(c["reportedOn"], Iso(r.ReportedOn)),
            new(c["status"], ClaimTexts.Status.TryGetValue(r.Status, out var s) ? s : r.Status),
            new(c["currency"], r.Currency),
            new(c["paidShare"], Money(r.PaidShare)),
            new(c["paidShareGel"], Money(r.PaidShareGel)),
            new(c["reserveShare"], Money(r.ReserveShare)),
            new(c["reserveShareGel"], Money(r.ReserveShareGel)),
        }).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(claims), "Claims");
        IReadOnlyList<object?>[] balance =
        [
            [bal["treaty"], $"{b.Treaty.Number} · {b.Treaty.Name}"],
            [bal["reinsurer"], b.Treaty.Reinsurer],
            [bal["period"], $"{Iso(b.From)} – {Iso(b.To)}"],
            [bal["cededGel"], Money(b.Totals.CededGel)],
            [bal["commissionGel"], Money(b.Totals.CommissionGel)],
            [bal["brokerageGel"], Money(b.Totals.BrokerageGel)],
            [bal["claimsPaidGel"], Money(b.Totals.ClaimsPaidGel)],
            [bal["reserveGel"], Money(b.Totals.ReserveGel)],
            [bal["balanceGel"], Money(b.Totals.BalanceGel)],
        ];
        book.AppendSheet(XlsxWriter.AoaToSheet(balance), "Balance");
        return book.Write();
    }

    /// <summary>The premium report by policy as an Excel file, in the column order of the page.</summary>
    public static byte[] PolicyPremium(PolicyPremiumReport.Report report)
    {
        string C(string key) => PolicyPremiumTexts.Columns.ByKey[key];
        IReadOnlyList<object?> header =
        [
            C("contractNumber"), C("policyNumber"), C("policyholder"), C("isModification"), C("classCode"), C("className"), C("currency"), C("premium"),
            C("statistical"), C("correction"), C("corrected"), C("startDate"), C("changeDate"), C("endDate"), C("cancelDate"), C("earned"), C("unearned"),
            C("riPremium"), C("riEarned"), C("riUnearned"), C("commissionEarned"), C("commissionUnearned"),
        ];
        var body = report.Rows.Select(r => (IReadOnlyList<object?>)new object?[]
        {
            r.ContractNumber, r.PolicyNumber, r.Policyholder, r.IsModification ? PolicyPremiumTexts.Yes : PolicyPremiumTexts.No, r.ClassCode, r.ClassName, r.Currency, Money(r.Premium),
            Money(r.StatisticalGel), Money(r.CorrectionGel), Money(r.CorrectedGel), Ddmmyyyy(r.StartDate), Ddmmyyyy(r.ChangeDate), Ddmmyyyy(r.EndDate),
            r.CancelDate is { } cd ? Ddmmyyyy(cd) : "", Money(r.EarnedGel), Money(r.UnearnedGel),
            Money(r.RiPremiumGel), Money(r.RiEarnedGel), Money(r.RiUnearnedGel), Money(r.CommissionEarnedGel), Money(r.CommissionUnearnedGel),
        });
        var t = report.Totals;
        IReadOnlyList<object?> total =
        [
            PolicyPremiumTexts.Total, "", "", "", "", "", "", "", Money(t.StatisticalGel), Money(t.CorrectionGel), Money(t.CorrectedGel), "", "", "", "",
            Money(t.EarnedGel), Money(t.UnearnedGel), Money(t.RiPremiumGel), Money(t.RiEarnedGel), Money(t.RiUnearnedGel), Money(t.CommissionEarnedGel), Money(t.CommissionUnearnedGel),
        ];
        IReadOnlyList<object?> title = [$"{Ddmmyyyy(report.From)} – {Ddmmyyyy(report.To)}"];
        var rows = new List<IReadOnlyList<object?>> { title, Array.Empty<object?>(), header };
        rows.AddRange(body);
        rows.Add(total);
        return XlsxLayout.ReadableWorkbook([new SheetRows("NBG list", rows, 2)]);
    }

    private static string BandKey(AgeBand b) => char.ToLowerInvariant(b.ToString()[0]) + b.ToString()[1..];

    /// <summary>The impairment calculation in the column order of the owner's template, one row per policy, and N1-4's totals.</summary>
    public static byte[] Impairment(ImpairmentReport.Report report)
    {
        string C(string key) => ImpairmentTexts.Columns[key];
        string Band(AgeBand b) => ImpairmentTexts.Bands.ByKey[BandKey(b)];
        string Kind(string k) => ImpairmentTexts.Kinds.TryGetValue(k, out var v) ? v : k;
        var bands = Domain.Finance.Impairment.AgeBands;
        var header = new List<object?>
        {
            C("number"), C("policyNumber"), C("coverFrom"), C("coverTo"), C("debtorKind"), C("intermediary"), C("policyholder"), C("classes"), C("segment"),
            C("insured"), C("beneficiary"), C("premium"), C("currency"),
        };
        header.AddRange(bands.Select(b => (object?)$"{C("earned")}: {Band(b)}"));
        header.AddRange([C("earnedTotal"), C("paid"), C("unpaidPremium"), C("rate"), C("unpaidPremiumGel"), C("unpaidEarned")]);
        header.AddRange(bands.Select(b => (object?)$"{C("unpaid")}: {Band(b)}"));
        header.Add(C("reserve"));
        header.AddRange(bands.Select(b => (object?)$"{C("reserve")}: {Band(b)}"));
        header.Add(C("reserveGel"));
        var rows = new List<IReadOnlyList<object?>> { header };
        var index = 0;
        foreach (var r in report.Rows)
        {
            var i = r.Impairment;
            var row = new List<object?>
            {
                ++index, r.PolicyNumber, Iso(r.CoverFrom), Iso(r.CancelledOn ?? r.CoverTo), Kind(r.PrimaryKind), r.Intermediaries ?? "",
                r.Policyholder, r.Classes, Ka.Party.Segment.TryGetValue(r.Segment, out var seg) ? seg : r.Segment, r.Insured ?? "", r.Beneficiary ?? "",
                Money(r.Premium), r.Currency,
            };
            row.AddRange(bands.Select(b => (object?)Money(i.Earned[b])));
            row.AddRange([Money(i.TotalEarned), Money(i.Paid), Money(r.UnpaidPremium), r.Rate, Money(r.UnpaidPremiumGel), Money(i.UnpaidEarned)]);
            row.AddRange(bands.Select(b => (object?)Money(i.Unpaid[b])));
            row.Add(Money(i.TotalReserve));
            row.AddRange(bands.Select(b => (object?)Money(i.Reserve[b])));
            row.Add(Money(r.ReserveGel));
            rows.Add(row);
        }
        var totals = new List<IReadOnlyList<object?>> { new object?[] { C("debtorKind"), C("policies"), C("unpaidPremiumGel"), C("unpaidEarnedGel"), C("reserveGel") } };
        totals.AddRange(report.Totals.Select(t => (IReadOnlyList<object?>)new object?[] { Kind(t.Kind), t.Policies, Money(t.UnpaidPremiumGel), Money(t.UnpaidEarnedGel), Money(t.ReserveGel) }));
        return XlsxLayout.ReadableWorkbook([new SheetRows("ფორმა", rows, 0), new SheetRows("N1-4", totals, 0)]);
    }
}
