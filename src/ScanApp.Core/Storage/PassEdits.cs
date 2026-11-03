using System.Text.Json.Serialization;
using ScanApp.Core.Cleanup;

namespace ScanApp.Core.Storage;

/// <summary>
/// Per-pass edits, stored beside the raw pass as <c>pass-NNN.edit.json</c>: trim overrides (a null bound means "use the
/// automatic trim") and the zone of interest (<see cref="PassZone"/>, a null limit means "no limit"). A file from
/// before the zone reads with no zone, and one from before the switch reads with the reflection filter on. The raw pass files are never touched.
/// </summary>
public sealed record PassEdits(int? TrimStart, int? TrimEnd,
                               double? XMin = null, double? XMax = null, double? HeightMin = null, double? HeightMax = null,
                               bool? ReflectionFilter = null)
{
    public static readonly PassEdits None = new(null, null);

    [JsonIgnore]
    public PassZone Zone => new(XMin, XMax, HeightMin, HeightMax);

    /// <summary>Whether the reflection filter applies to this pass: on unless switched off (null, from older files, is on).</summary>
    [JsonIgnore]
    public bool FiltersReflections => ReflectionFilter != false;

    public PassEdits WithZone(PassZone zone) =>
        this with { XMin = zone.XMin, XMax = zone.XMax, HeightMin = zone.HeightMin, HeightMax = zone.HeightMax };

    /// <summary>The automatic trims, the zone kept ("Auto").</summary>
    public PassEdits WithoutTrim() => this with { TrimStart = null, TrimEnd = null };
}
