using System.Numerics;
using System.Runtime.CompilerServices;
using ScanApp.Core.Combine;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Viewing;
using ScanApp.Rendering;

namespace ScanApp.App.Views;

/// <summary>Turns a preview's point sample into the arrays the 3D view takes.</summary>
public static class PointCloudData
{
    /// <summary>Each point sample's X and Z bounds, scanned once: <see cref="BuildCut"/> runs on every drag tick.</summary>
    private static readonly ConditionalWeakTable<PointSample, StrongBox<(float XLo, float XHi, float ZLo, float ZHi)>> Bounds = [];

    /// <summary>
    /// The raw 3D view's per-point attributes, in the order of <see cref="Build"/>'s points: the raw profile index, the
    /// height above the table and the flags (the same bits in Core and Rendering: 1 paused, 2 reflection).
    /// </summary>
    public static RawPointAttributes BuildRawAttributes(PassPreview rawPreview, RawGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(rawPreview);
        ArgumentNullException.ThrowIfNull(geometry);
        var profile = rawPreview.Points.Profile;
        var p = new float[profile.Length];
        for (int i = 0; i < p.Length; i++) p[i] = profile[i];
        return new RawPointAttributes(p, (float[])geometry.PointHeight.Clone(), (byte[])geometry.PointFlags.Clone());
    }

    /// <summary>
    /// The cut's uniforms (no limit is ±<see cref="float.MaxValue"/>) and the kept region's box in world coordinates:
    /// X from the limits, or the cloud's X bounds where a limit is infinite; y from the raw times of the trims times the
    /// speed; z the table's <see cref="TablePlane.A"/> plus the band, or the cloud's Z bounds where a band end is not set
    /// or there is no table, negated when flipped. The shader tests the heights above the table, unflipped.
    /// The box is axis-aligned: its z uses only the table's offset <see cref="TablePlane.A"/> (the plane at its centre),
    /// not its X or time slope or row offsets, so on a sloped table its faces drift from the grey boundary towards the
    /// box's ends. It is a guide; what is kept is the per-point test.
    /// </summary>
    public static (RawCutUniforms Cut, CutBox Box) BuildCut(CutLimits limits, bool showReflections, MotionAnalysis analysis,
                                                           PassPreview rawPreview, TablePlane? table, double speedMmPerS, bool flip)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(rawPreview);
        var cut = new RawCutUniforms(limits.TrimStart, limits.TrimEnd, Finite(limits.XMin), Finite(limits.XMax),
                                     Finite(limits.HeightMin), Finite(limits.HeightMax), limits.FilterReflections, showReflections);

        var (xLo, xHi, zLo, zHi) = Bounds.GetValue(rawPreview.Points, static points => new(BoundsOf(points))).Value;
        var times = analysis.TimeS;
        double TravelAt(int profile) => times.Length == 0 ? 0 : times[Math.Clamp(profile, 0, times.Length - 1)] * speedMmPerS;

        double xMin = double.IsInfinity(limits.XMin) ? xLo : limits.XMin;
        double xMax = double.IsInfinity(limits.XMax) ? xHi : limits.XMax;
        // Only the plane's centre offset: on a sloped table these faces drift from the cut points (see the summary).
        double zMin = table is not null && !double.IsInfinity(limits.HeightMin) ? table.A + limits.HeightMin : zLo;
        double zMax = table is not null && !double.IsInfinity(limits.HeightMax) ? table.A + limits.HeightMax : zHi;
        if (flip) (zMin, zMax) = (-zMax, -zMin);
        var box = new CutBox(new Vector3((float)xMin, (float)TravelAt(limits.TrimStart), (float)zMin),
                             new Vector3((float)xMax, (float)TravelAt(limits.TrimEnd), (float)zMax));
        return (cut, box);
    }

    /// <summary>The sample's X and Z bounds, all 0 when it is empty. They depend on neither the speed nor the flip.</summary>
    private static (float XLo, float XHi, float ZLo, float ZHi) BoundsOf(PointSample points)
    {
        if (points.Count == 0) return (0, 0, 0, 0);
        var (xLo, xHi, zLo, zHi) = (float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);
        for (int i = 0; i < points.Count; i++)
        {
            xLo = Math.Min(xLo, points.X[i]);
            xHi = Math.Max(xHi, points.X[i]);
            zLo = Math.Min(zLo, points.Z[i]);
            zHi = Math.Max(zHi, points.Z[i]);
        }
        return (xLo, xHi, zLo, zHi);
    }

    private static float Finite(double v) => (float)Math.Clamp(v, -float.MaxValue, float.MaxValue);

    /// <summary>
    /// World is z-up: x = X mm, y = travel mm (TimeS x speed), z = Z mm, negated when flipped.
    /// The colour value stays the original Z, so the renderer's own flip only swaps the ramp.
    /// </summary>
    public static (float[] Xyz, float[] Value) Build(PassPreview preview, double speedMmPerS, bool flip)
    {
        var points = preview.Points;
        var xyz = new float[points.Count * 3];
        float sign = flip ? -1f : 1f;
        for (int i = 0; i < points.Count; i++)
        {
            xyz[i * 3] = points.X[i];
            xyz[i * 3 + 1] = (float)(points.TimeS[i] * speedMmPerS);
            xyz[i * 3 + 2] = sign * points.Z[i];
        }
        return (xyz, points.Z);
    }

    /// <summary>
    /// A merged result as renderer input: x = X, y = Y, z = Z (negated when flipped). The value is the height Z in
    /// height mode (so the renderer's flip only swaps the ramp) or the pass index in pass mode. The range is Z's.
    /// </summary>
    public static (float[] Xyz, float[] Value, float ValueMin, float ValueMax) BuildMerged(MergedCloud cloud, bool byPass, bool flip)
    {
        int n = cloud.X.Length;
        var xyz = new float[n * 3];
        var value = new float[n];
        float sign = flip ? -1f : 1f;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            xyz[i * 3] = cloud.X[i];
            xyz[i * 3 + 1] = cloud.Y[i];
            xyz[i * 3 + 2] = sign * cloud.Z[i];
            value[i] = byPass ? cloud.PassIndex[i] : cloud.Z[i];
            min = Math.Min(min, cloud.Z[i]);
            max = Math.Max(max, cloud.Z[i]);
        }
        return n == 0 ? (xyz, value, 0f, 1f) : (xyz, value, min, max);
    }
}
