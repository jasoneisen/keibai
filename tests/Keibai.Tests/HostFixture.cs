using Alba;
using Keibai.Core.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Tests;

/// <summary>
/// Boots the real <c>Program</c> via Alba against a unique ephemeral <c>keibai</c> schema per run, so
/// integration tests exercise the true composition (ancillary Marten store + Wolverine). The connection
/// string comes from <c>ConnectionStrings__Keibai</c> (set by test.sh / CI) or defaults to the
/// devcontainer <c>db:5432</c>.
/// </summary>
public sealed class HostFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public IKeibaiStore Store => Host.Services.GetRequiredService<IKeibaiStore>();

    private string _schema = "keibai";

    public async ValueTask InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Keibai")
            ?? "Host=db;Port=5432;Database=keibai;Username=postgres;Password=postgres";

        // Ephemeral per-run schema so a shared 'keibai' database isolates each test host completely.
        _schema = "test_" + Guid.NewGuid().ToString("N");

        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseSetting("environment", "Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Keibai"] = connection,
                    ["Keibai:SchemaName"] = _schema,
                    // Keep ingestion off so a booted host never touches BIT; tests drive handlers directly.
                    ["Keibai:Ingestion:Enabled"] = "false",
                    ["Keibai:Auth:SharedPassword"] = string.Empty,
                    ["Keibai:BlobStore:Root"] = Path.Combine(Path.GetTempPath(), "keibai-test-" + Guid.NewGuid()),
                });
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        // Drop the ephemeral schema so re-runs stay clean.
        try
        {
            await Store.Advanced.Clean.CompletelyRemoveAllAsync();
        }
        catch
        {
            // best-effort teardown
        }

        await Host.DisposeAsync();
    }
}
