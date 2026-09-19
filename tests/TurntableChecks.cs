using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LOTRAOM_Viewport;

internal static class TurntableChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Provide an AssetPackages folder.");
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bannerlord Viewport", "settings.json");
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var output = Path.GetFullPath("obj/turntable-checks");
        var root = Path.Combine(output, Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{\"SaveSettings\":false}");
            Directory.CreateDirectory(root);
            _ = new Application();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            window = new MainWindow { Width = 1180, Height = 720, ShowActivated = false, Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual };
            object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, values);
            object Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
            Call("LoadAssetPackages", args[0]); window.Show(); Pump();
            var assets = (MeshAssetNode[])Call("GetBatchRenderAssets", false)!;
            var shield = assets.First(asset => asset.CanRender && asset.DisplayName == "sm_gd_shield_a1");
            var armour = assets.First(asset => asset.CanRender && asset.DisplayName.StartsWith("sk_gd_") && asset.DisplayName.Contains("chest"));
            Call("RenderSelectedAsset", shield); Pump();
            var scene = (Model3DGroup)Field("_assetModel");
            var children = scene.Children.ToArray(); var transform = scene.Transform;
            var camera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)window.FindName("ViewportCamera");
            var originalCamera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)camera.CloneCurrentValue();
            var options = new TurntableOptions { OutputDirectory = root, Name = "checks", Width = 128, Height = 128,
                Seconds = 1, FramesPerSecond = 4, Transparent = true, OrganiseByPackage = true };
            var search = (TextBox)window.FindName("AssetSearchBox");
            search.Text = shield.DisplayName; Pump();
            Assert(((MeshAssetNode[])Call("GetBatchRenderAssets", true)!).All(asset => asset.DisplayName.Contains(shield.DisplayName)) &&
                ((MeshAssetNode[])Call("GetBatchRenderAssets", true)!).Length > 0, "Filtered scope ignored search.");
            search.Clear(); Pump();
            var stored = options with { Filtered = true, Clockwise = false, Elevation = 25, StartAngle = 45.5 };
            Assert(JsonSerializer.Deserialize<TurntableOptions>(JsonSerializer.Serialize(stored)) == stored, "Settings JSON roundtrip failed.");
            Call("RestoreRenderSettings", new AppSettings { BatchTurntable = stored });
            Assert((TurntableOptions)Field("_batchTurntableOptions") == stored, "Batch settings not restored.");
            // Avoid expensive game-path reloads when checking only option persistence.
            ((TextBox)window.FindName("AssetPathBox")).Clear();
            var save = (CheckBox)window.FindName("SaveSettingsCheckBox"); save.IsChecked = true; Call("SaveSettings");
            using (var saved = JsonDocument.Parse(File.ReadAllText(settings)))
                Assert(saved.RootElement.GetProperty("BatchTurntable").GetProperty("Elevation").GetDouble() == 25 &&
                    saved.RootElement.GetProperty("BatchTurntable").GetProperty("StartAngle").GetDouble() == 45.5, "Enabled settings did not save batch options.");
            save.IsChecked = false; Call("SaveSettings");
            using (var saved = JsonDocument.Parse(File.ReadAllText(settings)))
                Assert(saved.RootElement.GetProperty("BatchTurntable").ValueKind == JsonValueKind.Null, "Disabled settings persisted batch options.");
            var progressType = typeof(MainWindow).Assembly.GetType("LOTRAOM_Viewport.TurntableProgress")!;
            object Progress(Action<object> action) => Activator.CreateInstance(typeof(ProbeProgress<>).MakeGenericType(progressType), action)!;
            object Run(MeshAssetNode[] selected, TurntableOptions opts, CancellationToken token, Action<object>? report = null)
            {
                var task = (Task)Call("RunBatchTurntableExportAsync", selected, opts, Progress(value =>
                {
                    if (Value<int>(value, "Completed") == 1)
                    {
                        var yaw = Math.Atan2(-camera.LookDirection.X, -camera.LookDirection.Z) * 180 / Math.PI;
                        var delta = ((yaw - opts.StartAngle) % 360 + 540) % 360 - 180;
                        Assert(Math.Abs(delta) < 1e-6, "Batch did not use the requested start angle.");
                    }
                    report?.Invoke(value);
                }), token)!;
                while (!task.IsCompleted) Pump();
                task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            static T Value<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
            void Restored()
            {
                Assert(scene.Children.SequenceEqual(children) && scene.Transform == transform, "Original scene not restored.");
                Assert(camera.Position == originalCamera.Position && camera.LookDirection == originalCamera.LookDirection &&
                    camera.UpDirection == originalCamera.UpDirection && camera.FieldOfView == originalCamera.FieldOfView &&
                    camera.NearPlaneDistance == originalCamera.NearPlaneDistance && camera.FarPlaneDistance == originalCamera.FarPlaneDistance, "Original camera not restored.");
                Assert(!(bool)Field("_batchRenderRunning") && !(bool)Field("_turntableRunning"), "Export guard remained set.");
            }
            var skipped = new MeshAssetNode { DisplayName = "skip", Kind = "Mesh" };
            var failed = new MeshAssetNode { DisplayName = "broken", CanRender = true, MeshGuid = Guid.NewGuid(),
                MetameshGuid = Guid.NewGuid(), SourcePath = Path.Combine(root, "missing.tpac"), Kind = "Mesh" };
            var result = Run([shield, skipped, failed, armour], options, CancellationToken.None);
            Assert(Value<int>(result, "Saved") == 2 && Value<int>(result, "Skipped") == 1 && Value<int>(result, "Failed") == 1 &&
                Value<int>(result, "Completed") == 8 && !Value<bool>(result, "Cancelled"), "Batch did not continue after skipped/failed meshes.");
            using (var report = JsonDocument.Parse(File.ReadAllText(Value<string>(result, "ReportPath"))))
                Assert(report.RootElement.GetProperty("Meshes").GetArrayLength() == 4, "Missing report entries.");
            var directory = Value<string>(result, "OutputPath");
            Assert(directory == Path.Combine(root, options.Name), "Batch folder has a suffix.");
            var sequences = Directory.GetFiles(directory, "turntable.json", SearchOption.AllDirectories);
            Assert(sequences.Length == 2, "Missing sequence manifests.");
            Assert(sequences.Select(path => new DirectoryInfo(Path.GetDirectoryName(path)!).Name).Order().SequenceEqual(
                new[] { shield.DisplayName, armour.DisplayName }.Order()), "Sequence folders do not use mesh names.");
            var renderPath = (string)typeof(MainWindow).GetMethod("GetBatchRenderPath", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [root, shield, true])!;
            Assert(Path.GetFileName(renderPath) == shield.DisplayName + ".png" &&
                new DirectoryInfo(Path.GetDirectoryName(renderPath)!).Name == shield.PackageName, "PNG/package names contain suffixes.");
            foreach (var manifest in sequences)
            {
                var frames = Directory.GetFiles(Path.GetDirectoryName(manifest)!, "*.png").Order().ToArray();
                Assert(frames.Length == 4, "Wrong frame count.");
                var pixels = frames.Select(Pixels).ToArray();
                Assert(pixels.All(data => Enumerable.Range(0, data.Length / 4).Count(index => data[index * 4 + 3] > 0) > 100), "Blank mesh frames.");
                Assert(!pixels[0].SequenceEqual(pixels[1]), "Orbit frames did not change.");
                Assert(frames.All(path => new FileInfo(path).Length > 500), "Empty PNG.");
            }
            Restored();
            var originalFrame = File.ReadAllBytes(Directory.GetFiles(directory, "frame-000000.png", SearchOption.AllDirectories).First());
            result = Run([shield, armour], options, CancellationToken.None);
            Assert(Value<int>(result, "Saved") == 0 && Value<int>(result, "Skipped") == 2 && Value<int>(result, "Failed") == 0 &&
                File.ReadAllBytes(Directory.GetFiles(directory, "frame-000000.png", SearchOption.AllDirectories).First()).SequenceEqual(originalFrame), "Repeat batch overwrote existing turntables.");
            Restored();
            using (var cancellation = new CancellationTokenSource())
            {
                result = Run([shield, armour], options with { Name = "cancelled" }, cancellation.Token, value =>
                { if (Value<int>(value, "Completed") == 2) cancellation.Cancel(); });
                Assert(Value<bool>(result, "Cancelled") && Value<int>(result, "Completed") == 2 && Value<int>(result, "Saved") == 0, "Cancellation did not stop between frames.");
                directory = Value<string>(result, "OutputPath");
                Assert(Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories).Length == 2 &&
                    Directory.GetFiles(directory, "turntable.json", SearchOption.AllDirectories).Length == 1, "Partial sequence not retained.");
                using var report = JsonDocument.Parse(File.ReadAllText(Value<string>(result, "ReportPath")));
                Assert(report.RootElement.GetProperty("Cancelled").GetBoolean() && report.RootElement.GetProperty("Meshes")[0].GetProperty("Status").GetString() == "Cancelled", "Cancellation report missing.");
            }
            Restored();
            var pngOptions = new BatchRenderOptions { OutputDirectory = Path.Combine(root, "png"), Width = 128, Height = 128,
                Transparent = true, OrganiseByPackage = false, Overwrite = true };
            var pngProgressType = typeof(MainWindow).Assembly.GetType("LOTRAOM_Viewport.BatchRenderProgress")!;
            var pngProgress = Activator.CreateInstance(typeof(ProbeProgress<>).MakeGenericType(pngProgressType), (Action<object>)(_ => { }))!;
            var pngTask = (Task)Call("RunBatchRenderAsync", new[] { shield, shield }, pngOptions, pngProgress, CancellationToken.None)!;
            while (!pngTask.IsCompleted) Pump(); pngTask.GetAwaiter().GetResult();
            var pngResult = pngTask.GetType().GetProperty("Result")!.GetValue(pngTask)!;
            Assert(Value<int>(pngResult, "Saved") == 1 && Value<int>(pngResult, "Skipped") == 1 &&
                File.Exists(Path.Combine(pngOptions.OutputDirectory, shield.DisplayName + ".png")) &&
                File.Exists(Path.Combine(pngOptions.OutputDirectory, "batch-report.json")), "Plain PNG names / duplicate name protection failed.");
            Restored();
            var singleOptions = options with { OutputDirectory = Path.Combine(root, "single"), Name = shield.DisplayName };
            object Single(TurntableOptions? selected = null)
            {
                var task = (Task)Call("RunTurntableExportAsync", selected ?? singleOptions, Progress(_ => { }), CancellationToken.None, false)!;
                while (!task.IsCompleted) Pump(); task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            var singleResult = Single();
            Assert(Value<string>(singleResult, "OutputPath") == Path.Combine(singleOptions.OutputDirectory, shield.DisplayName), "Single turntable has a suffix.");
            try { Single(); throw new Exception("Existing single output accepted."); }
            catch (IOException) { }
            Assert(Directory.GetFiles(Value<string>(singleResult, "OutputPath"), "*.png").Length == 4, "Single conflict changed existing frames.");
            Restored();
            var zero = Single(singleOptions with { Name = "angle-zero", StartAngle = 0 });
            var half = Single(singleOptions with { Name = "angle-half", StartAngle = 180 });
            var zeroFrames = Directory.GetFiles(Value<string>(zero, "OutputPath"), "*.png").Order().Select(Pixels).ToArray();
            var halfFrames = Directory.GetFiles(Value<string>(half, "OutputPath"), "*.png").Order().Select(Pixels).ToArray();
            Assert(!zeroFrames[0].SequenceEqual(halfFrames[0]) && zeroFrames[2].SequenceEqual(halfFrames[0]) &&
                zeroFrames[0].SequenceEqual(halfFrames[2]), "Start angle did not shift the exported orbit by half a turn.");
            Restored();
            var folders = Directory.GetDirectories(root).Length;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); result = Run([shield], options, cancellation.Token);
                Assert(Value<bool>(result, "Cancelled") && Value<int>(result, "Completed") == 0 && Directory.GetDirectories(root).Length == folders, "Pre-cancel created output.");
            }
            try { Run([shield], options with { Video = true, Transparent = false, FfmpegPath = Path.Combine(root, "missing.exe") }, CancellationToken.None); throw new Exception("Missing encoder accepted."); }
            catch (ArgumentException) { }
            Assert(Directory.GetDirectories(root).Length == folders, "Missing encoder created output.");
            Restored();
            var dialog = (TurntableExportWindow)Activator.CreateInstance(typeof(TurntableExportWindow), Private, null,
                [window, options with { Filtered = true, AutoFrame = false }, null, 4, 2], null)!;
            dialog.Show(); Pump();
            Assert(((ComboBox)dialog.FindName("ScopeCombo")).SelectedIndex == 1 && ((FrameworkElement)dialog.FindName("BatchScopePanel")).Visibility == Visibility.Visible, "Batch scope UI missing.");
            var read = (TurntableOptions)typeof(TurntableExportWindow).GetMethod("ReadOptions", Private)!.Invoke(dialog, null)!;
            Assert(read.Filtered && read.AutoFrame && read.OrganiseByPackage, "Batch options not read.");
            ((TextBox)dialog.FindName("StartAngleBox")).Text = "45.5";
            read = (TurntableOptions)typeof(TurntableExportWindow).GetMethod("ReadOptions", Private)!.Invoke(dialog, null)!;
            Assert(read.StartAngle == 45.5, "Start angle UI did not read fractional degrees.");
            var validate = typeof(TurntableOptions).GetMethod("Validate", Private)!;
            foreach (var invalid in new[] { -1d, 361d, double.NaN, double.PositiveInfinity })
            {
                try { validate.Invoke(options with { StartAngle = invalid }, null); throw new Exception("Invalid start angle accepted."); }
                catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException) { }
            }
            validate.Invoke(options with { StartAngle = 360 }, null);
            ((FrameworkElement)dialog.FindName("StartAngleBox")).BringIntoView(); Pump();
            Capture(dialog, Path.Combine(output, "batch-start-angle.png"));
            Capture(dialog, Path.Combine(output, "batch-dialog.png"));
            ((ComboBox)dialog.FindName("FormatCombo")).SelectedIndex = 1; Pump();
            Assert(((ComboBox)dialog.FindName("BackgroundCombo")).SelectedIndex == 1 && !((ComboBox)dialog.FindName("BackgroundCombo")).IsEnabled, "MP4 alpha not disabled.");
            Capture(dialog, Path.Combine(output, "batch-video-dialog.png")); dialog.Close();
            Capture(window, Path.Combine(output, "assets-toolbar.png"));
            if (args.Length > 1)
            {
                result = Run([shield, armour], options with { Video = true, Transparent = false, FfmpegPath = args[1] }, CancellationToken.None);
                Assert(Value<int>(result, "Saved") == 2 && Value<int>(result, "Failed") == 0, "MP4 batch failed.");
                var videos = Directory.GetFiles(Value<string>(result, "OutputPath"), "*.mp4", SearchOption.AllDirectories);
                Assert(videos.Length == 2 && videos.All(path => new FileInfo(path).Length > 500), "MP4 files missing.");
                Restored(); Console.WriteLine("Two real MP4 mesh exports: passed.");
            }
            Console.WriteLine("Single/batch start angles, half-turn pixel equivalence, fractional angle UI and persistence, validation, plain naming, duplicate protection, real mesh PNG batch, changing orbit pixels, reports, cancellation and scene/camera restoration: passed.");
        }
        finally
        {
            window?.Close();
            if (backup != null) File.WriteAllBytes(settings, backup); else if (File.Exists(settings)) File.Delete(settings);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ProbeProgress<T>(Action<object> action) : IProgress<T>
    { public void Report(T value) => action(value!); }
    private static byte[] Pixels(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data;
    }
    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
