using Dapper;
using Insurance.Application.Data;

namespace Insurance.Integration.Tests;

/// <summary>Fictional rows for the compliance tests (claims, legacy suspensions, the UN list file).</summary>
public static class ComplianceFixtures
{
    /// <summary>An approved 1 000 GEL claim on the policy's line, with its loss event.</summary>
    public static async Task<(Guid ClaimId, string Number)> ClaimAsync(Tx tx, Fixtures.PolicyRows policy, string? number = null)
    {
        var id = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        number ??= "CLM-T" + id.ToString("N")[..8].ToUpperInvariant();
        await tx.ExecuteAsync("""
            insert core.loss_event (id, number, policy_id, occurred_on, reported_on, description) values (@eventId, @eventNo, @policy, '2026-03-01', '2026-03-02', N'სატესტო შემთხვევა');
            insert core.claim (id, number, policy_id, policy_line_id, currency, occurred_on, reported_on, description, status, approved_amount, loss_event_id)
            values (@id, @number, @policy, @line, N'GEL', '2026-03-01', '2026-03-02', N'სატესტო ზარალი', N'approved', 1000, @eventId);
            """, new { eventId, eventNo = "EV-" + number, policy = policy.PolicyId, id, number, line = policy.LineId });
        return (id, number);
    }

    /// <summary>
    /// A suspension saved before targets existed whose number matched nothing (legacy migration 0022 marks such rows). Only
    /// that migration may set the mark, so the test owner switches the target trigger off for this one insert, as the
    /// legacy acceptance test did on its test database.
    /// </summary>
    public static async Task<Guid> LegacyUnmappedSuspensionAsync(TestDatabase db, Guid partyId, string reference)
    {
        var id = Guid.NewGuid();
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("""
            disable trigger core.fms_suspension_insert on core.fms_suspension;
            insert core.fms_suspension (id, party_id, kind, scope, transaction_ref, legacy_unmapped, instructed_at, ends_at)
            values (@id, @partyId, N'written', N'transaction', @reference, 1, sysdatetimeoffset(), dateadd(day, 3, sysdatetimeoffset()));
            enable trigger core.fms_suspension_insert on core.fms_suspension;
            """, new { id, partyId, reference });
        return id;
    }

    /// <summary>A small UN consolidated list in the list's own XML format, written to a file of this run (never the network).</summary>
    /// <param name="extraIndividuals">More INDIVIDUAL elements, after the list's own two.</param>
    /// <param name="entityReference">The entity's reference number: another one is the same list date with other content.</param>
    public static string UnListFile(string generated = "2026-10-02T23:00:05.487Z", string extraIndividuals = "", string entityReference = "CDe.001")
    {
        var path = Path.Combine(Path.GetTempPath(), $"un-list-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0"?>
            <CONSOLIDATED_LIST dateGenerated="{generated}">
              <INDIVIDUALS>
                <INDIVIDUAL>
                  <DATAID>6907993</DATAID>
                  <FIRST_NAME>ERIC</FIRST_NAME>
                  <SECOND_NAME>BADEGE</SECOND_NAME>
                  <UN_LIST_TYPE>DRC</UN_LIST_TYPE>
                  <REFERENCE_NUMBER>CDi.001</REFERENCE_NUMBER>
                  <LISTED_ON>2012-12-31-04:00</LISTED_ON>
                  <NATIONALITY><VALUE>Democratic Republic of the Congo</VALUE></NATIONALITY>
                  <INDIVIDUAL_DATE_OF_BIRTH><TYPE_OF_DATE>EXACT</TYPE_OF_DATE><YEAR>1971</YEAR></INDIVIDUAL_DATE_OF_BIRTH>
                  <INDIVIDUAL_DOCUMENT><TYPE_OF_DOCUMENT>Passport</TYPE_OF_DOCUMENT><NUMBER>AB-12 3456</NUMBER></INDIVIDUAL_DOCUMENT>
                </INDIVIDUAL>
                <INDIVIDUAL>
                  <DATAID>1</DATAID>
                  <FIRST_NAME>Zurab</FIRST_NAME>
                  <SECOND_NAME>Sankcioneli</SECOND_NAME>
                  <UN_LIST_TYPE>Test</UN_LIST_TYPE>
                  <REFERENCE_NUMBER>T.001</REFERENCE_NUMBER>
                  <INDIVIDUAL_ALIAS><QUALITY>Good</QUALITY><ALIAS_NAME>Zurabi Sanktsioneli</ALIAS_NAME></INDIVIDUAL_ALIAS>
                  <INDIVIDUAL_DATE_OF_BIRTH><DATE>1980-05-01</DATE></INDIVIDUAL_DATE_OF_BIRTH>
                </INDIVIDUAL>
                {extraIndividuals}
              </INDIVIDUALS>
              <ENTITIES>
                <ENTITY>
                  <DATAID>6908402</DATAID>
                  <FIRST_NAME>ADF</FIRST_NAME>
                  <UN_LIST_TYPE>DRC</UN_LIST_TYPE>
                  <REFERENCE_NUMBER>{entityReference}</REFERENCE_NUMBER>
                  <ENTITY_ALIAS><QUALITY>a.k.a.</QUALITY><ALIAS_NAME>Allied Democratic Forces</ALIAS_NAME></ENTITY_ALIAS>
                </ENTITY>
              </ENTITIES>
            </CONSOLIDATED_LIST>
            """);
        return path;
    }
}
