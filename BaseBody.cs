using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using TpacTool.Lib;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    // Native "man" skin (Native/ModuleData/skins.xml). A part is hidden when the covering slot holds an item whose
    // flag hides it: chest and leg armour cover unless they say covers_*="false", gloves and helmets only when they
    // say covers_*="true" (open bracers and helmets keep the hands and face visible).
    private static readonly (string Mesh, string Slot, string Flag, bool CoversByDefault)[] BaseBodyParts =
    [
        ("body_male_a", "Body", "covers_body", true),
        ("hands_male_a", "Arm", "covers_hands", false),
        ("feet_male_a", "Leg", "covers_legs", true),
        ("head_male_a", "Helmet", "covers_head", false),
    ];

    private readonly Dictionary<string, MeshAssetNode> _nativeBodyMeshes = new(StringComparer.OrdinalIgnoreCase);
    private string? _nativeBodyIndexPath;

    private void AddBaseBody(Model3DGroup assembled, IReadOnlyDictionary<string, string> loadout)
    {
        foreach (var part in BaseBodyParts)
        {
            if (IsBaseBodyPartHidden(part, loadout)) continue;
            if (FindBaseBodyMesh(part.Mesh) is not { } asset) continue;
            try { assembled.Children.Add(CopyEquipmentPreview("basebody:" + part.Mesh, false, () => LoadMeshModel(asset, out _, MeshRenderMode.Raw))); }
            catch (Exception) { }
        }
    }

    private bool IsBaseBodyPartHidden((string Mesh, string Slot, string Flag, bool CoversByDefault) part, IReadOnlyDictionary<string, string> loadout)
    {
        var manual = part.Mesh switch
        {
            "body_male_a" => HideBodyCheckBox.IsChecked,
            "hands_male_a" => HideHandsCheckBox.IsChecked,
            "feet_male_a" => HideLegsCheckBox.IsChecked,
            _ => HideFaceCheckBox.IsChecked,
        };
        if (manual == true) return true;
        if (!loadout.TryGetValue(part.Slot, out var equipment) || string.IsNullOrWhiteSpace(equipment)) return false;
        var id = equipment.StartsWith("Item.", StringComparison.OrdinalIgnoreCase) ? equipment[5..] : equipment;
        if (!_equipmentItems.TryGetValue(id, out var item)) return part.CoversByDefault;
        var flag = item.DescendantsAndSelf().Select(element => GetAttributeValue(element, part.Flag)).FirstOrDefault(value => value != null);
        return flag == null ? part.CoversByDefault : flag.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private MeshAssetNode? FindBaseBodyMesh(string name)
    {
        if (FindEquipmentMesh(name) is { } found) return found;
        if (_poseLibraryPath == null) return null;
        if (!string.Equals(_nativeBodyIndexPath, _poseLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            _nativeBodyIndexPath = _poseLibraryPath;
            _nativeBodyMeshes.Clear();
            var path = Path.Combine(_poseLibraryPath, "core_game.tpac");
            if (File.Exists(path))
            {
                var package = new AssetPackage(path, indexOnly: true, assetFilter: ShouldParseAsset);
                var info = new FileInfo(path);
                var wanted = BaseBodyParts.Select(part => part.Mesh).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var asset in package.Items)
                {
                    if (asset.Type == TpacTool.Lib.Material.TYPE_GUID) _materialPackagePaths.TryAdd(asset.Guid, path);
                    else if (asset.Type == Texture.TYPE_GUID)
                    {
                        if (asset.TypelessDataSegments.Any(segment => segment.TypeGuid == TexturePixelData.TYPE_GUID) &&
                            _texturePixelGuids.Add(asset.Guid)) _texturePackagePaths[asset.Guid] = path;
                        else _texturePackagePaths.TryAdd(asset.Guid, path);
                    }
                    else if (asset.Type == Metamesh.TYPE_GUID && wanted.Contains(asset.Name))
                        _nativeBodyMeshes[asset.Name] = new MeshAssetNode {
                            DisplayName = asset.Name, GroupKey = asset.Name, Kind = "Mesh", CanRender = true,
                            SourcePath = path, PackageName = Path.GetFileNameWithoutExtension(path),
                            ByteSize = info.Length, MetameshGuid = asset.Guid };
                }
            }
        }
        return _nativeBodyMeshes.GetValueOrDefault(name);
    }

    private void BaseBodyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_previewWorkspace == 1 && _troopPreviewLoadout.Count > 0)
            RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
    }
}
