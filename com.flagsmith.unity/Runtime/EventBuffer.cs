using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flagsmith
{
    /// <summary>
    /// Queues Experimentation events and cuts them into batches the events API accepts:
    /// at most 2 MiB a request, nothing older than 10 days, one copy of each exposure.
    /// Taken batches stay "in flight" until acknowledged, so saving mid-flush or a failed send loses nothing.
    /// </summary>
    public sealed class EventBuffer
    {
        public const string ExposureEvent = "$flag_exposure";
        public const int MaxBatchBytes = 2 * 1024 * 1024;
        public const long MaxAgeMs = 10L * 24 * 3600 * 1000;
        public const int MaxQueued = 10000;

        readonly List<JObject> _queue = new List<JObject>();
        readonly List<List<JObject>> _inFlight = new List<List<JObject>>();
        readonly HashSet<string> _exposureKeys = new HashSet<string>(); // queued or in flight

        /// <summary>Events not yet delivered, including those being sent.</summary>
        public int Count => _queue.Count + _inFlight.Sum(b => b?.Count ?? 0);

        public void Add(JObject e)
        {
            if (IsExposure(e) && !_exposureKeys.Add(ExposureKey(e))) return;
            if (_queue.Count >= MaxQueued)
            {
                Forget(_queue[0]);
                _queue.RemoveAt(0);
            }
            _queue.Add(e);
        }

        /// <summary>
        /// Moves everything queued in flight and returns it as request bodies, oldest first.
        /// Call <see cref="Acknowledge"/> for each batch the server took (or rejected for good), then <see cref="ReturnUnsent"/>.
        /// </summary>
        public List<string> TakeBatches(long nowMs)
        {
            var bodies = new List<string>();
            var current = new List<JObject>();
            var json = new List<string>();
            var size = 0;
            const int envelope = 14; // {"events":[ ]}
            foreach (var e in _queue)
            {
                var text = e.ToString(Formatting.None);
                var bytes = Encoding.UTF8.GetByteCount(text) + 1;
                // Too old for the API, or too big to ever fit: drop it.
                if (nowMs - ((long?)e["timestamp"] ?? 0) > MaxAgeMs || bytes + envelope > MaxBatchBytes) { Forget(e); continue; }
                if (size + bytes + envelope > MaxBatchBytes)
                {
                    bodies.Add(Wrap(json));
                    _inFlight.Add(current);
                    current = new List<JObject>();
                    json = new List<string>();
                    size = 0;
                }
                current.Add(e);
                json.Add(text);
                size += bytes;
            }
            if (current.Count > 0)
            {
                bodies.Add(Wrap(json));
                _inFlight.Add(current);
            }
            _queue.Clear();
            return bodies;
        }

        /// <summary>The batch at <paramref name="index"/> (from the last TakeBatches) needs no further sending.</summary>
        public void Acknowledge(int index)
        {
            if (index < 0 || index >= _inFlight.Count || _inFlight[index] == null) return;
            foreach (var e in _inFlight[index]) Forget(e);
            _inFlight[index] = null;
        }

        /// <summary>Puts every unacknowledged batch back at the front of the queue, in order.</summary>
        public void ReturnUnsent()
        {
            _queue.InsertRange(0, _inFlight.Where(b => b != null).SelectMany(b => b));
            _inFlight.Clear();
        }

        /// <summary>Everything not yet delivered (in flight first) as JSON, for saving across app restarts.</summary>
        public string Save() =>
            new JArray(_inFlight.Where(b => b != null).SelectMany(b => b).Concat(_queue)).ToString(Formatting.None);

        /// <summary>Adds events saved by <see cref="Save"/>. Ignores missing or unreadable data.</summary>
        public void Load(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                if (Json.Parse(json) is JArray events)
                    foreach (var e in events)
                        if (e is JObject o) Add(o);
            }
            catch (JsonReaderException) { }
        }

        void Forget(JObject e)
        {
            if (IsExposure(e)) _exposureKeys.Remove(ExposureKey(e));
        }

        static bool IsExposure(JObject e) => (string)e["event"] == ExposureEvent;
        static string ExposureKey(JObject e) => $"{e["feature_name"]}\n{e["identifier"]}\n{e["value"]}\n{e["metadata"]?["experiment_id"]}";

        static string Wrap(List<string> events) => "{\"events\":[" + string.Join(",", events) + "]}";
    }
}
