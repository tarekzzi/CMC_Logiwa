using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CMC_VER1
{
    public enum LogExportScope
    {
        All,
        Filtered,
        LastLines
    }

    /// <summary>Asks which part of the message log to export. The file name is chosen afterwards.</summary>
    public partial class ExportLogDialog : Window
    {
        private readonly int _maxLast;

        public LogExportScope Scope =>
            rbAll.IsChecked == true ? LogExportScope.All :
            rbFiltered.IsChecked == true ? LogExportScope.Filtered :
            LogExportScope.LastLines;

        public int LastLineCount { get; private set; }

        /// <param name="totalLines">Lines in the whole log.</param>
        /// <param name="filteredLines">Lines matching the current filter.</param>
        /// <param name="filter">The current filter text, or empty when no filter is set.</param>
        public ExportLogDialog(int totalLines, int filteredLines, string filter)
        {
            InitializeComponent();

            bool hasFilter = !string.IsNullOrEmpty(filter);
            // "Last N" counts from the end of what the operator currently sees.
            int viewLines = hasFilter ? filteredLines : totalLines;
            _maxLast = Math.Max(1, viewLines);

            rbAll.Content = $"_All lines ({totalLines:N0})";
            rbAll.IsChecked = !hasFilter;

            rbFiltered.Content = hasFilter
                ? $"Lines _matching \"{filter}\" ({filteredLines:N0})"
                : "Lines _matching the filter (no filter set)";
            rbFiltered.IsEnabled = hasFilter;
            rbFiltered.IsChecked = hasFilter;

            txtLast.Text = Math.Min(500, _maxLast).ToString();
            lblLines.Text = (hasFilter ? "matching lines" : "lines") + $"  (max {_maxLast:N0})";
        }

        private void txtLast_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => rbLast.IsChecked = true;

        private void txtLast_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) rbLast.IsChecked = true;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            if (Scope == LogExportScope.LastLines)
            {
                if (!int.TryParse(txtLast.Text.Replace(",", ""), out int count) || count < 1)
                {
                    MessageBox.Show(this, "Enter how many lines to export (1 or more).", "Export message log",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    txtLast.Focus();
                    return;
                }
                LastLineCount = Math.Min(count, _maxLast);
            }

            DialogResult = true;
        }
    }
}
