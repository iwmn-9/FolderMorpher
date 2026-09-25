using System.Collections.ObjectModel;

namespace FolderMorpher.Models
{
    public enum AdOuNodeType
    {
        DomainRoot,
        OrganizationalUnit,
        Container,
        LocalMachine,
        LocalCategory
    }

    public class AdOuNode
    {
        public string Name { get; set; } = string.Empty;
        public string DistinguishedName { get; set; } = string.Empty;
        public AdOuNodeType NodeType { get; set; }
        public string Icon => NodeType switch
        {
            AdOuNodeType.DomainRoot => "🌐",
            AdOuNodeType.OrganizationalUnit => "📁",
            AdOuNodeType.Container => "📦",
            AdOuNodeType.LocalMachine => "💻",
            AdOuNodeType.LocalCategory => "👥",
            _ => "📁"
        };

        public ObservableCollection<AdOuNode> Children { get; set; } = new();
        public bool IsExpanded { get; set; }
        public bool IsSelected { get; set; }

        public override string ToString() => Name;
    }
}
