using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LOTRAOM_Viewport;

public partial class AssetExportWindow : Window
{
    private readonly Func<AssetExportOptions, IProgress<string>, CancellationToken, Task<AssetExportResult>> _export;
    private readonly bool _troop, _hasSkeleton;
    private CancellationTokenSource? _cancellation;
    private string? _output;
    private bool _ready;
    internal event Action<AssetExportOptions>? OptionsChanged;

    internal AssetExportWindow(MainWindow owner, AssetExportOptions options, bool troop, bool hasSkeleton,
        Func<AssetExportOptions, IProgress<string>, CancellationToken, Task<AssetExportResult>> export)
    {
        Resources.MergedDictionaries.Add(owner.Resources);
        InitializeComponent();
        Owner = owner;
        _export = export; _troop = troop; _hasSkeleton = hasSkeleton;
        FolderBox.Text = options.OutputDirectory; NameBox.Text = options.Name;
        FbxCheck.IsChecked = options.Fbx; TexturesCheck.IsChecked = options.Textures;
        FormatCombo.SelectedIndex = options.TextureFormat;
        LodCombo.SelectedIndex = !troop && options.AllLods ? 1 : 0;
        LodCombo.IsEnabled = !troop;
        if (troop) LodCombo.ToolTip = "The displayed highest-detail loadout meshes are exported.";
        PosePanel.Visibility = troop ? Visibility.Visible : Visibility.Collapsed;
        PoseCombo.SelectedIndex = options.CurrentPose ? 1 : 0;
        SkeletonCheck.IsChecked = options.Skeleton;
        SkeletonCheck.ToolTip = hasSkeleton ? "Export the native human rig and available vertex weights." :
            "Requires the native human skeleton and a recognised human armour mesh or troop loadout.";
        FolderBox.TextChanged += Changed; NameBox.TextChanged += Changed;
        FormatCombo.SelectionChanged += Changed; LodCombo.SelectionChanged += Changed; PoseCombo.SelectionChanged += Changed;
        foreach (var check in new[] { FbxCheck, TexturesCheck, SkeletonCheck })
        { check.Checked += Changed; check.Unchecked += Changed; }
        _ready = true;
        UpdateControls();
    }

    private AssetExportOptions ReadOptions() => new()
    {
        OutputDirectory = FolderBox.Text.Trim(), Name = NameBox.Text.Trim(), Fbx = FbxCheck.IsChecked == true,
        Textures = TexturesCheck.IsChecked == true, TextureFormat = FormatCombo.SelectedIndex,
        AllLods = !_troop && LodCombo.SelectedIndex == 1, CurrentPose = _troop && PoseCombo.SelectedIndex == 1,
        Skeleton = _hasSkeleton && SkeletonCheck.IsChecked == true
    };

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _cancellation != null) return;
        UpdateControls();
        try { var options = ReadOptions(); options.Validate(); OptionsChanged?.Invoke(options); }
        catch (ArgumentException) { }
    }

    private void UpdateControls()
    {
        FormatCombo.IsEnabled = TexturesCheck.IsChecked == true;
        SkeletonCheck.IsEnabled = _hasSkeleton && (!_troop || PoseCombo.SelectedIndex == 0) && FbxCheck.IsChecked == true;
        PoseCombo.IsEnabled = FbxCheck.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose export folder" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation != null) return;
        AssetExportOptions options;
        try { options = ReadOptions(); options.Validate(); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        OptionsChanged?.Invoke(options);
        _cancellation = new();
        SettingsPanel.IsEnabled = ExportButton.IsEnabled = OpenButton.IsEnabled = false;
        CancelButton.Content = "Cancel"; ProgressBar.Visibility = Visibility.Visible; ProgressBar.IsIndeterminate = true;
        try
        {
            var result = await _export(options, new Progress<string>(message =>
            { if (_cancellation != null) StatusText.Text = message; }), _cancellation.Token);
            _output = result.Directory;
            StatusText.Text = $"Exported {result.Meshes} mesh parts and {result.Textures} textures. " +
                (result.Warnings == 0 ? "" : $"{result.Warnings} warnings; see export.json.");
            OpenButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { StatusText.Text = "Export cancelled. No partial output was retained."; }
        catch (Exception ex) { StatusText.Text = "Export failed: " + ex.Message; }
        finally
        {
            _cancellation.Dispose(); _cancellation = null;
            SettingsPanel.IsEnabled = ExportButton.IsEnabled = true;
            CancelButton.Content = "Close"; ProgressBar.IsIndeterminate = false; ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    { if (_cancellation != null) { StatusText.Text = "Cancelling..."; _cancellation.Cancel(); } else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_cancellation != null) { e.Cancel = true; _cancellation.Cancel(); } }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_output == null || !Directory.Exists(_output)) return;
        try { Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
