using CMC_VER1.Helpers;
using CMC_VER1.Models;
using CMC_VER1.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CMC_VER1
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

    public partial class MainWindow : Window
    {
        private const int MaxLogLines = 5000;

        private NetworkManager _networkManager;
        private static readonly HttpClient Http = new HttpClient();
        private List<string> _ordersInMemory = new List<string>();
        private readonly WaveApiService _waveApiService;

        // Every log line, oldest first. The list box shows only the lines matching _logFilter.
        private readonly ObservableCollection<LogEntry> _logLines = new ObservableCollection<LogEntry>();
        private readonly ICollectionView _logView;
        private string _logFilter = "";
        private bool _exitConfirmed;

        public string ApiKey { get; }
        public string Warehouse { get; }
        public string WarehouseCode { get; }
        public int WarehouseID { get; }

        public MainWindow(string apiKey, string warehouse, string warehouseCode, string userEmail, int warehouseID)
        {
            InitializeComponent();

            ApiKey = apiKey;
            Warehouse = warehouse;
            WarehouseCode = warehouseCode;
            WarehouseID = warehouseID;
            lblSession.Text = $"{warehouse} ({warehouseCode})   ·   {userEmail}";

            _logView = CollectionViewSource.GetDefaultView(_logLines);
            _logView.Filter = o => MatchesLogFilter((LogEntry)o);
            listBoxLogs.ItemsSource = _logView;

            SetConnectionState(false);
            UpdateLogCount();
            Closing += MainWindow_Closing;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            _waveApiService = new WaveApiService(new HttpClient());
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
                getJobNumber: () => Dispatcher.Invoke(() => txtJobNumber.Text),
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
                "Exit CMC Control System",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            _exitConfirmed = true;
            await _networkManager.DisconnectAsync();
            Close();
        }

        // ===== Wave import =====

        private async void cmbSelectWave_DropDownOpened(object sender, EventArgs e)
        {
            string previous = (cmbSelectWave.SelectedItem as WaveList)?.waveNumber;
            ShowWaveHint("Loading waves…");

            try
            {
                var requestBody = new
                {
                    waveType = "Orders",
                    operation = "packing",
                    warehouseCode = "B",
                    waveStatuses = new[] { "Pendig", "Started" },
                    operationStatuses = new[] { "Pending", "Started" }
                };

                var waveNumbers = await FetchWaveNumbers(ApiEndpoints.GetListAllWavesByTypeAndStatuses, requestBody);

                cmbSelectWave.ItemsSource = waveNumbers;
                cmbSelectWave.SelectedItem = waveNumbers.FirstOrDefault(w => w.waveNumber == previous);
                ShowWaveHint(waveNumbers.Count == 0 ? "No available waves." : null);
            }
            catch (Exception ex)
            {
                cmbSelectWave.ItemsSource = null;
                ShowWaveHint("Could not load waves.");
                MessageBox.Show(this, ex.Message, "Could not load waves", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowWaveHint(string text)
        {
            txtWaveHint.Text = string.IsNullOrEmpty(text) ? " " : text; // keep the line reserved so the cards below do not jump
            txtWaveHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Hidden : Visibility.Visible;
        }

        private async Task<List<WaveList>> FetchWaveNumbers(string apiUrl, object body)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, apiUrl))
            {
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                req.Headers.Add("API-Key", SessionVariables.ApiKey);
                req.Headers.Add("User-Email", SessionVariables.UserEmail);

                using (var resp = await Http.SendAsync(req))
                {
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadAsStringAsync();

                    var waves = JsonSerializer.Deserialize<List<WaveList>>(json) ?? new List<WaveList>();

                    return waves
                        .Where(w => !string.IsNullOrWhiteSpace(w.waveNumber))
                        .OrderBy(w => w.waveNumber, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        private async void btnImportData_Click(object sender, RoutedEventArgs e)
        {
            object originalContent = btnImportData.Content;
            btnImportData.IsEnabled = false;
            btnImportData.Content = "Importing…";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                string selectedWaveText = (cmbSelectWave.SelectedItem as WaveList)?.waveNumber;
                string warehouseCode = "B"; // change to "F" if needed for this screen/process

                if (string.IsNullOrWhiteSpace(selectedWaveText))
                {
                    MessageBox.Show(this, "Please select a wave number.", "Wave import",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _ordersInMemory = await _waveApiService.RequestCMCPackingTaskAsync(warehouseCode, selectedWaveText, true);

                if (_ordersInMemory == null || _ordersInMemory.Count == 0)
                {
                    MessageBox.Show(this, "No orders returned from RequestCMCPackingTask.", "Wave import",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int totalOrders = _ordersInMemory.Count;
                int totalRowsSaved = 0;
                int failedOrders = 0;
                StringBuilder errorLog = new StringBuilder();

                int done = 0;
                foreach (string fulfilmentNumber in _ordersInMemory)
                {
                    btnImportData.Content = $"Importing {++done} of {totalOrders}…";
                    try
                    {
                        var processResult = await _waveApiService.ProcessCMCPackingTaskAsync(
                            fulfilmentNumber,
                            warehouseCode,
                            selectedWaveText);

                        int rowsSaved = _waveApiService.SaveProcessResultToSql(
                            fulfilmentNumber,
                            processResult.data);

                        totalRowsSaved += rowsSaved;
                    }
                    catch (Exception ex)
                    {
                        failedOrders++;
                        errorLog.AppendLine($"Order {fulfilmentNumber}: {ex.Message}");
                    }
                }

                string finalMessage =
                    $"Import complete.{Environment.NewLine}" +
                    $"Orders processed: {totalOrders}{Environment.NewLine}" +
                    $"Rows saved to SQL: {totalRowsSaved}{Environment.NewLine}" +
                    $"Failed orders: {failedOrders}";

                if (failedOrders > 0)
                {
                    finalMessage += $"{Environment.NewLine}{Environment.NewLine}Errors:{Environment.NewLine}{errorLog}";
                }

                MessageBox.Show(this, finalMessage, "Wave import", MessageBoxButton.OK,
                    failedOrders > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Wave import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnImportData.Content = originalContent;
                btnImportData.IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        // ===== Reset order =====

        private void btnResetOrderNumber_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string orderNumber = txtResetOrderNumber.Text.Trim();

                if (string.IsNullOrWhiteSpace(orderNumber))
                {
                    MessageBox.Show(this, "Enter the order number to reset.", "Reset order",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    txtResetOrderNumber.Focus();
                    return;
                }

                var confirm = MessageBox.Show(this,
                    $"Reset order {orderNumber} in the CMC database?",
                    "Reset order",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (confirm != MessageBoxResult.Yes)
                    return;

                var parameters = new Dictionary<string, object>
                {
                    { "@OrderNumber", orderNumber }
                };

                SQLManager sqlManager = new SQLManager();

                sqlManager.ExecuteStoredProcedure("sp_Reset_Order", parameters);

                AppendLog($"INFO: Order {orderNumber} reset.");

                txtResetOrderNumber.Clear();
                txtResetOrderNumber.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Reset order failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
