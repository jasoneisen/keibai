using JasperFx;
using Keibai;
using Keibai.Core.Composition;
using Keibai.Core.Ingestion;
using Keibai.Web.Components;
using Wolverine;
using Wolverine.Http;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

// Composition root: the two extension methods ARE the merge artifact. At merge time the OMD host calls
// AddKeibai + ConfigureKeibaiMessaging beside its own single Marten/Wolverine/Blazor registration.
builder.Services.AddKeibai(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<NightlySweepScheduler>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddWolverineHttp();

builder.Host.UseWolverine(opts =>
{
    opts.ConfigureKeibaiMessaging();

    // Host-only durability: back the durable local queues with Postgres so a restart mid-crawl loses no
    // queued work. Deliberately NOT in ConfigureKeibaiMessaging (the merge artifact) — at merge time the
    // OMD host owns Wolverine's message store. Skipped under Testing (Alba boots many ephemeral hosts;
    // handler-driven tests don't need the durable transport).
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        var messagingConnection = builder.Configuration.GetConnectionString("Keibai")
            ?? throw new InvalidOperationException("ConnectionStrings:Keibai is required for durable messaging.");
        opts.PersistMessagesWithPostgresql(messagingConnection, "keibai_wolverine");
    }
});

var app = builder.Build();

// Shared-password gate (host-only middleware; blank password = open, for dev/test).
app.UseMiddleware<SharedPasswordMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Manual per-prefecture trigger for testing: POST /sync/prefecture/13 enqueues a Tokyo sweep.
app.MapPost("/sync/prefecture/{prefectureId}", async (string prefectureId, IMessageBus bus) =>
{
    await bus.PublishAsync(new SyncPrefectureListings(prefectureId));
    return Results.Accepted($"/sync/prefecture/{prefectureId}", new { enqueued = prefectureId });
});

// Manual nationwide trigger.
app.MapPost("/sync/all", async (IMessageBus bus) =>
{
    await bus.PublishAsync(new SyncCourts());
    return Results.Accepted("/sync/all", new { enqueued = "nationwide" });
});

// Phase 2 ops triggers: drain the archive backlog (deadline-ordered) + due re-checks; run the monitor.
app.MapPost("/archive/schedule", async (IMessageBus bus) =>
{
    await bus.PublishAsync(new ScheduleArchiveWork());
    return Results.Accepted("/archive/schedule", new { enqueued = "archive-work" });
});

app.MapPost("/monitor/run", async (IMessageBus bus) =>
{
    await bus.PublishAsync(new SummarizeSweep());
    return Results.Accepted("/monitor/run", new { enqueued = "monitor" });
});

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Redirect the bare root to the /jp market namespace.
app.MapGet("/", () => Results.Redirect("/jp"));

// JasperFx CLI verbs (marten/wolverine/projections/etc.) plus a plain `run`.
return await app.RunJasperFxCommands(args);
