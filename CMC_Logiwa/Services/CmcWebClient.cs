using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CMC_Logiwa.Services
{
    /// <summary>One page from GET /api/cmc/orders. OrdersJson is the raw "orders" array, handed to sp_Sync_UpsertOrders as is.</summary>
    public sealed class OrdersPage
    {
        public string OrdersJson { get; set; }
        public int Count { get; set; }
        public long MaxSeq { get; set; }
        public bool HasMore { get; set; }
    }

    public enum LabelRequestResult
    {
        /// <summary>Accepted (202): the web VM will fetch or resend the label.</summary>
        Accepted,
        /// <summary>404: Logiwa has no order for this tracking number (the web VM looked it up). The request is closed.</summary>
        NotKnownYet
    }

    /// <summary>Thrown for any non-success answer or network failure. The message never contains the API key or label data.</summary>
    public sealed class CmcWebException : Exception
    {
        /// <summary>True when the web service could not be reached at all (network error or timeout), as opposed to answering with an error for this one call.</summary>
        public bool Unreachable { get; }

        public CmcWebException(string message, Exception inner = null, bool unreachable = false) : base(message, inner)
        {
            Unreachable = unreachable;
        }
    }

    /// <summary>
    /// The packing PC's only way out: HTTPS calls to the web VM, authenticated with this PC's API key.
    /// The PC never talks to Logiwa.
    /// </summary>
    public sealed class CmcWebClient : IDisposable
    {
        private readonly HttpClient _http;

        public CmcWebClient(DbSettings settings)
        {
            // .NET Framework 4.7.2 does not always offer TLS 1.2 by default.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var baseUrl = (settings.WebServiceUrl ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new CmcWebException("The web service address must start with https://");

            _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            {
                BaseAddress = new Uri(baseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey.Trim());
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<OrdersPage> GetOrdersAsync(long afterSeq, int limit, CancellationToken ct)
        {
            string body = await SendAsync(() => _http.GetAsync($"api/cmc/orders?afterSeq={afterSeq}&limit={limit}", ct), "orders");

            try
            {
                var root = JObject.Parse(body);
                var orders = (JArray)root["orders"] ?? new JArray();
                return new OrdersPage
                {
                    OrdersJson = orders.ToString(Formatting.None),
                    Count = orders.Count,
                    MaxSeq = root.Value<long?>("maxSeq") ?? afterSeq,
                    HasMore = root.Value<bool?>("hasMore") ?? false
                };
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
            {
                throw new CmcWebException("The web service answered, but not with an order list.", ex);
            }
        }

        public async Task<LabelRequestResult> RequestLabelAsync(string trackingNumber, string machineId, DateTime requestedAtLocal, CancellationToken ct)
        {
            string json = JsonConvert.SerializeObject(new
            {
                trackingNumber,
                machineId,
                requestedAt = new DateTimeOffset(requestedAtLocal)
            });

            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var resp = await _http.PostAsync("api/cmc/label-requests", content, ct).ConfigureAwait(false))
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound) return LabelRequestResult.NotKnownYet;
                    if (resp.IsSuccessStatusCode) return LabelRequestResult.Accepted;
                    throw new CmcWebException(Describe(resp.StatusCode));
                }
            }
            catch (CmcWebException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new CmcWebException("Cannot reach the web service: " + ex.Message, ex, unreachable: true); }
        }

        /// <summary>Checks the address and the key without downloading any label: asks for orders after a sequence number that cannot exist.</summary>
        public async Task PingAsync(CancellationToken ct)
        {
            await SendAsync(() => _http.GetAsync("api/cmc/orders?afterSeq=9223372036854775000&limit=1", ct), "ping");
        }

        private static async Task<string> SendAsync(Func<Task<HttpResponseMessage>> call, string what)
        {
            try
            {
                using (var resp = await call().ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new CmcWebException(Describe(resp.StatusCode));
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (CmcWebException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new CmcWebException("Cannot reach the web service: " + ex.Message, ex); }
        }

        private static string Describe(HttpStatusCode code)
        {
            switch ((int)code)
            {
                case 401: return "The web service refused the API key (401). Check the key in Database settings.";
                case 403: return "The web service refused this PC's address (403).";
                default: return $"The web service answered {(int)code} {code}.";
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
