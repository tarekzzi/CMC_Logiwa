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

        private bool ShowDbSettings(bool firstRun)
        {
            var dlg = new DbSettingsWindow(firstRun) { Owner = this };
            return dlg.ShowDialog() == true;
        }

        private async void btnDbSettings_Click(object sender, RoutedEventArgs e)
        {
            if (ShowDbSettings(firstRun: false))
                await ConnectAsync();
        }

        private async Task ConnectAsync()
        {
            ToggleUi(enabled: false, "Connecting to the database…", isError: false);
            try
            {
                var settings = DbSettingsStore.Load();
                lblEnvironment.Text = settings == null ? "" : $"{settings.EnvironmentName}  ·  {settings.Server}  ·  {settings.Database}";
                _auth = new LocalAuthService(new SQLManager());
                bool hasUsers = await Task.Run(() => _auth.HasUsers());
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

                bool ok = await Task.Run(() => _auth.Authenticate(username, password));
                if (!ok)
                {
                    ToggleUi(enabled: true, "", isError: false);
                    ShowStatus("Sign-in failed. Check your username and password.", isError: true);
                    txtPassword.Clear();
                    txtPassword.Focus();
                    return;
                }

                // Temporary: MainWindow still carries the V1 wave-import parameters (removed in a later step).
                SessionVariables.UserEmail = username;
                var next = new MainWindow("", "Local", "", username, 0);

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
