using System.Collections;
using System.Collections.Generic;
using Flagsmith;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// "Flag Runner": a one-button runner whose tuning all comes from Flagsmith.
/// run_speed, jump_force, gravity_scale, coin_value, player_color, double_jump, spike_walls (kill switch),
/// and the tutorial_style experiment (text vs hand) measured by the first_run_complete event.
/// Builds its whole scene from code so it can be dropped into any empty scene.
/// </summary>
public sealed class FlagRunner : MonoBehaviour
{
    [System.Serializable] class Connection { public string api_url; public string environment_key; }

    const float GroundY = -3f, PlayerX = -4.5f, PlayerSize = 0.8f, GoalMeters = 100f;

    // Tuning: each field follows the Flagsmith flag of the same name (Bind below).
    // The initial values are the offline defaults.
    [FlagsmithValue("run_speed")] float runSpeed = 7f;
    [FlagsmithValue("jump_force")] float jumpForce = 13f;
    [FlagsmithValue("gravity_scale")] float gravityScale = 3.2f;
    [FlagsmithValue("coin_value")] int coinValue = 1;
    [FlagsmithValue("double_jump")] bool doubleJump;           // bools follow the flag's on/off state
    [FlagsmithValue("spike_walls")] bool spikeWalls = true;    // kill switch
    [FlagsmithValue("player_color")] string playerColorHex = "#FFB020";
    Color playerColor = new Color(1f, 0.69f, 0.13f);
    string tutorial = "text";                                   // experiment: read only between runs
    readonly Dictionary<string, string> shown = new Dictionary<string, string>();

    FlagsmithBehaviour flagsmith;
    string setupError;

    // Run state
    enum State { Ready, Running, Dead }
    State state;
    float distance, vy, nextSpawn = 6f, deadAt;
    int score, jumpsLeft, runs;
    bool grounded = true, goalSent;

    Transform player, playerVisual, cam;
    SpriteRenderer playerSprite;
    Vector3 camHome;
    float trauma, shakeT, flashUntil;
    readonly List<(Transform t, char kind, Vector2 size)> things = new List<(Transform, char, Vector2)>();
    readonly List<Transform> groundDashes = new List<Transform>();
    readonly Dictionary<string, float> changedAt = new Dictionary<string, float>();
    // Clip-friendly demo: the game plays itself (press A, or open the WebGL build with ?autoplay)
    // and every flag change gets a big caption, so a recording needs no hands on the keyboard.
    bool autoplay;
    string toast;
    float toastAt = -10f;
    Sprite square, circle, triangle;
    GUIStyle big, small, panel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() => new GameObject("Flag Runner").AddComponent<FlagRunner>();

    void Awake()
    {
        square = MakeSprite((x, y) => true);
        circle = MakeSprite((x, y) => (x - .5f) * (x - .5f) + (y - .5f) * (y - .5f) < .25f);
        triangle = MakeSprite((x, y) => Mathf.Abs(x - .5f) * 2f < 1f - y);

        cam = Camera.main.transform;
        camHome = cam.position;
        Camera.main.backgroundColor = new Color(0.08f, 0.09f, 0.13f);

        Spawn(square, new Vector2(0, GroundY - 2.5f), new Vector2(40, 5), new Color(0.15f, 0.17f, 0.24f));
        for (var i = 0; i < 24; i++)
            groundDashes.Add(Spawn(square, new Vector2(-12 + i, GroundY - 0.35f), new Vector2(0.4f, 0.08f), new Color(0.3f, 0.33f, 0.45f)));

        player = new GameObject("Player").transform;
        player.position = new Vector3(PlayerX, GroundY + PlayerSize / 2, 0);
        playerVisual = Spawn(square, Vector2.zero, Vector2.one * PlayerSize, playerColor, player);
        playerSprite = playerVisual.GetComponent<SpriteRenderer>();
        playerSprite.sortingOrder = 5;

        autoplay = Application.absoluteURL.Contains("autoplay");
        Connect();
    }

