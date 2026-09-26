using FolderMorpher.Contracts;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.HostClient;

internal static class LinkFixDtoMapper
{
    public static LinkFixItem ToViewItem(ShortcutLinkDto dto) => new()
    {
        FilePath = dto.FilePath, FileName = dto.FileName, FileType = dto.FileType,
        OldTarget = dto.OldTarget, NewTarget = dto.NewTarget,
        IsFixed = dto.IsFixed, Status = dto.Status,
        AssociatedOfficeItem = dto.AssociatedOfficeItem == null ? null : ToViewItem(dto.AssociatedOfficeItem)
    };

    public static OfficeLinkItem ToViewItem(OfficeLinkDto dto) => new()
    {
        FilePath = dto.FilePath, FileName = dto.FileName, Extension = dto.Extension,
        FoundPattern = dto.FoundPattern, TargetReplacement = dto.TargetReplacement,
        IsLocked = dto.IsLocked, LockUser = dto.LockUser, Categories = (OfficeLinkCategory)dto.Categories,
        LinkType = dto.LinkType, FixStatus = (OfficeFixStatus)dto.FixStatus,
        VersionStamp = new FileVersionStamp(dto.ExpectedLength, dto.ExpectedLastWriteTimeUtc),
        Status = dto.Status, IsFixed = dto.IsFixed
    };

    public static ShortcutLinkDto ToDto(LinkFixItem item) => new()
    {
        FilePath = item.FilePath, FileName = item.FileName, FileType = item.FileType,
        OldTarget = item.OldTarget, NewTarget = item.NewTarget,
        IsFixed = item.IsFixed, Status = item.Status,
        AssociatedOfficeItem = item.AssociatedOfficeItem == null ? null : ToDto(item.AssociatedOfficeItem)
    };

    public static OfficeLinkDto ToDto(OfficeLinkItem item) => new()
    {
        FilePath = item.FilePath, FileName = item.FileName, Extension = item.Extension,
        FoundPattern = item.FoundPattern, TargetReplacement = item.TargetReplacement,
        IsLocked = item.IsLocked, LockUser = item.LockUser, Categories = (int)item.Categories,
        LinkType = item.LinkType, FixStatus = (int)item.FixStatus,
        ExpectedLength = item.ExpectedLength, ExpectedLastWriteTimeUtc = item.ExpectedLastWriteTimeUtc,
        Status = item.Status, IsFixed = item.IsFixed
    };
}
