using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LOTRAOM_Viewport;

internal static class TroopBrowserChecks
{
    internal static void Run()
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bannerlord Viewport", "settings.json");
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var root = Path.GetFullPath("obj/troop-browser-checks");
        var fixtures = Path.Combine(root, Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{\"SaveSettings\":false}");
            string Fixture(string path, string xml)
            {
                var full = Path.Combine(fixtures, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, xml);
                return full;
            }
            var single = Fixture("ModuleA/ModuleData/troops/a.xml", """
                <NPCCharacters>
                  <NPCCharacter id="gondor_guard" name="{=guard}Gondor Guard"><equipment slot="Body" id="Item.armour_a" /></NPCCharacter>
                  <NPCCharacter id="elite_archer" name="Ranger" />
                </NPCCharacters>
                """);
            Fixture("ModuleA/SubModule.xml", "<Module><Xmls><XmlName id=\"Items\" path=\"items\" /></Xmls></Module>");
            Fixture("ModuleA/ModuleData/items.xml", "<Items><Item id=\"armour_a\" mesh=\"mesh_a\" /></Items>");
            Fixture("ModuleB/SubModule.xml", "<Module><Xmls><XmlName id=\"Items\" path=\"items\" /></Xmls></Module>");
            Fixture("ModuleB/ModuleData/items.xml", "<Items><Item id=\"armour_b\" mesh=\"mesh_b\" /></Items>");
            Fixture("ModuleB/ModuleData/nested/b.xml", """
                <NPCCharacters>
                  <NPCCharacter id="gondor_guard" name="Duplicate Guard" />
                  <NPCCharacter id="rohan_rider" name="Rohan Rider"><equipment slot="Body" id="Item.armour_b" /></NPCCharacter>
                </NPCCharacters>
                """);
            Fixture("broken.xml", "<NPCCharacters><broken>");
            Fixture("unrelated.xml", "<Items><Item id=\"not_a_troop\" /></Items>");
            var empty = Path.Combine(fixtures, "empty"); Directory.CreateDirectory(empty);

            _ = new Application();
            window = new MainWindow { Width = 1440, Height = 1000, ShowActivated = false, Left = -10000,
                Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            T Control<T>(string name) => (T)window.FindName(name);
            object? Invoke(string name, params object[] args) => typeof(MainWindow)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
            object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var list = Control<ListBox>("TroopList"); var search = Control<TextBox>("TroopSearchBox");
            var summary = Control<TextBlock>("TroopSummaryText");
            window.Show(); Control<TabControl>("WorkspaceTabs").SelectedIndex = 1; Pump();
            Invoke("LoadTroopXml", fixtures);
            Assert(list.Items.Count == 3, "Folder did not aggregate/deduplicate recursive troop XML");
            Assert(list.Items.Cast<TroopNode>().Single(t => t.Id == "gondor_guard").DisplayName == "Gondor Guard", "Duplicate order not deterministic");
            Assert(summary.Text.Contains("1 duplicate") && summary.Text.Contains("1 unreadable"), "Skipped files/duplicates not reported");
            Assert(summary.ToolTip?.ToString()?.Contains("broken.xml") == true, "Unreadable source absent from tooltip");
            var items = (Dictionary<string, System.Xml.Linq.XElement>)Field("_equipmentItems");
            Assert(items.ContainsKey("armour_a") && items.ContainsKey("armour_b"), "Folder did not resolve both owning modules");
            Assert(((HashSet<string>)Field("_equipmentXmlSourcePaths")).Contains(Path.GetFullPath(single)), "Folder troop sources not protected against XML overwrite");
            Control<CheckBox>("SaveSettingsCheckBox").IsChecked = true;
            var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(saved.TroopsPath == fixtures && saved.SaveSettings, "Selected troop folder not saved");
            Control<CheckBox>("SaveSettingsCheckBox").IsChecked = false;
            saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(saved.TroopsPath == null && !saved.SaveSettings, "Troop path retained with settings disabled");
            list.SelectedItem = list.Items.Cast<TroopNode>().Single(t => t.Id == "gondor_guard");
            Assert(Control<ComboBox>("BodyCombo").Text == "Item.armour_a", "Selecting aggregated troop did not fill loadout");
            var title = Control<TextBlock>("SelectedAssetTitle").Text;
            search.Text = "  ELITE_ARCHER  ";
            Assert(list.Items.Count == 1 && ((TroopNode)list.Items[0]).DisplayName == "Ranger", "ID search/trim/case insensitive filtering failed");
            Assert(Control<ComboBox>("BodyCombo").Text == "Item.armour_a" && Control<TextBlock>("SelectedAssetTitle").Text == title,
                "Filtering changed custom loadout or rendered preview");
            search.Text = "rOhAn"; Assert(list.Items.Count == 1, "Name search failed");
            search.Text = "no matching troop"; Assert(list.Items.Count == 0 && summary.Text.StartsWith("0 of 3"), "No-match state incorrect");
            search.Clear(); Assert(list.Items.Count == 3, "Clearing search did not restore all troops");
            Pump(); Capture(window, Path.Combine(root, "troops.png"));
            window.Width = 1180; window.Height = 720; Pump(); Capture(window, Path.Combine(root, "troops-small.png"));
            Invoke("LoadTroopXml", single); Assert(list.Items.Count == 2 && summary.ToolTip == null, "Single XML regression/stale warnings");
            search.Text = "gondor"; Invoke("LoadTroopXml", fixtures);
            Assert(list.Items.Count == 1, "Active filter was lost on reload");
            Invoke("LoadTroopXml", empty); search.Clear();
            Assert(list.Items.Count == 0 && summary.Text.Contains("0 XML"), "Empty-folder state failed");
            Invoke("LoadTroopXml", Path.Combine(fixtures, "broken.xml"));
            Assert(list.Items.Count == 0 && summary.Text.StartsWith("Could not load troops"), "Invalid single XML error failed");
            Console.WriteLine("Recursive troop folder loading, multi-module equipment, duplicates/errors, single XML, live name/ID filtering and preview preservation: passed.");
        }
        finally
        {
            window?.Close();
            if (backup != null) File.WriteAllBytes(settings, backup); else if (File.Exists(settings)) File.Delete(settings);
            if (Directory.Exists(fixtures)) Directory.Delete(fixtures, recursive: true);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
