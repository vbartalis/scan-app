using ScanApp.Core.Combine;

namespace ScanApp.Core.Tests.Combine;

public class TableCheckTests
{
    private const int Rows = 300;
    private const double RowS = 0.01, DurationS = Rows * RowS;

    /// <summary>A map whose table slopes <paramref name="b"/> across X and <paramref name="alongMm"/> over the pass.</summary>
    private static AlignmentMap Map(double b, double alongMm = 0.2, double fraction = 0.2) =>
        new(1, Rows, 0, 1, RowS, new float[Rows], new bool[Rows])
        {
            Table = new TablePlane(133, b, alongMm / DurationS, 0, DurationS / 2, RowS, new float[Rows]) { TableFraction = fraction },
        };

    [Fact]
    public void Uncertain_ConsistentTables_NoneFlagged()
    {
        Assert.Equal([false, false, false], TableCheck.Uncertain([Map(-0.012, 0.1), Map(-0.011, 0.3), Map(-0.013, -0.2)]));
    }

    [Fact]
    public void Uncertain_SlopeAlongTravel_Flagged()
    {
        Assert.Equal([false, true, false], TableCheck.Uncertain([Map(-0.012, 0.1), Map(-0.011, 6), Map(-0.013, 0.3)]));
    }

    [Fact]
    public void Uncertain_CrossXOutlier_Flagged()
    {
        Assert.Equal([false, false, false, true],
            TableCheck.Uncertain([Map(-0.012), Map(-0.011), Map(-0.013), Map(-0.040)]));
    }

    [Fact]
    public void Uncertain_TwoPassesDisagreeingAcrossX_BothFlagged()
    {
        Assert.Equal([true, true], TableCheck.Uncertain([Map(-0.012), Map(-0.030)]));
        Assert.Equal([false, false], TableCheck.Uncertain([Map(-0.012), Map(-0.015)]));
    }

    [Fact]
    public void Uncertain_TooFewTableCells_Flagged()
    {
        Assert.Equal([true, false], TableCheck.Uncertain([Map(-0.012, fraction: 0.005), Map(-0.012)]));
        Assert.Equal([false, false], TableCheck.Uncertain([Map(-0.012, fraction: 0.04), Map(-0.012)]));
    }

    [Fact]
    public void Uncertain_NoTable_Flagged()
    {
        var bare = new AlignmentMap(1, Rows, 0, 1, RowS, new float[Rows], new bool[Rows]);

        Assert.Equal([false, true], TableCheck.Uncertain([Map(-0.012), bare]));
    }
}
