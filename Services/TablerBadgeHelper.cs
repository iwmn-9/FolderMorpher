using System;
using System.IO;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Tabler File-Type バッジの配色・テキスト構造体
    /// </summary>
    public readonly record struct TablerBadgeInfo(string Text, string Background, string BorderBrush, string Foreground);

    /// <summary>
    /// アプリケーション全体のファイル・フォルダー向け Tabler File-Type バッジ生成の正本（Single Source of Truth）。
    /// 拡張子に応じた最適なパステル背景色、高コントラスト細線枠、および太字アクロニム刻印を一元管理する。
    /// </summary>
    public static class TablerBadgeHelper
    {
        public static TablerBadgeInfo GetBadge(string? pathOrExtension, bool isDirectory)
        {
            if (isDirectory)
            {
                return new TablerBadgeInfo("DIR", "#EFF6FF", "#60A5FA", "#2563EB"); // Blue-50 / Blue-400 / Blue-600
            }

            string ext = string.Empty;
            if (!string.IsNullOrWhiteSpace(pathOrExtension))
            {
                ext = pathOrExtension.StartsWith(".")
                    ? pathOrExtension.ToLowerInvariant()
                    : Path.GetExtension(pathOrExtension).ToLowerInvariant();
            }

            string badgeText = ext.TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(badgeText)) badgeText = "FILE";
            else if (badgeText.Length > 4) badgeText = badgeText.Substring(0, 4);

            return ext switch
            {
                // 表計算・CSV
                ".xlsx" or ".xls" or ".xlsm" => new TablerBadgeInfo(badgeText, "#ECFDF5", "#10B981", "#059669"), // Green
                ".csv" or ".tsv" => new TablerBadgeInfo(badgeText, "#F0FDF4", "#34D399", "#059669"),            // Emerald

                // 文書・PDF
                ".pdf" => new TablerBadgeInfo(badgeText, "#FEF2F2", "#EF4444", "#DC2626"),                       // Red
                ".docx" or ".doc" => new TablerBadgeInfo(badgeText, "#EFF6FF", "#3B82F6", "#2563EB"),            // Blue
                ".pptx" or ".ppt" => new TablerBadgeInfo(badgeText, "#FFF7ED", "#F97316", "#EA580C"),            // Orange
                ".txt" or ".log" or ".md" => new TablerBadgeInfo(badgeText, "#F8FAFC", "#94A3B8", "#475569"),    // Slate

                // アーカイブ
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".cab" => new TablerBadgeInfo(badgeText, "#FEF3C7", "#F59E0B", "#D97706"), // Amber

                // 画像
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tif" or ".tiff" => new TablerBadgeInfo(badgeText, "#F5F3FF", "#8B5CF6", "#7C3AED"), // Purple

                // 動画
                ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" => new TablerBadgeInfo(badgeText, "#ECFEFF", "#06B6D4", "#0891B2"), // Cyan

                // 音声
                ".mp3" or ".wav" or ".m4a" or ".flac" or ".wma" or ".aac" => new TablerBadgeInfo(badgeText, "#FDF2F8", "#EC4899", "#DB2777"), // Pink

                // 実行・スクリプト
                ".exe" or ".msi" => new TablerBadgeInfo(badgeText, "#F1F5F9", "#64748B", "#334155"),             // Dark Slate
                ".ps1" or ".bat" or ".cmd" or ".sh" => new TablerBadgeInfo(badgeText, "#F0FDF4", "#10B981", "#059669"), // Emerald

                // データベース
                ".sql" or ".db" or ".sqlite" or ".mdf" or ".accdb" => new TablerBadgeInfo(badgeText, "#EEF2FF", "#6366F1", "#4F46E5"), // Indigo

                // 設定・構造化データ
                ".json" or ".xml" or ".yaml" or ".yml" or ".config" or ".ini" => new TablerBadgeInfo(badgeText, "#F0FDFA", "#14B8A6", "#0D9488"), // Teal

                // ディスクイメージ・仮想ディスク
                ".iso" or ".img" or ".vhdx" or ".vhd" or ".vmdk" => new TablerBadgeInfo(badgeText, "#FEF9C3", "#EAB308", "#A16207"), // Yellow

                // その他汎用
                _ => new TablerBadgeInfo(badgeText, "#F8FAFC", "#CBD5E1", "#475569")                             // Light Slate
            };
        }
    }
}
