using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flagsmith.Tests
{
    class FakeTransport : ITransport
    {
        public readonly List<HttpRequest> Requests = new List<HttpRequest>();
        public readonly Queue<HttpResponse> Responses = new Queue<HttpResponse>();
        public HttpResponse Default = new HttpResponse(202, "{}", null);

        public Task<HttpResponse> Send(HttpRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Default);
        }

        public IEnumerable<HttpRequest> EventPosts => Requests.Where(r => r.Url.EndsWith("/events/"));
        public JArray EventsIn(HttpRequest r) => (JArray)JObject.Parse(r.Body)["events"];
    }

    public class FlagsmithClientTests
    {
        const string Key = "env-key";
        const string IdentityJson = @"{""identifier"":""p1"",""flags"":[
            {""feature"":{""name"":""run_speed""},""enabled"":true,""feature_state_value"":7.5},
            {""feature"":{""name"":""lives""},""enabled"":true,""feature_state_value"":3},
            {""feature"":{""name"":""double_jump""},""enabled"":false,""feature_state_value"":null},
            {""feature"":{""name"":""tutorial_style""},""enabled"":true,""feature_state_value"":""hand"",""variant"":""hand"",
             ""metadata"":{""experiment"":{""id"":4,""name"":""Tutorial"",""in_experiment"":true}}},
            {""feature"":{""name"":""shop_layout""},""enabled"":true,""feature_state_value"":""grid"",""variant"":""control"",
             ""metadata"":{""experiment"":{""id"":5,""name"":""Shop"",""in_experiment"":false}}}]}";

        FakeTransport transport;
        MemoryFlagCache cache;
        long now;
        FlagsmithClient client;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeTransport();
            cache = new MemoryFlagCache();
            now = 1_800_000_000_000;
            client = new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = Key, ApiUrl = "http://local:8000/api/v1/", RetryDelaySeconds = 0 },
                transport, cache, () => now);
        }

        HttpResponse Ok(string body) => new HttpResponse(200, body, null);

        [Test]
        public async Task IdentityFlags_ParseValuesAndSendKeyAndTraits()
        {
            transport.Responses.Enqueue(Ok(IdentityJson));
            var flags = await client.GetIdentityFlags("p1", new Dictionary<string, object> { ["level"] = 3 });

            var req = transport.Requests.Single();
            Assert.AreEqual("POST", req.Method);
            Assert.AreEqual("http://local:8000/api/v1/identities/", req.Url);
            Assert.AreEqual(Key, req.Headers["X-Environment-Key"]);
            var body = JObject.Parse(req.Body);
            Assert.AreEqual("p1", (string)body["identifier"]);
            Assert.AreEqual("level", (string)body["traits"][0]["trait_key"]);
            Assert.AreEqual(3, (int)body["traits"][0]["trait_value"]);

            Assert.AreEqual(7.5f, flags.GetValue("run_speed", 1f));
            Assert.AreEqual(3, flags.GetValue("lives", 0));
            Assert.AreEqual("7.5", flags.Get("run_speed").Value);
            Assert.IsFalse(flags.IsEnabled("double_jump"));
            Assert.IsFalse(flags.IsEnabled("missing"));
            Assert.AreEqual(42, flags.GetValue("missing", 42));
            Assert.AreEqual(FlagSource.Server, flags.Source);
        }

        [Test]
        public async Task NullMetadata_StillParses()
        {
            transport.Responses.Enqueue(Ok(@"[{""feature"":{""name"":""a""},""enabled"":true,""feature_state_value"":""x"",""metadata"":null}]"));
            var flags = await client.GetEnvironmentFlags();
            Assert.AreEqual(FlagSource.Server, flags.Source);
            Assert.AreEqual("x", flags.GetValue("a", ""));
            Assert.IsFalse(flags.Get("a").InExperiment);
        }

        [Test]
        public async Task EnvironmentFlags_UseGetOnFlagsEndpoint()
        {
            transport.Responses.Enqueue(Ok(@"[{""feature"":{""name"":""a""},""enabled"":true,""feature_state_value"":""x""}]"));
            var flags = await client.GetEnvironmentFlags();
            Assert.AreEqual("GET", transport.Requests[0].Method);
            Assert.AreEqual("http://local:8000/api/v1/flags/", transport.Requests[0].Url);
            Assert.AreEqual("x", flags.GetValue("a", ""));
        }

        [Test]
        public async Task ExperimentFlag_RecordsOneExposurePerBatch()
        {
            transport.Default = Ok(IdentityJson);
            var flag = await client.GetExperimentFlag("tutorial_style", "p1");
            await client.GetExperimentFlag("tutorial_style", "p1"); // same exposure again: deduped
            Assert.AreEqual("hand", flag.Value);
            Assert.AreEqual(4, flag.ExperimentId);

            transport.Default = new HttpResponse(202, "{}", null);
            await client.FlushEvents();
            var events = transport.EventsIn(transport.EventPosts.Single());
            Assert.AreEqual(1, events.Count);
            var e = (JObject)events[0];
            Assert.AreEqual("$flag_exposure", (string)e["event"]);
            Assert.AreEqual("tutorial_style", (string)e["feature_name"]);
            Assert.AreEqual("p1", (string)e["identifier"]);
            Assert.AreEqual("hand", (string)e["value"]);
            Assert.AreEqual(4, (int)e["metadata"]["experiment_id"]);
            Assert.AreEqual(now, (long)e["timestamp"]);
            Assert.AreEqual(Key, transport.EventPosts.Single().Headers["X-Environment-Key"]);
        }

        [Test]
        public async Task ExperimentFlag_NoExposureWhenNotEnrolledOrDisabled()
        {
            transport.Default = Ok(IdentityJson);
            await client.GetExperimentFlag("shop_layout", "p1");  // in_experiment false
            await client.GetExperimentFlag("double_jump", "p1");  // disabled, no experiment
            await client.GetExperimentFlag("run_speed", "p1");    // no experiment
            await client.FlushEvents();
            Assert.IsEmpty(transport.EventPosts);
        }

        [Test]
        public void TrackEvent_RejectsReservedAndEmptyNames()
        {
            Assert.Throws<System.ArgumentException>(() => client.TrackEvent("$flag_exposure"));
            Assert.Throws<System.ArgumentException>(() => client.TrackEvent(""));
        }

        [Test]
        public async Task Flush_PostsToEventsEndpoint_AndKeepsEventsWhenOffline()
        {
            client.TrackEvent("first_run_complete", "p1", 12.5, new Dictionary<string, object> { ["level"] = 1 });
            transport.Responses.Enqueue(new HttpResponse(0, null, "Cannot connect"));
            transport.Responses.Enqueue(new HttpResponse(0, null, "Cannot connect"));
            await client.FlushEvents();
            Assert.AreEqual(1, client.PendingEvents, "a network failure keeps the batch for the next flush");

            await client.FlushEvents();
            Assert.AreEqual(0, client.PendingEvents);
            var post = transport.EventPosts.Last();
            Assert.AreEqual("http://local:8000/api/v1/events/", post.Url);
            var e = transport.EventsIn(post)[0];
            Assert.AreEqual("first_run_complete", (string)e["event"]);
            Assert.AreEqual("12.5", (string)e["value"]);
            Assert.AreEqual(1, (int)e["metadata"]["level"]);
            Assert.IsNotNull(e["metadata"]["sdk_version"]);
        }

        [Test]
        public async Task Flush_DropsBatchTheServerRejects()
        {
            client.TrackEvent("x", "p1");
            transport.Responses.Enqueue(new HttpResponse(400, "{}", null));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("rejected a batch"));
            await client.FlushEvents();
            Assert.AreEqual(0, client.PendingEvents);
        }

        [Test]
        public async Task Flush_DropsEventsOlderThanTheServerAccepts()
        {
            client.TrackEvent("stale", "p1");
            now += 11L * 24 * 3600 * 1000; // 11 days offline; the server rejects > 10 days
            client.TrackEvent("fresh", "p1");
            await client.FlushEvents();
            var events = transport.EventsIn(transport.EventPosts.Single());
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("fresh", (string)events[0]["event"]);
        }

        [Test]
        public async Task Flush_SplitsBatchesOverTheSizeCap()
        {
            var big = new string('x', 300_000);
            for (var i = 0; i < 10; i++) client.TrackEvent("big", "p1", null, new Dictionary<string, object> { ["pad"] = big });
            await client.FlushEvents();
            Assert.Greater(transport.EventPosts.Count(), 1);
            Assert.IsTrue(transport.EventPosts.All(p => System.Text.Encoding.UTF8.GetByteCount(p.Body) <= EventBuffer.MaxBatchBytes));
            Assert.AreEqual(10, transport.EventPosts.Sum(p => transport.EventsIn(p).Count));
        }

        [Test]
        public async Task Offline_FallsBackToCache_ThenDefaults()
        {
            transport.Responses.Enqueue(Ok(IdentityJson));
            await client.GetIdentityFlags("p1");

            transport.Responses.Enqueue(new HttpResponse(0, null, "offline"));
            transport.Responses.Enqueue(new HttpResponse(0, null, "offline"));
            var cached = await client.GetIdentityFlags("p1");
            Assert.AreEqual(FlagSource.Cache, cached.Source);
            Assert.AreEqual(7.5f, cached.GetValue("run_speed", 1f));

            transport.Responses.Enqueue(new HttpResponse(0, null, "offline"));
            transport.Responses.Enqueue(new HttpResponse(0, null, "offline"));
            var none = await client.GetIdentityFlags("someone-else");
            Assert.AreEqual(FlagSource.Defaults, none.Source);
            Assert.AreEqual(1f, none.GetValue("run_speed", 1f));
        }

        [Test]
        public async Task ServerError_RetriesOnce()
        {
            transport.Responses.Enqueue(new HttpResponse(503, "", null));
            transport.Responses.Enqueue(Ok(IdentityJson));
            var flags = await client.GetIdentityFlags("p1");
            Assert.AreEqual(2, transport.Requests.Count);
            Assert.AreEqual(FlagSource.Server, flags.Source);
        }
        [Test]
        public async Task NonJsonSuccess_IsNotCached_AndFallsBack()
        {
            transport.Responses.Enqueue(Ok(IdentityJson));
            await client.GetIdentityFlags("p1");
            transport.Responses.Enqueue(Ok("<html>Wi-Fi login</html>")); // captive portal
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("failed"));
            var flags = await client.GetIdentityFlags("p1");
            Assert.AreEqual(FlagSource.Cache, flags.Source);
            Assert.AreEqual(7.5f, flags.GetValue("run_speed", 1f));
        }

        [Test]
        public async Task WrongShapeJson_IsNotCached_AndFallsBack()
        {
            transport.Responses.Enqueue(Ok(IdentityJson));
            await client.GetIdentityFlags("p1");
            transport.Responses.Enqueue(Ok(@"{""detail"":""Maintenance""}")); // valid JSON, but no flags
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("failed"));
            var flags = await client.GetIdentityFlags("p1");
            Assert.AreEqual(FlagSource.Cache, flags.Source);
            Assert.AreEqual(7.5f, flags.GetValue("run_speed", 1f), "the good cached flags must survive");
        }

        [Test]
        public void QueuedEvents_SurviveARestart()
        {
            client.TrackEvent("level_up", "p1");
            client.SaveEvents();
            var restarted = new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = Key, ApiUrl = "http://local:8000/api/v1/" }, transport, cache, () => now);
            Assert.AreEqual(1, restarted.PendingEvents);
        }

        [Test]
        public async Task RequeuedExposure_IsStillDeduped()
        {
            transport.Default = Ok(IdentityJson);
            await client.GetExperimentFlag("tutorial_style", "p1");
            transport.Default = new HttpResponse(0, null, "offline");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("retry"));
            await client.FlushEvents();
            transport.Default = Ok(IdentityJson);
            await client.GetExperimentFlag("tutorial_style", "p1");
            Assert.AreEqual(1, client.PendingEvents);
        }

        [Test]
        public void ServerSideKey_IsRefused()
        {
            Assert.Throws<System.ArgumentException>(() =>
                new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = "ser.abc123" }, transport, cache));
        }

        [Test]
        public async Task MalformedFlag_IsSkipped_AndTheRestStillLoad()
        {
            transport.Responses.Enqueue(Ok(@"{""flags"":[null,{""feature"":""x""},
                {""feature"":{""name"":""bad_exp""},""enabled"":true,""metadata"":{""experiment"":{""id"":""not-a-number""}}},
                {""feature"":{""name"":""run_speed""},""enabled"":true,""feature_state_value"":9}]}"));
            var flags = await client.GetIdentityFlags("p1");
            Assert.AreEqual(FlagSource.Server, flags.Source);
            Assert.AreEqual(9f, flags.GetValue("run_speed", 1f));

            transport.Default = new HttpResponse(0, null, "offline");
            var cached = await client.GetIdentityFlags("p1");
            Assert.AreEqual(FlagSource.Cache, cached.Source);
            Assert.AreEqual(9f, cached.GetValue("run_speed", 1f));
        }

        [Test]
        public async Task DateLikeString_IsReturnedUnchanged()
        {
            transport.Responses.Enqueue(Ok(@"{""flags"":[{""feature"":{""name"":""event_start""},""enabled"":true,""feature_state_value"":""2026-12-01T10:00:00Z""}]}"));
            var flags = await client.GetIdentityFlags("p1");
            Assert.AreEqual("2026-12-01T10:00:00Z", flags.GetValue("event_start", ""));
        }

        /// <summary>Holds event POSTs open until the test releases them, to act during a flush.</summary>
        class HoldingTransport : ITransport
        {
            public readonly FakeTransport Inner = new FakeTransport();
            public TaskCompletionSource<HttpResponse> Held;
            public Task<HttpResponse> Send(HttpRequest request)
            {
                if (!request.Url.EndsWith("/events/") || Held == null) return Inner.Send(request);
                Inner.Requests.Add(request);
                return Held.Task;
            }
        }

        [Test]
        public async Task SavingMidFlush_KeepsTheEventsBeingSent()
        {
            var holding = new HoldingTransport { Held = new TaskCompletionSource<HttpResponse>() };
            var c = new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = Key, ApiUrl = "http://local:8000/api/v1/", RetryDelaySeconds = 0 }, holding, cache, () => now);
            c.TrackEvent("a", "p1");
            c.TrackEvent("b", "p1");
            var flush = c.FlushEvents();
            c.SaveEvents(); // the app is paused while the POST is still out
            Assert.AreEqual(2, new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = Key }, transport, cache, () => now).PendingEvents);
            holding.Held.SetResult(new HttpResponse(202, "{}", null));
            await flush;
            Assert.AreEqual(0, c.PendingEvents);
        }

        [Test]
        public async Task ExposureTrackedDuringAFailedFlush_IsSentOnce()
        {
            var holding = new HoldingTransport { Held = new TaskCompletionSource<HttpResponse>() };
            holding.Inner.Default = Ok(IdentityJson);
            var c = new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = Key, ApiUrl = "http://local:8000/api/v1/", RetryDelaySeconds = 0 }, holding, cache, () => now);
            await c.GetExperimentFlag("tutorial_style", "p1");
            var flush = c.FlushEvents();
            await c.GetExperimentFlag("tutorial_style", "p1"); // seen again while the first exposure is in flight
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("retry"));
            holding.Held.SetResult(new HttpResponse(0, null, "offline"));
            await flush;
            Assert.AreEqual(1, c.PendingEvents);
        }

        [Test]
        public async Task RateLimited_KeepsEventsForTheNextFlush()
        {
            client.TrackEvent("x", "p1");
            transport.Default = new HttpResponse(429, "{}", null);
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("retry"));
            await client.FlushEvents();
            Assert.AreEqual(1, client.PendingEvents);
        }

        class ThrowingTransport : ITransport
        {
            public Task<HttpResponse> Send(HttpRequest request) => throw new System.InvalidOperationException("bad header");
        }

        [Test]
        public void TransportThatThrows_KeepsEvents()
        {
            var c = new FlagsmithClient(new FlagsmithOptions { EnvironmentKey = Key, ApiUrl = "http://local:8000/api/v1/" }, new ThrowingTransport(), cache, () => now);
            c.TrackEvent("x", "p1");
            Assert.ThrowsAsync<System.InvalidOperationException>(() => c.FlushEvents());
            Assert.AreEqual(1, c.PendingEvents);
        }

        enum Difficulty { Easy, Hard }

        class TypedTuning
        {
            [FlagsmithValue("difficulty")] public Difficulty difficulty = Difficulty.Easy;
            [FlagsmithValue("lives")] public int? lives;
        }

        [Test]
        public async Task Bind_HandlesEnumsAndNullables()
        {
            transport.Responses.Enqueue(Ok(@"{""flags"":[{""feature"":{""name"":""difficulty""},""enabled"":true,""feature_state_value"":""hard""},
                {""feature"":{""name"":""lives""},""enabled"":true,""feature_state_value"":3}]}"));
            var t = new TypedTuning();
            FlagsmithValueAttribute.Apply(await client.GetIdentityFlags("p1"), t);
            Assert.AreEqual(Difficulty.Hard, t.difficulty);
            Assert.AreEqual(3, t.lives);
        }

        class Tuning
        {
            [FlagsmithValue("run_speed")] public float speed = 1f;
            [FlagsmithValue("lives")] public int Lives { get; set; }
            [FlagsmithValue("double_jump")] public bool doubleJump = true;
            [FlagsmithValue("missing")] public string untouched = "keep";
        }

        [Test]
        public async Task Bind_SetsAttributedFieldsAndProperties()
        {
            transport.Responses.Enqueue(Ok(IdentityJson));
            var flags = await client.GetIdentityFlags("p1");
            var t = new Tuning();
            FlagsmithValueAttribute.Apply(flags, t);
            Assert.AreEqual(7.5f, t.speed);
            Assert.AreEqual(3, t.Lives);
            Assert.IsFalse(t.doubleJump, "a bool binds to the flag's enabled state");
            Assert.AreEqual("keep", t.untouched);
        }
    }
}

