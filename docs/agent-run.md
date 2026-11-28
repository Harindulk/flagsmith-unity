# One recorded agent run

This is the record of a coding agent using the [Agent Skill](../skills/flagsmith-unity/SKILL.md) to check an A/B test against a rule and ship the winner. It ran on 27 November 2026 against self-hosted Flagsmith 2.295.2, with Claude Code (model `claude-sonnet-5-5`) in headless mode. The agent had the skill, the Flagsmith CLI v2 and nothing else from this repo.

| | |
|---|---|
| Wall time | 20 seconds |
| Turns | 8 |
| Output tokens | 1,522 |
| Input tokens | 47,237 new, plus 301,621 read from the prompt cache |
| Cost | $0.23 |
| Flagsmith commands run | 5: `flagsmith config`, `experiment.sh results`, `experiment.sh ship`, `flagsmith eval`, and one `cat flagsmith.json`. The agent's own words are quoted; its file reads of the skill are left out |

## The prompt

> Our tutorial_style A/B test in Flagsmith has been running. We agreed this decision rule before starting: ship a variant when it has at least a 95% chance to be best and every arm has at least 200 players. Check the experiment against that rule. If it passes, ship the winner; you have my OK to do that in the Development environment. Tell me the numbers you based the decision on.

## What the agent did

**1. Looked at where it was.** It loaded the skill, read `references/experiment.md`, and ran:

```sh
cat flagsmith.json; flagsmith config
```

```
Project        Flag Runner (1)
Environment    Development (hndgnTTxBrjQ26JKDWjUFG)
API            http://localhost:8000
```

Agent: "Environment is Development (Flag Runner, project 1). Reading results is a read-only step."

**2. Read the results.** One command, one line of JSON back:

```sh
scripts/experiment.sh results tutorial_style
```

```json
{"as_of":"2026-11-27T05:02:27.118998Z","metrics":[{"metric_id":1,"players":{"control":{"conversions":214,"n":399,"rate_pct":53.6},"hand":{"conversions":268,"n":417,"rate_pct":64.3}},"vs_control":{"hand":{"chance_to_win_pct":99.7,"conclusive":true,"interval_pct":[5.9,33.7],"lift_pct":19.8}}}]}
```

The script asks Flagsmith to refresh the experiment, then cuts the API's response down to these fields with `jq`. `results` also sends two refresh requests, which change nothing but the cached numbers. The filtering is where the token saving comes from: measured on 28 November, the raw results response for this experiment is 1,458 bytes and the script's output is 294.

Agent: "The rule passes: 99.7% chance to win, with 399 and 417 players per arm. You approved shipping in Development, so I'm shipping now."

**3. Shipped the winner and checked it.**

```sh
FLAGSMITH_YES=1 scripts/experiment.sh ship tutorial_style hand; flagsmith eval tutorial_style --identity anyone --jq .value
```

```
About to: complete experiment 1 and serve 'hand' to everyone through tutorial_style (environment: Development)
{"name":"Tutorial style","status":"completed"}
hand
```

## What the agent reported

> The `tutorial_style` test passes the rule, and I shipped `hand` to everyone in the Development environment (Flag Runner, project 1). The experiment now shows as completed. Checking with `flagsmith eval tutorial_style --identity anyone` returns `hand`.
>
> | Arm | Players | Conversions | Conversion rate |
> |---|---|---|---|
> | control (`text`) | 399 | 214 | 53.6% |
> | `hand` | 417 | 268 | 64.3% |
>
> - **Chance to be best:** `hand` has a 99.7% chance, against the 95% threshold.
> - **Lift over control:** +19.8% relative, with a credible interval of +5.9 to +33.7. The interval excludes zero, so Flagsmith marks it conclusive.
> - **Sample size:** both arms are above the 200-player minimum.
>
> Production is untouched; I only changed Development. To carry this to production, say so and I'll show the command before running it.
>
> I haven't removed the losing `text` code path from the game.

The players in this experiment are synthetic (see the [README](../README.md#ab-testing-with-flagsmith-experimentation)). The agent run itself is real.

## Where a human approves

In this run the approval was in the prompt ("you have my OK to do that in the Development environment"). Without it, every write stops. This is what the script prints when the agent has no approval yet:

```
$ scripts/experiment.sh ship tutorial_style hand
About to: complete experiment 1 and serve 'hand' to everyone through tutorial_style (environment: Development)
error: nothing changed. Re-run with FLAGSMITH_YES=1 once the user has approved this.
```

And on an environment whose name looks like production (`prod`, `prd` or `live`, in any case), approval alone is not enough:

```
$ FLAGSMITH_YES=1 scripts/experiment.sh ship tutorial_style hand
About to: complete experiment 1 and serve 'hand' to everyone through tutorial_style (environment: Production)
error: 'Production' looks like production. Re-run with FLAGSMITH_CONFIRM_ENV='Production' only if the user explicitly asked for it.
```

Both refusals are covered by [`tests/test_scripts.py`](../tests/test_scripts.py). The agent sets `FLAGSMITH_YES` itself, so the script only forces it to stop and say what it is about to change. To make the approval a hard stop, Claude Code users can add the permission rule from [SKILL.md](../skills/flagsmith-unity/SKILL.md), which makes the client ask the human before every approved write.

## The setup job

The skill's other job is setting a game up from nothing. On a fresh Unity project called Jumper, one prompt ("Set up Flagsmith in this Unity game so I can tune it live without rebuilding...") had the agent read the game's code, create four flags through the CLI (`move_speed`, `jump_height`, `coin_reward`, `enable_dash`), install the package and bind the C# fields. It took 84 seconds, 14 turns, 5,016 output tokens and 56,603 new input tokens (679,911 from cache), and cost $0.34. The project it produced compiles in Unity 6000.6.
