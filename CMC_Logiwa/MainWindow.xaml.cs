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
            ShowEnvironmentBanner(settings?.EnvironmentName);

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
                DateTime startedAt = DateTime.Now;     // orders stored before now are "past" orders for the Logiwa check
                await LoadMachineSettingsAsync();   // before the sync starts: it uses the machine ID
                StartWebSync();
                // Background: the machine can work while Logiwa is asked. Runs at start-up, then once a day as a safety net for any
                // status webhook that was missed (the webhooks themselves arrive through the web sync within seconds).
                _ = Task.Run(async () =>
                {
                    DateTime from = startedAt;
                    while (true)
                    {
                        await RunShippedCheckAsync(from).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromHours(24)).ConfigureAwait(false);
                        from = DateTime.Now;
                    }
                });

                // The warehouse is required: ask for it right away instead of leaving the app half working.
                if (!_hasWarehouse)
                {
                    MessageBox.Show(this,
                        "Choose the warehouse this machine serves.\n\nOnly that warehouse's orders are downloaded, and the machine cannot be connected until it is set. Settings > Machine opens now.",
                        "Warehouse not set", MessageBoxButton.OK, MessageBoxImage.Information);
                    await OpenSettingsAsync(machineTab: true);
                }

                await RefreshDuplicatesAsync();
                _duplicatesTimer.Start();
            };
            Closed += (s, e) =>
            {
                _wantConnected = false;
                StopReconnect();
                _duplicatesTimer.Stop();
                _sync?.Stop();
            };
        }

        // ===== Web sync (orders and labels from the web VM, label requests back) =====

        private OrderSyncService _sync;

        private void StartWebSync()
        {
            _sync = new OrderSyncService(txtMachineId.Text.Trim());
            _sync.OnLog += AppendLog;
            _sync.OnStatus += s => Dispatcher.BeginInvoke(new Action(() => ShowSyncStatus(s)));
            _sync.StatusChanged += () => Dispatcher.BeginInvoke(new Action(async () => await overview.RefreshAsync()));
            _sync.Start();
        }

        private void ShowSyncStatus(SyncStatus s)
        {
            string text;
            Brush brush = Res("MutedBrush");

            if (!s.Configured)
                text = "Web sync is off: set the web service address and API key with the Settings button.";
            else if (s.NeedsWarehouse)
            {
                text = "Orders are not downloaded: choose the warehouse in Settings > Machine.";
                brush = Res("DangerBrush");
            }
            else if (s.Error != null)
            {
                text = "Web sync problem: " + s.Error;
                brush = Res("DangerBrush");
            }
            else
            {
                text = "Web sync OK " + (s.LastOk.HasValue ? s.LastOk.Value.ToString("HH:mm:ss") : "");
                if (s.PendingLabelRequests > 0) text += $"  ·  {s.PendingLabelRequests} label request(s) waiting";
            }

            lblSync.Text = text;
            lblSync.Foreground = brush;
            lblSync.ToolTip = text;
        }

        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>A strip above the header: red for PRO so nobody mistakes live orders for tests, a quiet blue for DEV.</summary>
        private void ShowEnvironmentBanner(string environmentName)
        {
            if (string.Equals(environmentName, DbSettings.Pro, StringComparison.OrdinalIgnoreCase))
            {
                envBanner.Background = Res("DangerBrush");
                envBannerText.Foreground = Brushes.White;
                envBannerText.Text = "PRODUCTION  ·  live orders, real cartons";
                envBanner.Visibility = Visibility.Visible;
            }
            else if (string.Equals(environmentName, DbSettings.Dev, StringComparison.OrdinalIgnoreCase))
            {
                envBanner.Background = Res("AccentSoftBrush");
                envBannerText.Foreground = Res("AccentBrush");
                envBannerText.Text = "DEV  ·  Logiwa sandbox, test data";
                envBanner.Visibility = Visibility.Visible;
            }
            else
            {
                envBanner.Visibility = Visibility.Collapsed;
            }
        }

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
            if (!_hasWarehouse)
            {
                if (MessageBox.Show(this,
                        "The warehouse this machine serves is not set, so the machine cannot be connected.\n\nOpen Settings > Machine now?",
                        "Connect", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                    await OpenSettingsAsync(machineTab: true);
                return;
            }

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
            _networkManager.OnConnectionLost += reason => Dispatcher.BeginInvoke(new Action(() => HandleConnectionLost(reason)));

            btnStartConnection.IsEnabled = false;
            _wantConnected = true;
            bool ok = await _networkManager.ConnectAsync();
            if (!ok)
            {
                _wantConnected = false;
                SetConnectionState(false);
            }
            else
            {
                lblLink.Text = "";
            }
        }

        private async void btnEndConnection_Click(object sender, RoutedEventArgs e)
        {
            _wantConnected = false;        // a deliberate disconnect: never reconnect on its own
            StopReconnect();
            lblLink.Text = "";
            if (_networkManager != null)
                await _networkManager.DisconnectAsync();
        }

        // ===== Automatic reconnect =====
        // The CW1000 is the TCP server and this app the client, so after a drop we simply try again every 3 s
        // (the same interval the machine uses when it is the client, protocol 2.3).

        private static readonly TimeSpan ReconnectEvery = TimeSpan.FromSeconds(3);
        private bool _wantConnected;
        private bool _autoReconnect = true;   // from Settings > Machine

        /// <summary>
        /// Puts the saved machine settings (database) into the Machine tab. The ID, address and port are only replaced while
        /// disconnected: an open connection keeps the values it was started with.
        /// </summary>
        private bool _hasWarehouse;
        private string _warehouseCode = "";

        private void ApplyMachineSettings(MachineSettings m, bool connected)
        {
            _autoReconnect = m.AutoReconnect;
            _hasWarehouse = m.HasWarehouse;
            _warehouseCode = m.WarehouseCode ?? m.WarehouseId ?? "";
            if (connected) return;
            txtMachineId.Text = m.MachineId;
            txtIPAddress.Text = m.IpAddress;
            txtPort.Text = m.Port.ToString();
        }

        private async Task LoadMachineSettingsAsync()
        {
            string error = null;
            MachineSettings m = await Task.Run(() => MachineSettingsStore.TryLoad(out error));
            if (m == null)
            {
                m = MachineSettings.Defaults;
                AppendLog(error != null
                    ? "ERROR: Cannot read the machine settings, using the defaults: " + error
                    : "INFO: No machine settings saved yet, using the defaults (Settings > Machine).");
            }
            ApplyMachineSettings(m, connected: _networkManager != null && _networkManager.IsConnected);
        }
        private System.Threading.CancellationTokenSource _reconnectCts;
        private DateTime _linkLostAt;

        private void HandleConnectionLost(string reason)
        {
            SetConnectionState(false);
            _linkLostAt = DateTime.Now;
            lblLink.Text = $"Link lost at {_linkLostAt:HH:mm:ss}: {reason}.";
            lblLink.Foreground = Res("DangerBrush");

            if (_wantConnected && _autoReconnect)
                StartReconnect();
            else
                _wantConnected = false;
        }

        private void StartReconnect()
        {
            StopReconnect();
            var cts = new System.Threading.CancellationTokenSource();
            _reconnectCts = cts;
            _ = ReconnectLoopAsync(cts.Token);
        }

        private void StopReconnect()
        {
            try { _reconnectCts?.Cancel(); } catch { }
            _reconnectCts = null;
        }

        private async Task ReconnectLoopAsync(System.Threading.CancellationToken ct)
        {
            int attempt = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    attempt++;
                    pillText.Text = "Reconnecting…";
                    lblLink.Text = $"Link lost at {_linkLostAt:HH:mm:ss}. Reconnecting automatically (attempt {attempt})…";
                    btnStartConnection.IsEnabled = false;
                    btnEndConnection.IsEnabled = true;

                    if (_networkManager != null && await _networkManager.ConnectAsync(silent: true))
                    {
                        if (ct.IsCancellationRequested) { await _networkManager.DisconnectAsync(); return; }
                        var gap = DateTime.Now - _linkLostAt;
                        AppendLog($"INFO: Reconnected after {(int)gap.TotalSeconds} s ({attempt} attempt(s)).");
                        lblLink.Text = $"Reconnected at {DateTime.Now:HH:mm:ss} (was down {(int)gap.TotalSeconds} s).";
                        lblLink.Foreground = Res("MutedBrush");
                        return;
                    }

                    await Task.Delay(ReconnectEvery, ct);
                }
            }
            catch (OperationCanceledException) { }
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

        /// <summary>
        /// Settings: users, and the database / web service. The users part works while the machine is connected;
        /// changing the database does not (the open connection uses the old one), the window says so.
        /// </summary>
        private async void btnSettings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync(machineTab: false);

        private async Task OpenSettingsAsync(bool machineTab)
        {
            var dlg = new SettingsWindow(machineConnected: _networkManager != null && _networkManager.IsConnected, startOnMachineTab: machineTab) { Owner = this };
            dlg.ShowDialog();

            if (dlg.MachineSettingsChanged)
            {
                await LoadMachineSettingsAsync();
                _sync?.Stop();      // the sync keeps the machine ID it was started with
                StartWebSync();
                AppendLog($"INFO: Machine settings saved: ID {txtMachineId.Text}, {txtIPAddress.Text}:{txtPort.Text}, warehouse {_warehouseCode}, automatic reconnect {(_autoReconnect ? "on" : "off")}.");
                await overview.RefreshAsync();
            }

            if (!dlg.ConnectionSettingsChanged)
                return;

            var settings = DbSettingsStore.Load();
            lblSession.Text = settings == null ? SessionVariables.UserName : $"{settings.EnvironmentName}   ·   {SessionVariables.UserName}";
            ShowEnvironmentBanner(settings?.EnvironmentName);
            AppendLog("INFO: Settings saved.");
            OrderSyncService.RequestPoll();   // the sync reads the settings on every run, so the new address and key apply now
            await overview.RefreshAsync();     // the database may be a different one now
        }

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

            if (ConfirmReset(item.OrderNumber) && await AllowResetAsync(item.OrderNumber))
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

            if (!ConfirmReset(orderNumber) || !await AllowResetAsync(orderNumber))
                return;

            if (ResetOrder(orderNumber))
            {
                txtResetOrderNumber.Clear();
                txtResetOrderNumber.Focus();
            }
            await RefreshDuplicatesAsync();
        }

        // ===== Shipped in Logiwa =====

        private readonly ShippedCheckService _shipped = new ShippedCheckService();

        /// <summary>At start-up: asks Logiwa (through the web VM) about every order not processed yet and drops the shipped ones.</summary>
        private async Task RunShippedCheckAsync(DateTime startedAt)
        {
            _shipped.OnLog -= AppendLog;
            _shipped.OnLog += AppendLog;
            try
            {
                ShippedCheckSummary s = await _shipped.RunAtStartAsync(startedAt, System.Threading.CancellationToken.None).ConfigureAwait(false);

                if (s.Skipped)
                    AppendLog($"INFO: Logiwa check skipped: {s.SkippedReason}.");
                else if (s.Error != null)
                    AppendLog($"ERROR: Logiwa check stopped after {s.Asked} order(s): {s.Error}");
                else if (s.Asked > 0)
                    AppendLog($"INFO: Logiwa check done: {s.Asked} open order(s) checked, {s.Marked} already finished in Logiwa (shipped, cancelled ...), {s.Cleared} offered again, {s.NoAnswer} without an answer.");

                if (s.Marked > 0 || s.Cleared > 0)
                    await Dispatcher.InvokeAsync(() => overview.RefreshAsync()).Task.Unwrap();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR: Logiwa check failed: " + ex.Message);
            }
        }

        /// <summary>Before a reset: Logiwa must not show the order as shipped. If it cannot be checked the operator decides.</summary>
        private async Task<bool> AllowResetAsync(string orderNumber)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            (ResetVerdict verdict, string status) result;
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    result = await _shipped.CheckBeforeResetAsync(orderNumber, cts.Token);
            }
            catch (Exception)
            {
                result = (ResetVerdict.Unknown, null);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            switch (result.verdict)
            {
                case ResetVerdict.Closed:
                    AppendLog($"INFO: Reset of {orderNumber} refused: finished in Logiwa ({result.status}).");
                    MessageBox.Show(this, $"{orderNumber} is already finished in Logiwa (status: {result.status}), so it cannot be processed again.",
                        "Reset order", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;

                case ResetVerdict.Unknown:
                    return MessageBox.Show(this,
                        $"Logiwa could not be asked whether {orderNumber} is already finished (shipped, cancelled ...) (web service not reachable, not set up, or the order is unknown there).\n\nReset it anyway?",
                        "Reset order", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

                default:
                    return true;
            }
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
                    case "SHIPPED":
                        MessageBox.Show(this, $"{orderNumber} is already finished in Logiwa, so it cannot be processed again.", "Reset order",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
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
