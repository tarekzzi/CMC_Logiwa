using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    /// <summary>
    /// ENQ (barcode scan). The scanned value is the carrier tracking number. The local database decides
    /// (sp_ENQ_Get_Data): OK, NOT_FOUND, DUPLICATE or NO_LABEL, and the machine panel shows the description.
    /// </summary>
    public class EnquiryHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;

        public EnquiryHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "ENQ")
                return;

            string matchLab = "";
            string packJobId = "";
            int result01 = 0;
            string description;

            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@Barcode", result.Barcode },
                    { "@Counter", result.Counter },
                    { "@MachineId", result.MachineId },
                    { "@User", SessionVariables.UserName }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_ENQ_Get_Data", parameters);

                if (dt != null && dt.Rows.Count > 0)
                {
                    DataRow row = dt.Rows[0];
                    string outcome = row["EnqResult"]?.ToString();
                    description = row["Description"]?.ToString() ?? "";

                    if (outcome == "OK")
                    {
                        packJobId = row["PackJobID"]?.ToString() ?? "";
                        matchLab = row["MatchLab"]?.ToString() ?? "";
                        result01 = 1;
                    }
                    else if (outcome == "NOT_FOUND" || outcome == "NO_LABEL")
                    {
                        // The reply does not wait for this: a rescan a few seconds later finds the order if it has arrived.
                        OrderSyncService.RequestPoll();
                    }
                }
                else
                {
                    description = "Not found, try again later";
                }
            }
            catch
            {
                // The machine must always get its reply, even if the database is unreachable.
                description = "System error, try again";
            }

            string payload = _formatter.FormatEnqResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                barcode: result.Barcode,
                result01: result01,
                matchLab: matchLab,
                packJobId: packJobId,
                description: description
            );

            await _networkManager.SendAsync(payload);

            // Front panel: what the operator needs to see without clicking anything.
            if (result01 == 1)
                _networkManager.LogInfo($"{result.Barcode}: accepted (job {packJobId})");
            else
                _networkManager.LogError($"{result.Barcode}: {description}");
        }
    }
}
