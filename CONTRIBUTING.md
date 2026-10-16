# Contributing

Thanks for looking. The project is deliberately small: it packages someone else's engine and puts
an HTTP API around it. Most useful contributions start as an issue.

## Before you write code

- **A bad conversion isn't a bug here.** Parsing, layout, OCR and tables belong to
  [marker](https://github.com/datalab-to/marker). [UPSTREAM.md](UPSTREAM.md) has the split, and the
  dependency override to mention if you report upstream.
- **Don't bump the engine pin casually.** It changes output for everyone. Read the bump steps in
  `UPSTREAM.md` first.

## Build and test

```bash
dotnet build BookMarker.slnx
dotnet test BookMarker.slnx -c Release
dotnet format BookMarker.slnx --verify-no-changes
```

The tests run the real pipeline against `tests/MarkerStub`, a stand-in for the `marker` CLI, which
is why they don't need a GPU, model weights or network. If you change something around the engine,
extend the stub and add a test rather than mocking the service.

Conversion quality, timing and GPU memory are checked by hand against a real container:
`./build-and-run.sh`, then `./scripts/acceptance.sh`.

## What a good pull request looks like

- One change, and a sentence in the description saying why.
- The three commands above pass. Paste the output; don't assert it.
- Behaviour changes bring the docs with them: `README.md` for users, `SPEC.md` for the contract,
  `CHANGELOG.md` under `[Unreleased]`.
- A new configuration knob goes in all four places or none: `BookMarkerOptions`, the Dockerfile
  `ENV` (if it should have an image default), the example compose file, and the README table.
- No version numbers in code or docs. `version.txt` is the only one. The engine version lives with
  the pin, in the `Dockerfile` and `UPSTREAM.md`.

## Style

`dotnet format` decides. Comments explain why something is the way it is — the code already says
what it does.

## Licence

Contributions come in under GPL-3.0-or-later, same as the rest of the repository and the engine it
packages.
