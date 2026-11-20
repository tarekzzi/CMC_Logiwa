using System;
using System.Collections.Generic;
using System.Linq;
using System.Data.SqlClient;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CMC_Logiwa.Services
{
    /// <summary>
    /// Where this PC's local database is and how to log in. One build serves DEV and PRO:
    /// the environment is chosen on the Database settings screen, not compiled in.
    /// </summary>
    public class DbSettings
    {
        public const string Dev = "DEV";
        public const string Pro = "PRO";

        public string EnvironmentName { get; set; } = Dev;
        public string Server { get; set; }
        public string Database { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        /// <summary>Web VM address (https) and this PC's API key for it. Empty = web sync is off. Files saved before this existed load with both empty.</summary>
        public string WebServiceUrl { get; set; }
        public string ApiKey { get; set; }

        public bool HasWebService => !string.IsNullOrWhiteSpace(WebServiceUrl) && !string.IsNullOrWhiteSpace(ApiKey);

        public static string DefaultWebServiceUrl(string env) => env == Pro ? "https://logiwacmc.fulfilmentaustralia.com.au" : "https://logiwacmcdev.fulfilmentaustralia.com.au";

        public static string DefaultDatabase(string env) => env == Pro ? "Local_CMC_Logiwa_PRO" : "Local_CMC_Logiwa_DEV";
        public static string DefaultUser(string env) => env == Pro ? "CMC_Logiwa_app_pro" : "CMC_Logiwa_app_dev";

        public string BuildConnectionString()
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = Database,
                UserID = User,
                Password = Password,
                ConnectTimeout = 15,
                ApplicationName = "CMC_Logiwa"
            }.ConnectionString;
        }
    }

    /// <summary>
    /// Saves the settings under C:\ProgramData\CMC_Logiwa\db.settings, encrypted with Windows DPAPI
    /// (machine scope): the file only decrypts on this PC, and the password never sits in the project,
    /// in App.config or in the published package.
    /// One file holds a DEV and a PRO set side by side and which one is in use, so one installed app serves both:
    /// switching the environment does not mean typing everything again. A file saved before this existed (one set) still loads.
    /// </summary>
    public static class DbSettingsStore
    {
        private sealed class SettingsFile
        {
            public string Active { get; set; }
            public Dictionary<string, DbSettings> Profiles { get; set; } = new Dictionary<string, DbSettings>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CMC_Logiwa.DbSettings.v1");
        private static readonly object Gate = new object();
        private static SettingsFile _cached;

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CMC_Logiwa", "db.settings");

        public static bool Exists => File.Exists(FilePath);

        /// <summary>The settings of the environment in use. Null when nothing is saved yet or the file cannot be decrypted on this PC.</summary>
        public static DbSettings Load()
        {
            lock (Gate)
            {
                var file = ReadFile();
                if (file == null || file.Active == null) return null;
                return file.Profiles.TryGetValue(file.Active, out var s) ? s : null;
            }
        }

        /// <summary>The saved settings of one environment (DEV or PRO), or null when that environment was never set up on this PC.</summary>
        public static DbSettings LoadProfile(string environmentName)
        {
            lock (Gate)
            {
                var file = ReadFile();
                return file != null && environmentName != null && file.Profiles.TryGetValue(environmentName, out var s) ? s : null;
            }
        }

        /// <summary>Saves one environment's settings and makes it the one in use.</summary>
        public static void Save(DbSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.EnvironmentName)) throw new ArgumentException("The environment is missing.", nameof(settings));

            lock (Gate)
            {
                var file = ReadFile() ?? new SettingsFile();
                file.Profiles[settings.EnvironmentName] = settings;
                file.Active = settings.EnvironmentName;
                WriteFile(file);
            }
        }

        /// <summary>Uses the other environment's saved settings from now on. False when that environment was never set up.</summary>
        public static bool SetActive(string environmentName)
        {
            lock (Gate)
            {
                var file = ReadFile();
                if (file == null || environmentName == null || !file.Profiles.ContainsKey(environmentName)) return false;
                file.Active = file.Profiles.Keys.First(k => string.Equals(k, environmentName, StringComparison.OrdinalIgnoreCase));
                WriteFile(file);
                return true;
            }
        }

        public static void Forget()
        {
            lock (Gate) { _cached = null; }
        }

        private static SettingsFile ReadFile()
        {
            if (_cached != null) return _cached;
            try
            {
                if (!File.Exists(FilePath)) return null;

                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine);
                JObject json = JObject.Parse(Encoding.UTF8.GetString(plain));

                SettingsFile file;
                if (json["Profiles"] != null)
                    file = json.ToObject<SettingsFile>();
                else
                {
                    // Saved before DEV and PRO were kept side by side: one set, which is the one in use.
                    var single = json.ToObject<DbSettings>();
                    string env = string.IsNullOrWhiteSpace(single.EnvironmentName) ? DbSettings.Dev : single.EnvironmentName;
                    single.EnvironmentName = env;
                    file = new SettingsFile { Active = env };
                    file.Profiles[env] = single;
                }

                file.Profiles = new Dictionary<string, DbSettings>(file.Profiles ?? new Dictionary<string, DbSettings>(), StringComparer.OrdinalIgnoreCase);
                _cached = file;
                return file;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteFile(SettingsFile file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(file));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine));
            _cached = file;
        }
    }
}