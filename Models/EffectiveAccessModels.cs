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
    /// フォルダツリー階層構造における実効権限の変化点分類
    /// </summary>
    public enum EffectiveAccessChangeType
    {
        InheritedSame = 0,       // 🔗 通常継承: 親からそのまま継承
        EnclaveGranted = 1,      // 🚨 飛び地 (権限獲得): 親はアクセス不可だが子でアクセス可能
        InheritanceSevered = 2,  // ⛔ 遮断 (権限消失): 親はアクセス可能だったが子で継承遮断/Denyにより消失
        PermissionChanged = 3    // ⚡ 権限変更: 親と異なる権限レベルへ昇格/変更
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
        /// 階層変化点分類 (飛び地・遮断・権限変更・通常継承)
        /// </summary>
        public EffectiveAccessChangeType ChangeType { get; set; } = EffectiveAccessChangeType.InheritedSame;

        /// <summary>
        /// 変化点 (飛び地または遮断または変更) かどうか
        /// </summary>
        public bool IsChangePoint => ChangeType != EffectiveAccessChangeType.InheritedSame;

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
            _ => "アクセス権なし (遮断)"
        };

        public string RightsBadgeBackground => PermissionLevel switch
        {
            EffectivePermissionLevel.FullControl => "#DC2626", // Red / High Privilege
            EffectivePermissionLevel.Modify => "#D97706",      // Amber
            EffectivePermissionLevel.ReadAndExecute => "#0284C7", // Sky Blue
            EffectivePermissionLevel.Read => "#059669",        // Emerald
            _ => "#94A3B8"                                      // Muted Gray / Severed
        };

        public string RightsBadgeForeground => "#FFFFFF";

        public string InheritanceBadgeText => IsInherited ? "継承" : "明示的付与";
        public string InheritanceBadgeColor => IsInherited ? "#64748B" : "#2563EB";

        public string ChangeBadgeText => ChangeType switch
        {
            EffectiveAccessChangeType.EnclaveGranted => "🚨 飛び地 (獲得)",
            EffectiveAccessChangeType.InheritanceSevered => "⛔ 遮断 (消失)",
            EffectiveAccessChangeType.PermissionChanged => "⚡ 権限変更",
            _ => "🔗 通常継承"
        };

        public string ChangeBadgeBackground => ChangeType switch
        {
            EffectiveAccessChangeType.EnclaveGranted => "#FEF2F2",
            EffectiveAccessChangeType.InheritanceSevered => "#FFF1F2",
            EffectiveAccessChangeType.PermissionChanged => "#FEF3C7",
            _ => "#F8FAFC"
        };

        public string ChangeBadgeBorder => ChangeType switch
        {
            EffectiveAccessChangeType.EnclaveGranted => "#FCA5A5",
            EffectiveAccessChangeType.InheritanceSevered => "#FDA4AF",
            EffectiveAccessChangeType.PermissionChanged => "#FCD34D",
            _ => "#E2E8F0"
        };

        public string ChangeBadgeForeground => ChangeType switch
        {
            EffectiveAccessChangeType.EnclaveGranted => "#DC2626",
            EffectiveAccessChangeType.InheritanceSevered => "#E11D48",
            EffectiveAccessChangeType.PermissionChanged => "#B45309",
            _ => "#64748B"
        };
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

        /// <summary>
        /// 実際にアクセス権が存在するフォルダー一覧 (後方互換・正本)
        /// </summary>
        public List<EffectiveFolderAccessItem> AccessibleFolders { get; set; } = new();

        /// <summary>
        /// 親ではアクセス可能だったがこの階層で継承切断またはDenyにより権限が消失したフォルダー一覧 (遮断)
        /// </summary>
        public List<EffectiveFolderAccessItem> SeveredFolders { get; set; } = new();

        /// <summary>
        /// 監査対象となった全フォルダー (アクセス可能 + 遮断)
        /// </summary>
        public IEnumerable<EffectiveFolderAccessItem> AllAuditItems => AccessibleFolders.Concat(SeveredFolders);

        public int TotalFoldersScanned { get; set; } = 0;
        public int FullControlCount { get; set; } = 0;
        public int ModifyCount { get; set; } = 0;
        public int ReadOnlyCount { get; set; } = 0;
        public int EnclaveCount { get; set; } = 0;
        public int SeveredCount => SeveredFolders.Count;

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
