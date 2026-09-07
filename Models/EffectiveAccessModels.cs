using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using AstraSize.Models;

namespace FolderMorpher.Models
{
    /// <summary>
    /// Effective permission level for human-readable display and sorting
    /// </summary>
    public enum EffectivePermissionLevel
    {
        None = 0,
        Read = 1,
        ReadAndExecute = 2,
        Modify = 3,
        FullControl = 4
    }

    /// <summary>
    /// An item representing a folder where the target user/group has effective permissions
    /// </summary>
    public class EffectiveFolderAccessItem
    {
        public string FolderPath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public EffectivePermissionLevel PermissionLevel { get; set; } = EffectivePermissionLevel.Read;
        public FileSystemRights AllowedRights { get; set; } = 0;
        public FileSystemRights DeniedRights { get; set; } = 0;
        public bool HasDeny { get; set; } = false;
        public bool IsInherited { get; set; } = false;

        /// <summary>
        /// Source of the permission grant: Direct, Specific Group, Nested Group, or Special Principal
        /// </summary>
        public string GrantSource { get; set; } = string.Empty;

        /// <summary>
        /// Detailed trail of how this permission was granted (e.g. "User -> Group A -> Group B")
        /// </summary>
        public string GrantPathTrace { get; set; } = string.Empty;

        public string FormattedRights => PermissionLevel switch
        {
            EffectivePermissionLevel.FullControl => "フルコントロール",
            EffectivePermissionLevel.Modify => "変更 (Modify)",
            EffectivePermissionLevel.ReadAndExecute => "読み取りと実行",
            EffectivePermissionLevel.Read => "読み取り",
            _ => "カスタム"
        };

        public string RightsBadgeBackground => PermissionLevel switch
        {
            EffectivePermissionLevel.FullControl => "#DC2626", // Red / High Privilege
            EffectivePermissionLevel.Modify => "#D97706",      // Amber
            EffectivePermissionLevel.ReadAndExecute => "#0284C7", // Sky Blue
            EffectivePermissionLevel.Read => "#059669",        // Emerald
            _ => "#64748B"                                      // Slate
        };

        public string RightsBadgeForeground => "#FFFFFF";

        public string InheritanceBadgeText => IsInherited ? "継承" : "明示的付与";
        public string InheritanceBadgeColor => IsInherited ? "#64748B" : "#2563EB";
    }

    /// <summary>
    /// Information about a group the target principal belongs to (supporting multi-level nesting)
    /// </summary>
    public class PrincipalGroupMembership
    {
        public string GroupName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Sid { get; set; } = string.Empty;
        public bool IsDirect { get; set; } = true;
        public int NestingDepth { get; set; } = 0;
        public string MembershipPath { get; set; } = string.Empty;

        public string DirectStatusText => IsDirect ? "直接所属" : $"入れ子所属 (深度 {NestingDepth})";
        public string DirectBadgeColor => IsDirect ? "#2563EB" : "#7C3AED";
    }

    /// <summary>
    /// Complete audit report for Effective Access
    /// </summary>
    public class EffectiveAccessAuditReport
    {
        public string TargetAccountName { get; set; } = string.Empty;
        public string TargetDisplayName { get; set; } = string.Empty;
        public AdPrincipalType PrincipalType { get; set; } = AdPrincipalType.User;
        public string RootFolderPath { get; set; } = string.Empty;
        public DateTime ScanTimestamp { get; set; } = DateTime.Now;

        public List<PrincipalGroupMembership> GroupMemberships { get; set; } = new();
        public List<EffectiveFolderAccessItem> AccessibleFolders { get; set; } = new();

        public int TotalFoldersScanned { get; set; } = 0;
        public int FullControlCount { get; set; } = 0;
        public int ModifyCount { get; set; } = 0;
        public int ReadOnlyCount { get; set; } = 0;

        public EffectiveAccessResolutionMode ResolutionMode { get; set; } = EffectiveAccessResolutionMode.DirectAclOnly;
        public string ResolutionStatusText { get; set; } = string.Empty;
        public bool IsUncPath => RootFolderPath.StartsWith(@"\\");
        public string UncShareNotice => IsUncPath
            ? "※UNC共有フォルダです。ファイルサーバー上のSMB共有アクセス権（Share Permissions）の上限も併せて適用されます。"
            : string.Empty;
    }

    public enum EffectiveAccessResolutionMode
    {
        ActiveDirectory = 0,
        CurrentLogonUserLocal = 1,
        DirectAclOnly = 2
    }
}
