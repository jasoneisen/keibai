using JasperFx;
using Keibai;
using Keibai.Core.Composition;
using Keibai.Core.Ingestion;
using Keibai.Web.Components;
using Wolverine;
using Wolverine.Http;

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

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Redirect the bare root to the /jp market namespace.
app.MapGet("/", () => Results.Redirect("/jp"));

// JasperFx CLI verbs (marten/wolverine/projections/etc.) plus a plain `run`.
return await app.RunJasperFxCommands(args);
