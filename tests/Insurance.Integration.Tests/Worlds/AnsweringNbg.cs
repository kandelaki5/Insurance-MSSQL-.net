using System.Net;
using System.Text;
using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests;

/// <summary>
/// The National Bank answering, offline: every request to the "nbg" client is recorded and answered by <see cref="Answer"/>; nothing
/// leaves the process (the handler never calls the network). The default answer is <see cref="Payload"/> — the bank's JSON format as the
/// old code reads it (legacy src/lib/rates.ts, nbg-rate-cache.test.ts PAYLOAD), with the fields the bank sends besides (ignored by both
/// programs): fictional figures, not an official rate.
/// </summary>
public sealed class AnsweringNbg : IHttpClientFactory
{
    /// <summary>
    /// USD 314.15 per 100 and EUR 3.5 per 1 to keep; decoys to leave out: another currency (GBP), a zero rate (USD), a zero quantity (EUR).
    /// </summary>
    public const string Payload = """
        [{"date":"2026-07-31T00:00:00.000Z","currencies":[
          {"code":"USD","quantity":100,"rateFormated":"314.1500","diffFormated":"0.0100","rate":314.15,"name":"აშშ დოლარი","diff":0.01,"date":"2026-07-30T17:45:00.000Z","validFromDate":"2026-07-31T00:00:00.000Z"},
          {"code":"EUR","quantity":1,"rateFormated":"3.5000","diffFormated":"0.0000","rate":3.5,"name":"ევრო","diff":0,"date":"2026-07-30T17:45:00.000Z","validFromDate":"2026-07-31T00:00:00.000Z"},
          {"code":"GBP","quantity":1,"rateFormated":"7.0000","diffFormated":"0.0000","rate":7,"name":"გირვანქა სტერლინგი","diff":0,"date":"2026-07-30T17:45:00.000Z","validFromDate":"2026-07-31T00:00:00.000Z"},
          {"code":"USD","quantity":1,"rateFormated":"0.0000","diffFormated":"0.0000","rate":0,"name":"აშშ დოლარი","diff":0,"date":"2026-07-30T17:45:00.000Z","validFromDate":"2026-07-31T00:00:00.000Z"},
          {"code":"EUR","quantity":0,"rateFormated":"100.0000","diffFormated":"0.0000","rate":100,"name":"ევრო","diff":0,"date":"2026-07-30T17:45:00.000Z","validFromDate":"2026-07-31T00:00:00.000Z"}
        ]}]
        """;

    /// <summary>A day the bank publishes nothing for: the answer has the date and no currency.</summary>
    public const string NoRates = """[{"date":"2026-07-31T00:00:00.000Z","currencies":[]}]""";

    public sealed record Request(string Method, string Url);

    public List<Request> Requests { get; } = [];

    /// <summary>The answer to each request (default: 200 with <see cref="Payload"/>); it may throw, as a failing connection does.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => Json(Payload);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(AnsweringNbg owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner.Requests) owner.Requests.Add(new Request(request.Method.Method, request.RequestUri!.ToString()));
            return Task.FromResult(owner.Answer(request));
        }
    }

    public HttpClient CreateClient(string name) => name == "nbg"
        ? new HttpClient(new Handler(this))
        : throw new InvalidOperationException($"no HTTP client '{name}' in this test");

    public static (Rates Rates, AnsweringNbg Nbg) For(Db db)
    {
        var nbg = new AnsweringNbg();
        return (new Rates(db, nbg), nbg);
    }
}
