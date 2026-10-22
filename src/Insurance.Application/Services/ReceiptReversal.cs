using Insurance.Application.Data;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.Billing;

namespace Insurance.Application.Services;

/// <summary>Reversing a receipt by a mirror payment (legacy src/lib/receipt-reversal.ts).</summary>
public static class ReceiptReversal
{
    /// <summary>A receipt to be reversed, as read under its lock.</summary>
    public sealed record ReceiptToReverse(Guid Id, decimal AmountGel, string Method, Guid? PayerPartyId);

    /// <summary>One allocation of a receipt as stored.</summary>
    public sealed record ReceiptLine(Guid InstallmentId, decimal Amount, decimal AmountGel, decimal Rate);

    /// <summary>
    /// The lock a later allocation of a receipt takes (0024). Taken before the receipt is read for its reversal, so the
    /// reversal mirrors every allocation made so far and nothing is allocated from the receipt once it is reversed.
    /// </summary>
    public static Task LockReceiptAsync(Tx tx, Guid paymentId) => tx.LockAsync("core.payment_allocation", paymentId.ToString());

    /// <summary>Every allocation of a receipt, whatever policy it settles, as stored, in the order they are read for its mirror.</summary>
    public static async Task<IReadOnlyList<ReceiptLine>> ReceiptLinesAsync(Tx tx, Guid paymentId) =>
        (await tx.QueryAsync<ReceiptLine>("""
            select installment_id as InstallmentId, amount as Amount, amount_gel as AmountGel, rate as Rate from core.payment_allocation
            where payment_id = @paymentId
            order by created_at, amount_gel, lower(convert(nchar(36), id)) collate Latin1_General_BIN2
            """, new { paymentId })).ToList();

    /// <summary>
    /// Reverses a receipt (review #9; docs/07 §4.3ა): a mirror payment with the opposite sign and opposite allocations,
    /// dated <paramref name="on"/>, so the original and its reversal both stay on record. Every amount, amount in GEL and
    /// rate is the stored one, negated exactly. The caller holds the receipt's lock and has checked that it is a receipt
    /// not yet reversed; a second concurrent reversal fails on the unique index over reverses_payment_id. A reversal
    /// approved by the financial director (0038) carries its request. Returns the reversal's id.
    /// </summary>
    public static async Task<Guid> MirrorReceiptAsync(Tx tx, ReceiptToReverse original, DateOnly on, string reference, Guid userId, Guid? requestId = null)
    {
        var reversal = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert into core.payment (id, received_on, amount_gel, method, kind, reverses_payment_id, payer_party_id, reference, created_by, reversal_request_id)
            values (@reversal, @on, @amountGel, @method, N'reversal', @originalId, @payer, @reference, @userId, @requestId)
            """, new { reversal, on, amountGel = -original.AmountGel, method = original.Method, originalId = original.Id, payer = original.PayerPartyId, reference, userId, requestId });
        // A receipt that gave money back (a transfer) has negative lines too, and the guard (0024) checks the running sum
        // row by row, so the mirror rows must be written in an order that keeps every running sum between the receipt's
        // amount and zero. An order is found by search (finance OrderMirrorLines, H60/H61); should the search report
        // 'impossible' or 'budget', the reversal is refused as a whole and nothing is written.
        var lines = await ReceiptLinesAsync(tx, original.Id);
        var found = Receivable.OrderMirrorLines(lines.Select(l => Tetri(l.AmountGel)).ToList(), Tetri(original.AmountGel));
        if (found.Order is null)
            throw new InvalidOperationException($"no order keeps the reversal of receipt {original.Id} within its amount ({found.Reason} after {found.States} search states, {lines.Count} lines)");
        foreach (var i in found.Order)
        {
            var line = lines[i];
            await tx.ExecuteAsync("""
                insert into core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on)
                values (@reversal, @installmentId, @amount, @amountGel, @rate, @on)
                """, new { reversal, installmentId = line.InstallmentId, amount = -line.Amount, amountGel = -line.AmountGel, rate = line.Rate, on });
        }
        return reversal;
    }
}
