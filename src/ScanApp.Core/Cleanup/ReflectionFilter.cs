using ScanApp.Core.Storage;

namespace ScanApp.Core.Cleanup;

/// <summary>The points of one pass that <see cref="ReflectionFilter"/> removed, by raw profile index and point index.</summary>
public sealed class ReflectionMask
{
    /// <summary>Removes nothing: the mask of a pass without reflectance.</summary>
    public static readonly ReflectionMask None = new(new Dictionary<int, int[]>());

    private readonly Dictionary<int, int[]> _points;

    /// <param name="points">Per raw profile, its removed point indices, ascending.</param>
    private ReflectionMask(Dictionary<int, int[]> points)
    {
        _points = points;
        Count = points.Values.Sum(p => p.Length);
    }

    /// <summary>The number of removed points.</summary>
    public int Count { get; }

    public bool Contains(int rawProfile, int point) =>
        _points.TryGetValue(rawProfile, out var points) && Array.BinarySearch(points, point) >= 0;

    /// <summary>The removed point indices of a raw profile, ascending; false when it has none.</summary>
    internal bool TryGetPoints(int rawProfile, out int[] points) => _points.TryGetValue(rawProfile, out points!);

    internal static ReflectionMask From(Dictionary<int, int[]> points) => points.Count == 0 ? None : new ReflectionMask(points);
}

/// <summary>
/// Finds reflection spikes: dim points well above their neighbourhood, removed as connected blobs.
/// Only passes that recorded reflectance are filtered.
/// <list type="bullet">
/// <item>Cells are 1 mm of X by 10 ms of map time, as in <see cref="Combine.AlignmentMapBuilder"/>, over the cleaned
/// pass; a cell's value is the median Z of its points.</item>
/// <item>A cell's local height is the median of the cell values within ±<see cref="NeighbourhoodMm"/>/2 in X and
/// ±<see cref="NeighbourhoodMm"/>/2 of travel at <see cref="AssumedSpeedMmPerS"/> (3 columns by 15 rows).</item>
/// <item>A point deviates when it stands more than <see cref="DeviationMm"/> above its cell's local height; a seed is a
/// deviating point with reflectance below <see cref="MaxReflectance"/>.</item>
/// <item>A blob is a set of cells holding deviating points, connected through their 8 neighbours, grown only from
/// cells holding a seed. Every deviating point in a blob is removed, so a dim spike's brighter fringe goes with it,
/// while a bright edge with no dim point beside it stays.</item>
/// </list>
/// </summary>
public static class ReflectionFilter
{
    /// <summary>A point more than this above its neighbourhood deviates.</summary>
    public const double DeviationMm = 2;
    /// <summary>The neighbourhood's size in X and in travel.</summary>
    public const double NeighbourhoodMm = 3;
    /// <summary>The travel speed that turns the neighbourhood's travel into map time (the preview's assumption).</summary>
    public const double AssumedSpeedMmPerS = 20;
    /// <summary>A seed is a deviating point with reflectance below this.</summary>
    public const int MaxReflectance = 50;

    private const double ColumnMm = 1.0;
    private const double RowS = 0.01;

    /// <summary>
    /// None when the pass has no reflectance. Reads the cleaned pass (trims, pauses; not the zone) twice: once for
    /// the cell medians, once to judge each point; progress covers both reads.
    /// </summary>
    public static ReflectionMask Find(PassReader reader, PassCleanup cleanup, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(cleanup);
        ct.ThrowIfCancellationRequested();
        var walk = CleanedPassWalk.Create(reader.ProfileCount, cleanup with { Reflections = ReflectionMask.None }, applyZone: false);
        if (!reader.HasReflectance || walk.Kept.Length == 0)
        {
            progress?.Report(1.0);
            return ReflectionMask.None;
        }

        var (xMin, xMax) = walk.SampleXRange(reader, ct);
        int columns = Math.Max(1, (int)Math.Ceiling((xMax - xMin) / ColumnMm - 1e-9));
        var grid = new Grid(xMin, columns, walk.Duration(reader), cleanup.Reverse);
        var cells = CellMedians(reader, walk, grid, Within(progress, 0, 0.5), ct);
        var local = LocalMedians(cells, grid, ct);
        var mask = Judge(reader, walk, grid, local, Within(progress, 0.5, 1), ct);
        progress?.Report(1.0);
        return mask;
    }

    /// <summary>The map grid of the cleaned pass: rows of 10 ms of map time (reversed for a camera-back pass).</summary>
    private sealed record Grid(double XMin, int Columns, double Duration, bool Reverse)
    {
        public int Rows { get; } = (int)Math.Floor(Duration / RowS + 1e-6) + 1;

        public int Row(double moving)
        {
            double t = Reverse ? Duration - moving : moving;
            return Math.Clamp((int)Math.Floor(t / RowS + 1e-6), 0, Rows - 1);
        }

        public int Column(float x) => (int)Math.Clamp((x - XMin) / ColumnMm, 0, Columns - 1);
    }

