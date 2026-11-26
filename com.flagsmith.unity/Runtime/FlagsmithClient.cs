using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Flagsmith
{
    [Serializable]
    public sealed class FlagsmithOptions
    {
        [Tooltip("Client-side environment key. Safe to ship in a build.")]
        public string EnvironmentKey;
        [Tooltip("Flagsmith Cloud: https://edge.api.flagsmith.com/api/v1/  Self-hosted: http://your-host:8000/api/v1/")]
        public string ApiUrl = "https://edge.api.flagsmith.com/api/v1/";
        [Tooltip("Where Experimentation events go. Empty = Flagsmith Cloud's events API, or <ApiUrl>events/ when self-hosted.")]
        public string EventsUrl;
        public float RetryDelaySeconds = 1f;
    }

    /// <summary>
    /// Flagsmith client for Unity: environment and identity flags, an offline cache, and Experimentation
    /// (exposures and events). Every call is async and runs on Unity's main thread, so it works on WebGL.
    /// </summary>
    public sealed class FlagsmithClient
    {
        public const string SdkVersion = "flagsmith-unity/0.1.0";
        const string CloudApi = "https://edge.api.flagsmith.com/api/v1/";
        const string CloudEvents = "https://events.api.flagsmith.com/v1/events";

        readonly FlagsmithOptions _options;
        readonly ITransport _transport;
        readonly IFlagCache _cache;
        readonly Func<long> _nowMs;
        readonly EventBuffer _events = new EventBuffer();
        readonly string _apiUrl;
        readonly string _eventsUrl;
        bool _flushing;

        public FlagsmithClient(FlagsmithOptions options, ITransport transport = null, IFlagCache cache = null, Func<long> nowMs = null)
        {
            if (string.IsNullOrEmpty(options?.EnvironmentKey))
                throw new ArgumentException("An environment key is required.", nameof(options));
            _options = options;
            _transport = transport ?? new UnityWebRequestTransport();
            _cache = cache ?? new FileFlagCache();
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _apiUrl = string.IsNullOrEmpty(options.ApiUrl) ? CloudApi : options.ApiUrl.TrimEnd('/') + "/";
            _eventsUrl = !string.IsNullOrEmpty(options.EventsUrl) ? options.EventsUrl
                : _apiUrl == CloudApi ? CloudEvents : _apiUrl + "events/";
        }

        public int PendingEvents => _events.Count;

        public async Task<Flags> GetEnvironmentFlags()
        {
            var json = await Fetch("GET", _apiUrl + "flags/", null, "env");
            return json == null ? Flags.Empty : Parse(JToken.Parse(json.Value.body), json.Value.source);
        }

        public async Task<Flags> GetIdentityFlags(string identifier, IDictionary<string, object> traits = null)
        {
            if (string.IsNullOrEmpty(identifier)) throw new ArgumentException("An identifier is required.", nameof(identifier));
            var body = new JObject
            {
                ["identifier"] = identifier,
                ["traits"] = new JArray((traits ?? new Dictionary<string, object>())
                    .Select(t => new JObject { ["trait_key"] = t.Key, ["trait_value"] = t.Value == null ? null : JToken.FromObject(t.Value) })),
            };
            var json = await Fetch("POST", _apiUrl + "identities/", body.ToString(Formatting.None), "id:" + identifier);
            return json == null ? Flags.Empty : Parse(JToken.Parse(json.Value.body)["flags"], json.Value.source);
        }

        /// <summary>
        /// Gets one flag for an identity and, if the identity is enrolled in a running experiment on it,
        /// records a "$flag_exposure" so the experiment can count who saw which variant.
        /// </summary>
        public async Task<Flag> GetExperimentFlag(string featureName, string identifier, IDictionary<string, object> traits = null)
        {
            var flag = (await GetIdentityFlags(identifier, traits)).Get(featureName);
            TrackExposure(flag, identifier);
            return flag;
        }

        /// <summary>Records a "$flag_exposure" for a flag you already fetched, if the identity is enrolled in its experiment.</summary>
        public void TrackExposure(Flag flag, string identifier)
        {
            if (flag != null && flag.Enabled && flag.InExperiment && flag.Variant != null && flag.ExperimentId != null)
                TrackExposureEvent(flag.Name, identifier, flag.Variant, flag.ExperimentId.Value);
        }

        /// <summary>Records an application event, such as a conversion, for Experimentation metrics.</summary>
        public void TrackEvent(string eventName, string identifier = null, object value = null, IDictionary<string, object> metadata = null)
        {
            if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("The event name must not be empty.", nameof(eventName));
            if (eventName.StartsWith("$")) throw new ArgumentException("Event names starting with \"$\" are reserved.", nameof(eventName));
            _events.Add(MakeEvent(eventName, null, identifier, value, metadata));
        }

        public void TrackExposureEvent(string featureName, string identifier, string variant, int experimentId)
        {
            if (string.IsNullOrEmpty(identifier)) return; // an exposure without an identity can't be matched to conversions
            _events.Add(MakeEvent(EventBuffer.ExposureEvent, featureName, identifier, variant,
                new Dictionary<string, object> { ["experiment_id"] = experimentId }));
        }

        /// <summary>Sends every queued event. Batches that fail to reach the server stay queued for the next flush.</summary>
        public async Task FlushEvents()
        {
            if (_flushing || _events.Count == 0) return;
            _flushing = true;
            try
            {
                var batches = _events.TakeBatches(_nowMs());
                for (var i = 0; i < batches.Count; i++)
                {
                    var res = await Send("POST", _eventsUrl, batches[i]);
                    if (res.IsSuccess) continue;
                    if (res.IsRetryable)
                    {
                        for (var j = batches.Count - 1; j >= i; j--) _events.Requeue(batches[j]);
                        Debug.LogWarning($"[Flagsmith] Events not sent ({res.Error ?? "HTTP " + res.Status}); will retry.");
                        return;
                    }
                    Debug.LogError($"[Flagsmith] Events API rejected a batch (HTTP {res.Status}): {res.Body}");
                }
            }
            finally { _flushing = false; }
        }

        JObject MakeEvent(string name, string feature, string identifier, object value, IDictionary<string, object> metadata)
        {
            var meta = new JObject();
            if (metadata != null)
                foreach (var m in metadata) meta[m.Key] = m.Value == null ? null : JToken.FromObject(m.Value);
            meta["sdk_version"] = SdkVersion;
            return new JObject
            {
                ["event"] = name,
                ["feature_name"] = feature,
                ["identifier"] = identifier,
                ["value"] = value == null ? null : value is bool b ? (b ? "true" : "false")
                    : value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString(),
                ["metadata"] = meta,
                ["timestamp"] = _nowMs(),
            };
        }

        static Flags Parse(JToken flags, FlagSource source) =>
            new Flags(flags is JArray a ? a.Select(Flag.FromJson) : Enumerable.Empty<Flag>(), source);

        async Task<(string body, FlagSource source)?> Fetch(string method, string url, string body, string cacheKey)
        {
            var key = _options.EnvironmentKey + "|" + cacheKey;
            var res = await Send(method, url, body);
            if (res.IsSuccess && res.Body != null)
            {
                _cache.Write(key, res.Body);
                return (res.Body, FlagSource.Server);
            }
            Debug.LogWarning($"[Flagsmith] {method} {url} failed ({res.Error ?? "HTTP " + res.Status}); using cached or default values.");
            var cached = _cache.Read(key);
            return cached == null ? ((string, FlagSource)?)null : (cached, FlagSource.Cache);
        }

        async Task<HttpResponse> Send(string method, string url, string body)
        {
            var request = new HttpRequest { Method = method, Url = url, Body = body };
            request.Headers["X-Environment-Key"] = _options.EnvironmentKey;
            request.Headers["Flagsmith-SDK-User-Agent"] = SdkVersion;
            if (body != null) request.Headers["Content-Type"] = "application/json";

            var res = await _transport.Send(request);
            if (!res.IsRetryable) return res;
            if (_options.RetryDelaySeconds > 0)
                await Awaitable.WaitForSecondsAsync(_options.RetryDelaySeconds);
            return await _transport.Send(request);
        }
    }
}
