---
name: flagsmith-unity
description: Set up Flagsmith feature flags, remote config and live tuning in a Unity project, and run A/B tests on a Unity game with Flagsmith Experimentation, using the Flagsmith CLI and the Flagsmith for Unity package. Use when the user wants to add Flagsmith to a Unity game, make gameplay values tunable without a rebuild, add a kill switch, or A/B test something in a Unity game.
license: MIT
compatibility: Needs a Unity 6 (6000.0+) project, the Flagsmith CLI v2 (flagsmith on PATH), jq, and a Flagsmith Cloud account or a self-hosted Flagsmith 2.263+. Experiments need Flagsmith Experimentation.
metadata:
  package: https://github.com/Harindulk/flagsmith-unity
  version: "0.1.0"
---

# Flagsmith for Unity

Two jobs. Pick one, read only its reference file, and finish it end to end.

| The user wants to | Read |
|---|---|
| Add Flagsmith to a Unity game, make values tunable, add a kill switch | [references/setup.md](references/setup.md) |
| A/B test something in a Unity game, read the result, ship the winner | [references/experiment.md](references/experiment.md) (needs setup done) |

## Safety rules (always)

1. **Read before you write.** Start with `flagsmith auth status` and `flagsmith config`. Say which project and environment you will change.
2. **Every write needs the user's OK.** Show the exact commands first. `scripts/experiment.sh` refuses to write until you re-run it with `FLAGSMITH_YES=1`, which you only do after the user agrees.
3. **Production is off limits by default.** Work in a Development or Staging environment. If the environment's name contains "prod" or "live", stop and ask; the script additionally needs `FLAGSMITH_CONFIRM_ENV=<exact name>`.
4. **Only client-side keys go into the game.** The environment key from `flagsmith environment list` is public and safe in a build. Never put a server-side key (`ser.…`), a Master API key or a login token in project files, scripts or chat. Credentials stay in the CLI's keyring or an environment variable.
5. **Never delete** features, segments or experiments unless the user asked for that exact deletion.

## CLI basics

- Install: `curl -fsSL https://get.flagsmith.com | sh` (or `go install github.com/Flagsmith/flagsmith-cli/v2@v2.0.0`).
- Flagsmith Cloud: the user runs `flagsmith login` (browser). Self-hosted: the user sets `FLAGSMITH_API_URL` and a Master API key in `FLAGSMITH_API_KEY_<host>_<port>` (for example `FLAGSMITH_API_KEY_localhost_8000`).
- Run commands from the Unity project root after `flagsmith init`, which writes `flagsmith.json` (project ID and client key, safe to commit).
- Keep output small: use `--jq` on every read, for example `flagsmith feature list --jq '.[].name'`.
