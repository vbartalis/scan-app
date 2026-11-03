using ScanApp.Core.Cleanup;

namespace ScanApp.Core.Viewing;

/// <summary>
/// The six limits and the switch as the raw views test them: trims in raw profile indices (inclusive), X in mm, the
/// height band in mm above the table. A missing limit is ±infinity.
/// </summary>
public sealed record CutLimits(int TrimStart, int TrimEnd, double XMin, double XMax, double HeightMin, double HeightMax,
                               bool FilterReflections)
{
    public static CutLimits From(ProfileRange trim, PassZone zone, bool filterReflections)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new(trim.Start, trim.End,
                   zone.XMin ?? double.NegativeInfinity, zone.XMax ?? double.PositiveInfinity,
                   zone.HeightMin ?? double.NegativeInfinity, zone.HeightMax ?? double.PositiveInfinity,
                   filterReflections);
    }
}

/// <summary>What the raw views keep of a pass: the rule they colour by, live while the operator drags a limit.</summary>
public static class RawCut
{
    /// <summary>
    /// Kept = inside the trims, not paused, inside X, inside the band (a NaN height counts as inside), and not a
    /// reflection while the filter is on. Exactly what the cleaned walk and the combine keep.
    /// </summary>
    public static bool Keeps(CutLimits limits, int profile, double x, float heightAboveTable, bool paused, bool reflection)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return profile >= limits.TrimStart && profile <= limits.TrimEnd
            && !paused
            && !(x < limits.XMin) && !(x > limits.XMax)
            && !(heightAboveTable < limits.HeightMin) && !(heightAboveTable > limits.HeightMax)
            && !(reflection && limits.FilterReflections);
    }
}
