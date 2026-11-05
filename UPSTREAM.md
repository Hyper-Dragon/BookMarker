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

A conversion run must not depend on the network, and two builds of the same commit should
produce the same output. Here's how far that goes.

Pinned:

- **The engine artifact.** Its SHA-256 is in the `Dockerfile` as `ADD --checksum`.
- **The surya release** that names the checkpoint paths (`surya-ocr==0.17.1`).
- **The model artifacts themselves**, by content. `docker/model-artifacts.lock` records a
  SHA-256 over the whole artifact tree and one over the render font. The build fetches the
  weights, then checks them; a mismatch stops the build and names both hashes. So the host
  cannot quietly change what a checkpoint path returns, and a bump that changes the weights
  is a deliberate act with a diff.
- **That they load offline.** The step after the download loads every model under
  `--network=none`.
- **The app's version and commit**, from `version.txt` and the build's `SOURCE_REVISION`.

The weights aren't stored in this repository. The largest artifact file is 1.35 GB and GitHub
refuses anything over 100 MiB; the set is 3.3 GB, which is past the free Git LFS allowance
and would be downloaded by everyone who clones. Recording their identity instead buys the
same guarantee for a few lines of text.

Still open: the rest of the Python dependency set. `pip install /opt/marker-pdf` resolves
marker's remaining ranges at build time, so two builds months apart can differ in transitive
packages, and that can change behaviour even though the weights are now fixed. A
`pip-compile --generate-hashes` constraints file would close it. The base image is pinned to a
tag rather than a digest for the same reason — convenient, not immutable.

So: the artifacts are pinned, the machinery around them mostly is, and the image digest remains
the authority on exactly what a given build contained.

## Bumping the pin

1. Read upstream's release notes. Anything that touches the flags BookMarker passes is a change
   here too.
2. Download the new sdist. Record its URL and SHA-256 here and in the `Dockerfile`, in both
   `ADD --checksum` and the `MARKER_VERSION` argument.
3. Check whether the `pdftext` override is still needed. If it isn't, drop it. Don't keep it out of
   habit.
4. If the new release names different surya checkpoints, update the `surya-ocr` pin in the
   `Dockerfile` to match.
5. Build. If the artifact check fails, decide whether the new weights are intended: if they are,
   regenerate `docker/model-artifacts.lock` with
   `docker run --rm --network none --entrypoint python <image> /usr/local/bin/preload_models.py --print`
   and commit the new hashes in the same change as the version bump, so the diff shows what
   changed.
6. `./build-and-run.sh`, then `./scripts/acceptance.sh`. The acceptance script compares the
   service's output against a direct `marker` run inside the container, which is the real
   regression test for a bump.
7. Put the engine version in `CHANGELOG.md` for that release.
