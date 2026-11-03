using System.Diagnostics;
using ScanApp.Core.Combine;
using Xunit.Abstractions;

namespace ScanApp.Core.Tests.Combine;

[Trait("Category", "Slow")]
[Collection(SlowCombineCollection.Name)]
public class PairAlignerTests(ITestOutputHelper output)
{
    private const double RefSpeed = 34, MovSpeed = 26;
    private const double LineWidthMm = 200, RateHz = 200;
    private const int Points = 201;   // 1 mm apart, one point per map column
    private static readonly double Theta45 = 45 * Math.PI / 180;
    private const double Tx = 30, Ty = -20;

    // The object sits at (-10, 60) in the reference frame: x -40..20, y 30..90.
    private static double World(double x, double y) => Object(x + 10, y - 60);

    // An L-shaped block 10 mm high (a 60 x 20 bar and a 20 x 60 upright) with an 8 mm cylinder (r 7) on the upright.
    private static double Object(double u, double v)
    {
        bool bar = u is >= -30 and <= 30 && v is >= -30 and <= -10;
        bool upright = u is >= -30 and <= -10 && v is >= -30 and <= 30;
        if (!bar && !upright) return 0;
        double du = u + 20, dv = v - 15;
        return du * du + dv * dv <= 49 ? 18 : 10;
    }

    // What a pass at pose (theta, tx, ty) sees: its point p lies at R(theta) p + (tx, ty) in the reference frame.
    private static Func<double, double, double> Seen(double theta, double tx, double ty) => Seen(World, theta, tx, ty);

    private static Func<double, double, double> Seen(Func<double, double, double> world, double theta, double tx, double ty) =>
        (x, y) => world(Math.Cos(theta) * x - Math.Sin(theta) * y + tx, Math.Sin(theta) * x + Math.Cos(theta) * y + ty);

    private static AlignmentMap Map(Func<double, double, double> height, double speed, double lengthMm) =>
        SimulatedPasses.Map(height, speed, lengthMm, LineWidthMm, Points, RateHz);

    private static readonly Lazy<AlignmentMap> Reference = new(() => Map(World, RefSpeed, 125));
    private static readonly Lazy<AlignmentMap> Rotated = new(() => Map(Seen(Theta45, Tx, Ty), MovSpeed, 150));

    private static double AngleError(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) * 180 / Math.PI;

    // Each scenario is aligned once and shared by the tests that check it.
    private sealed record Run(PairAlignment Result, long Milliseconds, double ScoreAtTruth);

    private static Lazy<Run> Aligned(Func<(AlignmentMap Reference, AlignmentMap Moving)> maps, double theta, double sRef,
                                     double sMov, double tx, double ty) => new(() =>
    {
        var (reference, moving) = maps();
        var sw = Stopwatch.StartNew();
        var result = PairAligner.Align(reference, moving, null, CancellationToken.None);
        return new Run(result, sw.ElapsedMilliseconds, PairAligner.Score(reference, moving, theta, sRef, sMov, tx, ty));
    });

    private const double PlateauWidthMm = 280, PlateauTx = 20, PlateauTy = 60;
    private const double BoardTx = 20, BoardTy = 90;
    private static readonly double Theta30 = 30 * Math.PI / 180;

    private static readonly Lazy<Run> At45 = Aligned(() => (Reference.Value, Rotated.Value), Theta45, RefSpeed, MovSpeed, Tx, Ty);
    private static readonly Lazy<Run> Partial = Aligned(() =>
    {
        var seen = Seen(Theta45, Tx, Ty);
        return (Reference.Value, Map(seen, MovSpeed, CutForCoverage(seen, 0.6)));
    }, Theta45, RefSpeed, MovSpeed, Tx, Ty);
    private static readonly Lazy<Run> Same = Aligned(() => (Reference.Value, Map(Seen(0, 15, -10), MovSpeed, 125)),
        0, RefSpeed, MovSpeed, 15, -10);
    private static readonly Lazy<Run> OnPlateau = Aligned(() =>
        (SimulatedPasses.Map(Plateau, RefSpeed, 120, PlateauWidthMm, (int)PlateauWidthMm + 1, RateHz),
         SimulatedPasses.Map(Seen(Plateau, Theta30, PlateauTx, PlateauTy), MovSpeed, 120, PlateauWidthMm,
             (int)PlateauWidthMm + 1, RateHz)), Theta30, RefSpeed, MovSpeed, PlateauTx, PlateauTy);
    private static readonly Lazy<Run> OnBoard = Aligned(() =>
        (Map(Board, RefSpeed, 150), Map(Seen(Board, Theta30, BoardTx, BoardTy), MovSpeed, 150)),
        Theta30, RefSpeed, MovSpeed, BoardTx, BoardTy);
    private static readonly Lazy<Run> OfBox = Aligned(() =>
        (Map(Box, RefSpeed, 125), Map(Seen(Box, Theta45, Tx, Ty), MovSpeed, 150)), Theta45, RefSpeed, MovSpeed, Tx, Ty);
    private static readonly Lazy<Run> OfSquare = Aligned(() =>
        (Map(Square, RefSpeed, 125), Map(Seen(Square, Theta45, Tx, Ty), MovSpeed, 150)), Theta45, RefSpeed, MovSpeed, Tx, Ty);

