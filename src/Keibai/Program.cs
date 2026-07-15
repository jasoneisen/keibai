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

// Offline backfill: re-parse stored detail captures to populate new attribute fields (no BIT traffic).
app.MapPost("/admin/reparse-details", async (IMessageBus bus) =>
{
    await bus.PublishAsync(new ReparseDetailCaptures());
    return Results.Accepted("/admin/reparse-details", new { enqueued = "reparse" });
});

// Sale-results triggers: backfill one court's ~3 years of 売却結果, the whole nationwide backfill, or
// sync one court's latest round.
app.MapPost("/results/backfill/{courtId}", async (string courtId, IMessageBus bus) =>
{
    await bus.PublishAsync(new BackfillResults(courtId));
    return Results.Accepted($"/results/backfill/{courtId}", new { enqueued = courtId });
});

app.MapPost("/results/backfill-all", async (IMessageBus bus) =>
{
    await bus.PublishAsync(new BackfillAllResults());
    return Results.Accepted("/results/backfill-all", new { enqueued = "all-courts" });
});

app.MapPost("/results/sync/{courtId}", async (string courtId, IMessageBus bus) =>
{
    await bus.PublishAsync(new SyncRoundResults(courtId, DateOnly.FromDateTime(DateTime.UtcNow)));
    return Results.Accepted($"/results/sync/{courtId}", new { enqueued = courtId });
});

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Redirect the bare root to the /jp market namespace.
app.MapGet("/", () => Results.Redirect("/jp"));

// JasperFx CLI verbs (marten/wolverine/projections/etc.) plus a plain `run`.
return await app.RunJasperFxCommands(args);
