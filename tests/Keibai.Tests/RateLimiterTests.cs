using Keibai.Core.Bit;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task First_acquire_is_immediate()
    {
        var time = new FakeTimeProvider();
        var limiter = new BitRateLimiter(TimeSpan.FromSeconds(3), time);

        var task = limiter.AcquireAsync();

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task Second_acquire_waits_the_full_interval()
    {
        var time = new FakeTimeProvider();
        var limiter = new BitRateLimiter(TimeSpan.FromSeconds(3), time);

        await limiter.AcquireAsync();
        var second = limiter.AcquireAsync();

        // Not yet allowed: only 2.999s have passed.
        time.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.False(second.IsCompleted);

        // Crossing the 3s boundary releases it.
        time.Advance(TimeSpan.FromMilliseconds(1));
        await second;
    }

    [Fact]
    public async Task Enforces_at_least_the_interval_between_many_requests()
    {
        var time = new FakeTimeProvider();
        var interval = TimeSpan.FromSeconds(3);
        var limiter = new BitRateLimiter(interval, time);

        // Kick off 4 acquisitions; each subsequent one must be spaced by the interval.
        await limiter.AcquireAsync();
        for (var i = 0; i < 3; i++)
        {
            var next = limiter.AcquireAsync();
            time.Advance(interval - TimeSpan.FromMilliseconds(1));
            Assert.False(next.IsCompleted);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await next;
        }
    }
}
