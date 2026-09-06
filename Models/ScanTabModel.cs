using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AstraSize.Models
{
    public class ScanTabModel : INotifyPropertyChanged
    {
        private string _tabId = Guid.NewGuid().ToString();
        private string _tabTitle = "新規タブ";
        private string _targetPath = string.Empty;
        private bool _isScanning = false;
        private bool _isSelected = false;
        private string _scannedSizeText = "0.00 GB";
        private string _totalFilesText = "0 ファイル / 0 フォルダ";
        private string _freeSpaceText = "-- GB 空き";
        private double _freePercent = 0;
        private string _freePercentText = "--%";
        private string _largestFileName = "--";
        private string _largestFileSize = "--";
        private string _diffText = "初回スキャン";
        private string _lastScanDateText = "-";
        private string _statusMessage = "準備完了";

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string TabId
        {
            get => _tabId;
            set { _tabId = value; OnPropertyChanged(); }
        }

        public string TabTitle
        {
            get => _tabTitle;
            set { _tabTitle = value; OnPropertyChanged(); }
        }

        public string TargetPath
        {
            get => _targetPath;
            set
            {
                _targetPath = value;
                OnPropertyChanged();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var trimmed = value.TrimEnd('\\');
                    var lastPart = System.IO.Path.GetFileName(trimmed);
                    TabTitle = string.IsNullOrEmpty(lastPart) ? trimmed : lastPart;
                }
            }
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { _isScanning = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string ScannedSizeText
        {
            get => _scannedSizeText;
            set { _scannedSizeText = value; OnPropertyChanged(); }
        }

        public string TotalFilesText
        {
            get => _totalFilesText;
            set { _totalFilesText = value; OnPropertyChanged(); }
        }

        public string FreeSpaceText
        {
            get => _freeSpaceText;
            set { _freeSpaceText = value; OnPropertyChanged(); }
        }

        public double FreePercent
        {
            get => _freePercent;
            set { _freePercent = value; OnPropertyChanged(); }
        }

        public string FreePercentText
        {
            get => _freePercentText;
            set { _freePercentText = value; OnPropertyChanged(); }
        }

        public string LargestFileName
        {
            get => _largestFileName;
            set { _largestFileName = value; OnPropertyChanged(); }
        }

        public string LargestFileSize
        {
            get => _largestFileSize;
            set { _largestFileSize = value; OnPropertyChanged(); }
        }

        public string DiffText
        {
            get => _diffText;
            set { _diffText = value; OnPropertyChanged(); }
        }

        public string LastScanDateText
        {
            get => _lastScanDateText;
            set { _lastScanDateText = value; OnPropertyChanged(); }
        }

        public FileItemNode? RootNode { get; set; }
        public ObservableCollection<FileItemNode> FlatVisibleItems { get; set; } = new();
        public ObservableCollection<FileItemNode> VisibleFlatList => FlatVisibleItems;
        public List<FileItemNode> AllFlatItems { get; set; } = new();
        public List<LargestFileInfo> TopLargeFiles { get; set; } = new();
        public List<ExtensionStat> ExtensionSummaries { get; set; } = new();
        public List<ExtensionStat> ExtensionList
        {
            get => ExtensionSummaries;
            set { ExtensionSummaries = value; OnPropertyChanged(); }
        }
        public ScanSummary? Summary { get; set; }
        public string FilterKeyword { get; set; } = string.Empty;

        public void FlattenTree()
        {
            FlatVisibleItems.Clear();
            if (RootNode == null) return;

            string filter = FilterKeyword?.Trim() ?? string.Empty;
            bool hasFilter = !string.IsNullOrEmpty(filter);

            void Traverse(FileItemNode node, int level)
            {
                node.Level = level;
                bool matches = !hasFilter || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                if (matches || !hasFilter)
                {
                    FlatVisibleItems.Add(node);
                }

                if (node.IsExpanded || hasFilter)
                {
                    foreach (var child in node.Children)
                    {
                        Traverse(child, level + 1);
                    }
                }
            }

            Traverse(RootNode, 0);
        }

        public void AggregateExtensions()
        {
            if (Summary?.ExtensionStats != null && Summary.ExtensionStats.Count > 0)
            {
                ExtensionSummaries = Summary.ExtensionStats;
            }
            else if (RootNode != null)
            {
                var (_, extStats) = Services.DiskScanService.GetInsightsForNode(RootNode);
                ExtensionSummaries = extStats;
            }
            OnPropertyChanged(nameof(ExtensionList));
            OnPropertyChanged(nameof(ExtensionSummaries));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
