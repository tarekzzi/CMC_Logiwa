using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// Dashboard: what can be processed now, how many boxes were finished today / this week / this month, and the speed of the
    /// machine (boxes per hour, average time per box) for a period, client and search of the user's choice, as charts and a list.
    /// </summary>
    public partial class OverviewView : UserControl
    {
        private const int MaxListed = 500;
        private const string AllClients = "All clients";

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private bool _refreshing;
        private bool _again;          // a refresh was asked for while one was running
        private bool _ready;          // the controls exist: ignore the change events raised while the XAML loads
        private bool _suppress;       // the code (not the user) is changing a filter control

        public OverviewView()
        {
            InitializeComponent();

            chartThroughput.BarBrush = (Brush)FindResource("AccentBrush");
            chartThroughput.ExtraBrush = (Brush)FindResource("DangerBrush");
            chartThroughput.EmptyText = "No boxes finished in this period.";
            chartSpeed.BarBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E));
            chartSpeed.EmptyText = "No timings in this period.";
            chartOperators.BarBrush = (Brush)FindResource("AccentBrush");
            chartOperators.ExtraBrush = (Brush)FindResource("DangerBrush");
            chartOperators.LineUnit = " s";
            chartOperators.EmptyText = "No boxes finished in this period.";

            _suppress = true;
            cboClient.Items.Add(AllClients);
            cboClient.SelectedIndex = 0;
            dpFrom.SelectedDate = DateTime.Today.AddDays(-6);
            dpTo.SelectedDate = DateTime.Today;
            _suppress = false;
            _ready = true;

            _timer.Tick += async (s, e) => await RefreshAsync();
            Loaded += async (s, e) =>
            {
                await RefreshAsync();
                _timer.Start();
            };
            Unloaded += (s, e) => _timer.Stop();
        }

        // ===== Filter events =====

        private async void Filter_Changed(object sender, EventArgs e)
        {
            if (!_ready || _suppress) return;
            bool custom = cboPeriod.SelectedIndex == 6;
            pnlFrom.Visibility = pnlTo.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            await RefreshAsync();
        }

        private async void Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RefreshAsync();
        }

        private async void btnSearch_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void btnClear_Click(object sender, RoutedEventArgs e)
        {
            _suppress = true;
            txtSearch.Clear();
            cboPeriod.SelectedIndex = 0;
            cboClient.SelectedIndex = 0;
            cboStatus.SelectedIndex = 0;
            pnlFrom.Visibility = pnlTo.Visibility = Visibility.Collapsed;
            _suppress = false;
            await RefreshAsync();
        }

        private async void List_Switched(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            bool finished = rbFinished.IsChecked == true;
            bool stops = rbStops.IsChecked == true;
            gridFinished.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
            gridAvailable.Visibility = !finished && !stops ? Visibility.Visible : Visibility.Collapsed;
            gridStops.Visibility = stops ? Visibility.Visible : Visibility.Collapsed;
            cboStatus.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
            btnExport.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
            cboStopMin.Visibility = stops ? Visibility.Visible : Visibility.Collapsed;
            await RefreshAsync();
        }

        // ===== Period =====

        /// <summary>The chosen period as [from, to) in local time, or null when a custom range is incomplete.</summary>
        private bool TryGetPeriod(out DateTime from, out DateTime toExclusive, out string title)
        {
            DateTime today = DateTime.Today;
            ScreenData.PeriodStarts(DateTime.Now, out _, out DateTime week, out DateTime month);
            from = today;
            toExclusive = today.AddDays(1);
            title = "today";

            switch (cboPeriod.SelectedIndex)
            {
                case 0: break;
                case 1: from = today.AddDays(-1); toExclusive = today; title = "yesterday"; break;
                case 2: from = today.AddDays(-6); title = "the last 7 days"; break;
                case 3: from = week; toExclusive = week.AddDays(7); title = "this week"; break;
                case 4: from = today.AddDays(-29); title = "the last 30 days"; break;
                case 5: from = month; toExclusive = month.AddMonths(1); title = "this month"; break;
                default:
                    if (!dpFrom.SelectedDate.HasValue || !dpTo.SelectedDate.HasValue) return false;
                    from = dpFrom.SelectedDate.Value.Date;
                    toExclusive = dpTo.SelectedDate.Value.Date.AddDays(1);
                    if (toExclusive <= from) return false;
                    title = $"{from:yyyy-MM-dd} to {toExclusive.AddDays(-1):yyyy-MM-dd}";
                    break;
            }
            return true;
        }

        // ===== Refresh =====

        public async Task RefreshAsync()
        {
            if (_refreshing) { _again = true; return; }
            _refreshing = true;
            try
            {
                do
                {
                    _again = false;
                    await DoRefreshAsync();
                } while (_again);
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async Task DoRefreshAsync()
        {
            try
            {
                DateTime now = DateTime.Now;
                var counts = await Task.Run(() => ScreenData.GetCounts(now));
                ShowCounts(counts);

                if (!TryGetPeriod(out DateTime from, out DateTime to, out string title))
                {
                    lblUpdated.Text = "Choose both dates of the range.";
                    return;
                }

                string search = txtSearch.Text;
                string client = cboClient.SelectedIndex > 0 ? cboClient.SelectedItem as string : null;
                string status = cboStatus.SelectedIndex > 0 ? ((ComboBoxItem)cboStatus.SelectedItem).Content.ToString() : null;
                bool hourly = (to - from).TotalDays <= 2;
                DateTime seriesTo = hourly && to > now ? new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1) : to;
                bool finishedList = rbFinished.IsChecked == true;
                bool stopsList = rbStops.IsChecked == true;
                int stopMin = StopMinutes();

                var summaryTask = Task.Run(() => ScreenData.GetSummary(from, to, search, client));
                var seriesTask = Task.Run(() => ScreenData.GetSeries(from, seriesTo, hourly, search, client));
                var clientsTask = Task.Run(() => ScreenData.GetDashboardClients());
                var operatorsTask = Task.Run(() => ScreenData.GetOperators(from, to, search, client));
                var stopsTask = Task.Run(() => ScreenData.GetStops(from, to, stopMin, search, client));
                var processedTask = finishedList ? Task.Run(() => ScreenData.GetProcessed(from, to, search, client, status, MaxListed)) : null;
                var jobsTask = finishedList || stopsList ? null : Task.Run(() => ScreenData.GetAvailableJobs(MaxListed));

                var summary = await summaryTask;
                var series = await seriesTask;
                var stops = await stopsTask;
                FillClients(await clientsTask);

                ShowSummary(summary, title, from, to, now);
                ShowCharts(series, hourly, title, stops, stopMin);
                ShowOperators(await operatorsTask);

                if (stopsList)
                {
                    gridStops.ItemsSource = stops;
                    lblEmpty.Text = $"No stops of {stopMin}+ minutes in this period.";
                    lblEmpty.Visibility = stops.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    lblListInfo.Text = $"{stops.Count:N0} stop(s), newest first";
                }
                else if (finishedList)
                {
                    var rows = await processedTask;
                    gridFinished.ItemsSource = rows;
                    lblEmpty.Text = "No boxes match this period and filter.";
                    lblEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    lblListInfo.Text = rows.Count >= MaxListed ? $"Newest {rows.Count:N0}" : $"{rows.Count:N0} box(es), newest first";
                }
                else
                {
                    var jobs = await jobsTask;
                    gridAvailable.ItemsSource = jobs;
                    lblEmpty.Text = "No jobs are waiting for the machine.";
                    lblEmpty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    lblListInfo.Text = counts.Available > jobs.Count ? $"Newest {jobs.Count:N0} of {counts.Available:N0}" : $"{counts.Available:N0} job(s), newest first";
                }

                lblUpdated.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss");
                lblUpdated.Foreground = (Brush)FindResource("MutedBrush");
            }
            catch (Exception ex)
            {
                lblUpdated.Text = "Cannot read the database: " + ex.Message;
                lblUpdated.Foreground = (Brush)FindResource("DangerBrush");
            }
        }

        private void FillClients(List<string> clients)
        {
            var wanted = new List<string> { AllClients };
            wanted.AddRange(clients);
            var have = cboClient.Items.Cast<object>().Select(o => o.ToString()).ToList();
            if (have.SequenceEqual(wanted)) return;

            string selected = cboClient.SelectedItem as string;
            _suppress = true;
            cboClient.Items.Clear();
            foreach (var c in wanted) cboClient.Items.Add(c);
            cboClient.SelectedItem = selected != null && wanted.Contains(selected) ? selected : AllClients;
            _suppress = false;
        }

        private void ShowCounts(DashboardCounts counts)
        {
            numAvailable.Text = counts.Available.ToString("N0");
            subAvailable.Text = counts.NoLabel == 0 && counts.InProgress == 0
                ? "Ready for the machine now"
                : $"{counts.NoLabel:N0} waiting for a label  ·  {counts.InProgress:N0} on the machine";

            numToday.Text = counts.ProcessedToday.ToString("N0");
            numWeek.Text = counts.ProcessedWeek.ToString("N0");
            numMonth.Text = counts.ProcessedMonth.ToString("N0");
            subToday.Text = Problems(counts.ProblemsToday);
            subWeek.Text = Problems(counts.ProblemsWeek);
            subMonth.Text = Problems(counts.ProblemsMonth);
            subToday.Foreground = ProblemBrush(counts.ProblemsToday);
            subWeek.Foreground = ProblemBrush(counts.ProblemsWeek);
            subMonth.Foreground = ProblemBrush(counts.ProblemsMonth);
        }

        private void ShowSummary(DashboardSummary s, string title, DateTime from, DateTime to, DateTime now)
        {
            lblPeriodCompleted.Text = "Boxes completed, " + title;
            numCompleted.Text = s.Completed.ToString("N0");
            subCompleted.Text = Problems(s.Problems);
            subCompleted.Foreground = ProblemBrush(s.Problems);

            if (s.Completed == 0 || s.ActiveHours == 0)
            {
                numPerHour.Text = "–";
                subPerHour.Text = "No boxes finished yet";
            }
            else
            {
                numPerHour.Text = ((double)s.Completed / s.ActiveHours).ToString("0.#");
                // The same figure over the whole working spell (first box to now / last box), so a long break lowers it.
                DateTime end = to > now ? now : s.LastAt ?? now;
                double spell = s.FirstAt.HasValue ? (end - s.FirstAt.Value).TotalHours : 0;
                subPerHour.Text = $"while running ({s.ActiveHours} active hour(s))"
                    + (spell >= 0.25 ? $"  ·  {s.Completed / spell:0.#} / h since the first box" : "");
            }

            if (s.AvgSec.HasValue)
            {
                numAvgTime.Text = s.AvgSec.Value.ToString("0.#") + " s";
                subAvgTime.Text = $"fastest {s.MinSec} s  ·  slowest {s.MaxSec} s";
            }
            else
            {
                numAvgTime.Text = "–";
                subAvgTime.Text = "No timings yet";
            }

            if (s.PeakHour.HasValue)
            {
                numPeak.Text = s.PeakCount.ToString("N0") + " boxes";
                subPeak.Text = $"{s.PeakHour.Value:HH:mm}–{s.PeakHour.Value.AddHours(1):HH:mm}" + (s.PeakHour.Value.Date != DateTime.Today ? $" on {s.PeakHour.Value:yyyy-MM-dd}" : "");
            }
            else
            {
                numPeak.Text = "–";
                subPeak.Text = "";
            }
        }

        private int StopMinutes()
        {
            switch (cboStopMin.SelectedIndex)
            {
                case 0: return 5;
                case 2: return 15;
                case 3: return 30;
                default: return 10;
            }
        }

        private void ShowOperators(List<OperatorRow> rows)
        {
            chartOperators.SetData(rows.Take(20).Select(o => new ChartPoint
            {
                Label = o.Operator,
                Bar = o.Completed,
                Extra = o.Problems,
                Line = o.AvgSec,
                Tooltip = $"{o.Operator}\n{o.Completed:N0} completed" + (o.Problems > 0 ? $", {o.Problems:N0} failed/removed" : "")
                          + (o.AvgSec.HasValue ? $"\naverage {o.AvgSec.Value:0.#} s per box" : "")
            }));
        }

        private async void btnExport_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetPeriod(out DateTime from, out DateTime to, out string title))
            {
                MessageBox.Show(Window.GetWindow(this), "Choose both dates of the range first.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export finished boxes",
                Filter = "CSV file (*.csv)|*.csv",
                FileName = $"CMC-finished-boxes-{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}.csv"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            string search = txtSearch.Text;
            string client = cboClient.SelectedIndex > 0 ? cboClient.SelectedItem as string : null;
            string status = cboStatus.SelectedIndex > 0 ? ((ComboBoxItem)cboStatus.SelectedItem).Content.ToString() : null;

            btnExport.IsEnabled = false;
            try
            {
                var rows = await Task.Run(() => ScreenData.GetProcessed(from, to, search, client, status, 1000000));
                await Task.Run(() =>
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Finished,Tracking number,Order,Client,Result,Seconds,Length mm,Width mm,Height mm,Weight g,Machine,Operator,Job");
                    foreach (var r in rows)
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(r.FinishedText), Csv(r.TrackingNumber), Csv(r.OrderCode), Csv(r.ClientCode), Csv(r.Status),
                            Csv(r.DurationSec?.ToString()), Csv(r.LengthMm?.ToString()), Csv(r.WidthMm?.ToString()), Csv(r.HeightMm?.ToString()),
                            Csv(r.WeightG?.ToString()), Csv(r.MachineId), Csv(r.Operator), Csv(r.PackJobId.ToString())
                        }));
                    System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));   // BOM so Excel reads it as UTF-8
                });
                lblUpdated.Text = $"Exported {rows.Count:N0} box(es) to {System.IO.Path.GetFileName(dlg.FileName)}";
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

        private void ShowCharts(List<SeriesPoint> series, bool hourly, string title, List<StopRow> stops, int stopMin)
        {
            int stoppedSec = stops.Sum(s => s.Seconds);
            lblChart1Sub.Text = "Blue = completed, red = failed or removed  ·  " + (stops.Count == 0
                ? $"no stops of {stopMin}+ min"
                : $"{stops.Count:N0} stop(s) of {stopMin}+ min, {stoppedSec / 60:N0} min in total (see Stops)");

            bool shortDays = !hourly && series.Count <= 8;
            string Label(DateTime d) => hourly ? d.ToString("HH") : shortDays ? d.ToString("ddd d") : d.ToString("d MMM");
            string Span(DateTime d) => hourly ? $"{d:yyyy-MM-dd HH:00}–{d.AddHours(1):HH:00}" : d.ToString("ddd yyyy-MM-dd");

            lblChart1.Text = hourly ? "Boxes completed per hour" : "Boxes completed per day";
            lblChart2.Text = hourly ? "Average time per box per hour (seconds)" : "Average time per box per day (seconds)";

            chartThroughput.SetData(series.Select(p => new ChartPoint
            {
                Label = Label(p.Start),
                Bar = p.Completed,
                Extra = p.Problems,
                Tooltip = $"{Span(p.Start)}\n{p.Completed:N0} completed" + (p.Problems > 0 ? $", {p.Problems:N0} failed/removed" : "")
            }));

            chartSpeed.SetData(series.Select(p => new ChartPoint
            {
                Label = Label(p.Start),
                Bar = p.AvgSec ?? 0,
                Tooltip = $"{Span(p.Start)}\n" + (p.AvgSec.HasValue ? $"average {p.AvgSec.Value:0.#} s per box ({p.Completed:N0} boxes)" : "no boxes")
            }));
        }

        private static string Problems(int n) => n == 0 ? "no problems" : n == 1 ? "1 problem (see Errors)" : $"{n:N0} problems (see Errors)";

        private Brush ProblemBrush(int n) => (Brush)FindResource(n == 0 ? "MutedBrush" : "DangerBrush");
    }
}
