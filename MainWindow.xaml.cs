using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using Microsoft.Win32;
using TpacTool.Lib;

namespace LOTRAOM_Viewport;

public partial class MainWindow : Window
{
    private static readonly string[] EquipmentSlots = ["Helmet", "Cape", "Body", "Arm", "Leg", "Item0", "Item1"];
    private readonly ObservableCollection<TpacPackageNode> _packages = [];
    private readonly ObservableCollection<TroopNode> _troops = [];
    private readonly Model3DGroup _scene = new();
    private readonly Model3DGroup _assetModel = new();
    private Point _lastMousePosition;
    private bool _isOrbiting;
    private double _yaw = 34;
    private double _pitch = -18;
    private double _distance = 8.2;

    public MainWindow()
    {
        InitializeComponent();

        AssetTree.ItemsSource = _packages;
        TroopList.ItemsSource = _troops;
        ModelViewport.Children.Add(new ModelVisual3D { Content = _scene });
        ResetScene();
        RenderEmptyPreview();
    }

    private void ChooseAssetPackagesFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Bannerlord AssetPackages folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadAssetPackages(dialog.FolderName);
    }

    private void LoadAssetPackages(string folderPath)
    {
        AssetPathBox.Text = folderPath;
        _packages.Clear();

        if (!Directory.Exists(folderPath))
        {
            ScanSummaryText.Text = "Selected folder does not exist.";
            PackageCountText.Text = "0";
            MeshCountText.Text = "0";
            RenderEmptyPreview();
            return;
        }

        var packageFiles = Directory.EnumerateFiles(folderPath, "*.tpac", SearchOption.AllDirectories)
            .OrderBy(Path.GetFileName)
            .ToArray();

        foreach (var filePath in packageFiles)
        {
            var info = new FileInfo(filePath);
            var package = new TpacPackageNode
            {
                DisplayName = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                Badge = FormatFileSize(info.Length)
            };

            PopulatePackageAssets(package, info);

            _packages.Add(package);
        }

        PackageCountText.Text = _packages.Count.ToString();
        MeshCountText.Text = _packages.Sum(package => package.Assets.Count).ToString();
        ExtractorStatusText.Text = _packages.Count == 0 ? "not loaded" : "TpacTool.Lib";
        ScanSummaryText.Text = _packages.Count == 0
            ? "No .tpac files found in this folder."
            : $"Parsed {_packages.Count} .tpac package(s).";

        if (_packages.Count == 0)
        {
            RenderEmptyPreview();
            SelectedAssetTitle.Text = "No packages found";
            SelectedAssetSubtitle.Text = "Choose a folder containing .tpac files.";
        }
        else
        {
            RenderEmptyPreview();
            SelectedAssetTitle.Text = "Packages loaded";
            SelectedAssetSubtitle.Text = "Select a mesh candidate from the left.";
        }
    }

    private void ChooseTroopXml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Bannerlord troop XML",
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadTroopXml(dialog.FileName);
    }

    private void LoadTroopXml(string filePath)
    {
        TroopXmlPathBox.Text = filePath;
        _troops.Clear();

        try
        {
            var document = XDocument.Load(filePath);
            var troops = document
                .Descendants()
                .Where(IsProbableTroopElement)
                .Select(ParseTroopNode)
                .Where(troop => !string.IsNullOrWhiteSpace(troop.Id))
                .GroupBy(troop => troop.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(troop => troop.DisplayName)
                .ToArray();

            foreach (var troop in troops)
            {
                _troops.Add(troop);
            }

            TroopSummaryText.Text = _troops.Count == 0
                ? "No troop-like entries found in this XML."
                : $"Loaded {_troops.Count} troop(s).";

            SelectedAssetTitle.Text = "Troop XML loaded";
            SelectedAssetSubtitle.Text = "Select a troop or build a custom loadout.";
        }
        catch (Exception ex)
        {
            TroopSummaryText.Text = $"Could not load XML: {ex.Message}";
            SelectedAssetTitle.Text = "Troop XML failed";
            SelectedAssetSubtitle.Text = ex.Message;
        }
    }

    private static bool IsProbableTroopElement(XElement element)
    {
        var name = element.Name.LocalName;
        var hasIdentity = GetAttributeValue(element, "id") != null || GetAttributeValue(element, "name") != null;
        if (!hasIdentity)
        {
            return false;
        }

        return name.Equals("NPCCharacter", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Troop", StringComparison.OrdinalIgnoreCase) ||
               element.Descendants().Any(descendant => descendant.Name.LocalName.Equals("equipment", StringComparison.OrdinalIgnoreCase));
    }

    private static TroopNode ParseTroopNode(XElement element)
    {
        var id = GetAttributeValue(element, "id") ?? GetAttributeValue(element, "name") ?? "unnamed_troop";
        var displayName = CleanXmlDisplayText(GetAttributeValue(element, "name")) ?? id;
        var troop = new TroopNode
        {
            Id = id,
            DisplayName = displayName
        };

        foreach (var slot in EquipmentSlots)
        {
            var value = FindEquipmentValue(element, slot);
            if (!string.IsNullOrWhiteSpace(value))
            {
                troop.Equipment[slot] = value;
            }
        }

        return troop;
    }

    private static string? FindEquipmentValue(XElement troopElement, string slot)
    {
        var directValue = GetAttributeValue(troopElement, slot);
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        foreach (var element in troopElement.Descendants().Where(descendant =>
                     descendant.Name.LocalName.Equals("equipment", StringComparison.OrdinalIgnoreCase)))
        {
            var slotValue = GetAttributeValue(element, "slot");
            if (!slot.Equals(slotValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return GetAttributeValue(element, "id") ??
                   GetAttributeValue(element, "item") ??
                   GetAttributeValue(element, "item_id") ??
                   GetAttributeValue(element, "name");
        }

        return null;
    }

    private static string? GetAttributeValue(XElement element, string attributeName)
    {
        return element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string? CleanXmlDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();
        var equalsIndex = cleaned.LastIndexOf('=');
        if (cleaned.StartsWith("{", StringComparison.Ordinal) && equalsIndex >= 0 && equalsIndex < cleaned.Length - 1)
        {
            cleaned = cleaned[(equalsIndex + 1)..].TrimEnd('}');
        }

        return cleaned;
    }

    private void TroopList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TroopList.SelectedItem is not TroopNode troop)
        {
            return;
        }

        SetEquipmentBoxes(troop.Equipment);
        RenderLoadoutPreview(troop.DisplayName, troop.Equipment);
    }

    private void ApplyCustomLoadout_Click(object sender, RoutedEventArgs e)
    {
        var loadout = ReadEquipmentBoxes();
        RenderLoadoutPreview("Custom loadout", loadout);
    }

    private void ClearCustomLoadout_Click(object sender, RoutedEventArgs e)
    {
        SetEquipmentBoxes(new Dictionary<string, string>());
        RenderLoadoutPreview("Custom loadout", new Dictionary<string, string>());
    }

    private Dictionary<string, string> ReadEquipmentBoxes()
    {
        var loadout = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSlotValue(loadout, "Helmet", HelmetBox.Text);
        AddSlotValue(loadout, "Cape", CapeBox.Text);
        AddSlotValue(loadout, "Body", BodyBox.Text);
        AddSlotValue(loadout, "Arm", ArmBox.Text);
        AddSlotValue(loadout, "Leg", LegBox.Text);
        AddSlotValue(loadout, "Item0", Item0Box.Text);
        AddSlotValue(loadout, "Item1", Item1Box.Text);
        return loadout;
    }

    private void SetEquipmentBoxes(IReadOnlyDictionary<string, string> loadout)
    {
        HelmetBox.Text = GetSlotValue(loadout, "Helmet");
        CapeBox.Text = GetSlotValue(loadout, "Cape");
        BodyBox.Text = GetSlotValue(loadout, "Body");
        ArmBox.Text = GetSlotValue(loadout, "Arm");
        LegBox.Text = GetSlotValue(loadout, "Leg");
        Item0Box.Text = GetSlotValue(loadout, "Item0");
        Item1Box.Text = GetSlotValue(loadout, "Item1");
    }

    private static void AddSlotValue(IDictionary<string, string> loadout, string slot, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            loadout[slot] = value.Trim();
        }
    }

    private static string GetSlotValue(IReadOnlyDictionary<string, string> loadout, string slot)
    {
        return loadout.TryGetValue(slot, out var value) ? value : string.Empty;
    }

    private void RenderLoadoutPreview(string title, IReadOnlyDictionary<string, string> loadout)
    {
        _assetModel.Children.Clear();

        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.03, 0), 2.1, 0.06, 2.1, Color.FromRgb(21, 25, 30)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.9, 0), 0.58, 1.25, 0.36, ResolveLoadoutColor(loadout, "Body", Color.FromRgb(92, 98, 104))));
        _assetModel.Children.Add(CreateSphere(new Point3D(0, 1.65, 0), 0.24, Color.FromRgb(128, 132, 128)));

        if (loadout.ContainsKey("Helmet"))
        {
            _assetModel.Children.Add(CreateBox(new Point3D(0, 1.92, 0), 0.42, 0.26, 0.36, ResolveLoadoutColor(loadout, "Helmet", Color.FromRgb(145, 150, 150))));
        }

        if (loadout.ContainsKey("Cape"))
        {
            _assetModel.Children.Add(CreateBox(new Point3D(0, 0.95, 0.25), 0.74, 1.25, 0.08, ResolveLoadoutColor(loadout, "Cape", Color.FromRgb(72, 68, 76))));
        }

        if (loadout.ContainsKey("Arm"))
        {
            var color = ResolveLoadoutColor(loadout, "Arm", Color.FromRgb(112, 118, 124));
            _assetModel.Children.Add(CreateBox(new Point3D(-0.47, 1.02, 0), 0.18, 0.82, 0.18, color, -12));
            _assetModel.Children.Add(CreateBox(new Point3D(0.47, 1.02, 0), 0.18, 0.82, 0.18, color, 12));
        }

        if (loadout.ContainsKey("Leg"))
        {
            var color = ResolveLoadoutColor(loadout, "Leg", Color.FromRgb(70, 78, 86));
            _assetModel.Children.Add(CreateBox(new Point3D(-0.17, 0.25, 0), 0.18, 0.62, 0.18, color));
            _assetModel.Children.Add(CreateBox(new Point3D(0.17, 0.25, 0), 0.18, 0.62, 0.18, color));
        }

        if (loadout.ContainsKey("Item0"))
        {
            _assetModel.Children.Add(CreateBox(new Point3D(-0.82, 0.95, -0.1), 0.08, 1.25, 0.08, ResolveLoadoutColor(loadout, "Item0", Color.FromRgb(94, 84, 68)), -18));
        }

        if (loadout.ContainsKey("Item1"))
        {
            _assetModel.Children.Add(CreateBox(new Point3D(0.82, 0.95, -0.04), 0.16, 1.0, 0.48, ResolveLoadoutColor(loadout, "Item1", Color.FromRgb(42, 48, 56)), 8));
        }

        SelectedAssetTitle.Text = title;
        SelectedAssetSubtitle.Text = loadout.Count == 0
            ? "No equipment selected."
            : $"{loadout.Count} equipment slot(s) populated.";
    }

    private static Color ResolveLoadoutColor(IReadOnlyDictionary<string, string> loadout, string slot, Color fallback)
    {
        return loadout.TryGetValue(slot, out var value) ? ColorFromName(value) : fallback;
    }

    private static void PopulatePackageAssets(TpacPackageNode package, FileInfo info)
    {
        try
        {
            var assetPackage = new AssetPackage(info.FullName, loadHeaderNow: true, loadDataNow: false);
            var modelAssets = assetPackage.Items
                .SelectMany(asset => CreateModelNodes(asset, package.DisplayName, info))
                .GroupBy(asset => asset.GroupKey)
                .Select(group => group
                    .OrderByDescending(asset => asset.CanRender)
                    .ThenByDescending(asset => asset.VertexCount)
                    .ThenBy(asset => asset.DisplayName)
                    .First())
                .OrderBy(asset => asset.DisplayName)
                .ToArray();

            foreach (var asset in modelAssets)
            {
                package.Assets.Add(asset);
            }

            package.Badge = $"{modelAssets.Length} model(s)";

            if (package.Assets.Count == 0)
            {
                package.Assets.Add(new MeshAssetNode
                {
                    DisplayName = package.DisplayName,
                    PackageName = package.DisplayName,
                    SourcePath = info.FullName,
                    ByteSize = info.Length,
                    Kind = "Package",
                    GroupKey = package.DisplayName,
                    Status = "Parsed; no Geometry or Metamesh records found"
                });
            }
        }
        catch (Exception ex)
        {
            package.Badge = "parse failed";
            package.Assets.Add(new MeshAssetNode
            {
                DisplayName = package.DisplayName,
                PackageName = package.DisplayName,
                SourcePath = info.FullName,
                ByteSize = info.Length,
                Kind = "Package",
                GroupKey = package.DisplayName,
                Status = $"Could not parse TPAC header: {ex.Message}"
            });
        }
    }

    private static IEnumerable<MeshAssetNode> CreateModelNodes(AssetItem asset, string packageName, FileInfo packageFile)
    {
        if (asset is Metamesh metamesh)
        {
            if (metamesh.Meshes.Count == 0)
            {
                yield return new MeshAssetNode
                {
                    DisplayName = metamesh.Name,
                    PackageName = packageName,
                    SourcePath = packageFile.FullName,
                    ByteSize = packageFile.Length,
                    Kind = "Metamesh",
                    GroupKey = NormalizeMeshDisplayName(metamesh.Name),
                    MetameshGuid = metamesh.Guid,
                    Status = "Parsed; no child meshes"
                };
                yield break;
            }

            foreach (var mesh in metamesh.Meshes)
            {
                var displayName = string.IsNullOrWhiteSpace(mesh.Name)
                    ? metamesh.Name
                    : mesh.Name;

                var canRender = mesh.VertexStream != null || mesh.EditData != null;
                yield return new MeshAssetNode
                {
                    DisplayName = NormalizeMeshDisplayName(displayName),
                    PackageName = packageName,
                    SourcePath = packageFile.FullName,
                    ByteSize = packageFile.Length,
                    Kind = "Mesh",
                    GroupKey = NormalizeMeshDisplayName(displayName),
                    MetameshGuid = metamesh.Guid,
                    MeshGuid = mesh.Guid,
                    VertexCount = mesh.VertexCount,
                    FaceCount = mesh.FaceCount,
                    CanRender = canRender,
                    Status = canRender
                        ? $"Parsed - {mesh.VertexCount:n0} vertices - {mesh.FaceCount:n0} faces"
                        : $"Parsed metadata - {mesh.VertexCount:n0} vertices - {mesh.FaceCount:n0} faces"
                };
            }

            yield break;
        }

        yield break;
    }

    private static string NormalizeMeshDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed_mesh";
        }

        var normalized = name.Trim();
        while (true)
        {
            var dotIndex = normalized.LastIndexOf('.');
            if (dotIndex < 0 || dotIndex == normalized.Length - 1)
            {
                return normalized;
            }

            var suffix = normalized[(dotIndex + 1)..];
            if (!suffix.All(char.IsDigit) && !IsLodSuffix(suffix))
            {
                return normalized;
            }

            normalized = normalized[..dotIndex];
        }
    }

    private static bool IsLodSuffix(string suffix)
    {
        return suffix.Length > 3 &&
               suffix.StartsWith("lod", StringComparison.OrdinalIgnoreCase) &&
               suffix[3..].All(char.IsDigit);
    }

    private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MeshAssetNode asset)
        {
            SelectedAssetTitle.Text = asset.DisplayName;
            SelectedAssetSubtitle.Text = $"{asset.Kind} - {asset.PackageName} - {asset.Status}";
            RenderSelectedAsset(asset);
        }
    }

    private void RenderSelectedAsset(MeshAssetNode asset)
    {
        if (asset.Kind != "Mesh" || asset.MeshGuid == Guid.Empty)
        {
            _assetModel.Children.Clear();
            SelectedAssetSubtitle.Text = $"{asset.Kind} - {asset.PackageName} - no renderable mesh stream selected";
            return;
        }

        try
        {
            var package = new AssetPackage(asset.SourcePath, loadHeaderNow: true, loadDataNow: false);
            var metamesh = package.Items.OfType<Metamesh>().FirstOrDefault(item => item.Guid == asset.MetameshGuid);
            var mesh = metamesh?.Meshes.FirstOrDefault(item => item.Guid == asset.MeshGuid);

            if (mesh?.VertexStream == null && mesh?.EditData == null)
            {
                _assetModel.Children.Clear();
                SelectedAssetSubtitle.Text = $"{asset.Kind} - {asset.PackageName} - mesh has no renderable geometry stream";
                return;
            }

            var model = mesh.VertexStream != null
                ? CreateTpacMeshModel(mesh.VertexStream.Data, asset.DisplayName, out var renderedVertices)
                : CreateTpacEditMeshModel(mesh.EditData!.Data, asset.DisplayName, out renderedVertices);

            _assetModel.Children.Clear();
            _assetModel.Children.Add(model);

            SelectedAssetSubtitle.Text =
                $"{asset.Kind} - {asset.PackageName} - rendered {renderedVertices:n0} vertices";
        }
        catch (Exception ex)
        {
            _assetModel.Children.Clear();
            SelectedAssetSubtitle.Text = $"{asset.Kind} - {asset.PackageName} - render failed: {ex.Message}";
        }
    }

    private void ResetScene()
    {
        _scene.Children.Clear();
        _scene.Children.Add(new AmbientLight(Color.FromRgb(92, 98, 105)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(238, 231, 203), new Vector3D(-0.4, -0.7, -0.45)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(84, 122, 160), new Vector3D(0.7, -0.25, 0.4)));
        _scene.Children.Add(CreateGrid(18, 1));
        _scene.Children.Add(_assetModel);
        UpdateCamera();
    }

    private void RenderEmptyPreview()
    {
        _assetModel.Children.Clear();
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.05, 0), 1.5, 0.1, 1.5, Color.FromRgb(60, 65, 70)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.75, 0), 0.72, 1.15, 0.46, Color.FromRgb(85, 92, 100)));
        _assetModel.Children.Add(CreateSphere(new Point3D(0, 1.55, 0), 0.28, Color.FromRgb(96, 104, 112)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.02, 0), 1.95, 0.04, 1.95, Color.FromRgb(24, 28, 33)));
    }

    private void RenderAssetPlaceholder(MeshAssetNode asset)
    {
        _assetModel.Children.Clear();

        var accent = ColorFromName(asset.DisplayName);
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.03, 0), 2.2, 0.06, 2.2, Color.FromRgb(21, 25, 30)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.82, 0), 0.78, 1.28, 0.48, Color.FromRgb(112, 121, 130)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 1.12, -0.27), 0.68, 0.5, 0.08, accent));
        _assetModel.Children.Add(CreateSphere(new Point3D(0, 1.72, 0), 0.3, Color.FromRgb(142, 148, 153)));
        _assetModel.Children.Add(CreateBox(new Point3D(-0.58, 1.02, 0), 0.22, 0.9, 0.22, Color.FromRgb(120, 128, 135), -18));
        _assetModel.Children.Add(CreateBox(new Point3D(0.58, 1.02, 0), 0.22, 0.9, 0.22, Color.FromRgb(120, 128, 135), 18));
        _assetModel.Children.Add(CreateBox(new Point3D(-0.2, 0.2, 0), 0.22, 0.56, 0.22, Color.FromRgb(82, 91, 99)));
        _assetModel.Children.Add(CreateBox(new Point3D(0.2, 0.2, 0), 0.22, 0.56, 0.22, Color.FromRgb(82, 91, 99)));
        _assetModel.Children.Add(CreateBox(new Point3D(0.95, 0.95, -0.08), 0.12, 1.25, 0.54, Color.FromRgb(28, 34, 42), -10));
    }

    private static GeometryModel3D CreateTpacMeshModel(VertexStreamData vertexStream, string name, out int renderedVertices)
    {
        var positions = GetVertexPositions(vertexStream).ToArray();
        if (positions.Length == 0)
        {
            throw new InvalidOperationException("Vertex stream did not contain positions.");
        }

        if (vertexStream.Indices.Length < 3)
        {
            throw new InvalidOperationException("Vertex stream did not contain triangle indices.");
        }

        renderedVertices = positions.Length;
        var minX = positions.Min(point => point.X);
        var minY = positions.Min(point => point.Y);
        var minZ = positions.Min(point => point.Z);
        var maxX = positions.Max(point => point.X);
        var maxY = positions.Max(point => point.Y);
        var maxZ = positions.Max(point => point.Z);
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var largestAxis = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
        var scale = largestAxis <= 0 ? 1 : 2.8 / largestAxis;

        var mesh = new MeshGeometry3D();
        foreach (var point in positions)
        {
            mesh.Positions.Add(new Point3D(
                (point.X - center.X) * scale,
                (point.Y - center.Y) * scale + 1.2,
                (point.Z - center.Z) * scale));
        }

        foreach (var index in vertexStream.Indices)
        {
            if (index >= 0 && index < positions.Length)
            {
                mesh.TriangleIndices.Add(index);
            }
        }

        var model = CreateModel(mesh, ColorFromName(name));
        return model;
    }

    private static GeometryModel3D CreateTpacEditMeshModel(MeshEditData editData, string name, out int renderedVertices)
    {
        if (editData.Positions.Length == 0 || editData.Vertices.Length == 0 || editData.Faces.Length == 0)
        {
            throw new InvalidOperationException("Mesh edit data did not contain positions, vertices, and faces.");
        }

        var rawPositions = editData.Vertices
            .Select(vertex =>
            {
                var index = (int)vertex.PositionIndex;
                if (index < 0 || index >= editData.Positions.Length)
                {
                    return new Point3D();
                }

                var position = editData.Positions[index];
                return ConvertBannerlordPoint(position.X, position.Y, position.Z);
            })
            .ToArray();

        renderedVertices = rawPositions.Length;
        var minX = rawPositions.Min(point => point.X);
        var minY = rawPositions.Min(point => point.Y);
        var minZ = rawPositions.Min(point => point.Z);
        var maxX = rawPositions.Max(point => point.X);
        var maxY = rawPositions.Max(point => point.Y);
        var maxZ = rawPositions.Max(point => point.Z);
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var largestAxis = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
        var scale = largestAxis <= 0 ? 1 : 2.8 / largestAxis;

        var mesh = new MeshGeometry3D();
        foreach (var point in rawPositions)
        {
            mesh.Positions.Add(new Point3D(
                (point.X - center.X) * scale,
                (point.Y - center.Y) * scale + 1.2,
                (point.Z - center.Z) * scale));
        }

        foreach (var face in editData.Faces)
        {
            if (IsValidIndex(face.V0, rawPositions.Length) &&
                IsValidIndex(face.V1, rawPositions.Length) &&
                IsValidIndex(face.V2, rawPositions.Length))
            {
                mesh.TriangleIndices.Add(face.V0);
                mesh.TriangleIndices.Add(face.V1);
                mesh.TriangleIndices.Add(face.V2);
            }
        }

        return CreateModel(mesh, ColorFromName(name));
    }

    private static bool IsValidIndex(int index, int length)
    {
        return index >= 0 && index < length;
    }

    private static IEnumerable<Point3D> GetVertexPositions(VertexStreamData vertexStream)
    {
        if (vertexStream.Positions.Length > 0)
        {
            foreach (var position in vertexStream.Positions)
            {
                yield return ConvertBannerlordPoint(position.X, position.Y, position.Z);
            }

            yield break;
        }

        foreach (var position in vertexStream.CompressedPositions)
        {
            yield return ConvertBannerlordPoint((float)position.X, (float)position.Y, (float)position.Z);
        }
    }

    private static Point3D ConvertBannerlordPoint(float x, float y, float z)
    {
        return new Point3D(x, z, -y);
    }

    private static Model3DGroup CreateGrid(int halfExtent, double step)
    {
        var group = new Model3DGroup();
        var lineColor = Color.FromRgb(28, 35, 43);

        for (var i = -halfExtent; i <= halfExtent; i++)
        {
            var thickness = i == 0 ? 0.026 : 0.014;
            var color = i == 0 ? Color.FromRgb(46, 56, 65) : lineColor;
            group.Children.Add(CreateBox(new Point3D(i * step, -0.015, 0), thickness, 0.012, halfExtent * step * 2, color));
            group.Children.Add(CreateBox(new Point3D(0, -0.014, i * step), halfExtent * step * 2, 0.012, thickness, color));
        }

        return group;
    }

    private static GeometryModel3D CreateBox(Point3D center, double width, double height, double depth, Color color, double zRotationDegrees = 0)
    {
        var x = width / 2;
        var y = height / 2;
        var z = depth / 2;
        var mesh = new MeshGeometry3D();

        var corners = new[]
        {
            new Point3D(-x, -y, -z), new Point3D(x, -y, -z), new Point3D(x, y, -z), new Point3D(-x, y, -z),
            new Point3D(-x, -y, z), new Point3D(x, -y, z), new Point3D(x, y, z), new Point3D(-x, y, z)
        };

        AddQuad(mesh, corners[0], corners[1], corners[2], corners[3]);
        AddQuad(mesh, corners[5], corners[4], corners[7], corners[6]);
        AddQuad(mesh, corners[4], corners[0], corners[3], corners[7]);
        AddQuad(mesh, corners[1], corners[5], corners[6], corners[2]);
        AddQuad(mesh, corners[3], corners[2], corners[6], corners[7]);
        AddQuad(mesh, corners[4], corners[5], corners[1], corners[0]);

        var model = CreateModel(mesh, color);
        var transforms = new Transform3DGroup();
        if (Math.Abs(zRotationDegrees) > 0.001)
        {
            transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), zRotationDegrees)));
        }

        transforms.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
        model.Transform = transforms;
        return model;
    }

    private static GeometryModel3D CreateSphere(Point3D center, double radius, Color color)
    {
        const int latitude = 18;
        const int longitude = 28;
        var mesh = new MeshGeometry3D();

        for (var lat = 0; lat <= latitude; lat++)
        {
            var theta = Math.PI * lat / latitude;
            var sinTheta = Math.Sin(theta);
            var cosTheta = Math.Cos(theta);

            for (var lon = 0; lon <= longitude; lon++)
            {
                var phi = 2 * Math.PI * lon / longitude;
                var x = radius * sinTheta * Math.Cos(phi);
                var y = radius * cosTheta;
                var z = radius * sinTheta * Math.Sin(phi);
                mesh.Positions.Add(new Point3D(center.X + x, center.Y + y, center.Z + z));
                mesh.Normals.Add(new Vector3D(x, y, z));
            }
        }

        for (var lat = 0; lat < latitude; lat++)
        {
            for (var lon = 0; lon < longitude; lon++)
            {
                var first = lat * (longitude + 1) + lon;
                var second = first + longitude + 1;
                mesh.TriangleIndices.Add(first);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(first + 1);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(second + 1);
                mesh.TriangleIndices.Add(first + 1);
            }
        }

        return CreateModel(mesh, color);
    }

    private static GeometryModel3D CreateModel(MeshGeometry3D mesh, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = new DiffuseMaterial(brush),
            BackMaterial = new DiffuseMaterial(brush)
        };
    }

    private static void AddQuad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.Positions.Add(d);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 3);
    }

    private void ModelViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isOrbiting = true;
        _lastMousePosition = e.GetPosition(ModelViewport);
        ModelViewport.CaptureMouse();
    }

    private void ModelViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOrbiting)
        {
            return;
        }

        var position = e.GetPosition(ModelViewport);
        var delta = position - _lastMousePosition;
        _lastMousePosition = position;

        _yaw += delta.X * 0.35;
        _pitch = Math.Clamp(_pitch - delta.Y * 0.25, -75, 45);
        UpdateCamera();
    }

    private void ModelViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isOrbiting = false;
        ModelViewport.ReleaseMouseCapture();
    }

    private void ModelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance - e.Delta * 0.004, 3.0, 18.0);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var yawRadians = _yaw * Math.PI / 180.0;
        var pitchRadians = _pitch * Math.PI / 180.0;
        var target = new Point3D(0, 0.85, 0);

        var x = target.X + _distance * Math.Cos(pitchRadians) * Math.Sin(yawRadians);
        var y = target.Y + _distance * Math.Sin(pitchRadians);
        var z = target.Z + _distance * Math.Cos(pitchRadians) * Math.Cos(yawRadians);

        ViewportCamera.Position = new Point3D(x, y, z);
        ViewportCamera.LookDirection = target - ViewportCamera.Position;
        ViewportCamera.UpDirection = new Vector3D(0, 1, 0);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static Color ColorFromName(string name)
    {
        var hash = name.Aggregate(17, (current, character) => current * 31 + character);
        return Color.FromRgb(
            (byte)(105 + Math.Abs(hash % 80)),
            (byte)(96 + Math.Abs(hash / 7 % 70)),
            (byte)(72 + Math.Abs(hash / 13 % 55)));
    }
}

public sealed class TpacPackageNode
{
    public string DisplayName { get; init; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public ObservableCollection<MeshAssetNode> Assets { get; } = [];
}

public sealed class MeshAssetNode
{
    public string DisplayName { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public long ByteSize { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string GroupKey { get; init; } = string.Empty;
    public Guid MetameshGuid { get; init; }
    public Guid MeshGuid { get; init; }
    public int VertexCount { get; init; }
    public int FaceCount { get; init; }
    public bool CanRender { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class TroopNode
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Dictionary<string, string> Equipment { get; } = new(StringComparer.OrdinalIgnoreCase);
}
