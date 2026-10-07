using System.Text;
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

            // Only genuine external formula nodes, never cell text, XML namespaces, or internal table structured references [Amount].
            if (element.Name.LocalName is "f" or "definedName" &&
                element.Name.NamespaceName == "http://schemas.openxmlformats.org/spreadsheetml/2006/main" &&
                IsExternalFormula(element.Value))
                yield return element;
        }
    }

    private static bool IsExternalFormula(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return false;
        if (formula.Contains("\\\\") || formula.Contains(":/")) return true;

        // Structured table references like SUM(Table1[Amount]) or [@Column] do not have workbook extensions or path separators.
        // Genuine external workbook references use [Book.xlsx] or [1]! or include path delimiters.
        int open = formula.IndexOf('[');
        while (open >= 0)
        {
            int close = formula.IndexOf(']', open);
            if (close < 0) break;
            string inside = formula.Substring(open + 1, close - open - 1);
            if (inside.Contains('\\') || inside.Contains('/') ||
                inside.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                inside.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase) ||
                inside.EndsWith(".xlsb", StringComparison.OrdinalIgnoreCase) ||
                inside.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) ||
                inside.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                (int.TryParse(inside, out _) && close + 1 < formula.Length && formula[close + 1] == '!'))
            {
                return true;
            }
            open = formula.IndexOf('[', close + 1);
        }
        return false;
    }

    internal static IEnumerable<string> Values(XDocument document) => Targets(document)
        .Select(target => target is XAttribute attribute ? attribute.Value : ((XElement)target).Value);

    internal static int Replace(XDocument document, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("Empty source link.");
        int changed = 0;
        foreach (var target in Targets(document))
        {
            if (target is XAttribute attribute)
            {
                string value = attribute.Value;
                string updated = value.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase);
                if (value != updated)
                {
                    attribute.Value = updated;
                    changed++;
                }
            }
            else if (target is XElement element)
            {
                string value = element.Value;
                string updated = ReplaceInFormula(value, oldValue, newValue);
                if (value != updated)
                {
                    element.Value = updated;
                    changed++;
                }
            }
        }
        return changed;
    }

    private static string ReplaceInFormula(string formula, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(formula) || string.IsNullOrEmpty(oldValue)) return formula;

        // In Excel formulas, paths inside single quotes escape single quotes as double single quotes:
        // 'C:\Old Path\[Book.xlsx]Sheet'!A1 -> 'C:\O''Brien\[Book.xlsx]Sheet'!A1
        string oldEscaped = oldValue.Replace("'", "''");
        string newEscaped = newValue.Replace("'", "''");

        if (formula.Contains(oldEscaped, StringComparison.OrdinalIgnoreCase))
        {
            return formula.Replace(oldEscaped, newEscaped, StringComparison.OrdinalIgnoreCase);
        }

        if (formula.Contains(oldValue, StringComparison.OrdinalIgnoreCase))
        {
            return ReplaceWithQuoteAwareness(formula, oldValue, newValue, newEscaped);
        }

        return formula;
    }

    private static string ReplaceWithQuoteAwareness(string formula, string oldValue, string newValue, string newEscaped)
    {
        var sb = new StringBuilder();
        int i = 0;
        bool inQuote = false;
        while (i < formula.Length)
        {
            if (formula[i] == '\'')
            {
                if (inQuote && i + 1 < formula.Length && formula[i + 1] == '\'')
                {
                    sb.Append("''");
                    i += 2;
                    continue;
                }
                inQuote = !inQuote;
                sb.Append('\'');
                i++;
                continue;
            }

            if (i + oldValue.Length <= formula.Length &&
                string.Equals(formula.Substring(i, oldValue.Length), oldValue, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(inQuote ? newEscaped : newValue);
                i += oldValue.Length;
                continue;
            }

            sb.Append(formula[i]);
            i++;
        }
        return sb.ToString();
    }
}
