using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Thread-safe line queue between the process stdout/stderr reader
    /// threads (producers) and the editor main thread (consumer). The
    /// consumer drains with a per-frame budget so a burst of CLI output
    /// cannot stall the editor UI.
    /// </summary>
    public sealed class LineChannel
    {
        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();

        /// <summary>Number of lines currently buffered (approximate).</summary>
        public int Count
        {
            get { return _queue.Count; }
        }

        /// <summary>Enqueues one line. Null lines are ignored.</summary>
        public void Enqueue(string line)
        {
            if (line != null)
            {
                _queue.Enqueue(line);
            }
        }

        /// <summary>Dequeues one line, or returns false when empty.</summary>
        public bool TryDequeue(out string line)
        {
            return _queue.TryDequeue(out line);
        }

        /// <summary>
        /// Dequeues up to <paramref name="maxLines"/> lines or until
        /// <paramref name="maxMillis"/> elapses, whichever comes first, and
        /// appends them to <paramref name="destination"/>. Returns the number
        /// of lines dequeued. maxLines &lt;= 0 means unlimited count (budget
        /// only); maxMillis &lt;= 0 means no time budget.
        /// </summary>
        public int TryDequeueBatch(List<string> destination, int maxLines, double maxMillis)
        {
            if (destination == null)
            {
                return 0;
            }
            int dequeued = 0;
            Stopwatch watch = maxMillis > 0 ? Stopwatch.StartNew() : null;
            string line;
            while ((maxLines <= 0 || dequeued < maxLines) && _queue.TryDequeue(out line))
            {
                destination.Add(line);
                dequeued++;
                if (watch != null && watch.Elapsed.TotalMilliseconds >= maxMillis)
                {
                    break;
                }
            }
            return dequeued;
        }

        /// <summary>Discards all buffered lines.</summary>
        public void Clear()
        {
            string ignored;
            while (_queue.TryDequeue(out ignored))
            {
            }
        }
    }
}
