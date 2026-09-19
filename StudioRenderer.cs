using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Hx = HelixToolkit.Wpf.SharpDX;
using PixelShader = System.Windows.Media.Effects.PixelShader;
using Vector3 = System.Numerics.Vector3;
using WpfMaterial = System.Windows.Media.Media3D.Material;
using WpfMesh = System.Windows.Media.Media3D.MeshGeometry3D;

namespace LOTRAOM_Viewport;

internal sealed class StudioRenderer : IDisposable
{
    private static readonly ConditionalWeakTable<WpfMaterial, Hx.PBRMaterial> Materials = new();
    private static readonly ConditionalWeakTable<WpfMaterial, ColourBlendingMaterial> ColourMaterials = new();
    private static readonly ConditionalWeakTable<WpfMesh, BoneIds[]> Skinning = new();
    private static readonly byte[] SrgbBytes = Enumerable.Range(0, 256).Select(value => EncodeSrgb(value / 255.0)).ToArray();
    private readonly List<Hx.BoneSkinMeshGeometryModel3D> _posedMeshes = [];
    private readonly List<Hx.MeshGeometryModel3D> _gridMeshes = [];
    internal Model3D? GridModel { get; set; }
    private Model3D? _posedModel;
    private Matrix4x4[]? _poseMatrices;
    internal (Model3D? Model, Matrix4x4[]? Matrices) PoseState => (_posedModel, _poseMatrices);
    private System.Windows.Media.Color _primaryColour = Colors.White;
    private System.Windows.Media.Color _secondaryColour = Colors.White;
    private bool _coloursEnabled;
    private readonly Hx.Viewport3DX _viewport;
    private readonly Model3DGroup _scene;
    private readonly DefaultEffectsManager _effects = new();
    private readonly TextureModel _environment = CreateStudioEnvironment();
    private bool _updatePending;
    private bool _disposed;

    public StudioRenderer(Hx.Viewport3DX viewport, Model3DGroup scene)
    {
        _viewport = viewport;
        _scene = scene;
        viewport.EffectsManager = _effects;
        // The GPU shaders shade in linear space; encode the final image for the display.
        viewport.BackgroundColor = System.Windows.Media.Color.FromRgb(1, 2, 3);
        viewport.Effect = new ColorOutputEffect();
        scene.Changed += SceneChanged;
    }

