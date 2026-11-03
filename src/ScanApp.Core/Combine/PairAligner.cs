using static ScanApp.Core.Combine.PairScorer;

namespace ScanApp.Core.Combine;

/// <summary>
/// Aligns two passes of one object, each assumed to move at a constant, unknown speed: an exhaustive coarse search
/// over rotation, both speeds and a 2D offset, then Nelder–Mead on the best few candidates. Frames and the pose are
/// as in <see cref="PairAlignment"/>.
/// </summary>
public static class PairAligner
{
    public const double MinSpeed = 5, MaxSpeed = 60;
    /// <summary>The height difference cap, also the cost of an object sample that lands on missing data.</summary>
    public const double CapMm = 2;

    private const double ThetaStepDeg = 3;
    private const double MaxSpeedStep = 1.05;
    private const double CoarseOffsetMm = 4, FineOffsetMm = 1;
    private const int VotePoints = 128;
    private const double MinVoteSpacingMm = 3;
    private const int Peaks = 3;
    private const int PeakSeparationBins = 3;
    private const float MinPeakShare = 0.5f;
    private const int ScoredCandidates = 10000;
    private const int CoarseSamples = 1500, MidSamples = 4000;
    private const double TinyScore = 1e-6;
    private const int Refined = 6, ByVote = 3;
    private const double DifferentDeg = 10, DifferentMm = 10;