    private static double Box(double x, double y) => Math.Abs(x + 10) <= 30 && Math.Abs(y - 60) <= 15 ? 10 : 0;

    private static double Square(double x, double y) => Math.Abs(x + 10) <= 20 && Math.Abs(y - 60) <= 20 ? 10 : 0;

    // A 1.6 mm board (x -70..70, y -20..220), below the object threshold, with twelve small seeded components
    // (1.5-4.5 mm half sizes, 3.5-9.5 mm tall) as the only object; the passes share only part of it.
    private static readonly (double X, double Y, double HalfW, double HalfH, double Height)[] Parts = MakeParts();

    private static (double, double, double, double, double)[] MakeParts()
    {
        var random = new Random(2);
        return [.. Enumerable.Range(0, 12).Select(_ => (-65 + 130 * random.NextDouble(), -15 + 230 * random.NextDouble(),
            1.5 + 3 * random.NextDouble(), 1.5 + 3 * random.NextDouble(), 3.5 + 6 * random.NextDouble()))];
    }

    private static double Board(double x, double y)
    {
        if (Math.Abs(x) > 70 || y < -20 || y > 220) return 0;
        foreach (var (px, py, hw, hh, h) in Parts)
            if (Math.Abs(x - px) <= hw && Math.Abs(y - py) <= hh) return 1.6 + h;
        return 1.6;
    }

    private void Show(string name, Run run) =>
        output.WriteLine($"{name} took {run.Milliseconds} ms: {run.Result}; score at truth {run.ScoreAtTruth:F4}");

    private static void AssertLocked(PairAlignment a, double theta, double tx, double ty, double speedTolerance, double offsetMm)
    {
        Assert.InRange(AngleError(a.ThetaRad, theta), 0, 1);
        Assert.InRange(a.ReferenceSpeed / RefSpeed, 1 - speedTolerance, 1 + speedTolerance);
        Assert.InRange(a.MovingSpeed / MovSpeed, 1 - speedTolerance, 1 + speedTolerance);
        Assert.InRange(a.Tx, tx - offsetMm, tx + offsetMm);
        Assert.InRange(a.Ty, ty - offsetMm, ty + offsetMm);
    }

    [Fact]
    public void Align_RecoversRotationSpeedsAndOffset()
    {
        var run = At45.Value;
        Show("45 deg", run);

        AssertLocked(run.Result, Theta45, Tx, Ty, 0.03, 2);
        Assert.True(run.Result.RunnerUpRatio > 1.5, $"runner-up ratio {run.Result.RunnerUpRatio}");
    }

    [Fact]
    public void Align_PartialOverlap_StillLocks()
    {
        Show("partial", Partial.Value);

        AssertLocked(Partial.Value.Result, Theta45, Tx, Ty, 0.03, 2);
    }

    [Fact]
    public void Align_SameRotation_GivesOnlyARatio()
    {
        var a = Same.Value.Result;
        Show("same rotation", Same.Value);

        Assert.InRange(AngleError(a.ThetaRad, 0), 0, 1);
        Assert.InRange(a.MovingSpeed / a.ReferenceSpeed / (MovSpeed / RefSpeed), 0.97, 1.03);
    }

