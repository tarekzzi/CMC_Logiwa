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

            var sql = new SQLManager();

            using (var web = new CmcWebClient(settings))
            {
                try
                {
                    await Task.Run(() => SyncOrders(sql, web, ct), ct).ConfigureAwait(false);
                    await Task.Run(() => SendLabelRequests(sql, web, ct), ct).ConfigureAwait(false);
                }
                catch (CmcWebException ex)
                {
                    sql.ExecuteStoredProcedure("dbo.sp_Sync_SetError", new Dictionary<string, object> { { "@Error", Truncate(ex.Message, 400) } });
                    Fail(ex.Message);
                    return;
                }
            }

            _status.LastOk = DateTime.Now;
            _status.Error = null;
            _lastLoggedError = null;
            _status.PendingLabelRequests = ReadPendingRequests(sql);
            OnStatus?.Invoke(_status);
        }

        private void SyncOrders(SQLManager sql, CmcWebClient web, CancellationToken ct)
        {
            DataTable state = sql.ExecuteStoredProcedure("dbo.sp_Sync_GetState", null);
            long after = state.Rows.Count == 0 ? 0 : Convert.ToInt64(state.Rows[0]["LastSyncSeq"]);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                OrdersPage page = web.GetOrdersAsync(after, PageSize, ct).GetAwaiter().GetResult();

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

        private void SendLabelRequests(SQLManager sql, CmcWebClient web, CancellationToken ct)
        {
            DataTable due = sql.ExecuteStoredProcedure("dbo.sp_LabelReq_GetNext", new Dictionary<string, object> { { "@Max", 20 } });

            foreach (DataRow row in due.Rows)
            {
                ct.ThrowIfCancellationRequested();
                int id = Convert.ToInt32(row["RequestId"]);
                string tracking = row["TrackingNumber"].ToString();
                string machine = row["MachineId"] == DBNull.Value ? _machineId : row["MachineId"].ToString();
                DateTime requestedAt = Convert.ToDateTime(row["RequestedAt"]);

                LabelRequestResult outcome;
                try
                {
                    outcome = web.RequestLabelAsync(tracking, machine, requestedAt, ct).GetAwaiter().GetResult();
                }
                catch (CmcWebException ex)
                {
                    sql.ExecuteStoredProcedure("dbo.sp_LabelReq_MarkFailed", new Dictionary<string, object>
                        { { "@RequestId", id }, { "@Error", Truncate(ex.Message, 400) } });
                    throw;   // the web service is not answering properly: stop here, retry next run
                }

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
