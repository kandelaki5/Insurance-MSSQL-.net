using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Insurance.Web.Tests;

/// <summary>
/// The real application served by Kestrel on a free local port, on a fresh test database, with a headless Chromium
/// driven by Playwright. The browser is the one installed for Playwright; set PLAYWRIGHT_CHROMIUM_EXECUTABLE to use
/// another (in this container the preinstalled /opt/pw-browsers build).
/// </summary>
public sealed class BrowserApp : IAsyncLifetime
{
    public WebApp Web { get; } = new();
    public string BaseUrl { get; private set; } = "";
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    private KestrelFactory? _server;

    /// <summary>The application on a real socket (Kestrel), on the test database.</summary>
    private sealed class KestrelFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Insurance", connection);
            // No test reaches the network: the National Bank and the UN list refuse every request.
            builder.ConfigureTestServices(s => s.AddSingleton<IHttpClientFactory>(new Insurance.Integration.Tests.RefusingNbg()));
        }
    }

    public async Task InitializeAsync()
    {
        await Web.InitializeAsync();
        _server = new KestrelFactory(Web.Db.AppConnection);
        var port = FreePort();
        _server.UseKestrel(port);
        _server.StartServer();
        BaseUrl = $"http://127.0.0.1:{port}";
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE");
        if (string.IsNullOrEmpty(executable) && File.Exists("/opt/pw-browsers/chromium-1194/chrome-linux/chrome"))
            executable = "/opt/pw-browsers/chromium-1194/chrome-linux/chrome";
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable,
            // The application runs on this machine; never through a proxy from the environment.
            Args = ["--no-proxy-server"],
        });
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        Playwright.Dispose();
        if (_server is not null) await _server.DisposeAsync();
        await Web.DisposeAsync();
    }

    /// <summary>A new browser page signed in as a new user of the role, through the login form.</summary>
    public async Task<IPage> SignedInPageAsync(string role)
    {
        var (_, username, password) = await Web.UserAsync(role);
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions { Locale = "ka-GE", TimezoneId = "Asia/Tbilisi" });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl + "/login");
        await page.FillAsync("#username", username);
        await page.FillAsync("#password", password);
        await page.RunAndWaitForResponseAsync(() => page.ClickAsync("button.btn"), r => r.Url.EndsWith("/login", StringComparison.Ordinal) && r.Request.Method == "POST");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Contains(await context.CookiesAsync(), c => c.Name == "session");
        return page;
    }
}
