using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CMC_VER1.Helpers;
using CMC_VER1.Models;
using CMC_VER1.Services;

namespace CMC_VER1
{
    public partial class LoginWindow : Window
    {
        // Hidden / app-provided value
        // Replace this with how you generate/persist your device ID.
        private const string MobileDeviceIdentifier = "j322123Da232fa24Sw";

        private readonly AuthenticationService _auth = new AuthenticationService();

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => txtEmail.Focus();
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e) => await LoginAsync();

        private void btnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private async Task LoginAsync()
        {
            var email = txtEmail.Text?.Trim();
            var password = txtPassword.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            {
                ShowStatus("Enter your email and password.", isError: true);
                if (string.IsNullOrWhiteSpace(email)) txtEmail.Focus(); else txtPassword.Focus();
                return;
            }

            ToggleUi(enabled: false, "Signing in…");

            try
            {
                var result = await _auth.AuthenticateAsync(email, password, MobileDeviceIdentifier);

                if (!result.IsAuthenticated)
                {
                    // Failure: show the message inline and let the user try again.
                    ToggleUi(enabled: true, "");
                    ShowStatus(string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "Sign-in failed. Check your email and password."
                            : result.ErrorMessage,
                        isError: true);

                    txtPassword.Clear();
                    txtPassword.Focus();
                    return;
                }

                // Success: pass values to the destination window and proceed.
                var next = new MainWindow(
                    SessionVariables.ApiKey = result.ApiKey,
                    SessionVariables.Warehouse = result.Warehouse,
                    SessionVariables.WarehouseCode = result.WarehouseCode,
                    SessionVariables.UserEmail = email,
                    SessionVariables.WarehouseID = result.WarehouseID);

                // Hide login and show the main window; closing the main window closes the app.
                Hide();
                next.Closed += (s, e) => Close();
                next.Show();
            }
            catch (Exception ex)
            {
                ToggleUi(enabled: true, "");
                MessageBox.Show(this, $"Unexpected error.\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleUi(bool enabled, string status)
        {
            txtEmail.IsEnabled = enabled;
            txtPassword.IsEnabled = enabled;
            btnLogin.IsEnabled = enabled;
            btnCancel.IsEnabled = enabled;
            btnLogin.Content = enabled ? "Sign in" : "Signing in…";
            ShowStatus(status, isError: false);
        }

        private void ShowStatus(string text, bool isError)
        {
            lblStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "MutedBrush");
            lblStatus.Text = text;
            lblStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
