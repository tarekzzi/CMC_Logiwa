using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CMC_VER1.Services;
using CMC_VER1.Models;
using CMC_VER1.Helpers;

namespace CMC_VER1
{
    public partial class LoginForm : Form
    {
        private readonly TextBox txtEmail = new TextBox();
        private readonly TextBox txtPassword = new TextBox();
        private readonly Button btnLogin = new Button();
        private readonly Button btnCancel = new Button();
        private readonly Label lblStatus = new Label();

        // Hidden / app-provided value
        // Replace this with how you generate/persist your device ID.
        private const string MobileDeviceIdentifier = "j322123Da232fa24Sw";

        private readonly AuthenticationService _auth = new AuthenticationService();

        public LoginForm()
        {
            Text = "3PLWMS – Sign in";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 220);

            var lblTitle = new Label
            {
                Text = "Please sign in",
                Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, 15)
            };

            var lblEmail = new Label { Text = "Email:", AutoSize = true, Location = new Point(20, 55) };
            txtEmail.Location = new Point(120, 50);
            txtEmail.Width = 260;

            var lblPassword = new Label { Text = "Password:", AutoSize = true, Location = new Point(20, 90) };
            txtPassword.Location = new Point(120, 85);
            txtPassword.Width = 260;
            txtPassword.UseSystemPasswordChar = true;

            btnLogin.Text = "Sign In";
            btnLogin.Location = new Point(120, 135);
            btnLogin.Width = 120;
            btnLogin.Click += async (s, e) => await LoginAsync();

            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(260, 135);
            btnCancel.Width = 120;
            btnCancel.Click += (s, e) => Close();

            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Location = new Point(20, 175);
            lblStatus.Text = "";

            Controls.Add(lblTitle);
            Controls.Add(lblEmail);
            Controls.Add(txtEmail);
            Controls.Add(lblPassword);
            Controls.Add(txtPassword);
            Controls.Add(btnLogin);
            Controls.Add(btnCancel);
            Controls.Add(lblStatus);

            // Pressing Enter submits
            AcceptButton = btnLogin;
            CancelButton = btnCancel;
        }

        private async Task LoginAsync()
        {
            var email = txtEmail.Text?.Trim();
            var password = txtPassword.Text;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show(this, "Please enter both Email and Password.", "Missing details",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ToggleUi(enabled: false, "Signing in…");

            try
            {
                var result = await _auth.AuthenticateAsync(email, password, MobileDeviceIdentifier);

                if (!result.IsAuthenticated)
                {
                    // Failure: show the message and let the user try again.
                    MessageBox.Show(this,
                        string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "Authentication failed. Please check your credentials."
                            : result.ErrorMessage,
                        "Sign-in failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    txtPassword.Clear();
                    txtPassword.Focus();
                    ToggleUi(enabled: true, "");
                    return;
                }

                // Success: pass values to the destination form and proceed.
                var next = new Main(
                    SessionVariables.ApiKey= result.ApiKey,
                    SessionVariables.Warehouse= result.Warehouse,
                    SessionVariables.WarehouseCode= result.WarehouseCode,
                    SessionVariables.UserEmail = email,
                    SessionVariables.WarehouseID= result.WarehouseID);


                // Option A: Hide login and show next form
                Hide();
                next.FormClosed += (s, e) => this.Close(); // Close app when next form closes
                next.Show();

                // Option B (Dialog): 
                // if (next.ShowDialog(this) == DialogResult.OK) { /* … */ }

            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Unexpected error.\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ToggleUi(enabled: true, "");
            }
        }

        private void ToggleUi(bool enabled, string status)
        {
            txtEmail.Enabled = enabled;
            txtPassword.Enabled = enabled;
            btnLogin.Enabled = enabled;
            btnCancel.Enabled = enabled;
            lblStatus.Text = status;
        }
    }
}
