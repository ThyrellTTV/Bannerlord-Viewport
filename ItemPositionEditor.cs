using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LOTRAOM_Viewport;

public sealed record ItemAttachmentTransform
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float RX { get; init; }
    public float RY { get; init; }
    public float RZ { get; init; }
    internal Vector3 Position => new(X, Y, Z);
    internal Vector3 Rotation => new(RX, RY, RZ);
    internal void Validate()
    {
        if (new[] { X, Y, Z, RX, RY, RZ }.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Position and rotation must be finite numbers.");
    }
    internal static ItemAttachmentTransform From(Vector3 position, Vector3 rotation) => new()
    { X = position.X, Y = position.Y, Z = position.Z, RX = rotation.X, RY = rotation.Y, RZ = rotation.Z };
}

public sealed record ItemPositionTuning
{
    public ItemAttachmentTransform? Held { get; init; }
    public ItemAttachmentTransform? Holstered { get; init; }
    public Dictionary<string, ItemCraftingOffsets>? Pieces { get; init; }
    internal void Validate()
    {
        Held?.Validate(); Holstered?.Validate();
        if (Pieces == null) return;
        foreach (var (id, offsets) in Pieces)
        {
            if (string.IsNullOrWhiteSpace(id) || offsets == null) throw new ArgumentException("Invalid crafting piece tuning.");
            offsets.Validate();
        }
    }
}

public sealed record ItemCraftingOffsets
{
    public float PieceOffset { get; init; }
    public float PreviousPieceOffset { get; init; }
    public float NextPieceOffset { get; init; }
    internal void Validate()
    {
        if (!float.IsFinite(PieceOffset) || !float.IsFinite(PreviousPieceOffset) || !float.IsFinite(NextPieceOffset))
            throw new ArgumentException("Crafting offsets must be finite numbers.");
    }
}

