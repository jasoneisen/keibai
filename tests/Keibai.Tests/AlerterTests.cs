using System.Net;
using Keibai.Core.Alerting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keibai.Tests;

public class AlerterTests
{
    [Fact]
    public async Task Ntfy_posts_body_to_topic_url_with_title_and_priority()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(async req =>
        {
            captured = req;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new AlertOptions
        {
            Ntfy = new NtfyOptions { BaseUrl = "https://ntfy.example", Topic = "my-secret-topic" },
        };
        var alerter = new NtfyAlerter(
            new SingleClientFactory(new HttpClient(handler)),
            new StaticOptionsMonitor<AlertOptions>(options),
            NullLogger<NtfyAlerter>.Instance);

        await alerter.SendAsync(new Alert("Court 31111 fetch failed", "3 retries exhausted.", AlertSeverity.Critical));

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://ntfy.example/my-secret-topic", captured.RequestUri!.ToString());
        Assert.Equal("Court 31111 fetch failed", Assert.Single(captured.Headers.GetValues("Title")));
        Assert.Equal("5", Assert.Single(captured.Headers.GetValues("Priority")));
        Assert.Equal("3 retries exhausted.", capturedBody);
    }

    [Fact]
    public async Task Ntfy_never_throws_when_the_server_errors()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        var alerter = new NtfyAlerter(
            new SingleClientFactory(new HttpClient(handler)),
            new StaticOptionsMonitor<AlertOptions>(new AlertOptions { Ntfy = new NtfyOptions { Topic = "t" } }),
            NullLogger<NtfyAlerter>.Instance);

        // Must not throw — a dead alert sink can never break ingestion.
        await alerter.SendAsync(new Alert("t", "b"));
    }

    [Fact]
    public async Task Composite_fans_out_to_every_sink_even_when_one_throws()
    {
        var a = new RecordingAlerter();
        var b = new ThrowingAlerter();
        var c = new RecordingAlerter();
        var composite = new CompositeAlerter([a, b, c], NullLogger<CompositeAlerter>.Instance);

        await composite.SendAsync(new Alert("t", "b"));

        Assert.Equal(1, a.Count);
        Assert.Equal(1, c.Count); // c still received it despite b throwing
    }

    private sealed class RecordingAlerter : IAlerter
    {
        public int Count { get; private set; }

        public Task SendAsync(Alert alert, CancellationToken ct = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAlerter : IAlerter
    {
        public Task SendAsync(Alert alert, CancellationToken ct = default) =>
            throw new InvalidOperationException("sink down");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
