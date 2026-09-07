using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Text.Json.Serialization;

namespace AstraSize.Models
{
    public enum AdPrincipalType
    {
        User,
        Group,
        Preset
    }

    /// <summary>
    /// Active Directory or local user/group card
    /// </summary>
    public class AdPrincipalItem : INotifyPropertyChanged
    {
        public string AccountName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public AdPrincipalType PrincipalType { get; set; } = AdPrincipalType.Group;
        public string Domain { get; set; } = "DOMAIN";
        public string Description { get; set; } = string.Empty;

        public string FullAccountName => string.IsNullOrEmpty(Domain) ? AccountName : $"{Domain}\\{AccountName}";
        public string IconGlyph => PrincipalType == AdPrincipalType.User ? "👤" : "👥";
        public string BadgeBackground => PrincipalType == AdPrincipalType.User ? "#E0F2FE" : "#FEF3C7";
        public string BadgeForeground => PrincipalType == AdPrincipalType.User ? "#0369A1" : "#B45309";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Detailed ACL entry supporting both basic and advanced Windows permissions (14 flags)
    /// </summary>
    public class SimAclEntry : INotifyPropertyChanged
    {
        private string _accountName = string.Empty;
        private string _displayName = string.Empty;
        private AdPrincipalType _principalType = AdPrincipalType.Group;
        private FileSystemRights _rights = FileSystemRights.ReadAndExecute;
        private AccessControlType _accessType = AccessControlType.Allow;
        private bool _isInherited = false;
        private InheritanceFlags _inheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        private PropagationFlags _propagationFlags = PropagationFlags.None;
        private string _appliesTo = "このフォルダー、サブフォルダーおよびファイル";

        public string AccountName
        {
            get => _accountName;
            set { _accountName = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedRights)); }
        }

