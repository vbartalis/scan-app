using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScanApp.App.ViewModels;

namespace ScanApp.App.Views;

/// <summary>
/// The six limits of the raw views as six rows that work the same way: a number box, a slider and a reset, then the
/// "Apply reflection filter" switch. Dragging a slider previews live (<see cref="WorkbenchViewModel.PreviewLimit"/>) and
/// saves once on release (<see cref="WorkbenchViewModel.CommitLimit"/>); Enter or focus loss in the box, the wheel
/// (one <see cref="LimitScale.Step"/> per notch), the reset and a slider moved without a drag save at once. A slider at its
/// own end is automatic (trims, shown "auto") or no limit (zone, shown "—"). Visible exactly when
/// <see cref="WorkbenchViewModel.ShownLimits"/> is not null.
/// </summary>
public sealed class LimitsPanel : Border
{
    internal const string Auto = "auto", NoLimit = "—", HintText = "Fitting the table…",
                          NoTableText = "No table found — height limits unavailable",
                          FitFailedText = "Could not fit the table — edit a limit to retry";

    private static readonly string[] Labels =
        ["Trim start (s)", "Trim end (s)", "X min (mm)", "X max (mm)", "Height min (mm)", "Height max (mm)"];

    private static readonly HashSet<string> Watched =
    [
        nameof(WorkbenchViewModel.ShownLimits), nameof(WorkbenchViewModel.Analysis), nameof(WorkbenchViewModel.Preview),
        nameof(WorkbenchViewModel.RawGeometry), nameof(WorkbenchViewModel.HeightLimitsReady), nameof(WorkbenchViewModel.RawFit),
        nameof(WorkbenchViewModel.ShowRaw),
        nameof(WorkbenchViewModel.CanEditTrim), nameof(WorkbenchViewModel.FilterReflections),
        nameof(WorkbenchViewModel.CanSwitchFilter), nameof(WorkbenchViewModel.Zone), nameof(WorkbenchViewModel.Trim),
    ];

    private readonly WorkbenchViewModel _vm;
    private readonly Row[] _rows;
    private readonly CheckBox _filter;
    private readonly TextBlock _hint;
    private bool _syncing;

    public LimitsPanel(WorkbenchViewModel vm)
    {
        _vm = vm;
        BorderBrush = Brushes.Gray;
        BorderThickness = new Thickness(1, 0, 0, 0);
        Padding = new Thickness(8);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,72,*,Auto"), ColumnSpacing = 6, RowSpacing = 4 };
        _rows = [.. Enum.GetValues<LimitKind>().Select(kind => new Row(this, kind))];
        _hint = new TextBlock { Text = HintText, Foreground = Brushes.Orange, Margin = new Thickness(0, 4, 0, 0) };
        _filter = new CheckBox { Content = "Apply reflection filter" };
        _filter.IsCheckedChanged += (_, _) => OnFilterChanged();

        int line = 0;
        foreach (var row in _rows)
        {
            if (row.Kind == LimitKind.HeightMin) AddLine(grid, _hint, line++);
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Control[] cells = [row.Label, row.Box, row.Slider, row.Reset];
            for (int c = 0; c < cells.Length; c++)
            {
                Grid.SetRow(cells[c], line);
                Grid.SetColumn(cells[c], c);
                grid.Children.Add(cells[c]);
            }
            line++;
        }
        AddLine(grid, _filter, line);
        Child = grid;

        _vm.PropertyChanged += OnViewModelChanged;
        Refresh();
    }

