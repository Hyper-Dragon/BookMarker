## What and why

<!-- One change, with a sentence on why it is needed. -->

## Gates

- [ ] `dotnet format BookMarker.slnx --verify-no-changes`
- [ ] `dotnet build BookMarker.slnx -c Release -warnaserror`
- [ ] `dotnet test BookMarker.slnx -c Release`

Test summary:

```text
(paste the "Passed! - Failed: 0, Passed: N" line)
```

## Documentation

- [ ] `CHANGELOG.md` updated under `[Unreleased]`
- [ ] `README.md` / `SPEC.md` updated if behaviour, configuration or the contract changed
- [ ] No version number written anywhere except `version.txt`

## Engine pin

- [ ] Unchanged
- [ ] Changed — the `Dockerfile` version, URL and checksum and `UPSTREAM.md` were updated in
      the same commit, and `scripts/acceptance.sh` was run against the new image
