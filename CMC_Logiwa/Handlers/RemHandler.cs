using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    /// <summary>
    /// REM (carton removed). Recorded locally by sp_Update_REM only; nothing is sent to Logiwa.
    /// The machine always gets its reply.
    /// </summary>
    public class RemHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;

        public RemHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "REM")
                return;

            if (!int.TryParse(result.Reference, out int packJobId))
                return;

            string dbError = null;
            string orderNumber = null;
            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Update_REM", parameters);
                if (dt != null && dt.Rows.Count > 0 && dt.Columns.Contains("OrderNumber"))
                    orderNumber = dt.Rows[0]["OrderNumber"]?.ToString();
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: the machine must still get its reply. Shown on the front panel below.
                dbError = ex.Message;
            }

            string responsePayload = _formatter.FormatRemResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(responsePayload);

            string who = string.IsNullOrEmpty(orderNumber) ? $"job {packJobId}" : orderNumber;
            if (dbError != null)
                _networkManager.LogError($"{who}: removal could not be saved ({dbError})");
            else
                _networkManager.LogError($"{who}: carton removed from the machine (Box Removed, 010)");
        }
    }
}
