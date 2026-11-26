using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Flagsmith
{
    /// <summary>
    /// Drop-in component: fetches flags for one identity, re-fetches them every few seconds (Live Tuning),
    /// and sends Experimentation events on a timer and when the app is paused or closed.
    /// </summary>
    public sealed class FlagsmithBehaviour : MonoBehaviour
    {
        [SerializeField] FlagsmithOptions options = new FlagsmithOptions();
        [Tooltip("Who the flags are for. Empty = a random ID kept in PlayerPrefs, so a player keeps their experiment variant.")]
        [SerializeField] string identifier;
        [Tooltip("Seconds between flag refreshes. 0 = fetch once.")]
        [SerializeField] float refreshSeconds = 2f;
        [Tooltip("Seconds between event uploads.")]
        [SerializeField] float flushSeconds = 10f;

        public FlagsmithClient Client { get; private set; }
        public string Identifier { get; private set; }
        public Flags Flags { get; private set; } = Flags.Empty;
        public IDictionary<string, object> Traits { get; } = new Dictionary<string, object>();

        /// <summary>Raised on the main thread after the first fetch and whenever a flag's state or value changes.</summary>
        public event Action<Flags> FlagsChanged;

        string _signature;

        /// <summary>Call right after AddComponent when you configure from code instead of the Inspector.</summary>
        public void Init(FlagsmithOptions opts, string id = null, float refresh = 2f)
        {
            options = opts;
            identifier = id;
            refreshSeconds = refresh;
            StartLoops();
        }

        void Start()
        {
            if (Client == null && !string.IsNullOrEmpty(options?.EnvironmentKey)) StartLoops();
        }

        void StartLoops()
        {
            if (Client != null) return;
            Client = new FlagsmithClient(options);
            Identifier = string.IsNullOrEmpty(identifier) ? PersistentId() : identifier;
            RefreshLoop();
            FlushLoop();
        }

        /// <summary>The flag, recording an exposure if this identity is enrolled in an experiment on it.</summary>
        public Flag GetExperimentFlag(string featureName)
        {
            var flag = Flags.Get(featureName);
            Client?.TrackExposure(flag, Identifier);
            return flag;
        }

        public void TrackEvent(string eventName, object value = null, IDictionary<string, object> metadata = null) =>
            Client?.TrackEvent(eventName, Identifier, value, metadata);

        public async Task Refresh()
        {
            var flags = await Client.GetIdentityFlags(Identifier, Traits);
            if (this == null) return; // destroyed while waiting
            // Offline with no cache gives empty defaults; keep the last good flags instead.
            if (flags.Source == FlagSource.Defaults && Flags.All.Count > 0) return;
            Flags = flags;
            var sig = string.Join("\n", flags.All.OrderBy(f => f.Name).Select(f => $"{f.Name}|{f.Enabled}|{f.Value}|{f.Variant}"));
            if (sig == _signature) return;
            _signature = sig;
            FlagsChanged?.Invoke(flags);
        }

        async void RefreshLoop()
        {
            try
            {
                do
                {
                    await Refresh();
                    if (refreshSeconds <= 0) return;
                    await WaitRealtime(refreshSeconds);
                } while (true);
            }
            catch (OperationCanceledException) { }
        }

        async void FlushLoop()
        {
            try
            {
                while (true)
                {
                    await WaitRealtime(Mathf.Max(1f, flushSeconds));
                    await Client.FlushEvents();
                }
            }
            catch (OperationCanceledException) { }
        }

        // Real time, so flags keep refreshing while a game is paused with Time.timeScale = 0.
        async Awaitable WaitRealtime(float seconds)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) await Awaitable.NextFrameAsync(destroyCancellationToken);
        }

        // shortcut: a WebGL tab closed between flushes loses its last events (no quit callback in browsers); a pagehide jslib with fetch keepalive would fix it.
        // Mobile apps are often killed after pausing, and a browser tab after losing focus: send what we have.
        void OnApplicationPause(bool paused) { if (paused) _ = Client?.FlushEvents(); }
        void OnApplicationFocus(bool focused) { if (!focused) _ = Client?.FlushEvents(); }
        void OnApplicationQuit() => _ = Client?.FlushEvents();

        static string PersistentId()
        {
            const string key = "flagsmith.identifier";
            var id = PlayerPrefs.GetString(key, "");
            if (id.Length == 0)
            {
                id = "player-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                PlayerPrefs.SetString(key, id);
                PlayerPrefs.Save();
            }
            return id;
        }
    }
}
