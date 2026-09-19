using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private TurntableOptions? _turntableOptions;
    private TurntableOptions? _batchTurntableOptions;
    private bool _turntableRunning;

    private void TurntableExport_Click(object sender, RoutedEventArgs e)
    {
        if (_turntableRunning || _batchRenderRunning || _assetExportRunning) return;
        if (_assetModel.Children.Count == 0 || _assetModel.Bounds.IsEmpty)
        {
            RenderExportStatusText.Text = "Select a model or loadout first.";
            return;
        }
        RenderExportToggle.IsChecked = false;
        _turntableOptions ??= new() { OutputDirectory = Path.Combine(SettingsDirectory, "Renders"),
            Name = SafeBatchName(SelectedAssetTitle.Text), FfmpegPath = FindFfmpeg() };
        _turntableOptions = _turntableOptions with { Name = SafeBatchName(SelectedAssetTitle.Text) };
        var dialog = new TurntableExportWindow(this, _turntableOptions, async (options, progress, cancellation) =>
        {
            _turntableOptions = options;
            SaveSettingsIfEnabled();
            return await RunTurntableExportAsync(options, progress, cancellation);
        });
        dialog.OptionsChanged += options => { _turntableOptions = options; SaveSettingsIfEnabled(); };
        dialog.ShowDialog();
        if (dialog.TryGetOptions(out var edited))
        {
            _turntableOptions = edited;
            SaveSettingsIfEnabled();
        }
    }

    private static string FindFfmpeg()
    {
        foreach (var directory in new[] { AppContext.BaseDirectory }.Concat(
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
            if (File.Exists(path)) return path;
        }
        return "";
    }

    private async Task<TurntableResult> RunTurntableExportAsync(TurntableOptions options,
        IProgress<TurntableProgress> progress, CancellationToken cancellation, bool batchMember = false)
    {
        options.Validate();
        if (_turntableRunning || (_batchRenderRunning && !batchMember) || _assetExportRunning) throw new InvalidOperationException("An export is already running.");
        var bounds = _renderer.GetRenderBounds(_assetModel);
        if (bounds.IsEmpty || _assetModel.Children.Count == 0) throw new InvalidOperationException("No model is selected.");
        if (options.Video && !File.Exists(options.FfmpegPath)) throw new ArgumentException("Choose an FFmpeg executable for MP4 export.");
        if (cancellation.IsCancellationRequested) return new(0, options.FrameCount, true, null);
        var camera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)ViewportCamera.CloneCurrentValue();
        var resumePlayback = _posePlaybackTimer.IsEnabled;
        var oldOrbiting = _isOrbiting;
        var root = Path.GetFullPath(options.OutputDirectory);
        var destination = Path.Combine(root, SafeBatchName(options.Name) + (options.Video ? ".mp4" : ""));
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Output already exists. Choose another output name or folder: " + destination);
        Directory.CreateDirectory(root);
        var temporaryVideo = destination + $".{Guid.NewGuid():N}.tmp.mp4";
        Process? encoder = null;
        Task<string>? encoderErrors = null;
        Task? encoderOutput = null;
        CancellationTokenRegistration encoderCancellation = default;
        var completed = 0;
        _turntableRunning = true;
        StopPosePlayback();
        _isOrbiting = false;
        try
        {
            if (options.Video)
            {
                if (!batchMember) await VerifyFfmpegAsync(options.FfmpegPath, cancellation);
                var start = new ProcessStartInfo(options.FfmpegPath)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
                };
                foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "rawvideo",
                    "-pixel_format", "bgra", "-video_size", $"{options.Width}x{options.Height}", "-framerate",
                    options.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0", "-an", "-c:v", "libx264",
                    "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-vf", "scale=out_color_matrix=bt709",
                    "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1",
                    "-movflags", "+faststart", temporaryVideo })
                    start.ArgumentList.Add(argument);
                encoder = Process.Start(start) ?? throw new IOException("FFmpeg could not be started.");
                encoderErrors = encoder.StandardError.ReadToEndAsync();
                encoderOutput = encoder.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                encoderCancellation = cancellation.Register(() =>
                {
                    try { if (!encoder.HasExited) encoder.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                });
            }
            else Directory.CreateDirectory(destination);

            var target = options.AutoFrame
                ? new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2)
                : camera.Position + camera.LookDirection;
            var distance = options.AutoFrame
                ? TurntableFitDistance(bounds, options.Width / (double)options.Height, camera.FieldOfView, options.Elevation)
                : camera.LookDirection.Length;
            var startYaw = options.StartAngle * Math.PI / 180;
            var pitch = options.Elevation * Math.PI / 180;
            var pixels = options.Video ? new byte[options.Width * options.Height * 4] : null;
            ViewportCamera.NearPlaneDistance = 0.01;
            ViewportCamera.FarPlaneDistance = Math.Max(camera.FarPlaneDistance, distance * 4);
            // Exclude the duplicate 360-degree frame so the finished turntable loops without a pause.
            for (var frame = 0; frame < options.FrameCount; frame++)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                cancellation.ThrowIfCancellationRequested();
                var yaw = startYaw + (options.Clockwise ? 1 : -1) * 2 * Math.PI * frame / options.FrameCount;
                var outward = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
                var right = Vector3D.CrossProduct(-outward, new Vector3D(0, 1, 0)); right.Normalize();
                ViewportCamera.Position = target + outward * distance;
                ViewportCamera.LookDirection = -outward * distance;
                ViewportCamera.UpDirection = Vector3D.CrossProduct(right, -outward);
                var bitmap = _renderer.CaptureRender(options.Width, options.Height, options.Transparent, options.IncludeGrid);
                cancellation.ThrowIfCancellationRequested();
                if (encoder != null)
                {
                    bitmap.CopyPixels(pixels!, options.Width * 4, 0);
                    await encoder.StandardInput.BaseStream.WriteAsync(pixels!, cancellation);
                }
                else
                {
                    var path = Path.Combine(destination, $"frame-{frame:D6}.png");
                    await Task.Run(() =>
                    {
                        var temporary = path + ".tmp";
                        try
                        {
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            using (var stream = new FileStream(temporary, FileMode.CreateNew)) png.Save(stream);
                            File.Move(temporary, path);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    });
                }
                progress.Report(new(++completed, options.FrameCount));
            }
            if (encoder != null)
            {
                encoder.StandardInput.Close();
                await encoder.WaitForExitAsync(cancellation);
                if (encoder.ExitCode != 0) throw new IOException("FFmpeg: " + (await encoderErrors!).Trim());
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporaryVideo, destination);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) when (cancellation.IsCancellationRequested) { }
        catch (IOException exception) when (encoder is { HasExited: true } && encoderErrors != null)
        {
            var error = (await encoderErrors).Trim();
            throw new IOException(string.IsNullOrEmpty(error) ? exception.Message : "FFmpeg: " + error, exception);
        }
        catch
        {
            if (!options.Video && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
        finally
        {
            try
            {
                encoderCancellation.Dispose();
                if (encoder != null)
                {
                    try
                    {
                        if (!encoder.HasExited) encoder.Kill(entireProcessTree: true);
                        await encoder.WaitForExitAsync();
                        if (encoderErrors != null) await encoderErrors;
                        if (encoderOutput != null) await encoderOutput;
                    }
                    finally { encoder.Dispose(); }
                }
                if (File.Exists(temporaryVideo)) File.Delete(temporaryVideo);
            }
            finally
            {
                ViewportCamera.Position = camera.Position;
                ViewportCamera.LookDirection = camera.LookDirection;
                ViewportCamera.UpDirection = camera.UpDirection;
                ViewportCamera.FieldOfView = camera.FieldOfView;
                ViewportCamera.NearPlaneDistance = camera.NearPlaneDistance;
                ViewportCamera.FarPlaneDistance = camera.FarPlaneDistance;
                _isOrbiting = oldOrbiting;
                _turntableRunning = false;
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (resumePlayback && _poseAvailable && _poseClip != null) PosePlay_Click(this, new RoutedEventArgs());
            }
        }
        if (!options.Video)
            await File.WriteAllTextAsync(Path.Combine(destination, "turntable.json"), JsonSerializer.Serialize(new
            {
                Settings = options, Frames = completed, Cancelled = cancellation.IsCancellationRequested,
                FramePattern = "frame-%06d.png"
            }, RenderSettingsJsonOptions));
        return new(completed, options.FrameCount, cancellation.IsCancellationRequested,
            !options.Video || File.Exists(destination) ? destination : null);
    }

    private static async Task VerifyFfmpegAsync(string path, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start) ?? throw new IOException("FFmpeg could not be started.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !(await output).StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The selected executable is not FFmpeg.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException("FFmpeg did not respond to the version check.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output;
            await errors;
        }
    }

    internal static double TurntableFitDistance(Rect3D bounds, double aspect, double fieldOfView, double elevation)
    {
        var vertical = Math.Tan(fieldOfView * Math.PI / 360) * 0.86;
        var horizontal = vertical * aspect;
        var pitch = elevation * Math.PI / 180;
        var sin = Math.Sin(pitch); var cos = Math.Cos(pitch);
        var radius = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeZ * bounds.SizeZ) / 2;
        var height = bounds.SizeY / 2;
        // Maximum camera-space extents over the entire orbit, not only the starting view.
        var distance = radius * Math.Sqrt(cos * cos + 1 / (horizontal * horizontal)) + Math.Abs(sin) * height;
        foreach (var sign in new[] { -1, 1 })
            distance = Math.Max(distance, radius * Math.Abs(cos - sign * sin / vertical) +
                height * Math.Abs(sin + sign * cos / vertical));
        return Math.Max(distance, 0.1 + cos * radius + Math.Abs(sin) * height);
    }
}

