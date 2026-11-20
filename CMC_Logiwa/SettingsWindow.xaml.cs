using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMC_Logiwa.Models;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// Settings: who can log in on this PC (add a user, set a password, switch a user off or on),
    /// and where the database and the web service are. Nobody is ever deleted.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly LocalAuthService _auth = new LocalAuthService(new SQLManager());
        private readonly bool _machineConnected;

        /// <summary>True when the database or web service settings were saved: the caller restarts what depends on them.</summary>
        public bool ConnectionSettingsChanged { get; private set; }

        /// <summary>True when the machine settings (ID, IP, port, auto reconnect) were saved: the caller reloads them.</summary>
        public bool MachineSettingsChanged { get; private set; }

        /// <param name="startOnMachineTab">true = open on Settings > Machine (the app asks for the warehouse at start-up).</param>
        public SettingsWindow(bool machineConnected, bool startOnMachineTab = false)
        {
            InitializeComponent();
            _machineConnected = machineConnected;

            ShowConnection();
            ApplyRole();
            if (startOnMachineTab && _isAdmin) tabsSettings.SelectedItem = tabMachine;
            Closing += SettingsWindow_Closing;
            Loaded += async (s, e) =>
            {
                await LoadUsersAsync();
                if (!_isAdmin) return;
                await LoadMachineAsync();
                await LoadWebSettingsAsync();
            };
        }

        private readonly bool _isAdmin = SessionVariables.IsAdmin;

        /// <summary>True when the administrator chose the other environment: the app restarts at the sign-in window.</summary>
        public bool EnvironmentSwitchRequested { get; private set; }

        /// <summary>
        /// Operators see the users and can change their own password; everything else (users, machine, database and web service,
        /// environment, web VM settings) is for administrators.
        /// </summary>
        private void ApplyRole()
        {
            if (_isAdmin) return;
            cardAddUser.Visibility = Visibility.Collapsed;
            btnToggleActive.Visibility = Visibility.Collapsed;
            btnToggleAdmin.Visibility = Visibility.Collapsed;
            tabMachine.Visibility = Visibility.Collapsed;
            tabWeb.Visibility = Visibility.Collapsed;
            btnChangeDb.IsEnabled = false;
            btnSwitchEnv.IsEnabled = false;
            lblChangeHint.Text = "Only an administrator can change these settings or the environment.";
        }

        // ===== Warehouse (drop-down filled from Logiwa through the web service) =====

        private MachineSettings _savedMachine;   // what the database holds now (for the saved warehouse and its code)

        private async Task LoadWarehousesAsync()
        {
            string savedId = _savedMachine?.WarehouseId;
            string savedCode = _savedMachine?.WarehouseCode;

            var items = new List<WarehouseInfo>();
            string problem = null;

            DbSettings s = DbSettingsStore.Load();
            if (s == null || !s.HasWebService)
                problem = "Set the web service address and API key first (Database and web service tab): the warehouses come from Logiwa through it.";
            else
            {
                btnReloadWarehouses.IsEnabled = false;
                lblWarehouseHint.Text = "Loading the warehouses from Logiwa...";
                lblWarehouseHint.Foreground = (Brush)FindResource("MutedBrush");
                try
                {
                    using (var web = new CmcWebClient(s))
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        items = await web.GetWarehousesAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    problem = "Cannot load the warehouses: " + ex.Message;
                }
                btnReloadWarehouses.IsEnabled = true;
            }

            // Keep the saved warehouse selectable even when the list could not be loaded.
            if (!string.IsNullOrWhiteSpace(savedId) && !items.Any(w => string.Equals(w.Identifier, savedId, StringComparison.OrdinalIgnoreCase)))
                items.Insert(0, new WarehouseInfo { Identifier = savedId, Code = string.IsNullOrWhiteSpace(savedCode) ? savedId : savedCode });

            cboWarehouse.ItemsSource = items;
            cboWarehouse.SelectedItem = items.FirstOrDefault(w => string.Equals(w.Identifier, savedId, StringComparison.OrdinalIgnoreCase));
            if (cboWarehouse.SelectedItem == null && items.Count == 1) cboWarehouse.SelectedIndex = 0;   // only one warehouse: nothing to choose

            cboWarehouse.IsEnabled = !_machineConnected && items.Count > 0;

            if (problem != null)
            {
                lblWarehouseHint.Text = problem;
                lblWarehouseHint.Foreground = (Brush)FindResource("DangerBrush");
            }
            else
            {
                lblWarehouseHint.Text = items.Count == 0
                    ? "Logiwa returned no warehouses."
                    : (string.IsNullOrWhiteSpace(savedId) ? "Choose the warehouse this machine serves: only its orders are downloaded." : "Only this warehouse's orders are downloaded.");
                lblWarehouseHint.Foreground = (Brush)FindResource(string.IsNullOrWhiteSpace(savedId) ? "DangerBrush" : "MutedBrush");
            }
        }

        private async void btnReloadWarehouses_Click(object sender, RoutedEventArgs e) => await LoadWarehousesAsync();

        // ===== Machine =====

        private async Task LoadMachineAsync()
        {
            string error = null;
            MachineSettings m = await Task.Run(() => MachineSettingsStore.TryLoad(out error));

            if (m == null)
            {
                m = MachineSettings.Defaults;
                lblMachineHint.Text = error != null
                    ? "Cannot read the machine settings (has db\\006_machine_settings_dev.sql been run?): " + error
                    : "No machine settings are saved yet. These are the defaults; Save stores them.";
                lblMachineHint.Foreground = (Brush)FindResource(error != null ? "DangerBrush" : "MutedBrush");
            }
            else
            {
                lblMachineHint.Text = m.UpdatedAt.HasValue ? $"Last saved {m.UpdatedAt:yyyy-MM-dd HH:mm}" + (string.IsNullOrEmpty(m.UpdatedBy) ? "" : " by " + m.UpdatedBy) + "." : "";
                lblMachineHint.Foreground = (Brush)FindResource("MutedBrush");
            }

            _savedMachine = m;
            txtMachineIdSetting.Text = m.MachineId;
            txtIpSetting.Text = m.IpAddress;
            txtPortSetting.Text = m.Port.ToString();
            chkAutoSetting.IsChecked = m.AutoReconnect;
            await LoadWarehousesAsync();

            // The open connection uses the current values: change them only while disconnected.
            txtMachineIdSetting.IsEnabled = txtIpSetting.IsEnabled = txtPortSetting.IsEnabled = !_machineConnected;
            chkAutoSetting.IsEnabled = true;   // the checkbox is read when the link drops, so it can change at any time
            btnSaveMachine.IsEnabled = true;
            if (_machineConnected)
                lblMachineHint.Text = "The machine is connected: disconnect first to change the machine ID, address or port. "
                                      + "The automatic reconnect option can be changed now. " + lblMachineHint.Text;
        }

        private async void btnSaveMachine_Click(object sender, RoutedEventArgs e) => await SaveMachineAsync();

        /// <summary>True when the machine settings were saved.</summary>
        private async Task<bool> SaveMachineAsync()
        {
            if (!int.TryParse(txtPortSetting.Text.Trim(), out int port))
            {
                ShowStatus("The port must be a number from 1 to 65535.", error: true);
                txtPortSetting.Focus();
                return false;
            }

            var warehouse = cboWarehouse.SelectedItem as WarehouseInfo;
            if (warehouse == null)
            {
                ShowStatus("Choose the warehouse this machine serves.", error: true);
                cboWarehouse.Focus();
                return false;
            }

            var m = new MachineSettings
            {
                MachineId = txtMachineIdSetting.Text.Trim(),
                IpAddress = txtIpSetting.Text.Trim(),
                Port = port,
                AutoReconnect = chkAutoSetting.IsChecked == true,
                WarehouseId = warehouse.Identifier,
                WarehouseCode = warehouse.Code
            };

            string user = SessionVariables.UserName;
            btnSaveMachine.IsEnabled = false;
            string error = await Task.Run(() => MachineSettingsStore.Save(m, user));
            btnSaveMachine.IsEnabled = true;

            if (error != null)
            {
                ShowStatus(error, error: true);
                return false;
            }

            MachineSettingsChanged = true;
            ShowStatus("Machine settings saved.", error: false);
            await LoadMachineAsync();
            return true;
        }

        // ===== Unsaved changes =====

        private bool _closeConfirmed;

        /// <summary>True when the Machine tab shows something different from what is saved (a choice in the drop-down is not saved until the button is pressed).</summary>
        private bool IsMachineDirty()
        {
            MachineSettings s = _savedMachine;
            if (s == null || !cboWarehouse.IsLoaded) return false;

            var warehouse = cboWarehouse.SelectedItem as WarehouseInfo;
            bool warehouseChanged = warehouse != null && !string.Equals(warehouse.Identifier, s.WarehouseId, StringComparison.OrdinalIgnoreCase);

            return warehouseChanged
                   || txtMachineIdSetting.Text.Trim() != (s.MachineId ?? "")
                   || txtIpSetting.Text.Trim() != (s.IpAddress ?? "")
                   || txtPortSetting.Text.Trim() != s.Port.ToString()
                   || (chkAutoSetting.IsChecked == true) != s.AutoReconnect;
        }

        private void MarkUnsaved(object sender, EventArgs e)
        {
            if (!IsMachineDirty()) return;
            lblWarehouseHint.Text = "Not saved yet: press \"Save machine settings\".";
            lblWarehouseHint.Foreground = (Brush)FindResource("DangerBrush");
        }

        private async void SettingsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closeConfirmed || !IsMachineDirty()) return;

            e.Cancel = true;   // decided before the first await: the window stays open while we ask
            var answer = MessageBox.Show(this,
                "The machine settings were changed but not saved. Save them now?",
                "Settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes && !await SaveMachineAsync()) return;

            _closeConfirmed = true;
            Close();
        }

        // ===== Users =====

        private async Task LoadUsersAsync(string select = null)
        {
            try
            {
                List<UserInfo> users = await Task.Run(() => _auth.ListUsers());
                gridUsers.ItemsSource = users;

                int active = users.FindAll(u => u.IsActive).Count;
                int admins = users.FindAll(u => u.IsActive && u.IsAdmin).Count;
                lblUsersInfo.Text = $"{users.Count} user(s), {active} active, {admins} administrator(s). A switched-off user cannot log in; nothing is deleted."
                                    + (_isAdmin ? "" : " You can change your own password.");

                if (select != null)
                    gridUsers.SelectedItem = users.Find(u => string.Equals(u.Username, select, StringComparison.OrdinalIgnoreCase));
                if (gridUsers.SelectedItem == null && users.Count > 0)
                    gridUsers.SelectedItem = users.Find(u => string.Equals(u.Username, SessionVariables.UserName, StringComparison.OrdinalIgnoreCase)) ?? users[0];

                UpdateSelectedUser();
            }
            catch (Exception ex)
            {
                ShowStatus("Cannot read the users: " + ex.Message, error: true);
            }
        }

        private UserInfo Selected => gridUsers.SelectedItem as UserInfo;

        private void gridUsers_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedUser();

        private void UpdateSelectedUser()
        {
            UserInfo u = Selected;
            lblSelected.Text = u == null ? "Selected user" : "Selected user: " + u.Username;
            bool self = u != null && string.Equals(u.Username, SessionVariables.UserName, StringComparison.OrdinalIgnoreCase);
            btnSetPassword.IsEnabled = u != null && (_isAdmin || self);   // an operator only sets their own password
            btnToggleActive.IsEnabled = u != null;
            btnToggleActive.Content = u != null && !u.IsActive ? "Switch on" : "Switch off";
            btnToggleAdmin.IsEnabled = u != null;
            btnToggleAdmin.Content = u != null && u.IsAdmin ? "Make operator (remove administrator)" : "Make administrator";
            txtResetPassword.Clear();
            txtResetPassword2.Clear();
        }

        private async void btnAddUser_Click(object sender, RoutedEventArgs e)
        {
            string name = txtNewUser.Text.Trim();
            if (txtNewPassword.Password != txtNewPassword2.Password)
            {
                ShowStatus("The two passwords are not the same.", error: true);
                txtNewPassword2.Focus();
                return;
            }

            if (!_isAdmin) return;
            bool admin = chkNewAdmin.IsChecked == true;
            if (!Run(() => _auth.CreateUser(name, txtNewPassword.Password, admin, out string err) ? null : err))
                return;

            txtNewUser.Clear();
            txtNewPassword.Clear();
            txtNewPassword2.Clear();
            chkNewAdmin.IsChecked = false;
            ShowStatus($"User {name} added as {(admin ? "administrator" : "operator")}.", error: false);
            await LoadUsersAsync(select: name);
        }

        private void btnSetPassword_Click(object sender, RoutedEventArgs e)
        {
            UserInfo u = Selected;
            if (u == null) return;
            if (!_isAdmin && !string.Equals(u.Username, SessionVariables.UserName, StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus("You can only change your own password.", error: true);
                return;
            }

            if (txtResetPassword.Password != txtResetPassword2.Password)
            {
                ShowStatus("The two passwords are not the same.", error: true);
                txtResetPassword2.Focus();
                return;
            }

            if (!Run(() => _auth.SetPassword(u.Username, txtResetPassword.Password, out string err) ? null : err))
                return;

            txtResetPassword.Clear();
            txtResetPassword2.Clear();
            ShowStatus($"New password set for {u.Username}.", error: false);
        }

        private async void btnToggleActive_Click(object sender, RoutedEventArgs e)
        {
            UserInfo u = Selected;
            if (u == null || !_isAdmin) return;

            bool makeActive = !u.IsActive;
            if (!makeActive && string.Equals(u.Username, SessionVariables.UserName, StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus("You are logged in as " + u.Username + ": switch yourself off from another user's login.", error: true);
                return;
            }

            if (!makeActive && MessageBox.Show(this,
                    $"Switch off {u.Username}? That user will not be able to log in until you switch it on again.",
                    "Switch off user", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            if (!Run(() => _auth.SetActive(u.Username, makeActive, out string err) ? null : err))
                return;

            ShowStatus($"{u.Username} is now {(makeActive ? "active" : "switched off")}.", error: false);
            await LoadUsersAsync(select: u.Username);
        }

        private async void btnToggleAdmin_Click(object sender, RoutedEventArgs e)
        {
            UserInfo u = Selected;
            if (u == null || !_isAdmin) return;

            bool makeAdmin = !u.IsAdmin;
            bool self = string.Equals(u.Username, SessionVariables.UserName, StringComparison.OrdinalIgnoreCase);
            string question = makeAdmin
                ? $"Make {u.Username} an administrator? Administrators manage users and change every setting, including the web VM / Logiwa settings."
                : $"Make {u.Username} an operator? {(self ? "You will lose the administrator role when you sign in next time." : "")}";
            if (MessageBox.Show(this, question, "User role", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            if (!Run(() => _auth.SetAdmin(u.Username, makeAdmin, out string err) ? null : err))
                return;

            ShowStatus($"{u.Username} is now {(makeAdmin ? "an administrator" : "an operator")}.", error: false);
            await LoadUsersAsync(select: u.Username);
        }

        /// <summary>Runs a database change; the delegate returns null on success or the message to show. Database errors are shown, not thrown.</summary>
        private bool Run(Func<string> action)
        {
            try
            {
                string error = action();
                if (error == null) return true;
                ShowStatus(error, error: true);
                return false;
            }
            catch (Exception ex)
            {
                ShowStatus("The change was not saved: " + ex.Message, error: true);
                return false;
            }
        }

        // ===== Database and web service =====

        private void ShowConnection()
        {
            DbSettings s = DbSettingsStore.Load();
            valEnvironment.Text = s?.EnvironmentName ?? "not set";
            valServer.Text = s?.Server ?? "not set";
            valDatabase.Text = s?.Database ?? "not set";
            valWeb.Text = string.IsNullOrWhiteSpace(s?.WebServiceUrl) ? "not set (web sync is off)" : s.WebServiceUrl;
            valKey.Text = string.IsNullOrWhiteSpace(s?.ApiKey) ? "not set" : "saved (hidden)";

            btnChangeDb.IsEnabled = !_machineConnected && _isAdmin;
            lblChangeHint.Text = _machineConnected
                ? "Disconnect from the machine first: the open connection uses the current database."
                : "";

            // DEV and PRO are both kept on this PC; switching restarts the app at the sign-in window (each has its own users).
            string other = s?.EnvironmentName == DbSettings.Pro ? DbSettings.Dev : DbSettings.Pro;
            bool otherReady = DbSettingsStore.LoadProfile(other) != null;
            btnSwitchEnv.Content = "Switch to " + other;
            btnSwitchEnv.IsEnabled = !_machineConnected && _isAdmin;
            lblEnvInfo.Text = $"In use: {s?.EnvironmentName ?? "not set"}. "
                              + (otherReady ? $"{other} is also set up on this PC: switching restarts the app at the sign-in window."
                                            : $"{other} is not set up on this PC yet: switching asks for its database and web service settings first.");
        }

        private void btnSwitchEnv_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAdmin || _machineConnected) return;
            string other = DbSettingsStore.Load()?.EnvironmentName == DbSettings.Pro ? DbSettings.Dev : DbSettings.Pro;

            if (DbSettingsStore.LoadProfile(other) == null)
            {
                var dlg = new DbSettingsWindow(firstRun: false, environment: other) { Owner = this };
                if (dlg.ShowDialog() != true) { ShowConnection(); return; }   // saving it also makes it the one in use
            }
            else
            {
                if (MessageBox.Show(this,
                        $"Switch this PC to {other}?\n\n" + (other == DbSettings.Pro
                            ? "PRO = live orders and real cartons."
                            : "DEV = Logiwa sandbox and test data: real orders are NOT processed while DEV is in use.")
                        + "\n\nThe app restarts at the sign-in window; sign in with a " + other + " user.",
                        "Switch environment", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                DbSettingsStore.SetActive(other);
            }

            EnvironmentSwitchRequested = true;
            Close();
        }

        private void btnChangeDb_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAdmin) return;
            string envBefore = DbSettingsStore.Load()?.EnvironmentName;
            var dlg = new DbSettingsWindow(firstRun: false) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            if (DbSettingsStore.Load()?.EnvironmentName != envBefore)
            {
                // The other environment was saved in that window: it is in use now, with its own users. Restart at the sign-in window.
                EnvironmentSwitchRequested = true;
                Close();
                return;
            }

            ConnectionSettingsChanged = true;
            ShowConnection();
            ShowStatus("Database and web service settings saved.", error: false);
        }

        // ===== Common =====

        private void ShowStatus(string text, bool error)
        {
            lblStatus.Text = text;
            lblStatus.Foreground = (Brush)FindResource(error ? "DangerBrush" : "SuccessBrush");
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
