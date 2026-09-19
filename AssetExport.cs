using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Assimp;
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using TpacTool.Lib;
using WpfMaterial = System.Windows.Media.Media3D.Material;
using WpfMesh = System.Windows.Media.Media3D.MeshGeometry3D;
using NMatrix = System.Numerics.Matrix4x4;

namespace LOTRAOM_Viewport;

public sealed record AssetExportOptions
{
    public string OutputDirectory { get; init; } = "";
    public string Name { get; init; } = "asset";
    public bool Fbx { get; init; } = true;
    public bool Textures { get; init; } = true;
    public int TextureFormat { get; init; } = 0;
    public bool AllLods { get; init; }
    public bool CurrentPose { get; init; }
    public bool Skeleton { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
            throw new ArgumentException("Choose an absolute output folder.");
        if (string.IsNullOrWhiteSpace(Name) || Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Name.TrimEnd(' ', '.') != Name || Name is "." or ".." || Name.Length > 100)
            throw new ArgumentException("Enter a valid output name (up to 100 characters).");
        var stem = Name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
            throw new ArgumentException("Choose a name that is not reserved by Windows.");
        if (!Fbx && !Textures) throw new ArgumentException("Select FBX, textures, or both.");
        if (TextureFormat is < 0 or > 2) throw new ArgumentException("Choose PNG, DDS, or both.");
    }
}

internal sealed record ExportBone(string Name, int Parent, NMatrix Local, NMatrix InverseBind);
internal sealed record ExportMaterialSource(TpacTool.Lib.Material Material, IReadOnlyDictionary<Guid, TpacTool.Lib.Texture> LocalTextures);
internal sealed record AssetExportResult(string Directory, int Meshes, int Textures, int Warnings);

internal static class AssetExportMetadata
{
    private sealed class Metadata
    {
        internal string Name = "mesh";
        internal VertexStreamData? Stream;
        internal MeshEditData? Edit;
        internal bool Human;
    }
    private static readonly ConditionalWeakTable<WpfMesh, Metadata> Data = new();
    internal static void SetName(WpfMesh mesh, string name) => Data.GetOrCreateValue(mesh).Name = name;
    internal static void SetStream(WpfMesh mesh, VertexStreamData stream) => Data.GetOrCreateValue(mesh).Stream = stream;
    internal static void SetEdit(WpfMesh mesh, MeshEditData edit) => Data.GetOrCreateValue(mesh).Edit = edit;
    internal static MeshEditData? Edit(WpfMesh mesh) => Data.TryGetValue(mesh, out var data) ? data.Edit : null;
    internal static void SetHuman(WpfMesh mesh, string body) => Data.GetOrCreateValue(mesh).Human = body is
        "human_body" or "head" or "helmet_head_shoulder" or "detailed_head" or "head&shoulders" or "cape_body" or "empire_helmet_g" or "arms" or "legs";
    internal static bool IsHuman(Model3D model) => model is Model3DGroup group ? group.Children.Any(IsHuman) :
        model is GeometryModel3D { Geometry: WpfMesh mesh } && Data.TryGetValue(mesh, out var data) && data.Human;
    internal static string Name(WpfMesh mesh) => Data.TryGetValue(mesh, out var data) ? data.Name : "mesh";
    internal static VertexStreamData? Stream(WpfMesh mesh) => Data.TryGetValue(mesh, out var data) ? data.Stream : null;
    internal static void Copy(WpfMesh source, WpfMesh target)
    {
        if (Data.TryGetValue(source, out var data)) Data.Add(target, data);
    }

    internal static HelixToolkit.SharpDX.BoneIds[]? Skinning(WpfMesh mesh)
    {
        if (StudioRenderer.GetExportSkinning(mesh) is { } skin) return skin;
        var edit = Edit(mesh);
#pragma warning disable CS0612
        if (edit == null || edit.Bones.Length != edit.Positions.Length || edit.Bones.Length == 0) return null;
        return edit.Vertices.Select(vertex =>
        {
            var bone = edit.Bones[vertex.PositionIndex];
            return new HelixToolkit.SharpDX.BoneIds { Bone1 = bone.B0, Bone2 = bone.B1, Bone3 = bone.B2, Bone4 = bone.B3,
                Weights = new Vector4(bone.W0, bone.W1, bone.W2, bone.W3) };
        }).ToArray();
#pragma warning restore CS0612
    }
}

