using CMC_VER1.Helpers;
using CMC_VER1.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CMC_VER1.Services
{
    internal class ShipmentApiService
    {
        private readonly HttpClient _http;

        public ShipmentApiService(HttpClient http)
        {
            _http = http;
        }

        public async Task<string> UpdateShipmentInfoAsync(UpdateShipmentInfoRequest requestModel)
        {
            //MessageBox.Show($"Request Payload:\nFulfilmentNumber: {requestModel.FulfilmentNumber}\nWaveNumber: {requestModel.WaveNumber}\nOrderStatus: {requestModel.OrderStatus}\nBoxSizeH: {requestModel.BoxSizeH}\nBoxSizeL: {requestModel.BoxSizeL}\nBoxSizeW: {requestModel.BoxSizeW}\nBoxArea: {requestModel.BoxArea}\nWeight_Carton: {requestModel.Weight_Carton}\nBox_Weight: {requestModel.Box_Weight}", "UpdateShipmentInfo Request", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (requestModel == null)
                throw new ArgumentNullException(nameof(requestModel));

            if (string.IsNullOrWhiteSpace(requestModel.FulfilmentNumber))
                throw new ArgumentException("FulfilmentNumber cannot be empty.");

            if (string.IsNullOrWhiteSpace(requestModel.WaveNumber))
                throw new ArgumentException("WaveNumber cannot be empty.");

            var payload = new List<UpdateShipmentInfoRequest>
            {
                requestModel
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.UpdateShipmentInfo))
            {
                req.Headers.Add("API-Key", SessionVariables.ApiKey);
                req.Headers.Add("User-Email", SessionVariables.UserEmail);

                req.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var resp = await _http.SendAsync(req))
                {
                    string responseJson = await resp.Content.ReadAsStringAsync();
                    //MessageBox.Show($"Response: {responseJson}", "UpdateShipmentInfo Result", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception(
                            $"UpdateShipmentInfo failed. Status: {(int)resp.StatusCode}. Response: {responseJson}");
                    }

                    return responseJson;
                }
            }
        }
    }
}