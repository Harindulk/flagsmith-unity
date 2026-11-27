using System;
using System.Collections.Generic;
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
            public int StateId;
            public string Name, Description, Value, SavedValue;
            public bool Enabled, SavedEnabled, Saving;
            public bool Dirty => Enabled != SavedEnabled || Value != SavedValue;
        }

        // Kept in EditorPrefs (this machine only) so the admin token never lands in the project or version control.
        string apiUrl, environmentKey, adminToken, filter = "";
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
                using (new EditorGUI.DisabledScope(loading || string.IsNullOrEmpty(environmentKey) || string.IsNullOrEmpty(adminToken)))
                    if (GUILayout.Button(rows.Count == 0 ? "Load flags" : "Reload", GUILayout.Width(110))) _ = Load();
                filter = EditorGUILayout.TextField(filter, EditorStyles.toolbarSearchField);
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

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
                EditorGUILayout.LabelField(new GUIContent(row.Name, row.Description), EditorStyles.boldLabel, GUILayout.MinWidth(120));
                row.Value = EditorGUILayout.DelayedTextField(row.Value ?? "", GUILayout.MinWidth(80));
                using (new EditorGUI.DisabledScope(!row.Dirty || row.Saving))
                    if (GUILayout.Button(row.Saving ? "…" : "Save", GUILayout.Width(50))) _ = Save(row);
            }
        }

        async Task Load()
        {
            loading = true;
            status = "Loading…";
            try
            {
                var env = await Call("GET", $"environments/{environmentKey}/");
                var project = (int)env["project"];
                var features = (await Call("GET", $"projects/{project}/features/?page_size=999"))["results"]
                    .ToDictionary(f => (int)f["id"], f => f);
                var states = (await Call("GET", $"environments/{environmentKey}/featurestates/?page_size=999"))["results"]
                    .Where(s => s["identity"].Type == JTokenType.Null && s["feature_segment"].Type == JTokenType.Null);
                rows = states.Where(s => features.ContainsKey((int)s["feature"])).Select(s =>
                {
                    var f = features[(int)s["feature"]];
                    var value = Flag.AsText(s["feature_state_value"]) ?? ""; // the text field returns "" for null, which would mark the row dirty
                    var enabled = (bool)s["enabled"];
                    return new Row
                    {
                        StateId = (int)s["id"], Name = (string)f["name"], Description = (string)f["description"],
                        Value = value, SavedValue = value, Enabled = enabled, SavedEnabled = enabled,
                    };
                }).OrderBy(r => r.Name).ToList();
                status = $"{rows.Count} flags in {(string)env["name"]}.";
            }
            catch (Exception e) { status = e.Message; }
            finally { loading = false; Repaint(); }
        }

        async Task Save(Row row)
        {
            row.Saving = true;
            try
            {
                var body = new JObject { ["enabled"] = row.Enabled, ["feature_state_value"] = row.Value == "" ? null : row.Value };
                await Call("PATCH", $"environments/{environmentKey}/featurestates/{row.StateId}/", body.ToString(Formatting.None));
                row.SavedEnabled = row.Enabled;
                row.SavedValue = row.Value;
                status = $"Saved {row.Name}.";
            }
            catch (Exception e) { status = e.Message; }
            finally { row.Saving = false; Repaint(); }
        }

        async Task<JToken> Call(string method, string path, string body = null)
        {
            var request = new HttpRequest { Method = method, Url = apiUrl.TrimEnd('/') + "/" + path, Body = body };
            // Organisation API keys look like "prefix.secret"; user tokens have no dot.
            request.Headers["Authorization"] = (adminToken.Contains(".") ? "Api-Key " : "Token ") + adminToken;
            if (body != null) request.Headers["Content-Type"] = "application/json";
            var res = await transport.Send(request);
            if (!res.IsSuccess) throw new Exception($"{method} {path} failed: {res.Error ?? "HTTP " + res.Status} {res.Body}");
            return JToken.Parse(res.Body);
        }
    }
}
