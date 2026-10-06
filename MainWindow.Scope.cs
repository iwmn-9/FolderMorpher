using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AstraSize.Models;
using FolderMorpher.HostClient;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize;

public partial class MainWindow
{
    private string _activeScopePath = string.Empty;

    private void InitializeFolderScope()
    {
        var settings = AppSettingsService.Instance.Current;
        var path = !string.IsNullOrWhiteSpace(settings.ActiveScopePath)
            ? settings.ActiveScopePath
            : _currentTab?.TargetPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = PathCanonicalizer.Normalize(path);
            SetActiveFolderScope(path, selectStorageTab: true, persist: false);
        }
        else UpdateScopeCaption();
        RenderScopeChoices();
    }

    internal void SetActiveFolderScope(string path, bool selectStorageTab = true, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = PathCanonicalizer.Normalize(path.Trim());
        path = path.TrimEnd('\\', '/');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (string.IsNullOrWhiteSpace(path)) return;
        bool changed = !string.Equals(_activeScopePath, path, StringComparison.OrdinalIgnoreCase);
        _activeScopePath = path;

        if (selectStorageTab && StorageTabs != null)
        {
            var tab = StorageTabs.FirstOrDefault(item => string.Equals(item.TargetPath, path, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                tab = new ScanTabModel { TargetPath = path, TabTitle = ScopeLeaf(path) };
                StorageTabs.Add(tab);
            }
            if (!ReferenceEquals(tab, _currentTab)) SelectTab(tab);
        }

        if (changed)
        {
            CancelCurrentSearch();
            ClearSearchResults();
        }
        if (SearchDirectTargetTextBox != null) SearchDirectTargetTextBox.Text = path;
        if (AuditPathTextBox != null) AuditPathTextBox.Text = path;
        if (LinkSearchScopeTextBox != null) LinkSearchScopeTextBox.Text = path;
        if (SimSourcePathTextBox != null) SimSourcePathTextBox.Text = path;
        UpdateScopeCaption();
        if (changed && NavTabStorage?.IsChecked != true)
            NavTab_Checked(this, new RoutedEventArgs());

        if (persist)
        {
            var settings = AppSettingsService.Instance.Current;
            settings.ActiveScopePath = path;
            settings.RecentScopePaths = new[] { path }
                .Concat((settings.RecentScopePaths ?? new List<string>()).Select(p => PathCanonicalizer.Normalize(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12).ToList();
            AppSettingsService.Instance.Save();
            RenderScopeChoices();
        }
    }

    private void UpdateScopeCaption()
    {
        if (ScopePickerText == null) return;
        ScopePickerText.Text = string.IsNullOrWhiteSpace(_activeScopePath)
            ? UiText("フォルダーを選択", "Choose a folder")
            : ScopeLeaf(_activeScopePath);
        ScopePickerButton.ToolTip = string.IsNullOrWhiteSpace(_activeScopePath)
            ? UiText("参照フォルダーを切り替える", "Switch the working folder")
            : _activeScopePath;
    }

    private static string ScopeLeaf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? path : leaf;
    }

    private void ScopePickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScopePopup.IsOpen) { ScopePopup.IsOpen = false; return; }
        ScopeFilterBox.Text = string.Empty;
        RenderScopeChoices();
        ScopePopup.IsOpen = true;
    }

    private void ScopeFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ScopeFilterHint != null)
            ScopeFilterHint.Visibility = string.IsNullOrEmpty(ScopeFilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (ScopeRecentItems != null && ScopeTree != null) RenderScopeChoices();
    }

    private void RenderScopeChoices()
    {
        if (ScopeRecentItems == null || ScopeTree == null) return;
        var settings = AppSettingsService.Instance.Current;
        var recentPaths = (settings.RecentScopePaths ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathCanonicalizer.Normalize(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var paths = recentPaths
            .Concat((settings.StorageTabPaths ?? new List<string>()).Select(path => PathCanonicalizer.Normalize(path)))
            .Append(PathCanonicalizer.Normalize(_activeScopePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var filter = ScopeFilterBox?.Text?.Trim() ?? string.Empty;
        var visible = paths.Where(path => path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ScopeRecentItems.Items.Clear();
        foreach (var path in visible)
            ScopeRecentItems.Items.Add(CreateScopeRecentRow(path));
        if (visible.Count == 0)
            ScopeRecentItems.Items.Add(new TextBlock
            {
                Text = UiText("登録済みフォルダーはありません", "No saved folders"),
                Foreground = System.Windows.Media.Brushes.SlateGray,
                Margin = new Thickness(8, 8, 0, 4)
            });

        ScopeTree.Items.Clear();
        foreach (var path in visible)
            ScopeTree.Items.Add(CreateScopeTreeItem(path));
    }

    private Border CreateScopeRecentRow(string path)
    {
        var selectButton = new Button
        {
            Content = CreateFolderLabel(path, showPath: true),
            ToolTip = path,
            Tag = path,
            Height = 46,
            Padding = new Thickness(9, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Style = (Style)FindResource("ScopePickerButtonStyle")
        };
        selectButton.Click += (_, _) => ChooseScope(path);
        var removeButton = new Button
        {
            Content = "×",
            ToolTip = UiText("参照一覧から外す（実フォルダーは削除しません）", "Remove from this list (files stay untouched)"),
            Width = 25,
            Height = 25,
            Visibility = Visibility.Hidden,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("ScopePickerButtonStyle")
        };
        removeButton.Click += (_, _) => RemoveRegisteredScope(path);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        Grid.SetColumn(removeButton, 1);
        grid.Children.Add(selectButton);
        grid.Children.Add(removeButton);
        var row = new Border
        {
            Background = string.Equals(path, _activeScopePath, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(237, 244, 253)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 0, 2),
            Child = grid
        };
        row.MouseEnter += (_, _) => removeButton.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => removeButton.Visibility = Visibility.Hidden;
        return row;
    }

    private void RemoveRegisteredScope(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var settings = AppSettingsService.Instance.Current;
        settings.RecentScopePaths = (settings.RecentScopePaths ?? new List<string>())
            .Where(saved => !string.Equals(saved, path, StringComparison.OrdinalIgnoreCase)).ToList();

        var removedTabs = StorageTabs.Where(tab => string.Equals(tab.TargetPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        bool selectedTabRemoved = _currentTab != null && removedTabs.Contains(_currentTab);
        foreach (var tab in removedTabs) StorageTabs.Remove(tab);

        if (string.Equals(_activeScopePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _activeScopePath = string.Empty;
            CancelCurrentSearch();
            ClearSearchResults();
            var nextTab = StorageTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.TargetPath));
            if (nextTab != null)
            {
                SelectTab(nextTab);
            }
            else
            {
                if (StorageTabs.Count == 0)
                    StorageTabs.Add(new ScanTabModel { TabTitle = UiText("新規スキャン", "New Scan"), TargetPath = string.Empty });
                SelectTab(StorageTabs[0]);
                SearchDirectTargetTextBox.Text = string.Empty;
                AuditPathTextBox.Text = string.Empty;
                LinkSearchScopeTextBox.Text = string.Empty;
                SimSourcePathTextBox.Text = string.Empty;
                settings.ActiveScopePath = string.Empty;
            }
        }
        else if (selectedTabRemoved)
        {
            if (StorageTabs.Count == 0)
                StorageTabs.Add(new ScanTabModel { TabTitle = UiText("新規スキャン", "New Scan"), TargetPath = string.Empty });
            SelectTab(StorageTabs[0]);
        }

        SaveStorageTabSession();
        AppSettingsService.Instance.Save();
        UpdateScopeCaption();
        RenderScopeChoices();
    }

    private TreeViewItem CreateScopeTreeItem(string path)
    {
        var item = new TreeViewItem
        {
            Header = CreateFolderLabel(path),
            Tag = path,
            ToolTip = path,
            FontSize = 13,
            Padding = new Thickness(5, 5, 5, 5)
        };
        item.Items.Add(new TreeViewItem { Header = "…", IsEnabled = false });
        item.Expanded += ScopeTreeItem_Expanded;
        return item;
    }

    private static StackPanel CreateFolderLabel(string path, bool showPath = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,5 L9,5 11,7 22,7 22,19 2,19 Z"),
            Stroke = new SolidColorBrush(Color.FromRgb(49, 91, 155)),
            StrokeThickness = 1.6,
            Width = 16,
            Height = 15,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock
        {
            Text = ScopeLeaf(path),
            Foreground = new SolidColorBrush(Color.FromRgb(32, 52, 81)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 355,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (showPath)
            caption.Children.Add(new TextBlock
            {
                Text = path,
                Foreground = new SolidColorBrush(Color.FromRgb(130, 146, 168)),
                FontSize = 10.5,
                MaxWidth = 375,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        panel.Children.Add(caption);
        return panel;
    }

    private async void ScopeTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || !ReferenceEquals(e.OriginalSource, item) || item.Tag is not string path)
            return;
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { IsEnabled: false }) return;
        item.Items.Clear();
        item.Items.Add(new TreeViewItem { Header = UiText("読み込み中…", "Loading…"), IsEnabled = false });
        try
        {
            var host = await FolderMorpherHostClient.Instance.GetServiceAsync();
            var children = await host.BrowseChildFoldersAsync(path);
            item.Items.Clear();
            foreach (var child in children.Take(256)) item.Items.Add(CreateScopeTreeItem(child));
            if (children.Count > 256)
                item.Items.Add(new TreeViewItem { Header = UiText("続きは「＋ 追加」から選択", "Use + Add to browse more"), IsEnabled = false });
        }
        catch (Exception ex)
        {
            item.Items.Clear();
            item.Items.Add(new TreeViewItem { Header = UiText("この場所を開けません", "Cannot open this folder"), IsEnabled = false, ToolTip = ex.Message });
        }
    }

    private void ScopeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path, IsEnabled: true }) ChooseScope(path);
    }

    private void ChooseScope(string path)
    {
        ScopePopup.IsOpen = false;
        SetActiveFolderScope(path);
    }

    private void ScopeAddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = UiText("参照フォルダーを選択", "Choose a working folder"),
            InitialDirectory = _activeScopePath
        };
        if (dialog.ShowDialog(this) == true) ChooseScope(dialog.FolderName);
    }

}
