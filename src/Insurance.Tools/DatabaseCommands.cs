using Dapper;
using Insurance.Domain.Common;
using Microsoft.Data.SqlClient;

namespace Insurance.Tools;

public static class DatabaseCommands
{
    /// <summary>Creates (or resets the password of) an administrator (legacy scripts/seed.ts).</summary>
    public static async Task SeedAdminAsync(string adminConnection, string username, string fullName, string password, Action<string> log)
    {
        var hash = Password.HashPassword(password);
        await using var db = new SqlConnection(adminConnection);
        await db.OpenAsync();
        await db.ExecuteAsync("""
            merge core.app_user as u
            using (select lower(@username) as username) as s on u.username = s.username
            when matched then update set password_hash = @hash, is_active = 1
            when not matched then insert (username, full_name, password_hash, role_code, branch_id)
              values (s.username, @fullName, @hash, N'admin', (select id from core.branch where code = N'HQ'));
            """, new { username, fullName, hash });
        log($"administrator \"{username.ToLowerInvariant()}\" is ready");
    }
}
