using System.Numerics;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Domain.Finance;
using PartyRole = Insurance.Domain.Common.PartyRole;

namespace Insurance.Integration.Tests;

/// <summary>
/// The regress read adapter on the real SQL Server (legacy src/lib/subrogation-report-store.test.ts, which fed it mock rows): the
/// rows of real cases, written by the real operations and audited by the real triggers, turned into exact values, and the real
/// card, report and period projections run on them, all in one snapshot.
/// </summary>
public sealed class SubrogationReportStoreTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2500;
    private static BigInteger Lari(decimal n) => new(decimal.Round(n * 100));
    private static Guid Key() => Guid.NewGuid();

    private sealed record Setup(int Y, Guid Actor, SubrogationTransact Transact, Dictionary<string, Guid> Claims, Guid Person, Guid Insurer);

    private async Task<Setup> SetupAsync()
    {
        var y = Interlocked.Increment(ref year);
        var actor = await w.UserAsync("admin");
        var p = await w.PolicyAsync(actor.Id, new DateOnly(y, 1, 1), [new("damage", 99999m, 0m), new("equipment", 99999m, 0m), new("theft", 99999m, 0m)]);
        var claims = new Dictionary<string, Guid>();
        foreach (var (name, coverage) in new[] { ("A", "damage"), ("B", "equipment"), ("OTHER", "theft") })
            claims[name] = (await w.RegisterAsync(actor, p, new DateOnly(y, 6, 1), new DateOnly(y, 6, 2), (coverage, "2000")))[0];
        foreach (var name in new[] { "A", "B" })
            await w.App.AsUserAsync(actor.Id, tx => tx.ExecuteAsync("""
                insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id)
                values (@claim, @on, 1000, 0, 0, 1000, 1000, 1, N'policyholder', @party)
                """, new { claim = claims[name], on = new DateOnly(y, 10, 1), party = p.PartyId }));
        // Old regress money and salvage outside any case (Q-R4): of A, and of another claim.
        await w.ExecAsync("""
            insert core.claim_recovery (claim_id, kind, received_on, amount_gel) values (@a, N'subrogation', @j1, 50), (@a, N'salvage', @j2, 30), (@o, N'subrogation', @a1, 70)
            """, new { a = claims["A"], o = claims["OTHER"], j1 = new DateOnly(y, 7, 10), j2 = new DateOnly(y, 7, 12), a1 = new DateOnly(y, 8, 1) });
        return new Setup(y, actor.Id, SubrogationCases.InDatabase(w.App, actor.Id), claims, await w.PartyAsync(name: "ბრალეული"), await w.PartyAsync(name: "მზღვეველი"));
    }

    private static async Task<Guid> Ok(Task<SubrogationResult> operation)
    {
        var r = await operation;
        Assert.True(r.Ok, r.Error);
        return r.Id!.Value;
    }

    [Fact]
    public void Amounts_as_text_become_exact_tetri_and_anything_else_is_refused_never_rounded()
    {
        Assert.Equal(new BigInteger(29), SubrogationReportStore.ExactTetri("0.29", "x"));
        Assert.Equal(new BigInteger(-60000), SubrogationReportStore.ExactTetri("-600.00", "x"));
        Assert.Equal(new BigInteger(100000), SubrogationReportStore.ExactTetri("1000", "x"));
        foreach (var bad in new[] { "1e3", "12.345", " 5", "", "--1", "NaN" })
            Assert.Throws<FormatException>(() => SubrogationReportStore.ExactTetri(bad, "x"));
        // Negative control: reading through a float and truncating loses a tetri.
        Assert.Equal(28L, (long)Math.Truncate(double.Parse("0.29", System.Globalization.CultureInfo.InvariantCulture) * 100));
        Assert.Equal("2026-10-31T20:30:00.000000Z", SubrogationReportStore.AuditInstant("2026-10-31T20:30:00.000000Z", "x"));
        Assert.Throws<FormatException>(() => SubrogationReportStore.AuditInstant("2026-10-31 20:30:00+00", "x"));
        Assert.Equal(new DateOnly(2026, 11, 1), SubrogationReport.TbilisiDay("2026-10-31T20:30:00.000000Z"));
    }

    [Fact]
    public void Every_statement_is_a_single_SELECT_with_parameters_only()
    {
        foreach (var sql in SubrogationReportStore.Sql.All)
        {
            var s = Regex.Replace(Regex.Replace(sql, "N?'[^']*'", "''"), "\"[^\"]*\"", "\"\"").ToLowerInvariant();
            Assert.Matches(@"^\s*select\b", s);
            Assert.DoesNotMatch(@"\b(insert|update|delete|merge|create|alter|drop|grant|revoke|truncate|exec|execute|set|declare)\b", s);
            Assert.DoesNotContain(";", s);
            Assert.DoesNotContain("{", s);
        }
    }

    [Fact]
    public async Task G23_G25_the_card_gives_0_1000_400_1000_800_and_names_the_payer()
    {
        var s = await SetupAsync();
        var caseId = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["A"], new DateOnly(s.Y, 10, 1), null, Key()));
        var payer = await Ok(SubrogationCases.AddPartyAsync(s.Transact, caseId, s.Person, PartyRole.LiablePerson, null, null, null));
        DateOnly D(int m, int d) => new(s.Y, m, d);
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, caseId, DocumentedMovement.Claimed, Lari(950), D(10, 3), DocumentKind.DemandLetter, null, D(10, 3), null, Key()));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, caseId, DocumentedMovement.Confirmed, Lari(950), D(10, 4), DocumentKind.DebtorAcceptance, null, D(10, 4), null, Key()));
        var r600 = await Ok(SubrogationCases.ReceiveAsync(s.Transact, caseId, payer, Lari(600), D(10, 10), "B-1", null, Key()));
        await Ok(SubrogationCases.ReverseAsync(s.Transact, caseId, r600, D(10, 10), "შეცდომა", Key()));
        await Ok(SubrogationCases.ReceiveAsync(s.Transact, caseId, payer, Lari(200), D(10, 11), null, null, Key()));
        var card = await SubrogationReportStore.CaseCardAsync(w.App, caseId, D(10, 31), D(10, 31));
        Assert.NotNull(card);
        Assert.Equal([BigInteger.Zero, Lari(950), Lari(350), Lari(950), Lari(750)], card.History.Select(h => h.BalanceAfter));
        Assert.Equal(Lari(750), card.Totals.Balance);
        Assert.Equal("ბრალეული ტესტიშვილი", card.History[2].Payer);
        Assert.Equal(MovementKind.Received, card.History[3].Undoes!.Kind);
        Assert.Null(await SubrogationReportStore.CaseCardAsync(w.App, Guid.NewGuid(), D(10, 31), D(10, 31)));
    }

    [Fact]
    public async Task H88_a_status_change_is_certain_only_on_the_days_before_it_began_and_a_broken_history_is_unknown()
    {
        var s = await SetupAsync();
        var caseId = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["A"], new DateOnly(2000, 1, 1), null, Key()));
        var party = await Ok(SubrogationCases.AddPartyAsync(s.Transact, caseId, s.Insurer, PartyRole.LiabilityInsurer, null, null, null));
        await Ok(SubrogationCases.RecordResponseAsync(s.Transact, caseId, party, SubrogationResponse.Disputed, new DateOnly(2000, 1, 2)));
        // The change was written now: its Tbilisi day is the day it began.
        var at = await w.ScalarAsync<string>("""
            select top (1) format(switchoffset(at, '+00:00'), 'yyyy-MM-dd\THH:mm:ss.ffffff\Z') from core.audit_log
            where table_name = N'subrogation_case' and row_id = @id and action = N'update' order by id desc
            """, new { id = caseId.ToString() });
        var began = SubrogationReport.TbilisiDay(at);
        Assert.Equal(CaseStatus.Opened, (await SubrogationReportStore.CaseCardAsync(w.App, caseId, began.AddDays(-1), began))!.Header.StatusOnDate.Value);
        Assert.True((await SubrogationReportStore.CaseCardAsync(w.App, caseId, began, began))!.Header.StatusOnDate.IsUnknown);
        Assert.True((await SubrogationReportStore.CaseCardAsync(w.App, caseId, new DateOnly(1999, 12, 31), began))!.Header.StatusOnDate.IsUnknown);
        // A history that does not end in the row as it is now is not reliable: unknown, not mended from today.
        await w.ExecAsync("""
            insert core.audit_log (table_name, row_id, action, new_row) values (N'subrogation_case', @id, N'update', N'{"status":"rejected"}')
            """, new { id = caseId.ToString() });
        Assert.True((await SubrogationReportStore.CaseCardAsync(w.App, caseId, began.AddDays(-1), began))!.Header.StatusOnDate.IsUnknown);
    }

    [Fact]
    public async Task H89_a_rejection_stays_in_force_against_a_later_dated_acceptance_and_a_later_confirmation_is_not_counted()
    {
        var s = await SetupAsync();
        DateOnly D(int m, int d) => new(s.Y, m, d);
        var caseId = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["B"], D(10, 1), null, Key()));
        var insurer = await Ok(SubrogationCases.AddPartyAsync(s.Transact, caseId, s.Insurer, PartyRole.LiabilityInsurer, null, null, 70000));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, caseId, DocumentedMovement.Confirmed, Lari(500), D(10, 1), DocumentKind.DebtorAcceptance, null, D(10, 1), null, Key()));
        await Ok(SubrogationCases.RecordResponseAsync(s.Transact, caseId, insurer, SubrogationResponse.Rejected, D(10, 15)));
        await Ok(SubrogationCases.RecordResponseAsync(s.Transact, caseId, insurer, SubrogationResponse.Accepted, D(11, 3)));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, caseId, DocumentedMovement.Confirmed, Lari(500), D(11, 1), DocumentKind.DebtorAcceptance, null, D(11, 1), null, Key()));
        var card = await SubrogationReportStore.CaseCardAsync(w.App, caseId, D(10, 31), D(10, 31));
        Assert.Equal(new ExpectedNow(D(10, 31), BigInteger.Zero, Lari(500)), card!.ExpectedNow);
        Assert.Equal(new ExpectedSplit(BigInteger.Zero, Lari(500)), card.Expected);
        Assert.Equal(new ExpectedNow(D(11, 3), Lari(700), Lari(300)), (await SubrogationReportStore.CaseCardAsync(w.App, caseId, D(11, 3), D(11, 3)))!.ExpectedNow);
    }

    private async Task<(Setup S, Guid CaseA, Guid CaseB)> TwoCasesAsync()
    {
        var s = await SetupAsync();
        DateOnly D(int m, int d) => new(s.Y, m, d);
        var a = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["A"], D(10, 1), null, Key()));
        var person = await Ok(SubrogationCases.AddPartyAsync(s.Transact, a, s.Person, PartyRole.LiablePerson, null, null, null));
        var insurer = await Ok(SubrogationCases.AddPartyAsync(s.Transact, a, s.Insurer, PartyRole.LiabilityInsurer, null, null, 70000));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, a, DocumentedMovement.Confirmed, Lari(900), D(10, 4), DocumentKind.DebtorAcceptance, null, D(10, 4), null, Key()));
        await Ok(SubrogationCases.ReceiveAsync(s.Transact, a, insurer, Lari(500), D(10, 10), null, null, Key()));
        var b = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["B"], D(10, 2), null, Key()));
        var bPerson = await Ok(SubrogationCases.AddPartyAsync(s.Transact, b, s.Person, PartyRole.LiablePerson, null, null, null));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, b, DocumentedMovement.Confirmed, Lari(300), D(10, 4), DocumentKind.DebtorAcceptance, null, D(10, 4), null, Key()));
        await Ok(SubrogationCases.ReceiveAsync(s.Transact, b, bPerson, Lari(300), D(10, 5), null, null, Key()));
        _ = person;
        return (s, a, b);
    }

    [Fact]
    public async Task QR1_both_cases_the_closed_one_too_with_the_totals_once_per_case_whoever_paid()
    {
        var (s, a, b) = await TwoCasesAsync();
        var asOf = new DateOnly(s.Y, 10, 31);
        var report = await SubrogationReportStore.ReportAsync(w.App, asOf);
        var mine = report.Rows.Where(r => r.ClaimNumber == Number(s, "A") || r.ClaimNumber == Number(s, "B")).ToList();
        Assert.Equal([Lari(400), BigInteger.Zero], mine.Select(r => r.Balance));
        Assert.Equal(["ბრალეული ტესტიშვილი", "მზღვეველი ტესტიშვილი"], mine[0].Debtors);
        Assert.Equal(Lari(800), mine.Aggregate(BigInteger.Zero, (t, r) => t + r.Received));
        Assert.Equal(CaseStatus.Closed, mine[1].StatusNow);
        var owed = await SubrogationReportStore.ReportAsync(w.App, asOf, onlyWithBalance: true);
        Assert.Contains(owed.Rows, r => r.ClaimNumber == Number(s, "A"));
        Assert.DoesNotContain(owed.Rows, r => r.ClaimNumber == Number(s, "B"));
        // A case opened later is not in the report on an earlier date.
        Assert.DoesNotContain((await SubrogationReportStore.ReportAsync(w.App, new DateOnly(s.Y, 10, 1))).Rows, r => r.ClaimNumber == Number(s, "B"));
        _ = (a, b);
    }

    private string Number(Setup s, string claim) => w.ScalarAsync<string>("select number from core.claim where id = @id", new { id = s.Claims[claim] }).GetAwaiter().GetResult();

    [Fact]
    public async Task QR4_old_regress_money_and_salvage_come_apart_of_every_claim_of_the_reports_claims_or_none_never_in_the_totals()
    {
        var (s, _, _) = await TwoCasesAsync();
        var asOf = new DateOnly(s.Y, 10, 31);
        Report Mine(Report r) => r;
        var own = await SubrogationReportStore.ReportAsync(w.App, asOf, outside: SubrogationReportStore.OutsideScope.ClaimsOfCases);
        var ownLines = own.Outside.Where(o => o.ClaimNumber == Number(s, "A")).ToList();
        Assert.Equal([(OutsideKind.Salvage, Lari(30)), (OutsideKind.Subrogation, Lari(50))], ownLines.Select(o => (o.Kind, o.Received)));
        Assert.DoesNotContain(own.Outside, o => o.ClaimNumber == Number(s, "OTHER"));
        var all = Mine(await SubrogationReportStore.ReportAsync(w.App, asOf));
        Assert.Contains(all.Outside, o => o.ClaimNumber == Number(s, "OTHER") && o.Received == Lari(70));
        var none = await SubrogationReportStore.ReportAsync(w.App, asOf, outside: SubrogationReportStore.OutsideScope.None);
        Assert.Empty(none.Outside);
        Assert.Equal(none.Total, all.Total);
    }

    [Fact]
    public async Task QR5_a_receipt_of_October_reversed_in_November_counts_in_each_month()
    {
        var s = await SetupAsync();
        DateOnly D(int m, int d) => new(s.Y, m, d);
        var caseId = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["A"], D(10, 1), null, Key()));
        var payer = await Ok(SubrogationCases.AddPartyAsync(s.Transact, caseId, s.Person, PartyRole.LiablePerson, null, null, null));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, caseId, DocumentedMovement.Confirmed, Lari(900), D(10, 1), DocumentKind.DebtorAcceptance, null, D(10, 1), null, Key()));
        var r = await Ok(SubrogationCases.ReceiveAsync(s.Transact, caseId, payer, Lari(600), D(10, 20), null, null, Key()));
        await Ok(SubrogationCases.ReverseAsync(s.Transact, caseId, r, D(11, 5), "x", Key()));
        var number = await w.ScalarAsync<string>("select number from core.subrogation_case where id = @caseId", new { caseId });
        var october = await SubrogationReportStore.PeriodAsync(w.App, D(10, 1), D(10, 31));
        var november = await SubrogationReportStore.PeriodAsync(w.App, D(11, 1), D(11, 30));
        Assert.Equal(Lari(600), october.Lines.Single(l => l.CaseNumber == number).Moved.Received);
        Assert.Equal(-Lari(600), november.Lines.Single(l => l.CaseNumber == number).Moved.Received);
    }

    [Fact]
    public async Task All_reads_of_one_report_see_one_committed_state()
    {
        var s = await SetupAsync();
        DateOnly D(int m, int d) => new(s.Y, m, d);
        var caseId = await Ok(SubrogationCases.OpenCaseAsync(s.Transact, s.Claims["A"], D(10, 1), null, Key()));
        var payer = await Ok(SubrogationCases.AddPartyAsync(s.Transact, caseId, s.Person, PartyRole.LiablePerson, null, null, null));
        await Ok(SubrogationCases.AddMovementAsync(s.Transact, caseId, DocumentedMovement.Confirmed, Lari(900), D(10, 1), DocumentKind.DebtorAcceptance, null, D(10, 1), null, Key()));
        var (first, second) = await w.App.SnapshotAsync(async tx =>
        {
            var before = await SubrogationReportStore.LoadCaseCardAsync(tx, caseId, D(10, 31), D(10, 31));
            // A receipt committed by another session meanwhile is not seen by this snapshot.
            await Ok(SubrogationCases.ReceiveAsync(s.Transact, caseId, payer, Lari(100), D(10, 20), null, null, Key()));
            var after = await SubrogationReportStore.LoadCaseCardAsync(tx, caseId, D(10, 31), D(10, 31));
            return (before!.Totals.Balance, after!.Totals.Balance);
        });
        Assert.Equal((Lari(900), Lari(900)), (first, second));
        Assert.Equal(Lari(800), (await SubrogationReportStore.CaseCardAsync(w.App, caseId, D(10, 31), D(10, 31)))!.Totals.Balance);
    }
}
