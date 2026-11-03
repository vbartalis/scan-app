using ScanApp.App.Views;

namespace ScanApp.App.Tests;

public class LatestValueThrottleTests
{
    private TimeSpan _now = TimeSpan.Zero;

    [Fact]
    public void LatestValueThrottle_AppliesOnlyNewestValuePerInterval()
    {
        var applied = new List<int>();
        var throttle = new LatestValueThrottle<int>(TimeSpan.FromMilliseconds(30), applied.Add, () => _now);

        throttle.Post(1);
        throttle.Post(2);
        throttle.Post(3);
        _now += TimeSpan.FromMilliseconds(30);
        throttle.Tick();
        throttle.Tick();

        Assert.Equal([3], applied);
    }

    [Fact]
    public void LatestValueThrottle_WithinInterval_WaitsForNextInterval()
    {
        var applied = new List<int>();
        var throttle = new LatestValueThrottle<int>(TimeSpan.FromMilliseconds(30), applied.Add, () => _now);
        _now += TimeSpan.FromMilliseconds(30);
        throttle.Post(1);
        throttle.Tick();

        throttle.Post(2);
        _now += TimeSpan.FromMilliseconds(10);
        throttle.Tick();
        Assert.Equal([1], applied);

        _now += TimeSpan.FromMilliseconds(20);
        throttle.Tick();
        Assert.Equal([1, 2], applied);
    }

    [Fact]
    public void LatestValueThrottle_NoPost_NoApply()
    {
        var applied = new List<int>();
        var throttle = new LatestValueThrottle<int>(TimeSpan.FromMilliseconds(30), applied.Add, () => _now);

        _now += TimeSpan.FromSeconds(1);
        throttle.Tick();

        Assert.Empty(applied);
    }
}
