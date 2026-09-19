using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LOTRAOM_Viewport;

public partial class TurntableExportWindow : Window
{
    private readonly Func<TurntableOptions, IProgress<TurntableProgress>, CancellationToken, Task<TurntableResult>> _render;
    private readonly bool _batch;
    private CancellationTokenSource? _cancellation;
    private bool _updating = true;
    private bool _running;
    private string? _outputDirectory;
    internal event Action<TurntableOptions>? OptionsChanged;

    internal TurntableExportWindow(MainWindow owner, TurntableOptions options,
        Func<TurntableOptions, IProgress<TurntableProgress>, CancellationToken, Task<TurntableResult>> render,
        int? allCount = null, int filteredCount = 0)
    {
        Resources.MergedDictionaries.Add(owner.Resources);
        InitializeComponent();
        Owner = owner;
        _render = render;
        _batch = allCount != null;
        if (_batch)
        {
            Title = HeaderText.Text = "Batch Turntables";
            BatchScopePanel.Visibility = Visibility.Visible;
            ScopeCombo.Items.Add($"All meshes ({allCount:N0})");
            ScopeCombo.Items.Add($"Current search ({filteredCount:N0})");
            ScopeCombo.SelectedIndex = options.Filtered ? 1 : 0;
            OrganiseCheckBox.IsChecked = options.OrganiseByPackage;
            AutoFrameCheckBox.Visibility = Visibility.Collapsed;
        }
        FormatCombo.SelectedIndex = options.Video ? 1 : 0;
        OutputFolderBox.Text = options.OutputDirectory;
        NameBox.Text = options.Name;
        FfmpegBox.Text = options.FfmpegPath;
        WidthBox.Text = options.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = options.Height.ToString(CultureInfo.InvariantCulture);
        SecondsBox.Text = options.Seconds.ToString(CultureInfo.InvariantCulture);
        FpsBox.Text = options.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        BackgroundCombo.SelectedIndex = options.Transparent ? 0 : 1;
        GridCheckBox.IsChecked = options.IncludeGrid;
        ElevationBox.Text = options.Elevation.ToString(CultureInfo.InvariantCulture);
        StartAngleBox.Text = options.StartAngle.ToString(CultureInfo.InvariantCulture);
        DirectionCombo.SelectedIndex = options.Clockwise ? 0 : 1;
        AutoFrameCheckBox.IsChecked = options.AutoFrame;
        _updating = false;
        UpdateFormat();
        ProgressText.Text = "Ready";
        SettingsPanel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => NotifyOptionsChanged()));
        SettingsPanel.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, _) => NotifyOptionsChanged()));
        SettingsPanel.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => NotifyOptionsChanged()));
        SettingsPanel.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => NotifyOptionsChanged()));
    }

    private void NotifyOptionsChanged()
    {
        if (!_updating && !_running && TryGetOptions(out var options)) OptionsChanged?.Invoke(options!);
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) UpdateFormat();
    }

    private void UpdateFormat()
    {
        var video = FormatCombo.SelectedIndex == 1;
        EncoderPanel.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        BackgroundCombo.IsEnabled = !video;
        if (video) BackgroundCombo.SelectedIndex = 1;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose turntable output folder" };
        if (Directory.Exists(OutputFolderBox.Text)) dialog.InitialDirectory = OutputFolderBox.Text;
        if (dialog.ShowDialog(this) == true) OutputFolderBox.Text = dialog.FolderName;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose FFmpeg executable", Filter = "Executable (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) FfmpegBox.Text = dialog.FileName;
    }

    private TurntableOptions ReadOptions()
    {
        int Number(TextBox box) => int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var options = new TurntableOptions
        {
            OutputDirectory = OutputFolderBox.Text.Trim(), Name = NameBox.Text.Trim(), Video = FormatCombo.SelectedIndex == 1,
            FfmpegPath = FfmpegBox.Text.Trim(), Width = Number(WidthBox), Height = Number(HeightBox),
            Seconds = Number(SecondsBox), FramesPerSecond = Number(FpsBox), Transparent = BackgroundCombo.SelectedIndex == 0,
            IncludeGrid = GridCheckBox.IsChecked == true, Clockwise = DirectionCombo.SelectedIndex == 0,
            AutoFrame = AutoFrameCheckBox.IsChecked == true,
            Filtered = _batch && ScopeCombo.SelectedIndex == 1,
            OrganiseByPackage = OrganiseCheckBox.IsChecked == true,
            StartAngle = double.TryParse(StartAngleBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) ? angle : double.NaN,
            Elevation = double.TryParse(ElevationBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var elevation) ? elevation : double.NaN
        };
        options.Validate();
        return _batch ? options with { AutoFrame = true } : options;
    }

    internal bool TryGetOptions(out TurntableOptions? options)
    {
        try { options = ReadOptions(); return true; }
        catch (ArgumentException) { options = null; return false; }
    }

    private async void StartExport_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        TurntableOptions options;
        try
        {
            options = ReadOptions();
            if (options.Video && !File.Exists(options.FfmpegPath)) throw new ArgumentException("Choose an FFmpeg executable for MP4 export.");
        }
        catch (ArgumentException exception) { StatusText.Text = exception.Message; return; }
        _running = true;
        _cancellation = new();
        SettingsPanel.IsEnabled = RunButton.IsEnabled = OpenOutputButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        StatusText.Text = "";
        ExportProgressBar.Maximum = options.FrameCount;
        ExportProgressBar.Value = 0;
        ProgressText.Text = $"0 / {options.FrameCount:N0} frames";
        try
        {
            var progress = new Progress<TurntableProgress>(value =>
            {
                if (!_running) return;
                ExportProgressBar.Maximum = Math.Max(1, value.Total);
                ExportProgressBar.Value = value.Completed;
                ProgressText.Text = value.MeshTotal > 0
                    ? $"Mesh {value.MeshIndex:N0} / {value.MeshTotal:N0} - {value.Saved:N0} saved, {value.Skipped:N0} skipped, {value.Failed:N0} failed"
                    : $"{value.Completed:N0} / {value.Total:N0} frames";
                CurrentMeshText.Text = value.MeshTotal > 0 ? $"{value.Mesh} - {value.Completed:N0} / {value.Total:N0} frames" : "";
                StatusText.Text = value.Completed == value.Total && options.Video ? "Finalising video..." : "";
            });
            var result = await _render(options, progress, _cancellation.Token);
            _outputDirectory = result.OutputPath == null ? null : _batch ? result.OutputPath : options.Video
                ? Path.GetDirectoryName(result.OutputPath) : result.OutputPath;
            StatusText.Text = _batch
                ? $"{(result.Cancelled ? "Cancelled" : "Finished")}: {result.Saved:N0} saved, {result.Skipped:N0} skipped, {result.Failed:N0} failed. Report: {Path.GetFileName(result.ReportPath)}"
                : result.Cancelled
                ? $"Cancelled. {result.Completed:N0} frames rendered." + (!options.Video ? " Partial PNG sequence retained." : "")
                : $"Saved {result.Completed:N0} frames. {Path.GetFileName(result.OutputPath)}";
        }
        catch (Exception exception) { StatusText.Text = "Export failed: " + exception.Message; }
        finally
        {
            _running = false;
            _cancellation.Dispose();
            _cancellation = null;
            SettingsPanel.IsEnabled = RunButton.IsEnabled = CancelButton.IsEnabled = true;
            OpenOutputButton.IsEnabled = _outputDirectory != null;
            CancelButton.Content = "Close";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running) CancelExport();
        else Close();
    }

    private void CancelExport()
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true;
        CancelExport();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_outputDirectory != null && Directory.Exists(_outputDirectory))
            Process.Start(new ProcessStartInfo(_outputDirectory) { UseShellExecute = true });
    }
}
