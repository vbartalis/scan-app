namespace ScanApp.Core.Combine;

/// <summary>Sanity checks on each pass's fitted table, across the passes of one combine.</summary>
public static class TableCheck
{
    /// <summary>The fewest table cells a fit can be trusted on; PCB passes have 4–41 %.</summary>
    public const double MinTableFraction = 0.02;
    /// <summary>The most a table may rise or fall along travel over the pass (|C|·duration); real ≈ 0, wrong fits 5–10 mm.</summary>
    public const double MaxAlongTravelMm = 2;
    /// <summary>The most a pass's cross-X slope may differ from the other passes' median; real ≤ 0.006.</summary>
    public const double MaxCrossXDiffPerMm = 0.01;

    /// <summary>
    /// One verdict per map, in order; true = "Table uncertain". A map is uncertain when it has no
    /// <see cref="AlignmentMap.Table"/>, when under <see cref="MinTableFraction"/> of its cells are table, when its table
    /// slopes more than <see cref="MaxAlongTravelMm"/> over the pass, or when its slope across X lies more than
    /// <see cref="MaxCrossXDiffPerMm"/> from the median of the other maps' (all passes ride the same rail, so they see
    /// the same cross-X tilt). Two disagreeing passes are both uncertain: neither can be told right.
    /// </summary>
    public static bool[] Uncertain(IReadOnlyList<AlignmentMap> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var verdicts = new bool[maps.Count];
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].Table is not { } table)
            {
                verdicts[i] = true;
                continue;
            }
            var others = maps.Where((m, j) => j != i && m.Table is not null).Select(m => m.Table!.B).Order().ToArray();
            verdicts[i] = table.TableFraction < MinTableFraction
                || Math.Abs(table.C) * maps[i].DurationS > MaxAlongTravelMm
                || others.Length > 0 && Math.Abs(table.B - Median(others)) > MaxCrossXDiffPerMm;
        }
        return verdicts;
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
}
