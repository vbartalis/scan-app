using System.Numerics;
using ScanApp.App.Views;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Viewing;

namespace ScanApp.App.Tests;

public class PointCloudDataTests
{
    // Four raw sample points: X -10..30, Z 1..4, profiles 0, 3, 6, 9 at 0.1 s per profile.
    private static PassPreview RawPreview()
    {
        var points = new PointSample([-10, 0, 10, 30], [1, 2, 3, 4], [0f, 0.3f, 0.6f, 0.9f], [0, 3, 6, 9], [5, 6, 7, 8]);
        var grid = new HeightGrid(1, 1, -10, 30, 10, [0], [2]);
        return new PassPreview(grid, points, 10, 0.9, 1, 4);
    }

    private static MotionAnalysis Analysis() =>
        new(new float[10], [.. Enumerable.Range(0, 10).Select(i => i * 0.1)], 0, 0, false, new ProfileRange(0, 9), true, [], []);

    private static TablePlane Table(double a) => new(a, 0, 0, 0, 0, 1, []);

    private static readonly double Inf = double.PositiveInfinity;

    [Fact]
    public void BuildRawAttributes_CopiesProfilesHeightsFlags()
    {
        var geometry = new RawGeometry([0.5f, float.NaN, 2f, -1f], [0, RawGeometry.Paused, RawGeometry.Reflection, 3], [], []);

        var a = PointCloudData.BuildRawAttributes(RawPreview(), geometry);

        Assert.Equal([0f, 3f, 6f, 9f], a.Profile);
        Assert.Equal([0.5f, float.NaN, 2f, -1f], a.HeightAboveTable);
        Assert.Equal([0, 1, 2, 3], a.Flags);
    }

    [Fact]
    public void BuildCut_InfiniteLimitsUseBounds()
    {
        var limits = new CutLimits(0, 9, -Inf, Inf, -Inf, Inf, true);

        var (cut, box) = PointCloudData.BuildCut(limits, false, Analysis(), RawPreview(), null, 100, false);

        Assert.Equal(-float.MaxValue, cut.XMin);
        Assert.Equal(float.MaxValue, cut.XMax);
        Assert.Equal(-float.MaxValue, cut.HeightMin);
        Assert.Equal(float.MaxValue, cut.HeightMax);
        Assert.True(cut.FilterReflections);
        Assert.False(cut.ShowReflections);
        Assert.Equal(new Vector3(-10, 0, 1), box.Min);
        Assert.Equal(new Vector3(30, 90, 4), box.Max, new Vector3Comparer(1e-4f));
    }

    [Fact]
    public void BuildCut_TrimUsesRawTimesTimesSpeed()
    {
        var limits = new CutLimits(2, 7, -5, 20, 0.5, 2.5, false);

        var (cut, box) = PointCloudData.BuildCut(limits, true, Analysis(), RawPreview(), Table(10), 50, false);

        Assert.Equal((2f, 7f, -5f, 20f, 0.5f, 2.5f), (cut.TrimStart, cut.TrimEnd, cut.XMin, cut.XMax, cut.HeightMin, cut.HeightMax));
        Assert.True(cut.ShowReflections);
        Assert.Equal(new Vector3(-5, 10, 10.5f), box.Min, new Vector3Comparer(1e-4f));
        Assert.Equal(new Vector3(20, 35, 12.5f), box.Max, new Vector3Comparer(1e-4f));
    }

    [Fact]
    public void BuildCut_FlipNegatesZ()
    {
        var limits = new CutLimits(0, 9, -Inf, Inf, 0.5, Inf, true);

        var (cut, box) = PointCloudData.BuildCut(limits, false, Analysis(), RawPreview(), Table(0), 100, true);

        // The test stays in heights above the table; only the drawn box is negated (and so reordered).
        // No max height: the cloud's top Z (4) closes the box.
        Assert.Equal(0.5f, cut.HeightMin);
        Assert.Equal(-4f, box.Min.Z, 4);
        Assert.Equal(-0.5f, box.Max.Z, 4);
        Assert.Equal((-10f, 30f), (box.Min.X, box.Max.X));
    }

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