    /// <summary>
    /// Aligns <paramref name="moving"/> onto <paramref name="reference"/>; both speeds unknown (5–60 mm/s).
    /// <list type="bullet">
    /// <item>Coarse grid: full-circle rotation in 3° steps × both speeds in ≤ 5 % steps.</item>
    /// <item>Offsets: per candidate, a 4 mm vote of edge-point pairs of similar height (edges are object cells next to
    /// a height step of more than the cap, so a uniform plateau's inside does not drown its features), and the
    /// best separated vote peaks (three).</item>
    /// <item>The 10 000 candidates with the strongest votes have each peak scored with <see cref="Score"/> on ~1 500
    /// samples per map; a candidate's cost is its best peak.</item>
    /// <item>Six mutually clearly different local minima get a 1 mm offset search and Nelder–Mead over
    /// (θ, sRef, sMov, tx, ty): the best, the best more than 10° away, the best more than 10 mm away at about the same
    /// rotation, then the next best; plus the three strongest, mutually different votes at their strongest peak.</item>
    /// </list>
    /// <see cref="PairAlignment.RunnerUpRatio"/> is the refined score of the best clearly different rival (θ more than
    /// 10° away, or the moving object's centre more than 10 mm away in the reference map) over the best refined score,
    /// so at least 1, and <see cref="PairAlignment.RunnerUp"/> is that rival. With no rival refined the ratio is 1 and the
    /// runner-up null. Both maps need object cells. Multi-threaded; progress ends at 1.0; a cancelled token
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static PairAlignment Align(AlignmentMap reference, AlignmentMap moving, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fine = new PairScorer(reference, moving);
        if (fine.Reference.ObjectCount == 0 || fine.Moving.ObjectCount == 0)
            throw new ArgumentException("Both alignment maps need object cells.");
        var mid = new PairScorer(reference, moving, MidSamples);
        var coarse = new PairScorer(reference, moving, CoarseSamples);
        var centre = fine.Moving;
        var reporter = new Reporter(progress);

        var thetas = Enumerable.Range(0, (int)Math.Round(360 / ThetaStepDeg)).Select(i => i * ThetaStepDeg * Math.PI / 180).ToArray();
        int intervals = (int)Math.Ceiling(Math.Log(MaxSpeed / MinSpeed) / Math.Log(MaxSpeedStep) - 1e-9);
        var speeds = Enumerable.Range(0, intervals + 1).Select(k => MinSpeed * Math.Pow(MaxSpeed / MinSpeed, k / (double)intervals)).ToArray();
        var refEdges = EdgeCells.From(reference);
        var movEdges = EdgeCells.From(moving);
        var refVotes = speeds.Select(s => VoteSet.From(refEdges, s)).ToArray();
        var movVotes = speeds.Select(s => VoteSet.From(movEdges, s)).ToArray();

        // Vote every candidate's offset peaks; then score the peaks of the strongest candidates.
        int nt = thetas.Length, ns = speeds.Length;
        var peaks = new (double X, double Y)[nt, ns, ns][];
        var strength = new double[nt, ns, ns];
        int done = 0;
        Parallel.For(0, nt, new ParallelOptions { CancellationToken = ct }, () => new VoteBuffers(), (i, _, buffers) =>
        {
            double c = Math.Cos(thetas[i]), s = Math.Sin(thetas[i]);
            for (int k = 0; k < ns; k++)
            {
                ct.ThrowIfCancellationRequested();
                var rotated = movVotes[k].Rotated(c, s);
                for (int j = 0; j < ns; j++)
                    (peaks[i, j, k], strength[i, j, k]) = OffsetPeaks(refVotes[j], rotated, buffers);
            }
            reporter.Report(0.4 * Interlocked.Increment(ref done) / nt);
            return buffers;
        }, _ => { });

        var strengths = strength.Cast<double>().ToArray();
        Array.Sort(strengths);
        double keep = strengths[Math.Max(0, strengths.Length - ScoredCandidates)];
        var cost = new double[nt, ns, ns];
        var offset = new (double X, double Y)[nt, ns, ns];
        done = 0;
        Parallel.For(0, nt, new ParallelOptions { CancellationToken = ct }, i =>
        {
            for (int j = 0; j < ns; j++)
            {
                ct.ThrowIfCancellationRequested();
                for (int k = 0; k < ns; k++)
                {
                    cost[i, j, k] = double.PositiveInfinity;
                    if (strength[i, j, k] < keep || strength[i, j, k] <= 0) continue;
                    foreach (var t in peaks[i, j, k])
                    {
                        double score = coarse.Score(thetas[i], speeds[j], speeds[k], t.X, t.Y);
                        if (score < cost[i, j, k]) (cost[i, j, k], offset[i, j, k]) = (score, t);
                    }
                }
            }
            reporter.Report(0.4 + 0.4 * Interlocked.Increment(ref done) / nt);
        });

        var candidates = LocalMinima(cost).Where(m => !double.IsPositiveInfinity(m.Cost))
            .Select(m => new PairAlignment(thetas[m.I], speeds[m.J], speeds[m.K], offset[m.I, m.J, m.K].X, offset[m.I, m.J, m.K].Y, m.Cost, 0))
            .ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("The coarse search found no candidate.");

        // Refine the best minimum, the best one more than 10° away and the best one more than 10 mm away at about the
        // same rotation, so a coarse twin is always refined; then fill up with the next mutually different minima.
        var chosen = new List<PairAlignment> { candidates[0] };
        void Choose(PairAlignment? p)
        {
            if (p is not null && chosen.Count < Refined && chosen.All(q => ClearlyDifferent(q, p, centre))) chosen.Add(p);
        }
        Choose(candidates.FirstOrDefault(p => AnglesApart(candidates[0], p)));
        Choose(candidates.FirstOrDefault(p => !AnglesApart(candidates[0], p) && ClearlyDifferent(candidates[0], p, centre)));
        foreach (var p in candidates)
        {
            if (chosen.Count == Refined) break;
            Choose(p);
        }

        // The coarse score is unforgiving of the 4 mm vote offset on small features, so the strongest votes (at their
        // strongest peak) get refined too, whatever their coarse score.
        int byScore = chosen.Count;
        var strongest = Enumerable.Range(0, nt * ns * ns).OrderByDescending(n => strength[n / (ns * ns), n / ns % ns, n % ns]);
        foreach (int n in strongest)
        {
            if (chosen.Count == byScore + ByVote) break;
            int i = n / (ns * ns), j = n / ns % ns, k = n % ns;
            if (peaks[i, j, k].Length == 0) break;
            var p = new PairAlignment(thetas[i], speeds[j], speeds[k], peaks[i, j, k][0].X, peaks[i, j, k][0].Y, cost[i, j, k], 0);
            if (chosen.All(q => ClearlyDifferent(q, p, centre))) chosen.Add(p);
        }

        var refined = new PairAlignment[chosen.Count];
        int refinedDone = 0;
        Parallel.For(0, chosen.Count, new ParallelOptions { CancellationToken = ct }, n =>
        {
            refined[n] = Refine(chosen[n], mid, fine, ct);
            reporter.Report(0.8 + 0.2 * Interlocked.Increment(ref refinedDone) / chosen.Count);
        });
        Array.Sort(refined, (a, b) => a.Score.CompareTo(b.Score));
        var best = refined[0];

        // Refined against refined only, so the ratio is at least 1. With no rival refined there is no evidence of a
        // clear win, so it is 1 ("uncertain"): a wrong alignment must not be shown as if it were right.
        var rival = refined.Skip(1).FirstOrDefault(p => ClearlyDifferent(best, p, centre));
        double ratio = rival is null ? 1 : rival.Score / Math.Max(best.Score, TinyScore);

        reporter.Report(1.0);
        return best with { RunnerUpRatio = ratio, RunnerUp = rival };
    }

