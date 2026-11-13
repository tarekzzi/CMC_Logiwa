using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace CMC_VER1
{
    public class SQLManager
    {
        private readonly string _connectionString;

        public SQLManager()
        {
            // Saved per PC by the Database settings screen (encrypted, outside the project and App.config).
            var settings = CMC_VER1.Services.DbSettingsStore.Load()
                ?? throw new InvalidOperationException("Database settings are not configured on this PC.");
            _connectionString = settings.BuildConnectionString();
        }

        public DataTable ExecuteStoredProcedure(string storedProcedureName, Dictionary<string, object> parameters)
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            using (SqlCommand cmd = new SqlCommand(storedProcedureName, conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;

                if (parameters != null)
                {
                    foreach (var p in parameters)
                    {
                        cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
                    }
                }

                DataTable dt = new DataTable();
                using (SqlDataAdapter ad = new SqlDataAdapter(cmd))
                {
                    ad.Fill(dt);
                }

                return dt;
            }
        }
    }
}