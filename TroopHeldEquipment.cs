using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using TpacTool.Lib;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private readonly HashSet<Model3D> _heldEquipmentModels = [];
    private IReadOnlyDictionary<string, string> _troopPreviewLoadout = new Dictionary<string, string>();
    private string _troopPreviewTitle = "Custom loadout";
    private bool _troopHeldWeapon, _troopHeldShield;
    private PoseAnimation? _equipmentPoseSelection;
    private string _troopHeldCategory = "";
    private string? _activeWeaponSlot;
    private bool _activeWeaponNeedsBothHands;
    private readonly Dictionary<string, XElement> _equipmentTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XElement> _equipmentPieceDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedHolsterGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Reference, bool Sheathed), Model3D> _equipmentPreviewModels = [];
    private readonly List<string> _equipmentAssetFolders = [];
    private readonly Dictionary<string, MeshAssetNode> _equipmentDependencyMeshes = new(StringComparer.OrdinalIgnoreCase);
    private bool _equipmentDependenciesIndexed;
    private readonly HashSet<Guid> _texturePixelGuids = [];

    private MeshAssetNode? FindEquipmentMesh(string name)
    {
        if (!ShouldParseAsset(Metamesh.TYPE_GUID, name)) return null;
        if (FindMeshOption(name) is { } selected) return selected;
        if (!_equipmentDependenciesIndexed)
        {
            foreach (var folder in _equipmentAssetFolders.AsEnumerable().Reverse().Where(Directory.Exists))
                foreach (var path in Directory.EnumerateFiles(folder, "*.tpac", SearchOption.AllDirectories).OrderBy(path => path))
                {
                    AssetPackage package;
                    // Index names and GUIDs without decoding unrelated asset metadata.
                    try { package = new AssetPackage(path, indexOnly: true, assetFilter: ShouldParseAsset); }
                    catch (Exception ex) { throw new InvalidDataException($"Could not index equipment dependency '{Path.GetFileName(path)}': {ex.Message}", ex); }
                    var info = new FileInfo(path);
                    foreach (var asset in package.Items)
                    {
                        if (asset.Type == TpacTool.Lib.Material.TYPE_GUID) _materialPackagePaths.TryAdd(asset.Guid, path);
                        else if (asset.Type == Texture.TYPE_GUID)
                        {
                            if (asset.TypelessDataSegments.Any(segment => segment.TypeGuid == TexturePixelData.TYPE_GUID) &&
                                _texturePixelGuids.Add(asset.Guid)) _texturePackagePaths[asset.Guid] = path;
                            else _texturePackagePaths.TryAdd(asset.Guid, path);
                        }
                        else if (asset.Type == Metamesh.TYPE_GUID && asset.TypelessDataSegments.Any(segment =>
                            segment.TypeGuid == VertexStreamData.TYPE_GUID || segment.TypeGuid == MeshEditData.TYPE_GUID))
                        {
                            var display = NormalizeMeshDisplayName(asset.Name);
                            _equipmentDependencyMeshes.TryAdd(display, new MeshAssetNode {
                                DisplayName = display, GroupKey = display, Kind = "Mesh", CanRender = true,
                                SourcePath = path, PackageName = Path.GetFileNameWithoutExtension(path),
                                ByteSize = info.Length, MetameshGuid = asset.Guid });
                        }
                    }
                }
            _equipmentDependenciesIndexed = true;
        }
        return _equipmentDependencyMeshes.GetValueOrDefault(NormalizeMeshDisplayName(name));
    }

    private Model3D CopyEquipmentPreview(string reference, bool sheathed, Func<Model3D> load)
    {
        var key = (reference.ToUpperInvariant(), sheathed);
        if (!_equipmentPreviewModels.TryGetValue(key, out var source))
            _equipmentPreviewModels[key] = source = load();
        return Copy(source);

        // Share immutable geometry/material data, but keep attachment transforms and wrappers independent.
        static Model3D Copy(Model3D source)
        {
            if (source is GeometryModel3D geometry)
                return new GeometryModel3D(geometry.Geometry, geometry.Material) { BackMaterial = geometry.BackMaterial, Transform = geometry.Transform };
            var group = (Model3DGroup)source;
            var result = new Model3DGroup { Transform = group.Transform };
            foreach (var child in group.Children) result.Children.Add(Copy(child));
            return result;
        }
    }

    private void EquipmentState_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _previewWorkspace != 1) return;
        RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
    }

    private bool IsSlotSheathed(string slot) => (slot == "Item0" ? Item0StateCombo : Item1StateCombo).SelectedIndex == 1;

    private void RefreshEquipmentStateControls(IReadOnlyDictionary<string, string> loadout)
    {
        foreach (var (slot, combo) in new[] { ("Item0", Item0StateCombo), ("Item1", Item1StateCombo) })
        {
            XElement? item = null;
            if (loadout.TryGetValue(slot, out var name))
            {
                try { _equipmentItems.TryGetValue(ResolveEquipmentItemReference(slot, name)[5..], out item); }
                catch (InvalidOperationException) { }
            }
            var ammo = item != null && IsAmmunition(WeaponCategory(item));
            var carried = item != null && (ammo || (IsShield(item) ? _activeWeaponNeedsBothHands : slot != _activeWeaponSlot));
            ((ComboBoxItem)combo.Items[0]).Content = carried ? "Carried" : "Held";
            combo.IsEnabled = item != null && !ammo;
            combo.ToolTip = ammo ? "Ammunition quiver or pouch" : carried ? "Carried while the other weapon occupies the hands" : "Equipment preview state";
        }
    }

    private static string WeaponCategory(XElement item)
    {
        var weapon = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Weapon");
        return GetAttributeValue(weapon ?? item, "weapon_class") ?? GetAttributeValue(item, "crafting_template") ?? "";
    }

    private static bool IsShield(XElement item) => GetAttributeValue(item, "type")?.Equals("Shield", StringComparison.OrdinalIgnoreCase) == true ||
        WeaponCategory(item) is "SmallShield" or "LargeShield";

    private static bool IsAmmunition(string category) => category is "Arrow" or "Bolt" or "Bullet" or "SlingStone";

    private void PrepareEquipmentHands(IReadOnlyDictionary<string, string> loadout)
    {
        _activeWeaponSlot = null;
        _activeWeaponNeedsBothHands = false;
        foreach (var slot in new[] { "Item0", "Item1" })
        {
            if (IsSlotSheathed(slot) || !loadout.TryGetValue(slot, out var name)) continue;
            try
            {
                var reference = ResolveEquipmentItemReference(slot, name);
                if (!_equipmentItems.TryGetValue(reference[5..], out var item) || IsShield(item)) continue;
                var category = WeaponCategory(item);
                if (IsAmmunition(category)) continue;
                _activeWeaponSlot = slot;
                _activeWeaponNeedsBothHands = category is "TwoHandedSword" or "TwoHandedAxe" or "TwoHandedMace" or
                    "TwoHandedPolearm" or "LowGripPolearm" or "Bow" or "Crossbow" or "Musket";
                break;
            }
            catch (InvalidOperationException) { /* The preview reports invalid item references per slot. */ }
        }
    }

    private static bool IsWeaponItem(XElement item) => item.Name.LocalName == "CraftedItem" ||
        item.Descendants().Any(element => element.Name.LocalName == "Weapon");

    private Model3D LoadHeldEquipmentModel(string equipmentName, string slot = "Item0")
    {
        var reference = ResolveEquipmentItemReference("Item0", equipmentName);
        if (!_equipmentItems.TryGetValue(reference[5..], out var item))
            throw new InvalidOperationException("Select an item ID from the loaded module XML.");
        var weapon = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Weapon");
        var category = WeaponCategory(item);
        var shield = IsShield(item);
        if (!shield && category is not ("OneHandedSword" or "Dagger" or "OneHandedAxe" or "Mace" or
            "TwoHandedSword" or "TwoHandedAxe" or "TwoHandedMace" or "OneHandedPolearm" or "TwoHandedPolearm" or
            "LowGripPolearm" or "Bow" or "Crossbow" or "Javelin" or "ThrowingAxe" or "ThrowingKnife" or "Stone" or
            "Arrow" or "Bolt" or "Bullet" or "SlingStone" or "Pistol" or "Musket"))
            throw new InvalidOperationException($"Unsupported weapon class '{category}'.");
        if (_troopPoses == null)
            throw new InvalidOperationException("Native animation assets are required to attach held equipment.");

        var sheathed = IsSlotSheathed(slot) || IsAmmunition(category) ||
            (shield ? _activeWeaponNeedsBothHands || _troopHeldShield : slot != _activeWeaponSlot);
        if (sheathed) return LoadSheathedEquipmentModel(item, reference, category);

        // Native human monster data uses finger/item bones, and an optional forearm shield mount.
        var secondary = item.Elements().Where(element => element.Name.LocalName == "Flags")
            .Any(flags => bool.TryParse(GetAttributeValue(flags, "ForceAttachOffHandSecondaryItemBone"), out var enabled) && enabled);
        var offhand = item.Elements().Where(element => element.Name.LocalName == "Flags")
            .Any(flags => bool.TryParse(GetAttributeValue(flags, "ForceAttachOffHandPrimaryItemBone"), out var enabled) && enabled);
        var boneName = shield ? secondary ? "l_foretwist1" : "l_finger0" : offhand || category == "Bow" ? "l_finger0" : "r_finger0";
        var attachment = _troopPoses.GetItemAttachment(boneName, CreateWeaponFrame(weapon));
        var model = LoadEquipmentModel(reference);
        StudioRenderer.AttachToBone(model, attachment.Bone, attachment.Frame);
        _heldEquipmentModels.Add(model);
        if (shield) _troopHeldShield = true;
        else { _troopHeldWeapon = true; _troopHeldCategory = category; }
        return model;
    }

    private Model3D LoadSheathedEquipmentModel(XElement item, string reference, string category)
    {
        var templateId = GetAttributeValue(item, "crafting_template") ?? "";
        _equipmentTemplates.TryGetValue(templateId, out var template);
        var holsters = GetAttributeValue(item, "item_holsters") ?? GetAttributeValue(template ?? item, "item_holsters");
        if (string.IsNullOrWhiteSpace(holsters))
            throw new InvalidOperationException("This item has no authored holster positions.");
        var shift = ReadWeaponVector(GetAttributeValue(item, "holster_position_shift") ??
            GetAttributeValue(template ?? item, "default_item_holster_position_offset"));
        if (item.Name.LocalName == "CraftedItem")
        {
            // WeaponDesign.CalculateHolsterShiftAmount scales the template + handle offset, then adds the guard length.
            foreach (var piece in item.Descendants().Where(element => element.Name.LocalName == "Piece"))
                if (_equipmentPieceDefinitions.TryGetValue(GetAttributeValue(piece, "id") ?? "", out var definition))
                {
                    var type = GetAttributeValue(piece, "Type") ?? GetAttributeValue(definition, "piece_type");
                    var scale = TryFloat(GetAttributeValue(piece, "scale_factor"), out var value) ? value * .01f : 1;
                    if (type == "Handle") shift = (shift + ReadWeaponVector(GetAttributeValue(definition, "item_holster_pos_shift"))) * scale;
                }
            foreach (var piece in item.Descendants().Where(element => element.Name.LocalName == "Piece"))
                if (_equipmentCraftingPieces.TryGetValue(GetAttributeValue(piece, "id") ?? "", out var definition) &&
                    (GetAttributeValue(piece, "Type") ?? definition.PieceType) == "Guard")
                    shift.Z += definition.Length * (TryFloat(GetAttributeValue(piece, "scale_factor"), out var scale) ? scale * .01f : 1) * .01f;
        }
        var attachment = _troopPoses!.GetHolsterAttachment(holsters.Split(':'), shift, _usedHolsterGroups);
        Model3D model;
        if (item.Name.LocalName == "CraftedItem")
            model = CopyEquipmentPreview(reference, true, () => LoadCraftedEquipmentModel(item, sheathed: true));
        else
        {
            var holsterMesh = GetAttributeValue(item, "holster_mesh_with_weapon") ?? GetAttributeValue(item, "holster_mesh");
            if (IsAmmunition(category) && string.IsNullOrWhiteSpace(holsterMesh))
                throw new InvalidOperationException("Ammunition has no authored quiver or pouch mesh.");
            model = string.IsNullOrWhiteSpace(holsterMesh) ? LoadEquipmentModel(reference) : CopyEquipmentPreview(reference, true, () =>
                LoadMeshModel(FindEquipmentMesh(holsterMesh) ?? throw new InvalidOperationException($"Holster mesh '{holsterMesh}' is not in the loaded module assets."), out _, MeshRenderMode.Raw));
        }
        // Packed quiver/scabbard meshes have their own orientation; only rotate a bare weapon mesh.
        if (GetAttributeValue(template ?? item, "rotate_weapon_in_holster") == "true" &&
            GetAttributeValue(template ?? item, "use_weapon_as_holster_mesh") == "true")
        {
            var rotation = new Transform3DGroup();
            rotation.Children.Add(model.Transform);
            rotation.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new System.Windows.Media.Media3D.Vector3D(1, 0, 0), 180)));
            model.Transform = rotation;
        }
        StudioRenderer.AttachToBone(model, attachment.Bone, attachment.Frame);
        _heldEquipmentModels.Add(model);
        _usedHolsterGroups.Add(attachment.Group);
        return model;
    }

    private static Matrix4x4 CreateWeaponFrame(XElement? weapon)
    {
        if (weapon == null) return Matrix4x4.Identity;
        var position = ReadWeaponVector(GetAttributeValue(weapon, "position"));
        var rotation = ReadWeaponVector(GetAttributeValue(weapon, "rotation")) * (MathF.PI / 180);
        // Match WeaponComponentData.Deserialize: rotate about local up, then side, then forward.
        var frame = Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationX(rotation.X) *
            Matrix4x4.CreateRotationZ(rotation.Z);
        frame.Translation = position;
        return frame;
    }

    private static Vector3 ReadWeaponVector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Vector3.Zero;
        var parts = value.Split(',');
        if (parts.Length != 3) throw new InvalidOperationException("Invalid equipment attachment vector.");
        var components = parts.Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (components.Any(component => !float.IsFinite(component)))
            throw new InvalidOperationException("Invalid equipment attachment vector.");
        return new Vector3(components[0], components[1], components[2]);
    }

    private void SelectEquipmentHoldingPose()
    {
        if (!_poseAvailable || _troopPoses == null || _heldEquipmentModels.Count == 0) return;
        if (PoseAnimationCombo.SelectedItem is PoseAnimation selected && selected.Source != null &&
            !ReferenceEquals(selected, _equipmentPoseSelection)) return;
        var animation = _troopPoses.GetHoldingPose(_troopHeldCategory, _troopHeldShield);
        if (animation == null) return;
        _equipmentPoseSelection = animation;
        if (!_poseOptions.Contains(animation)) _poseOptions.Add(animation);
        PoseAnimationCombo.SelectedItem = animation;
    }
}
