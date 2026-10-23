using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests;

/// <summary>The National Bank, unreachable: every request refused and counted (the legacy tests' refused fetch).</summary>
public sealed class RefusingNbg : IHttpClientFactory
{
    public List<string> Requests { get; } = [];

    private sealed class Handler(RefusingNbg owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner.Requests) owner.Requests.Add(request.RequestUri!.ToString());
            throw new HttpRequestException("network is forbidden in this test");
        }
    }

    public HttpClient CreateClient(string name) => new(new Handler(this));

    public static (Rates Rates, RefusingNbg Nbg) For(Db db)
    {
        var nbg = new RefusingNbg();
        return (new Rates(db, nbg), nbg);
    }
}

