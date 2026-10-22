using Dapper;
using Insurance.Application.Data;

namespace Insurance.Integration.Tests;

/// <summary>Fictional rows for database tests, written through the application role (as the application would).</summary>
public static class Fixtures
{
    public static async Task<Guid> UserAsync(TestDatabase db, string role = "admin")
    {
        var id = Guid.NewGuid();
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code)
            values (@id, @username, N'სატესტო მომხმარებელი', N'scrypt$00$00', @role)
            """, new { id, username = $"test.{id:N}"[..30], role });
        return id;
    }

    public static async Task<Guid> PersonAsync(Tx tx, string? taxId = null)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert core.party (id, kind, tax_id, first_name, last_name) values (@id, N'person', @taxId, N'გიორგი', N'ტესტიშვილი')
            """, new { id, taxId = taxId ?? RandomTaxId() });
        return id;
    }

    public static string RandomTaxId() => string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Random.Shared.Next(10))));

    public sealed record PolicyRows(Guid PolicyId, Guid LineId, Guid InstallmentId, Guid PartyId);

    /// <summary>An active GEL policy with one CASCO line, its issue premium row and one instalment.</summary>
    public static async Task<PolicyRows> ActivePolicyAsync(Tx tx, Guid userId, DateOnly from, decimal premium = 1200m, string currency = "GEL", decimal rate = 1m)
    {
        var party = await PersonAsync(tx);
        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        var line = Guid.NewGuid();
        var installment = Guid.NewGuid();
        var to = from.AddYears(1);
        await tx.ExecuteAsync("""
            insert core.contract (id, number, signed_on, policyholder_id, created_by) values (@contract, @contractNo, @from, @party, @userId);
            insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate,
                                created_by, segment, approved_at, approved_by, product_id)
            values (@policy, @contract, @policyNo, @from, @from, @to, N'active', @currency, @rate, @userId, N'retail', sysdatetimeoffset(), @userId,
                    (select id from core.product where code = N'CASCO'));
            insert core.policy_line (id, policy_id, class_code, premium, premium_gel, product_coverage_id)
            values (@line, @policy, N'5.1', @premium, round(@premium * @rate, 2),
                    (select pc.id from core.product_coverage pc join core.product p on p.id = pc.product_id where p.code = N'CASCO' and pc.code = N'damage'));
            insert core.premium_transaction (policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
            values (@line, N'issue', @premium, round(@premium * @rate, 2), @from, @from, @to, @userId);
            insert core.installment (id, policy_id, due_on, amount) values (@installment, @policy, @from, @premium);
            """, new { contract, contractNo = "C-" + contract.ToString("N")[..10], policy, policyNo = "CAS-" + policy.ToString("N")[..10], line, installment, party, from, to, premium, currency, rate, userId });
        return new PolicyRows(policy, line, installment, party);
    }

    public static async Task<Guid> ReceiptAsync(Tx tx, DateOnly on, decimal amountGel, Guid? payer = null)
    {
        var id = Guid.NewGuid();
        await tx.ExecuteAsync("""
            insert core.payment (id, received_on, amount_gel, method, payer_party_id) values (@id, @on, @amountGel, N'bank', @payer)
            """, new { id, on, amountGel, payer });
        return id;
    }
}
