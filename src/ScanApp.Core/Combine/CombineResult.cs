namespace ScanApp.Core.Combine;

/// <summary>Where a pass sits in the combined frame: a rotation about Z and a translation in X and Y.</summary>
public sealed record PassPose(double ThetaRad, double Tx, double Ty);

/// <summary>Estimated push travel over time: monotone samples, travel(t) by linear interpolation.</summary>
public sealed record SpeedProfile(double[] TimeS, double[] TravelMm)
{
    /// <summary>Travel at moving time <paramref name="timeS"/>, linear between samples and held beyond the ends.</summary>
    public double TravelAt(double timeS)
    {
        if (timeS <= TimeS[0]) return TravelMm[0];
        if (timeS >= TimeS[^1]) return TravelMm[^1];
        int i = Array.BinarySearch(TimeS, timeS);
        if (i >= 0) return TravelMm[i];
        i = ~i;
        double f = (timeS - TimeS[i - 1]) / (TimeS[i] - TimeS[i - 1]);
        return TravelMm[i - 1] + f * (TravelMm[i] - TravelMm[i - 1]);
    }
}

/// <summary>
/// What combining found for one pass. Keeps the pass id even after the pass itself is deleted.
/// <paramref name="TableUncertain"/>: its table fit looks wrong (<see cref="TableCheck"/>); false in older results.
/// <paramref name="ReflectionsRemoved"/>: the points the reflection filter removed; null when the pass has no
/// reflectance, when the filter was switched off for it, and in older results.
/// <paramref name="ReflectionFilterOff"/>: the pass's reflection filter was switched off; false in older results.
/// </summary>
public sealed record PassOutcome(int PassId, PassPose Pose, SpeedProfile Speed,
                                 double MeanSpeedMmPerS, double SpeedRangeFraction, double OverlapMadMm,
                                 bool TableUncertain = false, int? ReflectionsRemoved = null, bool ReflectionFilterOff = false);

/// <summary>
/// The metadata of one combine run, stored as <c>results/result-NNN/result.json</c>.
/// <paramref name="OpenCycles"/>: the pass-id triples (reference, A, B) whose alignments did not close
/// (<see cref="Certainty.Closes"/>); empty when every cycle closed, null with only two passes and in older results.
/// </summary>
public sealed record CombineResult(int Id, DateTimeOffset CreatedAt, int ReferencePassId,
                                   IReadOnlyList<PassOutcome> Passes, bool UncertainAlignment, bool ScaleUnknown,
                                   IReadOnlyList<int[]>? OpenCycles = null);

/// <summary>The merged points of a result. <c>PassIndex</c> indexes <see cref="CombineResult.Passes"/>.</summary>
public sealed record MergedCloud(float[] X, float[] Y, float[] Z, byte[] PassIndex);
