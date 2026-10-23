using Insurance.Application.Data;
using Insurance.Application.Excel;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;

namespace Insurance.Application.Services;

/// <summary>
/// AML supervisory report of the insurer (order №47/2020, forms №1–10): the aggregates the forms need for a half-year or
/// a year, computed from policies, clients, payments and claims (legacy src/lib/aml-report.ts). Rules (order №47 art.
/// 1–2): premium by issued policy regardless of payment, increases count and reductions do not; GEL at the policy's
/// effective-date rate; "life" means investment-type life insurance; residency by citizenship / place of incorporation;
/// clients of foreign-registered vehicles' compulsory MTPL are excluded (no such product yet). Amounts are whole tetri.
/// </summary>
public static partial class AmlReport
{
    public sealed record Period(DateOnly From, DateOnly To, string Label);

    public sealed record ClientGroupRow(string Key, int ActiveAtStart, int IssuedClients, long IssuedPremiumGel, int ActiveAtEnd);

    public sealed record CountryRow(string CountryCode, int PersonClients, int OtherClients, long PersonPremiumGel, long OtherPremiumGel, int PersonActiveAtEnd, int OtherActiveAtEnd);

    public sealed record OperationRow(string Kind, int Count, long AmountGel);

    public sealed record SanctionedRow(string CountryCode, int Clients, int Suspended);

    public sealed record StrRow(string CaseNumber, string SentAt, string StrType, bool IsResident, string CountryCode, string? ClassCode, string? ClassName, string? OtherBasis);

    /// <param name="ByRisk">Form №2: clients by risk level × (person/organization × resident/non-resident).</param>
    /// <param name="ByCountry">Form №3: clients by country of residency.</param>
    /// <param name="ByLine">Form №4: life (investment) vs other classes.</param>
    /// <param name="NonCash">Form №5: non-cash operations above 3 000 GEL each (country of the counterparty's bank: GE assumed).</param>
    /// <param name="Cash">Form №6: cash operations above 3 000 GEL each.</param>
    /// <param name="Sanctioned">Form №8: by country, clients whose assets are frozen (confirmed sanctions match) and clients whose operations were suspended on the FMS's instruction during the period.</param>
    /// <param name="StrReports">Form №9: one row per suspicious transaction report sent to the FMS in the period (art. 12).</param>
    public sealed record Report(
        Period Period, IReadOnlyList<ClientGroupRow> ByRisk, IReadOnlyList<CountryRow> ByCountry, IReadOnlyList<ClientGroupRow> ByLine,
        IReadOnlyList<OperationRow> NonCash, IReadOnlyList<OperationRow> Cash, IReadOnlyList<SanctionedRow> Sanctioned, IReadOnlyList<StrRow> StrReports);

    /// <summary>3 000 GEL: single-operation threshold of forms №5 and №6 (order №47 art. 8–9).</summary>
    public const decimal OperationThresholdGel = 3_000m;

