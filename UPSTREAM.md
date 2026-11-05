# Upstream: marker

BookMarker is packaging around marker, not a fork. This file is the record of what's pinned, what's
changed, and where each kind of bug belongs.

| | |
| --- | --- |
| Upstream project | marker — <https://github.com/datalab-to/marker> |
| Upstream package | `marker-pdf` |
| Pinned version | **1.10.2** |
| Licence | GPL-3.0-or-later, same as this repository |
| Patch status | unmodified — no upstream file is edited |
| Affiliation | none |

## What lives here

- the container image: CUDA base, engine install, healthcheck, compose file;
- the HTTP API: `POST /convert`, `GET /health`, `GET /api/status`, the status page, the status
  codes and `X-*` headers, and the `503` + `Retry-After` refusal;
- the LLM policy (`nollm` / `llmonly` / `llmfallback`);
- `llm_shim/marker_openai_service.py`, a marker LLM service that keeps the strict `json_schema`
  call and falls back to `json_object` for backends that reject it;
- scratch cleanup, memory caps, and the rest of the operational bits.

## What lives upstream

PDF parsing, layout detection, reading order, OCR, tables, equations, image extraction, the
`marker` CLI and its flags (`--page_range`, `--workers`, `--disable_multiprocessing`,
`--disable_image_extraction`, `--use_llm`, `--llm_service`, `--config_json`), the quality of the
output, the speed, and the model weights. None of it starts here.

## The pinned engine

| | |
| --- | --- |
| Artifact | <https://files.pythonhosted.org/packages/04/bc/a9e72fd015f562b1f86c9dbc201c80365e7baf43d5aa616ba1c89453537f/marker_pdf-1.10.2.tar.gz> |
| SHA-256 | `ce0fc839e11ad7519a576d254ca9d51a0f9454b9d7da02211f722b141317f9f1` |
| Checked by | the `Dockerfile` — `ADD --checksum=sha256:…` fails the build on a mismatch |
| Pinned on | 2026-10-16, re-checked 2026-11-04 |
| Source of record | the artifact URL above |

The engine source isn't committed here. The build downloads it from that URL, so the image can
only differ from upstream by the dependency override below.

## The one change: a dependency override

After installing marker, the image installs:

```text
pdftext==0.7.1
pypdfium2==5.10.1
```

marker 1.10.2 asks for `pdftext ~0.6.3`. The override fixes an infinite link-extraction hang on
some PDFs. It's outside what upstream declares and tests, and they don't have to care about it.

It replaces surya's own pin too. surya-ocr 0.17.1 declares `pypdfium2==4.30.0`, so 5.10.1 is
installed over that as well — which also means the two can't be requested in one `pip` command:
pip would call it unsatisfiable and refuse. The override is a second, later step on purpose.

So if you report a conversion bug upstream, mention it:

> BookMarker 1.10.2 container; marker-pdf 1.10.2 unmodified; `pdftext` 0.7.1 and `pypdfium2` 5.10.1
> installed over the declared range (link-extraction hang workaround). Reproduces outside
> BookMarker: [yes/no]

## Where to report what

| Symptom | Where |
| --- | --- |
| Wrong heading levels, reading order, tables, OCR or equations | [upstream marker](https://github.com/datalab-to/marker/issues), mentioning the `pdftext` override |
| A marker CLI flag not behaving as its docs say | upstream |
| `503` when you wanted a conversion, a wrong status code, a missing `X-*` header | here — that's the contract in [SPEC.md](SPEC.md) |
| Container won't start, health never passes, GPU not picked up | here |
| Your OpenAI-compatible backend being rejected | here if it's the shim's `json_schema` → `json_object` fallback, upstream if not |
| Running out of memory on a large PDF | either, but the answer is usually `page_range` or a bigger memory limit — see the README |

## Reproducibility

A conversion run must not depend on the network, and two builds of the same commit should produce
the same output. Everything below is pinned to that end.

- **The engine artifact.** Its SHA-256 is in the `Dockerfile` as `ADD --checksum`.
- **The base image**, by digest as well as tag (`pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime`
  `@sha256:2b59b1b9…`). That digest pins torch, torchvision, numpy and the rest of the base's 115
  packages — the tag alone is a promise the publisher can break, the digest is the bytes.
- **Every Python package installed on top of it**, by version and by artifact hash, in
  `docker/requirements.lock`. The build installs that file with `--require-hashes`, so PyPI has to
  serve exactly those files; a re-uploaded or substituted artifact fails the build.
- **The surya release**, `surya-ocr==0.17.1`, which names the model checkpoint paths. It's pinned
  in the lock like everything else, so the paths come from a file in this repository rather than
  from whatever pip resolved today.
- **The model artifacts**, by content: `docker/model-artifacts.lock` holds a SHA-256 over the
  artifact tree and one over the render font, and the build compares them after downloading.
- **That they load offline.** The step after the download loads every model under
  `--network=none`.
- **The app's version and commit**, from `version.txt` and the build's `SOURCE_REVISION`.

Two deps are deliberately outside the ranges their packages declare, and both are recorded in the
lock rather than applied at install time: `pdftext==0.7.1` (the link-extraction hang) and
`pypdfium2==5.10.1` (surya 0.17.1 declares `==4.30.0`). Because they contradict metadata, the lock
is installed with `--no-deps`: it already is the whole closure, and asking pip to re-resolve it
would fail on exactly that contradiction. The completeness check is the build's own functional
steps — `marker --help`, then all five models loaded with the network off — which import the
entire stack.

The weights aren't stored in this repository. The largest artifact file is 1.35 GB and GitHub
refuses anything over 100 MiB; the set is 3.3 GB, past the free Git LFS allowance and a download
everyone who clones would pay for. Recording their identity buys the same guarantee for a few
lines of text.

The one thing not pinned is your graphics driver, which no image can control. If a conversion
differs between two machines with the same image digest, that's where to look first.

## Bumping the pin

1. Read upstream's release notes. Anything that touches the flags BookMarker passes is a change
   here too.
2. Download the new sdist. Record its URL and SHA-256 here and in the `Dockerfile`, in both
   `ADD --checksum` and the `MARKER_VERSION` argument.
3. Check whether the `pdftext` override is still needed. If it isn't, drop it. Don't keep it out of
   habit.
4. If the new release names different surya checkpoints, update the `surya-ocr` pin in
   `docker/requirements.lock` to match.
5. Regenerate the lock. The versions in it are the ones the previous image actually contained, so
   after a bump they have to be re-derived rather than guessed:

   ```bash
   # build the image as it now stands, then diff its packages against the base image's
   docker build -t bookmarker:relock .
   docker run --rm --entrypoint pip <base-ref> freeze | sort > /tmp/base.txt
   docker run --rm --entrypoint pip bookmarker:relock freeze | sort > /tmp/final.txt
   ```

   Anything in `final.txt` that is missing from `base.txt`, or present at a different version,
   belongs in the lock. Hash each one with `pip download --no-deps "<name>==<version>"` followed
   by `sha256sum`, and read the diff before committing — it should be the change you intended, not
   forty packages moving because a range drifted.
6. Build. If the artifact check fails, decide whether the new weights are intended: if they are,
   regenerate `docker/model-artifacts.lock` with
   `docker run --rm --network none --entrypoint python <image> /usr/local/bin/preload_models.py --print`
   and commit the new hashes in the same change as the version bump, so the diff shows what
   changed.
7. `./build-and-run.sh`, then `./scripts/acceptance.sh`. The acceptance script compares the
   service's output against a direct `marker` run inside the container, which is the real
   regression test for a bump.
8. Put the engine version in `CHANGELOG.md` for that release.
