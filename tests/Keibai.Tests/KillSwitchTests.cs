using Keibai.Core.Bit;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

public class KillSwitchTests
{
    private sealed class StubOptionsMonitor(BitOptions value) : IOptionsMonitor<BitOptions>
    {
        public BitOptions CurrentValue { get; } = value;
        public BitOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<BitOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Refuses_all_traffic_when_disabled()
    {
        var options = new StubOptionsMonitor(new BitOptions { Enabled = false });
        var limiter = new BitRateLimiter(TimeSpan.Zero, new FakeTimeProvider());
        var inner = new CapturingHandler();
        var handler = new BitRateLimitingHandler(limiter, options) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<IngestionDisabledException>(() => client.GetAsync("/"));
        Assert.False(inner.WasCalled);
    }

    [Fact]
    public async Task Allows_traffic_when_enabled()
    {
        var options = new StubOptionsMonitor(new BitOptions { Enabled = true });
        var limiter = new BitRateLimiter(TimeSpan.Zero, new FakeTimeProvider());
        var inner = new CapturingHandler();
        var handler = new BitRateLimitingHandler(limiter, options) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var resp = await client.GetAsync("/");

        Assert.True(inner.WasCalled);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }
}
