namespace CMC_VER1
{
    partial class Main
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.btnStartConnection = new System.Windows.Forms.Button();
            this.btnEndConnection = new System.Windows.Forms.Button();
            this.listBoxLogs = new System.Windows.Forms.ListBox();
            this.pnlDataImport = new System.Windows.Forms.Panel();
            this.cmbSelectWave = new System.Windows.Forms.ComboBox();
            this.btnImportData = new System.Windows.Forms.Button();
            this.txtJobNumber = new System.Windows.Forms.TextBox();
            this.lblJobNumber = new System.Windows.Forms.Label();
            this.panel2 = new System.Windows.Forms.Panel();
            this.txtMachineId = new System.Windows.Forms.TextBox();
            this.lblNotConnected = new System.Windows.Forms.Label();
            this.txtPort = new System.Windows.Forms.TextBox();
            this.txtIPAddress = new System.Windows.Forms.TextBox();
            this.lblCMCControlSystem = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.txtResetOrderNumber = new System.Windows.Forms.TextBox();
            this.btnResetOrderNumber = new System.Windows.Forms.Button();
            this.pnlDataImport.SuspendLayout();
            this.panel2.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnStartConnection
            // 
            this.btnStartConnection.Location = new System.Drawing.Point(12, 44);
            this.btnStartConnection.Name = "btnStartConnection";
            this.btnStartConnection.Size = new System.Drawing.Size(214, 41);
            this.btnStartConnection.TabIndex = 0;
            this.btnStartConnection.Text = "Start Connection";
            this.btnStartConnection.UseVisualStyleBackColor = true;
            this.btnStartConnection.Click += new System.EventHandler(this.btnStartConnection_Click);
            // 
            // btnEndConnection
            // 
            this.btnEndConnection.Location = new System.Drawing.Point(12, 91);
            this.btnEndConnection.Name = "btnEndConnection";
            this.btnEndConnection.Size = new System.Drawing.Size(214, 37);
            this.btnEndConnection.TabIndex = 1;
            this.btnEndConnection.Text = "End Connection";
            this.btnEndConnection.UseVisualStyleBackColor = true;
            this.btnEndConnection.Click += new System.EventHandler(this.btnEndConnection_Click);
            // 
            // listBoxLogs
            // 
            this.listBoxLogs.FormattingEnabled = true;
            this.listBoxLogs.ItemHeight = 20;
            this.listBoxLogs.Location = new System.Drawing.Point(12, 158);
            this.listBoxLogs.Name = "listBoxLogs";
            this.listBoxLogs.SelectionMode = System.Windows.Forms.SelectionMode.MultiExtended;
            this.listBoxLogs.Size = new System.Drawing.Size(1735, 844);
            this.listBoxLogs.TabIndex = 2;
            // 
            // pnlDataImport
            // 
            this.pnlDataImport.Controls.Add(this.btnResetOrderNumber);
            this.pnlDataImport.Controls.Add(this.txtResetOrderNumber);
            this.pnlDataImport.Controls.Add(this.label1);
            this.pnlDataImport.Controls.Add(this.cmbSelectWave);
            this.pnlDataImport.Controls.Add(this.btnImportData);
            this.pnlDataImport.Controls.Add(this.txtJobNumber);
            this.pnlDataImport.Controls.Add(this.lblJobNumber);
            this.pnlDataImport.Location = new System.Drawing.Point(885, 25);
            this.pnlDataImport.Name = "pnlDataImport";
            this.pnlDataImport.Size = new System.Drawing.Size(1048, 498);
            this.pnlDataImport.TabIndex = 3;
            // 
            // cmbSelectWave
            // 
            this.cmbSelectWave.FormattingEnabled = true;
            this.cmbSelectWave.Location = new System.Drawing.Point(154, 29);
            this.cmbSelectWave.Name = "cmbSelectWave";
            this.cmbSelectWave.Size = new System.Drawing.Size(397, 28);
            this.cmbSelectWave.TabIndex = 3;
            this.cmbSelectWave.DropDown += new System.EventHandler(this.cmbSelectWave_DropDown);
            // 
            // btnImportData
            // 
            this.btnImportData.Location = new System.Drawing.Point(26, 130);
            this.btnImportData.Name = "btnImportData";
            this.btnImportData.Size = new System.Drawing.Size(273, 39);
            this.btnImportData.TabIndex = 2;
            this.btnImportData.Text = "Import Data";
            this.btnImportData.UseVisualStyleBackColor = true;
            this.btnImportData.Click += new System.EventHandler(this.btnImportData_Click);
            // 
            // txtJobNumber
            // 
            this.txtJobNumber.Location = new System.Drawing.Point(154, 86);
            this.txtJobNumber.Name = "txtJobNumber";
            this.txtJobNumber.Size = new System.Drawing.Size(267, 26);
            this.txtJobNumber.TabIndex = 1;
            // 
            // lblJobNumber
            // 
            this.lblJobNumber.AutoSize = true;
            this.lblJobNumber.Location = new System.Drawing.Point(22, 32);
            this.lblJobNumber.Name = "lblJobNumber";
            this.lblJobNumber.Size = new System.Drawing.Size(107, 20);
            this.lblJobNumber.TabIndex = 0;
            this.lblJobNumber.Text = "Job/Wave No.";
            // 
            // panel2
            // 
            this.panel2.Controls.Add(this.txtMachineId);
            this.panel2.Controls.Add(this.lblNotConnected);
            this.panel2.Controls.Add(this.txtPort);
            this.panel2.Controls.Add(this.txtIPAddress);
            this.panel2.Controls.Add(this.listBoxLogs);
            this.panel2.Controls.Add(this.btnStartConnection);
            this.panel2.Controls.Add(this.btnEndConnection);
            this.panel2.Location = new System.Drawing.Point(12, 111);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(1850, 1043);
            this.panel2.TabIndex = 4;
            // 
            // txtMachineId
            // 
            this.txtMachineId.Location = new System.Drawing.Point(264, 123);
            this.txtMachineId.Name = "txtMachineId";
            this.txtMachineId.Size = new System.Drawing.Size(364, 26);
            this.txtMachineId.TabIndex = 6;
            this.txtMachineId.Text = "5069";
            // 
            // lblNotConnected
            // 
            this.lblNotConnected.AutoSize = true;
            this.lblNotConnected.Font = new System.Drawing.Font("Arial Black", 22F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblNotConnected.ForeColor = System.Drawing.Color.Red;
            this.lblNotConnected.Location = new System.Drawing.Point(660, 52);
            this.lblNotConnected.Name = "lblNotConnected";
            this.lblNotConnected.Size = new System.Drawing.Size(385, 62);
            this.lblNotConnected.TabIndex = 5;
            this.lblNotConnected.Text = "Not Connected";
            // 
            // txtPort
            // 
            this.txtPort.Location = new System.Drawing.Point(264, 91);
            this.txtPort.Name = "txtPort";
            this.txtPort.Size = new System.Drawing.Size(365, 26);
            this.txtPort.TabIndex = 4;
            this.txtPort.Text = "9200";
            // 
            // txtIPAddress
            // 
            this.txtIPAddress.Location = new System.Drawing.Point(266, 58);
            this.txtIPAddress.Name = "txtIPAddress";
            this.txtIPAddress.Size = new System.Drawing.Size(364, 26);
            this.txtIPAddress.TabIndex = 3;
            this.txtIPAddress.Text = "127.0.0.1";
            // 
            // lblCMCControlSystem
            // 
            this.lblCMCControlSystem.AutoSize = true;
            this.lblCMCControlSystem.Font = new System.Drawing.Font("Arial Black", 22F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblCMCControlSystem.Location = new System.Drawing.Point(20, 40);
            this.lblCMCControlSystem.Name = "lblCMCControlSystem";
            this.lblCMCControlSystem.Size = new System.Drawing.Size(523, 62);
            this.lblCMCControlSystem.TabIndex = 5;
            this.lblCMCControlSystem.Text = "CMC Control System";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(26, 218);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(156, 20);
            this.label1.TabIndex = 4;
            this.label1.Text = "Reset Order Number";
            // 
            // txtResetOrderNumber
            // 
            this.txtResetOrderNumber.Location = new System.Drawing.Point(198, 218);
            this.txtResetOrderNumber.Name = "txtResetOrderNumber";
            this.txtResetOrderNumber.Size = new System.Drawing.Size(274, 26);
            this.txtResetOrderNumber.TabIndex = 5;
            // 
            // btnResetOrderNumber
            // 
            this.btnResetOrderNumber.Location = new System.Drawing.Point(198, 262);
            this.btnResetOrderNumber.Name = "btnResetOrderNumber";
            this.btnResetOrderNumber.Size = new System.Drawing.Size(219, 33);
            this.btnResetOrderNumber.TabIndex = 6;
            this.btnResetOrderNumber.Text = "Reset Order Number";
            this.btnResetOrderNumber.UseVisualStyleBackColor = true;
            this.btnResetOrderNumber.Click += new System.EventHandler(this.btnResetOrderNumber_Click);
            // 
            // Main
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(2366, 1228);
            this.Controls.Add(this.pnlDataImport);
            this.Controls.Add(this.lblCMCControlSystem);
            this.Controls.Add(this.panel2);
            this.Name = "Main";
            this.Text = "CMC Control System";
            this.pnlDataImport.ResumeLayout(false);
            this.pnlDataImport.PerformLayout();
            this.panel2.ResumeLayout(false);
            this.panel2.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnStartConnection;
        private System.Windows.Forms.Button btnEndConnection;
        private System.Windows.Forms.ListBox listBoxLogs;
        private System.Windows.Forms.Panel pnlDataImport;
        private System.Windows.Forms.Label lblJobNumber;
        private System.Windows.Forms.TextBox txtJobNumber;
        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.Label lblCMCControlSystem;
        private System.Windows.Forms.Button btnImportData;
        private System.Windows.Forms.TextBox txtIPAddress;
        private System.Windows.Forms.TextBox txtPort;
        private System.Windows.Forms.Label lblNotConnected;
        private System.Windows.Forms.TextBox txtMachineId;
        private System.Windows.Forms.ComboBox cmbSelectWave;
        private System.Windows.Forms.TextBox txtResetOrderNumber;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button btnResetOrderNumber;
    }
}

