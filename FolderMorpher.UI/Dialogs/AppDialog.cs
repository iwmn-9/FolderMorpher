using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FolderMorpher.Services;

namespace AstraSize;

/// <summary>App-owned confirmation surface. Return values preserve MessageBox semantics.</summary>
public static class AppDialog
{
    public static MessageBoxResult Show(string message, string caption = "", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(message, caption, buttons, image, defaultResult));

        bool ja = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
        var fallback = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.No
        };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window.IsVisible)
            ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);
        var window = new Window
        {
            Title = caption,
            Width = 480,
            MinHeight = 190,
            MaxHeight = 670,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = Brush("#1C2C43")
        };
        if (owner != null && owner != window) window.Owner = owner;
        var iconColor = image switch
        {
            MessageBoxImage.Error => "#B43B42",
            MessageBoxImage.Warning => "#A86F18",
            MessageBoxImage.Question => "#285AA7",
            _ => "#285AA7"
        };
        var icon = image switch
        {
            MessageBoxImage.Error => "!",
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?",
            _ => "i"
        };
        var frame = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush("#DCE4EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(15),
            Padding = new Thickness(25, 23, 25, 21),
            Effect = new DropShadowEffect { BlurRadius = 25, ShadowDepth = 8, Opacity = .16, Color = Color.FromRgb(31, 52, 82) }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.Child = layout;
        window.Content = frame;

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
        heading.Children.Add(new Border
        {
            Width = 27,
            Height = 27,
            CornerRadius = new CornerRadius(7),
            Background = Brush("#EFF4FB"),
            Child = new TextBlock { Text = icon, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brush(iconColor), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        heading.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(caption) ? (ja ? "FolderMorpher からの確認" : "FolderMorpher") : caption,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);

        var scroll = new ScrollViewer { MaxHeight = 450, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = Brush("#4B5B72"),
            Margin = new Thickness(0, 0, 0, 20)
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        MessageBoxResult result = fallback;
        void AddAction(string label, MessageBoxResult value, bool primary)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 82,
                Height = 34,
                Padding = new Thickness(13, 0, 13, 0),
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)Application.Current.FindResource(primary ? "FluentButtonPrimary" : "FluentButtonSecondary"),
                IsDefault = value == (defaultResult == MessageBoxResult.None ? (buttons == MessageBoxButton.OK ? MessageBoxResult.OK : fallback) : defaultResult),
                IsCancel = value == fallback
            };
            button.Click += (_, _) => { result = value; window.Close(); };
            actions.Children.Add(button);
        }

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddAction(ja ? "閉じる" : "Close", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.OKCancel:
                AddAction(ja ? "キャンセル" : "Cancel", MessageBoxResult.Cancel, false);
                AddAction("OK", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.YesNo:
                AddAction(ja ? "いいえ" : "No", MessageBoxResult.No, false);
                AddAction(ja ? "はい" : "Yes", MessageBoxResult.Yes, true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddAction(ja ? "キャンセル" : "Cancel", MessageBoxResult.Cancel, false);
                AddAction(ja ? "いいえ" : "No", MessageBoxResult.No, false);
                AddAction(ja ? "はい" : "Yes", MessageBoxResult.Yes, true);
                break;
        }
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = fallback; window.Close(); e.Handled = true; }
        };
        window.ShowDialog();
        return result;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
