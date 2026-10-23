using System.Numerics;
using System.Text.RegularExpressions;
using Insurance.Application.Data;

namespace Insurance.Application.Services;

/// <summary>
/// A change's number goes by the first policy of its chain (owner, 13.10.2026; docs/05 a16; legacy PR #225 change-number.ts):
/// MHL000001's changes are MHL000001/1, MHL000001/2 in turn, a change of MHL000001/1 too, never MHL000001/1/1. With a word,
/// the next of that kind instead — a request awaiting approval (ROOT/მოთხოვნა-1) or the record of an open-month correction
/// (ROOT/შესწორება-1), neither a policy of its own. The next is one past the highest taken, so each stays unique; gaps are
/// never filled. The number is given on approval (owner, 14.10.2026); the request and correction forms are the old program's
/// technical choice, not separately approved by the owner.
/// </summary>
public static partial class ChangeNumber
{
    [GeneratedRegex("^[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Ordinal();

    public static string Next(string root, IEnumerable<string> taken, string? word = null)
    {
        var prefix = word is null ? $"{root}/" : $"{root}/{word}-";
        var highest = taken.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..]).Where(rest => Ordinal().IsMatch(rest))
            .Select(rest => BigInteger.Parse(rest, System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(BigInteger.Zero).Max();
        return prefix + (highest + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The first policy of the chain a policy belongs to, and the numbers already taken under it. The policy and then that
    /// first policy are locked, in this order wherever a change is entered or approved, so two changes of one original are
    /// numbered one after the other.
    /// </summary>
    public static async Task<(string Root, IReadOnlyList<string> Taken)> NumberingAsync(Tx tx, Guid policyId)
    {
        await tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @policyId", new { policyId });
        var root = (await tx.QuerySingleOrDefaultAsync<(Guid Id, string Number)?>("""
            with chain (id, number, replaces_policy_id) as (
              select id, number, replaces_policy_id from core.policy where id = @policyId
              union all
              select p.id, p.number, p.replaces_policy_id from core.policy p join chain c on p.id = c.replaces_policy_id)
            select id, number from chain where replaces_policy_id is null
            option (maxrecursion 0)
            """, new { policyId })) ?? throw new InvalidOperationException($"policy {policyId} has no first policy");
        if (root.Id != policyId)
            await tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = root.Id });
        var prefix = root.Number + "/";
        var taken = (await tx.QueryAsync<string>("select number from core.policy where left(number, len(@prefix)) = @prefix", new { prefix })).ToList();
        return (root.Number, taken);
    }
}
