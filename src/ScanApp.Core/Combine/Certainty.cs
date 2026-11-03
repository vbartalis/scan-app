using static ScanApp.Core.Combine.PairScorer;

namespace ScanApp.Core.Combine;

/// <summary>
/// Whether an alignment can be trusted, judged on features rather than on the overall score, which a big flat object
/// dilutes: two passes compare the best pose and its runner-up on edge cells only (<see cref="FeatureRatio"/>); three
/// or more check that the pairwise alignments agree around each triangle through the reference (<see cref="Closes"/>).
/// </summary>
public static class Certainty
{
    /// <summary>
    /// A two-pass alignment is certain when its runner-up's feature score is at least this times its own. On the PCB
    /// (2026-11-01) correct pairs give 0.92–1.30 and a symmetric box 1.03, so no value separates them; it stays on the
    /// safe side.
    /// </summary>
    public const double MinFeatureRatio = 1.2;

    /// <summary>How far a cycle may miss and still close: degrees, and mm where pass B's object centre lands.</summary>
    public const double CycleDeg = 5, CycleMm = 5;

    private const double TinyScore = 1e-6;
    private const int CentreSamples = 2000;

    /// <summary>
    /// <see cref="PairScorer.FeatureScore"/> of <paramref name="best"/>'s <see cref="PairAlignment.RunnerUp"/> over that
    /// of <paramref name="best"/>; 1 when there is no runner-up (no evidence of a clear win).
    /// </summary>
    public static double FeatureRatio(AlignmentMap reference, AlignmentMap moving, PairAlignment best)
    {
        if (best.RunnerUp is null) return 1;
        var scorer = new PairScorer(reference, moving);
        return scorer.FeatureScore(best.RunnerUp) / Math.Max(scorer.FeatureScore(best), TinyScore);
    }

    /// <summary>
    /// True when going B → A → R (<paramref name="aToB"/>, B onto A, then <paramref name="refToA"/>, A onto R) agrees
    /// with going B → R directly (<paramref name="refToB"/>): the rotations within <see cref="CycleDeg"/> and, for the
    /// object centre of <paramref name="b"/> (B's map), the landing points within <see cref="CycleMm"/>. Points pass
    /// between the pairs as (x, moving time), each pair's time axis scaled by that pair's own speed for the pass, so a
    /// pair that knows only a speed ratio (same orientation) still composes. The landing points are compared in R's
    /// time scaled by <paramref name="refToB"/>'s reference speed.
    /// </summary>
    public static bool Closes(PairAlignment refToA, PairAlignment refToB, PairAlignment aToB, AlignmentMap b)
    {
        double miss = Math.Abs(Math.IEEERemainder(refToA.ThetaRad + aToB.ThetaRad - refToB.ThetaRad, 2 * Math.PI));
        if (miss > CycleDeg * Math.PI / 180) return false;

        var centre = ScoreSamples.From(b, CentreSamples);
        double x = centre.CentreX, t = centre.CentreT;
        var (dx, dy) = refToB.ToReference(x, refToB.MovingSpeed * t);
        var (ax, ay) = aToB.ToReference(x, aToB.MovingSpeed * t);
        var (vx, vy) = refToA.ToReference(ax, refToA.MovingSpeed * (ay / aToB.ReferenceSpeed));
        double dt = dy / refToB.ReferenceSpeed - vy / refToA.ReferenceSpeed;
        return Math.Sqrt((dx - vx) * (dx - vx) + Math.Pow(dt * refToB.ReferenceSpeed, 2)) <= CycleMm;
    }
}
