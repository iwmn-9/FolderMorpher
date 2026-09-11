using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Text.Json.Serialization;
using FolderMorpher.Services;

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
            get => AclInheritanceHelper.ToAppliesToString(_inheritanceFlags, _propagationFlags);
            set
            {
                var parsed = AclInheritanceHelper.TryFromAppliesToString(value);
                if (parsed.HasValue)
                {
                    _inheritanceFlags = parsed.Value.inheritance;
                    _propagationFlags = parsed.Value.propagation;
                    OnPropertyChanged(nameof(InheritanceFlags));
                    OnPropertyChanged(nameof(PropagationFlags));
                }
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public string IconGlyph => PrincipalType == AdPrincipalType.User ? "👤" : "👥";

        [JsonIgnore]
        public string FormattedRights
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                if (IsFullControl) return isJa ? "フルコントロール" : "Full Control";
                if (IsModify) return isJa ? "変更 (Modify)" : "Modify";
                if (IsReadExecute) return isJa ? "読み取りと実行" : "Read & Execute";
                if (IsRead) return isJa ? "読み取り" : "Read";
                if (IsWrite) return isJa ? "書き込み" : "Write";
                return isJa ? "カスタム権限" : "Custom Rights";
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

        [JsonIgnore]
        public string InheritedBadgeLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "🔒 継承" : "🔒 Inherited";

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(FormattedRights));
            OnPropertyChanged(nameof(AppliesTo));
            OnPropertyChanged(nameof(InheritedBadgeLabel));
        }

        public SimAclEntry Clone()
        {
            return new SimAclEntry
            {
                AccountName = this.AccountName,
                DisplayName = this.DisplayName,
                PrincipalType = this.PrincipalType,
                Rights = this.Rights,
                AccessType = this.AccessType,
                IsInherited = this.IsInherited,
                InheritanceFlags = this.InheritanceFlags,
                PropagationFlags = this.PropagationFlags
            };
        }

        /// <summary>
        /// ドメイン修飾の有無（DOMAIN\User と User）を考慮して同一アカウントかを安全に判定する
        /// </summary>
        public static bool IsSameAccount(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;

            bool hasDomainA = a.Contains('\\');
            bool hasDomainB = b.Contains('\\');

            // 両方にドメインがあり、かつ完全一致しなかった場合は別アカウント
            if (hasDomainA && hasDomainB)
                return false;

            // 片方にのみドメインがある場合、アカウント名部分を比較
            var nameA = hasDomainA ? a.Substring(a.IndexOf('\\') + 1) : a;
            var nameB = hasDomainB ? b.Substring(b.IndexOf('\\') + 1) : b;
            return string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Windowsカーネルが自動付与する Synchronize ビットを吸収し、実質的な権限ビットが同一か判定する
        /// </summary>
        public static bool IsSameRights(FileSystemRights r1, FileSystemRights r2)
        {
            if (r1 == r2) return true;
            return (r1 & ~FileSystemRights.Synchronize) == (r2 & ~FileSystemRights.Synchronize);
        }

        /// <summary>
        /// ACEの適用先・権限主体のキー（アカウント、種別、継承・伝播フラグ）が一致するか判定
        /// </summary>
        public bool MatchesKey(SimAclEntry other)
        {
            if (other == null) return false;
            return IsSameAccount(this.AccountName, other.AccountName) &&
                   this.AccessType == other.AccessType &&
                   this.InheritanceFlags == other.InheritanceFlags &&
                   this.PropagationFlags == other.PropagationFlags;
        }

        /// <summary>
        /// 権限ビットまで含めて完全に同一のACEルールか判定（Synchronizeビットは正規化）
        /// </summary>
        public bool MatchesExact(SimAclEntry other)
        {
            return MatchesKey(other) && IsSameRights(this.Rights, other.Rights);
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
        public string LevelPillText
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                return Level == 0 
                    ? (isJa ? "第1階層 (ルート)" : "Level 1 (Root)") 
                    : (isJa ? $"第{Level + 1}階層" : $"Level {Level + 1}");
            }
        }

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
        public string InheritStatusBadge
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                return InheritAcl 
                    ? (isJa ? "🔗 継承" : "🔗 Inherited") 
                    : (isJa ? "🛡️ 固有" : "🛡️ Explicit");
            }
        }

        [JsonIgnore]
        public bool HasMapping => MappedSourcePaths.Count > 0;

        [JsonIgnore]
        public bool IsMerged => MappedSourcePaths.Count > 1;

        [JsonIgnore]
        public string MappingBadgeText
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                if (MappedSourcePaths.Count == 0) return isJa ? "新設 (未紐づけ)" : "New (Unmapped)";
                if (MappedSourcePaths.Count == 1) return isJa ? $"🔗 移行元: {MappedSourcePaths[0]}" : $"🔗 Source: {MappedSourcePaths[0]}";
                return isJa ? $"🔗 {MappedSourcePaths.Count}箇所の現行を統合中" : $"🔗 Merging {MappedSourcePaths.Count} sources";
            }
        }

        [JsonIgnore]
        public string QuickJumpRootToolTip => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "第1階層(ルート)へ一気ジャンプ" : "Jump to Root (Level 1)";

        [JsonIgnore]
        public string QuickPromoteToolTip => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "1階層昇格" : "Promote 1 Level";

        [JsonIgnore]
        public string QuickDemoteToolTip => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "1階層降格" : "Demote 1 Level";

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(LevelPillText));
            OnPropertyChanged(nameof(InheritStatusBadge));
            OnPropertyChanged(nameof(MappingBadgeText));
            OnPropertyChanged(nameof(QuickJumpRootToolTip));
            OnPropertyChanged(nameof(QuickPromoteToolTip));
            OnPropertyChanged(nameof(QuickDemoteToolTip));
            foreach (var acl in AclEntries)
            {
                acl.NotifyLanguageChanged();
            }
            foreach (var child in Children)
            {
                child.NotifyLanguageChanged();
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

        public static string ToAppliesToString(InheritanceFlags inheritance, PropagationFlags propagation, bool? isEnglish = null)
        {
            bool en = isEnglish ?? (LocalizationService.Instance.CurrentLanguage == AppLanguage.English);
            if (inheritance == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && propagation == PropagationFlags.None)
                return en ? AppliesTo_All_En : AppliesTo_All;
            if (inheritance == InheritanceFlags.None && propagation == PropagationFlags.None)
                return en ? AppliesTo_ThisFolderOnly_En : AppliesTo_ThisFolderOnly;
            if (inheritance == InheritanceFlags.ContainerInherit && propagation == PropagationFlags.None)
                return en ? AppliesTo_ThisFolderAndSubfolders_En : AppliesTo_ThisFolderAndSubfolders;
            if (inheritance == InheritanceFlags.ObjectInherit && propagation == PropagationFlags.None)
                return en ? AppliesTo_ThisFolderAndFiles_En : AppliesTo_ThisFolderAndFiles;
            if (inheritance == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && (propagation & PropagationFlags.InheritOnly) == PropagationFlags.InheritOnly)
                return en ? AppliesTo_SubfoldersAndFilesOnly_En : AppliesTo_SubfoldersAndFilesOnly;
            if (inheritance == InheritanceFlags.ContainerInherit && (propagation & PropagationFlags.InheritOnly) == PropagationFlags.InheritOnly)
                return en ? AppliesTo_SubfoldersOnly_En : AppliesTo_SubfoldersOnly;
            if (inheritance == InheritanceFlags.ObjectInherit && (propagation & PropagationFlags.InheritOnly) == PropagationFlags.InheritOnly)
                return en ? AppliesTo_FilesOnly_En : AppliesTo_FilesOnly;

            // フォールバック
            if (inheritance == InheritanceFlags.None) return en ? AppliesTo_ThisFolderOnly_En : AppliesTo_ThisFolderOnly;
            return en ? AppliesTo_All_En : AppliesTo_All;
        }

        public const string AppliesTo_All_En = "This folder, subfolders and files";
        public const string AppliesTo_ThisFolderOnly_En = "This folder only";
        public const string AppliesTo_ThisFolderAndSubfolders_En = "This folder and subfolders";
        public const string AppliesTo_ThisFolderAndFiles_En = "This folder and files";
        public const string AppliesTo_SubfoldersAndFilesOnly_En = "Subfolders and files only";
        public const string AppliesTo_SubfoldersOnly_En = "Subfolders only";
        public const string AppliesTo_FilesOnly_En = "Files only";

        public static (InheritanceFlags inheritance, PropagationFlags propagation)? TryFromAppliesToString(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var trimmed = text.Trim();
            if (string.Equals(trimmed, AppliesTo_ThisFolderOnly, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_ThisFolderOnly_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.None, PropagationFlags.None);

            if (string.Equals(trimmed, AppliesTo_ThisFolderAndSubfolders, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_ThisFolderAndSubfolders_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.ContainerInherit, PropagationFlags.None);

            if (string.Equals(trimmed, AppliesTo_ThisFolderAndFiles, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_ThisFolderAndFiles_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.ObjectInherit, PropagationFlags.None);

            if (string.Equals(trimmed, AppliesTo_SubfoldersAndFilesOnly, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_SubfoldersAndFilesOnly_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly);

            if (string.Equals(trimmed, AppliesTo_SubfoldersOnly, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_SubfoldersOnly_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly);

            if (string.Equals(trimmed, AppliesTo_FilesOnly, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_FilesOnly_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly);

            if (string.Equals(trimmed, AppliesTo_All, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, AppliesTo_All_En, StringComparison.OrdinalIgnoreCase))
                return (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);

            return null;
        }

        public static (InheritanceFlags inheritance, PropagationFlags propagation) FromAppliesToString(string? text)
        {
            var parsed = TryFromAppliesToString(text);
            return parsed ?? (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
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

    /// <summary>
    /// スケルトン展開の実行結果（検証・エラー集約構造体）
    /// タプル分解 (CreatedCount, Logs) をサポートし既存コードと100%後方互換
    /// </summary>
    public class DeploySkeletonResult
    {
        public int CreatedCount { get; set; }
        public int SkippedExistingCount { get; set; }
        public int AclAppliedCount { get; set; }
        public int FailedCount => Errors.Count;
        public List<string> Errors { get; set; } = new();
        public List<string> Logs { get; set; } = new();
        public List<string> DeployedFolderPaths { get; set; } = new();
        public bool IsSuccess => FailedCount == 0;

        public void Deconstruct(out int createdCount, out List<string> logs)
        {
            createdCount = CreatedCount;
            logs = Logs;
        }
    }

    /// <summary>
    /// スケルトン展開の実行計画（Plan-First 貫通オブジェクト）
    /// Preview から Commit まで同一インスタンスを通し、計画と適用の乖離を物理的に排除する
    /// </summary>
    public class SkeletonDeployPlan
    {
        public string DestinationRoot { get; set; } = string.Empty;
        public List<SkeletonFolderAction> FolderActions { get; set; } = new();
        public List<SimDiffItem> DiffReviews { get; set; } = new();
        public int PlannedCreateCount => FolderActions.Count(a => !a.IsExisting);
        public int PlannedExistingCount => FolderActions.Count(a => a.IsExisting);
    }

    public class SkeletonFolderAction
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FullTargetPath { get; set; } = string.Empty;
        public bool IsExisting { get; set; }
        public bool InheritAcl { get; set; } = true;
        public List<SimAclEntry> AclEntries { get; set; } = new();
    }
}