    void Connect()
    {
        var file = Resources.Load<TextAsset>("flagsmith");
        var conn = file == null ? null : JsonUtility.FromJson<Connection>(file.text);
        if (string.IsNullOrEmpty(conn?.environment_key))
        {
            setupError = "No Flagsmith connection. Run server/seed.py, then press Play again. Using default tuning.";
            return;
        }
        flagsmith = gameObject.AddComponent<FlagsmithBehaviour>();
        flagsmith.Bind(this);                // updates the [FlagsmithValue] fields first...
        flagsmith.FlagsChanged += OnTuned;   // ...then this shows what changed
        // 2 s refresh = Live Tuning for the demo; ship with the 60 s default.
        flagsmith.Init(new FlagsmithOptions { EnvironmentKey = conn.environment_key, ApiUrl = conn.api_url }, refresh: 2f);
    }

    void OnTuned(Flags flags)
    {
        if (state == State.Ready) tutorial = flags.GetValue("tutorial_style", tutorial);
        if (ColorUtility.TryParseHtmlString(playerColorHex, out var c)) playerColor = c;

        foreach (var (key, value) in Tuning())
        {
            if (shown.TryGetValue(key, out var old) && old != value && Time.time > 1f)
            {
                changedAt[key] = Time.unscaledTime;
                flashUntil = Time.unscaledTime + 0.15f;
                Pop(playerVisual, 1.25f, 0.8f);
                toast = $"{key}   {old}  ->  {value}";
                toastAt = Time.unscaledTime;
            }
            shown[key] = value;
        }

        if (!spikeWalls) // kill switch: remove live walls right away
            for (var i = things.Count - 1; i >= 0; i--)
                if (things[i].kind == 'W') { Destroy(things[i].t.gameObject); things.RemoveAt(i); }
    }

    (string key, string value)[] Tuning() => new[]
    {
        ("run_speed", Show(runSpeed)), ("jump_force", Show(jumpForce)), ("gravity_scale", Show(gravityScale)),
        ("coin_value", Show(coinValue)), ("player_color", Show(playerColor)), ("double_jump", Show(doubleJump)),
        ("spike_walls", Show(spikeWalls)), ("tutorial_style", tutorial),
    };

    static string Show(object v) => v switch
    {
        float f => f.ToString("0.##"),
        bool b => b ? "on" : "off",
        Color c => "#" + ColorUtility.ToHtmlStringRGB(c),
        _ => v?.ToString(),
    };

    // Jump so the peak of the arc lands on the next obstacle, using the live tuning values.
    bool AutoJump()
    {
        var g = gravityScale * 9.81f;
        var lead = runSpeed * (jumpForce / g) * 0.95f;
        foreach (var (t, kind, size) in things)
        {
            if (kind == 'C') continue;
            var dx = t.position.x - player.position.x;
            if (dx > 0 && dx < lead) return grounded;
        }
        return false;
    }

    void Update()
    {
        var pressed = (Keyboard.current?.spaceKey.wasPressedThisFrame ?? false)
                      || (Pointer.current?.press.wasPressedThisFrame ?? false);
        if (Keyboard.current?.aKey.wasPressedThisFrame ?? false) autoplay = !autoplay;
        if (autoplay)
            pressed |= state == State.Ready || (state == State.Running && AutoJump())
                       || (state == State.Dead && Time.unscaledTime - deadAt > 1.2f);
        playerSprite.color = Time.unscaledTime < flashUntil ? Color.white : playerColor;

        switch (state)
        {
            case State.Ready:
                if (pressed && !WaitingForFlags) { StartRun(); Jump(); }
                break;
            case State.Running:
                if (pressed) Jump();
                Simulate(Time.deltaTime);
                break;
            case State.Dead:
                if (pressed && Time.unscaledTime - deadAt > 0.6f) ResetRun();
                break;
        }
        Shake(Time.unscaledDeltaTime);
        playerVisual.localScale = Vector3.Lerp(playerVisual.localScale, Vector3.one * PlayerSize, 1 - Mathf.Exp(-14f * Time.unscaledDeltaTime));
    }

    // Hold the first run until flags arrive (or 4 s pass offline), so the first tutorial shown is the real variant.
    bool WaitingForFlags => flagsmith != null && flagsmith.Flags.All.Count == 0 && Time.realtimeSinceStartup < 4f;

