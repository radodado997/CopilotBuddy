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
                // ObjectManager's caches are read from the bot thread, the UI thread and
                // plugin threads; the unsynchronised Stopwatch + field pair could hand a
                // reader a value produced for a different frame.
                lock (_sync)
                {
                    if (!_stopwatch.IsRunning || _stopwatch.Elapsed > _duration)
                    {
                        _cachedValue = _producer();
                        _stopwatch.Restart();
                    }
                    return _cachedValue;
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
            }
        }
    }
}