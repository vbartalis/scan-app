using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScanApp.App.ViewModels;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Viewing;

namespace ScanApp.App.Views;

internal enum TrimHandle { None, Start, End }

/// <summary>
/// Draws a pass's height grid as a top view: X left to right, travel top to bottom. The travel axis is
/// scaled to DurationS x SpeedMmPerS mm, so the speed sets the aspect ratio. Raises <see cref="Hovered"/> with a readout.
/// For a raw preview (one row per ProfilesPerRow raw profiles) with <see cref="Limits"/> set it greys the cells the
/// limits cut (<see cref="CutCells"/>, from the <see cref="Geometry"/>), marks the reflection cells magenta while
/// <see cref="ShowReflections"/> is on, marks <see cref="BackwardRuns"/>, and draws the trim and X lines, which drag:
/// <see cref="TrimLineDragging"/> / <see cref="XLineDragging"/> live, <see cref="TrimLineDragged"/> /
/// <see cref="XLineDragged"/> once on release.
/// </summary>
public sealed class HeightMapView : Control
{
    public static readonly StyledProperty<PassPreview?> PreviewProperty =
        AvaloniaProperty.Register<HeightMapView, PassPreview?>(nameof(Preview));

    public static readonly StyledProperty<double> SpeedMmPerSProperty =
        AvaloniaProperty.Register<HeightMapView, double>(nameof(SpeedMmPerS), 20);

    public static readonly StyledProperty<bool> FlipHeightProperty =
        AvaloniaProperty.Register<HeightMapView, bool>(nameof(FlipHeight));

    public static readonly StyledProperty<IReadOnlyList<ProfileRange>?> BackwardRunsProperty =
        AvaloniaProperty.Register<HeightMapView, IReadOnlyList<ProfileRange>?>(nameof(BackwardRuns));

    public static readonly StyledProperty<CutLimits?> LimitsProperty =
        AvaloniaProperty.Register<HeightMapView, CutLimits?>(nameof(Limits));

    public static readonly StyledProperty<RawGeometry?> GeometryProperty =
        AvaloniaProperty.Register<HeightMapView, RawGeometry?>(nameof(Geometry));

    public static readonly StyledProperty<bool> ShowReflectionsProperty =
        AvaloniaProperty.Register<HeightMapView, bool>(nameof(ShowReflections));

    public static readonly StyledProperty<bool> CanDragProperty =
        AvaloniaProperty.Register<HeightMapView, bool>(nameof(CanDrag), true);

    private const double HandleGrabPx = 6;
    /// <summary>A dragged X line this close to the grid edge lands on it (no limit); it is also the least gap kept between the lines.</summary>
    internal const double XLimitSnapMm = 1;
    /// <summary>Cut cells, opaque #5A5A5A, and removed reflections, opaque #FF00FF, as premultiplied BGRA.</summary>
    private const uint CutArgb = 0xFF5A5A5A, ReflectionArgb = 0xFFFF00FF;
    private static readonly IBrush BackwardBrush = new SolidColorBrush(Color.FromArgb(170, 255, 140, 0));
    private static readonly IPen HandlePen = new Pen(Brushes.White, 2);

    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _overlay;
    private (CutLimits Limits, RawGeometry? Geometry, bool Reflections, PassPreview Preview)? _overlayKey;
    private TrimHandle _dragHandle;
    private ProfileRange _dragTrim;
    private bool? _dragLeftLimit;   // which X line is being dragged: true the left (Min), false the right (Max)
    private (double? Min, double? Max) _dragLimits;
    private bool _dragMoved;

    static HeightMapView() =>
        AffectsRender<HeightMapView>(PreviewProperty, SpeedMmPerSProperty, FlipHeightProperty,
            BackwardRunsProperty, LimitsProperty, GeometryProperty, ShowReflectionsProperty);

