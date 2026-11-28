# Changelog

All notable changes to this package are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Editor window: per-player mode. Enter a player ID (or press Running in Play mode) to list the flags as that player gets them, save an override for that player only, and clear it.

## [0.1.0] - 2026-11-27

### Added
- `FlagsmithClient`: environment and identity flags with traits, typed `GetValue<T>`, invariant-culture parsing, one retry on network errors and HTTP 5xx.
- Offline support: the last good response per identity is cached on disk, with defaults when there is no cache.
- Experimentation: `GetExperimentFlag` and `TrackExposure` record `$flag_exposure` events for enrolled identities; `TrackEvent` records metric events. Events are batched, de-duplicated, split at 2 MiB, dropped after 10 days, retried after network failures and saved to disk across restarts.
- `FlagsmithBehaviour` component: persistent player identity, background refresh (Live Tuning), `FlagsChanged` event, automatic flushing on a timer and on pause, focus loss and quit.
- `[FlagsmithValue]` attribute and `FlagsmithBehaviour.Bind` to keep fields and properties in sync with flags.
- Editor window (Window > Flagsmith) to list, toggle and edit flags through the Management API. The admin token is stored in `EditorPrefs` only.
- Quickstart sample.

### Security
- The client refuses server-side environment keys.
