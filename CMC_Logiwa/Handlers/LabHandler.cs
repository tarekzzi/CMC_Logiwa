using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
    /// <summary>
    /// LAB (label check and measurement). sp_Update_LAB stores the measurements and decides the result
    /// (1 = ok); the reply carries the result and MatchLab (the tracking number the printed label must match).
    /// A failed check is shown on the front panel log with the machine's event text.
    /// </summary>
    public class LabHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;

        public LabHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "LAB")
                return;

            string packJobId = result.Reference ?? "";

            int labResults = 0;
            string matchLab = "";
            string orderNumber = null;
            string eventText = null;
            string eventCode = null;
            string dbError = null;

            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId },
                    { "@LabStatus", result.LabStatus },
                    { "@LabEvent", result.LabEvent ?? "" },
                    { "@LabSizeH", result.LabSizeH },
                    { "@LabSizeL", result.LabSizeL },
                    { "@LabSizeW", result.LabSizeW },
                    { "@LabArea",  result.LabArea },
                    { "@LabWeightCarton", result.LabWeightCarton },
                    { "@LabWeightScale", result.LabWeightScale }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Update_LAB", parameters);

                if (dt != null && dt.Rows.Count > 0)
                {
                    DataRow row = dt.Rows[0];

                    if (int.TryParse(row["LABResults"]?.ToString(), out int parsed))
                        labResults = parsed;

                    matchLab = row["MatchLab"]?.ToString() ?? "";
                    orderNumber = Read(row, "OrderNumber");
                    eventCode = Read(row, "EventCode");
                    eventText = Read(row, "EventText");
                }
            }
            catch (Exception ex)
            {
                // The machine must always get its reply: report "not ok" and show why on the front panel.
                labResults = 0;
                matchLab = "";
                dbError = ex.Message;
            }

            string responsePayload = _formatter.FormatLabResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference,
                result: labResults,
                matchLab: matchLab,
                flagToReject: "",
                sorter: ""
            );

            await _networkManager.SendAsync(responsePayload);

            string who = string.IsNullOrEmpty(orderNumber) ? $"job {packJobId}" : orderNumber;
            if (dbError != null)
                _networkManager.LogError($"{who}: label check could not be saved ({dbError})");
            else if (labResults != 1)
                _networkManager.LogError($"{who}: label check failed - {Describe(eventText, eventCode)}");
        }

        private static string Read(DataRow row, string column) =>
            row.Table.Columns.Contains(column) && row[column] != DBNull.Value ? row[column].ToString() : null;

        private static string Describe(string eventText, string eventCode) =>
            string.IsNullOrEmpty(eventText) ? "no reason given" : (string.IsNullOrEmpty(eventCode) ? eventText : $"{eventText} ({eventCode})");
    }
}
