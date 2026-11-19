using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CMC_Logiwa.Services
{
    /// <summary>What a start-up check did.</summary>
    public sealed class ShippedCheckSummary
    {
        public bool Skipped { get; set; }            // web service not configured
        public string SkippedReason { get; set; }
        public int Asked { get; set; }               // orders sent to the web VM
        public int Marked { get; set; }              // finished in Logiwa: will not be offered to the machine
        public int Cleared { get; set; }             // were marked, Logiwa has taken them back
        public int NoAnswer { get; set; }            // unknown / error / not found: left as they are
        public string Error { get; set; }            // the web VM could not be reached or refused: the check stopped early
    }

    /// <summary>The answer for one tracking number before a reset.</summary>
    public enum ResetVerdict
    {
        /// <summary>Logiwa says it is finished (shipped, delivered, cancelled, deleted): do not reset.</summary>
        Closed,
        /// <summary>Logiwa says it is still open: reset is fine.</summary>
        Open,
        /// <summary>Could not find out (web VM or Logiwa not reachable, order unknown there): the operator decides.</summary>
        Unknown
    }

    /// <summary>
    /// Orders that Logiwa already shows as finished (shipped, delivered, cancelled, deleted) must not be packed. In the background at
    /// start-up every Ready / NoLabel order that was stored before the start is checked through the web VM (the web VM is the only one
    /// that talks to Logiwa); finished ones get the status Shipped. Orders that arrive later are not checked: they come from Logiwa's
    /// ReadyToShip webhook. A reset of a finished box is checked the same way just before it happens.
    /// </summary>
    public sealed class ShippedCheckService
    {
        private const int PageSize = 200;        // rows read from the database at a time
        private const int BatchSize = 25;        // orders per web request: the web VM makes one Logiwa call per order

        public event Action<string> OnLog;

        /// <summary>
        /// Checks every open order stored before <paramref name="storedBefore"/>, newest first. Meant to run in the background:
        /// the machine can scan while it runs (a database change only happens if the order is still waiting at that moment).
        /// </summary>
        public async Task<ShippedCheckSummary> RunAtStartAsync(DateTime storedBefore, CancellationToken ct)
        {
            var summary = new ShippedCheckSummary();

            DbSettings settings = DbSettingsStore.Load();
            if (settings == null || !settings.HasWebService)
            {
                summary.Skipped = true;
                summary.SkippedReason = "web service not configured";
                return summary;
            }

            var sql = new SQLManager();

            using (var web = new CmcWebClient(settings))
            {
                int? beforeId = null;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    int? cursor = beforeId;
                    DataTable page = await Task.Run(() => sql.ExecuteStoredProcedure("dbo.sp_Shipped_GetToCheck", new Dictionary<string, object>
                    {
                        { "@StoredBefore", storedBefore },
                        { "@BeforeId", cursor },
                        { "@Max", PageSize }
                    }), ct).ConfigureAwait(false);

                    var rows = page.Rows.Cast<DataRow>()
                        .Select(r => new { Id = Convert.ToInt32(r["PackJobID"]), Tracking = r["TrackingNumber"].ToString(), Shipment = r["ShipmentIdentifier"].ToString().Trim() })
                        .ToList();
                    if (rows.Count == 0) break;
                    beforeId = rows.Min(r => r.Id);

                    // Only real Logiwa identifiers (GUIDs) can be asked about. Anything else (test data such as TEST-SHIPMENT-1,
                    // a typo) is left as it is: it would only make the web VM refuse the request.
                    int notAskable = rows.Count(r => !Guid.TryParse(r.Shipment, out _));
                    summary.NoAnswer += notAskable;
                    rows = rows.Where(r => Guid.TryParse(r.Shipment, out _)).ToList();

                    for (int i = 0; i < rows.Count; i += BatchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var batch = rows.Skip(i).Take(BatchSize).ToList();
                        summary.Asked += batch.Count;

                        List<OrderStatusItem> answers;
                        try
                        {
                            answers = await web.GetOrderStatusAsync(batch.Select(b => b.Shipment).Distinct(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
                        }
                        catch (CmcWebException ex)
                        {
                            summary.Error = ex.Message;
                            return summary;      // the rest would fail the same way: try again at the next start
                        }

                        foreach (var job in batch)
                        {
                            var answer = answers.FirstOrDefault(a => string.Equals(a.ShipmentIdentifier, job.Shipment, StringComparison.OrdinalIgnoreCase));
                            if (answer == null || answer.State != "found")
                            {
                                summary.NoAnswer++;
                                continue;
                            }

                            string result = await Task.Run(() => Apply(sql, job.Id, answer), ct).ConfigureAwait(false);
                            if (result == "MARKED")
                            {
                                summary.Marked++;
                                OnLog?.Invoke($"INFO: {job.Tracking} is finished in Logiwa (status {answer.Status}): it will not be offered to the machine.");
                            }
                            else if (result == "CLEARED")
                            {
                                summary.Cleared++;
                                OnLog?.Invoke($"INFO: {job.Tracking} is open in Logiwa again (status {answer.Status}): offered to the machine again.");
                            }
                        }
                    }
                }
            }

            return summary;
        }

        /// <summary>
        /// Asks Logiwa about one tracking number just before it is reset. If Logiwa says it is finished, the local record is marked
        /// (a Ready / NoLabel one becomes Shipped; a finished box keeps its status and only stores the Logiwa status).
        /// </summary>
        public async Task<(ResetVerdict Verdict, string LogiwaStatus)> CheckBeforeResetAsync(string trackingNumber, CancellationToken ct)
        {
            DbSettings settings = DbSettingsStore.Load();
            if (settings == null || !settings.HasWebService) return (ResetVerdict.Unknown, null);

            var sql = new SQLManager();
            DataTable dt = await Task.Run(() => sql.ExecuteStoredProcedure("dbo.sp_Shipped_GetByOrder", new Dictionary<string, object> { { "@OrderNumber", trackingNumber } }), ct);
            if (dt.Rows.Count == 0) return (ResetVerdict.Unknown, null);

            DataRow row = dt.Rows[0];
            string shipment = row["ShipmentIdentifier"] == DBNull.Value ? "" : row["ShipmentIdentifier"].ToString().Trim();
            if (!Guid.TryParse(shipment, out _)) return (ResetVerdict.Unknown, null);

            try
            {
                using (var web = new CmcWebClient(settings))
                {
                    var answers = await web.GetOrderStatusAsync(new[] { shipment }, ct);
                    var answer = answers.FirstOrDefault(a => string.Equals(a.ShipmentIdentifier, shipment, StringComparison.OrdinalIgnoreCase));
                    if (answer == null || answer.State != "found") return (ResetVerdict.Unknown, null);

                    int packJobId = Convert.ToInt32(row["PackJobID"]);
                    await Task.Run(() => Apply(sql, packJobId, answer), ct);
                    return (answer.Closed ? ResetVerdict.Closed : ResetVerdict.Open, answer.Status);
                }
            }
            catch (CmcWebException)
            {
                return (ResetVerdict.Unknown, null);
            }
        }

        private static string Apply(SQLManager sql, int packJobId, OrderStatusItem answer)
        {
            DataTable dt = sql.ExecuteStoredProcedure("dbo.sp_Shipped_Apply", new Dictionary<string, object>
            {
                { "@PackJobID", packJobId },
                { "@LogiwaStatus", answer.Status },
                { "@Shipped", answer.Closed },
                { "@Hold", answer.Hold },
                { "@Cancelled", answer.Cancelled }
            });
            return dt.Rows.Count == 0 ? "" : dt.Rows[0]["Result"].ToString();
        }
    }
}