    public PassPreview? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    /// <summary>Travel speed in mm/s, used to scale the travel axis.</summary>
    public double SpeedMmPerS
    {
        get => GetValue(SpeedMmPerSProperty);
        set => SetValue(SpeedMmPerSProperty, value);
    }

    public bool FlipHeight
    {
        get => GetValue(FlipHeightProperty);
        set => SetValue(FlipHeightProperty, value);
    }

    /// <summary>Backward runs to mark as bands, in raw profile indices; drawn only with <see cref="Limits"/>.</summary>
    public IReadOnlyList<ProfileRange>? BackwardRuns
    {
        get => GetValue(BackwardRunsProperty);
        set => SetValue(BackwardRunsProperty, value);
    }

    /// <summary>The limits to cut by and draw lines for (trims as raw profile indices); null = no overlay (the cleaned view).</summary>
    public CutLimits? Limits
    {
        get => GetValue(LimitsProperty);
        set => SetValue(LimitsProperty, value);
    }

    /// <summary>The raw pass's cell heights above the table and flags; until it is set only the trims and X cut.</summary>
    public RawGeometry? Geometry
    {
        get => GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    /// <summary>True to mark the cells holding a point the reflection filter removes (or would) magenta.</summary>
    public bool ShowReflections
    {
        get => GetValue(ShowReflectionsProperty);
        set => SetValue(ShowReflectionsProperty, value);
    }

    /// <summary>False while the limits cannot be edited (combining): the lines then cannot be grabbed.</summary>
    public bool CanDrag
    {
        get => GetValue(CanDragProperty);
        set => SetValue(CanDragProperty, value);
    }

    /// <summary>A trim line is being dragged: the limit and its raw profile index (start: first kept, end: last kept).</summary>
    public event Action<LimitKind, int>? TrimLineDragging;

    /// <summary>A trim line drag was released, once, with its final raw profile index.</summary>
    public event Action<LimitKind, int>? TrimLineDragged;

    /// <summary>An X line is being dragged, in mm; on the grid edge it means no limit.</summary>
    public event Action<LimitKind, double>? XLineDragging;

    /// <summary>An X line drag was released, once, with its final X in mm.</summary>
    public event Action<LimitKind, double>? XLineDragged;

    /// <summary>Readout for the point under the pointer; empty when the pointer is off the image.</summary>
    public event Action<string>? Hovered;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PreviewProperty || change.Property == FlipHeightProperty)
            RebuildBitmap();
    }

    private void RebuildBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (Preview is not { IsEmpty: false } preview) return;

        var grid = preview.Grid;
        var pixels = new int[grid.Columns * grid.Rows];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (int)HeightColorMap.ToArgb(grid.Z[i], preview.ZMin, preview.ZMax, FlipHeight);

        var bitmap = new WriteableBitmap(new PixelSize(grid.Columns, grid.Rows), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bitmap.Lock())
        {
            for (int r = 0; r < grid.Rows; r++)
                Marshal.Copy(pixels, r * grid.Columns, fb.Address + r * fb.RowBytes, grid.Columns);
        }
        _bitmap = bitmap;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        if (Preview is not { } preview) return;

        if (preview.IsEmpty || _bitmap is null)
        {
            var text = new FormattedText("No valid points in this pass", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.Gray);
            context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
            return;
        }

        var dest = ImageRect(bounds.Size, preview, SpeedMmPerS);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), dest);
            if (ShownLimits() is not { } limits) return;
            if (Overlay(preview, limits) is { } overlay) context.DrawImage(overlay, new Rect(overlay.Size), dest);
            RenderLines(context, preview.Grid, dest, limits);
        }
    }

    /// <summary>
    /// Per cell of <paramref name="grid"/>, true where <see cref="RawCut.Keeps"/> drops it: the cell's row stands for its
    /// first profile, its X is the column centre, its height and flags come from <paramref name="geometry"/>.
    /// </summary>
    internal static bool[] CutCells(HeightGrid grid, CutLimits limits, RawGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Cut(grid, limits, geometry.CellHeight, geometry.CellFlags);
    }

    /// <summary>As <see cref="CutCells"/>; without heights and flags (null) every height is NaN and nothing is flagged.</summary>
    private static bool[] Cut(HeightGrid grid, CutLimits limits, float[]? heights, byte[]? flags)
    {
        var cut = new bool[grid.Z.Length];
        for (int row = 0; row < grid.Rows; row++)
        {
            int profile = row * grid.ProfilesPerRow;
            for (int column = 0; column < grid.Columns; column++)
            {
                int i = row * grid.Columns + column;
                byte f = flags?[i] ?? 0;
                cut[i] = !RawCut.Keeps(limits, profile, grid.ColumnToX(column), heights?[i] ?? float.NaN,
                                       (f & RawGeometry.Paused) != 0, (f & RawGeometry.Reflection) != 0);
            }
        }
        return cut;
    }

    /// <summary>The limits to draw: <see cref="Limits"/> with the line being dragged where the pointer has it.</summary>
    private CutLimits? ShownLimits()
    {
        if (Limits is not { } limits) return null;
        if (_dragHandle != TrimHandle.None) return limits with { TrimStart = _dragTrim.Start, TrimEnd = _dragTrim.End };
        if (_dragLeftLimit is not null)
            return limits with
            {
                XMin = _dragLimits.Min ?? double.NegativeInfinity,
                XMax = _dragLimits.Max ?? double.PositiveInfinity,
            };
        return limits;
    }

    private static ProfileRange TrimOf(CutLimits limits) => new(limits.TrimStart, limits.TrimEnd);

    private static (double? Min, double? Max) XOf(CutLimits limits) =>
        (double.IsFinite(limits.XMin) ? limits.XMin : null, double.IsFinite(limits.XMax) ? limits.XMax : null);

    /// <summary>
    /// The grid-sized overlay: cut cells grey, reflection cells magenta (over grey) while <see cref="ShowReflections"/> is
    /// on, the rest clear; empty cells stay clear. Rebuilt only when what it shows changes.
    /// </summary>
    private WriteableBitmap? Overlay(PassPreview preview, CutLimits limits)
    {
        var grid = preview.Grid;
        var geometry = Geometry is { } g && g.CellHeight.Length == grid.Z.Length && g.CellFlags.Length == grid.Z.Length ? g : null;
        var key = (limits, geometry, ShowReflections, preview);
        if (_overlay is not null && _overlayKey is { } last && last.Equals(key)) return _overlay;

        bool[] cut = Cut(grid, limits, geometry?.CellHeight, geometry?.CellFlags);
        var pixels = new int[cut.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (float.IsNaN(grid.Z[i])) continue;
            if (ShowReflections && geometry is not null && (geometry.CellFlags[i] & RawGeometry.Reflection) != 0)
                pixels[i] = unchecked((int)ReflectionArgb);
            else if (cut[i])
                pixels[i] = unchecked((int)CutArgb);
        }

        if (_overlay is null || _overlay.PixelSize != new PixelSize(grid.Columns, grid.Rows))
        {
            _overlay?.Dispose();
            _overlay = new WriteableBitmap(new PixelSize(grid.Columns, grid.Rows), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var fb = _overlay.Lock())
        {
            for (int r = 0; r < grid.Rows; r++)
                Marshal.Copy(pixels, r * grid.Columns, fb.Address + r * fb.RowBytes, grid.Columns);
        }
        _overlayKey = key;
        return _overlay;
    }

    private void RenderLines(DrawingContext context, HeightGrid grid, Rect dest, CutLimits limits)
    {
        int ppr = grid.ProfilesPerRow, rows = grid.Rows;
        double YOf(int edge) => EdgeY(edge, ppr, rows, dest);
        var trim = TrimOf(limits);
        double startY = YOf(EdgeOf(TrimHandle.Start, trim)), endY = YOf(EdgeOf(TrimHandle.End, trim));

        if (BackwardRuns is { } runs)
            foreach (var run in runs)
            {
                double top = YOf(run.Start), bottom = YOf(run.End + 1);
                context.FillRectangle(BackwardBrush, new Rect(dest.X, top, dest.Width, Math.Max(2, bottom - top)));
            }
        context.DrawLine(HandlePen, new Point(dest.X, startY), new Point(dest.Right, startY));
        context.DrawLine(HandlePen, new Point(dest.X, endY), new Point(dest.Right, endY));

        var (min, max) = XOf(limits);
        if (min is { } left) DrawX(left);
        if (max is { } right) DrawX(right);
        void DrawX(double x)
        {
            double px = Math.Clamp(XToPixel(x, grid, dest), dest.X, dest.Right);
            context.DrawLine(HandlePen, new Point(px, dest.Y), new Point(px, dest.Bottom));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Limits is not { } limits) return;
        var p = e.GetPosition(this);
        if (XLimitAt(p) is { } left)
        {
            _dragLeftLimit = left;
            _dragLimits = XOf(limits);
        }
        else if (HandleAt(p) is { } handle && IsDraggable(TrimOf(limits)))
        {
            _dragHandle = handle;
            _dragTrim = TrimOf(limits);
        }
        else return;
        _dragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragHandle == TrimHandle.None && _dragLeftLimit is null) return;
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    /// <summary>Ends a drag; one that moved raises its Dragged event once, with the value the line was left at.</summary>
    private void EndDrag()
    {
        var (handle, trim, left, limits, moved) = (_dragHandle, _dragTrim, _dragLeftLimit, _dragLimits, _dragMoved);
        if (handle == TrimHandle.None && left is null) return;
        _dragHandle = TrimHandle.None;
        _dragLeftLimit = null;
        _dragMoved = false;
        InvalidateVisual();
        if (!moved) return;
        if (left is { } isLeft && Preview is { } preview) XLineDragged?.Invoke(XKind(isLeft), XValue(isLeft, limits, preview.Grid));
        else if (handle != TrimHandle.None) TrimLineDragged?.Invoke(TrimKind(handle), TrimValue(handle, trim));
    }

    private static LimitKind XKind(bool left) => left ? LimitKind.XMin : LimitKind.XMax;

    private static LimitKind TrimKind(TrimHandle handle) => handle == TrimHandle.Start ? LimitKind.TrimStart : LimitKind.TrimEnd;

    /// <summary>The X a dragged line stands at; an absent limit is the grid edge.</summary>
    private static double XValue(bool left, (double? Min, double? Max) limits, HeightGrid grid) =>
        left ? limits.Min ?? grid.XMin : limits.Max ?? grid.XMax;

    private static int TrimValue(TrimHandle handle, ProfileRange trim) => handle == TrimHandle.Start ? trim.Start : trim.End;

    /// <summary>Which trim line is within the grab distance of <paramref name="p"/>, if any.</summary>
    private TrimHandle? HandleAt(Point p)
    {
        if (Limits is not { } limits || Preview is not { IsEmpty: false } preview) return null;
        var trim = TrimOf(limits);
        var image = ImageRect(Bounds.Size, preview, SpeedMmPerS);
        int ppr = preview.Grid.ProfilesPerRow, rows = preview.Grid.Rows;
        double dStart = Math.Abs(p.Y - EdgeY(EdgeOf(TrimHandle.Start, trim), ppr, rows, image));
        double dEnd = Math.Abs(p.Y - EdgeY(EdgeOf(TrimHandle.End, trim), ppr, rows, image));
        if (Math.Min(dStart, dEnd) > HandleGrabPx) return null;
        return dStart <= dEnd ? TrimHandle.Start : TrimHandle.End;
    }

    /// <summary>Which X line (true = left) is within the grab distance of <paramref name="p"/>, if any.</summary>
    private bool? XLimitAt(Point p)
    {
        if (Limits is not { } cut || Preview is not { IsEmpty: false } preview) return null;
        var limits = XOf(cut);
        var image = ImageRect(Bounds.Size, preview, SpeedMmPerS);
        if (p.Y < image.Y || p.Y > image.Bottom) return null;
        double dLeft = Math.Abs(p.X - Math.Max(image.X, XToPixel(limits.Min ?? preview.Grid.XMin, preview.Grid, image)));
        double dRight = Math.Abs(p.X - Math.Min(image.Right, XToPixel(limits.Max ?? preview.Grid.XMax, preview.Grid, image)));
        if (Math.Min(dLeft, dRight) > HandleGrabPx) return null;
        return dLeft <= dRight;
    }

    /// <summary>The pixel x of an X in mm; the image spans the grid's XMin..XMax.</summary>
    internal static double XToPixel(double x, HeightGrid grid, Rect image) =>
        grid.XMax > grid.XMin ? image.X + (x - grid.XMin) / (grid.XMax - grid.XMin) * image.Width : image.X;

    /// <summary>The X in mm at pixel x; the inverse of <see cref="XToPixel"/>.</summary>
    internal static double PixelToX(double px, HeightGrid grid, Rect image) =>
        image.Width > 0 ? grid.XMin + (px - image.X) / image.Width * (grid.XMax - grid.XMin) : grid.XMin;

    /// <summary>
    /// The X limits after dragging the left (Min) or right (Max) edge to <paramref name="x"/>: kept inside the grid and at
    /// least <see cref="XLimitSnapMm"/> from the other limit (or the far grid edge), and cleared (null) within
    /// <see cref="XLimitSnapMm"/> of its own grid edge.
    /// </summary>
    internal static (double? Min, double? Max) DragXLimit((double? Min, double? Max) limits, bool left, double x,
                                                         double gridXMin, double gridXMax)
    {
        if (left)
        {
            double min = Math.Min(x, (limits.Max ?? gridXMax) - XLimitSnapMm);
            return (min - gridXMin <= XLimitSnapMm ? null : min, limits.Max);
        }
        double max = Math.Max(x, (limits.Min ?? gridXMin) + XLimitSnapMm);
        return (limits.Min, gridXMax - max <= XLimitSnapMm ? null : max);
    }

    private void DragTo(Point p, PassPreview preview)
    {
        var image = ImageRect(Bounds.Size, preview, SpeedMmPerS);
        int ppr = preview.Grid.ProfilesPerRow, rows = preview.Grid.Rows;
        var trim = DragTrim(_dragTrim, _dragHandle, EdgeAt(p.Y, ppr, rows, image), rows * ppr - 1);
        if (trim == _dragTrim) return;
        _dragTrim = trim;
        _dragMoved = true;
        InvalidateVisual();
        TrimLineDragging?.Invoke(TrimKind(_dragHandle), TrimValue(_dragHandle, trim));
    }

    /// <summary>The boundary a handle sits on, as a profile edge: the start of the first kept profile, or the end of the last (End + 1).</summary>
    internal static int EdgeOf(TrimHandle handle, ProfileRange trim) => handle == TrimHandle.Start ? trim.Start : trim.End + 1;

    /// <summary>The pixel y of a profile edge (edge n is the top of profile n).</summary>
    internal static double EdgeY(int edge, int profilesPerRow, int rows, Rect image) =>
        RowToPixel((double)edge / profilesPerRow, rows, image);

    /// <summary>The profile edge nearest pixel y; the inverse of <see cref="EdgeY"/>.</summary>
    internal static int EdgeAt(double y, int profilesPerRow, int rows, Rect image) =>
        (int)Math.Round(RowAt(y, rows, image) * profilesPerRow);

    /// <summary>True when the trim spans at least two profiles, so both handles have room to move.</summary>
    internal static bool IsDraggable(ProfileRange trim) => trim.End > trim.Start;

    /// <summary>The trim after dragging <paramref name="handle"/> to <paramref name="edge"/>, keeping Start before End inside 0..<paramref name="last"/>.</summary>
    internal static ProfileRange DragTrim(ProfileRange trim, TrimHandle handle, int edge, int last) =>
        handle == TrimHandle.Start
            ? new ProfileRange(Math.Clamp(edge, 0, Math.Max(0, trim.End - 1)), trim.End)
            : new ProfileRange(trim.Start, Math.Clamp(edge - 1, trim.Start + 1, Math.Max(last, trim.Start + 1)));

    /// <summary>The pixel y of a fractional grid row (row 0 at the top of <paramref name="image"/>).</summary>
    internal static double RowToPixel(double row, int rows, Rect image) => image.Y + row / rows * image.Height;

    /// <summary>The fractional grid row at pixel y; the inverse of <see cref="RowToPixel"/>.</summary>
    internal static double RowAt(double y, int rows, Rect image) => (y - image.Y) / image.Height * rows;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragLeftLimit is { } left && Preview is { IsEmpty: false } limited)
        {
            var grid = limited.Grid;
            double x = PixelToX(e.GetPosition(this).X, grid, ImageRect(Bounds.Size, limited, SpeedMmPerS));
            var limits = DragXLimit(_dragLimits, left, x, grid.XMin, grid.XMax);
            if (limits == _dragLimits) return;
            _dragLimits = limits;
            _dragMoved = true;
            InvalidateVisual();
            XLineDragging?.Invoke(XKind(left), XValue(left, limits, grid));
            return;
        }
        if (_dragHandle != TrimHandle.None && Preview is { IsEmpty: false } dragged)
        {
            DragTo(e.GetPosition(this), dragged);
            return;
        }
        var at = e.GetPosition(this);
        Cursor = XLimitAt(at) is not null ? new Cursor(StandardCursorType.SizeWestEast)
            : HandleAt(at) is not null ? new Cursor(StandardCursorType.SizeNorthSouth) : null;
        string text = string.Empty;
        if (Preview is { IsEmpty: false } preview)
            text = Readout(preview, SpeedMmPerS, ImageRect(Bounds.Size, preview, SpeedMmPerS), e.GetPosition(this));
        Hovered?.Invoke(text);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hovered?.Invoke(string.Empty);
    }

    /// <summary>The largest centred rectangle in <paramref name="size"/> with the pass's mm aspect (X width : travel length).</summary>
    internal static Rect ImageRect(Size size, PassPreview preview, double speedMmPerS)
    {
        double widthMm = preview.Grid.XMax - preview.Grid.XMin;
        double lengthMm = preview.DurationS * speedMmPerS;
        if (!(widthMm > 0) || !(lengthMm > 0)) return new Rect(size);

        double scale = Math.Min(size.Width / widthMm, size.Height / lengthMm);
        double w = widthMm * scale, h = lengthMm * scale;
        return new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
    }

    /// <summary>The readout for a pointer position, or empty when it is outside the drawn image.</summary>
    internal static string Readout(PassPreview preview, double speedMmPerS, Rect image, Point p)
    {
        var grid = preview.Grid;
        if (image.Width <= 0 || image.Height <= 0 || !image.Contains(p)) return string.Empty;

        double u = (p.X - image.X) / image.Width;   // 0..1 across X
        double v = (p.Y - image.Y) / image.Height;  // 0..1 along travel
        int column = Math.Clamp((int)(u * grid.Columns), 0, grid.Columns - 1);
        int row = Math.Clamp((int)(v * grid.Rows), 0, grid.Rows - 1);

        double x = grid.ColumnToX(u * grid.Columns - 0.5);
        double travel = v * preview.DurationS * speedMmPerS;
        float z = grid.CellAt(column, row);
        string zText = float.IsNaN(z) ? "–" : z.ToString("F2", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"X {x:F1} mm · travel {travel:F1} mm · Z {zText} mm · profile {row * grid.ProfilesPerRow}");
    }
}
