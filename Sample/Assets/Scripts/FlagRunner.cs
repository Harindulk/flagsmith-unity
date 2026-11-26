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

    // Tuning (Flagsmith values; these are the offline defaults)
    float runSpeed = 7f, jumpForce = 13f, gravityScale = 3.2f;
    int coinValue = 1;
    bool doubleJump, spikeWalls = true;
    Color playerColor = new Color(1f, 0.69f, 0.13f);
    string tutorial = "text";

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
        flagsmith.FlagsChanged += ApplyFlags;
        flagsmith.Init(new FlagsmithOptions { EnvironmentKey = conn.environment_key, ApiUrl = conn.api_url });
    }

    void ApplyFlags(Flags flags)
    {
        Set("run_speed", ref runSpeed, flags.GetValue("run_speed", runSpeed));
        Set("jump_force", ref jumpForce, flags.GetValue("jump_force", jumpForce));
        Set("gravity_scale", ref gravityScale, flags.GetValue("gravity_scale", gravityScale));
        Set("coin_value", ref coinValue, flags.GetValue("coin_value", coinValue));
        Set("double_jump", ref doubleJump, flags.IsEnabled("double_jump"));
        Set("spike_walls", ref spikeWalls, flags.IsEnabled("spike_walls"));
        if (ColorUtility.TryParseHtmlString(flags.GetValue("player_color", ""), out var c)) Set("player_color", ref playerColor, c);
        if (state == State.Ready) tutorial = flags.GetValue("tutorial_style", tutorial);

        if (!spikeWalls) // kill switch: remove live walls right away
            for (var i = things.Count - 1; i >= 0; i--)
                if (things[i].kind == 'W') { Destroy(things[i].t.gameObject); things.RemoveAt(i); }
    }

    void Set<T>(string key, ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (Time.time > 1f) { changedAt[key] = Time.unscaledTime; flashUntil = Time.unscaledTime + 0.15f; Pop(playerVisual, 1.25f, 0.8f); }
    }

    void Update()
    {
        var pressed = (Keyboard.current?.spaceKey.wasPressedThisFrame ?? false)
                      || (Pointer.current?.press.wasPressedThisFrame ?? false);
        playerSprite.color = Time.unscaledTime < flashUntil ? Color.white : playerColor;

        switch (state)
        {
            case State.Ready:
                if (pressed) { StartRun(); Jump(); }
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

    void StartRun()
    {
        state = State.Running;
        runs++;
        // The variant counts as seen once the run starts, because the tutorial was on screen.
        flagsmith?.GetExperimentFlag("tutorial_style");
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
            goalSent = true; // once per session: the metric counts players who finish a first run
            flagsmith?.TrackEvent("first_run_complete", Mathf.RoundToInt(distance), new Dictionary<string, object> { ["runs"] = runs });
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
        nextSpawn = distance + Random.Range(8f, 13f);
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

        if (state == State.Ready)
        {
            if (tutorial == "hand") DrawTapHand(u);
            else GUI.Label(new Rect(0, Screen.height * 0.45f, Screen.width, 60 * u), "TAP OR PRESS SPACE TO JUMP", big);
        }
        if (state == State.Dead && Time.unscaledTime - deadAt > 0.6f)
            GUI.Label(new Rect(0, Screen.height * 0.45f, Screen.width, 60 * u), "Tap to run again", big);
        if (goalSent && distance < GoalMeters + 15f)
            GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 60 * u), "100 m! First run complete", big);

        DrawFlagPanel(u);
        if (setupError != null) GUI.Label(new Rect(24 * u, Screen.height - 40 * u, Screen.width, 30 * u), setupError, small);
    }

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
        var rows = new[]
        {
            ("run_speed", runSpeed.ToString("0.##")), ("jump_force", jumpForce.ToString("0.##")),
            ("gravity_scale", gravityScale.ToString("0.##")), ("coin_value", coinValue.ToString()),
            ("player_color", "#" + ColorUtility.ToHtmlStringRGB(playerColor)), ("double_jump", doubleJump ? "on" : "off"),
            ("spike_walls", spikeWalls ? "on" : "off"), ("tutorial_style", tutorial),
        };
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
