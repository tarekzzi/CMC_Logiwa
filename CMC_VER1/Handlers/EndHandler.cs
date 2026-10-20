//using CMC_VER1.Models;
//using CMC_VER1.Services;
//using System;
//using System.Collections.Generic;
//using System.Threading.Tasks;

//namespace CMC_VER1.Handlers
//{
//    public class EndHandler
//    {
//        private readonly NetworkManager _networkManager;
//        private readonly FormatProcessMessages _formatter;
//        private readonly SQLManager _sql;

//        public EndHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
//        {
//            _networkManager = networkManager;
//            _formatter = formatter;
//            _sql = sql;
//        }

//        public async Task Handle(MessageProcessingResult result)
//        {
//            if (result == null || !result.IsRequest || result.MessageType != "END")
//                return;

//            if (!int.TryParse(result.Reference, out int packJobId))
//                return;

//            try
//            {
//                var parameters = new Dictionary<string, object>
//                {
//                    { "@PackJobID", packJobId },
//                    { "@EndStatus", result.EndStatus },
//                    { "@EndEvent", result.EndEvent ?? "" },
//                    { "@EndSizeH", result.EndSizeH },
//                    { "@EndSizeL", result.EndSizeL },
//                    { "@EndSizeW", result.EndSizeW },
//                    { "@EndArea", result.EndArea },
//                    {"@EndWeightCarton", result.EndWeightCarton },
//                    {"@EndWeightScale", result.EndWeightScale }
//                };

//                _sql.ExecuteStoredProcedure("dbo.sp_Update_END", parameters);
//            }
//            catch
//            {
//            }

//            string responsePayload = _formatter.FormatEndResponse(
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
    public class EndHandler
    {
        private readonly NetworkManager _networkManager;
        private readonly FormatProcessMessages _formatter;
        private readonly SQLManager _sql;
        private readonly ShipmentApiService _shipmentApiService;

        public EndHandler(NetworkManager networkManager, FormatProcessMessages formatter, SQLManager sql)
        {
            _networkManager = networkManager;
            _formatter = formatter;
            _sql = sql;
            _shipmentApiService = new ShipmentApiService(new HttpClient());
        }

        public async Task Handle(MessageProcessingResult result)
        {
            if (result == null || !result.IsRequest || result.MessageType != "END")
                return;

            if (!int.TryParse(result.Reference, out int packJobId))
                return;

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
                //MessageBox.Show("Stored procedure executed successfully.", "Database Update");
                if (dt != null && dt.Rows.Count > 0)
                {
                    DataRow row = dt.Rows[0];

                    string fulfilmentNumber = row["FulfilmentNumber"]?.ToString();
                    string waveNumber = row["WaveNumber"]?.ToString();
                    //
                    // MessageBox.Show($"FulfilmentNumber: {fulfilmentNumber}, WaveNumber: {waveNumber}", "Database Values");

                    int finalResult = 0;
                    int.TryParse(row["FinalResult"]?.ToString(), out finalResult);

                    if (!string.IsNullOrWhiteSpace(fulfilmentNumber) &&
                        !string.IsNullOrWhiteSpace(waveNumber))
                    {
                        bool isSuccess = finalResult == 1;

                        var shipmentRequest = new UpdateShipmentInfoRequest
                        {
                            FulfilmentNumber = fulfilmentNumber,
                            WaveNumber = waveNumber,
                            OrderStatus = isSuccess ? "Completed" : "Failed",

                            BoxSizeH = isSuccess ? ConvertToApiString(result.EndSizeH) : "",
                            BoxSizeL = isSuccess ? ConvertToApiString(result.EndSizeL) : "",
                            BoxSizeW = isSuccess ? ConvertToApiString(result.EndSizeW) : "",
                            BoxArea = isSuccess ? ConvertToApiString(result.EndArea) : "",
                            Weight_Carton = isSuccess ? ConvertToApiString(result.EndWeightCarton) : "",
                            Box_Weight = isSuccess ? ConvertToApiString(result.EndWeightScale) : ""
                        };

                        string apiResponse = await _shipmentApiService.UpdateShipmentInfoAsync(shipmentRequest);
                        //MessageBox.Show(apiResponse, "API Response");
                    }
                }
            }
            catch (Exception ex)
            {
                // Add logging here if needed
            }

            string responsePayload = _formatter.FormatEndResponse(
                machineId: result.MachineId,
                counter: result.Counter,
                reference: result.Reference
            );

            await _networkManager.SendAsync(responsePayload);
        }

        private string ConvertToApiString(object value)
        {
            if (value == null)
                return "";

            return value.ToString();
        }
    }
}