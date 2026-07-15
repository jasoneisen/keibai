using Bunit;
using Keibai.Core.Alerting;
using Keibai.Web.Components.Pages;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Web.Tests;

/// <summary>Static-SSR render tests for the ops dashboard (<c>/jp/ops</c>) — fakes only, no DB.</summary>
public sealed class OpsPageTests
{
    private static OpsSnapshot Snapshot(
        double storageGb = 12.5,
        double maxGigabytes = 50,
        int courtsTotal = 3,
        IReadOnlyList<DisabledCourt>? disabledCourts = null,
        IReadOnlyList<PrefectureHealth>? prefectures = null,
        DailyStatsView? today = null,
        long? queueDepth = 7,
        IReadOnlyList<AlertRow>? recentAlerts = null) =>
        new(
            StorageBytes: (long)(storageGb * 1024 * 1024 * 1024),
            StorageGb: storageGb,
            MaxGigabytes: maxGigabytes,
            CourtsTotal: courtsTotal,
            DisabledCourts: disabledCourts ?? [],
            Prefectures: prefectures ?? [],
            Today: today ?? new DailyStatsView("2026-07-15", 40, 38, 2, 5, 3),
            QueueDepth: queueDepth,
            RecentAlerts: recentAlerts ?? []);

    private static PrefectureHealth Pref(
        string id = "13",
        string name = "東京都",
        string health = "green",
        int found = 42,
        int @new = 4,
        int errors = 0,
        bool blocked = false,
        IReadOnlyList<int>? history = null) =>
        new(id, name, DateTimeOffset.Parse("2026-07-15T09:30:00Z"), found, @new, errors, blocked, health,
            history ?? []);

    private static IRenderedComponent<Ops> Render(OpsSnapshot snapshot)
    {
        var reader = new FakeOpsReader { Snapshot = snapshot };
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<IOpsReader>(reader);
        return ctx.Render<Ops>(p => p.AddCascadingValue<HttpContext>(TestHttp.Get()));
    }

    [Fact]
    public void Healthy_snapshot_renders_storage_a_green_prefecture_and_today_stats()
    {
        var snap = Snapshot(
            storageGb: 12.5,
            prefectures: [Pref(name: "東京都", health: "green", found: 42, @new: 4)]);

        var cut = Render(snap);
        var html = cut.Markup;

        // Storage GB against threshold.
        Assert.Contains("12.5 GB", html);
        Assert.Contains("50 GB threshold", html);
        Assert.Contains("bg-success", html);

        // A green prefecture traffic-light row.
        Assert.Contains("東京都", html);
        Assert.Contains("text-bg-success", html);

        // Today's stats (archives 38/40, results 5, rechecks 3).
        Assert.Contains("38 / 40", html);
        Assert.Contains("2026-07-15", html);
    }

    [Fact]
    public void Disabled_court_renders_danger_alert_with_name_and_reason()
    {
        var snap = Snapshot(
            disabledCourts:
            [
                new DisabledCourt("0100", "札幌地方裁判所", "block hit", DateTimeOffset.Parse("2026-07-14T02:00:00Z")),
            ]);

        var cut = Render(snap);
        var alert = cut.Find("div.alert.alert-danger");
        var text = alert.TextContent;

        Assert.Contains("札幌地方裁判所", text);
        Assert.Contains("0100", text);
        Assert.Contains("block hit", text);
    }

    [Fact]
    public void Critical_alert_and_sparkline_render()
    {
        var snap = Snapshot(
            prefectures: [Pref(history: [3, 7, 5, 9, 4])],
            recentAlerts:
            [
                new AlertRow("Storage over threshold", "48 GB of 50 GB used", AlertSeverity.Critical,
                    DateTimeOffset.Parse("2026-07-15T08:00:00Z")),
            ]);

        var cut = Render(snap);
        var html = cut.Markup;

        // Critical alert appears in the recent-alerts list with a danger badge.
        Assert.Contains("Storage over threshold", html);
        Assert.Contains("text-bg-danger", html);
        Assert.DoesNotContain("No recent alerts", html);

        // Sparkline renders for a prefecture that has history.
        Assert.Contains("<span class=\"sparkline", html);
    }

    [Fact]
    public void Watchdog_off_when_threshold_is_zero()
    {
        var cut = Render(Snapshot(maxGigabytes: 0));
        Assert.Contains("watchdog off", cut.Markup);
    }
}
