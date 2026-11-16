using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    // Module XSLTs (LOTRLOME_Armory's weapon_descriptions.xslt) extend vanilla descriptions with
    // <xsl:template match="WeaponDescription[@id='X']/AvailablePieces"> blocks.
    private static readonly Regex WeaponDescriptionPatch = new(@"WeaponDescription\[@id='([^']+)'\]/AvailablePieces", RegexOptions.Compiled);
    private readonly Dictionary<string, HashSet<string>> _weaponDescriptionPieces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _weaponDescriptionClasses = new(StringComparer.OrdinalIgnoreCase);

    private void AddWeaponDescriptionPieces(string descriptionId, XElement source)
    {
        if (!_weaponDescriptionPieces.TryGetValue(descriptionId, out var pieces))
            _weaponDescriptionPieces[descriptionId] = pieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in source.Descendants().Where(element => element.Name.LocalName == "AvailablePiece"))
            if (GetAttributeValue(piece, "id") is { } id) pieces.Add(id);
    }

    // The engine gives a crafted item every description of its template whose AvailablePieces hold all of its
    // pieces, in template order; the first one is the primary usage (a TwoHandedPolearm spear is a 1H polearm).
    private string? CraftedPrimaryWeaponClass(XElement item)
    {
        if (_weaponDescriptionPieces.Count == 0) return null;
        var templateId = GetAttributeValue(item, "crafting_template") ?? "";
        if (!_equipmentTemplates.TryGetValue(templateId, out var template)) return null;
        var pieces = item.Descendants().Where(element => element.Name.LocalName == "Piece")
            .Select(element => GetAttributeValue(element, "id")).OfType<string>().Where(id => id.Length > 0).ToArray();
        if (pieces.Length == 0) return null;
        foreach (var description in template.Elements().Where(element => element.Name.LocalName == "WeaponDescriptions")
                     .SelectMany(element => element.Elements()).Select(element => GetAttributeValue(element, "id")))
        {
            if (description == null || !_weaponDescriptionPieces.TryGetValue(description, out var available)) continue;
            if (pieces.All(available.Contains)) return _weaponDescriptionClasses.GetValueOrDefault(description, description);
        }
        return null;
    }
}
