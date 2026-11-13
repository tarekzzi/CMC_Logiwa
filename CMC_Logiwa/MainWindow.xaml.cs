using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CMC_Logiwa
{
    public enum LogKind { Received, Sent, Info, Error }

    /// <summary>One line of the message log.</summary>
    public sealed class LogEntry
    {
        public LogEntry(string message)
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff");
            Message = message;
            if (message.StartsWith("ERROR:", StringComparison.Ordinal)) Kind = LogKind.Error;
            else if (message.StartsWith("SENT:", StringComparison.Ordinal)) Kind = LogKind.Sent;
            else if (message.StartsWith("INFO:", StringComparison.Ordinal)) Kind = LogKind.Info;
            else Kind = LogKind.Received;
        }

        public string Time { get; }
        public string Message { get; }
        public LogKind Kind { get; }

        /// <summary>The text used for filtering and export.</summary>
        public string Line => $"{Time}  {Message}";
    }

    /// <summary>One row of the "Duplicate scans" list.</summary>
    public sealed class DuplicateItem
    {
        public string OrderNumber { get; set; }
        public string Text { get; set; }
        public override string ToString() => Text;
    }

    public partial class MainWindow : Window
    {
        private const int MaxLogLines = 5000;

        private NetworkManager _networkManager;

        // Every log line, oldest first. The list box shows only the lines matching _logFilter.
        private readonly ObservableCollection<LogEntry> _logLines = new ObservableCollection<LogEntry>();
        private readonly ICollectionView _logView;
        private string _logFilter = "";
        private bool _exitConfirmed;

        private readonly DispatcherTimer _duplicatesTimer;
        private bool _refreshingDuplicates;

        public MainWindow(string userName)
        {
            InitializeComponent();

            var settings = DbSettingsStore.Load();
            lblSession.Text = settings == null ? userName : $"{settings.EnvironmentName}   ·   {userName}";

            _logView = CollectionViewSource.GetDefaultView(_logLines);
            _logView.Filter = o => MatchesLogFilter((LogEntry)o);
            listBoxLogs.ItemsSource = _logView;

            SetConnectionState(false);
            UpdateLogCount();
            Closing += MainWindow_Closing;
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            _duplicatesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _duplicatesTimer.Tick += async (s, e) => await RefreshDuplicatesAsync();
            Loaded += async (s, e) =>
            {
                await RefreshDuplicatesAsync();
                _duplicatesTimer.Start();
            };
            Closed += (s, e) => _duplicatesTimer.Stop();
        }

        private Brush Res(string key) => (Brush)FindResource(key);

        private void SetConnectionState(bool connected)
        {
            pill.Background = Res(connected ? "SuccessBgBrush" : "DangerHoverBrush");
            pillDot.Fill = Res(connected ? "SuccessBrush" : "DangerBrush");
            pillText.Foreground = Res(connected ? "SuccessBrush" : "DangerBrush");
            pillText.Text = connected ? "Connected" : "Not connected";

            btnStartConnection.IsEnabled = !connected;
            btnEndConnection.IsEnabled = connected;
            txtIPAddress.IsEnabled = !connected;
            txtPort.IsEnabled = !connected;
            txtMachineId.IsEnabled = !connected;
        }

        private async void btnStartConnection_Click(object sender, RoutedEventArgs e)
        {
            var sql = new SQLManager();

            if (!int.TryParse(txtPort.Text, out int portNumber))
            {
                MessageBox.Show(this, "Invalid port number.", "Connect", MessageBoxButton.OK, MessageBoxImage.Information);
                txtPort.Focus();
                return;
            }

            string machineId = txtMachineId.Text.Trim();

            if (string.IsNullOrWhiteSpace(machineId))
            {
                MessageBox.Show(this, "Machine ID is required.", "Connect", MessageBoxButton.OK, MessageBoxImage.Information);
                txtMachineId.Focus();
                return;
            }

            _networkManager = new NetworkManager(
                txtIPAddress.Text,
                portNumber,
                sqlManager: sql,
                machineId: machineId
            );

            _networkManager.OnMessageReceived += AppendLog;
            _networkManager.OnMessageSent += AppendLog;
            _networkManager.OnError += AppendLog;

            _networkManager.OnConnected += () => Dispatcher.BeginInvoke(new Action(() => SetConnectionState(true)));
            _networkManager.OnDisconnected += () => Dispatcher.BeginInvoke(new Action(() => SetConnectionState(false)));

            btnStartConnection.IsEnabled = false;
            await _networkManager.ConnectAsync();
            if (!_networkManager.IsConnected)
                SetConnectionState(false);
        }

        private async void btnEndConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_networkManager != null)
                await _networkManager.DisconnectAsync();
        }

        // ===== Message log =====

        private void AppendLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                // Ignore the rare case where the window closed while a message was in flight.
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }

            var entry = new LogEntry(message);
            _logLines.Add(entry);
            while (_logLines.Count > MaxLogLines)
                _logLines.RemoveAt(0);

            // Follow new lines unless the operator has selected something to read.
            if (listBoxLogs.SelectedItems.Count == 0 && MatchesLogFilter(entry))
                listBoxLogs.ScrollIntoView(entry);

            UpdateLogCount();
        }

        private bool MatchesLogFilter(LogEntry entry) =>
            _logFilter.Length == 0 || entry.Line.IndexOf(_logFilter, StringComparison.OrdinalIgnoreCase) >= 0;

        private List<LogEntry> GetFilteredLogLines() => _logLines.Where(MatchesLogFilter).ToList();

        private void UpdateLogCount()
        {
            int shown = _logFilter.Length == 0 ? _logLines.Count : _logLines.Count(MatchesLogFilter);

            lblLogCount.Text = _logFilter.Length == 0
                ? $"{_logLines.Count:N0} lines"
                : $"{shown:N0} of {_logLines.Count:N0} lines match \"{_logFilter}\"";
            lblLogCount.Foreground = Res(_logFilter.Length == 0 ? "MutedBrush" : "AccentBrush");

            lblLogEmpty.Text = _logLines.Count == 0
                ? "No messages yet.\nConnect to the machine to see traffic here."
                : "No lines match the filter.";
            lblLogEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void txtLogFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _logFilter = (txtLogFilter.Text ?? "").Trim();
            _logView.Refresh();

            var last = _logLines.LastOrDefault(MatchesLogFilter);
            if (last != null)
                listBoxLogs.ScrollIntoView(last);

            UpdateLogCount();
        }

        private void txtLogFilter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                txtLogFilter.Clear();
                e.Handled = true;
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+F jumps to the log filter from anywhere in the window.
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                txtLogFilter.Focus();
                txtLogFilter.SelectAll();
                e.Handled = true;
            }
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            UpdateLogCount();
        }

        private void btnExportLog_Click(object sender, RoutedEventArgs e)
        {
            if (_logLines.Count == 0)
            {
                MessageBox.Show(this, "The message log is empty, so there is nothing to export.", "Export message log",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<LogEntry> filtered = GetFilteredLogLines();
            List<LogEntry> linesToSave;

            var dialog = new ExportLogDialog(_logLines.Count, filtered.Count, _logFilter) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            switch (dialog.Scope)
            {
                case LogExportScope.Filtered:
                    linesToSave = filtered;
                    break;
                case LogExportScope.LastLines:
                    List<LogEntry> view = _logFilter.Length == 0 ? _logLines.ToList() : filtered;
                    linesToSave = view.Skip(Math.Max(0, view.Count - dialog.LastLineCount)).ToList();
                    break;
                default:
                    linesToSave = _logLines.ToList();
                    break;
            }

            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save message log",
                Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
                FileName = $"CMC-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExt = "txt",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (save.ShowDialog(this) != true)
                return;

            try
            {
                System.IO.File.WriteAllLines(save.FileName, linesToSave.Select(l => l.Line), Encoding.UTF8);
                AppendLog($"INFO: Exported {linesToSave.Count:N0} log lines to {save.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not save the log", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== Exit =====

        private void btnExit_Click(object sender, RoutedEventArgs e) => Close();

        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_exitConfirmed || _networkManager == null || !_networkManager.IsConnected)
                return;

            if (Application.Current.Dispatcher.HasShutdownStarted)
                return;

            e.Cancel = true;

            var answer = MessageBox.Show(this,
                "The machine is still connected. Disconnect and exit?",
                "Exit CMC Logiwa",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            _exitConfirmed = true;
            await _networkManager.DisconnectAsync();
            Close();
        }

        // ===== Duplicate scans: reset or reject =====

        /// <summary>
        /// Lists tracking numbers scanned again after they were processed (last 24 h) that nobody has
        /// reset or rejected yet. Refreshed every few seconds while the window is open.
        /// </summary>
        private async Task RefreshDuplicatesAsync()
        {
            if (_refreshingDuplicates)
                return;
            _refreshingDuplicates = true;

            try
            {
                DataTable dt = await Task.Run(() => new SQLManager().ExecuteStoredProcedure(
                    "dbo.sp_List_RecentDuplicates", new Dictionary<string, object> { { "@Hours", 24 } }));

                string selected = (lstDuplicates.SelectedItem as DuplicateItem)?.OrderNumber;

                var items = new List<DuplicateItem>();
                foreach (DataRow row in dt.Rows)
                {
                    string order = row["OrderNumber"]?.ToString();
                    string status = row["OrderStatus"] == DBNull.Value ? "archived" : row["OrderStatus"].ToString();
                    DateTime last = Convert.ToDateTime(row["LastScan"]);
                    int scans = Convert.ToInt32(row["Scans"]);

                    items.Add(new DuplicateItem
                    {
                        OrderNumber = order,
                        Text = $"{order}   ·   {status}   ·   {last:HH:mm:ss}" + (scans > 1 ? $"   ({scans}×)" : "")
                    });
                }

                lstDuplicates.ItemsSource = items;
                lstDuplicates.SelectedItem = items.FirstOrDefault(i => i.OrderNumber == selected);

                lblDuplicatesHint.Text = items.Count == 0 ? "No duplicate scans waiting." : $"{items.Count} waiting.";
            }
            catch
            {
                lblDuplicatesHint.Text = "Cannot read the duplicate list.";
            }
            finally
            {
                _refreshingDuplicates = false;
            }
        }

        private async void btnResetSelected_Click(object sender, RoutedEventArgs e)
        {
            var item = lstDuplicates.SelectedItem as DuplicateItem;
            if (item == null)
            {
                MessageBox.Show(this, "Select a duplicate scan first.", "Reset", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (ConfirmReset(item.OrderNumber))
            {
                ResetOrder(item.OrderNumber);
                await RefreshDuplicatesAsync();
            }
        }

        private async void btnRejectSelected_Click(object sender, RoutedEventArgs e)
        {
            var item = lstDuplicates.SelectedItem as DuplicateItem;
            if (item == null)
            {
                MessageBox.Show(this, "Select a duplicate scan first.", "Reject", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                new SQLManager().ExecuteStoredProcedure("dbo.sp_Reject_ByOrder", new Dictionary<string, object>
                {
                    { "@OrderNumber", item.OrderNumber },
                    { "@User", SessionVariables.UserName }
                });
                AppendLog($"INFO: Duplicate {item.OrderNumber} rejected.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Reject failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await RefreshDuplicatesAsync();
        }

        // ===== Reset order =====

        private async void btnResetOrderNumber_Click(object sender, RoutedEventArgs e)
        {
            string orderNumber = txtResetOrderNumber.Text.Trim();

            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                MessageBox.Show(this, "Enter the tracking number to reset.", "Reset order",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtResetOrderNumber.Focus();
                return;
            }

            if (!ConfirmReset(orderNumber))
                return;

            if (ResetOrder(orderNumber))
            {
                txtResetOrderNumber.Clear();
                txtResetOrderNumber.Focus();
            }
            await RefreshDuplicatesAsync();
        }

        private bool ConfirmReset(string orderNumber) =>
            MessageBox.Show(this,
                $"Reset {orderNumber} so it can be processed again?\n\nThe previous result stays in the history.",
                "Reset order",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;

        /// <summary>Creates a new Ready record for the tracking number (any operator may do this). Returns true on success.</summary>
        private bool ResetOrder(string orderNumber)
        {
            try
            {
                DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_Reset_ByOrder", new Dictionary<string, object>
                {
                    { "@OrderNumber", orderNumber },
                    { "@User", SessionVariables.UserName }
                });

                string outcome = dt.Rows.Count > 0 ? dt.Rows[0]["Result"]?.ToString() : null;

                switch (outcome)
                {
                    case "OK":
                        AppendLog($"INFO: {orderNumber} reset (new record {dt.Rows[0]["NewPackJobID"]}).");
                        return true;
                    case "NOT_FOUND":
                        MessageBox.Show(this, $"{orderNumber} was not found.", "Reset order",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return false;
                    case "NOTHING_TO_RESET":
                        MessageBox.Show(this, $"{orderNumber} has not been processed yet, so there is nothing to reset.",
                            "Reset order", MessageBoxButton.OK, MessageBoxImage.Information);
                        return false;
                    default:
                        MessageBox.Show(this, "The reset did not return a result.", "Reset order",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Reset order failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
    }
}
