# The tutorial test on real players

The [hosted demo](https://harindulk.github.io/flagsmith-unity/) runs the `tutorial_style` experiment on Flagsmith Cloud (free plan), with the same setup as the self-hosted demo: a 50/50 split between `text` (control) and `hand`, the `$flag_exposure` event when the tutorial appears, and `first_run_complete` when a player's first run reaches 100 m.

| | |
|---|---|
| Started | 28 November 2026 |
| Where | Flagsmith Cloud, project "Flag Runner", environment "Production", experiment "Tutorial style" |
| Who counts | A visitor who takes over from autoplay and starts a run of their own. Each browser counts once. Autoplay runs and automated browsers never join |
| What is sent | A random player ID, the variant shown, and whether the first run reached 100 m |
| Decision rule | The same as the simulation: ship a variant at 95% chance to be best, with at least 200 players per arm |

## Results

No players yet: the test went live with this page. This table is updated by hand from the experiment's results page.

| Date (IST) | `text` finished / players | `hand` finished / players | Chance to be best |
|---|---|---|---|
| 28 Nov | 0 / 0 | 0 / 0 | not enough data |

With a hackathon weekend of traffic this will stay far below 200 players per arm, so expect "not enough data" for a while. The [simulated run](../README.md#the-demo-experiment) shows the loop at full size.

## How the playground reaches Flagsmith Cloud

The playground's WebGL build is made against a stand-in URL that the page answers, so the flag panel can be local to each visitor. For the experiment the page forwards two things to Flagsmith Cloud unchanged: the first `identities/` lookup of each page load (for `tutorial_style` and its experiment data) and every `events/` batch. The code is in [`site/play/index.html`](../site/play/index.html). The page waits four seconds for Cloud's answer. After that the visitor gets the text tutorial outside the experiment and none of their events are forwarded. A forwarded event that fails is not retried, so the real count can be slightly lower than the number of people who played. A game built against Flagsmith Cloud directly needs none of this: the package sends both by itself, which the live-server test checks.
