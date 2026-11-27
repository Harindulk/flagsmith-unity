# Set up Flagsmith in a Unity project

Goal: the game reads chosen values from Flagsmith, keeps working offline, and picks up changes while it runs.

## 1. Check

```sh
cat ProjectSettings/ProjectVersion.txt          # needs 6000.0 or newer
flagsmith --version && flagsmith auth status
```

Not logged in: ask the user to run `flagsmith login` (Cloud) or to set the self-hosted variables from SKILL.md. Wait for them.

## 2. Bind the project

```sh
flagsmith project list --jq '.[] | {id, name}'
flagsmith environment list -p <project> --jq '.[] | {name, api_key}'
flagsmith init --project <project> --environment <Development api_key>
```

Ask which project if there is more than one. Use the Development environment unless the user names another.

## 3. Install the package

Add this line to `dependencies` in `Packages/manifest.json` (keep the JSON valid):

```json
"com.flagsmith.unity": "https://github.com/Harindulk/flagsmith-unity.git?path=/com.flagsmith.unity"
```

## 4. Add the bootstrap

Create `Assets/Flagsmith/FlagsmithBootstrap.cs`. Put in the client key from `flagsmith.json` (`environment`). For self-hosted, also set `ApiUrl` to `<FLAGSMITH_API_URL>/api/v1/`; for Cloud, leave `ApiUrl` out.

```csharp
using Flagsmith;
using UnityEngine;

public static class FlagsmithBootstrap
{
    public static FlagsmithBehaviour Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Create()
    {
        var go = new GameObject("Flagsmith");
        Object.DontDestroyOnLoad(go);
        Instance = go.AddComponent<FlagsmithBehaviour>();
        // Client-side key: public by design. 2 s refresh in development builds for live tuning, 60 s in release.
        Instance.Init(new FlagsmithOptions { EnvironmentKey = "CLIENT_KEY" }, refresh: Debug.isDebugBuild ? 2f : 60f);
    }
}
```

## 5. Pick what to make tunable

Find gameplay numbers and switches:

```sh
grep -rnE --include='*.cs' '^\s*(\[[^]]*\]\s*)*((public|private|protected|internal) )?(float|int|bool) \w+ = [^;(]+;' Assets
```

Skip anything in `Packages/`, `Editor/` or third-party folders. Propose at most five, with a snake_case flag name and the current value as the default, and let the user choose. Good picks: speeds, jump or gravity values, prices and rewards, spawn rates, and a bool for any feature that might need a kill switch.

## 6. Create the flags (after the user's OK)

```sh
flagsmith feature create run_speed --value 7 --enabled --description "Player run speed"
flagsmith feature create double_jump --enabled          # a bool member follows the on/off state
```

Skip names that `flagsmith feature list --jq '.[].name'` already shows.

## 7. Bind the fields

In each class that owns a chosen field: add `using Flagsmith;`, put `[FlagsmithValue("<flag>")]` on the field (the initialiser stays as the offline default), and bind once:

```csharp
[FlagsmithValue("run_speed")] [SerializeField] float runSpeed = 7f;

void Awake() => FlagsmithBootstrap.Instance.Bind(this);
```

If the class already has `Awake`, add the `Bind` call to it. For code that must react when a value changes (UI, spawners), subscribe to `FlagsmithBootstrap.Instance.FlagsChanged`.

## 8. Verify and hand over

```sh
flagsmith evaluate --jq '.flags | map_values(if .value == null then .enabled else .value end)'   # what the game will receive
```

If a Unity Editor is open or the `unity` CLI is available, let it compile and check the Console for errors. Then tell the user:

> Press Play, then run `flagsmith flag update run_speed --value 12`. The running game changes within two seconds. `flagsmith flag disable double_jump` is your kill switch.

Report what you changed: the files, the flags created, and the environment.