public partial class MainWindow
{
    private readonly ConditionalWeakTable<WpfMaterial, ExportMaterialSource> _exportMaterials = new();
    private readonly Dictionary<Model3D, string> _troopExportModels = new();
    private MeshAssetNode? _exportSelectedAsset;
    private AssetExportOptions? _assetExportOptions;
    private bool _assetExportRunning;

    private void AssetExport_Click(object sender, RoutedEventArgs e)
    {
        if (_assetExportRunning || _batchRenderRunning || _turntableRunning) return;
        var troop = _previewWorkspace == 1;
        if (_assetModel.Children.Count == 0 || (!troop && (_previewWorkspace != 0 || _exportSelectedAsset?.Kind != "Mesh")))
        {
            RenderExportToggle.IsChecked = true;
            RenderExportStatusText.Text = "Select a mesh or troop loadout first.";
            return;
        }
        RenderExportToggle.IsChecked = false;
        var options = (_assetExportOptions ?? new() { OutputDirectory = Path.Combine(SettingsDirectory, "Exports") })
            with { Name = SafeBatchName(troop ? _troopPreviewTitle : _exportSelectedAsset!.DisplayName) };
        var resume = _posePlaybackTimer.IsEnabled;
        StopPosePlayback();
        try
        {
            var dialog = new AssetExportWindow(this, options, troop, _troopPoses != null && (troop || AssetExportMetadata.IsHuman(_assetModel)),
                RunAssetExportAsync);
            dialog.OptionsChanged += edited => { _assetExportOptions = edited; SaveSettingsIfEnabled(); };
            dialog.ShowDialog();
        }
        finally { if (resume && _poseAvailable && _poseClip != null) PosePlay_Click(this, new RoutedEventArgs()); }
    }

