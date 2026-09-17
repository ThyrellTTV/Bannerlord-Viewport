using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace LOTRAOM_Viewport;

public partial class BatchRenderWindow : Window
{
    private readonly Func<BatchRenderOptions, IProgress<BatchRenderProgress>, CancellationToken, Task<BatchRenderResult>> _render;
    private readonly int[] _counts;
    private CancellationTokenSource? _cancellation;
    private bool _updating = true;
    private bool _running;
    private string? _completedOutputDirectory;

    internal BatchRenderWindow(MainWindow owner, BatchRenderOptions options, int allCount, int filteredCount,
        Func<BatchRenderOptions, IProgress<BatchRenderProgress>, CancellationToken, Task<BatchRenderResult>> render)
    {
        Resources.MergedDictionaries.Add(owner.Resources);
        InitializeComponent();
        Owner = owner;
        _render = render;
        _counts = [allCount, filteredCount];
        ScopeCombo.Items.Add($"All parsed meshes ({allCount:N0})");
        ScopeCombo.Items.Add($"Filtered meshes ({filteredCount:N0})");
        ScopeCombo.SelectedIndex = options.Filtered ? 1 : 0;
        OutputFolderBox.Text = options.OutputDirectory;
        WidthBox.Text = options.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = options.Height.ToString(CultureInfo.InvariantCulture);
        SizePresetCombo.SelectedIndex = (options.Width, options.Height) switch
        {
            (512, 512) => 0, (1024, 1024) => 1, (2048, 2048) => 2, (2048, 3072) => 3, (3840, 2160) => 4, _ => 5
        };
        ExistingFilesCombo.SelectedIndex = options.Overwrite ? 1 : 0;
        BackgroundCombo.SelectedIndex = options.Transparent ? 0 : 1;
        GridCheckBox.IsChecked = options.IncludeGrid;
        OrganiseCheckBox.IsChecked = options.OrganiseByPackage;
        CameraPresetCombo.SelectedIndex = options.Elevation != 12 ? 5 : options.Yaw switch
        {
            180 => 0, 0 => 1, 90 => 2, 270 => 3, 145 => 4, _ => 5
        };
        YawBox.Text = options.Yaw.ToString(CultureInfo.InvariantCulture);
        ElevationBox.Text = options.Elevation.ToString(CultureInfo.InvariantCulture);
        FieldOfViewBox.Text = options.FieldOfView.ToString(CultureInfo.InvariantCulture);
        FillSlider.Value = options.Fill * 100;
        LightSlider.Value = options.LightIntensity;
        FactionColoursCheckBox.IsChecked = options.UseFactionColours;
        PrimaryHexBox.Text = Hex(options.PrimaryColour);
        SecondaryHexBox.Text = Hex(options.SecondaryColour);
        _updating = false;
        RefreshSwatches();
        ProgressText.Text = "Ready";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose batch render output folder", Multiselect = false };
        if (Directory.Exists(OutputFolderBox.Text)) dialog.InitialDirectory = OutputFolderBox.Text;
        if (dialog.ShowDialog(this) == true) OutputFolderBox.Text = dialog.FolderName;
    }

