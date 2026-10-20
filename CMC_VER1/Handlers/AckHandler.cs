//using CMC_VER1.Models;
//using CMC_VER1.Services;
//using System;
//using System.Collections.Generic;
//using System.Data;
//using System.Threading.Tasks;
//using System.Windows.Forms;

//namespace CMC_VER1.Handlers
//{
//    public class AckHandler
//    {
//        private readonly NetworkManager _networkManager;
//        private readonly FormatProcessMessages _formatter;
//        private readonly SQLManager _sql;

//        public AckHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
//        {
//            _networkManager = networkManager;
//            _formatter = formatter;
//            _sql = sql;
//        }

//        public async Task Handle(MessageProcessingResult result)
//        {
//            if (result == null || !result.IsRequest || result.MessageType != "ACK")
//                return;


//            string responsePayload = _formatter.FormatAckResponse(
//                machineId: result.MachineId,
//                counter: result.Counter,
//                reference: result.Reference
//            );

//            await _networkManager.SendAsync(responsePayload);


//            _ = Task.Run(async () =>
//            {
//                try
//                {

//                    string packId = result.Counter ?? "";


//                    if (!int.TryParse(result.Reference, out int packJobId))
//                        return;

//                    int ackStatus = 0;
//                    ackStatus = result.AckResult;

//                    var parameters = new Dictionary<string, object>
//                    {
//                        { "@PackID", packId },
//                        { "@PackJobID", packJobId },
//                        { "@ACK_Status", ackStatus }
//                    };

//                    _sql.ExecuteStoredProcedure("dbo.sp_Update_ACK_Status", parameters);



//                    if (ackStatus != 1)
//                        return;


//                    var prnParams = new Dictionary<string, object>
//                    {
//                        { "@PackJobID", packJobId }
//                    };
//                    DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Get_PRN_Data", prnParams);
//                    if (dt == null || dt.Rows.Count == 0)
//                        return;
//                    string labelData = dt.Rows[0]["LabelData"]?.ToString() ?? "";
//                    if (string.IsNullOrWhiteSpace(labelData))
//                        return;

//                    string cisCounter = _networkManager.GetNextCisCounter();

//                    string prnPayload = _formatter.FormatPrnRequest(
//                        machineId: result.MachineId,
//                        cisCounter: cisCounter,
//                        reference: result.Reference,
//                        labelData: labelData
//                    );
//                    await _networkManager.SendAsync(prnPayload);
//                }
//                catch
//                {

//                }
//            });
//        }
//    }
//}
using CMC_VER1.Models;
using CMC_VER1.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace CMC_VER1.Handlers
{
    public class AckHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;

        public AckHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "ACK")
                return;

            // 1) Send ACK response immediately
            string responsePayload = _formatter.FormatAckResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(responsePayload);

            string packId = result.Counter ?? "";
            int ackStatus = result.AckResult;

            if (!int.TryParse(result.Reference, out int packJobId))
                return;

            // 2) Update SQL asynchronously
            _ = Task.Run(() =>
            {
                try
                {
                    var parameters = new Dictionary<string, object>
                    {
                        { "@PackID", packId },
                        { "@PackJobID", packJobId },
                        { "@ACK_Status", ackStatus }
                    };

                    _sql.ExecuteStoredProcedure("dbo.sp_Update_ACK_Status", parameters);
                }
                catch
                {
                }
            });

            // 3) If ACK failed, stop here
            if (ackStatus != 1)
                return;

            // 4) Send PRN
            await SendPRNAsync(result.MachineId, result.Reference, packJobId);
        }

        private async Task SendPRNAsync(string machineId, string reference, int packJobId)
        {
            try
            {
                var prnParams = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Get_PRN_Data", prnParams);

                if (dt.Rows.Count == 0)
                    return;

                string labelData = dt.Rows[0]["LabelData"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(labelData))
                    return;

                string cisCounter = _networkManager.GetNextCisCounter();

                string prnPayload = _formatter.FormatPrnRequest(
                    machineId,
                    cisCounter,
                    reference,
                    labelData
                );

                await _networkManager.SendAsync(prnPayload);
            }
            catch
            {
            }
        }
    }
}