        public string DisplayName
        {
            get => string.IsNullOrEmpty(_displayName) ? _accountName : _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public AdPrincipalType PrincipalType
        {
            get => _principalType;
            set { _principalType = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconGlyph)); }
        }

        public FileSystemRights Rights
        {
            get => _rights;
            set
            {
                _rights = value;
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        public AccessControlType AccessType
        {
            get => _accessType;
            set { _accessType = value; OnPropertyChanged(); }
        }

        public bool IsInherited
        {
            get => _isInherited;
            set { _isInherited = value; OnPropertyChanged(); }
        }

        public InheritanceFlags InheritanceFlags
        {
            get => _inheritanceFlags;
            set
            {
                _inheritanceFlags = value;
                _appliesTo = AclInheritanceHelper.ToAppliesToString(_inheritanceFlags, _propagationFlags);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AppliesTo));
            }
        }

        public PropagationFlags PropagationFlags
        {
            get => _propagationFlags;
            set
            {
                _propagationFlags = value;
                _appliesTo = AclInheritanceHelper.ToAppliesToString(_inheritanceFlags, _propagationFlags);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AppliesTo));
            }
        }

        public string AppliesTo
        {
            get => _appliesTo;
            set
            {
                _appliesTo = value;
                var (inh, prop) = AclInheritanceHelper.FromAppliesToString(value);
                _inheritanceFlags = inh;
                _propagationFlags = prop;
                OnPropertyChanged();
                OnPropertyChanged(nameof(InheritanceFlags));
                OnPropertyChanged(nameof(PropagationFlags));
            }
        }

        [JsonIgnore]
        public string IconGlyph => PrincipalType == AdPrincipalType.User ? "👤" : "👥";

        [JsonIgnore]
        public string FormattedRights
        {
            get
            {
                if (IsFullControl) return "フルコントロール";
                if (IsModify) return "変更 (Modify)";
                if (IsReadExecute) return "読み取りと実行";
                if (IsRead) return "読み取り";
                if (IsWrite) return "書き込み";
                return "カスタム権限";
            }
        }

        // ========================================================
        // 基本アクセス許可 (Basic Permissions)
        // ========================================================
        [JsonIgnore]
        public bool IsFullControl
        {
            get => (_rights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            set
            {
                if (value)
                {
                    _rights |= FileSystemRights.FullControl;
                }
                else
                {
                    _rights &= ~FileSystemRights.FullControl;
                }
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        [JsonIgnore]
        public bool IsModify
        {
            get => (_rights & FileSystemRights.Modify) == FileSystemRights.Modify;
            set
            {
                if (value)
                {
                    _rights |= FileSystemRights.Modify;
                }
                else
                {
                    _rights &= ~FileSystemRights.Modify;
                    _rights &= ~FileSystemRights.FullControl;
                }
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        [JsonIgnore]
        public bool IsReadExecute
        {
            get => (_rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute;
            set
            {
                if (value)
                {
                    _rights |= FileSystemRights.ReadAndExecute;
                }
                else
                {
                    _rights &= ~FileSystemRights.ReadAndExecute;
                    _rights &= ~FileSystemRights.Modify;
                    _rights &= ~FileSystemRights.FullControl;
                }
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        [JsonIgnore]
        public bool IsListFolder
        {
            get => (_rights & FileSystemRights.ListDirectory) == FileSystemRights.ListDirectory;
            set
            {
                if (value) _rights |= FileSystemRights.ListDirectory;
                else
                {
                    _rights &= ~FileSystemRights.ListDirectory;
                    _rights &= ~FileSystemRights.ReadAndExecute;
                    _rights &= ~FileSystemRights.Modify;
                    _rights &= ~FileSystemRights.FullControl;
                }
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        [JsonIgnore]
        public bool IsRead
        {
            get => (_rights & FileSystemRights.Read) == FileSystemRights.Read;
            set
            {
                if (value) _rights |= FileSystemRights.Read;
                else
                {
                    _rights &= ~FileSystemRights.Read;
                    _rights &= ~FileSystemRights.ReadAndExecute;
                    _rights &= ~FileSystemRights.Modify;
                    _rights &= ~FileSystemRights.FullControl;
                }
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        [JsonIgnore]
        public bool IsWrite
        {
            get => (_rights & FileSystemRights.Write) == FileSystemRights.Write;
            set
            {
                if (value) _rights |= FileSystemRights.Write;
                else
                {
                    _rights &= ~FileSystemRights.Write;
                    _rights &= ~FileSystemRights.Modify;
                    _rights &= ~FileSystemRights.FullControl;
                }
                OnPropertyChanged();
                NotifyAllRightsProperties();
            }
        }

        // ========================================================
        // 高度なアクセス許可 (Advanced Permissions - 14項目)
        // ========================================================
        [JsonIgnore]
        public bool AdvTraverse
        {
            get => (_rights & FileSystemRights.Traverse) == FileSystemRights.Traverse;
            set { SetAdvFlag(FileSystemRights.Traverse, value); }
        }

        [JsonIgnore]
        public bool AdvListDirectory
        {
            get => (_rights & FileSystemRights.ListDirectory) == FileSystemRights.ListDirectory;
            set { SetAdvFlag(FileSystemRights.ListDirectory, value); }
        }

        [JsonIgnore]
        public bool AdvReadAttributes
        {
            get => (_rights & FileSystemRights.ReadAttributes) == FileSystemRights.ReadAttributes;
            set { SetAdvFlag(FileSystemRights.ReadAttributes, value); }
        }

        [JsonIgnore]
        public bool AdvReadExtendedAttributes
        {
            get => (_rights & FileSystemRights.ReadExtendedAttributes) == FileSystemRights.ReadExtendedAttributes;
            set { SetAdvFlag(FileSystemRights.ReadExtendedAttributes, value); }
        }

        [JsonIgnore]
        public bool AdvCreateFiles
        {
            get => (_rights & FileSystemRights.CreateFiles) == FileSystemRights.CreateFiles;
            set { SetAdvFlag(FileSystemRights.CreateFiles, value); }
        }

        [JsonIgnore]
        public bool AdvCreateDirectories
        {
            get => (_rights & FileSystemRights.CreateDirectories) == FileSystemRights.CreateDirectories;
            set { SetAdvFlag(FileSystemRights.CreateDirectories, value); }
        }

        [JsonIgnore]
        public bool AdvWriteAttributes
        {
            get => (_rights & FileSystemRights.WriteAttributes) == FileSystemRights.WriteAttributes;
            set { SetAdvFlag(FileSystemRights.WriteAttributes, value); }
        }

        [JsonIgnore]
        public bool AdvWriteExtendedAttributes
        {
            get => (_rights & FileSystemRights.WriteExtendedAttributes) == FileSystemRights.WriteExtendedAttributes;
            set { SetAdvFlag(FileSystemRights.WriteExtendedAttributes, value); }
        }

        [JsonIgnore]
        public bool AdvDelete
        {
            get => (_rights & FileSystemRights.Delete) == FileSystemRights.Delete;
            set { SetAdvFlag(FileSystemRights.Delete, value); }
        }

        [JsonIgnore]
        public bool AdvDeleteSubdirectoriesAndFiles
        {
            get => (_rights & FileSystemRights.DeleteSubdirectoriesAndFiles) == FileSystemRights.DeleteSubdirectoriesAndFiles;
            set { SetAdvFlag(FileSystemRights.DeleteSubdirectoriesAndFiles, value); }
        }

        [JsonIgnore]
        public bool AdvReadPermissions
        {
            get => (_rights & FileSystemRights.ReadPermissions) == FileSystemRights.ReadPermissions;
            set { SetAdvFlag(FileSystemRights.ReadPermissions, value); }
        }

        [JsonIgnore]
        public bool AdvChangePermissions
        {
            get => (_rights & FileSystemRights.ChangePermissions) == FileSystemRights.ChangePermissions;
            set { SetAdvFlag(FileSystemRights.ChangePermissions, value); }
        }

        [JsonIgnore]
        public bool AdvTakeOwnership
        {
            get => (_rights & FileSystemRights.TakeOwnership) == FileSystemRights.TakeOwnership;
            set { SetAdvFlag(FileSystemRights.TakeOwnership, value); }
        }

        [JsonIgnore]
        public bool AdvSynchronize
        {
            get => (_rights & FileSystemRights.Synchronize) == FileSystemRights.Synchronize;
            set { SetAdvFlag(FileSystemRights.Synchronize, value); }
        }

        private void SetAdvFlag(FileSystemRights flag, bool value)
        {
            if (value) _rights |= flag;
            else _rights &= ~flag;
            OnPropertyChanged(nameof(Rights));
            NotifyAllRightsProperties();
        }

        private void NotifyAllRightsProperties()
        {
            OnPropertyChanged(nameof(FormattedRights));
            OnPropertyChanged(nameof(IsFullControl));
            OnPropertyChanged(nameof(IsModify));
            OnPropertyChanged(nameof(IsReadExecute));
            OnPropertyChanged(nameof(IsListFolder));
            OnPropertyChanged(nameof(IsRead));
            OnPropertyChanged(nameof(IsWrite));

            OnPropertyChanged(nameof(AdvTraverse));
            OnPropertyChanged(nameof(AdvListDirectory));
            OnPropertyChanged(nameof(AdvReadAttributes));
            OnPropertyChanged(nameof(AdvReadExtendedAttributes));
            OnPropertyChanged(nameof(AdvCreateFiles));
            OnPropertyChanged(nameof(AdvCreateDirectories));
            OnPropertyChanged(nameof(AdvWriteAttributes));
            OnPropertyChanged(nameof(AdvWriteExtendedAttributes));
            OnPropertyChanged(nameof(AdvDelete));
            OnPropertyChanged(nameof(AdvDeleteSubdirectoriesAndFiles));
            OnPropertyChanged(nameof(AdvReadPermissions));
            OnPropertyChanged(nameof(AdvChangePermissions));
            OnPropertyChanged(nameof(AdvTakeOwnership));
            OnPropertyChanged(nameof(AdvSynchronize));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Virtual Folder Node in the FolderMorpher Simulation Tree
    /// </summary>
    public class SimFolderNode : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name = "新規フォルダ";
        private int _level = 0;
        private long _estimatedSizeBytes = 0;
        private bool _inheritAcl = true;
        private bool _isExpanded = true;
        private bool _isSelected = false;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(RelativePath)); }
        }

        public int Level
        {
            get => _level;
            set
            {
                _level = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IndentMargin));
                OnPropertyChanged(nameof(LevelPillText));
                OnPropertyChanged(nameof(LevelPillBackground));
                OnPropertyChanged(nameof(LevelPillForeground));
            }
        }

        public long EstimatedSizeBytes
        {
            get => _estimatedSizeBytes;
            set { _estimatedSizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedSize)); }
        }

        public bool InheritAcl
        {
            get => _inheritAcl;
            set { _inheritAcl = value; OnPropertyChanged(); OnPropertyChanged(nameof(InheritStatusBadge)); }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// N:1 mapping source paths (複数の現行フォルダを統合・紐づけ可能)
        /// </summary>
        public ObservableCollection<string> MappedSourcePaths { get; set; } = new();

        public ObservableCollection<SimFolderNode> Children { get; set; } = new();
        public ObservableCollection<SimAclEntry> AclEntries { get; set; } = new();

        [JsonIgnore]
        public SimFolderNode? Parent { get; set; }

        [JsonIgnore]
        public System.Windows.Thickness IndentMargin => new System.Windows.Thickness(Math.Min(12, Level) * 18, 0, 0, 0);

        [JsonIgnore]
        public string LevelPillText => Level == 0 ? "第1階層 (ルート)" : $"第{Level + 1}階層";

        [JsonIgnore]
        public string LevelPillBackground => Level switch
        {
            0 => "#065F46",
            1 => "#1E3A8A",
            2 => "#4C1D95",
            3 => "#831843",
            _ => "#713F12"
        };

        [JsonIgnore]
        public string LevelPillForeground => Level switch
        {
            0 => "#6EE7B7",
            1 => "#93C5FD",
            2 => "#C4B5FD",
            3 => "#FBCFE8",
            _ => "#FDE047"
        };

        [JsonIgnore]
        public string FormattedSize => FileItemNode.FormatBytes(EstimatedSizeBytes);

        [JsonIgnore]
        public string InheritStatusBadge => InheritAcl ? "🔗 継承" : "🛡️ 固有";

        [JsonIgnore]
        public bool HasMapping => MappedSourcePaths.Count > 0;

        [JsonIgnore]
        public bool IsMerged => MappedSourcePaths.Count > 1;

        [JsonIgnore]
        public string MappingBadgeText
        {
            get
            {
                if (MappedSourcePaths.Count == 0) return "新設 (未紐づけ)";
                if (MappedSourcePaths.Count == 1) return $"🔗 移行元: {MappedSourcePaths[0]}";
                return $"🔗 {MappedSourcePaths.Count}箇所の現行を統合中";
            }
        }

        [JsonIgnore]
        public string RelativePath
        {
            get
            {
                var parts = new List<string> { Name };
                var curr = Parent;
                while (curr != null)
                {
                    parts.Insert(0, curr.Name);
                    curr = curr.Parent;
                }
                return string.Join("\\", parts);
            }
        }

        public void NotifyMappingChanged()
        {
            OnPropertyChanged(nameof(MappedSourcePaths));
            OnPropertyChanged(nameof(HasMapping));
            OnPropertyChanged(nameof(IsMerged));
            OnPropertyChanged(nameof(MappingBadgeText));
        }

        public void NotifyAclChanged()
        {
            OnPropertyChanged(nameof(AclEntries));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Human-friendly Difference Review Model (Before vs After)
    /// </summary>
    public class SimDiffItem
    {
        public string DiffType { get; set; } = "階層移動";
        public string DiffTypeBadgeBackground { get; set; } = "#3B82F6";
        public string DiffTypeBadgeForeground { get; set; } = "#FFFFFF";

        public string SourcePath { get; set; } = string.Empty;
        public string SourceDetail { get; set; } = string.Empty;

        public string TargetPath { get; set; } = string.Empty;
        public string TargetDetail { get; set; } = string.Empty;

        public List<string> AclChanges { get; set; } = new();

        public string FormattedAclChanges => string.Join("\n", AclChanges);
    }

    /// <summary>
    /// FolderMorpher Project File (.fmorph)
    /// </summary>
    public class FolderMorphProject
    {
        public string Version { get; set; } = "2.0";
        public string AppName { get; set; } = "FolderMorpher";
        public string ProjectName { get; set; } = "新規ファイルサーバー移行設計";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastModifiedAt { get; set; } = DateTime.Now;
        public string SourceRootPath { get; set; } = string.Empty;
        public string TargetRootPath { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public List<SimFolderNode> RootFolders { get; set; } = new();
    }

    /// <summary>
    /// Helper for converting between NTFS InheritanceFlags/PropagationFlags and UI friendly strings
    /// </summary>
    public static class AclInheritanceHelper
    {
        public const string AppliesTo_All = "このフォルダー、サブフォルダーおよびファイル";
        public const string AppliesTo_ThisFolderOnly = "このフォルダーのみ";
        public const string AppliesTo_ThisFolderAndSubfolders = "このフォルダーおよびサブフォルダー";
        public const string AppliesTo_ThisFolderAndFiles = "このフォルダーおよびファイル";
        public const string AppliesTo_SubfoldersAndFilesOnly = "サブフォルダーおよびファイルのみ";
        public const string AppliesTo_SubfoldersOnly = "サブフォルダーのみ";
        public const string AppliesTo_FilesOnly = "ファイルのみ";

        public static string ToAppliesToString(InheritanceFlags inheritance, PropagationFlags propagation)
        {
            if (inheritance == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && propagation == PropagationFlags.None)
                return AppliesTo_All;
            if (inheritance == InheritanceFlags.None && propagation == PropagationFlags.None)
                return AppliesTo_ThisFolderOnly;
            if (inheritance == InheritanceFlags.ContainerInherit && propagation == PropagationFlags.None)
                return AppliesTo_ThisFolderAndSubfolders;
            if (inheritance == InheritanceFlags.ObjectInherit && propagation == PropagationFlags.None)
                return AppliesTo_ThisFolderAndFiles;
            if (inheritance == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && (propagation & PropagationFlags.InheritOnly) == PropagationFlags.InheritOnly)
                return AppliesTo_SubfoldersAndFilesOnly;
            if (inheritance == InheritanceFlags.ContainerInherit && (propagation & PropagationFlags.InheritOnly) == PropagationFlags.InheritOnly)
                return AppliesTo_SubfoldersOnly;
            if (inheritance == InheritanceFlags.ObjectInherit && (propagation & PropagationFlags.InheritOnly) == PropagationFlags.InheritOnly)
                return AppliesTo_FilesOnly;

            // フォールバック
            if (inheritance == InheritanceFlags.None) return AppliesTo_ThisFolderOnly;
            return AppliesTo_All;
        }

        public static (InheritanceFlags inheritance, PropagationFlags propagation) FromAppliesToString(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);

            return text.Trim() switch
            {
                AppliesTo_ThisFolderOnly => (InheritanceFlags.None, PropagationFlags.None),
                AppliesTo_ThisFolderAndSubfolders => (InheritanceFlags.ContainerInherit, PropagationFlags.None),
                AppliesTo_ThisFolderAndFiles => (InheritanceFlags.ObjectInherit, PropagationFlags.None),
                AppliesTo_SubfoldersAndFilesOnly => (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
                AppliesTo_SubfoldersOnly => (InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly),
                AppliesTo_FilesOnly => (InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
                _ => (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None)
            };
        }
    }

    /// <summary>
    /// Helper for bidirectional binding and mapping between UI controls and ACL entries
    /// </summary>
    public static class AclUiBindingHelper
    {
        public static AccessControlType IndexToAccessType(int selectedIndex)
            => selectedIndex == 1 ? AccessControlType.Deny : AccessControlType.Allow;

        public static int AccessTypeToIndex(AccessControlType accessType)
            => accessType == AccessControlType.Deny ? 1 : 0;

        public static void ApplyModalToEntry(SimAclEntry target, string displayName, int accessTypeIndex, string appliesToText)
        {
            target.DisplayName = displayName;
            target.AccessType = IndexToAccessType(accessTypeIndex);
            target.AppliesTo = appliesToText;
        }
    }
}
