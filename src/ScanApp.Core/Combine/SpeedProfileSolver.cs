using static ScanApp.Core.Combine.PairScorer;

namespace ScanApp.Core.Combine;

/// <summary>
/// A pass's travel over time (its speed profile), found by dynamic programming against another pass's surface with
/// the rigid pose held fixed. Frames and the pose are as in <see cref="PairAlignment"/>, with the moving pass's s·t
/// replaced by its travel.
/// </summary>
public static class SpeedProfileSolver
{
    /// <summary>Spacing of the solved travel samples (nodes).</summary>
    public const double NodeS = 0.05;
    /// <summary>Travel positions are searched on this lattice.</summary>
    public const double LatticeMm = 0.25;
    public const double MaxSpeed = 75;
    /// <summary>Cost per lattice unit that the travel step changes by from one node to the next.</summary>
    private const double SpeedChangeCost = 0.05;
    /// <summary>A moving sample outside the reference map is missing data too, at half the cap.</summary>
    private const double OutsideCost = PairAligner.CapMm / 2;
    private const int MarginColumns = 10;
    private const double SpanMarginS = 0.2;

    /// <summary>
    /// Per-row travel increments by dynamic programming so <paramref name="moving"/> agrees with
    /// <paramref name="reference"/> at the given pose; missing data costs.
    /// <list type="bullet">
    /// <item>Nodes every ~50 ms of the moving pass, from 0 to its map's duration; each node's cells are the mean of
    /// its map rows, over the object's columns ± 10 mm.</item>
    /// <item>A node at travel y is scored like <see cref="PairAligner.Score"/>: each cell (x, y) goes through the pose
    /// into <paramref name="reference"/>, read at time y_ref / ReferenceSpeed; the cost is min(|Δh|, 2 mm). An object
    /// cell on missing data costs the cap and a table cell there is skipped, so the pass cannot hide in a shadow; a
    /// cell outside the reference map costs half the cap.</item>
    /// <item>Travel increases on a 0.25 mm lattice, at least 5 and at most 75 mm/s, and pays 0.05 per lattice unit of speed change
    /// between nodes. The search spans <c>MovingSpeed</c>·t widened by 40 mm plus 15 %.</item>
    /// <item>Nodes more than 0.2 s outside the moving pass's object carry no information; they continue at the object
    /// span's mean speed. A pass with no object gets <c>MovingSpeed</c>·t.</item>
    /// </list>
    /// </summary>
    public static SpeedProfile Solve(AlignmentMap reference, AlignmentMap moving, PairAlignment pose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (moving.Rows == 0) throw new ArgumentException("The moving map has no rows.", nameof(moving));
        double duration = moving.DurationS;
        int nodes = Math.Max(2, (int)Math.Ceiling(duration / NodeS - 1e-9) + 1);
        double dt = duration / (nodes - 1);
        var times = new double[nodes];
        for (int b = 0; b < nodes; b++) times[b] = b * dt;
        SpeedProfile Constant() => new(times, [.. times.Select(t => pose.MovingSpeed * t)]);

        int cMin = int.MaxValue, cMax = -1, rMin = int.MaxValue, rMax = -1;
        for (int r = 0; r < moving.Rows; r++)
            for (int c = 0; c < moving.Columns; c++)
                if (moving.IsObject[r * moving.Columns + c])
                    (cMin, cMax, rMin, rMax) = (Math.Min(cMin, c), Math.Max(cMax, c), Math.Min(rMin, r), Math.Max(rMax, r));
        if (cMax < 0) return Constant();
        int b0 = Math.Max(0, (int)Math.Ceiling((TOf(moving, rMin) - SpanMarginS) / dt - 1e-9));
        int b1 = Math.Min(nodes - 1, (int)Math.Floor((TOf(moving, rMax) + SpanMarginS) / dt + 1e-9));
        if (b1 - b0 < 2) return Constant();

        (cMin, cMax) = (Math.Max(0, cMin - MarginColumns), Math.Min(moving.Columns - 1, cMax + MarginColumns));
        var blocks = Blocks(moving, times, dt, cMin, cMax);
        var cost = Costs(reference, moving, pose, blocks, cMin, b0, b1, out double y0, out int positions, ct);
        int minStep = Math.Max(1, (int)Math.Ceiling(PairAligner.MinSpeed * dt / LatticeMm - 1e-9));
        var path = Path(cost, b1 - b0 + 1, positions, minStep, (int)Math.Ceiling(MaxSpeed * dt / LatticeMm), ct);

        var travel = new double[nodes];
        for (int b = b0; b <= b1; b++) travel[b] = y0 + path[b - b0] * LatticeMm;
        double speed = (travel[b1] - travel[b0]) / (times[b1] - times[b0]);
        for (int b = 0; b < b0; b++) travel[b] = travel[b0] - (times[b0] - times[b]) * speed;
        for (int b = b1 + 1; b < nodes; b++) travel[b] = travel[b1] + (times[b] - times[b1]) * speed;
        return new SpeedProfile(times, travel);
    }

