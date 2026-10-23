using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Xunit.Sdk;
using static Insurance.Integration.Tests.Accounting.MonthReportOracle;

namespace Insurance.Integration.Tests.Accounting;

/// <summary>One month's figures a case adds to its class (whole tetri; policies issued: a count).</summary>
public sealed record Fig(int Issued, BigInteger Stat, BigInteger Fin, BigInteger Earned, BigInteger Upr)
{
    public static readonly Fig Zero = new(0, 0, 0, 0, 0);
    public override string ToString() => $"issued {Issued}, statistical written {Amount(Stat)}, written {Amount(Fin)}, earned {Amount(Earned)}, UPR {Amount(Upr)}";
}

/// <summary>A currency's receivable at a month's end: owed, overdue, in GEL, and the rate as the pages show it ("2.8000 (31.10.2026)").</summary>
public sealed record Owed(BigInteger Outstanding, BigInteger Overdue, BigInteger Gel, string? RateShown = null)
{
    public string Text(bool withRate) => $"outstanding {Amount(Outstanding)}, overdue {Amount(Overdue)}, in GEL {Amount(Gel)}{(withRate ? $", rate {RateShown}" : "")}";
}

/// <summary>The rest of the money: ceded premium, ceded UPR, commission; paid, RBNS, IBNR, recovered, the reinsurer's paid and RBNS; the treaty premium.</summary>
public sealed record Money(BigInteger[] Ceded, BigInteger[] Claims, BigInteger Treaty);

public sealed record Want(Fig Cls, IReadOnlyDictionary<string, Owed> Owed, Money? Money = null);

/// <summary>A month as read: the report, and (in the browser-level port) the monthly report page and the financial director's panel.</summary>
public sealed record MonthView(PremiumReport.Report Report, string? Page, string? Panel);

/// <summary>
/// The cases R1–R10 of legacy scripts/month-report-precision-db-check.ts (scripts/integration/month-report-cases.ts, Codex
/// INS-MONTHLY-REPORT-PRECISION-CHECKER-OFFLINE-PREP-20261019 — "PREPARED, NOT RUN" in the old program), with their amounts, days and
/// steps. Each case runs in a database of its own, so what it adds to a month is the month itself (the legacy "before its fixtures"
/// read of a shared copy is empty here). Every step reads the month through <see cref="Read"/>: the report (PremiumReport.BuildAsync,
/// the one the page and the panel call), and — when the browser-level port reads them — the report page and the panel, every figure
/// parsed back from its text exactly.
/// Adaptations, by the new program's clock (it has none to set): the receipts of R4, R5 and R9 go through the real RecordPayment on
/// today, so their first month m0 is the current month and the receipt day d0 is the 20th, or today before the 20th (the allocation is
/// booked today, in m0, as the legacy child's on its d0); the other cases keep the planned months October and November 2026, whose
/// pinned texts (PINNED_2026_10) are checked too. R6's receipt, advance and request are written as the rows the actions wrote on their
/// days (an allocation is always booked today); its approval is the real ApproveReceiptReversalRequest, failing once after the mirror's
/// first line and then approved — the actions' own rows are proved by LegacyPremiumPrecisionTests B/D2.
/// </summary>
public sealed partial class MonthReportCase
{
    public const string Tag = "check-month-report";
    public const string ClassCode = "2";

    public required ReportWorld W { get; init; }
    public required Func<string, Task<MonthView>> Read { get; init; }
    public required string M0 { get; init; }
    public string M1 => NextMonth(M0);
    public DateOnly First0 => Day(M0 + "-01");
    public DateOnly End0 => MonthEnd(M0);
    public DateOnly First1 => Day(M1 + "-01");
    public DateOnly End1 => MonthEnd(M1);
    public DateOnly CoverTo => First0.AddDays(365);
    public int Total => CoverTo.DayNumber - First0.DayNumber;
    public int A => End0.DayNumber - First0.DayNumber + 1;
    public int B => End1.DayNumber - First0.DayNumber + 1;
    public static DateOnly Today => Rates.TodayInTbilisi();

    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Fig> _kept = new(StringComparer.Ordinal);
    public List<string> Checked { get; } = [];

    public Guid Id(string name) => _ids.TryGetValue(name, out var id) ? id : _ids[name] = Guid.NewGuid();

