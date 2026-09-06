using System;
using System.Collections.Generic;
using System.Security.AccessControl;

namespace AstraSize.Models
{
    public enum AclChangeType
    {
        Unchanged,
        Added,
        Modified,
        Removed
    }

    public class AclEntry
    {
        public string Identity { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public FileSystemRights Rights { get; set; }
        public AccessControlType AccessType { get; set; } = AccessControlType.Allow;
        public bool IsInherited { get; set; }

        public string FormattedRights
        {
            get => GetFriendlyRightsName(Rights);
            set { }
        }

        public string InheritanceText
        {
            get => IsInherited ? "親から継承" : "固有設定";
            set { }
        }

        // Simulation tracking
        public AclChangeType ChangeType { get; set; } = AclChangeType.Unchanged;
        public string ChangeDescription { get; set; } = string.Empty;

        public static string GetFriendlyRightsName(FileSystemRights rights)
        {
            if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                return "フル コントロール";
            if ((rights & FileSystemRights.Modify) == FileSystemRights.Modify)
                return "変更 (読取/書込/削除)";
            if ((rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                return "読取と実行";
            if ((rights & FileSystemRights.Read) == FileSystemRights.Read)
                return "読取のみ";
            if ((rights & FileSystemRights.Write) == FileSystemRights.Write)
                return "書込のみ";
            return rights.ToString();
        }
    }

    public class FolderAclNode
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool AreAccessRulesProtected { get; set; } // true if inheritance disabled
        public List<AclEntry> Entries { get; set; } = new();
        public List<FolderAclNode> Children { get; set; } = new();

        public string InheritanceBadge
        {
            get => AreAccessRulesProtected ? "🔒 [固有]" : "🔗 [継承]";
            set { }
        }

        // Simulation flags
        public bool HasSimulationChanges { get; set; }
    }

    public class AclSnapshot
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TargetPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Note { get; set; } = string.Empty;
        public string Sddl { get; set; } = string.Empty;
        public string FormattedDate => Timestamp.ToString("yyyy/MM/dd HH:mm:ss");
    }
}
