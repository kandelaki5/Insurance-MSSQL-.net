using System.Globalization;
using System.Numerics;
using Insurance.Application.Data;
using Insurance.Domain.Finance;
using SubrogationRules = Insurance.Domain.Finance.Subrogation;

namespace Insurance.Application.Services;

/// <summary>
/// Reading the regress card and the report "regress claims on a date" (legacy src/lib/subrogation-report-store.ts):
/// parameterised SELECTs only, their rows turned into the inputs of Insurance.Domain.Finance.SubrogationReport, and the real
/// CaseCardOf / ReportOnDate / MovedIn run on them. Nothing is written.
///
/// - All reads of one card or report go through one snapshot-isolation transaction (<see cref="Db.SnapshotAsync"/>, the
///   legacy read-only repeatable-read transaction): every query sees the same committed state.
/// - Amounts are decimal(18, 2) and become exact tetri; the audit time is read as an ISO UTC instant and turned into a
///   Tbilisi day only by the projection.
/// - Histories are read in audit-id order and must start with the row's insert and end in the row as it is now; anything
///   else is not a reliable history and is passed on as none (unknown), never mended.
/// - The date asked for (asOf) and today's business day (today) are separate arguments.
/// </summary>
public static class SubrogationReportStore
{
    private const string CaseColumns = """
        select c.id as Id, c.number as Number, c.opened_on as OpenedOn, c.status as Status, c.basis_kind as BasisKind, c.basis_number as BasisNumber,
               c.basis_date as BasisDate, cl.id as ClaimId, cl.number as ClaimNumber, p.number as PolicyNumber, l.class_code as ClassCode
        from core.subrogation_case c
        join core.claim cl on cl.id = c.claim_id
        join core.policy p on p.id = cl.policy_id
        join core.policy_line l on l.id = cl.policy_line_id
        """;

    /// <summary>The SQL this reader runs: SELECTs with parameters, nothing put together from input.</summary>
    public static class Sql
    {
        public const string CaseById = CaseColumns + "\nwhere c.id = @caseId";
        public const string CasesOpenedBy = CaseColumns + "\nwhere c.opened_on <= @asOf\norder by c.number";
        public const string Parties = """
            select sp.id as Id, sp.case_id as CaseId, coalesce(pt.name, concat_ws(N' ', pt.first_name, pt.last_name)) as Name,
                   sp.role as Role, sp.limit_gel as LimitGel, sp.response as Response, sp.response_on as ResponseOn
            from core.subrogation_party sp join core.party pt on pt.id = sp.party_id
            where sp.case_id in @ids
            order by sp.created_at, lower(convert(nchar(36), sp.id)) collate Latin1_General_BIN2
            """;
        public const string Movements = """
            select id as Id, case_id as CaseId, kind as Kind, booked_on as BookedOn, amount_gel as AmountGel, reverses_movement_id as ReversesMovementId,
                   payer_id as PayerId, document_kind as DocumentKind, document_number as DocumentNumber, document_date as DocumentDate, note as Note
            from core.subrogation_movement
            where case_id in @ids
            order by created_at, lower(convert(nchar(36), id)) collate Latin1_General_BIN2
            """;
        public const string CaseHistory = """
            select id as AuditId, row_id as RowId, action as Action, format(switchoffset(at, '+00:00'), 'yyyy-MM-dd\THH:mm:ss.ffffff\Z') as At,
                   json_value(new_row, '$.status') as Status
            from core.audit_log
            where table_name = N'subrogation_case' and action in (N'insert', N'update') and row_id in @ids
            order by id
            """;
        public const string PartyHistory = """
            select id as AuditId, row_id as RowId, action as Action, format(switchoffset(at, '+00:00'), 'yyyy-MM-dd\THH:mm:ss.ffffff\Z') as At,
                   json_value(new_row, '$.response') as Response, json_value(new_row, '$.response_on') as ResponseOn, json_value(new_row, '$.limit_gel') as LimitGel
            from core.audit_log
            where table_name = N'subrogation_party' and action in (N'insert', N'update') and row_id in @ids
            order by id
            """;
        public const string OutsideAll = """
            select cl.number as ClaimNumber, r.kind as Kind, r.received_on as ReceivedOn, r.amount_gel as AmountGel
            from core.claim_recovery r join core.claim cl on cl.id = r.claim_id
            where r.subrogation_case_id is null and r.received_on <= @asOf
            order by cl.number, r.kind, r.received_on, lower(convert(nchar(36), r.id)) collate Latin1_General_BIN2
            """;
        public const string OutsideOfClaims = """
            select cl.number as ClaimNumber, r.kind as Kind, r.received_on as ReceivedOn, r.amount_gel as AmountGel
            from core.claim_recovery r join core.claim cl on cl.id = r.claim_id
            where r.subrogation_case_id is null and r.received_on <= @asOf and r.claim_id in @claims
            order by cl.number, r.kind, r.received_on, lower(convert(nchar(36), r.id)) collate Latin1_General_BIN2
            """;

