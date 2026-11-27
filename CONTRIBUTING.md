# Contributing

Thanks for helping. Bug reports with steps to reproduce, and small focused pull requests, are the most useful.

## Development setup

You need Unity 6000.6, Docker and Python 3.

```sh
cd server
docker compose up -d
python3 seed.py
```

Open `Sample/` in Unity. It references the package from `../com.flagsmith.unity` as a local package, so edits to the package show up straight away.

## Running the tests

In Unity: Window > General > Test Runner > EditMode > Run All.

From the command line, with the [Unity CLI](https://docs.unity.com/):

```sh
unity test Sample --mode EditMode --output test-results.xml
```

`LiveServerTests` talks to the local server and is skipped when `server/.seed.json` is missing. Everything else uses a fake transport and needs no network.

## Guidelines

- Keep the runtime free of threads, `Task.Delay` and native code so it keeps working on WebGL.
- Add a test for every bug fix and every new public method.
- Public API changes go in `com.flagsmith.unity/CHANGELOG.md` under Unreleased.
- Never commit environment keys, admin tokens or `server/.seed.json`.
