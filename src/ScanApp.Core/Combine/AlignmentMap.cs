namespace ScanApp.Core.Combine;

/// <summary>Height above the table, row-major [row][column]; rows = 10 ms of moving time, columns = 1 mm of X. NaN = no data.</summary>
public sealed record AlignmentMap(int Columns, int Rows, double XMinMm, double ColumnMm, double RowS,
                                  float[] Height, bool[] IsObject)
{
    public float At(int column, int row) => Height[row * Columns + column];

    public double DurationS => Rows * RowS;

    /// <summary>The table the heights are measured from, in the pass's raw Z; null on a map not built from a pass.</summary>
    public TablePlane? Table { get; init; }
}

/// <summary>
/// A pass's table in its raw Z: the plane <c>A + B·(x − XMid) + C·(t − TMid)</c> in scanner X and map time, plus each
/// row's offset (scanner shake). Raw Z minus <see cref="At"/> is height above the table.
/// </summary>
public sealed record TablePlane(double A, double B, double C, double XMid, double TMid, double RowS, float[] RowOffsets)
{
    /// <summary>Share of the valid cells within <see cref="TableFit.BandMm"/> of the fitted table, row offsets included.</summary>
    public double TableFraction { get; init; }

    public double At(double x, double t)
    {
        double offset = RowOffsets.Length == 0 ? 0
            : RowOffsets[Math.Clamp((int)Math.Floor(t / RowS + 1e-6), 0, RowOffsets.Length - 1)];
        return A + B * (x - XMid) + C * (t - TMid) + offset;
    }
}