    [Fact]
    public void Align_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PairAligner.Align(Reference.Value, Rotated.Value, null, cts.Token));
    }

    // An 8 mm plateau, 120 x 160 mm (x -60..60, y -20..140), wider than both passes see, with eight small features.
    private static double Plateau(double x, double y)
    {
        if (Math.Abs(x) > 60 || y < -20 || y > 140) return 0;
        (double X, double Y, bool Square)[] features =
            [(-40, 70, true), (0, 65, false), (40, 90, true), (50, 110, false),
             (-20, 95, false), (20, 105, true), (-50, 110, true), (10, 85, false)];
        foreach (var (fx, fy, square) in features)
        {
            double dx = x - fx, dy = y - fy;
            if (square ? Math.Abs(dx) <= 4 && Math.Abs(dy) <= 4 : dx * dx + dy * dy <= 16) return square ? 13 : 12;
        }
        return 8;
    }

    [Fact]
    public void Align_UniformPlateau_PartialOverlap_StillLocks()
    {
        var run = OnPlateau.Value;
        Show("plateau", run);

        AssertLocked(run.Result, Theta30, PlateauTx, PlateauTy, 0.05, 3);
        Assert.True(run.Result.RunnerUpRatio > 1.5, $"runner-up ratio {run.Result.RunnerUpRatio}");
    }

    [Fact]
    public void Align_FlatBoardWithComponents_PartialOverlap_StillLocks()
    {
        var run = OnBoard.Value;
        Show("board", run);

        AssertLocked(run.Result, Theta30, BoardTx, BoardTy, 0.05, 3);
    }

    [Fact]
    public void Align_SymmetricObjects_AreUncertain()
    {
        Show("box", OfBox.Value);
        Show("square", OfSquare.Value);

        Assert.True(OfBox.Value.Result.RunnerUpRatio < 1.5, $"box runner-up ratio {OfBox.Value.Result.RunnerUpRatio}");
        Assert.True(OfSquare.Value.Result.RunnerUpRatio < 1.5, $"square runner-up ratio {OfSquare.Value.Result.RunnerUpRatio}");
    }

    [Fact]
    public void Align_RunnerUpRatio_IsAtLeastOne()
    {
        foreach (var run in new[] { At45, Partial, Same, OnPlateau, OnBoard, OfBox, OfSquare })
            Assert.True(run.Value.Result.RunnerUpRatio >= 1, $"runner-up ratio {run.Value.Result}");
    }

    [Fact]
    public void Align_ReportsTheRunnerUpPose()
    {
        var best = At45.Value.Result;
        var rival = best.RunnerUp;

        Assert.NotNull(rival);
        output.WriteLine($"runner-up {rival}");
        // Clearly different: more than 10° apart, or the moving object's centre lands more than 10 mm away.
        var moving = Rotated.Value;
        double cx = 0, ct = 0;
        int n = 0;
        for (int r = 0; r < moving.Rows; r++)
            for (int c = 0; c < moving.Columns; c++)
                if (moving.IsObject[r * moving.Columns + c])
                    (cx, ct, n) = (cx + moving.XMinMm + (c + 0.5) * moving.ColumnMm, ct + (r + 0.5) * moving.RowS, n + 1);
        (cx, ct) = (cx / n, ct / n);
        var (bx, by) = best.ToReference(cx, best.MovingSpeed * ct);
        var (rx, ry) = rival.ToReference(cx, rival.MovingSpeed * ct);
        double apartMm = Math.Sqrt(Math.Pow(bx - rx, 2) + Math.Pow((by / best.ReferenceSpeed - ry / rival.ReferenceSpeed) * best.ReferenceSpeed, 2));
        Assert.True(AngleError(best.ThetaRad, rival.ThetaRad) > 10 || apartMm > 10, $"rival {rival}, {apartMm:F1} mm apart");
        Assert.Equal(best.RunnerUpRatio, rival.Score / Math.Max(best.Score, 1e-6), 1e-9);
    }

    [Fact]
    public void Align_CancelledMidRun_StopsPromptly()
    {
        using var cts = new CancellationTokenSource();
        var sw = new Stopwatch();
        var progress = new InlineProgress(v =>
        {
            if (v > 0 && !cts.IsCancellationRequested) { cts.Cancel(); sw.Start(); }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => PairAligner.Align(Reference.Value, Rotated.Value, progress, cts.Token));
        Assert.True(cts.IsCancellationRequested);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 2000);
    }

    [Fact]
    public void PairScorer_MatchesStaticScore()
    {
        var scorer = new PairScorer(Reference.Value, Rotated.Value);

        Assert.Equal(PairAligner.Score(Reference.Value, Rotated.Value, Theta45, RefSpeed, MovSpeed, Tx + 3, Ty),
            scorer.Score(Theta45, RefSpeed, MovSpeed, Tx + 3, Ty), 12);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public void Align_NoObject_Throws()
    {
        var flat = Map((_, _) => 0, RefSpeed, 60);

        Assert.Throws<ArgumentException>(() => PairAligner.Align(Reference.Value, flat, null, CancellationToken.None));
    }

    [Fact]
    public void Score_IsSymmetricAndCapped()
    {
        var r = Reference.Value;
        var m = Rotated.Value;
        var truth = new PairAlignment(Theta45, RefSpeed, MovSpeed, Tx, Ty, 0, 0);
        var inverse = truth.Inverse();

        double atTruth = PairAligner.Score(r, m, Theta45, RefSpeed, MovSpeed, Tx, Ty);
        double swapped = PairAligner.Score(m, r, inverse.ThetaRad, inverse.ReferenceSpeed, inverse.MovingSpeed,
            inverse.Tx, inverse.Ty);

        Assert.Equal(atTruth, swapped, 9);
        Assert.InRange(atTruth, 0, 0.2);
        Assert.True(PairAligner.Score(r, m, Theta45, RefSpeed, MovSpeed, Tx + 15, Ty) > 3 * atTruth);
        Assert.Equal(0, PairAligner.Score(r, r, 0, RefSpeed, RefSpeed, 0, 0), 6);

        // No overlap is as bad as the worst mismatch, never better: tiny overlaps are not rewarded.
        Assert.Equal(2, PairAligner.Score(r, m, Theta45, RefSpeed, MovSpeed, Tx + 1000, Ty), 9);
        var random = new Random(3);
        for (int i = 0; i < 50; i++)
        {
            double s = PairAligner.Score(r, m, random.NextDouble() * 2 * Math.PI, 5 + 55 * random.NextDouble(),
                5 + 55 * random.NextDouble(), random.NextDouble() * 200 - 100, random.NextDouble() * 200 - 100);
            Assert.InRange(s, 0, 2);
        }
    }

    [Fact]
    public void Inverse_MapsReferencePointsBack()
    {
        var pose = new PairAlignment(0.7, 30, 20, 12, -8, 0, 0);
        var (x, y) = pose.ToReference(5, 40);

        var (bx, by) = pose.Inverse().ToReference(x, y);

        Assert.Equal(5, bx, 9);
        Assert.Equal(40, by, 9);
        Assert.Equal((pose.ReferenceSpeed, pose.MovingSpeed), (pose.Inverse().MovingSpeed, pose.Inverse().ReferenceSpeed));
    }

    [Fact]
    public void NelderMead_MinimisesRosenbrock()
    {
        static double Rosenbrock(double[] p) => 100 * Math.Pow(p[1] - p[0] * p[0], 2) + Math.Pow(1 - p[0], 2);

        var (x, f) = NelderMead.Minimize(Rosenbrock, [-1.2, 1], [0.5, 0.5], 5000);

        Assert.InRange(x[0], 0.999, 1.001);
        Assert.InRange(x[1], 0.998, 1.002);
        Assert.InRange(f, 0, 1e-6);
    }

    /// <summary>The pass length (from y = 0) at which the pass sees <paramref name="fraction"/> of the object's area.</summary>
    private static double CutForCoverage(Func<double, double, double> seen, double fraction)
    {
        const double step = 0.5;
        var perRow = new List<int>();
        for (double y = 0; y < 200; y += step)
        {
            int n = 0;
            for (double x = -LineWidthMm / 2; x <= LineWidthMm / 2; x += step)
                if (seen(x, y) > 3) n++;
            perRow.Add(n);
        }
        double total = perRow.Sum(), sum = 0;
        for (int i = 0; i < perRow.Count; i++)
        {
            sum += perRow[i];
            if (sum >= fraction * total) return i * step;
        }
        return perRow.Count * step;
    }
}