    /// <summary>
    /// The median Z of each cell, NaN where empty. Map time runs monotonically along the walk, so the points are
    /// gathered one row at a time.
    /// </summary>
    private static float[] CellMedians(PassReader reader, CleanedPassWalk walk, Grid grid, IProgress<double>? progress,
                                       CancellationToken ct)
    {
        var cells = new float[grid.Rows * grid.Columns];
        Array.Fill(cells, float.NaN);
        var buffers = new List<float>[grid.Columns];
        for (int c = 0; c < buffers.Length; c++) buffers[c] = [];
        int current = -1, step = Math.Max(1, walk.Kept.Length / 100);

        void Flush()
        {
            for (int c = 0; c < buffers.Length; c++)
            {
                var b = buffers[c];
                if (b.Count == 0) continue;
                b.Sort();
                cells[current * grid.Columns + c] = b[b.Count / 2];
                b.Clear();
            }
        }

        foreach (var (p, ordinal, moving) in walk.Walk(reader, ct))
        {
            int row = grid.Row(moving);
            if (row != current)
            {
                if (current >= 0) Flush();
                current = row;
            }
            for (int j = 0; j < p.X.Length; j++)
            {
                float x = p.X[j], z = p.Z[j];
                if (float.IsNaN(x) || float.IsNaN(z)) continue;
                buffers[grid.Column(x)].Add(z);
            }
            int done = ordinal + 1;
            if (done % step == 0) progress?.Report(done / (double)walk.Kept.Length);
        }
        if (current >= 0) Flush();
        return cells;
    }

    /// <summary>Each cell's neighbourhood median of the cell values; NaN where the cell is empty.</summary>
    private static float[] LocalMedians(float[] cells, Grid grid, CancellationToken ct)
    {
        double half = NeighbourhoodMm / 2;
        int halfColumns = (int)Math.Floor(half / ColumnMm + 1e-9);
        int halfRows = (int)Math.Floor(half / (AssumedSpeedMmPerS * RowS) + 1e-9);
        var local = new float[cells.Length];
        var window = new float[(2 * halfColumns + 1) * (2 * halfRows + 1)];
        for (int r = 0; r < grid.Rows; r++)
        {
            ct.ThrowIfCancellationRequested();
            for (int c = 0; c < grid.Columns; c++)
            {
                int i = r * grid.Columns + c;
                if (float.IsNaN(cells[i]))
                {
                    local[i] = float.NaN;
                    continue;
                }
                int n = 0;
                for (int rr = Math.Max(0, r - halfRows); rr <= Math.Min(grid.Rows - 1, r + halfRows); rr++)
                    for (int cc = Math.Max(0, c - halfColumns); cc <= Math.Min(grid.Columns - 1, c + halfColumns); cc++)
                    {
                        float v = cells[rr * grid.Columns + cc];
                        if (!float.IsNaN(v)) window[n++] = v;
                    }
                Array.Sort(window, 0, n);
                local[i] = window[n / 2];
            }
        }
        return local;
    }

    /// <summary>Finds the deviating points and the seed cells, grows the blobs, and masks the deviating points in them.</summary>
    private static ReflectionMask Judge(PassReader reader, CleanedPassWalk walk, Grid grid, float[] local,
                                        IProgress<double>? progress, CancellationToken ct)
    {
        var deviantRaw = new List<int>();
        var deviantPoint = new List<int>();
        var deviantCell = new List<int>();
        var hasDeviant = new bool[local.Length];
        var hasSeed = new bool[local.Length];
        int step = Math.Max(1, walk.Kept.Length / 100);

        foreach (var (p, ordinal, moving) in walk.Walk(reader, ct))
        {
            int row = grid.Row(moving), raw = walk.Kept[ordinal];
            var reflectance = p.Reflectance!;
            for (int j = 0; j < p.X.Length; j++)
            {
                float x = p.X[j], z = p.Z[j];
                if (float.IsNaN(x) || float.IsNaN(z)) continue;
                int cell = row * grid.Columns + grid.Column(x);
                if (!(z - local[cell] > DeviationMm)) continue;
                deviantRaw.Add(raw);
                deviantPoint.Add(j);
                deviantCell.Add(cell);
                hasDeviant[cell] = true;
                if (reflectance[j] < MaxReflectance) hasSeed[cell] = true;
            }
            int done = ordinal + 1;
            if (done % step == 0) progress?.Report(done / (double)walk.Kept.Length);
        }

        var inBlob = Blobs(hasDeviant, hasSeed, grid, ct);
        var removed = new Dictionary<int, List<int>>();
        for (int k = 0; k < deviantRaw.Count; k++)
        {
            if (!inBlob[deviantCell[k]]) continue;
            if (!removed.TryGetValue(deviantRaw[k], out var points)) removed[deviantRaw[k]] = points = [];
            points.Add(deviantPoint[k]);   // ascending: a profile's points are judged in order
        }
        return ReflectionMask.From(removed.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }

    /// <summary>The cells holding deviating points that connect (8 neighbours) to a cell holding a seed.</summary>
    private static bool[] Blobs(bool[] hasDeviant, bool[] hasSeed, Grid grid, CancellationToken ct)
    {
        var inBlob = new bool[hasDeviant.Length];
        var stack = new Stack<int>();
        for (int start = 0; start < hasSeed.Length; start++)
        {
            if (!hasSeed[start] || inBlob[start]) continue;
            ct.ThrowIfCancellationRequested();
            inBlob[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int cell = stack.Pop(), r = cell / grid.Columns, c = cell % grid.Columns;
                for (int rr = Math.Max(0, r - 1); rr <= Math.Min(grid.Rows - 1, r + 1); rr++)
                    for (int cc = Math.Max(0, c - 1); cc <= Math.Min(grid.Columns - 1, c + 1); cc++)
                    {
                        int next = rr * grid.Columns + cc;
                        if (!hasDeviant[next] || inBlob[next]) continue;
                        inBlob[next] = true;
                        stack.Push(next);
                    }
            }
        }
        return inBlob;
    }

    private static IProgress<double>? Within(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Relay(f => progress.Report(from + (to - from) * f));

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
