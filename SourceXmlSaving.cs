using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Xml;
using System.Xml.Linq;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _equipmentItemSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _equipmentTemplateSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    private static XElement? FirstBattleRoster(XElement troop) => troop.Descendants()
        .FirstOrDefault(element => element.Name.LocalName.Equals("EquipmentRoster", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(GetAttributeValue(element, "civilian"), "true", StringComparison.OrdinalIgnoreCase));

    private bool CommitPositionEditorsForSave()
    {
        if (ItemPositionEditorPanel.Visibility != Visibility.Visible) return true;
        if (ItemPositionFields.IsEnabled && !CommitItemPositionControls()) return false;
        return TroopCraftingOffsetsPanel.Visibility != Visibility.Visible || CommitTroopCraftingOffsets();
    }

    private SourceXmlEdits BuildTroopSourceEdits()
    {
        var troop = _activeTroop ?? throw new InvalidOperationException("Select a source troop before saving XML.");
        var edits = new SourceXmlEdits();
        var target = edits.File(troop.SourcePath).Find(troop.SourceElementName, troop.Id);
        var roster = FirstBattleRoster(target);
        var loadout = ReadEquipmentBoxes();
        if (roster == null && EquipmentSlots.Any(slot => GetAttributeValue(target, slot) != null))
        {
            foreach (var slot in EquipmentSlots)
                target.SetAttributeValue(slot, loadout.TryGetValue(slot, out var value) ? ResolveEquipmentItemReference(slot, value) : null);
        }
        else
        {
            if (roster == null)
            {
                // Preserve flat equipment XML rather than introducing a second equipment container.
                if (target.Elements().Any(element => element.Name.LocalName.Equals("equipment", StringComparison.OrdinalIgnoreCase))) roster = target;
                else
                {
                    var container = target.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("Equipments", StringComparison.OrdinalIgnoreCase));
                    if (container == null) { container = new XElement(target.Name.Namespace + "Equipments"); AppendSourceElement(target, container); }
                    roster = new XElement(target.Name.Namespace + "EquipmentRoster"); AppendSourceElement(container, roster);
                }
            }
            var updated = CreateEquipmentRosterXml(loadout);
            foreach (var slot in EquipmentSlots)
            {
                var canonical = slot == "Helmet" ? "Head" : slot;
                var matches = roster.Elements().Where(element => element.Name.LocalName.Equals("equipment", StringComparison.OrdinalIgnoreCase) &&
                    CanonicalEquipmentSlot(GetAttributeValue(element, "slot")).Equals(slot, StringComparison.OrdinalIgnoreCase)).ToArray();
                var replacement = updated.Elements().FirstOrDefault(element => GetAttributeValue(element, "slot") == canonical);
                if (replacement == null) { foreach (var match in matches) match.Remove(); continue; }
                if (matches.Length > 1) throw new InvalidOperationException($"Troop '{troop.Id}' has duplicate {slot} entries in its first battle roster.");
                if (matches.Length == 1) matches[0].SetAttributeValue("id", GetAttributeValue(replacement, "id"));
                else AppendSourceElement(roster, new XElement(roster.Name.Namespace + "equipment", replacement.Attributes()));
            }
        }
        foreach (var id in loadout.Select(pair => ResolveEquipmentItemReference(pair.Key, pair.Value)[5..]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_itemPositions.ContainsKey(id)) AddItemSourceEdits(edits, id);
            if (_equipmentItems.TryGetValue(id, out var item))
                foreach (var reference in item.Descendants("Piece"))
                    if (_craftingPieces.FirstOrDefault(piece => piece.Id.Equals(GetAttributeValue(reference, "id"), StringComparison.OrdinalIgnoreCase) && piece.IsDirty) is { } piece)
                        AddCraftingPieceSourceEdit(edits, piece);
        }
        return edits;
    }

    private static string CanonicalEquipmentSlot(string? slot) => slot?.ToLowerInvariant() switch
    { "head" => "Helmet", "gloves" => "Arm", _ => slot ?? "" };

    private void AddItemSourceEdits(SourceXmlEdits edits, string id)
    {
        if (!edits.ItemIds.Add(id)) return;
        if (!_equipmentItemSourcePaths.TryGetValue(id, out var itemPath)) throw new InvalidOperationException($"Source XML for item '{id}' is unknown.");
        var source = _equipmentItems[id];
        var documents = BuildItemPositionDocuments(id, true);
        var changed = documents.Items.Root!.Elements().Single();
        var target = edits.File(itemPath).Find(source.Name, id);
        foreach (var attribute in new[] { "item_holsters", "holster_position_shift", "crafting_template" })
            target.SetAttributeValue(attribute, GetAttributeValue(changed, attribute));
        if (source.Name.LocalName != "CraftedItem" && changed.Descendants("Weapon").FirstOrDefault() is { } weapon)
        {
            var targetWeapon = target.Descendants("Weapon").FirstOrDefault() ?? throw new InvalidOperationException($"Item '{id}' has no source Weapon element.");
            foreach (var attribute in new[] { "position", "rotation" }) targetWeapon.SetAttributeValue(attribute, GetAttributeValue(weapon, attribute));
        }
        if (documents.Templates?.Root?.Elements().SingleOrDefault() is { } template)
        {
            var originalId = GetAttributeValue(source, "crafting_template") ?? "";
            if (!_equipmentTemplateSourcePaths.TryGetValue(originalId, out var path)) throw new InvalidOperationException($"Source XML for template '{originalId}' is unknown.");
            edits.File(path).Upsert(template, "CraftingTemplates");
        }
        if (documents.Holsters != null)
        {
            var path = _troopPoses?.HolsterSourcePath ?? throw new InvalidOperationException("Source holster XML is unavailable.");
            foreach (var holster in documents.Holsters.Descendants("item_holster")) edits.File(path).Upsert(holster, "item_holsters");
        }
        if (BuildItemCraftingPieceDocument(id) is { } pieces)
        {
            foreach (var originalId in _itemPositions[id].Pieces!.Keys)
            {
                if (!_equipmentPieceSourcePaths.TryGetValue(originalId, out var path)) throw new InvalidOperationException($"Source XML for piece '{originalId}' is unknown.");
                var clone = pieces.Root!.Elements().Single(piece => GetAttributeValue(piece, "id") == CustomPositionId(id, "piece:" + originalId));
                edits.File(path).Upsert(clone, "CraftingPieces");
            }
            var refs = target.Element("Pieces") ?? throw new InvalidOperationException($"Item '{id}' has no source Pieces element.");
            foreach (var originalId in _itemPositions[id].Pieces!.Keys)
            {
                var desired = CustomPositionId(id, "piece:" + originalId);
                var reference = refs.Elements("Piece").SingleOrDefault(piece => string.Equals(GetAttributeValue(piece, "id"), originalId, StringComparison.OrdinalIgnoreCase));
                if (reference == null) throw new InvalidOperationException($"Source piece references for '{id}' changed; reload the source before saving.");
                reference.SetAttributeValue("id", desired);
            }
        }
        foreach (var reference in source.Descendants("Piece"))
            if (_craftingPieces.FirstOrDefault(piece => piece.Id.Equals(GetAttributeValue(reference, "id"), StringComparison.OrdinalIgnoreCase) && piece.IsDirty) is { } piece)
                AddCraftingPieceSourceEdit(edits, piece);
    }

    private void AddCraftingPieceSourceEdit(SourceXmlEdits edits, CraftingPieceNode piece)
    {
        if (!edits.CraftingNodes.Add(piece)) return;
        if (piece.SourcePath.Length == 0) throw new InvalidOperationException($"Source XML for crafting piece '{piece.Id}' is unknown.");
        var offsets = new ItemCraftingOffsets { PieceOffset = piece.PieceOffset, PreviousPieceOffset = piece.PreviousPieceOffset, NextPieceOffset = piece.NextPieceOffset };
        offsets.Validate();
        var target = edits.File(piece.SourcePath).Find("CraftingPiece", piece.Id);
        var build = target.Elements().FirstOrDefault(element => element.Name.LocalName == "BuildData");
        if (build == null) { build = new XElement(target.Name.Namespace + "BuildData"); AppendSourceElement(target, build); }
        build.SetAttributeValue("piece_offset", offsets.PieceOffset.ToString("G9", CultureInfo.InvariantCulture));
        build.SetAttributeValue("previous_piece_offset", offsets.PreviousPieceOffset.ToString("G9", CultureInfo.InvariantCulture));
        build.SetAttributeValue("next_piece_offset", offsets.NextPieceOffset.ToString("G9", CultureInfo.InvariantCulture));
        edits.HasSharedPieceEdits = true;
    }

    private SourceXmlEdits BuildCraftingSourceEdits()
    {
        var edits = new SourceXmlEdits();
        foreach (var piece in _craftingPieces.Where(piece => piece.IsDirty)) AddCraftingPieceSourceEdit(edits, piece);
        if (_craftingSourceItemId is { } id)
        {
            if (!_equipmentItemSourcePaths.TryGetValue(id, out var path)) throw new InvalidOperationException($"Source XML for crafted item '{id}' is unknown.");
            var tuning = _itemPositions.GetValueOrDefault(id);
            var originalTemplate = GetAttributeValue(_equipmentItems[id], "crafting_template");
            var selectedTemplate = CraftingTemplateCombo.SelectedItem as string;
            if (tuning?.Holstered != null && selectedTemplate != null && selectedTemplate != "(All pieces)" && selectedTemplate != originalTemplate)
                throw new InvalidOperationException("Save the troop positioning edits before changing this weapon's crafting template.");
            if (tuning != null) AddItemSourceEdits(edits, id);
            var item = edits.File(path).Find("CraftedItem", id);
            var pieces = item.Element("Pieces") ?? throw new InvalidOperationException($"Item '{id}' has no Pieces element.");
            foreach (var (slot, piece) in new[] { ("Blade", _craftingBlade), ("Guard", _craftingGuard), ("Handle", _craftingHandle), ("Pommel", _craftingPommel) })
            {
                var existing = pieces.Elements("Piece").SingleOrDefault(reference => string.Equals(GetAttributeValue(reference, "Type"), slot, StringComparison.OrdinalIgnoreCase));
                if (piece == null) { existing?.Remove(); continue; }
                if (piece.Scale <= 0) throw new InvalidOperationException("Crafting scale must be positive.");
                if (existing == null) { existing = new XElement(pieces.Name.Namespace + "Piece", new XAttribute("Type", slot)); AppendSourceElement(pieces, existing); }
                existing.SetAttributeValue("id", tuning?.Pieces?.ContainsKey(piece.Id) == true ? CustomPositionId(id, "piece:" + piece.Id) : piece.Id);
                existing.SetAttributeValue("scale_factor", piece.Scale);
                edits.CraftingScaleSaved.Add(piece);
            }
            if (!pieces.Elements("Piece").Any()) throw new InvalidOperationException("Select crafting pieces before saving the item.");
            if (selectedTemplate is { } template && template != "(All pieces)" && tuning?.Holstered == null) item.SetAttributeValue("crafting_template", template);
            edits.ItemIds.Add(id);
        }
        else if (edits.CraftingNodes.Any(piece => piece.Scale != piece.OriginalScale))
            throw new InvalidOperationException("Scale belongs to a crafted item's piece reference. Open the source weapon from Troops before saving scale changes.");
        if (edits.Files.Count == 0) throw new InvalidOperationException("No crafting edits to save.");
        return edits;
    }

    private void SaveCraftingSourceXml_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitCraftingOffsetBoxes();
            var edits = BuildCraftingSourceEdits();
            if (!ConfirmSourceSave(edits)) return;
            SaveSourceEdits(edits);
            CraftingSummaryText.Text = "Saved crafting edits to source XML. Backups created.";
        }
        catch (Exception ex) { CraftingSummaryText.Text = $"Could not save XML: {ex.Message}"; }
    }

    private bool ConfirmSourceSave(SourceXmlEdits edits) => MessageBox.Show(this,
        "Write changes directly to these source XML files?\n\n" + string.Join("\n", edits.Files.Keys) +
        (edits.HasSharedPieceEdits ? "\n\nStandalone crafting-piece offsets also affect other weapons using those pieces." : "") +
        "\n\nA timestamped .bak copy will be created beside each changed file. Saving makes these values the new editing baseline.",
        "Save source XML", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void SaveSourceEdits(SourceXmlEdits edits)
    {
        edits.Write();
        // Rebase only successfully saved definitions so preview tuning is not applied twice after a reload.
        foreach (var (path, file) in edits.Files)
            foreach (var element in file.Document.Root?.DescendantsAndSelf() ?? [])
            {
                var id = GetAttributeValue(element, "id");
                if (string.IsNullOrWhiteSpace(id) || !file.UpdatedDefinitions.Contains((element.Name, id))) continue;
                switch (element.Name.LocalName)
                {
                    case "Item": case "CraftedItem":
                        if (_equipmentItemSourcePaths.GetValueOrDefault(id) == path || edits.ItemIds.Contains(id))
                        { _equipmentItems[id] = new XElement(element); _equipmentItemSourcePaths[id] = path; }
                        break;
                    case "CraftingPiece":
                        if (ParseCraftingPiece(element) is { } piece)
                        {
                            piece.SourcePath = path;
                            _equipmentPieceDefinitions[id] = new XElement(element); _equipmentCraftingPieces[id] = piece; _equipmentPieceSourcePaths[id] = path;
                        }
                        break;
                    case "CraftingTemplate":
                        _equipmentTemplates[id] = new XElement(element); _equipmentTemplateSourcePaths[id] = path;
                        break;
                }
            }
        foreach (var id in edits.ItemIds) { _itemPositions.Remove(id); _itemPositionHistory.Remove(id); }
        foreach (var piece in edits.CraftingNodes)
        {
            piece.OriginalPieceOffset = piece.PieceOffset; piece.OriginalPreviousPieceOffset = piece.PreviousPieceOffset;
            piece.OriginalNextPieceOffset = piece.NextPieceOffset;
            if (edits.CraftingScaleSaved.Contains(piece)) piece.OriginalScale = piece.Scale;
        }
        if (_troopPoses != null && edits.Files.TryGetValue(_troopPoses.HolsterSourcePath, out var holsters))
            _troopPoses.UpdateHolsterDefinitions(holsters.Document.Descendants("item_holster"));
        _equipmentPreviewModels.Clear();
        if (_previewWorkspace == 1) RenderLoadoutPreview(_troopPreviewTitle, _troopPreviewLoadout);
        else if (_previewWorkspace == 2)
        {
            if (_craftingSourceItemId is { } id && edits.ItemIds.Contains(id)) LoadCraftingItem(_equipmentItems[id]);
            else SetActiveCraftingPiece(_activeCraftingPiece);
        }
        SaveSettingsIfEnabled();
    }

    private static void AppendSourceElement(XElement parent, XElement child)
    {
        var indent = parent.Elements().FirstOrDefault()?.PreviousNode as XText;
        if (parent.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value))
        {
            if (indent != null && string.IsNullOrWhiteSpace(indent.Value)) trailing.AddBeforeSelf(new XText(indent.Value), child);
            else trailing.AddBeforeSelf(child);
        }
        else parent.Add(child);
    }

    private sealed class SourceXmlFile
    {
        internal byte[] Original { get; }
        internal XDocument Document { get; }
        internal HashSet<(XName Name, string Id)> UpdatedDefinitions { get; } = [];
        internal SourceXmlFile(string path)
        {
            Original = System.IO.File.ReadAllBytes(path);
            using var stream = new MemoryStream(Original);
            Document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        internal XElement Find(XName name, string id)
        {
            var entries = Document.Root?.DescendantsAndSelf().Where(element => element.Name == name &&
                string.Equals(GetAttributeValue(element, "id"), id, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (entries.Length != 1) throw new InvalidOperationException($"Expected one {name} '{id}' in its source XML; found {entries.Length}. Reload the source before saving.");
            UpdatedDefinitions.Add((name, id));
            return entries[0];
        }
        internal void Upsert(XElement definition, string containerName)
        {
            var container = Document.Root?.DescendantsAndSelf().SingleOrDefault(element => element.Name.LocalName == containerName)
                ?? throw new InvalidOperationException($"Source XML has no unique {containerName} container.");
            var id = GetAttributeValue(definition, "id")!;
            var existing = container.Elements(definition.Name).Where(element => GetAttributeValue(element, "id") == id).ToArray();
            if (existing.Length > 1) throw new InvalidOperationException($"Source XML has duplicate generated definition '{id}'.");
            if (existing.Length == 1) existing[0].ReplaceWith(new XElement(definition));
            else AppendSourceElement(container, new XElement(definition));
            UpdatedDefinitions.Add((definition.Name, id));
        }
        internal byte[] Serialize()
        {
            var encodingName = Document.Declaration?.Encoding ?? "utf-8";
            var encoding = encodingName.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                ? new UTF8Encoding(Original.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) : Encoding.GetEncoding(encodingName);
            var text = encoding.GetString(Original);
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = encoding, Indent = false,
                OmitXmlDeclaration = Document.Declaration == null, NewLineChars = text.Contains("\r\n") ? "\r\n" : "\n", NewLineHandling = NewLineHandling.Replace }))
                Document.Save(writer);
            return stream.ToArray();
        }
    }

    private sealed class SourceXmlEdits
    {
        internal Dictionary<string, SourceXmlFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> ItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<CraftingPieceNode> CraftingNodes { get; } = [];
        internal HashSet<CraftingPieceNode> CraftingScaleSaved { get; } = [];
        internal bool HasSharedPieceEdits { get; set; }
        internal SourceXmlFile File(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Source XML path is unknown.");
            path = Path.GetFullPath(path);
            if (!Files.TryGetValue(path, out var file)) Files[path] = file = new SourceXmlFile(path);
            return file;
        }
        internal void Write()
        {
            var staged = new List<(string Path, string Temporary, byte[] Bytes)>();
            var written = new List<string>();
            var suffix = ".viewport-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
            try
            {
                foreach (var (path, file) in Files)
                {
                    if (!System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Original))
                        throw new IOException($"Source XML changed while preparing the save: {path}. Reload and try again.");
                    var bytes = file.Serialize();
                    if (bytes.AsSpan().SequenceEqual(file.Original)) continue;
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    staged.Add((path, temporary, bytes)); System.IO.File.WriteAllBytes(temporary, bytes);
                }
                foreach (var file in staged)
                {
                    if (!System.IO.File.ReadAllBytes(file.Path).AsSpan().SequenceEqual(Files[file.Path].Original))
                        throw new IOException($"Source XML changed before writing: {file.Path}. Reload and try again.");
                    System.IO.File.Replace(file.Temporary, file.Path, file.Path + suffix);
                    written.Add(file.Path);
                }
            }
            catch
            {
                foreach (var path in written.AsEnumerable().Reverse())
                {
                    var stagedFile = staged.Single(file => file.Path == path);
                    if (System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(stagedFile.Bytes))
                        System.IO.File.Copy(path + suffix, path, overwrite: true);
                }
                throw;
            }
            finally { foreach (var file in staged) if (System.IO.File.Exists(file.Temporary)) System.IO.File.Delete(file.Temporary); }
        }
    }
}