    public CurrentUser Accountant => new(W.Accountant, "test.accountant", "სატესტო მომხმარებელი", "accountant", "accountant");
    public CurrentUser Admin => new(W.Admin, "test.admin", "სატესტო მომხმარებელი", "admin", "admin");
    private Rates? _rates;
    public Rates ActionRates => _rates ??= RefusingNbg.For(W.App).Rates;

    /// <summary>The cases by name; m0 is the current month for the cases with a real receipt, else October 2026.</summary>
    public static readonly IReadOnlyDictionary<string, (bool Current, Func<MonthReportCase, Task> Run)> Cases = new Dictionary<string, (bool, Func<MonthReportCase, Task>)>
    {
        ["R1"] = (false, c => c.R1()), ["R2"] = (false, c => c.R2()), ["R3"] = (false, c => c.R3()), ["R4"] = (true, c => c.R4()), ["R5"] = (true, c => c.R5()),
        ["R6"] = (false, c => c.R6()), ["R7"] = (false, c => c.R7()), ["R8"] = (false, c => c.R8()), ["R9"] = (true, c => c.R9()),
        ["R10"] = (false, c => c.R10()),
    };

    public static string MonthOf(string name) => Cases[name].Current ? Today.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "2026-10";

    // ================================================================ checks

    private void Same(string label, string expected, string actual)
    {
        Checked.Add(label);
        if (expected != actual) throw new XunitException($"{label}\n  expected: {expected}\n  actual:   {actual}");
    }

    private static string Cells(IEnumerable<BigInteger> v) => string.Join(", ", v.Select(Amount));

    /// <summary>The month again: the case's figures on the report and, when read, its page and the panel — each exactly.</summary>
    public async Task ReportAsync(string label, string month, Want want, string? keep = null)
    {
        var view = await Read(month);
        var r = view.Report;
        var row = r.Classes.FirstOrDefault(c => c.ClassCode == ClassCode);
        var cls = row is null ? Fig.Zero : new Fig(row.PoliciesIssued, row.StatisticalWrittenGel, row.FinancialWrittenGel, row.EarnedGel, row.UnearnedGel);
        if (keep is not null) _kept[keep] = cls;
        Same($"{label} — the report, class {ClassCode}", want.Cls.ToString(), cls.ToString());
        var t = r.Totals;
        Same($"{label} — the report, totals", want.Cls.ToString(), new Fig(t.PoliciesIssued, t.StatisticalWrittenGel, t.FinancialWrittenGel, t.EarnedGel, t.UnearnedGel).ToString());
        var page = view.Page is null ? null : WebUtility.HtmlDecode(view.Page);
        var panel = view.Panel is null ? null : WebUtility.HtmlDecode(view.Panel);
        string[] parts = page is null ? [] : page.Split("<h2 class=\"section\">");
        if (page is not null)
        {
            Same($"{label} — the report page, class {ClassCode}", want.Cls.ToString(), PageFig(FirstRow(parts[0], ClassCode), 2).ToString());
            Same($"{label} — the report page, totals", want.Cls.ToString(), PageFig(TotalRow(parts[0]), 1).ToString());
        }
        if (panel is not null)
            Same($"{label} — the panel's figures", Cells([want.Cls.Fin, want.Cls.Earned, want.Cls.Upr]),
                Cells(new[] { "financialWritten", "earned", "unearned" }.Select(k => PageTetri(PanelFigure(panel, k), $"panel {k}"))));
        if (want.Money is { } m)
        {
            BigInteger[] Ceded(PremiumReport.ClassRow? c) => c is null ? [0, 0, 0] : [c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel];
            BigInteger[] Claims(PremiumReport.ClaimsRow? c) => c is null ? [0, 0, 0, 0, 0, 0] : [c.PaidGel, c.RbnsGel, c.IbnrGel, c.RecoveredGel, c.ReinsurerPaidGel, c.ReinsurerRbnsGel];
            var ct = r.ClaimsTotals;
            Same($"{label} — the report, class {ClassCode}: ceded, ceded UPR, commission", Cells(m.Ceded), Cells(Ceded(row)));
            Same($"{label} — the report, totals: ceded, ceded UPR, commission", Cells(m.Ceded), Cells([t.CededWrittenGel, t.CededUnearnedGel, t.RiCommissionGel]));
            Same($"{label} — the report, class {ClassCode} claims", Cells(m.Claims), Cells(Claims(r.Claims.FirstOrDefault(c => c.ClassCode == ClassCode))));
            Same($"{label} — the report, claims totals", Cells(m.Claims), Cells([ct.PaidGel, ct.RbnsGel, ct.IbnrGel, ct.RecoveredGel, ct.ReinsurerPaidGel, ct.ReinsurerRbnsGel]));
            Same($"{label} — the report, the treaty premium", Amount(m.Treaty), Amount(r.TreatyPremiumGel));
            if (page is not null)
            {
                Same($"{label} — the report page, class {ClassCode}: ceded, ceded UPR, commission", Cells(m.Ceded), Cells(PageAmounts(FirstRow(parts[0], ClassCode), 7, 3)));
                Same($"{label} — the report page, totals: ceded, ceded UPR, commission", Cells(m.Ceded), Cells(PageAmounts(TotalRow(parts[0]), 6, 3)));
                var hasClaims = r.Claims.Count > 0;
                Same($"{label} — the report page, class {ClassCode} claims", Cells(m.Claims), Cells(hasClaims ? PageAmounts(FirstRow(parts[1], ClassCode), 2, 6) : new BigInteger[6]));
                Same($"{label} — the report page, claims totals", Cells(m.Claims), Cells(hasClaims ? PageAmounts(TotalRow(parts[1]), 1, 6) : new BigInteger[6]));
                var treaty = TreatyLine().Match(parts[0]);
                Same($"{label} — the report page, the treaty premium line", Amount(m.Treaty), Amount(treaty.Success ? PageTetri(treaty.Groups[1].Value, "treaty premium") : 0));
            }
            if (panel is not null)
                Same($"{label} — the panel: paid, RBNS, IBNR, recovered", Cells(m.Claims.Take(4)),
                    Cells(new[] { "paid", "rbns", "ibnr", "recovered" }.Select(k => PageTetri(PanelFigure(panel, k), $"panel {k}"))));
        }
        foreach (var (currency, o) in want.Owed)
        {
            var x = r.Receivables.FirstOrDefault(y => y.Currency == currency);
            var got = x is null ? new Owed(0, 0, 0) : new Owed(x.Outstanding, x.Overdue, x.OutstandingGel,
                currency == "GEL" ? null : $"{x.Rate.ToString("0.0000", CultureInfo.InvariantCulture)} ({Dmy(x.RateDate)})");
            var withRate = currency != "GEL";
            Same($"{label} — the report, {currency} receivable", o.Text(withRate), got.Text(withRate));
            if (page is not null) Same($"{label} — the report page, {currency} receivable", o.Text(withRate), PageOwed(FirstRow(parts[2], currency)).Text(withRate));
            if (panel is not null) Same($"{label} — the panel, {currency} receivable", o.Text(withRate), PageOwed(PanelReceivable(panel, currency)).Text(withRate));
        }
    }