    internal async Task<AssetExportResult> RunAssetExportAsync(AssetExportOptions options,
        IProgress<string> progress, CancellationToken cancellation)
    {
        options.Validate();
        if (_assetExportRunning || _batchRenderRunning || _turntableRunning)
            throw new InvalidOperationException("An export is already running.");
        var troop = _previewWorkspace == 1;
        if (!troop && (_previewWorkspace != 0 || _exportSelectedAsset?.Kind != "Mesh"))
            throw new InvalidOperationException("Select a mesh or troop loadout first.");
        if (troop && options.AllLods) throw new ArgumentException("Loadout export uses the displayed highest-detail meshes.");
        var warnings = new List<string>();
        var materialSources = new Dictionary<int, ExportMaterialSource>();
        var scene = new Scene { RootNode = new Node("Scene") };
        var materialIndices = new Dictionary<WpfMaterial, int>();
        var bones = options.Skeleton && !options.CurrentPose && (troop || AssetExportMetadata.IsHuman(_assetModel))
            ? _troopPoses?.GetExportBones() : null;
        var matrices = troop && options.CurrentPose ? _renderer.PoseState.Matrices?.ToArray() : null;
        var destination = Path.Combine(Path.GetFullPath(options.OutputDirectory),
            options.Name + $"-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var staging = destination + ".partial";
        var textureCount = 0;
        _assetExportRunning = true;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            progress.Report("Preparing geometry...");
            if (troop)
            {
                if (_troopExportModels.Count == 0) throw new InvalidOperationException("The loadout has no renderable meshes.");
                foreach (var (model, name) in _troopExportModels) AddModel(model, Matrix3D.Identity, name);
                foreach (var slot in _troopPreviewLoadout.Keys)
                    if (!_troopExportModels.Values.Any(name => name.StartsWith(slot + "_", StringComparison.Ordinal)))
                        warnings.Add($"{slot}: no preview mesh was available to export.");
            }
            else
            {
                var raw = LoadMeshModel(_exportSelectedAsset!, out _, MeshRenderMode.Raw, allLods: options.AllLods,
                    onSkipped: warnings.Add);
                AddModel(raw, Matrix3D.Identity, _exportSelectedAsset!.DisplayName);
            }
            if (scene.MeshCount == 0) throw new InvalidOperationException("No triangles could be exported.");
            if (bones != null)
            {
                var nodes = bones.Select(bone => new Node(bone.Name) { Transform = ToAssimp(bone.Local) }).ToArray();
                var armature = new Node("human_skeleton");
                scene.RootNode.Children.Add(armature);
                for (var i = 0; i < nodes.Length; i++)
                    (bones[i].Parent < 0 ? armature : nodes[bones[i].Parent]).Children.Add(nodes[i]);
            }
            Directory.CreateDirectory(staging);
            var textureReport = new List<object>();
            var exported = new Dictionary<Guid, string?>();
            if (options.Textures)
            {
                var folder = Path.Combine(staging, "textures");
                Directory.CreateDirectory(folder);
                foreach (var (materialIndex, source) in materialSources)
                foreach (var (slot, reference) in source.Material.Textures)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (reference.Guid == Guid.Empty) continue;
                    if (!exported.TryGetValue(reference.Guid, out var relative))
                    {
                        relative = null;
                        try
                        {
                            var texture = ResolveTexture(source.LocalTextures, reference.Guid);
                            if (texture?.TexturePixels?.Data.PrimaryRawImage is not { Length: > 0 })
                                throw new InvalidDataException("Texture pixel data is unavailable.");
                            progress.Report($"Exporting {texture.Name}...");
                            var stem = SafeBatchName(texture.Name) + "_" + texture.Guid.ToString("N");
                            var files = new List<string>();
                            if (options.TextureFormat != 1)
                            {
                                try
                                {
                                    var bitmap = CreateTextureBitmap(texture) ?? throw new InvalidDataException("PNG decoder returned no pixels.");
                                    var encoder = new PngBitmapEncoder();
                                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                    using var data = new MemoryStream();
                                    encoder.Save(data);
                                    var file = stem + ".png";
                                    await File.WriteAllBytesAsync(Path.Combine(folder, file), data.ToArray(), cancellation);
                                    relative = "textures/" + file;
                                    files.Add(file);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                { warnings.Add($"{texture.Name}: PNG unavailable ({ex.Message}). Select DDS to preserve this format."); }
                            }
                            if (options.TextureFormat != 0)
                            {
                                try
                                {
                                    using var data = new MemoryStream();
                                    new TpacTool.IO.DdsExporter { Texture = texture }.Export(data);
                                    var file = stem + ".dds";
                                    await File.WriteAllBytesAsync(Path.Combine(folder, file), data.ToArray(), cancellation);
                                    relative ??= "textures/" + file;
                                    files.Add(file);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                { warnings.Add($"{texture.Name}: DDS unavailable ({ex.Message})."); }
                            }
                            if (files.Count > 0) textureCount++;
                            textureReport.Add(new { texture.Name, texture.Guid, texture.Width, texture.Height,
                                Format = texture.Format.ToString(), texture.MipmapCount, texture.ArrayCount, Files = files });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { warnings.Add($"Texture {reference.Guid}: {ex.Message}"); }
                        exported[reference.Guid] = relative;
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                    // Packed gloss/metallic and faction masks are preserved, not mislabelled as standard PBR maps.
                    var usage = slot switch { 0 => TextureType.Diffuse, 2 => TextureType.Normals, _ => TextureType.Unknown };
                    if (relative != null && usage != TextureType.Unknown)
                        scene.Materials[materialIndex].AddMaterialTexture(new TextureSlot(relative, usage, 0,
                            TextureMapping.FromUV, 0, 1, TextureOperation.Multiply, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0));
                }
            }
            if (options.Fbx)
            {
                progress.Report("Writing FBX...");
                await Task.Run(() =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var context = new AssimpContext();
                    if (!context.ExportFile(scene, Path.Combine(staging, options.Name + ".fbx"), "fbx", PostProcessSteps.None))
                        throw new IOException("Assimp could not write the FBX file.");
                }, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            var report = new { options, Meshes = scene.MeshCount, Units = "centimetres", UpAxis = "Y",
                Pose = options.CurrentPose ? "Current pose (static)" : "Bind pose", Skeleton = bones != null,
                Materials = materialSources.Select(pair => new { Index = pair.Key, pair.Value.Material.Name,
                    pair.Value.Material.Guid, Flags = pair.Value.Material.ShaderMaterialFlags,
                    pair.Value.Material.BlendMode, pair.Value.Material.AlphaTest,
                    pair.Value.Material.ExtraMaterialSettings.SpecularCoef, pair.Value.Material.ExtraMaterialSettings.GlossCoef,
                    Slots = pair.Value.Material.Textures.Select(slot => new { Slot = slot.Key, TextureGuid = slot.Value.Guid }) }),
                Textures = textureReport, Warnings = warnings,
                FactionColours = new { Primary = _tableauPrimaryColour.ToString(), Secondary = _tableauSecondaryColour.ToString(), BakedIntoTextures = false },
                Notes = "Reconstructed geometry. Original texture channels/alpha are retained; Bannerlord shaders, cloth simulation, morphs and animation clips are not exported." };
            await File.WriteAllTextAsync(Path.Combine(staging, "export.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return new(destination, options.Fbx ? scene.MeshCount : 0, textureCount, warnings.Count);
        }
        finally
        {
            _assetExportRunning = false;
            // Only remove files in this export's uniquely named staging directory.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        void AddModel(Model3D model, Matrix3D parent, string name)
        {
            cancellation.ThrowIfCancellationRequested();
            var transform = model.Transform.Value;
            transform.Append(parent);
            if (model is Model3DGroup group)
            {
                foreach (var child in group.Children) AddModel(child, transform, name);
                return;
            }
            if (model is not GeometryModel3D { Geometry: WpfMesh geometry } part || geometry.TriangleIndices.Count < 3) return;
            var mesh = new Assimp.Mesh(name + "_" + AssetExportMetadata.Name(geometry) + "_" + scene.MeshCount, PrimitiveType.Triangle);
            if (!materialIndices.TryGetValue(part.Material, out var materialIndex))
            {
                materialIndex = scene.MaterialCount;
                var material = new Assimp.Material { Name = "material_" + materialIndex, ColorDiffuse = new Color4D(1, 1, 1, 1),
                    Opacity = 1, ShadingMode = ShadingMode.Phong };
                if (_exportMaterials.TryGetValue(part.Material, out var source))
                {
                    material.Name = source.Material.Name;
                    materialSources[materialIndex] = source;
                }
                else warnings.Add($"{mesh.Name}: original material references were unavailable.");
                scene.Materials.Add(material);
                materialIndices[part.Material] = materialIndex;
            }
            mesh.MaterialIndex = materialIndex;
            var skin = AssetExportMetadata.Skinning(geometry);
            var compatible = skin != null && skin.All(id => new[] { id.Bone1, id.Bone2, id.Bone3, id.Bone4 }
                .All(b => b >= 0 && b < (matrices?.Length ?? bones?.Length ?? 0)));
            if (skin != null && !compatible && (bones != null || matrices != null))
                warnings.Add($"{mesh.Name}: skinning does not match the human skeleton; exported without skinning.");
            var frame = ToNumerics(transform);
            if (!NMatrix.Invert(frame, out var inverse)) throw new InvalidDataException("Mesh transform is singular.");
            var normalFrame = NMatrix.Transpose(inverse);
            var sourceStream = AssetExportMetadata.Stream(geometry);
            var sourceEdit = AssetExportMetadata.Edit(geometry);
            var secondUvs = sourceStream?.Uv2 ?? sourceEdit?.Vertices.Select(v => v.SecondUv).ToArray();
            var colours1 = sourceStream?.Colors1 ?? sourceEdit?.Vertices.Select(v => v.Color).ToArray();
            var colours2 = sourceStream?.Colors2 ?? sourceEdit?.Vertices.Select(v => v.SecondColor).ToArray();
            mesh.UVComponentCount[0] = 2;
            if (secondUvs?.Length == geometry.Positions.Count) mesh.UVComponentCount[1] = 2;
            var boneWeights = new Dictionary<int, List<VertexWeight>>();
            var normals = geometry.Normals.Count == geometry.Positions.Count
                ? geometry.Normals.Select(n => new Vector3((float)n.X, (float)n.Y, (float)n.Z)).ToArray()
                : MeshGeometryHelper.CalculateNormals(new Vector3Collection(geometry.Positions.Select(p =>
                    new Vector3((float)p.X, (float)p.Y, (float)p.Z))), new IntCollection(geometry.TriangleIndices)).ToArray();
            for (var i = 0; i < geometry.Positions.Count; i++)
            {
                var p = geometry.Positions[i];
                var position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
                var normal = normals[i];
                if (compatible && skin != null)
                {
                    var id = skin[i];
                    int[] indices = [id.Bone1, id.Bone2, id.Bone3, id.Bone4];
                    float[] weights = [id.Weights.X, id.Weights.Y, id.Weights.Z, id.Weights.W];
                    var total = weights.Sum(w => Math.Max(0, w));
                    var posedPosition = Vector3.Zero;
                    var posedNormal = Vector3.Zero;
                    for (var b = 0; b < 4; b++)
                    {
                        var weight = total > 0 ? Math.Max(0, weights[b]) / total : b == 0 ? 1 : 0;
                        if (weight == 0) continue;
                        if (matrices != null)
                        {
                            posedPosition += Vector3.Transform(position, matrices[indices[b]]) * weight;
                            posedNormal += Vector3.TransformNormal(normal, matrices[indices[b]]) * weight;
                        }
                        else if (bones != null)
                        {
                            if (!boneWeights.TryGetValue(indices[b], out var list)) boneWeights[indices[b]] = list = new();
                            list.Add(new VertexWeight(i, weight));
                        }
                    }
                    if (matrices != null) { position = posedPosition; normal = posedNormal; }
                }
                position = Vector3.Transform(position, frame) * 100;
                normal = Vector3.TransformNormal(normal, normalFrame);
                if (normal.LengthSquared() > 0) normal = Vector3.Normalize(normal);
                mesh.Vertices.Add(new Assimp.Vector3D(position.X, position.Y, position.Z));
                mesh.Normals.Add(new Assimp.Vector3D(normal.X, normal.Y, normal.Z));
                var uv = i < geometry.TextureCoordinates.Count ? geometry.TextureCoordinates[i] : new Point();
                mesh.TextureCoordinateChannels[0].Add(new Assimp.Vector3D((float)uv.X, 1 - (float)uv.Y, 0));
                if (mesh.UVComponentCount[1] == 2)
                {
                    var second = secondUvs![i];
                    mesh.TextureCoordinateChannels[1].Add(new Assimp.Vector3D(second.X, 1 - second.Y, 0));
                }
                AddColour(colours1, 0, i);
                AddColour(colours2, 1, i);
            }
            for (var i = 0; i + 2 < geometry.TriangleIndices.Count; i += 3)
            {
                var a = geometry.TriangleIndices[i]; var b = geometry.TriangleIndices[i + 1]; var c = geometry.TriangleIndices[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= mesh.VertexCount || b >= mesh.VertexCount || c >= mesh.VertexCount)
                    throw new InvalidDataException("Mesh contains invalid triangle indices.");
                mesh.Faces.Add(new Face(transform.Determinant < 0 ? [a, c, b] : [a, b, c]));
            }
            foreach (var (index, weights) in boneWeights)
            {
                var bone = new Assimp.Bone { Name = bones![index].Name, OffsetMatrix = ToAssimp(inverse * bones[index].InverseBind) };
                bone.VertexWeights.AddRange(weights);
                mesh.Bones.Add(bone);
            }
            var node = new Node(mesh.Name);
            node.MeshIndices.Add(scene.MeshCount);
            scene.RootNode.Children.Add(node);
            scene.Meshes.Add(mesh);

            void AddColour(AbstractMeshData.Color[]? colours, int channel, int i)
            {
                if (colours?.Length != geometry.Positions.Count) return;
                var colour = colours[i];
                mesh.VertexColorChannels[channel].Add(new Color4D(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f));
            }
        }
    }

    private static NMatrix ToNumerics(Matrix3D m) => new((float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24, (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ, (float)m.M44);

    private static Assimp.Matrix4x4 ToAssimp(NMatrix m) => new(m.M11, m.M21, m.M31, m.M41 * 100,
        m.M12, m.M22, m.M32, m.M42 * 100, m.M13, m.M23, m.M33, m.M43 * 100, m.M14, m.M24, m.M34, m.M44);
}
