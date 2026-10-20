using CMC_VER1.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Net.WebRequestMethods;
using CMC_VER1.Models;
using CMC_VER1.Helpers;

namespace CMC_VER1
{
    public partial class Main : Form
    {
        private NetworkManager _networkManager;
        private static readonly HttpClient Http = new HttpClient();
        private List<string> _ordersInMemory = new List<string>();
        private readonly WaveApiService _waveApiService;
        public string ApiKey { get; }
        public string Warehouse { get; }
        public string WarehouseCode { get; }
        public int WarehouseID { get; }
        public Main(string apiKey, string warehouse, string warehouseCode, string userEmail, int warehouseID)
        {
            
            InitializeComponent();
            lblNotConnected.Text = "Not Connected";
            lblNotConnected.ForeColor = Color.Red;
            ApiKey = apiKey;
            Warehouse = warehouse;
            WarehouseCode = warehouseCode;
            WarehouseID = warehouseID;
            userEmail = userEmail;
            _waveApiService = new WaveApiService(new HttpClient());
        }

        //private async void btnStartConnection_Click(object sender, EventArgs e)
        //{
        //    var sql = new SQLManager();

        //    if (!int.TryParse(txtPort.Text, out int portNumber))
        //    {
        //        MessageBox.Show("Invalid port number");
        //        return;
        //    }

        //    _networkManager = new NetworkManager(txtIPAddress.Text, portNumber, sqlManager: sql, getJobNumber: () => txtJobNumber.Text);

        //    _networkManager.OnMessageReceived += msg => BeginInvoke(new Action(() => listBoxLogs.Items.Add(msg)));
        //    _networkManager.OnMessageSent += msg => BeginInvoke(new Action(() => listBoxLogs.Items.Add(msg)));
        //    _networkManager.OnError += msg => BeginInvoke(new Action(() => listBoxLogs.Items.Add(msg)));

        //    _networkManager.OnConnected += () =>
        //         BeginInvoke(new Action(() =>
        //         {
        //             lblNotConnected.Text = "Connected";
        //             lblNotConnected.ForeColor = Color.Green;
        //         }));

        //                _networkManager.OnDisconnected += () =>
        //                    BeginInvoke(new Action(() =>
        //                    {
        //                        lblNotConnected.Text = "Not Connected";
        //                        lblNotConnected.ForeColor = Color.Red;
        //                    }));

        //                await _networkManager.ConnectAsync();

        //}
        private async void btnStartConnection_Click(object sender, EventArgs e)
        {
            var sql = new SQLManager();

            if (!int.TryParse(txtPort.Text, out int portNumber))
            {
                MessageBox.Show("Invalid port number");
                return;
            }

            string machineId = txtMachineId.Text.Trim();   // add a textbox for machine id if you do not already have one

            if (string.IsNullOrWhiteSpace(machineId))
            {
                MessageBox.Show("Machine ID is required");
                return;
            }

            _networkManager = new NetworkManager(
                txtIPAddress.Text,
                portNumber,
                sqlManager: sql,
                getJobNumber: () => txtJobNumber.Text,
                machineId: machineId
            );

            _networkManager.OnMessageReceived += msg => BeginInvoke(new Action(() => listBoxLogs.Items.Add(msg)));
            _networkManager.OnMessageSent += msg => BeginInvoke(new Action(() => listBoxLogs.Items.Add(msg)));
            _networkManager.OnError += msg => BeginInvoke(new Action(() => listBoxLogs.Items.Add(msg)));

            _networkManager.OnConnected += () =>
                BeginInvoke(new Action(() =>
                {
                    lblNotConnected.Text = "Connected";
                    lblNotConnected.ForeColor = Color.Green;
                }));

            _networkManager.OnDisconnected += () =>
                BeginInvoke(new Action(() =>
                {
                    lblNotConnected.Text = "Not Connected";
                    lblNotConnected.ForeColor = Color.Red;
                }));

            await _networkManager.ConnectAsync();
        }

        private async void btnEndConnection_Click(object sender, EventArgs e)
        {
            if (_networkManager != null)
                await _networkManager.DisconnectAsync();

        }

        private async void cmbSelectWave_DropDown(object sender, EventArgs e)
        {
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
                cmbSelectWave.DataSource = null;
                cmbSelectWave.Items.Clear();
                cmbSelectWave.Items.Add("Loading…");
                var waveNumbers = await FetchWaveNumbers(ApiEndpoints.GetListAllWavesByTypeAndStatuses, requestBody);

                if (waveNumbers.Count > 0)
                {
                    cmbSelectWave.DataSource = waveNumbers;
                    cmbSelectWave.ValueMember = "waveID";
                    cmbSelectWave.DisplayMember = "waveNumber";
                    cmbSelectWave.SelectedIndex = -1;
                }
                else
                {
                    cmbSelectWave.DataSource = null;
                    cmbSelectWave.Items.Clear();
                    cmbSelectWave.Items.Add("No available waves");
                }
            }
            catch (Exception ex)
            {
                cmbSelectWave.DataSource = null;
                cmbSelectWave.Items.Clear();
                cmbSelectWave.Items.Add("Failed to load waves");
                MessageBox.Show("Error loading waves: " + ex.Message);
            }
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

        private async void btnImportData_Click(object sender, EventArgs e)
        {
            try
            {
                string selectedWaveText = cmbSelectWave.Text?.ToString();
                string warehouseCode = "B"; // change to "F" if needed for this screen/process

                if (string.IsNullOrWhiteSpace(selectedWaveText))
                {
                    MessageBox.Show("Please select a wave number.");
                    return;
                }

                _ordersInMemory = await _waveApiService.RequestCMCPackingTaskAsync(warehouseCode, selectedWaveText, true);

                if (_ordersInMemory == null || _ordersInMemory.Count == 0)
                {
                    MessageBox.Show("No orders returned from RequestCMCPackingTask.");
                    return;
                }

                int totalOrders = _ordersInMemory.Count;
                int totalRowsSaved = 0;
                int failedOrders = 0;
                StringBuilder errorLog = new StringBuilder();

                foreach (string fulfilmentNumber in _ordersInMemory)
                {
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

                MessageBox.Show(finalMessage);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        private void btnResetOrderNumber_Click(object sender, EventArgs e)
        {
            try
            {
                string orderNumber = txtResetOrderNumber.Text.Trim();

                if (string.IsNullOrWhiteSpace(orderNumber))
                {
                    MessageBox.Show("Please enter Order Number.");
                    return;
                }

                        var parameters = new Dictionary<string, object>
                {
                    { "@OrderNumber", orderNumber }
                };

                SQLManager sqlManager = new SQLManager();

                sqlManager.ExecuteStoredProcedure("sp_Reset_Order", parameters);

                MessageBox.Show("Order reset completed.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }
    }
    
}