    /// <summary>The month's whole report kept, or compared with the one kept: every figure of every class and currency, exactly.</summary>
    public async Task SnapshotAsync(string label, string month, string key, bool compare = false)
    {
        var now = ReportText((await Read(month)).Report);
        if (!compare) _snapshots[key] = now;
        else Same($"{label} — the whole report of {month}, every figure exactly as read before", _snapshots[key], now);
    }

    /// <summary>Written in all = earned in all + the last month's UPR, over the months kept.</summary>
    public void Identity(string label, params string[] months)
    {
        var kept = months.Select(m => _kept[m]).ToList();
        var written = kept.Aggregate(BigInteger.Zero, (s, k) => s + k.Fin);
        var earned = kept.Aggregate(BigInteger.Zero, (s, k) => s + k.Earned);
        Same($"{label} — written in all = earned in all + the last UPR", Amount(written), Amount(earned + kept[^1].Upr));
    }

    public Fig Kept(string month) => _kept[month];

    private static readonly string[] CountedTables =
    [
        "party", "contract", "policy", "policy_line", "premium_transaction", "installment", "installment_adjustment", "payment", "payment_allocation",
        "receipt_reversal_request", "receipt_reversal_request_line", "premium_refund_request", "ri_treaty", "premium_cession", "loss_event", "claim",
        "claim_reserve", "claim_payment", "claim_payment_request", "claim_recovery", "claim_cession",
    ];

