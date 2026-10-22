using System.Security.Cryptography;
using System.Text;
using Dapper;
using Insurance.Application.Data;
using Insurance.Domain.Common;

namespace Insurance.Application.Services;

/// <summary>The signed-in user, as every page and action sees it (legacy CurrentUser).</summary>
public sealed record CurrentUser(Guid Id, string Username, string FullName, string RoleCode, string RoleName)
{
    public PermissionSubject Subject => new(Id.ToString(), RoleCode);

    /// <summary>Every role but the viewer may write (legacy canWrite); finer rights come from the permissions.</summary>
    public bool CanWrite => RoleCode != "viewer";
    public bool IsAdmin => RoleCode == "admin";
}

/// <summary>
/// Own authentication (legacy src/lib/auth.ts and login/actions.ts): a random token in an http-only cookie, only its
/// SHA-256 kept in core.session for 12 hours; scrypt password hashes; password guessing slowed per account and per
/// network source.
/// </summary>
public sealed class Auth(Db db)
{
    public const string Cookie = "session";
    public const int SessionHours = 12;
    // After this many failures inside the window further attempts are refused until the window passes.
    public const int WindowMinutes = 15;
    public const int MaxFailuresPerUser = 5;
    public const int MaxFailuresPerSource = 30;

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A new session for the user; returns the token for the cookie.</summary>
    public async Task<string> CreateSessionAsync(Guid userId)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await db.ReadAsync(c => c.ExecuteAsync("""
            insert into core.session (token_hash, user_id, expires_at)
            values (@hash, @userId, dateadd(hour, @hours, sysdatetimeoffset()))
            """, new { hash = HashToken(token), userId, hours = SessionHours }));
        return token;
    }

    public Task DestroySessionAsync(string token) =>
        db.ReadAsync(c => c.ExecuteAsync("delete from core.session where token_hash = @hash", new { hash = HashToken(token) }));

    public async Task<CurrentUser?> UserForTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        return await db.ReadAsync(c => c.QuerySingleOrDefaultAsync<CurrentUser>("""
            select u.id as Id, u.username as Username, u.full_name as FullName, u.role_code as RoleCode, r.name_ka as RoleName
            from core.session s
            join core.app_user u on u.id = s.user_id
            join core.role r on r.code = u.role_code
            where s.token_hash = @hash and s.expires_at > sysdatetimeoffset() and u.is_active = 1
            """, new { hash = HashToken(token) }));
    }

    /// <summary>
    /// The user id when the name and password are right and the attempt is not throttled; otherwise null. The same
    /// answer for a throttled, unknown or wrong attempt, so nothing is revealed. Every attempt is recorded.
    /// </summary>
    public async Task<Guid?> LoginAsync(string rawUsername, string password, string? source)
    {
        var username = rawUsername.Trim().ToLowerInvariant();
        if (username.Length > 100) username = username[..100];
        return await db.ReadAsync(async c =>
        {
            var counts = await c.QuerySingleAsync<(int ByUser, int BySource)>("""
                select count(case when username = @username then 1 end),
                       count(case when source is not null and source = @source then 1 end)
                from core.login_attempt
                where succeeded = 0 and attempted_at > dateadd(minute, -@window, sysdatetimeoffset())
                """, new { username, source, window = WindowMinutes });
            if (counts.ByUser >= MaxFailuresPerUser || counts.BySource >= MaxFailuresPerSource) return (Guid?)null;

            var user = await c.QuerySingleOrDefaultAsync<(Guid Id, string Hash)?>(
                "select id, password_hash from core.app_user where username = @username and is_active = 1", new { username });
            var ok = user is { } u && Password.VerifyPassword(password, u.Hash);

            await c.ExecuteAsync("insert into core.login_attempt (username, source, succeeded) values (@username, @source, @ok)", new { username, source, ok });
            await c.ExecuteAsync("delete from core.login_attempt where attempted_at < dateadd(day, -1, sysdatetimeoffset())");
            return ok ? user!.Value.Id : (Guid?)null;
        });
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
