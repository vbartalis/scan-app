using ScanApp.Core.Cleanup;
using ScanApp.Core.Model;
using ScanApp.Core.Storage;
using ScanApp.Core.Viewing;
using static ScanApp.Core.Combine.PairScorer;

namespace ScanApp.Core.Combine;

/// <summary>
/// Combines several passes of one object into one saved result: every pass is aligned to the first (the reference),
/// each pass's travel is solved as a speed profile, and the cleaned points of all passes are merged into the
/// reference frame. Frames are as in <see cref="PairAlignment"/>, with each pass's s·t replaced by its travel.
/// </summary>
public static class Combiner
{
    /// <summary>The merged cloud's cap, shared by the passes in proportion to their sizes.</summary>
    public const int MaxPoints = 2_000_000;
    /// <summary>The reference speed when no pass is clearly rotated (the preview's assumption).</summary>
    public const double ScaleUnknownSpeed = 20;
    public const int Rounds = 3;
    /// <summary>A pass within this of 0° or 180° from the reference adds no scale.</summary>
    public const double SameRotationDeg = 10;

    private const double RectifiedRowMm = 0.5;

    /// <summary>An overlap MAD at or above this share of the cap means the pass barely overlaps its partner.</summary>
    private const double NearCapShare = 0.9;

    /// <summary>
    /// A pass whose mean speed lies outside the aligner's 5–60 mm/s, or whose overlap MAD is at or near the 2 mm cap
    /// (almost no overlap), was not solved convincingly; any such pass marks the result "Uncertain alignment".
    /// </summary>
    public static bool IsImplausible(PassOutcome outcome) =>
        outcome.MeanSpeedMmPerS < PairAligner.MinSpeed || outcome.MeanSpeedMmPerS > PairAligner.MaxSpeed
        || outcome.OverlapMadMm >= NearCapShare * PairAligner.CapMm;
    private const double SmoothingS = 0.5;

    /// <summary>
    /// Reads, cleans, maps and aligns the passes (first = reference), refines 3 rounds, merges the cloud, saves the result.
    /// <list type="bullet">
    /// <item>Reading: per pass, <see cref="MotionAnalyzer"/>, its saved edits and its direction give the
    /// <see cref="PassCleanup"/> (trims and zone), <see cref="ReflectionFilter.Find"/> its reflection mask (none without
    /// reflectance), then <see cref="AlignmentMapBuilder"/> the map from the masked pass, with the zone applied per
    /// cell; the mask also holds for the merge. A map with no object cell, also when the zone removed it, throws
    /// <see cref="InvalidDataException"/> "No object found in pass N." The map's table is set on the pass's cleanup for
    /// the merge.</item>
    /// <item>Aligning: <see cref="PairAligner.Align"/> of every other pass to the reference; with three or more passes,
    /// also of each non-reference pass to the one before it (P1←P2, P2←P3, …: N − 2 more), counted in the same stage,
    /// for the cycle check. The scale comes from the
    /// clearly rotated passes (median of their reference speeds); with none, the reference moves at 20 mm/s and
    /// <see cref="CombineResult.ScaleUnknown"/> is set. Same-orientation passes keep only their speed ratio.</item>
    /// <item>Speed profiles, 3 rounds: each other pass is solved by <see cref="SpeedProfileSolver"/> against the
    /// reference and its pose refitted; the reference is solved against the first clearly rotated pass (inverse pose)
    /// unless the scale is unknown (then it stays at 20 mm/s), and the poses are refitted again. The reference's
    /// overlap is judged against that same pass (the second selected when the scale is unknown).</item>
    /// <item>Table check: after reading, <see cref="TableCheck.Uncertain"/> judges every pass's table; its verdict is
    /// the outcome's <see cref="PassOutcome.TableUncertain"/>. The mask's size is its
    /// <see cref="PassOutcome.ReflectionsRemoved"/> (null for a pass without reflectance).</item>
    /// <item>"Uncertain alignment": with two passes, a <see cref="Certainty.FeatureRatio"/> below
    /// <see cref="Certainty.MinFeatureRatio"/>; with three or more, any triangle (reference, Pi, Pi+1) whose alignments
    /// do not close (<see cref="Certainty.Closes"/>, or whose chained pair cannot be aligned at all; listed in
    /// <see cref="CombineResult.OpenCycles"/>); in every case
    /// also an implausible pass (<see cref="IsImplausible"/>) or a pass whose table is uncertain.</item>
    /// <item>Merging: each pass's cleaned points inside its zone, sampled like <see cref="PassPreviewBuilder"/>, go
    /// through its travel and pose, Z as height above that pass's table; at most <see cref="MaxPoints"/> in total.</item>
    /// </list>
    /// Progress reports a stage ("Reading", "Aligning", "Speed profiles", "Merging") and the fraction of that stage;
    /// within a pass's share of "Reading", the analysis is 0–0.4, the filter 0.4–0.7 and the map 0.7–1.
    /// Runs on the thread pool. Nothing is written before the final <see cref="ScanRepository.SaveResult"/>, so a
    /// failed or cancelled combine leaves no trace; raw passes and edits are only read.
    /// </summary>
    public static Task<CombineResult> CombineAsync(ScanRepository scan, IReadOnlyList<int> passIds,
                                                   IProgress<(string stage, double fraction)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(passIds);
        if (passIds.Count < 2) throw new ArgumentException("Combining needs at least two passes.", nameof(passIds));
        if (passIds.Count > 256) throw new ArgumentException("At most 256 passes can be combined.", nameof(passIds));
        if (passIds.Distinct().Count() != passIds.Count) throw new ArgumentException("A pass is selected twice.", nameof(passIds));
        var ids = passIds.ToArray();
        return Task.Run(() => Combine(scan, ids, progress, ct), ct);
    }

