//using CMC_VER1.Models;
//using CMC_VER1.Services;
//using System;
//using System.Collections.Generic;
//using System.Threading.Tasks;

//namespace CMC_VER1.Handlers
//{
//    public class RemHandler
//    {
//        private readonly NetworkManager _networkManager;
//        private readonly FormatProcessMessages _formatter;
//        private readonly SQLManager _sql;

//        public RemHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
//        {
//            _networkManager = networkManager;
//            _formatter = formatter;
//            _sql = sql;
//        }

//        public async Task Handle(MessageProcessingResult result)
//        {
//            if (result == null || !result.IsRequest || result.MessageType != "REM")
//                return;

//            if (!int.TryParse(result.Reference, out int packJobId))
//                return;

//            try
//            {
//                var parameters = new Dictionary<string, object>
//                {
//                    { "@PackJobID", packJobId }
//                };

//                _sql.ExecuteStoredProcedure("dbo.sp_Update_REM", parameters);
//            }
//            catch
//            {
//            }

//            string responsePayload = _formatter.FormatRemResponse(
//                machineId: result.MachineId,
//                counter: result.Counter,
//                reference: result.Reference
//            );

//            await _networkManager.SendAsync(responsePayload);
//        }
//    }
//}
using CMC_VER1.Models;
using CMC_VER1.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Threading.Tasks;

namespace CMC_VER1.Handlers
{
    public class RemHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;
        private readonly ShipmentApiService _shipmentApiService;

        public RemHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
            _shipmentApiService = new ShipmentApiService(new HttpClient());
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "REM")
                return;

            if (!int.TryParse(result.Reference, out int packJobId))
                return;

            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@PackJobID", packJobId }
                };

                DataTable dt = _sql.ExecuteStoredProcedure("dbo.sp_Update_REM", parameters);

                if (dt != null && dt.Rows.Count > 0)
                {
                    string orderNumber = dt.Rows[0]["OrderNumber"]?.ToString();
                    string waveNumber = dt.Rows[0]["WaveNumber"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(orderNumber) && !string.IsNullOrWhiteSpace(waveNumber))
                    {
                        var shipmentRequest = new UpdateShipmentInfoRequest
                        {
                            FulfilmentNumber = orderNumber,
                            WaveNumber = waveNumber,
                            OrderStatus = "Failed",
                            BoxSizeH = "",
                            BoxSizeL = "",
                            BoxSizeW = "",
                            BoxArea = "",
                            Weight_Carton = "",
                            Box_Weight = ""
                        };

                        await _shipmentApiService.UpdateShipmentInfoAsync(shipmentRequest);
                    }
                }
            }
            catch
            {
            }

            string responsePayload = _formatter.FormatRemResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(responsePayload);
        }
    }
}