using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Bounded, thread-safe record of the tools/call invocations whose HTTP
    /// waiter timed out while the tool was ALREADY running on the main
    /// thread (design note 2026-09-09-jobs-and-destructive-confirm section
    /// 1; the pattern is UnityMCP's syncWaitMs + job_status). Before this
    /// ledger existed such a call's result was simply lost: the agent was
    /// told "still running", and the only way to learn what a 9-minute
    /// menu had done was to grep Editor.log or re-inspect the scene.
    ///
    /// Pure C# (no Unity API) so it is unit-testable and safe to read from
    /// an HTTP worker thread -- which is exactly what uap_job_status does,
    /// bypassing the main thread that may still be blocked by the very job
    /// being asked about. Lifetime is the Editor session: a domain reload
    /// resets the static <see cref="Shared"/> instance together with the
    /// dispatcher whose items it tracks.
    /// </summary>
    public sealed class UapJobLedger
    {
        /// <summary>How many jobs are kept. Oldest COMPLETED jobs are evicted first; a running job is never evicted.</summary>
        public const int Capacity = 32;

        /// <summary>The Editor-session ledger the production dispatcher and uap_job_status share.</summary>
        public static readonly UapJobLedger Shared = new UapJobLedger();

        private readonly object _lock = new object();
        private readonly List<UapJob> _jobs = new List<UapJob>();
        private readonly Dictionary<string, UapJob> _byId = new Dictionary<string, UapJob>(StringComparer.Ordinal);
        private readonly string _prefix;
        private int _nextSerial;

        public UapJobLedger()
        {
            // A per-ledger prefix keeps ids from repeating across domain
            // reloads (each reload makes a fresh ledger), so a stale id from
            // before a reload never resolves to a NEW job that happens to
            // have the same serial.
            _prefix = Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>Issues the next job id (e.g. "job-3f9a-7"). Cheap; every dispatched call takes one whether or not it is ever registered.</summary>
        public string NewId()
        {
            int serial = System.Threading.Interlocked.Increment(ref _nextSerial);
            return "job-" + _prefix + "-" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Makes <paramref name="job"/> visible to <see cref="Find"/>/
        /// <see cref="List"/>. Registering the same job twice is a no-op.
        /// Evicts the oldest completed job(s) once over
        /// <see cref="Capacity"/>; if every job is still running the
        /// ledger grows past the cap rather than forgetting a live one.
        /// </summary>
        public void Register(UapJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException("job");
            }
            lock (_lock)
            {
                if (_byId.ContainsKey(job.Id))
                {
                    return;
                }
                _byId[job.Id] = job;
                _jobs.Add(job);
                while (_jobs.Count > Capacity)
                {
                    int victim = -1;
                    for (int i = 0; i < _jobs.Count; i++)
                    {
                        if (_jobs[i].IsCompleted)
                        {
                            victim = i;
                            break;
                        }
                    }
                    if (victim < 0)
                    {
                        break;
                    }
                    _byId.Remove(_jobs[victim].Id);
                    _jobs.RemoveAt(victim);
                }
            }
        }

        /// <summary>The job with this id, or null when unknown (never registered, evicted, or from before a domain reload).</summary>
        public UapJob Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            lock (_lock)
            {
                UapJob job;
                return _byId.TryGetValue(id, out job) ? job : null;
            }
        }

        /// <summary>Snapshot of every registered job, newest first.</summary>
        public List<UapJob> List()
        {
            lock (_lock)
            {
                var snapshot = new List<UapJob>(_jobs.Count);
                for (int i = _jobs.Count - 1; i >= 0; i--)
                {
                    snapshot.Add(_jobs[i]);
                }
                return snapshot;
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _jobs.Count;
                }
            }
        }

        /// <summary>Test seam: forgets every job (a domain reload does the same to <see cref="Shared"/>).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _jobs.Clear();
                _byId.Clear();
            }
        }
    }
}
