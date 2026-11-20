using System;
using System.Collections.Generic;
using System.Data;
using System.Security.Cryptography;

namespace CMC_Logiwa.Services
{
    /// <summary>One row of the Users list on the Settings screen.</summary>
    public sealed class UserInfo
    {
        public string Username { get; set; }
        public bool IsActive { get; set; }
        public bool IsAdmin { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }

        public string StatusText => IsActive ? "Active" : "Switched off";
        public string RoleText => IsAdmin ? "Administrator" : "Operator";
        public string CreatedText => CreatedAt.ToString("yyyy-MM-dd");
        public string LastLoginText => LastLoginAt.HasValue ? LastLoginAt.Value.ToString("yyyy-MM-dd HH:mm") : "never";
    }

    /// <summary>
    /// Local username/password login against the Users table of the local database.
    /// Passwords are never stored: PBKDF2 (SHA-256) hash + random salt, computed here.
    /// </summary>
    public class LocalAuthService
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int DefaultIterations = 100000;
        public const int MinPasswordLength = 4;

        private readonly SQLManager _sql;

        public LocalAuthService(SQLManager sql)
        {
            _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        }

        /// <summary>True when at least one user exists (otherwise the login window offers to create the first one).</summary>
        public bool HasUsers()
        {
            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_Count", null);
            return dt.Rows.Count > 0 && Convert.ToInt32(dt.Rows[0]["UserCount"]) > 0;
        }

        /// <summary>The very first user always becomes administrator (the procedure makes sure of it).</summary>
        public bool CreateUser(string username, string password, out string error) => CreateUser(username, password, false, out error);

        public bool CreateUser(string username, string password, bool isAdmin, out string error)
        {
            error = null;
            username = (username ?? "").Trim();

            if (username.Length == 0 || username.Length > 50)
            {
                error = "Enter a username (up to 50 characters).";
                return false;
            }
            if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            {
                error = $"The password must be at least {MinPasswordLength} characters.";
                return false;
            }

            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            byte[] hash = ComputeHash(password, salt, DefaultIterations);

            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_Create", new Dictionary<string, object>
            {
                { "@Username", username },
                { "@PasswordHash", hash },
                { "@Salt", salt },
                { "@Iterations", DefaultIterations },
                { "@IsAdmin", isAdmin }
            });

            string result = dt.Rows.Count > 0 ? dt.Rows[0]["Result"]?.ToString() : null;
            if (result == "OK")
                return true;

            error = result == "EXISTS" ? "That username already exists." : "The user could not be created.";
            return false;
        }

        /// <summary>All users, for the Settings screen. Hashes are not read.</summary>
        public List<UserInfo> ListUsers()
        {
            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_List", null);
            var list = new List<UserInfo>();
            foreach (DataRow row in dt.Rows)
            {
                list.Add(new UserInfo
                {
                    Username = row["Username"].ToString(),
                    IsActive = Convert.ToBoolean(row["IsActive"]),
                    IsAdmin = row.Table.Columns.Contains("IsAdmin") && Convert.ToBoolean(row["IsAdmin"]),
                    CreatedAt = Convert.ToDateTime(row["CreatedAt"]),
                    LastLoginAt = row["LastLoginAt"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(row["LastLoginAt"])
                });
            }
            return list;
        }

        /// <summary>Sets a new password for an existing user (a new random salt every time).</summary>
        public bool SetPassword(string username, string password, out string error)
        {
            error = null;
            username = (username ?? "").Trim();

            if (username.Length == 0)
            {
                error = "Select a user first.";
                return false;
            }
            if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            {
                error = $"The password must be at least {MinPasswordLength} characters.";
                return false;
            }

            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_SetPassword", new Dictionary<string, object>
            {
                { "@Username", username },
                { "@PasswordHash", ComputeHash(password, salt, DefaultIterations) },
                { "@Salt", salt },
                { "@Iterations", DefaultIterations }
            });

            if (dt.Rows.Count > 0 && Convert.ToInt32(dt.Rows[0]["Updated"]) > 0)
                return true;

            error = "That user was not found.";
            return false;
        }

        /// <summary>
        /// Switches a user on or off (a switched-off user cannot log in; nothing is deleted).
        /// The last active user cannot be switched off.
        /// </summary>
        public bool SetActive(string username, bool active, out string error)
        {
            error = null;
            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_SetActive", new Dictionary<string, object>
            {
                { "@Username", (username ?? "").Trim() },
                { "@IsActive", active }
            });

            string result = dt.Rows.Count > 0 ? dt.Rows[0]["Result"]?.ToString() : null;
            if (result == "OK")
                return true;

            error = result == "LAST_ACTIVE" ? "This is the only active user: switching it off would leave nobody able to log in."
                  : result == "LAST_ADMIN" ? "This is the only active administrator: give another user the administrator role first."
                  : result == "NOT_FOUND" ? "That user was not found."
                  : "The change could not be saved.";
            return false;
        }

        /// <summary>
        /// Gives or takes the administrator role. The last active administrator keeps it (the procedure refuses), so somebody can
        /// always manage the PC.
        /// </summary>
        public bool SetAdmin(string username, bool isAdmin, out string error)
        {
            error = null;
            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_SetAdmin", new Dictionary<string, object>
            {
                { "@Username", (username ?? "").Trim() },
                { "@IsAdmin", isAdmin }
            });

            string result = dt.Rows.Count > 0 ? dt.Rows[0]["Result"]?.ToString() : null;
            if (result == "OK")
                return true;

            error = result == "LAST_ADMIN" ? "This is the only active administrator: give another user the administrator role first."
                  : result == "NOT_FOUND" ? "That user was not found."
                  : "The change could not be saved.";
            return false;
        }

        /// <summary>Returns true when the username exists, is active and the password matches.</summary>
        public bool Authenticate(string username, string password) => Authenticate(username, password, out _);

        /// <param name="isAdmin">The user's role, read with the password check (false when the login fails).</param>
        public bool Authenticate(string username, string password, out bool isAdmin)
        {
            isAdmin = false;
            username = (username ?? "").Trim();
            if (username.Length == 0 || string.IsNullOrEmpty(password))
                return false;

            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_User_Get", new Dictionary<string, object>
            {
                { "@Username", username }
            });

            if (dt.Rows.Count == 0)
                return false;

            DataRow row = dt.Rows[0];
            if (!Convert.ToBoolean(row["IsActive"]))
                return false;

            byte[] salt = (byte[])row["Salt"];
            byte[] expected = (byte[])row["PasswordHash"];
            int iterations = Convert.ToInt32(row["Iterations"]);

            byte[] actual = ComputeHash(password, salt, iterations);
            if (!FixedTimeEquals(expected, actual))
                return false;

            isAdmin = row.Table.Columns.Contains("IsAdmin") && Convert.ToBoolean(row["IsAdmin"]);

            try
            {
                _sql.ExecuteStoredProcedure("dbo.sp_User_Touch", new Dictionary<string, object> { { "@Username", username } });
            }
            catch
            {
                // last-login stamp is not important enough to refuse a valid login
            }
            return true;
        }

        private static byte[] ComputeHash(string password, byte[] salt, int iterations)
        {
            // HashAlgorithmName overload exists from .NET Framework 4.7.2
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(HashSize);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
