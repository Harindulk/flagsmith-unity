using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Flagsmith.Tests
{
    /// <summary>
    /// Runs against the self-hosted server from server/ after `python3 server/seed.py`.
    /// Skipped when that seed output is missing.
    /// To run it against another server, set FLAGSMITH_TEST_ENV_KEY to a client-side key whose project has the
    /// demo flags. Leave FLAGSMITH_TEST_API_URL unset for Flagsmith Cloud.
    /// </summary>
    [Category("Integration")]
    public class LiveServerTests
    {
        JObject seed;

        [SetUp]
        public void SetUp()
        {
            var key = System.Environment.GetEnvironmentVariable("FLAGSMITH_TEST_ENV_KEY");
            if (!string.IsNullOrEmpty(key))
            {
                seed = new JObject { ["environment_key"] = key, ["api_url"] = System.Environment.GetEnvironmentVariable("FLAGSMITH_TEST_API_URL") };
                return;
            }
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../server/.seed.json"));
            if (!File.Exists(path)) Assert.Ignore("No seeded local server (run server/seed.py).");
            seed = JObject.Parse(File.ReadAllText(path));
        }

        FlagsmithClient NewClient() => new FlagsmithClient(new FlagsmithOptions
        {
            EnvironmentKey = (string)seed["environment_key"],
            ApiUrl = (string)seed["api_url"],
            RetryDelaySeconds = 0,
        }, null, new MemoryFlagCache());

        [Test]
        public async Task RealServer_ServesFlags_AndStoresExperimentEvents()
        {
            var client = NewClient();
            var id = "it-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            var flags = await client.GetIdentityFlags(id);
            Assert.AreEqual(FlagSource.Server, flags.Source);
            Assert.Greater(flags.GetValue("run_speed", 0f), 0f);

            var flag = await client.GetExperimentFlag("tutorial_style", id);
            // Once the seeded experiment has been shipped nobody is enrolled; the flag then serves the winner to everyone.
            if (flag.InExperiment) CollectionAssert.Contains(new[] { "hand", "control" }, flag.Variant);
            else CollectionAssert.Contains(new[] { "hand", "text" }, (string)flag.Value);

            client.TrackEvent("first_run_complete", id, null, new System.Collections.Generic.Dictionary<string, object> { ["synthetic"] = true });
            await client.FlushEvents();
            // 0 pending = the events API answered 2xx; a rejected batch logs an error, which fails the test.
            // (The dashboard's events counter is cached, so it isn't checked here.)
            Assert.AreEqual(0, client.PendingEvents);
        }
    }
}
