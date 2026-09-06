using System.Collections.Generic;
using System.Windows;
using AstraSize.Models;

namespace AstraSize
{
    public partial class HistoryWindow : Window
    {
        public HistoryWindow(string targetPath, List<ScanSnapshot> history)
        {
            InitializeComponent();
            TargetPathText.Text = $"対象パス: {targetPath}";
            HistoryItemsControl.ItemsSource = history;

            if (history == null || history.Count == 0)
            {
                EmptyHistoryText.Visibility = Visibility.Visible;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