public sealed record TurntableOptions
{
    public string OutputDirectory { get; init; } = "";
    public string Name { get; init; } = "turntable";
    public bool Video { get; init; }
    public string FfmpegPath { get; init; } = "";
    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public int Seconds { get; init; } = 8;
    public int FramesPerSecond { get; init; } = 30;
    public bool Transparent { get; init; } = true;
    public bool IncludeGrid { get; init; }
    public double Elevation { get; init; } = 12;
    public double StartAngle { get; init; } = 180;
    public bool Clockwise { get; init; } = true;
    public bool AutoFrame { get; init; } = true;
    public bool Filtered { get; init; }
    public bool OrganiseByPackage { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public int FrameCount => Seconds * FramesPerSecond;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw new ArgumentException("Choose an output folder.");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter an output name.");
        if (Width < 64 || Height < 64 || Width > 8192 || Height > 8192 || (long)Width * Height > 33554432)
            throw new ArgumentException("Use dimensions from 64 to 8192 pixels, up to 32 megapixels.");
        if (Video && (Width % 2 != 0 || Height % 2 != 0)) throw new ArgumentException("MP4 width and height must be even numbers.");
        if (Video && Transparent) throw new ArgumentException("MP4 requires a studio background.");
        if (Seconds < 1 || Seconds > 60 || FramesPerSecond < 1 || FramesPerSecond > 60)
            throw new ArgumentException("Use 1 to 60 seconds and 1 to 60 frames per second.");
        if (!double.IsFinite(Elevation) || Elevation < -75 || Elevation > 75)
            throw new ArgumentException("Use elevation from -75 to 75 degrees.");
        if (!double.IsFinite(StartAngle) || StartAngle < 0 || StartAngle > 360)
            throw new ArgumentException("Use a start angle from 0 to 360 degrees.");
    }
}

internal sealed record TurntableProgress(int Completed, int Total, string Mesh = "", int MeshIndex = 0, int MeshTotal = 0, int Saved = 0, int Skipped = 0, int Failed = 0);
internal sealed record TurntableResult(int Completed, int Total, bool Cancelled, string? OutputPath,
    int Saved = 0, int Skipped = 0, int Failed = 0, string? ReportPath = null);
