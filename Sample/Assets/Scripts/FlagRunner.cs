using System.Collections;
using System.Collections.Generic;
using Flagsmith;
using UnityEngine;

/// <summary>
/// "Flag Runner": a one-button runner whose look and tuning all come from Flagsmith.
/// run_speed, jump_force, gravity_scale, coin_value, player_skin, double_jump, spike_walls (kill switch),
/// winter_event (reskins the whole level), and the tutorial_style experiment (text vs hand) measured by
/// the first_run_complete event. Builds its whole scene from code so it can be dropped into any empty scene.
/// Art: Kenney's New Platformer Pack (CC0), under Resources/Art.
/// </summary>
public sealed class FlagRunner : MonoBehaviour
{
    [System.Serializable] class Connection { public string api_url; public string environment_key; }

    const float GroundY = -3f, PlayerX = -4.5f, PlayerSize = 0.8f, GoalMeters = 100f;
    const float TrailSpeed = 9.5f; // from this run speed up, the player leaves a trail

    // Tuning: each field follows the Flagsmith flag of the same name (Bind below).
    // The initial values are the offline defaults.
    [FlagsmithValue("run_speed")] float runSpeed = 7f;
    [FlagsmithValue("jump_force")] float jumpForce = 13f;
    [FlagsmithValue("gravity_scale")] float gravityScale = 3.2f;
    [FlagsmithValue("coin_value")] int coinValue = 1;
    [FlagsmithValue("double_jump")] bool doubleJump;           // bools follow the flag's on/off state
    [FlagsmithValue("spike_walls")] bool spikeWalls = true;    // kill switch
    [FlagsmithValue("winter_event")] bool winterEvent;         // a live event: the whole level changes
    [FlagsmithValue("player_skin")] string playerSkin = "green"; // green, pink, yellow, purple or beige
    string tutorial = "text";                                   // experiment: read only between runs
    readonly Dictionary<string, string> shown = new Dictionary<string, string>();

    FlagsmithBehaviour flagsmith;
    string setupError;

    // Run state
    enum State { Ready, Running, Dead }
    State state;
    float distance, vy, nextSpawn = 6f, deadAt, deadVy;
    int score, jumpsLeft, runs;
    bool grounded = true, goalSent, firstRun;

    Transform player, playerVisual, cam;
    SpriteRenderer playerSprite;
    Sprite walkA, walkB, jumpFrame, hitFrame;
    Vector3 camHome;
    float trauma, shakeT, nextGhost, screenFlash;
    string skinShown;
    bool winterShown;
    readonly List<(Transform t, char kind, Vector2 size)> things = new List<(Transform, char, Vector2)>();
    readonly List<SpriteRenderer> groundTop = new List<SpriteRenderer>(), groundFill = new List<SpriteRenderer>(),
        hills = new List<SpriteRenderer>(), clouds = new List<SpriteRenderer>();
    readonly List<Transform> snow = new List<Transform>();
    readonly Dictionary<string, Sprite> art = new Dictionary<string, Sprite>();
    readonly Dictionary<string, float> changedAt = new Dictionary<string, float>();
    // Clip-friendly demo: the game plays itself (press A, or open the WebGL build with ?autoplay)
    // and every flag change gets a big caption, so a recording needs no hands on the keyboard.
    bool autoplay;
    string toast;
    float toastAt = -10f;
    Sprite square, circle;
    GUIStyle big, small, panel;

    static readonly Color Sky = new Color32(195, 227, 255, 255), Frost = new Color(0.86f, 0.9f, 1f); // Frost tints the sky layers in winter

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() => new GameObject("Flag Runner").AddComponent<FlagRunner>();