    /// <summary>The case's rows per table (legacy `counts`): in a database of its own, every row of these tables is the case's.</summary>
    public async Task CountsAsync(string label, Dictionary<string, int> counts)
    {
        await using var c = await W.Database.OpenAdminAsync();
        var actual = new List<string>();
        foreach (var t in CountedTables)
        {
            var n = await c.ExecuteScalarAsync<int>($"select count(*) from core.{t}");
            if (n != 0) actual.Add($"{t} {n}");
        }
        Same($"{label}: the check's rows, per table, exactly the case's own", string.Join(", ", CountedTables.Where(counts.ContainsKey).Select(t => $"{t} {counts[t]}")), string.Join(", ", actual));
    }

    public void Pinned(string label, string expected, BigInteger actual) => Same($"{label} (pinned for October 2026)", expected, Amount(actual));

    public static string ReportText(PremiumReport.Report r) => string.Join("\n", new[] { $"{r.Month} {r.From} {r.To}" }
        .Concat(r.Classes.Select(c => c.ToString())).Append(r.Totals.ToString()).Concat(r.Receivables.Select(c => c.ToString()))
        .Concat(r.Claims.Select(c => c.ToString())).Append(r.ClaimsTotals.ToString()).Append(r.TreatyPremiumGel.ToString(CultureInfo.InvariantCulture)));

    // ---------------------------------------------------------------- what the pages show

    [GeneratedRegex(@"<tr[^>]*>([\s\S]*?)</tr>")] private static partial Regex RowRe();
    [GeneratedRegex(@"<td[^>]*>([\s\S]*?)</td>")] private static partial Regex CellRe();
    [GeneratedRegex("<[^>]*>")] private static partial Regex TagRe();
    [GeneratedRegex(@"^(-?)(\d{1,3}(?:,\d{3})*|0)\.(\d{2})$")] private static partial Regex PageAmount();
    private static Regex TreatyLine() => new(Regex.Escape(ReinsuranceTexts.Report.TreatyPremium) + ": <strong>([^<]*)</strong>");

    private static List<string[]> Rows(string html) =>
        [.. RowRe().Matches(html).Select(r => CellRe().Matches(r.Groups[1].Value).Select(c => TagRe().Replace(c.Groups[1].Value, "").Trim()).ToArray()).Where(c => c.Length > 0)];

    private static string[] FirstRow(string html, string first) => Rows(html).FirstOrDefault(c => c[0] == first) ?? [];

    private static string[] TotalRow(string html)
    {
        var at = html.IndexOf("<tr class=\"total-row\">", StringComparison.Ordinal);
        return at < 0 ? [] : Rows(html[at..]).First();
    }

