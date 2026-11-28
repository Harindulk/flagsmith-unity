# Flagsmith for Unity

Feature flags, remote config, Live Tuning and A/B testing with [Flagsmith](https://flagsmith.com) for Unity 6 and newer. Tested against self-hosted Flagsmith, including WebGL builds. Flagsmith Cloud uses the same client API but has not been tested yet.

- Add a `FlagsmithBehaviour`, paste your client-side environment key, and read flags with `Flags.GetValue("run_speed", 7f)` or bind fields with `[FlagsmithValue("run_speed")]`.
- Record experiment exposures with `GetExperimentFlag` and conversions with `TrackEvent`.
- Edit flags from Window > Flagsmith.

Guide and API reference: [Documentation~/index.md](Documentation~/index.md). Source, demo game and self-hosting setup: https://github.com/Harindulk/flagsmith-unity

Community package, not an official Flagsmith SDK. MIT licensed.
