using ScanApp.Core.Cleanup;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Combine;

public static class AlignmentMapBuilder
{
    private const double ColumnMm = 1.0;
    private const double RowS = 0.01;
    internal const float ObjectMm = 3f;

    /// <summary>
    /// Builds the map with one sequential read of the cleaned pass: kept profiles only, in moving time (pauses take
    /// none), reversed for a camera-back pass. A cell is the mean Z of the valid points in it. A plane in X and time
    /// is fitted to the table from its low end, then each row's median table residual is subtracted, which removes
    /// scanner shake. Table cells end at exactly 0, object cells (above 3 mm) keep their height, empty cells stay NaN.
    /// <see cref="AlignmentMap.Table"/> keeps that plane and the row offsets. The table is fitted on every kept point,
    /// ignoring the cleanup's zone; then, judged at the column centre, cells outside the zone's X range or above its
    /// upper height limit become NaN, and cells below its lower limit become table (0).
    /// </summary>
    public static AlignmentMap Build(PassReader reader, PassCleanup cleanup, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var walk = CleanedPassWalk.Create(reader.ProfileCount, cleanup, applyZone: false);
        if (walk.Kept.Length == 0)
        {
            progress?.Report(1.0);
            return new AlignmentMap(1, 0, 0, ColumnMm, RowS, [], []);
        }

        var (xMin, xMax) = walk.SampleXRange(reader, ct);
        int columns = Math.Max(1, (int)Math.Ceiling((xMax - xMin) / ColumnMm - 1e-9));
        double duration = walk.Duration(reader);
        int rows = (int)Math.Floor(duration / RowS + 1e-6) + 1;

        var sum = new double[rows * columns];
        var count = new int[rows * columns];
        int step = Math.Max(1, walk.Kept.Length / 100);

        foreach (var (p, ordinal, moving) in walk.Walk(reader, ct))
        {
            double t = moving;
            if (cleanup.Reverse) t = duration - t;
            int row = Math.Clamp((int)Math.Floor(t / RowS + 1e-6), 0, rows - 1);

            for (int j = 0; j < p.X.Length; j++)
            {
                float x = p.X[j], z = p.Z[j];
                if (float.IsNaN(x) || float.IsNaN(z)) continue;
                int col = (int)Math.Clamp((x - xMin) / ColumnMm, 0, columns - 1);
                sum[row * columns + col] += z;
                count[row * columns + col]++;
            }

            int done = ordinal + 1;
            if (done % step == 0 && done < walk.Kept.Length) progress?.Report(done / (double)walk.Kept.Length);
        }

        var height = new float[rows * columns];
        for (int i = 0; i < height.Length; i++)
            height[i] = count[i] == 0 ? float.NaN : (float)(sum[i] / count[i]);

        var (isObject, table) = RemoveTable(height, columns, rows, xMin, duration, cleanup.Zone);
        progress?.Report(1.0);
        return new AlignmentMap(columns, rows, xMin, ColumnMm, RowS, height, isObject) { Table = table };
    }

    /// <summary>
    /// Fits the table (<see cref="TableFit.Fit"/>), subtracts its plane and each row's offset, and zeroes the table
    /// cells in place: object cells (above 3 mm) keep their height, empty cells stay NaN. Then applies the zone: outside
    /// its X range or above its band → NaN, below its band → table (0). Without a table only the X range applies.
    /// </summary>
    private static (bool[] IsObject, TablePlane? Table) RemoveTable(float[] cells, int columns, int rows, double xMin, double duration,
                                                                    PassZone zone)
    {
        var isObject = new bool[cells.Length];
        var table = TableFit.Fit(cells, columns, rows, xMin, ColumnMm, RowS, duration);
        if (table is null)
        {
            for (int i = 0; i < cells.Length; i++)
                if (!zone.InX(xMin + (i % columns + 0.5) * ColumnMm)) cells[i] = float.NaN;
            return (isObject, null);
        }
        // The plane residual is rounded to float before the row offset comes off, as TableFit derives the offsets.
        for (int r = 0; r < rows; r++)
        {
            double t = (r + 0.5) * RowS - table.TMid;
            for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c;
                if (float.IsNaN(cells[i])) continue;
                double centre = xMin + (c + 0.5) * ColumnMm, x = centre - table.XMid;
                float h = (float)(cells[i] - (table.A + table.B * x + table.C * t)) - table.RowOffsets[r];
                if (!zone.InX(centre) || h > zone.HeightMax) { cells[i] = float.NaN; continue; }
                isObject[i] = h > ObjectMm && !(h < zone.HeightMin);
                cells[i] = isObject[i] ? h : 0f;
            }
        }
        return (isObject, table);
    }
}
