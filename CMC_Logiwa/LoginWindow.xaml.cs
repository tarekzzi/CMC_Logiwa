using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CMC_Logiwa.Models;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// Local login: username + password checked against the Users table of the local database.
    /// When no user exists yet, the same window creates the first one.
    /// </summary>
    public partial class LoginWindow : Window
    {
        private LocalAuthService _auth;
        private bool _setupMode;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await InitAsync();
        }

        private async Task InitAsync()
        {
            // First run on this PC: ask where the local database is before anything else.
            if (DbSettingsStore.Load() == null && !ShowDbSettings(firstRun: true))
            {
                Close();
                return;
            }
            await ConnectAsync();
        }

        private bool ShowDbSettings(bool firstRun, string environment = null)
        {
            var dlg = new DbSettingsWindow(firstRun, environment) { Owner = this };
            return dlg.ShowDialog() == true;
        }

        /// <summary>
        /// Once the database works and has users, its settings are an administrator's job: the administrator types their user name and
        /// password in the sign-in fields first. Without a working database anybody may fix the settings (otherwise nobody could).
        /// </summary>
        private async void btnDbSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_auth != null && !_setupMode && _dbReachable)
            {
                string user = txtUsername.Text?.Trim();
                string pw = txtPassword.Password;
                bool isAdmin = false;
                bool ok = !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pw)
                          && await Task.Run(() => _auth.Authenticate(user, pw, out isAdmin));
                if (!ok || !isAdmin)
                {
                    ShowStatus("Database settings are for administrators: enter an administrator's user name and password above, then press \"Database settings…\" again.", isError: true);
                    return;
                }
            }

            if (ShowDbSettings(firstRun: false, environment: SelectedEnv))
                await ConnectAsync();
            else
                ShowEnvironmentChoice();
        }

        private bool _dbReachable;
        private bool _envLoading;

        private string SelectedEnv => (cboEnv.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? DbSettings.Dev;

        /// <summary>Shows the environment in use in the drop-down (without reacting to it).</summary>
        private void ShowEnvironmentChoice()
        {
            _envLoading = true;
            cboEnv.SelectedIndex = DbSettingsStore.Load()?.EnvironmentName == DbSettings.Pro ? 1 : 0;
            _envLoading = false;
        }

        /// <summary>
        /// DEV or PRO: each has its own saved database / web service settings on this PC and its own users. An environment that was
        /// never set up here opens the Database settings for it.
        /// </summary>
        private async void cboEnv_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_envLoading || !IsLoaded) return;
            string env = SelectedEnv;
            if (DbSettingsStore.Load()?.EnvironmentName == env) return;

            if (DbSettingsStore.LoadProfile(env) != null)
            {
                DbSettingsStore.SetActive(env);
                txtPassword.Clear();
                await ConnectAsync();
                return;
            }

            if (MessageBox.Show(this, $"{env} is not set up on this PC yet. Enter its database and web service settings now?",
                    "Environment", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
                && ShowDbSettings(firstRun: false, environment: env))
            {
                await ConnectAsync();
                return;
            }
            ShowEnvironmentChoice();   // stay where we were
        }

        private async Task ConnectAsync()
        {
            ToggleUi(enabled: false, "Connecting to the database…", isError: false);
            _dbReachable = false;
            ShowEnvironmentChoice();
            try
            {
                var settings = DbSettingsStore.Load();
                lblEnvironment.Text = settings == null ? "" : $"{settings.EnvironmentName}  ·  {settings.Server}  ·  {settings.Database}";
                _auth = new LocalAuthService(new SQLManager());
                bool hasUsers = await Task.Run(() => _auth.HasUsers());
                _dbReachable = true;
                SetSetupMode(!hasUsers);
                ToggleUi(enabled: true, "", isError: false);
                txtUsername.Focus();
            }
            catch (Exception ex)
            {
                // Cannot reach the database: show why, keep the form usable so the user can retry.
                ToggleUi(enabled: true, "", isError: false);
                ShowStatus("Cannot reach the local database.\n" + ex.Message, isError: true);
            }
        }

        private void SetSetupMode(bool setup)
        {
            _setupMode = setup;
            lblSubtitle.Text = setup
                ? "No users exist yet. Create the first user."
                : "Sign in with your username and password.";
            panelConfirm.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            btnLogin.Content = setup ? "Create user" : "Sign in";
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

        private void btnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private async Task SubmitAsync()
        {
            var username = txtUsername.Text?.Trim();
            var password = txtPassword.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                ShowStatus("Enter your username and password.", isError: true);
                if (string.IsNullOrWhiteSpace(username)) txtUsername.Focus(); else txtPassword.Focus();
                return;
            }

            if (_setupMode && password != txtConfirm.Password)
            {
                ShowStatus("The passwords do not match.", isError: true);
                txtConfirm.Clear();
                txtConfirm.Focus();
                return;
            }

            ToggleUi(enabled: false, _setupMode ? "Creating user…" : "Signing in…", isError: false);

            try
            {
                if (_setupMode)
                {
                    string error = null;
                    bool created = await Task.Run(() => _auth.CreateUser(username, password, out error));
                    if (!created)
                    {
                        ToggleUi(enabled: true, "", isError: false);
                        ShowStatus(error, isError: true);
                        return;
                    }
                }

                bool isAdmin = false;
                bool ok = await Task.Run(() => _auth.Authenticate(username, password, out isAdmin));
                if (!ok)
                {
                    ToggleUi(enabled: true, "", isError: false);
                    ShowStatus("Sign-in failed. Check your username and password.", isError: true);
                    txtPassword.Clear();
                    txtPassword.Focus();
                    return;
                }

                SessionVariables.UserName = username;
                SessionVariables.IsAdmin = isAdmin;
                var next = new MainWindow(username);

                Hide();
                next.Closed += (s, args) => Close();
                next.Show();
            }
            catch (Exception ex)
            {
                ToggleUi(enabled: true, "", isError: false);
                MessageBox.Show(this, $"Unexpected error.\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleUi(bool enabled, string status, bool isError)
        {
            txtUsername.IsEnabled = enabled;
            txtPassword.IsEnabled = enabled;
            txtConfirm.IsEnabled = enabled;
            btnLogin.IsEnabled = enabled;
            btnCancel.IsEnabled = enabled;
            btnDbSettings.IsEnabled = enabled;
            cboEnv.IsEnabled = enabled;
            ShowStatus(status, isError);
        }

        private void ShowStatus(string text, bool isError)
        {
            lblStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "MutedBrush");
            lblStatus.Text = text;
            lblStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
