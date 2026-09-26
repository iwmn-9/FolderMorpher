using AstraSize.Models;
using FolderMorpher.Models;
using FolderMorpher.Services;
using System.Reflection;

namespace FolderMorpher.UI;

public static class PresentationTestRunner
{
    public static void VerifyRuntimeLocalization(AstraSize.MainWindow window)
    {
        var language = LocalizationService.Instance;
        var original = language.CurrentLanguage;
        var apply = typeof(AstraSize.MainWindow).GetMethod("ApplyLocalization", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Main window localization entry point is missing.");
        try
        {
            language.SetLanguage(AppLanguage.English);
            apply.Invoke(window, null);
            if (window.SearchMenuOpen.Header?.ToString() != "📄 Open File" ||
                window.SearchMenuExportCsv.Header?.ToString() != "📑 Export to CSV (.csv)" ||
                window.ColMigWaveWarnings.Header?.ToString() != "Warnings" ||
                window.Resources["MigWave48hBadge"]?.ToString() != "⚠️ >48h" ||
                window.AuditCheckDuplicatesCheckBox.ToolTip?.ToString() != "Find files with identical SHA-256 hashes")
                throw new InvalidOperationException("Runtime English labels were not applied to context menus, wave table, or audit controls.");

            language.SetLanguage(AppLanguage.Japanese);
            apply.Invoke(window, null);
            if (window.SearchMenuOpen.Header?.ToString() != "📄 ファイルを開く" ||
                window.ColMigWaveWarnings.Header?.ToString() != "警告・判定" ||
                window.Resources["MigWave48hBadge"]?.ToString() != "⚠️ 48h超")
                throw new InvalidOperationException("Runtime Japanese labels were not restored.");
        }
        finally
        {
            language.SetLanguage(original);
            apply.Invoke(window, null);
        }
    }

    public static void VerifyStorageNodePresentation()
    {
        var parent = new FileItemNode(@"C:\Root", "Root", 1000, true);
        parent.Children.Add(new FileItemNode(@"C:\Root\Sub", "Sub", 400, true) { Parent = parent });
        if (parent.ExpandIcon != "▶" || parent.IconGlyph != "📁" || parent.FontWeight != "Bold")
            throw new InvalidOperationException("Storage node presentation bindings changed.");
        parent.IsExpanded = true;
        if (parent.ExpandIcon != "▼" || parent.BadgeBackground != "#FEF3C7")
            throw new InvalidOperationException("Storage node expanded presentation changed.");
        var lazy = new FileItemNode(@"C:\Root\Lazy", "Lazy", 1, true) { HasUnloadedChildren = true };
        var iconChanged = false;
        lazy.PropertyChanged += (_, args) => iconChanged |= args.PropertyName == nameof(lazy.ExpandIcon);
        lazy.IsExpanded = true;
        if (!lazy.HasChildren || lazy.ExpandIcon != "▼" || !iconChanged)
            throw new InvalidOperationException("Lazy folder expansion did not notify the arrow binding.");
        parent.Children[0].Percentage = 40;
        parent.Children[0].DiffBytes = 50 * 1024 * 1024;
        if (parent.Children[0].ShareFormatted != "40.0%" ||
            parent.Children[0].DiffFormatted != "+50 MB ▲")
            throw new InvalidOperationException("Storage share/diff presentation changed.");
    }

    public static void VerifyAuditAndSimulationPresentation()
    {
        var audit = new AuditItem { FileName = "sample.pdf", WasteScore = 80 };
        audit.ScoreBreakdown.Add(new ScoreFactorItem { NameJa = "検出理由", NameEn = "Detection reason", Points = 80 });
        if (audit.BadgeText != "PDF" || audit.ConfidenceBadgeBgHex != "#FEE2E2")
            throw new InvalidOperationException("Audit presentation bindings changed.");
        var originalLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.SetLanguage(AppLanguage.Japanese);
            if (audit.ScoreDisplay != "80点" || audit.ScoreBreakdownActionLabel != "内訳" ||
                !audit.ScoreBreakdownSummary.Contains("検出理由 (+80点) → 合計: 80点"))
                throw new InvalidOperationException("Japanese audit score action is missing.");
            LocalizationService.Instance.SetLanguage(AppLanguage.English);
            if (audit.ScoreDisplay != "80 pts" || audit.ScoreBreakdownActionLabel != "Details" ||
                !audit.ScoreBreakdownSummary.Contains("Detection reason (+80 pts) → Total: 80 pts"))
                throw new InvalidOperationException("English audit score action is missing.");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(originalLanguage);
        }
        audit.IssueType = AuditIssueType.Duplicate;
        audit.DuplicateGroupIndex = 1;
        if (audit.RowBackgroundHex != "#EFF6FF")
            throw new InvalidOperationException("Audit group palette changed.");
        var principal = new AdPrincipalItem { PrincipalType = AdPrincipalType.User };
        if (principal.IconGlyph != "👤" || principal.BadgeBackground != "#E0F2FE")
            throw new InvalidOperationException("Principal presentation bindings changed.");
        var folder = new SimFolderNode { Level = 1 };
        if (folder.IndentMargin != "18,0,0,0" || folder.LevelPillBackground != "#1E3A8A")
            throw new InvalidOperationException("Simulation tree presentation bindings changed.");
        var diff = new SimDiffItem { Kind = SimDiffKind.Consolidation };
        if (diff.DiffTypeBadgeBackground != "#D97706")
            throw new InvalidOperationException("Migration diff palette changed.");
        var acl = new SimAclEntry { PrincipalType = AdPrincipalType.Group };
        var aclDiff = new LiveAclDiffItem { PrincipalType = acl.PrincipalType };
        if (acl.IconGlyph != "👥" || aclDiff.IconGlyph != "👥")
            throw new InvalidOperationException("ACL principal icon changed.");
        var access = new EffectiveFolderAccessItem
        {
            PermissionLevel = EffectivePermissionLevel.FullControl,
            ChangeType = EffectiveAccessChangeType.EnclaveGranted
        };
        if (access.RightsBadgeBackground != "#DC2626" || access.ChangeBadgeForeground != "#DC2626")
            throw new InvalidOperationException("Effective access palette changed.");
    }
}
