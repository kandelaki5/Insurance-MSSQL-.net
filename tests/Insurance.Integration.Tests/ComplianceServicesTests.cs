using Dapper;
using Insurance.Application.Excel;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Compliance;

namespace Insurance.Integration.Tests;

/// <summary>
/// The compliance services on a real database: what KYC and the client risk read for a client (legacy src/lib/kyc.ts,
/// client-risk.ts), and the AML report's aggregates and workbook (legacy src/lib/aml-report.ts), each on fictional rows
/// of its own period.
/// </summary>
public sealed class ComplianceServicesTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync(sql, param);
    }

    [Fact]
    public async Task KYC_reads_the_calendar_years_non_life_premium_and_the_owners_and_counts_the_clients_with_issues()
    {
        var actor = await Fixtures.UserAsync(db);
        var asOf = new DateOnly(2033, 6, 1);
        var person = await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2033, 2, 1), premium: 3500m));
        var status = await db.App.ReadTxAsync(tx => PartyKyc.LoadKycStatusAsync(tx, person.PartyId, asOf));
        Assert.NotNull(status);
        Assert.Equal((3500m, false, new DateOnly(2033, 2, 1)), (status.Input.AnnualPremiumGel, status.Input.HasLifePolicy, status.Input.RelationshipFrom));
        Assert.Equal(KycTier.Verification, status.Status.Tier);
        Assert.Equal([KycIssue.NotIdentified, KycIssue.ClientVerificationRequired], status.Status.Issues);
        // Next year the premium of this year does not count.
        Assert.Equal(KycTier.Reduced, (await db.App.ReadTxAsync(tx => PartyKyc.LoadKycStatusAsync(tx, person.PartyId, new DateOnly(2034, 3, 1))))!.Status.Tier);

        // An organization above 5 000 with an unverified owner, 30 days after the relationship began.
        var org = Guid.NewGuid();
        await ExecAsync("""
            insert core.party (id, kind, name, tax_id, identified_at, verified_at) values (@org, N'organization', N'შპს KYC', @taxId, '2033-01-05', '2033-01-06');
            update core.contract set policyholder_id = @org where policyholder_id = @person;
            insert core.party_beneficial_owner (party_id, first_name, last_name, criterion, share_percent, identified_at) values (@org, N'ნიკა', N'მფლობელი', N'share', 100, '2033-01-05');
            insert core.party_beneficial_owner (party_id, first_name, last_name, criterion, share_percent, valid_to) values (@org, N'ძველი', N'მფლობელი', N'share', 100, '2033-01-01');
            """, new { org, taxId = Fixtures.RandomTaxId()[..9], person = person.PartyId });
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("""
            insert core.premium_transaction (policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
            values (@line, N'correction', 2000, 2000, '2033-03-01', '2033-03-01', '2034-02-01', @actor)
            """, new { line = person.LineId, actor }));
        var orgStatus = (await db.App.ReadTxAsync(tx => PartyKyc.LoadKycStatusAsync(tx, org, asOf)))!;
        Assert.Equal(5500m, orgStatus.Input.AnnualPremiumGel);
        Assert.Single(orgStatus.Owners);
        Assert.Equal([KycIssue.NoSelfDeclaration, KycIssue.OwnerVerificationOverdue], orgStatus.Status.Issues);
        Assert.Equal(new DateOnly(2033, 3, 3), orgStatus.Status.OwnerVerificationDueOn);

        var count = await db.App.ReadTxAsync(tx => PartyKyc.PartiesWithKycIssuesAsync(tx, asOf));
        var expected = 0;
        foreach (var id in await db.App.ReadTxAsync(tx => tx.QueryAsync<Guid>("select id from core.party")))
            if ((await db.App.ReadTxAsync(tx => PartyKyc.LoadKycStatusAsync(tx, id, asOf)))!.Status.Issues.Count > 0) expected++;
        Assert.Equal(expected, count);
        Assert.True(count >= 1);
        Assert.Null(await db.App.ReadTxAsync(tx => PartyKyc.LoadKycStatusAsync(tx, Guid.NewGuid(), asOf)));
    }

    [Fact]
    public async Task The_risk_reads_this_and_last_years_premium_and_the_settings_and_records_an_override_with_its_own_review_date()
    {
        var actor = await Fixtures.UserAsync(db);
        var p = await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2035, 1, 10), premium: 60000m));
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("""
            insert core.premium_transaction (policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
            values (@line, N'correction', 1000, 1000, '2034-12-01', '2035-01-10', '2036-01-10', @actor)
            """, new { line = p.LineId, actor }));
        await ExecAsync("update core.party set country_code = N'IR', is_resident = 0, foreign_id = N'IR-1', sanctions_checked_at = '2033-01-01' where id = @id", new { id = p.PartyId });
        await ExecAsync("""
            merge core.setting as s using (select N'aml.high_risk_countries' as [key], N'kp, ir' as value union all select N'aml.review_months_low', N'6') as n on s.[key] = n.[key]
            when matched then update set value = n.value when not matched then insert ([key], value) values (n.[key], n.value);
            """);
        var asOf = new DateOnly(2035, 3, 1);
        var computed = (await db.App.ReadTxAsync(tx => PartyRisk.ComputePartyRiskAsync(tx, p.PartyId, asOf)))!;
        Assert.Equal((60000m, 1000m), (computed.Facts.AnnualPremiumGel, computed.Facts.PreviousYearPremiumGel));
        Assert.Equal([FactorCode.HighRiskCountry, FactorCode.NotVerified, FactorCode.SanctionsCheckStale, FactorCode.PremiumVeryHigh, FactorCode.PremiumJump],
            computed.Assessment.Factors.Select(f => f.Code));
        Assert.Equal(RiskLevel.High, computed.Assessment.Level);

        await db.App.AsUserAsync(actor, async tx => { await PartyRisk.RecordPartyRiskAsync(tx, p.PartyId, actor, asOf, computed.Assessment, new PartyRisk.Override(RiskLevel.Low, "დოკუმენტებით დადასტურდა")); return 0; });
        await using var c = await db.OpenAdminAsync();
        Assert.Equal(("low", new DateTime(2035, 9, 1), new DateTime(2035, 3, 1)), await c.QuerySingleAsync<(string, DateTime, DateTime)>(
            "select risk_level, risk_review_on, risk_assessed_on from core.party where id = @id", new { id = p.PartyId }));
        Assert.Equal(("high", "low", true, actor), await c.QuerySingleAsync<(string, string, bool, Guid)>(
            "select computed_level, level, is_override, assessed_by from core.party_risk_assessment where party_id = @id", new { id = p.PartyId }));
        await ExecAsync("delete core.setting where [key] in (N'aml.high_risk_countries', N'aml.review_months_low')");
    }

    [Fact]
    public async Task The_AML_report_counts_clients_premium_operations_suspensions_and_reports_of_its_period_and_writes_its_workbook()
    {
        var actor = await Fixtures.UserAsync(db);
        var resident = await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2031, 2, 1)));
        var foreigner = await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2031, 3, 1), premium: 800m));
        await ExecAsync("""
            update core.party set is_resident = 0, country_code = N'DE', foreign_id = N'DE-1', sanctions_blocked = 1 where id = @foreigner;
            insert core.party_risk_assessment (party_id, assessed_on, computed_level, level, score, factors, review_due_on) values (@resident, '2031-01-10', N'high', N'high', 5, N'[]', '2032-01-10');
            insert core.payment (received_on, amount_gel, method, payer_party_id) values ('2031-04-01', 3500, N'cash', @resident), ('2031-04-02', 3000, N'bank', @resident), ('2031-05-02', 4000.5, N'bank', @foreigner), ('2031-08-01', 9000, N'cash', @resident);
            insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at) values (newid(), @resident, N'written', N'all', '2031-03-01T10:00:00+04:00', '2031-03-05T10:00:00+04:00');
            insert core.aml_case (number, party_id, opened_on, source, description, status, examined_on, examination, suspicion_at, str_sent_at, str_type, class_code)
            values (N'AML-R-1', @foreigner, '2031-04-01', N'monitoring', N'x', N'reported', '2031-04-01', N'y', '2031-04-01T09:00:00+04:00', '2031-04-02T11:15:00+04:00', N'ml', N'5');
            """, new { resident = resident.PartyId, foreigner = foreigner.PartyId });

        var period = AmlReport.AmlPeriod(2031, 1);
        Assert.Equal((new DateOnly(2031, 1, 1), new DateOnly(2031, 6, 30), "2031 I ნახევარი"), (period.From, period.To, period.Label));
        var r = await db.App.ReadTxAsync(tx => AmlReport.BuildAsync(tx, period));
        Assert.Equal([new AmlReport.ClientGroupRow("high:person_resident", 0, 1, 120000, 1), new AmlReport.ClientGroupRow("none:person_non_resident", 0, 1, 80000, 1)], r.ByRisk);
        Assert.Equal([new AmlReport.ClientGroupRow("life", 0, 0, 0, 0), new AmlReport.ClientGroupRow("other", 0, 2, 200000, 2)], r.ByLine);
        Assert.Equal([new AmlReport.CountryRow("GE", 1, 0, 120000, 0, 1, 0), new AmlReport.CountryRow("DE", 1, 0, 80000, 0, 1, 0)], r.ByCountry);
        Assert.Equal([new AmlReport.OperationRow("premium_received", 1, 400050), new AmlReport.OperationRow("premium_refunded", 0, 0), new AmlReport.OperationRow("claims_paid", 0, 0)], r.NonCash);
        Assert.Equal([new AmlReport.OperationRow("premium_received", 1, 350000), new AmlReport.OperationRow("premium_refunded", 0, 0), new AmlReport.OperationRow("claims_paid", 0, 0)], r.Cash);
        Assert.Equal([new AmlReport.SanctionedRow("DE", 1, 0), new AmlReport.SanctionedRow("GE", 0, 1)], r.Sanctioned);
        var str = Assert.Single(r.StrReports);
        Assert.Equal(new AmlReport.StrRow("AML-R-1", "02.04.2031 11:15", "ml", false, "DE", "5", str.ClassName, null), str);
        // The second half has none of it but still knows the clients active at its start.
        var second = await db.App.ReadTxAsync(tx => AmlReport.BuildAsync(tx, AmlReport.AmlPeriod(2031, 2)));
        Assert.Equal([new AmlReport.ClientGroupRow("high:person_resident", 1, 0, 0, 1), new AmlReport.ClientGroupRow("none:person_non_resident", 1, 0, 0, 1)], second.ByRisk);
        Assert.Equal(1, second.Cash[0].Count);

        var book = XlsxReader.Read(AmlReport.Workbook(r));
        Assert.Equal(["N2", "N3", "N4", "N5", "N6", "N8", "N9"], book.SheetNames);
        var n2 = SheetToJson.ToArrays(book.Sheets["N2"]);
        Assert.Equal([AmlTexts.Columns["risk"], AmlTexts.Columns["segment"], AmlTexts.Columns["activeAtStart"], AmlTexts.Columns["issuedClients"], AmlTexts.Columns["issuedPremiumGel"], AmlTexts.Columns["activeAtEnd"]], n2[0]);
        Assert.Equal([AmlTexts.Risk["high"], AmlTexts.Segment["person_resident"], 0d, 1d, 1200d, 1d], n2[1]);
        var n5 = SheetToJson.ToArrays(book.Sheets["N5"]);
        Assert.Equal([AmlTexts.Operation["premium_received"], 1d, 4000.5d], n5[1]);
        var n9 = SheetToJson.ToArrays(book.Sheets["N9"]);
        Assert.Equal([AmlTexts.Cases.StrTypes["ml"], AmlTexts.Residency["non_resident"], "DE", $"5. {str.ClassName}", "", "AML-R-1", "02.04.2031 11:15"], n9[1]);
        // A blocked client counts in form №8 whatever the period (its client facts are not limited to the period).
        Assert.Contains(new AmlReport.SanctionedRow("DE", 1, 0), (await db.App.ReadTxAsync(tx => AmlReport.BuildAsync(tx, AmlReport.AmlPeriod(2040, 0)))).Sanctioned);
        // An empty form №8 still has its header and one empty row.
        var empty = XlsxReader.Read(AmlReport.Workbook(new AmlReport.Report(AmlReport.AmlPeriod(2040, 0), [], [], [], [], [], [], [])));
        Assert.Equal(["", 0d, 0d], SheetToJson.ToArrays(empty.Sheets["N8"])[1]);
    }
}
