using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using ScanApp.App.ViewModels;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Model;
using ScanApp.Rendering;

namespace ScanApp.App.Views;

/// <summary>The workbench window, laid out in code: top bar, pass list, live profile, pass view, status line.</summary>
public sealed class WorkbenchWindow : Window
{
    private readonly WorkbenchViewModel _vm;
    private readonly DispatcherTimer _liveViewTimer;
    private HeightMapView? _heightMap;
    private PointCloudView? _cloud;
    private StackPanel? _resultLines;
    private Button? _resetButton;
    private readonly List<Control> _passOnlyTools = [];
    private readonly List<Control> _resultOnlyTools = [];

    public WorkbenchWindow(WorkbenchViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        Title = "Scan workbench";
        Width = 1200;
        Height = 800;

        var root = new DockPanel();
        root.Children.Add(TopBar());
        root.Children.Add(StatusLine());
        var passes = PassList();
        DockPanel.SetDock(passes, Dock.Left);
        root.Children.Add(passes);
        root.Children.Add(MainArea());
        Content = root;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        _liveViewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => _vm.TickLiveView());
        _liveViewTimer.Start();
        Closing += OnClosing;
        Closed += (_, _) => _liveViewTimer.Stop();
    }

    private Control TopBar()
    {
        var newScan = new Button { Content = "New scan", Command = Guarded(_vm.NewScanCommand, () => "", NewScanAsync) };
        var openScan = new Button { Content = "Open scan", Command = Guarded(_vm.OpenScanCommand, () => "", OpenScanAsync) };
        var deleteScan = new Button { Content = "Delete scan", Command = Guarded(_vm.DeleteScanCommand, () => null, DeleteScanAsync) };

        var source = new ComboBox { ItemsSource = Enum.GetValues<SourceKind>(), MinWidth = 130 };
        source[!SelectingItemsControl.SelectedItemProperty] =
            new Binding(nameof(WorkbenchViewModel.SelectedSource)) { Mode = BindingMode.TwoWay };

        var direction = new ToggleSwitch { OffContent = "Camera front", OnContent = "Camera back", IsChecked = _vm.SelectedDirection == PassDirection.CameraBack };
        direction.IsCheckedChanged += (_, _) =>
            _vm.SelectedDirection = direction.IsChecked == true ? PassDirection.CameraBack : PassDirection.CameraFront;

        var record = new Button { Content = "● Record pass", Command = _vm.RecordCommand };
        var stop = new Button { Content = "■ Stop", Command = _vm.StopCommand };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8) };
        bar.Children.AddRange([newScan, openScan, deleteScan, Label("Source"), source, Label("Direction"), direction, record, stop]);
        DockPanel.SetDock(bar, Dock.Top);
        return bar;
    }

    /// <summary>A button command that runs view-side work (dialogs) but is enabled exactly when the view model's command is.</summary>
    private static AsyncRelayCommand Guarded(ICommand vmCommand, Func<object?> parameter, Func<Task> run)
    {
        var command = new AsyncRelayCommand(run, () => vmCommand.CanExecute(parameter()));
        vmCommand.CanExecuteChanged += (_, _) => command.NotifyCanExecuteChanged();
        return command;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    private Control PassList()
    {
        var list = new ListBox
        {
            Width = 280,
            ItemsSource = _vm.Passes,
            ItemTemplate = new FuncDataTemplate<PassMetadata>((pass, _) => PassRow(pass)),
        };
        list[!SelectingItemsControl.SelectedItemProperty] =
            new Binding(nameof(WorkbenchViewModel.SelectedPass)) { Mode = BindingMode.TwoWay };
        var deletePass = new Button
        {
            Content = "Delete pass",
            Command = Guarded(_vm.DeletePassCommand, () => _vm.SelectedPass, DeletePassAsync),
        };

        var combine = new Button { Content = "Combine selected", Command = _vm.CombineCommand };
        var cancel = new Button { Content = "Cancel", Command = _vm.CancelCombineCommand };
        var combineButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        combineButtons.Children.AddRange([combine, cancel]);

        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
        progress[!RangeBase.ValueProperty] = new Binding(nameof(WorkbenchViewModel.CombineProgress));
        progress[!Visual.IsVisibleProperty] = new Binding(nameof(WorkbenchViewModel.IsCombining));
        var stage = new TextBlock { Foreground = Brushes.Gray };
        stage[!TextBlock.TextProperty] = new Binding(nameof(WorkbenchViewModel.CombineStage));
        stage[!Visual.IsVisibleProperty] = new Binding(nameof(WorkbenchViewModel.IsCombining));

        var results = new ListBox
        {
            MaxHeight = 150,
            ItemsSource = _vm.Results,
            ItemTemplate = new FuncDataTemplate<ScanApp.Core.Combine.CombineResult>(
                (r, _) => new TextBlock { Text = r is null ? "" : WorkbenchViewModel.DescribeResult(r), TextWrapping = TextWrapping.Wrap }),
        };
        results[!SelectingItemsControl.SelectedItemProperty] =
            new Binding(nameof(WorkbenchViewModel.SelectedResult)) { Mode = BindingMode.TwoWay };
        var deleteResult = new Button
        {
            Content = "Delete result",
            Command = Guarded(_vm.DeleteResultCommand, () => _vm.SelectedResult, DeleteResultAsync),
        };

        var below = new StackPanel { Margin = new Thickness(8), Spacing = 6 };
        below.Children.AddRange([deletePass, combineButtons, progress, stage, new TextBlock { Text = "Results" }, results, deleteResult]);
        DockPanel.SetDock(below, Dock.Bottom);
        var panel = new DockPanel { Width = 280 };
        panel.Children.AddRange([below, list]);
        return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    /// <summary>A pass list row; its text follows the view model's backward flags, which appear when an analysis completes.</summary>
    private Control PassRow(PassMetadata? pass)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var check = new CheckBox { MinWidth = 0, Margin = new Thickness(0, 0, 4, 0) };
        void SyncCheck() => check.IsChecked = pass is not null && _vm.CheckedPasses.Contains(pass.Id);
        check.IsCheckedChanged += (_, _) =>
        {
            if (pass is null) return;
            bool want = check.IsChecked == true, have = _vm.CheckedPasses.Contains(pass.Id);
            if (want && !have) _vm.CheckedPasses.Add(pass.Id);
            else if (!want && have) _vm.CheckedPasses.Remove(pass.Id);
        };
        void OnChecked(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => SyncCheck();
        SyncCheck();
        void Update() =>
            text.Text = pass is null ? "" : WorkbenchViewModel.DescribePass(pass, _vm.IsBackwardFlagged(pass.Id));
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.IsBackwardFlagged)) Update();
        }
        Update();
        text.AttachedToVisualTree += (_, _) =>
        {
            _vm.PropertyChanged += OnChanged;
            _vm.CheckedPasses.CollectionChanged += OnChecked;
            Update();
            SyncCheck();
        };
        text.DetachedFromVisualTree += (_, _) =>
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.CheckedPasses.CollectionChanged -= OnChecked;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.AddRange([check, text]);
        return row;
    }

    private async Task DeletePassAsync()
    {
        if (_vm.SelectedPass is not { } pass) return;
        string question = $"Permanently delete {WorkbenchViewModel.DescribePass(pass)}? This cannot be undone.";
        if (await new ConfirmDialog("Delete pass", question, "Delete pass").ShowDialog<bool>(this))
            await _vm.DeletePassCommand.ExecuteAsync(pass);
    }

    private async Task DeleteResultAsync()
    {
        if (_vm.SelectedResult is not { } result) return;
        string question = $"Permanently delete {WorkbenchViewModel.DescribeResult(result)}? This cannot be undone.";
        if (await new ConfirmDialog("Delete result", question, "Delete result").ShowDialog<bool>(this))
            await _vm.DeleteResultCommand.ExecuteAsync(result);
    }

    private async Task DeleteScanAsync()
    {
        if (_vm.Scan is not { } scan) return;
        string question = $"Permanently delete scan \"{scan.Info.Name}\" and its {scan.Passes.Count} passes? This cannot be undone.";
        if (await new ConfirmDialog("Delete scan", question, "Delete scan").ShowDialog<bool>(this))
            await _vm.DeleteScanCommand.ExecuteAsync(null);
    }

    private Control StatusLine()
    {
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        message[!TextBlock.TextProperty] = new Binding(nameof(WorkbenchViewModel.StatusMessage));
        var hover = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(16, 0, 0, 0) };
        hover[!TextBlock.TextProperty] = new Binding(nameof(WorkbenchViewModel.HoverReadout));
        DockPanel.SetDock(hover, Dock.Right);
        var line = new DockPanel { Margin = new Thickness(8, 4) };
        line.Children.AddRange([hover, message]);
        DockPanel.SetDock(line, Dock.Bottom);
        return line;
    }

    private Control MainArea()
    {
        var profile = new ProfileView { Height = 140 };
        profile[!ProfileView.ProfileProperty] = new Binding(nameof(WorkbenchViewModel.LatestProfile));
        DockPanel.SetDock(profile, Dock.Top);

        var toolbar = PassViewToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);

        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4 };
        progress[!RangeBase.ValueProperty] = new Binding(nameof(WorkbenchViewModel.PreviewProgress));
        progress[!Visual.IsVisibleProperty] = new Binding(nameof(WorkbenchViewModel.IsLoadingPreview));
        DockPanel.SetDock(progress, Dock.Top);

        _heightMap = new HeightMapView();
        _heightMap[!HeightMapView.PreviewProperty] = new Binding(nameof(WorkbenchViewModel.Preview));
        _heightMap[!HeightMapView.SpeedMmPerSProperty] = new Binding(nameof(WorkbenchViewModel.AssumedSpeedMmPerS));
        _heightMap[!HeightMapView.FlipHeightProperty] = new Binding(nameof(WorkbenchViewModel.FlipHeight));
        _heightMap[!HeightMapView.ShowReflectionsProperty] = new Binding(nameof(WorkbenchViewModel.ShowRemovedReflections));
        _heightMap[!HeightMapView.CanDragProperty] = new Binding(nameof(WorkbenchViewModel.CanEditTrim));
        _heightMap.Hovered += text => _vm.HoverReadout = text;
        _heightMap.TrimLineDragging += (kind, index) => { if (TrimSeconds(index) is { } s) _vm.PreviewLimit(kind, s); };
        _heightMap.TrimLineDragged += (kind, index) => { if (TrimSeconds(index) is { } s) _vm.CommitLimit(kind, s); };
        _heightMap.XLineDragging += (kind, mm) => _vm.PreviewLimit(kind, XLimit(kind, mm));
        _heightMap.XLineDragged += (kind, mm) => _vm.CommitLimit(kind, XLimit(kind, mm));
        _cloud = new PointCloudView { IsVisible = false };

        var noPoints = new TextBlock
        {
            Text = "No valid points in this pass",
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        noPoints[!Visual.IsVisibleProperty] = new Binding(nameof(WorkbenchViewModel.Preview))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<ScanApp.Core.Viewing.PassPreview?, bool>(p => p is { IsEmpty: true }),
        };

        var views = new Grid();
        views.Children.AddRange([_heightMap, _cloud, noPoints]);

        var resultLines = new StackPanel { Margin = new Thickness(8), Spacing = 2, IsVisible = false };
        _resultLines = resultLines;
        DockPanel.SetDock(resultLines, Dock.Bottom);

        var limits = new LimitsPanel(_vm) { Width = 380 };
        DockPanel.SetDock(limits, Dock.Right);

        var area = new DockPanel();
        area.Children.AddRange([profile, toolbar, progress, resultLines, limits, views]);
        return area;
    }

    private static StackPanel Group(params Control[] children)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        group.Children.AddRange(children);
        return group;
    }

    private Control PassViewToolbar()
    {
        var heightMap = new RadioButton { Content = "Height map", GroupName = "view", IsChecked = _vm.ViewMode == MainViewMode.HeightMap };
        var threeD = new RadioButton { Content = "3D", GroupName = "view", IsChecked = _vm.ViewMode == MainViewMode.ThreeD };
        heightMap.IsCheckedChanged += (_, _) => { if (heightMap.IsChecked == true) _vm.ViewMode = MainViewMode.HeightMap; };
        threeD.IsCheckedChanged += (_, _) => { if (threeD.IsChecked == true) _vm.ViewMode = MainViewMode.ThreeD; };

        var speed = new TextBox { Width = 70, Text = FormatSpeed(_vm.AssumedSpeedMmPerS) };
        void CommitSpeed()
        {
            _vm.AssumedSpeedMmPerS = double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ? v : 0;
            speed.Text = FormatSpeed(_vm.AssumedSpeedMmPerS);
        }
        speed.LostFocus += (_, _) => CommitSpeed();
        speed.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) CommitSpeed(); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.AssumedSpeedMmPerS)) speed.Text = FormatSpeed(_vm.AssumedSpeedMmPerS);
        };

        var flip = new CheckBox { Content = "Flip height" };
        flip[!ToggleButton.IsCheckedProperty] = new Binding(nameof(WorkbenchViewModel.FlipHeight)) { Mode = BindingMode.TwoWay };

        var reset = new Button { Content = "Reset view" };
        reset.Click += (_, _) => _cloud?.ResetView();
        reset.IsVisible = _vm.ViewMode == MainViewMode.ThreeD;   // from then on UpdateViews owns it

        var raw = new CheckBox { Content = "Show raw" };
        raw[!ToggleButton.IsCheckedProperty] = new Binding(nameof(WorkbenchViewModel.ShowRaw)) { Mode = BindingMode.TwoWay };
        var reflections = new CheckBox { Content = "Show removed reflections" };
        reflections[!ToggleButton.IsCheckedProperty] =
            new Binding(nameof(WorkbenchViewModel.ShowRemovedReflections)) { Mode = BindingMode.TwoWay };
        // Only the raw views mark removed reflections; its group's visibility already covers the result view.
        reflections[!Visual.IsVisibleProperty] = new Binding(nameof(WorkbenchViewModel.ShowRaw));

        var auto = new Button { Content = "Auto", Command = _vm.AutoTrimCommand };
        var hint = new TextBlock
        {
            Text = "No still reference in this pass — check the trim.",
            Foreground = Brushes.Orange,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint[!Visual.IsVisibleProperty] = new Binding(nameof(WorkbenchViewModel.TrimNeedsCheck));

        var colourHeight = new RadioButton { Content = "height", GroupName = "colour", IsChecked = !_vm.ColorByPass };
        var colourPass = new RadioButton { Content = "pass", GroupName = "colour", IsChecked = _vm.ColorByPass };
        colourPass.IsCheckedChanged += (_, _) => _vm.ColorByPass = colourPass.IsChecked == true;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(WorkbenchViewModel.ColorByPass)) return;
            colourPass.IsChecked = _vm.ColorByPass;
            colourHeight.IsChecked = !_vm.ColorByPass;
        };
        var colourLabel = Label("Colour:");
        var speedLabel = Label("Assumed speed (mm/s)");
        var clearZone = new Button { Content = "Clear zone", Command = _vm.ClearZoneCommand };

        // Whole groups are shown or hidden, never the tools inside them: a local IsVisible would replace the hint's binding.
        var passToolsA = Group(heightMap, threeD, speedLabel, speed);
        var passToolsB = Group(raw, reflections, auto, clearZone, hint);
        var resultTools = Group(colourLabel, colourHeight, colourPass);
        _passOnlyTools.AddRange([passToolsA, passToolsB]);
        _resultOnlyTools.Add(resultTools);
        resultTools.IsVisible = false;
        _resetButton = reset;

        // Wraps when the window is narrow.
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 0, 4) };
        Control[] items = [passToolsA, flip, reset, passToolsB, resultTools];
        foreach (var item in items) item.Margin = new Thickness(0, 0, 8, 4);
        bar.Children.AddRange(items);
        return bar;
    }

    /// <summary>A trim line's raw profile index in seconds from the first raw profile; null without an analysis.</summary>
    private double? TrimSeconds(int index) =>
        _vm.Analysis is { TimeS: { Length: > 0 } t } ? t[Math.Clamp(index, 0, t.Length - 1)] : null;

    /// <summary>A dragged X line in mm as a limit: on (or past) the range's own end it is no limit (null).</summary>
    private double? XLimit(LimitKind kind, double mm) => _vm.ScaleOf(kind) switch
    {
        { } scale when kind == LimitKind.XMin && mm <= scale.Min => null,
        { } scale when kind == LimitKind.XMax && mm >= scale.Max => null,
        _ => mm,
    };

    /// <summary>
    /// Feeds the height map the limits it cuts by and draws (<see cref="WorkbenchViewModel.ShownLimits"/>, null outside
    /// the raw view of a pass), the raw geometry, and the backward runs that overlap the trim.
    /// </summary>
    private void UpdateMapLimits()
    {
        if (_heightMap is null) return;
        var limits = _vm.ShownLimits;
        _heightMap.Limits = limits;
        _heightMap.Geometry = limits is null ? null : _vm.RawGeometry;
        _heightMap.BackwardRuns = limits is not null && _vm.Analysis is { } analysis
            ? analysis.BackwardRuns.Where(r => r.End >= limits.TrimStart && r.Start <= limits.TrimEnd).ToList()
            : null;
    }

    private static string FormatSpeed(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkbenchViewModel.ViewMode):
                UpdateViews();
                break;
            case nameof(WorkbenchViewModel.SelectedResult):
                if (_vm.SelectedResult is not null) _vm.ColorByPass = true;
                UpdateViews();
                UpdateMapLimits();
                break;
            case nameof(WorkbenchViewModel.ResultCloud):
                // A newly loaded cloud is framed.
                if (ShowingResult) UpdateCloud();
                break;
            case nameof(WorkbenchViewModel.ColorByPass):
                // Only the colours change: keep the operator's camera.
                if (ShowingResult) UpdateCloud(refit: false);
                break;
            case nameof(WorkbenchViewModel.Preview):
            case nameof(WorkbenchViewModel.AssumedSpeedMmPerS):
            case nameof(WorkbenchViewModel.FlipHeight):
                if (CloudVisible) UpdateCloud();
                break;
            case nameof(WorkbenchViewModel.ShownLimits):
            case nameof(WorkbenchViewModel.Analysis):
                UpdateMapLimits();
                // A limit change only moves the cut: uniforms and the box, no upload.
                if (CloudVisible && !ShowingResult) UpdateCloudCut();
                break;
            case nameof(WorkbenchViewModel.RawGeometry):
                UpdateMapLimits();
                if (CloudVisible && !ShowingResult)
                {
                    UpdateCloudRaw();
                    UpdateCloudCut();
                }
                break;
            case nameof(WorkbenchViewModel.RawTable):
            case nameof(WorkbenchViewModel.ShowRemovedReflections):
                if (CloudVisible && !ShowingResult) UpdateCloudCut();
                break;
        }
    }

    private bool ShowingResult => _vm.SelectedResult is not null;

    private bool CloudVisible => ShowingResult || _vm.ViewMode == MainViewMode.ThreeD;

    /// <summary>Shows the 3D view for a result or in 3D mode, else the height map; swaps the pass tools for the result tools.</summary>
    private void UpdateViews()
    {
        if (_heightMap is null || _cloud is null) return;
        bool result = ShowingResult;
        bool threeD = CloudVisible;
        _heightMap.IsVisible = !threeD;
        _cloud.IsVisible = threeD;
        foreach (var tool in _passOnlyTools) tool.IsVisible = !result;
        foreach (var tool in _resultOnlyTools) tool.IsVisible = result;
        if (_resetButton is not null) _resetButton.IsVisible = threeD;
        if (_resultLines is not null)
        {
            _resultLines.IsVisible = result;
            _resultLines.Children.Clear();
            if (_vm.SelectedResult is { } r)
            {
                foreach (string flag in WorkbenchViewModel.DescribeFlags(r))
                    _resultLines.Children.Add(new TextBlock { Text = flag, FontWeight = FontWeight.SemiBold });
                var lines = _vm.OutcomeLines(r);
                for (int i = 0; i < lines.Count; i++)
                    _resultLines.Children.Add(PassLegendRow(i, lines[i]));
            }
        }
        if (threeD) UpdateCloud();
    }

    /// <summary>One per-pass line with a swatch in the colour that pass has in the by-pass view.</summary>
    private static Control PassLegendRow(int passIndex, string text)
    {
        var (r, g, b) = PassPalette.Color(passIndex);
        var swatch = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange([swatch, new TextBlock { Text = text }]);
        return row;
    }

    /// <summary>
    /// Uploads the current preview or result to the 3D view; flip negates z here, and the renderer swaps the colours.
    /// <paramref name="refit"/> false keeps the camera (a colour-mode change).
    /// </summary>
    private void UpdateCloud(bool refit = true)
    {
        if (_cloud is null) return;
        if (ShowingResult)
        {
            if (_vm.ResultCloud is not { } merged)
            {
                _cloud.SetPoints([], [], 0, 1);
                return;
            }
            var (rxyz, rvalue, rmin, rmax) = PointCloudData.BuildMerged(merged, _vm.ColorByPass, _vm.FlipHeight);
            _cloud.SetPoints(rxyz, rvalue, rmin, rmax, refit);
            _cloud.SetColorMode(_vm.ColorByPass ? PointColorMode.Pass : PointColorMode.Height, _vm.FlipHeight);
            UpdateCloudRaw();
            UpdateCloudCut();
            return;
        }
        if (_vm.Preview is not { IsEmpty: false } preview)
        {
            _cloud.SetPoints([], [], 0, 1);
            UpdateCloudRaw();
            UpdateCloudCut();
            return;
        }
        var (xyz, value) = PointCloudData.Build(preview, _vm.AssumedSpeedMmPerS, _vm.FlipHeight);
        _cloud.SetPoints(xyz, value, preview.ZMin, preview.ZMax);
        _cloud.SetColorMode(PointColorMode.Height, _vm.FlipHeight);
        UpdateCloudRaw();
        UpdateCloudCut();
    }

    /// <summary>
    /// The raw view of a pass hands the 3D view each point's profile, height and flags (uploaded once, per raw
    /// geometry); the cleaned view and results get null and draw as before. Geometry that does not match the preview's
    /// points (one of them is still being refreshed) counts as none.
    /// </summary>
    private void UpdateCloudRaw()
    {
        if (_cloud is null) return;
        _cloud.SetRawAttributes(RawCloudPreview() is { } preview && _vm.RawGeometry is { } geometry
            ? PointCloudData.BuildRawAttributes(preview, geometry)
            : null);
    }

    /// <summary>The 3D view's cut and box from <see cref="WorkbenchViewModel.ShownLimits"/>; null outside the raw view.</summary>
    private void UpdateCloudCut()
    {
        if (_cloud is null) return;
        // The box shows as soon as the limits do; the points turn grey once the raw geometry has arrived.
        if (!ShowingResult && _vm.ShownLimits is { } limits && _vm.Analysis is { } analysis
            && _vm.Preview is { IsEmpty: false } preview)
        {
            var (cut, box) = PointCloudData.BuildCut(limits, _vm.ShowRemovedReflections, analysis, preview, _vm.RawTable,
                                                     _vm.AssumedSpeedMmPerS, _vm.FlipHeight);
            _cloud.SetCut(cut, box);
        }
        else _cloud.SetCut(null, null);
    }

    /// <summary>The preview the 3D view shows when it is the raw view of a pass with matching raw geometry, else null.</summary>
    private ScanApp.Core.Viewing.PassPreview? RawCloudPreview() =>
        !ShowingResult && _vm.ShownLimits is not null && _vm.Preview is { IsEmpty: false } preview
        && _vm.RawGeometry is { } geometry && geometry.PointHeight.Length == preview.Points.Count
            ? preview
            : null;

    private async Task NewScanAsync()
    {
        string? name = await new TextInputDialog("New scan", "Scan name").ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
            _vm.NewScanCommand.Execute(name.Trim());
    }

    private async Task OpenScanAsync()
    {
        var scans = _vm.ListScans();
        if (scans.Count == 0)
        {
            _vm.StatusMessage = $"No scans yet in {_vm.DataDirectory}.";
            return;
        }
        var dialog = new ScanListDialog(_vm.ListScans, name => _vm.DeleteScanByNameCommand.ExecuteAsync(name));
        string? chosen = await dialog.ShowDialog<string?>(this);
        if (chosen is not null)
            _vm.OpenScanCommand.Execute(chosen);
    }

    /// <summary>Closing while connecting or recording first stops, saves the pass and releases the scanner.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_vm.IsBusy) return;
        e.Cancel = true;
        try
        {
            await _vm.ShutdownAsync();
        }
        catch (Exception error)
        {
            _vm.StatusMessage = $"Could not stop cleanly: {error.Message}";
        }
        if (!_vm.IsBusy) Close();
    }

    /// <summary>A modal yes/no question; returns true only when the named action is confirmed.</summary>
    private sealed class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string question, string confirmText)
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var confirm = new Button { Content = confirmText };
            var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
            confirm.Click += (_, _) => Close(true);
            cancel.Click += (_, _) => Close(false);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.AddRange([confirm, cancel]);
            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
            panel.Children.AddRange([new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, buttons]);
            Content = panel;
        }
    }

    /// <summary>
    /// A modal list of scan names; returns the name to open, or null when cancelled. Delete removes the selected
    /// scan after confirmation without opening it, so scans that cannot be opened can still be deleted.
    /// </summary>
    private sealed class ScanListDialog : Window
    {
        public ScanListDialog(Func<IReadOnlyList<string>> listScans, Func<string, Task> delete)
        {
            Title = "Open scan";
            Width = 360;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var list = new ListBox { ItemsSource = listScans(), SelectedIndex = 0 };
            var open = new Button { Content = "Open", IsDefault = true };
            var deleteButton = new Button { Content = "Delete…" };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            open.Click += (_, _) => Close(list.SelectedItem as string);
            cancel.Click += (_, _) => Close(null);
            list.DoubleTapped += (_, _) => Close(list.SelectedItem as string);
            deleteButton.Click += async (_, _) =>
            {
                if (list.SelectedItem is not string name) return;
                string question = $"Permanently delete scan \"{name}\" and all its passes? This cannot be undone.";
                if (!await new ConfirmDialog("Delete scan", question, "Delete scan").ShowDialog<bool>(this)) return;
                await delete(name);
                var remaining = listScans();
                if (remaining.Count == 0) { Close(null); return; }
                list.ItemsSource = remaining;
                list.SelectedIndex = 0;
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.AddRange([deleteButton, open, cancel]);
            DockPanel.SetDock(buttons, Dock.Bottom);
            var panel = new DockPanel { Margin = new Thickness(16) };
            panel.Children.AddRange([buttons, list]);
            Content = panel;
            Opened += (_, _) => list.Focus();
        }
    }

    /// <summary>A minimal modal text prompt; returns the text, or null when cancelled.</summary>
    private sealed class TextInputDialog : Window
    {
        public TextInputDialog(string title, string prompt)
        {
            Title = title;
            Width = 360;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var input = new TextBox();
            var ok = new Button { Content = "OK", IsDefault = true };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            ok.Click += (_, _) => Close(input.Text);
            cancel.Click += (_, _) => Close(null);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.AddRange([ok, cancel]);
            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
            panel.Children.AddRange([new TextBlock { Text = prompt }, input, buttons]);
            Content = panel;
            Opened += (_, _) => input.Focus();
        }
    }
}
