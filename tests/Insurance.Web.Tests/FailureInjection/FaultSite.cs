using System.Net;
using Dapper;
using Insurance.Application.Data;
using Insurance.Domain.Common;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.FailureInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests.FailureInjection;

/// <summary>
/// The real web application on a fresh test database, as WebApp, with the tests' command interceptor (Faults) in its DbOptions —
/// every statement the application sends passes it, so one read can be made to fail once or a grant taken away between two
/// statements — and the National Bank unreachable. In Development an error that leaves a page is answered 500 with its message
/// (the developer exception page): "the same error comes out, no page".
/// </summary>
public sealed class FaultSite : IAsyncLifetime, IAsyncDisposable
{
    public TestDatabase Db { get; }
    public Faults Faults { get; } = new();
    public RefusingNbg Nbg { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private readonly bool owned;

    public FaultSite() : this(new TestDatabase(), owned: true) { }

    private FaultSite(TestDatabase db, bool owned)
    {
        Db = db;
        this.owned = owned;
    }

    /// <summary>A site over a database made elsewhere (a report world's); it is not dropped with the site.</summary>
    public static FaultSite Over(TestDatabase db)
    {
        var site = new FaultSite(db, owned: false);
        site.Factory = Build(db.AppConnection, site.Faults, site.Nbg, db.Clock);
        return site;
    }

    public static WebApplicationFactory<Program> Build(string connection, ICommandInterceptor interceptor, IHttpClientFactory nbg, TimeProvider clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Insurance", connection);
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(new DbOptions { ConnectionString = connection, Interceptor = interceptor, Clock = clock });
                s.AddSingleton(nbg);
            });
        });

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        Factory = Build(Db.AppConnection, Faults, Nbg, Db.Clock);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (owned) await Db.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    /// <summary>A site with a database of its own, for a test whose figures count every row of the database.</summary>
    public static async Task<FaultSite> CreateAsync()
    {
        var site = new FaultSite();
        await site.InitializeAsync();
        return site;
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A fictional user of the role with a random password, signed in through the login form; the client and the user's id.</summary>
    public async Task<(HttpClient Client, Guid UserId)> SignedInAsync(string role)
    {
        var id = Guid.NewGuid();
        var username = $"test.{role}.{id.ToString("N")[..8]}";
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await using (var c = await Db.OpenAdminAsync())
            await c.ExecuteAsync("""
                insert core.app_user (id, username, full_name, password_hash, role_code, branch_id)
                values (@id, @username, @fullName, @hash, @role, (select id from core.branch where code = N'HQ'))
                """, new { id, username, fullName = $"სატესტო {role} {id.ToString("N")[..6]}", hash = Password.HashPassword(password), role });
        var client = Client();
        var response = await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (client, id);
    }

    public async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = await Db.OpenAdminAsync();
        await c.ExecuteAsync(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await Db.OpenAdminAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!;
    }

    /// <summary>A change made on a connection of its own, synchronously (inside the interceptor, between two statements).</summary>
    public void Now(string sql, object? param = null)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(Db.AdminConnection);
        c.Open();
        c.Execute(sql, param);
    }

    /// <summary>The page failed with the injected error: 500 and its message, none of the page.</summary>
    public static async Task<string> FailedAsync(HttpResponseMessage response, InjectedFault fault)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(fault.Message, WebUtility.HtmlDecode(body), StringComparison.Ordinal);
        Assert.DoesNotContain("<nav", body, StringComparison.Ordinal);
        return body;
    }
}
