using System;
using System.Collections.Generic;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Contracts
{
    /// <summary>
    /// ホストの稼働状態・健全性 DTO
    /// </summary>
    public class HostStatusDto
    {
        public bool IsRunning { get; set; } = true;
        public string Version { get; set; } = "2.2.20";
        public string MachineName { get; set; } = Environment.MachineName;
        public string UserName { get; set; } = Environment.UserName;
        public int ProcessId { get; set; } = Environment.ProcessId;
        public DateTime StartTime { get; set; } = DateTime.Now;
        public int ActiveJobCount { get; set; }
    }

    /// <summary>
    /// ストレージスキャン要求 DTO
    /// </summary>
    public class StorageScanRequestDto
    {
        public string TargetPath { get; set; } = string.Empty;
        public int MaxConcurrency { get; set; } = 2;
    }

    /// <summary>
    /// ストレージスキャン進捗 DTO
    /// </summary>
    public class StorageScanProgressDto
    {
        public string CurrentDirectory { get; set; } = string.Empty;
        public long ScannedFilesCount { get; set; }
        public long ScannedBytes { get; set; }
        public bool IsCompleted { get; set; }
    }

    /// <summary>
    /// ストレージスキャン結果 DTO
    /// </summary>
    public class StorageScanResultDto
    {
        public string TargetPath { get; set; } = string.Empty;
        public FileItemNode? RootNode { get; set; }
        public List<LargestFileInfo> Top10Files { get; set; } = new();
        public TimeSpan Elapsed { get; set; }
        public long TotalBytes { get; set; }
        public int TotalFiles { get; set; }
        public int TotalFolders { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 監査要求 DTO
    /// </summary>
    public class AuditScanRequestDto
    {
        public string TargetPath { get; set; } = string.Empty;
        public AuditBandwidthLimit BandwidthLimit { get; set; } = AuditBandwidthLimit.Standard50MB;
        public List<string>? IgnoredPaths { get; set; }
    }

    /// <summary>
    /// 監査結果 DTO
    /// </summary>
    public class AuditReportDto
    {
        public string TargetPath { get; set; } = string.Empty;
        public List<AuditItem> Items { get; set; } = new();
        public AuditSummary Summary { get; set; } = new();
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// ACL 適用要求 DTO
    /// </summary>
    public class AclApplyRequestDto
    {
        public string TargetFolder { get; set; } = string.Empty;
        public List<SimAclEntry> TargetEntries { get; set; } = new();
        public bool InheritFromParent { get; set; } = true;
    }

    /// <summary>
    /// ACL 適用結果 DTO
    /// </summary>
    public class AclApplyResultDto
    {
        public bool Success { get; set; }
        public string? OriginalSddl { get; set; }
        public string? NewSddl { get; set; }
        public string? ErrorMessage { get; set; }
        public int AddedCount { get; set; }
        public int RemovedCount { get; set; }
        public int ModifiedCount { get; set; }
    }

    /// <summary>
    /// リンク修復スキャン結果 DTO
    /// </summary>
    public class LinkFixScanResultDto
    {
        public List<FolderMorpher.Services.LinkFixItem> BrokenLinks { get; set; } = new();
        public List<FolderMorpher.Services.OfficeLinkItem> OfficeLinks { get; set; } = new();
        public int ScannedFilesCount { get; set; }
    }

    /// <summary>
    /// リンク修復適用要求 DTO
    /// </summary>
    public class LinkFixApplyRequestDto
    {
        public List<FolderMorpher.Services.LinkFixItem> TargetShortcuts { get; set; } = new();
        public List<FolderMorpher.Services.OfficeLinkItem> TargetOfficeLinks { get; set; } = new();
        public string OldPrefix { get; set; } = string.Empty;
        public string NewPrefix { get; set; } = string.Empty;
    }

    /// <summary>
    /// リンク修復結果 DTO
    /// </summary>
    public class LinkFixApplyResultDto
    {
        public int RepairedCount { get; set; }
        public int FailedCount { get; set; }
        public List<string> ErrorLog { get; set; } = new();
    }

    /// <summary>
    /// メディアスキャン結果 DTO
    /// </summary>
    public class MediaScanResultDto
    {
        public List<MediaItem> Images { get; set; } = new();
        public List<MediaItem> Videos { get; set; } = new();
    }
}
