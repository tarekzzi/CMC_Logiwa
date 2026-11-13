
using CMC_Logiwa.Models;
using CMC_Logiwa.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_Logiwa.Handlers
{
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
                    {"@LabWeightCarton", result.LabWeightCarton },
                    {"@LabWeightScale", result.LabWeightScale }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Update_LAB", parameters);

                //await SendPRNAsync(result.MachineId, result.Reference, packJobId);

                


                if (dt != null && dt.Rows.Count > 0)
                {
                    
                    object oRes = dt.Rows[0]["LABResults"];
                    if (oRes != null && int.TryParse(oRes.ToString(), out int parsed))
                        labResults = parsed;

                    matchLab = dt.Rows[0]["MatchLab"]?.ToString() ?? "";
                }
                else
                {
                    
                    labResults = 0;
                    matchLab = "";
                }
            }
            catch
            {
                
                labResults = 0;
                matchLab = "";
            }

            
            //int flagToReject = (labResults == 1) ? 0 : 1;
            //string flagToReject = "";

            string responsePayload = _formatter.FormatLabResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference,
                result: labResults,
                matchLab: matchLab,
                //flagToReject: flagToReject,
                flagToReject: "",
                sorter: ""                  
            );

            await _networkManager.SendAsync(responsePayload);
        }
        //private async Task SendPRNAsync(string machineId, string reference, string packJobId)
        //{
        //    try
        //    {
        //        if (!int.TryParse(packJobId, out int packJobIdInt))
        //            return;

        //        var prnParams = new Dictionary<string, object>
        //        {
        //            { "@PackJobID", packJobIdInt }
        //        };

        //        DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Get_PRN_Data", prnParams);

        //        if (dt.Rows.Count == 0)
        //            return;

        //        string labelData = dt.Rows[0]["LabelData"]?.ToString() ?? "";

        //        if (string.IsNullOrWhiteSpace(labelData))
        //            return;

        //        string cisCounter = _networkManager.GetNextCisCounter();

        //        string prnPayload = _formatter.FormatPrnRequest(
        //            machineId,
        //            cisCounter,
        //            reference,
        //            labelData
        //        );

        //        await _networkManager.SendAsync(prnPayload);
        //    }
        //    catch
        //    {
        //    }
        //}
    }
}