    /// <summary>
    /// Symmetric capped |Δheight| over the overlap (cap 2 mm); the cost used everywhere. The samples of each map are its
    /// valid cells within 10 mm and 0.3 s of its object (subsampled to ≤ 25 000); each is looked up, bilinearly, in the
    /// other map at the pose. A sample outside the other map is skipped, so a pass that covers only part of the object
    /// is not pulled to stretch over the rest. Inside it, an object sample on missing data costs the cap (a pass must
    /// not hide in a shadow) and a table sample there is skipped. Each way (moving in reference, reference in moving)
    /// gives the mean over its counted samples; the score is their average, blended towards the cap when neither way
    /// has at least half its object samples inside the other map, so tiny overlaps are never rewarded. To score many
    /// poses of one pair, build a <see cref="PairScorer"/> once.
    /// </summary>
    public static double Score(AlignmentMap reference, AlignmentMap moving, double thetaRad, double sRef, double sMov, double tx, double ty) =>
        new PairScorer(reference, moving).Score(thetaRad, sRef, sMov, tx, ty);

    /// <summary>Per-thread vote and block-sum buffers.</summary>
    private sealed class VoteBuffers
    {
        public float[] Votes = [], Blocks = [];
    }

    /// <summary>
    /// Offsets (reference minus rotated moving point) that edge-point pairs of similar height agree on: votes weighted
    /// 1 − |Δh| / cap in 4 mm bins, summed over 2 × 2 blocks; up to three peaks more than three bins apart, each at its
    /// block's vote-weighted centre. The strength is the best block over the mean block (votes spread evenly over all
    /// offsets), so speeds that squeeze the points together, and pile up chance votes, gain nothing.
    /// </summary>
    private static ((double X, double Y)[] Peaks, double Strength) OffsetPeaks(VoteSet r, VoteSet m, VoteBuffers buffers)
    {
        if (r.Count == 0 || m.Count == 0) return ([], 0);
        double x0 = r.MinX - m.MaxX - CoarseOffsetMm, y0 = r.MinY - m.MaxY - CoarseOffsetMm;
        int nx = (int)((r.MaxX - m.MinX - x0) / CoarseOffsetMm) + 2, ny = (int)((r.MaxY - m.MinY - y0) / CoarseOffsetMm) + 2;
        if (buffers.Votes.Length < nx * ny) buffers.Votes = new float[nx * ny];
        var votes = buffers.Votes;
        Array.Clear(votes, 0, nx * ny);

        const double perBin = 1 / CoarseOffsetMm;
        const float perCap = (float)(1 / CapMm);
        double total = 0;
        // Both sets are sorted by height, so each moving point only meets the reference points within the cap.
        double[] rx = r.X, ry = r.Y;
        float[] rh = r.H;
        int rn = rx.Length, lo = 0, hi = 0;
        for (int a = 0; a < m.Count; a++)
        {
            double mx = m.X[a] + x0, my = m.Y[a] + y0;
            float mh = m.H[a];
            while (lo < rn && rh[lo] <= mh - CapMm) lo++;
            while (hi < rn && rh[hi] < mh + CapMm) hi++;
            for (int b = lo; b < hi; b++)
            {
                float w = 1 - Math.Abs(rh[b] - mh) * perCap;
                votes[(int)((ry[b] - my) * perBin) * nx + (int)((rx[b] - mx) * perBin)] += w;
                total += w;
            }
        }

        // Greedy peak picking: the highest 2 x 2 block, then the highest one not within three bins of a pick, and so on;
        // a peak under half the first is not worth scoring.
        int bw = nx - 1, bh = ny - 1;
        if (buffers.Blocks.Length < bw * bh) buffers.Blocks = new float[bw * bh];
        var blocks = buffers.Blocks;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int i = by * nx + bx;
                blocks[by * bw + bx] = votes[i] + votes[i + 1] + votes[i + nx] + votes[i + nx + 1];
            }

        var picks = new List<(double X, double Y)>(Peaks);
        float first = 0;
        for (int p = 0; p < Peaks; p++)
        {
            int best = -1;
            float bestSum = p == 0 ? 0 : MinPeakShare * first;
            for (int i = 0; i < bw * bh; i++)
                if (blocks[i] > bestSum) (bestSum, best) = (blocks[i], i);
            if (best < 0) break;
            int bx = best % bw, by = best / bw;
            if (p == 0) first = bestSum;

            double sx = 0, sy = 0, sw = 0;
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    double w = votes[(by + dy) * nx + bx + dx];
                    (sx, sy, sw) = (sx + w * (bx + dx + 0.5), sy + w * (by + dy + 0.5), sw + w);
                }
            picks.Add((x0 + sx / sw * CoarseOffsetMm, y0 + sy / sw * CoarseOffsetMm));

