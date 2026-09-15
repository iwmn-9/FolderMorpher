using System;
using System.Collections.Generic;
using System.Linq;
using AstraSize.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Models
{
    /// <summary>
    /// 移行バッチの分割ポリシー
    /// </summary>
    public enum MigrationSplitPolicy
    {
        /// <summary>
        /// 第1階層（トップレベルフォルダ・部署）ごとにWave分割（推奨）
        /// </summary>
        ByTopLevelFolder = 0,

        /// <summary>
        /// 容量バジェット（指定サイズ以下）ごとに自動クラスタリング
        /// </summary>
        BySizeBudget = 1,

        /// <summary>
        /// 一括出力（全フォルダを単一バッチに集約）
        /// </summary>
        SingleBatch = 2
    }

    /// <summary>
    /// 単一の移行波次（Wave）の計画データ
    /// </summary>
    public class MigrationWavePlan
    {
        public int WaveNumber { get; set; } = 1;
        public string WaveName { get; set; } = string.Empty;
        public List<SimFolderNode> TargetNodes { get; set; } = new();

        public long TotalSizeBytes { get; set; }
        public long TotalFileCount { get; set; }

        public string TotalSizeFormatted => FormatHelper.FormatBytes(TotalSizeBytes, 2);
        public string TotalFileCountFormatted => $"{TotalFileCount:N0} 件";

        /// <summary>
        /// 1Gbps実効 (約80MB/s) 換算の初回フルコピー想定時間
        /// </summary>
        public TimeSpan EstimatedFullCopyTime { get; set; }

        /// <summary>
        /// 差分5%換算の本番カットオーバー想定時間
        /// </summary>
        public TimeSpan EstimatedCutoverTime { get; set; }

        public string FullCopyTimeFormatted => FormatTimeSpan(EstimatedFullCopyTime);
        public string CutoverTimeFormatted => FormatTimeSpan(EstimatedCutoverTime);

        /// <summary>
        /// ファイル数が10万件超（ランダムI/O過多警告）
        /// </summary>
        public bool HasHighFileCountWarning => TotalFileCount > 100_000;

        /// <summary>
        /// 初回コピーが48時間超（週末枠オーバー警告）
        /// </summary>
        public bool HasOver48hWarning => EstimatedFullCopyTime.TotalHours > 48.0;

        /// <summary>
        /// このWaveにマッピングされている旧環境のソースパス一覧
        /// </summary>
        public List<string> MappedSourcePaths { get; set; } = new();

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1.0)
            {
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            }
            if (ts.TotalHours >= 1.0)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            }
            if (ts.TotalMinutes >= 1.0)
            {
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            }
            return $"{Math.Max(1, (int)ts.TotalSeconds)}s";
        }
    }

    /// <summary>
    /// 移行パッケージ生成の実行オプション
    /// </summary>
    public class MigrationPackageOptions
    {
        public MigrationSplitPolicy Policy { get; set; } = MigrationSplitPolicy.ByTopLevelFolder;
        public long SizeBudgetBytes { get; set; } = 500L * 1024 * 1024 * 1024; // 既定 500GB
        public string OutputDirectory { get; set; } = string.Empty;
        public string TargetRoot { get; set; } = string.Empty;
        public bool CopyAcl { get; set; } = false; // 推奨: false (/COPY:DAT)
        public int Threads { get; set; } = 16;
        public bool IncludeRunbookExcel { get; set; } = true;
        public bool IncludeOldShareLock { get; set; } = true;
    }
}