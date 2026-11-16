using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml.Linq;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private bool _gripView;
    private readonly Dictionary<string, (int Bone, Matrix4x4 Frame)> _heldAttachments = new(StringComparer.OrdinalIgnoreCase);

    private static string ItemGripOf(XElement item) =>
        GetAttributeValue(item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Weapon") ?? item, "item_usage") == "hand_shield"
            ? "hand_shield" : "shield";

    // The grip the preview uses: the tuned override, else the item's own item_usage.
    private string EffectiveGrip(string id) => _itemPositions.GetValueOrDefault(id)?.Grip ??
        (_equipmentItems.TryGetValue(id, out var item) ? ItemGripOf(item) : "shield");

    // item_usage and the off-hand bone flag are one decision: strapped hangs on the forearm, centre grip in the fist.
    private static void ApplyGripFlags(XElement flags, string grip)
    {
        flags.SetAttributeValue("ForceAttachOffHandSecondaryItemBone", grip == "shield" ? "true" : null);
        flags.SetAttributeValue("ForceAttachOffHandPrimaryItemBone", grip == "hand_shield" ? "true" : null);
    }

    private void ApplyGripToItem(XElement item, string grip)
    {
        var weapon = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Weapon");
        if (weapon == null) item.Add(new XElement("ItemComponent", weapon = new XElement("Weapon")));
        weapon.SetAttributeValue("item_usage", grip);
        var flags = item.Elements().FirstOrDefault(element => element.Name.LocalName == "Flags");
        if (flags == null) item.Add(flags = new XElement("Flags"));
        ApplyGripFlags(flags, grip);
    }

    private void RefreshGripControls(XElement item, string id, bool holstered)
    {
        var shield = IsShield(item) && !holstered;
        ItemPositionGripPanel.Visibility = shield ? Visibility.Visible : Visibility.Collapsed;
        if (!shield) return;
        var grip = EffectiveGrip(id);
        var wasUpdating = _updatingItemPositions;
        _updatingItemPositions = true;
        try { ItemPositionGripCombo.SelectedIndex = grip == "hand_shield" ? 1 : 0; }
        finally { _updatingItemPositions = wasUpdating; }
    }

    private void ItemPositionGrip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingItemPositions || ActivePositionItem is not { } selected ||
            ItemPositionGripCombo.SelectedItem is not ComboBoxItem { Tag: string grip }) return;
        var original = ItemGripOf(_equipmentItems[selected.Id]);
        var tuning = _itemPositions.GetValueOrDefault(selected.Id) ?? new();
        _itemPositions[selected.Id] = tuning with { Grip = grip == original ? null : grip };
        _equipmentPreviewModels.Clear();
        RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
        ItemPositionStatusText.Text = grip == "shield"
            ? "Strapped: item_usage=\"shield\" + ForceAttachOffHandSecondaryItemBone (forearm)."
            : "Centre grip: item_usage=\"hand_shield\" + ForceAttachOffHandPrimaryItemBone (fist).";
    }

    private void ItemPositionGripView_Click(object sender, RoutedEventArgs e)
    {
        _gripView = ItemPositionGripViewCheckBox.IsChecked == true;
        if (_previewWorkspace == 1 && _troopPreviewLoadout.Count > 0)
            RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
    }

    private void AddGripAxes(Model3DGroup assembled)
    {
        var slot = ActivePositionItem?.Slot ?? (_heldAttachments.ContainsKey("Item1") ? "Item1" : "Item0");
        if (!_heldAttachments.TryGetValue(slot, out var attachment)) return;
        var axes = new Model3DGroup();
        // Bannerlord item-frame axes: X side (red), Y forward (green), Z up (blue).
        axes.Children.Add(Axis(new Vector3(1, 0, 0), Colors.Red));
        axes.Children.Add(Axis(new Vector3(0, 1, 0), Colors.LimeGreen));
        axes.Children.Add(Axis(new Vector3(0, 0, 1), Colors.DodgerBlue));
        StudioRenderer.AttachToBone(axes, attachment.Bone, attachment.Frame);
        assembled.Children.Add(axes);

        static GeometryModel3D Axis(Vector3 direction, Color color)
        {
            const float length = 0.25f, half = 0.004f;
            var side = MathF.Abs(direction.Z) > 0.5f ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
            var a = Vector3.Normalize(Vector3.Cross(direction, side)) * half;
            var b = Vector3.Normalize(Vector3.Cross(direction, a)) * half;
            var mesh = new MeshGeometry3D();
            foreach (var t in new[] { 0f, length })
                foreach (var (u, v) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    var p = direction * t + a * u + b * v;
                    mesh.Positions.Add(ConvertBannerlordPoint(p.X, p.Y, p.Z));
                }
            int[] faces = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0];
            foreach (var index in faces) mesh.TriangleIndices.Add(index);
            var material = new EmissiveMaterial(new SolidColorBrush(color));
            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            group.Children.Add(material);
            return new GeometryModel3D(mesh, group) { BackMaterial = group };
        }
    }
}
