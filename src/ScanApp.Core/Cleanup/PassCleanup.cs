using ScanApp.Core.Combine;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Cleanup;

/// <summary>
/// How to clean one pass for viewing: keep only <c>Trim</c>, drop the profiles of <c>Pauses</c> (they take no travel time),
/// and optionally reverse the travel for a camera-back pass. <see cref="Zone"/> drops the points outside the zone of
/// interest; its height band applies only once <see cref="Table"/> is set by whoever has fitted the pass's table.
/// </summary>
public sealed record PassCleanup(ProfileRange Trim, IReadOnlyList<ProfileRange> Pauses, bool Reverse)
{
    public PassZone Zone { get; init; } = PassZone.None;

    /// <summary>The pass's table in its raw Z (map time); the height band is measured from it. Null = not fitted yet.</summary>
    public TablePlane? Table { get; init; }

    /// <summary>The reflection spikes to drop (<see cref="ReflectionFilter.Find"/>), in every walk of the pass.</summary>
    public ReflectionMask Reflections { get; init; } = ReflectionMask.None;

    /// <summary>Edits override the automatic trim and give the zone; the analysis pauses are clipped to the resulting trim.</summary>
    public static PassCleanup From(MotionAnalysis analysis, PassEdits edits, bool reverse)
    {
        var trim = new ProfileRange(edits.TrimStart ?? analysis.AutoTrim.Start, edits.TrimEnd ?? analysis.AutoTrim.End);
        var pauses = new List<ProfileRange>();
        foreach (var p in analysis.Pauses)
        {
            int start = Math.Max(p.Start, trim.Start), end = Math.Min(p.End, trim.End);
            if (start <= end) pauses.Add(new ProfileRange(start, end));
        }
        return new PassCleanup(trim, pauses, reverse) { Zone = edits.Zone };
    }
}
