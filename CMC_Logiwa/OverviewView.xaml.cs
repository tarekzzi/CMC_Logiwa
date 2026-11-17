using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// Overview tab: how many jobs were processed today / this week / this month, how many are waiting for the machine,
    /// and the list of those. Refreshes itself every few seconds while the window is open.
    /// </summary>
    public partial class OverviewView : UserControl
    {
        private const int MaxListed = 200;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private bool _refreshing;

        public OverviewView()
        {
            InitializeComponent();
            _timer.Tick += async (s, e) => await RefreshAsync();
            Loaded += async (s, e) =>
            {
                await RefreshAsync();
                _timer.Start();
            };
            Unloaded += (s, e) => _timer.Stop();
        }

        public async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                DateTime now = DateTime.Now;
                var counts = await Task.Run(() => ScreenData.GetCounts(now));
                var jobs = await Task.Run(() => ScreenData.GetAvailableJobs(MaxListed));

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

                gridAvailable.ItemsSource = jobs;
                lblEmpty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                lblAvailableInfo.Text = counts.Available > jobs.Count
                    ? $"Newest {jobs.Count:N0} of {counts.Available:N0}"
                    : $"{counts.Available:N0} job(s), newest first";
                lblUpdated.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss");
                lblUpdated.Foreground = (Brush)FindResource("MutedBrush");
            }
            catch (Exception ex)
            {
                lblUpdated.Text = "Cannot read the database: " + ex.Message;
                lblUpdated.Foreground = (Brush)FindResource("DangerBrush");
            }
            finally
            {
                _refreshing = false;
            }
        }

        private static string Problems(int n) => n == 0 ? "no problems" : n == 1 ? "1 problem (see Errors)" : $"{n:N0} problems (see Errors)";

        private Brush ProblemBrush(int n) => (Brush)FindResource(n == 0 ? "MutedBrush" : "DangerBrush");
    }
}