    /// <summary>One pass's state through the combine: its map, travel and pose (in travel coordinates).</summary>
    private sealed class Pass(int id, PassReader reader, PassCleanup cleanup, AlignmentMap map, bool filtersReflections)
    {
        public bool FiltersReflections { get; } = filtersReflections;
        public int Id { get; } = id;
        public PassReader Reader { get; } = reader;
        public PassCleanup Cleanup { get; } = cleanup;
        public AlignmentMap Map { get; } = map;
        public SpeedProfile Speed { get; set; } = null!;
        public Rectified Rect { get; set; } = null!;
        public PassPose Pose { get; set; } = new(0, 0, 0);
        public bool Rotated { get; set; }

        public void SetSpeed(SpeedProfile speed)
        {
            Speed = speed;
            Rect = Rectified.From(Map, speed);
        }
    }

    private static CombineResult Combine(ScanRepository scan, int[] ids, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        var passes = new List<Pass>();
        try
        {
            Read(scan, ids, passes, progress, ct);
            var tableUncertain = TableCheck.Uncertain([.. passes.Select(p => p.Map)]);
            var (scaleUnknown, pairCertain, openCycles) = Align(passes, progress, ct);
            var partner = passes[Partner(passes, scaleUnknown)];
            Profiles(passes, partner, progress, ct);
            var outcomes = passes.Select((p, i) => Outcome(p, i == 0 ? partner : passes[0], i == 0) with
            {
                TableUncertain = tableUncertain[i],
                ReflectionsRemoved = p.Reader.HasReflectance && p.FiltersReflections ? p.Cleanup.Reflections.Count : null,
                ReflectionFilterOff = !p.FiltersReflections,
            }).ToList();
            var cloud = Merge(passes, progress, ct);
            bool uncertain = !pairCertain || openCycles?.Count > 0 || outcomes.Any(IsImplausible) || tableUncertain.Any(u => u);
            // The last report comes before the save, so nothing that runs after it can fail a saved combine.
            progress?.Report(("Merging", 1.0));
            ct.ThrowIfCancellationRequested();
            return scan.SaveResult(id => new CombineResult(id, DateTimeOffset.Now, ids[0], outcomes, uncertain, scaleUnknown, openCycles), cloud);
        }
        finally
        {
            foreach (var p in passes) p.Reader.Dispose();
        }
    }

