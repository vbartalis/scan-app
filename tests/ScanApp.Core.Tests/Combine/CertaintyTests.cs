using ScanApp.Core.Combine;
using ScanApp.Core.Simulation;
using Xunit.Abstractions;

namespace ScanApp.Core.Tests.Combine;

public class CertaintyTests(ITestOutputHelper output)
{
    private const double SpeedR = 34, SpeedA = 26, SpeedB = 30;
    private static readonly double ThetaA = 45 * Math.PI / 180, ThetaB = 120 * Math.PI / 180;
    private const double TxA = 30, TyA = -20, TxB = 10, TyB = 40;

    /// <summary>A 20 × 30-row block 10 mm high in a 60 × 100 map, so pass B has an object centre.</summary>
    private static readonly AlignmentMap B = MakeBlockMap();

    private static AlignmentMap MakeBlockMap()
    {
        const int columns = 60, rows = 100;
        var height = new float[columns * rows];
        var isObject = new bool[columns * rows];
        for (int r = 40; r < 70; r++)
            for (int c = 15; c < 35; c++)
            {
                height[r * columns + c] = 10;
                isObject[r * columns + c] = true;
            }
        return new AlignmentMap(columns, rows, -30, 1, 0.01, height, isObject);
    }

    /// <summary>The pose of a pass at (θ, t) in the reference frame, seen from a pass at (θ0, t0): R(−θ0)(·) composed.</summary>
    private static PairAlignment Relative(double theta0, double tx0, double ty0, double s0, double theta, double tx, double ty, double s)
    {
        double c = Math.Cos(theta0), sn = Math.Sin(theta0);
        double dx = tx - tx0, dy = ty - ty0;
        return new PairAlignment(theta - theta0, s0, s, c * dx + sn * dy, -sn * dx + c * dy, 0, 0);
    }

    private static PairAlignment RefToA => new(ThetaA, SpeedR, SpeedA, TxA, TyA, 0, 0);
    private static PairAlignment RefToB => new(ThetaB, SpeedR, SpeedB, TxB, TyB, 0, 0);
    private static PairAlignment AToB => Relative(ThetaA, TxA, TyA, SpeedA, ThetaB, TxB, TyB, SpeedB);

    [Fact]
    public void Closes_ConsistentTriangle_IsTrue()
    {
        Assert.Equal(75, AToB.ThetaRad * 180 / Math.PI, 9);
        Assert.True(Certainty.Closes(RefToA, RefToB, AToB, B));
    }

    [Fact]
    public void Closes_LegOffBy90Degrees_IsFalse()
    {
        var leg = AToB with { ThetaRad = AToB.ThetaRad + Math.PI / 2 };
        Assert.False(Certainty.Closes(RefToA, RefToB, leg, B));
    }

    [Fact]
    public void Closes_LegOffBy10Mm_IsFalse()
    {
        var leg = AToB with { Tx = AToB.Tx + 10 };
        Assert.False(Certainty.Closes(RefToA, RefToB, leg, B));
    }

    [Fact]
    public void Closes_SameOrientationLegWithArbitrarySpeeds_IsTrue()
    {
        // B rides at A's rotation, so the A←B pair fixes only the speed ratio: here both speeds are 1.3 × the truth,
        // and its offset along travel is in that pair's own (stretched) units.
        var refToB = new PairAlignment(ThetaA, SpeedR, SpeedB, TxB, TyB, 0, 0);
        var truth = Relative(ThetaA, TxA, TyA, SpeedA, ThetaA, TxB, TyB, SpeedB);
        var leg = truth with { ReferenceSpeed = 1.3 * SpeedA, MovingSpeed = 1.3 * SpeedB, Ty = 1.3 * truth.Ty };

        Assert.Equal(0, leg.ThetaRad, 9);
        Assert.True(Certainty.Closes(RefToA, refToB, leg, B));
    }

