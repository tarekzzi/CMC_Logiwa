using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// History tab: one row per box with what the machine did (completed, failed, removed, not packed) next to what Logiwa says
    /// (open, shipped, cancelled, on hold). Rows where the two disagree say why. Read only, except that "Check in Logiwa now" asks
    /// Logiwa about the selected box and stores the answer (the same check that runs before a reset).
    /// </summary>
    public partial class HistoryView : UserControl
    {
        private const int MaxRows = 1000;
        private const int MaxExport = 1000000;
        private bool _loading;
        private readonly ShippedCheckService _shipped = new ShippedCheckService();

        public HistoryView()
        {
            InitializeComponent();

            // The last 7 days to start with; widen the dates to look further back.
            dpFrom.SelectedDate = DateTime.Today.AddDays(-6);
            dpTo.SelectedDate = DateTime.Today;

            IsVisibleChanged += async (s, e) =>
            {
                if (IsVisible)
                    await SearchAsync();
            };
        }

        private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        private async void btnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync();

        private async void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtSearch.Clear();
            dpFrom.SelectedDate = null;
            dpTo.SelectedDate = null;
            cboMachine.SelectedIndex = 0;
            cboLogiwa.SelectedIndex = 0;
            chkDiff.IsChecked = false;
            await SearchAsync();
        }

        private async void Filter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SearchAsync();
            }
        }

        public async Task SearchAsync()
        {
            if (_loading) return;

            DateTime? from = dpFrom.SelectedDate, to = dpTo.SelectedDate;
            if (from.HasValue && to.HasValue && from.Value > to.Value)
            {
                MessageBox.Show(Window.GetWindow(this), "The From date is after the To date.", "History", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string search = txtSearch.Text, machine = TagOf(cboMachine), state = TagOf(cboLogiwa);
            bool diff = chkDiff.IsChecked == true;

            _loading = true;
            btnSearch.IsEnabled = false;
            try
            {
                var rowsTask = Task.Run(() => ScreenData.GetHistory(search, from, to, machine, state, diff, MaxRows));
                var countsTask = Task.Run(() => ScreenData.GetHistoryCounts(search, from, to));
                List<HistoryRow> rows = await rowsTask;
                HistoryCounts c = await countsTask;

                gridHistory.ItemsSource = rows;
                lblEmpty.Text = rows.Count == 0 ? "No boxes match.\nChange the filters or widen the dates." : "";
                lblEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                lblCount.Text = rows.Count >= MaxRows
                    ? $"Showing the newest {MaxRows:N0}: narrow the dates or the filters to see fewer. Export CSV has every match."
                    : $"{rows.Count:N0} box(es) listed.";
                lblTotals.Text = $"In this date range, {c.Total:N0} box(es).  Machine: {c.Completed:N0} completed, {c.Failed:N0} failed, {c.Removed:N0} removed, "
                    + $"{c.InProgress:N0} in progress, {c.NotPacked:N0} not packed.  Logiwa: {c.LogiwaOpen:N0} open, {c.LogiwaShipped:N0} shipped, "
                    + $"{c.LogiwaCancelled:N0} cancelled, {c.LogiwaOnHold:N0} on hold, {c.LogiwaUnknown:N0} unknown.  Differences: {c.Differences:N0}.";
                lblCount.Foreground = (Brush)FindResource("MutedBrush");
                ShowHistory(null);
            }
            catch (Exception ex)
            {
                lblCount.Text = "Cannot read the history: " + ex.Message;
                lblCount.Foreground = (Brush)FindResource("DangerBrush");
            }
            finally
            {
                _loading = false;
                btnSearch.IsEnabled = true;
            }
        }

        private async void gridHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = gridHistory.SelectedItem as HistoryRow;
            btnCheck.IsEnabled = row != null && !row.Archived;
            if (row == null)
            {
                ShowHistory(null);
                return;
            }

            try
            {
                List<JobEvent> events = await Task.Run(() => ScreenData.GetJobEvents(row.PackJobId));
                if (!ReferenceEquals(gridHistory.SelectedItem, row)) return;   // the selection moved on while loading
                ShowHistory(row, events);
            }
            catch (Exception ex)
            {
                lblHistoryTitle.Text = "Cannot read the history: " + ex.Message;
                lstHistory.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowHistory(HistoryRow row, List<JobEvent> events = null)
        {
            if (row == null)
            {
                lblHistoryTitle.Text = "Select a row to see every step the machine reported for it.";
                lstHistory.ItemsSource = null;
                lstHistory.Visibility = Visibility.Collapsed;
                return;
            }

            bool any = events != null && events.Count > 0;
            string checkedText = row.LogiwaCheckedAt.HasValue ? $" Logiwa status stored {row.LogiwaCheckedAt.Value:yyyy-MM-dd HH:mm}." : "";
            lblHistoryTitle.Text = (any
                ? $"History of {row.TrackingNumber} (job {row.PackJobId}){(row.Archived ? ", archived" : "")}."
                : $"No steps were recorded for {row.TrackingNumber} (job {row.PackJobId}).") + checkedText;
            lstHistory.ItemsSource = events;
            lstHistory.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Asks Logiwa (through the web VM) for the selected box right now. The answer is stored on the box like the daily check does.</summary>
        private async void btnCheck_Click(object sender, RoutedEventArgs e)
        {
            var row = gridHistory.SelectedItem as HistoryRow;
            if (row == null) return;

            btnCheck.IsEnabled = false;
            string before = row.LogiwaStatus;
            try
            {
                var result = await _shipped.CheckBeforeResetAsync(row.TrackingNumber, CancellationToken.None);
                if (result.Verdict == ResetVerdict.Unknown)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        "Logiwa could not be asked about " + row.TrackingNumber + ".\nThe web VM or Logiwa may be unreachable, or the order is not known there.",
                        "Check in Logiwa", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    string was = string.IsNullOrEmpty(before) ? "nothing stored" : before;
                    MessageBox.Show(Window.GetWindow(this),
                        $"Logiwa says {row.TrackingNumber} is: {result.LogiwaStatus}\n(this PC had: {was}).",
                        "Check in Logiwa", MessageBoxButton.OK, MessageBoxImage.Information);
                    string keep = row.TrackingNumber;
                    await SearchAsync();
                    gridHistory.SelectedItem = (gridHistory.ItemsSource as List<HistoryRow>)?.FirstOrDefault(r => r.TrackingNumber == keep);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Cannot check: " + ex.Message, "Check in Logiwa", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnCheck.IsEnabled = gridHistory.SelectedItem is HistoryRow r2 && !r2.Archived;
            }
        }

        private async void btnExport_Click(object sender, RoutedEventArgs e)
        {
            DateTime? from = dpFrom.SelectedDate, to = dpTo.SelectedDate;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export box history",
                Filter = "CSV file (*.csv)|*.csv",
                FileName = $"CMC-history-{DateTime.Now:yyyyMMdd-HHmm}.csv"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            string search = txtSearch.Text, machine = TagOf(cboMachine), state = TagOf(cboLogiwa);
            bool diff = chkDiff.IsChecked == true;

            btnExport.IsEnabled = false;
            try
            {
                var rows = await Task.Run(() => ScreenData.GetHistory(search, from, to, machine, state, diff, MaxExport));
                await Task.Run(() =>
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Received (local),Received (UTC),Logiwa (UTC),Tracking number,Order,Client,Local status,Machine result,Logiwa status,Logiwa state,Difference,Packed at,Length mm,Width mm,Height mm,Weight g,Machine reason,Machine,Operator,Archived,Job");
                    foreach (var r in rows)
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(r.ReceivedText), Csv(r.ReceivedUtcText), Csv(r.LogiwaText), Csv(r.TrackingNumber), Csv(r.OrderCode), Csv(r.ClientCode),
                            Csv(r.LocalStatus), Csv(r.MachineResult), Csv(r.LogiwaStatus), Csv(r.LogiwaState), Csv(r.Difference), Csv(r.CompletedText),
                            Csv(r.LengthMm?.ToString()), Csv(r.WidthMm?.ToString()), Csv(r.HeightMm?.ToString()), Csv(r.WeightG?.ToString()),
                            Csv(r.MachineReason), Csv(r.MachineId), Csv(r.Operator), Csv(r.Archived ? "yes" : ""), Csv(r.PackJobId.ToString())
                        }));
                    System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));   // BOM so Excel reads it as UTF-8
                });
                lblCount.Text = $"Exported {rows.Count:N0} box(es) to {System.IO.Path.GetFileName(dlg.FileName)}";
                lblCount.Foreground = (Brush)FindResource("MutedBrush");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Cannot export: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnExport.IsEnabled = true;
            }
        }

        /// <summary>Quotes a CSV field; a leading = + - @ is prefixed so Excel does not run it as a formula.</summary>
        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if ("=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
