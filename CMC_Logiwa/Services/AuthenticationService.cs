using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CMC_Logiwa
{
    public sealed class AuthenticationService
    {
        // Keep a single static HttpClient for the app’s lifetime
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private const string Endpoint =
            "https://api1.3plwms.com.au/UsersAPI/api/Users/CheckMobileAuthentication";

        public class AuthRequest
        {
            [JsonProperty("email")]
            public string Email { get; set; }

            [JsonProperty("password")]
            public string Password { get; set; }

            [JsonProperty("mobileDeviceIdentifier")]
            public string MobileDeviceIdentifier { get; set; }
        }

        public class AuthResponse
        {
            [JsonProperty("isAuthenticated")]
            public bool IsAuthenticated { get; set; }

            [JsonProperty("errorMessage")]
            public string ErrorMessage { get; set; }

            [JsonProperty("apiKey")]
            public string ApiKey { get; set; }

            [JsonProperty("warehouse")]
            public string Warehouse { get; set; }

            [JsonProperty("warehouseCode")]
            public string WarehouseCode { get; set; }

            [JsonProperty("warehouseID")]
            public int WarehouseID { get; set; }
        }

        public async Task<AuthResponse> AuthenticateAsync(string email, string password, string mobileDeviceIdentifier)
        {
            var request = new AuthRequest
            {
                Email = email?.Trim(),
                Password = password, // do not trim passwords
                MobileDeviceIdentifier = mobileDeviceIdentifier
            };

            var json = JsonConvert.SerializeObject(request);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var message = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                message.Content = content;
                message.Headers.Accept.ParseAdd("application/json");

                //using (var resp = await _http.SendAsync(message).ConfigureAwait(false))
                //{
                //    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                //    // If the API always returns the JSON contract you shared, we can parse it directly.
                //    // Otherwise you could check resp.IsSuccessStatusCode first.
                //    var parsed = JsonConvert.DeserializeObject<AuthResponse>(body) ?? new AuthResponse
                //    {
                //        IsAuthenticated = false,
                //        ErrorMessage = "Empty or invalid response from server."
                //    };

                //    // If HTTP status is non-success and server didn't set a message, add a generic one
                //    if (!resp.IsSuccessStatusCode && string.IsNullOrWhiteSpace(parsed.ErrorMessage))
                //    {
                //        parsed.IsAuthenticated = false;
                //        parsed.ErrorMessage = $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}";
                //    }

                //    return parsed;
                //}
                using (var resp = await _http.SendAsync(message).ConfigureAwait(false))
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // 1) If HTTP failed, do not try to parse as JSON
                    if (!resp.IsSuccessStatusCode)
                    {
                        return new AuthResponse
                        {
                            IsAuthenticated = false,
                            ErrorMessage = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}"
                        };
                    }

                    // 2) Ensure we actually got JSON
                    var trimmed = body?.TrimStart();
                    if (string.IsNullOrWhiteSpace(trimmed) || (trimmed[0] != '{' && trimmed[0] != '['))
                    {
                        return new AuthResponse
                        {
                            IsAuthenticated = false,
                            ErrorMessage = $"Server returned non-JSON response: {body}"
                        };
                    }

                    // 3) Safe to parse
                    var parsed = JsonConvert.DeserializeObject<AuthResponse>(body);
                    return parsed ?? new AuthResponse
                    {
                        IsAuthenticated = false,
                        ErrorMessage = "Empty or invalid JSON response from server."
                    };
                }
            }
        }
    }
}
