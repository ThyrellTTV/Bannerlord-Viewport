using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private bool _batchRenderRunning;
    private BatchRenderOptions? _batchRenderOptions;

    private MeshAssetNode[] GetBatchRenderAssets(bool filtered)
    {
        var query = filtered ? AssetSearchBox.Text.Trim() : "";
        return _allPackages.SelectMany(package => package.Assets)
            .Where(asset => asset.Kind == "Mesh" && asset.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(asset => (Path.GetFullPath(asset.SourcePath).ToUpperInvariant(), asset.MetameshGuid))
            .OrderBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void BatchRender_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRenderRunning || _turntableRunning || _assetExportRunning) return;
        var all = GetBatchRenderAssets(false);
        if (all.Length == 0)
        {
            MessageBox.Show(this, "No parsed mesh assets are available.", "Batch Render", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _batchRenderOptions ??= new BatchRenderOptions
        {
            OutputDirectory = Path.Combine(SettingsDirectory, "Renders"),
            PrimaryColour = _tableauPrimaryColour, SecondaryColour = _tableauSecondaryColour
        };
        var filtered = GetBatchRenderAssets(true);
        var dialog = new BatchRenderWindow(this, _batchRenderOptions, all.Length, filtered.Length,
            async (options, progress, cancellation) =>
            {
                _batchRenderOptions = options;
                SaveSettingsIfEnabled();
                return await RunBatchRenderAsync(options.Filtered ? filtered : all, options, progress, cancellation);
            });
        dialog.OptionsChanged += options => { _batchRenderOptions = options; SaveSettingsIfEnabled(); };
        dialog.ShowDialog();
        if (dialog.TryGetOptions(out var edited))
        {
            _batchRenderOptions = edited;
            SaveSettingsIfEnabled();
        }
    }

    private async Task<BatchRenderResult> RunBatchRenderAsync(MeshAssetNode[] assets, BatchRenderOptions options,
        IProgress<BatchRenderProgress> progress, CancellationToken cancellation)
    {
        if (_batchRenderRunning) throw new InvalidOperationException("A batch render is already running.");
        options.Validate();
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var reportPath = Path.Combine(outputDirectory, "batch-report.json");
        var originalModels = _assetModel.Children.ToArray();
        var originalTransform = _assetModel.Transform;
        var originalPose = _renderer.PoseState;
        var camera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)ViewportCamera.CloneCurrentValue();
        var originalLookupKeys = _lookupPackageCache.Keys.ToHashSet();
        var resumePlayback = _posePlaybackTimer.IsEnabled;
        var oldOrbiting = _isOrbiting;
        var completed = 0; var saved = 0; var skipped = 0; var failed = 0;
        var entries = new List<object>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _batchRenderRunning = true;
        StopPosePlayback();
        _tableauColourTimer.Stop();
        _isOrbiting = false;
        _renderer.SetPose(null, null);
        try
        {
            foreach (var asset in assets)
            {
                if (cancellation.IsCancellationRequested) break;
                progress.Report(new(completed, assets.Length, saved, skipped, failed, asset.DisplayName));
                // Give render updates, progress, and cancellation a turn between meshes.
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (cancellation.IsCancellationRequested) break;
                string? destination = null;
                try
                {
                    destination = GetBatchRenderPath(outputDirectory, asset, options.OrganiseByPackage);
                    if (!asset.CanRender || asset.MeshGuid == Guid.Empty)
                    {
                        skipped++;
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, Status = "Skipped", Reason = "No renderable geometry stream." });
                    }
                    else if (!destinations.Add(destination))
                    {
                        skipped++;
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, Output = destination, Status = "Skipped", Reason = "Another mesh in this batch uses the same output name." });
                    }
                    else if (!options.Overwrite && File.Exists(destination))
                    {
                        skipped++;
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, Output = destination, Status = "Skipped", Reason = "File already exists." });
                    }
                    else
                    {
                        var model = LoadMeshModel(asset, out _, MeshRenderMode.Raw);
                        if (model.Bounds.IsEmpty || Math.Max(model.Bounds.SizeX, Math.Max(model.Bounds.SizeY, model.Bounds.SizeZ)) <= 0)
                            throw new InvalidOperationException("Mesh bounds are empty.");
                        var height = model.Bounds.SizeY * 4.2 / Math.Max(model.Bounds.SizeX, Math.Max(model.Bounds.SizeY, model.Bounds.SizeZ));
                        var fit = CreateFitTransform(model.Bounds).Value;
                        fit.Translate(new Vector3D(0, height / 2 - 1.35, 0));
                        model.Transform = new MatrixTransform3D(fit);
                        _assetModel.Children.Clear();
                        _assetModel.Transform = Transform3D.Identity;
                        _assetModel.Children.Add(model);
                        _renderer.SetColours(options.PrimaryColour, options.SecondaryColour, options.UseFactionColours);
                        FrameBatchMesh(model.Bounds, options);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        if (cancellation.IsCancellationRequested) break;
                        _renderer.ScaleLighting(options.LightIntensity);
                        var bitmap = _renderer.CaptureRender(options.Width, options.Height, options.Transparent, options.IncludeGrid);
                        if (cancellation.IsCancellationRequested) break;
                        await Task.Run(() =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using (var stream = new FileStream(temporary, FileMode.CreateNew)) encoder.Save(stream);
                                File.Move(temporary, destination, options.Overwrite);
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        });
                        saved++;
                        entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, Output = destination, Status = "Saved" });
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    entries.Add(new { Mesh = asset.DisplayName, Package = asset.SourcePath, Output = destination, Status = "Failed", Reason = exception.Message });
                }
                finally
                {
                    foreach (var key in _lookupPackageCache.Keys.Where(key => !originalLookupKeys.Contains(key)).ToArray())
                        _lookupPackageCache.Remove(key);
                }
                completed++;
                progress.Report(new(completed, assets.Length, saved, skipped, failed, asset.DisplayName));
            }
        }
        finally
        {
            _assetModel.Children.Clear();
            _assetModel.Transform = originalTransform;
            foreach (var model in originalModels) _assetModel.Children.Add(model);
            ViewportCamera.Position = camera.Position;
            ViewportCamera.LookDirection = camera.LookDirection;
            ViewportCamera.UpDirection = camera.UpDirection;
            ViewportCamera.FieldOfView = camera.FieldOfView;
            ViewportCamera.NearPlaneDistance = camera.NearPlaneDistance;
            ViewportCamera.FarPlaneDistance = camera.FarPlaneDistance;
            _isOrbiting = oldOrbiting;
            _batchRenderRunning = false;
            UpdateTableauColourEditorVisibility();
            _renderer.SetPose(originalPose.Model, originalPose.Matrices);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (resumePlayback && _poseAvailable && _poseClip != null) PosePlay_Click(this, new RoutedEventArgs());
        }
        var cancelled = cancellation.IsCancellationRequested;
        var report = JsonSerializer.Serialize(new
        {
            CreatedUtc = DateTime.UtcNow, Total = assets.Length, Completed = completed,
            Saved = saved, Skipped = skipped, Failed = failed, Cancelled = cancelled,
            Settings = new { options.Width, options.Height, options.Transparent, options.IncludeGrid, options.Yaw, options.Elevation,
                options.FieldOfView, options.Fill, options.LightIntensity, options.UseFactionColours,
                PrimaryColour = options.PrimaryColour.ToString(), SecondaryColour = options.SecondaryColour.ToString(),
                options.OrganiseByPackage, options.Overwrite }, Meshes = entries
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, report);
        return new(saved, skipped, failed, cancelled, reportPath);
    }

    private void FrameBatchMesh(Rect3D bounds, BatchRenderOptions options)
    {
        var target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var yaw = options.Yaw * Math.PI / 180;
        var pitch = options.Elevation * Math.PI / 180;
        var outward = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
        var forward = -outward;
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0)); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        var vertical = Math.Tan(options.FieldOfView * Math.PI / 360) * options.Fill;
        var horizontal = vertical * options.Width / options.Height;
        var distance = 0.1;
        // Fit every bounds corner in camera space, including depth, at the requested image aspect ratio.
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        for (var z = 0; z < 2; z++)
        {
            var offset = new Vector3D((x - 0.5) * bounds.SizeX, (y - 0.5) * bounds.SizeY, (z - 0.5) * bounds.SizeZ);
            var depth = Vector3D.DotProduct(offset, forward);
            distance = Math.Max(distance, Math.Max(Math.Abs(Vector3D.DotProduct(offset, right)) / horizontal - depth,
                Math.Abs(Vector3D.DotProduct(offset, up)) / vertical - depth));
            distance = Math.Max(distance, 0.1 - depth);
        }
        ViewportCamera.FieldOfView = options.FieldOfView;
        ViewportCamera.Position = target + outward * distance;
        ViewportCamera.LookDirection = -outward * distance;
        ViewportCamera.UpDirection = up;
        ViewportCamera.NearPlaneDistance = 0.01;
        ViewportCamera.FarPlaneDistance = Math.Max(100, distance * 4);
    }

    private static string GetBatchRenderPath(string outputDirectory, MeshAssetNode asset, bool organise)
    {
        var source = Path.GetFullPath(asset.SourcePath);
        if (organise)
        {
            var module = new FileInfo(source).Directory;
            while (module?.Parent != null && !module.Parent.Name.Equals("Modules", StringComparison.OrdinalIgnoreCase)) module = module.Parent;
            outputDirectory = Path.Combine(outputDirectory, SafeBatchName(module?.Parent == null ? "Assets" : module.Name),
                SafeBatchName(asset.PackageName));
        }
        return Path.Combine(outputDirectory, SafeBatchName(asset.DisplayName) + ".png");
    }

    private static string SafeBatchName(string name)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "unnamed";
        if (name.Length > 64) name = name[..64].TrimEnd(' ', '.');
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')) name = "_" + name;
        return name;
    }
}

