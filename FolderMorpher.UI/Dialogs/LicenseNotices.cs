using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FolderMorpher.Services;

namespace AstraSize;

/// <summary>The distributed notices are embedded so a portable EXE carries its attributions.</summary>
internal static class LicenseNotices
{
    internal static string Read()
    {
        using var stream = typeof(LicenseNotices).Assembly.GetManifestResourceStream("FolderMorpher.ThirdPartyNotices.txt")
            ?? throw new InvalidOperationException("Embedded license notices are missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static Window CreateWindow(Window owner)
    {
        var window = new Window
        {
            Title = Strings.LicensesTitle,
            Width = Math.Min(840, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(680, SystemParameters.WorkArea.Height - 40),
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Segoe UI")
        };
        if (owner.IsVisible) window.Owner = owner;
        var frame = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush("#DCE4EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(15),
            Padding = new Thickness(24),
            Effect = new DropShadowEffect { BlurRadius = 25, ShadowDepth = 8, Opacity = .16 }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.Child = layout;
        window.Content = frame;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock
        {
            Text = Strings.LicensesTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 18,
            Foreground = Brush("#1C2C43")
        });
        heading.Children.Add(new TextBlock
        {
            Text = Strings.LicensesDescription,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brush("#64748B")
        });
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
        layout.Children.Add(heading);

        // TextBox permits selection/copy and renders only the visible part of the long notice.
        var text = new TextBox
        {
            Name = "LicenseNoticeText",
            Text = Read(),
            IsReadOnly = true,
            IsUndoEnabled = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("#334155"),
            Background = Brush("#F8FAFC"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(text, 1);
        layout.Children.Add(text);
        var close = new Button
        {
            Content = Strings.Close,
            MinWidth = 90,
            Height = 34,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.FindResource("FluentButtonPrimary"),
            IsCancel = true
        };
        close.Click += (_, _) => window.Close();
        Grid.SetRow(close, 2);
        layout.Children.Add(close);
        return window;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