    void StartRun()
    {
        state = State.Running;
        runs++;
        // Count the exposure only if the tutorial on screen is the variant Flagsmith served this player
        // (not the offline default), so the experiment never credits a variant nobody saw.
        var served = flagsmith?.Flags.Get("tutorial_style");
        if (served != null && served.Value == tutorial) flagsmith.GetExperimentFlag("tutorial_style");
    }

    void Jump()
    {
        if (!grounded && jumpsLeft <= 0) return;
        if (!grounded) jumpsLeft--;
        grounded = false;
        vy = jumpForce;
        Pop(playerVisual, 0.7f, 1.35f);
    }

    void Simulate(float dt)
    {
        var step = runSpeed * dt;
        distance += step;

        vy -= gravityScale * 9.81f * dt;
        var p = player.position;
        p.y += vy * dt;
        var floor = GroundY + PlayerSize / 2;
        if (p.y <= floor)
        {
            if (!grounded) Pop(playerVisual, 1.35f, 0.7f);
            p.y = floor; vy = 0; grounded = true; jumpsLeft = doubleJump ? 1 : 0;
        }
        player.position = p;
        playerVisual.localRotation = grounded ? Quaternion.identity : Quaternion.Euler(0, 0, -distance * 40f);

        foreach (var d in groundDashes)
        {
            var dp = d.position; dp.x -= step;
            if (dp.x < -12) dp.x += 24;
            d.position = dp;
        }

        if (distance >= nextSpawn) SpawnObstacle();

        var me = new Rect(p.x - PlayerSize * .4f, p.y - PlayerSize * .4f, PlayerSize * .8f, PlayerSize * .8f);
        for (var i = things.Count - 1; i >= 0; i--)
        {
            var (t, kind, size) = things[i];
            t.position += Vector3.left * step;
            if (t.position.x < -14) { Destroy(t.gameObject); things.RemoveAt(i); continue; }
            var hit = new Rect(t.position.x - size.x * .4f, t.position.y - size.y * .45f, size.x * .8f, size.y * .8f);
            if (!me.Overlaps(hit)) continue;
            if (kind == 'C')
            {
                score += coinValue;
                StartCoroutine(CoinPop(t));
                things.RemoveAt(i);
            }
            else { Die(); return; }
        }

        if (!goalSent && distance >= GoalMeters)
        {
            goalSent = true;
            // Once per player, ever: the metric is "players who finish a first run", so replays must not add to it.
            var key = "flagrunner.first_run_complete." + flagsmith?.Identifier;
            if (flagsmith != null && PlayerPrefs.GetInt(key, 0) == 0)
            {
                PlayerPrefs.SetInt(key, 1);
                PlayerPrefs.Save();
                flagsmith.TrackEvent("first_run_complete", Mathf.RoundToInt(distance), new Dictionary<string, object> { ["runs"] = runs });
            }
            trauma = Mathf.Max(trauma, 0.25f);
        }
    }

    void SpawnObstacle()
    {
        var x = 11f;
        if (spikeWalls && Random.value < 0.3f)
        {
            var size = new Vector2(0.6f, 2.0f);
            things.Add((Spawn(square, new Vector2(x, GroundY + size.y / 2), size, new Color(0.93f, 0.27f, 0.33f)), 'W', size));
        }
        else
        {
            var n = Random.Range(1, 3);
            for (var i = 0; i < n; i++)
            {
                var size = new Vector2(0.8f, 0.8f);
                things.Add((Spawn(triangle, new Vector2(x + i * 0.8f, GroundY + size.y / 2), size, new Color(0.93f, 0.27f, 0.33f)), 'S', size));
            }
        }
        for (var i = 0; i < 3; i++)
        {
            var size = Vector2.one * 0.45f;
            things.Add((Spawn(circle, new Vector2(x + 3.5f + i * 0.8f, GroundY + 2.2f), size, new Color(1f, 0.84f, 0.3f)), 'C', size));
        }
        // Space obstacles by the current jump's length so the level stays playable under any live tuning.
        var airtime = 2f * jumpForce / (gravityScale * 9.81f);
        nextSpawn = distance + Random.Range(8f, 13f) * Mathf.Max(1f, airtime / 0.83f);
    }

