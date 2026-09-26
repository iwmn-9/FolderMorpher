using AstraSize.Models;
using FolderMorpher.Contracts;

namespace FolderMorpher.HostClient;

internal static class HistoryDtoMapper
{
    public static ScanSnapshot ToView(ScanSnapshotDto dto) => new()
    {
        Id = dto.Id,
        TargetPath = dto.TargetPath,
        Timestamp = dto.Timestamp,
        TotalBytes = dto.TotalBytes,
        TotalFiles = dto.TotalFiles,
        SubFolders = dto.SubFolders.Select(folder => new FolderSnapshot { Name = folder.Name, Size = folder.Size }).ToList()
    };
}
