//using CMC_VER1.Helpers;
//using CMC_VER1.Models;
//using System;
//using System.Collections.Generic;
//using System.Net.Http;
//using System.Text;
//using System.Text.Json;
//using System.Threading.Tasks;

//namespace CMC_VER1.Services
//{
//    internal class WaveApiService
//    {
//        private readonly HttpClient Http;

//        public WaveApiService(HttpClient httpClient)
//        {
//            Http = httpClient;
//        }

//        public async Task<List<string>> RequestCMCPackingTaskAsync(string warehouseCode, string selectedWaveText, bool firstCall)
//        {
//            if (string.IsNullOrWhiteSpace(warehouseCode))
//                throw new ArgumentException("warehouseCode cannot be empty.");

//            if (string.IsNullOrWhiteSpace(selectedWaveText))
//                throw new ArgumentException("selectedWaveText cannot be empty.");

//            var payload = new
//            {
//                warehouseCode = warehouseCode,
//                waveNumber = selectedWaveText,
//                firstCall = firstCall
//            };

//            using (var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.RequestCMCPackingTask))
//            {
//                req.Headers.Add("API-Key", SessionVariables.ApiKey);
//                req.Headers.Add("User-Email", SessionVariables.UserEmail);

//                req.Content = new StringContent(
//                    JsonSerializer.Serialize(payload),
//                    Encoding.UTF8,
//                    "application/json");

//                using (var resp = await Http.SendAsync(req))
//                {
//                    string responseJson = await resp.Content.ReadAsStringAsync();

//                    if (!resp.IsSuccessStatusCode)
//                    {
//                        throw new Exception($"API call failed. Status: {(int)resp.StatusCode}. Response: {responseJson}");
//                    }

//                    var orders = JsonSerializer.Deserialize<List<string>>(responseJson, new JsonSerializerOptions
//                    {
//                        PropertyNameCaseInsensitive = true
//                    });

//                    return orders ?? new List<string>();
//                }
//            }
//        }
//    }
//}
using CMC_VER1.Helpers;
using CMC_VER1.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CMC_VER1.Services
{
    internal class WaveApiService
    {
        private readonly HttpClient Http;
        private readonly SQLManager _sqlManager;

        public WaveApiService(HttpClient httpClient)
        {
            Http = httpClient;
            _sqlManager = new SQLManager();
        }

        public async Task<List<string>> RequestCMCPackingTaskAsync(string warehouseCode, string selectedWaveText, bool firstCall)
        {
            if (string.IsNullOrWhiteSpace(warehouseCode))
                throw new ArgumentException("warehouseCode cannot be empty.");

            if (string.IsNullOrWhiteSpace(selectedWaveText))
                throw new ArgumentException("selectedWaveText cannot be empty.");

            var payload = new
            {
                warehouseCode = warehouseCode,
                waveNumber = selectedWaveText,
                firstCall = firstCall
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.RequestCMCPackingTask))
            {
                req.Headers.Add("API-Key", SessionVariables.ApiKey);
                req.Headers.Add("User-Email", SessionVariables.UserEmail);

                req.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var resp = await Http.SendAsync(req))
                {
                    string responseJson = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception($"RequestCMCPackingTask failed. Status: {(int)resp.StatusCode}. Response: {responseJson}");
                    }

                    var orders = JsonSerializer.Deserialize<List<string>>(responseJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return orders ?? new List<string>();
                }
            }
        }

        public async Task<ProcessCMCPackingTaskResponse> ProcessCMCPackingTaskAsync(string fulfilmentNumber, string warehouseCode, string waveNumber)
        {
            if (string.IsNullOrWhiteSpace(fulfilmentNumber))
                throw new ArgumentException("fulfilmentNumber cannot be empty.");

            if (string.IsNullOrWhiteSpace(warehouseCode))
                throw new ArgumentException("warehouseCode cannot be empty.");

            if (string.IsNullOrWhiteSpace(waveNumber))
                throw new ArgumentException("waveNumber cannot be empty.");

            var payload = new
            {
                fulfilmentNumbers = fulfilmentNumber,
                warehouseCode = warehouseCode,
                waveNumber = waveNumber
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.ProcessCMCPackingTask))
            {
                req.Headers.Add("API-Key", SessionVariables.ApiKey);
                req.Headers.Add("User-Email", SessionVariables.UserEmail);

                req.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var resp = await Http.SendAsync(req))
                {
                    string responseJson = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception($"ProcessCMCPackingTask failed for fulfilmentNumber [{fulfilmentNumber}]. Status: {(int)resp.StatusCode}. Response: {responseJson}");
                    }

                    var result = JsonSerializer.Deserialize<ProcessCMCPackingTaskResponse>(responseJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return result ?? new ProcessCMCPackingTaskResponse
                    {
                        message = "",
                        data = new List<ProcessCMCPackingTaskItem>()
                    };
                }
            }
        }

        public int SaveProcessResultToSql(string fulfilmentNumber, List<ProcessCMCPackingTaskItem> items)
        {
            if (items == null || items.Count == 0)
                return 0;

            int savedCount = 0;

            foreach (var item in items)
            {
                var parameters = new Dictionary<string, object>
                {
                    { "@FulfilmentNumber", fulfilmentNumber },
                    { "@WaveNumber", item.waveNumber },
                    { "@SourceOrderNumber", item.sourceOrderNumber },
                    { "@TenantCode", item.tenantCode },
                    { "@TenantName", item.tenantName },
                    { "@ItemCode", item.itemCode },
                    { "@ItemBarcode", item.itemBarcode },
                    { "@ItemSerialNo", item.itemSerialNo },
                    { "@Qty", item.qty },
                    { "@LabelDataLen", item.labelDataLen },
                    { "@LabelData", item.labelData },
                    { "@MatchLab", DBNull.Value }, // ignored as per your rule
                    { "@TrackingNumber", item.trackingNumber },
                    { "@UserName", (object)SessionVariables.UserEmail ?? DBNull.Value }
                };

                _sqlManager.ExecuteStoredProcedure("sp_AddNewOrder", parameters);
                savedCount++;
            }

            return savedCount;
        }
    }
}