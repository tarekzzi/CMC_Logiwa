using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace CMC_Logiwa.Services
{
    /// <summary>The CMC machine this PC talks to. Stored in the local database (db\006_machine_settings_dev.sql).</summary>
    public sealed class MachineSettings
    {
        /// <summary>Used when the table is missing or empty, so the app still starts on a PC where 006 was not run yet.</summary>
        public static MachineSettings Defaults => new MachineSettings { MachineId = "002", IpAddress = "10.2.1.201", Port = 9200, AutoReconnect = true };

        public string MachineId { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public bool AutoReconnect { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string UpdatedBy { get; set; }

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
                    UpdatedAt = r["UpdatedAt"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["UpdatedAt"]),
                    UpdatedBy = r["UpdatedBy"] == DBNull.Value ? null : r["UpdatedBy"].ToString()
                };
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
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
                    { "@User", userName }
                });

                string result = dt.Rows.Count == 0 ? "" : dt.Rows[0]["Result"].ToString();
                switch (result)
                {
                    case "OK": return null;
                    case "BAD_MACHINE_ID": return "The machine ID is not valid.";
                    case "BAD_IP": return "The IP address is not valid.";
                    case "BAD_PORT": return "The port is not valid.";
                    default: return "The database did not confirm the change.";
                }
            }
            catch (Exception ex)
            {
                return "The change was not saved: " + ex.Message;
            }
        }
    }
}
