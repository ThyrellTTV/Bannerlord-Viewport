using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using LOTRAOM_Viewport;
using Hx = HelixToolkit.Wpf.SharpDX;

// Integration check: supply an AssetPackages folder and troop XML from a local game install.
internal static class ItemPositionChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Supply AssetPackages folder and troops_gondor.xml.");
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bannerlord Viewport", "settings.json");
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{\"SaveSettings\":false}");
            var app = new Application();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            window = new MainWindow { Width = 1440, Height = 1000, ShowActivated = false,
                Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
            object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, values);
            T Control<T>(string name) => (T)window.FindName(name);
            (XDocument Items, XDocument? Holsters, XDocument? Templates) Documents(string id, bool full = false) =>
                ((XDocument, XDocument?, XDocument?))Call("BuildItemPositionDocuments", id, full)!;
            var output = Path.GetFullPath("obj/item-position-checks");
            Directory.CreateDirectory(output);
            Call("LoadAssetPackages", args[0]);
            window.Show();
            Call("LoadTroopXml", args[1]);
            Wait(() => Field("_troopPoses") != null, "Native rig failed to load");
            var troop = Control<ListBox>("TroopList").Items.Cast<TroopNode>().Single(t => t.Id == "gondor_ca_pikeman");
            var loadout = new Dictionary<string, string>(troop.Equipment);
            loadout.Remove("Item0"); loadout["Item1"] = "Item.sm_gd_shield_a1";
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 1;
            Call("SetEquipmentBoxes", loadout);
            Call("RenderLoadoutPreview", "Shield positioning", loadout);
            Wait(() => Field("_poseClip") != null, "Holding clip failed to load"); Pump(300);
            var items = (Dictionary<string, XElement>)Field("_equipmentItems")!;
            var positions = (Dictionary<string, ItemPositionTuning>)Field("_itemPositions")!;
            var source = items["sm_gd_shield_a1"].ToString();
            Assert(Control<Border>("ItemPositionEditorPanel").Visibility == Visibility.Visible, "Editor hidden");
            Assert(!Control<Expander>("ItemPositionInspectorSection").IsExpanded, "Equipment section opened automatically");
            Control<Expander>("ItemPositionInspectorSection").IsExpanded = true;
            Assert((bool)Call("CommitItemPositionControls")!, "Unchanged controls invalid");
            Assert(positions.Count == 0, "Formatting created an unintended tuning");
            var viewport = Control<Hx.Viewport3DX>("ModelViewport");
            Vector3[] RigidVertices() => viewport.Items.OfType<Hx.BoneSkinMeshGeometryModel3D>()
                .Where(m => m.Geometry is HelixToolkit.SharpDX.BoneSkinnedMeshGeometry3D { VertexBoneIds.Count: > 0 } geometry &&
                    geometry.VertexBoneIds.All(b => b.Weights.X == 1 && b.Bone1 == geometry.VertexBoneIds[0].Bone1))
                .SelectMany(m => ((HelixToolkit.SharpDX.Model.Scene.BoneSkinMeshNode)m.SceneNode).TryGetSkinnedVertices(viewport.EffectsManager!)!).ToArray();
            var before = RigidVertices();
            Assert(before.Length > 0, "No rigid shield mesh");
            var alternativeShield = items.First(pair => pair.Key != "sm_gd_shield_a1" && pair.Value.Name.LocalName == "Item" &&
                pair.Value.Attributes().Any(attribute => attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase) && attribute.Value == "Shield") && pair.Value.Attribute("mesh") is { } mesh &&
                mesh.Value != items["sm_gd_shield_a1"].Attribute("mesh")?.Value && Call("FindEquipmentMesh", mesh.Value) != null);
            var shieldCombo = Control<ComboBox>("Item1Combo");
            Call("FilterEquipmentCombo", shieldCombo, "Item." + alternativeShield.Key);
            shieldCombo.SelectedItem = shieldCombo.Items.Cast<MeshAssetNode>().Single(option => option.DisplayName == "Item." + alternativeShield.Key);
            Pump(300);
            var liveVertices = RigidVertices();
            Assert(((IReadOnlyDictionary<string, string>)Field("_troopPreviewLoadout")!)["Item1"] == "Item." + alternativeShield.Key && liveVertices.Length > 0 &&
                (liveVertices.Length != before.Length || before.Zip(liveVertices).Max(pair => Vector3.Distance(pair.First, pair.Second)) > .0001f),
                "Dropdown selection did not change rendered shield geometry without Apply");
            Call("PosePlay_Click", window, new RoutedEventArgs());
            Call("FilterEquipmentCombo", shieldCombo, "Item.sm_gd_shield_a1");
            shieldCombo.SelectedItem = shieldCombo.Items.Cast<MeshAssetNode>().Single(option => option.DisplayName == "Item.sm_gd_shield_a1");
            Pump(300);
            Assert(((DispatcherTimer)Field("_posePlaybackTimer")!).IsEnabled, "Live loadout change stopped animation playback");
            Call("StopPosePlayback");
            before = RigidVertices();
            var held = new ItemAttachmentTransform { X = .08f, Y = -.03f, Z = .02f, RX = 10, RY = -15, RZ = 5 };
            Call("SetItemPosition", "sm_gd_shield_a1", new ItemPositionTuning { Held = held }, true); Pump(300);
            var after = RigidVertices();
            Assert(before.Length == after.Length && before.Zip(after).Max(pair => Vector3.Distance(pair.First, pair.Second)) > .03f, "Held frame did not move mesh");
            Assert(items["sm_gd_shield_a1"].ToString() == source, "Source item mutated");
            var fragment = Documents("sm_gd_shield_a1").Items;
            Assert(fragment.Descendants("Weapon").Single().Attribute("position")!.Value.StartsWith("0.079", StringComparison.Ordinal) ||
                fragment.Descendants("Weapon").Single().Attribute("position")!.Value.StartsWith("0.08", StringComparison.Ordinal), "Wrong metre conversion");
            Assert(fragment.Descendants("Weapon").Single().Attribute("rotation")!.Value == "10,-15,5", "Wrong rotation XML");
            Assert(Documents("sm_gd_shield_a1", true).Items.Descendants("Item").Single().Attribute("mesh")!.Value == items["sm_gd_shield_a1"].Attribute("mesh")!.Value, "Full export lost item data");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionUndoButton"), new RoutedEventArgs());
            Assert(!positions.ContainsKey("sm_gd_shield_a1"), "Undo failed");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionRedoButton"), new RoutedEventArgs());
            Assert(positions["sm_gd_shield_a1"].Held == held, "Redo failed");
            var boxes = (List<TextBox>)Field("_itemPositionBoxes")!;
            boxes[0].Text = "9";
            Assert((bool)Call("CommitItemPositionControls")! && MathF.Abs(positions["sm_gd_shield_a1"].Held!.X - .09f) < .00001f, "Control centimetre conversion failed");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionUndoButton"), new RoutedEventArgs());
            boxes[3].Text = "NaN";
            Assert(!(bool)Call("CommitItemPositionControls")!, "Invalid input accepted");
            Call("PopulateItemPositionControls");
            var slider = Control<Slider>("PoseFrameSlider");
            slider.Value = (slider.Minimum + slider.Maximum) / 2;
            var frame = (float)Field("_poseFrame")!;
            Call("SetItemPosition", "sm_gd_shield_a1", new ItemPositionTuning { Held = held }, true);
            Assert(MathF.Abs((float)Field("_poseFrame")! - frame) < .0001f, "Tuning reset animation frame");
            Call("PosePlay_Click", window, new RoutedEventArgs());
            Call("SetItemPosition", "sm_gd_shield_a1", new ItemPositionTuning { Held = held }, true);
            Assert(((DispatcherTimer)Field("_posePlaybackTimer")!).IsEnabled, "Tuning stopped playback");
            Call("StopPosePlayback");
            Control<ComboBox>("ItemPositionModeCombo").SelectedIndex = 1; Pump(300);
            Assert(Control<ComboBox>("Item1StateCombo").SelectedIndex == 1 && !(bool)Field("_troopHeldShield")!, "Editor mode did not change preview state");
            var holstered = new ItemAttachmentTransform { X = .04f, Y = -.02f, Z = .05f, RX = 20, RY = 10, RZ = -30 };
            Call("SetItemPosition", "sm_gd_shield_a1", new ItemPositionTuning { Held = held, Holstered = holstered }, true);
            var documents = Documents("sm_gd_shield_a1");
            Assert(documents.Holsters != null && documents.Templates == null, "Shield companion XML missing");
            var library = Field("_troopPoses")!;
            var holsters = (Dictionary<string, XElement>)library.GetType().GetField("_holsters", Private)!.GetValue(library)!;
            var names = items["sm_gd_shield_a1"].Attribute("item_holsters")!.Value.Split(':');
            var oldHolsters = names.ToDictionary(name => name, name => holsters[name].ToString());
            var custom = documents.Holsters!.Descendants("item_holster").ToArray();
            foreach (var definition in custom) holsters.Add(definition.Attribute("id")!.Value, definition);
            var attachment = library.GetType().GetMethod("GetHolsterAttachment", Private)!;
            var originalFrame = ((int, Matrix4x4, string))attachment.Invoke(library, [names, new Vector3(.04f, -.02f, .05f), new HashSet<string>(), new Vector3(20, 10, -30)])!;
            var exportedFrame = ((int, Matrix4x4, string))attachment.Invoke(library, [custom.Select(h => h.Attribute("id")!.Value).ToArray(), new Vector3(.04f, -.02f, .05f), new HashSet<string>(), Vector3.Zero])!;
            Assert(originalFrame.Item1 == exportedFrame.Item1 && MatrixDifference(originalFrame.Item2, exportedFrame.Item2) < .00001f, "Holster XML differs from preview");
            Assert(oldHolsters.All(pair => holsters[pair.Key].ToString() == pair.Value), "Shared holster changed");
            var file = Path.Combine(output, "shield.xml");
            foreach (var previous in new[] { file, Path.Combine(output, "shield_holsters.xml") }) if (File.Exists(previous)) File.Delete(previous);
            Call("WriteItemPositionDocuments", file, documents);
            Assert(XNode.DeepEquals(XDocument.Load(file).Root, documents.Items.Root), "Saved XML differs");
            Reject(() => Call("WriteItemPositionDocuments", file, documents), "Existing companion overwritten");
            Reject(() => Call("WriteItemPositionDocuments", args[1], documents), "Source overwrite allowed");
            var protectedPath = Path.Combine(output, "source.xml"); File.WriteAllText(protectedPath, "protected");
            ((HashSet<string>)Field("_equipmentXmlSourcePaths")!).Add(protectedPath);
            Reject(() => Call("WriteItemPositionDocuments", protectedPath, documents), "Indexed source overwrite allowed");
            Assert(File.ReadAllText(protectedPath) == "protected", "Source file changed");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionResetButton"), new RoutedEventArgs());
            Assert(positions["sm_gd_shield_a1"].Held == held && positions["sm_gd_shield_a1"].Holstered == null, "Reset crossed attachment modes");
            var templates = (Dictionary<string, XElement>)Field("_equipmentTemplates")!;
            var pieces = (Dictionary<string, CraftingPieceNode>)Field("_equipmentCraftingPieces")!;
            var crafted = items.First(pair => pair.Value.Name.LocalName == "CraftedItem" &&
                pair.Value.Attribute("crafting_template")?.Value == "TwoHandedSword" && pair.Value.Descendants("Piece").All(piece =>
                    pieces.TryGetValue(piece.Attribute("id")!.Value, out var part) && Call("FindMeshOption", part.MeshName) != null));
            loadout["Item0"] = "Item." + crafted.Key; loadout.Remove("Item1");
            Control<ComboBox>("Item0StateCombo").SelectedIndex = 0;
            Call("SetEquipmentBoxes", loadout); Call("RenderLoadoutPreview", "Crafted positioning", loadout);
            Control<ComboBox>("ItemPositionModeCombo").SelectedIndex = 0;
            Assert(!Control<StackPanel>("ItemPositionFields").IsEnabled, "Unsupported crafted held frame enabled");
            var templateBefore = templates["TwoHandedSword"].ToString();
            var craftedBefore = crafted.Value.ToString();
            Control<ComboBox>("ItemPositionModeCombo").SelectedIndex = 1;
            Call("SetItemPosition", crafted.Key, new ItemPositionTuning { Holstered = holstered }, true);
            var craftedDocs = Documents(crafted.Key, true);
            Assert(craftedDocs.Templates != null && craftedDocs.Holsters != null, "Crafted companions missing");
            var craftedXml = craftedDocs.Items.Descendants("CraftedItem").Single();
            Assert(craftedXml.Attribute("holster_position_shift") == null && !craftedXml.Descendants("Weapon").Any(), "Ignored crafted positioning exported");
            Assert(craftedXml.Attribute("crafting_template")!.Value == craftedDocs.Templates!.Descendants("CraftingTemplate").Single().Attribute("id")!.Value, "Crafted template reference wrong");
            Assert(crafted.Value.ToString() == craftedBefore && templates["TwoHandedSword"].ToString() == templateBefore, "Crafted source mutated");
            Assert(positions["sm_gd_shield_a1"].Held == held, "Tuning was not isolated per item");
            Control<ComboBox>("ItemPositionModeCombo").SelectedIndex = 0;
            Wait(() => Field("_poseClip") != null, "Crafted holding clip did not load"); Pump(250);
            Assert(Control<Expander>("TroopCraftingOffsetsPanel").Visibility == Visibility.Visible &&
                Control<StackPanel>("ItemPositionFields").Visibility == Visibility.Collapsed, "Inline crafted editor not available");
            var inlinePieceId = (string)Control<ComboBox>("TroopCraftingPieceCombo").SelectedItem.GetType().GetProperty("Id")!.GetValue(Control<ComboBox>("TroopCraftingPieceCombo").SelectedItem)!;
            var originalPiece = pieces[inlinePieceId];
            var originalOffset = originalPiece.PieceOffset;
            var pieceDefinitions = (Dictionary<string, XElement>)Field("_equipmentPieceDefinitions")!;
            var originalDefinition = pieceDefinitions[inlinePieceId].ToString();
            var beforeInline = RigidVertices();
            var inlineFrame = (float)Field("_poseFrame")!;
            Call("TroopCraftingOffsetStep_Click", new Button { Tag = "0,1" }, new RoutedEventArgs()); Pump(250);
            var afterInline = RigidVertices();
            Assert(Control<TabControl>("WorkspaceTabs").SelectedIndex == 1 && (int)Field("_previewWorkspace")! == 1,
                "Inline edit left the troops preview");
            Assert(positions[crafted.Key].Pieces![inlinePieceId].PieceOffset == originalOffset + 1, "Wrong inline centimetre step");
            Assert(beforeInline.Length == afterInline.Length && beforeInline.Zip(afterInline).Max(pair => Vector3.Distance(pair.First, pair.Second)) > .005f,
                "Inline offset did not move the equipped mesh");
            Assert(MathF.Abs((float)Field("_poseFrame")! - inlineFrame) < .0001f && originalPiece.PieceOffset == originalOffset &&
                pieceDefinitions[inlinePieceId].ToString() == originalDefinition, "Inline edit mutated source or animation frame");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionUndoButton"), new RoutedEventArgs()); Pump(250);
            Assert(positions[crafted.Key].Pieces == null && positions[crafted.Key].Holstered == holstered, "Undo did not isolate inline offsets");
            Assert(beforeInline.Zip(RigidVertices()).Max(pair => Vector3.Distance(pair.First, pair.Second)) < .00001f, "Undo did not restore cached weapon geometry");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionRedoButton"), new RoutedEventArgs());
            Call("TroopCraftingReset_Click", window, new RoutedEventArgs());
            Assert(positions[crafted.Key].Pieces == null && positions[crafted.Key].Holstered == holstered, "Piece reset changed holster tuning");
            Control<TextBox>("TroopCraftingOffsetBox").Text = (originalOffset + 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Control<TextBox>("TroopCraftingPreviousBox").Text = "2";
            Control<TextBox>("TroopCraftingNextBox").Text = "-3";
            Assert((bool)Call("CommitTroopCraftingOffsets")!, "Typed inline offsets rejected");
            var inlineOffsets = positions[crafted.Key].Pieces![inlinePieceId];
            Control<TextBox>("TroopCraftingNextBox").Text = "NaN";
            Assert(!(bool)Call("CommitTroopCraftingOffsets")! && positions[crafted.Key].Pieces![inlinePieceId] == inlineOffsets, "Nonfinite inline offset accepted");
            Call("PopulateTroopCraftingOffsets");
            Control<ComboBox>("TroopCraftingPieceCombo").SelectedIndex = 1;
            Call("TroopCraftingOffsetStep_Click", new Button { Tag = "0,1" }, new RoutedEventArgs());
            Assert(positions[crafted.Key].Pieces!.Count == 2, "Offsets did not retain the other edited piece");
            Call("TroopCraftingReset_Click", window, new RoutedEventArgs());
            Assert(positions[crafted.Key].Pieces!.Count == 1 && positions[crafted.Key].Pieces![inlinePieceId] == inlineOffsets, "Selected-piece reset affected another piece");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionResetButton"), new RoutedEventArgs());
            Assert(positions[crafted.Key].Pieces == null && positions[crafted.Key].Holstered == holstered, "All-piece reset affected holster positioning");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionUndoButton"), new RoutedEventArgs());
            Control<ComboBox>("TroopCraftingPieceCombo").SelectedIndex = 0;
            Call("PosePlay_Click", window, new RoutedEventArgs());
            Call("TroopCraftingOffsetStep_Click", new Button { Tag = "2,1" }, new RoutedEventArgs());
            Assert(((DispatcherTimer)Field("_posePlaybackTimer")!).IsEnabled, "Inline edit stopped animation playback");
            Call("StopPosePlayback");
            Call("ItemPositionHistory_Click", Control<Button>("ItemPositionUndoButton"), new RoutedEventArgs());
            var pieceXml = (XDocument)Call("BuildItemCraftingPieceDocument", crafted.Key)!;
            var customPiece = pieceXml.Descendants("CraftingPiece").Single();
            var customPieceId = customPiece.Attribute("id")!.Value;
            Assert(customPieceId != inlinePieceId && float.Parse(customPiece.Element("BuildData")!.Attribute("piece_offset")!.Value,
                System.Globalization.CultureInfo.InvariantCulture) == originalOffset + 5, "Custom piece export mismatch");
            var inlineDocuments = Documents(crafted.Key, true);
            Assert(inlineDocuments.Items.Descendants("Piece").Any(reference => reference.Attribute("id")!.Value == customPieceId) &&
                !inlineDocuments.Items.Descendants("Piece").Any(reference => reference.Attribute("id")!.Value == inlinePieceId), "Item export did not reference custom piece");
            pieceDefinitions[customPieceId] = customPiece;
            pieces[customPieceId] = (CraftingPieceNode)typeof(MainWindow).GetMethod("ParseCraftingPiece", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [customPiece])!;
            var importedItem = new XElement(inlineDocuments.Items.Descendants("CraftedItem").Single());
            templates[importedItem.Attribute("crafting_template")!.Value] = inlineDocuments.Templates!.Descendants("CraftingTemplate").Single();
            var tunedGeometry = (System.Windows.Media.Media3D.Model3DGroup)Call("LoadCraftedEquipmentModel", crafted.Value, false)!;
            var exportedGeometry = (System.Windows.Media.Media3D.Model3DGroup)Call("LoadCraftedEquipmentModel", importedItem, false)!;
            Assert(tunedGeometry.Bounds == exportedGeometry.Bounds, "Exported crafting XML did not reproduce geometry");
            var inlineFile = Path.Combine(output, "inline.xml");
            foreach (var previous in new[] { "inline.xml", "inline_holsters.xml", "inline_templates.xml", "inline_pieces.xml" })
                if (File.Exists(Path.Combine(output, previous))) File.Delete(Path.Combine(output, previous));
            Call("WriteItemPositionDocuments", inlineFile, inlineDocuments);
            Assert(XNode.DeepEquals(XDocument.Load(Path.Combine(output, "inline_pieces.xml")).Root, pieceXml.Root), "Saved piece companion mismatch");
            var otherItem = new XElement(crafted.Value); otherItem.SetAttributeValue("id", "test_shared_piece_other_item");
            var untunedGeometry = (System.Windows.Media.Media3D.Model3DGroup)Call("LoadCraftedEquipmentModel", otherItem, false)!;
            Assert(untunedGeometry.Bounds != tunedGeometry.Bounds && originalPiece.PieceOffset == originalOffset, "Offsets leaked to another item");
            var saved = new AppSettings { SaveSettings = true, ItemPositions = new(positions) };
            positions.Clear(); Call("RestoreRenderSettings", JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!);
            Assert(positions["sm_gd_shield_a1"].Held == held && positions[crafted.Key].Holstered == holstered, "Settings roundtrip lost tuning");
            Assert(positions[crafted.Key].Pieces![inlinePieceId] == inlineOffsets, "Inline settings roundtrip lost offsets");
            Control<CheckBox>("SaveSettingsCheckBox").IsChecked = true; Call("SaveSettings");
            Assert(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings))!.ItemPositions!.Count == 2, "Enabled persistence failed");
            Control<CheckBox>("SaveSettingsCheckBox").IsChecked = false; Call("SaveSettings");
            Assert(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings))!.ItemPositions == null, "Disabled persistence retained tuning");
            typeof(MainWindow).GetField("_yaw", Private)!.SetValue(window, 180d);
            typeof(MainWindow).GetField("_pitch", Private)!.SetValue(window, 10d);
            typeof(MainWindow).GetField("_distance", Private)!.SetValue(window, 8.2d);
            Call("UpdateCamera");
            Pump(300); Capture(window, Path.Combine(output, "editor.png"));
            window.Width = 1180; window.Height = 720; Pump(300); Capture(window, Path.Combine(output, "editor-small.png"));
            Assert(Control<Border>("ItemPositionEditorPanel").ActualWidth <= 380, "Editor width overflow");
            var craftingPieces = (System.Collections.ObjectModel.ObservableCollection<CraftingPieceNode>)Field("_craftingPieces")!;
            var unrelated = new CraftingPieceNode { Id = "test_unrelated_tuning", PieceType = "Blade", PieceOffset = 3 };
            craftingPieces.Add(unrelated);
            window.Width = 1440; window.Height = 1000;
            Call("ItemPositionCrafting_Click", window, new RoutedEventArgs()); Pump(250);
            Assert(Control<TabControl>("WorkspaceTabs").SelectedIndex == 2, "Crafting button did not navigate");
            Assert((string)Control<ComboBox>("CraftingTemplateCombo").SelectedItem == "TwoHandedSword", "Crafting template not filled");
            foreach (var reference in crafted.Value.Descendants("Piece"))
            {
                var slot = reference.Attribute("Type")!.Value;
                var selectedPiece = (CraftingPieceNode)Control<ComboBox>("Crafting" + slot + "Combo").SelectedItem;
                Assert(selectedPiece.Id == reference.Attribute("id")!.Value && selectedPiece.Scale ==
                    ((int?)reference.Attribute("scale_factor") ?? 100), "Weapon piece/scale not filled");
            }
            Assert(craftingPieces.Contains(unrelated) && unrelated.IsDirty && unrelated.PieceOffset == 3, "Auto-fill discarded unrelated tuning");
            var model = (System.Windows.Media.Media3D.Model3DGroup)Field("_assetModel")!;
            Assert(model.Children.Count == 1 && ((System.Windows.Media.Media3D.Model3DGroup)model.Children[0]).Children.Count == crafted.Value.Descendants("Piece").Count(),
                "Auto-filled weapon preview is missing pieces");
            Assert(Control<Expander>("CraftingInspectorSection").IsExpanded, "Offset section not opened for explicit editing");
            var activePiece = (CraftingPieceNode)Field("_activeCraftingPiece")!;
            var offset = activePiece.PieceOffset;
            Call("CraftingOffsetStep_Click", new Button { Tag = "piece_offset,1" }, new RoutedEventArgs()); Pump(200);
            Assert(activePiece.PieceOffset > offset && model.Children.Count > 0, "Offset edit blanked the auto-filled preview");
            Capture(window, Path.Combine(output, "auto-filled-crafting.png"));
            var partial = new XElement(crafted.Value);
            foreach (var reference in partial.Descendants("Piece").ToArray())
                if (reference.Attribute("Type")!.Value is "Guard" or "Pommel") reference.Remove();
                else reference.SetAttributeValue("scale_factor", reference.Attribute("Type")!.Value == "Blade" ? 85 : 105);
            ((Dictionary<string, string>)Field("_craftingSearchText")!)["Blade"] = "stale search";
            Call("LoadCraftingItem", partial);
            Assert(Control<ComboBox>("CraftingGuardCombo").SelectedItem == CraftingPieceNode.Empty &&
                Control<ComboBox>("CraftingPommelCombo").SelectedItem == CraftingPieceNode.Empty && Field("_craftingGuard") == null && Field("_craftingPommel") == null,
                "Previous weapon pieces were left selected");
            Assert(((CraftingPieceNode)Control<ComboBox>("CraftingBladeCombo").SelectedItem).Scale == 85 &&
                ((CraftingPieceNode)Control<ComboBox>("CraftingHandleCombo").SelectedItem).Scale == 105, "Nondefault item scales not imported");
            Assert(activePiece.PieceOffset > offset && craftingPieces.Contains(unrelated), "Replacing build lost tuning");
            var invalid = new XElement(partial); invalid.Descendants("Piece").First().SetAttributeValue("id", "missing_test_piece");
            Reject(() => Call("LoadCraftingItem", invalid), "Missing piece was accepted");
            Assert(((CraftingPieceNode)Control<ComboBox>("CraftingBladeCombo").SelectedItem).Scale == 85, "Failed import changed current selection");
            Assert(crafted.Value.ToString() == craftedBefore && templates["TwoHandedSword"].ToString() == templateBefore, "Auto-fill changed source XML");
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 0;
            Assert(Control<Border>("ItemPositionEditorPanel").Visibility == Visibility.Collapsed, "Editor visible on assets");
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 2;
            Assert(Control<Border>("ItemPositionEditorPanel").Visibility == Visibility.Collapsed, "Editor visible on crafting");
            // Source saving is exercised only on copies, never on the installed game XML.
            var sourceCopies = Path.Combine(output, "source-save-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(sourceCopies);
            var copiedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string CopySource(string path)
            {
                path = Path.GetFullPath(path);
                if (!copiedPaths.TryGetValue(path, out var copy))
                {
                    copy = Path.Combine(sourceCopies, Guid.NewGuid().ToString("N") + ".xml");
                    File.Copy(path, copy); copiedPaths[path] = copy;
                }
                return copy;
            }
            var holsterSourceField = library.GetType().GetField("<HolsterSourcePath>k__BackingField", Private)!;
            var actualHolsterSource = (string)holsterSourceField.GetValue(library)!;
            try
            {
                foreach (var field in new[] { "_equipmentItemSourcePaths", "_equipmentPieceSourcePaths", "_equipmentTemplateSourcePaths" })
                {
                    var paths = (Dictionary<string, string>)Field(field)!;
                    foreach (var id in paths.Keys.ToArray()) paths[id] = CopySource(paths[id]);
                }
                foreach (var piece in craftingPieces.Where(piece => piece.SourcePath.Length > 0)) piece.SourcePath = CopySource(piece.SourcePath);
                holsterSourceField.SetValue(library, CopySource(actualHolsterSource));
                positions.Clear();
                positions["sm_gd_shield_a1"] = new() { Held = held, Holstered = holstered };
                positions[crafted.Key] = new() { Holstered = holstered, Pieces = new() { [originalPiece.Id] = new() { PieceOffset = originalOffset + 5, PreviousPieceOffset = 2, NextPieceOffset = -3 } } };
                var saveLoadout = new Dictionary<string, string>(loadout) { ["Item0"] = "Item." + crafted.Key, ["Item1"] = "Item.sm_gd_shield_a1" };
                Call("SetEquipmentBoxes", saveLoadout);
                Control<TabControl>("WorkspaceTabs").SelectedIndex = 1;
                Control<ComboBox>("Item0StateCombo").SelectedIndex = 1; Control<ComboBox>("Item1StateCombo").SelectedIndex = 1;
                Call("RenderLoadoutPreview", "Source-save comparison", saveLoadout); Pump(300);
                var beforeSourceSave = RigidVertices();
                var batch = Activator.CreateInstance(typeof(MainWindow).GetNestedType("SourceXmlEdits", BindingFlags.NonPublic)!, nonPublic: true)!;
                Call("AddItemSourceEdits", batch, "sm_gd_shield_a1"); Call("AddItemSourceEdits", batch, crafted.Key);
                Call("SaveSourceEdits", batch); Pump(300);
                var afterSourceSave = RigidVertices();
                Assert(beforeSourceSave.Length > 0 && beforeSourceSave.Length == afterSourceSave.Length && beforeSourceSave.Zip(afterSourceSave).Max(pair => Vector3.Distance(pair.First, pair.Second)) < .00001f,
                    "Saving/rebasing piece and holster edits changed the displayed GPU geometry");
                Assert(positions.Count == 0 && copiedPaths.Values.All(path => File.Exists(path)), "Source-save rebase/copies failed");
                Assert(Directory.EnumerateFiles(sourceCopies, "*.bak").Any(), "Source-save backups missing");
                Capture(window, Path.Combine(output, "source-saved.png"));
            }
            finally
            {
                holsterSourceField.SetValue(library, actualHolsterSource);
                Directory.Delete(sourceCopies, recursive: true);
            }
            if (viewport.RenderException != null) throw viewport.RenderException;
            Console.WriteLine("Held GPU movement, inline troop offsets/playback/undo/reset/isolation, item/piece/holster XML roundtrip, source-save GPU rebase on XML copies, safe saves, settings, tab visibility and crafting auto-fill: passed.");
        }
        finally
        {
            window?.Close();
            if (backup != null) File.WriteAllBytes(settings, backup); else if (File.Exists(settings)) File.Delete(settings);
        }
    }
    private static float MatrixDifference(Matrix4x4 a, Matrix4x4 b) => new[]
    {
        a.M11-b.M11,a.M12-b.M12,a.M13-b.M13,a.M14-b.M14,a.M21-b.M21,a.M22-b.M22,a.M23-b.M23,a.M24-b.M24,
        a.M31-b.M31,a.M32-b.M32,a.M33-b.M33,a.M34-b.M34,a.M41-b.M41,a.M42-b.M42,a.M43-b.M43,a.M44-b.M44
    }.Max(MathF.Abs);
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException or InvalidOperationException) { return; }
        throw new Exception(message);
    }
    private static void Wait(Func<bool> condition, string message)
    {
        for (var i = 0; i < 600; i++) { if (condition()) return; Pump(100); }
        throw new Exception(message);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