public partial class MainWindow
{
    private readonly Dictionary<string, ItemPositionTuning> _itemPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Stack<ItemPositionTuning> Undo, Stack<ItemPositionTuning> Redo)> _itemPositionHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _equipmentPieceSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _equipmentXmlSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TextBox> _itemPositionBoxes = [];
    private bool _updatingItemPositions;
    private bool _updatingTroopCrafting;
    private const string TroopCraftingNumberError = "Enter a finite number for each crafting offset.";
    private sealed record PositionPiece(string Id, string Slot)
    { public override string ToString() => $"{Slot}: {Id}"; }
    private sealed record PositionItem(string Id, string Slot)
    { public override string ToString() => $"{Slot}: {Id}"; }
    private PositionItem? ActivePositionItem => ItemPositionItemCombo.SelectedItem as PositionItem;

    private void InitializeItemPositionEditor()
    {
        for (var i = 0; i < 6; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var width in new[] { new GridLength(28), new GridLength(1, GridUnitType.Star), new GridLength(34), new GridLength(34) })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var label = new TextBlock { Text = new[] { "X", "Y", "Z", "X", "Y", "Z" }[i], VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            var box = new TextBox { Tag = i, MinWidth = 70 };
            box.LostFocus += ItemPositionBox_LostFocus; box.KeyDown += ItemPositionBox_KeyDown;
            Grid.SetColumn(box, 1); row.Children.Add(box); _itemPositionBoxes.Add(box);
            foreach (var direction in new[] { -1, 1 })
            {
                var button = new Button { Content = direction < 0 ? "-" : "+", Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0), Tag = (i, direction), ToolTip = direction < 0 ? "Decrease" : "Increase" };
                button.Click += ItemPositionStep_Click;
                Grid.SetColumn(button, direction < 0 ? 2 : 3); row.Children.Add(button);
            }
            (i < 3 ? ItemPositionTranslationRows : ItemPositionRotationRows).Children.Add(row);
        }
    }

    private void RefreshItemPositionEditor()
    {
        if (ItemPositionEditorPanel == null || _updatingItemPositions) return;
        var previous = ActivePositionItem;
        var items = new List<PositionItem>();
        if (_previewWorkspace == 1 && WorkspaceTabs.SelectedIndex == 1)
            foreach (var slot in new[] { "Item0", "Item1" })
                if (_troopPreviewLoadout.TryGetValue(slot, out var reference))
                {
                    try
                    {
                        var id = ResolveEquipmentItemReference(slot, reference)[5..];
                        if (_equipmentItems.ContainsKey(id)) items.Add(new(id, slot));
                    }
                    catch (InvalidOperationException) { }
                }
        _updatingItemPositions = true;
        try
        {
            ItemPositionEditorPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ItemPositionItemCombo.ItemsSource = items;
            ItemPositionItemCombo.SelectedItem = items.FirstOrDefault(item => item.Id == previous?.Id && item.Slot == previous.Slot) ?? items.FirstOrDefault();
            if (ActivePositionItem is { } selected) ItemPositionModeCombo.SelectedIndex = PositionItemIsCarried(selected) ? 1 : 0;
        }
        finally { _updatingItemPositions = false; }
        PopulateItemPositionControls();
    }

    private bool PositionItemIsCarried(PositionItem item)
    {
        var state = item.Slot == "Item0" ? Item0StateCombo : Item1StateCombo;
        return IsSlotSheathed(item.Slot) || ((ComboBoxItem)state.Items[0]).Content?.ToString() == "Carried";
    }

    private ItemAttachmentTransform OriginalItemTransform(XElement item, bool holstered)
    {
        if (!holstered)
        {
            var weapon = item.Descendants("Weapon").FirstOrDefault();
            return ItemAttachmentTransform.From(ReadWeaponVector(GetAttributeValue(weapon ?? item, "position")),
                ReadWeaponVector(GetAttributeValue(weapon ?? item, "rotation")));
        }
        _equipmentTemplates.TryGetValue(GetAttributeValue(item, "crafting_template") ?? "", out var template);
        var shift = item.Name.LocalName == "CraftedItem"
            ? GetAttributeValue(template ?? item, "default_item_holster_position_offset")
            : GetAttributeValue(item, "holster_position_shift");
        return ItemAttachmentTransform.From(ReadWeaponVector(shift), Vector3.Zero);
    }

    private void ItemPositionSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingItemPositions || _itemPositionBoxes.Count != 6) return;
        if (ActivePositionItem is { } selected)
        {
            var state = selected.Slot == "Item0" ? Item0StateCombo : Item1StateCombo;
            if (sender == ItemPositionModeCombo && state.IsEnabled)
                state.SelectedIndex = ItemPositionModeCombo.SelectedIndex;
            if (sender == ItemPositionItemCombo)
            {
                _updatingItemPositions = true;
                try { ItemPositionModeCombo.SelectedIndex = PositionItemIsCarried(selected) ? 1 : 0; }
                finally { _updatingItemPositions = false; }
            }
        }
        PopulateItemPositionControls();
    }

    private void PopulateItemPositionControls()
    {
        if (ActivePositionItem is not { } selected || _itemPositionBoxes.Count != 6) return;
        var item = _equipmentItems[selected.Id];
        var holstered = ItemPositionModeCombo.SelectedIndex == 1;
        var tuning = _itemPositions.GetValueOrDefault(selected.Id) ?? new();
        var transform = (holstered ? tuning.Holstered : tuning.Held) ?? OriginalItemTransform(item, holstered);
        float[] values = [transform.X * 100, transform.Y * 100, transform.Z * 100, transform.RX, transform.RY, transform.RZ];
        for (var i = 0; i < 6; i++) _itemPositionBoxes[i].Text = values[i].ToString("0.######", CultureInfo.InvariantCulture);
        var crafted = item.Name.LocalName == "CraftedItem";
        var ammunition = IsAmmunition(WeaponCategory(item));
        var state = selected.Slot == "Item0" ? Item0StateCombo : Item1StateCombo;
        ((ComboBoxItem)ItemPositionModeCombo.Items[0]).IsEnabled = !ammunition && ((ComboBoxItem)state.Items[0]).Content?.ToString() != "Carried";
        ItemPositionFields.IsEnabled = holstered || (!crafted && !ammunition && item.Descendants("Weapon").Any());
        ItemPositionFields.Visibility = crafted && !holstered ? Visibility.Collapsed : Visibility.Visible;
        ItemPositionAttachmentSteps.Visibility = crafted && !holstered ? Visibility.Collapsed : Visibility.Visible;
        ItemPositionCraftingButton.Visibility = crafted ? Visibility.Visible : Visibility.Collapsed;
        ItemPositionStatusText.Text = "";
        if (ammunition && !holstered) ItemPositionStatusText.Text = "Ammunition uses a carried holster frame.";
        ItemPositionRotationLabel.Text = holstered ? "Holster rotation adjustment (degrees)" : "Rotation (degrees)";
        ItemPositionResetButton.ToolTip = crafted && !holstered ? "Reset all crafting offsets for this item" : "Reset this attachment mode";
        try { ItemPositionXmlBox.Text = BuildItemPositionExport(selected.Id, ItemPositionExportModeCombo.SelectedIndex == 1).ToString(); }
        catch (InvalidOperationException ex) { ItemPositionXmlBox.Text = ""; ItemPositionStatusText.Text = ex.Message; }
        catch (InvalidDataException ex) { ItemPositionXmlBox.Text = ""; ItemPositionStatusText.Text = ex.Message; }
        var history = _itemPositionHistory.GetValueOrDefault(selected.Id);
        ItemPositionUndoButton.IsEnabled = history.Undo?.Count > 0;
        ItemPositionRedoButton.IsEnabled = history.Redo?.Count > 0;
        PopulateTroopCraftingControls(item);
    }

    private void ItemPositionBox_LostFocus(object sender, RoutedEventArgs e) => CommitItemPositionControls();
    private void ItemPositionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitItemPositionControls(); e.Handled = true; }
        if (e.Key == Key.Escape) { PopulateItemPositionControls(); e.Handled = true; }
    }

    private void ItemPositionStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<int, int> tag }) return;
        if (!CommitItemPositionControls()) return;
        var stepCombo = tag.Item1 < 3 ? ItemPositionStepCombo : ItemRotationStepCombo;
        var step = float.Parse(((ComboBoxItem)stepCombo.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
        var box = _itemPositionBoxes[tag.Item1];
        var value = float.Parse(box.Text, CultureInfo.InvariantCulture) + step * tag.Item2;
        box.Text = value.ToString("0.######", CultureInfo.InvariantCulture);
        CommitItemPositionControls();
    }

    private bool CommitItemPositionControls()
    {
        if (_updatingItemPositions || ActivePositionItem is not { } selected || !ItemPositionFields.IsEnabled) return false;
        var values = new float[6];
        for (var i = 0; i < values.Length; i++)
            if (!float.TryParse(_itemPositionBoxes[i].Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i]))
            { ItemPositionStatusText.Text = "Enter a finite number for each axis."; return false; }
        var transform = ItemAttachmentTransform.From(new(values[0] / 100, values[1] / 100, values[2] / 100), new(values[3], values[4], values[5]));
        var previous = _itemPositions.GetValueOrDefault(selected.Id) ?? new();
        var holstered = ItemPositionModeCombo.SelectedIndex == 1;
        var current = (holstered ? previous.Holstered : previous.Held) ?? OriginalItemTransform(_equipmentItems[selected.Id], holstered);
        float[] displayed = [current.X * 100, current.Y * 100, current.Z * 100, current.RX, current.RY, current.RZ];
        if (displayed.Select((value, index) => float.Parse(value.ToString("0.######", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) == values[index]).All(equal => equal)) return true;
        if (current == transform) return true;
        SetItemPosition(selected.Id, holstered ? previous with { Holstered = transform } : previous with { Held = transform });
        return true;
    }

    private void SetItemPosition(string id, ItemPositionTuning tuning, bool remember = true)
    {
        tuning.Validate();
        var previous = _itemPositions.GetValueOrDefault(id) ?? new();
        if (!_itemPositionHistory.TryGetValue(id, out var history)) _itemPositionHistory[id] = history = (new(), new());
        if (remember) { history.Undo.Push(previous); history.Redo.Clear(); }
        if (!ReferenceEquals(previous.Pieces, tuning.Pieces))
            foreach (var key in _equipmentPreviewModels.Keys.Where(key => key.Reference.Equals("Item." + id, StringComparison.OrdinalIgnoreCase) || key.Reference.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray())
                _equipmentPreviewModels.Remove(key);
        if (tuning.Held == null && tuning.Holstered == null && tuning.Pieces is not { Count: > 0 }) _itemPositions.Remove(id);
        else _itemPositions[id] = tuning;
        var resume = _posePlaybackTimer.IsEnabled;
        RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
        PopulateItemPositionControls();
        SaveSettingsIfEnabled();
        if (resume && _poseAvailable && _poseClip != null) PosePlay_Click(this, new RoutedEventArgs());
    }

    private void ItemPositionHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePositionItem is not { } selected) return;
        var tuning = _itemPositions.GetValueOrDefault(selected.Id) ?? new();
        if (sender == ItemPositionResetButton)
        {
            var reset = ItemPositionModeCombo.SelectedIndex == 1 ? tuning with { Holstered = null }
                : _equipmentItems[selected.Id].Name.LocalName == "CraftedItem" ? tuning with { Pieces = null } : tuning with { Held = null };
            if (reset != tuning) SetItemPosition(selected.Id, reset);
            else PopulateItemPositionControls();
            return;
        }
        if (!_itemPositionHistory.TryGetValue(selected.Id, out var history)) return;
        var from = sender == ItemPositionUndoButton ? history.Undo : history.Redo;
        var to = sender == ItemPositionUndoButton ? history.Redo : history.Undo;
        if (from.Count == 0) return;
        to.Push(tuning); SetItemPosition(selected.Id, from.Pop(), remember: false);
    }

    private Matrix4x4 TunedWeaponFrame(string id, XElement? weapon)
    {
        if (_itemPositions.GetValueOrDefault(id)?.Held is not { } held) return CreateWeaponFrame(weapon);
        return CreateWeaponFrame(new XElement("Weapon", new XAttribute("position", ItemVector(held.Position)), new XAttribute("rotation", ItemVector(held.Rotation))));
    }

    private static string ItemVector(Vector3 vector) => string.Create(CultureInfo.InvariantCulture, $"{vector.X:G9},{vector.Y:G9},{vector.Z:G9}");
    private static string CustomPositionId(string itemId, string source) => "viewport_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(itemId + "|" + source)))[..20].ToLowerInvariant();

    private ItemCraftingOffsets OriginalCraftingOffsets(string id)
    {
        var piece = _craftingPieces.FirstOrDefault(piece => piece.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && piece.IsDirty) ?? _equipmentCraftingPieces.GetValueOrDefault(id);
        if (piece == null) throw new InvalidOperationException($"Crafting piece '{id}' is unavailable.");
        return new() { PieceOffset = piece.PieceOffset, PreviousPieceOffset = piece.PreviousPieceOffset, NextPieceOffset = piece.NextPieceOffset };
    }

    private void PopulateTroopCraftingControls(XElement item)
    {
        var previous = (TroopCraftingPieceCombo.SelectedItem as PositionPiece)?.Id;
        _updatingTroopCrafting = true;
        try
        {
            var pieces = item.Name.LocalName == "CraftedItem" ? item.Descendants("Piece").Select(reference =>
            {
                var id = GetAttributeValue(reference, "id") ?? "";
                var slot = GetAttributeValue(reference, "Type") ?? _equipmentCraftingPieces.GetValueOrDefault(id)?.PieceType ?? "";
                return new PositionPiece(id, slot);
            }).ToArray() : [];
            TroopCraftingOffsetsPanel.Visibility = pieces.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TroopCraftingPieceCombo.ItemsSource = pieces;
            TroopCraftingPieceCombo.SelectedItem = pieces.FirstOrDefault(piece => piece.Id == previous) ?? pieces.FirstOrDefault();
        }
        finally { _updatingTroopCrafting = false; }
        PopulateTroopCraftingOffsets();
    }

    private void TroopCraftingPiece_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingTroopCrafting) PopulateTroopCraftingOffsets();
    }

    private void PopulateTroopCraftingOffsets()
    {
        if (ActivePositionItem is not { } selected || TroopCraftingPieceCombo.SelectedItem is not PositionPiece piece) return;
        try
        {
            var offsets = _itemPositions.GetValueOrDefault(selected.Id)?.Pieces?.GetValueOrDefault(piece.Id) ?? OriginalCraftingOffsets(piece.Id);
            TroopCraftingOffsetBox.Text = offsets.PieceOffset.ToString("G9", CultureInfo.InvariantCulture);
            TroopCraftingPreviousBox.Text = offsets.PreviousPieceOffset.ToString("G9", CultureInfo.InvariantCulture);
            TroopCraftingNextBox.Text = offsets.NextPieceOffset.ToString("G9", CultureInfo.InvariantCulture);
            TroopCraftingOffsetFields.IsEnabled = true;
            if (ItemPositionStatusText.Text == TroopCraftingNumberError) ItemPositionStatusText.Text = "";
        }
        catch (InvalidOperationException ex) { TroopCraftingOffsetFields.IsEnabled = false; ItemPositionStatusText.Text = ex.Message; }
    }

    private bool CommitTroopCraftingOffsets()
    {
        if (_updatingItemPositions || _updatingTroopCrafting || ActivePositionItem is not { } selected ||
            TroopCraftingPieceCombo.SelectedItem is not PositionPiece piece || !TroopCraftingOffsetFields.IsEnabled) return false;
        var boxes = new[] { TroopCraftingOffsetBox, TroopCraftingPreviousBox, TroopCraftingNextBox };
        var values = new float[3];
        for (var i = 0; i < values.Length; i++)
            if (!float.TryParse(boxes[i].Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i]))
            { ItemPositionStatusText.Text = TroopCraftingNumberError; return false; }
        if (ItemPositionStatusText.Text == TroopCraftingNumberError) ItemPositionStatusText.Text = "";
        var tuning = _itemPositions.GetValueOrDefault(selected.Id) ?? new();
        var offsets = new ItemCraftingOffsets { PieceOffset = values[0], PreviousPieceOffset = values[1], NextPieceOffset = values[2] };
        if (offsets == (tuning.Pieces?.GetValueOrDefault(piece.Id) ?? OriginalCraftingOffsets(piece.Id))) return true;
        var pieces = new Dictionary<string, ItemCraftingOffsets>(tuning.Pieces ?? [], StringComparer.OrdinalIgnoreCase) { [piece.Id] = offsets };
        SetItemPosition(selected.Id, tuning with { Pieces = pieces });
        return true;
    }

    private void TroopCraftingOffset_LostFocus(object sender, RoutedEventArgs e) => CommitTroopCraftingOffsets();
    private void TroopCraftingOffset_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTroopCraftingOffsets(); e.Handled = true; }
        if (e.Key == Key.Escape) { PopulateTroopCraftingOffsets(); e.Handled = true; }
    }

    private void TroopCraftingOffsetStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !CommitTroopCraftingOffsets()) return;
        var parts = tag.Split(',');
        var boxes = new[] { TroopCraftingOffsetBox, TroopCraftingPreviousBox, TroopCraftingNextBox };
        var box = boxes[int.Parse(parts[0], CultureInfo.InvariantCulture)];
        var step = float.Parse(((ComboBoxItem)TroopCraftingStepCombo.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
        box.Text = (float.Parse(box.Text, CultureInfo.InvariantCulture) + step * int.Parse(parts[1], CultureInfo.InvariantCulture)).ToString("G9", CultureInfo.InvariantCulture);
        CommitTroopCraftingOffsets();
    }

    private void TroopCraftingReset_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePositionItem is not { } selected || TroopCraftingPieceCombo.SelectedItem is not PositionPiece piece ||
            _itemPositions.GetValueOrDefault(selected.Id) is not { Pieces: { } previous } tuning || !previous.ContainsKey(piece.Id)) return;
        var pieces = new Dictionary<string, ItemCraftingOffsets>(previous, StringComparer.OrdinalIgnoreCase);
        pieces.Remove(piece.Id);
        SetItemPosition(selected.Id, tuning with { Pieces = pieces.Count > 0 ? pieces : null });
    }

    private XDocument? BuildItemCraftingPieceDocument(string id)
    {
        if (_itemPositions.GetValueOrDefault(id)?.Pieces is not { Count: > 0 } offsets) return null;
        var source = _equipmentItems[id];
        if (source.Name.LocalName != "CraftedItem") throw new InvalidOperationException("Crafting offsets require a crafted item.");
        var references = source.Descendants("Piece").Select(piece => GetAttributeValue(piece, "id") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<XElement>();
        foreach (var (pieceId, tuning) in offsets)
        {
            tuning.Validate();
            if (!references.Contains(pieceId) || !_equipmentPieceDefinitions.TryGetValue(pieceId, out var definition))
                throw new InvalidOperationException($"Crafting piece '{pieceId}' is not available for this item.");
            var copy = new XElement(definition);
            copy.SetAttributeValue("id", CustomPositionId(id, "piece:" + pieceId));
            var build = copy.Element("BuildData");
            if (build == null) { build = new XElement("BuildData"); copy.Add(build); }
            build.SetAttributeValue("piece_offset", tuning.PieceOffset.ToString("G9", CultureInfo.InvariantCulture));
            build.SetAttributeValue("previous_piece_offset", tuning.PreviousPieceOffset.ToString("G9", CultureInfo.InvariantCulture));
            build.SetAttributeValue("next_piece_offset", tuning.NextPieceOffset.ToString("G9", CultureInfo.InvariantCulture));
            definitions.Add(copy);
        }
        return new(new XElement("CraftingPieces", definitions));
    }

    private (XDocument Items, XDocument? Holsters, XDocument? Templates) BuildItemPositionDocuments(string id, bool fullItem)
    {
        var source = _equipmentItems[id];
        var tuning = _itemPositions.GetValueOrDefault(id) ?? new();
        var item = fullItem ? new XElement(source) : new XElement(source.Name, new XAttribute("id", id));
        if (!fullItem)
        {
            foreach (var name in source.Name.LocalName == "CraftedItem" ? new[] { "crafting_template" } : new[] { "item_holsters", "holster_position_shift" })
                if (source.Attribute(name) is { } attribute) item.Add(new XAttribute(attribute));
        }
        var heldTransform = tuning.Held ?? (!fullItem && source.Descendants("Weapon").Any() ? OriginalItemTransform(source, false) : null);
        if (heldTransform is { } held && source.Name.LocalName != "CraftedItem")
        {
            var originalWeapon = source.Descendants("Weapon").FirstOrDefault();
            var weapon = fullItem ? item.Descendants("Weapon").FirstOrDefault() : new XElement("Weapon");
            if (weapon == null || originalWeapon == null) throw new InvalidOperationException("This item has no Weapon positioning element.");
            weapon.SetAttributeValue("position", ItemVector(held.Position));
            weapon.SetAttributeValue("rotation", ItemVector(held.Rotation));
            if (!fullItem) item.Add(new XElement("ItemComponent", weapon));
        }
        XDocument? holsterDocument = null, templateDocument = null;
        if (tuning.Holstered is { } holstered)
        {
            var crafted = source.Name.LocalName == "CraftedItem";
            _equipmentTemplates.TryGetValue(GetAttributeValue(source, "crafting_template") ?? "", out var originalTemplate);
            if (crafted && originalTemplate == null) throw new InvalidOperationException("The item's crafting template is unavailable.");
            var target = crafted ? new XElement(originalTemplate!) : item;
            target.SetAttributeValue(crafted ? "default_item_holster_position_offset" : "holster_position_shift", ItemVector(holstered.Position));
            if (holstered.Rotation != Vector3.Zero)
            {
                var candidates = GetAttributeValue(source, "item_holsters") ?? GetAttributeValue(originalTemplate ?? source, "item_holsters");
                if (string.IsNullOrWhiteSpace(candidates) || _troopPoses == null) throw new InvalidOperationException("The item's holster definitions are unavailable.");
                var definitions = new List<XElement>();
                var replacements = new List<string>();
                foreach (var name in candidates.Split(':'))
                {
                    var definition = _troopPoses.GetHolsterDefinition(name);
                    var customId = CustomPositionId(id, name);
                    definition.SetAttributeValue("id", customId);
                    definition.SetAttributeValue("base_set", name);
                    definition.SetAttributeValue("holster_position", "0,0,0");
                    definition.SetAttributeValue("holster_rotation_yaw_pitch_roll", ItemVector(new(holstered.RZ, holstered.RY, holstered.RX)));
                    definitions.Add(definition); replacements.Add(customId);
                }
                target.SetAttributeValue("item_holsters", string.Join(':', replacements));
                holsterDocument = new(new XElement("base", new XElement("item_holsters", definitions)));
            }
            if (crafted)
            {
                var templateId = CustomPositionId(id, "template");
                target.SetAttributeValue("id", templateId);
                item.SetAttributeValue("crafting_template", templateId);
                templateDocument = new(new XElement("CraftingTemplates", target));
            }
        }
        if (BuildItemCraftingPieceDocument(id) != null)
        {
            var pieces = fullItem ? item.Element("Pieces") : new XElement(source.Element("Pieces") ?? throw new InvalidOperationException("The item's Pieces element is missing."));
            if (pieces == null) throw new InvalidOperationException("The item's Pieces element is missing.");
            foreach (var reference in pieces.Elements("Piece"))
                if (GetAttributeValue(reference, "id") is { } pieceId && tuning.Pieces!.ContainsKey(pieceId))
                    reference.SetAttributeValue("id", CustomPositionId(id, "piece:" + pieceId));
            if (!fullItem) item.Add(pieces);
        }
        var items = new XDocument(new XElement("Items", item));
        if (!fullItem) items.Root!.AddFirst(new XComment(" Positioning only: merge these attributes and any piece references into the existing item definition; do not replace the entire item. "));
        return (items, holsterDocument, templateDocument);
    }

    private XDocument BuildItemPositionExport(string id, bool fullItem) => BuildItemPositionDocuments(id, fullItem).Items;

    private void CopyItemPositionXml_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePositionItem is not { } selected) return;
        try
        {
            if (ItemPositionFields.IsEnabled && !CommitItemPositionControls()) return;
            if (TroopCraftingOffsetsPanel.Visibility == Visibility.Visible && !CommitTroopCraftingOffsets()) return;
            var documents = BuildItemPositionDocuments(selected.Id, ItemPositionExportModeCombo.SelectedIndex == 1);
            var pieces = BuildItemCraftingPieceDocument(selected.Id);
            Clipboard.SetText(documents.Items + (documents.Holsters != null ? "\n\n<!-- item_holsters.xml -->\n" + documents.Holsters : "") +
                (documents.Templates != null ? "\n\n<!-- crafting_templates.xml -->\n" + documents.Templates : "") +
                (pieces != null ? "\n\n<!-- crafting_pieces.xml -->\n" + pieces : ""));
            ItemPositionStatusText.Text = "Copied positioning XML.";
        }
        catch (Exception ex) { ItemPositionStatusText.Text = ex.Message; }
    }

    private void SaveItemPositionXml_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePositionItem is not { } selected) return;
        try
        {
            if (ItemPositionFields.IsEnabled && !CommitItemPositionControls()) return;
            if (TroopCraftingOffsetsPanel.Visibility == Visibility.Visible && !CommitTroopCraftingOffsets()) return;
            var edits = new SourceXmlEdits();
            AddItemSourceEdits(edits, selected.Id);
            if (!ConfirmSourceSave(edits)) return;
            SaveSourceEdits(edits);
            ItemPositionStatusText.Text = "Saved item edits to source XML. Backups created.";
        }
        catch (Exception ex) { ItemPositionStatusText.Text = ex.Message; }
    }

    private void WriteItemPositionDocuments(string path, (XDocument Items, XDocument? Holsters, XDocument? Templates) documents)
    {
        path = Path.GetFullPath(path);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var files = new List<(string Path, XDocument Document)>();
        if (documents.Holsters != null) files.Add((stem + "_holsters.xml", documents.Holsters));
        if (documents.Templates != null) files.Add((stem + "_templates.xml", documents.Templates));
        if (documents.Items.Root?.Elements("CraftedItem").FirstOrDefault()?.Attribute("id")?.Value is { } itemId && BuildItemCraftingPieceDocument(itemId) is { } pieces)
            files.Add((stem + "_pieces.xml", pieces));
        files.Add((path, documents.Items));
        foreach (var file in files)
        {
            if (IsGameModulePath(file.Path) || _equipmentXmlSourcePaths.Contains(file.Path) ||
                new[] { TroopXmlPathBox.Text, CraftingPiecesPathBox.Text, CraftingTemplatesPathBox.Text }.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Any(value => Path.GetFullPath(value).Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Choose an export file outside the source XML and game Modules folder, then merge the XML manually.");
            if (file.Path != path && File.Exists(file.Path))
                throw new IOException($"Companion file already exists: {Path.GetFileName(file.Path)}. Choose another output name.");
        }
        var temporaryFiles = new List<(string Path, string Temporary)>();
        var createdCompanions = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                temporaryFiles.Add((file.Path, temporary));
                file.Document.Save(temporary);
            }
            foreach (var file in temporaryFiles)
            {
                File.Move(file.Temporary, file.Path, overwrite: file.Path == path);
                if (file.Path != path) createdCompanions.Add(file.Path);
            }
        }
        catch
        {
            foreach (var companion in createdCompanions) File.Delete(companion);
            throw;
        }
        finally
        {
            foreach (var file in temporaryFiles) if (File.Exists(file.Temporary)) File.Delete(file.Temporary);
        }
    }

    private static bool IsGameModulePath(string path)
    {
        var folder = new FileInfo(Path.GetFullPath(path)).Directory;
        while (folder != null)
        {
            if (folder.Name.Equals("Modules", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(folder.FullName, "Native"))) return true;
            folder = folder.Parent;
        }
        return false;
    }

    private void ItemPositionCrafting_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePositionItem is not { } selected) return;
        try { LoadCraftingItem(_equipmentItems[selected.Id]); }
        catch (InvalidOperationException ex) { ItemPositionStatusText.Text = ex.Message; }
    }

    private void LoadCraftingItem(XElement item)
    {
        var templateId = GetAttributeValue(item, "crafting_template");
        if (item.Name.LocalName != "CraftedItem" || string.IsNullOrWhiteSpace(templateId))
            throw new InvalidOperationException("This item has no crafting build.");
        var selections = new Dictionary<string, (CraftingPieceNode Piece, int Scale)>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in item.Descendants("Piece"))
        {
            var id = GetAttributeValue(reference, "id") ?? "";
            var piece = _craftingPieces.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (piece == null && _equipmentPieceDefinitions.TryGetValue(id, out var definition)) piece = ParseCraftingPiece(definition);
            if (piece == null) throw new InvalidOperationException($"Crafting piece '{id}' is unavailable.");
            if (piece.SourcePath.Length == 0 && _equipmentPieceSourcePaths.TryGetValue(id, out var sourcePath)) piece.SourcePath = Path.GetFullPath(sourcePath);
            var slot = GetAttributeValue(reference, "Type") ?? piece.PieceType;
            if (!CraftingSlots.Contains(slot, StringComparer.OrdinalIgnoreCase) || !piece.PieceType.Equals(slot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsupported crafting slot '{slot}' for '{id}'.");
            var scaleText = GetAttributeValue(reference, "scale_factor");
            var scale = 100;
            if (scaleText != null && (!int.TryParse(scaleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out scale) || scale <= 0))
                throw new InvalidOperationException($"Invalid crafting scale for '{id}'.");
            if (!selections.TryAdd(slot, (piece, scale))) throw new InvalidOperationException($"Duplicate crafting slot '{slot}'.");
        }
        if (selections.Count == 0) throw new InvalidOperationException("This item has no crafting pieces.");
        _equipmentTemplates.TryGetValue(templateId, out var template);
        var order = template?.Descendants("PieceData").Select(element => (
            Type: GetAttributeValue(element, "piece_type") ?? "",
            Order: int.TryParse(GetAttributeValue(element, "build_order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0))
            .Where(entry => entry.Type.Length > 0).ToArray() ?? [];
        if (order.Length == 0 && !_craftingTemplateBuildOrders.ContainsKey(templateId) && !BundledCraftingBuildOrders.ContainsKey(templateId))
            throw new InvalidOperationException($"Crafting template '{templateId}' has no build order.");

        _isUpdatingCraftingCombos = true;
        try
        {
            if (_craftingPieces.Count == 0 && _equipmentPieceSourcePaths.TryGetValue(selections.Values.First().Piece.Id, out var path))
                CraftingPiecesPathBox.Text = path;
            // Merge module pieces rather than reloading one XML file and losing existing tuning.
            foreach (var (piece, scale) in selections.Values)
            {
                if (!_craftingPieces.Contains(piece)) _craftingPieces.Add(piece);
                if (piece.Scale == piece.OriginalScale) piece.Scale = scale;
                piece.OriginalScale = scale;
            }
            if (order.Length > 0) _craftingTemplateBuildOrders[templateId] = order;
            var allowed = template?.Descendants().Where(element => element.Name.LocalName is "UsablePiece" or "AvailablePiece")
                .Select(element => GetAttributeValue(element, "piece_id") ?? GetAttributeValue(element, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed is { Count: > 0 }) _craftingTemplatePieceIds[templateId] = allowed;
            if (_craftingTemplatePieceIds.TryGetValue(templateId, out var filter)) filter.UnionWith(selections.Values.Select(selection => selection.Piece.Id));
            _craftingSearchText.Clear();
            RefreshCraftingTemplates();
            CraftingTemplateCombo.SelectedItem = templateId;
            foreach (var (combo, slot) in GetCraftingCombos())
                PopulateCraftingCombo(combo, slot, selections.TryGetValue(slot, out var selection) ? selection.Piece.Id : null);
            _craftingBlade = selections.GetValueOrDefault("Blade").Piece;
            _craftingGuard = selections.GetValueOrDefault("Guard").Piece;
            _craftingHandle = selections.GetValueOrDefault("Handle").Piece;
            _craftingPommel = selections.GetValueOrDefault("Pommel").Piece;
        }
        finally { _isUpdatingCraftingCombos = false; }
        _craftingSourceItemId = GetAttributeValue(item, "id");
        CraftingTab.IsSelected = true;
        SetActiveCraftingPiece(_craftingBlade ?? selections.Values.First().Piece);
        CraftingInspectorSection.IsExpanded = true;
        CraftingSummaryText.Text = $"Loaded {selections.Count} piece(s) for {GetAttributeValue(item, "id")}.";
        RenderCraftingWeaponPreview();
        SaveSettingsIfEnabled();
    }
}
