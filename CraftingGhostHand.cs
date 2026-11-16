using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private void CraftingGhostHand_Click(object sender, RoutedEventArgs e) => RenderCraftingWeaponPreview();

    // Troops hold a crafted weapon on r_finger0 with an identity item frame (CreateWeaponFrame(null)), its
    // pieces scaled by 0.01 / CraftingPieceUnitScale. Inverting that chain puts the posed hand into the
    // crafting preview's own units, so it sits exactly where the game grips the weapon.
    private void AddCraftingGhostHand(Model3DGroup weapon)
    {
        CraftingGhostHandStatusText.Text = "";
        if (CraftingGhostHandCheckBox.IsChecked != true) return;
        if (_troopPoses == null) { CraftingGhostHandStatusText.Text = "Hand needs the Native animation assets (load a troop or asset folder first)."; return; }
        if (FindBaseBodyMesh("hands_male_a") is not { } handAsset) { CraftingGhostHandStatusText.Text = "hands_male_a was not found in Native's core_game.tpac."; return; }

        var template = CraftingTemplateCombo.SelectedItem as string ?? "";
        var category = template switch
        {
            "TwoHandedSword" => "TwoHandedSword",
            "TwoHandedAxe" or "TwoHandedMace" => "TwoHandedAxe",
            "TwoHandedPolearm" or "Pike" => "TwoHandedPolearm",
            "Javelin" => "Javelin",
            _ => "OneHandedSword",
        };
        var pose = _troopPoses.GetHoldingPose(category, false);
        if (pose == null) { CraftingGhostHandStatusText.Text = "No holding pose for this template."; return; }
        try
        {
            var clip = _troopPoses.Load(pose);
            var matrices = _troopPoses.Evaluate(clip, clip.FirstFrame);
            var (bone, frame) = _troopPoses.GetItemAttachment("r_finger0", Matrix4x4.Identity);
            var scale = Matrix4x4.CreateScale((float)(0.01 / CraftingPieceUnitScale));
            if (!Matrix4x4.Invert(scale * frame * matrices[bone], out var toCrafting)) return;

            var source = LoadMeshModel(handAsset, out _, MeshRenderMode.Raw);
            var brush = new SolidColorBrush(Color.FromArgb(150, 226, 186, 150));
            var material = new DiffuseMaterial(brush);
            foreach (var part in Flatten(source))
            {
                if (part.Geometry is not MeshGeometry3D mesh || StudioRenderer.GetExportSkinning(mesh) is not { } skin || skin.Length != mesh.Positions.Count) continue;
                var hand = new MeshGeometry3D();
                foreach (var (point, i) in mesh.Positions.Select((point, i) => (point, i)))
                {
                    var p = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
                    var w = skin[i];
                    var posed = Vector3.Transform(p, matrices[w.Bone1]) * w.Weights.X + Vector3.Transform(p, matrices[w.Bone2]) * w.Weights.Y +
                        Vector3.Transform(p, matrices[w.Bone3]) * w.Weights.Z + Vector3.Transform(p, matrices[w.Bone4]) * w.Weights.W;
                    var c = Vector3.Transform(posed, toCrafting);
                    hand.Positions.Add(new Point3D(c.X, c.Y, c.Z));
                }
                foreach (var index in mesh.TriangleIndices) hand.TriangleIndices.Add(index);
                hand.Normals = SmoothNormals(hand);
                weapon.Children.Add(new GeometryModel3D(hand, material) { BackMaterial = material });
            }
            CraftingGhostHandStatusText.Text = $"Hand: {pose.DisplayName}, r_finger0.";
        }
        catch (Exception ex) { CraftingGhostHandStatusText.Text = "Hand could not be posed: " + ex.Message; }

        static IEnumerable<GeometryModel3D> Flatten(Model3D model) => model switch
        {
            GeometryModel3D geometry => [geometry],
            Model3DGroup group => group.Children.SelectMany(Flatten),
            _ => [],
        };
    }

    private static Vector3DCollection SmoothNormals(MeshGeometry3D mesh)
    {
        var normals = new Vector3D[mesh.Positions.Count];
        for (var i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
        {
            int a = mesh.TriangleIndices[i], b = mesh.TriangleIndices[i + 1], c = mesh.TriangleIndices[i + 2];
            var n = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        var result = new Vector3DCollection(normals.Length);
        foreach (var n in normals) { var v = n; if (v.LengthSquared > 0) v.Normalize(); result.Add(v); }
        return result;
    }
}
