using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using LOTRAOM_Viewport;

internal static class EquipmentDropdownChecks
{
    internal static void Run()
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bannerlord Viewport", "settings.json");
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var output = Path.GetFullPath("obj/equipment-dropdown-checks");
        var root = Path.Combine(output, Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.WriteAllText(settings, "{\"SaveSettings\":false}");
            Directory.CreateDirectory(Path.Combine(root, "ModuleData"));
            new XDocument(new XElement("Module", new XElement("Xmls", new XElement("XmlName", new XAttribute("id", "Items"), new XAttribute("path", "items")))))
                .Save(Path.Combine(root, "SubModule.xml"));
            var slots = new[] { ("Helmet", "HeadArmor"), ("Cape", "Cape"), ("Body", "BodyArmor"), ("Arm", "HandArmor"), ("Leg", "LegArmor"), ("Item0", "OneHandedWeapon"), ("Item1", "Shield") };
            var items = new XElement("Items"); var loadout = new Dictionary<string, string>();
            foreach (var (slot, type) in slots)
            {
                loadout[slot] = "Item." + slot.ToLowerInvariant() + "_first";
                foreach (var suffix in new[] { "first", "second" })
                {
                    var item = new XElement("Item", new XAttribute("id", slot.ToLowerInvariant() + "_" + suffix),
                        new XAttribute("type", type), new XAttribute("name", suffix == "second" ? "Quilted alternative " + slot : "Original " + slot));
                    if (slot is "Item0" or "Item1") item.Add(new XElement("ItemComponent", new XElement("Weapon", new XAttribute("weapon_class", slot == "Item0" ? "OneHandedSword" : "LargeShield"))));
                    items.Add(item);
                }
            }
            new XDocument(items).Save(Path.Combine(root, "ModuleData/items.xml"));
            var troopPath = Path.Combine(root, "ModuleData/troops.xml");
            new XDocument(new XElement("NPCCharacters", new XElement("NPCCharacter", new XAttribute("id", "test"), new XAttribute("name", "Dropdown test"),
                new XElement("Equipments", new XElement("EquipmentRoster", loadout.Select(pair => new XElement("equipment", new XAttribute("slot", pair.Key == "Helmet" ? "Head" : pair.Key), new XAttribute("id", pair.Value))))))))
                .Save(troopPath);
            _ = new Application(); window = new MainWindow { Width = 1180, Height = 720, ShowActivated = false, Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual };
            T Control<T>(string name) => (T)window.FindName(name);
            object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
            Dictionary<string, string> PreviewLoadout() => new((IReadOnlyDictionary<string, string>)typeof(MainWindow)
                .GetField("_troopPreviewLoadout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            window.Show(); Control<TabControl>("WorkspaceTabs").SelectedIndex = 1; Pump();
            Call("LoadTroopXml", troopPath); Control<ListBox>("TroopList").SelectedIndex = 0; Pump();
            foreach (var (slot, _) in slots)
            {
                var combo = Control<ComboBox>(slot + "Combo"); combo.BringIntoView(); Pump();
                var arrow = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                void ClickArrow() => typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(arrow, null);
                ClickArrow(); Pump();
                Assert(combo.IsDropDownOpen && popup.IsOpen && popup.Child.IsVisible && popup.Child.RenderSize.Height > 30, slot + ": arrow did not display dropdown");
                Assert(combo.Items.Count >= 2 && combo.Text == loadout[slot], slot + ": current Item.id emptied list or changed equipment");
                if (slot is not ("Item0" or "Item1")) Assert(combo.Items.Cast<MeshAssetNode>().All(node => node.DisplayName.StartsWith("Item." + slot.ToLowerInvariant())), slot + ": incorrect armour slot options");
                if (slot == "Leg") Capture(popup.Child, Path.Combine(output, "leg-dropdown.png"));
                var alternate = combo.Items.Cast<MeshAssetNode>().Single(node => node.DisplayName == "Item." + slot.ToLowerInvariant() + "_second");
                combo.SelectedItem = alternate; combo.IsDropDownOpen = false; Pump();
                Assert(combo.Text == alternate.DisplayName && combo.SelectedItem == alternate, slot + ": choosing an item cleared selection");
                Assert(PreviewLoadout()[slot] == alternate.DisplayName, slot + ": selection did not update rendered loadout without Apply");
                ClickArrow(); Pump(); Assert(combo.Items.Count >= 2 && combo.Text == alternate.DisplayName, slot + ": reopening only showed selected item");
                combo.IsDropDownOpen = false;
                var textBox = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
                textBox.Focus(); textBox.Text = slot.ToLowerInvariant() + "_sec"; textBox.CaretIndex = textBox.Text.Length; Pump();
                Assert(combo.Items.Count == 1 && combo.Text == textBox.Text && textBox.Text == slot.ToLowerInvariant() + "_sec", slot + ": ID search lost text or failed filtering");
                Assert(PreviewLoadout()[slot] == alternate.DisplayName, slot + ": partial search replaced displayed equipment");
                textBox.Text = "quilted"; Pump();
                Assert(combo.Items.Count >= 1 && combo.Items.Cast<MeshAssetNode>().All(node => node.Status.Contains("Quilted")), slot + ": display-name search failed");
                textBox.Text = "no matches"; Pump(); Assert(combo.Items.Count == 0 && combo.Text == "no matches", slot + ": unmatched typing cleared text");
                textBox.Clear(); Pump(); Assert(combo.Items.Count >= 2 && combo.Text == "", slot + ": clearing search failed");
                Assert(!PreviewLoadout().ContainsKey(slot), slot + ": clearing slot did not remove rendered equipment");
                textBox.Text = loadout[slot]; Pump();
                Assert(PreviewLoadout()[slot] == loadout[slot], slot + ": typing a recognised Item.id did not update render");
                combo.IsDropDownOpen = false;
                Call("SetEquipmentBoxes", loadout); Pump();
                Assert(!combo.IsDropDownOpen && combo.Text == loadout[slot], slot + ": programmatic loadout opened/filtered focused field");
                Call("RenderLoadoutPreview", "Dropdown test", loadout);
                ClickArrow(); Pump(); Assert(combo.Items.Count >= 2, slot + ": programmatic loadout left stale search");
                combo.IsDropDownOpen = false;
            }
            Console.WriteLine("All seven custom-loadout arrow popups, live rendered selections/removals without Apply, slot-specific Item IDs, preserved values/search previews, reopening, ID/name search and programmatic loads: passed.");
        }
        finally
        {
            window?.Close(); if (backup != null) File.WriteAllBytes(settings, backup); else if (File.Exists(settings)) File.Delete(settings);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.RenderSize.Width), (int)Math.Ceiling(element.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
