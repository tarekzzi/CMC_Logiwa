using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CMC_Logiwa.Services
{
    /// <summary>What the status line in the main window shows.</summary>
    public sealed class SyncStatus
    {
        public bool Configured { get; set; }
        public DateTime? LastOk { get; set; }
        public string Error { get; set; }
        public int PendingLabelRequests { get; set; }
        public bool NeedsWarehouse { get; set; }     // no warehouse chosen in Settings > Machine: orders are not downloaded
    }

    /// <summary>
    /// Keeps the local database in step with the web VM and carries label requests back:
    /// every few seconds it downloads orders (with labels) newer than SyncState.LastSyncSeq into PackJob,
    /// then sends the label requests the ENQ handler queued. Runs never overlap. A "not found" or "no label"
    /// scan asks for an immediate run through RequestPoll().
    /// </summary>
    public sealed class OrderSyncService
    {
        private const int PageSize = 100;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

        private static volatile OrderSyncService _current;

        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private readonly object _gate = new object();
        private readonly string _machineId;
        private CancellationTokenSource _cts;
        private Task _loop;
        private string _lastLoggedError;
        private readonly SyncStatus _status = new SyncStatus();

        public OrderSyncService(string machineId)
        {
            _machineId = machineId;
        }

        /// <summary>Progress and problems for the message log (already prefixed INFO: or ERROR:). Raised on a worker thread.</summary>
        public event Action<string> OnLog;
        /// <summary>Raised on a worker thread after every run.</summary>
        public event Action<SyncStatus> OnStatus;

        /// <summary>Asks the running service for a poll right now (skipped when none is running or one is already due).</summary>
        public static void RequestPoll() => _current?.Wake();

        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null) return;
                _cts = new CancellationTokenSource();
                _current = this;
                _loop = Task.Run(() => LoopAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_loop == null) return;
                if (ReferenceEquals(_current, this)) _current = null;
                _cts.Cancel();
                _loop = null;
            }
        }

        private void Wake()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Fail(ex.Message); }   // never let the loop die

                try { await _wake.WaitAsync(Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            var settings = DbSettingsStore.Load();
            if (settings == null || !settings.HasWebService)
            {
                _status.Configured = false;
                _status.Error = null;
                OnStatus?.Invoke(_status);
                return;
            }
            _status.Configured = true;

            // The warehouse this machine serves (Settings > Machine). Without it no orders are downloaded and no label requests
            // are sent: every warehouse's orders would arrive. Finished boxes are still reported to Logiwa.
            string loadError;
            MachineSettings machine = MachineSettingsStore.TryLoad(out loadError);
            string warehouse = machine != null && machine.HasWarehouse ? machine.WarehouseId : null;
            _status.NeedsWarehouse = warehouse == null;

            var sql = new SQLManager();

            using (var web = new CmcWebClient(settings))
            {
                // Independent jobs against the same web service. A problem in one does not skip the others, except
                // when the web service cannot be reached at all: then the rest would only time out as well.
                CmcWebException failure = null;
                var steps = new List<Action>();
                if (warehouse != null)
                {
                    steps.Add(() => SyncOrders(sql, web, warehouse, ct));
                    steps.Add(() => SendLabelRequests(sql, web, warehouse, ct));
                }
                steps.Add(() => SendResults(sql, web, ct));

                foreach (var step in steps)
                {
                    try
                    {
                        await Task.Run(step, ct).ConfigureAwait(false);
                    }
                    catch (CmcWebException ex)
                    {
                        if (failure == null) failure = ex;
                        if (ex.Unreachable) break;
                    }
                }

                if (failure != null)
                {
                    sql.ExecuteStoredProcedure("dbo.sp_Sync_SetError", new Dictionary<string, object> { { "@Error", Truncate(failure.Message, 400) } });
                    Fail(failure.Message);
                    return;
                }
            }

            _status.LastOk = DateTime.Now;
            _status.Error = null;
            _lastLoggedError = null;
            _status.PendingLabelRequests = ReadPendingRequests(sql);
            OnStatus?.Invoke(_status);
        }

        private void SyncOrders(SQLManager sql, CmcWebClient web, string warehouse, CancellationToken ct)
        {
            DataTable state = sql.ExecuteStoredProcedure("dbo.sp_Sync_GetState", null);
            long after = state.Rows.Count == 0 ? 0 : Convert.ToInt64(state.Rows[0]["LastSyncSeq"]);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                OrdersPage page = web.GetOrdersAsync(after, PageSize, warehouse, ct).GetAwaiter().GetResult();

                // An empty page still goes through the procedure: it stamps LastPollAt and clears the last error.
                DataTable result = sql.ExecuteStoredProcedure("dbo.sp_Sync_UpsertOrders", new Dictionary<string, object>
                {
                    { "@Json", page.Count == 0 ? "[]" : page.OrdersJson },
                    { "@MaxSeq", page.MaxSeq }
                });

                if (page.Count > 0)
                {
                    int inserted = Convert.ToInt32(result.Rows[0]["Inserted"]);
                    int updated = Convert.ToInt32(result.Rows[0]["Updated"]);
                    OnLog?.Invoke($"INFO: Web sync: {page.Count} order(s) received, {inserted} new, {updated} updated.");
                }

                if (!page.HasMore || page.MaxSeq <= after) break;
                after = page.MaxSeq;
            }
        }

        private void SendLabelRequests(SQLManager sql, CmcWebClient web, string warehouse, CancellationToken ct)
        {
            DataTable due = sql.ExecuteStoredProcedure("dbo.sp_LabelReq_GetNext", new Dictionary<string, object> { { "@Max", 20 } });

            // The requests are sent oldest first. One request that keeps failing must not hold back the ones behind it:
            // every failure is recorded on that request (sp_LabelReq_MarkFailed pushes its next attempt out, up to 5 minutes)
            // and the loop goes on. Only a web service that cannot be reached at all stops the loop, because the
            // remaining requests would fail the same way (each waiting for the 30 second timeout).
            CmcWebException webFailure = null;

            foreach (DataRow row in due.Rows)
            {
                ct.ThrowIfCancellationRequested();

                int id = 0;
                string tracking = null;
                try
                {
                    id = Convert.ToInt32(row["RequestId"]);
                    tracking = row["TrackingNumber"].ToString();
                    string machine = row["MachineId"] == DBNull.Value ? _machineId : row["MachineId"].ToString();
                    DateTime requestedAt = Convert.ToDateTime(row["RequestedAt"]);

                    LabelRequestResult outcome = web.RequestLabelAsync(tracking, machine, requestedAt, warehouse, ct).GetAwaiter().GetResult();

                    if (outcome == LabelRequestResult.Accepted)
                    {
                        sql.ExecuteStoredProcedure("dbo.sp_LabelReq_MarkSent", new Dictionary<string, object> { { "@RequestId", id } });
                        OnLog?.Invoke($"INFO: Label requested for {tracking}.");
                    }
                    else
                    {
                        // Logiwa has no order for this tracking number (or it cannot be pinned down): close the request.
                        // Scanning it again after a couple of minutes asks again.
                        sql.ExecuteStoredProcedure("dbo.sp_LabelReq_MarkDone", new Dictionary<string, object>
                            { { "@RequestId", id }, { "@Note", "Logiwa does not know this tracking number." } });
                        OnLog?.Invoke($"INFO: Logiwa has no order for {tracking}.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (CmcWebException ex)
                {
                    TryMarkFailed(sql, id, ex.Message);
                    webFailure = ex;
                    if (ex.Unreachable) break;   // the others would only time out as well: try again next run
                }
                catch (Exception ex)
                {
                    // A local problem with this one request (for example a database error while closing it).
                    // The request stays queued and is tried again later; the others are not held back.
                    TryMarkFailed(sql, id, ex.Message);
                    OnLog?.Invoke($"ERROR: Label request for {tracking ?? "?"}: {ex.Message}");
                }
            }

            // Still report a web problem in the status line, but only after every request had its turn.
            if (webFailure != null) throw webFailure;
        }

        /// <summary>
        /// Sends the measurements of finished boxes (the END handler queued them as PushStatus = Pending) to the web VM,
        /// which writes them to Logiwa. Same rules as the label requests: oldest first, a failing result is recorded on
        /// itself (sp_Outbox_MarkFailed: retry after 10 s, 20 s ... up to 5 minutes, never gives up) and does not hold back
        /// the others; only an unreachable web service stops the run.
        /// </summary>
        private void SendResults(SQLManager sql, CmcWebClient web, CancellationToken ct)
        {
            DataTable due = sql.ExecuteStoredProcedure("dbo.sp_Outbox_GetNext", new Dictionary<string, object> { { "@Max", 20 } });
            CmcWebException webFailure = null;

            foreach (DataRow row in due.Rows)
            {
                ct.ThrowIfCancellationRequested();

                int id = 0;
                string tracking = null;
                try
                {
                    var result = new OutboxResult
                    {
                        PackJobId = Convert.ToInt32(row["PackJobID"]),
                        TrackingNumber = row["TrackingNumber"].ToString(),
                        MachineId = row["MachineId"] == DBNull.Value ? _machineId : row["MachineId"].ToString(),
                        BoxWeightG = IntOrZero(row["BoxWeightG"]),
                        LengthMm = IntOrZero(row["LengthMm"]),
                        WidthMm = IntOrZero(row["WidthMm"]),
                        HeightMm = IntOrZero(row["HeightMm"]),
                        CompletedAtLocal = row["CompletedAt"] == DBNull.Value ? DateTime.Now : Convert.ToDateTime(row["CompletedAt"])
                    };
                    id = result.PackJobId;
                    tracking = result.TrackingNumber;

                    web.PostResultAsync(result, ct).GetAwaiter().GetResult();

                    sql.ExecuteStoredProcedure("dbo.sp_Outbox_MarkSent", new Dictionary<string, object> { { "@PackJobID", id } });
                    OnLog?.Invoke($"INFO: Result sent for {tracking}: {result.BoxWeightG} g, {result.LengthMm} x {result.WidthMm} x {result.HeightMm} mm.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (CmcWebException ex)
                {
                    TryMarkResultFailed(sql, id, ex.Message);
                    webFailure = ex;
                    if (ex.Unreachable) break;
                }
                catch (Exception ex)
                {
                    TryMarkResultFailed(sql, id, ex.Message);
                    OnLog?.Invoke($"ERROR: Result for {tracking ?? "?"}: {ex.Message}");
                }
            }

            if (webFailure != null) throw webFailure;
        }

        private static int IntOrZero(object value) => value == DBNull.Value ? 0 : Convert.ToInt32(value);

        private static void TryMarkResultFailed(SQLManager sql, int packJobId, string message)
        {
            if (packJobId <= 0) return;
            try
            {
                sql.ExecuteStoredProcedure("dbo.sp_Outbox_MarkFailed", new Dictionary<string, object>
                    { { "@PackJobID", packJobId }, { "@Error", Truncate(message, 400) } });
            }
            catch
            {
                // The database is the problem; the next run tries again.
            }
        }

        private static void TryMarkFailed(SQLManager sql, int requestId, string message)
        {
            if (requestId <= 0) return;
            try
            {
                sql.ExecuteStoredProcedure("dbo.sp_LabelReq_MarkFailed", new Dictionary<string, object>
                    { { "@RequestId", requestId }, { "@Error", Truncate(message, 400) } });
            }
            catch
            {
                // The database is the problem; the next run tries again. Nothing more to do for this request.
            }
        }

        private static int ReadPendingRequests(SQLManager sql)
        {
            try
            {
                DataTable t = sql.ExecuteStoredProcedure("dbo.sp_Outbox_Counts", null);
                return t.Rows.Count == 0 ? 0 : Convert.ToInt32(t.Rows[0]["PendingLabelRequests"]);
            }
            catch { return 0; }
        }

        private void Fail(string message)
        {
            _status.Error = message;
            // Same problem every 20 seconds would flood the log: tell it once until it changes or clears.
            if (message != _lastLoggedError)
            {
                _lastLoggedError = message;
                OnLog?.Invoke("ERROR: Web sync: " + message);
            }
            OnStatus?.Invoke(_status);
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);
    }
}