    void Awake()
    {
        square = MakeSprite((x, y) => true);
        circle = MakeSprite((x, y) => (x - .5f) * (x - .5f) + (y - .5f) * (y - .5f) < .25f);
        foreach (var s in Resources.LoadAll<Sprite>("Art")) art[s.name] = s;

        // Closer than Unity's default view, so the character reads at a glance on a phone.
        Camera.main.orthographicSize = 4.2f;
        cam = Camera.main.transform;
        cam.position = camHome = new Vector3(0, -0.7f, cam.position.z);

        // Far to near: clouds, hills, ground. Background tiles are 4 units wide, ground tiles 1.
        for (var i = 0; i < 7; i++)
        {
            clouds.Add(Paint(Art("background_clouds"), new Vector2(-12 + i * 4, GroundY + 5.5f), 1f, -30));
            hills.Add(Paint(null, new Vector2(-12 + i * 4, GroundY + 1.5f), 1f, -20));
        }
        for (var i = 0; i < 27; i++)
        {
            groundTop.Add(Paint(null, new Vector2(-13 + i, GroundY - 0.5f), 1f, 0));
            groundFill.Add(Paint(null, new Vector2(-13 + i, GroundY - 1.5f), 1f, 0));
            groundFill.Add(Paint(null, new Vector2(-13 + i, GroundY - 2.5f), 1f, 0));
        }
        for (var i = 0; i < 70; i++)
        {
            var flake = Paint(circle, new Vector2(Random.Range(-9f, 9f), Random.Range(-5f, 5.5f)), Random.Range(0.06f, 0.14f), 8);
            flake.color = new Color(1, 1, 1, 0.9f);
            snow.Add(flake.transform);
        }

        player = new GameObject("Player").transform;
        player.position = new Vector3(PlayerX, GroundY + PlayerSize / 2, 0);
        playerSprite = Paint(null, Vector2.zero, 1f, 5, player);
        playerVisual = playerSprite.transform;

        ApplySkin();
        ApplyTheme();

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

        var live = Time.time > 1f; // the first values at startup are not "changes"
        foreach (var (key, value) in Tuning())
        {
            if (shown.TryGetValue(key, out var old) && old != value && live)
            {
                changedAt[key] = Time.unscaledTime;
                Pop(1.3f, 0.75f);
                toast = $"{key}   {old}  ->  {value}";
                toastAt = Time.unscaledTime;
            }
            shown[key] = value;
        }

        if (playerSkin != skinShown)
        {
            ApplySkin();
            if (live) { StartCoroutine(Burst(player.position, Art("star"), 10, 0.5f)); trauma = Mathf.Max(trauma, 0.3f); }
        }
        if (winterEvent != winterShown)
        {
            ApplyTheme();
            if (live) { screenFlash = 1f; trauma = Mathf.Max(trauma, 0.45f); }
        }
        foreach (var (t, kind, _) in things) // coins already on screen change with coin_value too
            if (kind == 'C') t.GetComponentInChildren<SpriteRenderer>().sprite = CoinSprite();

        if (!spikeWalls) // kill switch: walls already on screen go up in a puff right away
            for (var i = things.Count - 1; i >= 0; i--)
                if (things[i].kind == 'W')
                {
                    StartCoroutine(Burst(things[i].t.position, circle, 12, 0.22f));
                    Destroy(things[i].t.gameObject);
                    things.RemoveAt(i);
                }
    }

    (string key, string value)[] Tuning() => new[]
    {
        ("run_speed", Show(runSpeed)), ("jump_force", Show(jumpForce)), ("gravity_scale", Show(gravityScale)),
        ("coin_value", Show(coinValue)), ("player_skin", playerSkin), ("double_jump", Show(doubleJump)),
        ("spike_walls", Show(spikeWalls)), ("winter_event", Show(winterEvent)), ("tutorial_style", tutorial),
    };

    static string Show(object v) => v switch
    {
        float f => f.ToString("0.##"),
        bool b => b ? "on" : "off",
        _ => v?.ToString(),
    };

    // ---- look: skin and theme follow their flags ----

    Sprite Art(string name) => art.TryGetValue(name, out var s) ? s : square;