        public static readonly IReadOnlyList<string> All = [CaseById, CasesOpenedBy, Parties, Movements, CaseHistory, PartyHistory, OutsideAll, OutsideOfClaims];
    }

    private sealed record CaseDb(Guid Id, string Number, DateOnly OpenedOn, string Status, string? BasisKind, string? BasisNumber, DateOnly? BasisDate,
        Guid ClaimId, string ClaimNumber, string PolicyNumber, string ClassCode);

    private sealed record PartyDb(Guid Id, Guid CaseId, string Name, string Role, decimal? LimitGel, string? Response, DateOnly? ResponseOn);

    private sealed record MovementDb(Guid Id, Guid CaseId, string Kind, DateOnly BookedOn, decimal AmountGel, Guid? ReversesMovementId, Guid? PayerId,
        string DocumentKind, string? DocumentNumber, DateOnly? DocumentDate, string? Note);

    private sealed record CaseAuditDb(long AuditId, string RowId, string Action, string At, string? Status);

    private sealed record PartyAuditDb(long AuditId, string RowId, string Action, string At, string? Response, string? ResponseOn, string? LimitGel);

    private sealed record OutsideDb(string ClaimNumber, string Kind, DateOnly ReceivedOn, decimal AmountGel);

    /// <summary>'1234.56' / '-600.00' (an amount as text) → exact signed tetri; anything else is refused, never rounded.</summary>
    public static BigInteger ExactTetri(string text, string what)
    {
        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        // No spaces, exponent or more than two decimals: anything else is not what the database gave.
        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? body : body[..dot];
        var fraction = dot < 0 ? "" : body[(dot + 1)..];
        if (whole.Length == 0 || !whole.All(char.IsAsciiDigit) || (dot >= 0 && (fraction.Length is < 1 or > 2 || !fraction.All(char.IsAsciiDigit))))
            throw new FormatException($"{what}: not an amount: {text}");
        var value = SubrogationRules.ToTetri(body) ?? throw new FormatException($"{what}: not an amount: {text}");
        return negative ? -value : value;
    }

    private static BigInteger Exact(decimal amount, string what) => ExactTetri(amount.ToString(CultureInfo.InvariantCulture), what);

