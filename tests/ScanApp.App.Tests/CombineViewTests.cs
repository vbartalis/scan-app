using ScanApp.App.ViewModels;
using ScanApp.App.Views;
using ScanApp.Core.Combine;

namespace ScanApp.App.Tests;

public class CombineViewTests
{
    private static PassOutcome Outcome(int id, double speed, double range, double overlap) =>
        new(id, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, speed]), speed, range, overlap);

    [Fact]
    public void DescribeOutcome_FormatsSpeedRangeAndOverlap()
    {
        Assert.Equal("pass 4: 25.9 mm/s ±28 % · overlap 0.12 mm",
            WorkbenchViewModel.DescribeOutcome(Outcome(4, 25.93, 0.28, 0.1234), false, false));
    }

    [Fact]
    public void DescribeOutcome_MarksTheReference()
    {
        Assert.Equal("pass 1 (reference): 34.6 mm/s ±25 % · overlap 0.09 mm",
            WorkbenchViewModel.DescribeOutcome(Outcome(1, 34.64, 0.25, 0.09), true, false));
    }

    [Fact]
    public void DescribeOutcome_UsesInvariantCulture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("hu-HU");
        try
        {
            Assert.Contains("25.9 mm/s", WorkbenchViewModel.DescribeOutcome(Outcome(4, 25.9, 0.28, 0.12), false, false));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void DescribeOutcome_MarksADeletedPass()
    {
        Assert.Equal("pass 4: 25.9 mm/s ±28 % · overlap 0.12 mm (deleted)",
            WorkbenchViewModel.DescribeOutcome(Outcome(4, 25.93, 0.28, 0.1234), false, true));
    }

    [Fact]
    public void DescribeOutcome_MarksTableUncertain()
    {
        Assert.Equal("pass 3: 25.9 mm/s ±28 % · overlap 0.12 mm · table uncertain",
            WorkbenchViewModel.DescribeOutcome(Outcome(3, 25.93, 0.28, 0.1234) with { TableUncertain = true }, false, false));
    }

    [Fact]
    public void DescribeOutcome_ListsReflectionsRemoved()
    {
        Assert.Equal("pass 3: 25.9 mm/s ±28 % · overlap 0.12 mm · 412 reflections removed",
            WorkbenchViewModel.DescribeOutcome(Outcome(3, 25.93, 0.28, 0.1234) with { ReflectionsRemoved = 412 }, false, false));
        Assert.Equal("pass 3: 25.9 mm/s ±28 % · overlap 0.12 mm · table uncertain · 0 reflections removed (deleted)",
            WorkbenchViewModel.DescribeOutcome(Outcome(3, 25.93, 0.28, 0.1234) with { TableUncertain = true, ReflectionsRemoved = 0 },
                false, true));
    }

    [Fact]
    public void DescribeOutcome_FilterOff_SaysSo()
    {
        Assert.Equal("pass 3: 25.9 mm/s ±28 % · overlap 0.12 mm · reflection filter off",
            WorkbenchViewModel.DescribeOutcome(Outcome(3, 25.93, 0.28, 0.1234) with { ReflectionFilterOff = true }, false, false));
        Assert.Equal("pass 3: 25.9 mm/s ±28 % · overlap 0.12 mm · table uncertain · reflection filter off (deleted)",
            WorkbenchViewModel.DescribeOutcome(Outcome(3, 25.93, 0.28, 0.1234) with { TableUncertain = true, ReflectionFilterOff = true },
                false, true));
    }

    [Fact]
    public void DescribeFlags_UsesTheSpecText()
    {
        CombineResult Make(bool uncertain, bool scaleUnknown) =>
            new(1, DateTimeOffset.Now, 1, [Outcome(1, 20, 0, 0.1), Outcome(2, 20, 0, 0.1)], uncertain, scaleUnknown);

        Assert.Empty(WorkbenchViewModel.DescribeFlags(Make(false, false)));
        Assert.Equal(["Uncertain alignment"], WorkbenchViewModel.DescribeFlags(Make(true, false)));
        Assert.Equal(["Scale unknown — no pass at a different rotation; travel uses the reference speed estimate only"],
            WorkbenchViewModel.DescribeFlags(Make(false, true)));
        Assert.Equal(2, WorkbenchViewModel.DescribeFlags(Make(true, true)).Count);
    }

    [Fact]
    public void DescribeFlags_NamesOpenCycles()
    {
        var r = new CombineResult(1, DateTimeOffset.Now, 13, [Outcome(13, 20, 0, 0.1), Outcome(14, 20, 0, 0.1), Outcome(16, 20, 0, 0.1)],
            true, false, OpenCycles: [[13, 14, 16]]);

        Assert.Equal(["Uncertain alignment", "Passes 13, 14 and 16 do not agree: their alignments do not close"],
            WorkbenchViewModel.DescribeFlags(r));
    }

    private static MergedCloud Cloud() =>
        new([1f, 2f, 3f], [10f, 20f, 30f], [5f, -1f, 7f], [0, 2, 9]);

    [Fact]
    public void BuildMerged_HeightMode_ValueIsZ()
    {
        var d = PointCloudData.BuildMerged(Cloud(), byPass: false, flip: false);
        Assert.Equal([1f, 10f, 5f, 2f, 20f, -1f, 3f, 30f, 7f], d.Xyz);
        Assert.Equal([5f, -1f, 7f], d.Value);
        Assert.Equal(-1f, d.ValueMin);
        Assert.Equal(7f, d.ValueMax);
    }

    [Fact]
    public void BuildMerged_PassMode_ValueIsPassIndex()
    {
        var d = PointCloudData.BuildMerged(Cloud(), byPass: true, flip: false);
        Assert.Equal([0f, 2f, 9f], d.Value);
    }

    [Fact]
    public void BuildMerged_Flip_NegatesZButKeepsHeightValue()
    {
        var d = PointCloudData.BuildMerged(Cloud(), byPass: false, flip: true);
        Assert.Equal([-5f, 1f, -7f], new[] { d.Xyz[2], d.Xyz[5], d.Xyz[8] });
        Assert.Equal([5f, -1f, 7f], d.Value);
    }

    [Fact]
    public void BuildMerged_Empty_GivesUnitRange()
    {
        var d = PointCloudData.BuildMerged(new MergedCloud([], [], [], []), false, false);
        Assert.Empty(d.Xyz);
        Assert.Equal(0f, d.ValueMin);
        Assert.Equal(1f, d.ValueMax);
    }
}
