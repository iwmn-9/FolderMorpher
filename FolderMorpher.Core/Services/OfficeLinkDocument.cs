using System.Xml;
using System.Xml.Linq;

namespace FolderMorpher.Services;

// Shared structured interpretation for discovery, search, repair and verification.
internal static class OfficeLinkDocument
{
    internal static XDocument Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static IEnumerable<XObject> Targets(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName == "Relationship" &&
                element.Name.NamespaceName == "http://schemas.openxmlformats.org/package/2006/relationships" &&
                string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase) &&
                element.Attribute("Target") is XAttribute target)
                yield return target;
            // Only formula nodes, never cell text or XML namespace declarations.
            if (element.Name.LocalName is "f" or "definedName" &&
                element.Name.NamespaceName == "http://schemas.openxmlformats.org/spreadsheetml/2006/main" &&
                (element.Value.Contains('[') || element.Value.Contains("\\\\") || element.Value.Contains(":/")))
                yield return element;
        }
    }

    internal static IEnumerable<string> Values(XDocument document) => Targets(document)
        .Select(target => target is XAttribute attribute ? attribute.Value : ((XElement)target).Value);

    internal static int Replace(XDocument document, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("Empty source link.");
        int changed = 0;
        foreach (var target in Targets(document))
        {
            string value = target is XAttribute attribute ? attribute.Value : ((XElement)target).Value;
            string updated = value.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase);
            if (value == updated) continue;
            if (target is XAttribute attr) attr.Value = updated;
            else ((XElement)target).Value = updated;
            changed++;
        }
        return changed;
    }
}