    /// <summary>1 = first half, 2 = second half, 0 = the whole year.</summary>
    public static Period AmlPeriod(int year, int half) => half switch
    {
        1 => new Period(new DateOnly(year, 1, 1), new DateOnly(year, 6, 30), $"{year} I ნახევარი"),
        2 => new Period(new DateOnly(year, 7, 1), new DateOnly(year, 12, 31), $"{year} II ნახევარი"),
        _ => new Period(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), $"{year} წელი"),
    };

    /// <summary>The period of the address: a four-digit year (else this year) and half=2 / half=year (else the first half).</summary>
    public static (Period Period, string HalfValue) ParsePeriod(string? year, string? half, DateOnly today)
    {
        var y = year is { Length: 4 } && year.All(c => c is >= '0' and <= '9') ? int.Parse(year, System.Globalization.CultureInfo.InvariantCulture) : today.Year;
        var h = half == "2" ? 2 : half == "year" ? 0 : 1;
        return (AmlPeriod(y, h), half == "2" ? "2" : half == "year" ? "year" : "1");
    }

    private sealed record ClientFact(
        Guid PartyId, string Kind, bool IsResident, string CountryCode, string? RiskLevel, bool SanctionsBlocked, bool ActiveAtStart, bool ActiveAtEnd,
        bool IssuedInPeriod, decimal IssuedPremiumGel, decimal IssuedLifePremiumGel, decimal IssuedOtherPremiumGel, bool HasLife, bool HasOther);

    private static async Task<List<ClientFact>> ClientFactsAsync(Tx db, Period p)
    {
        var dayBefore = Dates.AddDays(p.From, -1);
        return (await db.QueryAsync<ClientFact>("""
            with policies as (
              select po.id, c.policyholder_id, po.effective_from, po.effective_to, po.cancelled_on
              from core.policy po join core.contract c on c.id = po.contract_id
              where po.status in (N'active', N'cancelled')
            ),
            issued as (
              select po.policyholder_id,
                     sum(case when l.premium_gel > 0 then l.premium_gel else 0 end) as premium_gel,
                     sum(case when l.premium_gel > 0 and ic.category = N'life' then l.premium_gel else 0 end) as life_premium_gel,
                     sum(case when l.premium_gel > 0 and ic.category <> N'life' then l.premium_gel else 0 end) as other_premium_gel,
                     max(case when ic.category = N'life' then 1 else 0 end) as has_life, max(case when ic.category <> N'life' then 1 else 0 end) as has_other
              from policies po join core.policy_line l on l.policy_id = po.id join core.insurance_class ic on ic.code = l.class_code
              where po.effective_from >= @from and po.effective_from <= @to
              group by po.policyholder_id
            )
            select pa.id as PartyId, pa.kind as Kind, pa.is_resident as IsResident, pa.country_code as CountryCode,
                   coalesce((select top (1) a.level from core.party_risk_assessment a where a.party_id = pa.id and a.assessed_on <= @to order by a.created_at desc, a.id desc), pa.risk_level) as RiskLevel,
                   pa.sanctions_blocked as SanctionsBlocked,
                   convert(bit, case when exists (select 1 from policies po where po.policyholder_id = pa.id and po.effective_from <= @dayBefore and po.effective_to > @dayBefore
                           and (po.cancelled_on is null or po.cancelled_on > @dayBefore)) then 1 else 0 end) as ActiveAtStart,
                   convert(bit, case when exists (select 1 from policies po where po.policyholder_id = pa.id and po.effective_from <= @to and po.effective_to > @to
                           and (po.cancelled_on is null or po.cancelled_on > @to)) then 1 else 0 end) as ActiveAtEnd,
                   convert(bit, case when i.policyholder_id is not null then 1 else 0 end) as IssuedInPeriod,
                   coalesce(i.premium_gel, 0) as IssuedPremiumGel,
                   coalesce(i.life_premium_gel, 0) as IssuedLifePremiumGel,
                   coalesce(i.other_premium_gel, 0) as IssuedOtherPremiumGel,
                   convert(bit, coalesce(i.has_life, 0)) as HasLife, convert(bit, coalesce(i.has_other, 0)) as HasOther
            from core.party pa left join issued i on i.policyholder_id = pa.id
            where exists (select 1 from policies po where po.policyholder_id = pa.id)
            order by pa.created_at, pa.id
            """, new { from = p.From, to = p.To, dayBefore })).ToList();
    }

    private static long Minor(decimal value) => (long)decimal.Round(value * 100m, MidpointRounding.AwayFromZero);

    private sealed class Group(string key)
    {
        public string Key { get; } = key;
        public int ActiveAtStart, IssuedClients, ActiveAtEnd;
        public long IssuedPremiumGel;
        public ClientGroupRow Row() => new(Key, ActiveAtStart, IssuedClients, IssuedPremiumGel, ActiveAtEnd);
    }

    private static List<ClientGroupRow> GroupBy(IEnumerable<ClientFact> facts, Func<ClientFact, string?> keyOf, Func<ClientFact, long>? premiumOf = null)
    {
        premiumOf ??= f => Minor(f.IssuedPremiumGel);
        var rows = new List<Group>();
        foreach (var f in facts)
        {
            var key = keyOf(f);
            if (key is null) continue;
            var row = rows.FirstOrDefault(r => r.Key == key);
            if (row is null) rows.Add(row = new Group(key));
            if (f.ActiveAtStart) row.ActiveAtStart++;
            if (f.ActiveAtEnd) row.ActiveAtEnd++;
            var premium = premiumOf(f);
            if (f.IssuedInPeriod && premium > 0) { row.IssuedClients++; row.IssuedPremiumGel += premium; }
        }
        return rows.Select(r => r.Row()).ToList();
    }

    private sealed record OperationSql(string Channel, string Kind, int Count, decimal? AmountGel);

    public static async Task<Report> BuildAsync(Tx db, Period period)
    {
        var facts = await ClientFactsAsync(db, period);
        static string Segment(ClientFact f) => $"{f.Kind}_{(f.IsResident ? "resident" : "non_resident")}";

        var byRisk = GroupBy(facts, f => $"{f.RiskLevel ?? "none"}:{Segment(f)}");
        var byLine = GroupBy(facts, _ => "life", f => Minor(f.IssuedLifePremiumGel)).Select(r => r with { ActiveAtStart = 0, ActiveAtEnd = 0 })
            .Concat(GroupBy(facts, _ => "other", f => Minor(f.IssuedOtherPremiumGel))).ToList();

        var countries = new List<CountryRow>();
        foreach (var f in facts)
        {
            var code = JsText.ToUpperCase(f.CountryCode);
            var index = countries.FindIndex(c => c.CountryCode == code);
            var row = index >= 0 ? countries[index] : new CountryRow(code, 0, 0, 0, 0, 0, 0);
            var premium = Minor(f.IssuedPremiumGel);
            if (f.IssuedInPeriod && premium > 0)
                row = f.Kind == "person"
                    ? row with { PersonClients = row.PersonClients + 1, PersonPremiumGel = row.PersonPremiumGel + premium }
                    : row with { OtherClients = row.OtherClients + 1, OtherPremiumGel = row.OtherPremiumGel + premium };
            if (f.ActiveAtEnd)
                row = f.Kind == "person" ? row with { PersonActiveAtEnd = row.PersonActiveAtEnd + 1 } : row with { OtherActiveAtEnd = row.OtherActiveAtEnd + 1 };
            if (index >= 0) countries[index] = row; else countries.Add(row);
        }
        // Georgia first, then the codes in order (localeCompare of ASCII codes).
        var byCountry = countries.OrderBy(c => c.CountryCode == "GE" ? 0 : 1).ThenBy(c => c.CountryCode, StringComparer.InvariantCulture).ToList();

        var operations = (await db.QueryAsync<OperationSql>("""
            select x.Channel, x.Kind, count(*) as Count, sum(x.Amount) as AmountGel
            from (
              select case when method = N'cash' then N'cash' else N'non_cash' end as Channel,
                     case when kind = N'receipt' then N'premium_received' else N'premium_refunded' end as Kind,
                     abs(amount_gel) as Amount
              from core.payment
              where method <> N'claim_offset' and kind in (N'receipt', N'refund') and abs(amount_gel) > @threshold
                and received_on >= @from and received_on <= @to
            ) x
            group by x.Channel, x.Kind
            union all
            -- A claim payment's reversal is not an operation of its own, and the payment stays one (docs/07 §4.3ა ზ, B1: until the
            -- owner answers, as a premium receipt's reversal is treated above).
            select N'non_cash', N'claims_paid', count(*), sum(net_paid_gel)
            from core.claim_payment
            where kind = N'payment' and net_paid_gel > @threshold and payee_kind <> N'workshop' and paid_on >= @from and paid_on <= @to
            """, new { threshold = OperationThresholdGel, from = period.From, to = period.To })).ToList();
        List<OperationRow> ToRows(string channel) =>
            new[] { "premium_received", "premium_refunded", "claims_paid" }.Select(kind =>
            {
                var row = operations.FirstOrDefault(o => o.Channel == channel && o.Kind == kind);
                return new OperationRow(kind, row?.Count ?? 0, row is null ? 0 : Minor(row.AmountGel ?? 0));
            }).ToList();

        var sanctioned = new List<(string Code, int Clients, int Suspended)>();
        void Add(string code, int clients, int suspended)
        {
            var key = JsText.ToUpperCase(code);
            var i = sanctioned.FindIndex(s => s.Code == key);
            if (i < 0) sanctioned.Add((key, clients, suspended));
            else sanctioned[i] = (key, sanctioned[i].Clients + clients, sanctioned[i].Suspended + suspended);
        }
        foreach (var f in facts.Where(x => x.SanctionsBlocked)) Add(f.CountryCode, 1, 0);
        // Every instruction received in the period counts: an oral one suspends operations from the moment it is received
        // and stays in force until the officer resumes them (art. 36 p. 3).
        var suspended = await db.QueryAsync<(string CountryCode, int Clients)>("""
            select pa.country_code, count(distinct pa.id)
            from core.fms_suspension s join core.party pa on pa.id = s.party_id
            where convert(date, switchoffset(s.instructed_at, '+04:00')) between @from and @to
            group by pa.country_code
            """, new { from = period.From, to = period.To });
        foreach (var s in suspended) Add(s.CountryCode, 0, s.Clients);

        var strReports = (await db.QueryAsync<StrRow>("""
            select c.number as CaseNumber, format(switchoffset(c.str_sent_at, '+04:00'), 'dd.MM.yyyy HH:mm') as SentAt, c.str_type as StrType,
                   pa.is_resident as IsResident, pa.country_code as CountryCode, c.class_code as ClassCode, ic.name_ka as ClassName, c.str_other_basis as OtherBasis
            from core.aml_case c join core.party pa on pa.id = c.party_id left join core.insurance_class ic on ic.code = c.class_code
            where c.str_sent_at is not null and convert(date, switchoffset(c.str_sent_at, '+04:00')) between @from and @to
            order by c.str_sent_at
            """, new { from = period.From, to = period.To })).Select(r => r with { CountryCode = JsText.ToUpperCase(r.CountryCode) }).ToList();

        return new Report(period, byRisk, byCountry, byLine, ToRows("non_cash"), ToRows("cash"),
            sanctioned.Select(s => new SanctionedRow(s.Code, s.Clients, s.Suspended)).ToList(), strReports);
    }

    /// <summary>Whole tetri as the old money(): Number((minor / 100).toFixed(2)), a JavaScript number.</summary>
    private static double Money(long tetri) => double.Parse(Billing.TetriText(tetri), System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<KeyValuePair<string, object?>> Record(params (string Column, object? Value)[] cells) =>
        cells.Select(c => new KeyValuePair<string, object?>(AmlTexts.Columns[c.Column], c.Value));

    /// <summary>The report as an Excel workbook with one sheet per form, in the forms' own column order (legacy amlWorkbook).</summary>
    public static byte[] Workbook(Report r)
    {
        static string Label(IReadOnlyDictionary<string, string> map, string key) => map.GetValueOrDefault(key) ?? key;
        var book = new XlsxBook();
        var n2 = r.ByRisk.Select(row =>
        {
            var parts = row.Key.Split(':');
            return Record(("risk", Label(AmlTexts.Risk, parts[0])), ("segment", Label(AmlTexts.Segment, parts[1])), ("activeAtStart", row.ActiveAtStart),
                ("issuedClients", row.IssuedClients), ("issuedPremiumGel", Money(row.IssuedPremiumGel)), ("activeAtEnd", row.ActiveAtEnd));
        }).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(n2), "N2");
        var n3 = r.ByCountry.Select(c => Record(("country", c.CountryCode), ("personClients", c.PersonClients), ("otherClients", c.OtherClients),
            ("personPremiumGel", Money(c.PersonPremiumGel)), ("otherPremiumGel", Money(c.OtherPremiumGel)), ("personActiveAtEnd", c.PersonActiveAtEnd),
            ("otherActiveAtEnd", c.OtherActiveAtEnd))).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(n3), "N3");
        var n4 = r.ByLine.Select(row => Record(("line", Label(AmlTexts.Line, row.Key)), ("activeAtStart", row.ActiveAtStart), ("issuedClients", row.IssuedClients),
            ("issuedPremiumGel", Money(row.IssuedPremiumGel)), ("activeAtEnd", row.ActiveAtEnd))).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(n4), "N4");
        List<IEnumerable<KeyValuePair<string, object?>>> Ops(IEnumerable<OperationRow> rows) =>
            rows.Select(o => Record(("operation", Label(AmlTexts.Operation, o.Kind)), ("count", o.Count), ("amountGel", Money(o.AmountGel)))).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(Ops(r.NonCash)), "N5");
        book.AppendSheet(XlsxWriter.JsonToSheet(Ops(r.Cash)), "N6");
        var n8 = r.Sanctioned.Select(s => Record(("country", s.CountryCode), ("frozenClients", s.Clients), ("suspendedClients", s.Suspended))).ToList();
        book.AppendSheet(XlsxWriter.JsonToSheet(n8.Count > 0 ? n8 : [Record(("country", ""), ("frozenClients", 0), ("suspendedClients", 0))]), "N8");
        var c9 = AmlTexts.Columns;
        IReadOnlyList<object?> header = [c9["strType"], c9["residency"], c9["country"], c9["line"], c9["additionalInfo"], c9["caseNumber"], c9["sentAt"]];
        var n9 = r.StrReports.Select(s => (IReadOnlyList<object?>)
        [
            Label(AmlTexts.Cases.StrTypes, s.StrType), s.IsResident ? AmlTexts.Residency["resident"] : AmlTexts.Residency["non_resident"], s.CountryCode,
            s.ClassCode is not null ? $"{s.ClassCode}. {s.ClassName ?? ""}" : "", s.OtherBasis ?? "", s.CaseNumber, s.SentAt,
        ]);
        book.AppendSheet(XlsxWriter.AoaToSheet(new[] { header }.Concat(n9)), "N9");
        return book.Write();
    }
}
