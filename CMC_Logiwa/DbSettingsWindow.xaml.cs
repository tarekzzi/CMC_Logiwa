using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMC_Logiwa.Services;

namespace CMC_Logiwa
{
    /// <summary>
    /// Asks where the local database is (environment, server, database, SQL login, password),
    /// tests the connection and saves the settings encrypted on this PC.
    /// </summary>
    public partial class DbSettingsWindow : Window
    {
        // The saved settings of the environment shown (DEV and PRO are kept side by side): blank password / key = keep these.
        private DbSettings _existing;
        private bool _loading = true;

        /// <param name="environment">DEV or PRO to show first (null = the one in use).</param>
        public DbSettingsWindow(bool firstRun, string environment = null)
        {
            InitializeComponent();

            string env = environment ?? DbSettingsStore.Load()?.EnvironmentName ?? DbSettings.Dev;
            cboEnvironment.SelectedIndex = env == DbSettings.Pro ? 1 : 0;
            ShowProfile(env);

            if (firstRun)
                lblIntro.Text = "This PC has no database settings yet. Choose the environment and enter the local database details. Saved encrypted on this PC only.";

            _loading = false;
            Loaded += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtServer.Text)) txtServer.Focus(); else txtPassword.Focus();
            };
        }

        /// <summary>Fills the form with the saved settings of this environment, or its defaults when it was never set up here.</summary>
        private void ShowProfile(string env)
        {
            _existing = DbSettingsStore.LoadProfile(env);
            var current = DbSettingsStore.Load();

            txtServer.Text = _existing?.Server ?? current?.Server ?? "";   // usually the same SQL Server for DEV and PRO on one PC
            txtDatabase.Text = _existing?.Database ?? DbSettings.DefaultDatabase(env);
            txtUser.Text = _existing?.User ?? DbSettings.DefaultUser(env);
            txtWebUrl.Text = _existing != null ? _existing.WebServiceUrl ?? "" : DbSettings.DefaultWebServiceUrl(env);
            txtPassword.Clear();
            txtApiKey.Clear();
            lblPasswordHint.Visibility = _existing != null ? Visibility.Visible : Visibility.Collapsed;
            lblApiKeyHint.Visibility = !string.IsNullOrEmpty(_existing?.ApiKey) ? Visibility.Visible : Visibility.Collapsed;
            _saveAnywayOffered = false;
        }

        private string SelectedEnvironment =>
            (cboEnvironment.SelectedItem as ComboBoxItem)?.Content as string ?? DbSettings.Dev;

        private void cboEnvironment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // Each environment has its own saved settings (password and API key included): show the chosen one's.
            ShowProfile(SelectedEnvironment);
            ShowStatus("", isError: false);
        }
        // Set after a failed web service test: pressing Save again keeps the settings anyway (the web VM may just be down).
        private bool _saveAnywayOffered;

        private async void btnSave_Click(object sender, RoutedEventArgs e)
        {
            string password = txtPassword.Password;
            if (string.IsNullOrEmpty(password) && _existing != null)
                password = _existing.Password;   // blank = keep this environment's saved password

            var settings = new DbSettings
            {
                EnvironmentName = SelectedEnvironment,
                Server = txtServer.Text.Trim(),
                Database = txtDatabase.Text.Trim(),
                User = txtUser.Text.Trim(),
                Password = password,
                WebServiceUrl = txtWebUrl.Text.Trim()
            };

            // Blank key = keep the saved one, but only for the same environment.
            string apiKey = txtApiKey.Password.Trim();
            if (apiKey.Length == 0 && _existing != null)
                apiKey = _existing.ApiKey ?? "";
            settings.ApiKey = apiKey;

            if (settings.Server.Length == 0 || settings.Database.Length == 0 ||
                settings.User.Length == 0 || string.IsNullOrEmpty(settings.Password))
            {
                ShowStatus("Fill in the server, database, SQL login and password.", isError: true);
                return;
            }

            bool anyWeb = settings.WebServiceUrl.Length > 0 || settings.ApiKey.Length > 0;
            if (anyWeb && !settings.HasWebService)
            {
                ShowStatus("Fill in both the web service address and the API key, or leave both empty.", isError: true);
                return;
            }
            if (settings.HasWebService && !settings.WebServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus("The web service address must start with https://", isError: true);
                return;
            }

            SetBusy(true);
            ShowStatus("Testing the connection…", isError: false);

            string error = await Task.Run(() => TestConnection(settings));
            if (error != null)
            {
                SetBusy(false);
                ShowStatus(error, isError: true);
                return;
            }

            if (settings.HasWebService && !_saveAnywayOffered)
            {
                ShowStatus("Testing the web service…", isError: false);
                string webError = await TestWebServiceAsync(settings);
                if (webError != null)
                {
                    _saveAnywayOffered = true;
                    SetBusy(false);
                    ShowStatus(webError + "\nPress “Test and save” again to save anyway.", isError: true);
                    return;
                }
            }

            try
            {
                DbSettingsStore.Save(settings);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowStatus("Could not save the settings on this PC.\n" + ex.Message, isError: true);
                return;
            }

            DialogResult = true;
        }

        /// <summary>Returns null when the login works and the CMC_Logiwa tables are there, otherwise a readable reason.</summary>
        private static string TestConnection(DbSettings settings)
        {
            try
            {
                using (var conn = new SqlConnection(settings.BuildConnectionString()))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(
                        "SELECT CASE WHEN OBJECT_ID(N'dbo.PackJob', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL THEN 1 ELSE 0 END", conn))
                    {
                        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1)
                            return "Connected, but the CMC_Logiwa tables were not found in '" + settings.Database + "'. Run the schema script first.";
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return "Could not connect.\n" + ex.Message;
            }
        }

        /// <summary>Returns null when the address and key are accepted, otherwise a readable reason.</summary>
        private static async Task<string> TestWebServiceAsync(DbSettings settings)
        {
            try
            {
                using (var web = new CmcWebClient(settings))
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await web.PingAsync(cts.Token);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void SetBusy(bool busy)
        {
            cboEnvironment.IsEnabled = !busy;
            txtServer.IsEnabled = !busy;
            txtDatabase.IsEnabled = !busy;
            txtUser.IsEnabled = !busy;
            txtPassword.IsEnabled = !busy;
            txtWebUrl.IsEnabled = !busy;
            txtApiKey.IsEnabled = !busy;
            btnSave.IsEnabled = !busy;
            btnCancel.IsEnabled = !busy;
        }

        private void ShowStatus(string text, bool isError)
        {
            lblStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "MutedBrush");
            lblStatus.Text = text;
            lblStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
