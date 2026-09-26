using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using FolderMorpher.Services;

namespace AstraSize.Models
{
    public partial class FileItemNode : INotifyPropertyChanged
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
                    OnPropertyChanged("DiffFormatted");
                    OnPropertyChanged(nameof(HasDiff));
                    OnPropertyChanged("DiffBadgeBackground");
                    OnPropertyChanged("DiffBadgeForeground");
                }
            }
        }

        public bool HasDiff => DiffBytes.HasValue && DiffBytes.Value != 0;


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
            get => IsDirectory && (Children.Count > 0 || HasUnloadedChildren);
            set { }
        }

        public bool HasChildren => CanExpand;
        private bool _hasUnloadedChildren;
        public bool HasUnloadedChildren
        {
            get => _hasUnloadedChildren;
            set
            {
                if (_hasUnloadedChildren == value) return;
                _hasUnloadedChildren = value;
                OnPropertyChanged(nameof(HasUnloadedChildren));
                OnPropertyChanged(nameof(CanExpand));
                OnPropertyChanged(nameof(HasChildren));
                OnPropertyChanged("ExpandGlyph");
                OnPropertyChanged("ExpandIcon");
            }
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
                    OnPropertyChanged("PercentageFormatted");
                    OnPropertyChanged("FormattedPercentage");
                    OnPropertyChanged(nameof(PercentageOfParent));
                    OnPropertyChanged(nameof(SharePercentage));
                    OnPropertyChanged("ShareFormatted");
                }
            }
        }

        public bool IsRoot => Parent == null;

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
                    OnPropertyChanged("ExpandGlyph");
                    OnPropertyChanged("ExpandIcon");
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

}
