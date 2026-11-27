using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>One measured box waiting to be sent: a row of sp_Outbox_GetNext. Millimetres and grams as the machine reported them.</summary>
    /// <summary>What Logiwa says about one order. State: found, notfound, unknown (the web VM never received it) or error.</summary>
    public sealed class OrderStatusItem
    {
        public string ShipmentIdentifier { get; set; }
        public string State { get; set; }
        public string Status { get; set; }
        public bool Closed { get; set; }
        public bool Hold { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed class OutboxResult
    {
        public int PackJobId { get; set; }
        public string TrackingNumber { get; set; }
        public string MachineId { get; set; }
        public int BoxWeightG { get; set; }
        public int LengthMm { get; set; }
        public int WidthMm { get; set; }
        public int HeightMm { get; set; }
        public DateTime CompletedAtLocal { get; set; }
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

        /// <summary>The warehouses Logiwa knows, for the drop-down in Settings > Machine.</summary>
        public async Task<List<WarehouseInfo>> GetWarehousesAsync(CancellationToken ct)
        {
            string body = await SendAsync(() => _http.GetAsync("api/cmc/warehouses", ct), "warehouses", ct);

            try
            {
                var list = new List<WarehouseInfo>();
                foreach (JObject o in (JArray)JObject.Parse(body)["warehouses"] ?? new JArray())
                    list.Add(new WarehouseInfo
                    {
                        Identifier = o.Value<string>("identifier"),
                        Code = o.Value<string>("code") ?? "",
                        Description = o.Value<string>("description")
                    });
                return list.Where(w => !string.IsNullOrWhiteSpace(w.Identifier)).ToList();
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
            {
                throw new CmcWebException("The web service answered, but not with a warehouse list.", ex);
            }
        }

        /// <param name="warehouseId">Only this warehouse's orders are sent (the one this machine serves).</param>
        public async Task<OrdersPage> GetOrdersAsync(long afterSeq, int limit, string warehouseId, CancellationToken ct)
        {
            string url = $"api/cmc/orders?afterSeq={afterSeq}&limit={limit}" +
                         (string.IsNullOrWhiteSpace(warehouseId) ? "" : "&warehouseIdentifier=" + Uri.EscapeDataString(warehouseId));
            string body = await SendAsync(() => _http.GetAsync(url, ct), "orders", ct);

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

        /// <summary>One status change announced by Logiwa's webhook. Kind: closed (shipped, cancelled ...), hold or open.</summary>
        public sealed class StatusChange
        {
            public string ShipmentIdentifier { get; set; }
            public string Status { get; set; }
            public string Kind { get; set; }
        }

        public sealed class StatusChangesPage
        {
            public List<StatusChange> Changes { get; } = new List<StatusChange>();
            public long MaxSeq { get; set; }
            public bool HasMore { get; set; }
        }

        public async Task<StatusChangesPage> GetOrderChangesAsync(long afterSeq, int limit, string warehouseId, CancellationToken ct)
        {
            string url = $"api/cmc/order-changes?afterSeq={afterSeq}&limit={limit}" +
                         (string.IsNullOrWhiteSpace(warehouseId) ? "" : "&warehouseIdentifier=" + Uri.EscapeDataString(warehouseId));
            string body = await SendAsync(() => _http.GetAsync(url, ct), "order changes", ct);

            try
            {
                var root = JObject.Parse(body);
                var page = new StatusChangesPage
                {
                    MaxSeq = root.Value<long?>("maxSeq") ?? afterSeq,
                    HasMore = root.Value<bool?>("hasMore") ?? false
                };
                foreach (JObject o in (JArray)root["changes"] ?? new JArray())
                    page.Changes.Add(new StatusChange
                    {
                        ShipmentIdentifier = o.Value<string>("shipmentIdentifier"),
                        Status = o.Value<string>("status"),
                        Kind = o.Value<string>("kind") ?? "open"
                    });
                return page;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
            {
                throw new CmcWebException("The web service answered, but not with a status change list.", ex);
            }
        }

        public async Task<LabelRequestResult> RequestLabelAsync(string trackingNumber, string machineId, DateTime requestedAtLocal, string warehouseId, CancellationToken ct)
        {
            string json = JsonConvert.SerializeObject(new
            {
                trackingNumber,
                machineId,
                requestedAt = new DateTimeOffset(requestedAtLocal),
                warehouseIdentifier = string.IsNullOrWhiteSpace(warehouseId) ? null : warehouseId   // the web VM looks the tracking number up in this warehouse
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

        /// <summary>
        /// Sends what the machine measured for one box. Returns normally when the web service stored it (202) or already had it;
        /// every other answer, and a network failure, is a CmcWebException so the caller retries later.
        /// </summary>
        public async Task PostResultAsync(OutboxResult result, CancellationToken ct)
        {
            string json = JsonConvert.SerializeObject(new
            {
                trackingNumber = result.TrackingNumber,
                packJobId = result.PackJobId,
                machineId = result.MachineId,
                boxWeightG = result.BoxWeightG,
                lengthMm = result.LengthMm,
                widthMm = result.WidthMm,
                heightMm = result.HeightMm,
                completedAt = new DateTimeOffset(result.CompletedAtLocal)
            });

            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var resp = await _http.PostAsync("api/cmc/results", content, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode) return;
                    throw new CmcWebException(Describe(resp.StatusCode));
                }
            }
            catch (CmcWebException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new CmcWebException("Cannot reach the web service: " + ex.Message, ex, unreachable: true); }
        }

        /// <summary>Checks the address and the key without downloading any label: asks for orders after a sequence number that cannot exist.</summary>
        /// <summary>
        /// Asks the web VM for the Logiwa status of orders (at most 50 per call). Orders the web VM cannot answer for
        /// come back as "unknown" or "error": the caller must leave those as they are.
        /// </summary>
        public async Task<List<OrderStatusItem>> GetOrderStatusAsync(IEnumerable<string> shipmentIdentifiers, CancellationToken ct)
        {
            string json = JsonConvert.SerializeObject(new { shipmentIdentifiers = shipmentIdentifiers.ToArray() });
            string body;

            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var resp = await _http.PostAsync("api/cmc/order-status", content, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new CmcWebException(Describe(resp.StatusCode));
                    body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (CmcWebException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new CmcWebException("Cannot reach the web service: " + ex.Message, ex, unreachable: true); }

            try
            {
                var list = new List<OrderStatusItem>();
                foreach (JObject o in (JArray)JObject.Parse(body)["orders"] ?? new JArray())
                    list.Add(new OrderStatusItem
                    {
                        ShipmentIdentifier = o.Value<string>("shipmentIdentifier"),
                        State = o.Value<string>("state") ?? "",
                        Status = o.Value<string>("status"),
                        Closed = o.Value<bool?>("closed") ?? false,
                        Hold = o.Value<bool?>("hold") ?? false,
                        Cancelled = o.Value<bool?>("cancelled") ?? false
                    });
                return list;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
            {
                throw new CmcWebException("The web service answered, but not with an order status list.", ex);
            }
        }

        /// <summary>
        /// GET /api/cmc/health: what the web VM knows about its database, its calls to Logiwa and the webhooks.
        /// A CmcWebException means the web VM did not answer properly (the caller reports that as a problem itself).
        /// </summary>
        public async Task<BackendHealthReport> GetHealthAsync(CancellationToken ct)
        {
            string body = await SendAsync(() => _http.GetAsync("api/cmc/health", ct), "health", ct);

            try
            {
                JObject root;
                using (var reader = new JsonTextReader(new System.IO.StringReader(body)) { DateParseHandling = DateParseHandling.None })
                    root = JObject.Load(reader);

                var report = new BackendHealthReport
                {
                    Ok = root.Value<bool?>("ok") ?? false,
                    LastWebhookAt = LocalTime(root.Value<string>("lastWebhookAt")),
                    PcCheckMinutes = root.Value<int?>("pcCheckMinutes")
                };
                foreach (JObject p in (JArray)root["problems"] ?? new JArray())
                    report.Problems.Add(new BackendProblem
                    {
                        Code = p.Value<string>("code") ?? "unknown",
                        Message = p.Value<string>("message") ?? "",
                        Since = LocalTime(p.Value<string>("since"))
                    });
                return report;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
            {
                throw new CmcWebException("The web service answered, but not with a health report (is the web VM up to date?).", ex);
            }
        }

        /// <summary>GET /api/cmc/settings: definitions, values in force, read-only information and recent changes (raw JSON, dates as text).</summary>
        public async Task<JObject> GetSettingsAsync(CancellationToken ct)
        {
            string body = await SendAsync(() => _http.GetAsync("api/cmc/settings", ct), "settings", ct);
            try { return ParseNoDates(body); }
            catch (JsonException ex) { throw new CmcWebException("The web service answered, but not with its settings (is the web VM up to date?).", ex); }
        }

        /// <summary>Result of a settings save: the new state on success, or the reasons it was refused (key may be empty for the whole save).</summary>
        public sealed class SettingsSaveResult
        {
            public JObject Settings { get; set; }
            public List<KeyValuePair<string, string>> Errors { get; } = new List<KeyValuePair<string, string>>();
            public bool Ok => Errors.Count == 0;
        }

        /// <summary>
        /// PUT /api/cmc/settings with the administrator key (sent for this call only, never stored). Refusals (400 / 401 / 403) come back
        /// as Errors; other failures are a CmcWebException.
        /// </summary>
        public async Task<SettingsSaveResult> SaveSettingsAsync(string adminKey, string changedBy, string machineId,
            JObject values, bool confirmOpenIpList, CancellationToken ct)
        {
            var payload = new JObject
            {
                ["changedBy"] = changedBy,
                ["machineId"] = machineId,
                ["values"] = values,
                ["confirmOpenIpList"] = confirmOpenIpList
            };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Put, "api/cmc/settings"))
                {
                    req.Headers.Add("X-Admin-Key", adminKey ?? "");
                    req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var result = new SettingsSaveResult();
                        int code = (int)resp.StatusCode;

                        if (resp.IsSuccessStatusCode)
                        {
                            result.Settings = ParseNoDates(body);
                            return result;
                        }
                        if (code == 400 || code == 401 || code == 403)
                        {
                            JObject err = null;
                            try { err = ParseNoDates(body); } catch (JsonException) { }
                            foreach (JObject e in (JArray)err?["errors"] ?? new JArray())
                                result.Errors.Add(new KeyValuePair<string, string>(e.Value<string>("key") ?? "", e.Value<string>("message") ?? ""));
                            if (result.Errors.Count == 0)
                                result.Errors.Add(new KeyValuePair<string, string>("", code == 401
                                    ? "Refused (401): the API key or the administrator key is not correct."
                                    : Describe(resp.StatusCode)));
                            return result;
                        }
                        throw new CmcWebException(Describe(resp.StatusCode));
                    }
                }
            }
            catch (CmcWebException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new CmcWebException("Cannot reach the web service: " + ex.Message, ex, unreachable: true); }
        }

        private static JObject ParseNoDates(string body)
        {
            using (var reader = new JsonTextReader(new System.IO.StringReader(body)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static DateTime? LocalTime(string iso) =>
            DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
                ? t.LocalDateTime : (DateTime?)null;

        public async Task PingAsync(CancellationToken ct)
        {
            await SendAsync(() => _http.GetAsync("api/cmc/orders?afterSeq=9223372036854775000&limit=1", ct), "ping", ct);
        }

        private static async Task<string> SendAsync(Func<Task<HttpResponseMessage>> call, string what, CancellationToken ct)
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
            // Only a cancel the caller asked for ends the run. A timeout is also an OperationCanceledException: that is "cannot reach".
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new CmcWebException("Cannot reach the web service: " + ex.Message, ex, unreachable: true); }
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