    private static void AddLine(Grid grid, Control control, int line)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(control, line);
        Grid.SetColumnSpan(control, 4);
        grid.Children.Add(control);
    }

    internal TextBox BoxOf(LimitKind kind) => _rows[(int)kind].Box;

    internal Slider SliderOf(LimitKind kind) => _rows[(int)kind].Slider;

    internal Button ResetOf(LimitKind kind) => _rows[(int)kind].Reset;

    internal CheckBox FilterBox => _filter;

    internal TextBlock HeightHint => _hint;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && Watched.Contains(name)) Refresh();
    }

    /// <summary>Re-reads every row, the switch and the visibility from the view model.</summary>
    private void Refresh()
    {
        IsVisible = _vm.ShownLimits is not null;
        _hint.IsVisible = !_vm.HeightLimitsReady;
        _hint.Text = _vm.RawFit switch
        {
            RawFitState.NoTable => NoTableText,
            RawFitState.Failed => FitFailedText,
            _ => HintText,
        };
        foreach (var row in _rows) row.Refresh();
        _syncing = true;
        _filter.IsChecked = _vm.FilterReflections;
        _filter.IsEnabled = _vm.CanSwitchFilter;
        _syncing = false;
    }

    private void OnFilterChanged()
    {
        if (_syncing) return;
        _vm.SetFilterReflections(_filter.IsChecked == true);
        Refresh();
    }

    private void Commit(LimitKind kind, double? value)
    {
        _vm.CommitLimit(kind, value);
        Refresh();
    }

    private static bool IsTrim(LimitKind kind) => kind is LimitKind.TrimStart or LimitKind.TrimEnd;

    private static string Format(LimitKind kind, double? value) =>
        value is { } v ? v.ToString("0.###", CultureInfo.CurrentCulture) : IsTrim(kind) ? Auto : NoLimit;

    /// <summary>One limit's label, box, slider (0..1 over <see cref="WorkbenchViewModel.ScaleOf"/>) and reset.</summary>
    private sealed class Row
    {
        private readonly LimitsPanel _panel;
        private string _shown = "";
        private bool _dragging, _dragMoved;
        private double? _dragValue;

        public Row(LimitsPanel panel, LimitKind kind)
        {
            _panel = panel;
            Kind = kind;
            Label = new TextBlock { Text = Labels[(int)kind], VerticalAlignment = VerticalAlignment.Center };
            Box = new TextBox();
            Slider = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1, MinWidth = 120 };
            Reset = new Button { Content = IsTrim(kind) ? Auto : "✕", VerticalAlignment = VerticalAlignment.Center };

            Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitBox(); };
            Box.LostFocus += (_, _) => CommitBox();
            Slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => StartDrag(), RoutingStrategies.Tunnel, handledEventsToo: true);
            Slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => EndDrag(),
                              RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            Slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
            Slider.ValueChanged += (_, e) => OnSliderMoved(e.NewValue);
            Box.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            Slider.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            Reset.Click += (_, _) => panel.Commit(kind, null);
        }

        public LimitKind Kind { get; }
        public TextBlock Label { get; }
        public TextBox Box { get; }
        public Slider Slider { get; }
        public Button Reset { get; }

        private WorkbenchViewModel Vm => _panel._vm;

        public void Refresh()
        {
            var scale = Vm.ScaleOf(Kind);
            bool ready = Kind is not (LimitKind.HeightMin or LimitKind.HeightMax) || Vm.HeightLimitsReady;
            bool enabled = scale is not null && ready && Vm.CanEditTrim && Vm.ShownLimits is not null;
            Box.IsEnabled = Slider.IsEnabled = Reset.IsEnabled = enabled;

            double? value = Vm.LimitValue(Kind);
            // A box being typed in keeps its text until it is committed.
            if (!(Box.IsFocused && Box.Text != _shown))
            {
                _shown = Format(Kind, value);
                Box.Text = _shown;
            }
            if (_dragging || scale is null) return;
            _panel._syncing = true;
            Slider.Value = scale.PositionOf(value);
            _panel._syncing = false;
        }

        private void CommitBox()
        {
            string text = (Box.Text ?? "").Trim();
            if (text == _shown) return;
            double? value;
            if (text.Length == 0 || text == Auto || text == NoLimit) value = null;
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) && double.IsFinite(v)) value = v;
            else
            {
                Box.Text = _shown;
                return;
            }
            _shown = text;   // so the refresh after the commit replaces it, accepted or not
            _panel.Commit(Kind, value);
            _shown = Format(Kind, Vm.LimitValue(Kind));
            Box.Text = _shown;
        }

        private void StartDrag()
        {
            _dragging = true;
            _dragMoved = false;
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_dragMoved) _panel.Commit(Kind, _dragValue);
            else Refresh();
        }

        private void OnSliderMoved(double position)
        {
            if (_panel._syncing || Vm.ScaleOf(Kind) is not { } scale) return;
            double? value = scale.ValueAt(position);
            if (_dragging)
            {
                _dragMoved = true;
                _dragValue = value;
                Vm.PreviewLimit(Kind, value);
            }
            else
                _panel.Commit(Kind, value);
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            e.Handled = true;
            int steps = Math.Sign(e.Delta.Y);
            if (steps == 0 || !Slider.IsEnabled || Vm.ScaleOf(Kind) is not { } scale) return;
            _panel.Commit(Kind, scale.Nudge(Vm.NudgeOrigin(Kind), steps));
        }
    }
}