public sealed record BatchRenderOptions
{
    public string OutputDirectory { get; init; } = "";
    public bool Filtered { get; init; }
    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public bool Transparent { get; init; } = true;
    public bool IncludeGrid { get; init; }
    public double Yaw { get; init; } = 180;
    public double Elevation { get; init; } = 12;
    public double FieldOfView { get; init; } = 45;
    public double Fill { get; init; } = 0.86;
    public float LightIntensity { get; init; } = 1;
    public bool UseFactionColours { get; init; } = true;
    public Color PrimaryColour { get; init; } = Colors.White;
    public Color SecondaryColour { get; init; } = Colors.White;
    public bool OrganiseByPackage { get; init; } = true;
    public bool Overwrite { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw new ArgumentException("Choose an output folder.");
        if (Width < 64 || Height < 64 || Width > 8192 || Height > 8192 || (long)Width * Height > 33554432)
            throw new ArgumentException("Use dimensions from 64 to 8192 pixels, up to 32 megapixels.");
        if (!double.IsFinite(Yaw) || Yaw < -360 || Yaw > 360 || !double.IsFinite(Elevation) || Elevation < -75 || Elevation > 75)
            throw new ArgumentException("Use yaw from -360 to 360 and elevation from -75 to 75 degrees.");
        if (!double.IsFinite(FieldOfView) || FieldOfView < 10 || FieldOfView > 90)
            throw new ArgumentException("Use a field of view from 10 to 90 degrees.");
        if (!double.IsFinite(Fill) || Fill < 0.5 || Fill > 0.95 || !float.IsFinite(LightIntensity) || LightIntensity < 0.25 || LightIntensity > 2)
            throw new ArgumentException("Framing or lighting is outside the supported range.");
    }
}

internal sealed record BatchRenderProgress(int Completed, int Total, int Saved, int Skipped, int Failed, string Current);
internal sealed record BatchRenderResult(int Saved, int Skipped, int Failed, bool Cancelled, string ReportPath);
