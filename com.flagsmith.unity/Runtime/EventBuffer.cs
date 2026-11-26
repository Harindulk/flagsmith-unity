using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flagsmith
{
    /// <summary>
    /// Queues Experimentation events and cuts them into batches the events API accepts:
    /// at most 2 MiB a request, nothing older than 10 days, one copy of each exposure per batch.
    /// </summary>
    public sealed class EventBuffer
    {
        public const string ExposureEvent = "$flag_exposure";
        public const int MaxBatchBytes = 2 * 1024 * 1024;
        public const long MaxAgeMs = 10L * 24 * 3600 * 1000;
        public const int MaxQueued = 10000;

        readonly List<JObject> _queue = new List<JObject>();
        readonly HashSet<string> _exposureKeys = new HashSet<string>();

        public int Count => _queue.Count;

        public void Add(JObject e)
        {
            if ((string)e["event"] == ExposureEvent)
            {
                var key = $"{e["feature_name"]}\n{e["identifier"]}\n{e["value"]}\n{e["metadata"]?["experiment_id"]}";
                if (!_exposureKeys.Add(key)) return;
            }
            // shortcut: in-memory only, so events still queued when the app is killed are lost; persist to disk if that matters.
            if (_queue.Count >= MaxQueued) _queue.RemoveAt(0);
            _queue.Add(e);
        }

        /// <summary>Removes everything queued and returns it as request bodies, oldest first.</summary>
        public List<string> TakeBatches(long nowMs)
        {
            var batches = new List<string>();
            var current = new List<string>();
            var size = 0;
            const int envelope = 14; // {"events":[ ]}
            foreach (var e in _queue)
            {
                if (nowMs - (long)e["timestamp"] > MaxAgeMs) continue;
                var json = e.ToString(Formatting.None);
                var bytes = Encoding.UTF8.GetByteCount(json) + 1;
                if (bytes + envelope > MaxBatchBytes) continue; // one event can never fit: drop it
                if (size + bytes + envelope > MaxBatchBytes)
                {
                    batches.Add(Wrap(current));
                    current.Clear();
                    size = 0;
                }
                current.Add(json);
                size += bytes;
            }
            if (current.Count > 0) batches.Add(Wrap(current));
            _queue.Clear();
            _exposureKeys.Clear();
            return batches;
        }

        /// <summary>Puts a batch that could not be delivered back at the front of the queue.</summary>
        public void Requeue(string batchBody)
        {
            var events = (JArray)JObject.Parse(batchBody)["events"];
            var restored = new List<JObject>();
            foreach (JObject e in events) restored.Add(e);
            _queue.InsertRange(0, restored);
        }

        static string Wrap(List<string> events) => "{\"events\":[" + string.Join(",", events) + "]}";
    }
}