    private void SceneChanged(object? sender, EventArgs e)
    {
        if (_updatePending || _disposed)
        {
            return;
        }
        _updatePending = true;
        _viewport.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _updatePending = false;
            if (!_disposed)
            {
                UpdateScene();
            }
        }));
    }

    internal void UpdateScene()
    {
        foreach (var element in _viewport.Items)
        {
            element.Dispose();
        }
        _viewport.Items.Clear();
        _posedMeshes.Clear();
        _gridMeshes.Clear();
        _viewport.Items.Add(new Hx.EnvironmentMap3D { Texture = _environment, SkipRendering = true });
        AddModel(_scene, Matrix3D.Identity);
    }

    private void AddModel(Model3D model, Matrix3D parent, bool posed = false, bool grid = false)
    {
        posed |= ReferenceEquals(model, _posedModel);
        grid |= ReferenceEquals(model, GridModel);
        var transform = model.Transform.Value;
        transform.Append(parent);
        switch (model)
        {
            case Model3DGroup group:
                foreach (var child in group.Children)
                {
                    AddModel(child, transform, posed, grid);
                }
                break;
            case AmbientLight ambient:
                var ambientElement = new Hx.AmbientLight3D { Color = ambient.Color };
                ((HelixToolkit.SharpDX.Model.Scene.LightNode)ambientElement.SceneNode).Color = LinearColor(ambient.Color);
                _viewport.Items.Add(ambientElement);
                break;
            case DirectionalLight directional:
                var directionalElement = new Hx.DirectionalLight3D { Color = directional.Color, Direction = directional.Direction };
                ((HelixToolkit.SharpDX.Model.Scene.LightNode)directionalElement.SceneNode).Color = LinearColor(directional.Color);
                _viewport.Items.Add(directionalElement);
                break;
            case GeometryModel3D { Geometry: WpfMesh source } geometry:
                var hasSkinning = Skinning.TryGetValue(source, out var skinIds);
                var skinned = posed && _poseMatrices != null && hasSkinning &&
                    skinIds!.All(id => id.Bone1 < _poseMatrices.Length && id.Bone2 < _poseMatrices.Length && id.Bone3 < _poseMatrices.Length && id.Bone4 < _poseMatrices.Length);
                var mesh = skinned ? new BoneSkinnedMeshGeometry3D { VertexBoneIds = skinIds! }
                    : new HelixToolkit.SharpDX.MeshGeometry3D();
                mesh.Positions = new Vector3Collection(source.Positions.Select(point => new Vector3((float)point.X, (float)point.Y, (float)point.Z)));
                mesh.Indices = new IntCollection(source.TriangleIndices);
                mesh.TextureCoordinates = new Vector2Collection(source.TextureCoordinates.Select(point => new Vector2((float)point.X, (float)point.Y)));
                mesh.Normals = source.Normals.Count == source.Positions.Count
                    ? new Vector3Collection(source.Normals.Select(normal => new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z)))
                    : new Vector3Collection(MeshGeometryHelper.CalculateNormals(mesh.Positions, mesh.Indices));
                Hx.MeshGeometryModel3D element;
                if (skinned)
                {
                    var skin = new Hx.BoneSkinMeshGeometryModel3D { BoneMatrices = _poseMatrices! };
                    _posedMeshes.Add(skin);
                    element = skin;
                }
                else element = new Hx.MeshGeometryModel3D();
                element.Geometry = mesh;
                element.Material = GetMaterial(geometry.Material);
                element.Transform = new MatrixTransform3D(transform);
                element.CullMode = CullMode.None;
                element.IsHitTestVisible = false;
                _viewport.Items.Add(element);
                if (grid) _gridMeshes.Add(element);
                break;
        }
    }

    internal Rect3D GetRenderBounds(Model3D model)
    {
        var bounds = Rect3D.Empty;
        void Include(Model3D part, Matrix3D parent, bool posed)
        {
            posed |= ReferenceEquals(part, _posedModel);
            var transform = part.Transform.Value;
            transform.Append(parent);
            if (part is Model3DGroup group)
            {
                foreach (var child in group.Children) Include(child, transform, posed);
            }
            else if (part is GeometryModel3D { Geometry: WpfMesh mesh })
            {
                var hasSkinning = Skinning.TryGetValue(mesh, out var bones);
                var skinned = posed && _poseMatrices != null && hasSkinning &&
                    bones!.All(id => id.Bone1 < _poseMatrices.Length && id.Bone2 < _poseMatrices.Length &&
                        id.Bone3 < _poseMatrices.Length && id.Bone4 < _poseMatrices.Length);
                for (var i = 0; i < mesh.Positions.Count; i++)
                {
                    var point = mesh.Positions[i];
                    if (skinned)
                    {
                        var bone = bones![i];
                        var position = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
                        var vertex = Vector3.Transform(position, _poseMatrices![bone.Bone1]) * bone.Weights.X +
                            Vector3.Transform(position, _poseMatrices[bone.Bone2]) * bone.Weights.Y +
                            Vector3.Transform(position, _poseMatrices[bone.Bone3]) * bone.Weights.Z +
                            Vector3.Transform(position, _poseMatrices[bone.Bone4]) * bone.Weights.W;
                        point = new Point3D(vertex.X, vertex.Y, vertex.Z);
                    }
                    bounds.Union(transform.Transform(point));
                }
            }
        }
        // Evaluate skinning once for export framing; WPF's ordinary bounds remain in the bind pose.
        Include(model, Matrix3D.Identity, false);
        return bounds;
    }

    internal BitmapSource CaptureRender(int width, int height, bool transparent, bool includeGrid)
    {
        if (width < 64 || height < 64 || width > 8192 || height > 8192 || (long)width * height > 33554432)
            throw new ArgumentOutOfRangeException(nameof(width), "Use dimensions from 64 to 8192 pixels, up to 32 megapixels.");
        var host = _viewport.RenderHost;
        if (host == null || !host.IsRendering) throw new InvalidOperationException("The viewport is not ready to export.");
        var oldWidth = (int)host.ActualWidth;
        var oldHeight = (int)host.ActualHeight;
        var background = _viewport.BackgroundColor;
        var fxaa = _viewport.FXAALevel;
        var msaa = _viewport.MSAA;
        var visibility = _gridMeshes.Select(mesh => mesh.Visibility).ToArray();
        try
        {
            _viewport.BackgroundColor = transparent ? System.Windows.Media.Color.FromArgb(0, 0, 0, 0) : background;
            if (transparent)
            {
                _viewport.FXAALevel = FXAALevel.None;
                _viewport.MSAA = MSAALevel.Four;
            }
            if (!includeGrid) foreach (var mesh in _gridMeshes) mesh.Visibility = Visibility.Collapsed;
            host.Resize(width, height);
            _viewport.InvalidateRender();
            host.UpdateAndRender();
            var context = _effects.Device!.ImmediateContext;
            var source = host.RenderBuffer?.BackBuffer?.Resource as Texture2D;
            if (!HelixToolkit.SharpDX.Utilities.ScreenCapture.CaptureTexture(context, source, out var staging) || staging == null)
                throw new InvalidOperationException("The renderer did not produce an image.");
            var pixels = new byte[checked(width * height * 4)];
            // Helix's bitmap helper converts to 24-bit RGB and discards alpha. Read the GPU texture instead.
            using (staging)
            {
                if (staging.Description.Format != Format.B8G8R8A8_UNorm || staging.Description.Width != width || staging.Description.Height != height)
                    throw new InvalidOperationException("The renderer returned an unexpected image format or size.");
                var data = context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    for (var row = 0; row < height; row++)
                        Marshal.Copy(IntPtr.Add(data.DataPointer, row * data.RowPitch), pixels, row * width * 4, width * 4);
                }
                finally { context.UnmapSubresource(staging, 0); }
            }
            // GPU capture bypasses the WPF display effect. Encode the same linear-to-sRGB curve.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3] / 255.0;
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[i + channel] = !transparent || alpha == 1 ? SrgbBytes[pixels[i + channel]] :
                        alpha == 0 ? (byte)0 : EncodeSrgb(Math.Min(1, pixels[i + channel] / (255.0 * alpha)));
                }
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            result.Freeze();
            return result;
        }
        finally
        {
            _viewport.BackgroundColor = background;
            _viewport.FXAALevel = fxaa;
            _viewport.MSAA = msaa;
            for (var i = 0; i < _gridMeshes.Count; i++) _gridMeshes[i].Visibility = visibility[i];
            host.Resize(oldWidth, oldHeight);
            _viewport.InvalidateRender();
        }
    }

    private static byte EncodeSrgb(double linear)
    {
        var encoded = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
    }

    internal void ScaleLighting(float intensity)
    {
        foreach (var element in _viewport.Items)
            if (element.SceneNode is HelixToolkit.SharpDX.Model.Scene.LightNode light)
            {
                var colour = light.Color;
                light.Color = new Color4(colour.Red * intensity, colour.Green * intensity, colour.Blue * intensity, colour.Alpha);
            }
        _viewport.InvalidateRender();
    }

    internal static void RegisterSkinning(WpfMesh mesh, TpacTool.Lib.VertexStreamData stream)
    {
        if (stream.BoneIndices?.Length != mesh.Positions.Count || stream.BoneWeights?.Length != mesh.Positions.Count) return;
        var ids = new BoneIds[mesh.Positions.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            var bone = stream.BoneIndices[i];
            var weight = stream.BoneWeights[i];
            var x = weight.W1 / 255f;
            var y = weight.W2 / 255f;
            var z = weight.W3 / 255f;
            ids[i] = new BoneIds { Bone1 = bone.B1, Bone2 = bone.B2, Bone3 = bone.B3, Bone4 = bone.B4,
                Weights = new Vector4(x, y, z, 1 - x - y - z) };
        }
        Skinning.Add(mesh, ids);
    }

    internal static BoneIds[]? GetExportSkinning(WpfMesh mesh) => Skinning.TryGetValue(mesh, out var ids) ? ids : null;

    internal static bool CanPose(Model3D model, int boneCount)
    {
        if (model is Model3DGroup group) return group.Children.Any(child => CanPose(child, boneCount));
        return model is GeometryModel3D { Geometry: WpfMesh mesh } && Skinning.TryGetValue(mesh, out var ids) &&
            ids.Length > 0 && ids.All(id => id.Bone1 < boneCount && id.Bone2 < boneCount && id.Bone3 < boneCount && id.Bone4 < boneCount);
    }

    internal static void AttachToBone(Model3D model, int bone, Matrix4x4 bindFrame)
    {
        var frame = new Matrix3D(bindFrame.M11, bindFrame.M12, bindFrame.M13, bindFrame.M14,
            bindFrame.M21, bindFrame.M22, bindFrame.M23, bindFrame.M24,
            bindFrame.M31, bindFrame.M32, bindFrame.M33, bindFrame.M34,
            bindFrame.M41, bindFrame.M42, bindFrame.M43, bindFrame.M44);
        Bake(model, frame);

        void Bake(Model3D part, Matrix3D parent)
        {
            var transform = part.Transform.Value;
            transform.Append(parent);
            if (part is Model3DGroup group)
                foreach (var child in group.Children) Bake(child, transform);
            if (part is GeometryModel3D { Geometry: WpfMesh source } geometry)
            {
                // Bind-space geometry plus rigid weights uses the same GPU skinning as armour.
                var mesh = source.CloneCurrentValue();
                AssetExportMetadata.Copy(source, mesh);
                mesh.Positions = new Point3DCollection(source.Positions.Select(transform.Transform));
                mesh.Normals = new Vector3DCollection(source.Normals.Select(normal =>
                {
                    normal = transform.Transform(normal);
                    if (normal.LengthSquared > 0) normal.Normalize();
                    return normal;
                }));
                Skinning.Add(mesh, Enumerable.Repeat(new BoneIds { Bone1 = bone, Bone2 = bone, Bone3 = bone,
                    Bone4 = bone, Weights = new Vector4(1, 0, 0, 0) }, mesh.Positions.Count).ToArray());
                geometry.Geometry = mesh;
            }
            part.Transform = Transform3D.Identity;
        }
    }

    internal void SetPose(Model3D? model, Matrix4x4[]? matrices)
    {
        var rebuild = !ReferenceEquals(model, _posedModel) || (matrices == null) != (_poseMatrices == null);
        _posedModel = model;
        _poseMatrices = matrices;
        if (rebuild) SceneChanged(this, EventArgs.Empty);
        else if (matrices != null) foreach (var mesh in _posedMeshes) mesh.BoneMatrices = matrices;
    }

    private Hx.PBRMaterial GetMaterial(WpfMaterial material)
    {
        if (Materials.TryGetValue(material, out var result))
        {
            ApplyColours(material, result);
            return result;
        }
        var diffuse = (System.Windows.Media.Media3D.DiffuseMaterial)material;
        var color = diffuse.Brush is SolidColorBrush solid ? solid.Color : Colors.White;
        result = new Hx.PBRMaterial
        {
            AlbedoColor = new Color4(color.ScR, color.ScG, color.ScB, color.ScA),
            RoughnessFactor = 0.85,
            MetallicFactor = 0,
            ReflectanceFactor = 0.5,
            EnableAutoTangent = true,
            VertexColorBlendingFactor = 0,
            RenderEnvironmentMap = true
        };
        if (diffuse.Brush is ImageBrush { ImageSource: BitmapSource bitmap })
        {
            result.AlbedoMap = CreateTexture(bitmap, true);
        }
        Materials.Add(material, result);
        return result;
    }

    private static Color4 LinearColor(System.Windows.Media.Color color)
    {
        return new Color4(color.ScR, color.ScG, color.ScB, color.ScA);
    }

    internal static void RegisterMaterial(WpfMaterial material, BitmapSource albedo, BitmapSource? packedMap,
        BitmapSource? normalMap, bool reconstructNormalZ, double specularCoefficient, double glossCoefficient,
        IEnumerable<string>? shaderFlags = null, BitmapSource? colourMask = null, BitmapSource? tableauMask = null)
    {
        var pbr = new Hx.PBRMaterial
        {
            AlbedoColor = new Color4(1, 1, 1, 1),
            AlbedoMap = CreateTexture(albedo, true),
            RoughnessFactor = packedMap == null ? 0.85 : 1,
            MetallicFactor = packedMap == null ? 0 : 1,
            ReflectanceFactor = 0.5,
            EnableAutoTangent = true,
            VertexColorBlendingFactor = 0,
            RenderEnvironmentMap = true
        };
        if (packedMap != null)
        {
            pbr.RoughnessMetallicMap = ConvertMetallicGlossMap(packedMap, specularCoefficient, glossCoefficient);
        }
        if (normalMap != null)
        {
            pbr.NormalMap = CreateNormalTexture(normalMap, reconstructNormalZ);
        }
        Materials.Add(material, pbr);
        var flags = new HashSet<string>(shaderFlags ?? [], StringComparer.OrdinalIgnoreCase);
        if (colourMask != null && !flags.Contains("use_tableau_blending") && !flags.Contains("use_colormapping") &&
            !flags.Contains("use_double_colormap_with_mask_texture"))
        {
            // Some exported materials omit colour flags but retain their authored R/G mask.
            // A secondary diffuse/detail image is not a mask: its blue channel is populated.
            var pixels = CopyPixels(colourMask);
            var mask = true;
            var red = false;
            var green = false;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 8) { mask = false; break; }
                red |= pixels[i + 2] > 8;
                green |= pixels[i + 1] > 8;
            }
            if (mask && (red || green))
                flags.Add(green ? "use_double_colormap_with_mask_texture" : "use_colormapping");
        }
        if (flags.Contains("use_tableau_blending") || flags.Contains("use_colormapping") ||
            flags.Contains("use_double_colormap_with_mask_texture"))
        {
            ColourMaterials.Add(material, new ColourBlendingMaterial(albedo, colourMask, tableauMask, flags, pbr.AlbedoMap));
        }
    }

    internal static bool HasFactionColourBlending(Model3D model)
    {
        return EnumerateMaterials(model).Any(material => ColourMaterials.TryGetValue(material, out _));
    }

    private static IEnumerable<WpfMaterial> EnumerateMaterials(Model3D model)
    {
        if (model is GeometryModel3D geometry && geometry.Material != null) yield return geometry.Material;
        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            foreach (var material in EnumerateMaterials(child)) yield return material;
        }
    }

    internal void SetColours(System.Windows.Media.Color primary, System.Windows.Media.Color secondary, bool enabled)
    {
        _primaryColour = primary;
        _secondaryColour = secondary;
        _coloursEnabled = enabled;
        foreach (var material in EnumerateMaterials(_scene).Distinct())
        {
            if (Materials.TryGetValue(material, out var pbr)) ApplyColours(material, pbr);
        }
    }

    private void ApplyColours(WpfMaterial material, Hx.PBRMaterial pbr)
    {
        if (!ColourMaterials.TryGetValue(material, out var data)) return;
        if (data.Applied && data.Primary == _primaryColour && data.Secondary == _secondaryColour && data.Enabled == _coloursEnabled) return;
        pbr.AlbedoMap = _coloursEnabled ? data.Blend(_primaryColour, _secondaryColour) : data.Original;
        data.Primary = _primaryColour;
        data.Secondary = _secondaryColour;
        data.Enabled = _coloursEnabled;
        data.Applied = true;
    }

    private sealed class ColourBlendingMaterial
    {
        private readonly byte[] _albedo;
        private readonly byte[]? _colourMask;
        private readonly byte[]? _tableauMask;
        private readonly int _width, _height, _colourWidth, _colourHeight, _tableauWidth, _tableauHeight;
        private readonly bool _doubleColour, _singleColour, _separateMask;
        private static readonly double[] LinearChannels = Enumerable.Range(0, 256)
            .Select(value => value <= 10 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4)).ToArray();
        internal bool Tableau { get; }
        internal TextureModel Original { get; }
        internal System.Windows.Media.Color Primary, Secondary;
        internal bool Applied, Enabled;

        internal ColourBlendingMaterial(BitmapSource albedo, BitmapSource? colourMask, BitmapSource? tableauMask,
            HashSet<string> flags, TextureModel original)
        {
            _albedo = CopyPixels(albedo);
            _width = albedo.PixelWidth;
            _height = albedo.PixelHeight;
            if (colourMask != null)
            {
                _colourMask = CopyPixels(colourMask);
                _colourWidth = colourMask.PixelWidth;
                _colourHeight = colourMask.PixelHeight;
            }
            if (tableauMask != null)
            {
                _tableauMask = CopyPixels(tableauMask);
                _tableauWidth = tableauMask.PixelWidth;
                _tableauHeight = tableauMask.PixelHeight;
            }
            Tableau = flags.Contains("use_tableau_blending");
            _doubleColour = flags.Contains("use_double_colormap_with_mask_texture");
            _singleColour = flags.Contains("use_colormapping");
            _separateMask = flags.Contains("use_tableau_mask_as_separate_texture");
            Original = original;
        }

        internal TextureModel Blend(System.Windows.Media.Color primary, System.Windows.Media.Color secondary)
        {
            var pixels = (byte[])_albedo.Clone();
            double[] first = [primary.ScB, primary.ScG, primary.ScR];
            double[] second = [secondary.ScB, secondary.ScG, secondary.ScR];
            for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                var index = (y * _width + x) * 4;
                var maskIndex = _colourMask == null ? 0 : ((y * _colourHeight / _height) * _colourWidth + x * _colourWidth / _width) * 4;
                var tableauIndex = _tableauMask == null ? 0 : ((y * _tableauHeight / _height) * _tableauWidth + x * _tableauWidth / _width) * 4;
                // Bannerlord's base alpha is a tableau mask, not cloth transparency.
                var weight = !Tableau ? 1 : _separateMask && _tableauMask != null
                    ? _tableauMask[tableauIndex + 2] / 255.0 : 1 - _albedo[index + 3] / 255.0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var original = LinearChannels[_albedo[index + channel]];
                    double coloured;
                    if ((_doubleColour || _singleColour) && _colourMask != null)
                    {
                        var red = _colourMask[maskIndex + 2] / 255.0;
                        if (_doubleColour)
                        {
                            var green = _colourMask[maskIndex + 1] / 255.0;
                            coloured = original * (1 + (first[channel] - 1) * Math.Sqrt(red));
                            coloured *= 1 + (second[channel] - 1) * Math.Sqrt(green);
                        }
                        else coloured = original * (1 + (second[channel] - 1) * red);
                    }
                    else coloured = Tableau ? first[channel] : original;
                    var linear = Math.Clamp(original + (coloured - original) * weight, 0, 1);
                    pixels[index + channel] = (byte)Math.Round(255 * (linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055));
                }
                if (Tableau && !_separateMask) pixels[index + 3] = 255;
            }
            return new TextureModel(pixels, Format.B8G8R8A8_UNorm_SRgb, _width, _height);
        }
    }

    internal static TextureModel ConvertMetallicGlossMap(BitmapSource bitmap, double specularCoefficient, double glossCoefficient)
    {
        var pixels = CopyPixels(bitmap);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Bannerlord R=metallic, G=gloss; Helix uses B=metallic, G=roughness.
            var metallic = Math.Clamp(pixels[i + 2] / 255.0 * specularCoefficient, 0, 1);
            var gloss = Math.Clamp(pixels[i + 1] / 255.0 * glossCoefficient, 0, 1);
            var roughness = Math.Max(0.045, 1 - gloss);
            pixels[i] = (byte)Math.Round(metallic * 255);
            // Soften glossy highlights slightly while leaving fully matte surfaces unchanged.
            pixels[i + 1] = (byte)Math.Round((roughness + 0.06 * (1 - roughness)) * 255);
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        return new TextureModel(pixels, Format.B8G8R8A8_UNorm, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static TextureModel CreateNormalTexture(BitmapSource bitmap, bool reconstructZ)
    {
        var pixels = CopyPixels(bitmap);
        if (reconstructZ)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var x = pixels[i + 2] / 127.5 - 1;
                var y = pixels[i + 1] / 127.5 - 1;
                pixels[i] = (byte)Math.Round((Math.Sqrt(Math.Max(0, 1 - x * x - y * y)) + 1) * 127.5);
            }
        }
        return new TextureModel(pixels, Format.B8G8R8A8_UNorm, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static TextureModel CreateTexture(BitmapSource bitmap, bool srgb)
    {
        return new TextureModel(CopyPixels(bitmap), srgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm,
            bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static byte[] CopyPixels(BitmapSource bitmap)
    {
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static TextureModel CreateStudioEnvironment()
    {
        const int size = 128;
        const int mipCount = 8;
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        // DDS DX10 float cubemap, with a complete mip chain for rough reflections.
        writer.Write(0x20534444u);
        writer.Write(124u);
        writer.Write(0x2100fu);
        writer.Write(size);
        writer.Write(size);
        writer.Write(size * 16);
        writer.Write(0);
        writer.Write(mipCount);
        for (var i = 0; i < 11; i++) writer.Write(0);
        writer.Write(32);
        writer.Write(4);
        writer.Write(0x30315844u);
        for (var i = 0; i < 5; i++) writer.Write(0);
        writer.Write(0x401008u);
        writer.Write(0xfe00u);
        for (var i = 0; i < 3; i++) writer.Write(0);
        writer.Write((int)Format.R32G32B32A32_Float);
        writer.Write(3);
        writer.Write(4);
        writer.Write(1);
        writer.Write(0);

        for (var face = 0; face < 6; face++)
        {
            var pixels = new Vector3[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = 2f * (x + 0.5f) / size - 1;
                var v = 2f * (y + 0.5f) / size - 1;
                var direction = Vector3.Normalize(face switch
                {
                    0 => new Vector3(1, -v, -u), 1 => new Vector3(-1, -v, u),
                    2 => new Vector3(u, 1, v), 3 => new Vector3(u, -1, -v),
                    4 => new Vector3(u, -v, 1), _ => new Vector3(-u, -v, -1)
                });
                var background = 0.025f + 0.035f * Math.Max(0, direction.Y);
                var radiance = background
                    + StudioPanel(direction, new Vector3(-0.6f, 0.25f, -0.75f), 0.22f, 0.75f) * 1.9f
                    + StudioPanel(direction, new Vector3(0.75f, 0.15f, -0.65f), 0.12f, 0.65f) * 1.45f
                    + StudioPanel(direction, new Vector3(0, 0.9f, -0.35f), 0.65f, 0.2f) * 1.05f;
                pixels[y * size + x] = new Vector3(radiance);
            }
            for (var mip = 0; mip < mipCount; mip++)
            {
                foreach (var pixel in pixels)
                {
                    writer.Write(pixel.X); writer.Write(pixel.Y); writer.Write(pixel.Z); writer.Write(1f);
                }
                var width = size >> mip;
                if (width == 1) break;
                var nextWidth = width / 2;
                var next = new Vector3[nextWidth * nextWidth];
                for (var y = 0; y < nextWidth; y++)
                for (var x = 0; x < nextWidth; x++)
                {
                    var index = y * 2 * width + x * 2;
                    next[y * nextWidth + x] = (pixels[index] + pixels[index + 1] + pixels[index + width] + pixels[index + width + 1]) * 0.25f;
                }
                pixels = next;
            }
        }
        stream.Position = 0;
        return new TextureModel(stream);
    }

    private static float StudioPanel(Vector3 direction, Vector3 center, float width, float height)
    {
        center = Vector3.Normalize(center);
        var forward = Vector3.Dot(direction, center);
        if (forward <= 0) return 0;
        var right = Vector3.Normalize(Vector3.Cross(center, Vector3.UnitY));
        var up = Vector3.Cross(right, center);
        var x = Math.Abs(Vector3.Dot(direction, right) / forward);
        var y = Math.Abs(Vector3.Dot(direction, up) / forward);
        return Math.Clamp((width - x) / 0.035f, 0, 1) * Math.Clamp((height - y) / 0.035f, 0, 1);
    }

    public void Dispose()
    {
        _disposed = true;
        _scene.Changed -= SceneChanged;
        foreach (var element in _viewport.Items) element.Dispose();
        _viewport.Items.Clear();
        _viewport.EffectsManager = null;
        _effects.Dispose();
        _environment.Load().Texture?.Dispose();
    }

    private sealed class ColorOutputEffect : ShaderEffect
    {
        private static readonly PixelShader OutputShader = CreateShader();
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(ColorOutputEffect), 0);

        public ColorOutputEffect()
        {
            PixelShader = OutputShader;
            UpdateShaderValue(InputProperty);
        }

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        private static PixelShader CreateShader()
        {
            const string source = """
                sampler2D image : register(s0);
                float4 main(float2 uv : TEXCOORD) : COLOR
                {
                    float4 c = tex2D(image, uv);
                    float3 low = 12.92 * c.rgb;
                    float3 high = 1.055 * pow(max(c.rgb, 0), 1.0 / 2.4) - 0.055;
                    return float4(lerp(low, high, step(0.0031308, c.rgb)), c.a);
                }
                """;
            using var bytecode = SharpDX.D3DCompiler.ShaderBytecode.Compile(source, "main", "ps_2_0");
            using var stream = new MemoryStream(bytecode.Bytecode.Data);
            var shader = new PixelShader();
            shader.SetStreamSource(stream);
            shader.Freeze();
            return shader;
        }
    }
}
