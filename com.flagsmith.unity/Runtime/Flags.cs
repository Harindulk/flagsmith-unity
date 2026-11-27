using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Flagsmith
{
    public enum FlagSource { Server, Cache, Defaults }

    public sealed class Flag
    {
        public string Name { get; internal set; }
        public bool Enabled { get; internal set; }
        /// <summary>The remote value as text (numbers use the invariant culture), or null.</summary>
        public string Value { get; internal set; }
        /// <summary>Key of the multivariate option served to the identity, e.g. "control".</summary>
        public string Variant { get; internal set; }
        public int? ExperimentId { get; internal set; }
        public string ExperimentName { get; internal set; }
        public bool InExperiment { get; internal set; }

        internal static Flag FromJson(JToken t)
        {
            var experiment = t["metadata"] is JObject meta ? meta["experiment"] : null; // "metadata" may be null or missing
            return new Flag
            {
                Name = (string)t["feature"]?["name"],
                Enabled = (bool?)t["enabled"] ?? false,
                Value = AsText(t["feature_state_value"]),
                Variant = (string)t["variant"],
                ExperimentId = experiment?.Type == JTokenType.Object ? (int?)experiment["id"] : null,
                ExperimentName = experiment?.Type == JTokenType.Object ? (string)experiment["name"] : null,
                InExperiment = experiment?.Type == JTokenType.Object && ((bool?)experiment["in_experiment"] ?? false),
            };
        }

        internal static string AsText(JToken v)
        {
            if (v == null || v.Type == JTokenType.Null) return null;
            if (v.Type == JTokenType.Boolean) return (bool)v ? "true" : "false";
            if (v.Type == JTokenType.Float) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(((JValue)v).Value, CultureInfo.InvariantCulture);
        }
    }

    public sealed class Flags
    {
        readonly Dictionary<string, Flag> _byName = new Dictionary<string, Flag>();

        public FlagSource Source { get; }
        public IReadOnlyCollection<Flag> All => _byName.Values;

        internal Flags(IEnumerable<Flag> flags, FlagSource source)
        {
            Source = source;
            foreach (var f in flags)
                if (f.Name != null) _byName[f.Name] = f;
        }

        internal static Flags Empty => new Flags(Array.Empty<Flag>(), FlagSource.Defaults);

        public Flag Get(string name) => name != null && _byName.TryGetValue(name, out var f) ? f : null;

        public bool IsEnabled(string name) => Get(name)?.Enabled ?? false;

        /// <summary>The flag's value converted to T, or <paramref name="fallback"/> when it is missing, null or unparsable.</summary>
        public T GetValue<T>(string name, T fallback)
        {
            var raw = Get(name)?.Value;
            if (raw == null) return fallback;
            try
            {
                var target = typeof(T);
                if (target == typeof(string)) return (T)(object)raw;
                if (target == typeof(bool)) return (T)(object)bool.Parse(raw);
                return (T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                return fallback;
            }
        }
    }
}