    void ApplySkin()
    {
        var skin = art.ContainsKey($"character_{playerSkin}_walk_a") ? playerSkin : "green"; // unknown value: keep playing
        walkA = Art($"character_{skin}_walk_a");
        walkB = Art($"character_{skin}_walk_b");
        jumpFrame = Art($"character_{skin}_jump");
        hitFrame = Art($"character_{skin}_hit");
        skinShown = playerSkin;
    }

    void ApplyTheme()
    {
        var top = Art(winterEvent ? "terrain_snow_block_top" : "terrain_grass_block_top");
        var fill = Art(winterEvent ? "terrain_snow_block_center" : "terrain_grass_block_center");
        var hill = Art(winterEvent ? "background_fade_trees" : "background_color_trees");
        foreach (var g in groundTop) g.sprite = top;
        foreach (var g in groundFill) g.sprite = fill;
        var tint = winterEvent ? Frost : Color.white;
        foreach (var h in hills) { h.sprite = hill; h.color = tint; }
        foreach (var c in clouds) c.color = tint;
        foreach (var f in snow) f.gameObject.SetActive(winterEvent);
        foreach (var (t, kind, _) in things)
            if (kind == 'D') t.GetComponentInChildren<SpriteRenderer>().sprite = Art(winterEvent ? "snow" : "bush");
        Camera.main.backgroundColor = Sky * tint; // the clouds tile fades into this colour at its top edge
        winterShown = winterEvent;
    }

    Sprite CoinSprite() => Art(coinValue >= 10 ? "gem_red" : coinValue >= 5 ? "gem_blue" : "coin_gold");

    // Jump so the peak of the arc lands on the next obstacle, using the live tuning values.
    bool AutoJump()
    {
        var g = gravityScale * 9.81f;
        var lead = runSpeed * (jumpForce / g) * 0.95f;
        foreach (var (t, kind, size) in things)
        {
            if (kind == 'C' || kind == 'D') continue;
            var dx = t.position.x - player.position.x;
            if (dx > 0 && dx < lead) return grounded;
        }
        return false;
    }

