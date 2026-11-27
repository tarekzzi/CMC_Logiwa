using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CMC_Logiwa.Services
{
    /// <summary>One problem the backend (web VM, its database, Logiwa) has. Code is stable: one popup per new code.</summary>
    public sealed class BackendProblem
    {
        public string Code { get; set; }
        public string Message { get; set; }
        /// <summary>Local time the problem started, when known.</summary>
        public DateTime? Since { get; set; }
    }

    public sealed class BackendHealthReport
    {
        public bool Ok { get; set; }
        public DateTime? LastWebhookAt { get; set; }
        /// <summary>Set on the web VM (Settings > Web VM / Logiwa): how often this PC checks.</summary>
        public int? PcCheckMinutes { get; set; }
        public List<BackendProblem> Problems { get; } = new List<BackendProblem>();
    }

    /// <summary>Result of one check, compared with the one before.</summary>
    public sealed class BackendHealthState
    {
        public DateTime CheckedAt { get; set; }
        /// <summary>False when the web service address / API key are not set: nothing is checked, nothing is shown.</summary>
        public bool Configured { get; set; }
        public List<BackendProblem> Problems { get; set; } = new List<BackendProblem>();
        /// <summary>Problems that were not there at the previous check: the operator gets one popup for these.</summary>
        public List<BackendProblem> NewProblems { get; set; } = new List<BackendProblem>();
        /// <summary>There were problems at the previous check and there are none now.</summary>
        public bool Recovered { get; set; }
        /// <summary>When the first of the problems just solved started (for "back after ...").</summary>
        public DateTime? DownSince { get; set; }
        public bool Ok => Configured && Problems.Count == 0;
    }

    /// <summary>
    /// Asks the web VM every 5 minutes whether the backend works (GET /api/cmc/health) so the operator learns about an outage
    /// without looking at the log. The web sync asks for an early check when it starts or stops failing (RequestCheck).
    /// Not reachable at all is a problem of its own ("unreachable"); its start time is remembered here.
    /// </summary>
    public sealed class BackendHealthService
    {
        public const string Unreachable = "unreachable";
        public const string Refused = "webservice";

        private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);   // early checks are never closer than this

        // Defaults until the web VM tells its settings (and when it cannot be reached).
        private TimeSpan _interval = TimeSpan.FromMinutes(5);

        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private Task _loop;

        private Dictionary<string, BackendProblem> _previous = new Dictionary<string, BackendProblem>();
        private DateTime? _unreachableSince;
        private DateTime? _downSince;

        /// <summary>Raised on a worker thread after every check.</summary>
        public event Action<BackendHealthState> OnState;

        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null) return;
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => LoopAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_loop == null) return;
                _cts.Cancel();
                _loop = null;
            }
        }

        /// <summary>Check now instead of waiting for the next 5 minute tick.</summary>
        public void RequestCheck()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var state = await CheckAsync(ct).ConfigureAwait(false);
                    OnState?.Invoke(state);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { /* never let the loop die; the next check tries again */ }

                try
                {
                    await Task.Delay(MinGap, ct).ConfigureAwait(false);
                    await _wake.WaitAsync(_interval - MinGap, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task<BackendHealthState> CheckAsync(CancellationToken ct)
        {
            var now = DateTime.Now;
            var state = new BackendHealthState { CheckedAt = now };

            DbSettings settings = DbSettingsStore.Load();
            if (settings == null || !settings.HasWebService)
            {
                _previous.Clear();
                _unreachableSince = null;
                _downSince = null;
                return state;   // Configured = false
            }
            state.Configured = true;

            var problems = new List<BackendProblem>();
            try
            {
                using (var web = new CmcWebClient(settings))
                {
                    BackendHealthReport report = await web.GetHealthAsync(ct).ConfigureAwait(false);
                    if (report.PcCheckMinutes is int m && m >= 1 && m <= 60) _interval = TimeSpan.FromMinutes(m);

                    // Only communication problems arrive here: the web VM cannot reach its database or Logiwa. Quiet periods (no orders,
                    // no webhooks) are not problems.
                    problems.AddRange(report.Problems);
                    if (!report.Ok && problems.Count == 0)
                        problems.Add(new BackendProblem { Code = Refused, Message = "The web VM reports a problem without details." });
                }
                _unreachableSince = null;
            }
            catch (CmcWebException ex) when (ex.Unreachable)
            {
                if (_unreachableSince == null) _unreachableSince = now;
                problems.Add(new BackendProblem
                {
                    Code = Unreachable,
                    Message = "The web VM cannot be reached from this PC: new orders, labels and status changes are not arriving and finished boxes are not sent to Logiwa (they wait on this PC).",
                    Since = _unreachableSince
                });
            }
            catch (CmcWebException ex)
            {
                _unreachableSince = null;
                problems.Add(new BackendProblem { Code = Refused, Message = "The web VM answered with an error: " + ex.Message });
            }

            var current = problems.GroupBy(p => p.Code).ToDictionary(g => g.Key, g => g.First());
            state.Problems = current.Values.ToList();
            state.NewProblems = current.Values.Where(p => !_previous.ContainsKey(p.Code)).ToList();

            if (current.Count > 0)
            {
                if (_downSince == null)
                    _downSince = current.Values.Select(p => p.Since ?? now).Min();
            }
            else if (_previous.Count > 0)
            {
                state.Recovered = true;
                state.DownSince = _downSince;
                _downSince = null;
            }

            _previous = current;
            return state;
        }
    }
}
