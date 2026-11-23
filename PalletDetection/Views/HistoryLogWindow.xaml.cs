using PalletDetection.Models;
using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace PalletDetection.Views
{
    public partial class HistoryLogWindow : Window
    {
        private readonly Action _clearLogs;

        public HistoryLogWindow(
            ObservableCollection<TaskHistoryItem> history,
            ObservableCollection<string> logs,
            Action clearLogs = null)
        {
            InitializeComponent();
            _clearLogs = clearLogs;
            dgFullHistory.ItemsSource = history;
            lstOperationLogs.ItemsSource = logs;
        }

        private void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            _clearLogs?.Invoke();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