    private static void Read(ScanRepository scan, int[] ids, List<Pass> passes, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        const string stage = "Reading";
        progress?.Report((stage, 0));
        for (int i = 0; i < ids.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            int id = ids[i];
            var metadata = scan.Passes.FirstOrDefault(p => p.Id == id)
                ?? throw new ArgumentException($"Pass {id} does not exist in this scan.", nameof(ids));
            var reader = scan.OpenPass(id);
            try
            {
                var analysis = MotionAnalyzer.Analyze(reader, new MotionOptions(), Within(progress, stage, i, ids.Length, 0, 0.4), ct);
                var edits = scan.LoadEdits(id);
                var cleanup = PassCleanup.From(analysis, edits, metadata.Direction == PassDirection.CameraBack);
                // The mask holds for the map and, through the pass's cleanup, for the merge; a pass with the filter off has none.
                if (edits.FiltersReflections)
                    cleanup = cleanup with
                    {
                        Reflections = ReflectionFilter.Find(reader, cleanup, Within(progress, stage, i, ids.Length, 0.4, 0.7), ct),
                    };
                var map = AlignmentMapBuilder.Build(reader, cleanup, Within(progress, stage, i, ids.Length, 0.7, 1), ct);
                if (!map.IsObject.Any(o => o)) throw new InvalidDataException($"No object found in pass {id}.");
                // The map's table is what the zone's height band is measured from in the merge walk.
                passes.Add(new Pass(id, reader, cleanup with { Table = map.Table }, map, edits.FiltersReflections));
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Aligns every other pass to the reference, judges the alignments (<see cref="Certainty"/>) and sets the starting
    /// travels and poses. Returns whether the scale is unknown; with two passes, whether the pair is certain on features
    /// (true with more); with three or more, the cycles that did not close (null with two).
    /// </summary>
    private static (bool ScaleUnknown, bool PairCertain, List<int[]>? OpenCycles) Align(List<Pass> passes,
        IProgress<(string, double)>? progress, CancellationToken ct)
    {
        const string stage = "Aligning";
        progress?.Report((stage, 0));
        var reference = passes[0];
        var pairs = new PairAlignment[passes.Count];
        int alignments = 2 * passes.Count - 3;   // N − 1 to the reference, N − 2 chained
        for (int i = 1; i < passes.Count; i++)
        {
            pairs[i] = PairAligner.Align(reference.Map, passes[i].Map, Within(progress, stage, i - 1, alignments, 0, 1), ct);
            passes[i].Rotated = !SameOrientation(pairs[i].ThetaRad);
        }

        // Two passes: best against runner-up on features. More: each chained pair Pi←Pi+1 must agree with the
        // composition through the reference.
        bool pairCertain = true;
        List<int[]>? openCycles = null;
        if (passes.Count == 2)
        {
            pairCertain = Certainty.FeatureRatio(reference.Map, passes[1].Map, pairs[1]) >= Certainty.MinFeatureRatio;
        }
        else
        {
            openCycles = [];
            for (int i = 1; i < passes.Count - 1; i++)
            {
                if (!ChainCloses(pairs[i], pairs[i + 1], passes[i], passes[i + 1],
                        Within(progress, stage, passes.Count - 2 + i, alignments, 0, 1), ct))
                    openCycles.Add([reference.Id, passes[i].Id, passes[i + 1].Id]);
            }
        }

        var rotated = pairs.Skip(1).Where(p => !SameOrientation(p.ThetaRad)).Select(p => p.ReferenceSpeed).Order().ToArray();
        bool scaleUnknown = rotated.Length == 0;
        double sRef = scaleUnknown ? ScaleUnknownSpeed
            : rotated.Length % 2 == 1 ? rotated[rotated.Length / 2] : 0.5 * (rotated[rotated.Length / 2 - 1] + rotated[rotated.Length / 2]);

        reference.SetSpeed(Constant(reference.Map, sRef));
        for (int i = 1; i < passes.Count; i++)
        {
            // Rescale the pair to the common reference speed, keeping where the moving object's centre lands.
            var pair = pairs[i];
            double k = sRef / pair.ReferenceSpeed, sMov = k * pair.MovingSpeed;
            var centre = ScoreSamples.From(passes[i].Map, 2000);
            var (lx, ly) = pair.ToReference(centre.CentreX, pair.MovingSpeed * centre.CentreT);
            double c = Math.Cos(pair.ThetaRad), s = Math.Sin(pair.ThetaRad), my = sMov * centre.CentreT;
            passes[i].Pose = new PassPose(pair.ThetaRad, lx - (c * centre.CentreX - s * my), k * ly - (s * centre.CentreX + c * my));
            passes[i].SetSpeed(Constant(passes[i].Map, sMov));
            passes[i].Pose = Refit(reference.Rect, passes[i].Rect, passes[i].Pose, ct);
        }
        progress?.Report((stage, 1));
        return (scaleUnknown, pairCertain, openCycles);
    }

    /// <summary>
    /// Aligns <paramref name="b"/> to <paramref name="a"/> and checks the triangle through the reference. A chained
    /// pair that cannot be aligned at all (e.g. the coarse search finds no candidate: the two passes share no feature)
    /// is no evidence of agreement, so its cycle is open rather than the combine failing; cancellation still throws.
    /// </summary>
    private static bool ChainCloses(PairAlignment refToA, PairAlignment refToB, Pass a, Pass b, IProgress<double>? progress,
                                    CancellationToken ct)
    {
        PairAlignment chain;
        try
        {
            chain = PairAligner.Align(a.Map, b.Map, progress, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return false;
        }
        return Certainty.Closes(refToA, refToB, chain, b.Map);
    }

    /// <summary>
    /// The pass the reference's own travel is solved against: the first clearly rotated one, since only a rotated pair
    /// fixes the scale (a same-orientation pair could warp together freely); the second selected when there is none.
    /// </summary>
    private static int Partner(List<Pass> passes, bool scaleUnknown)
    {
        int rotated = passes.FindIndex(1, p => p.Rotated);
        return scaleUnknown || rotated < 0 ? 1 : rotated;
    }

    private static bool SameOrientation(double thetaRad)
    {
        double deg = Math.Abs(Math.IEEERemainder(thetaRad, Math.PI)) * 180 / Math.PI;
        return deg <= SameRotationDeg;
    }

    private static SpeedProfile Constant(AlignmentMap map, double speed) =>
        new([0, map.DurationS], [0, speed * map.DurationS]);

    /// <summary>Alternates speed profiles and pose refits for <see cref="Rounds"/> rounds.</summary>
    private static void Profiles(List<Pass> passes, Pass partner, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        bool scaleUnknown = !partner.Rotated;
        const string stage = "Speed profiles";
        progress?.Report((stage, 0));
        var reference = passes[0];
        int steps = Rounds * (passes.Count - 1 + (scaleUnknown ? 0 : 1)), done = 0;
        for (int round = 0; round < Rounds; round++)
        {
            for (int i = 1; i < passes.Count; i++)
            {
                var p = passes[i];
                p.SetSpeed(SpeedProfileSolver.Solve(reference.Rect.Map, p.Map, SolvePose(reference.Rect, p.Pose, p.Rect.Speed), ct));
                p.Pose = Refit(reference.Rect, p.Rect, p.Pose, ct);
                progress?.Report((stage, ++done / (double)steps));
            }
            if (scaleUnknown) continue;

            // The reference against its rotated partner, seen from that pass.
            var inverse = Inverse(partner.Pose);
            reference.SetSpeed(SpeedProfileSolver.Solve(partner.Rect.Map, reference.Map, SolvePose(partner.Rect, inverse, reference.Rect.Speed), ct));
            for (int i = 1; i < passes.Count; i++) passes[i].Pose = Refit(reference.Rect, passes[i].Rect, passes[i].Pose, ct);
            progress?.Report((stage, ++done / (double)steps));
        }
    }

    private static PassPose Inverse(PassPose p)
    {
        double c = Math.Cos(p.ThetaRad), s = Math.Sin(p.ThetaRad);
        return new PassPose(-p.ThetaRad, -(c * p.Tx + s * p.Ty), -(-s * p.Tx + c * p.Ty));
    }

    /// <summary>The pose for <see cref="SpeedProfileSolver.Solve"/>: the moving pass in plain travel, the reference rectified.</summary>
    private static PairAlignment SolvePose(Rectified reference, PassPose pose, double movingSpeed) =>
        new(pose.ThetaRad, reference.Speed, movingSpeed, pose.Tx, pose.Ty - reference.Y0, 0, 0);

    /// <summary>The pose between two rectified maps, for <see cref="PairScorer"/>.</summary>
    private static PairAlignment RectPose(Rectified reference, Rectified moving, PassPose pose)
    {
        double c = Math.Cos(pose.ThetaRad), s = Math.Sin(pose.ThetaRad);
        return new PairAlignment(pose.ThetaRad, reference.Speed, moving.Speed,
            pose.Tx - s * moving.Y0, pose.Ty + c * moving.Y0 - reference.Y0, 0, 0);
    }

    private static PassPose FromRect(Rectified reference, Rectified moving, double theta, double tx, double ty)
    {
        double c = Math.Cos(theta), s = Math.Sin(theta);
        return new PassPose(theta, tx + s * moving.Y0, ty - c * moving.Y0 + reference.Y0);
    }

    /// <summary>Nelder–Mead over the rotation and offset with both travels held, scored by <see cref="PairScorer"/>.</summary>
    private static PassPose Refit(Rectified reference, Rectified moving, PassPose pose, CancellationToken ct)
    {
        var scorer = new PairScorer(reference.Map, moving.Map);
        var start = RectPose(reference, moving, pose);
        double Cost(double[] x)
        {
            ct.ThrowIfCancellationRequested();
            return scorer.Score(x[0], reference.Speed, moving.Speed, x[1], x[2]);
        }
        var (best, _) = NelderMead.Minimize(Cost, [start.ThetaRad, start.Tx, start.Ty], [0.005, 0.5, 0.5], 300, 1e-7);
        return FromRect(reference, moving, best[0], best[1], best[2]);
    }

    /// <summary>What the combine found for one pass; the reference's overlap is judged against <paramref name="other"/>.</summary>
    private static PassOutcome Outcome(Pass pass, Pass other, bool isReference)
    {
        var profile = pass.Speed;
        double span = profile.TimeS[^1] - profile.TimeS[0];
        double mean = span > 0 ? (profile.TravelMm[^1] - profile.TravelMm[0]) / span : 0;
        double mad = OverlapMad(pass.Rect, other.Rect, isReference ? Inverse(other.Pose) : pass.Pose);
        return new PassOutcome(pass.Id, isReference ? new PassPose(0, 0, 0) : pass.Pose, profile, mean, RangeFraction(profile, mean), mad);
    }

    /// <summary>(p95 − p5) of the speed, smoothed over about 0.5 s, halved and over the mean: ±28 % reads 0.28.</summary>
    private static double RangeFraction(SpeedProfile p, double mean)
    {
        int n = p.TimeS.Length - 1;
        if (n < 1 || mean <= 0) return 0;
        var speed = new double[n];
        for (int i = 0; i < n; i++) speed[i] = (p.TravelMm[i + 1] - p.TravelMm[i]) / (p.TimeS[i + 1] - p.TimeS[i]);
        int half = Math.Max(0, (int)Math.Round(SmoothingS / ((p.TimeS[^1] - p.TimeS[0]) / n) / 2));
        var smooth = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(n - 1, i + half);
            double sum = 0;
            for (int j = a; j <= b; j++) sum += speed[j];
            smooth[i] = sum / (b - a + 1);
        }
        Array.Sort(smooth);
        double P(double q) => smooth[Math.Clamp((int)Math.Round(q * (n - 1)), 0, n - 1)];
        return (P(0.95) - P(0.05)) / 2 / mean;
    }

    /// <summary>
    /// The median |Δheight| over the moving pass's object cells that land on the reference's object; the cap when none
    /// do (the result must stay storable as JSON, which has no NaN).
    /// </summary>
    private static double OverlapMad(Rectified moving, Rectified reference, PassPose pose)
    {
        var p = RectPose(reference, moving, pose);
        double c = Math.Cos(p.ThetaRad), s = Math.Sin(p.ThetaRad);
        var m = moving.Map;
        var diffs = new List<double>();
        for (int r = 0; r < m.Rows; r++)
            for (int col = 0; col < m.Columns; col++)
            {
                if (!m.IsObject[r * m.Columns + col]) continue;
                double x = XOf(m, col), y = moving.Speed * TOf(m, r);
                if (!TryLookup(reference.Map, c * x - s * y + p.Tx, (s * x + c * y + p.Ty) / reference.Speed, out double h)) continue;
                if (!(h > AlignmentMapBuilder.ObjectMm)) continue;
                diffs.Add(Math.Abs(m.At(col, r) - h));
            }
        if (diffs.Count == 0) return PairAligner.CapMm;
        diffs.Sort();
        return diffs[diffs.Count / 2];
    }

    /// <summary>
    /// Every pass's sampled cleaned points through its travel and pose into the reference frame, each Z as height above
    /// that pass's own table (its map's <see cref="TablePlane"/>), so passes at different rotations agree.
    /// </summary>
    private static MergedCloud Merge(List<Pass> passes, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        const string stage = "Merging";
        progress?.Report((stage, 0));
        var walks = passes.Select(p => CleanedPassWalk.Create(p.Reader.ProfileCount, p.Cleanup)).ToArray();
        var sizes = passes.Select((p, i) => (double)walks[i].Kept.Length * p.Reader.PointsPerProfile).ToArray();
        double total = Math.Max(1, sizes.Sum());
        var xs = new List<float>();
        var ys = new List<float>();
        var zs = new List<float>();
        var tags = new List<byte>();

        for (int i = 0; i < passes.Count; i++)
        {
            var pass = passes[i];
            var walk = walks[i];
            int profiles = walk.Kept.Length, points = pass.Reader.PointsPerProfile;
            if (profiles == 0) continue;
            int budget = (int)Math.Floor(MaxPoints * sizes[i] / total);
            int k = PassPreviewBuilder.SampleStride(profiles, points, budget);
            double duration = walk.Duration(pass.Reader);
            var pose = i == 0 ? new PassPose(0, 0, 0) : pass.Pose;
            double c = Math.Cos(pose.ThetaRad), s = Math.Sin(pose.ThetaRad);
            int step = Math.Max(1, profiles / 20);
            var table = pass.Map.Table;

            foreach (var (p, ordinal, moving) in walk.Walk(pass.Reader, ct))
            {
                int shown = pass.Cleanup.Reverse ? profiles - 1 - ordinal : ordinal;
                if (ordinal % step == 0) progress?.Report((stage, (i + ordinal / (double)profiles) / passes.Count));
                if (shown % k != 0 && shown != profiles - 1) continue;
                double t = pass.Cleanup.Reverse ? duration - moving : moving;
                double y = pass.Speed.TravelAt(t);
                for (int j = 0; j < points; j += k)
                {
                    float x = p.X[j], z = p.Z[j];
                    if (float.IsNaN(x) || float.IsNaN(z)) continue;
                    xs.Add((float)(c * x - s * y + pose.Tx));
                    ys.Add((float)(s * x + c * y + pose.Ty));
                    zs.Add(table is null ? z : (float)(z - table.At(x, t)));
                    tags.Add((byte)i);
                }
            }
        }
        return new MergedCloud([.. xs], [.. ys], [.. zs], [.. tags]);
    }

    /// <summary>Progress of a sub-step <paramref name="index"/> of <paramref name="count"/>, mapped to [from, to] of its share of the stage.</summary>
    private static IProgress<double>? Within(IProgress<(string, double)>? progress, string stage, int index, int count,
                                             double from, double to) =>
        progress is null ? null : new Relay(f => progress.Report((stage, (index + from + (to - from) * f) / count)));

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>
    /// A map resampled along its pass's travel: row k is travel Y0 + (k + 0.5)·0.5 mm, its time axis that travel over
    /// the mean speed, so a constant-speed scorer reads it at <see cref="Speed"/>. Heights are interpolated between the
    /// nearest map rows (the nearest row next to missing data); the object is again height above 3 mm.
    /// </summary>
    internal sealed record Rectified(AlignmentMap Map, double Speed, double Y0)
    {
        public static Rectified From(AlignmentMap map, SpeedProfile profile)
        {
            double y0 = profile.TravelMm[0], y1 = profile.TravelMm[^1];
            double span = profile.TimeS[^1] - profile.TimeS[0];
            double speed = Math.Max(1e-3, span > 0 ? (y1 - y0) / span : 0);
            int rows = Math.Max(1, (int)Math.Ceiling((y1 - y0) / RectifiedRowMm));
            int columns = map.Columns;
            var height = new float[rows * columns];
            var isObject = new bool[rows * columns];
            for (int k = 0; k < rows; k++)
            {
                double t = TimeAt(profile, y0 + (k + 0.5) * RectifiedRowMm);
                double v = Math.Clamp(t / map.RowS - 0.5, 0, Math.Max(0, map.Rows - 1));
                int r0 = (int)v, r1 = Math.Min(r0 + 1, map.Rows - 1);
                float f = (float)(v - r0);
                for (int c = 0; c < columns; c++)
                {
                    float a = map.At(c, r0), b = map.At(c, r1);
                    float h = float.IsNaN(a) || float.IsNaN(b) ? (f < 0.5f ? a : b) : a + f * (b - a);
                    height[k * columns + c] = h;
                    isObject[k * columns + c] = h > AlignmentMapBuilder.ObjectMm;
                }
            }
            return new Rectified(new AlignmentMap(columns, rows, map.XMinMm, map.ColumnMm, RectifiedRowMm / speed, height, isObject),
                speed, y0);
        }

        /// <summary>The first moving time at which the monotone travel reaches <paramref name="y"/>.</summary>
        private static double TimeAt(SpeedProfile p, double y)
        {
            var t = p.TimeS;
            var d = p.TravelMm;
            if (y <= d[0]) return t[0];
            if (y >= d[^1]) return t[^1];
            int lo = 0, hi = d.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (d[mid] < y) lo = mid;
                else hi = mid;
            }
            return d[hi] > d[lo] ? t[lo] + (y - d[lo]) / (d[hi] - d[lo]) * (t[hi] - t[lo]) : t[hi];
        }
    }
}
