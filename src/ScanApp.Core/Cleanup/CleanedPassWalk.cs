using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Cleanup;

/// <summary>
/// How the builders walk a pass through its <see cref="PassCleanup"/> (null = the raw pass): the kept profiles are
/// those inside the trim and outside every pause, and time is moving time from the first kept profile, where each
/// pause removes its span (from its first profile to the next kept profile). Reversal is left to the caller.
/// With <c>applyZone</c>, the walk drops the points outside the cleanup's <see cref="PassCleanup.Zone"/>: the X range
/// always, the height band only when <see cref="PassCleanup.Table"/> is set. In every mode, the walk drops the points
/// of the cleanup's <see cref="PassCleanup.Reflections"/>.
/// </summary>
internal sealed class CleanedPassWalk
{
    private const int XRangeSamples = 300;

    private readonly PassZone _zone;
    private readonly bool _band;
    private readonly bool _applyZone;
    private readonly PassCleanup? _cleanup;
    private readonly ReflectionMask _reflections;

    private CleanedPassWalk(int[] kept, PassCleanup? cleanup = null, bool applyZone = false)
    {
        Kept = kept;
        _cleanup = cleanup;
        _zone = cleanup?.Zone ?? PassZone.None;
        _band = _zone.HasBand && cleanup?.Table is not null;
        _applyZone = applyZone && (_zone.HasXRange || _band);
        _reflections = cleanup?.Reflections ?? ReflectionMask.None;
    }

    /// <summary>The raw indices of the kept profiles, ascending.</summary>
    public int[] Kept { get; }

    public static CleanedPassWalk Create(int totalProfiles, PassCleanup? cleanup, bool applyZone = true)
    {
        if (cleanup is null) return new CleanedPassWalk(Enumerable.Range(0, totalProfiles).ToArray());
        int start = Math.Max(0, cleanup.Trim.Start), end = Math.Min(totalProfiles - 1, cleanup.Trim.End);
        var pauses = cleanup.Pauses.OrderBy(r => r.Start).ToArray();
        var kept = new List<int>(Math.Max(0, end - start + 1));
        for (int i = start; i <= end; i++)
            if (!IsInPause(pauses, i)) kept.Add(i);
        return new CleanedPassWalk(kept.ToArray(), cleanup, applyZone);
    }

    private static bool IsInPause(ProfileRange[] pauses, int index)
    {
        foreach (var r in pauses)
            if (index >= r.Start && index <= r.End) return true;
        return false;
    }

    /// <summary>Moving time of the last kept profile; needs at least one kept profile.</summary>
    public double Duration(PassReader reader)
    {
        double total = (reader.ReadAt(Kept[^1]).TimestampNs - reader.ReadAt(Kept[0]).TimestampNs) / 1e9;
        for (int i = 1; i < Kept.Length; i++)
            if (Kept[i] != Kept[i - 1] + 1)
                total -= (reader.ReadAt(Kept[i]).TimestampNs - reader.ReadAt(Kept[i - 1] + 1).TimestampNs) / 1e9;
        return total;
    }

    /// <summary>The X range of about 300 evenly spaced kept profiles (valid points only); (0, 0) when there are none.</summary>
    public (float Min, float Max) SampleXRange(PassReader reader, CancellationToken ct)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        int n = Math.Min(Kept.Length, XRangeSamples);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            int at = n == 1 ? 0 : (int)((long)i * (Kept.Length - 1) / (n - 1));
            var p = reader.ReadAt(Kept[at]);
            for (int j = 0; j < p.X.Length; j++)
            {
                if (float.IsNaN(p.X[j]) || float.IsNaN(p.Z[j])) continue;
                if (p.X[j] < min) min = p.X[j];
                if (p.X[j] > max) max = p.X[j];
            }
        }
        return min <= max ? (min, max) : (0f, 0f);
    }

    /// <summary>
    /// One sequential read: each kept profile with its ordinal among the kept ones and its moving time. When the zone
    /// applies, a profile comes as a copy with its out-of-zone points NaN in X and Z, and likewise with its masked
    /// reflection points; the reader's arrays are unchanged.
    /// </summary>
    public IEnumerable<(Profile Profile, int Ordinal, double TimeS)> Walk(PassReader reader, CancellationToken ct)
    {
        double duration = _applyZone && _band && _cleanup!.Reverse && Kept.Length > 0 ? Duration(reader) : 0;
        long firstNs = 0, pauseStartNs = 0;
        bool inPause = false;
        double offset = 0;
        int ordinal = 0, raw = 0;
        foreach (var p in reader.ReadAll())
        {
            ct.ThrowIfCancellationRequested();
            int at = raw++;
            if (ordinal >= Kept.Length) yield break;
            if (at < Kept[ordinal])
            {
                if (ordinal > 0 && !inPause) { inPause = true; pauseStartNs = p.TimestampNs; }
                continue;
            }
            if (ordinal == 0) firstNs = p.TimestampNs;
            else if (inPause) offset += (p.TimestampNs - pauseStartNs) / 1e9;
            inPause = false;
            double moving = (p.TimestampNs - firstNs) / 1e9 - offset;
            var shown = _applyZone ? Zoned(p, _cleanup!.Reverse ? duration - moving : moving) : p;
            if (_reflections.TryGetPoints(at, out var masked)) shown = Masked(shown, masked, copied: _applyZone);
            yield return (shown, ordinal, moving);
            ordinal++;
        }
    }

    /// <summary>The profile with the given points NaN in X and Z; a copy unless <paramref name="copied"/> says it already is one.</summary>
    private static Profile Masked(Profile p, int[] points, bool copied)
    {
        var x = copied ? p.X : (float[])p.X.Clone();
        var z = copied ? p.Z : (float[])p.Z.Clone();
        foreach (int j in points)
        {
            x[j] = float.NaN;
            z[j] = float.NaN;
        }
        return copied ? p : new Profile(p.Sequence, p.TimestampNs, x, z, p.Reflectance);
    }

    /// <summary>A copy of the profile with the points outside the zone NaN; a point's height is Z above the table at map time <paramref name="t"/>.</summary>
    private Profile Zoned(Profile p, double t)
    {
        var x = (float[])p.X.Clone();
        var z = (float[])p.Z.Clone();
        var table = _cleanup!.Table;
        for (int j = 0; j < x.Length; j++)
        {
            if (float.IsNaN(x[j]) || float.IsNaN(z[j])) continue;
            if (_zone.InX(x[j]) && (!_band || _zone.InBand(z[j] - table!.At(x[j], t)))) continue;
            x[j] = float.NaN;
            z[j] = float.NaN;
        }
        return new Profile(p.Sequence, p.TimestampNs, x, z, p.Reflectance);
    }
}
