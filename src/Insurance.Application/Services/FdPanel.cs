using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Application.Services;

/// <summary>
/// The financial director's panel, its lists (legacy src/lib/fd-panel.ts; owner, 17.10.2026, docs/05 ვ8): the three
/// kinds of request above the company's threshold not yet carried out — claim payments, premium refunds and premium
/// receipt reversals — each its own list, oldest first, a page at a time. A request is not carried out while no row
/// carries it (no claim payment with its request_id, no payment with its refund_request_id or reversal_request_id).
/// Nothing here writes or decides: the authorisation is on the claim's or the policy's card. Who may see the panel is
/// decided before this is called (finance.panel.view).
/// </summary>
public static class FdPanel
{
    /// <summary>Rows of one list on one page; one more is read to know whether there is a next page.</summary>
    public const int PageSize = 50;

    public static readonly IReadOnlyList<string> Queues = ["claim", "refund", "reversal"];

    /// <param name="RequestedOn">The day it was asked, in Tbilisi (dd.mm.yyyy).</param>
    /// <param name="CardId">The card it is authorised on: the claim (claim payments) or the policy (refunds, reversals).</param>
    /// <param name="ReceiptId">The receipt a reversal reverses; null for the other kinds.</param>
    /// <param name="AmountGel">The GEL stored with the request: the net to transfer, the refund, the receipt's GEL.</param>
    public sealed record PendingRequest(Guid Id, string RequestedOn, Guid CardId, string CardNumber, Guid? ReceiptId, decimal AmountGel, string? Requester);

    public enum ReadState { Ok, NotReady, Failed }

    /// <summary>A list read: its page; or its tables are not there — never shown as "nothing pending"; or it failed.</summary>
    public sealed record QueueRead(ReadState State, IReadOnlyList<PendingRequest> Rows, Guid? Next);

    /// <summary>Each list's cursor as given in the address (after the request with this id); anything that is not a request id is dropped.</summary>
    public static IReadOnlyDictionary<string, string?> ParseCursor(Func<string, string?> param)
    {
        var cursor = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var queue in Queues)
        {
            var v = param(queue)?.Trim();
            cursor[queue] = Uuid.IsUuid(v) ? v!.ToLowerInvariant() : null;
        }
        return cursor;
    }

    private const string RequestedOn = "format(switchoffset(q.created_at, '+04:00'), 'dd.MM.yyyy')";

    // (created_at, id) after the cursor's, ids compared as PostgreSQL compares uuids (their lower-case text).
    private const string After = """
        (@after is null or q.created_at > a.created_at
         or (q.created_at = a.created_at and lower(convert(nchar(36), q.id)) collate Latin1_General_BIN2 > lower(convert(nchar(36), a.id)) collate Latin1_General_BIN2))
        """;

    private const string Order = "order by q.created_at, lower(convert(nchar(36), q.id)) collate Latin1_General_BIN2";

    private static string Sql(string queue) => queue switch
    {
        "claim" => $"""
            select top (@limit) q.id as Id, {RequestedOn} as RequestedOn, q.claim_id as CardId, c.number as CardNumber,
                   convert(uniqueidentifier, null) as ReceiptId, q.net_paid_gel as AmountGel, u.full_name as Requester
            from core.claim_payment_request q join core.claim c on c.id = q.claim_id left join core.app_user u on u.id = q.created_by
            left join core.claim_payment_request a on a.id = @after
            where not exists (select 1 from core.claim_payment m where m.request_id = q.id) and {After}
            {Order}
            """,
        "refund" => $"""
            select top (@limit) q.id as Id, {RequestedOn} as RequestedOn, q.policy_id as CardId, p.number as CardNumber,
                   convert(uniqueidentifier, null) as ReceiptId, q.amount_gel as AmountGel, u.full_name as Requester
            from core.premium_refund_request q join core.policy p on p.id = q.policy_id left join core.app_user u on u.id = q.created_by
            left join core.premium_refund_request a on a.id = @after
            where not exists (select 1 from core.payment m where m.refund_request_id = q.id) and {After}
            {Order}
            """,
        _ => $"""
            select top (@limit) q.id as Id, {RequestedOn} as RequestedOn, q.policy_id as CardId, p.number as CardNumber,
                   q.receipt_id as ReceiptId, q.amount_gel as AmountGel, u.full_name as Requester
            from core.receipt_reversal_request q join core.policy p on p.id = q.policy_id left join core.app_user u on u.id = q.created_by
            left join core.receipt_reversal_request a on a.id = @after
            where not exists (select 1 from core.payment m where m.reversal_request_id = q.id) and {After}
            {Order}
            """,
    };

    /// <summary>
    /// One list's page, or why it cannot be shown. A cursor that names no request of that list reads from the first (as
    /// the legacy subquery compared to null and kept nothing — see below). A failed read is a failure, never an empty list.
    /// </summary>
    public static async Task<QueueRead> ReadPendingQueueAsync(Tx db, string queue, string? after, Action<string, Exception>? log = null)
    {
        try
        {
            // Legacy: (created_at, id) > (select … where a.id = after); a cursor that is no request of the list compares to
            // null and reads nothing. Kept: such a cursor gives an empty page.
            if (after is not null && !await ExistsAsync(db, queue, Guid.Parse(after))) return new QueueRead(ReadState.Ok, [], null);
            var rows = (await db.QueryAsync<PendingRequest>(Sql(queue), new { limit = PageSize + 1, after = after is null ? (Guid?)null : Guid.Parse(after) })).ToList();
            var page = rows.Take(PageSize).ToList();
            return new QueueRead(ReadState.Ok, page, rows.Count > PageSize ? page[^1].Id : null);
        }
        catch (SqlException e) when (e.Number is 207 or 208)
        {
            // A table or a column that is not there: the migration of that list is not applied.
            return new QueueRead(ReadState.NotReady, [], null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Legacy readPendingQueue: any error of the read is "could not be read" (logged), never an empty list; only a
            // cancelled request is let through.
            log?.Invoke(queue, e);
            return new QueueRead(ReadState.Failed, [], null);
        }
    }

    private static async Task<bool> ExistsAsync(Tx db, string queue, Guid id)
    {
        var table = queue switch { "claim" => "core.claim_payment_request", "refund" => "core.premium_refund_request", _ => "core.receipt_reversal_request" };
        return await db.ExecuteScalarAsync<int>($"select count(*) from {table} where id = @id", new { id }) > 0;
    }

    /// <summary>A stored GEL amount for the screen, exactly: 90071992547408.01 → 90,071,992,547,408.01.</summary>
    public static string AmountText(decimal amount) => Billing.GroupedTetri(Billing.Tetri(amount));
}