    /// <summary>An audit instant as the SQL gives it: ISO UTC with 'Z'.</summary>
    public static string AuditInstant(string text, string what)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z\z"))
            throw new FormatException($"{what}: not an ISO UTC instant: {text}");
        return text;
    }

    private static T Parse<T>(string code, string what) where T : struct, Enum
    {
        try { return Codes.Parse<T>(code); }
        catch (ArgumentException) { throw new FormatException($"{what}: unknown value {code}"); }
    }

    private static PartyResponse? ResponseOf(string? code, string what) => code is null ? null : Parse<PartyResponse>(code, what);

    private static DateOnly? DayOf(string? text, string what)
    {
        if (text is null) return null;
        return FormValues.DateOf(text) ?? throw new FormatException($"{what}: not a date: {text}");
    }

    /// <summary>
    /// Histories by row, in the order read (audit id). A history counts only if it starts with the row's insert and its last
    /// value is the row as it is now; otherwise it is not reliable and the row gets none — the projection then says
    /// unknown. Nothing is filled in from today's row.
    /// </summary>
    private static Dictionary<string, List<Recorded<T>>> ReliableHistories<T>(IEnumerable<(string RowId, string Action, string At, T Value)> rows, IReadOnlyDictionary<string, T> now, Func<T, T, bool> same)
    {
        var byRow = new Dictionary<string, List<(string RowId, string Action, string At, T Value)>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!byRow.TryGetValue(r.RowId, out var list)) byRow[r.RowId] = list = [];
            list.Add(r);
        }
        var result = new Dictionary<string, List<Recorded<T>>>(StringComparer.Ordinal);
        foreach (var (rowId, list) in byRow)
        {
            if (list[0].Action != "insert" || !now.TryGetValue(rowId, out var current) || !same(list[^1].Value, current)) continue;
            result[rowId] = list.Select(r => new Recorded<T>(r.At, r.Value)).ToList();
        }
        return result;
    }

    private static bool SameTerms(PartyTerms a, PartyTerms b) => a.Response == b.Response && a.ResponseOn == b.ResponseOn && a.Limit == b.Limit;

    private sealed record LoadedCase(ReportCase Case, IReadOnlyList<MovementRecord> Movements, Guid ClaimId);

    private static async Task<List<LoadedCase>> LoadCasesAsync(Tx q, IReadOnlyList<CaseDb> caseRows)
    {
        if (caseRows.Count == 0) return [];
        var ids = caseRows.Select(c => c.Id).ToList();
        var partyRows = await Billing.InChunksAsync(ids, chunk => q.QueryAsync<PartyDb>(Sql.Parties, new { ids = chunk }));
        var movementRows = await Billing.InChunksAsync(ids, chunk => q.QueryAsync<MovementDb>(Sql.Movements, new { ids = chunk }));
        // Audit rows by row id (lower-case text), in audit-id order across the parts read.
        var caseAudit = (await Billing.InChunksAsync(ids, chunk => q.QueryAsync<CaseAuditDb>(Sql.CaseHistory, new { ids = chunk.Select(i => i.ToString()).ToList() })))
            .OrderBy(a => a.AuditId).ToList();
        var partyAudit = (await Billing.InChunksAsync(partyRows.Select(p => p.Id).ToList(),
                chunk => q.QueryAsync<PartyAuditDb>(Sql.PartyHistory, new { ids = chunk.Select(i => i.ToString()).ToList() })))
            .OrderBy(a => a.AuditId).ToList();

        var statusNow = caseRows.ToDictionary(c => c.Id.ToString(), c => Parse<CaseStatus>(c.Status, "status"), StringComparer.Ordinal);
        var statusHistories = ReliableHistories(
            caseAudit.Select(a => (a.RowId, a.Action, AuditInstant(a.At, "audit at"), Parse<CaseStatus>(a.Status ?? "", "audit status"))),
            statusNow, (a, b) => a == b);

        var termsNow = partyRows.ToDictionary(p => p.Id.ToString(),
            p => new PartyTerms(ResponseOf(p.Response, "response"), p.ResponseOn, p.LimitGel is { } l ? Exact(l, "limit") : null), StringComparer.Ordinal);
        var partyHistories = ReliableHistories(
            partyAudit.Select(a => (a.RowId, a.Action, AuditInstant(a.At, "audit at"),
                new PartyTerms(ResponseOf(a.Response, "audit response"), DayOf(a.ResponseOn, "audit response_on"), a.LimitGel is { } l ? ExactTetri(l, "audit limit") : null))),
            termsNow, SameTerms);

        var parties = new Dictionary<Guid, List<PartyRecord>>();
        foreach (var p in partyRows)
        {
            var now = termsNow[p.Id.ToString()];
            var role = Parse<Domain.Finance.PartyRole>(p.Role, "role");
            if (!parties.TryGetValue(p.CaseId, out var list)) parties[p.CaseId] = list = [];
            list.Add(new PartyRecord(p.Id.ToString(), p.Name, role, now.Limit, now.Response, now.ResponseOn, partyHistories.GetValueOrDefault(p.Id.ToString())));
        }
        var movements = new Dictionary<Guid, List<MovementRecord>>();
        foreach (var m in movementRows)
        {
            if (!movements.TryGetValue(m.CaseId, out var list)) movements[m.CaseId] = list = [];
            list.Add(new MovementRecord(m.Id.ToString(), Parse<MovementKind>(m.Kind, "movement kind"), Exact(m.AmountGel, "movement amount"), m.BookedOn,
                m.ReversesMovementId?.ToString(), m.DocumentKind, m.DocumentNumber, m.DocumentDate, m.PayerId?.ToString(), m.Note));
        }
        return caseRows.Select(c =>
        {
            var record = new CaseRecord(c.Id.ToString(), c.Number, c.ClaimId.ToString(), c.ClaimNumber, c.PolicyNumber, c.ClassCode, c.OpenedOn, statusNow[c.Id.ToString()],
                c.BasisKind is { } kind ? new CaseBasis(kind, c.BasisNumber ?? throw new FormatException("basis number"), c.BasisDate ?? throw new FormatException("basis date")) : null,
                statusHistories.GetValueOrDefault(c.Id.ToString()));
            var own = movements.GetValueOrDefault(c.Id) ?? [];
            return new LoadedCase(new ReportCase(record, parties.GetValueOrDefault(c.Id) ?? [], own), own, c.ClaimId);
        }).ToList();
    }

    /// <summary>The card of one case on <paramref name="asOf"/>, with today's business day apart; null when there is no such case.</summary>
    public static async Task<CaseCard?> LoadCaseCardAsync(Tx q, Guid caseId, DateOnly asOf, DateOnly today)
    {
        var rows = (await q.QueryAsync<CaseDb>(Sql.CaseById, new { caseId })).ToList();
        if (rows.Count == 0) return null;
        var c = (await LoadCasesAsync(q, rows))[0];
        return SubrogationReport.CaseCardOf(c.Case.Record, c.Case.Parties, c.Movements, asOf, today);
    }

    public enum OutsideScope { All, ClaimsOfCases, None }

    /// <summary>
    /// The report on <paramref name="asOf"/> (Q-R1): every case opened by then, with the optional only-with-balance filter.
    /// Old case-less regress money and salvage come apart (Q-R4) — of every claim, of the report's claims only, or not at
    /// all — and never into the regress totals.
    /// </summary>
    public static async Task<Report> LoadReportAsync(Tx q, DateOnly asOf, bool onlyWithBalance = false, OutsideScope outside = OutsideScope.All)
    {
        var rows = (await q.QueryAsync<CaseDb>(Sql.CasesOpenedBy, new { asOf })).ToList();
        var cases = await LoadCasesAsync(q, rows);
        IReadOnlyList<OutsideDb> outsideRows = outside switch
        {
            OutsideScope.None => [],
            OutsideScope.All => (await q.QueryAsync<OutsideDb>(Sql.OutsideAll, new { asOf })).ToList(),
            _ => cases.Count == 0 ? [] : await Billing.InChunksAsync(cases.Select(c => c.ClaimId).Distinct().ToList(), claims => q.QueryAsync<OutsideDb>(Sql.OutsideOfClaims, new { asOf, claims })),
        };
        var recoveries = outsideRows.Select(r => new OutsideRecovery(r.ClaimNumber, Parse<OutsideKind>(r.Kind, "recovery kind"), r.ReceivedOn, Exact(r.AmountGel, "recovery amount"))).ToList();
        return SubrogationReport.ReportOnDate(cases.Select(c => c.Case).ToList(), asOf, new ReportOptions(onlyWithBalance, recoveries));
    }

    public sealed record PeriodLine(string CaseNumber, PeriodMovement Moved);

    public sealed record PeriodReport(DateOnly From, DateOnly To, IReadOnlyList<PeriodLine> Lines, PeriodMovement Total);

    /// <summary>What moved in a period (Q-R5), per case opened by its end, and the column totals; a reversal in its own period.</summary>
    public static async Task<PeriodReport> LoadPeriodAsync(Tx q, DateOnly from, DateOnly to)
    {
        var rows = (await q.QueryAsync<CaseDb>(Sql.CasesOpenedBy, new { asOf = to })).ToList();
        var cases = await LoadCasesAsync(q, rows);
        var lines = cases.Select(c => new PeriodLine(c.Case.Record.CaseNumber, SubrogationReport.MovedIn(c.Case.Movements, from, to))).ToList();
        var total = lines.Aggregate(new PeriodMovement(0, 0, 0, 0, 0), (t, l) => new PeriodMovement(
            t.Claimed + l.Moved.Claimed, t.Confirmed + l.Moved.Confirmed, t.Received + l.Moved.Received, t.Cancelled + l.Moved.Cancelled, t.BalanceChange + l.Moved.BalanceChange));
        return new PeriodReport(from, to, lines, total);
    }

    /// <summary>The card in one snapshot (the legacy readOnlySnapshot).</summary>
    public static Task<CaseCard?> CaseCardAsync(Db db, Guid caseId, DateOnly asOf, DateOnly today) => db.SnapshotAsync(tx => LoadCaseCardAsync(tx, caseId, asOf, today));

    public static Task<Report> ReportAsync(Db db, DateOnly asOf, bool onlyWithBalance = false, OutsideScope outside = OutsideScope.All) =>
        db.SnapshotAsync(tx => LoadReportAsync(tx, asOf, onlyWithBalance, outside));

    public static Task<PeriodReport> PeriodAsync(Db db, DateOnly from, DateOnly to) => db.SnapshotAsync(tx => LoadPeriodAsync(tx, from, to));
}