    void Die()
    {
        state = State.Dead;
        deadAt = Time.unscaledTime;
        trauma = 0.7f;
        StartCoroutine(HitStop(0.09f));
        for (var i = 0; i < 14; i++) StartCoroutine(Shard(player.position));
        playerVisual.gameObject.SetActive(false);
    }

    void ResetRun()
    {
        foreach (var t in things) Destroy(t.t.gameObject);
        things.Clear();
        distance = 0; score = 0; vy = 0; grounded = true; nextSpawn = 6f;
        player.position = new Vector3(PlayerX, GroundY + PlayerSize / 2, 0);
        playerVisual.gameObject.SetActive(true);
        Pop(playerVisual, 0.6f, 1.4f);
        state = State.Ready;
    }

    // ---- juice ----

    static void Pop(Transform t, float sx, float sy) => t.localScale = new Vector3(sx, sy, 1) * PlayerSize;

    void Shake(float dt)
    {
        trauma = Mathf.Max(0, trauma - 1.6f * dt);
        shakeT += dt * 30f;
        var s = trauma * trauma;
        cam.position = camHome + new Vector3(0.35f * s * Mathf.Sin(shakeT * 1.7f), 0.25f * s * Mathf.Sin(shakeT * 2.3f), 0);
    }

    IEnumerator HitStop(float seconds)
    {
        Time.timeScale = 0.05f;
        yield return new WaitForSecondsRealtime(seconds);
        Time.timeScale = 1f;
    }

    IEnumerator CoinPop(Transform t)
    {
        var sr = t.GetComponent<SpriteRenderer>();
        var start = t.localScale;
        for (float k = 0; k < 1; k += Time.unscaledDeltaTime / 0.25f)
        {
            t.localScale = start * (1 + 0.8f * Mathf.Sin(k * Mathf.PI * 0.5f));
            t.position += Vector3.up * Time.unscaledDeltaTime * 2f;
            sr.color = new Color(1, 0.84f, 0.3f, 1 - k);
            yield return null;
        }
        Destroy(t.gameObject);
    }

    IEnumerator Shard(Vector3 from)
    {
        var t = Spawn(square, from, Vector2.one * Random.Range(0.12f, 0.25f), playerColor);
        var v = new Vector3(Random.Range(-6f, 6f), Random.Range(3f, 11f), 0);
        for (float life = 0; life < 0.9f; life += Time.unscaledDeltaTime)
        {
            v.y -= 25f * Time.unscaledDeltaTime;
            t.position += v * Time.unscaledDeltaTime;
            t.Rotate(0, 0, 600 * Time.unscaledDeltaTime);
            yield return null;
        }
        Destroy(t.gameObject);
    }

    // ---- HUD ----

