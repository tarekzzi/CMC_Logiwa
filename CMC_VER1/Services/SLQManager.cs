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
            _connectionString =
                ConfigurationManager.ConnectionStrings["CMCConnection"]?.ConnectionString
                ?? throw new Exception("Connection string 'CMCConnection' not found.");
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