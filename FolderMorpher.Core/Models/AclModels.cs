using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using FolderMorpher.Services;

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
            get => IsInherited 
                ? (LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "親から継承" : "Inherited") 
                : (LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "固有設定" : "Explicit");
            set { }
        }

        // Simulation tracking
        public AclChangeType ChangeType { get; set; } = AclChangeType.Unchanged;
        public string ChangeDescription { get; set; } = string.Empty;

        public static string GetFriendlyRightsName(FileSystemRights rights)
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                return isJa ? "フル コントロール" : "Full Control";
            if ((rights & FileSystemRights.Modify) == FileSystemRights.Modify)
                return isJa ? "変更 (読取/書込/削除)" : "Modify (Read/Write/Delete)";
            if ((rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                return isJa ? "読取と実行" : "Read & Execute";
            if ((rights & FileSystemRights.Read) == FileSystemRights.Read)
                return isJa ? "読取のみ" : "Read Only";
            if ((rights & FileSystemRights.Write) == FileSystemRights.Write)
                return isJa ? "書込のみ" : "Write Only";
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
        public string ChangeSummary { get; set; } = string.Empty;
        public string FormattedDate => Timestamp.ToString("yyyy/MM/dd HH:mm:ss");
    }

    public class LiveAclPanelModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _folderPath = string.Empty;
        private string _folderName = string.Empty;
        private bool _inheritAcl = true;
        private bool _hasChanges = false;
        private string _statusMessage = string.Empty;
        private bool _isApplying = false;

        public string PanelId { get; set; } = Guid.NewGuid().ToString("N");

        public string FolderPath
        {
            get => _folderPath;
            set
            {
                _folderPath = value;
                FolderName = System.IO.Path.GetFileName(value.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(FolderName)) FolderName = value;
                OnPropertyChanged();
            }
        }

        public string FolderName
        {
            get => _folderName;
            set { _folderName = value; OnPropertyChanged(); }
        }

        public bool InheritAcl
        {
            get => _inheritAcl;
            set
            {
                if (_inheritAcl != value)
                {
                    _inheritAcl = value;
                    if (!_inheritAcl)
                    {
                        // High 1: 継承OFF時、既存の継承ACEを明示ACEへ自動変換・昇格（ロックアウト防止・編集可能化）
                        foreach (var entry in CurrentAclEntries)
                        {
                            if (entry.IsInherited)
                            {
                                entry.IsInherited = false;
                            }
                        }
                    }
                    else
                    {
                        // 継承ON復元時: Originalで継承だったルールはIsInheritedを復元
                        foreach (var cur in CurrentAclEntries)
                        {
                            var orig = OriginalAclEntries.FirstOrDefault(o => o.MatchesKey(cur));
                            if (orig != null && orig.IsInherited)
                            {
                                cur.IsInherited = true;
                            }
                        }
                    }
                    OnPropertyChanged();
                    UpdateChangeStatus();
                }
            }
        }

        public bool HasChanges
        {
            get => _hasChanges;
            set { _hasChanges = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public bool IsApplying
        {
            get => _isApplying;
            set { _isApplying = value; OnPropertyChanged(); }
        }

        public string OriginalSddl { get; set; } = string.Empty;
        public bool OriginalInheritAcl { get; set; } = true;

        /// <summary>
        /// 初回読み込み時の明示ACEスナップショット（不変）
        /// </summary>
        public List<SimAclEntry> OriginalAclEntries { get; set; } = new();

        /// <summary>
        /// 現在UIで編集中の明示ACEコレクション
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<SimAclEntry> CurrentAclEntries { get; set; } = new();

        public void UpdateChangeStatus()
        {
            if (InheritAcl != OriginalInheritAcl)
            {
                HasChanges = true;
                return;
            }

            if (CurrentAclEntries.Count != OriginalAclEntries.Count)
            {
                HasChanges = true;
                return;
            }

            // High 2: マルチセット（多重集合）ペアリングによる完全一致チェック
            var remainingOrig = new List<SimAclEntry>(OriginalAclEntries);
            foreach (var cur in CurrentAclEntries)
            {
                var matchedIndex = remainingOrig.FindIndex(o => o.MatchesExact(cur) && o.IsInherited == cur.IsInherited);
                if (matchedIndex >= 0)
                {
                    remainingOrig.RemoveAt(matchedIndex);
                }
                else
                {
                    HasChanges = true;
                    return;
                }
            }

            HasChanges = (remainingOrig.Count > 0);
        }

        public string InheritCheckboxLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
            ? "親フォルダからの権限継承を含める"
            : "Inherit permissions from parent";

        public string UnappliedBadgeLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
            ? "● 未適用"
            : "● Unapplied";

        public string DropZoneHintLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
            ? "📥 アカウントをドロップして追加"
            : "📥 Drop account here to add";

        public string InheritedBadgeLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
            ? "🔒 継承"
            : "🔒 Inherited";

        public string RollbackButtonLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
            ? "↩️ ロールバック"
            : "↩️ Rollback";

        public string CheckButtonLabel => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
            ? "🔍 チェック"
            : "🔍 Check";

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(InheritCheckboxLabel));
            OnPropertyChanged(nameof(UnappliedBadgeLabel));
            OnPropertyChanged(nameof(DropZoneHintLabel));
            OnPropertyChanged(nameof(InheritedBadgeLabel));
            OnPropertyChanged(nameof(RollbackButtonLabel));
            OnPropertyChanged(nameof(CheckButtonLabel));
            foreach (var e in CurrentAclEntries)
            {
                e.NotifyLanguageChanged();
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// 外部ACL変更との競合を検出した際にスローされる例外
    /// </summary>
    public class AclConflictException : InvalidOperationException
    {
        public string FolderPath { get; }
        public string CurrentSddl { get; }
        public string ExpectedSddl { get; }

        public AclConflictException(string folderPath, string currentSddl, string expectedSddl)
            : base($"フォルダー「{folderPath}」のACLは読み込み後に外部で変更されています。")
        {
            FolderPath = folderPath;
            CurrentSddl = currentSddl;
            ExpectedSddl = expectedSddl;
        }
    }

    public enum LiveAclDiffType
    {
        Added,
        Removed,
        Modified,
        Untouched
    }

    /// <summary>
    /// Live ACL Dry-Run 差分プレビュー用アイテム
    /// </summary>
    public class LiveAclDiffItem
    {
        public LiveAclDiffType DiffType { get; set; } = LiveAclDiffType.Added;

        public string DiffTypeDisplay
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                return DiffType switch
                {
                    LiveAclDiffType.Added => isJa ? "＋ 追加" : "+ Add",
                    LiveAclDiffType.Removed => isJa ? "ー 削除" : "- Remove",
                    LiveAclDiffType.Modified => isJa ? "〜 変更" : "~ Modify",
                    LiveAclDiffType.Untouched => isJa ? "＝ 維持" : "= Keep",
                    _ => isJa ? "変更" : "Change"
                };
            }
        }

        public string BadgeBackground => DiffType switch
        {
            LiveAclDiffType.Added => "#DCFCE7",
            LiveAclDiffType.Removed => "#FEE2E2",
            LiveAclDiffType.Modified => "#DBEAFE",
            _ => "#F1F5F9"
        };

        public string BadgeForeground => DiffType switch
        {
            LiveAclDiffType.Added => "#15803D",
            LiveAclDiffType.Removed => "#B91C1C",
            LiveAclDiffType.Modified => "#1D4ED8",
            _ => "#64748B"
        };

        public string AccountName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "👤";
        public AccessControlType AccessType { get; set; } = AccessControlType.Allow;
        public string AccessTypeDisplay
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                return AccessType == AccessControlType.Deny 
                    ? (isJa ? "⛔ 拒否" : "⛔ Deny") 
                    : (isJa ? "✅ 許可" : "✅ Allow");
            }
        }
        public string AccessTypeBadgeBg => AccessType == AccessControlType.Deny ? "#FEE2E2" : "#F0FDF4";
        public string AccessTypeBadgeFg => AccessType == AccessControlType.Deny ? "#B91C1C" : "#15803D";
        public string BeforeRights { get; set; } = "―";
        public string AfterRights { get; set; } = "―";
        public string Details { get; set; } = string.Empty;
        public string AppliesTo { get; set; } = "このフォルダー、サブフォルダーおよびファイル";
        public bool IsInherited { get; set; } = false;
    }

    /// <summary>
    /// Live ACL 実行計画 (Change Plan)
    /// Dry-Run (Check) から本番適用 (Commit)、OS実態検証 (Verify) まで同一インスタンスで貫通する正本
    /// </summary>
    public class AclChangePlan
    {
        public string FolderPath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string OriginalSddl { get; set; } = string.Empty;

        // 継承変更
        public bool InheritanceBefore { get; set; }
        public bool InheritanceAfter { get; set; }
        public bool InheritanceChanged => InheritanceBefore != InheritanceAfter;
        public int InheritedAcesPromotedCount { get; set; }

        // 差分分類 (ピンポイント適用用)
        public List<SimAclEntry> Added { get; set; } = new();
        public List<SimAclEntry> Removed { get; set; } = new();
        public List<(SimAclEntry OldEntry, SimAclEntry NewEntry)> Modified { get; set; } = new();
        public List<SimAclEntry> Untouched { get; set; } = new();

        // プレビュー表示用アイテム
        public List<LiveAclDiffItem> DiffItems { get; set; } = new();

        // 期待される変更後ACE一覧 (Verify突合用)
        public List<SimAclEntry> ExpectedAfterEntries { get; set; } = new();

        public bool HasChanges => InheritanceChanged || Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
        public int TotalMutations => Added.Count + Removed.Count + Modified.Count;
    }

    /// <summary>
    /// Live ACL 適用後の正常性検証 (Verify) 結果
    /// </summary>
    public class AclVerificationResult
    {
        public bool IsSuccess { get; set; } = true;
        public string StatusText { get; set; } = "正常";
        public List<string> Discrepancies { get; set; } = new();
    }
}
