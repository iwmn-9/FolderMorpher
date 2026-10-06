using AstraSize.Models;
using FolderMorpher.Contracts;

namespace FolderMorpher.Host;

internal static class StorageDtoMapper
{
    public static StorageNodeDto ToDto(FileItemNode node)
    {
        var dto = ToDtoWithoutChildren(node);
        dto.Children = node.Children.Select(ToDto).ToList();
        return dto;
    }

    public static StorageNodeDto ToLevelDto(FileItemNode node, int remainingLevels)
    {
        var dto = ToDtoWithoutChildren(node);
        if (remainingLevels > 0)
            dto.Children = node.Children.Select(child => ToLevelDto(child, remainingLevels - 1)).ToList();
        else
            dto.HasUnloadedChildren = node.IsDirectory && (node.HasUnloadedChildren || node.Children.Count > 0);
        return dto;
    }

    private static StorageNodeDto ToDtoWithoutChildren(FileItemNode node) => new()
    {
        Name = node.Name, FullPath = node.FullPath, SizeBytes = node.Size,
        FileCount = node.FileCount, FolderCount = node.FolderCount,
        IsDirectory = node.IsDirectory, IsRoot = node.IsRoot,
        PercentageOfRoot = node.Percentage, LastModified = node.LastModified,
        CreationTime = node.CreationTime, Sha256 = node.Sha256,
        ErrorMessage = node.ErrorMessage, DiffBytes = node.DiffBytes,
        HasUnloadedChildren = node.HasUnloadedChildren,
        CachedTopFiles = node.CachedTopFiles?.Select(ToDto).ToList() ?? new(),
        CachedExtensionStats = node.CachedExtensionStats?.Select(ToDto).ToList() ?? new()
    };

    public static FileItemNode ToCore(StorageNodeDto dto, FileItemNode? parent = null)
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
            HasUnloadedChildren = dto.HasUnloadedChildren,
            Percentage = dto.PercentageOfRoot,
            Level = parent == null ? 0 : parent.Level + 1,
            CachedTopFiles = dto.CachedTopFiles.Select(ToCore).ToList(),
            CachedExtensionStats = dto.CachedExtensionStats.Select(ToCore).ToList()
        };
        foreach (var child in dto.Children) node.Children.Add(ToCore(child, node));
        return node;
    }

    public static StorageTopFileDto ToDto(LargestFileInfo file) => new()
    {
        Name = file.Name, FullPath = file.FullPath, SizeBytes = file.Size,
        Extension = file.Extension, Category = file.Category
    };

    public static LargestFileInfo ToCore(StorageTopFileDto file) => new()
    {
        Name = file.Name, FullPath = file.FullPath, Size = file.SizeBytes,
        Extension = file.Extension, Category = file.Category
    };

    public static StorageExtensionStatDto ToDto(ExtensionStat stat) => new()
    {
        Extension = stat.Extension, TotalSizeBytes = stat.TotalSize,
        FileCount = stat.FileCount, Percentage = stat.Percentage
    };

    public static ExtensionStat ToCore(StorageExtensionStatDto stat) => new()
    {
        Extension = stat.Extension, TotalSize = stat.TotalSizeBytes,
        FileCount = stat.FileCount, Percentage = stat.Percentage
    };
}
