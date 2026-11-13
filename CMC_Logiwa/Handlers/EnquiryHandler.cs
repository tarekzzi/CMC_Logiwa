using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    public class EnquiryHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;
        private readonly Func<string> _getJobNumber;

        public EnquiryHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql, Func<string> getJobNumber)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
            _getJobNumber = getJobNumber;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "ENQ")
                return;

            string jobNumber = _getJobNumber?.Invoke() ?? "";

            var parameters = new Dictionary<string, object>
            {
                { "@Barcode", result.Barcode },
                { "@JobNumber", jobNumber },
                { "@Counter", result.Counter }
            };

            DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_ENQ_Get_Data", parameters);

            string matchLab = "";
            string packJobId = "";
            int result01 = 0;
            string description = null;

            if (dt != null && dt.Rows.Count > 0 && dt.Columns.Contains("IsDuplicate")
                && Convert.ToInt32(dt.Rows[0]["IsDuplicate"] == DBNull.Value ? 0 : dt.Rows[0]["IsDuplicate"]) == 1)
            {
                // More than one open record matches this barcode: reject like "not found",
                // and let the machine pop-up say why (MSG_DESCRIPTION is shown on the CMC panel).
                packJobId = "";
                matchLab = "";
                result01 = 0;
                description = "Duplicate Record";
            }
            else if (dt != null && dt.Rows.Count > 0)
            {
                packJobId = dt.Rows[0]["PackJobID"]?.ToString() ?? "";
                matchLab = dt.Rows[0]["MatchLab"]?.ToString() ?? "";
                result01 = 1;
            }
            else
            {
                // No SQL match found
                packJobId = "";
                matchLab = "";
                result01 = 0;
                description = "Not Found";
            }

            string payload = _formatter.FormatEnqResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                barcode: result.Barcode,
                jobNumber: jobNumber,
                result01: result01,
                matchLab: matchLab,
                packJobId: packJobId,
                description: description
            );

            await _networkManager.SendAsync(payload);
        }
    }
}