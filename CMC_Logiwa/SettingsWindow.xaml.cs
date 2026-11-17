using System;
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

        public SettingsWindow(bool machineConnected)
        {
            InitializeComponent();
            _machineConnected = machineConnected;

            ShowConnection();
            Loaded += async (s, e) => await LoadUsersAsync();
        }

        // ===== Users =====

        private async Task LoadUsersAsync(string select = null)
        {
            try
            {
                List<UserInfo> users = await Task.Run(() => _auth.ListUsers());
                gridUsers.ItemsSource = users;

                int active = users.FindAll(u => u.IsActive).Count;
                lblUsersInfo.Text = $"{users.Count} user(s), {active} active. A switched-off user cannot log in; nothing is deleted.";

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
            btnSetPassword.IsEnabled = u != null;
            btnToggleActive.IsEnabled = u != null;
            btnToggleActive.Content = u != null && !u.IsActive ? "Switch on" : "Switch off";
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

            if (!Run(() => _auth.CreateUser(name, txtNewPassword.Password, out string err) ? null : err))
                return;

            txtNewUser.Clear();
            txtNewPassword.Clear();
            txtNewPassword2.Clear();
            ShowStatus($"User {name} added.", error: false);
            await LoadUsersAsync(select: name);
        }

        private void btnSetPassword_Click(object sender, RoutedEventArgs e)
        {
            UserInfo u = Selected;
            if (u == null) return;

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
            if (u == null) return;

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

            btnChangeDb.IsEnabled = !_machineConnected;
            lblChangeHint.Text = _machineConnected
                ? "Disconnect from the machine first: the open connection uses the current database."
                : "";
        }

        private void btnChangeDb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DbSettingsWindow(firstRun: false) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

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
