using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Flagsmith.Editor
{
    /// <summary>
    /// Window > Flagsmith: lists an environment's flags and edits them through the Management API.
    /// With a FlagsmithBehaviour polling in Play mode, edits reach the running game within one refresh.
    /// </summary>
    public sealed class FlagsmithWindow : EditorWindow
    {
        const string Prefix = "Flagsmith.Window.";

        sealed class Row
        {
            public int StateId, FeatureId, OverrideId; // OverrideId: this player's own override, 0 when there is none
            public string Name, Description, Value, SavedValue;
            public bool Enabled, SavedEnabled, Saving;
            public JTokenType ValueType; // the server's type for this value, kept when saving
            public bool Dirty => Enabled != SavedEnabled || Value != SavedValue;
        }

        // Kept in EditorPrefs (this machine only) so the admin token never lands in the project or version control.
        string apiUrl, environmentKey, adminToken, filter = "";
        // Per-player mode: with a player ID loaded, the rows are that player's flags and Save writes an override for them only.
        string player = "", loadedPlayer = "";
        int playerPk;
        List<Row> rows = new List<Row>();
        string status;
        bool loading;
        Vector2 scroll;
        readonly ITransport transport = new UnityWebRequestTransport();

        [MenuItem("Window/Flagsmith")]
        public static void Open() => GetWindow<FlagsmithWindow>("Flagsmith");

        void OnEnable()
        {
            apiUrl = EditorPrefs.GetString(Prefix + "ApiUrl", "https://api.flagsmith.com/api/v1/");
            environmentKey = EditorPrefs.GetString(Prefix + "EnvironmentKey", "");
            adminToken = EditorPrefs.GetString(Prefix + "AdminToken", "");
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            apiUrl = EditorGUILayout.TextField(new GUIContent("API URL", "Flagsmith Cloud: https://api.flagsmith.com/api/v1/  Self-hosted: http://localhost:8000/api/v1/"), apiUrl);
            environmentKey = EditorGUILayout.TextField(new GUIContent("Environment key", "The client-side key of the environment to edit."), environmentKey);
            adminToken = EditorGUILayout.PasswordField(new GUIContent("Admin token", "An Organisation API key or a user API token. Stored in EditorPrefs on this machine only."), adminToken);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(Prefix + "ApiUrl", apiUrl);
                EditorPrefs.SetString(Prefix + "EnvironmentKey", environmentKey);
                EditorPrefs.SetString(Prefix + "AdminToken", adminToken);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                player = EditorGUILayout.TextField(new GUIContent("Only this player", "Empty: edit flags for everyone. A player ID (the game shows it): Save then changes a flag for that player only."), player);
                using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
                    if (GUILayout.Button(new GUIContent("Running", "Use the player of the game running in Play mode"), GUILayout.Width(70)))
                    {
                        var running = FindAnyObjectByType<FlagsmithBehaviour>();
                        if (running != null) { player = running.Identifier; GUI.FocusControl(null); }
                    }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(loading || string.IsNullOrEmpty(environmentKey) || string.IsNullOrEmpty(adminToken)))
                    if (GUILayout.Button(rows.Count == 0 ? "Load flags" : "Reload", GUILayout.Width(110))) _ = Load();
                filter = EditorGUILayout.TextField(filter, EditorStyles.toolbarSearchField);
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
            if (Stale) EditorGUILayout.HelpBox("The player ID changed. Press Reload to load that target before saving.", MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var row in rows.Where(r => r.Name.IndexOf(filter ?? "", StringComparison.OrdinalIgnoreCase) >= 0))
                DrawRow(row);
            EditorGUILayout.EndScrollView();

            if (EditorApplication.isPlaying && rows.Count > 0)
                EditorGUILayout.HelpBox("Play mode: saved changes reach the game on its next flag refresh.", MessageType.Info);
        }

        void DrawRow(Row row)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                row.Enabled = EditorGUILayout.Toggle(row.Enabled, GUILayout.Width(18));
                EditorGUILayout.LabelField(new GUIContent(row.OverrideId != 0 ? row.Name + "  (this player)" : row.Name, row.Description), EditorStyles.boldLabel, GUILayout.MinWidth(120));
                row.Value = EditorGUILayout.DelayedTextField(row.Value ?? "", GUILayout.MinWidth(80));
                using (new EditorGUI.DisabledScope(!row.Dirty || row.Saving || Stale))
                    if (GUILayout.Button(row.Saving ? "…" : "Save", GUILayout.Width(50))) _ = Save(row);
                if (loadedPlayer != "")
                    using (new EditorGUI.DisabledScope(row.OverrideId == 0 || row.Saving || Stale))
                        if (GUILayout.Button(new GUIContent("Clear", "Remove this player's override"), GUILayout.Width(50))) _ = Clear(row);
            }
        }

        // The rows on screen belong to another target than the one typed in: saving would hit the wrong one.
        bool Stale => rows.Count > 0 && (player ?? "").Trim() != loadedPlayer;

        async Task Load(string target = null)
        {
            var id = (target ?? player ?? "").Trim();
            loading = true;
            status = "Loading…";
            try
            {
                var env = await Call("GET", $"environments/{environmentKey}/");
                var project = (int)env["project"];
                var features = (await Call("GET", $"projects/{project}/features/?page_size=999"))["results"]
                    .ToDictionary(f => (int)f["id"], f => f);
                if (id != "") { await LoadPlayer(id, features, (string)env["name"]); return; }
                var states = (await Call("GET", $"environments/{environmentKey}/featurestates/?page_size=999"))["results"]
                    .Where(s => s["identity"].Type == JTokenType.Null && s["feature_segment"].Type == JTokenType.Null);
                rows = states.Where(s => features.ContainsKey((int)s["feature"])).Select(s =>
                {
                    var f = features[(int)s["feature"]];
                    var value = Flag.AsText(s["feature_state_value"]) ?? ""; // the text field returns "" for null, which would mark the row dirty
                    var enabled = (bool)s["enabled"];
                    var type = s["feature_state_value"]?.Type ?? JTokenType.Null;
                    return new Row
                    {
                        StateId = (int)s["id"], Name = (string)f["name"], Description = (string)f["description"],
                        Value = value, SavedValue = value, Enabled = enabled, SavedEnabled = enabled, ValueType = type,
                    };
                }).OrderBy(r => r.Name).ToList();
                loadedPlayer = "";
                status = $"{rows.Count} flags in {(string)env["name"]}.";
            }
            catch (Exception e) { status = e.Message; }
            finally { loading = false; Repaint(); }
        }

        // Flags as one player gets them. Saving here writes an identity override, so only that player's game changes.
        async Task LoadPlayer(string id, Dictionary<int, JToken> features, string envName)
        {
            var found = (await Call("GET", $"environments/{environmentKey}/identities/?q={Uri.EscapeDataString(id)}"))["results"]
                .FirstOrDefault(i => (string)i["identifier"] == id);
            if (found == null) throw new Exception($"No player with the ID \"{id}\" has connected to {envName} yet. The ID is FlagsmithBehaviour.Identifier.");
            playerPk = (int)found["id"];
            var path = $"environments/{environmentKey}/identities/{playerPk}/featurestates/";
            var overrides = (await Call("GET", path + "?page_size=999"))["results"].ToDictionary(o => (int)o["feature"], o => (int)o["id"]);
            rows = (await Call("GET", path + "all/")).Where(s => features.ContainsKey((int)s["feature"]["id"])).Select(s =>
            {
                var f = features[(int)s["feature"]["id"]];
                var value = Flag.AsText(s["feature_state_value"]) ?? "";
                var enabled = (bool)s["enabled"];
                return new Row
                {
                    FeatureId = (int)f["id"], OverrideId = overrides.TryGetValue((int)f["id"], out var o) ? o : 0,
                    Name = (string)f["name"], Description = (string)f["description"],
                    Value = value, SavedValue = value, Enabled = enabled, SavedEnabled = enabled,
                    ValueType = s["feature_state_value"]?.Type ?? JTokenType.Null,
                };
            }).OrderBy(r => r.Name).ToList();
            loadedPlayer = id;
            status = $"{rows.Count} flags as {id} gets them in {envName}. Save changes a flag for this player only.";
        }

        async Task Clear(Row row)
        {
            row.Saving = true;
            try
            {
                await Call("DELETE", $"environments/{environmentKey}/identities/{playerPk}/featurestates/{row.OverrideId}/");
                await Load(loadedPlayer); // show what the player gets now
            }
            catch (Exception e) { status = e.Message; row.Saving = false; Repaint(); }
        }

        async Task Save(Row row)
        {
            row.Saving = true;
            // What is sent is what counts as saved, even if the row is edited while the request is out.
            var enabled = row.Enabled;
            var value = row.Value;
            try
            {
                if (loadedPlayer != "")
                {
                    // An override stands on its own, so it always carries both the state and the value.
                    var o = new JObject { ["feature"] = row.FeatureId, ["enabled"] = enabled, ["feature_state_value"] = Typed(value, row.ValueType) }.ToString(Formatting.None);
                    var path = $"environments/{environmentKey}/identities/{playerPk}/featurestates/";
                    if (row.OverrideId == 0) row.OverrideId = (int)(await Call("POST", path, o))["id"];
                    else await Call("PUT", $"{path}{row.OverrideId}/", o);
                }
                else
                {
                    var body = new JObject { ["enabled"] = enabled };
                    if (value != row.SavedValue) body["feature_state_value"] = Typed(value, row.ValueType); // a toggle alone leaves the value alone
                    await Call("PATCH", $"environments/{environmentKey}/featurestates/{row.StateId}/", body.ToString(Formatting.None));
                }
                row.SavedEnabled = enabled;
                row.SavedValue = value;
                status = loadedPlayer != "" ? $"Saved {row.Name} for {loadedPlayer} only." : $"Saved {row.Name}.";
            }
            catch (Exception e) { status = e.Message; }
            finally { row.Saving = false; Repaint(); }
        }

        // Flagsmith stores the JSON type it receives, so an integer flag must stay an integer.
        static JToken Typed(string value, JTokenType type)
        {
            if (value == "") return JValue.CreateNull();
            if (type == JTokenType.Integer && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            if (type == JTokenType.Boolean && bool.TryParse(value, out var b)) return b;
            return value;
        }

        async Task<JToken> Call(string method, string path, string body = null)
        {
            var request = new HttpRequest { Method = method, Url = apiUrl.TrimEnd('/') + "/" + path, Body = body };
            // Organisation API keys look like "prefix.secret"; user tokens have no dot.
            request.Headers["Authorization"] = (adminToken.Contains(".") ? "Api-Key " : "Token ") + adminToken;
            if (body != null) request.Headers["Content-Type"] = "application/json";
            var res = await transport.Send(request);
            if (!res.IsSuccess) throw new Exception($"{method} {path} failed: {res.Error ?? "HTTP " + res.Status} {res.Body}");
            return string.IsNullOrEmpty(res.Body) ? null : Json.Parse(res.Body);
        }
    }
}
