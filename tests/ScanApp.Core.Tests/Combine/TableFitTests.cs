using ScanApp.Core.Combine;

namespace ScanApp.Core.Tests.Combine;

public class TableFitTests
{
    private const int Columns = 200, Rows = 300;
    private const double XMin = -100, ColumnMm = 1, RowS = 0.01, DurationS = Rows * RowS;

    private static double XOf(int c) => XMin + (c + 0.5) * ColumnMm;

    private static double TableAt(int c) => 133 - 0.012 * XOf(c);

    /// <summary>Raw-Z cells of a tilted table at z = 133 − 0.012·x with 0.02 mm noise; <paramref name="above"/> lifts a cell off it.</summary>
    private static float[] Cells(Func<int, int, double> above)
    {
        var random = new Random(1);
        var cells = new float[Columns * Rows];
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                cells[r * Columns + c] = (float)(TableAt(c) + above(c, r) + 0.02 * (random.NextDouble() - 0.5) * 3.46);
        return cells;
    }

    private static TablePlane Fit(float[] cells)
    {
        var plane = TableFit.Fit(cells, Columns, Rows, XMin, ColumnMm, RowS, DurationS);
        Assert.NotNull(plane);
        return plane;
    }

    private static void AssertTheTable(TablePlane plane)
    {
        Assert.InRange(plane.A, 133 - 0.05, 133 + 0.05);
        Assert.InRange(plane.B, -0.012 - 0.0005, -0.012 + 0.0005);
        Assert.InRange(Math.Abs(plane.C), 0, 0.01);
    }

    [Fact]
    public void Fit_RaisedBoardCoveringMostCells_FindsTheTable()
    {
        var plane = Fit(Cells((_, r) => r is >= 30 and < 285 ? 10 : 0));   // 255 of 300 rows: 85 % board

        AssertTheTable(plane);
        Assert.InRange(plane.TableFraction, 0.13, 0.17);
    }

    [Fact]
    public void Fit_ObjectOnMostlyTable_FindsTheSameTable()
    {
        var plane = Fit(Cells((c, r) => c is >= 60 and < 140 && r is >= 75 and < 225 ? 15 : 0));   // 80 x 150 cells: 20 %

        AssertTheTable(plane);
        Assert.True(plane.TableFraction > 0.6, $"TableFraction {plane.TableFraction}");
    }

    [Fact]
    public void Fit_FewCellsBelowTheTable_DoNotMoveIt()
    {
        var plane = Fit(Cells((c, r) => (r * 31 + c * 17) % 100 == 37 ? -30 : 0));   // 1 %, scattered

        Assert.InRange(plane.A, 133 - 0.05, 133 + 0.05);
    }

    [Fact]
    public void Fit_NoValidCells_IsNull()
    {
        var cells = Enumerable.Repeat(float.NaN, Columns * Rows).ToArray();

        Assert.Null(TableFit.Fit(cells, Columns, Rows, XMin, ColumnMm, RowS, DurationS));
    }
}
