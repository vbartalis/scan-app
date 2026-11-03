namespace ScanApp.Core.Combine;

/// <summary>
/// <see cref="PairAligner.Score"/> for one pair of maps, with the samples built once, so a pose refit or a solver can
/// score many poses cheaply. Frames and the pose are as in <see cref="PairAlignment"/>.
/// </summary>
public sealed class PairScorer
{
    internal const int FineSamples = 25000;
    private const double MarginMm = 10, MarginS = 0.3;
    private const double MinObjectOverlap = 0.5;

    private readonly AlignmentMap _reference, _moving;
    private PairAligner.EdgeCells? _referenceEdges, _movingEdges;

    public PairScorer(AlignmentMap reference, AlignmentMap moving) : this(reference, moving, FineSamples) { }

    internal PairScorer(AlignmentMap reference, AlignmentMap moving, int maxSamples)
    {
        _reference = reference;
        _moving = moving;
        Reference = ScoreSamples.From(reference, maxSamples);
        Moving = ScoreSamples.From(moving, maxSamples);
    }

    internal ScoreSamples Reference { get; }
    internal ScoreSamples Moving { get; }

    public double Score(double thetaRad, double sRef, double sMov, double tx, double ty) =>
        Score(new PairAlignment(thetaRad, sRef, sMov, tx, ty, 0, 0));

    /// <summary>The score at <paramref name="pose"/>'s rotation, speeds and offset (its Score is ignored).</summary>
    public double Score(PairAlignment pose)
    {
        var (movCost, movInside) = Direction(Moving, pose, _reference);
        var (refCost, refInside) = Direction(Reference, pose.Inverse(), _moving);
        double mean = 0.5 * (movCost + refCost);
        double deficit = Math.Max(0, 1 - Math.Max(movInside, refInside) / MinObjectOverlap);
        return mean + (PairAligner.CapMm - mean) * deficit;
    }

    /// <summary>
    /// Symmetric mean capped |Δh| over each map's edge cells (PairAligner's EdgeCells: object cells beside a height
    /// step of more than the cap) looked up in the other map at <paramref name="pose"/>; missing data there costs the
    /// cap, off-map is skipped. Bare board interior and table cannot dilute it. Each way gives the mean over its counted
    /// cells (the cap when none count); the score is their average. The edge cells are found on the first call.
    /// </summary>
    public double FeatureScore(PairAlignment pose)
    {
        _referenceEdges ??= PairAligner.EdgeCells.From(_reference);
        _movingEdges ??= PairAligner.EdgeCells.From(_moving);
        return 0.5 * (FeatureDirection(_movingEdges, pose, _reference) + FeatureDirection(_referenceEdges, pose.Inverse(), _moving));
    }

    private static double FeatureDirection(PairAligner.EdgeCells from, PairAlignment pose, AlignmentMap to)
    {
        const double cap = PairAligner.CapMm;
        double c = Math.Cos(pose.ThetaRad), s = Math.Sin(pose.ThetaRad);
        double sum = 0;
        int counted = 0;
        for (int i = 0; i < from.X.Length; i++)
        {
            double x = from.X[i], y = pose.MovingSpeed * from.T[i];
            double qx = c * x - s * y + pose.Tx, qy = s * x + c * y + pose.Ty;
            if (!TryLookup(to, qx, qy / pose.ReferenceSpeed, out double h)) continue;
            sum += double.IsNaN(h) ? cap : Math.Min(cap, Math.Abs(from.H[i] - h));
            counted++;
        }
        return counted == 0 ? cap : sum / counted;
    }

    /// <summary>
    /// Mean capped difference of <paramref name="from"/>'s samples, mapped by <paramref name="pose"/>, against
    /// <paramref name="to"/>, and the fraction of its object samples that land inside <paramref name="to"/>.
    /// </summary>
    private static (double Cost, double ObjectInside) Direction(ScoreSamples from, PairAlignment pose, AlignmentMap to)
    {
        const double cap = PairAligner.CapMm;
        double c = Math.Cos(pose.ThetaRad), s = Math.Sin(pose.ThetaRad);
        double sumCost = 0;
        int counted = 0, objects = 0, objectsInside = 0;
        for (int i = 0; i < from.Count; i++)
        {
            double x = from.X[i], y = pose.MovingSpeed * from.T[i];
            double qx = c * x - s * y + pose.Tx, qy = s * x + c * y + pose.Ty;
            bool isObject = from.IsObject[i];
            if (isObject) objects++;
            if (!TryLookup(to, qx, qy / pose.ReferenceSpeed, out double h)) continue;
            if (isObject) objectsInside++;
            if (double.IsNaN(h))
            {
                if (!isObject) continue;
                sumCost += cap;
            }
            else
            {
                sumCost += Math.Min(cap, Math.Abs(from.H[i] - h));
            }
            counted++;
        }
        return (counted == 0 ? cap : sumCost / counted, objects == 0 ? 0 : objectsInside / (double)objects);
    }