    void Update()
    {
        var pressed = Input.GetKeyDown(KeyCode.Space)
                      || Input.GetMouseButtonDown(0);
        if (Input.GetKeyDown(KeyCode.A)) autoplay = !autoplay;
        else if (autoplay && pressed) autoplay = false; // a real player's first tap takes over from the bot
        if (autoplay)
            pressed |= state == State.Ready || (state == State.Running && AutoJump())
                       || (state == State.Dead && Time.unscaledTime - deadAt > 1.2f);

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
                // knocked up and out of the level
                deadVy -= 30f * Time.unscaledDeltaTime;
                playerVisual.localPosition += Vector3.up * deadVy * Time.unscaledDeltaTime;
                playerVisual.Rotate(0, 0, 420f * Time.unscaledDeltaTime);
                if (pressed && Time.unscaledTime - deadAt > 0.6f) ResetRun();
                break;
        }
        Animate();
        Snow(Time.unscaledDeltaTime);
        Shake(Time.unscaledDeltaTime);
        screenFlash = Mathf.Max(0, screenFlash - Time.unscaledDeltaTime / 0.45f);
        playerVisual.localScale = Vector3.Lerp(playerVisual.localScale, Vector3.one * 1.3f, 1 - Mathf.Exp(-14f * Time.unscaledDeltaTime));
    }

    void Animate()
    {
        if (state == State.Dead) { playerSprite.sprite = hitFrame; return; }
        // The sprite has empty space under the feet, so it sits a little above the collision box's centre.
        var bob = state == State.Ready ? Mathf.Sin(Time.unscaledTime * 4f) * 0.04f : 0f;
        playerVisual.localPosition = new Vector3(0, 0.12f + bob, 0);
        playerSprite.sprite = !grounded ? jumpFrame : (int)(distance / 0.45f) % 2 == 0 ? walkA : walkB;
        playerVisual.localRotation = Quaternion.Euler(0, 0, grounded ? 0 : Mathf.Clamp(vy * 1.4f, -16f, 16f));
    }

    // Hold the first run until flags arrive (or 4 s pass offline), so the first tutorial shown is the real variant.
    bool WaitingForFlags => flagsmith != null && flagsmith.Flags.All.Count == 0 && Time.realtimeSinceStartup < 4f;

    void StartRun()
    {
        state = State.Running;
        runs++;
        // Autoplay is a bot, not a player: its runs never enter the experiment and never use up the player's first run.
        firstRun = false;
        if (autoplay) return;
        // The metric is "finished the very first run", so remember (per player, across sessions) that it has started.
        var firstRunKey = "flagrunner.first_run_started." + flagsmith?.Identifier;
        firstRun = PlayerPrefs.GetInt(firstRunKey, 0) == 0;
        PlayerPrefs.SetInt(firstRunKey, 1);
        PlayerPrefs.Save();
        // Count the exposure only if the tutorial on screen is the variant Flagsmith served this player
        // (not the offline default), so the experiment never credits a variant nobody saw.
        var served = flagsmith?.Flags.Get("tutorial_style");
        if (served != null && served.Value == tutorial) flagsmith.GetExperimentFlag("tutorial_style");
    }

    void Jump()
    {
        if (!grounded && jumpsLeft <= 0) return;
        if (!grounded) { jumpsLeft--; StartCoroutine(Burst(player.position + Vector3.down * 0.3f, Art("star"), 5, 0.3f)); } // the double jump
        grounded = false;
        vy = jumpForce;
        Pop(0.75f, 1.3f);
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
            if (!grounded) Pop(1.3f, 0.75f);
            p.y = floor; vy = 0; grounded = true; jumpsLeft = doubleJump ? 1 : 0;
        }
        player.position = p;

        Scroll(groundTop, step, 27f);
        Scroll(groundFill, step, 27f);
        Scroll(hills, step * 0.22f, 28f);
        Scroll(clouds, step * 0.08f, 28f);

        if (runSpeed >= TrailSpeed && Time.time >= nextGhost)
        {
            nextGhost = Time.time + 0.05f;
            StartCoroutine(Ghost());
        }

        if (distance >= nextSpawn) SpawnObstacle();

        var me = new Rect(p.x - PlayerSize * .4f, p.y - PlayerSize * .4f, PlayerSize * .8f, PlayerSize * .8f);
        for (var i = things.Count - 1; i >= 0; i--)
        {
            var (t, kind, size) = things[i];
            t.position += Vector3.left * step;
            if (t.position.x < -14) { Destroy(t.gameObject); things.RemoveAt(i); continue; }
            if (kind == 'D') continue; // decoration
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
            // Only the player's very first run counts: a later run reaching 100 m is not a "first run complete".
            if (firstRun) flagsmith?.TrackEvent("first_run_complete", Mathf.RoundToInt(distance));
            trauma = Mathf.Max(trauma, 0.25f);
            StartCoroutine(Burst(player.position, Art("star"), 12, 0.5f));
        }
    }

    static void Scroll(List<SpriteRenderer> tiles, float step, float span)
    {
        foreach (var tile in tiles)
        {
            var p = tile.transform.position;
            p.x -= step;
            if (p.x < -span / 2) p.x += span;
            tile.transform.position = p;
        }
    }

    void SpawnObstacle()
    {
        var x = 11f;
        if (spikeWalls && Random.value < 0.3f)
        {
            // A wall of two spiked blocks: too tall to clear without a well-timed jump.
            var wall = Thing('W', new Vector2(x, GroundY + 1f), new Vector2(0.7f, 2.0f));
            Paint(Art("block_spikes"), new Vector2(0, -0.5f), 1f, 2, wall);
            Paint(Art("block_spikes"), new Vector2(0, 0.5f), 1f, 2, wall);
        }
        else
        {
            var n = Random.Range(1, 3);
            for (var i = 0; i < n; i++)
            {
                var spike = Thing('S', new Vector2(x + i * 0.8f, GroundY + 0.3f), new Vector2(0.8f, 0.6f));
                Paint(Art("spikes"), new Vector2(0, 0.2f), 1f, 2, spike);
            }
        }
        for (var i = 0; i < 3; i++)
        {
            var coin = Thing('C', new Vector2(x + 3.5f + i * 0.8f, GroundY + 2.2f), Vector2.one * 0.45f);
            Paint(CoinSprite(), Vector2.zero, 0.9f, 2, coin);
        }
        var decor = Thing('D', new Vector2(x + Random.Range(5.5f, 7f), GroundY + 0.5f), Vector2.one);
        Paint(Art(winterEvent ? "snow" : "bush"), Vector2.zero, 1f, -5, decor);

        // Space obstacles by the current jump's length so the level stays playable under any live tuning.
        var airtime = 2f * jumpForce / (gravityScale * 9.81f);
        nextSpawn = distance + Random.Range(8f, 13f) * Mathf.Max(1f, airtime / 0.83f);
    }

    void Die()
    {
        state = State.Dead;
        deadAt = Time.unscaledTime;
        deadVy = 9f;
        trauma = 0.7f;
        StartCoroutine(HitStop(0.09f));
        StartCoroutine(Burst(player.position, Art("star"), 14, 0.35f));
    }

    void ResetRun()
    {
        foreach (var t in things) Destroy(t.t.gameObject);
        things.Clear();
        distance = 0; score = 0; vy = 0; grounded = true; nextSpawn = 6f; goalSent = false;
        player.position = new Vector3(PlayerX, GroundY + PlayerSize / 2, 0);
        playerVisual.localRotation = Quaternion.identity;
        Pop(0.6f, 1.4f);
        state = State.Ready;
    }

    // ---- juice ----

    void Pop(float sx, float sy) => playerVisual.localScale = new Vector3(sx, sy, 1) * 1.3f;

    void Shake(float dt)
    {
        trauma = Mathf.Max(0, trauma - 1.6f * dt);
        shakeT += dt * 30f;
        var s = trauma * trauma;
        cam.position = camHome + new Vector3(0.35f * s * Mathf.Sin(shakeT * 1.7f), 0.25f * s * Mathf.Sin(shakeT * 2.3f), 0);
    }

    void Snow(float dt)
    {
        if (!winterEvent) return;
        var drift = state == State.Running ? runSpeed * 0.25f : 0.6f;
        for (var i = 0; i < snow.Count; i++)
        {
            var p = snow[i].position;
            p.y -= (1.2f + (i % 5) * 0.35f) * dt;
            p.x -= (drift + Mathf.Sin(Time.unscaledTime * 1.3f + i) * 0.4f) * dt;
            if (p.y < -5.2f) p.y += 10.6f;
            if (p.x < -9.2f) p.x += 18.4f;
            snow[i].position = p;
        }
    }

    IEnumerator HitStop(float seconds)
    {
        Time.timeScale = 0.05f;
        yield return new WaitForSecondsRealtime(seconds);
        Time.timeScale = 1f;
    }

    IEnumerator CoinPop(Transform t)
    {
        var sr = t.GetComponentInChildren<SpriteRenderer>();
        var start = t.localScale;
        for (float k = 0; k < 1; k += Time.unscaledDeltaTime / 0.25f)
        {
            t.localScale = start * (1 + 0.8f * Mathf.Sin(k * Mathf.PI * 0.5f));
            t.position += Vector3.up * Time.unscaledDeltaTime * 2f;
            sr.color = new Color(1, 1, 1, 1 - k);
            yield return null;
        }
        Destroy(t.gameObject);
    }

    // The speed trail: a fading copy of the current frame, left behind as the level scrolls.
    IEnumerator Ghost()
    {
        var g = Paint(playerSprite.sprite, playerVisual.position, playerVisual.localScale.y, 4);
        g.transform.rotation = playerVisual.rotation;
        for (float k = 0; k < 1; k += Time.deltaTime / 0.28f)
        {
            g.color = new Color(1, 1, 1, 0.42f * (1 - k));
            g.transform.position += Vector3.left * runSpeed * 0.55f * Time.deltaTime;
            yield return null;
        }
        Destroy(g.gameObject);
    }

    IEnumerator Burst(Vector3 from, Sprite sprite, int count, float size)
    {
        var bits = new (Transform t, Vector3 v)[count];
        for (var i = 0; i < count; i++)
        {
            var a = Random.Range(0f, Mathf.PI * 2);
            bits[i] = (Paint(sprite, from, size * Random.Range(0.6f, 1.1f), 6).transform,
                new Vector3(Mathf.Cos(a), Mathf.Sin(a) + 0.6f, 0) * Random.Range(3f, 8f));
        }
        for (float life = 0; life < 0.7f; life += Time.unscaledDeltaTime)
        {
            for (var i = 0; i < count; i++)
            {
                bits[i].v.y -= 22f * Time.unscaledDeltaTime;
                bits[i].t.position += bits[i].v * Time.unscaledDeltaTime;
                bits[i].t.Rotate(0, 0, 500 * Time.unscaledDeltaTime);
                bits[i].t.localScale *= 1 - 1.2f * Time.unscaledDeltaTime;
            }
            yield return null;
        }
        foreach (var b in bits) Destroy(b.t.gameObject);
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

        if (screenFlash > 0) // a live event lands: one white flash over the whole level
        {
            GUI.color = new Color(1, 1, 1, screenFlash * screenFlash);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        GUI.DrawTexture(new Rect(20 * u, 12 * u, 44 * u, 44 * u), CoinSprite().texture);
        Outlined(new Rect(64 * u, 16 * u, 600 * u, 40 * u), $"{score}     {distance:0} m", Bold(small, 28 * u), u);

        if (state == State.Ready && WaitingForFlags)
            Outlined(new Rect(0, Screen.height * 0.4f, Screen.width, 60 * u), "Connecting to Flagsmith…", big, u);
        else if (state == State.Ready)
        {
            if (tutorial == "hand") DrawTapHand(u);
            else Outlined(new Rect(0, Screen.height * 0.4f, Screen.width, 60 * u), "TAP OR PRESS SPACE TO JUMP", big, u);
        }
        if (state == State.Dead && Time.unscaledTime - deadAt > 0.6f)
            Outlined(new Rect(0, Screen.height * 0.4f, Screen.width, 60 * u), "Tap to run again", big, u);
        // The bot's runs are not anyone's first run, so autoplay shows no banner.
        if (goalSent && !autoplay && distance < GoalMeters + 15f)
            Outlined(new Rect(0, Screen.height * 0.18f, Screen.width, 60 * u), firstRun ? "100 m! First run complete" : "100 m!", big, u);

        DrawFlagPanel(u);
        DrawToast(u);
        if (autoplay) Outlined(new Rect(24 * u, 58 * u, 300 * u, 30 * u), "AUTOPLAY: tap or Space to play", small, u);
        if (setupError != null) Outlined(new Rect(24 * u, Screen.height - 40 * u, Screen.width, 30 * u), setupError, small, u);
    }

    // White text with a dark edge, readable over the sky and over snow.
    static void Outlined(Rect r, string text, GUIStyle style, float u)
    {
        var d = Mathf.Max(1.5f, 2f * u);
        GUI.color = new Color(0.08f, 0.1f, 0.18f, 0.85f);
        GUI.Label(new Rect(r.x - d, r.y, r.width, r.height), text, style);
        GUI.Label(new Rect(r.x + d, r.y, r.width, r.height), text, style);
        GUI.Label(new Rect(r.x, r.y - d, r.width, r.height), text, style);
        GUI.Label(new Rect(r.x, r.y + d, r.width, r.height), text, style);
        GUI.color = Color.white;
        GUI.Label(r, text, style);
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
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, panel);
        GUI.Label(new Rect(x, y + 6 * u, w, 22 * u), "<color=#C5CCDD>changed in Flagsmith, no rebuild</color>", Centered(small));
        GUI.Label(new Rect(x, y + 28 * u, w, 40 * u), $"<color=#FFD54D><b>{toast}</b></color>", Centered(Bold(small, 28 * u)));
        GUI.color = Color.white;
    }

    static GUIStyle Centered(GUIStyle s) => new GUIStyle(s) { alignment = TextAnchor.MiddleCenter, richText = true };

    void DrawTapHand(float u)
    {
        // The "hand" variant: a pulsing tap marker instead of words.
        var k = Mathf.Repeat(Time.unscaledTime, 1.1f) / 1.1f;
        var c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var ring = Mathf.Lerp(20, 90, k) * u;
        GUI.color = new Color(1, 1, 1, 1 - k);
        GUI.DrawTexture(new Rect(c.x - ring, c.y - ring, ring * 2, ring * 2), circle.texture);
        GUI.color = new Color(0.08f, 0.1f, 0.18f, 0.9f);
        var dot = (k < 0.15f ? 30 : 36) * u;
        GUI.DrawTexture(new Rect(c.x - dot / 2, c.y - dot / 2 - (k < 0.15f ? 0 : 14 * u), dot, dot), circle.texture);
        GUI.color = Color.white;
        dot -= 8 * u;
        GUI.DrawTexture(new Rect(c.x - dot / 2, c.y - dot / 2 - (k < 0.15f ? 0 : 14 * u), dot, dot), circle.texture);
    }

    void DrawFlagPanel(float u)
    {
        var rows = Tuning();
        float w = 270 * u, h = (rows.Length + 2) * 24 * u + 16 * u, x = Screen.width - w - 16 * u, y = 16 * u;
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, panel);
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, panel); // twice: the default box is too faint over a bright sky
        var src = flagsmith == null ? "defaults" : flagsmith.Flags.Source.ToString().ToLowerInvariant();
        GUI.Label(new Rect(x + 10 * u, y + 6 * u, w, 28 * u), $"<b>Flagsmith</b>  <color=#C5CCDD>{src}</color>", small);
        for (var i = 0; i < rows.Length; i++)
        {
            var (key, val) = rows[i];
            var hot = changedAt.TryGetValue(key, out var at) && Time.unscaledTime - at < 1.5f;
            var colour = hot ? "#FFD54D" : "#FFFFFF";
            GUI.Label(new Rect(x + 10 * u, y + (i + 1.3f) * 24 * u, w, 28 * u),
                $"<color=#C5CCDD>{key}</color>  <color={colour}><b>{val}</b></color>", small);
        }
        if (flagsmith != null)
            GUI.Label(new Rect(x + 10 * u, y + (rows.Length + 1.3f) * 24 * u, w, 28 * u), $"<color=#AEB6CC>{flagsmith.Identifier}</color>", small);
    }

    static GUIStyle Bold(GUIStyle s, float size) => new GUIStyle(s) { fontSize = Mathf.RoundToInt(size), fontStyle = FontStyle.Bold };

    // ---- scene objects ----

    /// <summary>An obstacle, coin or decoration: an empty object at the centre of its hit box; its sprites are children.</summary>
    Transform Thing(char kind, Vector2 centre, Vector2 size)
    {
        var t = new GameObject(kind.ToString()).transform;
        t.position = centre;
        things.Add((t, kind, size));
        return t;
    }

    static SpriteRenderer Paint(Sprite sprite, Vector2 pos, float scale, int order, Transform parent = null)
    {
        var sr = new GameObject("Sprite").AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        sr.transform.SetParent(parent, false);
        sr.transform.localPosition = pos;
        sr.transform.localScale = new Vector3(scale, scale, 1);
        return sr;
    }

    // Plain shapes for particles and the tap marker.
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
        return UnityEngine.Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(.5f, .5f), n);
    }
}
