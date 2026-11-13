using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    /// <summary>
    /// prn (the machine's print result, a lowercase response to the PRN the app sent).
    /// V1 required an uppercase request here, so this never ran there; it now records the result
    /// and shows a failed print on the front panel log.
    /// </summary>
    public class PrnResultHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly SQLManager _sql;

        public PrnResultHandler(NetworkManager networkManager, SQLManager sql)
        {
            _networkManager = networkManager;
            _sql = sql;
        }

        public Task Handle(MessageProcessingResult result)
        {
            if (result == null || result.MessageType != "prn")
                return Task.CompletedTask;

            if (!int.TryParse(result.Reference, out int packJobId))
                return Task.CompletedTask;

            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId },
                    { "@PRNResult", result.PrnResult }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Update_PRN", parameters);

                string order = dt != null && dt.Rows.Count > 0 ? dt.Rows[0]["OrderNumber"]?.ToString() : null;
                string who = string.IsNullOrEmpty(order) ? $"job {packJobId}" : order;

                if (result.PrnResult == 0)
                    _networkManager.LogError($"{who}: label print failed");
            }
            catch (Exception ex)
            {
                _networkManager.LogError($"job {packJobId}: print result could not be saved ({ex.Message})");
            }

            return Task.CompletedTask;
        }
    }
}
