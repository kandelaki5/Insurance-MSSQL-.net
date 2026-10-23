using Insurance.Application.Data;
using Insurance.Domain.Regulatory;

namespace Insurance.Application.Services;

/// <summary>Form 6's raw rows, read as stored (legacy src/lib/regulatory/form6-source.ts: four plain reads; the choosing, ordering and de-duplication is Form6Rows').</summary>
public static class Form6SourceReader
{
    // uuids as PostgreSQL text (lower case) and ordered as it orders them.
    private const string Uuid = "lower(convert(nchar(36), {0}))";
    private static string U(string column) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Uuid, column);

    public static async Task<Form6Source> ReadAsync(Tx db, DateOnly asOf)
    {
        var treaties = (await db.QueryAsync<Form6Treaty>($"""
            select {U("t.id")} as Id, t.number as Number, t.kind as Kind, convert(nvarchar(10), t.period_from, 23) as PeriodFrom, convert(nvarchar(10), t.period_to, 23) as PeriodTo,
                   t.reinsurer_rating as ReinsurerRating, t.rating_agency as RatingAgency,
                   r.display_name as Reinsurer, rtrim(r.country_code) as ReinsurerCountry, b.display_name as Broker, rtrim(b.country_code) as BrokerCountry
            from core.ri_treaty t join core.party r on r.id = t.reinsurer_party_id left join core.party b on b.id = t.broker_party_id
            order by t.number
            """)).ToList();
        var treatyClasses = (await db.QueryAsync<Form6TreatyClass>($"""
            select {U("treaty_id")} as TreatyId, class_code as ClassCode from core.ri_treaty_class
            order by {U("treaty_id")} collate Latin1_General_BIN2, class_code
            """)).ToList();
        // The classes of the policy lines placed with a treaty by the day (a facultative placement lists no class of its own).
        var placedClasses = (await db.QueryAsync<Form6TreatyClass>($"""
            select TreatyId, ClassCode from (
              select distinct {U("c.treaty_id")} as TreatyId, l.class_code as ClassCode
              from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id join core.policy_line l on l.id = t.policy_line_id
              where c.booked_on <= @asOf
            ) placed
            order by TreatyId collate Latin1_General_BIN2, ClassCode
            """, new { asOf })).ToList();
        var classes = (await db.QueryAsync<Form6Class>("select code as Code, name_ka as NameKa, sort_order as SortOrder from core.insurance_class order by sort_order")).ToList();
        return new Form6Source(treaties, treatyClasses, placedClasses, classes);
    }
}