    /// <summary>
    /// Bilinear height at scanner X <paramref name="x"/> and moving time <paramref name="t"/>, the nearest cell next to
    /// missing data (so NaN when that is missing); false outside the map.
    /// </summary>
    internal static bool TryLookup(AlignmentMap m, double x, double t, out double height)
    {
        double u = (x - m.XMinMm) / m.ColumnMm - 0.5, v = t / m.RowS - 0.5;
        if (!(u >= -0.5 && u <= m.Columns - 0.5 && v >= -0.5 && v <= m.Rows - 0.5))
        {
            height = double.NaN;
            return false;
        }
        u = Math.Clamp(u, 0, m.Columns - 1);
        v = Math.Clamp(v, 0, m.Rows - 1);
        int c0 = (int)u, r0 = (int)v;
        int c1 = Math.Min(c0 + 1, m.Columns - 1), r1 = Math.Min(r0 + 1, m.Rows - 1);
        double fu = u - c0, fv = v - r0;
        var h = m.Height;
        float h00 = h[r0 * m.Columns + c0], h01 = h[r0 * m.Columns + c1];
        float h10 = h[r1 * m.Columns + c0], h11 = h[r1 * m.Columns + c1];
        if (float.IsNaN(h00) || float.IsNaN(h01) || float.IsNaN(h10) || float.IsNaN(h11))
            height = h[(fv < 0.5 ? r0 : r1) * m.Columns + (fu < 0.5 ? c0 : c1)];
        else
            height = (1 - fv) * ((1 - fu) * h00 + fu * h01) + fv * ((1 - fu) * h10 + fu * h11);
        return true;
    }

    internal static double XOf(AlignmentMap m, int column) => m.XMinMm + (column + 0.5) * m.ColumnMm;

    internal static double TOf(AlignmentMap m, int row) => (row + 0.5) * m.RowS;

    /// <summary>Score samples of one map: valid cells near its object, subsampled, with the object's centre.</summary>
    internal sealed class ScoreSamples
    {
        public required double[] X, T;
        public required float[] H;
        public required bool[] IsObject;
        public int ObjectCount;
        public double CentreX, CentreT;
        public int Count => X.Length;

        public static ScoreSamples From(AlignmentMap m, int max)
        {
            int cMin = int.MaxValue, cMax = -1, rMin = int.MaxValue, rMax = -1, n = 0;
            double sx = 0, st = 0;
            for (int r = 0; r < m.Rows; r++)
                for (int c = 0; c < m.Columns; c++)
                {
                    if (!m.IsObject[r * m.Columns + c]) continue;
                    (cMin, cMax, rMin, rMax) = (Math.Min(cMin, c), Math.Max(cMax, c), Math.Min(rMin, r), Math.Max(rMax, r));
                    sx += XOf(m, c);
                    st += TOf(m, r);
                    n++;
                }
            if (n == 0) return new ScoreSamples { X = [], T = [], H = [], IsObject = [] };

            int mc = (int)Math.Ceiling(MarginMm / m.ColumnMm), mr = (int)Math.Ceiling(MarginS / m.RowS);
            (cMin, cMax) = (Math.Max(0, cMin - mc), Math.Min(m.Columns - 1, cMax + mc));
            (rMin, rMax) = (Math.Max(0, rMin - mr), Math.Min(m.Rows - 1, rMax + mr));
            double cells = (double)(cMax - cMin + 1) * (rMax - rMin + 1);
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cells / max)));

            var xs = new List<double>();
            var ts = new List<double>();
            var hs = new List<float>();
            var objects = new List<bool>();
            for (int r = rMin; r <= rMax; r += stride)
                for (int c = cMin; c <= cMax; c += stride)
                {
                    float h = m.At(c, r);
                    if (float.IsNaN(h)) continue;
                    xs.Add(XOf(m, c));
                    ts.Add(TOf(m, r));
                    hs.Add(h);
                    objects.Add(m.IsObject[r * m.Columns + c]);
                }
            return new ScoreSamples
            {
                X = [.. xs], T = [.. ts], H = [.. hs], IsObject = [.. objects],
                ObjectCount = n, CentreX = sx / n, CentreT = st / n,
            };
        }
    }
}
