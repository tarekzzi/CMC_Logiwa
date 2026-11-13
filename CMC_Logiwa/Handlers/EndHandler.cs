using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    /// <summary>
    /// END (carton finished). The result, size and weight are stored locally by sp_Update_END.
    /// Nothing is sent to Logiwa from here: a successful END is queued in the local outbox and the
    /// background service pushes it to the web VM. The machine always gets its reply, and the outcome
    /// is shown on the front panel log.
    /// </summary>
    public class EndHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;

        public EndHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "END")
                return;

            if (!int.TryParse(result.Reference, out int packJobId))
                return;

            int finalResult = 0;
            string orderNumber = null;
            string eventText = null;
            string eventCode = null;
            string dbError = null;

            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId },
                    { "@EndStatus", result.EndStatus },
                    { "@EndEvent", result.EndEvent ?? "" },
                    { "@EndSizeH", result.EndSizeH },
                    { "@EndSizeL", result.EndSizeL },
                    { "@EndSizeW", result.EndSizeW },
                    { "@EndArea", result.EndArea },
                    { "@EndWeightCarton", result.EndWeightCarton },
                    { "@EndWeightScale", result.EndWeightScale }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Update_END", parameters);

                if (dt != null && dt.Rows.Count > 0)
                {
                    DataRow row = dt.Rows[0];
                    int.TryParse(row["FinalResult"]?.ToString(), out finalResult);
                    orderNumber = Read(row, "OrderNumber");
                    eventCode = Read(row, "EventCode");
                    eventText = Read(row, "EventText");
                }
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: the machine must still get its reply. Shown on the front panel below.
                dbError = ex.Message;
            }

            string responsePayload = _formatter.FormatEndResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(responsePayload);

            string who = string.IsNullOrEmpty(orderNumber) ? $"job {packJobId}" : orderNumber;
            if (dbError != null)
                _networkManager.LogError($"{who}: end of carton could not be saved ({dbError})");
            else if (finalResult == 1)
                _networkManager.LogInfo($"{who}: completed, weight and size queued for sending");
            else
                _networkManager.LogError($"{who}: failed at end - " +
                    (string.IsNullOrEmpty(eventText) ? "no reason given" : (string.IsNullOrEmpty(eventCode) ? eventText : $"{eventText} ({eventCode})")));
        }

        private static string Read(DataRow row, string column) =>
            row.Table.Columns.Contains(column) && row[column] != DBNull.Value ? row[column].ToString() : null;
    }
}
