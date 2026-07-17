using Alba;
using Keibai.Core.Composition;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Tests;

/// <summary>
/// Boots the real <c>Program</c> via Alba against a unique ephemeral <c>test_*</c> schema per run, so
/// integration tests exercise the true composition (ancillary Marten store + Wolverine). The connection
/// string comes from <c>ConnectionStrings__Keibai</c> (set by test.sh / CI) or defaults to the
/// devcontainer <c>db:5432</c>.
///
/// SAFETY: the overrides are passed as process ENVIRONMENT VARIABLES, not (only) via Alba's
/// ConfigureAppConfiguration. Program's composition root reads configuration while the
/// WebApplicationBuilder is being built, and WebApplicationFactory applies in-memory test config too
/// late for that read — so the in-memory override silently fell back to the production 'keibai' schema,
/// and DisposeAsync's clean once WIPED real crawl data (2026-07-16). Env vars are part of the builder's
/// native configuration from the start, so they always reach the composition root. Two interlocks below
/// make any regression loud instead of destructive.
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

        // Env vars reach Program's composition root regardless of host-builder plumbing (see class doc).
        // Process-wide is fine: one fixture instance per test process, and every value is test-only.
        Environment.SetEnvironmentVariable("ConnectionStrings__Keibai", connection);
        Environment.SetEnvironmentVariable("Keibai__SchemaName", _schema);
        Environment.SetEnvironmentVariable("Keibai__Ingestion__Enabled", "false");
        Environment.SetEnvironmentVariable("Keibai__Auth__SharedPassword", string.Empty);
        Environment.SetEnvironmentVariable(
            "Keibai__BlobStore__Root", Path.Combine(Path.GetTempPath(), "keibai-test-" + Guid.NewGuid()));
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseSetting("environment", "Testing");
        });

        // INTERLOCK 1: fail fast if the booted store is not on this run's ephemeral schema. A silent
        // fallback to 'keibai' here means the override plumbing regressed — refuse to run (and null the
        // host so DisposeAsync cannot clean the wrong schema).
        var actualSchema = Store.Options.DatabaseSchemaName;
        if (actualSchema != _schema)
        {
            var host = Host;
            Host = null!;
            await host.DisposeAsync();
            throw new InvalidOperationException(
                $"Test host booted against schema '{actualSchema}' instead of the ephemeral '{_schema}'. " +
                "Refusing to run: cleanup against a non-test schema would destroy real data.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null)
        {
            return;
        }

        // Drop the ephemeral schema so re-runs stay clean.
        try
        {
            // INTERLOCK 2: never clean a schema we did not create. Belt-and-braces with the boot check —
            // CompletelyRemoveAllAsync against 'keibai' (or anything non-test_*) is the data-loss path.
            if (Store.Options.DatabaseSchemaName.StartsWith("test_", StringComparison.Ordinal))
            {
                await Store.Advanced.Clean.CompletelyRemoveAllAsync();
            }
        }
        catch
        {
            // best-effort teardown
        }

        await Host.DisposeAsync();
    }
}
