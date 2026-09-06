using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

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
        public string? ErrorMessage { get; set; }
        public int Level { get; set; } = 0;

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

        public string ExpandGlyph
        {
            get => !CanExpand ? "" : IsExpanded ? "▼" : "▶";
            set { }
        }

        public Thickness IndentMargin
        {
            get => new Thickness(Level * 16, 0, 0, 0);
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
                }
            }
        }

        public double PercentageOfParent
        {
            get => Percentage;
            set => Percentage = value;
        }

        public string FormattedSize
        {
            get => FormatBytes(Size);
            set { }
        }

        public string PercentageFormatted
        {
            get => $"{Percentage:F1}%";
            set { }
        }

        public string FormattedPercentage
        {
            get => $"{Percentage:0.#}%";
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

        public static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";
            string sign = bytes < 0 ? "-" : "";
            long absBytes = Math.Abs(bytes);
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB" };
            int place = Convert.ToInt32(Math.Floor(Math.Log(absBytes, 1024)));
            if (place >= suf.Length) place = suf.Length - 1;
            double num = Math.Round(absBytes / Math.Pow(1024, place), 1);
            return $"{sign}{num:0.#} {suf[place]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
