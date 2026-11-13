using System;
using System.Data.SqlClient;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CMC_VER1.Services
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
    /// </summary>
    public static class DbSettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CMC_Logiwa.DbSettings.v1");
        private static readonly object Gate = new object();
        private static DbSettings _cached;

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CMC_Logiwa", "db.settings");

        public static bool Exists => File.Exists(FilePath);

        /// <summary>Returns null when nothing is saved yet or the file cannot be decrypted on this PC.</summary>
        public static DbSettings Load()
        {
            lock (Gate)
            {
                if (_cached != null)
                    return _cached;

                try
                {
                    if (!File.Exists(FilePath))
                        return null;

                    byte[] protectedBytes = File.ReadAllBytes(FilePath);
                    byte[] plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
                    _cached = JsonConvert.DeserializeObject<DbSettings>(Encoding.UTF8.GetString(plain));
                    return _cached;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void Save(DbSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            lock (Gate)
            {
                string dir = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(dir);

                byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings));
                byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
                File.WriteAllBytes(FilePath, protectedBytes);
                _cached = settings;
            }
        }

        public static void Forget()
        {
            lock (Gate) { _cached = null; }
        }
    }
}
