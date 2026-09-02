using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// In-process queue for B2C interpretations (singleton).
    ///
    /// Why a queue and not a bare <c>Task.Run</c> (as CAM batches do): it gives
    /// us a single place to cap concurrency. Limits come from appsettings
    /// (<see cref="InterpretationQueueSettings"/>) so they can be tuned on the
    /// server without a rebuild; defaults are 3 app-wide and 1 per user.
    /// </summary>
    public class InterpretationJobQueue
    {
        private readonly IOptionsMonitor<InterpretationQueueSettings> _options;

        public InterpretationJobQueue(IOptionsMonitor<InterpretationQueueSettings> options)
        {
            _options = options;
        }

        /// <summary>Jobs running at once in this process (>= 1).</summary>
        public int MaxConcurrent => Math.Max(1, _options.CurrentValue.MaxConcurrent);

        /// <summary>Jobs queued or running per user (>= 1).</summary>
        public int MaxPerUser => Math.Max(1, _options.CurrentValue.MaxPerUser);

        private readonly Channel<InterpretationJob> _channel =
            Channel.CreateUnbounded<InterpretationJob>();

        // email -> jobs queued or running. Also the per-user gate.
        private readonly ConcurrentDictionary<string, int> _perUser =
            new(StringComparer.OrdinalIgnoreCase);

        public ChannelReader<InterpretationJob> Reader => _channel.Reader;

        /// <summary>
        /// True when the user already reached their per-user limit (queued or
        /// running). Checked BEFORE the credit is reserved so we never have to
        /// roll back.
        /// </summary>
        public bool IsUserBusy(string email) =>
            _perUser.TryGetValue(email, out var n) && n >= MaxPerUser;

        /// <summary>
        /// Reserves the user's slot and queues the job. Returns false when the
        /// per-user limit was hit (caller must refund the credit it reserved).
        /// </summary>
        public bool TryEnqueue(InterpretationJob job)
        {
            var limit = MaxPerUser;
            var count = _perUser.AddOrUpdate(job.UserEmail, 1, (_, n) => n + 1);
            if (count > limit)
            {
                ReleaseUser(job.UserEmail);
                return false;
            }

            if (!_channel.Writer.TryWrite(job))
            {
                ReleaseUser(job.UserEmail);
                return false;
            }
            return true;
        }

        /// <summary>Frees the user's slot. Called by the worker in a finally block.</summary>
        public void ReleaseUser(string email)
        {
            while (_perUser.TryGetValue(email, out var n))
            {
                if (n <= 1)
                {
                    if (_perUser.TryRemove(new KeyValuePair<string, int>(email, n))) return;
                }
                else if (_perUser.TryUpdate(email, n - 1, n)) return;
            }
        }

        /// <summary>Jobs queued or running right now, app-wide (admin/diagnostics).</summary>
        public int ActiveCount => _perUser.Values.Sum();

        /// <summary>Jobs still waiting for a free slot (admin/diagnostics).</summary>
        public int QueuedCount => _channel.Reader.Count;
    }
}
