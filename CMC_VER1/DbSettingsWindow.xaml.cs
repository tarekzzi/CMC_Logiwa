using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMC_VER1.Services;

namespace CMC_VER1
{
    /// <summary>
    /// Asks where the local database is (environment, server, database, SQL login, password),
    /// tests the connection and saves the settings encrypted on this PC.
    /// </summary>
    public partial class DbSettingsWindow : Window
    {
        private readonly DbSettings _existing;
        private bool _loading = true;

        public DbSettingsWindow(bool firstRun)
        {
            InitializeComponent();

            _existing = DbSettingsStore.Load();

            string env = _existing?.EnvironmentName ?? DbSettings.Dev;
            cboEnvironment.SelectedIndex = env == DbSettings.Pro ? 1 : 0;
            txtServer.Text = _existing?.Server ?? "";
            txtDatabase.Text = _existing?.Database ?? DbSettings.DefaultDatabase(env);
            txtUser.Text = _existing?.User ?? DbSettings.DefaultUser(env);

            if (firstRun)
                lblIntro.Text = "This PC has no database settings yet. Choose the environment and enter the local database details. Saved encrypted on this PC only.";
            if (_existing != null)
                lblPasswordHint.Visibility = Visibility.Visible;

            _loading = false;
            Loaded += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtServer.Text)) txtServer.Focus(); else txtPassword.Focus();
            };
        }

        private string SelectedEnvironment =>
            (cboEnvironment.SelectedItem as ComboBoxItem)?.Content as string ?? DbSettings.Dev;

        private void cboEnvironment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            // Follow the environment only while the fields still hold the other environment's defaults.
            string other = SelectedEnvironment == DbSettings.Pro ? DbSettings.Dev : DbSettings.Pro;
            if (string.IsNullOrWhiteSpace(txtDatabase.Text) || txtDatabase.Text == DbSettings.DefaultDatabase(other))
                txtDatabase.Text = DbSettings.DefaultDatabase(SelectedEnvironment);
            if (string.IsNullOrWhiteSpace(txtUser.Text) || txtUser.Text == DbSettings.DefaultUser(other))
                txtUser.Text = DbSettings.DefaultUser(SelectedEnvironment);
        }

        private async void btnSave_Click(object sender, RoutedEventArgs e)
        {
            string password = txtPassword.Password;
            if (string.IsNullOrEmpty(password) && _existing != null)
                password = _existing.Password;   // blank = keep the saved password

            var settings = new DbSettings
            {
                EnvironmentName = SelectedEnvironment,
                Server = txtServer.Text.Trim(),
                Database = txtDatabase.Text.Trim(),
                User = txtUser.Text.Trim(),
                Password = password
            };

            if (settings.Server.Length == 0 || settings.Database.Length == 0 ||
                settings.User.Length == 0 || string.IsNullOrEmpty(settings.Password))
            {
                ShowStatus("Fill in the server, database, SQL login and password.", isError: true);
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

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void SetBusy(bool busy)
        {
            cboEnvironment.IsEnabled = !busy;
            txtServer.IsEnabled = !busy;
            txtDatabase.IsEnabled = !busy;
            txtUser.IsEnabled = !busy;
            txtPassword.IsEnabled = !busy;
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