    /// <summary>A page's amount back to whole tetri, exactly from its text ("90,071,992,547,408.01"); anything else fails.</summary>
    public static BigInteger PageTetri(string? text, string what)
    {
        var m = PageAmount().Match(text ?? "");
        if (!m.Success) throw new XunitException($"{what}: not an amount on the page ({text})");
        var t = BigInteger.Parse(m.Groups[2].Value.Replace(",", "", StringComparison.Ordinal) + m.Groups[3].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value == "-" ? -t : t;
    }

    private static Fig PageFig(string[] cells, int at) => cells.Length == 0 ? Fig.Zero
        : new Fig(int.Parse(cells[at], CultureInfo.InvariantCulture), PageTetri(cells[at + 1], "statistical"), PageTetri(cells[at + 2], "written"), PageTetri(cells[at + 3], "earned"), PageTetri(cells[at + 4], "UPR"));

    private static BigInteger[] PageAmounts(string[] cells, int at, int count) =>
        cells.Length == 0 ? new BigInteger[count] : [.. Enumerable.Range(at, count).Select(i => PageTetri(cells[i], $"cell {i}"))];

    private static Owed PageOwed(string[] cells) => cells.Length == 0 ? new Owed(0, 0, 0)
        : new Owed(PageTetri(cells[1], "outstanding"), PageTetri(cells[2], "overdue"), PageTetri(cells[4], "in GEL"), cells[3] == "" ? null : cells[3]);

    private static string PanelFigure(string panel, string key)
    {
        var m = Regex.Match(panel, $"<tr data-figure=\"{key}\">([\\s\\S]*?)</tr>");
        if (!m.Success) throw new XunitException($"the panel shows no {key}");
        return TagRe().Replace(CellRe().Matches(m.Groups[1].Value)[1].Groups[1].Value, "").Trim();
    }

    private static string[] PanelReceivable(string panel, string currency)
    {
        var m = Regex.Match(panel, $"<tr data-receivable=\"{currency}\">([\\s\\S]*?)</tr>");
        return m.Success ? [.. CellRe().Matches(m.Groups[1].Value).Select(c => TagRe().Replace(c.Groups[1].Value, "").Trim())] : [];
    }

    // ================================================================ fixtures (the legacy setup rows)

    private Task Exec(string sql, object? param = null) => W.ExecAsync(sql, param);
    private static DateTimeOffset At(DateOnly d, int h, int min = 0) => new(d.Year, d.Month, d.Day, h, min, 0, TimeSpan.FromHours(4));
    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private async Task PartyAsync(Guid id, string name, string foreign) =>
        await Exec("insert core.party (id, kind, name, foreign_id, is_resident, country_code) values (@id, N'organization', @name, @foreign, 0, N'GE')", new { id, name, foreign });

    /// <summary>A client of the case and its contract.</summary>
    private async Task ClientAsync(string k)
    {
        await PartyAsync(Id($"{k}:party"), $"{Tag} {k} (გამოგონილი)", $"{Tag}-{k}");
        await Exec("insert core.contract (id, number, signed_on, policyholder_id, created_by) values (@id, @number, @on, @party, @admin)",
            new { id = Id($"{k}:contract"), number = $"{Tag}-C-{k}", on = First0, party = Id($"{k}:party"), admin = W.Admin });
    }

    /// <summary>A policy, its line, its issue ledger row (booked on the cover's first day) and its instalments.</summary>
    private async Task IssuedAsync(string k, string p, string currency, string premium, string gel, (DateOnly Due, string Amount)[] insts, string? rate = null)
    {
        rate ??= currency == "GEL" ? "1.000000" : "2.700000";
        await W.WriteAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment, approved_at, approved_by, created_by)
                values (@id, @contract, @number, @from, @from, @to, N'active', @currency, @rate, N'corporate', @approvedAt, @admin, @admin)
                """, new { id = Id($"{k}:{p}"), contract = Id($"{k}:contract"), number = $"{Tag}-P-{k}-{p}", from = First0, to = CoverTo, currency, rate = D(rate), approvedAt = At(First0, 9), admin = W.Admin });
            await tx.ExecuteAsync("insert core.policy_line (id, policy_id, class_code, premium, premium_gel) values (@id, @policy, @code, @premium, @gel)",
                new { id = Id($"{k}:{p}:line"), policy = Id($"{k}:{p}"), code = ClassCode, premium = D(premium), gel = D(gel) });
            await tx.ExecuteAsync("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_at, created_by)
                values (@id, @line, N'issue', @premium, @gel, @from, @from, @to, null, @at, @admin)
                """, new { id = Id($"{k}:{p}:issue"), line = Id($"{k}:{p}:line"), premium = D(premium), gel = D(gel), from = First0, to = CoverTo, at = At(First0, 10), admin = W.Admin });
            for (var n = 0; n < insts.Length; n++)
                await tx.ExecuteAsync("insert core.installment (id, policy_id, due_on, amount) values (@id, @policy, @due, @amount)",
                    new { id = Id($"{k}:{p}:i{n + 1}"), policy = Id($"{k}:{p}"), due = insts[n].Due, amount = D(insts[n].Amount) });
            return 0;
        });
    }

    /// <summary>A policy's issue ledger row read back as stored.</summary>
    private async Task IssueRowAsync(string k, string p, string premium, string gel, bool onlyIssue = false)
    {
        await using var c = await W.Database.OpenAdminAsync();
        var rows = (await c.QueryAsync<(string, decimal, decimal, DateOnly, DateOnly, DateOnly, Guid?)>(
            "select kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id from core.premium_transaction where policy_line_id = @line and (@onlyIssue = 0 or id = @issue)",
            new { line = Id($"{k}:{p}:line"), onlyIssue, issue = Id($"{k}:{p}:issue") })).ToList();
        Same($"{k}: {p}'s issue ledger row as stored", $"issue {premium} {gel} {Iso(First0)} {Iso(First0)} {Iso(CoverTo)} -",
            string.Join("; ", rows.Select(r => $"{r.Item1} {r.Item2.ToString(CultureInfo.InvariantCulture)} {r.Item3.ToString(CultureInfo.InvariantCulture)} {Iso(r.Item4)} {Iso(r.Item5)} {Iso(r.Item6)} {(r.Item7 is null ? "-" : r.Item7.ToString())}")));
    }

    private async Task<List<string>> RowsAsync(string sql, object param)
    {
        await using var c = await W.Database.OpenAdminAsync();
        return [.. await c.QueryAsync<string>(sql, param)];
    }

    /// <summary>The receipts of a client as stored: day, GEL, method, kind, payer, reference, by, reverses, request.</summary>
    private Task<List<string>> PaymentsOfAsync(Guid party) => RowsAsync("""
        select concat(convert(char(10), received_on, 23), ' ', convert(nvarchar(40), amount_gel), ' ', method, ' ', kind, ' ', lower(convert(nchar(36), payer_party_id)), ' ',
                      reference, ' ', lower(convert(nchar(36), created_by)), ' ', isnull(lower(convert(nchar(36), reverses_payment_id)), N'-'), ' ', isnull(lower(convert(nchar(36), reversal_request_id)), N'-'))
        from core.payment where payer_party_id = @party order by created_at, id
        """, new { party });

    private string Payment(DateOnly on, string gel, string kind, Guid party, Guid by, Guid? reverses = null, Guid? request = null) =>
        $"{Iso(on)} {gel} bank {kind} {party} {Tag} {by} {(reverses is { } r ? r.ToString() : "-")} {(request is { } q ? q.ToString() : "-")}";

    /// <summary>A payment's lines as stored: instalment, amount, GEL, rate, booked, by.</summary>
    private Task<List<string>> LinesOfAsync(Guid payment) => RowsAsync("""
        select concat(lower(convert(nchar(36), installment_id)), ' ', convert(nvarchar(40), amount), ' ', convert(nvarchar(40), amount_gel), ' ', convert(nvarchar(40), rate), ' ',
                      convert(char(10), booked_on, 23), ' ', lower(convert(nchar(36), created_by)))
        from core.payment_allocation where payment_id = @payment order by created_at, amount_gel, id
        """, new { payment });

    private static string Line(Guid installment, string amount, string gel, string rate, DateOnly on, Guid by) => $"{installment} {amount} {gel} {rate} {Iso(on)} {by}";

    private async Task<Guid> PaymentIdAsync(Guid party, string kind)
    {
        await using var c = await W.Database.OpenAdminAsync();
        return await c.QuerySingleAsync<Guid>("select id from core.payment where payer_party_id = @party and kind = @kind", new { party, kind });
    }

    /// <summary>The receipt day of the cases with a real receipt: the 20th of the current month, or today before the 20th.</summary>
    public DateOnly D0Today => Today.Day >= 20 ? new DateOnly(Today.Year, Today.Month, 20) : Today;

    /// <summary>The real RecordPayment as the accountant (booked today), its answer as the legacy expected it.</summary>
    private async Task ReceiveAsync(string k, string p, DateOnly on, string amount, BigInteger left)
    {
        var answer = await PaymentActions.RecordPaymentAsync(W.App, ActionRates, Accountant, new Dictionary<string, string>
        {
            ["policy_id"] = Id($"{k}:{p}").ToString(), ["amount_gel"] = amount, ["received_on"] = Iso(on), ["method"] = "bank", ["reference"] = Tag,
        });
        Same($"{k}: recordPayment {amount} GEL received on {Iso(on)} for {p}", $"redirect /policies/{Id($"{k}:{p}")}?paid={left}",
            answer.Redirect is { } to ? $"redirect {to}" : $"{answer.Message} {string.Join(", ", answer.Errors.Select(e => $"{e.Key}: {e.Value}"))}");
    }

    private Fig IssuesIn(BigInteger[] amounts, int month)
    {
        var upTo = month == 0 ? A : B;
        var before = month == 0 ? 0 : A;
        var earned = amounts.Aggregate(BigInteger.Zero, (s, m) => s + EarnedTo(m, upTo, Total) - EarnedTo(m, before, Total));
        var all = amounts.Aggregate(BigInteger.Zero, (s, m) => s + m);
        return new Fig(month == 0 ? amounts.Length : 0, month == 0 ? all : 0, month == 0 ? all : 0, earned,
            amounts.Aggregate(BigInteger.Zero, (s, m) => s + m - EarnedTo(m, upTo, Total)));
    }

    private static Dictionary<string, Owed> Owe(string currency, Owed o) => new(StringComparer.Ordinal) { [currency] = o };
    private static readonly Dictionary<string, Owed> Nothing = [];
    private bool October => M0 == "2026-10";
}
