namespace ScanApp.Core.Combine;

/// <summary>Fits a pass's table, the plane its heights are measured from, to a grid of raw-Z cells.</summary>
public static class TableFit
{
    public const double SeedPercentile = 0.02, SeedBandMm = 3, BandMm = 1;
    private const int MinTableCellsPerRow = 10;

    /// <summary>
    /// Raw-Z cells, row-major (rows = <paramref name="rowS"/> of map time, columns = <paramref name="columnMm"/> of X),
    /// NaN = empty. A plane <c>z = a + b·x + c·t</c> is fitted by least squares to the cells below the
    /// <see cref="SeedPercentile"/> height plus <see cref="SeedBandMm"/>, then twice more to the cells within
    /// <see cref="BandMm"/> of the fit. Seeding from the low end finds the table even when an object covers most of
    /// the view. Each row's offset is its median residual within the band (at least 10 cells, else 0).
    /// The cells are not changed. Null when no cell is valid.
    /// </summary>
    public static TablePlane? Fit(float[] cells, int columns, int rows, double xMinMm, double columnMm, double rowS, double durationS)
    {
        var valid = cells.Where(z => !float.IsNaN(z)).ToArray();
        if (valid.Length == 0) return null;
        Array.Sort(valid);
        double seed = valid[(int)(valid.Length * SeedPercentile)];

        // Centred coordinates keep the normal equations well conditioned.
        double xMid = xMinMm + columns * columnMm / 2, tMid = durationS / 2;
        double X(int c) => xMinMm + (c + 0.5) * columnMm - xMid;
        double T(int r) => (r + 0.5) * rowS - tMid;

        double a = seed, b = 0, slope = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            var m = new double[3, 3];
            var v = new double[3];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                {
                    double z = cells[r * columns + c];
                    if (double.IsNaN(z)) continue;
                    bool table = pass == 0 ? z < seed + SeedBandMm : Math.Abs(z - (a + b * X(c) + slope * T(r))) < BandMm;
                    if (!table) continue;
                    Span<double> f = [1, X(c), T(r)];
                    for (int i = 0; i < 3; i++)
                    {
                        v[i] += f[i] * z;
                        for (int j = 0; j < 3; j++) m[i, j] += f[i] * f[j];
                    }
                }
            if (Solve(m, v) is { } s) (a, b, slope) = (s[0], s[1], s[2]);
        }

        var rowValues = new List<float>();
        var rowOffsets = new float[rows];
        int tableCells = 0;
        for (int r = 0; r < rows; r++)
        {
            rowValues.Clear();
            for (int c = 0; c < columns; c++)
            {
                float z = cells[r * columns + c];
                if (float.IsNaN(z)) continue;
                float res = (float)(z - (a + b * X(c) + slope * T(r)));
                if (Math.Abs(res) < BandMm) rowValues.Add(res);
            }
            if (rowValues.Count >= MinTableCellsPerRow)
            {
                rowValues.Sort();
                rowOffsets[r] = rowValues[rowValues.Count / 2];
            }
            for (int c = 0; c < columns; c++)
            {
                float z = cells[r * columns + c];
                if (!float.IsNaN(z) && Math.Abs(z - (a + b * X(c) + slope * T(r)) - rowOffsets[r]) < BandMm) tableCells++;
            }
        }
        return new TablePlane(a, b, slope, xMid, tMid, rowS, rowOffsets) { TableFraction = tableCells / (double)valid.Length };
    }

    /// <summary>Solves a 3 x 3 system by Gaussian elimination with pivoting; null when it is singular.</summary>
    private static double[]? Solve(double[,] m, double[] v)
    {
        for (int i = 0; i < 3; i++)
        {
            int pivot = i;
            for (int j = i + 1; j < 3; j++)
                if (Math.Abs(m[j, i]) > Math.Abs(m[pivot, i])) pivot = j;
            if (Math.Abs(m[pivot, i]) < 1e-9) return null;
            for (int k = 0; k < 3; k++) (m[i, k], m[pivot, k]) = (m[pivot, k], m[i, k]);
            (v[i], v[pivot]) = (v[pivot], v[i]);
            for (int j = i + 1; j < 3; j++)
            {
                double f = m[j, i] / m[i, i];
                for (int k = i; k < 3; k++) m[j, k] -= f * m[i, k];
                v[j] -= f * v[i];
            }
        }
        var x = new double[3];
        for (int i = 2; i >= 0; i--)
        {
            double s = v[i];
            for (int k = i + 1; k < 3; k++) s -= m[i, k] * x[k];
            x[i] = s / m[i, i];
        }
        return x;
    }
}