    /// <summary>Per node, the mean height of each column (cMin..cMax) over the map rows nearest that node; NaN when none is valid.</summary>
    private static float[][] Blocks(AlignmentMap m, double[] times, double dt, int cMin, int cMax)
    {
        int width = cMax - cMin + 1;
        var sum = new double[times.Length * width];
        var count = new int[times.Length * width];
        for (int r = 0; r < m.Rows; r++)
        {
            int b = Math.Clamp((int)Math.Round(TOf(m, r) / dt), 0, times.Length - 1);
            for (int c = cMin; c <= cMax; c++)
            {
                float h = m.At(c, r);
                if (float.IsNaN(h)) continue;
                sum[b * width + c - cMin] += h;
                count[b * width + c - cMin]++;
            }
        }
        var blocks = new float[times.Length][];
        for (int b = 0; b < times.Length; b++)
        {
            blocks[b] = new float[width];
            for (int i = 0; i < width; i++)
                blocks[b][i] = count[b * width + i] == 0 ? float.NaN : (float)(sum[b * width + i] / count[b * width + i]);
        }
        return blocks;
    }

    /// <summary>The cost of each node b0..b1 at each lattice position, row-major [node][position].</summary>
    private static float[] Costs(AlignmentMap reference, AlignmentMap moving, PairAlignment pose, float[][] blocks, int cMin,
                                 int b0, int b1, out double y0, out int positions, CancellationToken ct)
    {
        double span = pose.MovingSpeed * moving.DurationS, margin = 40 + 0.15 * span;
        double start = -margin;
        int nj = (int)Math.Ceiling((span + 2 * margin) / LatticeMm) + 1;
        double c = Math.Cos(pose.ThetaRad), s = Math.Sin(pose.ThetaRad);
        int width = blocks[0].Length;
        var cost = new float[(b1 - b0 + 1) * nj];
        Parallel.For(b0, b1 + 1, new ParallelOptions { CancellationToken = ct }, b =>
        {
            var block = blocks[b];
            for (int j = 0; j < nj; j++)
            {
                double y = start + j * LatticeMm, sumCost = 0;
                int counted = 0;
                for (int i = 0; i < width; i++)
                {
                    float hm = block[i];
                    if (float.IsNaN(hm)) continue;
                    double x = XOf(moving, cMin + i);
                    double qx = c * x - s * y + pose.Tx, qy = s * x + c * y + pose.Ty;
                    if (!TryLookup(reference, qx, qy / pose.ReferenceSpeed, out double h))
                    {
                        sumCost += OutsideCost;
                    }
                    else if (double.IsNaN(h))
                    {
                        if (hm <= AlignmentMapBuilder.ObjectMm) continue;
                        sumCost += PairAligner.CapMm;
                    }
                    else
                    {
                        sumCost += Math.Min(PairAligner.CapMm, Math.Abs(hm - h));
                    }
                    counted++;
                }
                cost[(b - b0) * nj + j] = counted == 0 ? (float)OutsideCost : (float)(sumCost / counted);
            }
        });
        y0 = start;
        positions = nj;
        return cost;
    }

    /// <summary>
    /// The cheapest increasing lattice path through the nodes: state (position, last step of minStep..maxStep units),
    /// each node adding its cost and 0.05 per unit of step change. Free start and end. The minimum step keeps the speed
    /// at 5 mm/s or more, so a pass with nothing to match cannot collapse to standing still.
    /// </summary>
    private static int[] Path(float[] cost, int nodes, int nj, int minStep, int maxStep, CancellationToken ct)
    {
        int ns = maxStep + 1;
        var d = new float[nj * ns];
        var next = new float[nj * ns];
        var back = new byte[(long)nodes * nj * ns];
        for (int j = 0; j < nj; j++)
            for (int k = 0; k < ns; k++) d[j * ns + k] = k < minStep ? float.PositiveInfinity : cost[j];
        var change = new float[ns * ns];
        for (int k = 0; k < ns; k++)
            for (int kp = 0; kp < ns; kp++) change[k * ns + kp] = (float)(SpeedChangeCost * Math.Abs(k - kp));

        for (int b = 1; b < nodes; b++)
        {
            ct.ThrowIfCancellationRequested();
            var (from, to) = (d, next);
            long row = (long)b * nj * ns;
            Parallel.For(0, nj, j =>
            {
                float here = cost[b * nj + j];
                for (int k = 0; k < ns; k++)
                {
                    int jp = j - k;
                    float best = float.PositiveInfinity;
                    byte bestK = 0;
                    if (jp >= 0 && k >= minStep)
                        for (int kp = 0; kp < ns; kp++)
                        {
                            float v = from[jp * ns + kp] + change[k * ns + kp];
                            if (v < best) (best, bestK) = (v, (byte)kp);
                        }
                    to[j * ns + k] = best + here;
                    back[row + j * ns + k] = bestK;
                }
            });
            (d, next) = (next, d);
        }

        int endJ = 0, endK = 0;
        for (int j = 0; j < nj; j++)
            for (int k = 0; k < ns; k++)
                if (d[j * ns + k] < d[endJ * ns + endK]) (endJ, endK) = (j, k);
        var path = new int[nodes];
        path[nodes - 1] = endJ;
        for (int b = nodes - 1, k = endK; b > 0; b--)
        {
            int kp = back[(long)b * nj * ns + path[b] * ns + k];
            path[b - 1] = path[b] - k;
            k = kp;
        }
        return path;
    }
}
