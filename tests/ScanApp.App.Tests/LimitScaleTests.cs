using ScanApp.App.ViewModels;

namespace ScanApp.App.Tests;

public class LimitScaleTests
{
    [Fact]
    public void Ends_MapToNull_ByKind()
    {
        foreach (var kind in new[] { LimitKind.TrimStart, LimitKind.XMin, LimitKind.HeightMin })
        {
            var scale = new LimitScale(kind, -10, 10);
            Assert.Null(scale.ValueAt(0));
            Assert.Null(scale.ValueAt(-0.5));
            Assert.Equal(10, scale.ValueAt(1));
            Assert.Equal(0, scale.PositionOf(null));
        }
        foreach (var kind in new[] { LimitKind.TrimEnd, LimitKind.XMax, LimitKind.HeightMax })
        {
            var scale = new LimitScale(kind, -10, 10);
            Assert.Null(scale.ValueAt(1));
            Assert.Null(scale.ValueAt(1.5));
            Assert.Equal(-10, scale.ValueAt(0));
            Assert.Equal(1, scale.PositionOf(null));
        }
    }

    [Fact]
    public void PositionValue_RoundTrips()
    {
        foreach (var kind in Enum.GetValues<LimitKind>())
        {
            var scale = new LimitScale(kind, 2, 7);
            foreach (double position in new[] { 0.1, 0.25, 0.5, 0.9 })
            {
                double value = scale.ValueAt(position)!.Value;
                Assert.Equal(2 + 5 * position, value, 9);
                Assert.Equal(position, scale.PositionOf(value), 9);
            }
            Assert.Equal(0, scale.PositionOf(-100));
            Assert.Equal(1, scale.PositionOf(100));
        }
    }

    [Fact]
    public void Nudge_StepsByKind()
    {
        Assert.Equal(0.05, new LimitScale(LimitKind.TrimStart, 0, 5).Step);
        Assert.Equal(0.05, new LimitScale(LimitKind.TrimEnd, 0, 5).Step);
        Assert.Equal(1, new LimitScale(LimitKind.XMin, -100, 100).Step);
        Assert.Equal(1, new LimitScale(LimitKind.XMax, -100, 100).Step);
        Assert.Equal(0.5, new LimitScale(LimitKind.HeightMin, -2, 12).Step);
        Assert.Equal(0.5, new LimitScale(LimitKind.HeightMax, -2, 12).Step);

        Assert.Equal(2.1, new LimitScale(LimitKind.TrimStart, 0, 5).Nudge(2, 2)!.Value, 9);
        Assert.Equal(-13, new LimitScale(LimitKind.XMax, -100, 100).Nudge(-10, -3)!.Value, 9);
        Assert.Equal(4.5, new LimitScale(LimitKind.HeightMin, -2, 12).Nudge(4, 1)!.Value, 9);
        // From automatic / no limit, a step goes inward from the limit's own end.
        Assert.Equal(-99, new LimitScale(LimitKind.XMin, -100, 100).Nudge(null, 1)!.Value, 9);
        Assert.Equal(11.5, new LimitScale(LimitKind.HeightMax, -2, 12).Nudge(null, -1)!.Value, 9);
        // The far end clamps.
        Assert.Equal(100, new LimitScale(LimitKind.XMin, -100, 100).Nudge(99.5, 5)!.Value, 9);
    }

    [Fact]
    public void Nudge_OntoTheEnd_GivesNull()
    {
        Assert.Null(new LimitScale(LimitKind.TrimStart, 0, 5).Nudge(0.05, -1));
        Assert.Null(new LimitScale(LimitKind.TrimEnd, 0, 5).Nudge(4.9, 3));
        Assert.Null(new LimitScale(LimitKind.XMin, -100, 100).Nudge(-98, -10));
        Assert.Null(new LimitScale(LimitKind.HeightMax, -2, 12).Nudge(11.5, 1));
        // Already at the end and stepping outward stays there.
        Assert.Null(new LimitScale(LimitKind.HeightMin, -2, 12).Nudge(null, -1));
    }
}
