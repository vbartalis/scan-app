namespace ScanApp.Core.Viewing;

/// <summary>
/// Row-major Z, NaN = no points in the cell. Row r covers profiles [r*ProfilesPerRow, (r+1)*ProfilesPerRow).
/// RowStartTimeS is the time of each row's first profile, in seconds since the pass's first profile.
/// </summary>
public sealed record HeightGrid(int Columns, int Rows, float XMin, float XMax, int ProfilesPerRow,
                                double[] RowStartTimeS, float[] Z)
{
    public float CellAt(int column, int row) => Z[row * Columns + column];

    /// <summary>The X in mm at the centre of a column (fractional columns interpolate).</summary>
    public double ColumnToX(double column) => XMin + (column + 0.5) / Columns * (XMax - XMin);
}
