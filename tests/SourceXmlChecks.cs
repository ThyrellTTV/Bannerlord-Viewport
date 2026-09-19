using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using LOTRAOM_Viewport;

internal static class SourceXmlChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bannerlord Viewport", "settings.json");
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var root = Path.GetFullPath(Path.Combine("obj/source-xml-checks", Guid.NewGuid().ToString("N")));
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.WriteAllText(settings, "{\"SaveSettings\":false}");
            string Fixture(string file, string xml)
            {
                var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, xml.Replace("\n", "\r\n"), new UTF8Encoding(true)); return path;
            }
            Fixture("SubModule.xml", """
                <Module><Xmls><XmlName id="Items" path="items" /><XmlName id="CraftingPieces" path="pieces" /><XmlName id="CraftingTemplates" path="templates" /></Xmls></Module>
                """);
            var troopPath = Fixture("ModuleData/troops/a.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <NPCCharacters>
                  <!-- Keep troop comments -->
                  <NPCCharacter id="test_guard" name="Test Guard" level="25">
                    <skills><skill id="Athletics" value="70" /></skills>
                    <Equipments>
                      <EquipmentRoster civilian="true"><equipment slot="Body" id="Item.civilian" /></EquipmentRoster>
                      <EquipmentRoster custom="preserve">
                        <equipment slot="Head" id="Item.old_helmet" />
                        <equipment slot="Body" id="Item.armour_a" />
                        <equipment slot="Item0" id="Item.sword" />
                        <equipment slot="Item1" id="Item.shield" />
                        <equipment slot="Item3" id="Item.ammo" />
                        <equipment slot="Horse" id="Item.horse" />
                      </EquipmentRoster>
                      <EquipmentRoster><equipment slot="Body" id="Item.variant" /></EquipmentRoster>
                    </Equipments>
                  </NPCCharacter>
                  <NPCCharacter id="other_guard" name="Other"><Equipments><EquipmentRoster><equipment slot="Body" id="Item.other" /></EquipmentRoster></Equipments></NPCCharacter>
                </NPCCharacters>
                """);
            var itemPath = Fixture("ModuleData/items.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Items>
                  <!-- Keep item comments -->
                  <Item id="armour_a" mesh="armour_a" />
                  <Item id="armour_b" mesh="armour_b" />
                  <Item id="shield" type="Shield" weight="2" item_holsters="shield_back" holster_position_shift="0,0,0">
                    <ItemComponent><Weapon weapon_class="LargeShield" position="0,0,0" rotation="0,0,0" missile_speed="15" /></ItemComponent>
                  </Item>
                  <CraftedItem id="sword" name="Sword" crafting_template="TwoHandedSword" value="100">
                    <Pieces><Piece id="blade" Type="Blade" scale_factor="100" /><Piece id="handle" Type="Handle" scale_factor="100" /></Pieces>
                  </CraftedItem>
                  <CraftedItem id="other_sword" crafting_template="TwoHandedSword"><Pieces><Piece id="blade" Type="Blade" scale_factor="100" /></Pieces></CraftedItem>
                </Items>
                """);
            var piecePath = Fixture("ModuleData/pieces.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <CraftingPieces>
                  <!-- Keep piece comments -->
                  <CraftingPiece id="blade" piece_type="Blade" mesh="blade" length="80"><BuildData piece_offset="1" previous_piece_offset="0" next_piece_offset="0" custom="keep" /><BladeData thrust_damage_factor="5" /></CraftingPiece>
                  <CraftingPiece id="handle" piece_type="Handle" mesh="handle" length="20" />
                </CraftingPieces>
                """);
            var templatePath = Fixture("ModuleData/templates.xml", """
                <CraftingTemplates>
                  <CraftingTemplate id="TwoHandedSword" item_holsters="sword_back" default_item_holster_position_offset="0,0,0">
                    <PieceDatas><PieceData piece_type="Handle" build_order="0" /><PieceData piece_type="Blade" build_order="1" /></PieceDatas>
                  </CraftingTemplate>
                </CraftingTemplates>
                """);
            _ = new Application(); window = new MainWindow();
            object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
            object Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
            T Control<T>(string name) => (T)window.FindName(name);
            var positions = (Dictionary<string, ItemPositionTuning>)Field("_itemPositions");
            Call("LoadTroopXml", troopPath);
            var troop = Control<ListBox>("TroopList").Items.Cast<TroopNode>().Single(t => t.Id == "test_guard");
            Assert(troop.Equipment["Body"] == "Item.armour_a", "Preview did not use first battle roster");
            Control<ListBox>("TroopList").SelectedItem = troop;
            Call("SetEquipmentBoxes", new Dictionary<string, string> { ["Body"] = "Item.armour_b", ["Item0"] = "Item.sword", ["Item1"] = "Item.shield" });
            positions["shield"] = new() { Held = new() { X = .05f, RY = 25 }, Holstered = new() { Y = .12f } };
            positions["sword"] = new() { Holstered = new() { Z = .07f }, Pieces = new() { ["blade"] = new() { PieceOffset = 5, PreviousPieceOffset = 2, NextPieceOffset = -3 } } };
            var originalTroops = XDocument.Load(troopPath); var originalPieces = XDocument.Load(piecePath); var originalTemplates = XDocument.Load(templatePath);
            var originals = new[] { troopPath, itemPath, piecePath, templatePath }.ToDictionary(path => path, File.ReadAllBytes);
            Control<TextBox>("TroopSearchBox").Text = "no visible troops";
            var edits = Call("BuildTroopSourceEdits")!; Call("SaveSourceEdits", edits);
            Control<TextBox>("TroopSearchBox").Clear();
            var savedTroops = XDocument.Load(troopPath); var savedItems = XDocument.Load(itemPath); var savedPieces = XDocument.Load(piecePath); var savedTemplates = XDocument.Load(templatePath);
            var savedTroop = savedTroops.Descendants("NPCCharacter").Single(t => (string?)t.Attribute("id") == "test_guard");
            var rosters = savedTroop.Descendants("EquipmentRoster").ToArray();
            var oldRosters = originalTroops.Descendants("NPCCharacter").First().Descendants("EquipmentRoster").ToArray();
            Assert(XNode.DeepEquals(rosters[0], oldRosters[0]) && XNode.DeepEquals(rosters[2], oldRosters[2]), "Civilian/variant roster changed");
            Assert((string?)rosters[1].Attribute("custom") == "preserve" && rosters[1].Elements("equipment").Single(e => (string?)e.Attribute("slot") == "Body").Attribute("id")!.Value == "Item.armour_b", "Battle loadout not merged");
            Assert(!rosters[1].Elements("equipment").Any(e => (string?)e.Attribute("slot") == "Head"), "Cleared slot remained in source");
            Assert(rosters[1].Elements("equipment").Any(e => (string?)e.Attribute("slot") == "Item3") && rosters[1].Elements("equipment").Any(e => (string?)e.Attribute("slot") == "Horse"), "Unsupported slots removed");
            Assert((string?)savedTroop.Attribute("level") == "25" && savedTroop.Descendants("skill").Any(), "Troop metadata removed");
            Assert(XNode.DeepEquals(savedTroops.Descendants("NPCCharacter").Last(), originalTroops.Descendants("NPCCharacter").Last()), "Unrelated troop changed");
            var shield = savedItems.Descendants("Item").Single(i => (string?)i.Attribute("id") == "shield");
            Assert(VectorEquals((string?)shield.Descendants("Weapon").Single().Attribute("position"), .05f, 0, 0) && VectorEquals((string?)shield.Descendants("Weapon").Single().Attribute("rotation"), 0, 25, 0), "Held shield offsets not written");
            Assert(VectorEquals((string?)shield.Attribute("holster_position_shift"), 0, .12f, 0), "Holstered shield offset not written");
            var sword = savedItems.Descendants("CraftedItem").Single(i => (string?)i.Attribute("id") == "sword");
            var cloneId = (string)sword.Descendants("Piece").First().Attribute("id")!;
            var clone = savedPieces.Descendants("CraftingPiece").Single(p => (string?)p.Attribute("id") == cloneId);
            Assert((string?)clone.Element("BuildData")?.Attribute("piece_offset") == "5" && clone.Element("BladeData") != null, "Per-item piece clone missing offsets/metadata");
            Assert(XNode.DeepEquals(savedPieces.Descendants("CraftingPiece").First(), originalPieces.Descendants("CraftingPiece").First()), "Shared original piece modified by per-item save");
            Assert((string?)savedItems.Descendants("CraftedItem").Last().Descendants("Piece").First().Attribute("id") == "blade", "Per-item edit leaked to another weapon");
            var customTemplate = savedTemplates.Descendants("CraftingTemplate").Single(t => (string?)t.Attribute("id") == (string?)sword.Attribute("crafting_template"));
            Assert(VectorEquals((string?)customTemplate.Attribute("default_item_holster_position_offset"), 0, 0, .07f), "Per-item template clone not registered in source");
            Assert(XNode.DeepEquals(savedTemplates.Descendants("CraftingTemplate").First(), originalTemplates.Descendants("CraftingTemplate").First()), "Shared template modified");
            Assert(positions.Count == 0, "Saved tuning was not rebased");
            Assert(Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories).Count() == 5, "Direct save created companion XML files");
            foreach (var (path, bytes) in originals)
            {
                var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".viewport-*.bak");
                Assert(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(bytes), "Original backup not exact");
                var saved = File.ReadAllBytes(path); Assert(saved.AsSpan().StartsWith(new byte[] { 239, 187, 191 }), "UTF8 BOM lost");
                var text = File.ReadAllText(path); Assert(text.Contains("\r\n") && !text.Replace("\r\n", "").Contains('\n'), "Source line endings changed");
            }
            Assert(File.ReadAllText(troopPath).Contains("Keep troop comments") && File.ReadAllText(itemPath).Contains("Keep item comments") && File.ReadAllText(piecePath).Contains("Keep piece comments"), "Comments lost");
            Call("LoadTroopXml", troopPath);
            var items = (Dictionary<string, XElement>)Field("_equipmentItems");
            Assert(items["sword"].Descendants("Piece").First().Attribute("id")!.Value == cloneId, "Saved item did not reload");
            Assert(((Dictionary<string, CraftingPieceNode>)Field("_equipmentCraftingPieces"))[cloneId].PieceOffset == 5, "Saved custom piece not indexed on reload");

            // Standalone crafting edits modify the original selected definitions, not BuildData snippets/files.
            Call("LoadCraftingPieces", piecePath);
            var nodes = (ObservableCollection<CraftingPieceNode>)Field("_craftingPieces"); var blade = nodes.Single(p => p.Id == "blade");
            blade.PieceOffset = 3.5f; Call("SaveSourceEdits", Call("BuildCraftingSourceEdits"));
            Assert((string?)XDocument.Load(piecePath).Descendants("CraftingPiece").First().Element("BuildData")?.Attribute("piece_offset") == "3.5", "Standalone crafting BuildData not merged");
            Assert(!blade.IsDirty, "Saved piece remained dirty");
            Call("LoadCraftingItem", items["sword"]);
            var selected = (CraftingPieceNode)Control<ComboBox>("CraftingBladeCombo").SelectedItem;
            selected.Scale = 85; selected.NextPieceOffset = -4;
            Call("SaveSourceEdits", Call("BuildCraftingSourceEdits"));
            Assert((string?)XDocument.Load(itemPath).Descendants("CraftedItem").First().Descendants("Piece").First().Attribute("scale_factor") == "85", "Scale not written to source item's reference");
            Assert(!selected.IsDirty, "Saved selected-piece scale not rebased");
            positions["sword"] = new() { Holstered = new() { Z = .11f }, Pieces = new() { [selected.Id] = new() { PieceOffset = 6, PreviousPieceOffset = 1, NextPieceOffset = -2 } } };
            Call("SaveSourceEdits", Call("BuildCraftingSourceEdits"));
            var combined = XDocument.Load(itemPath).Descendants("CraftedItem").First();
            var combinedPieceId = (string)combined.Descendants("Piece").First().Attribute("id")!;
            Assert((string?)XDocument.Load(piecePath).Descendants("CraftingPiece").Single(p => (string?)p.Attribute("id") == combinedPieceId).Element("BuildData")?.Attribute("piece_offset") == "6", "Crafting source save dropped pending inline offsets");
            Assert(VectorEquals((string?)XDocument.Load(templatePath).Descendants("CraftingTemplate").Single(t => (string?)t.Attribute("id") == (string?)combined.Attribute("crafting_template")).Attribute("default_item_holster_position_offset"), 0, 0, .11f), "Crafting source save dropped pending holster positioning");
            Assert(((CraftingPieceNode)Control<ComboBox>("CraftingBladeCombo").SelectedItem).Id == combinedPieceId && positions.Count == 0, "Source crafting editor not rebound to saved custom piece");
            Call("SaveSourceEdits", Call("BuildCraftingSourceEdits"));
            Assert((string?)XDocument.Load(itemPath).Descendants("CraftedItem").First().Descendants("Piece").First().Attribute("id") == combinedPieceId, "Repeated save reverted inline edits");

            positions["shield"] = new() { Held = new() { X = .09f } };
            var editType = typeof(MainWindow).GetNestedType("SourceXmlEdits", BindingFlags.NonPublic)!;
            object ItemEdits()
            {
                var batch = Activator.CreateInstance(editType, nonPublic: true)!; Call("AddItemSourceEdits", batch, "shield"); return batch;
            }
            var conflicting = ItemEdits();
            File.AppendAllText(itemPath, "\r\n<!-- external edit -->");
            Reject(() => Call("SaveSourceEdits", conflicting), "Changed-on-disk source overwritten");
            Assert(File.ReadAllText(itemPath).Contains("external edit"), "External content lost on conflict");
            Call("SaveSourceEdits", ItemEdits());
            Assert(File.ReadAllText(itemPath).Contains("external edit"), "Fresh merge lost external comment");
            Call("LoadCraftingPieces", piecePath); blade = ((ObservableCollection<CraftingPieceNode>)Field("_craftingPieces")).Single(p => p.Id == "blade");
            blade.PieceOffset = float.NaN;
            var beforeInvalid = File.ReadAllBytes(piecePath); Reject(() => Call("BuildCraftingSourceEdits"), "Nonfinite source offsets accepted");
            Assert(File.ReadAllBytes(piecePath).SequenceEqual(beforeInvalid), "Invalid edit touched disk");
            blade.PieceOffset = blade.OriginalPieceOffset; blade.Scale = 95;
            Reject(() => Call("BuildCraftingSourceEdits"), "Standalone preview scale written to piece definition");
            var rollbackA = Fixture("rollback/a.xml", "<Items><Item id=\"a\" /></Items>");
            var rollbackB = Fixture("rollback/b.xml", "<Items><Item id=\"b\" /></Items>");
            var rollbackOriginals = new[] { rollbackA, rollbackB }.ToDictionary(path => path, File.ReadAllBytes);
            var rollbackBatch = Activator.CreateInstance(editType, nonPublic: true)!;
            foreach (var path in rollbackOriginals.Keys)
            {
                var file = editType.GetMethod("File", Private)!.Invoke(rollbackBatch, [path])!;
                ((XDocument)file.GetType().GetProperty("Document", Private)!.GetValue(file)!).Root!.SetAttributeValue("edited", "true");
            }
            File.SetAttributes(rollbackB, FileAttributes.ReadOnly);
            try
            {
                Reject(() => editType.GetMethod("Write", Private)!.Invoke(rollbackBatch, null), "Read-only destination unexpectedly written");
                Assert(rollbackOriginals.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "Failed multi-file save left partial source changes");
            }
            finally { File.SetAttributes(rollbackB, FileAttributes.Normal); }
            Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Staged save files left behind");
            Console.WriteLine("Source XML merges, battle/civilian/variant preservation, equipped offsets, per-item piece/template registration, reload/rebase, standalone piece/scale saves, exact backups/BOM/CRLF/comments, conflict detection, read-only rollback and finite validation: passed.");
        }
        finally
        {
            window?.Close(); if (backup != null) File.WriteAllBytes(settings, backup); else if (File.Exists(settings)) File.Delete(settings);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool VectorEquals(string? value, params float[] expected)
    {
        var parts = value?.Split(',').Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return parts?.Length == expected.Length && parts.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < .000001f);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (TargetInvocationException) { return; } throw new Exception(message);
    }
}