    // The raised board (bare board on ~85 % of the cells), and a pass that sees it 5 mm right and 10 mm on, at the
    // simulator's default noise.
    private const double RateHz = 200, LineWidthMm = 200;
    private const int Points = 201;
    private const double BoardRef = 20, BoardMov = 25, BoardTx = 5, BoardTy = 10;

    private static readonly Lazy<(AlignmentMap Reference, AlignmentMap Moving)> Board = new(() =>
    {
        var board = SimulatedProfileSource.RaisedBoard().HeightAt;
        return (SimulatedPasses.Map(board, BoardRef, 200, LineWidthMm, Points, RateHz),
                SimulatedPasses.Map((x, y) => board(x + BoardTx, y + BoardTy), BoardMov, 180, LineWidthMm, Points, RateHz));
    });

    [Fact]
    public void FeatureScore_RaisedBoardOff15Mm_SeparatesByMinFeatureRatio()
    {
        var (reference, moving) = Board.Value;
        var scorer = new PairScorer(reference, moving);
        var truth = new PairAlignment(0, BoardRef, BoardMov, BoardTx, BoardTy, 0, 0);
        var off = truth with { Tx = truth.Tx + 15 };

        double featureRatio = scorer.FeatureScore(off) / scorer.FeatureScore(truth);
        output.WriteLine($"feature {scorer.FeatureScore(truth):F4} -> {scorer.FeatureScore(off):F4}, ratio {featureRatio:F3}");

        Assert.True(featureRatio >= Certainty.MinFeatureRatio, $"feature ratio {featureRatio:F3}");
    }

    /// <summary>A 10 mm block (with a 10 mm column at one corner) on bare table, padded by <paramref name="pad"/> cells of table each side.</summary>
    private static AlignmentMap BlockOnTable(int blockColumn, int pad)
    {
        const int size = 60;
        int columns = size + 2 * pad, rows = size + pad;
        var height = new float[columns * rows];
        var isObject = new bool[columns * rows];
        for (int r = 20; r < 40; r++)
            for (int c = blockColumn; c < blockColumn + 20; c++)
            {
                int i = r * columns + c + pad;
                height[i] = r < 25 && c < blockColumn + 5 ? 20 : 10;
                isObject[i] = true;
            }
        // 1 mm columns and, at 10 mm/s, 1 mm rows; padding left keeps the columns where they were, padding below is
        // later in time, so every cell keeps its place.
        return new AlignmentMap(columns, rows, -30 - pad, 1, 0.1, height, isObject);
    }

    [Fact]
    public void FeatureScore_MoreBareTable_IsUnchanged()
    {
        // The moving block sits 5 mm left of the reference's, so the truth is Tx = 5; the other pose is 8 mm off.
        var tight = new PairScorer(BlockOnTable(20, 0), BlockOnTable(15, 0));
        var padded = new PairScorer(BlockOnTable(20, 40), BlockOnTable(15, 40));
        var truth = new PairAlignment(0, 10, 10, 5, 0, 0, 0);
        var off = truth with { Tx = 13 };

        foreach (var pose in new[] { truth, off })
            Assert.Equal(tight.FeatureScore(pose), padded.FeatureScore(pose), 12);
        Assert.True(tight.FeatureScore(off) > tight.FeatureScore(truth));
        // The overall score does see the extra table: it pulls the off pose's mean down.
        Assert.True(padded.Score(off) < tight.Score(off), $"score {tight.Score(off):F4} -> {padded.Score(off):F4}");
    }

    [Fact]
    public void FeatureRatio_NoRunnerUp_IsOne()
    {
        var (reference, moving) = Board.Value;
        var best = new PairAlignment(0, BoardRef, BoardMov, BoardTx, BoardTy, 0.1, 1);

        Assert.Null(best.RunnerUp);
        Assert.Equal(1, Certainty.FeatureRatio(reference, moving, best));
    }
}