    void OnGUI()
    {
        var u = Screen.height / 720f;
        big ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        small ??= new GUIStyle(GUI.skin.label) { richText = true };
        panel ??= new GUIStyle(GUI.skin.box);
        big.fontSize = Mathf.RoundToInt(40 * u);
        small.fontSize = Mathf.RoundToInt(17 * u);

        GUI.Label(new Rect(24 * u, 16 * u, 600 * u, 40 * u), $"<b>{distance:0} m</b>   <color=#FFD54D>● {score}</color>", Bold(small, 26 * u));

        if (state == State.Ready && WaitingForFlags)
            GUI.Label(new Rect(0, Screen.height * 0.45f, Screen.width, 60 * u), "Connecting to Flagsmith…", big);
        else if (state == State.Ready)
        {
            if (tutorial == "hand") DrawTapHand(u);
            else GUI.Label(new Rect(0, Screen.height * 0.45f, Screen.width, 60 * u), "TAP OR PRESS SPACE TO JUMP", big);
        }
        if (state == State.Dead && Time.unscaledTime - deadAt > 0.6f)
            GUI.Label(new Rect(0, Screen.height * 0.45f, Screen.width, 60 * u), "Tap to run again", big);
        if (goalSent && distance < GoalMeters + 15f)
            GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 60 * u), "100 m! First run complete", big);

        DrawFlagPanel(u);
        DrawToast(u);
        if (autoplay) GUI.Label(new Rect(24 * u, 52 * u, 300 * u, 30 * u), "<color=#6B7390>AUTOPLAY (A)</color>", small);
        if (setupError != null) GUI.Label(new Rect(24 * u, Screen.height - 40 * u, Screen.width, 30 * u), setupError, small);
    }

    void DrawToast(float u)
    {
        var age = Time.unscaledTime - toastAt;
        if (toast == null || age > 2.8f) return;
        var a = Mathf.Clamp01(Mathf.Min(age / 0.12f, (2.8f - age) / 0.4f));
        var rise = (1 - Mathf.Clamp01(age / 0.25f)) * 20 * u;
        float w = 560 * u, h = 74 * u, x = (Screen.width - w) / 2, y = Screen.height - h - 150 * u + rise;
        GUI.color = new Color(1, 1, 1, a);
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, panel);
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, panel);
        GUI.Label(new Rect(x, y + 6 * u, w, 22 * u), "<color=#9AA3B8>changed in Flagsmith, no rebuild</color>", Centered(small));
        GUI.Label(new Rect(x, y + 28 * u, w, 40 * u), $"<color=#FFD54D><b>{toast}</b></color>", Centered(Bold(small, 28 * u)));
        GUI.color = Color.white;
    }

    static GUIStyle Centered(GUIStyle s) => new GUIStyle(s) { alignment = TextAnchor.MiddleCenter, richText = true };

    void DrawTapHand(float u)
    {
        // The "hand" variant: a pulsing finger-tap ring instead of words.
        var k = Mathf.Repeat(Time.unscaledTime, 1.1f) / 1.1f;
        var c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var ring = Mathf.Lerp(20, 90, k) * u;
        GUI.color = new Color(1, 1, 1, 1 - k);
        GUI.DrawTexture(new Rect(c.x - ring, c.y - ring, ring * 2, ring * 2), circle.texture);
        GUI.color = Color.white;
        var dot = (k < 0.15f ? 26 : 32) * u;
        GUI.DrawTexture(new Rect(c.x - dot / 2, c.y - dot / 2 - (k < 0.15f ? 0 : 14 * u), dot, dot), circle.texture);
    }

    void DrawFlagPanel(float u)
    {
        var rows = Tuning();
        float w = 270 * u, h = (rows.Length + 2) * 24 * u + 16 * u, x = Screen.width - w - 16 * u, y = 16 * u;
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, panel);
        var src = flagsmith == null ? "defaults" : flagsmith.Flags.Source.ToString().ToLowerInvariant();
        GUI.Label(new Rect(x + 10 * u, y + 6 * u, w, 28 * u), $"<b>Flagsmith</b>  <color=#9AA3B8>{src}</color>", small);
        for (var i = 0; i < rows.Length; i++)
        {
            var (key, val) = rows[i];
            var hot = changedAt.TryGetValue(key, out var at) && Time.unscaledTime - at < 1.5f;
            var colour = hot ? "#FFD54D" : "#E6E9F2";
            GUI.Label(new Rect(x + 10 * u, y + (i + 1.3f) * 24 * u, w, 28 * u),
                $"<color=#9AA3B8>{key}</color>  <color={colour}><b>{val}</b></color>", small);
        }
        if (flagsmith != null)
            GUI.Label(new Rect(x + 10 * u, y + (rows.Length + 1.3f) * 24 * u, w, 28 * u), $"<color=#6B7390>{flagsmith.Identifier}</color>", small);
    }

    static GUIStyle Bold(GUIStyle s, float size) => new GUIStyle(s) { fontSize = Mathf.RoundToInt(size) };

    // ---- sprites built in code, so the sample has no art assets ----

    Transform Spawn(Sprite sprite, Vector2 pos, Vector2 size, Color color, Transform parent = null)
    {
        var go = new GameObject(sprite == circle ? "Coin" : "Block");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1);
        return go.transform;
    }

    static Sprite MakeSprite(System.Func<float, float, bool> inside)
    {
        const int n = 64;
        var tex = new Texture2D(n, n) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            px[y * n + x] = inside((x + .5f) / n, (y + .5f) / n) ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(.5f, .5f), n);
    }
}
