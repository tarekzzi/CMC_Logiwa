using System;
using System.Collections.Generic;
using System.Data;
using System.Security.Cryptography;

namespace CMC_Logiwa.Services
{
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

        public bool CreateUser(string username, string password, out string error)
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
                { "@Iterations", DefaultIterations }
            });

            string result = dt.Rows.Count > 0 ? dt.Rows[0]["Result"]?.ToString() : null;
            if (result == "OK")
                return true;

            error = result == "EXISTS" ? "That username already exists." : "The user could not be created.";
            return false;
        }

        /// <summary>Returns true when the username exists, is active and the password matches.</summary>
        public bool Authenticate(string username, string password)
        {
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
