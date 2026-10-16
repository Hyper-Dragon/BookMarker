# Upstream: marker

BookMarker is **packaging around marker, not a fork**. This file records what is
pinned, what is changed, and where each kind of bug belongs.

| | |
| --- | --- |
| Upstream project | marker — <https://github.com/datalab-to/marker> |
| Upstream package | `marker-pdf` |
| Pinned version | **1.10.2** |
| Licence | GPL-3.0-or-later (the same licence as this repository) |
| Patch status | **unmodified** — no upstream file is edited |
| Affiliation | none |

## What BookMarker adds

Everything here, and nothing else:

- the container image (CUDA base, pinned engine install, healthcheck, compose file);
- the HTTP contract — `POST /convert`, `GET /health`, `GET /api/status`, the status
  page, the status codes and `X-*` headers, `503` + `Retry-After` backpressure;
- the tri-state LLM policy (`nollm` / `llmonly` / `llmfallback`);
- `llm_shim/marker_openai_service.py`, a marker LLM service that keeps the strict
  `json_schema` call and falls back to `json_object` for backends that reject it;
- scratch lifecycle, memory caps and the operational envelope.

## What marker provides

PDF parsing, layout detection, reading order, OCR, tables, equations, image
extraction, the `marker` CLI and its flags (`--page_range`, `--workers`,
`--disable_multiprocessing`, `--disable_image_extraction`, `--use_llm`,
`--llm_service`, `--config_json`), the conversion quality, the speed, and the model
weights. None of that originates here.

## Provenance of the pinned engine

| | |
| --- | --- |
| Artifact | <https://files.pythonhosted.org/packages/04/bc/a9e72fd015f562b1f86c9dbc201c80365e7baf43d5aa616ba1c89453537f/marker_pdf-1.10.2.tar.gz> |
| SHA-256 | `ce0fc839e11ad7519a576d254ca9d51a0f9454b9d7da02211f722b141317f9f1` |
| Verified by | the `Dockerfile`: `ADD --checksum=sha256:…` fails the build on a mismatch |
| Pinned on | 2026-10-16, re-verified 2026-11-04 |
| Upstream source of record | the artifact URL above |

The engine source is **not** committed to this repository. It is fetched at build
time from the URL above, so the published image and this tree can only differ from
upstream by the dependency override described next.

## The one deliberate deviation: a dependency override

After installing marker, the image installs:

```text
pdftext==0.7.1
pypdfium2==5.10.1
```

marker 1.10.2 declares `pdftext ~0.6.3`. The override fixes an infinite
link-extraction hang seen on some PDFs. This is a deviation from the dependency set
upstream declares and tests, and upstream is not obliged to support it.

**If you report a conversion bug upstream, say so**, for example:

> BookMarker 1.10.2 container; marker-pdf 1.10.2 unmodified; `pdftext` 0.7.1 and
> `pypdfium2` 5.10.1 installed over the declared range (link-extraction hang
> workaround). Reproduces outside BookMarker: [yes/no]

## Where to report what

| Symptom | Where |
| --- | --- |
| Wrong heading levels, reading order, tables, OCR or equations in the markdown | [upstream marker](https://github.com/datalab-to/marker/issues) — with the `pdftext` override disclosed as above |
| A marker CLI flag does not do what its docs say | upstream |
| `503` when you expected a conversion, wrong status code, missing `X-*` header | here — that is the service contract in [SPEC.md](SPEC.md) |
| Container will not start, health never passes, GPU not picked up | here |
| Your OpenAI-compatible backend is rejected | here if it is the shim's `json_schema` → `json_object` fallback; upstream otherwise |
| Out of memory on a large PDF | here is fine, but the answer is usually `page_range` or a larger memory limit — see the README |

## Reproducibility

What is pinned: the engine artifact (sha256, checked by the build), the base image tag the
acceptance run was done against, and the application's own version (`version.txt`, plus the
commit when the build supplies `SourceRevisionId`).

What is **not** pinned: the Python dependency set. `pip install /opt/marker-pdf` resolves
marker's declared ranges at build time, so two builds months apart can differ in transitive
packages. A `pip-compile --generate-hashes` constraints file wired into the Dockerfile is the
fix and is not done yet. Until it is, treat the published image digest as the authority on what
a given build contains, and use the version and commit the service reports to identify it.

## Bumping the pin

1. Read the upstream release notes. Anything that touches the flags BookMarker
   passes is a behaviour change here too.
2. Download the new sdist and record its URL and SHA-256 **here and in the
   `Dockerfile`** (`ADD --checksum` and the `MARKER_VERSION` argument).
3. Re-check whether the `pdftext` override is still needed. If it is not, remove it
   and delete this section's exception — do not carry it forward by habit.
4. `./build-and-run.sh`, then `./scripts/acceptance.sh`. The acceptance script
   compares service output against a direct `marker` CLI run inside the container;
   that comparison is the real regression test for a bump.
5. Note the engine version in `CHANGELOG.md` under that release.