    private void SizePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SizePresetCombo.SelectedIndex == 5) return;
        var size = SizePresetCombo.SelectedIndex switch
        {
            0 => (512, 512), 1 => (1024, 1024), 2 => (2048, 2048), 3 => (2048, 3072), _ => (3840, 2160)
        };
        _updating = true;
        WidthBox.Text = size.Item1.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = size.Item2.ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void Dimensions_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) SizePresetCombo.SelectedIndex = 5;
    }

    private void CameraPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || CameraPresetCombo.SelectedIndex == 5) return;
        _updating = true;
        YawBox.Text = (CameraPresetCombo.SelectedIndex switch { 0 => 180, 1 => 0, 2 => 90, 3 => 270, _ => 145 }).ToString(CultureInfo.InvariantCulture);
        ElevationBox.Text = "12";
        _updating = false;
    }

    private void Camera_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) CameraPresetCombo.SelectedIndex = 5;
    }

    private void Colour_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) RefreshSwatches();
    }

    private void RefreshSwatches()
    {
        foreach (var (box, swatch) in new[] { (PrimaryHexBox, PrimarySwatch), (SecondaryHexBox, SecondarySwatch) })
        {
            if (TryColour(box.Text, out var colour))
            {
                box.ClearValue(Control.BorderBrushProperty);
                swatch.Background = new SolidColorBrush(colour);
            }
            else box.BorderBrush = Brushes.IndianRed;
        }
    }

    private void ColourPicker_Click(object sender, RoutedEventArgs e)
    {
        var box = Equals(((Button)sender).Tag, "Primary") ? PrimaryHexBox : SecondaryHexBox;
        if (!TryColour(box.Text, out var colour)) colour = Colors.White;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true, Color = System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B)
        };
        var nativeOwner = new System.Windows.Forms.NativeWindow();
        nativeOwner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (dialog.ShowDialog(nativeOwner) == System.Windows.Forms.DialogResult.OK)
                box.Text = Hex(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        }
        finally { nativeOwner.ReleaseHandle(); }
    }

    private BatchRenderOptions ReadOptions()
    {
        int Dimension(TextBox box) => int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        double Angle(TextBox box) => double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
        var useColours = FactionColoursCheckBox.IsChecked == true;
        var validPrimary = TryColour(PrimaryHexBox.Text, out var primary);
        var validSecondary = TryColour(SecondaryHexBox.Text, out var secondary);
        if (useColours && (!validPrimary || !validSecondary)) throw new ArgumentException("Enter six-digit hex codes for both faction colours.");
        var options = new BatchRenderOptions
        {
            OutputDirectory = OutputFolderBox.Text.Trim(), Filtered = ScopeCombo.SelectedIndex == 1,
            Width = Dimension(WidthBox), Height = Dimension(HeightBox), Transparent = BackgroundCombo.SelectedIndex == 0,
            IncludeGrid = GridCheckBox.IsChecked == true, OrganiseByPackage = OrganiseCheckBox.IsChecked == true,
            Overwrite = ExistingFilesCombo.SelectedIndex == 1, Yaw = Angle(YawBox), Elevation = Angle(ElevationBox),
            FieldOfView = Angle(FieldOfViewBox), Fill = FillSlider.Value / 100, LightIntensity = (float)LightSlider.Value,
            UseFactionColours = useColours, PrimaryColour = validPrimary ? primary : Colors.White,
            SecondaryColour = validSecondary ? secondary : Colors.White
        };
        options.Validate();
        if (_counts[options.Filtered ? 1 : 0] == 0) throw new ArgumentException("No meshes match the selected scope.");
        return options;
    }

    private async void StartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        BatchRenderOptions options;
        try { options = ReadOptions(); }
        catch (Exception exception) { StatusText.Text = exception.Message; return; }
        _running = true;
        _cancellation = new CancellationTokenSource();
        SettingsPanel.IsEnabled = RunButton.IsEnabled = OpenOutputButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Cancel";
        StatusText.Text = "";
        BatchProgressBar.Value = 0;
        BatchProgressBar.Maximum = _counts[options.Filtered ? 1 : 0];
        var progress = new Progress<BatchRenderProgress>(value =>
        {
            BatchProgressBar.Maximum = Math.Max(1, value.Total);
            BatchProgressBar.Value = value.Completed;
            ProgressText.Text = $"{value.Completed:N0} / {value.Total:N0}   Saved {value.Saved:N0}   Skipped {value.Skipped:N0}   Failed {value.Failed:N0}";
            CurrentMeshText.Text = value.Current;
        });
        try
        {
            var result = await _render(options, progress, _cancellation.Token);
            _completedOutputDirectory = Path.GetFullPath(options.OutputDirectory);
            StatusText.Text = $"{(result.Cancelled ? "Cancelled" : "Complete")}. Report: {Path.GetFileName(result.ReportPath)}";
            CurrentMeshText.Text = "";
        }
        catch (Exception exception) { StatusText.Text = "Batch failed: " + exception.Message; }
        finally
        {
            _running = false;
            _cancellation.Dispose();
            _cancellation = null;
            SettingsPanel.IsEnabled = RunButton.IsEnabled = CancelButton.IsEnabled = true;
            OpenOutputButton.IsEnabled = _completedOutputDirectory != null;
            CancelButton.Content = "Close";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) { Close(); return; }
        CancelBatch();
    }

    private void CancelBatch()
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true;
        CancelBatch();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_completedOutputDirectory != null && Directory.Exists(_completedOutputDirectory))
            Process.Start(new ProcessStartInfo(_completedOutputDirectory) { UseShellExecute = true });
    }

    private static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private static bool TryColour(string text, out Color colour)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            colour = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
        colour = default;
        return false;
    }
}
