using AstraSize.Models;
using FolderMorpher.Contracts;

namespace FolderMorpher.HostClient;

internal static class StorageNodeMapper
{
    public static StorageNodeDto ToFlatDto(FileItemNode node) => new()
    {
        Name = node.Name,
        FullPath = node.FullPath,
        SizeBytes = node.Size,
        FileCount = node.FileCount,
        FolderCount = node.FolderCount,
        IsDirectory = node.IsDirectory,
        IsRoot = node.IsRoot,
        PercentageOfRoot = node.Percentage,
        LastModified = node.LastModified,
        CreationTime = node.CreationTime,
        Sha256 = node.Sha256,
        ErrorMessage = node.ErrorMessage
    };

    public static StorageNodeDto ToTreeDto(FileItemNode node)
    {
        var dto = ToFlatDto(node);
        dto.Children = node.Children.Select(ToTreeDto).ToList();
        return dto;
    }

    public static FileItemNode ToViewNode(StorageNodeDto dto, FileItemNode? parent = null)
    {
        var node = new FileItemNode(dto.FullPath, dto.Name, dto.SizeBytes, dto.IsDirectory, dto.LastModified)
        {
            Parent = parent,
            FileCount = dto.FileCount,
            FolderCount = dto.FolderCount,
            CreationTime = dto.CreationTime,
            Sha256 = dto.Sha256,
            ErrorMessage = dto.ErrorMessage,
            DiffBytes = dto.DiffBytes,
            Percentage = dto.PercentageOfRoot,
            Level = parent == null ? 0 : parent.Level + 1,
            CachedTopFiles = dto.CachedTopFiles.Select(ToViewFile).ToList(),
            CachedExtensionStats = dto.CachedExtensionStats.Select(stat => new ExtensionStat
            {
                Extension = stat.Extension, TotalSize = stat.TotalSizeBytes,
                FileCount = stat.FileCount, Percentage = stat.Percentage
            }).ToList()
        };
        foreach (var child in dto.Children) node.Children.Add(ToViewNode(child, node));
        return node;
    }

    public static LargestFileInfo ToViewFile(StorageTopFileDto dto) => new()
    {
        Name = dto.Name, FullPath = dto.FullPath, Size = dto.SizeBytes,
        Extension = dto.Extension, Category = dto.Category
    };
}
