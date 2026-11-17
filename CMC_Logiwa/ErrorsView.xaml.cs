using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// Errors tab: boxes that did not pack (Failed or Removed) with the reason the machine reported, filtered by
    /// tracking number / job number / order code and by date. The newest first; a click shows every step of that job.
    /// </summary>
    public partial class ErrorsView : UserControl
    {
        private const int MaxRows = 500;
        private bool _loading;

        public ErrorsView()
        {
            InitializeComponent();

            // The last 7 days to start with: the errors that matter are usually recent.
            dpFrom.SelectedDate = DateTime.Today.AddDays(-6);
            dpTo.SelectedDate = DateTime.Today;

            // Load every time the tab is shown (new errors appear without pressing Search), not at start-up.
            IsVisibleChanged += async (s, e) =>
            {
                if (IsVisible)
                    await SearchAsync();
            };
        }

        private async void btnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync();

        private async void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtSearch.Clear();
            dpFrom.SelectedDate = null;
            dpTo.SelectedDate = null;
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
                MessageBox.Show(Window.GetWindow(this), "The From date is after the To date.", "Errors", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _loading = true;
            btnSearch.IsEnabled = false;
            try
            {
                string search = txtSearch.Text;
                List<ErrorRow> rows = await Task.Run(() => ScreenData.GetErrors(search, from, to, MaxRows));
                gridErrors.ItemsSource = rows;
                lblEmpty.Text = rows.Count == 0 ? "No errors match.\nChange the tracking number or the dates." : "";
                lblEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                lblCount.Text = rows.Count >= MaxRows
                    ? $"Showing the newest {MaxRows:N0}: narrow the dates or the tracking number to see fewer."
                    : $"{rows.Count:N0} error(s)";
                lblCount.Foreground = (Brush)FindResource("MutedBrush");
                ShowHistory(null);
            }
            catch (Exception ex)
            {
                lblCount.Text = "Cannot read the errors: " + ex.Message;
                lblCount.Foreground = (Brush)FindResource("DangerBrush");
            }
            finally
            {
                _loading = false;
                btnSearch.IsEnabled = true;
            }
        }

        private async void gridErrors_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = gridErrors.SelectedItem as ErrorRow;
            if (row == null)
            {
                ShowHistory(null);
                return;
            }

            try
            {
                List<JobEvent> events = await Task.Run(() => ScreenData.GetJobEvents(row.PackJobId));
                if (!ReferenceEquals(gridErrors.SelectedItem, row)) return;   // the selection moved on while loading
                ShowHistory(row, events);
            }
            catch (Exception ex)
            {
                lblHistoryTitle.Text = "Cannot read the history: " + ex.Message;
                lstHistory.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowHistory(ErrorRow row, List<JobEvent> events = null)
        {
            if (row == null)
            {
                lblHistoryTitle.Text = "Select a row to see every step the machine reported for it.";
                lstHistory.ItemsSource = null;
                lstHistory.Visibility = Visibility.Collapsed;
                return;
            }

            bool any = events != null && events.Count > 0;
            lblHistoryTitle.Text = any
                ? $"History of {row.TrackingNumber} (job {row.PackJobId}){(row.Archived ? ", archived" : "")}"
                : $"No steps were recorded for {row.TrackingNumber} (job {row.PackJobId}).";
            lstHistory.ItemsSource = events;
            lstHistory.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
