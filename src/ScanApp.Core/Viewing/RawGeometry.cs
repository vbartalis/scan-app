using ScanApp.Core.Cleanup;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Viewing;

/// <summary>
/// Per raw pass, what the raw views test the limits against: each sample point's and each grid cell's height above the
/// table and its flags. Computed once per (table, mask) and reused for every limit change.
/// </summary>
/// <param name="PointHeight">Per point of the raw preview's sample: Z minus the table; NaN without a table.</param>
/// <param name="PointFlags">Per point: <see cref="Paused"/> and <see cref="Reflection"/>.</param>
/// <param name="CellHeight">Per cell of the raw preview's grid: the mean Z minus the table; NaN without a table or for an empty cell.</param>
/// <param name="CellFlags">Per cell: <see cref="Paused"/> (the row's first profile) and <see cref="Reflection"/> (a masked point in it).</param>
public sealed record RawGeometry(float[] PointHeight, byte[] PointFlags, float[] CellHeight, byte[] CellFlags)
{
    public const byte Paused = 1, Reflection = 2;

    /// <summary>
    /// <paramref name="rawPreview"/>: built with cleanup null. <paramref name="cleanup"/>: the saved one, with Table and
    /// Reflections set (either may be absent). <paramref name="reflectionCells"/>: from <see cref="ReflectionCells.Find"/>,
    /// or null for none. Paused comes from <c>analysis.Pauses</c>, unclipped by the trims.
    /// </summary>
    public static RawGeometry Build(PassPreview rawPreview, MotionAnalysis analysis, PassCleanup cleanup, bool[]? reflectionCells)
    {
        ArgumentNullException.ThrowIfNull(rawPreview);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(cleanup);
        var table = cleanup.Table;
        double[] mapTime = table is null ? [] : MapTimes(analysis, cleanup);
        ProfileRange[] pauses = [.. analysis.Pauses.OrderBy(r => r.Start)];

        var points = rawPreview.Points;
        var pointHeight = new float[points.Count];
        var pointFlags = new byte[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            int profile = points.Profile[i];
            pointHeight[i] = table is null || (uint)profile >= (uint)mapTime.Length
                ? float.NaN
                : (float)(points.Z[i] - table.At(points.X[i], mapTime[profile]));
            byte flags = 0;
            if (IsPaused(pauses, profile)) flags |= Paused;
            if (cleanup.Reflections.Contains(profile, points.Point[i])) flags |= Reflection;
            pointFlags[i] = flags;
        }

        var grid = rawPreview.Grid;
        var cellHeight = new float[grid.Z.Length];
        var cellFlags = new byte[grid.Z.Length];
        for (int row = 0; row < grid.Rows; row++)
        {
            int first = row * grid.ProfilesPerRow;
            byte rowFlags = IsPaused(pauses, first) ? Paused : (byte)0;
            bool timed = table is not null && first < mapTime.Length;
            for (int column = 0; column < grid.Columns; column++)
            {
                int i = row * grid.Columns + column;
                float z = grid.Z[i];
                cellHeight[i] = timed && !float.IsNaN(z)
                    ? (float)(z - table!.At(grid.ColumnToX(column), mapTime[first]))
                    : float.NaN;
                cellFlags[i] = reflectionCells is not null && reflectionCells[i] ? (byte)(rowFlags | Reflection) : rowFlags;
            }
        }
        return new RawGeometry(pointHeight, pointFlags, cellHeight, cellFlags);
    }

    /// <summary>
    /// The map time of every raw profile. Kept profiles get the time <see cref="CleanedPassWalk"/> gives them: moving
    /// time from the first kept profile, each pause taking only the step into it, <c>duration − t</c> for a reversed
    /// pass. A profile in a pause gets the time of the next kept profile; profiles before the first or after the last
    /// kept one are extended outward by raw time steps. Times come from <c>analysis.TimeS</c>.
    /// </summary>
    public static double[] MapTimes(MotionAnalysis analysis, PassCleanup cleanup)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(cleanup);
        var raw = analysis.TimeS;
        int n = raw.Length;
        var times = new double[n];
        if (n == 0) return times;
        int[] kept = CleanedPassWalk.Create(n, cleanup, applyZone: false).Kept;
        if (kept.Length == 0)
        {
            for (int i = 0; i < n; i++) times[i] = raw[i] - raw[0];
            return times;
        }

        for (int i = 0; i <= kept[0]; i++) times[i] = raw[i] - raw[kept[0]];
        for (int k = 1; k < kept.Length; k++)
        {
            // A gap (a pause) counts only the step into it, as the walk does; its profiles take the next kept one's time.
            int previous = kept[k - 1];
            double t = times[previous] + raw[previous + 1] - raw[previous];
            for (int i = previous + 1; i <= kept[k]; i++) times[i] = t;
        }
        int last = kept[^1];
        for (int i = last + 1; i < n; i++) times[i] = times[last] + raw[i] - raw[last];

        if (cleanup.Reverse)
        {
            double duration = times[last];
            for (int i = 0; i < n; i++) times[i] = duration - times[i];
        }
        return times;
    }

    private static bool IsPaused(ProfileRange[] pauses, int profile)
    {
        foreach (var r in pauses)
            if (profile >= r.Start && profile <= r.End) return true;
        return false;
    }
}

/// <summary>The cells of a raw preview grid that hold a point of a <see cref="ReflectionMask"/>.</summary>
public static class ReflectionCells
{
    /// <summary>
    /// Per cell of <paramref name="rawGrid"/> (built with cleanup null from <paramref name="reader"/>): true where a masked
    /// point lands, binned as the preview builder bins it. Reads only the masked profiles, with ReadAt.
    /// </summary>
    public static bool[] Find(PassReader reader, HeightGrid rawGrid, ReflectionMask mask)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(rawGrid);
        ArgumentNullException.ThrowIfNull(mask);
        var marked = new bool[rawGrid.Z.Length];
        if (mask.Count == 0) return marked;
        double xSpan = rawGrid.XMax - rawGrid.XMin;
        int profiles = Math.Min(reader.ProfileCount, rawGrid.Rows * rawGrid.ProfilesPerRow);
        for (int i = 0; i < profiles; i++)
        {
            if (!mask.TryGetPoints(i, out var points)) continue;
            var p = reader.ReadAt(i);
            int row = i / rawGrid.ProfilesPerRow;
            foreach (int j in points)
            {
                float x = p.X[j];
                if (float.IsNaN(x) || float.IsNaN(p.Z[j])) continue;
                int column = xSpan > 0 ? (int)Math.Clamp((x - rawGrid.XMin) / xSpan * rawGrid.Columns, 0, rawGrid.Columns - 1) : 0;
                marked[row * rawGrid.Columns + column] = true;
            }
        }
        return marked;
    }
}
