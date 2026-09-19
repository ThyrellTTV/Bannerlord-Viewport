using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private void BatchTurntableExport_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRenderRunning || _turntableRunning || _assetExportRunning) return;
        var all = GetBatchRenderAssets(false);
        if (all.Length == 0)
        {
            MessageBox.Show(this, "No parsed mesh assets are available.", "Batch Turntables", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var filtered = GetBatchRenderAssets(true);
        _batchTurntableOptions ??= (_turntableOptions ?? new TurntableOptions
        { OutputDirectory = Path.Combine(SettingsDirectory, "Renders"), FfmpegPath = FindFfmpeg() }) with { Name = "batch-turntables", AutoFrame = true };
        var dialog = new TurntableExportWindow(this, _batchTurntableOptions, async (options, progress, cancellation) =>
        {
            _batchTurntableOptions = options;
            SaveSettingsIfEnabled();
            return await RunBatchTurntableExportAsync(options.Filtered ? filtered : all, options, progress, cancellation);
        }, all.Length, filtered.Length);
        dialog.OptionsChanged += options => { _batchTurntableOptions = options; SaveSettingsIfEnabled(); };
        dialog.ShowDialog();
        if (dialog.TryGetOptions(out var edited)) { _batchTurntableOptions = edited; SaveSettingsIfEnabled(); }
    }

    private async Task<TurntableResult> RunBatchTurntableExportAsync(MeshAssetNode[] assets, TurntableOptions options,
        IProgress<TurntableProgress> progress, CancellationToken cancellation)
    {
        options.Validate();
        if (_batchRenderRunning || _turntableRunning || _assetExportRunning) throw new InvalidOperationException("An export is already running.");
        if (assets.Length == 0) throw new ArgumentException("No meshes match the selected scope.");
        var totalFrames = checked(assets.Length * options.FrameCount);
        if (cancellation.IsCancellationRequested) return new(0, totalFrames, true, null);
        if (options.Video)
        {
            if (!File.Exists(options.FfmpegPath)) throw new ArgumentException("Choose an FFmpeg executable for MP4 export.");
            try { await VerifyFfmpegAsync(options.FfmpegPath, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return new(0, totalFrames, true, null); }
        }
        var directory = Path.Combine(Path.GetFullPath(options.OutputDirectory), SafeBatchName(options.Name));
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "batch-turntables.json");
        var models = _assetModel.Children.ToArray();
        var transform = _assetModel.Transform;
        var pose = _renderer.PoseState;
        var camera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)ViewportCamera.CloneCurrentValue();
        var lookupKeys = _lookupPackageCache.Keys.ToHashSet();
        var playing = _posePlaybackTimer.IsEnabled;
        var orbiting = _isOrbiting;
        var saved = 0; var skipped = 0; var failed = 0; var frames = 0;
        var entries = new List<object>();
        _batchRenderRunning = true;
        StopPosePlayback(); _tableauColourTimer.Stop(); _isOrbiting = false;
        _renderer.SetPose(null, null);
        try
        {
            for (var index = 0; index < assets.Length; index++)
            {
                if (cancellation.IsCancellationRequested) break;
                var asset = assets[index];
                progress.Report(new(0, options.FrameCount, asset.DisplayName, index + 1, assets.Length, saved, skipped, failed));
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (cancellation.IsCancellationRequested) break;
                var meshFrames = 0;
                try
                {
                    if (!asset.CanRender || asset.MeshGuid == Guid.Empty)
                    {
                        skipped++;
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, asset.MetameshGuid, Status = "Skipped", Reason = "No renderable geometry stream." });
                        continue;
                    }
                    var output = options.OrganiseByPackage ? Path.GetDirectoryName(GetBatchRenderPath(directory, asset, true))! : directory;
                    var destination = Path.Combine(output, SafeBatchName(asset.DisplayName) + (options.Video ? ".mp4" : ""));
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        skipped++;
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, asset.MetameshGuid, Output = destination, Status = "Skipped", Reason = "Output already exists." });
                        continue;
                    }
                    var model = LoadMeshModel(asset, out _, MeshRenderMode.Raw);
                    var bounds = model.Bounds;
                    var extent = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
                    if (bounds.IsEmpty || extent <= 0) throw new InvalidOperationException("Mesh bounds are empty.");
                    var fit = CreateFitTransform(bounds).Value;
                    fit.Translate(new Vector3D(0, bounds.SizeY * 4.2 / extent / 2 - 1.35, 0));
                    model.Transform = new MatrixTransform3D(fit);
                    _assetModel.Children.Clear(); _assetModel.Transform = Transform3D.Identity; _assetModel.Children.Add(model);
                    _renderer.SetColours(_tableauPrimaryColour, _tableauSecondaryColour, StudioRenderer.HasFactionColourBlending(_assetModel));
                    var meshOptions = options with { OutputDirectory = output, Name = asset.DisplayName, AutoFrame = true };
                    var meshIndex = index;
                    var meshProgress = new InlineTurntableProgress(value =>
                    {
                        meshFrames = value.Completed;
                        progress.Report(value with
                        { Mesh = asset.DisplayName, MeshIndex = meshIndex + 1, MeshTotal = assets.Length, Saved = saved, Skipped = skipped, Failed = failed });
                    });
                    var result = await RunTurntableExportAsync(meshOptions, meshProgress, cancellation, batchMember: true);
                    frames += result.Completed;
                    if (result.Cancelled)
                    {
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, asset.MetameshGuid, Output = result.OutputPath, Status = "Cancelled", Frames = result.Completed });
                        break;
                    }
                    saved++;
                    entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, asset.MetameshGuid, Output = result.OutputPath, Status = "Saved", Frames = result.Completed });
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    failed++;
                    entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, asset.MetameshGuid, Status = "Failed", Reason = exception.Message });
                }
                finally
                {
                    foreach (var key in _lookupPackageCache.Keys.Where(key => !lookupKeys.Contains(key)).ToArray()) _lookupPackageCache.Remove(key);
                    progress.Report(new(meshFrames, options.FrameCount, asset.DisplayName, index + 1, assets.Length, saved, skipped, failed));
                }
            }
        }
        finally
        {
            _assetModel.Children.Clear(); _assetModel.Transform = transform;
            foreach (var model in models) _assetModel.Children.Add(model);
            ViewportCamera.Position = camera.Position; ViewportCamera.LookDirection = camera.LookDirection;
            ViewportCamera.UpDirection = camera.UpDirection; ViewportCamera.FieldOfView = camera.FieldOfView;
            ViewportCamera.NearPlaneDistance = camera.NearPlaneDistance; ViewportCamera.FarPlaneDistance = camera.FarPlaneDistance;
            _isOrbiting = orbiting; _batchRenderRunning = false;
            UpdateTableauColourEditorVisibility(); _renderer.SetPose(pose.Model, pose.Matrices);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (playing && _poseAvailable && _poseClip != null) PosePlay_Click(this, new RoutedEventArgs());
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            CreatedUtc = DateTime.UtcNow, Total = assets.Length, Saved = saved, Skipped = skipped, Failed = failed,
            Cancelled = cancellation.IsCancellationRequested, Frames = frames, Settings = options, Meshes = entries
        }, RenderSettingsJsonOptions));
        return new(frames, totalFrames, cancellation.IsCancellationRequested, directory, saved, skipped, failed, reportPath);
    }

    private sealed class InlineTurntableProgress(Action<TurntableProgress> report) : IProgress<TurntableProgress>
    { public void Report(TurntableProgress value) => report(value); }
}
