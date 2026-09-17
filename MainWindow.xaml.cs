using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using Microsoft.Win32;
using TpacTool.Lib;

namespace LOTRAOM_Viewport;

public partial class MainWindow : Window
{
    private static readonly HashSet<Guid> PreviewAssetTypes = [Metamesh.TYPE_GUID, TpacTool.Lib.Material.TYPE_GUID, Texture.TYPE_GUID];
    private const double AssetPreviewSize = 2.8;
    private const double CraftingPieceUnitScale = 0.035;
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Bannerlord Viewport");
    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string[] EquipmentSlots = ["Helmet", "Cape", "Body", "Arm", "Leg", "Item0", "Item1"];
    private static readonly string[] CraftingSlots = ["Blade", "Guard", "Handle", "Pommel"];
    private static readonly (string Type, int Order)[] DefaultCraftingBuildOrder =
        [("Handle", 0), ("Guard", 1), ("Blade", 2), ("Pommel", -1)];
    private static readonly (string Type, int Order)[] AxeMaceCraftingBuildOrder =
        [("Handle", 0), ("Blade", 1), ("Pommel", -1)];
    private static readonly Dictionary<string, (string Type, int Order)[]> BundledCraftingBuildOrders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OneHandedSword"] = DefaultCraftingBuildOrder,
            ["TwoHandedSword"] = DefaultCraftingBuildOrder,
            ["Dagger"] = DefaultCraftingBuildOrder,
            ["ThrowingKnife"] = DefaultCraftingBuildOrder,
            ["TwoHandedPolearm"] = DefaultCraftingBuildOrder,
            ["Pike"] = DefaultCraftingBuildOrder,
            ["Javelin"] = DefaultCraftingBuildOrder,
            ["OneHandedAxe"] = AxeMaceCraftingBuildOrder,
            ["TwoHandedAxe"] = AxeMaceCraftingBuildOrder,
            ["Mace"] = AxeMaceCraftingBuildOrder,
            ["TwoHandedMace"] = AxeMaceCraftingBuildOrder,
            ["ThrowingAxe"] = [("Handle", 0), ("Blade", 1)]
        };
    private readonly ObservableCollection<TpacPackageNode> _packages = [];
    private readonly ObservableCollection<MeshAssetNode> _assetSearchResults = [];
    private readonly List<TpacPackageNode> _allPackages = [];
    private readonly ObservableCollection<TroopNode> _troops = [];
    private readonly ObservableCollection<MeshAssetNode> _meshOptions = [];
    private readonly List<MeshAssetNode> _weaponOptions = [];
    private readonly Dictionary<string, XElement> _equipmentItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CraftingPieceNode> _equipmentCraftingPieces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<CraftingPieceNode> _craftingPieces = [];
    private readonly Dictionary<string, HashSet<string>> _craftingTemplatePieceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Type, int Order)[]> _craftingTemplateBuildOrders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _craftingSearchText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ComboBox, ObservableCollection<MeshAssetNode>> _filteredMeshOptions = [];
    private readonly Dictionary<TextBox, ComboBox> _equipmentTextBoxes = [];
    private readonly Dictionary<ComboBox, ObservableCollection<CraftingPieceNode>> _filteredCraftingOptions = [];
    private readonly Dictionary<TextBox, ComboBox> _craftingTextBoxes = [];
    private readonly Dictionary<Guid, string> _materialPackagePaths = [];
    private readonly Dictionary<Guid, string> _texturePackagePaths = [];
    private readonly Dictionary<(string Path, Guid Asset), AssetPackage> _lookupPackageCache = [];
    private bool _isUpdatingComboFilters;
    private bool _isUpdatingCraftingCombos;
    private bool _isUpdatingCraftingEditor;
    private bool _isLoadingSavedSettings;
    private CraftingPieceNode? _craftingBlade;
    private CraftingPieceNode? _craftingGuard;
    private CraftingPieceNode? _craftingHandle;
    private CraftingPieceNode? _craftingPommel;
    private CraftingPieceNode? _activeCraftingPiece;
    private float _craftingStepSize = 1f;
    private readonly Model3DGroup _scene = new();
    private readonly Model3DGroup _assetModel = new();
    private readonly StudioRenderer _renderer;
    private bool _isUpdatingTableauColours = true;
    private Color _tableauPrimaryColour = Colors.White;
    private Color _tableauSecondaryColour = Colors.White;
    private int _previewWorkspace = -1;
    private readonly System.Windows.Threading.DispatcherTimer _tableauColourTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120)
    };
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
        BindEquipmentDropdowns();
        BindCraftingDropdowns();
        _renderer = new StudioRenderer(ModelViewport, _scene);
        InitializeTroopPoseControls();
        _tableauColourTimer.Tick += (_, _) =>
        {
            _tableauColourTimer.Stop();
            UpdateTableauColourEditorVisibility();
        };
        _assetModel.Changed += (_, _) => UpdateTableauColourEditorVisibility();
        _isUpdatingTableauColours = false;
        Closed += (_, _) => { _tableauColourTimer.Stop(); _renderer.Dispose(); };
        ResetScene();
        RenderEmptyPreview();
        LoadSavedSettingsIfAvailable();
        RefreshTroopPoseLibrary();
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs))
        {
            return;
        }

        UpdateCraftingEditorVisibility();
        UpdateTableauColourEditorVisibility();
        RefreshTroopPoseAvailability();
        UpdateTroopXmlExport();
    }

    private void UpdateTableauColourEditorVisibility()
    {
        if (_isUpdatingTableauColours || TableauColourPanel == null) return;
        var enabled = _previewWorkspace is 0 or 1 && StudioRenderer.HasFactionColourBlending(_assetModel);
        TableauColourPanel.Visibility = enabled && WorkspaceTabs.SelectedIndex == _previewWorkspace
            ? Visibility.Visible : Visibility.Collapsed;
        _renderer.SetColours(_tableauPrimaryColour, _tableauSecondaryColour, enabled);
    }

    private static bool TryParseTableauColour(string text, out Color colour)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            colour = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
        colour = default;
        return false;
    }

    private void TableauHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingTableauColours || sender is not TextBox box) return;
        if (!TryParseTableauColour(box.Text, out var colour))
        {
            box.BorderBrush = Brushes.IndianRed;
            box.ToolTip = "Enter a six-digit hex colour.";
            return;
        }
        box.ClearValue(Control.BorderBrushProperty);
        box.ClearValue(FrameworkElement.ToolTipProperty);
        if (Equals(box.Tag, "Primary"))
        {
            _tableauPrimaryColour = colour;
            TableauPrimarySwatch.Background = new SolidColorBrush(colour);
        }
        else
        {
            _tableauSecondaryColour = colour;
            TableauSecondarySwatch.Background = new SolidColorBrush(colour);
        }
        _tableauColourTimer.Stop();
        _tableauColourTimer.Start();
    }

    private void TableauHex_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && TryParseTableauColour(box.Text, out var colour))
            box.Text = $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    }

    private void TableauColourPicker_Click(object sender, RoutedEventArgs e)
    {
        var primary = sender is Button button && Equals(button.Tag, "Primary");
        var colour = primary ? _tableauPrimaryColour : _tableauSecondaryColour;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B)
        };
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                (primary ? TableauPrimaryHexBox : TableauSecondaryHexBox).Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
        finally { owner.ReleaseHandle(); }
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
        SaveSettingsIfEnabled();
    }

    private void LoadAssetPackages(string folderPath)
    {
        AssetPathBox.Text = folderPath;
        _packages.Clear();
        _allPackages.Clear();
        _materialPackagePaths.Clear();
        _texturePackagePaths.Clear();
        _texturePixelGuids.Clear();
        _lookupPackageCache.Clear();
        _equipmentPreviewModels.Clear();
        _equipmentDependencyMeshes.Clear();
        _equipmentDependenciesIndexed = false;

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

            _allPackages.Add(package);
        }

        ApplyAssetSearchFilter();
        PackageCountText.Text = _packages.Count.ToString();
        MeshCountText.Text = _packages.Sum(package => package.Assets.Count).ToString();
        RefreshMeshOptions();
        RefreshTroopPoseLibrary();
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

        if (TroopList.SelectedItem is TroopNode troop)
        {
            LoadEquipmentDefinitions(TroopXmlPathBox.Text);
            RenderLoadoutPreview(troop.DisplayName, troop.Equipment);
        }
    }

    private void SaveSettingsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSavedSettings)
        {
            return;
        }

        if (SaveSettingsCheckBox.IsChecked == true)
        {
            SaveSettings();
        }
        else
        {
            SaveSettings();
            SettingsStatusText.Text = "Settings saving is off.";
        }
    }

    private void LoadSavedSettingsIfAvailable()
    {
        if (!File.Exists(SettingsFilePath))
        {
            SettingsStatusText.Text = "Settings are not saved.";
            return;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath));
            if (settings?.SaveSettings != true)
            {
                SettingsStatusText.Text = "Settings saving is off.";
                return;
            }

            _isLoadingSavedSettings = true;
            try
            {
                SaveSettingsCheckBox.IsChecked = true;
            }
            finally
            {
                _isLoadingSavedSettings = false;
            }

            var loaded = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.AssetPackagesPath) &&
                Directory.Exists(settings.AssetPackagesPath))
            {
                LoadAssetPackages(settings.AssetPackagesPath);
                loaded.Add("AssetPackages");
            }

            if (!string.IsNullOrWhiteSpace(settings.CraftingPiecesPath) &&
                File.Exists(settings.CraftingPiecesPath))
            {
                LoadCraftingPieces(settings.CraftingPiecesPath);
                loaded.Add("crafting pieces");
            }

            if (!string.IsNullOrWhiteSpace(settings.CraftingTemplatesPath) &&
                File.Exists(settings.CraftingTemplatesPath))
            {
                LoadCraftingTemplates(settings.CraftingTemplatesPath);
                loaded.Add("crafting templates");
            }

            SettingsStatusText.Text = loaded.Count == 0
                ? "Saved settings found, but paths are missing."
                : $"Loaded saved {string.Join(", ", loaded)}.";
        }
        catch (Exception ex)
        {
            _isLoadingSavedSettings = false;
            SettingsStatusText.Text = $"Could not load settings: {ex.Message}";
        }
    }

    private void SaveSettingsIfEnabled()
    {
        if (SaveSettingsCheckBox.IsChecked == true && !_isLoadingSavedSettings)
        {
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var settings = new AppSettings
            {
                SaveSettings = SaveSettingsCheckBox.IsChecked == true,
                AssetPackagesPath = GetPersistablePath(AssetPathBox.Text),
                CraftingPiecesPath = GetPersistablePath(CraftingPiecesPathBox.Text),
                CraftingTemplatesPath = GetPersistablePath(CraftingTemplatesPathBox.Text)
            };

            File.WriteAllText(
                SettingsFilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

            SettingsStatusText.Text = settings.SaveSettings
                ? $"Settings saved to {SettingsFilePath}"
                : "Settings saving is off.";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"Could not save settings: {ex.Message}";
        }
    }

    private static string? GetPersistablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("Select ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Optional ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private void AssetSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyAssetSearchFilter();
        MeshCountText.Text = string.IsNullOrWhiteSpace(AssetSearchBox.Text)
            ? _packages.Sum(package => package.Assets.Count).ToString()
            : _assetSearchResults.Count.ToString();
    }

    private void ApplyAssetSearchFilter()
    {
        var query = AssetSearchBox?.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(query))
        {
            AssetTree.ItemsSource = _packages;
            _packages.Clear();
            foreach (var package in _allPackages)
            {
                _packages.Add(package);
            }

            return;
        }

        AssetTree.ItemsSource = _assetSearchResults;
        _packages.Clear();
        _assetSearchResults.Clear();

        foreach (var asset in _allPackages
                     .SelectMany(package => package.Assets)
                     .Where(asset =>
                         asset.Kind == "Mesh" &&
                         asset.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(asset => asset.DisplayName))
            {
                _assetSearchResults.Add(asset);
            }
    }

    private void BindEquipmentDropdowns()
    {
        foreach (var comboBox in GetEquipmentCombos())
        {
            var filteredOptions = new ObservableCollection<MeshAssetNode>();
            _filteredMeshOptions[comboBox] = filteredOptions;
            comboBox.ItemsSource = filteredOptions;
            comboBox.IsEditable = true;
            comboBox.IsTextSearchEnabled = false;
            comboBox.StaysOpenOnEdit = true;
            TextSearch.SetTextPath(comboBox, nameof(MeshAssetNode.DisplayName));
            comboBox.Loaded += EquipmentCombo_Loaded;
            comboBox.DropDownOpened += EquipmentCombo_DropDownOpened;
            comboBox.SelectionChanged += CustomLoadout_Changed;
            comboBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(CustomLoadout_Changed));
        }
    }

    private void EquipmentCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            AttachEquipmentComboTextBox(comboBox);
        }
    }

    private void RefreshMeshOptions()
    {
        var currentValues = ReadEquipmentBoxes();
        _meshOptions.Clear();

        foreach (var mesh in _packages
                     .SelectMany(package => package.Assets)
                     .Where(asset => asset.Kind == "Mesh")
                     .OrderBy(asset => asset.DisplayName))
        {
            _meshOptions.Add(mesh);
        }

        foreach (var comboBox in GetEquipmentCombos())
        {
            FilterEquipmentCombo(comboBox, comboBox.Text);
        }

        SetEquipmentBoxes(currentValues);
    }

    private void EquipmentCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            AttachEquipmentComboTextBox(comboBox);
            FilterEquipmentCombo(comboBox, comboBox.Text);
        }
    }

    private void EquipmentCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingComboFilters ||
            sender is not TextBox textBox ||
            !_equipmentTextBoxes.TryGetValue(textBox, out var comboBox))
        {
            return;
        }

        FilterEquipmentCombo(comboBox, textBox.Text);
        if (comboBox.IsKeyboardFocusWithin)
        {
            comboBox.IsDropDownOpen = true;
        }
    }

    private void AttachEquipmentComboTextBox(ComboBox comboBox)
    {
        comboBox.ApplyTemplate();
        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is not TextBox textBox ||
            _equipmentTextBoxes.ContainsKey(textBox))
        {
            return;
        }

        _equipmentTextBoxes[textBox] = comboBox;
        textBox.TextChanged += EquipmentCombo_TextChanged;
    }

    private void FilterEquipmentCombo(ComboBox comboBox, string? searchText)
    {
        if (!_filteredMeshOptions.TryGetValue(comboBox, out var filteredOptions))
        {
            return;
        }

        var needle = searchText?.Trim() ?? string.Empty;
        IEnumerable<MeshAssetNode> options = ReferenceEquals(comboBox, Item0Combo) || ReferenceEquals(comboBox, Item1Combo)
            ? _weaponOptions : _meshOptions;
        var matches = string.IsNullOrWhiteSpace(needle)
            ? options.Take(250)
            : options
                .Where(option => option.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    option.Kind == "Item" && option.Status.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(250);

        _isUpdatingComboFilters = true;
        filteredOptions.Clear();
        foreach (var match in matches)
        {
            filteredOptions.Add(match);
        }
        _isUpdatingComboFilters = false;
    }

    private void BindCraftingDropdowns()
    {
        foreach (var (comboBox, slot) in GetCraftingCombos())
        {
            var filteredOptions = new ObservableCollection<CraftingPieceNode>();
            _filteredCraftingOptions[comboBox] = filteredOptions;
            comboBox.Tag = slot;
            comboBox.ItemsSource = filteredOptions;
            comboBox.IsEditable = true;
            comboBox.IsTextSearchEnabled = false;
            comboBox.StaysOpenOnEdit = true;
            TextSearch.SetTextPath(comboBox, nameof(CraftingPieceNode.Id));
            comboBox.Loaded += CraftingCombo_Loaded;
            comboBox.DropDownOpened += CraftingCombo_DropDownOpened;
            comboBox.GotKeyboardFocus += CraftingCombo_ActivateSelectedPiece;
            comboBox.AddHandler(ComboBoxItem.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(CraftingComboItem_Click), true);
        }
    }

    private IEnumerable<(ComboBox ComboBox, string Slot)> GetCraftingCombos()
    {
        yield return (CraftingBladeCombo, "Blade");
        yield return (CraftingGuardCombo, "Guard");
        yield return (CraftingHandleCombo, "Handle");
        yield return (CraftingPommelCombo, "Pommel");
    }

    private void CraftingCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            AttachCraftingComboTextBox(comboBox);
        }
    }

    private void CraftingCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            AttachCraftingComboTextBox(comboBox);
            ActivateSelectedCraftingPiece(comboBox);
            FilterCraftingCombo(comboBox, comboBox.Text, restoreSelection: true);
            if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox textBox)
            {
                textBox.SelectAll();
            }
        }
    }

    private void CraftingCombo_ActivateSelectedPiece(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            ActivateSelectedCraftingPiece(comboBox);
        }
    }

    private void CraftingComboItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            ItemsControl.ContainerFromElement(comboBox, e.OriginalSource as DependencyObject) is not ComboBoxItem item ||
            item.DataContext is not CraftingPieceNode piece)
        {
            return;
        }

        comboBox.SelectedItem = piece;
        comboBox.IsDropDownOpen = false;
        ApplyCraftingPieceSelection(comboBox, piece == CraftingPieceNode.Empty ? null : piece);
        e.Handled = true;
    }

    private void ActivateSelectedCraftingPiece(ComboBox comboBox)
    {
        if (_isUpdatingCraftingCombos)
        {
            return;
        }

        if (comboBox.SelectedItem is CraftingPieceNode piece &&
            piece != CraftingPieceNode.Empty)
        {
            SetActiveCraftingPiece(piece);
        }
    }

    private void AttachCraftingComboTextBox(ComboBox comboBox)
    {
        comboBox.ApplyTemplate();
        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is not TextBox textBox ||
            _craftingTextBoxes.ContainsKey(textBox))
        {
            return;
        }

        _craftingTextBoxes[textBox] = comboBox;
        textBox.TextChanged += CraftingCombo_TextChanged;
    }

    private void CraftingCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingCraftingCombos ||
            sender is not TextBox textBox ||
            !_craftingTextBoxes.TryGetValue(textBox, out var comboBox))
        {
            return;
        }

        if (comboBox.SelectedItem is CraftingPieceNode selectedPiece &&
            selectedPiece != CraftingPieceNode.Empty &&
            textBox.Text.Equals(selectedPiece.Id, StringComparison.OrdinalIgnoreCase))
        {
            ActivateSelectedCraftingPiece(comboBox);
            return;
        }

        FilterCraftingCombo(comboBox, textBox.Text, restoreSelection: false);
        if (comboBox.IsKeyboardFocusWithin)
        {
            comboBox.IsDropDownOpen = true;
        }
    }

    private void FilterCraftingCombo(ComboBox comboBox, string? searchText, bool restoreSelection)
    {
        if (!_filteredCraftingOptions.TryGetValue(comboBox, out var filteredOptions) ||
            comboBox.Tag is not string slot)
        {
            return;
        }

        var normalizedSearchText = (searchText ?? string.Empty).Trim();
        var selectedPiece = comboBox.SelectedItem as CraftingPieceNode;
        if (normalizedSearchText.Equals(CraftingPieceNode.Empty.Id, StringComparison.OrdinalIgnoreCase) ||
            selectedPiece != null && normalizedSearchText.Equals(selectedPiece.Id, StringComparison.OrdinalIgnoreCase))
        {
            normalizedSearchText = string.Empty;
        }

        _craftingSearchText[slot] = normalizedSearchText;
        var selectedId = selectedPiece?.Id;
        var typedText = searchText ?? string.Empty;
        var matches = FilterCraftingPieces(slot).Take(300).ToArray();

        _isUpdatingCraftingCombos = true;
        filteredOptions.Clear();
        filteredOptions.Add(CraftingPieceNode.Empty);
        foreach (var match in matches)
        {
            filteredOptions.Add(match);
        }

        if (restoreSelection && !string.IsNullOrWhiteSpace(selectedId))
        {
            comboBox.SelectedItem = filteredOptions.FirstOrDefault(piece =>
                piece.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        }
        else if (!restoreSelection)
        {
            comboBox.Text = typedText;
            if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox textBox)
            {
                textBox.Text = typedText;
                textBox.CaretIndex = typedText.Length;
            }
        }

        _isUpdatingCraftingCombos = false;
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

    private void ChooseCraftingPieces_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Bannerlord crafting_pieces.xml",
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadCraftingPieces(dialog.FileName);
            SaveSettingsIfEnabled();
        }
    }

    private void ChooseCraftingTemplates_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Bannerlord crafting_templates.xml or XSLT",
            Filter = "XML/XSLT files (*.xml;*.xslt)|*.xml;*.xslt|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadCraftingTemplates(dialog.FileName);
            SaveSettingsIfEnabled();
        }
    }

    private void LoadCraftingPieces(string filePath)
    {
        CraftingPiecesPathBox.Text = filePath;
        _craftingPieces.Clear();
        ClearCraftingSelections();

        try
        {
            var document = XDocument.Load(filePath);
            var pieces = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("CraftingPiece", StringComparison.OrdinalIgnoreCase))
                .Select(ParseCraftingPiece)
                .Where(piece => piece != null)
                .Cast<CraftingPieceNode>()
                .GroupBy(piece => piece.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(piece => piece.Id)
                .ToArray();

            foreach (var piece in pieces)
            {
                _craftingPieces.Add(piece);
            }

            RefreshCraftingTemplates();
            RefreshCraftingPieceCombos();
            CraftingSummaryText.Text = _craftingPieces.Count == 0
                ? "No CraftingPiece entries found."
                : $"Loaded {_craftingPieces.Count} crafting piece(s).";
            SelectedAssetTitle.Text = "Crafting pieces loaded";
            SelectedAssetSubtitle.Text = "Pick weapon pieces to render them from loaded TPAC meshes.";
        }
        catch (Exception ex)
        {
            CraftingSummaryText.Text = $"Could not load crafting pieces: {ex.Message}";
        }
    }

    private void LoadCraftingTemplates(string filePath)
    {
        CraftingTemplatesPathBox.Text = filePath;
        _craftingTemplatePieceIds.Clear();
        _craftingTemplateBuildOrders.Clear();

        try
        {
            var document = XDocument.Load(filePath);
            XNamespace xsl = "http://www.w3.org/1999/XSL/Transform";

            foreach (var template in document.Descendants(xsl + "template"))
            {
                var match = template.Attribute("match")?.Value ?? string.Empty;
                var idMatch = Regex.Match(match, @"CraftingTemplate\[@id='([^']+)'\]");
                if (!idMatch.Success)
                {
                    continue;
                }

                var templateId = idMatch.Groups[1].Value;
                var ids = template.Descendants()
                    .Where(element => element.Name.LocalName is "UsablePiece" or "AvailablePiece")
                    .Select(element => GetAttributeValue(element, "piece_id") ?? GetAttributeValue(element, "id"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                _craftingTemplatePieceIds[templateId] = ids;
            }

            foreach (var template in document.Descendants()
                         .Where(element => element.Name.LocalName.Equals("CraftingTemplate", StringComparison.OrdinalIgnoreCase)))
            {
                var templateId = GetAttributeValue(template, "id");
                if (string.IsNullOrWhiteSpace(templateId))
                {
                    continue;
                }

                var ids = template.Descendants()
                    .Where(element => element.Name.LocalName is "UsablePiece" or "AvailablePiece")
                    .Select(element => GetAttributeValue(element, "piece_id") ?? GetAttributeValue(element, "id"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (ids.Count > 0)
                {
                    _craftingTemplatePieceIds[templateId] = ids;
                }

                var order = template.Descendants()
                    .Where(element => element.Name.LocalName.Equals("PieceData", StringComparison.OrdinalIgnoreCase))
                    .Select(element => (
                        Type: GetAttributeValue(element, "piece_type") ?? string.Empty,
                        Order: int.TryParse(GetAttributeValue(element, "build_order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderValue)
                            ? orderValue
                            : 0))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Type))
                    .ToArray();

                if (order.Length > 0)
                {
                    _craftingTemplateBuildOrders[templateId] = order;
                }
            }

            RefreshCraftingTemplates();
            RefreshCraftingPieceCombos();
            CraftingSummaryText.Text = $"Loaded {_craftingTemplatePieceIds.Count} crafting template filter(s).";
        }
        catch (Exception ex)
        {
            CraftingSummaryText.Text = $"Could not load crafting templates: {ex.Message}";
        }
    }

    private void RefreshCraftingTemplates()
    {
        var selected = CraftingTemplateCombo.SelectedItem as string;
        CraftingTemplateCombo.Items.Clear();
        CraftingTemplateCombo.Items.Add("(All pieces)");

        foreach (var template in BundledCraftingBuildOrders.Keys
                     .Concat(_craftingTemplatePieceIds.Keys)
                     .Concat(_craftingTemplateBuildOrders.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value))
        {
            CraftingTemplateCombo.Items.Add(template);
        }

        CraftingTemplateCombo.SelectedItem = selected != null && CraftingTemplateCombo.Items.Contains(selected)
            ? selected
            : "(All pieces)";
    }

    private void RefreshCraftingPieceCombos()
    {
        var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blade"] = _craftingBlade?.Id,
            ["Guard"] = _craftingGuard?.Id,
            ["Handle"] = _craftingHandle?.Id,
            ["Pommel"] = _craftingPommel?.Id
        };

        _isUpdatingCraftingCombos = true;
        PopulateCraftingCombo(CraftingBladeCombo, "Blade", previous["Blade"]);
        PopulateCraftingCombo(CraftingGuardCombo, "Guard", previous["Guard"]);
        PopulateCraftingCombo(CraftingHandleCombo, "Handle", previous["Handle"]);
        PopulateCraftingCombo(CraftingPommelCombo, "Pommel", previous["Pommel"]);
        _craftingBlade = RestoreCraftingSelection(CraftingBladeCombo, previous["Blade"]);
        _craftingGuard = RestoreCraftingSelection(CraftingGuardCombo, previous["Guard"]);
        _craftingHandle = RestoreCraftingSelection(CraftingHandleCombo, previous["Handle"]);
        _craftingPommel = RestoreCraftingSelection(CraftingPommelCombo, previous["Pommel"]);
        _isUpdatingCraftingCombos = false;
    }

    private void PopulateCraftingCombo(ComboBox comboBox, string slot, string? previousId)
    {
        if (!_filteredCraftingOptions.TryGetValue(comboBox, out var filteredOptions))
        {
            return;
        }

        filteredOptions.Clear();
        filteredOptions.Add(CraftingPieceNode.Empty);

        foreach (var piece in FilterCraftingPieces(slot))
        {
            filteredOptions.Add(piece);
        }

        comboBox.SelectedItem = string.IsNullOrWhiteSpace(previousId)
            ? CraftingPieceNode.Empty
            : filteredOptions.FirstOrDefault(piece => piece.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase)) ??
              CraftingPieceNode.Empty;
    }

    private IEnumerable<CraftingPieceNode> FilterCraftingPieces(string slot)
    {
        HashSet<string>? allowedIds = null;
        if (CraftingTemplateCombo.SelectedItem is string templateId &&
            !templateId.Equals("(All pieces)", StringComparison.OrdinalIgnoreCase) &&
            _craftingTemplatePieceIds.TryGetValue(templateId, out var templateIds))
        {
            allowedIds = templateIds;
        }

        _craftingSearchText.TryGetValue(slot, out var searchText);
        return _craftingPieces
            .Where(piece => piece.PieceType.Equals(slot, StringComparison.OrdinalIgnoreCase))
            .Where(piece => allowedIds == null || allowedIds.Contains(piece.Id))
            .Where(piece => string.IsNullOrWhiteSpace(searchText) ||
                            piece.Id.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                            piece.MeshName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(piece => piece.Id);
    }

    private CraftingPieceNode? RestoreCraftingSelection(ComboBox comboBox, string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            return null;
        }

        if (!_filteredCraftingOptions.TryGetValue(comboBox, out var filteredOptions))
        {
            return null;
        }

        foreach (var item in filteredOptions)
        {
            if (item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return item;
            }
        }

        return null;
    }

    private void CraftingTemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCraftingCombos)
        {
            return;
        }

        RefreshCraftingPieceCombos();
        RenderCraftingWeaponPreview();
    }

    private void CraftingPieceSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is string slot)
        {
            _craftingSearchText[slot] = textBox.Text;
            RefreshCraftingPieceCombos();
        }
    }

    private void CraftingPieceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCraftingCombos || sender is not ComboBox comboBox)
        {
            return;
        }

        var selected = comboBox.SelectedItem as CraftingPieceNode;
        if (selected == CraftingPieceNode.Empty)
        {
            selected = null;
        }
        else if (selected == null)
        {
            return;
        }

        ApplyCraftingPieceSelection(comboBox, selected);
    }

    private void ApplyCraftingPieceSelection(ComboBox comboBox, CraftingPieceNode? selected)
    {
        if (comboBox.Tag is string slot)
        {
            _craftingSearchText[slot] = string.Empty;
        }

        if (comboBox == CraftingBladeCombo)
        {
            _craftingBlade = selected;
        }
        else if (comboBox == CraftingGuardCombo)
        {
            _craftingGuard = selected;
        }
        else if (comboBox == CraftingHandleCombo)
        {
            _craftingHandle = selected;
        }
        else if (comboBox == CraftingPommelCombo)
        {
            _craftingPommel = selected;
        }

        SetActiveCraftingPiece(selected);
        RenderCraftingWeaponPreview();
    }

    private void ApplyCraftingWeapon_Click(object sender, RoutedEventArgs e)
    {
        RenderCraftingWeaponPreview();
    }

    private void ClearCraftingWeapon_Click(object sender, RoutedEventArgs e)
    {
        ClearCraftingSelections();
        RenderCraftingWeaponPreview();
    }

    private void ClearCraftingSelections()
    {
        _craftingBlade = null;
        _craftingGuard = null;
        _craftingHandle = null;
        _craftingPommel = null;
        SetActiveCraftingPiece(null);

        if (CraftingBladeCombo != null)
        {
            CraftingBladeCombo.SelectedItem = CraftingPieceNode.Empty;
            CraftingGuardCombo.SelectedItem = CraftingPieceNode.Empty;
            CraftingHandleCombo.SelectedItem = CraftingPieceNode.Empty;
            CraftingPommelCombo.SelectedItem = CraftingPieceNode.Empty;
        }
    }

    private void SetActiveCraftingPiece(CraftingPieceNode? piece)
    {
        _activeCraftingPiece = piece;
        UpdateCraftingEditorVisibility();
        _isUpdatingCraftingEditor = true;
        CraftingActivePieceText.Text = piece?.Id ?? "Select a piece to edit.";
        CraftingPieceOffsetBox.Text = piece == null ? string.Empty : FormatCraftingFloat(piece.PieceOffset);
        CraftingPreviousOffsetBox.Text = piece == null ? string.Empty : FormatCraftingFloat(piece.PreviousPieceOffset);
        CraftingNextOffsetBox.Text = piece == null ? string.Empty : FormatCraftingFloat(piece.NextPieceOffset);
        CraftingScaleBox.Text = piece == null ? string.Empty : piece.Scale.ToString(CultureInfo.InvariantCulture);
        _isUpdatingCraftingEditor = false;
        UpdateCraftingXmlOutput();
    }

    private void UpdateCraftingEditorVisibility()
    {
        CraftingEditorPanel.Visibility = _activeCraftingPiece != null && CraftingTab.IsSelected
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CraftingStepRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radioButton &&
            TryFloat(radioButton.Tag as string, out var step))
        {
            _craftingStepSize = step;
        }
    }

    private void CraftingOffsetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitCraftingOffsetBoxes(sender as TextBox);
    }

    private void CraftingOffsetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCraftingOffsetBoxes(sender as TextBox);
            e.Handled = true;
        }
    }

    private void CraftingOffsetStep_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCraftingPiece == null ||
            sender is not Button button ||
            button.Tag is not string tag)
        {
            return;
        }

        var parts = tag.Split(',');
        if (parts.Length != 2 || !TryFloat(parts[1], out var direction))
        {
            return;
        }

        var delta = direction * _craftingStepSize;
        switch (parts[0])
        {
            case "piece_offset":
                _activeCraftingPiece.PieceOffset += delta;
                break;
            case "previous_piece_offset":
                _activeCraftingPiece.PreviousPieceOffset += delta;
                break;
            case "next_piece_offset":
                _activeCraftingPiece.NextPieceOffset += delta;
                break;
        }

        SetActiveCraftingPiece(_activeCraftingPiece);
        RenderCraftingWeaponPreview();
    }

    private void CommitCraftingOffsetBoxes(TextBox? sourceBox = null)
    {
        if (_isUpdatingCraftingEditor || _activeCraftingPiece == null)
        {
            return;
        }

        if (TryFloat(CraftingPieceOffsetBox.Text, out var pieceOffset))
        {
            _activeCraftingPiece.PieceOffset = pieceOffset;
        }

        if (TryFloat(CraftingPreviousOffsetBox.Text, out var previousOffset))
        {
            _activeCraftingPiece.PreviousPieceOffset = previousOffset;
        }

        if (TryFloat(CraftingNextOffsetBox.Text, out var nextOffset))
        {
            _activeCraftingPiece.NextPieceOffset = nextOffset;
        }

        if (int.TryParse(CraftingScaleBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) &&
            scale > 0)
        {
            _activeCraftingPiece.Scale = scale;
        }

        SetActiveCraftingPiece(_activeCraftingPiece);
        RenderCraftingWeaponPreview();
    }

    private void UpdateCraftingXmlOutput()
    {
        if (_activeCraftingPiece == null)
        {
            CraftingXmlOutputBox.Text = string.Empty;
            return;
        }

        CraftingXmlOutputBox.Text =
            $"<!-- {_activeCraftingPiece.Id} -->{Environment.NewLine}" +
            $"<BuildData{Environment.NewLine}" +
            $"    piece_offset=\"{FormatCraftingFloat(_activeCraftingPiece.PieceOffset)}\"{Environment.NewLine}" +
            $"    previous_piece_offset=\"{FormatCraftingFloat(_activeCraftingPiece.PreviousPieceOffset)}\"{Environment.NewLine}" +
            $"    next_piece_offset=\"{FormatCraftingFloat(_activeCraftingPiece.NextPieceOffset)}\" />";
    }

    private void CopyCraftingBuildData_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CraftingXmlOutputBox.Text))
        {
            Clipboard.SetText(CraftingXmlOutputBox.Text);
        }
    }

    private void ExportCraftingOffsets_Click(object sender, RoutedEventArgs e)
    {
        var dirty = _craftingPieces.Where(piece => piece.IsDirty).ToArray();
        if (dirty.Length == 0)
        {
            CraftingSummaryText.Text = "No tuned crafting offsets to export.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export tuned crafting offsets",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "crafting_offset_tuning.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var payload = dirty.Select(piece => new
        {
            id = piece.Id,
            piece_type = piece.PieceType,
            mesh = piece.MeshName,
            original = new
            {
                piece_offset = piece.OriginalPieceOffset,
                previous_piece_offset = piece.OriginalPreviousPieceOffset,
                next_piece_offset = piece.OriginalNextPieceOffset,
                scale_factor = piece.OriginalScale
            },
            tuned = new
            {
                piece_offset = piece.PieceOffset,
                previous_piece_offset = piece.PreviousPieceOffset,
                next_piece_offset = piece.NextPieceOffset,
                scale_factor = piece.Scale
            }
        });

        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        CraftingSummaryText.Text = $"Exported {dirty.Length} tuned piece(s).";
    }

    private static string FormatCraftingFloat(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void RenderCraftingWeaponPreview()
    {
        _previewWorkspace = 2;
        SuspendTroopPose();
        _assetModel.Children.Clear();

        var pieces = new[]
        {
            ("Blade", _craftingBlade),
            ("Guard", _craftingGuard),
            ("Handle", _craftingHandle),
            ("Pommel", _craftingPommel)
        };

        var selectedPieces = pieces.Where(item => item.Item2 != null).ToArray();
        if (selectedPieces.Length == 0)
        {
            SelectedAssetTitle.Text = "Crafting weapon";
            SelectedAssetSubtitle.Text = "Select weapon pieces to preview.";
            CraftingWeaponLengthText.Text = "Weapon length: -";
            return;
        }

        var pivots = CalculateCraftingPivots(out var weaponLength);
        var assembledWeapon = new Model3DGroup();
        var rendered = 0;
        var missing = 0;

        foreach (var (slot, piece) in selectedPieces)
        {
            if (piece == null ||
                !pivots.TryGetValue(slot, out var pivot) ||
                float.IsNaN(pivot))
            {
                continue;
            }

            var meshAsset = FindMeshOption(piece.MeshName);
            if (meshAsset == null)
            {
                missing++;
                continue;
            }

            try
            {
                var model = LoadMeshModel(meshAsset, out _, MeshRenderMode.CraftingPiece, piece.VisualLength);
                var transforms = new Transform3DGroup();
                if (piece.Scale != 100)
                {
                    transforms.Children.Add(new ScaleTransform3D(piece.ScaleFactor, piece.ScaleFactor, piece.ScaleFactor));
                }

                transforms.Children.Add(new TranslateTransform3D(0, pivot * CraftingPieceUnitScale, 0));
                model.Transform = transforms;
                assembledWeapon.Children.Add(model);
                rendered++;
            }
            catch
            {
                missing++;
            }
        }

        if (rendered > 0)
        {
            assembledWeapon.Transform = CreateFitTransform(assembledWeapon.Bounds);
            _assetModel.Children.Add(assembledWeapon);
        }

        CraftingWeaponLengthText.Text = float.IsNaN(weaponLength)
            ? "Weapon length: -"
            : $"Weapon length: {weaponLength:0.##}";
        SelectedAssetTitle.Text = "Crafting weapon";
        SelectedAssetSubtitle.Text = rendered == 0
            ? "No selected crafting piece meshes could be rendered from loaded TPAC assets."
            : $"Rendered {rendered} piece(s)" + (missing == 0 ? "." : $"; {missing} mesh match(es) missing.");
    }

    private Dictionary<string, float> CalculateCraftingPivots(out float weaponLength)
    {
        var selectedByType = new Dictionary<string, CraftingPieceNode?>
        {
            ["Blade"] = _craftingBlade,
            ["Guard"] = _craftingGuard,
            ["Handle"] = _craftingHandle,
            ["Pommel"] = _craftingPommel
        };

        return CalculateCraftingPivots(selectedByType, GetCraftingBuildOrder(), out weaponLength);
    }

    private static Dictionary<string, float> CalculateCraftingPivots(
        IReadOnlyDictionary<string, CraftingPieceNode?> selectedByType,
        (string Type, int Order)[] buildOrder,
        out float weaponLength)
    {
        var pivots = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var bottom = 0f;
        var top = 0f;

        foreach (var (type, order) in buildOrder)
        {
            var piece = selectedByType.TryGetValue(type, out var selected) ? selected : null;
            if (piece == null)
            {
                pivots[type] = float.NaN;
                continue;
            }

            var sign = Math.Sign(order);
            if (sign == 0)
            {
                top += piece.ScaledPieceOffset;
                bottom -= piece.ScaledPieceOffset;
            }
            else if (sign < 0)
            {
                bottom += piece.ScaledDistanceToNextPiece + piece.ScaledPieceOffset - piece.ScaledNextPieceOffset;
            }
            else
            {
                top += piece.ScaledDistanceToPreviousPiece + piece.ScaledPieceOffset - piece.ScaledPreviousPieceOffset;
            }

            pivots[type] = sign * (sign < 0 ? bottom : top) + (sign == 0 ? piece.ScaledPieceOffset : 0f);

            if (sign == 0)
            {
                bottom += piece.ScaledDistanceToPreviousPiece - piece.ScaledPreviousPieceOffset;
                top += piece.ScaledDistanceToNextPiece - piece.ScaledNextPieceOffset;
            }
            else if (sign < 0)
            {
                bottom += piece.ScaledDistanceToPreviousPiece - piece.ScaledPreviousPieceOffset;
            }
            else
            {
                top += piece.ScaledDistanceToNextPiece - piece.ScaledNextPieceOffset;
            }
        }

        weaponLength = float.NaN;
        if (selectedByType.TryGetValue("Blade", out var blade) && blade != null &&
            pivots.TryGetValue("Blade", out var bladePivot) && !float.IsNaN(bladePivot))
        {
            var maxPieceReach = selectedByType.Values
                .Where(piece => piece != null)
                .Select(piece => piece!.ScaledDistanceToNextPiece + piece.ScaledPieceOffset)
                .DefaultIfEmpty(0)
                .Max();
            weaponLength = MathF.Max(bladePivot + blade.ScaledDistanceToNextPiece, maxPieceReach);
        }

        return pivots;
    }

    private (string Type, int Order)[] GetCraftingBuildOrder()
    {
        if (CraftingTemplateCombo.SelectedItem is string templateId &&
            !templateId.Equals("(All pieces)", StringComparison.OrdinalIgnoreCase))
        {
            if (_craftingTemplateBuildOrders.TryGetValue(templateId, out var loaded))
            {
                return loaded;
            }

            if (BundledCraftingBuildOrders.TryGetValue(templateId, out var bundled))
            {
                return bundled;
            }
        }

        return DefaultCraftingBuildOrder;
    }

    private static CraftingPieceNode? ParseCraftingPiece(XElement element)
    {
        var id = GetAttributeValue(element, "id");
        var pieceType = GetAttributeValue(element, "piece_type");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pieceType))
        {
            return null;
        }

        var lengthParsed = TryFloat(GetAttributeValue(element, "length"), out var length);
        if (lengthParsed)
        {
            return CreateCraftingPiece(element, id, pieceType, length, length / 2f, length / 2f);
        }

        TryFloat(GetAttributeValue(element, "distance_to_next_piece"), out var distanceToNext);
        TryFloat(GetAttributeValue(element, "distance_to_previous_piece"), out var distanceToPrevious);
        return CreateCraftingPiece(element, id, pieceType, distanceToNext + distanceToPrevious, distanceToNext, distanceToPrevious);
    }

    private static CraftingPieceNode CreateCraftingPiece(
        XElement element,
        string id,
        string pieceType,
        float length,
        float distanceToNext,
        float distanceToPrevious)
    {
        var buildData = element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("BuildData", StringComparison.OrdinalIgnoreCase));
        return new CraftingPieceNode
        {
            Id = id,
            PieceType = pieceType,
            MeshName = GetAttributeValue(element, "mesh") ?? id,
            Length = length,
            DistanceToNextPiece = distanceToNext,
            DistanceToPreviousPiece = distanceToPrevious,
            PieceOffset = ReadBuildDataFloat(buildData, "piece_offset"),
            PreviousPieceOffset = ReadBuildDataFloat(buildData, "previous_piece_offset"),
            NextPieceOffset = ReadBuildDataFloat(buildData, "next_piece_offset"),
            OriginalPieceOffset = ReadBuildDataFloat(buildData, "piece_offset"),
            OriginalPreviousPieceOffset = ReadBuildDataFloat(buildData, "previous_piece_offset"),
            OriginalNextPieceOffset = ReadBuildDataFloat(buildData, "next_piece_offset")
        };
    }

    private static float ReadBuildDataFloat(XElement? buildData, string attributeName)
    {
        TryFloat(buildData == null ? null : GetAttributeValue(buildData, attributeName), out var value);
        return value;
    }

    private static bool TryFloat(string? value, out float parsed)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private void LoadEquipmentDefinitions(string troopPath)
    {
        _equipmentItems.Clear();
        _equipmentCraftingPieces.Clear();
        _equipmentTemplates.Clear();
        _equipmentPieceDefinitions.Clear();
        _equipmentPreviewModels.Clear();
        _equipmentAssetFolders.Clear();
        _equipmentDependencyMeshes.Clear();
        _equipmentDependenciesIndexed = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in new[] { AssetPathBox.Text, troopPath })
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            var directory = Directory.Exists(sourcePath)
                ? new DirectoryInfo(sourcePath)
                : new FileInfo(sourcePath).Directory;
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SubModule.xml")))
            {
                directory = directory.Parent;
            }

            if (directory != null)
            {
                IndexEquipmentModule(directory.FullName, visited);
            }
        }
        _weaponOptions.Clear();
        _weaponOptions.AddRange(_equipmentItems.Where(pair => IsWeaponItem(pair.Value))
            .Select(pair => new MeshAssetNode { DisplayName = "Item." + pair.Key, Kind = "Item",
                Status = CleanXmlDisplayText(GetAttributeValue(pair.Value, "name")) ?? pair.Key })
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase));
        var currentLoadout = ReadEquipmentBoxes();
        FilterEquipmentCombo(Item0Combo, Item0Combo.Text);
        FilterEquipmentCombo(Item1Combo, Item1Combo.Text);
        SetEquipmentBoxes(currentLoadout);
    }

    private void IndexEquipmentModule(string modulePath, HashSet<string> visited)
    {
        var manifestPath = Path.Combine(modulePath, "SubModule.xml");
        if (!File.Exists(manifestPath) || !visited.Add(modulePath))
        {
            return;
        }

        var manifest = XDocument.Load(manifestPath);
        var modulesPath = Directory.GetParent(modulePath)!.FullName;
        foreach (var dependency in manifest.Descendants()
                     .Where(element => element.Name.LocalName.Equals("DependedModule", StringComparison.OrdinalIgnoreCase)))
        {
            var id = GetAttributeValue(dependency, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                IndexEquipmentModule(Path.Combine(modulesPath, id), visited);
            }
        }

        foreach (var entry in manifest.Descendants()
                     .Where(element => element.Name.LocalName.Equals("XmlName", StringComparison.OrdinalIgnoreCase)))
        {
            var kind = GetAttributeValue(entry, "id");
            if (!string.Equals(kind, "Items", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "CraftingPieces", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "CraftingTemplates", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = GetAttributeValue(entry, "path");
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var dataPath = Path.Combine(modulePath, "ModuleData", relativePath);
            var files = Directory.Exists(dataPath)
                ? Directory.EnumerateFiles(dataPath, "*.xml", SearchOption.AllDirectories).OrderBy(path => path)
                : File.Exists(dataPath + ".xml") ? new[] { dataPath + ".xml" }.AsEnumerable() : [];
            foreach (var file in files)
            {
                var document = XDocument.Load(file);
                foreach (var element in document.Descendants())
                {
                    if (element.Name.LocalName is "Item" or "CraftedItem")
                    {
                        var id = GetAttributeValue(element, "id");
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            _equipmentItems[id] = element;
                        }
                    }
                    else if (element.Name.LocalName.Equals("CraftingPiece", StringComparison.OrdinalIgnoreCase) &&
                             ParseCraftingPiece(element) is { } piece)
                    {
                        _equipmentCraftingPieces[piece.Id] = piece;
                        _equipmentPieceDefinitions[piece.Id] = element;
                    }
                    else if (element.Name.LocalName == "CraftingTemplate" && GetAttributeValue(element, "id") is { } templateId)
                    {
                        _equipmentTemplates[templateId] = element;
                    }
                }
            }
        }
        _equipmentAssetFolders.Add(Path.Combine(modulePath, "AssetPackages"));
        _equipmentAssetFolders.Add(Path.Combine(modulePath, "EmAssetPackages"));
    }

    private void LoadTroopXml(string filePath)
    {
        TroopXmlPathBox.Text = filePath;
        _troops.Clear();

        try
        {
            LoadEquipmentDefinitions(filePath);
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
            UpdateTroopXmlExport();
            RefreshTroopPoseLibrary();

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
            var canonicalSlot = slotValue?.ToLowerInvariant() switch
            {
                "head" => "Helmet",
                "gloves" => "Arm",
                _ => slotValue
            };
            if (!slot.Equals(canonicalSlot, StringComparison.OrdinalIgnoreCase))
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
        var tokenEnd = cleaned.IndexOf('}');
        if (cleaned.StartsWith("{=", StringComparison.Ordinal) && tokenEnd >= 0)
        {
            cleaned = cleaned[(tokenEnd + 1)..];
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
        AddSlotValue(loadout, "Helmet", GetComboValue(HelmetCombo));
        AddSlotValue(loadout, "Cape", GetComboValue(CapeCombo));
        AddSlotValue(loadout, "Body", GetComboValue(BodyCombo));
        AddSlotValue(loadout, "Arm", GetComboValue(ArmCombo));
        AddSlotValue(loadout, "Leg", GetComboValue(LegCombo));
        AddSlotValue(loadout, "Item0", GetComboValue(Item0Combo));
        AddSlotValue(loadout, "Item1", GetComboValue(Item1Combo));
        return loadout;
    }

    private void CustomLoadout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingComboFilters) UpdateTroopXmlExport();
    }

    private XElement CreateEquipmentRosterXml(IReadOnlyDictionary<string, string> loadout)
    {
        var roster = new XElement("EquipmentRoster");
        foreach (var slot in EquipmentSlots)
        {
            if (!loadout.TryGetValue(slot, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            var reference = ResolveEquipmentItemReference(slot, value.Trim());
            roster.Add(new XElement("equipment", new XAttribute("slot", slot == "Helmet" ? "Head" : slot),
                new XAttribute("id", reference)));
        }
        return roster;
    }

    private string ResolveEquipmentItemReference(string slot, string value)
    {
        var explicitReference = value.StartsWith("Item.", StringComparison.OrdinalIgnoreCase);
        var id = explicitReference ? value[5..].Trim() : value;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"{slot}: enter an item ID after Item.");
        if (_equipmentItems.TryGetValue(id, out var item)) return "Item." + GetAttributeValue(item, "id");
        if (explicitReference) return "Item." + id;

        var itemType = slot switch
        {
            "Helmet" => "HeadArmor", "Cape" => "Cape", "Body" => "BodyArmor",
            "Arm" => "HandArmor", "Leg" => "LegArmor", _ => ""
        };
        var matches = _equipmentItems.Where(pair =>
            string.Equals(GetAttributeValue(pair.Value, "mesh"), value, StringComparison.OrdinalIgnoreCase) &&
            (slot is "Item0" or "Item1" ? IsWeaponItem(pair.Value) :
                GetAttributeValue(pair.Value, "type") is not { Length: > 0 } type || type.Equals(itemType, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => GetAttributeValue(pair.Value, "id") ?? pair.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length == 1) return "Item." + matches[0];
        if (matches.Length > 1)
            throw new InvalidOperationException($"{slot}: mesh '{value}' belongs to multiple items. Enter the exact Item.id.");
        if (_meshOptions.Any(mesh => mesh.DisplayName.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{slot}: no item ID found for mesh '{value}'. Enter its Item.id.");
        return "Item." + id;
    }

    private void UpdateTroopXmlExport()
    {
        if (TroopXmlExportPanel == null) return;
        TroopXmlExportPanel.Visibility = WorkspaceTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var loadout = ReadEquipmentBoxes();
            TroopXmlOutputBox.Text = CreateEquipmentRosterXml(loadout).ToString();
            CopyTroopXmlButton.IsEnabled = ExportTroopXmlButton.IsEnabled = loadout.Count > 0;
            TroopXmlStatusText.Text = loadout.Count == 0 ? "No equipment selected." : "";
        }
        catch (InvalidOperationException ex)
        {
            TroopXmlOutputBox.Text = "";
            CopyTroopXmlButton.IsEnabled = ExportTroopXmlButton.IsEnabled = false;
            TroopXmlStatusText.Text = ex.Message;
        }
    }

    private void CopyTroopXml_Click(object sender, RoutedEventArgs e)
    {
        UpdateTroopXmlExport();
        if (!CopyTroopXmlButton.IsEnabled) return;
        Clipboard.SetText(TroopXmlOutputBox.Text);
        TroopXmlStatusText.Text = "XML copied.";
    }

    private void ExportTroopXml_Click(object sender, RoutedEventArgs e)
    {
        UpdateTroopXmlExport();
        if (!ExportTroopXmlButton.IsEnabled) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export custom equipment roster", Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            FileName = "equipment_roster.xml", DefaultExt = ".xml", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, TroopXmlOutputBox.Text);
            TroopXmlStatusText.Text = "Equipment roster exported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TroopXmlStatusText.Text = $"Could not export XML: {ex.Message}";
        }
    }

    private void SetEquipmentBoxes(IReadOnlyDictionary<string, string> loadout)
    {
        SetComboValue(HelmetCombo, GetSlotValue(loadout, "Helmet"));
        SetComboValue(CapeCombo, GetSlotValue(loadout, "Cape"));
        SetComboValue(BodyCombo, GetSlotValue(loadout, "Body"));
        SetComboValue(ArmCombo, GetSlotValue(loadout, "Arm"));
        SetComboValue(LegCombo, GetSlotValue(loadout, "Leg"));
        SetComboValue(Item0Combo, GetSlotValue(loadout, "Item0"));
        SetComboValue(Item1Combo, GetSlotValue(loadout, "Item1"));
        UpdateTroopXmlExport();
    }

    private IEnumerable<ComboBox> GetEquipmentCombos()
    {
        yield return HelmetCombo;
        yield return CapeCombo;
        yield return BodyCombo;
        yield return ArmCombo;
        yield return LegCombo;
        yield return Item0Combo;
        yield return Item1Combo;
    }

    private static string GetComboValue(ComboBox comboBox)
    {
        return comboBox.SelectedItem is MeshAssetNode mesh
            ? mesh.DisplayName
            : comboBox.Text;
    }

    private static void SetComboValue(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = null;
        comboBox.Text = value;
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
        _previewWorkspace = 1;
        StopPosePlayback();
        if (_troopPreviewLoadout.Count != loadout.Count || loadout.Any(pair =>
            !_troopPreviewLoadout.TryGetValue(pair.Key, out var previous) || !previous.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
            _equipmentPreviewModels.Clear();
        _troopPreviewTitle = title;
        _troopPreviewLoadout = new Dictionary<string, string>(loadout, StringComparer.OrdinalIgnoreCase);
        _troopHeldWeapon = _troopHeldShield = false;
        _troopHeldCategory = "";
        _usedHolsterGroups.Clear();
        _heldEquipmentModels.Clear();
        _assetModel.Children.Clear();
        var assembledLoadout = new Model3DGroup();

        if (loadout.Count == 0)
        {
            RefreshEquipmentStateControls(loadout);
            SelectedAssetTitle.Text = title;
            SelectedAssetSubtitle.Text = "No equipment selected.";
            RefreshTroopPoseAvailability();
            return;
        }

        var renderedSlots = new List<string>();
        var missingSlots = new List<string>();
        PrepareEquipmentHands(loadout);
        foreach (var slot in EquipmentSlots)
        {
            if (!loadout.TryGetValue(slot, out var equipmentName))
            {
                continue;
            }

            try
            {
                var model = slot is "Item0" or "Item1"
                    ? LoadHeldEquipmentModel(equipmentName, slot) : LoadEquipmentModel(equipmentName);
                assembledLoadout.Children.Add(model);
                renderedSlots.Add(slot);
            }
            catch (Exception ex)
            {
                missingSlots.Add($"{slot}: {ex.Message}");
            }
        }

        if (assembledLoadout.Children.Count > 0)
        {
            // Mesh loading already converts the axes; preserve the equipment's shared origin.
            var armourBounds = assembledLoadout.Children.Where(model => !_heldEquipmentModels.Contains(model))
                .Aggregate(Rect3D.Empty, (bounds, model) => { bounds.Union(model.Bounds); return bounds; });
            assembledLoadout.Transform = CreateFitTransform(armourBounds.IsEmpty ? assembledLoadout.Bounds : armourBounds, AssetPreviewSize);
            _assetModel.Children.Add(assembledLoadout);
        }

        SelectedAssetTitle.Text = title;
        SelectedAssetSubtitle.Text = renderedSlots.Count == 0
            ? "No selected loadout meshes could be rendered. " + string.Join("; ", missingSlots)
            : missingSlots.Count == 0 ? "" : $"Unavailable equipment: {string.Join("; ", missingSlots)}";
        RefreshTroopPoseAvailability();
        SelectEquipmentHoldingPose();
        RefreshEquipmentStateControls(loadout);
    }

    private Model3D LoadEquipmentModel(string equipmentName)
        => CopyEquipmentPreview(equipmentName, false, () => LoadEquipmentModelSource(equipmentName));

    private Model3D LoadEquipmentModelSource(string equipmentName)
    {
        var itemId = equipmentName.StartsWith("Item.", StringComparison.OrdinalIgnoreCase)
            ? equipmentName[5..]
            : equipmentName;
        if (_equipmentItems.TryGetValue(itemId, out var item))
        {
            if (item.Name.LocalName.Equals("CraftedItem", StringComparison.OrdinalIgnoreCase))
            {
                return LoadCraftedEquipmentModel(item);
            }

            var meshName = GetAttributeValue(item, "mesh");
            if (string.IsNullOrWhiteSpace(meshName))
            {
                throw new InvalidOperationException($"Item '{itemId}' has no mesh.");
            }

            itemId = meshName;
        }
        else if (equipmentName.StartsWith("Item.", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Item '{itemId}' was not found in module XML.");
        }

        var meshAsset = FindEquipmentMesh(itemId)
            ?? throw new InvalidOperationException($"Mesh '{itemId}' is not in the loaded TPAC assets.");
        return LoadMeshModel(meshAsset, out _, MeshRenderMode.Raw);
    }

    private Model3DGroup LoadCraftedEquipmentModel(XElement item, bool sheathed = false)
    {
        var pieces = new Dictionary<string, CraftingPieceNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in item.Descendants().Where(element => element.Name.LocalName == "Piece"))
        {
            var id = GetAttributeValue(reference, "id") ?? string.Empty;
            if (!_equipmentCraftingPieces.TryGetValue(id, out var definition))
            {
                throw new InvalidOperationException($"Crafting piece '{id}' was not found in module XML.");
            }

            var piece = new CraftingPieceNode
            {
                MeshName = definition.MeshName,
                Length = definition.Length,
                DistanceToNextPiece = definition.DistanceToNextPiece,
                DistanceToPreviousPiece = definition.DistanceToPreviousPiece,
                PieceOffset = definition.PieceOffset,
                PreviousPieceOffset = definition.PreviousPieceOffset,
                NextPieceOffset = definition.NextPieceOffset,
                Scale = int.TryParse(GetAttributeValue(reference, "scale_factor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) ? scale : 100
            };
            pieces[GetAttributeValue(reference, "Type") ?? definition.PieceType] = piece;
        }

        var template = GetAttributeValue(item, "crafting_template") ?? string.Empty;
        _equipmentTemplates.TryGetValue(template, out var templateDefinition);
        var hiddenTypes = (GetAttributeValue(templateDefinition ?? item, "hidden_piece_types_on_holster") ?? "").Split(':');
        var useWeapon = GetAttributeValue(templateDefinition ?? item, "use_weapon_as_holster_mesh") == "true";
        var order = _craftingTemplateBuildOrders.TryGetValue(template, out var loadedOrder) ? loadedOrder
            : BundledCraftingBuildOrders.TryGetValue(template, out var bundledOrder) ? bundledOrder
            : throw new InvalidOperationException($"Crafting template '{template}' has no build order.");
        var pivots = CalculateCraftingPivots(pieces, order, out _);
        var weapon = new Model3DGroup();
        foreach (var (type, piece) in pieces)
        {
            if (piece == null || !pivots.TryGetValue(type, out var pivot) || float.IsNaN(pivot))
            {
                continue;
            }

            var meshName = piece.MeshName;
            var meshLength = piece.VisualLength;
            var meshScale = piece.ScaleFactor;
            if (sheathed && !useWeapon)
            {
                var reference = item.Descendants().First(element => element.Name.LocalName == "Piece" &&
                    (GetAttributeValue(element, "Type") ?? "") == type);
                _equipmentPieceDefinitions.TryGetValue(GetAttributeValue(reference, "id") ?? "", out var definition);
                var holsterData = definition?.DescendantsAndSelf().FirstOrDefault(element => !string.IsNullOrWhiteSpace(GetAttributeValue(element, "holster_mesh")));
                var holsterMesh = holsterData == null ? null : GetAttributeValue(holsterData, "holster_mesh");
                if (!string.IsNullOrWhiteSpace(holsterMesh))
                {
                    meshName = holsterMesh;
                    if (TryFloat(GetAttributeValue(holsterData!, "holster_mesh_length"), out var authoredLength) && authoredLength > 0)
                        meshLength = authoredLength;
                    var scaleType = GetAttributeValue(templateDefinition ?? item, "piece_type_to_scale_holster_with") ?? type;
                    if (pieces.TryGetValue(scaleType, out var scalePiece) && scalePiece != null) meshScale = scalePiece.ScaleFactor;
                }
                // Keep a bare blade when its author supplied no scabbard mesh.
                else if (hiddenTypes.Contains(type) && type != "Blade") continue;
            }
            var asset = FindEquipmentMesh(meshName)
                ?? throw new InvalidOperationException($"Mesh '{meshName}' is not in the loaded TPAC assets.");
            var model = LoadMeshModel(asset, out _, MeshRenderMode.CraftingPiece, meshLength);
            var transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(meshScale, meshScale, meshScale));
            transform.Children.Add(new TranslateTransform3D(0, pivot * CraftingPieceUnitScale, 0));
            model.Transform = transform;
            weapon.Children.Add(model);
        }

        // Convert the crafting preview's centimetre scale to the armour's metre scale.
        var equipmentScale = 0.01 / CraftingPieceUnitScale;
        weapon.Transform = new ScaleTransform3D(equipmentScale, equipmentScale, equipmentScale);
        return weapon;
    }

    private MeshAssetNode? FindMeshOption(string meshName)
    {
        var normalizedMeshName = NormalizeMeshDisplayName(meshName);
        return _meshOptions.FirstOrDefault(option =>
            option.DisplayName.Equals(meshName, StringComparison.OrdinalIgnoreCase) ||
            option.DisplayName.Equals(normalizedMeshName, StringComparison.OrdinalIgnoreCase));
    }

    private void PopulatePackageAssets(TpacPackageNode package, FileInfo info)
    {
        try
        {
            var assetPackage = new AssetPackage(info.FullName, loadHeaderNow: true, loadDataNow: false);
            IndexPackageLookups(assetPackage, info.FullName);
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

    private void IndexPackageLookups(AssetPackage assetPackage, string packagePath)
    {
        foreach (var material in assetPackage.Items.OfType<TpacTool.Lib.Material>())
        {
            _materialPackagePaths.TryAdd(material.Guid, packagePath);
        }

        foreach (var texture in assetPackage.Items.OfType<Texture>())
        {
            if (texture.TexturePixels != null && _texturePixelGuids.Add(texture.Guid)) _texturePackagePaths[texture.Guid] = packagePath;
            else _texturePackagePaths.TryAdd(texture.Guid, packagePath);
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

            var meshes = GetHighestDetailMeshes(metamesh);
            var canRender = meshes.Any(mesh => mesh.VertexStream != null || mesh.EditData != null);
            var vertexCount = meshes.Sum(mesh => mesh.VertexCount);
            var faceCount = meshes.Sum(mesh => mesh.FaceCount);
            yield return new MeshAssetNode
            {
                DisplayName = NormalizeMeshDisplayName(metamesh.Name),
                PackageName = packageName,
                SourcePath = packageFile.FullName,
                ByteSize = packageFile.Length,
                Kind = "Mesh",
                GroupKey = NormalizeMeshDisplayName(metamesh.Name),
                MetameshGuid = metamesh.Guid,
                MeshGuid = meshes[0].Guid,
                VertexCount = vertexCount,
                FaceCount = faceCount,
                CanRender = canRender,
                Status = canRender
                    ? $"Parsed - {meshes.Length} part(s) - {vertexCount:n0} vertices - {faceCount:n0} faces"
                    : $"Parsed metadata - {vertexCount:n0} vertices - {faceCount:n0} faces"
            };

            yield break;
        }

        yield break;
    }

    private static Mesh[] GetHighestDetailMeshes(Metamesh metamesh)
    {
        var lod = metamesh.Meshes.Min(mesh => mesh.Lod);
        return metamesh.Meshes.Where(mesh => mesh.Lod == lod).ToArray();
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
            RenderSelectedAsset(asset);
        }
    }

    private void RenderSelectedAsset(MeshAssetNode asset)
    {
        _previewWorkspace = 0;
        SuspendTroopPose();
        SelectedAssetTitle.Text = asset.DisplayName;
        if (asset.Kind != "Mesh" || asset.MeshGuid == Guid.Empty)
        {
            _assetModel.Children.Clear();
            SelectedAssetSubtitle.Text = "No renderable mesh selected.";
            return;
        }

        try
        {
            var model = LoadMeshModel(asset, out _);

            _assetModel.Children.Clear();
            _assetModel.Children.Add(model);

            SelectedAssetSubtitle.Text = "";
        }
        catch (Exception ex)
        {
            _assetModel.Children.Clear();
            SelectedAssetSubtitle.Text = $"Unable to render this item: {ex.Message}";
        }
    }

    private Model3DGroup LoadMeshModel(
        MeshAssetNode asset,
        out int renderedVertices,
        MeshRenderMode renderMode = MeshRenderMode.AssetPreview,
        float targetLength = 0)
    {
        var package = new AssetPackage(asset.SourcePath, loadHeaderNow: true, loadDataNow: false,
            assetTypes: PreviewAssetTypes, assetGuids: new HashSet<Guid> { asset.MetameshGuid });
        var metamesh = package.Items.OfType<Metamesh>().FirstOrDefault(item => item.Guid == asset.MetameshGuid);
        if (metamesh == null || metamesh.Meshes.Count == 0)
        {
            throw new InvalidOperationException("Mesh has no renderable geometry stream.");
        }

        var model = new Model3DGroup();
        renderedVertices = 0;
        foreach (var mesh in GetHighestDetailMeshes(metamesh))
        {
            if (mesh.VertexStream == null && mesh.EditData == null)
            {
                continue;
            }

            var materialResult = TryCreateTpacMaterial(package, metamesh, mesh, mesh.Name);
            var part = mesh.VertexStream != null
                ? CreateTpacMeshModel(mesh.VertexStream.Data, asset.DisplayName, materialResult.Material, MeshRenderMode.Raw, 0, out var partVertices)
                : CreateTpacEditMeshModel(mesh.EditData!.Data, asset.DisplayName, materialResult.Material, MeshRenderMode.Raw, 0, out partVertices);
            model.Children.Add(part);
            renderedVertices += partVertices;
        }

        if (model.Children.Count == 0)
        {
            throw new InvalidOperationException("Mesh has no renderable geometry stream.");
        }

        if (renderMode != MeshRenderMode.Raw)
        {
            // Fit all material and cloth parts together without changing their shared origin.
            var geometries = model.Children.Cast<GeometryModel3D>().Select(part => (MeshGeometry3D)part.Geometry).ToArray();
            var positions = geometries.SelectMany(geometry => geometry.Positions).ToArray();
            var transform = CreateMeshCoordinateTransform(positions, renderMode, targetLength);
            foreach (var geometry in geometries)
            {
                for (var i = 0; i < geometry.Positions.Count; i++)
                {
                    geometry.Positions[i] = transform(geometry.Positions[i]);
                }
            }
        }

        return model;
    }

    private void ResetScene()
    {
        _scene.Children.Clear();
        // Linear light intensities keep fill/rim subordinate to the angled studio key.
        _scene.Children.Add(new AmbientLight(Color.FromScRgb(1, 0.01f, 0.01f, 0.01f)));
        _scene.Children.Add(new DirectionalLight(Color.FromScRgb(1, 0.48f, 0.47f, 0.45f), new Vector3D(-0.9, -0.75, 0.65)));
        _scene.Children.Add(new DirectionalLight(Color.FromScRgb(1, 0.065f, 0.07f, 0.08f), new Vector3D(0.8, -0.3, 0.4)));
        _scene.Children.Add(new DirectionalLight(Color.FromScRgb(1, 0.18f, 0.185f, 0.19f), new Vector3D(0.35, -0.4, -0.9)));
        _renderer.GridModel = CreateGrid(18, 1);
        _scene.Children.Add(_renderer.GridModel);
        _scene.Children.Add(_assetModel);
        UpdateCamera();
    }

    private void RenderEmptyPreview()
    {
        _previewWorkspace = -1;
        SuspendTroopPose();
        _assetModel.Children.Clear();
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.05, 0), 1.5, 0.1, 1.5, Color.FromRgb(60, 65, 70)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.75, 0), 0.72, 1.15, 0.46, Color.FromRgb(85, 92, 100)));
        _assetModel.Children.Add(CreateSphere(new Point3D(0, 1.55, 0), 0.28, Color.FromRgb(96, 104, 112)));
        _assetModel.Children.Add(CreateBox(new Point3D(0, 0.02, 0), 1.95, 0.04, 1.95, Color.FromRgb(24, 28, 33)));
    }

    private void RenderAssetPlaceholder(MeshAssetNode asset)
    {
        _previewWorkspace = 0;
        SuspendTroopPose();
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

    private static GeometryModel3D CreateTpacMeshModel(
        VertexStreamData vertexStream,
        string name,
        System.Windows.Media.Media3D.Material? material,
        MeshRenderMode renderMode,
        float targetLength,
        out int renderedVertices)
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
        var transform = CreateMeshCoordinateTransform(positions, renderMode, targetLength);

        var mesh = new MeshGeometry3D();
        var normals = GetVertexNormals(vertexStream, positions.Length);
        for (var i = 0; i < positions.Length; i++)
        {
            mesh.Positions.Add(transform(positions[i]));
            if (normals != null)
            {
                mesh.Normals.Add(normals[i]);
            }

            mesh.TextureCoordinates.Add(i < vertexStream.Uv1.Length
                ? ConvertTexturePoint(vertexStream.Uv1[i])
                : new Point());
        }

        foreach (var index in vertexStream.Indices)
        {
            if (index >= 0 && index < positions.Length)
            {
                mesh.TriangleIndices.Add(index);
            }
        }

        StudioRenderer.RegisterSkinning(mesh, vertexStream);
        return CreateModel(mesh, material, ColorFromName(name));
    }

    private static GeometryModel3D CreateTpacEditMeshModel(
        MeshEditData editData,
        string name,
        System.Windows.Media.Media3D.Material? material,
        MeshRenderMode renderMode,
        float targetLength,
        out int renderedVertices)
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
        var transform = CreateMeshCoordinateTransform(rawPositions, renderMode, targetLength);

        var mesh = new MeshGeometry3D();
        var normals = editData.Vertices
            .Select(vertex => ConvertBannerlordNormal(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z))
            .ToArray();
        var hasNormals = normals.All(normal => normal.LengthSquared > 0);
        for (var i = 0; i < rawPositions.Length; i++)
        {
            mesh.Positions.Add(transform(rawPositions[i]));
            if (hasNormals)
            {
                mesh.Normals.Add(normals[i]);
            }

            mesh.TextureCoordinates.Add(ConvertTexturePoint(editData.Vertices[i].Uv));
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

        return CreateModel(mesh, material, ColorFromName(name));
    }

    private static Point ConvertTexturePoint(System.Numerics.Vector2 uv)
    {
        return new Point(uv.X, uv.Y);
    }

    private static Func<Point3D, Point3D> CreateMeshCoordinateTransform(
        Point3D[] positions,
        MeshRenderMode renderMode,
        float targetLength)
    {
        if (renderMode == MeshRenderMode.Raw)
        {
            return point => point;
        }

        var minX = positions.Min(point => point.X);
        var minY = positions.Min(point => point.Y);
        var minZ = positions.Min(point => point.Z);
        var maxX = positions.Max(point => point.X);
        var maxY = positions.Max(point => point.Y);
        var maxZ = positions.Max(point => point.Z);
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var sizeX = maxX - minX;
        var sizeY = maxY - minY;
        var sizeZ = maxZ - minZ;
        var largestAxis = Math.Max(Math.Max(sizeX, sizeY), sizeZ);

        if (renderMode == MeshRenderMode.CraftingPiece)
        {
            var weaponAxisLength = sizeY > 0 ? sizeY : largestAxis;
            var craftingScale = weaponAxisLength <= 0
                ? CraftingPieceUnitScale
                : Math.Max(targetLength, 1) * CraftingPieceUnitScale / weaponAxisLength;

            return point => new Point3D(
                point.X * craftingScale,
                point.Y * craftingScale,
                point.Z * craftingScale);
        }

        var scale = largestAxis <= 0 ? 1 : renderMode switch
        {
            _ => AssetPreviewSize / largestAxis
        };

        return point => new Point3D(
            (point.X - center.X) * scale,
            (point.Y - center.Y) * scale + 1.2,
            (point.Z - center.Z) * scale);
    }

    private static Transform3D CreateFitTransform(Rect3D bounds, double targetSize = 4.2)
    {
        if (bounds.IsEmpty ||
            Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ) <= 0)
        {
            return Transform3D.Identity;
        }

        var center = new Point3D(
            bounds.X + bounds.SizeX / 2,
            bounds.Y + bounds.SizeY / 2,
            bounds.Z + bounds.SizeZ / 2);
        var largestAxis = Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ);
        var scale = targetSize / largestAxis;

        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z));
        transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
        transform.Children.Add(new TranslateTransform3D(0, 1.35, 0));
        return transform;
    }

    private MaterialLoadResult TryCreateTpacMaterial(
        AssetPackage package,
        Metamesh? metamesh,
        Mesh mesh,
        string fallbackName)
    {
        var materialGuidCount = GetMaterialGuids(metamesh, mesh).Count(guid => guid != Guid.Empty);
        var resolvedMaterialCount = 0;
        var textureRefCount = 0;
        var resolvedTextureCount = 0;
        var decodeFailureCount = 0;

        try
        {
            var materials = package.Items
                .OfType<TpacTool.Lib.Material>()
                .GroupBy(item => item.Guid)
                .ToDictionary(group => group.Key, group => group.First());
            var textures = package.Items
                .OfType<Texture>()
                .GroupBy(item => item.Guid)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var material in GetExternalMaterialCandidates(materials, metamesh, mesh))
            {
                resolvedMaterialCount++;
                textureRefCount += material.Textures.Count;

                foreach (var texture in GetTextureCandidates(material, textures, ResolveTexture))
                {
                    resolvedTextureCount++;
                    ImageBrush? brush;
                    try
                    {
                        brush = CreateTextureBrush(texture);
                    }
                    catch
                    {
                        decodeFailureCount++;
                        continue;
                    }

                    if (brush == null)
                    {
                        continue;
                    }

                    var litMaterial = CreateLitMaterial(material, textures, brush, out var lightingStatus);
                    return new MaterialLoadResult(litMaterial,
                        $"texture: applied {texture.Name} ({texture.Width}x{texture.Height}, {texture.Format}, score {TextureScore(texture)}); {lightingStatus}");
                }
            }

            foreach (var material in GetDependencyMaterialCandidates(package, metamesh, mesh))
            {
                resolvedMaterialCount++;
                textureRefCount += material.Textures.Count;

                foreach (var texture in GetTextureCandidates(material, textures, ResolveTexture))
                {
                    resolvedTextureCount++;
                    ImageBrush? brush;
                    try
                    {
                        brush = CreateTextureBrush(texture);
                    }
                    catch
                    {
                        decodeFailureCount++;
                        continue;
                    }

                    if (brush == null)
                    {
                        continue;
                    }

                    var litMaterial = CreateLitMaterial(material, textures, brush, out var lightingStatus);
                    return new MaterialLoadResult(litMaterial,
                        $"texture: applied dependency {texture.Name} ({texture.Width}x{texture.Height}, {texture.Format}, score {TextureScore(texture)}); {lightingStatus}");
                }
            }

            foreach (var texture in GetDependencyTextureCandidates(package, metamesh, mesh))
            {
                resolvedTextureCount++;
                ImageBrush? brush;
                try
                {
                    brush = CreateTextureBrush(texture);
                }
                catch
                {
                    decodeFailureCount++;
                    continue;
                }

                if (brush == null)
                {
                    continue;
                }

                return new MaterialLoadResult(new DiffuseMaterial(brush),
                    $"texture: applied dependency texture {texture.Name} ({texture.Width}x{texture.Height}, {texture.Format}, score {TextureScore(texture)})");
            }
        }
        catch (Exception ex)
        {
            return new MaterialLoadResult(null, $"texture: resolver failed ({ex.Message})");
        }

        return new MaterialLoadResult(null,
            $"texture: none applied; material refs {materialGuidCount}, resolved materials {resolvedMaterialCount}, texture refs {textureRefCount}, usable textures {resolvedTextureCount}, decode failures {decodeFailureCount}");
    }

    private IEnumerable<TpacTool.Lib.Material> GetExternalMaterialCandidates(
        IReadOnlyDictionary<Guid, TpacTool.Lib.Material> localMaterials,
        Metamesh? metamesh,
        Mesh mesh)
    {
        var seen = new HashSet<Guid>();
        foreach (var guid in GetMaterialGuids(metamesh, mesh))
        {
            if (guid == Guid.Empty || !seen.Add(guid))
            {
                continue;
            }

            var material = ResolveMaterial(localMaterials, guid);
            if (material != null)
            {
                yield return material;
            }
        }
    }

    private static IEnumerable<TpacTool.Lib.Material> GetDependencyMaterialCandidates(
        AssetPackage package,
        Metamesh? metamesh,
        Mesh mesh)
    {
        var targetGuids = GetDependencyTargetGuids(metamesh, mesh);
        return package.Items
            .OfType<TpacTool.Lib.Material>()
            .Where(material => DependsOnAny(material, targetGuids))
            .OrderByDescending(material => DependencyScore(material, targetGuids));
    }

    private static IEnumerable<Texture> GetDependencyTextureCandidates(
        AssetPackage package,
        Metamesh? metamesh,
        Mesh mesh)
    {
        var targetGuids = GetDependencyTargetGuids(metamesh, mesh);
        return package.Items
            .OfType<Texture>()
            .Where(texture => IsUsableTexture(texture) && DependsOnAny(texture, targetGuids))
            .OrderByDescending(texture => TextureScore(texture));
    }

    private static HashSet<Guid> GetDependencyTargetGuids(Metamesh? metamesh, Mesh mesh)
    {
        var guids = new HashSet<Guid> { mesh.Guid };
        if (metamesh != null)
        {
            guids.Add(metamesh.Guid);
        }

        return guids;
    }

    private static bool DependsOnAny(AssetItem asset, IReadOnlySet<Guid> targetGuids)
    {
        return asset.UnknownDependences.Any(dep =>
            targetGuids.Contains(dep.UnknownGuid1) ||
            targetGuids.Contains(dep.UnknownGuid2) ||
            targetGuids.Contains(dep.UnknownGuid3));
    }

    private static int DependencyScore(AssetItem asset, IReadOnlySet<Guid> targetGuids)
    {
        var score = 0;
        foreach (var dep in asset.UnknownDependences)
        {
            if (targetGuids.Contains(dep.UnknownGuid1))
            {
                score += 10;
            }

            if (targetGuids.Contains(dep.UnknownGuid2))
            {
                score += 20;
            }

            if (targetGuids.Contains(dep.UnknownGuid3))
            {
                score += 5;
            }
        }

        return score;
    }

    private static IEnumerable<TpacTool.Lib.Material> GetMaterialCandidates(
        IReadOnlyDictionary<Guid, TpacTool.Lib.Material> materials,
        Metamesh? metamesh,
        Mesh mesh)
    {
        var seen = new HashSet<Guid>();
        foreach (var guid in GetMaterialGuids(metamesh, mesh))
        {
            if (guid == Guid.Empty || !seen.Add(guid))
            {
                continue;
            }

            if (materials.TryGetValue(guid, out var material))
            {
                yield return material;
            }
        }
    }

    private static IEnumerable<Guid> GetMaterialGuids(Metamesh? metamesh, Mesh mesh)
    {
        yield return mesh.Material.Guid;
        yield return mesh.SecondMaterial.Guid;
        yield return metamesh?.Material ?? Guid.Empty;
    }

    private TpacTool.Lib.Material? ResolveMaterial(
        IReadOnlyDictionary<Guid, TpacTool.Lib.Material> localMaterials,
        Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }

        if (localMaterials.TryGetValue(guid, out var localMaterial))
        {
            return localMaterial;
        }

        if (!_materialPackagePaths.TryGetValue(guid, out var packagePath))
        {
            return null;
        }

        var package = LoadLookupPackage(packagePath, guid);
        return package?.Items.OfType<TpacTool.Lib.Material>().FirstOrDefault(item => item.Guid == guid);
    }

    private Texture? ResolveTexture(
        IReadOnlyDictionary<Guid, Texture> localTextures,
        Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }

        if (localTextures.TryGetValue(guid, out var localTexture))
        {
            return localTexture;
        }

        if (!_texturePackagePaths.TryGetValue(guid, out var packagePath))
        {
            return null;
        }

        var package = LoadLookupPackage(packagePath, guid);
        return package?.Items.OfType<Texture>().FirstOrDefault(item => item.Guid == guid);
    }

    private AssetPackage? LoadLookupPackage(string packagePath, Guid assetGuid)
    {
        var key = (Path.GetFullPath(packagePath).ToUpperInvariant(), assetGuid);
        if (_lookupPackageCache.TryGetValue(key, out var package))
        {
            return package;
        }

        if (!File.Exists(packagePath))
        {
            return null;
        }

        package = new AssetPackage(packagePath, loadHeaderNow: true, loadDataNow: false,
            assetTypes: PreviewAssetTypes, assetGuids: new HashSet<Guid> { assetGuid });
        _lookupPackageCache[key] = package;
        return package;
    }

    private static IEnumerable<Texture> GetTextureCandidates(
        TpacTool.Lib.Material material,
        IReadOnlyDictionary<Guid, Texture> textures,
        Func<IReadOnlyDictionary<Guid, Texture>, Guid, Texture?> resolveTexture)
    {
        return material.Textures
            .Select(pair => (Slot: pair.Key, Texture: resolveTexture(textures, pair.Value.Guid)))
            .Where(item => item.Texture != null && IsUsableTexture(item.Texture))
            .OrderByDescending(item => TextureScore(item.Slot, item.Texture!))
            .Select(item => item.Texture!);
    }

    private static bool IsUsableTexture(Texture texture)
    {
        if (!texture.Format.IsSupported() || !texture.Format.IsVisual())
        {
            return false;
        }

        if (texture.TexturePixels == null)
        {
            return false;
        }

        return true;
    }

    private static int TextureScore(Texture texture)
    {
        return TextureScore(0, texture);
    }

    private static int TextureScore(int slot, Texture texture)
    {
        var score = slot == 0 ? 100 : Math.Max(0, 60 - slot);
        var names = new[] { texture.Name, texture.Source };
        if (names.Any(IsDiffuseTextureName))
        {
            score += 100;
        }

        if (texture.Format == TextureFormat.BC7 ||
            texture.Format == TextureFormat.DXT1 ||
            texture.Format == TextureFormat.DXT3 ||
            texture.Format == TextureFormat.DXT5 ||
            texture.Format == TextureFormat.R8G8B8A8_UNORM ||
            texture.Format == TextureFormat.B8G8R8A8_UNORM)
        {
            score += 10;
        }

        if (names.Any(IsNonDiffuseTextureName) ||
            texture.Flags.Concat(texture.SystemFlags ?? [])
                .Any(flag =>
                    flag.Contains("bump", StringComparison.OrdinalIgnoreCase) ||
                    flag.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
                    flag.Contains("specular", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 120;
        }

        return score;
    }

    private static bool IsDiffuseTextureName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
        return name.EndsWith("_d", StringComparison.Ordinal) ||
               name.EndsWith("_diffuse", StringComparison.Ordinal) ||
               name.EndsWith("_albedo", StringComparison.Ordinal) ||
               name.EndsWith("_basecolor", StringComparison.Ordinal) ||
               name.EndsWith("_base_color", StringComparison.Ordinal);
    }

    private static bool IsNonDiffuseTextureName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
        return name.EndsWith("_n", StringComparison.Ordinal) ||
               name.EndsWith("_normal", StringComparison.Ordinal) ||
               name.EndsWith("_bump", StringComparison.Ordinal) ||
               name.EndsWith("_s", StringComparison.Ordinal) ||
               name.EndsWith("_spec", StringComparison.Ordinal) ||
               name.EndsWith("_specular", StringComparison.Ordinal) ||
               name.EndsWith("_m", StringComparison.Ordinal) ||
               name.EndsWith("_metallic", StringComparison.Ordinal) ||
               name.EndsWith("_ao", StringComparison.Ordinal) ||
               name.EndsWith("_occlusion", StringComparison.Ordinal);
    }

    private System.Windows.Media.Media3D.Material CreateLitMaterial(
        TpacTool.Lib.Material source,
        IReadOnlyDictionary<Guid, Texture> textures,
        ImageBrush diffuseBrush,
        out string status)
    {
        var diffuse = new DiffuseMaterial(diffuseBrush);
        status = "no metallic/gloss map";
        try
        {
            Texture? GetMap(int slot)
            {
                return source.Textures.TryGetValue(slot, out var reference)
                    ? ResolveTexture(textures, reference.Guid) : null;
            }
            var texture = source.ExtraMaterialSettings.SpecularCoef > 0 ? GetMap(4) : null;
            var packedMap = texture != null && IsUsableTexture(texture) ? CreateTextureBitmap(texture) : null;
            var normalTexture = GetMap(2);
            var normalMap = normalTexture != null && IsUsableTexture(normalTexture) ? CreateTextureBitmap(normalTexture) : null;
            var flags = source.ShaderMaterialFlags;
            var usesColours = flags.Contains("use_tableau_blending", StringComparer.OrdinalIgnoreCase) ||
                flags.Contains("use_colormapping", StringComparer.OrdinalIgnoreCase) ||
                flags.Contains("use_double_colormap_with_mask_texture", StringComparer.OrdinalIgnoreCase);
            var colourTexture = GetMap(1);
            var colourMask = colourTexture != null && IsUsableTexture(colourTexture) ? CreateTextureBitmap(colourTexture) : null;
            var tableauTexture = usesColours && flags.Contains("use_tableau_mask_as_separate_texture", StringComparer.OrdinalIgnoreCase) ? GetMap(10) : null;
            var tableauMask = tableauTexture != null && IsUsableTexture(tableauTexture) ? CreateTextureBitmap(tableauTexture) : null;
            StudioRenderer.RegisterMaterial(diffuse, (BitmapSource)diffuseBrush.ImageSource, packedMap, normalMap,
                normalTexture?.Format == TextureFormat.BC5,
                source.ExtraMaterialSettings.SpecularCoef, source.ExtraMaterialSettings.GlossCoef, flags, colourMask, tableauMask);
            status = packedMap == null ? "no metallic/gloss map" : $"metallic/gloss: {texture!.Name}";
            if (normalMap != null) status += $"; normal: {normalTexture!.Name}";
            return diffuse;
        }
        catch (Exception ex)
        {
            status = $"metallic/gloss map failed ({ex.Message})";
            return diffuse;
        }
    }

    private static ImageBrush? CreateTextureBrush(Texture texture)
    {
        var bitmap = CreateTextureBitmap(texture);
        return bitmap == null ? null : CreateImageBrush(bitmap);
    }

    private static BitmapSource? CreateTextureBitmap(Texture texture)
    {
        if (texture.TexturePixels?.Data.PrimaryRawImage is not { Length: > 0 } pixels)
        {
            return null;
        }

        var width = checked((int)texture.Width);
        var height = checked((int)texture.Height);
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Lock();
        try
        {
            var writer = new TextureUtil.ARGB32Writer(bitmap.BackBuffer, width, height, bitmap.BackBufferStride);
            TextureUtil.DecodeTextureDataToWriter(pixels, width, height, texture.Format, writer);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.Unlock();
        }
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageBrush CreateImageBrush(BitmapSource bitmap)
    {
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
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

    private static Vector3D[]? GetVertexNormals(VertexStreamData stream, int vertexCount)
    {
        Vector3D[] normals;
        if (stream.Normals?.Length == vertexCount)
        {
            normals = stream.Normals.Select(normal => ConvertBannerlordNormal(normal.X, normal.Y, normal.Z)).ToArray();
        }
        else if (stream.CompressedNormals?.Length == vertexCount)
        {
            normals = stream.CompressedNormals.Select(normal => ConvertBannerlordNormal((float)normal.X, (float)normal.Y, (float)normal.Z)).ToArray();
        }
        else
        {
            return null;
        }
        return normals.All(normal => normal.LengthSquared > 0) ? normals : null;
    }

    private static Vector3D ConvertBannerlordNormal(float x, float y, float z)
    {
        var normal = new Vector3D(x, z, -y);
        if (!double.IsFinite(normal.LengthSquared) || normal.LengthSquared < 1e-12)
        {
            return new Vector3D();
        }
        normal.Normalize();
        return normal;
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

    private static GeometryModel3D CreateModel(
        MeshGeometry3D mesh,
        System.Windows.Media.Media3D.Material? material,
        Color fallbackColor)
    {
        if (material == null)
        {
            return CreateModel(mesh, fallbackColor);
        }

        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
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
    public string Badge => string.Empty;
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
    public ObservableCollection<MeshAssetNode> Assets { get; } = [];
}

public sealed record MaterialLoadResult(
    System.Windows.Media.Media3D.Material? Material,
    string Status);

public sealed class CraftingPieceNode
{
    public static readonly CraftingPieceNode Empty = new() { Id = "(None)" };

    public string Id { get; set; } = string.Empty;
    public string PieceType { get; set; } = string.Empty;
    public string MeshName { get; set; } = string.Empty;
    public float Length { get; set; }
    public float DistanceToNextPiece { get; set; }
    public float DistanceToPreviousPiece { get; set; }
    public float PieceOffset { get; set; }
    public float PreviousPieceOffset { get; set; }
    public float NextPieceOffset { get; set; }
    public int Scale { get; set; } = 100;
    public float OriginalPieceOffset { get; set; }
    public float OriginalPreviousPieceOffset { get; set; }
    public float OriginalNextPieceOffset { get; set; }
    public int OriginalScale { get; set; } = 100;
    public float ScaleFactor => Scale * 0.01f;
    public float ScaledLength => Length * ScaleFactor;
    public float ScaledDistanceToNextPiece => DistanceToNextPiece * ScaleFactor;
    public float ScaledDistanceToPreviousPiece => DistanceToPreviousPiece * ScaleFactor;
    public float ScaledPieceOffset => PieceOffset * ScaleFactor;
    public float ScaledPreviousPieceOffset => PreviousPieceOffset * ScaleFactor;
    public float ScaledNextPieceOffset => NextPieceOffset * ScaleFactor;
    public float VisualLength => Math.Max(Length, DistanceToNextPiece + DistanceToPreviousPiece);
    public bool IsDirty =>
        PieceOffset != OriginalPieceOffset ||
        PreviousPieceOffset != OriginalPreviousPieceOffset ||
        NextPieceOffset != OriginalNextPieceOffset ||
        Scale != OriginalScale;

    public override string ToString() => Id;
}

public enum MeshRenderMode
{
    AssetPreview,
    CraftingPiece,
    Raw
}

public sealed class AppSettings
{
    public bool SaveSettings { get; init; }
    public string? AssetPackagesPath { get; init; }
    public string? CraftingPiecesPath { get; init; }
    public string? CraftingTemplatesPath { get; init; }
}

public sealed class TroopNode
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Dictionary<string, string> Equipment { get; } = new(StringComparer.OrdinalIgnoreCase);
}
