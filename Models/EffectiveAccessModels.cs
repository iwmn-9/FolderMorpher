using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using AstraSize.Models;

using FolderMorpher.Services;

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
        Baseline = 0,            // 🏁 基準点: 走査ルート自身
        InheritedSame = 1,       // 🔗 通常継承: 親からそのまま継承 (実効権限も同一)
        ExplicitBoundary = 2,    // 🔧 明示化境界: 親と実効権限は同一だが明示ACE化
        EnclaveGranted = 3,      // 🚨 飛び地 (権限獲得): 親はアクセス不可だが子でアクセス可能
        InheritanceSevered = 4,  // ⛔ 遮断 (権限消失): 親はアクセス可能だったが子で継承遮断/Denyにより消失
        PermissionChanged = 5,   // ⚡ 権限変更: 親と異なる権限レベルへ昇格/変更
        ScanUnavailable = 6      // ⚠️ 走査不能: 管理者権限不足やネットワークエラー等でACL取得不能
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
        /// 階層変化点分類 (基準点・飛び地・遮断・走査不能・明示化境界・権限変更・通常継承)
        /// </summary>
        public EffectiveAccessChangeType ChangeType { get; set; } = EffectiveAccessChangeType.InheritedSame;

        /// <summary>
        /// 変化点 (通常継承および基準点以外) かどうか
        /// </summary>
        public bool IsChangePoint => ChangeType != EffectiveAccessChangeType.InheritedSame
                                  && ChangeType != EffectiveAccessChangeType.Baseline;

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
            EffectivePermissionLevel.FullControl => Strings.SecFullControl,
            EffectivePermissionLevel.Modify => Strings.SecModify,
            EffectivePermissionLevel.ReadAndExecute => Strings.SecReadExecute,
            EffectivePermissionLevel.Read => Strings.SecRead,
            _ => Strings.RevRightsNone
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

        public string InheritanceBadgeText => IsInherited ? Strings.RevInheritedBadge : Strings.RevExplicitBadge;
        public string InheritanceBadgeColor => IsInherited ? "#64748B" : "#2563EB";

        public string ChangeBadgeText => ChangeType switch
        {
            EffectiveAccessChangeType.Baseline => Strings.RevChangeBaseline,
            EffectiveAccessChangeType.EnclaveGranted => Strings.RevChangeEnclave,
            EffectiveAccessChangeType.InheritanceSevered => Strings.RevChangeSevered,
            EffectiveAccessChangeType.ScanUnavailable => Strings.RevChangeUnavailable,
            EffectiveAccessChangeType.ExplicitBoundary => Strings.RevChangeExplicitBoundary,
            EffectiveAccessChangeType.PermissionChanged => Strings.RevChangeModified,
            _ => Strings.RevChangeInherited
        };

        public string ChangeBadgeBackground => ChangeType switch
        {
            EffectiveAccessChangeType.Baseline => "#F1F5F9",
            EffectiveAccessChangeType.EnclaveGranted => "#FEF2F2",
            EffectiveAccessChangeType.InheritanceSevered => "#FFF1F2",
            EffectiveAccessChangeType.ScanUnavailable => "#FFFBEB",
            EffectiveAccessChangeType.ExplicitBoundary => "#F0F9FF",
            EffectiveAccessChangeType.PermissionChanged => "#FEF3C7",
            _ => "#F8FAFC"
        };

        public string ChangeBadgeBorder => ChangeType switch
        {
            EffectiveAccessChangeType.Baseline => "#CBD5E1",
            EffectiveAccessChangeType.EnclaveGranted => "#FCA5A5",
            EffectiveAccessChangeType.InheritanceSevered => "#FDA4AF",
            EffectiveAccessChangeType.ScanUnavailable => "#FDE68A",
            EffectiveAccessChangeType.ExplicitBoundary => "#BAE6FD",
            EffectiveAccessChangeType.PermissionChanged => "#FCD34D",
            _ => "#E2E8F0"
        };

        public string ChangeBadgeForeground => ChangeType switch
        {
            EffectiveAccessChangeType.Baseline => "#475569",
            EffectiveAccessChangeType.EnclaveGranted => "#DC2626",
            EffectiveAccessChangeType.InheritanceSevered => "#E11D48",
            EffectiveAccessChangeType.ScanUnavailable => "#D97706",
            EffectiveAccessChangeType.ExplicitBoundary => "#0284C7",
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
        /// 管理者自身の権限不足やエラー等によりACLを走査・判定できなかったフォルダー一覧 (走査不能)
        /// </summary>
        public List<EffectiveFolderAccessItem> UnavailableFolders { get; set; } = new();

        /// <summary>
        /// 監査対象となった全フォルダー (アクセス可能 + 遮断 + 走査不能)
        /// </summary>
        public IEnumerable<EffectiveFolderAccessItem> AllAuditItems => AccessibleFolders.Concat(SeveredFolders).Concat(UnavailableFolders);

        public int TotalFoldersScanned { get; set; } = 0;
        public int FullControlCount { get; set; } = 0;
        public int ModifyCount { get; set; } = 0;
        public int ReadOnlyCount { get; set; } = 0;
        public int EnclaveCount { get; set; } = 0;
        public int SeveredCount => SeveredFolders.Count;
        public int UnavailableCount => UnavailableFolders.Count;
        public int ExplicitBoundaryCount { get; set; } = 0;

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
