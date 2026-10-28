using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml.Linq;

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

    private void BatchCraftedTurntableExport_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRenderRunning || _turntableRunning || _assetExportRunning) return;
        var items = GetBatchCraftedWeapons();
        if (items.Length == 0)
        {
            MessageBox.Show(this, "Load an item XML file or folder containing CraftedItem definitions first.",
                "Batch Crafted Turntables", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_craftingPieces.Count == 0 && _equipmentCraftingPieces.Count == 0)
        {
            MessageBox.Show(this, "Load the crafting piece XML used by these items first.",
                "Batch Crafted Turntables", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _batchCraftingTurntableOptions ??= (_batchTurntableOptions ?? _turntableOptions ?? new TurntableOptions
        { OutputDirectory = Path.Combine(SettingsDirectory, "Renders"), FfmpegPath = FindFfmpeg() }) with
        { Name = "crafted-turntables", AutoFrame = true, Filtered = false, OrganiseByPackage = false };
        var dialog = new TurntableExportWindow(this, _batchCraftingTurntableOptions,
            async (options, progress, cancellation) =>
            {
                _batchCraftingTurntableOptions = options;
                SaveSettingsIfEnabled();
                return await RunBatchCraftedTurntableExportAsync(items, options, progress, cancellation);
            }, items.Length, 0, "Crafted weapons", false, false);
        dialog.OptionsChanged += options => { _batchCraftingTurntableOptions = options; SaveSettingsIfEnabled(); };
        dialog.ShowDialog();
        if (dialog.TryGetOptions(out var edited)) { _batchCraftingTurntableOptions = edited; SaveSettingsIfEnabled(); }
    }

    private CraftedWeaponBatchItem[] GetBatchCraftedWeapons()
    {
        var manuallyLoaded = _craftingItemsSourceLoaded;
        var items = manuallyLoaded ? _craftingItems : _equipmentItems;
        return items.Where(pair => pair.Value.Name.LocalName.Equals("CraftedItem", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new CraftedWeaponBatchItem(pair.Key, pair.Value,
                manuallyLoaded ? _craftingItemSourcePaths.GetValueOrDefault(pair.Key) ?? "" :
                    _equipmentItemSourcePaths.GetValueOrDefault(pair.Key) ?? ""))
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<TurntableResult> RunBatchCraftedTurntableExportAsync(CraftedWeaponBatchItem[] items, TurntableOptions options,
        IProgress<TurntableProgress> progress, CancellationToken cancellation)
    {
        options.Validate();
        if (_batchRenderRunning || _turntableRunning || _assetExportRunning) throw new InvalidOperationException("An export is already running.");
        if (items.Length == 0) throw new ArgumentException("No crafted weapons are available.");
        var totalFrames = checked(items.Length * options.FrameCount);
        if (cancellation.IsCancellationRequested) return new(0, totalFrames, true, null);
        if (options.Video)
        {
            if (!File.Exists(options.FfmpegPath)) throw new ArgumentException("Choose an FFmpeg executable for MP4 export.");
            try { await VerifyFfmpegAsync(options.FfmpegPath, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return new(0, totalFrames, true, null); }
        }
        var directory = Path.Combine(Path.GetFullPath(options.OutputDirectory), SafeBatchName(options.Name));
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "batch-crafted-turntables.json");
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
            for (var index = 0; index < items.Length; index++)
            {
                if (cancellation.IsCancellationRequested) break;
                var item = items[index];
                progress.Report(new(0, options.FrameCount, item.Id, index + 1, items.Length, saved, skipped, failed));
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (cancellation.IsCancellationRequested) break;
                var itemFrames = 0;
                try
                {
                    var destination = Path.Combine(directory, SafeBatchName(item.Id) + (options.Video ? ".mp4" : ""));
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        skipped++;
                        entries.Add(new { Item = item.Id, Source = item.SourcePath, Output = destination, Status = "Skipped", Reason = "Output already exists." });
                        continue;
                    }
                    var model = LoadCraftedEquipmentModel(item.Definition);
                    var bounds = model.Bounds;
                    if (bounds.IsEmpty || Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ)) <= 0)
                        throw new InvalidOperationException("Crafted weapon bounds are empty.");
                    model.Transform = CreateFitTransform(bounds);
                    _assetModel.Children.Clear(); _assetModel.Transform = Transform3D.Identity; _assetModel.Children.Add(model);
                    _renderer.SetColours(_tableauPrimaryColour, _tableauSecondaryColour, StudioRenderer.HasFactionColourBlending(_assetModel));
                    var itemOptions = options with { OutputDirectory = directory, Name = item.Id, AutoFrame = true };
                    var itemIndex = index;
                    var itemProgress = new InlineTurntableProgress(value =>
                    {
                        itemFrames = value.Completed;
                        progress.Report(value with
                        { Mesh = item.Id, MeshIndex = itemIndex + 1, MeshTotal = items.Length, Saved = saved, Skipped = skipped, Failed = failed });
                    });
                    var result = await RunTurntableExportAsync(itemOptions, itemProgress, cancellation, batchMember: true);
                    frames += result.Completed;
                    if (result.Cancelled)
                    {
                        entries.Add(new { Item = item.Id, Source = item.SourcePath, Output = result.OutputPath, Status = "Cancelled", Frames = result.Completed });
                        break;
                    }
                    saved++;
                    entries.Add(new { Item = item.Id, Source = item.SourcePath, Output = result.OutputPath, Status = "Saved", Frames = result.Completed });
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    failed++;
                    entries.Add(new { Item = item.Id, Source = item.SourcePath, Status = "Failed", Reason = exception.Message });
                }
                finally
                {
                    foreach (var key in _lookupPackageCache.Keys.Where(key => !lookupKeys.Contains(key)).ToArray()) _lookupPackageCache.Remove(key);
                    progress.Report(new(itemFrames, options.FrameCount, item.Id, index + 1, items.Length, saved, skipped, failed));
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
            CreatedUtc = DateTime.UtcNow, Total = items.Length, Saved = saved, Skipped = skipped, Failed = failed,
            Cancelled = cancellation.IsCancellationRequested, Frames = frames, Settings = options, Items = entries
        }, RenderSettingsJsonOptions));
        return new(frames, totalFrames, cancellation.IsCancellationRequested, directory, saved, skipped, failed, reportPath);
    }

    private sealed class InlineTurntableProgress(Action<TurntableProgress> report) : IProgress<TurntableProgress>
    { public void Report(TurntableProgress value) => report(value); }

    private sealed record CraftedWeaponBatchItem(string Id, XElement Definition, string SourcePath);
}
