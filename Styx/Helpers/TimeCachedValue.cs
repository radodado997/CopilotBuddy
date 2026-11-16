using System;
using System.Diagnostics;

namespace Styx.Helpers
{
    /// <summary>
    /// Simple wrapper that caches the result of a producer delegate for a fixed amount of time.
    /// Ported directly from HonorBuddy 3.3.5a.
    /// </summary>
    public class TimeCachedValue<T>
    {
        private readonly TimeSpan _duration;
        private readonly Stopwatch _stopwatch;
        private readonly Func<T> _producer;
        private readonly object _sync = new object();
        private T _cachedValue;
        // Incremented by Reset().  A producer that started before a Reset must not
        // publish its (pre-reset) result afterwards.
        private long _generation;

        public TimeCachedValue(TimeSpan duration, Func<T> producer)
        {
            if (producer == null)
                throw new ArgumentNullException(nameof(producer));
            _producer = producer;
            _duration = duration;
            _stopwatch = new Stopwatch();
        }

        /// <summary>
        /// Gets the cached value, regenerating it if the interval has elapsed.
        /// </summary>
        public T Value
        {
            get
            {
                long generation;
                lock (_sync)
                {
                    if (_stopwatch.IsRunning && _stopwatch.Elapsed <= _duration)
                        return _cachedValue;

                    generation = _generation;
                }

                // The producer runs outside the lock.  It usually walks ObjectManager,
                // which flushes these caches from inside its own lock (Update() ->
                // ResetCaches()), so calling it while holding _sync could deadlock.
                T produced = _producer();

                lock (_sync)
                {
                    // Publish only if no Reset() happened while we were producing.
                    if (generation == _generation)
                    {
                        _cachedValue = produced;
                        _stopwatch.Restart();
                    }
                    return produced;
                }
            }
        }

        public static implicit operator T(TimeCachedValue<T> tcv)
        {
            return tcv.Value;
        }

        /// <summary>
        /// Clears the cached data so that the next access will call the producer again.
        /// HonorBuddy recreated its cache objects on updates; we provide Reset for convenience.
        /// </summary>
        public void Reset()
        {
            lock (_sync)
            {
                _stopwatch.Reset();
                _cachedValue = default!;
                _generation++;
            }
        }
    }
}