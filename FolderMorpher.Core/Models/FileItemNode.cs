using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using FolderMorpher.Services;

namespace AstraSize.Models
{
    public class FileItemNode : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public long SizeBytes => Size;
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime? LastModified { get; set; }
        public DateTime? CreationTime { get; set; }
        public string? Sha256 { get; set; }
        public string? ErrorMessage { get; set; }
        public int Level { get; set; } = 0;

        public List<LargestFileInfo>? CachedTopFiles { get; set; }
        public List<ExtensionStat>? CachedExtensionStats { get; set; }

        private long? _diffBytes;
        public long? DiffBytes
        {
            get => _diffBytes;
            set
            {
                if (_diffBytes != value)
                {
                    _diffBytes = value;
                    OnPropertyChanged(nameof(DiffBytes));
                    OnPropertyChanged(nameof(DiffFormatted));
                    OnPropertyChanged(nameof(HasDiff));
                    OnPropertyChanged(nameof(DiffBadgeBackground));
                    OnPropertyChanged(nameof(DiffBadgeForeground));
                }
            }
        }

        public bool HasDiff => DiffBytes.HasValue && DiffBytes.Value != 0;

        public string? DiffFormatted
        {
            get
            {
                if (!DiffBytes.HasValue || DiffBytes.Value == 0) return null;
                if (DiffBytes.Value > 0)
                    return $"+{FormatBytes(DiffBytes.Value)} ▲";
                return $"-{FormatBytes(Math.Abs(DiffBytes.Value))} ▼";
            }
        }

        public string DiffBadgeBackground => (DiffBytes.HasValue && DiffBytes.Value > 0) ? "#FEE2E2" : "#E0F2FE";
        public string DiffBadgeForeground => (DiffBytes.HasValue && DiffBytes.Value > 0) ? "#DC2626" : "#0284C7";

        public FileItemNode()
        {
        }

        public FileItemNode(string fullPath, string name, long size, bool isDirectory, DateTime? lastModified = null)
        {
            FullPath = fullPath;
            Name = name;
            Size = size;
            IsDirectory = isDirectory;
            LastModified = lastModified;
        }

        public bool CanExpand
        {
            get => IsDirectory && Children.Count > 0;
            set { }
        }

        public bool HasChildren => CanExpand;

        public string ExpandGlyph
        {
            get => !CanExpand ? "" : IsExpanded ? "▼" : "▶";
            set { }
        }

        public string ExpandIcon => ExpandGlyph;

        public string IconGlyph => IsDirectory ? "📁" : "📄";

        private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(FullPath, IsDirectory);
        public string BadgeText => BadgeInfo.Text;
        public string BadgeBackground => BadgeInfo.Background;
        public string BadgeBorderBrush => BadgeInfo.BorderBrush;
        public string BadgeForeground => BadgeInfo.Foreground;

        public string FontWeight => IsDirectory ? "Bold" : "Normal";

        public string IndentMargin
        {
            get => $"{Level * 16},0,0,0";
            set { }
        }

        private double _percentage;
        public double Percentage
        {
            get => _percentage;
            set
            {
                if (Math.Abs(_percentage - value) > 0.001)
                {
                    _percentage = value;
                    OnPropertyChanged(nameof(Percentage));
                    OnPropertyChanged(nameof(PercentageFormatted));
                    OnPropertyChanged(nameof(FormattedPercentage));
                    OnPropertyChanged(nameof(PercentageOfParent));
                    OnPropertyChanged(nameof(SharePercentage));
                    OnPropertyChanged(nameof(ShareFormatted));
                }
            }
        }

        public bool IsRoot => Parent == null;
        public string PercentageDisplay => IsRoot ? "―" : $"{Percentage:F1}%";

        public double PercentageOfParent
        {
            get => Percentage;
            set => Percentage = value;
        }

        public double SharePercentage
        {
            get => Percentage;
            set => Percentage = value;
        }

        public bool IsDriveRoot => IsRoot;

        public string ShareFormatted => PercentageFormatted;

        public string FormattedSize
        {
            get => FormatBytes(Size);
            set { }
        }

        public string PercentageFormatted
        {
            get => IsRoot ? "―" : $"{Percentage:F1}%";
            set { }
        }

        public string FormattedPercentage
        {
            get => IsRoot ? "―" : $"{Percentage:0.#}%";
            set { }
        }

        public string FormattedFileCount
        {
            get => $"{FileCount:N0}";
            set { }
        }

        public string FormattedFolderCount
        {
            get => $"{FolderCount:N0}";
            set { }
        }

        public string FormattedLastModified
        {
            get => LastModified?.ToString("yyyy/MM/dd HH:mm") ?? "-";
            set { }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                    OnPropertyChanged(nameof(ExpandGlyph));
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public ObservableCollection<FileItemNode> Children { get; set; } = new();
        public FileItemNode? Parent { get; set; }

        public List<FileItemNode> GetVisibleFlatList()
        {
            var list = new List<FileItemNode>();
            AppendVisible(this, list);
            return list;
        }

        private void AppendVisible(FileItemNode node, List<FileItemNode> list)
        {
            list.Add(node);
            if (node.IsExpanded)
            {
                foreach (var child in node.Children)
                {
                    AppendVisible(child, list);
                }
            }
        }

        public List<FileItemNode> GetSubtreeFlatList()
        {
            var list = new List<FileItemNode>();
            AppendAll(this, list);
            return list;
        }

        private void AppendAll(FileItemNode node, List<FileItemNode> list)
        {
            list.Add(node);
            foreach (var child in node.Children)
            {
                AppendAll(child, list);
            }
        }

        public static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public class FolderChildShareItem
    {
        public FileItemNode OriginalNode { get; set; } = null!;
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string FormattedSize => FileItemNode.FormatBytes(Size);
        public bool IsDirectory { get; set; }
        public string IconGlyph => IsDirectory ? "📁" : "📄";

        private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(FullPath, IsDirectory);
        public string BadgeText => BadgeInfo.Text;
        public string BadgeBackground => BadgeInfo.Background;
        public string BadgeBorderBrush => BadgeInfo.BorderBrush;
        public string BadgeForeground => BadgeInfo.Foreground;
        public double RelativeSharePercentage { get; set; }
        public string RelativeShareFormatted => $"{RelativeSharePercentage:F1}%";
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
    }
}
