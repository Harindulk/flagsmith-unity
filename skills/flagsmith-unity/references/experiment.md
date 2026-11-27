# A/B test a Unity game with Flagsmith Experimentation

Needs setup.md done (package installed, `FlagsmithBootstrap` present, `flagsmith.json` in the project root). Run `scripts/experiment.sh` from the project root; it only uses the Flagsmith CLI and jq.

## 1. Design it with the user

Agree on four things and say them back in one line:

- **Hypothesis:** what should improve and why. Example: "A pointing-hand tutorial gets more new players through their first run than a text hint."
- **Flag and variants:** a snake_case flag, the control value (what ships today) and one challenger. Example: `tutorial_style`, `text` vs `hand`.
- **Metric event:** one event that fires early, when the player succeeds. Example: `first_run_complete`. Never start it with `$`.
- **Where exposure happens:** the exact moment the player sees the difference, not game start.

## 2. Create and start it (after the user's OK)

```sh
scripts/experiment.sh create tutorial_style text hand first_run_complete "A pointing-hand tutorial gets more new players through their first run than a text hint"
```

Without `FLAGSMITH_YES=1` the script prints what it will do and stops. Show that to the user; re-run with `FLAGSMITH_YES=1` once they agree. It creates the multivariate flag (control as the default value, the challenger as a keyed variant), a metric on the event, a 50/50 experiment with 100% enrolment, and starts it. On self-hosted it also connects Flagsmith's own database as the event store if none exists.

## 3. Wire the game

```csharp
// At the moment the player sees the tutorial (records the exposure for enrolled players):
var style = FlagsmithBootstrap.Instance.GetExperimentFlag("tutorial_style");
ShowTutorial(style?.Value ?? "text");

// When the player succeeds:
FlagsmithBootstrap.Instance.TrackEvent("first_run_complete");
```

Rules that keep the result clean:

- Call `GetExperimentFlag` only where the variant is shown, and show the value it returned.
- Flags arrive a moment after launch. If the variant shows at startup, wait for the first `FlagsChanged` (or about 4 s offline) before showing it, or the player sees the default and no exposure is recorded.
- Fire the metric once per player per success (for "first run", guard it with a saved flag such as `PlayerPrefs`).
- Players keep one ID across sessions automatically, so they stay in one variant.

## 4. Check the split

```sh
for i in $(seq 1 20); do flagsmith eval tutorial_style --identity qa-$i --jq .variant; done | sort | uniq -c
```

Expect both variants, roughly 10 each (`control` is the control value). Only one: the experiment is not running, or the variant has no key. `eval` does not save these identities.

## 5. Read the result

```sh
scripts/experiment.sh results tutorial_style
```

It refreshes the numbers (Flagsmith rate-limits refreshes, so they can lag a few minutes) and prints a summary. Read out each variant's conversion rate, the lift over control, the chance to be best and the credible interval. `conclusive` is true when the interval excludes zero. If it is false, say so and suggest waiting for more players, unless the user set a decision rule up front (for example "ship at 95% chance to win with 200+ players per arm"); then apply that rule and quote it. Never invent traffic. If the user wants a demo with simulated players, they must label it as simulated wherever they show it.

## 6. Ship the winner (after the user's OK)

```sh
scripts/experiment.sh ship tutorial_style hand        # or: ship tutorial_style control
```

This completes the experiment in Flagsmith, removes its rollout and serves the winner to everyone through the same flag, with no rebuild. Confirm with `flagsmith eval tutorial_style --identity anyone --jq .value`. Then offer to remove the losing code path in a follow-up change.
