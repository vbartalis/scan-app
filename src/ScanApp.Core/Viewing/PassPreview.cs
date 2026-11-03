using ScanApp.Core.Cleanup;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Viewing;

/// <summary>A downsampled view of one pass: a height grid and a point sample. ZMin/ZMax are NaN when the pass has no valid points; the sample can be smaller than that suggests (a very sparse pass may sample none).</summary>
public sealed record PassPreview(HeightGrid Grid, PointSample Points, int ProfileCount, double DurationS,
                                 float ZMin, float ZMax)
{
    public bool IsEmpty => float.IsNaN(ZMin);
}

public static class PassPreviewBuilder
{
    /// <summary>
    /// Builds the preview with one sequential read of the pass, after reading about 300 profiles
    /// with ReadAt to find the X range. Points outside that range land in the edge columns.
    /// With a cleanup, only the kept profiles count (inside the trim, outside the pauses), time is moving time
    /// (pauses take none, the first kept profile is at 0) and a reversed pass runs backwards; null is the raw pass.
    /// </summary>
    public static PassPreview Build(PassReader reader, PreviewOptions options, PassCleanup? cleanup,
                                    IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int points = reader.PointsPerProfile;
        var walk = CleanedPassWalk.Create(reader.ProfileCount, cleanup);
        int profiles = walk.Kept.Length;
        bool reverse = cleanup?.Reverse ?? false;
        int columns = Math.Max(1, options.GridColumns);
        int ppr = Math.Max(1, (int)Math.Ceiling(profiles / (double)Math.Max(1, options.MaxGridRows)));
        int rows = (profiles + ppr - 1) / ppr;

        var (xMin, xMax) = walk.SampleXRange(reader, ct);
        double xSpan = xMax - xMin;

        int k = SampleStride(profiles, points, options.MaxPoints);

        var sum = new double[rows * columns];
        var count = new int[rows * columns];
        var rowStart = new double[rows];
        var sx = new List<float>();
        var sz = new List<float>();
        var st = new List<float>();
        var sp = new List<int>();
        var sj = new List<int>();
        float zMin = float.NaN, zMax = float.NaN;
        double duration = 0;       // moving time of the last kept profile
        int step = Math.Max(1, profiles / 100);

        // Times are moving times counted from the first kept profile; a reversed pass flips them at the end.
        foreach (var (p, ordinal, t) in walk.Walk(reader, ct))
        {
            duration = t;

            int shown = reverse ? profiles - 1 - ordinal : ordinal;
            int row = shown / ppr;
            if (shown % ppr == 0) rowStart[row] = t;
            bool sampled = shown % k == 0 || shown == profiles - 1;
            int rawIndex = walk.Kept[ordinal];

            for (int j = 0; j < points; j++)
            {
                float x = p.X[j], z = p.Z[j];
                if (float.IsNaN(x) || float.IsNaN(z)) continue;

                if (!(z >= zMin)) zMin = z;
                if (!(z <= zMax)) zMax = z;

                int col = xSpan > 0 ? (int)Math.Clamp((x - xMin) / xSpan * columns, 0, columns - 1) : 0;
                sum[row * columns + col] += z;
                count[row * columns + col]++;

                if (sampled && j % k == 0)
                {
                    sx.Add(x);
                    sz.Add(z);
                    st.Add((float)t);
                    sp.Add(rawIndex);
                    sj.Add(j);
                }
            }

            int done = ordinal + 1;
            if (done % step == 0 && done < profiles) progress?.Report(done / (double)profiles);
        }
        progress?.Report(1.0);

        if (reverse)
        {
            for (int i = 0; i < rowStart.Length; i++) rowStart[i] = duration - rowStart[i];
            for (int i = 0; i < st.Count; i++) st[i] = (float)(duration - st[i]);
        }

        var grid = new float[rows * columns];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = count[i] == 0 ? float.NaN : (float)(sum[i] / count[i]);

        return new PassPreview(
            new HeightGrid(columns, rows, xMin, xMax, ppr, rowStart, grid),
            new PointSample(sx.ToArray(), sz.ToArray(), st.ToArray(), sp.ToArray(), sj.ToArray()),
            profiles, duration, zMin, zMax);
    }

    internal static int SampleStride(int profiles, int points, int maxPoints)
    {
        if (profiles == 0 || points == 0) return 1;
        int k = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)profiles * points / Math.Max(1, maxPoints))));
        // Ceilings can overshoot the cap, and the last profile is always sampled in addition.
        while (k < Math.Max(profiles, points)
               && (long)((profiles + k - 1) / k + 1) * ((points + k - 1) / k) > maxPoints)
            k++;
        return k;
    }
}
