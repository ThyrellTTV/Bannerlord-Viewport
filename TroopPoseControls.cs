using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private readonly ObservableCollection<PoseAnimation> _poseOptions = [];
    private readonly DispatcherTimer _posePlaybackTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private readonly Stopwatch _posePlaybackClock = new();
    private double _poseDisplayElapsed;
    private TroopPoses? _troopPoses;
    private PoseClip? _poseClip;
    private TextBox? _poseSearchBox;
    private string? _poseLibraryPath;
    private bool _isUpdatingPoseControls = true;
    private bool _poseAvailable;
    private volatile bool _poseClosed;
    private int _poseLibraryVersion;
    private int _poseSelectionVersion;
    private float _poseFrame;

    private void InitializeTroopPoseControls()
    {
        _poseOptions.Add(PoseAnimation.BindPose);
        PoseAnimationCombo.ItemsSource = _poseOptions;
        PoseAnimationCombo.SelectedIndex = 0;
        PoseAnimationCombo.Loaded += (_, _) =>
        {
            if (PoseAnimationCombo.Template.FindName("PART_EditableTextBox", PoseAnimationCombo) is TextBox box && !ReferenceEquals(box, _poseSearchBox))
            {
                if (_poseSearchBox != null) _poseSearchBox.TextChanged -= PoseSearch_TextChanged;
                _poseSearchBox = box;
                box.TextChanged += PoseSearch_TextChanged;
            }
        };
        _posePlaybackTimer.Tick += PosePlayback_Tick;
        Loaded += (_, _) => RefreshTroopPoseLibrary();
        Closed += (_, _) =>
        {
            _poseClosed = true;
            _poseLibraryVersion++;
            _poseSelectionVersion++;
            _posePlaybackTimer.Stop();
        };
        _isUpdatingPoseControls = false;
        RefreshTroopPoseAvailability();
    }

    private void RefreshTroopPoseLibrary()
    {
        if (_isUpdatingPoseControls || _poseClosed || !IsLoaded) return;
        foreach (var source in new[] { AssetPathBox.Text, TroopXmlPathBox.Text })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var directory = Directory.Exists(source) ? new DirectoryInfo(source) : new FileInfo(source).Directory;
            while (directory != null && !directory.Name.Equals("Modules", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
            if (directory == null) continue;
            var path = Path.Combine(directory.FullName, "Native", "AssetPackages");
            if (File.Exists(Path.Combine(path, "animations.tpac")) && File.Exists(Path.Combine(path, "core_game.tpac")))
            {
                _ = LoadTroopPoseLibraryAsync(path);
                return;
            }
        }
    }

    private async Task LoadTroopPoseLibraryAsync(string path)
    {
        if (string.Equals(_poseLibraryPath, path, StringComparison.OrdinalIgnoreCase)) return;
        var version = ++_poseLibraryVersion;
        _poseSelectionVersion++;
        StopPosePlayback();
        _poseClip = null;
        _poseFrame = 0;
        _troopPoses = null;
        _poseLibraryPath = path;
        _renderer.SetPose(null, null);
        RefreshTroopPoseAvailability();
        PoseStatusText.Text = "Loading animations...";
        try
        {
            var library = await Task.Run(() => new TroopPoses(path));
            if (_poseClosed) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_poseClosed || version != _poseLibraryVersion) return;
                _troopPoses = library;
                _isUpdatingPoseControls = true;
                _poseOptions.Clear();
                _poseOptions.Add(PoseAnimation.BindPose);
                foreach (var animation in library.Animations) _poseOptions.Add(animation);
                PoseAnimationCombo.SelectedIndex = 0;
                _isUpdatingPoseControls = false;
                if (_previewWorkspace == 1 && (_troopPreviewLoadout.ContainsKey("Item0") || _troopPreviewLoadout.ContainsKey("Item1")))
                    RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
                else RefreshTroopPoseAvailability();
            });
        }
        catch (Exception ex)
        {
            if (_poseClosed) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_poseClosed || version != _poseLibraryVersion) return;
                _poseLibraryPath = null;
                PoseStatusText.Text = "Animation assets could not be loaded.";
                PoseStatusText.ToolTip = ex.Message;
            });
        }
    }

    private async void ChoosePoseAnimations_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Native AssetPackages folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await LoadTroopPoseLibraryAsync(dialog.FolderName);
    }

    private void PoseSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingPoseControls || _troopPoses == null || sender is not TextBox box) return;
        if (PoseAnimationCombo.SelectedItem is PoseAnimation selected && box.Text == selected.DisplayName) return;
        var search = box.Text;
        var caret = box.CaretIndex;
        _isUpdatingPoseControls = true;
        _poseOptions.Clear();
        _poseOptions.Add(PoseAnimation.BindPose);
        foreach (var animation in _troopPoses.Animations.Where(animation => animation.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)))
            _poseOptions.Add(animation);
        box.Text = search;
        box.CaretIndex = Math.Min(caret, search.Length);
        PoseAnimationCombo.IsDropDownOpen = true;
        _isUpdatingPoseControls = false;
    }

    private async void PoseAnimationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPoseControls || PoseAnimationCombo.SelectedItem is not PoseAnimation selected) return;
        PoseAnimationCombo.ToolTip = selected.DisplayName;
        var version = ++_poseSelectionVersion;
        StopPosePlayback();
        _poseClip = null;
        _poseFrame = 0;
        _isUpdatingPoseControls = true;
        PoseFrameSlider.Minimum = 0;
        PoseFrameSlider.Maximum = 1;
        PoseFrameSlider.Value = 0;
        _isUpdatingPoseControls = false;
        _renderer.SetPose(null, null);
        RefreshTroopPoseAvailability();
        if (selected.Source == null || _troopPoses == null) return;
        var library = _troopPoses;
        PoseStatusText.Text = "Loading pose...";
        try
        {
            var clip = await Task.Run(() => library.Load(selected));
            if (_poseClosed) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_poseClosed || version != _poseSelectionVersion) return;
                _poseClip = clip;
                _poseFrame = clip.FirstFrame;
                _isUpdatingPoseControls = true;
                PoseFrameSlider.Minimum = 0;
                PoseFrameSlider.Maximum = clip.LastFrame;
                PoseFrameSlider.Minimum = clip.FirstFrame;
                PoseFrameSlider.Value = _poseFrame;
                _isUpdatingPoseControls = false;
                RefreshTroopPoseAvailability();
            });
        }
        catch (Exception ex)
        {
            if (_poseClosed) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_poseClosed || version != _poseSelectionVersion) return;
                PoseStatusText.Text = "This animation could not be posed.";
                PoseStatusText.ToolTip = ex.Message;
            });
        }
    }

    private void RefreshTroopPoseAvailability()
    {
        if (_isUpdatingPoseControls || _poseClosed) return;
        _poseAvailable = _previewWorkspace == 1 && WorkspaceTabs.SelectedIndex == 1 &&
            _troopPoses != null && StudioRenderer.CanPose(_assetModel, _troopPoses.BoneCount);
        PoseAnimationCombo.IsEnabled = _poseAvailable;
        PoseResetButton.IsEnabled = _poseAvailable;
        PoseFrameSlider.IsEnabled = PoseFrameBox.IsEnabled = _poseAvailable && _poseClip != null;
        PosePlayButton.IsEnabled = _poseAvailable && _poseClip != null && _poseClip.LastFrame > _poseClip.FirstFrame;
        if (!_poseAvailable) StopPosePlayback();
        PoseStatusText.Text = _troopPoses == null ? "Animation library unavailable." :
            !_poseAvailable ? "No compatible troop equipment." : "";
        PoseStatusText.ToolTip = null;
        UpdatePoseFrameDisplay();
        if (_poseAvailable) ApplyTroopPose();
    }

    private void SuspendTroopPose()
    {
        if (_isUpdatingPoseControls) return;
        StopPosePlayback();
        _heldEquipmentModels.Clear();
        _renderer.SetPose(null, null);
        RefreshTroopPoseAvailability();
    }

    private void ApplyTroopPose()
    {
        if (_batchRenderRunning) return;
        if (_poseAvailable && _poseClip != null && _troopPoses != null)
            _renderer.SetPose(_assetModel, _troopPoses.Evaluate(_poseClip, _poseFrame));
        else _renderer.SetPose(null, null);
    }

    private void UpdatePoseFrameDisplay()
    {
        PoseFrameBox.Text = _poseFrame.ToString("0.##", CultureInfo.InvariantCulture);
        PoseFrameRangeText.Text = "/ " + (_poseClip?.LastFrame ?? 0).ToString("0", CultureInfo.InvariantCulture);
    }

    private void PoseFrameSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingPoseControls || !_poseAvailable || _poseClip == null) return;
        StopPosePlayback();
        _poseFrame = (float)e.NewValue;
        UpdatePoseFrameDisplay();
        ApplyTroopPose();
    }

    private void PoseFrameBox_LostFocus(object sender, RoutedEventArgs e) => CommitPoseFrame();

    private void PoseFrameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => StopPosePlayback();

    private void PoseFrameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitPoseFrame();
        e.Handled = true;
    }

    private void CommitPoseFrame()
    {
        if (_poseClip == null || !_poseAvailable) return;
        if (float.TryParse(PoseFrameBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var frame) && float.IsFinite(frame))
        {
            StopPosePlayback();
            _poseFrame = Math.Clamp(frame, _poseClip.FirstFrame, _poseClip.LastFrame);
            _isUpdatingPoseControls = true;
            PoseFrameSlider.Value = _poseFrame;
            _isUpdatingPoseControls = false;
            ApplyTroopPose();
        }
        UpdatePoseFrameDisplay();
    }

    private void PosePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_posePlaybackTimer.IsEnabled) { StopPosePlayback(); return; }
        if (!_poseAvailable || _poseClip == null || _poseClip.LastFrame <= _poseClip.FirstFrame) return;
        if (_poseFrame >= _poseClip.LastFrame) _poseFrame = _poseClip.FirstFrame;
        _poseDisplayElapsed = 0;
        _posePlaybackClock.Restart();
        _posePlaybackTimer.Start();
        PosePlayIcon.Text = "\uE769";
        PosePlayButton.ToolTip = "Pause animation";
    }

    private void PosePlayback_Tick(object? sender, EventArgs e)
    {
        if (!_poseAvailable || _poseClip == null) { StopPosePlayback(); return; }
        var seconds = _posePlaybackClock.Elapsed.TotalSeconds;
        _posePlaybackClock.Restart();
        _poseDisplayElapsed += seconds;
        var speed = PoseSpeedCombo.SelectedItem is ComboBoxItem item
            ? double.Parse((string)item.Tag, CultureInfo.InvariantCulture) : 1;
        _poseFrame += (float)(seconds * 30 * speed);
        if (_poseFrame > _poseClip.LastFrame)
        {
            if (PoseLoopCheckBox.IsChecked == true)
                _poseFrame = _poseClip.FirstFrame + (_poseFrame - _poseClip.FirstFrame) % (_poseClip.LastFrame - _poseClip.FirstFrame);
            else { _poseFrame = _poseClip.LastFrame; StopPosePlayback(); }
        }
        _isUpdatingPoseControls = true;
        PoseFrameSlider.Value = _poseFrame;
        _isUpdatingPoseControls = false;
        ApplyTroopPose();
        // Text edits trigger WPF layout; the mesh and slider can update more often than the readout.
        if (_poseDisplayElapsed >= 0.1 || !_posePlaybackTimer.IsEnabled)
        {
            _poseDisplayElapsed = 0;
            UpdatePoseFrameDisplay();
        }
    }

    private void StopPosePlayback()
    {
        _posePlaybackTimer.Stop();
        _posePlaybackClock.Reset();
        UpdatePoseFrameDisplay();
        PosePlayIcon.Text = "\uE768";
        PosePlayButton.ToolTip = "Play animation";
    }

    private void PoseReset_Click(object sender, RoutedEventArgs e)
    {
        _equipmentPoseSelection = null;
        _isUpdatingPoseControls = true;
        _poseOptions.Clear();
        _poseOptions.Add(PoseAnimation.BindPose);
        if (_troopPoses != null) foreach (var animation in _troopPoses.Animations) _poseOptions.Add(animation);
        PoseAnimationCombo.SelectedIndex = 0;
        _isUpdatingPoseControls = false;
        PoseAnimationCombo_SelectionChanged(PoseAnimationCombo,
            new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
    }
}
