using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace CMC_Logiwa.Services
{
    /// <summary>A Logiwa warehouse, as offered by the web VM for the drop-down in Settings > Machine.</summary>
    public sealed class WarehouseInfo
    {
        public string Identifier { get; set; }
        public string Code { get; set; }
        public string Description { get; set; }

        /// <summary>What the drop-down shows: "B - Main warehouse".</summary>
        public string Display => string.IsNullOrWhiteSpace(Description) ? Code : Code + " - " + Description;

        /// <summary>The app's ComboBox style shows the selected item through ToString, not DisplayMemberPath.</summary>
        public override string ToString() => Display;
    }

    /// <summary>The CMC machine this PC talks to. Stored in the local database (db\006 and db\008 scripts).</summary>
    public sealed class MachineSettings
    {
        /// <summary>Used when the table is missing or empty, so the app still starts on a PC where 006 was not run yet. No warehouse: it must be chosen.</summary>
        public static MachineSettings Defaults => new MachineSettings { MachineId = "002", IpAddress = "10.2.1.201", Port = 9200, AutoReconnect = true };

        public string MachineId { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public bool AutoReconnect { get; set; }

        /// <summary>Logiwa identifier (GUID) of the warehouse this machine serves. Required before the app downloads orders or connects.</summary>
        public string WarehouseId { get; set; }
        public string WarehouseCode { get; set; }

        public DateTime? UpdatedAt { get; set; }
        public string UpdatedBy { get; set; }

        public bool HasWarehouse => !string.IsNullOrWhiteSpace(WarehouseId) && Guid.TryParse(WarehouseId, out _);

        /// <summary>Null when the values are fine, otherwise what is wrong (in words for the user).</summary>
        public string Validate()
        {
            string id = (MachineId ?? "").Trim();
            if (id.Length == 0) return "The machine ID is required.";
            if (id.Length > 20) return "The machine ID can be at most 20 characters.";
            if (id.Any(c => c == '|' || char.IsControl(c))) return "The machine ID cannot contain the | character or control characters.";
            if (string.IsNullOrWhiteSpace(IpAddress)) return "The IP address is required.";
            if (IpAddress.Trim().Length > 100) return "The IP address is too long.";
            if (Port < 1 || Port > 65535) return "The port must be a number from 1 to 65535.";
            if (!HasWarehouse) return "Choose the warehouse this machine serves.";
            return null;
        }
    }

    public static class MachineSettingsStore
    {
        /// <summary>Reads the saved settings; null when the database has none or cannot be read (the caller then uses the defaults).</summary>
        public static MachineSettings TryLoad(out string error)
        {
            error = null;
            try
            {
                DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_MachineSettings_Get", null);
                if (dt.Rows.Count == 0) return null;
                DataRow r = dt.Rows[0];
                return new MachineSettings
                {
                    MachineId = r["MachineId"].ToString(),
                    IpAddress = r["IpAddress"].ToString(),
                    Port = Convert.ToInt32(r["Port"]),
                    AutoReconnect = Convert.ToBoolean(r["AutoReconnect"]),
                    // the warehouse columns exist after db\008; before that the warehouse simply counts as not chosen
                    WarehouseId = Text(r, "WarehouseId"),
                    WarehouseCode = Text(r, "WarehouseCode"),
                    UpdatedAt = r["UpdatedAt"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["UpdatedAt"]),
                    UpdatedBy = Text(r, "UpdatedBy")
                };
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static string Text(DataRow r, string column)
        {
            if (!r.Table.Columns.Contains(column) || r[column] == DBNull.Value) return null;
            return r[column].ToString();
        }

        /// <summary>Saves the settings. Returns null when saved, otherwise the reason it was not.</summary>
        public static string Save(MachineSettings s, string userName)
        {
            string problem = s.Validate();
            if (problem != null) return problem;

            try
            {
                DataTable dt = new SQLManager().ExecuteStoredProcedure("dbo.sp_MachineSettings_Save", new Dictionary<string, object>
                {
                    { "@MachineId", s.MachineId.Trim() },
                    { "@IpAddress", s.IpAddress.Trim() },
                    { "@Port", s.Port },
                    { "@AutoReconnect", s.AutoReconnect },
                    { "@User", userName },
                    { "@WarehouseId", s.WarehouseId.Trim() },
                    { "@WarehouseCode", s.WarehouseCode }
                });

                string result = dt.Rows.Count == 0 ? "" : dt.Rows[0]["Result"].ToString();
                switch (result)
                {
                    case "OK": return null;
                    case "BAD_MACHINE_ID": return "The machine ID is not valid.";
                    case "BAD_IP": return "The IP address is not valid.";
                    case "BAD_PORT": return "The port is not valid.";
                    case "BAD_WAREHOUSE": return "The warehouse is not valid.";
                    default: return "The database did not confirm the change.";
                }
            }
            catch (Exception ex)
            {
                string hint = ex.Message.IndexOf("too many arguments", StringComparison.OrdinalIgnoreCase) >= 0
                    ? " (has db\\008_machine_warehouse_dev.sql been run?)" : "";
                return "The change was not saved: " + ex.Message + hint;
            }
        }
    }
}
