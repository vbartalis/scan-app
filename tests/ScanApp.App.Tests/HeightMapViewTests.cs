using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ScanApp.App.ViewModels;
using ScanApp.App.Views;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Viewing;

namespace ScanApp.App.Tests;

public class HeightMapViewTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(99)]
    [InlineData(100)]
    public void RowAt_InvertsRowToPixel(int row)
    {
        var image = new Rect(12.5, 30, 200, 480.25);

        double y = HeightMapView.RowToPixel(row, 100, image);

        Assert.Equal(row, HeightMapView.RowAt(y, 100, image), 9);
        Assert.Equal(image.Y, HeightMapView.RowToPixel(0, 100, image), 9);
        Assert.Equal(image.Bottom, HeightMapView.RowToPixel(100, 100, image), 9);
    }

    // 100 rows of 3 profiles (300 profiles), image 200 px tall starting at y = 10: 2 px per row, 2/3 px per profile.
    private static readonly Rect Image = new(0, 10, 100, 200);

    [AvaloniaFact]
    public void EdgeY_PutsTheTrimEndAtTheBottomOfItsLastKeptProfile()
    {
        double endY = HeightMapView.EdgeY(HeightMapView.EdgeOf(TrimHandle.End, new ProfileRange(30, 149)), 3, 100, Image);

        // 150 profiles = 50 rows = 100 px below the top
        Assert.Equal(110, endY, 9);
        Assert.Equal(HeightMapView.EdgeY(150, 3, 100, Image), endY, 9);
        Assert.Equal(50, HeightMapView.EdgeY(60, 3, 100, Image), 9);
    }

    [AvaloniaFact]
    public void EdgeAt_InvertsEdgeY_AndEndIsTheEdgeMinusOne()
    {
        var trim = new ProfileRange(30, 149);
        double y = HeightMapView.EdgeY(HeightMapView.EdgeOf(TrimHandle.End, trim), 3, 100, Image);

        int edge = HeightMapView.EdgeAt(y, 3, 100, Image);

        Assert.Equal(150, edge);
        Assert.Equal(trim, HeightMapView.DragTrim(trim, TrimHandle.End, edge, 299));
    }

    [AvaloniaFact]
    public void DragTrim_ClampsBothHandles()
    {
        var trim = new ProfileRange(30, 149);

        Assert.Equal(new ProfileRange(0, 149), HeightMapView.DragTrim(trim, TrimHandle.Start, -20, 299));
        Assert.Equal(new ProfileRange(148, 149), HeightMapView.DragTrim(trim, TrimHandle.Start, 400, 299));
        Assert.Equal(new ProfileRange(30, 31), HeightMapView.DragTrim(trim, TrimHandle.End, 0, 299));
        Assert.Equal(new ProfileRange(30, 299), HeightMapView.DragTrim(trim, TrimHandle.End, 900, 299));
    }

    [AvaloniaFact]
    public void DragXLimit_ToGridEdge_ClearsTheLimit()
    {
        (double?, double?) limits = (-50, 50);

        Assert.Equal((null, 50), HeightMapView.DragXLimit(limits, left: true, -99.5, -100, 100));
        Assert.Equal((null, 50), HeightMapView.DragXLimit(limits, left: true, -140, -100, 100));
        Assert.Equal((-50, null), HeightMapView.DragXLimit(limits, left: false, 99.2, -100, 100));
        Assert.Equal((-50, null), HeightMapView.DragXLimit(limits, left: false, 130, -100, 100));
        Assert.Equal((-98, 50), HeightMapView.DragXLimit(limits, left: true, -98, -100, 100));
        Assert.Equal((-50, 97.5), HeightMapView.DragXLimit((-50, null), left: false, 97.5, -100, 100));
    }

    [AvaloniaFact]
    public void DragXLimit_KeepsMinBelowMax()
    {
        (double?, double?) limits = (-50, 50);

        var (min, max) = HeightMapView.DragXLimit(limits, left: true, 80, -100, 100);
        Assert.Equal(50, max);
        Assert.True(min < max);

        (min, max) = HeightMapView.DragXLimit(limits, left: false, -80, -100, 100);
        Assert.Equal(-50, min);
        Assert.True(min < max);
    }

    [AvaloniaTheory]
    [InlineData(0, 0, false)]
    [InlineData(5, 5, false)]
    [InlineData(0, 1, true)]
    [InlineData(10, 20, true)]
    public void CanDrag_NeedsAtLeastTwoProfiles(int start, int end, bool expected) =>
        Assert.Equal(expected, HeightMapView.IsDraggable(new ProfileRange(start, end)));

    [AvaloniaFact]
    public void CutCells_MatchesRawCutPerCell()
    {
        // 4 columns over X 0..40 (centres 5, 15, 25, 35), 3 rows of 2 profiles (first profiles 0, 2, 4).
        var grid = new HeightGrid(4, 3, 0, 40, 2, [0, 0.1, 0.2], [1, 1, 1, 1, 1, float.NaN, 1, 1, 1, 1, 1, 1]);
        float[] heights = [0, 5, -2, 1, 2, float.NaN, 0.5f, 2.5f, 1, 1, 1, 1];
        byte[] flags = [0, 0, 0, 0, RawGeometry.Paused, 0, 0, RawGeometry.Reflection, 0, RawGeometry.Reflection, 0, 0];
        var geometry = new RawGeometry([], [], heights, flags);
        var limits = new CutLimits(2, 5, 10, double.PositiveInfinity, -1, 2, FilterReflections: true);

        bool[] cut = HeightMapView.CutCells(grid, limits, geometry);

        var expected = new bool[12];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 4; column++)
            {
                int i = row * 4 + column;
                expected[i] = !RawCut.Keeps(limits, row * 2, grid.ColumnToX(column), heights[i],
                                            (flags[i] & RawGeometry.Paused) != 0, (flags[i] & RawGeometry.Reflection) != 0);
            }
        Assert.Equal(expected, cut);
        Assert.Equal([true, true, true, true, true, false, false, true, true, true, false, false], cut);
    }

    /// <summary>A 400 x 400 window showing a 10 x 100 grid of 3 profiles per row over X -100..100: 4 px per row, 20 mm per 40 px.</summary>
    private static (HeightMapView view, Window window) MapWindow(CutLimits limits)
    {
        var grid = new HeightGrid(10, 100, -100, 100, 3, new double[100], Enumerable.Repeat(1f, 1000).ToArray());
        var preview = new PassPreview(grid, new PointSample([], [], [], [], []), 300, 10, 0, 2);
        var view = new HeightMapView { Preview = preview, SpeedMmPerS = 20, Limits = limits };
        var window = new Window { Width = 400, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    [AvaloniaFact]
    public void DragTrimLine_RaisesDraggingThenDragged()
    {
        var limits = new CutLimits(30, 269, double.NegativeInfinity, double.PositiveInfinity,
                                   double.NegativeInfinity, double.PositiveInfinity, true);
        var (view, window) = MapWindow(limits);
        var events = new List<string>();
        view.TrimLineDragging += (kind, index) => events.Add($"dragging {kind} {index}");
        view.TrimLineDragged += (kind, index) => events.Add($"dragged {kind} {index}");

        // The start line sits at the top of profile 30: row 10, y = 40.
        window.MouseDown(new Point(200, 41), MouseButton.Left);
        window.MouseMove(new Point(200, 60));
        window.MouseMove(new Point(200, 80));
        window.MouseUp(new Point(200, 80), MouseButton.Left);
        // The end line sits at the bottom of profile 269: edge 270, row 90, y = 360.
        window.MouseDown(new Point(200, 359), MouseButton.Left);
        window.MouseMove(new Point(200, 320));
        window.MouseUp(new Point(200, 320), MouseButton.Left);

        Assert.Equal(["dragging TrimStart 45", "dragging TrimStart 60", "dragged TrimStart 60",
                      "dragging TrimEnd 239", "dragged TrimEnd 239"], events);
        window.Close();
    }

    [AvaloniaFact]
    public void Lines_WhenCanDragIsOff_CannotBeGrabbed()
    {
        var limits = new CutLimits(30, 269, double.NegativeInfinity, 50, double.NegativeInfinity, double.PositiveInfinity, true);
        var (view, window) = MapWindow(limits);
        var events = new List<string>();
        view.TrimLineDragging += (kind, _) => events.Add($"trim {kind}");
        view.TrimLineDragged += (kind, _) => events.Add($"trim {kind}");
        view.XLineDragging += (kind, _) => events.Add($"x {kind}");
        view.XLineDragged += (kind, _) => events.Add($"x {kind}");

        view.CanDrag = false;   // as while combining
        window.MouseDown(new Point(200, 41), MouseButton.Left);
        window.MouseMove(new Point(200, 80));
        window.MouseUp(new Point(200, 80), MouseButton.Left);
        window.MouseDown(new Point(301, 200), MouseButton.Left);
        window.MouseMove(new Point(350, 200));
        window.MouseUp(new Point(350, 200), MouseButton.Left);
        Assert.Empty(events);

        view.CanDrag = true;
        window.MouseDown(new Point(301, 200), MouseButton.Left);
        window.MouseMove(new Point(350, 200));
        window.MouseUp(new Point(350, 200), MouseButton.Left);
        Assert.Equal(["x XMax", "x XMax"], events);
        window.Close();
    }

    [AvaloniaFact]
    public void DragXLine_RaisesDraggingThenDragged()
    {
        var limits = new CutLimits(0, 299, double.NegativeInfinity, 50, double.NegativeInfinity, double.PositiveInfinity, true);
        var (view, window) = MapWindow(limits);
        var events = new List<string>();
        view.XLineDragging += (kind, mm) => events.Add($"dragging {kind} {mm:0.#}");
        view.XLineDragged += (kind, mm) => events.Add($"dragged {kind} {mm:0.#}");
        view.TrimLineDragged += (kind, _) => events.Add($"trim {kind}");

        // X min has no limit, so its line sits on the left edge; X max 50 mm is at x = 300.
        window.MouseDown(new Point(2, 200), MouseButton.Left);
        window.MouseMove(new Point(100, 200));
        window.MouseUp(new Point(100, 200), MouseButton.Left);
        window.MouseDown(new Point(301, 200), MouseButton.Left);
        window.MouseMove(new Point(399, 200));
        window.MouseUp(new Point(399, 200), MouseButton.Left);

        // A drag to within 1 mm of the grid edge lands on the edge: no limit.
        Assert.Equal(["dragging XMin -50", "dragged XMin -50", "dragging XMax 100", "dragged XMax 100"], events);
        window.Close();
    }
}