            // Zeroing the neighbourhood is exact suppression: a later pick is the best block outside every earlier one.
            for (int iy = Math.Max(0, by - PeakSeparationBins); iy <= Math.Min(bh - 1, by + PeakSeparationBins); iy++)
                for (int ix = Math.Max(0, bx - PeakSeparationBins); ix <= Math.Min(bw - 1, bx + PeakSeparationBins); ix++)
                    blocks[iy * bw + ix] = 0;
        }
        var found = picks.ToArray();
        return (found, total <= 0 ? 0 : first / (4 * total / (nx * ny)));
    }

    /// <summary>Grid cells no higher than any neighbour (rotation wraps), best first.</summary>
    private static List<(double Cost, int I, int J, int K)> LocalMinima(double[,,] cost)
    {
        int ni = cost.GetLength(0), nj = cost.GetLength(1), nk = cost.GetLength(2);
        var minima = new List<(double Cost, int I, int J, int K)>();
        for (int i = 0; i < ni; i++)
            for (int j = 0; j < nj; j++)
                for (int k = 0; k < nk; k++)
                {
                    double c = cost[i, j, k];
                    bool isMin = true;
                    for (int di = -1; di <= 1 && isMin; di++)
                        for (int dj = -1; dj <= 1 && isMin; dj++)
                            for (int dk = -1; dk <= 1 && isMin; dk++)
                            {
                                int jj = j + dj, kk = k + dk;
                                if (jj < 0 || kk < 0 || jj >= nj || kk >= nk) continue;
                                if (cost[(i + di + ni) % ni, jj, kk] < c) isMin = false;
                            }
                    if (isMin) minima.Add((c, i, j, k));
                }
        minima.Sort();
        return minima;
    }

    /// <summary>
    /// Different rotation (more than 10°) or a different place (more than 10 mm, in the reference map) for the moving
    /// object's centre. Comparing where the centre lands keeps same-orientation poses that differ only in both speeds'
    /// common scale, which fit equally well, from counting as different.
    /// </summary>
    private static bool ClearlyDifferent(PairAlignment a, PairAlignment b, ScoreSamples moving)
    {
        if (AnglesApart(a, b)) return true;
        var (ax, ay) = a.ToReference(moving.CentreX, a.MovingSpeed * moving.CentreT);
        var (bx, by) = b.ToReference(moving.CentreX, b.MovingSpeed * moving.CentreT);
        double dt = ay / a.ReferenceSpeed - by / b.ReferenceSpeed;
        return Math.Sqrt((ax - bx) * (ax - bx) + Math.Pow(dt * a.ReferenceSpeed, 2)) > DifferentMm;
    }

    private static bool AnglesApart(PairAlignment a, PairAlignment b) =>
        Math.Abs(Math.IEEERemainder(a.ThetaRad - b.ThetaRad, 2 * Math.PI)) > DifferentDeg * Math.PI / 180;

    /// <summary>
    /// A 1 mm offset search (±4 mm), then Nelder–Mead over θ, both speeds and where the moving centre lands: two rounds
    /// on the mid samples, a last one on the fine samples with smaller steps. The score returned is the fine one.
    /// </summary>
    private static PairAlignment Refine(PairAlignment start, PairScorer mid, PairScorer fine, CancellationToken ct)
    {
        var best = start with { Score = double.MaxValue };
        int steps = (int)Math.Round(CoarseOffsetMm / FineOffsetMm);
        for (int dy = -steps; dy <= steps; dy++)
            for (int dx = -steps; dx <= steps; dx++)
            {
                var p = start with { Tx = start.Tx + dx * FineOffsetMm, Ty = start.Ty + dy * FineOffsetMm };
                double score = mid.Score(p);
                if (score < best.Score) best = p with { Score = score };
            }
        ct.ThrowIfCancellationRequested();

        // The landing point (lx, lt · sRef) of the moving centre replaces (tx, ty), so rotating or rescaling keeps the
        // overlap in place; lt is in reference moving time.
        double cx = fine.Moving.CentreX, cT = fine.Moving.CentreT;
        var (lx, ly) = best.ToReference(cx, best.MovingSpeed * cT);
        PairAlignment Pose(double[] p)
        {
            double c = Math.Cos(p[0]), s = Math.Sin(p[0]), my = p[2] * cT;
            return new PairAlignment(p[0], p[1], p[2], p[3] - (c * cx - s * my), p[4] * p[1] - (s * cx + c * my), 0, 0);
        }
        Func<double[], double> On(PairScorer scorer) => p =>
        {
            ct.ThrowIfCancellationRequested();
            if (p[1] < MinSpeed || p[1] > MaxSpeed || p[2] < MinSpeed || p[2] > MaxSpeed) return 10 * CapMm;
            return scorer.Score(Pose(p));
        };
        double[] Steps(double[] x, double scale) =>
            [2 * Math.PI / 180 * scale, 0.03 * x[1] * scale, 0.03 * x[2] * scale, 2 * scale, 2 / x[1] * scale];

        var x = new[] { best.ThetaRad, best.ReferenceSpeed, best.MovingSpeed, lx, ly / best.ReferenceSpeed };
        (x, _) = NelderMead.Minimize(On(mid), x, Steps(x, 1), 400);
        (x, _) = NelderMead.Minimize(On(mid), x, Steps(x, 0.5), 300);
        (x, double f) = NelderMead.Minimize(On(fine), x, Steps(x, 0.25), 200);
        return Pose(x) with { Score = f };
    }

    /// <summary>
    /// Edge cells of one map, found once: object cells with a valid 4-neighbour more than the cap lower or higher
    /// (a missing or off-map neighbour is no evidence, so a pass that ends on the object adds no false edge). Also the
    /// cells <see cref="PairScorer.FeatureScore"/> compares.
    /// </summary>
    internal sealed record EdgeCells(double[] X, double[] T, float[] H)
    {
        public static EdgeCells From(AlignmentMap m)
        {
            var xs = new List<double>();
            var ts = new List<double>();
            var hs = new List<float>();
            for (int r = 0; r < m.Rows; r++)
                for (int c = 0; c < m.Columns; c++)
                {
                    if (!m.IsObject[r * m.Columns + c]) continue;
                    float h = m.At(c, r);
                    bool Step(int cc, int rr) =>
                        cc >= 0 && rr >= 0 && cc < m.Columns && rr < m.Rows && Math.Abs(m.At(cc, rr) - h) > CapMm;
                    if (!(Step(c - 1, r) || Step(c + 1, r) || Step(c, r - 1) || Step(c, r + 1))) continue;
                    xs.Add(XOf(m, c));
                    ts.Add(TOf(m, r));
                    hs.Add(h);
                }
            return new EdgeCells([.. xs], [.. ts], [.. hs]);
        }
    }

    /// <summary>
    /// Edge points of one map in its frame at one speed, thinned to one per square cell (at least 3 mm), at most
    /// <see cref="VotePoints"/>, sorted by height.
    /// </summary>
    private sealed class VoteSet
    {
        public required double[] X, Y;
        public required float[] H;
        public double MinX, MaxX, MinY, MaxY;
        public int Count => X.Length;

        public static VoteSet From(EdgeCells edges, double speed)
        {
            for (double spacing = MinVoteSpacingMm; ; spacing *= 1.25)
            {
                var taken = new HashSet<(int, int)>();
                var picked = new List<int>();
                for (int i = 0; i < edges.X.Length; i++)
                    if (taken.Add(((int)Math.Floor(edges.X[i] / spacing), (int)Math.Floor(speed * edges.T[i] / spacing))))
                        picked.Add(i);
                if (picked.Count > VotePoints) continue;
                picked.Sort((a, b) => edges.H[a].CompareTo(edges.H[b]));
                return Make([.. picked.Select(i => edges.X[i])], [.. picked.Select(i => speed * edges.T[i])],
                    [.. picked.Select(i => edges.H[i])]);
            }
        }

        public VoteSet Rotated(double c, double s)
        {
            var xs = new double[Count];
            var ys = new double[Count];
            for (int i = 0; i < Count; i++) (xs[i], ys[i]) = (c * X[i] - s * Y[i], s * X[i] + c * Y[i]);
            return Make(xs, ys, H);
        }

        private static VoteSet Make(double[] xs, double[] ys, float[] hs) => new()
        {
            X = xs, Y = ys, H = hs,
            MinX = xs.Length == 0 ? 0 : xs.Min(), MaxX = xs.Length == 0 ? 0 : xs.Max(),
            MinY = ys.Length == 0 ? 0 : ys.Min(), MaxY = ys.Length == 0 ? 0 : ys.Max(),
        };
    }

    /// <summary>Thread-safe progress that never goes backwards.</summary>
    private sealed class Reporter(IProgress<double>? progress)
    {
        private readonly Lock _lock = new();
        private double _last;

        public void Report(double value)
        {
            if (progress is null) return;
            lock (_lock)
            {
                if (value <= _last) return;
                _last = value;
                progress.Report(value);
            }
        }
    }
}
