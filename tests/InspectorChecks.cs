using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LOTRAOM_Viewport;

internal static class InspectorChecks
{
    internal static void Run()
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bannerlord Viewport", "settings.json");
        var backup = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{\"SaveSettings\":false}");
            var app = new Application();
            window = new MainWindow { Width = 1440, Height = 1000, ShowActivated = false, Left = -10000,
                Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            T Control<T>(string name) => (T)window.FindName(name);
            window.Show(); Pump();
            var inspector = Control<Border>("ViewportInspector");
            var sections = new[] { "CraftingInspectorSection", "ItemPositionInspectorSection", "TableauInspectorSection", "TroopXmlInspectorSection" }
                .Select(Control<Expander>).ToArray();
            Assert(inspector.Visibility == Visibility.Collapsed, "Empty inspector visible");
            Assert(sections.All(section => !section.IsExpanded), "Sections did not start collapsed");
            foreach (var name in new[] { "ItemPositionEditorPanel", "TableauColourPanel", "TroopXmlExportPanel" }) Control<Border>(name).Visibility = Visibility.Visible;
            Pump();
            Assert(inspector.Visibility == Visibility.Visible && inspector.ActualHeight < 180, "Collapsed inspector too large");
            Capture(window, "inspector-collapsed.png");
            ToggleButton Header(Expander section) => Descendants(section).OfType<ToggleButton>().First();
            Header(sections[1]).SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump();
            Assert(sections[1].IsExpanded, "Header did not open section");
            Control<Expander>("ItemPositionXmlPreview").IsExpanded = true; Pump();
            Assert(sections[1].IsExpanded, "Nested XML preview collapsed its parent");
            Header(sections[2]).SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump();
            Assert(sections[2].IsExpanded && !sections[1].IsExpanded, "Accordion left multiple sections open");
            Assert(Control<TextBox>("TableauPrimaryHexBox").IsVisible, "Faction controls hidden inside open section");
            Capture(window, "inspector-colours.png");
            Header(sections[3]).SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump();
            Assert(sections[3].IsExpanded && !sections[2].IsExpanded, "XML section did not close colours");
            Control<ToggleButton>("InspectorCollapseToggle").IsChecked = true; Pump();
            Assert(inspector.ActualWidth == 40 && inspector.ActualHeight <= 42 && !Control<ScrollViewer>("InspectorSectionsScroll").IsVisible,
                "Whole inspector did not tuck away");
            Assert(sections[3].IsExpanded, "Tucking inspector discarded section choice");
            Capture(window, "inspector-hidden.png");
            Control<ToggleButton>("InspectorCollapseToggle").IsChecked = false; Pump();
            Assert(inspector.ActualWidth == 380 && Control<TextBox>("TroopXmlOutputBox").IsVisible, "Reopening lost selected section");
            Header(sections[3]).SetCurrentValue(ToggleButton.IsCheckedProperty, false); Pump();
            Assert(sections.All(section => !section.IsExpanded), "Active header did not close section");
            window.Width = 1180; window.Height = 720;
            Header(sections[1]).SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump();
            Assert(inspector.ActualHeight <= Control<Grid>("InspectorHost").ActualHeight + 1, "Inspector exceeds viewport height");
            var scroll = Control<ScrollViewer>("InspectorSectionsScroll");
            scroll.ScrollToEnd(); Pump();
            Assert(scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0, "Long section is not scrollable");
            Capture(window, "inspector-small.png");
            foreach (var name in new[] { "ItemPositionEditorPanel", "TableauColourPanel", "TroopXmlExportPanel" }) Control<Border>(name).Visibility = Visibility.Collapsed;
            Pump(); Assert(inspector.Visibility == Visibility.Collapsed, "Inspector remains after sections disappear");
            Control<Border>("CraftingEditorPanel").Visibility = Visibility.Visible;
            sections[0].IsExpanded = true; Pump();
            Assert(inspector.Visibility == Visibility.Visible && sections.Skip(1).All(section => !section.IsExpanded), "Crafting inspector not exclusive");
            Assert(!Control<Expander>("CraftingXmlPreview").IsExpanded, "Crafting XML was unfolded automatically");
            Capture(window, "inspector-crafting.png");
            Console.WriteLine("Inspector defaults, header toggles, accordion, nested XML, tuck/reopen, conditional visibility and small-window scrolling: passed.");
        }
        finally
        {
            window?.Close();
            if (backup != null) File.WriteAllBytes(settings, backup); else if (File.Exists(settings)) File.Delete(settings);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string name)
    {
        var directory = Path.GetFullPath("obj/inspector-checks"); Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
