# Contributing

Thanks for looking. This project is deliberately small: it packages someone else's engine and
adds an HTTP contract around it. The most useful contributions usually start with an issue.

## Before you write code

- **A bug in the converted markdown is not a bug here.** Parsing, layout, OCR and tables belong to
  [marker](https://github.com/datalab-to/marker); see [UPSTREAM.md](UPSTREAM.md) for where each
  kind of report belongs, and for the dependency override you must disclose if you report upstream.
- **The engine pin is not bumped casually.** It changes conversion output for every user. Read the
  bump procedure in `UPSTREAM.md` before opening that PR.

## Build and test

```bash
dotnet build BookMarker.slnx
dotnet test BookMarker.slnx -c Release
dotnet format BookMarker.slnx --verify-no-changes
```

The test suite runs the real service pipeline against `tests/MarkerStub`, a stand-in for the
`marker` CLI. That is why the contract tests need no GPU, no model weights and no network. If you
change behaviour around the engine, extend the stub and add a test rather than mocking the
service.

What the suite cannot cover — conversion quality, timing, GPU memory behaviour — is checked
manually against a real container (`./build-and-run.sh`, `./scripts/acceptance.sh`).

## What a good pull request looks like

- One change, with a sentence saying **why** in the description.
- The three gates above pass, and you paste the output rather than asserting it.
- Behaviour changes come with the documentation change: `README.md` for users, `SPEC.md` for the
  contract, `CHANGELOG.md` under `[Unreleased]`.
- If you add a configuration knob, it appears in **all four** places or not at all:
  `BookMarkerOptions`, the Dockerfile `ENV` (if it should have an image default), the example
  compose file, and the README table.
- No version numbers in code or docs: `version.txt` is the only one. The engine version lives with
  the pin in the `Dockerfile` and `UPSTREAM.md`.

## Style

`dotnet format` is the arbiter — run it, don't argue with it. Comments explain *why* something is
the way it is; the code already says what it does.

## Licence

Contributions are accepted under GPL-3.0-or-later, the licence of this repository and of the
engine it packages.
